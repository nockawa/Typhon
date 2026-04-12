using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using K4os.Compression.LZ4;

namespace Typhon.Profiler.Server;

/// <summary>
/// Current state of a live session, built from the INIT frame.
/// </summary>
public sealed class LiveSessionState
{
    public TraceFileHeader Header { get; init; }
    public SystemDefinitionRecord[] Systems { get; init; }
    public Dictionary<int, string> SpanNames { get; } = new();
    public double TicksPerUs => Header.TimestampFrequency / 1_000_000.0;
}

/// <summary>
/// A single tick's worth of events, ready for SSE serialization.
/// </summary>
public sealed class LiveTickBatch
{
    public int TickNumber { get; init; }
    public LiveTraceEvent[] Events { get; init; }
    public Dictionary<int, string> NewSpanNames { get; init; }
}

/// <summary>
/// JSON-serializable trace event matching the client's TraceEvent interface.
/// </summary>
public sealed class LiveTraceEvent
{
    public double TimestampUs { get; init; }
    public int TickNumber { get; init; }
    public int SystemIndex { get; init; }
    public int ChunkIndex { get; init; }
    public int WorkerId { get; init; }
    public int EventType { get; init; }
    public int Phase { get; init; }
    public int SkipReason { get; init; }
    public int EntitiesProcessed { get; init; }
    public int Payload { get; init; }
}

/// <summary>
/// Background service that connects to a Typhon engine's TCP live stream port,
/// reads framed binary trace data, and fans out to SSE subscribers via bounded channels.
/// </summary>
public sealed partial class LiveSessionService : BackgroundService
{
    private readonly ILogger<LiveSessionService> _logger;
    private readonly string _engineHost;
    private readonly int _enginePort;

    private volatile LiveSessionState _state;
    private readonly ConcurrentDictionary<int, Channel<LiveTickBatch>> _subscribers = new();
    private int _nextSubscriberId;
    private long _tickCount;

    private static readonly int EventSize = Unsafe.SizeOf<TraceEvent>();

    public LiveSessionService(IConfiguration config, ILogger<LiveSessionService> logger)
    {
        _logger = logger;
        _engineHost = config.GetValue<string>("LiveStream:Host") ?? "127.0.0.1";
        _enginePort = config.GetValue<int>("LiveStream:Port", LiveStreamProtocol.DefaultPort);
    }

    /// <summary>Current session state, or null if not connected.</summary>
    public LiveSessionState GetState() => _state;

    /// <summary>Total ticks received in the current session.</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>Whether a live session is currently connected.</summary>
    public bool IsConnected => _state != null;

    /// <summary>Subscribe to receive tick batches. Returns a subscriber ID and channel reader.</summary>
    public (int Id, ChannelReader<LiveTickBatch> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<LiveTickBatch>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Interlocked.Increment(ref _nextSubscriberId);
        _subscribers[id] = channel;
        LogSubscriberConnected(id, _subscribers.Count);
        return (id, channel.Reader);
    }

    /// <summary>Remove a subscriber.</summary>
    public void Unsubscribe(int id)
    {
        if (_subscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
            LogSubscriberDisconnected(id, _subscribers.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        LogStarting(_engineHost, _enginePort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_engineHost, _enginePort, ct);
                tcp.NoDelay = true;
                LogConnected(_engineHost, _enginePort);

                await ProcessStreamAsync(tcp.GetStream(), ct);
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                // Engine not running or connection refused — retry
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                LogConnectionLost();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogUnexpectedError(ex);
            }

            _state = null;
            _pendingSpanNames = null;
            Interlocked.Exchange(ref _tickCount, 0);

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task ProcessStreamAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuf = new byte[LiveStreamProtocol.FrameHeaderSize];

        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(headerBuf, ct);
            var (type, length) = LiveStreamProtocol.ReadFrameHeader(headerBuf);

            // Defensive: reject implausible frame sizes (8 MB ceiling — a tick with every worker maxed out is < 1 MB compressed)
            if (length < 0 || length > 8 * 1024 * 1024)
            {
                LogMalformedFrame(type.ToString(), $"invalid length {length}");
                return; // disconnect and let the reconnect loop restart
            }

            var payload = new byte[length];
            if (length > 0)
            {
                await stream.ReadExactlyAsync(payload, ct);
            }

            // Each handler is wrapped so a malformed frame logs and is dropped rather than poisoning the reconnect loop
            try
            {
                switch (type)
                {
                    case LiveFrameType.Init:
                        HandleInit(payload);
                        break;

                    case LiveFrameType.TickEvents:
                        HandleTickEvents(payload);
                        break;

                    case LiveFrameType.SpanNames:
                        HandleSpanNames(payload);
                        break;

                    case LiveFrameType.Shutdown:
                        LogShutdownReceived();
                        _state = null;
                        return;

                    default:
                        LogUnknownFrame((byte)type);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMalformedFrame(type.ToString(), ex.Message);
                // Continue reading — next frame may be valid
            }
        }
    }

    private void HandleInit(byte[] payload)
    {
        var headerSize = Unsafe.SizeOf<TraceFileHeader>();
        if (payload.Length < headerSize)
        {
            LogMalformedFrame("Init", $"payload length {payload.Length} < header size {headerSize}");
            return;
        }

        var span = payload.AsSpan();

        // Read header (64 bytes). The wire format is little-endian on x86/x64/ARM64.
        // Running on big-endian hardware is unsupported and will produce garbage here.
        var header = MemoryMarshal.Read<TraceFileHeader>(span[..headerSize]);
        var pos = headerSize;

        // Read system definitions (same binary format as file)
        var systems = ReadSystemDefinitions(span, ref pos);

        var state = new LiveSessionState
        {
            Header = header,
            Systems = systems
        };

        ReadSpanNames(span, ref pos, state.SpanNames);

        _state = state;
        Interlocked.Exchange(ref _tickCount, 0);
        LogInitReceived(header.SystemCount, header.WorkerCount, header.BaseTickRate);
    }

    // Reusable scratch for DTO conversion to avoid per-tick allocation.
    // Sized on first use; grows on demand. Accessed only from the single reader thread.
    private LiveTraceEvent[] _dtoScratch;

    // Span names received via SPAN_NAMES frame since the last TICK_EVENTS broadcast. Attached to
    // the first batch of the next TICK_EVENTS so the client has the name dictionary before it
    // renders any chunk/span that might reference it.
    private Dictionary<int, string> _pendingSpanNames;

    private void HandleTickEvents(byte[] payload)
    {
        var state = _state;
        if (state == null)
        {
            return;
        }

        if (payload.Length < 12)
        {
            LogMalformedFrame("TickEvents", "payload shorter than 12-byte block header");
            return;
        }

        var span = payload.AsSpan();

        // Read block header: uncompressed (4B) + compressed (4B) + event count (4B)
        var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(span);
        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var eventCount = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);

        if (uncompressedSize < 0 || compressedSize < 0 || eventCount < 0
            || payload.Length < 12 + compressedSize
            || uncompressedSize != eventCount * Unsafe.SizeOf<TraceEvent>())
        {
            LogMalformedFrame("TickEvents", $"inconsistent block header (u={uncompressedSize}, c={compressedSize}, n={eventCount})");
            return;
        }

        var rawBuffer = new byte[uncompressedSize];
        var decoded = LZ4Codec.Decode(
            span.Slice(12, compressedSize),
            rawBuffer.AsSpan(0, uncompressedSize));

        if (decoded != uncompressedSize)
        {
            LogDecompressionMismatch(uncompressedSize, decoded);
            return;
        }

        // Cast to TraceEvent[] and restore absolute timestamps (undo delta encoding)
        var rawEvents = MemoryMarshal.Cast<byte, TraceEvent>(rawBuffer.AsSpan(0, uncompressedSize));
        var events = new TraceEvent[eventCount];
        rawEvents[..eventCount].CopyTo(events);

        for (var i = 1; i < events.Length; i++)
        {
            events[i].TimestampTicks += events[i - 1].TimestampTicks;
        }

        if (events.Length == 0)
        {
            return;
        }

        // Events are sorted by timestamp, which means ticks form contiguous runs (once a tick's
        // events are done, we never see that tick again). Walk the array once and emit one batch
        // per run — no Dictionary, no per-tick List, one DTO allocation per event.
        if (_dtoScratch == null || _dtoScratch.Length < events.Length)
        {
            _dtoScratch = new LiveTraceEvent[Math.Max(events.Length, 1024)];
        }

        var ticksPerUs = state.TicksPerUs;
        var runStart = 0;
        var runTick = events[0].TickNumber;

        for (var i = 0; i < events.Length; i++)
        {
            ref var evt = ref events[i];

            if (evt.TickNumber != runTick)
            {
                EmitTickBatch(runTick, runStart, i, events, ticksPerUs);
                runStart = i;
                runTick = evt.TickNumber;
            }

            _dtoScratch[i] = new LiveTraceEvent
            {
                TimestampUs = evt.TimestampTicks / ticksPerUs,
                TickNumber = evt.TickNumber,
                SystemIndex = evt.SystemIndex,
                ChunkIndex = evt.ChunkIndex,
                WorkerId = evt.WorkerId,
                EventType = (int)evt.EventType,
                Phase = (int)evt.Phase,
                SkipReason = evt.SkipReason,
                EntitiesProcessed = evt.EntitiesProcessed,
                Payload = evt.Payload
            };
        }

        EmitTickBatch(runTick, runStart, events.Length, events, ticksPerUs);
    }

    /// <summary>Emit a batch for the given run of events. Copies out of <c>_dtoScratch</c> into an array sized exactly to the run.</summary>
    private void EmitTickBatch(int tickNumber, int startInclusive, int endExclusive, TraceEvent[] _, double ticksPerUs)
    {
        var count = endExclusive - startInclusive;
        if (count == 0)
        {
            return;
        }

        var batchEvents = new LiveTraceEvent[count];
        Array.Copy(_dtoScratch, startInclusive, batchEvents, 0, count);

        // Attach pending span names to the FIRST batch of this frame so the client has them before
        // rendering events that reference them. Subsequent batches from the same frame carry null.
        var attachedSpanNames = _pendingSpanNames;
        _pendingSpanNames = null;

        var batch = new LiveTickBatch
        {
            TickNumber = tickNumber,
            Events = batchEvents,
            NewSpanNames = attachedSpanNames
        };

        Interlocked.Increment(ref _tickCount);
        BroadcastBatch(batch);
    }

    private void HandleSpanNames(byte[] payload)
    {
        var state = _state;
        if (state == null)
        {
            return;
        }

        if (payload.Length < 2)
        {
            LogMalformedFrame("SpanNames", "payload shorter than 2-byte count");
            return;
        }

        var span = payload.AsSpan();
        var pos = 0;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span);
        pos += 2;

        for (var i = 0; i < count; i++)
        {
            if (pos + 3 > span.Length)
            {
                LogMalformedFrame("SpanNames", $"truncated at entry {i}");
                return;
            }

            var id = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
            pos += 2;

            var nameLen = span[pos++];
            if (pos + nameLen > span.Length)
            {
                LogMalformedFrame("SpanNames", $"truncated name at entry {i}");
                return;
            }

            var name = Encoding.UTF8.GetString(span.Slice(pos, nameLen));
            pos += nameLen;

            // Update both the authoritative state (so new SSE clients see it in their initial metadata)
            // and the pending buffer (so the next tick batch carries it).
            state.SpanNames[id] = name;
            (_pendingSpanNames ??= new Dictionary<int, string>())[id] = name;
        }

        if (count > 0)
        {
            LogSpanNamesReceived(count);
        }
    }

    private void BroadcastBatch(LiveTickBatch batch)
    {
        foreach (var (_, channel) in _subscribers)
        {
            channel.Writer.TryWrite(batch); // Drop if subscriber is slow (bounded channel)
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Binary parsing helpers (same format as TraceFileReader)
    // ══════════════��═════════════════════════════════════���══════════

    private static SystemDefinitionRecord[] ReadSystemDefinitions(ReadOnlySpan<byte> data, ref int pos)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;

        var systems = new SystemDefinitionRecord[count];

        for (var i = 0; i < count; i++)
        {
            var index = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            pos += 2;

            var nameLen = data[pos++];
            var name = Encoding.UTF8.GetString(data.Slice(pos, nameLen));
            pos += nameLen;

            var type = data[pos++];
            var priority = data[pos++];
            var isParallel = data[pos++] != 0;
            var tierFilter = data[pos++];

            var predCount = data[pos++];
            var predecessors = new ushort[predCount];
            for (var p = 0; p < predCount; p++)
            {
                predecessors[p] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                pos += 2;
            }

            var succCount = data[pos++];
            var successors = new ushort[succCount];
            for (var s = 0; s < succCount; s++)
            {
                successors[s] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                pos += 2;
            }

            systems[i] = new SystemDefinitionRecord
            {
                Index = index,
                Name = name,
                Type = type,
                Priority = priority,
                IsParallel = isParallel,
                TierFilter = tierFilter,
                Predecessors = predecessors,
                Successors = successors
            };
        }

        return systems;
    }

    private static void ReadSpanNames(ReadOnlySpan<byte> data, ref int pos, Dictionary<int, string> spanNames)
    {
        if (pos + 4 > data.Length)
        {
            return;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        if (magic != TraceFileWriter.SpanNameTableMagic)
        {
            return;
        }

        pos += 4;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;

        for (var i = 0; i < count; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            pos += 2;

            var nameLen = data[pos++];
            var name = Encoding.UTF8.GetString(data.Slice(pos, nameLen));
            pos += nameLen;

            spanNames[id] = name;
        }
    }
}

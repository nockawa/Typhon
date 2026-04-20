using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Typhon.Profiler;

namespace Typhon.Profiler.Server;

/// <summary>
/// Snapshot of the session metadata received from the engine via the INIT frame — header + system / archetype / component type tables.
/// </summary>
public sealed class LiveSessionState
{
    public TraceFileHeader Header { get; init; }
    public IReadOnlyList<SystemDefinitionRecord> Systems { get; init; }
    public IReadOnlyList<ArchetypeRecord> Archetypes { get; init; }
    public IReadOnlyList<ComponentTypeRecord> ComponentTypes { get; init; }

    /// <summary>Ticks-per-microsecond conversion factor derived from the header.</summary>
    public double TicksPerUs => Header.TimestampFrequency / 1_000_000.0;
}

/// <summary>
/// A single tick's worth of decoded records, grouped and ready for SSE serialization.
/// </summary>
public sealed class LiveTickBatch
{
    public int TickNumber { get; init; }
    public LiveTraceEvent[] Events { get; init; }
}

/// <summary>
/// Background service that connects to a Typhon engine's <c>TcpExporter</c> port, reads framed v3 profiler data, decodes typed records into
/// <see cref="LiveTraceEvent"/> DTOs, and fans out one <see cref="LiveTickBatch"/> per tick to SSE subscribers via bounded channels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Protocol:</b> <see cref="LiveStreamProtocol"/> frames. One <see cref="LiveFrameType.Init"/> frame on connect (header + metadata tables),
/// then <see cref="LiveFrameType.Block"/> frames (LZ4-compressed record batches), then <see cref="LiveFrameType.Shutdown"/> on session end.
/// </para>
/// <para>
/// <b>Connect loop:</b> the service acts as a TCP client. On <see cref="SocketException"/> or <see cref="IOException"/> it waits 2 seconds and
/// tries again, so the viewer can be started before the engine and "find" it as soon as the engine's exporter becomes available.
/// </para>
/// <para>
/// <b>Tick derivation:</b> v3 records don't carry a tick number (see design note in <see cref="RecordDecoder"/>). The decoder counts
/// <c>TickStart</c> records and assigns the current counter to every subsequent record. This works for a fresh session-from-start; on reconnect
/// the counter restarts from 1.
/// </para>
/// </remarks>
public sealed partial class LiveSessionService : BackgroundService
{
    /// <summary>Default TCP port the engine's <c>TcpExporter</c> listens on, if the server's config doesn't override it.</summary>
    public const int DefaultLivePort = 9100;

    private readonly ILogger<LiveSessionService> _logger;
    private readonly string _engineHost;
    private readonly int _enginePort;

    private volatile LiveSessionState _state;
    private RecordDecoder _decoder;
    private readonly ConcurrentDictionary<int, Channel<LiveTickBatch>> _subscribers = new();
    private int _nextSubscriberId;
    private long _tickCount;

    // Accumulated ThreadInfo registrations for the current session. The engine emits a kind-77 record once per slot claim; a browser that
    // subscribes AFTER those records have been broadcast would otherwise never learn the slot→thread-name mapping and couldn't lay out
    // span lanes. Every newly-received ThreadInfo updates this map; every new subscriber gets the map replayed as a synthetic tick-0 batch
    // before real tick batches start flowing. Cleared on Init (new session ⇒ slot claims start over).
    private readonly ConcurrentDictionary<byte, LiveTraceEvent> _threadRegistrations = new();

    // First PerTickSnapshot (kind 76) observed in the current session. The engine stamps capacity/total gauges (TotalPageCount,
    // TransientStoreMaxBytes, WalCommitBufferCapacity, StagingPoolCapacity — see GaugeId.cs) into the first snapshot and never re-emits them.
    // Late-subscribing browsers would otherwise render those gauges as zero/missing for the whole session. Cached on first observation, exposed
    // via GetInitialGaugesSnapshot for the metadata DTO. Cleared on Init / disconnect.
    private volatile LiveTraceEvent _firstGaugeSnapshot;

    public LiveSessionService(IConfiguration config, ILogger<LiveSessionService> logger)
    {
        _logger = logger;
        _engineHost = config.GetValue<string>("LiveStream:Host") ?? "127.0.0.1";
        _enginePort = config.GetValue<int>("LiveStream:Port", DefaultLivePort);
    }

    /// <summary>Current session state, or <c>null</c> if not connected.</summary>
    public LiveSessionState GetState() => _state;

    /// <summary>Total ticks broadcast during the current session.</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>Whether a live session is currently connected.</summary>
    public bool IsConnected => _state != null;

    /// <summary>Subscribe to receive tick batches. Returns a subscriber ID and a channel reader.</summary>
    public (int Id, ChannelReader<LiveTickBatch> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<LiveTickBatch>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Interlocked.Increment(ref _nextSubscriberId);
        _subscribers[id] = channel;
        LogSubscriberConnected(id, _subscribers.Count);
        return (id, channel.Reader);
    }

    /// <summary>
    /// Snapshot the current slot→thread-name map, built from every ThreadInfo record received so far in the session. Caller reads it to
    /// populate <c>TraceMetadata.threadNames</c> on the client — the browser can't rely on kind-77 records arriving in normal tick batches
    /// because the synthesized records emitted by the engine's <c>TcpExporter.TrySendCatchupThreadInfoBlock</c> are consumed by the server
    /// before any SSE subscriber exists.
    /// </summary>
    /// <remarks>
    /// Returns a sparse dictionary keyed by slot. Empty when no ThreadInfo records have arrived yet (session just started, no worker has
    /// emitted anything). The map mutates as new slots claim — re-fetch on each metadata request rather than caching.
    /// </remarks>
    public Dictionary<byte, string> GetThreadNamesSnapshot()
    {
        var result = new Dictionary<byte, string>(_threadRegistrations.Count);
        foreach (var kv in _threadRegistrations)
        {
            if (kv.Value.ThreadName is { Length: > 0 } name)
            {
                result[kv.Key] = name;
            }
        }
        return result;
    }

    /// <summary>
    /// Snapshot the first-observed PerTickSnapshot's gauge values. Returns null if no snapshot has been seen yet (no ticks received since
    /// TCP connect / after Init). Intended for the metadata DTO so late-subscribing browsers see one-shot init-value gauges (page cache
    /// capacity, staging pool size, etc.) that the engine only emits in the session's first snapshot.
    /// </summary>
    public Dictionary<int, double> GetInitialGaugesSnapshot() => _firstGaugeSnapshot?.Gauges;

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
                // Engine not running or connection refused — retry quietly.
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

            FlushPendingTick();
            _state = null;
            _decoder = null;
            Interlocked.Exchange(ref _tickCount, 0);
            _threadRegistrations.Clear(); // mappings are only meaningful within the current session
            _firstGaugeSnapshot = null;

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

            // Defensive: reject implausible frame sizes. A full 256 KB block compressed is well under 2 MB; 8 MB is a generous ceiling.
            if (length < 0 || length > 8 * 1024 * 1024)
            {
                LogMalformedFrame(type.ToString(), $"invalid length {length}");
                return;
            }

            // Rent from ArrayPool<byte> rather than `new byte[length]` on every frame. At 5-10 blocks/sec up to 256 KB each, the prior path
            // produced 1-2.5 MB/sec of LOH (buffers ≥ 85 KB go straight to LOH), which induces Gen 2 collections periodically. Pooled buffer
            // can be larger than `length`; handlers must operate on the [0, length) slice only, not the whole array.
            var payload = length > 0 ? System.Buffers.ArrayPool<byte>.Shared.Rent(length) : Array.Empty<byte>();
            try
            {
                if (length > 0)
                {
                    await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct);
                }

                switch (type)
                {
                    case LiveFrameType.Init:
                        HandleInit(payload, length);
                        break;

                    case LiveFrameType.Block:
                        HandleBlock(payload, length);
                        break;

                    case LiveFrameType.Shutdown:
                        FlushPendingTick();
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
                // Continue reading — next frame may be valid.
            }
            finally
            {
                if (length > 0)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
    }

    private void HandleInit(byte[] payload, int length)
    {
        // The INIT payload is byte-identical to a .typhon-trace file's first four sections (header + systems + archetypes + componentTypes).
        // Wrap the valid prefix in a MemoryStream (pooled buffer may be larger than `length`) and reuse TraceFileReader to avoid duplicating
        // the table-parsing logic here.
        using var ms = new MemoryStream(payload, index: 0, count: length, writable: false);
        using var reader = new TraceFileReader(ms);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();

        var state = new LiveSessionState
        {
            Header = reader.Header,
            Systems = reader.Systems.ToArray(),
            Archetypes = reader.Archetypes.ToArray(),
            ComponentTypes = reader.ComponentTypes.ToArray(),
        };

        _state = state;
        _decoder = new RecordDecoder(reader.Header.TimestampFrequency);

        // Seed the decoder's running tick counter from the engine's reported tick-at-connect. Without this, the decoder restarts at 1 on every
        // TCP reconnect and tick numbers the browser sees don't match anything the engine logs. With it, tick numbers are absolute and align
        // across reconnects. Seed value is (engineTickAtInit - 1) so the first TickStart record (which increments before tagging) lands on
        // engineTickAtInit — matches the same convention the file-replay path uses via SetCurrentTick. Header carries 0 for file-based traces
        // (no scheduler running at file-start), in which case we leave the decoder at its default of 0 → first TickStart becomes tick 1.
        if (reader.Header.EngineTickAtInit > 0)
        {
            _decoder.SetCurrentTick((int)(reader.Header.EngineTickAtInit - 1));
        }

        Interlocked.Exchange(ref _tickCount, 0);
        _threadRegistrations.Clear(); // fresh session ⇒ slot claims start over; old mappings are no longer meaningful
        _firstGaugeSnapshot = null;   // fresh session ⇒ new set of init-value gauges; discard the previous one
        _pendingTickEvents.Clear();
        _pendingTickNumber = -1;
        LogInitReceived(state.Systems.Count, state.Header.WorkerCount, state.Header.BaseTickRate);
    }

    // Reusable scratch buffer for decoding. Grows on demand; single reader thread has exclusive access.
    private byte[] _rawBlockBuffer = [];

    private void HandleBlock(byte[] payload, int length)
    {
        var state = _state;
        var decoder = _decoder;
        if (state == null || decoder == null)
        {
            return;
        }

        if (length < TraceBlockEncoder.BlockHeaderSize)
        {
            LogMalformedFrame("Block", "payload shorter than block header");
            return;
        }

        var (uncompressedBytes, compressedBytes, recordCount) = TraceBlockEncoder.ReadBlockHeader(payload);
        if (uncompressedBytes < 0 || compressedBytes < 0 || recordCount < 0
            || length < TraceBlockEncoder.BlockHeaderSize + compressedBytes)
        {
            LogMalformedFrame("Block", $"inconsistent block header (u={uncompressedBytes}, c={compressedBytes}, n={recordCount})");
            return;
        }

        if (_rawBlockBuffer.Length < uncompressedBytes)
        {
            _rawBlockBuffer = new byte[uncompressedBytes];
        }

        try
        {
            TraceBlockEncoder.DecodeBlock(
                payload.AsSpan(TraceBlockEncoder.BlockHeaderSize, compressedBytes),
                uncompressedBytes,
                _rawBlockBuffer);
        }
        catch (InvalidDataException ex)
        {
            LogDecompressionMismatch(uncompressedBytes, 0);
            LogMalformedFrame("Block", ex.Message);
            return;
        }

        var decoded = new List<LiveTraceEvent>(recordCount);
        decoder.DecodeBlock(_rawBlockBuffer.AsSpan(0, uncompressedBytes), decoded);

        if (decoded.Count == 0)
        {
            return;
        }

        // Snapshot any ThreadInfo records into the replay map BEFORE broadcasting, so any subscriber that joins mid-broadcast sees a
        // consistent view (either missing this slot entirely, or seeing it via the synthetic tick-0 batch plus the normal tick batch).
        foreach (var ev in decoded)
        {
            if (ev.Kind == (int)Typhon.Engine.Profiler.TraceEventKind.ThreadInfo)
            {
                _threadRegistrations[ev.ThreadSlot] = ev;
            }
            else if (ev.Kind == (int)Typhon.Engine.Profiler.TraceEventKind.PerTickSnapshot && _firstGaugeSnapshot == null)
            {
                // First PerTickSnapshot of the session — carries capacity/total gauges that are never re-emitted. Cache for late subscribers.
                _firstGaugeSnapshot = ev;
            }
        }

        // Buffer events by tick number across blocks. The engine's profiler consumer thread drains at ~1ms cadence; at 60 Hz a single
        // tick's events span ~10-16 blocks. Emitting one SSE batch per contiguous same-tick run within a block would produce many
        // fragmentary batches with the same tickNumber — the client's processTickAndAppend assumes one call = one complete tick and
        // breaks when fed fragments (appends fragmentary TickData entries with startUs = Infinity, corrupting the global time origin).
        //
        // Strategy: accumulate events for the current tick in _pendingTickEvents. Flush (EmitTickBatch) only when a different tick
        // number is seen — that's the guaranteed "current tick is definitively complete" signal because records within a block are
        // timestamp-sorted and TickStart of tick N+1 can only appear after all of tick N's records. Trailing partial ticks are flushed
        // on Shutdown frame or TCP disconnect; see ExecuteAsync + the Shutdown case in ProcessStreamAsync.
        foreach (var ev in decoded)
        {
            if (_pendingTickNumber == -1)
            {
                _pendingTickNumber = ev.TickNumber;
            }
            else if (ev.TickNumber != _pendingTickNumber)
            {
                EmitTickBatch(_pendingTickNumber, _pendingTickEvents, 0, _pendingTickEvents.Count);
                _pendingTickEvents.Clear();
                _pendingTickNumber = ev.TickNumber;
            }
            _pendingTickEvents.Add(ev);
        }
    }

    // Pending-tick buffer. Single-threaded access (only the ExecuteAsync reader loop writes), no locking needed.
    private readonly List<LiveTraceEvent> _pendingTickEvents = new(256);
    private int _pendingTickNumber = -1;

    /// <summary>
    /// Flush any partially-accumulated tick to subscribers. Called from the stream reader on Shutdown, and from ExecuteAsync when a TCP
    /// disconnect occurs, so trailing events aren't lost. Safe to call repeatedly — resets the pending state even if no events pending.
    /// </summary>
    private void FlushPendingTick()
    {
        if (_pendingTickNumber != -1 && _pendingTickEvents.Count > 0)
        {
            EmitTickBatch(_pendingTickNumber, _pendingTickEvents, 0, _pendingTickEvents.Count);
        }
        _pendingTickEvents.Clear();
        _pendingTickNumber = -1;
    }

    private void EmitTickBatch(int tickNumber, List<LiveTraceEvent> source, int startInclusive, int endExclusive)
    {
        var count = endExclusive - startInclusive;
        if (count == 0)
        {
            return;
        }

        var batchEvents = new LiveTraceEvent[count];
        source.CopyTo(startInclusive, batchEvents, 0, count);

        var batch = new LiveTickBatch
        {
            TickNumber = tickNumber,
            Events = batchEvents,
        };

        Interlocked.Increment(ref _tickCount);
        BroadcastBatch(batch);
    }

    private void BroadcastBatch(LiveTickBatch batch)
    {
        foreach (var (_, channel) in _subscribers)
        {
            channel.Writer.TryWrite(batch); // Drop if a subscriber is slow (bounded channel, DropOldest).
        }
    }
}

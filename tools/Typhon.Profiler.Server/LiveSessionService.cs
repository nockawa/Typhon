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

            _state = null;
            _decoder = null;
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
        Interlocked.Exchange(ref _tickCount, 0);
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

        // Records arrive sorted by timestamp (consumer drain guarantees it), and TickStart events always precede the tick's other records —
        // so tick numbers form contiguous runs within a block. Walk once, slice into per-tick batches, broadcast each.
        var runStart = 0;
        var runTick = decoded[0].TickNumber;

        for (var i = 1; i < decoded.Count; i++)
        {
            if (decoded[i].TickNumber != runTick)
            {
                EmitTickBatch(runTick, decoded, runStart, i);
                runStart = i;
                runTick = decoded[i].TickNumber;
            }
        }
        EmitTickBatch(runTick, decoded, runStart, decoded.Count);
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

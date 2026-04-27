using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using ProfilerRecordDecoder = Typhon.Profiler.RecordDecoder;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session equivalent of the old singleton <c>LiveSessionService</c> — owns the TCP connection to a running Typhon
/// app's profiler exporter, decodes v3 record frames, and fans out per-tick <see cref="LiveTickBatch"/> values to SSE
/// subscribers via bounded channels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connect semantics.</b> <see cref="StartAsync"/> attempts the first TCP connect with 3 × 2 s upfront retry. On
/// total failure it throws <see cref="WorkbenchException"/> with HTTP 503, which the controller surfaces as
/// <c>ProblemDetails</c>. Once the first connect succeeds the runtime lives, and the background read loop silently
/// reconnects on later <see cref="SocketException"/> / <see cref="IOException"/> (same 2 s cadence as the old code).
/// </para>
/// <para>
/// <b>Metadata.</b> The first <see cref="LiveFrameType.Init"/> frame is projected into a <see cref="ProfilerMetadataDto"/>
/// and stored on <see cref="Metadata"/>. <see cref="MetadataReady"/> resolves at the same time. Tick summaries / chunk
/// manifest / global metrics start empty in Phase 1b — Phase 2 will grow them server-side as blocks arrive.
/// </para>
/// <para>
/// <b>Tick derivation.</b> The wire format doesn't carry a tick number on non-<c>TickStart</c> records; the decoder
/// counts <c>TickStart</c> records and stamps every following record with the current value.
/// </para>
/// <para>
/// <b>Known limitation — tick counter reset on reconnect.</b> When the TCP link drops and the runtime reconnects,
/// the decoder is re-created (<c>_decoder = null</c> in the read loop's catch path) and the tick counter restarts
/// from 1 after the next <c>Init</c> frame. Clients tracking absolute tick progression will see a discontinuity
/// across the reconnect boundary. This matches the behaviour of the retired <c>Typhon.Profiler.Server</c> —
/// preserving it keeps client expectations stable across the port. Fixing it properly requires the engine to
/// include the current tick number in every <c>Init</c> frame (so the decoder can seed its counter on reconnect),
/// which is a wire-protocol change out of scope for this PR.
/// </para>
/// <para>
/// <b>Disposal.</b> Cancels the read loop, closes the socket, completes every subscriber channel. Safe to call
/// multiple times.
/// </para>
/// </remarks>
public sealed partial class AttachSessionRuntime : IDisposable
{
    private const int DefaultPort = 9100;
    private const int ConnectRetryCount = 3;
    private const int ConnectRetryDelayMs = 2000;
    private const int ReconnectDelayMs = 2000;
    private const int MaxFrameBytes = 8 * 1024 * 1024;

    private readonly string _endpointAddress;
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ProfilerMetadataDto> _metadataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, Channel<LiveTickBatch>> _subscribers = new();

    private ProfilerRecordDecoder _decoder;
    private byte[] _rawBlockBuffer = [];
    private long _tickCount;
    private volatile string _connectionStatus = "connecting";
    private bool _disposed;

    /// <summary>Engine endpoint as provided by the client (e.g. <c>"localhost:9100"</c>).</summary>
    public string EndpointAddress => _endpointAddress;

    /// <summary>Metadata built from the Init frame. <c>null</c> until the first Init arrives.</summary>
    public ProfilerMetadataDto Metadata { get; private set; }

    /// <summary>Resolves with the metadata DTO once the first Init frame arrives. Faults if the runtime is disposed before that.</summary>
    public Task<ProfilerMetadataDto> MetadataReady => _metadataTcs.Task;

    /// <summary>Total ticks broadcast during the current session (survives reconnects → the decoder resets on reconnect).</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>Current connection status — <c>"connected"</c>, <c>"reconnecting"</c>, or <c>"disconnected"</c>.</summary>
    public string ConnectionStatus => _connectionStatus;

    /// <summary>True while the TCP socket is currently held open.</summary>
    public bool IsConnected => _connectionStatus == "connected";

    /// <summary>Fires once per-tick batch — subscribers (SSE handlers) forward into their subscriber channels.</summary>
    public event Action<LiveTickBatch> TickReceived;

    /// <summary>Fires once per Init frame (so once on first connect, once per reconnect with fresh metadata).</summary>
    public event Action<ProfilerMetadataDto> MetadataReceived;

    /// <summary>Fires when the connection state changes. SSE handlers emit heartbeat frames when this fires.</summary>
    public event Action<string> ConnectionStateChanged;

    /// <summary>
    /// Latest tick-0 (pre-tick) batch — typically carries `ThreadInfo` catch-up records replayed by `TcpExporter`
    /// to late-connecting clients, plus any `MemoryAllocEvent`/`GcStart`/`GcEnd` instants emitted before the first
    /// `TickStart`. Cached here because the TCP read loop processes the catch-up block as soon as bytes arrive,
    /// which is BEFORE any SSE subscriber registers `TickReceived` — so without caching the pre-tick batch the
    /// client never sees it. SSE handlers replay it on connect via <see cref="MetadataTickBatch"/>, the same way
    /// they replay <see cref="Metadata"/>.
    /// </summary>
    public LiveTickBatch MetadataTickBatch { get; private set; }

    private AttachSessionRuntime(string endpointAddress, string host, int port, ILogger logger)
    {
        _endpointAddress = endpointAddress;
        _host = host;
        _port = port;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new attach-session runtime. Performs 3 × 2 s upfront TCP connect retry before returning; throws
    /// <see cref="WorkbenchException"/> (HTTP 503) if all attempts fail so the controller can surface a clean error.
    /// On success the read loop kicks off on the thread pool and the runtime is returned live.
    /// </summary>
    public static async Task<AttachSessionRuntime> StartAsync(string endpointAddress, ILogger logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointAddress);
        var (host, port) = ParseEndpoint(endpointAddress);
        var ipv4 = await ResolveIPv4Async(host, ct);

        TcpClient tcp = null;
        Exception lastError = null;
        for (var attempt = 0; attempt < ConnectRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(ipv4, port, ct);
                tcp.NoDelay = true;
                break;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                try { tcp.Dispose(); } catch { }
                tcp = null;
                if (attempt < ConnectRetryCount - 1)
                {
                    await Task.Delay(ConnectRetryDelayMs, ct);
                }
            }
            catch (OperationCanceledException)
            {
                try { tcp.Dispose(); } catch { }
                throw;
            }
        }

        if (tcp == null)
        {
            throw new WorkbenchException(
                StatusCodes.Status503ServiceUnavailable,
                "attach_connect_failed",
                $"Failed to connect to {host}:{port} after {ConnectRetryCount} attempts. {lastError?.Message}",
                lastError);
        }

        var runtime = new AttachSessionRuntime(endpointAddress, host, port, logger);
        runtime.LogStarting(host, port);
        runtime.SetConnectionStatus("connected");
        runtime.LogConnected(host, port);
        // Detach: the read loop runs until the session is disposed or the socket dies. We fire it
        // with a fault-continuation so an unhandled exception doesn't disappear into TaskScheduler.
        // UnobservedTaskException — it lands in the logger where we can actually diagnose it.
        _ = Task.Run(() => runtime.ReadLoopAsync(tcp))
            .ContinueWith(
                t => runtime.LogReadLoopFaulted(t.Exception!),
                default,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return runtime;
    }

    /// <summary>Creates a subscriber channel and returns its reader. Use <see cref="Unsubscribe"/> on teardown.</summary>
    public (Guid Id, ChannelReader<LiveTickBatch> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<LiveTickBatch>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        LogSubscriberConnected(id, _subscribers.Count);
        return (id, channel.Reader);
    }

    /// <summary>
    /// User-initiated disconnect from the engine. Cancels the read loop and any in-flight reconnect attempts so the
    /// runtime settles on <c>"disconnected"</c> status. Does NOT dispose the runtime — Metadata, MetadataTickBatch,
    /// and the SSE subscriber channels are kept alive so the user can keep inspecting the captured tick buffer
    /// (live data just stops flowing). Idempotent; subsequent calls no-op.
    /// </summary>
    public void RequestDisconnect()
    {
        if (_disposed)
        {
            return;
        }
        try { _cts.Cancel(); }
        catch (ObjectDisposedException)
        {
            // already torn down; nothing to do
        }
    }

    /// <summary>Removes a subscriber channel and completes it. No-op if the subscriber is already gone.</summary>
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
            LogSubscriberDisconnected(id, _subscribers.Count);
        }
    }

    private async Task ReadLoopAsync(TcpClient initialSocket)
    {
        var ct = _cts.Token;
        var socket = initialSocket;
        var streamEnd = StreamEndReason.ConnectionLost;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (socket)
                {
                    streamEnd = await ProcessStreamAsync(socket.GetStream(), ct);
                }
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                streamEnd = StreamEndReason.ConnectionLost;
                // Engine dropped — reconnect.
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                streamEnd = StreamEndReason.ConnectionLost;
                LogConnectionLost();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                streamEnd = StreamEndReason.ConnectionLost;
                LogUnexpectedError(ex);
            }

            socket = null;
            if (ct.IsCancellationRequested) break;

            // The engine sent a Shutdown frame — terminal. Stay disconnected; do NOT enter the reconnect loop,
            // because the engine has explicitly told us the session is over (graceful exit). Reconnecting after
            // a Shutdown frame would just spin until either the engine restarts (a new session) or the user
            // closes the Workbench tab.
            if (streamEnd == StreamEndReason.Shutdown)
            {
                break;
            }

            SetConnectionStatus("reconnecting");
            _decoder = null;

            // Silent reconnect loop — 2 s between attempts until we succeed or the runtime is disposed.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectDelayMs, ct);
                    var ipv4 = await ResolveIPv4Async(_host, ct);
                    var next = new TcpClient();
                    await next.ConnectAsync(ipv4, _port, ct);
                    next.NoDelay = true;
                    socket = next;
                    SetConnectionStatus("connected");
                    LogConnected(_host, _port);
                    break;
                }
                catch (SocketException) { /* retry silently */ }
                catch (IOException) { /* retry silently */ }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }

        SetConnectionStatus("disconnected");
    }

    /// <summary>How <see cref="ProcessStreamAsync"/> exited — distinguishes "engine sent Shutdown" (terminal,
    /// no reconnect) from "stream ended for any other reason" (reconnect).</summary>
    private enum StreamEndReason
    {
        ConnectionLost,
        Shutdown,
    }

    private async Task<StreamEndReason> ProcessStreamAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuf = new byte[LiveStreamProtocol.FrameHeaderSize];

        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(headerBuf, ct);
            var (type, length) = LiveStreamProtocol.ReadFrameHeader(headerBuf);

            if (length < 0 || length > MaxFrameBytes)
            {
                LogMalformedFrame(type.ToString(), $"invalid length {length}");
                return StreamEndReason.ConnectionLost;
            }

            // Pool buffers to keep LOH pressure down: blocks routinely exceed 85 KB at high-fanout workloads.
            var payload = length > 0 ? ArrayPool<byte>.Shared.Rent(length) : [];
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
                        return StreamEndReason.Shutdown;

                    default:
                        LogUnknownFrame((byte)type);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMalformedFrame(type.ToString(), ex.Message);
            }
            finally
            {
                if (length > 0)
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }

        return StreamEndReason.ConnectionLost;
    }

    private void HandleInit(byte[] payload, int length)
    {
        // INIT payload = first four sections of a .typhon-trace file (header + systems + archetypes + componentTypes).
        // Wrap the exact slice (pool buffer may be larger) in a MemoryStream and reuse TraceFileReader.
        using var ms = new MemoryStream(payload, index: 0, count: length, writable: false);
        using var reader = new TraceFileReader(ms);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();

        _decoder = new ProfilerRecordDecoder(reader.Header.TimestampFrequency);
        Interlocked.Exchange(ref _tickCount, 0);

        var metadata = BuildMetadataDto(reader);
        Metadata = metadata;
        _metadataTcs.TrySetResult(metadata);
        MetadataReceived?.Invoke(metadata);
        LogInitReceived(reader.Systems.Count, reader.Header.WorkerCount, reader.Header.BaseTickRate);
    }

    private static ProfilerMetadataDto BuildMetadataDto(TraceFileReader reader)
    {
        var h = reader.Header;
        var headerDto = new ProfilerHeaderDto(
            Version: h.Version,
            TimestampFrequency: h.TimestampFrequency,
            BaseTickRate: h.BaseTickRate,
            WorkerCount: h.WorkerCount,
            SystemCount: h.SystemCount,
            ArchetypeCount: h.ArchetypeCount,
            ComponentTypeCount: h.ComponentTypeCount,
            CreatedUtcTicks: h.CreatedUtcTicks,
            SamplingSessionStartQpc: h.SamplingSessionStartQpc);

        var systems = new SystemDefinitionDto[reader.Systems.Count];
        for (var i = 0; i < reader.Systems.Count; i++)
        {
            var sr = reader.Systems[i];
            systems[i] = new SystemDefinitionDto(
                Index: sr.Index,
                Name: sr.Name,
                Type: sr.Type,
                Priority: sr.Priority,
                IsParallel: sr.IsParallel,
                TierFilter: sr.TierFilter,
                Predecessors: sr.Predecessors,
                Successors: sr.Successors);
        }

        var archetypes = new ArchetypeDto[reader.Archetypes.Count];
        for (var i = 0; i < reader.Archetypes.Count; i++)
        {
            archetypes[i] = new ArchetypeDto(reader.Archetypes[i].ArchetypeId, reader.Archetypes[i].Name);
        }

        var componentTypes = new ComponentTypeDto[reader.ComponentTypes.Count];
        for (var i = 0; i < reader.ComponentTypes.Count; i++)
        {
            componentTypes[i] = new ComponentTypeDto(reader.ComponentTypes[i].ComponentTypeId, reader.ComponentTypes[i].Name);
        }

        // Live sessions start empty — tick summaries / manifest / metrics grow as blocks arrive (deferred to Phase 2).
        // Fingerprint is empty for attach sessions (no source file to hash); clients that care can use session id instead.
        return new ProfilerMetadataDto(
            Fingerprint: string.Empty,
            Header: headerDto,
            Systems: systems,
            Archetypes: archetypes,
            ComponentTypes: componentTypes,
            SpanNames: new Dictionary<int, string>(),
            GlobalMetrics: new GlobalMetricsDto(
                GlobalStartUs: 0,
                GlobalEndUs: 0,
                MaxTickDurationUs: 0,
                MaxSystemDurationUs: 0,
                P95TickDurationUs: 0,
                TotalEvents: 0,
                TotalTicks: 0,
                SystemAggregates: []),
            TickSummaries: [],
            ChunkManifest: [],
            GcSuspensions: []);
    }

    private void HandleBlock(byte[] payload, int length)
    {
        var decoder = _decoder;
        if (decoder == null || Metadata == null)
        {
            // Block arrived before Init — engine bug or mid-session reconnect race. Drop silently.
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

        if (decoded.Count == 0) return;

        // Records within a block are timestamp-ordered and TickStart precedes its tick's records — contiguous runs.
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
        if (count == 0) return;

        var events = new LiveTraceEvent[count];
        source.CopyTo(startInclusive, events, 0, count);

        var batch = new LiveTickBatch
        {
            TickNumber = tickNumber,
            Events = events,
        };

        // Cache the FIRST tick-0 batch we ever see — that's the catch-up ThreadInfo block synthesized by the
        // engine's TcpExporter on accept. Subsequent tick-0 batches (the regular stream's pre-first-TickStart
        // events: kind 152 WorkerBetweenTick etc.) must NOT overwrite it; they're not metadata, they'll reach
        // any live subscriber via the regular event-firing path. Late-connecting SSE clients only need the
        // catch-up replayed (see ProfilerLiveStream.HandleAsync).
        if (tickNumber == 0 && MetadataTickBatch == null)
        {
            MetadataTickBatch = batch;
        }

        Interlocked.Increment(ref _tickCount);
        TickReceived?.Invoke(batch);
        BroadcastBatch(batch);
    }

    private void BroadcastBatch(LiveTickBatch batch)
    {
        foreach (var (_, channel) in _subscribers)
        {
            channel.Writer.TryWrite(batch); // DropOldest absorbs slow subscribers — bounded channel policy.
        }
    }

    private void SetConnectionStatus(string status)
    {
        if (_connectionStatus == status) return;
        _connectionStatus = status;
        ConnectionStateChanged?.Invoke(status);
    }

    /// <summary>
    /// Resolve <paramref name="host"/> to its first IPv4 address. The engine's <c>TcpExporter</c> binds
    /// <see cref="IPAddress.Any"/> (IPv4-only); going through <c>TcpClient.ConnectAsync(string, int)</c>
    /// resolves to <c>[::1, 127.0.0.1]</c> on Windows for "localhost", attempts <c>::1</c> first, and burns
    /// ~2 s on Happy-Eyeballs fallback before reaching the IPv4 listener. Pinning to
    /// <see cref="AddressFamily.InterNetwork"/> here skips that round-trip entirely.
    /// </summary>
    private static async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
        {
            return literal;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
        }
        catch (SocketException ex)
        {
            throw new WorkbenchException(
                StatusCodes.Status503ServiceUnavailable,
                "attach_dns_failed",
                $"Failed to resolve '{host}' to an IPv4 address: {ex.Message}",
                ex);
        }

        if (addresses.Length == 0)
        {
            throw new WorkbenchException(
                StatusCodes.Status503ServiceUnavailable,
                "attach_dns_failed",
                $"Host '{host}' resolved to zero IPv4 addresses. The Workbench connects to the engine over IPv4 only.");
        }

        return addresses[0];
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        // IPv6 literals are bracketed ("[::1]:9100"); we don't accept those yet — keep parsing simple.
        var idx = trimmed.LastIndexOf(':');
        if (idx < 0) return (trimmed, DefaultPort);
        var host = trimmed[..idx];
        var portStr = trimmed[(idx + 1)..];
        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
        {
            throw new WorkbenchException(
                StatusCodes.Status400BadRequest,
                "invalid_endpoint",
                $"Invalid port '{portStr}' in endpoint '{endpoint}'. Expected host:port (1..65535).");
        }
        return (host, port);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        if (!_metadataTcs.Task.IsCompleted)
        {
            _metadataTcs.TrySetException(new ObjectDisposedException(nameof(AttachSessionRuntime)));
        }
        foreach (var kv in _subscribers)
        {
            try { kv.Value.Writer.TryComplete(); } catch { }
        }
        _subscribers.Clear();
    }
}

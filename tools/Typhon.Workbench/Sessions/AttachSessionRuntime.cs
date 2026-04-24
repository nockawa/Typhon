using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions.Profiler;
using ProfilerRecordDecoder = Typhon.Workbench.Sessions.Profiler.RecordDecoder;

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
/// across the reconnect boundary. This matches the behaviour of the old <c>Typhon.Profiler.Server</c> —
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

        TcpClient tcp = null;
        Exception lastError = null;
        for (var attempt = 0; attempt < ConnectRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(host, port, ct);
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (socket)
                {
                    await ProcessStreamAsync(socket.GetStream(), ct);
                }
                // Graceful stream end (Shutdown frame, clean close).
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                // Engine dropped — reconnect.
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

            socket = null;
            if (ct.IsCancellationRequested) break;

            SetConnectionStatus("reconnecting");
            _decoder = null;

            // Silent reconnect loop — 2 s between attempts until we succeed or the runtime is disposed.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectDelayMs, ct);
                    var next = new TcpClient();
                    await next.ConnectAsync(_host, _port, ct);
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

    private async Task ProcessStreamAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuf = new byte[LiveStreamProtocol.FrameHeaderSize];

        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(headerBuf, ct);
            var (type, length) = LiveStreamProtocol.ReadFrameHeader(headerBuf);

            if (length < 0 || length > MaxFrameBytes)
            {
                LogMalformedFrame(type.ToString(), $"invalid length {length}");
                return;
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
                        return;

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

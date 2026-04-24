using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Typhon.Engine.Profiler;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions.Profiler;
using ProfilerRecordDecoder = Typhon.Workbench.Sessions.Profiler.RecordDecoder;
using ProfilerCacheBuilder = Typhon.Workbench.Sessions.Profiler.TraceFileCacheBuilder;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Manages the lifecycle of a trace-session runtime: background cache build, metadata projection, lazy chunk reads.
/// Mirrors <see cref="EngineLifecycle"/>'s role for <see cref="OpenSession"/>, but for recorded traces (no engine hosted).
/// </summary>
/// <remarks>
/// <para>
/// <b>Async build.</b> <see cref="Start"/> is a synchronous factory that kicks off a background <see cref="Task"/> and
/// returns immediately. Clients poll <see cref="Metadata"/> (null until build completes) or subscribe to
/// <see cref="BuildProgressChanged"/> via the profiler build-progress SSE endpoint.
/// </para>
/// <para>
/// <b>Disposal.</b> Cancels the background build, disposes the cache reader. Safe to call multiple times.
/// </para>
/// </remarks>
public sealed partial class TraceSessionRuntime : IDisposable
{
    /// <summary>Public event-args shape for <see cref="BuildProgressChanged"/>. Neutral of internal builder types.</summary>
    public readonly record struct BuildProgressEventArgs(long BytesRead, long TotalBytes, int TickCount, long EventCount);

    private readonly string _filePath;
    private readonly string _cachePath;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ProfilerMetadataDto> _metadataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TraceFileCacheReader _reader;
    private long _timestampFrequency;
    private string _buildError;
    private bool _disposed;

    /// <summary>The source <c>.typhon-trace</c> path.</summary>
    public string FilePath => _filePath;

    /// <summary>The sidecar cache path (typically <c>&lt;filePath&gt;.typhon-trace-cache</c>).</summary>
    public string CacheFilePath => _cachePath;

    /// <summary>Projected metadata — null until the background build completes.</summary>
    public ProfilerMetadataDto Metadata { get; private set; }

    /// <summary>Task that resolves with the metadata DTO once the build completes, or faults on build error.</summary>
    public Task<ProfilerMetadataDto> MetadataReady => _metadataTcs.Task;

    /// <summary>True when the build has completed (success or failure).</summary>
    public bool IsBuildComplete => MetadataReady.IsCompleted;

    /// <summary>Source timestamp frequency (ticks/second from the source header). 0 until build completes.</summary>
    public long TimestampFrequency => _timestampFrequency;

    /// <summary>Fires every ~200 ms during build with progress counters. Also fires at phase transitions (done / error).</summary>
    public event Action<BuildProgressEventArgs> BuildProgressChanged;

    /// <summary>Fires exactly once when the build finishes (success). Subscribers receive the final metadata DTO.</summary>
    public event Action<ProfilerMetadataDto> BuildCompleted;

    /// <summary>Fires exactly once when the build fails. Subscribers receive the error message.</summary>
    public event Action<string> BuildFailed;

    private TraceSessionRuntime(string filePath, string cachePath, ILogger logger)
    {
        _filePath = filePath;
        _cachePath = cachePath;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new trace-session runtime. Throws <see cref="FileNotFoundException"/> synchronously if <paramref name="filePath"/>
    /// does not exist. Otherwise returns immediately — the sidecar cache is built on a background task.
    /// </summary>
    public static TraceSessionRuntime Start(string filePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Trace file not found.", fullPath);
        }

        var cachePath = ProfilerCacheBuilder.GetCachePathFor(fullPath);
        var runtime = new TraceSessionRuntime(fullPath, cachePath, logger);
        // Fault-continuation — BuildAsync already catches its own exceptions and faults the
        // metadata TCS, but if an unexpected error escapes its top-level try/catch the task becomes
        // unobserved. Logging it here gives us a diagnostic breadcrumb either way.
        _ = Task.Run(runtime.BuildAsync)
            .ContinueWith(
                t => runtime.LogBuildTaskFaulted(t.Exception!, fullPath),
                default,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return runtime;
    }

    /// <summary>
    /// Reads the raw LZ4-compressed bytes of a chunk. Awaits build completion (or rethrows the build error on fault).
    /// Returns a pooled array — caller is responsible for returning it via <see cref="ArrayPool{T}.Return"/> after use.
    /// </summary>
    /// <returns>(bytes, actual length). The pooled array may be larger than the actual data — use <paramref name="length"/>.</returns>
    public async ValueTask<(byte[] Bytes, int Length)> ReadChunkCompressedAsync(int chunkIdx)
    {
        ThrowIfDisposed();
        var metadata = await _metadataTcs.Task.ConfigureAwait(false);
        if (metadata == null || _reader == null)
        {
            throw new InvalidOperationException("Runtime not ready — build has not completed.");
        }
        if ((uint)chunkIdx >= (uint)_reader.ChunkManifest.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIdx),
                $"Chunk index {chunkIdx} out of range (manifest has {_reader.ChunkManifest.Count} entries).");
        }

        var entry = _reader.ChunkManifest[chunkIdx];
        var bytes = ArrayPool<byte>.Shared.Rent((int)entry.CacheByteLength);
        _reader.ReadChunkRaw(entry, bytes.AsSpan(0, (int)entry.CacheByteLength));
        return (bytes, (int)entry.CacheByteLength);
    }

    /// <summary>Returns the manifest entry for the given chunk — used by the controller to set response headers.</summary>
    public async ValueTask<ChunkManifestEntry> GetChunkManifestEntryAsync(int chunkIdx)
    {
        ThrowIfDisposed();
        var metadata = await _metadataTcs.Task.ConfigureAwait(false);
        if (metadata == null || _reader == null)
        {
            throw new InvalidOperationException("Runtime not ready — build has not completed.");
        }
        if ((uint)chunkIdx >= (uint)_reader.ChunkManifest.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIdx));
        }
        return _reader.ChunkManifest[chunkIdx];
    }

    private async Task BuildAsync()
    {
        try
        {
            var ct = _cts.Token;
            ct.ThrowIfCancellationRequested();

            // Step 1 — check existing cache freshness via fingerprint. If the cache file exists AND its fingerprint matches the source's
            // current fingerprint, skip the rebuild and reuse it. This keeps reopens under 100 ms for traces that haven't changed.
            var fingerprint = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(_filePath, fingerprint);

            var needsRebuild = true;
            if (File.Exists(_cachePath))
            {
                try
                {
                    using var probeStream = File.OpenRead(_cachePath);
                    using var probeReader = new TraceFileCacheReader(probeStream);
                    if (probeReader.VerifyFingerprint(fingerprint))
                    {
                        needsRebuild = false;
                    }
                }
                catch
                {
                    // Any open/read failure on the probe → rebuild. Old/incompatible cache versions land here.
                    needsRebuild = true;
                }
            }

            if (needsRebuild)
            {
                var progress = new Progress<ProfilerCacheBuilder.BuildProgress>(p =>
                {
                    BuildProgressChanged?.Invoke(new BuildProgressEventArgs(p.BytesRead, p.TotalBytes, p.TickCount, p.EventCount));
                });
                // Blocking synchronous call; Task.Run in Start already put us on a thread-pool thread, so no further scheduling needed.
                ProfilerCacheBuilder.Build(_filePath, _cachePath, progress);
            }

            // Step 2 — open the cache reader with a Windows-MMF-style retry loop (defense against fresh-write-then-read races on NTFS).
            _reader = await OpenCacheWithRetryAsync(_cachePath, ct);

            // Step 3 — project the metadata DTO. This is cheap (<10 ms even for 500K-tick traces) because the sections are already
            // loaded into memory by the reader's constructor.
            _timestampFrequency = ReadSourceTimestampFrequency(_filePath);
            var metadata = BuildMetadataDto(_reader, _filePath, _timestampFrequency, fingerprint);

            Metadata = metadata;
            _metadataTcs.TrySetResult(metadata);
            BuildCompleted?.Invoke(metadata);
        }
        catch (OperationCanceledException)
        {
            _metadataTcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _buildError = ex.Message;
            LogBuildFailed(ex, _filePath);
            _metadataTcs.TrySetException(ex);
            BuildFailed?.Invoke(ex.Message);
        }
    }

    private static async Task<TraceFileCacheReader> OpenCacheWithRetryAsync(string cachePath, CancellationToken ct)
    {
        // Mirrors EngineLifecycle.OpenAsync: 6 × 100ms retry on Windows-MMF transient sharing violations.
        const int maxAttempts = 6;
        const int retryDelayMs = 100;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = File.OpenRead(cachePath);
                return new TraceFileCacheReader(stream);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(retryDelayMs, ct);
            }
        }
    }

    private static long ReadSourceTimestampFrequency(string sourcePath)
    {
        using var fs = File.OpenRead(sourcePath);
        using var reader = new TraceFileReader(fs);
        var header = reader.ReadHeader();
        return header.TimestampFrequency;
    }

    /// <summary>
    /// Projects the cache reader's sections into a wire-ready metadata DTO. Source file is read once to extract system / archetype /
    /// component tables (which the cache doesn't keep — the builder only consumes them for aggregate computation).
    /// </summary>
    private static ProfilerMetadataDto BuildMetadataDto(
        TraceFileCacheReader reader,
        string sourcePath,
        long timestampFrequency,
        byte[] fingerprint)
    {
        // Read source tables (header is already read by ReadSourceTimestampFrequency, but we need the tables — re-open and walk).
        ProfilerHeaderDto headerDto;
        SystemDefinitionDto[] systems;
        ArchetypeDto[] archetypes;
        ComponentTypeDto[] componentTypes;

        using (var fs = File.OpenRead(sourcePath))
        using (var traceReader = new TraceFileReader(fs))
        {
            var h = traceReader.ReadHeader();
            headerDto = new ProfilerHeaderDto(
                Version: h.Version,
                TimestampFrequency: h.TimestampFrequency,
                BaseTickRate: h.BaseTickRate,
                WorkerCount: h.WorkerCount,
                SystemCount: h.SystemCount,
                ArchetypeCount: h.ArchetypeCount,
                ComponentTypeCount: h.ComponentTypeCount,
                CreatedUtcTicks: h.CreatedUtcTicks,
                SamplingSessionStartQpc: h.SamplingSessionStartQpc);

            var systemRecords = traceReader.ReadSystemDefinitions();
            systems = new SystemDefinitionDto[systemRecords.Count];
            for (var i = 0; i < systemRecords.Count; i++)
            {
                var sr = systemRecords[i];
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

            var archetypeRecords = traceReader.ReadArchetypes();
            archetypes = new ArchetypeDto[archetypeRecords.Count];
            for (var i = 0; i < archetypeRecords.Count; i++)
            {
                archetypes[i] = new ArchetypeDto(archetypeRecords[i].ArchetypeId, archetypeRecords[i].Name);
            }

            var componentRecords = traceReader.ReadComponentTypes();
            componentTypes = new ComponentTypeDto[componentRecords.Count];
            for (var i = 0; i < componentRecords.Count; i++)
            {
                componentTypes[i] = new ComponentTypeDto(componentRecords[i].ComponentTypeId, componentRecords[i].Name);
            }
        }

        // Tick summaries, manifest, metrics, aggregates all come from the cache reader (already in memory).
        var tickSummaries = new TickSummaryDto[reader.TickSummaries.Count];
        for (var i = 0; i < reader.TickSummaries.Count; i++)
        {
            var ts = reader.TickSummaries[i];
            tickSummaries[i] = new TickSummaryDto(
                TickNumber: ts.TickNumber,
                StartUs: ts.StartUs,
                DurationUs: ts.DurationUs,
                EventCount: ts.EventCount,
                MaxSystemDurationUs: ts.MaxSystemDurationUs,
                ActiveSystemsBitmask: ts.ActiveSystemsBitmask.ToString());
        }

        var manifest = new ChunkManifestEntryDto[reader.ChunkManifest.Count];
        for (var i = 0; i < reader.ChunkManifest.Count; i++)
        {
            var e = reader.ChunkManifest[i];
            var isContinuation = (e.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;
            manifest[i] = new ChunkManifestEntryDto(
                FromTick: e.FromTick,
                ToTick: e.ToTick,
                EventCount: e.EventCount,
                IsContinuation: isContinuation);
        }

        var metrics = reader.GlobalMetrics;
        var systemAggregates = new SystemAggregateDto[reader.SystemAggregates.Count];
        for (var i = 0; i < reader.SystemAggregates.Count; i++)
        {
            var sa = reader.SystemAggregates[i];
            systemAggregates[i] = new SystemAggregateDto(sa.SystemIndex, sa.InvocationCount, sa.TotalDurationUs);
        }

        var globalMetrics = new GlobalMetricsDto(
            GlobalStartUs: metrics.GlobalStartUs,
            GlobalEndUs: metrics.GlobalEndUs,
            MaxTickDurationUs: metrics.MaxTickDurationUs,
            MaxSystemDurationUs: metrics.MaxSystemDurationUs,
            P95TickDurationUs: metrics.P95TickDurationUs,
            TotalEvents: metrics.TotalEvents,
            TotalTicks: metrics.TotalTicks,
            SystemAggregates: systemAggregates);

        // GC suspensions — walk every chunk once, filter GcSuspension records. Ported from old TraceSessionService.
        // baselineQpc is 0 for file-based traces (the startTs field is already a QPC value in the same time base as the summaries).
        var gcSuspensions = ComputeGcSuspensions(reader, baselineQpc: 0, timestampFrequency);

        var fingerprintHex = Convert.ToHexString(fingerprint);

        return new ProfilerMetadataDto(
            Fingerprint: fingerprintHex,
            Header: headerDto,
            Systems: systems,
            Archetypes: archetypes,
            ComponentTypes: componentTypes,
            SpanNames: new Dictionary<int, string>(reader.SpanNames),
            GlobalMetrics: globalMetrics,
            TickSummaries: tickSummaries,
            ChunkManifest: manifest,
            GcSuspensions: gcSuspensions);
    }

    /// <summary>
    /// Walk every chunk in the cache reader, extracting GC-suspension records. Cached once per session in the returned array
    /// (the runtime's <see cref="Metadata"/> holds it, so this is called exactly once per build).
    /// </summary>
    private static GcSuspensionDto[] ComputeGcSuspensions(TraceFileCacheReader reader, long baselineQpc, long timestampFrequency)
    {
        if (timestampFrequency <= 0 || reader.ChunkManifest.Count == 0)
        {
            return [];
        }

        var maxCompressed = 0;
        var maxUncompressed = 0;
        foreach (var entry in reader.ChunkManifest)
        {
            if ((int)entry.CacheByteLength > maxCompressed) maxCompressed = (int)entry.CacheByteLength;
            if ((int)entry.UncompressedBytes > maxUncompressed) maxUncompressed = (int)entry.UncompressedBytes;
        }
        if (maxUncompressed == 0) return [];

        var compressedScratch = ArrayPool<byte>.Shared.Rent(maxCompressed);
        var uncompressedScratch = ArrayPool<byte>.Shared.Rent(maxUncompressed);
        var result = new List<GcSuspensionDto>();
        try
        {
            foreach (var entry in reader.ChunkManifest)
            {
                var compSpan = compressedScratch.AsSpan(0, (int)entry.CacheByteLength);
                var uncompSpan = uncompressedScratch.AsSpan(0, (int)entry.UncompressedBytes);
                reader.DecompressChunk(entry, uncompSpan, compSpan);
                WalkRecordsForSuspensions(uncompSpan, baselineQpc, timestampFrequency, result);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedScratch);
            ArrayPool<byte>.Shared.Return(uncompressedScratch);
        }

        result.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));
        return result.ToArray();
    }

    private static void WalkRecordsForSuspensions(
        ReadOnlySpan<byte> records,
        long baselineQpc,
        long timestampFrequency,
        List<GcSuspensionDto> sink)
    {
        var pos = 0;
        while (pos + 3 <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size == 0 || size == 0xFFFF) break;
            if (pos + size > records.Length) break;
            var kind = (TraceEventKind)records[pos + 2];
            if (kind == TraceEventKind.GcSuspension)
            {
                var data = GcSuspensionEventCodec.Decode(records.Slice(pos, size));
                var startUs = (data.StartTimestamp - baselineQpc) * 1_000_000.0 / timestampFrequency;
                var durationUs = data.DurationTicks * 1_000_000.0 / timestampFrequency;
                sink.Add(new GcSuspensionDto(startUs, durationUs, data.ThreadSlot));
            }
            pos += size;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TraceSessionRuntime));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
    }
}

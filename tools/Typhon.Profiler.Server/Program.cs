using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.ResponseCompression;
using Typhon.Profiler;
using Typhon.Profiler.Server;

var builder = WebApplication.CreateBuilder(args);

// Apply the same JSON conventions to every Minimal API result: camelCase naming + omit null-valued properties from the wire.
// Without this, `Results.Ok(dto)` serializes every nullable field on LiveTraceEvent as `"fieldName":null`, bloating the payload ~10× for records
// that only use 2–3 of the ~25 optional fields. With it, a PageCacheFetch DTO shrinks from ~800 B to ~150 B on the wire.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// Response compression — narrow scope: application/json only (the SPA bundle is 68 KB; compressing it on localhost isn't worth the middleware
// overhead per request). Brotli registered before Gzip so it wins Accept-Encoding negotiation. Fastest level because the win here is wire-size
// on multi-hundred-MB payloads, not maximum compression ratio. Note: `text/event-stream` is NOT in the whitelist, so SSE stays uncompressed.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["application/json"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// CORS for local dev (Vite dev server runs on a different port)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});

// Live streaming service — connects to engine TCP port, fans out via SSE
builder.Services.AddSingleton<LiveSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveSessionService>());

// File trace session service — owns per-path sidecar caches, builds them on demand, hands out readers.
builder.Services.AddSingleton<TraceSessionService>();

var app = builder.Build();

// Per-process upload directory under %TEMP%. Using the process ID as a subdirectory ensures concurrent server instances
// never collide and — more importantly — lets the shutdown cleanup handler wipe this *exact* directory without the risk of
// trampling files owned by another running instance. Uploads from /api/trace/upload land here; Open-from-path traces do not
// touch this directory at all, so sidecar caches next to source files are never at risk of being deleted by the cleanup pass.
var uploadDir = Path.Combine(Path.GetTempPath(), "typhon-profiler", Environment.ProcessId.ToString());
Directory.CreateDirectory(uploadDir);

// Register shutdown cleanup. ApplicationStopping fires during graceful shutdown (Ctrl+C, SIGTERM, IIS recycle). Crashes and
// kills bypass this — those leak the directory, which is acceptable: subsequent runs get a fresh PID-stamped dir, and the
// parent `typhon-profiler` folder just accumulates a handful of orphaned subdirs that the OS's own TEMP cleanup will reap.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        if (Directory.Exists(uploadDir))
        {
            Directory.Delete(uploadDir, recursive: true);
        }
    }
    catch
    {
        // Best-effort cleanup — don't block shutdown on filesystem errors (file-locked by another process, permission issue).
    }
});

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
app.UseCors();
app.UseResponseCompression();

// Serve static files from the client build output (production). Index.html references hashed-filename bundles, so the bundles themselves
// are safe to cache forever (different hash → different URL). But index.html ITSELF must never be cached — otherwise a rebuild that changes
// the bundle hash won't propagate to a user until their browser's heuristic cache expires (hours to days). This explicit Cache-Control on
// index.html guarantees every page load hits the latest bundle reference.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
        else if (path.Contains('-') && (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
        {
            // Hashed bundle (e.g., index-DdU7BA6X.js) — filename changes on every rebuild, safe to cache forever.
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    }
});

// ══════════════════════════════════════════════════════════════
// Metadata DTO builders — shared between REST and SSE paths
// ══════════════════════════════════════════════════════════════

static object BuildHeaderDto(TraceFileHeader header) => new
{
    version = header.Version,
    timestampFrequency = header.TimestampFrequency,
    baseTickRate = header.BaseTickRate,
    workerCount = header.WorkerCount,
    systemCount = header.SystemCount,
    archetypeCount = header.ArchetypeCount,
    componentTypeCount = header.ComponentTypeCount,
    createdUtc = new DateTime(header.CreatedUtcTicks, DateTimeKind.Utc),
    samplingSessionStartQpc = header.SamplingSessionStartQpc,
};

// DTO builders — LINQ-free so the per-request cost of /api/trace/open and /api/live/metadata is a direct copy rather than an allocated
// iterator chain. Takes IReadOnlyList<T> (matches what TraceFileReader exposes) and returns an eagerly-built object[] — System.Text.Json
// walks it exactly once, no enumerator overhead.
static object[] BuildSystemDtos(IReadOnlyList<SystemDefinitionRecord> systems)
{
    var result = new object[systems.Count];
    for (var i = 0; i < systems.Count; i++)
    {
        var s = systems[i];
        result[i] = new
        {
            index = s.Index,
            name = s.Name,
            type = s.Type,
            priority = s.Priority,
            isParallel = s.IsParallel,
            tierFilter = s.TierFilter,
            predecessors = s.Predecessors,
            successors = s.Successors,
        };
    }
    return result;
}

static object[] BuildArchetypeDtos(IReadOnlyList<ArchetypeRecord> archetypes)
{
    var result = new object[archetypes.Count];
    for (var i = 0; i < archetypes.Count; i++)
    {
        var a = archetypes[i];
        result[i] = new { archetypeId = a.ArchetypeId, name = a.Name };
    }
    return result;
}

static object[] BuildComponentTypeDtos(IReadOnlyList<ComponentTypeRecord> componentTypes)
{
    var result = new object[componentTypes.Count];
    for (var i = 0; i < componentTypes.Count; i++)
    {
        var c = componentTypes[i];
        result[i] = new { componentTypeId = c.ComponentTypeId, name = c.Name };
    }
    return result;
}

// ══════════════════════════════════════════════════════════════
// REST API — Trace file operations
// ══════════════════════════════════════════════════════════════

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.2.0" }));

/// <summary>Upload trace files (multipart form: 'trace' = .typhon-trace, optional 'nettrace' = .nettrace).</summary>
// Hard upper bound on uploaded trace payloads. A Typhon trace is normally a few MB to a few hundred MB; 8 GB leaves room for
// very long-running production captures while denying "upload a 1 TB sparse file to fill the disk" style abuse. Enforced BEFORE
// reading the body — rejecting at the framework layer is preferable to rejecting after we've already begun streaming to disk.
const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;  // 8 GB

app.MapPost("/api/trace/upload", async (HttpRequest request) =>
{
    // Per-file and total-size cap. Check the Content-Length header BEFORE ReadFormAsync — a malicious client can otherwise
    // stream indefinitely. The ReadForm path has its own form-options limits (ASP.NET Core's MultipartBodyLengthLimit), but the
    // default is 128 MB and silently truncates without a clean error; an explicit front-line check is clearer and also works
    // when the client sends no Content-Length (chunked) — in that case we fall back to a per-file stream-length check below.
    if (request.ContentLength is long declared && declared > MaxUploadBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var form = await request.ReadFormAsync();

    var traceFile = form.Files.GetFile("trace");
    if (traceFile == null)
    {
        return Results.BadRequest(new { error = "No .typhon-trace file in upload" });
    }

    // Per-file size cap too — a request with chunked encoding might slip past the Content-Length check above. IFormFile exposes
    // the declared payload length after the framework has parsed the multipart headers; reject before opening the disk stream.
    if (traceFile.Length > MaxUploadBytes || (form.Files.GetFile("nettrace") is { } nf && nf.Length > MaxUploadBytes))
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var sessionId = Guid.NewGuid().ToString();
    var tempTracePath = Path.Combine(uploadDir, $"{sessionId}.typhon-trace");

    // Wrap each CopyToAsync in try/catch that deletes the partial file on failure. Without this, a client abort mid-upload leaves a
    // half-written .typhon-trace that only the graceful-shutdown cleanup sweep would eventually reap — long-running server accumulates cruft.
    try
    {
        await using var fs = File.Create(tempTracePath);
        await traceFile.CopyToAsync(fs);
    }
    catch
    {
        FileOps.TryDelete(tempTracePath);
        throw;
    }

    var nettraceFile = form.Files.GetFile("nettrace");
    string tempNettracePath = null;
    if (nettraceFile != null)
    {
        tempNettracePath = Path.Combine(uploadDir, $"{sessionId}.nettrace");
        try
        {
            await using var fs = File.Create(tempNettracePath);
            await nettraceFile.CopyToAsync(fs);
        }
        catch
        {
            FileOps.TryDelete(tempNettracePath);
            FileOps.TryDelete(tempTracePath);     // Roll back the earlier successful trace upload too — the pair is a single logical session.
            throw;
        }
    }

    using var reader = new TraceFileReader(File.OpenRead(tempTracePath));
    var header = reader.ReadHeader();
    var systems = reader.ReadSystemDefinitions();
    var archetypes = reader.ReadArchetypes();
    var componentTypes = reader.ReadComponentTypes();

    return Results.Ok(new
    {
        path = tempTracePath,
        metadata = new
        {
            header = BuildHeaderDto(header),
            systems = BuildSystemDtos(systems),
            archetypes = BuildArchetypeDtos(archetypes),
            componentTypes = BuildComponentTypeDtos(componentTypes),
        }
    });
});

/// <summary>Load a trace file and return metadata (header + system/archetype/component-type tables).</summary>
app.MapGet("/api/trace/metadata", (string path) =>
{
    var validated = PathGuard.ValidateTracePath(path, ".typhon-trace");
    if (validated == null)
    {
        return Results.NotFound(new { error = "Trace file not found or invalid path", path });
    }

    using var reader = new TraceFileReader(File.OpenRead(validated));
    var header = reader.ReadHeader();
    var systems = reader.ReadSystemDefinitions();
    var archetypes = reader.ReadArchetypes();
    var componentTypes = reader.ReadComponentTypes();

    return Results.Ok(new
    {
        header = BuildHeaderDto(header),
        systems = BuildSystemDtos(systems),
        archetypes = BuildArchetypeDtos(archetypes),
        componentTypes = BuildComponentTypeDtos(componentTypes),
    });
});

/// <summary>
/// Open a trace file for viewing: builds the sidecar cache on first call, hits the cache on subsequent calls. Returns metadata + per-tick
/// summaries + global metrics in one response so the viewer can render the timeline overview instantly without any detail-chunk loads.
/// </summary>
app.MapGet("/api/trace/open", (string path, TraceSessionService sessions) =>
{
    var validated = PathGuard.ValidateTracePath(path, ".typhon-trace");
    if (validated == null)
    {
        return Results.NotFound(new { error = "Trace file not found or invalid path", path });
    }

    // Read source metadata (small, fast — reads only the file's header/tables, not the record stream).
    using var sourceReader = new TraceFileReader(File.OpenRead(validated));
    var header = sourceReader.ReadHeader();
    var systems = sourceReader.ReadSystemDefinitions();
    var archetypes = sourceReader.ReadArchetypes();
    var componentTypes = sourceReader.ReadComponentTypes();

    // Build/hit the sidecar cache. Synchronous for Phase 1 — a cold build for a 158 MB trace takes ~0.3-0.5 s; viewer opens happen infrequently
    // enough that request-thread blocking is acceptable. Phase 2 introduces progressive build + HTTP 202 semantics.
    var cacheReader = sessions.GetOrBuild(validated);

    // Pack tick summaries and system aggregates into simple JSON shapes. Even for 500K ticks the payload stays in the tens of MB range, which
    // the existing compression middleware handles cleanly. For Phase 2 we'll switch this to a binary summary endpoint to avoid per-tick JSON
    // overhead, but monolithic JSON is fine for Phase 1 correctness-validation.
    var summaries = new object[cacheReader.TickSummaries.Count];
    for (var i = 0; i < cacheReader.TickSummaries.Count; i++)
    {
        var s = cacheReader.TickSummaries[i];
        summaries[i] = new
        {
            tickNumber = s.TickNumber,
            startUs = s.StartUs,
            durationUs = s.DurationUs,
            eventCount = s.EventCount,
            maxSystemDurationUs = s.MaxSystemDurationUs,
            activeSystemsBitmask = s.ActiveSystemsBitmask.ToString(),
        };
    }

    var sysAggregates = new object[cacheReader.SystemAggregates.Count];
    for (var i = 0; i < cacheReader.SystemAggregates.Count; i++)
    {
        var a = cacheReader.SystemAggregates[i];
        sysAggregates[i] = new
        {
            systemIndex = a.SystemIndex,
            invocationCount = a.InvocationCount,
            totalDurationUs = a.TotalDurationUs,
        };
    }

    var gm = cacheReader.GlobalMetrics;

    // Chunk manifest — small (20-40 bytes per entry, typically ≤ 10K entries), goes straight into the JSON response so the client has the full
    // address book from the start. Client uses the manifest-index position (chunkIdx) to issue chunk requests against the endpoints.
    //
    // `isContinuation` is set for chunks emitted mid-tick by the intra-tick splitter (v8+). The client must seed its tick counter at FromTick
    // directly for continuation chunks rather than FromTick-1, so this flag is essential on the wire; without it the decoder mis-tags every
    // event in a continuation chunk by one tick.
    var manifest = new object[cacheReader.ChunkManifest.Count];
    for (var i = 0; i < cacheReader.ChunkManifest.Count; i++)
    {
        var c = cacheReader.ChunkManifest[i];
        manifest[i] = new
        {
            fromTick = c.FromTick,
            toTick = c.ToTick,
            eventCount = c.EventCount,
            isContinuation = (c.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0,
        };
    }

    // Full GC-suspension list at open time — lets the client compute a STABLE yMax for the GC pause-per-tick bar chart regardless of
    // which chunks are currently resident in the LRU cache. Without this, panning into a new region (which triggers chunk loads/evicts)
    // caused the chart scale to rescale every time a new chunk's suspensions arrived. Computed once per session-slot lifetime via
    // TraceSessionService.GetOrComputeGcSuspensions; subsequent /open calls reuse the cached list.
    var suspensionDtos = sessions.GetOrComputeGcSuspensions(validated, header.SamplingSessionStartQpc, header.TimestampFrequency);
    var suspensions = new object[suspensionDtos.Count];
    for (var i = 0; i < suspensionDtos.Count; i++)
    {
        var s = suspensionDtos[i];
        suspensions[i] = new { startUs = s.StartUs, durationUs = s.DurationUs, threadSlot = s.ThreadSlot };
    }

    return Results.Ok(new
    {
        status = "ready",
        // Hex-encoded source fingerprint (SHA-256 of mtime + length + first/last 4 KB). Stable for a given trace file content;
        // changes if the file is rebuilt. Client uses this as an invalidation key for its OPFS-backed chunk cache — same
        // fingerprint ⇒ cached chunks still valid; different fingerprint ⇒ cached chunks become orphaned (garbage-collected by
        // the client's global cleanup sweep).
        fingerprint = cacheReader.GetSourceFingerprintHex(),
        header = BuildHeaderDto(header),
        systems = BuildSystemDtos(systems),
        archetypes = BuildArchetypeDtos(archetypes),
        componentTypes = BuildComponentTypeDtos(componentTypes),
        spanNames = cacheReader.SpanNames,
        globalMetrics = new
        {
            globalStartUs = gm.GlobalStartUs,
            globalEndUs = gm.GlobalEndUs,
            maxTickDurationUs = gm.MaxTickDurationUs,
            maxSystemDurationUs = gm.MaxSystemDurationUs,
            p95TickDurationUs = gm.P95TickDurationUs,
            totalEvents = gm.TotalEvents,
            totalTicks = gm.TotalTicks,
            systemAggregates = sysAggregates,
        },
        tickSummaries = summaries,
        chunkManifest = manifest,
        gcSuspensions = suspensions,
    });
});

/// <summary>
/// Fetch one chunk's events as JSON. The chunk is identified by <paramref name="chunkIdx"/> — its position in the manifest returned by
/// <c>/api/trace/open</c>. Server reads the LZ4 payload from the sidecar cache, decompresses, walks it through <see cref="RecordDecoder"/>,
/// and returns the events as JSON. Continuation chunks (bit 0 of <see cref="ChunkManifestEntry.Flags"/>) seed the decoder at FromTick
/// directly instead of FromTick-1 so events carry the correct tick number for an intra-tick split.
/// </summary>
app.MapGet("/api/trace/chunk", (string path, int chunkIdx, TraceSessionService sessions) =>
{
    // Same threat model as the other endpoints: localhost is reachable by any browser the user visits, and the CORS policy
    // is wide-open. Without PathGuard here, a malicious page could probe arbitrary paths via this endpoint.
    var validated = PathGuard.ValidateTracePath(path, ".typhon-trace");
    if (validated == null)
    {
        return Results.NotFound(new { error = "Trace file not found or invalid path", path });
    }

    var cacheReader = sessions.GetOrBuild(validated);
    if ((uint)chunkIdx >= (uint)cacheReader.ChunkManifest.Count)
    {
        return Results.NotFound(new { error = "chunkIdx out of range", chunkIdx, manifestCount = cacheReader.ChunkManifest.Count });
    }
    var entry = cacheReader.ChunkManifest[chunkIdx];
    var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;

    // Rent buffers for compressed payload + uncompressed decoded bytes, size to the manifest entry's exact lengths.
    var compressedBuf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)entry.CacheByteLength);
    var uncompressedBuf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)entry.UncompressedBytes);
    try
    {
        cacheReader.DecompressChunk(entry, uncompressedBuf.AsSpan(0, (int)entry.UncompressedBytes), compressedBuf.AsSpan(0, (int)entry.CacheByteLength));

        var decoder = new RecordDecoder(GetSourceTimestampFrequency(validated));
        // Normal chunks start with a TickStart → seed at FromTick-1 so the TickStart's increment lands on FromTick.
        // Continuation chunks have no TickStart at the head → seed at FromTick directly so subsequent events land on FromTick.
        if (isContinuation) decoder.SetCurrentTickForContinuation((int)entry.FromTick);
        else                decoder.SetCurrentTick((int)entry.FromTick - 1);

        var events = new List<LiveTraceEvent>(capacity: (int)entry.EventCount);
        decoder.DecodeBlock(uncompressedBuf.AsSpan(0, (int)entry.UncompressedBytes), events);

        return Results.Ok(new
        {
            chunkIdx,
            fromTick = entry.FromTick,
            toTick = entry.ToTick,
            isContinuation,
            events,
        });
    }
    finally
    {
        System.Buffers.ArrayPool<byte>.Shared.Return(compressedBuf);
        System.Buffers.ArrayPool<byte>.Shared.Return(uncompressedBuf);
    }
});

/// <summary>
/// Binary variant of <c>/api/trace/chunk</c> — returns the RAW LZ4-compressed chunk bytes as application/octet-stream with metadata in HTTP
/// response headers. Clients decompress + decode in a Web Worker with the TypeScript <c>chunkDecoder</c>, skipping server-side JSON
/// serialization entirely. Wire savings: ~3-5× for typical chunks (JSON overhead vs compact binary records), plus zero server CPU spent on
/// JSON encode. Decoded records on the client still carry identical field sets to the legacy JSON path — the wire is what changes.
/// </summary>
/// <remarks>
/// Response headers carry everything a client needs to decode:
///   X-Chunk-From-Tick, X-Chunk-To-Tick — echo of the stored manifest entry's range
///   X-Chunk-Event-Count — record count, so the client can pre-size its output array
///   X-Chunk-Uncompressed-Bytes — output buffer size for LZ4 decompression
///   X-Chunk-Is-Continuation — "1" if this chunk starts mid-tick (from an intra-tick split); "0" otherwise. The client must
///     seed its tick counter differently based on this flag — see chunkDecoder.ts.
///   X-Timestamp-Frequency — ticks-per-second for the ts → µs conversion (avoids a second roundtrip)
/// </remarks>
app.MapGet("/api/trace/chunk-binary", (string path, int chunkIdx, HttpContext http, TraceSessionService sessions) =>
{
    // PathGuard — see identical block in /api/trace/chunk above. Chunk endpoints previously used a bare File.Exists, which
    // exposed a file-existence oracle + unbounded session-dictionary growth to any origin calling localhost.
    var validated = PathGuard.ValidateTracePath(path, ".typhon-trace");
    if (validated == null)
    {
        return Results.NotFound(new { error = "Trace file not found or invalid path", path });
    }

    var cacheReader = sessions.GetOrBuild(validated);
    if ((uint)chunkIdx >= (uint)cacheReader.ChunkManifest.Count)
    {
        return Results.NotFound(new { error = "chunkIdx out of range", chunkIdx, manifestCount = cacheReader.ChunkManifest.Count });
    }
    var entry = cacheReader.ChunkManifest[chunkIdx];
    var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;

    http.Response.Headers[ChunkBinaryHeaders.FromTick] = entry.FromTick.ToString();
    http.Response.Headers[ChunkBinaryHeaders.ToTick] = entry.ToTick.ToString();
    http.Response.Headers[ChunkBinaryHeaders.EventCount] = entry.EventCount.ToString();
    http.Response.Headers[ChunkBinaryHeaders.UncompressedBytes] = entry.UncompressedBytes.ToString();
    http.Response.Headers[ChunkBinaryHeaders.IsContinuation] = isContinuation ? "1" : "0";
    // Timestamp frequency is stamped on the session slot by TraceSessionService.GetOrBuild — this read is a dictionary lookup, no file I/O.
    // Fall back to the source-file read only if the cache miss (shouldn't happen since GetOrBuild was called just above).
    var ts = sessions.GetCachedTimestampFrequency(validated);
    if (ts == 0) ts = GetSourceTimestampFrequency(validated);
    http.Response.Headers[ChunkBinaryHeaders.TimestampFrequency] = ts.ToString();
    // Expose the custom headers to CORS — needed because the SPA may be served from a different origin during Vite dev. The string is
    // derived from the SAME names used in the setters above (see ChunkBinaryHeaders), so renaming or adding a header updates both sides
    // atomically — no drift between what we send and what we expose.
    http.Response.Headers["Access-Control-Expose-Headers"] = ChunkBinaryHeaders.ExposedHeadersList;

    // Stream the compressed payload directly to the response body from a pooled buffer — no intermediate byte[] allocation. The stream
    // callback runs AFTER headers above are flushed, and owns the ArrayPool rent/return cycle so the buffer isn't released before the write
    // completes. Previous impl allocated a fresh byte[] of the same size just to satisfy Results.Bytes's copy-in signature.
    var entryCopy = entry;
    return Results.Stream(async stream =>
    {
        var compressedBuf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)entryCopy.CacheByteLength);
        try
        {
            cacheReader.ReadChunkRaw(entryCopy, compressedBuf.AsSpan(0, (int)entryCopy.CacheByteLength));
            await stream.WriteAsync(compressedBuf.AsMemory(0, (int)entryCopy.CacheByteLength));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(compressedBuf);
        }
    }, "application/octet-stream");
});

// Helper: read just the source file's header to get TimestampFrequency. Needed for chunk decoding since the cache doesn't currently store it.
// Phase 2b candidate: add TimestampFrequency to the CacheHeader so we don't need to re-open the source file per chunk request.
static long GetSourceTimestampFrequency(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new TraceFileReader(stream);
    var header = reader.ReadHeader();
    return header.TimestampFrequency;
}

/// <summary>
/// Binary tick-summary feed. Returns the raw packed <see cref="TickSummary"/> array (24 bytes per entry) as application/octet-stream. Used for
/// progressive-overview polling in Phase 2; in Phase 1 the full summary is already included in /api/trace/open, so this endpoint is provided for
/// completeness + future use but is optional for viewers that already have the summary from open.
/// </summary>
app.MapGet("/api/trace/summary", (string path, TraceSessionService sessions) =>
{
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        return Results.NotFound(new { error = "Trace file not found", path });
    }

    var cacheReader = sessions.GetOrBuild(path);
    // Stream the summary directly from the reader's backing list — no ToArray() copy, no byte[] intermediate. For a 500K-tick summary that's
    // 500K × 32 B = 16 MB saved per /api/trace/summary request (previously allocated twice: once as TickSummary[], once as byte[]).
    //
    // Span<T> can't live across an await, so each iteration (1) takes the span synchronously, (2) copies up to 64 KB into a pooled byte[],
    // (3) awaits the write of that byte[] slice. The span is re-derived per iteration from the (list, offset) pair.
    var summaryList = cacheReader.TickSummaries;
    return Results.Stream(async stream =>
    {
        var entrySize = System.Runtime.InteropServices.Marshal.SizeOf<TickSummary>();
        var totalBytes = summaryList.Count * entrySize;
        const int MaxBytesPerWrite = 64 * 1024;
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(totalBytes, 1), MaxBytesPerWrite));
        try
        {
            var off = 0;
            while (off < totalBytes)
            {
                var n = Math.Min(MaxBytesPerWrite, totalBytes - off);
                // Synchronous scope — span lifetime ends before the await.
                if (summaryList is List<TickSummary> concrete)
                {
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(concrete);
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(span);
                    bytes.Slice(off, n).CopyTo(buf);
                }
                else
                {
                    // Defensive fallback — shouldn't hit since TraceFileCacheReader uses List<TickSummary>. Copy one entry at a time.
                    for (var i = 0; i < n; i += entrySize)
                    {
                        var idx = (off + i) / entrySize;
                        var one = summaryList[idx];
                        var src = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TickSummary>(in one));
                        src.CopyTo(buf.AsSpan(i, entrySize));
                    }
                }
                await stream.WriteAsync(buf.AsMemory(0, n));
                off += n;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }, "application/octet-stream");
});

// /api/trace/events removed in Phase 2: superseded by /api/trace/chunk (range-scoped) + /api/trace/open (manifest). Clients iterate the
// manifest and fetch chunks on demand instead of loading the entire trace in one response.

// ══════════════════════════════════════════════════════════════
// Flame Graph API — parses companion .nettrace file
// ══════════════════════════════════════════════════════════════

/// <summary>Build a flame graph from CPU samples in the companion .nettrace file.</summary>
app.MapGet("/api/trace/flamegraph", (string path, double? fromUs, double? toUs, int? threadId) =>
{
    // Validate + canonicalize the user-supplied trace path before deriving the sibling .nettrace path. Without this, a
    // payload like `path=../../../win.ini` turns into `nettracePath=../../../win.nettrace` and the File.Exists fallback
    // short-circuits back through user-land — a classic directory-traversal oracle.
    var validatedTrace = PathGuard.ValidateTracePath(path, ".typhon-trace");
    if (validatedTrace == null)
    {
        return Results.NotFound(new { error = "Trace file not found or invalid path", path });
    }
    var nettracePath = Path.ChangeExtension(validatedTrace, ".nettrace");
    if (!File.Exists(nettracePath))
    {
        return Results.NotFound(new { error = "No .nettrace file found (CPU sampling was not enabled)", nettracePath });
    }

    try
    {
        using var traceReader = new TraceFileReader(File.OpenRead(validatedTrace));
        var header = traceReader.ReadHeader();

        var from = fromUs ?? 0;
        var to = toUs ?? double.MaxValue;
        var tid = threadId ?? -1;

        var (root, totalSamples) = FlameGraphService.Build(
            nettracePath, header.SamplingSessionStartQpc, header.TimestampFrequency, from, to, tid);

        return Results.Ok(new
        {
            totalSamples,
            hasSamples = totalSamples > 0,
            root = FlameGraphService.ToSerializable(root)
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

// ══════════════════════════════════════════════════════════════
// Live streaming API — SSE events from engine via TCP
// ══════════════════════════════════════════════════════════════

app.MapGet("/api/live/status", (LiveSessionService live) =>
{
    return Results.Ok(new
    {
        connected = live.IsConnected,
        tickCount = live.TickCount,
    });
});

// Shared metadata DTO builder — used by both /api/live/metadata and the first /api/live/events payload.
// Important: do NOT enable response compression on the SSE endpoint. SSE streams need immediate bytes on the wire;
// compression buffers them and breaks real-time delivery.
// threadNames is carried here rather than as a separate SSE event so the client populates TraceMetadata.threadNames at session start
// without having to process kind-77 records through the tick pipeline. Delivering them as a synthetic tick batch breaks processTickEvents
// (no TickStart → startUs = Infinity → global time origin corruption), hence this metadata-side channel.
// initialGauges carries capacity/total gauges from the session's first PerTickSnapshot (kind 76) — those values are never re-emitted,
// so late subscribers would see them as zero/missing without this replay path. Same rationale as threadNames, different one-shot kind.
static object BuildLiveMetadataDto(LiveSessionState state, Dictionary<byte, string> threadNames, Dictionary<int, double> initialGauges) => new
{
    header = BuildHeaderDto(state.Header),
    systems = BuildSystemDtos(state.Systems),
    archetypes = BuildArchetypeDtos(state.Archetypes),
    componentTypes = BuildComponentTypeDtos(state.ComponentTypes),
    threadNames = threadNames,
    initialGauges = initialGauges,
};

app.MapGet("/api/live/metadata", (LiveSessionService live) =>
{
    var state = live.GetState();
    if (state == null)
    {
        return Results.NotFound(new { error = "No live session" });
    }
    return Results.Ok(BuildLiveMetadataDto(state, live.GetThreadNamesSnapshot(), live.GetInitialGaugesSnapshot()));
});

/// <summary>
/// Progressive cache-build feed. Subscribes to the build progress of <paramref name="path"/>'s sidecar cache. If the cache is already fresh
/// this endpoint fires <c>event: done</c> immediately and closes. If a build is needed, it kicks it off on the thread pool and forwards each
/// <c>BuildProgress</c> snapshot as an <c>event: progress</c> frame; the final frame is <c>event: done</c> on success or <c>event: error</c>
/// on failure. Clients should subscribe to this BEFORE calling <c>/api/trace/open</c> to surface a progress bar during slow first-opens.
/// </summary>
app.MapGet("/api/trace/build-progress", async (HttpContext context, string path, TraceSessionService sessions) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        var err = JsonSerializer.Serialize(new { message = "Path not found" }, jsonOpts);
        await context.Response.WriteAsync($"event: error\ndata: {err}\n\n");
        await context.Response.Body.FlushAsync();
        return;
    }

    // Bounded channel as the bridge from the builder's synchronous progress callbacks (running on the build thread) to the SSE writer loop
    // (running on the request thread). Capacity 32 is comfortably more than a second's worth of ~5/sec reports, and DropOldest means a slow
    // client browser never backpressures the builder itself — worst case we skip an intermediate progress frame.
    var channel = System.Threading.Channels.Channel.CreateBounded<TraceFileCacheBuilder.BuildProgress>(
        new System.Threading.Channels.BoundedChannelOptions(32)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    var progress = new ActionProgress<TraceFileCacheBuilder.BuildProgress>(p => channel.Writer.TryWrite(p));

    // Kick the build on the thread pool so the SSE writer loop (below) can start pumping progress events immediately. GetOrBuildWithProgress
    // serializes per-path under SessionSlot.Lock, so if another caller is already building this same trace, this Task will block on the lock
    // and the channel will stay empty — in that case the SSE stream just sits idle until the first build completes, then returns fast.
    var buildTask = Task.Run(() =>
    {
        try
        {
            sessions.GetOrBuildWithProgress(path, progress);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    });

    // Link RequestAborted with ApplicationStopping so the loop exits promptly on host shutdown — without this, the SSE endpoint holds the
    // host hostage for the full HostOptions.ShutdownTimeout (default 30 s) because RequestAborted only fires on client-side disconnect.
    using var ctSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
    var ct = ctSource.Token;
    try
    {
        await foreach (var p in channel.Reader.ReadAllAsync(ct))
        {
            var json = JsonSerializer.Serialize(p, jsonOpts);
            await context.Response.WriteAsync($"event: progress\ndata: {json}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }

        try
        {
            await buildTask;
            await context.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
        }
        catch (Exception ex)
        {
            var err = JsonSerializer.Serialize(new { message = ex.Message }, jsonOpts);
            await context.Response.WriteAsync($"event: error\ndata: {err}\n\n", ct);
        }
        await context.Response.Body.FlushAsync(ct);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected. The build task keeps running to completion on the thread pool — its result still lands in the cache file and
        // any subsequent open call will find it already built. Attach an observer for the task's exception so a build failure after client
        // disconnect doesn't surface as an UnobservedTaskException at GC time (log noise + FailFast on some configs).
        _ = buildTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
});

app.MapGet("/api/live/events", async (HttpContext context, LiveSessionService live) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    // Send metadata as first event if session is active.
    var state = live.GetState();
    if (state != null)
    {
        var meta = JsonSerializer.Serialize(BuildLiveMetadataDto(state, live.GetThreadNamesSnapshot(), live.GetInitialGaugesSnapshot()), jsonOpts);
        await context.Response.WriteAsync($"event: metadata\ndata: {meta}\n\n");
        await context.Response.Body.FlushAsync();
    }

    var (subId, reader) = live.Subscribe();
    try
    {
        // Link RequestAborted with ApplicationStopping so the SSE loop exits promptly when the host is shutting down. Without this the endpoint
        // holds the host hostage for the full graceful-shutdown timeout (~30 s) because the browser is still connected (so RequestAborted never
        // fires on shutdown). With it, Ctrl+C on the server exits in <1 s; the browser's EventSource sees a normal connection close and (with
        // the reconnect fix in liveSource.ts) auto-retries when the server comes back.
        using var ctSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var ct = ctSource.Token;

        while (!ct.IsCancellationRequested)
        {
            LiveTickBatch batch;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(5000);

                if (await reader.WaitToReadAsync(timeoutCts.Token))
                {
                    if (!reader.TryRead(out batch))
                    {
                        continue;
                    }
                }
                else
                {
                    break; // Channel completed
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — send heartbeat.
                await context.Response.WriteAsync($"event: heartbeat\ndata: {{\"status\":\"connected\"}}\n\n");
                await context.Response.Body.FlushAsync();
                continue;
            }

            var json = JsonSerializer.Serialize(batch, jsonOpts);
            await context.Response.WriteAsync($"event: tick\ndata: {json}\n\n");
            await context.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected
    }
    finally
    {
        live.Unsubscribe(subId);
    }
});

app.Run();

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> implementation. Unlike the framework's <see cref="Progress{T}"/>, this does NOT capture a
/// <see cref="SynchronizationContext"/> or post callbacks asynchronously — it calls the handler on whatever thread called <c>Report</c>.
/// Used by the progressive-build SSE endpoint, where we need strict ordering (progress frames must arrive in the order they were produced)
/// and we're already writing to a thread-safe Channel so no marshaling is needed.
/// </summary>
internal sealed class ActionProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public ActionProgress(Action<T> action) { _action = action; }
    public void Report(T value) => _action(value);
}

/// <summary>
/// Single source of truth for the custom response-header names on <c>/api/trace/chunk-binary</c>. Declaring them here lets both the setter
/// block and the <c>Access-Control-Expose-Headers</c> value be derived from the same list — a future rename or addition only needs one
/// edit, instead of two locations that could drift (and silently break CORS for the SPA when Vite dev-server runs on a different origin).
/// </summary>
internal static class ChunkBinaryHeaders
{
    public const string FromTick = "X-Chunk-From-Tick";
    public const string ToTick = "X-Chunk-To-Tick";
    public const string EventCount = "X-Chunk-Event-Count";
    public const string UncompressedBytes = "X-Chunk-Uncompressed-Bytes";
    public const string IsContinuation = "X-Chunk-Is-Continuation";
    public const string TimestampFrequency = "X-Timestamp-Frequency";

    /// <summary>Comma-separated list of every custom header, suitable for <c>Access-Control-Expose-Headers</c>.</summary>
    public static readonly string ExposedHeadersList = string.Join(", ", new[]
    {
        FromTick, ToTick, EventCount, UncompressedBytes, IsContinuation, TimestampFrequency,
    });
}

/// <summary>
/// Best-effort file deletion helpers. Used by the upload handler's rollback path — never throw into the caller's exception flow, since a
/// failed cleanup is strictly less important than surfacing the original upload error.
/// </summary>
internal static class FileOps
{
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Swallow — cleanup is best-effort. File-locked or permission-denied failures just leave the file for the shutdown sweep.
        }
    }
}

/// <summary>
/// Input-validation helpers for the HTTP surface. Every API endpoint that takes a user-controlled <c>path</c> query string
/// must route it through <see cref="ValidateTracePath"/> before touching the filesystem — otherwise a malicious client can
/// read arbitrary files via relative-path traversal (e.g., <c>path=../../etc/passwd</c>).
/// </summary>
internal static class PathGuard
{
    /// <summary>
    /// Validates that <paramref name="userPath"/> is a well-formed absolute path pointing at a file with one of the
    /// <paramref name="allowedExtensions"/>, resolves <c>..</c> segments via <see cref="Path.GetFullPath"/>, and returns the
    /// canonicalized absolute path. Returns <c>null</c> on any validation failure — callers reply with 400/404 on null.
    /// </summary>
    /// <remarks>
    /// <para><b>Threat model:</b> the server runs on localhost but CORS is wide-open, so a malicious webpage the user visits
    /// can make requests against <c>localhost:5000</c>. Without extension-whitelisting and <c>..</c>-segment resolution,
    /// <c>/api/trace/metadata?path=../../../secrets.txt</c> would happily read arbitrary files and echo portions back in error
    /// messages. The extension whitelist is the primary defense — our own event codec will fail on a non-trace file anyway, but
    /// rejecting at the boundary is simpler + denies any chance of timing-based file-existence oracles.</para>
    /// </remarks>
    public static string ValidateTracePath(string userPath, params string[] allowedExtensions)
    {
        if (string.IsNullOrWhiteSpace(userPath))
        {
            return null;
        }

        string normalized;
        try
        {
            // GetFullPath resolves `..` segments, normalizes slashes, and throws on invalid chars. Any exception here means
            // the input isn't a sane filesystem path — reject.
            normalized = Path.GetFullPath(userPath);
        }
        catch
        {
            return null;
        }

        // Extension whitelist — the primary defense. Users can only ever touch files with known profiler extensions,
        // which eliminates the "read /etc/passwd" style of attack outright.
        if (allowedExtensions is { Length: > 0 })
        {
            bool extensionOk = false;
            foreach (var ext in allowedExtensions)
            {
                if (normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    extensionOk = true;
                    break;
                }
            }
            if (!extensionOk)
            {
                return null;
            }
        }

        if (!File.Exists(normalized))
        {
            return null;
        }

        return normalized;
    }
}

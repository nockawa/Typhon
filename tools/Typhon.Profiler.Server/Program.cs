using System.Text.Json;
using System.Text.Json.Serialization;
using Typhon.Profiler;
using Typhon.Profiler.Server;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
app.UseCors();

// Serve static files from the client build output (production)
app.UseDefaultFiles();
app.UseStaticFiles();

// ══════════════════════════════════════════════════════════════
// REST API — Trace file operations
// ══════════════════════════════════════════════════════════════

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.1.0" }));

/// <summary>Upload trace files (multipart form: 'trace' = .typhon-trace, optional 'nettrace' = .nettrace).</summary>
app.MapPost("/api/trace/upload", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();

    var traceFile = form.Files.GetFile("trace");
    if (traceFile == null)
    {
        return Results.BadRequest(new { error = "No .typhon-trace file in upload" });
    }

    var tempDir = Path.Combine(Path.GetTempPath(), "typhon-profiler");
    Directory.CreateDirectory(tempDir);
    var sessionId = Guid.NewGuid().ToString();
    var tempTracePath = Path.Combine(tempDir, $"{sessionId}.typhon-trace");

    // Save .typhon-trace
    await using (var fs = File.Create(tempTracePath))
    {
        await traceFile.CopyToAsync(fs);
    }

    // Save companion .nettrace if provided
    var nettraceFile = form.Files.GetFile("nettrace");
    if (nettraceFile != null)
    {
        var tempNettracePath = Path.Combine(tempDir, $"{sessionId}.nettrace");
        await using var fs = File.Create(tempNettracePath);
        await nettraceFile.CopyToAsync(fs);
    }

    using var reader = new TraceFileReader(File.OpenRead(tempTracePath));
    var header = reader.ReadHeader();
    var systems = reader.ReadSystemDefinitions();
    reader.ReadSpanNames();

    return Results.Ok(new
    {
        path = tempTracePath,
        metadata = new
        {
            header = new
            {
                version = header.Version,
                timestampFrequency = header.TimestampFrequency,
                baseTickRate = header.BaseTickRate,
                workerCount = header.WorkerCount,
                systemCount = header.SystemCount,
                createdUtc = new DateTime(header.CreatedUtcTicks, DateTimeKind.Utc),
                samplingSessionStartQpc = header.SamplingSessionStartQpc
            },
            systems = systems.Select(s => new
            {
                index = s.Index,
                name = s.Name,
                type = s.Type,
                priority = s.Priority,
                isParallel = s.IsParallel,
                tierFilter = s.TierFilter,
                predecessors = s.Predecessors,
                successors = s.Successors
            })
        }
    });
});

/// <summary>Load a trace file and return metadata (header + system definitions).</summary>
app.MapGet("/api/trace/metadata", (string path) =>
{
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        return Results.NotFound(new { error = "Trace file not found", path });
    }

    using var reader = new TraceFileReader(File.OpenRead(path));
    var header = reader.ReadHeader();
    var systems = reader.ReadSystemDefinitions();

    return Results.Ok(new
    {
        header = new
        {
            version = header.Version,
            timestampFrequency = header.TimestampFrequency,
            baseTickRate = header.BaseTickRate,
            workerCount = header.WorkerCount,
            systemCount = header.SystemCount,
            createdUtc = new DateTime(header.CreatedUtcTicks, DateTimeKind.Utc)
        },
        systems = systems.Select(s => new
        {
            index = s.Index,
            name = s.Name,
            type = s.Type,
            priority = s.Priority,
            isParallel = s.IsParallel,
            tierFilter = s.TierFilter,
            predecessors = s.Predecessors,
            successors = s.Successors
        })
    });
});

/// <summary>Load all events from a trace file, optionally filtered by tick range.</summary>
app.MapGet("/api/trace/events", (string path, int? fromTick, int? toTick) =>
{
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        return Results.NotFound(new { error = "Trace file not found", path });
    }

    using var reader = new TraceFileReader(File.OpenRead(path));
    reader.ReadHeader();
    reader.ReadSystemDefinitions();
    reader.ReadSpanNames();
    var events = reader.ReadAllEvents();

    // Filter by tick range if specified
    if (fromTick.HasValue || toTick.HasValue)
    {
        var from = fromTick ?? 0;
        var to = toTick ?? int.MaxValue;
        events = events.Where(e => e.TickNumber >= from && e.TickNumber <= to).ToList();
    }

    var ticksPerUs = reader.Header.TimestampFrequency / 1_000_000.0;

    return Results.Ok(new
    {
        totalEvents = events.Count,
        spanNames = reader.SpanNames,
        events = events.Select(e => new
        {
            timestampUs = e.TimestampTicks / ticksPerUs,
            tickNumber = e.TickNumber,
            systemIndex = e.SystemIndex,
            chunkIndex = e.ChunkIndex,
            workerId = e.WorkerId,
            eventType = (int)e.EventType,
            phase = (int)e.Phase,
            skipReason = e.SkipReason,
            entitiesProcessed = e.EntitiesProcessed,
            payload = e.Payload
        })
    });
});

/// <summary>Export trace to Chrome Trace JSON format.</summary>
app.MapGet("/api/trace/export/chrome", (string path) =>
{
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        return Results.NotFound(new { error = "Trace file not found", path });
    }

    var memStream = new MemoryStream();
    ChromeTraceExporter.Export(path, memStream);
    memStream.Position = 0;

    return Results.File(memStream, "application/json", "trace.json");
});

// ══════════════════════════════════════════════════════════════
// Flame Graph API — parses companion .nettrace file
// ══════════════════════════════════════════════════════════════

/// <summary>Build a flame graph from CPU samples in the companion .nettrace file.</summary>
app.MapGet("/api/trace/flamegraph", (string path, double? fromUs, double? toUs, int? threadId) =>
{
    var nettracePath = Path.ChangeExtension(path, ".nettrace");
    if (!File.Exists(nettracePath))
    {
        return Results.NotFound(new { error = "No .nettrace file found (CPU sampling was not enabled)", nettracePath });
    }

    try
    {
        // Read the trace header for timestamp correlation
        using var traceReader = new TraceFileReader(File.OpenRead(path));
        var header = traceReader.ReadHeader();

        var from = fromUs ?? 0;
        var to = toUs ?? double.MaxValue;
        var tid = threadId ?? -1;

        var (root, totalSamples) = Typhon.Profiler.Server.FlameGraphService.Build(
            nettracePath, header.SamplingSessionStartQpc, header.TimestampFrequency, from, to, tid);

        return Results.Ok(new
        {
            totalSamples,
            hasSamples = totalSamples > 0,
            root = Typhon.Profiler.Server.FlameGraphService.ToSerializable(root)
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
        tickCount = live.TickCount
    });
});

// Shared metadata DTO builder — used by both /api/live/metadata and the first /api/live/events payload.
// Note for future maintainers: do NOT enable response compression (UseResponseCompression) on these
// endpoints. SSE streams need immediate bytes on the wire — compression buffers them and breaks real-time
// delivery. CORS/static-files middleware above is already configured to leave this path untouched.
static object BuildLiveMetadataDto(LiveSessionState state) => new
{
    header = new
    {
        version = state.Header.Version,
        timestampFrequency = state.Header.TimestampFrequency,
        baseTickRate = state.Header.BaseTickRate,
        workerCount = state.Header.WorkerCount,
        systemCount = state.Header.SystemCount,
        createdUtc = new DateTime(state.Header.CreatedUtcTicks, DateTimeKind.Utc),
        samplingSessionStartQpc = state.Header.SamplingSessionStartQpc
    },
    systems = state.Systems.Select(s => new
    {
        index = s.Index,
        name = s.Name,
        type = s.Type,
        priority = s.Priority,
        isParallel = s.IsParallel,
        tierFilter = s.TierFilter,
        predecessors = s.Predecessors,
        successors = s.Successors
    }),
    spanNames = state.SpanNames
};

app.MapGet("/api/live/metadata", (LiveSessionService live) =>
{
    var state = live.GetState();
    if (state == null)
    {
        return Results.NotFound(new { error = "No live session" });
    }

    return Results.Ok(BuildLiveMetadataDto(state));
});

app.MapGet("/api/live/events", async (HttpContext context, LiveSessionService live) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    // Send metadata as first event if session is active
    var state = live.GetState();
    if (state != null)
    {
        var meta = JsonSerializer.Serialize(BuildLiveMetadataDto(state), jsonOpts);

        await context.Response.WriteAsync($"event: metadata\ndata: {meta}\n\n");
        await context.Response.Body.FlushAsync();
    }

    var (subId, reader) = live.Subscribe();
    try
    {
        var ct = context.RequestAborted;

        while (!ct.IsCancellationRequested)
        {
            // Wait for data with a 5-second timeout for heartbeat
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
                // Timeout — send heartbeat
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

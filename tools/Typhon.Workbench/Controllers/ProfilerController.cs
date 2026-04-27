using System.Buffers;
using Microsoft.AspNetCore.Mvc;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Session-scoped profiler endpoints. Serves metadata + binary chunk payloads for the Workbench's Trace session mode.
/// Backed by the session's <see cref="TraceSessionRuntime"/>, which owns the sidecar cache reader.
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId:guid}/profiler")]
[Tags("Profiler")]
[RequireBootstrapToken]
[RequireSession]
public sealed class ProfilerController : ControllerBase
{
    /// <summary>
    /// Returns the full metadata DTO once the session is ready. For Trace sessions this means the sidecar cache build
    /// completed; for Attach sessions it means the first Init frame arrived. Returns 202 Accepted with an empty body
    /// while the session is still waiting — clients poll via TanStack Query <c>refetchInterval</c>, or for Trace mode
    /// they can subscribe to <c>GET /api/sessions/{id}/profiler/build-progress</c> for incremental UX, and for Attach
    /// mode <c>GET /api/sessions/{id}/profiler/stream</c> for live updates.
    /// </summary>
    [HttpGet("metadata")]
    public ActionResult<ProfilerMetadataDto> GetMetadata(Guid sessionId)
    {
        var session = HttpContext.Items["Session"];

        if (session is TraceSession trace)
        {
            var runtime = trace.Runtime;
            if (runtime.IsBuildComplete)
            {
                if (runtime.Metadata != null)
                {
                    return Ok(runtime.Metadata);
                }
                return Problem(
                    title: "trace_build_failed",
                    detail: "Trace cache build failed. See server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Build in flight — client should retry (TanStack Query handles this via refetchInterval).
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        if (session is AttachSession attach)
        {
            if (attach.Runtime.Metadata != null)
            {
                return Ok(attach.Runtime.Metadata);
            }
            // Init frame hasn't arrived yet — client retries.
            Response.Headers["Retry-After"] = "1";
            return StatusCode(StatusCodes.Status202Accepted);
        }

        return Conflict(new ProblemDetails
        {
            Title = "session_kind_mismatch",
            Detail = "Profiler metadata is only available for Trace and Attach sessions.",
            Status = StatusCodes.Status409Conflict,
        });
    }

    /// <summary>
    /// User-initiated disconnect for an Attach session. Drops the TCP connection to the engine and pins the
    /// runtime status to <c>disconnected</c>; the session itself stays alive in the <see cref="SessionManager"/>
    /// so the client can keep inspecting the captured tick buffer. Idempotent — repeated calls are 204 no-ops.
    /// To free the session entirely, the client should call <c>DELETE /api/sessions/{id}</c>.
    /// </summary>
    [HttpPost("disconnect")]
    public IActionResult Disconnect(Guid sessionId)
    {
        var session = HttpContext.Items["Session"];
        if (session is not AttachSession attach)
        {
            return Conflict(new ProblemDetails
            {
                Title = "session_kind_mismatch",
                Detail = "Disconnect is only valid on Attach sessions.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        attach.Runtime.RequestDisconnect();
        return NoContent();
    }

    /// <summary>
    /// Returns the raw LZ4-compressed bytes of a single chunk. Response headers carry everything the browser worker
    /// needs to decode the payload:
    /// <list type="bullet">
    ///   <item><c>X-Chunk-From-Tick</c> / <c>X-Chunk-To-Tick</c> — chunk's tick range (ToTick exclusive).</item>
    ///   <item><c>X-Chunk-Event-Count</c> — number of records in the chunk.</item>
    ///   <item><c>X-Chunk-Uncompressed-Bytes</c> — decompressed size (needed to size the output buffer).</item>
    ///   <item><c>X-Chunk-Is-Continuation</c> — <c>"1"</c> for mid-tick split chunks, <c>"0"</c> otherwise.</item>
    ///   <item><c>X-Timestamp-Frequency</c> — source Stopwatch frequency (ticks/sec) for µs conversion.</item>
    ///   <item><c>Access-Control-Expose-Headers</c> — lists the above so browsers expose them to JS.</item>
    /// </list>
    /// </summary>
    [HttpGet("chunks/{chunkIdx:int}")]
    public async Task GetChunk(Guid sessionId, int chunkIdx, CancellationToken ct)
    {
        var session = HttpContext.Items["Session"] as TraceSession;
        if (session == null)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var runtime = session.Runtime;
        if (!runtime.IsBuildComplete)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            await Response.WriteAsync("Build not complete — call /profiler/metadata first.", ct);
            return;
        }
        if (runtime.Metadata == null)
        {
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        ChunkManifestEntry entry;
        try
        {
            entry = await runtime.GetChunkManifestEntryAsync(chunkIdx);
        }
        catch (ArgumentOutOfRangeException)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;

        Response.Headers["X-Chunk-From-Tick"] = entry.FromTick.ToString();
        Response.Headers["X-Chunk-To-Tick"] = entry.ToTick.ToString();
        Response.Headers["X-Chunk-Event-Count"] = entry.EventCount.ToString();
        Response.Headers["X-Chunk-Uncompressed-Bytes"] = entry.UncompressedBytes.ToString();
        Response.Headers["X-Chunk-Is-Continuation"] = isContinuation ? "1" : "0";
        Response.Headers["X-Timestamp-Frequency"] = runtime.TimestampFrequency.ToString();
        Response.Headers["Access-Control-Expose-Headers"] = string.Join(", ", new[]
        {
            "X-Chunk-From-Tick",
            "X-Chunk-To-Tick",
            "X-Chunk-Event-Count",
            "X-Chunk-Uncompressed-Bytes",
            "X-Chunk-Is-Continuation",
            "X-Timestamp-Frequency",
        });
        Response.ContentType = "application/octet-stream";
        Response.ContentLength = (int)entry.CacheByteLength;

        var (bytes, length) = await runtime.ReadChunkCompressedAsync(chunkIdx);
        try
        {
            await Response.Body.WriteAsync(bytes.AsMemory(0, length), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}

using System.Text.Json;
using System.Threading.Channels;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE stream of <see cref="BuildProgressDto"/> frames while a Trace session's sidecar cache is being built.
/// Emits <c>progress</c> events throttled by the builder's 200 ms interval; emits a terminal <c>done</c> or
/// <c>error</c> event when the build finishes, then closes the stream.
/// </summary>
public static class ProfilerBuildProgressStream
{
    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
        CancellationToken ct)
    {
        // Dual-source auth: header first (server-to-server), URL sessionId fallback (browser EventSource).
        WbSession session = null;
        if (ctx.Request.Headers.TryGetValue("X-Session-Token", out var rawToken)
            && Guid.TryParse(rawToken, out var token)
            && sessions.TryGet(token, out var s))
        {
            session = s;
        }
        else if (sessions.TryGet(sessionId, out var s2))
        {
            session = s2;
        }

        if (session is not TraceSession trace)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        await ctx.Response.Body.FlushAsync(ct);

        var channel = Channel.CreateUnbounded<BuildProgressDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        Action<TraceSessionRuntime.BuildProgressEventArgs> progressHandler = args =>
        {
            channel.Writer.TryWrite(new BuildProgressDto(
                Phase: "building",
                BytesRead: args.BytesRead,
                TotalBytes: args.TotalBytes,
                TickCount: args.TickCount,
                EventCount: args.EventCount));
        };

        Action<ProfilerMetadataDto> completedHandler = _ =>
        {
            channel.Writer.TryWrite(new BuildProgressDto(Phase: "done"));
            channel.Writer.TryComplete();
        };

        Action<string> failedHandler = msg =>
        {
            channel.Writer.TryWrite(new BuildProgressDto(Phase: "error", Message: msg));
            channel.Writer.TryComplete();
        };

        // Subscribe BEFORE the terminal-state check. If the build completes between the subscribe
        // and the check, the completedHandler has already queued the terminal event into our
        // channel — FlushLoop will deliver it. Doing the check first (as an earlier version did)
        // produced a race where a build transitioning to complete in between was observed as
        // "not complete" but the event fired before the handler was subscribed, leaving the
        // client hanging on an SSE connection that never emitted a terminal.
        trace.Runtime.BuildProgressChanged += progressHandler;
        trace.Runtime.BuildCompleted += completedHandler;
        trace.Runtime.BuildFailed += failedHandler;

        try
        {
            // Terminal-fast-path: if the build ALREADY finished before we subscribed, seed the
            // channel with the terminal event manually. The event was fired before subscription
            // so we'd never receive it via the handler — but we know the outcome from the runtime's
            // state, so we synthesize the equivalent frame here.
            if (trace.Runtime.IsBuildComplete)
            {
                if (trace.Runtime.Metadata != null)
                {
                    channel.Writer.TryWrite(new BuildProgressDto(Phase: "done"));
                }
                else
                {
                    channel.Writer.TryWrite(new BuildProgressDto(Phase: "error", Message: "Build failed before stream connected."));
                }
                channel.Writer.TryComplete();
            }

            await FlushLoop(ctx, channel.Reader, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal client disconnect.
        }
        catch (IOException)
        {
            // Kestrel can surface forcible disconnect as IOException before the ct fires.
        }
        finally
        {
            trace.Runtime.BuildProgressChanged -= progressHandler;
            trace.Runtime.BuildCompleted -= completedHandler;
            trace.Runtime.BuildFailed -= failedHandler;
            channel.Writer.TryComplete();
        }
    }

    private static async Task FlushLoop(
        HttpContext ctx,
        ChannelReader<BuildProgressDto> reader,
        CancellationToken ct)
    {
        // All frames ship as default `message` events — the Workbench's generic useEventSource hook only listens to
        // onmessage. Terminal-state signal lives in the payload's `phase` field, which the client switches on.
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var evt))
            {
                var payload = JsonSerializer.Serialize(evt, SseJsonOptions.Web);
                await WriteDataAsync(ctx, payload, ct);
                if (evt.Phase is "done" or "error")
                {
                    return;
                }
            }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    private static async Task WriteDataAsync(HttpContext ctx, string data, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"data: {data}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}

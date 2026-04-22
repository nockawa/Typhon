using System.Text.Json;
using System.Threading.Channels;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Resources;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE stream pushing <see cref="ResourceGraphEventDto"/> frames whenever the session's engine
/// reports an <see cref="IResourceRegistry.NodeMutated"/> event. Rate-limited to ~10 frames per
/// second per session via a 100 ms flush interval that coalesces duplicate (Kind, NodeId) events
/// inside each window.
/// </summary>
public static class ResourceGraphStream
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
        CancellationToken ct)
    {
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

        if (session is not OpenSession open)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var registry = open.Engine.Registry;
        if (registry == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        // Force headers to flush now so clients don't block awaiting the first event frame.
        await ctx.Response.Body.FlushAsync(ct);

        var channel = Channel.CreateUnbounded<ResourceMutationEventArgs>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        Action<ResourceMutationEventArgs> handler = args =>
        {
            // Engine contract: handlers must not throw; if the channel is closed we simply drop.
            channel.Writer.TryWrite(args);
        };

        registry.NodeMutated += handler;
        try
        {
            await FlushLoop(ctx, channel.Reader, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal exit on client disconnect.
        }
        catch (IOException)
        {
            // Kestrel surfaces a forcible client disconnect as IOException before the request
            // cancellation token fires — treat identically.
        }
        finally
        {
            registry.NodeMutated -= handler;
            channel.Writer.TryComplete();
        }
    }

    private static async Task FlushLoop(
        HttpContext ctx,
        ChannelReader<ResourceMutationEventArgs> reader,
        CancellationToken ct)
    {
        // Latest-wins per (Kind, NodeId) within the window — coalesces bursts.
        var pending = new Dictionary<(ResourceMutationKind, string), ResourceMutationEventArgs>();

        // Leading-edge debounce: block on WaitToReadAsync so idle sessions don't burn wakeups, then
        // drain whatever arrived together with the first event, flush, and hold a short post-flush
        // window so follow-up bursts coalesce into the next frame.
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var evt))
            {
                pending[(evt.Kind, evt.NodeId)] = evt;
            }

            if (pending.Count == 0)
            {
                continue;
            }

            foreach (var evt in pending.Values)
            {
                var payload = JsonSerializer.Serialize(new ResourceGraphEventDto(
                    Kind: evt.Kind.ToString(),
                    NodeId: evt.NodeId,
                    ParentId: evt.ParentId,
                    Type: evt.Type.ToString(),
                    Timestamp: new DateTimeOffset(evt.Timestamp, TimeSpan.Zero)));
                await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
            pending.Clear();

            // Coalescing window — subsequent events during this delay batch into the next frame.
            await Task.Delay(FlushInterval, ct);
        }
    }
}

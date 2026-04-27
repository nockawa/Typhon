using System.Text.Json;
using System.Threading.Channels;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;
using Typhon.Profiler;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE stream of <see cref="LiveStreamEventDto"/> frames for an Attach session — the live counterpart to
/// <see cref="ProfilerBuildProgressStream"/>. Emits <c>metadata</c> on connect + on reconnect, <c>tick</c> per decoded
/// tick batch, and <c>heartbeat</c> on connection-state changes and on 5 s idle timeouts.
/// </summary>
public static class ProfilerLiveStream
{
    // JSON options shared across all SSE streams — see SseJsonOptions for the rationale.
    private static JsonSerializerOptions WireOpts => SseJsonOptions.Web;

    private const int HeartbeatTimeoutMs = 5000;

    public static async Task HandleAsync(
        Guid sessionId,
        HttpContext ctx,
        SessionManager sessions,
        CancellationToken ct)
    {
        // Dual-source auth: header first (server-to-server), URL sessionId fallback (browser EventSource can't set custom headers).
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

        if (session is not AttachSession attach)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        await ctx.Response.Body.FlushAsync(ct);

        var runtime = attach.Runtime;

        // Shared bounded channel. Capacity is a soft limit — DropOldest drops stale ticks first if a slow client
        // falls behind. Metadata and heartbeats are rare enough that they effectively never get dropped in practice.
        var channel = Channel.CreateBounded<LiveStreamEventDto>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

        Action<LiveTickBatch> tickHandler = batch =>
            channel.Writer.TryWrite(new LiveStreamEventDto(Kind: "tick", Tick: batch));

        Action<ProfilerMetadataDto> metaHandler = meta =>
            channel.Writer.TryWrite(new LiveStreamEventDto(Kind: "metadata", Metadata: meta));

        Action<string> stateHandler = status =>
            channel.Writer.TryWrite(new LiveStreamEventDto(Kind: "heartbeat", Status: status));

        runtime.TickReceived += tickHandler;
        runtime.MetadataReceived += metaHandler;
        runtime.ConnectionStateChanged += stateHandler;

        try
        {
            // Emit current state on connect so the client doesn't have to wait for the next event cycle.
            if (runtime.Metadata != null)
            {
                await WriteEventAsync(ctx, new LiveStreamEventDto(Kind: "metadata", Metadata: runtime.Metadata), ct);
            }
            // Replay the pre-tick (tickNumber == 0) batch — typically the catch-up ThreadInfo records
            // synthesized by the engine's TcpExporter on accept. The TCP read loop processes those bytes
            // before this SSE handler ever registers TickReceived, so the live event-firing path drops them.
            // Replaying the cached snapshot here is the same pattern as the Metadata snapshot above.
            if (runtime.MetadataTickBatch != null)
            {
                await WriteEventAsync(ctx, new LiveStreamEventDto(Kind: "tick", Tick: runtime.MetadataTickBatch), ct);
            }
            await WriteEventAsync(ctx, new LiveStreamEventDto(Kind: "heartbeat", Status: runtime.ConnectionStatus), ct);

            await DrainLoopAsync(ctx, channel.Reader, runtime, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal client disconnect.
        }
        catch (IOException)
        {
            // Kestrel can surface forcible disconnect as IOException before ct fires.
        }
        finally
        {
            runtime.TickReceived -= tickHandler;
            runtime.MetadataReceived -= metaHandler;
            runtime.ConnectionStateChanged -= stateHandler;
            channel.Writer.TryComplete();
        }
    }

    private static async Task DrainLoopAsync(
        HttpContext ctx,
        ChannelReader<LiveStreamEventDto> reader,
        AttachSessionRuntime runtime,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LiveStreamEventDto evt = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HeartbeatTimeoutMs);

                if (await reader.WaitToReadAsync(timeout.Token))
                {
                    if (!reader.TryRead(out evt))
                    {
                        continue;
                    }
                }
                else
                {
                    // Writer completed — stream over.
                    return;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 5 s without an event — emit an idle heartbeat with the current status.
                await WriteEventAsync(
                    ctx,
                    new LiveStreamEventDto(Kind: "heartbeat", Status: runtime.ConnectionStatus),
                    ct);
                continue;
            }

            if (evt != null)
            {
                await WriteEventAsync(ctx, evt, ct);
            }
        }
    }

    private static async Task WriteEventAsync(HttpContext ctx, LiveStreamEventDto evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, WireOpts);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}

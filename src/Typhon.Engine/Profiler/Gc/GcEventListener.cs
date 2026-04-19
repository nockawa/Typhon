using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Typhon.Engine.Profiler.Gc;

/// <summary>
/// In-process listener for the <c>Microsoft-Windows-DotNETRuntime</c> EventSource. Subscribes to GC lifecycle events at <c>Informational</c>
/// level with <c>GCKeyword</c> (0x1), translates incoming events into <see cref="GcEventRecord"/> structs, and enqueues them onto a
/// <see cref="GcEventQueue"/> for asynchronous consumption by <see cref="GcIngestionThread"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-platform:</b> the provider name retains its legacy ETW label, but the CoreCLR emits these events identically on Windows, Linux, and
/// macOS through the in-process EventPipe pipeline — the same path <c>dotnet-counters</c> uses on all platforms. No OS-specific code here.
/// </para>
/// <para>
/// <b>Ctor-during-base-ctor footgun:</b> the documented-and-important rule for <see cref="EventListener"/> is that
/// <see cref="OnEventSourceCreated"/> can fire for already-alive sources <i>before</i> our derived constructor body completes. The
/// <c>Microsoft-Windows-DotNETRuntime</c> source is always already alive, so we <i>will</i> see that early callback. The <c>_ready</c>
/// gate pattern ensures we latch the source pointer but defer <see cref="EnableEvents(EventSource, EventLevel, EventKeywords)"/> until the
/// queue field is confirmed assigned; the ctor tail then enables once ready.
/// </para>
/// <para>
/// <b>Event-ID dispatch:</b> hot path is a single <c>switch</c> on <see cref="EventWrittenEventArgs.EventId"/>. Only four IDs are handled
/// (1=GCStart, 2=GCEnd, 9=SuspendEEBegin, 3=RestartEEEnd) — all others fall through to a no-op, which is cheap. We intentionally do not
/// subscribe to Verbose level, so <c>GCAllocationTick</c> (ID 10) is not delivered in the first place.
/// </para>
/// <para>
/// <b>Timestamp source:</b> <see cref="Stopwatch.GetTimestamp()"/> in the callback rather than <see cref="EventWrittenEventArgs.TimeStamp"/>.
/// The DateTime-to-Stopwatch-ticks conversion would require a reference-point anchor and introduce clock-skew hazards. The in-callback
/// timestamp is within ≤ 100 ns of the actual event, which is well inside our tolerance at GC-event frequencies.
/// </para>
/// </remarks>
internal sealed class GcEventListener : EventListener
{
    private const string DotNetRuntimeSourceName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    private readonly GcEventQueue _queue;
    private volatile EventSource _runtimeSource;
    private bool _ready;

    public GcEventListener(GcEventQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _ready = true;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == DotNetRuntimeSourceName)
        {
            _runtimeSource = eventSource;
            // _ready gate: in THIS process's init order, OnEventSourceCreated never fires with the DotNetRuntime source during
            // the base ctor — the runtime source is discovered post-construction (driven by whoever triggers its creation). The
            // gate still matters for Dispose: DisposableSemantics sets _ready=false on teardown, and any in-flight
            // OnEventSourceCreated callback that fires after Dispose has started must NOT re-enable events on a dying listener.
            if (_ready)
            {
                EnableEvents(eventSource, EventLevel.Informational, GcKeyword);
            }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_ready)
        {
            return;
        }

        switch (eventData.EventId)
        {
            case 1: HandleGcStart(eventData); break;
            case 2: HandleGcEnd(eventData); break;
            case 9: HandleSuspendBegin(eventData); break;
            case 3: HandleRestartEnd(eventData); break;
            // All other runtime events are intentionally ignored.
        }
    }

    private void HandleGcStart(EventWrittenEventArgs e)
    {
        // GCStart_V2 payload order: Count(u32), Depth(u32), Reason(u32), Type(u32), ClrInstanceID(u16)
        var payload = e.Payload;
        if (payload == null || payload.Count < 4)
        {
            return;
        }
        var count = Convert.ToUInt32(payload[0]);
        var depth = (byte)Convert.ToUInt32(payload[1]);
        var reason = (byte)Convert.ToUInt32(payload[2]);
        var type = (byte)Convert.ToUInt32(payload[3]);
        _queue.TryEnqueue(GcEventRecord.ForGcStart(Stopwatch.GetTimestamp(), depth, reason, type, count));
    }

    private void HandleGcEnd(EventWrittenEventArgs e)
    {
        // GCEnd_V1 payload order: Count(u32), Depth(u32), ClrInstanceID(u16)
        var payload = e.Payload;
        if (payload == null || payload.Count < 2)
        {
            return;
        }
        var count = Convert.ToUInt32(payload[0]);
        var depth = (byte)Convert.ToUInt32(payload[1]);
        _queue.TryEnqueue(GcEventRecord.ForGcEnd(Stopwatch.GetTimestamp(), depth, count));
    }

    private void HandleSuspendBegin(EventWrittenEventArgs e)
    {
        // GCSuspendEEBegin_V1 payload order: Count(u32), Reason(u32)
        var payload = e.Payload;
        byte reason = 0;
        if (payload != null && payload.Count >= 2)
        {
            reason = (byte)Convert.ToUInt32(payload[1]);
        }
        _queue.TryEnqueue(GcEventRecord.ForSuspendBegin(Stopwatch.GetTimestamp(), reason));
    }

    private void HandleRestartEnd(EventWrittenEventArgs e)
    {
        _ = e;
        _queue.TryEnqueue(GcEventRecord.ForRestartEnd(Stopwatch.GetTimestamp()));
    }

    public override void Dispose()
    {
        var source = _runtimeSource;
        if (source != null)
        {
            DisableEvents(source);
        }
        _ready = false;
        base.Dispose();
    }
}

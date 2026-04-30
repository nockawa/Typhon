using System;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.RuntimePhaseSpan"/>. Wraps one <see cref="TickPhase"/> region inside
/// <c>TyphonRuntime.OnTickEndInternal</c> as a real span, so child spans (PageCacheFlush, BTreeInsert, …) attach via <c>parentSpanId</c>.
/// </summary>
public ref struct RuntimePhaseSpanEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimePhaseSpan;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public byte Phase;

    public readonly int ComputeSize() => RuntimePhaseSpanEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimePhaseSpanEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Phase, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

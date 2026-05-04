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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte Phase;

    public readonly int ComputeSize()
    {
        var s = RuntimePhaseSpanEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimePhaseSpanEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Phase, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.StoragePageCacheDirtyWalk"/>.</summary>
public ref struct StoragePageCacheDirtyWalkEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.StoragePageCacheDirtyWalk;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public int RangeStart;
    public int RangeLen;
    public int DirtyMs;

    public readonly int ComputeSize() => StorageMiscEventCodec.ComputeSizeDirtyWalk(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => StorageMiscEventCodec.EncodeDirtyWalk(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, RangeStart, RangeLen, DirtyMs, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

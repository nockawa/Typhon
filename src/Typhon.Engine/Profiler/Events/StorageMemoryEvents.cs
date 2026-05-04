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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int RangeStart;
    public int RangeLen;
    public int DirtyMs;

    public readonly int ComputeSize()
    {
        var s = StorageMiscEventCodec.ComputeSizeDirtyWalk(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => StorageMiscEventCodec.EncodeDirtyWalk(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, RangeStart, RangeLen, DirtyMs, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

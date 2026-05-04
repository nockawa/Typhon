using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainInsert"/>.</summary>
public ref struct SpatialMaintainInsertEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialMaintainInsert;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public long EntityPK;
    public ushort ComponentTypeId;
    public byte DidDegenerate;

    public readonly int ComputeSize()
    {
        var s = SpatialMaintainEventCodec.ComputeSizeInsert(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialMaintainEventCodec.EncodeInsert(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityPK, ComponentTypeId, DidDegenerate, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainUpdateSlowPath"/>.</summary>
public ref struct SpatialMaintainUpdateSlowPathEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialMaintainUpdateSlowPath;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public long EntityPK;
    public ushort ComponentTypeId;
    public float EscapeDistSq;

    public readonly int ComputeSize()
    {
        var s = SpatialMaintainEventCodec.ComputeSizeUpdateSlowPath(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialMaintainEventCodec.EncodeUpdateSlowPath(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityPK, ComponentTypeId, EscapeDistSq, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainInsert"/>.</summary>
public ref struct SpatialMaintainInsertEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long EntityPK;
    public ushort ComponentTypeId;
    public byte DidDegenerate;

    public readonly int ComputeSize() => SpatialMaintainEventCodec.ComputeSizeInsert(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialMaintainEventCodec.EncodeInsert(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityPK, ComponentTypeId, DidDegenerate, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainUpdateSlowPath"/>.</summary>
public ref struct SpatialMaintainUpdateSlowPathEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long EntityPK;
    public ushort ComponentTypeId;
    public float EscapeDistSq;

    public readonly int ComputeSize() => SpatialMaintainEventCodec.ComputeSizeUpdateSlowPath(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialMaintainEventCodec.EncodeUpdateSlowPath(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityPK, ComponentTypeId, EscapeDistSq, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

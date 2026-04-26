using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeInsert"/>.</summary>
public ref struct SpatialRTreeInsertEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long EntityId;
    public byte Depth;
    public byte DidSplit;
    public byte RestartCount;

    public readonly int ComputeSize() => SpatialRTreeEventCodec.ComputeSizeInsert(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialRTreeEventCodec.EncodeInsert(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityId, Depth, DidSplit, RestartCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeRemove"/>.</summary>
public ref struct SpatialRTreeRemoveEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long EntityId;
    public byte LeafCollapse;

    public readonly int ComputeSize() => SpatialRTreeEventCodec.ComputeSizeRemove(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialRTreeEventCodec.EncodeRemove(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityId, LeafCollapse, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeNodeSplit"/>.</summary>
public ref struct SpatialRTreeNodeSplitEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public byte Depth;
    public byte SplitAxis;
    public byte LeftCount;
    public byte RightCount;

    public readonly int ComputeSize() => SpatialRTreeEventCodec.ComputeSizeNodeSplit(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialRTreeEventCodec.EncodeNodeSplit(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, Depth, SplitAxis, LeftCount, RightCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeBulkLoad"/>.</summary>
public ref struct SpatialRTreeBulkLoadEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public int EntityCount;
    public int LeafCount;

    public readonly int ComputeSize() => SpatialRTreeEventCodec.ComputeSizeBulkLoad(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialRTreeEventCodec.EncodeBulkLoad(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, EntityCount, LeafCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

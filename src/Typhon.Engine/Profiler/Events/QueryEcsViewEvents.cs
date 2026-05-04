using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 7 ref structs for Query / ECS:Query / ECS:View span events.
// Instants are emitted directly via EmitX factories (no ref struct needed).
// ═════════════════════════════════════════════════════════════════════════════

public ref struct QueryParseEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryParse;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort PredicateCount;
    public byte BranchCount;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeParse(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeParse(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, PredicateCount, BranchCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryParseDnfEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryParseDnf;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort InBranches;
    public ushort OutBranches;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeParseDnf(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeParseDnf(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, InBranches, OutBranches, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryPlanEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryPlan;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte EvaluatorCount;
    public ushort IndexFieldIdx;
    public long RangeMin;
    public long RangeMax;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizePlan(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodePlan(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, EvaluatorCount, IndexFieldIdx, RangeMin, RangeMax, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryEstimateEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryEstimate;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort FieldIdx;
    public long Cardinality;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeEstimate(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeEstimate(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FieldIdx, Cardinality, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryPlanSortEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryPlanSort;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte EvaluatorCount;
    public uint SortNs;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizePlanSort(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodePlanSort(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, EvaluatorCount, SortNs, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecuteIndexScanEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryExecuteIndexScan;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort PrimaryFieldIdx;
    public byte Mode;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeIndexScan(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeIndexScan(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, PrimaryFieldIdx, Mode, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecuteIterateEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryExecuteIterate;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int ChunkCount;
    public int EntryCount;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeIterate(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeIterate(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ChunkCount, EntryCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecuteFilterEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryExecuteFilter;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte FilterCount;
    public int RejectedCount;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeFilter(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeFilter(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FilterCount, RejectedCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecutePaginationEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryExecutePagination;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int Skip;
    public int Take;
    public byte EarlyTerm;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizePagination(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodePagination(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Skip, Take, EarlyTerm, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryCountEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.QueryCount;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int ResultCount;
    public readonly int ComputeSize()
    {
        var s = QueryEventCodec.ComputeSizeCount(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeCount(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ResultCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ── ECS:Query depth spans ──

public ref struct EcsQueryConstructEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsQueryConstruct;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort TargetArchId;
    public byte Polymorphic;
    public byte MaskSize;
    public readonly int ComputeSize()
    {
        var s = EcsQueryDepthEventCodec.ComputeSizeConstruct(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryDepthEventCodec.EncodeConstruct(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, TargetArchId, Polymorphic, MaskSize, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsQuerySubtreeExpandEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsQuerySubtreeExpand;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort SubtreeCount;
    public ushort RootId;
    public readonly int ComputeSize()
    {
        var s = EcsQueryDepthEventCodec.ComputeSizeSubtreeExpand(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryDepthEventCodec.EncodeSubtreeExpand(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, SubtreeCount, RootId, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ── ECS:View depth spans ──

public ref struct EcsViewRefreshPullEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsViewRefreshPull;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public uint QueryNs;
    public ushort ArchetypeMaskBits;
    public readonly int ComputeSize()
    {
        var s = EcsViewEventCodec.ComputeSizeRefreshPull(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeRefreshPull(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, QueryNs, ArchetypeMaskBits, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsViewIncrementalDrainEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsViewIncrementalDrain;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int DeltaCount;
    public byte Overflow;
    public readonly int ComputeSize()
    {
        var s = EcsViewEventCodec.ComputeSizeIncrementalDrain(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeIncrementalDrain(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, DeltaCount, Overflow, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsViewRefreshFullEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsViewRefreshFull;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int OldCount;
    public int NewCount;
    public uint RequeryNs;
    public readonly int ComputeSize()
    {
        var s = EcsViewEventCodec.ComputeSizeRefreshFull(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeRefreshFull(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, OldCount, NewCount, RequeryNs, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsViewRefreshFullOrEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsViewRefreshFullOr;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int OldCount;
    public int NewCount;
    public byte BranchCount;
    public readonly int ComputeSize()
    {
        var s = EcsViewEventCodec.ComputeSizeRefreshFullOr(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeRefreshFullOr(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, OldCount, NewCount, BranchCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

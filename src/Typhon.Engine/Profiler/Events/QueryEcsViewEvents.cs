using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 7 ref structs for Query / ECS:Query / ECS:View span events.
// Instants are emitted directly via EmitX factories (no ref struct needed).
// ═════════════════════════════════════════════════════════════════════════════

public ref struct QueryParseEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort PredicateCount;
    public byte BranchCount;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeParse(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeParse(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, PredicateCount, BranchCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryParseDnfEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort InBranches;
    public ushort OutBranches;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeParseDnf(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeParseDnf(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, InBranches, OutBranches, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryPlanEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public byte EvaluatorCount;
    public ushort IndexFieldIdx;
    public long RangeMin;
    public long RangeMax;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizePlan(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodePlan(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, EvaluatorCount, IndexFieldIdx, RangeMin, RangeMax, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryEstimateEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort FieldIdx;
    public long Cardinality;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeEstimate(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeEstimate(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FieldIdx, Cardinality, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryPlanSortEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public byte EvaluatorCount;
    public uint SortNs;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizePlanSort(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodePlanSort(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, EvaluatorCount, SortNs, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecuteIndexScanEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort PrimaryFieldIdx;
    public byte Mode;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeIndexScan(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeIndexScan(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, PrimaryFieldIdx, Mode, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecuteIterateEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int ChunkCount;
    public int EntryCount;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeIterate(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeIterate(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ChunkCount, EntryCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecuteFilterEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public byte FilterCount;
    public int RejectedCount;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeFilter(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeFilter(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FilterCount, RejectedCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryExecutePaginationEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int Skip;
    public int Take;
    public byte EarlyTerm;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizePagination(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodePagination(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Skip, Take, EarlyTerm, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct QueryCountEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int ResultCount;
    public readonly int ComputeSize() => QueryEventCodec.ComputeSizeCount(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => QueryEventCodec.EncodeCount(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ResultCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ── ECS:Query depth spans ──

public ref struct EcsQueryConstructEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort TargetArchId;
    public byte Polymorphic;
    public byte MaskSize;
    public readonly int ComputeSize() => EcsQueryDepthEventCodec.ComputeSizeConstruct(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryDepthEventCodec.EncodeConstruct(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, TargetArchId, Polymorphic, MaskSize, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsQuerySubtreeExpandEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort SubtreeCount;
    public ushort RootId;
    public readonly int ComputeSize() => EcsQueryDepthEventCodec.ComputeSizeSubtreeExpand(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryDepthEventCodec.EncodeSubtreeExpand(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, SubtreeCount, RootId, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ── ECS:View depth spans ──

public ref struct EcsViewRefreshPullEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public uint QueryNs;
    public ushort ArchetypeMaskBits;
    public readonly int ComputeSize() => EcsViewEventCodec.ComputeSizeRefreshPull(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeRefreshPull(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, QueryNs, ArchetypeMaskBits, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsViewIncrementalDrainEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int DeltaCount;
    public byte Overflow;
    public readonly int ComputeSize() => EcsViewEventCodec.ComputeSizeIncrementalDrain(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeIncrementalDrain(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, DeltaCount, Overflow, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsViewRefreshFullEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int OldCount;
    public int NewCount;
    public uint RequeryNs;
    public readonly int ComputeSize() => EcsViewEventCodec.ComputeSizeRefreshFull(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeRefreshFull(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, OldCount, NewCount, RequeryNs, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsViewRefreshFullOrEvent : ITraceEventEncoder
{
    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int OldCount;
    public int NewCount;
    public byte BranchCount;
    public readonly int ComputeSize() => EcsViewEventCodec.ComputeSizeRefreshFullOr(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewEventCodec.EncodeRefreshFullOr(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, OldCount, NewCount, BranchCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionInit"/>.</summary>
public ref struct DataTransactionInitEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;
    public ushort UowId;

    public readonly int ComputeSize() => DataTransactionEventCodec.ComputeSizeInit(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten) => 
        DataTransactionEventCodec.EncodeInit(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, UowId, 
            out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionPrepare"/>.</summary>
public ref struct DataTransactionPrepareEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;

    public readonly int ComputeSize() => DataTransactionEventCodec.ComputeSizePrepare(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DataTransactionEventCodec.EncodePrepare(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, 
            out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionValidate"/>.</summary>
public ref struct DataTransactionValidateEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;
    public int EntryCount;

    public readonly int ComputeSize() => DataTransactionEventCodec.ComputeSizeValidate(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DataTransactionEventCodec.EncodeValidate(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, 
            EntryCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataTransactionCleanup"/>.</summary>
public ref struct DataTransactionCleanupEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;
    public int EntityCount;

    public readonly int ComputeSize() => DataTransactionEventCodec.ComputeSizeCleanup(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DataTransactionEventCodec.EncodeCleanup(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, 
            EntityCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataMvccVersionCleanup"/>.</summary>
public ref struct DataMvccVersionCleanupEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Pk;
    public ushort EntriesFreed;

    public readonly int ComputeSize() => DataMvccEventCodec.ComputeSizeVersionCleanup(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DataMvccEventCodec.EncodeVersionCleanup(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Pk, 
            EntriesFreed, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataIndexBTreeRangeScan"/>.</summary>
public ref struct DataIndexBTreeRangeScanEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public int ResultCount;
    public byte RestartCount;

    public readonly int ComputeSize() => DataIndexBTreeEventCodec.ComputeSizeRangeScan(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DataIndexBTreeEventCodec.EncodeRangeScan(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, 
            ResultCount, RestartCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.DataIndexBTreeBulkInsert"/>.</summary>
public ref struct DataIndexBTreeBulkInsertEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public int BufferId;
    public int EntryCount;

    public readonly int ComputeSize() => DataIndexBTreeEventCodec.ComputeSizeBulkInsert(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DataIndexBTreeEventCodec.EncodeBulkInsert(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, 
            BufferId, EntryCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

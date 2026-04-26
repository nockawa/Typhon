using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.TransactionCommit"/>. Required: TSN. Optional: component count, conflict-detected flag.
/// </summary>
public ref struct TransactionCommitEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;

    private int _componentCount;
    private bool _conflictDetected;
    private byte _optMask;

    public int ComponentCount
    {
        readonly get => _componentCount;
        set { _componentCount = value; _optMask |= TransactionEventCodec.OptComponentCount; }
    }

    public bool ConflictDetected
    {
        readonly get => _conflictDetected;
        set { _conflictDetected = value; _optMask |= TransactionEventCodec.OptConflictDetected; }
    }

    public readonly int ComputeSize()
        => TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommit, TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.Encode(destination, endTimestamp, TraceEventKind.TransactionCommit, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, 0, _optMask, _componentCount, _conflictDetected, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct TransactionRollbackEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;

    private int _componentCount;
    private TransactionRollbackReason _reason;
    private byte _optMask;

    public int ComponentCount
    {
        readonly get => _componentCount;
        set { _componentCount = value; _optMask |= TransactionEventCodec.OptComponentCount; }
    }

    /// <summary>Phase 6 (D3): rollback reason byte. Setting any value flips the OptReason mask bit so the producer always emits the trailing byte.</summary>
    public TransactionRollbackReason Reason
    {
        readonly get => _reason;
        set { _reason = value; _optMask |= TransactionEventCodec.OptReason; }
    }

    public readonly int ComputeSize()
        => TransactionEventCodec.ComputeSize(TraceEventKind.TransactionRollback, TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.Encode(destination, endTimestamp, TraceEventKind.TransactionRollback, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, 0, _optMask, _componentCount, false, out bytesWritten, _reason);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct TransactionCommitComponentEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;
    public int ComponentTypeId;

    private int _rowCount;
    private byte _optMask;

    /// <summary>Phase 6: number of rows mutated within this component-type's commit. Setting any value flips the OptRowCount mask bit.</summary>
    public int RowCount
    {
        readonly get => _rowCount;
        set { _rowCount = value; _optMask |= TransactionEventCodec.OptRowCount; }
    }

    public readonly int ComputeSize()
        => TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommitComponent, TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.Encode(destination, endTimestamp, TraceEventKind.TransactionCommitComponent, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, ComponentTypeId, _optMask, componentCount: 0, conflictDetected: false, 
            out bytesWritten, rowCount: _rowCount);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// WAL serialization inside Transaction.Commit. Required: TSN. Optional: walLsn (set after SerializeToWal returns).
/// Wire payload for Persist: <c>[i64 tsn][i64 walLsn][u8 optMask]</c> — walLsn is in the componentTypeId slot (reused as i64)
/// but to keep things clean we use a separate encode path.
/// </summary>
public ref struct TransactionPersistEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tsn;

    private long _walLsn;
    private byte _optMask;

    public long WalLsn
    {
        readonly get => _walLsn;
        set { _walLsn = value; _optMask |= TransactionEventCodec.OptWalLsn; }
    }

    public readonly int ComputeSize()
        => TransactionEventCodec.ComputePersistSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => TransactionEventCodec.EncodePersist(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, Tsn, _optMask, _walLsn, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.CheckpointCycle"/>. Required: targetLsn, reason. Optional: dirtyPageCount (set after
/// dirty-page collection completes).
/// </summary>
public ref struct CheckpointCycleEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long TargetLsn;
    public byte Reason;

    private int _dirtyPageCount;
    private byte _optMask;

    public int DirtyPageCount
    {
        readonly get => _dirtyPageCount;
        set { _dirtyPageCount = value; _optMask |= CheckpointEventCodec.OptDirtyPageCount; }
    }

    public readonly int ComputeSize()
        => CheckpointEventCodec.ComputeCycleSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => CheckpointEventCodec.EncodeCycle(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, TargetLsn, Reason, _optMask, _dirtyPageCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Checkpoint collect-dirty-pages phase — no typed payload (span header only).</summary>
public ref struct CheckpointCollectEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => CheckpointEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.CheckpointCollect, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Checkpoint write-dirty-pages phase. Optional: writtenCount (set after pages are written).
/// </summary>
public ref struct CheckpointWriteEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    private int _writtenCount;
    private byte _optMask;

    public int WrittenCount
    {
        readonly get => _writtenCount;
        set { _writtenCount = value; _optMask |= CheckpointEventCodec.OptWrittenCount; }
    }

    public readonly int ComputeSize()
        => CheckpointEventCodec.ComputeOptionalCountSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => CheckpointEventCodec.EncodeOptionalCount(destination, endTimestamp, TraceEventKind.CheckpointWrite, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, _optMask, _writtenCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Checkpoint fsync phase — no typed payload (span header only).</summary>
public ref struct CheckpointFsyncEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => CheckpointEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.CheckpointFsync, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Checkpoint transition-UoW-entries phase. Optional: transitionedCount (set after transition completes).
/// </summary>
public ref struct CheckpointTransitionEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    private int _transitionedCount;
    private byte _optMask;

    public int TransitionedCount
    {
        readonly get => _transitionedCount;
        set { _transitionedCount = value; _optMask |= CheckpointEventCodec.OptTransitionedCount; }
    }

    public readonly int ComputeSize()
        => CheckpointEventCodec.ComputeOptionalCountSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => CheckpointEventCodec.EncodeOptionalCount(destination, endTimestamp, TraceEventKind.CheckpointTransition, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, _optMask, _transitionedCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Checkpoint recycle-WAL-segments phase. Optional: recycledCount (set after recycling completes).
/// </summary>
public ref struct CheckpointRecycleEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    private int _recycledCount;
    private byte _optMask;

    public int RecycledCount
    {
        readonly get => _recycledCount;
        set { _recycledCount = value; _optMask |= CheckpointEventCodec.OptRecycledCount; }
    }

    public readonly int ComputeSize()
        => CheckpointEventCodec.ComputeOptionalCountSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => CheckpointEventCodec.EncodeOptionalCount(destination, endTimestamp, TraceEventKind.CheckpointRecycle, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, _optMask, _recycledCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


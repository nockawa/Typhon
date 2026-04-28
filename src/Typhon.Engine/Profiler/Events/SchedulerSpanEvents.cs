using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerSystemSingleThreaded"/>.</summary>
public ref struct SchedulerSystemSingleThreadedEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerSystemSingleThreaded;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort SysIdx;
    public byte IsParallelQuery;
    public ushort ChunkCount;

    public readonly int ComputeSize() => SchedulerSystemEventCodec.ComputeSizeSingleThreaded(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerSystemEventCodec.EncodeSingleThreaded(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, SysIdx, IsParallelQuery, ChunkCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerWorkerIdle"/>.</summary>
public ref struct SchedulerWorkerIdleEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerWorkerIdle;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public byte WorkerId;
    public ushort SpinCount;
    public uint IdleUs;

    public readonly int ComputeSize() => SchedulerWorkerEventCodec.ComputeSizeIdle(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerWorkerEventCodec.EncodeIdle(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, WorkerId, SpinCount, IdleUs, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerWorkerBetweenTick"/>.</summary>
public ref struct SchedulerWorkerBetweenTickEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerWorkerBetweenTick;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public byte WorkerId;
    public uint WaitUs;
    public byte WakeReason;

    public readonly int ComputeSize() => SchedulerWorkerEventCodec.ComputeSizeBetweenTick(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerWorkerEventCodec.EncodeBetweenTick(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, WorkerId, WaitUs, WakeReason, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerDependencyFanOut"/>.</summary>
public ref struct SchedulerDependencyFanOutEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerDependencyFanOut;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort CompletingSysIdx;
    public ushort SuccCount;
    public ushort SkippedCount;

    public readonly int ComputeSize() => SchedulerDependencyEventCodec.ComputeSizeFanOut(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerDependencyEventCodec.EncodeFanOut(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, CompletingSysIdx, SuccCount, SkippedCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerGraphBuild"/>.</summary>
public ref struct SchedulerGraphBuildEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerGraphBuild;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort SysCount;
    public ushort EdgeCount;
    public ushort TopoLen;

    public readonly int ComputeSize() => SchedulerGraphEventCodec.ComputeSizeBuild(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerGraphEventCodec.EncodeBuild(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, SysCount, EdgeCount, TopoLen, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerGraphRebuild"/>. Design stub — no producer in Phase 4.</summary>
public ref struct SchedulerGraphRebuildEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerGraphRebuild;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort OldSysCount;
    public ushort NewSysCount;
    public byte Reason;

    public readonly int ComputeSize() => SchedulerGraphEventCodec.ComputeSizeRebuild(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerGraphEventCodec.EncodeRebuild(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, OldSysCount, NewSysCount, Reason, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

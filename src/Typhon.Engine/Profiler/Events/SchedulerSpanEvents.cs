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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort SysIdx;
    public byte IsParallelQuery;
    public ushort ChunkCount;

    public readonly int ComputeSize()
    {
        var s = SchedulerSystemEventCodec.ComputeSizeSingleThreaded(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerSystemEventCodec.EncodeSingleThreaded(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, SysIdx, IsParallelQuery, ChunkCount, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte WorkerId;
    public ushort SpinCount;
    public uint IdleUs;

    public readonly int ComputeSize()
    {
        var s = SchedulerWorkerEventCodec.ComputeSizeIdle(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerWorkerEventCodec.EncodeIdle(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, WorkerId, SpinCount, IdleUs, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte WorkerId;
    public uint WaitUs;
    public byte WakeReason;

    public readonly int ComputeSize()
    {
        var s = SchedulerWorkerEventCodec.ComputeSizeBetweenTick(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerWorkerEventCodec.EncodeBetweenTick(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, WorkerId, WaitUs, WakeReason, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort CompletingSysIdx;
    public ushort SuccCount;
    public ushort SkippedCount;

    public readonly int ComputeSize()
    {
        var s = SchedulerDependencyEventCodec.ComputeSizeFanOut(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerDependencyEventCodec.EncodeFanOut(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, CompletingSysIdx, SuccCount, SkippedCount, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort SysCount;
    public ushort EdgeCount;
    public ushort TopoLen;

    public readonly int ComputeSize()
    {
        var s = SchedulerGraphEventCodec.ComputeSizeBuild(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerGraphEventCodec.EncodeBuild(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, SysCount, EdgeCount, TopoLen, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort OldSysCount;
    public ushort NewSysCount;
    public byte Reason;

    public readonly int ComputeSize()
    {
        var s = SchedulerGraphEventCodec.ComputeSizeRebuild(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerGraphEventCodec.EncodeRebuild(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, OldSysCount, NewSysCount, Reason, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerSystemSingleThreaded"/>.</summary>
[TraceEvent(TraceEventKind.SchedulerSystemSingleThreaded, EmitEncoder = true)]
public ref partial struct SchedulerSystemSingleThreadedEvent
{
    [BeginParam]
    public ushort SysIdx;
    [BeginParam]
    public byte IsParallelQuery;
    [BeginParam]
    public ushort ChunkCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerWorkerIdle"/>.</summary>
[TraceEvent(TraceEventKind.SchedulerWorkerIdle, EmitEncoder = true)]
public ref partial struct SchedulerWorkerIdleEvent
{
    [BeginParam]
    public byte WorkerId;
    public ushort SpinCount;
    public uint IdleUs;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerWorkerBetweenTick"/>.</summary>
[TraceEvent(TraceEventKind.SchedulerWorkerBetweenTick, EmitEncoder = true)]
public ref partial struct SchedulerWorkerBetweenTickEvent
{
    [BeginParam]
    public byte WorkerId;
    public uint WaitUs;
    public byte WakeReason;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerDependencyFanOut"/>.</summary>
[TraceEvent(TraceEventKind.SchedulerDependencyFanOut, EmitEncoder = true)]
public ref partial struct SchedulerDependencyFanOutEvent
{
    [BeginParam]
    public ushort CompletingSysIdx;
    public ushort SuccCount;
    public ushort SkippedCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerGraphBuild"/>.</summary>
[TraceEvent(TraceEventKind.SchedulerGraphBuild, EmitEncoder = true)]
public ref partial struct SchedulerGraphBuildEvent
{
    public ushort SysCount;
    public ushort EdgeCount;
    public ushort TopoLen;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SchedulerGraphRebuild"/>. Design stub — no producer in Phase 4.</summary>
[TraceEvent(TraceEventKind.SchedulerGraphRebuild, EmitEncoder = true)]
public ref partial struct SchedulerGraphRebuildEvent
{
    [BeginParam]
    public ushort OldSysCount;
    [BeginParam]
    public ushort NewSysCount;
    [BeginParam]
    public byte Reason;

}

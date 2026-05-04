using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialTierIndexRebuild"/>.</summary>
[TraceEvent(TraceEventKind.SpatialTierIndexRebuild, EmitEncoder = true)]
public ref partial struct SpatialTierIndexRebuildEvent
{
    [BeginParam]
    public ushort ArchetypeId;
    public int ClusterCount;
    public int OldVersion;
    public int NewVersion;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialTriggerEval"/>.</summary>
[TraceEvent(TraceEventKind.SpatialTriggerEval, EmitEncoder = true)]
public ref partial struct SpatialTriggerEvalEvent
{
    [BeginParam]
    public ushort RegionId;
    public ushort OccupantCount;
    public ushort EnterCount;
    public ushort LeaveCount;

}

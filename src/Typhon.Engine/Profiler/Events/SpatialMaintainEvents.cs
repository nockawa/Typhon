using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainInsert"/>.</summary>
[TraceEvent(TraceEventKind.SpatialMaintainInsert, EmitEncoder = true)]
public ref partial struct SpatialMaintainInsertEvent
{
    [BeginParam]
    public long EntityPK;
    [BeginParam]
    public ushort ComponentTypeId;
    public byte DidDegenerate;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainUpdateSlowPath"/>.</summary>
[TraceEvent(TraceEventKind.SpatialMaintainUpdateSlowPath, EmitEncoder = true)]
public ref partial struct SpatialMaintainUpdateSlowPathEvent
{
    [BeginParam]
    public long EntityPK;
    [BeginParam]
    public ushort ComponentTypeId;
    [BeginParam]
    public float EscapeDistSq;

}

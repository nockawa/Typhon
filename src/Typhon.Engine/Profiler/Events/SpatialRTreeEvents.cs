using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeInsert"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeInsert, EmitEncoder = true)]
public ref partial struct SpatialRTreeInsertEvent
{
    [BeginParam]
    public long EntityId;
    public byte Depth;
    public byte DidSplit;
    public byte RestartCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeRemove"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeRemove, EmitEncoder = true)]
public ref partial struct SpatialRTreeRemoveEvent
{
    [BeginParam]
    public long EntityId;
    public byte LeafCollapse;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeNodeSplit"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeNodeSplit, EmitEncoder = true)]
public ref partial struct SpatialRTreeNodeSplitEvent
{
    [BeginParam]
    public byte Depth;
    public byte SplitAxis;
    public byte LeftCount;
    public byte RightCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeBulkLoad"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeBulkLoad, EmitEncoder = true)]
public ref partial struct SpatialRTreeBulkLoadEvent
{
    [BeginParam]
    public int EntityCount;
    public int LeafCount;

}

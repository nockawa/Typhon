using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.StoragePageCacheDirtyWalk"/>.</summary>
[TraceEvent(TraceEventKind.StoragePageCacheDirtyWalk, EmitEncoder = true)]
public ref partial struct StoragePageCacheDirtyWalkEvent
{
    [BeginParam]
    public int RangeStart;
    [BeginParam]
    public int RangeLen;
    public int DirtyMs;

}

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.StatisticsRebuild"/>. Three required fields, no optionals.
/// </summary>
[TraceEvent(TraceEventKind.StatisticsRebuild, EmitEncoder = true)]
public ref partial struct StatisticsRebuildEvent
{
    [BeginParam]
    public int EntityCount;
    [BeginParam]
    public int MutationCount;
    [BeginParam]
    public int SamplingInterval;

}


using JetBrains.Annotations;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Reason the CLR triggered a garbage collection. Values match <c>GCStart_V2.Reason</c> from the
/// <c>Microsoft-Windows-DotNETRuntime</c> EventSource — do not renumber.
/// </summary>
[PublicAPI]
public enum GcReason : byte
{
    SmallObjectHeapAllocation = 0,
    Induced = 1,
    LowMemory = 2,
    Empty = 3,
    LargeObjectHeapAllocation = 4,
    OutOfSpaceSmallObjectHeap = 5,
    OutOfSpaceLargeObjectHeap = 6,
    InducedNotForced = 7,
}

/// <summary>
/// Type classification of a garbage collection. Values match <c>GCStart_V2.Type</c> from the
/// <c>Microsoft-Windows-DotNETRuntime</c> EventSource — do not renumber.
/// </summary>
[PublicAPI]
public enum GcType : byte
{
    /// <summary>Blocking GC that ran entirely outside any background GC window.</summary>
    BlockingOutsideBackground = 0,

    /// <summary>Background (concurrent) GC.</summary>
    Background = 1,

    /// <summary>Blocking GC that ran while a background GC was active.</summary>
    BlockingDuringBackground = 2,
}

/// <summary>
/// Reason the CLR suspended the execution engine. Values match <c>GCSuspendEEBegin_V1.Reason</c> from the
/// <c>Microsoft-Windows-DotNETRuntime</c> EventSource — do not renumber.
/// </summary>
[PublicAPI]
public enum GcSuspendReason : byte
{
    Other = 0,
    ForGC = 1,
    ForAppDomainShutdown = 2,
    ForCodePitching = 3,
    ForShutdown = 4,
    ForDebugger = 5,
    ForGCPrep = 6,
    ForDebuggerSweep = 7,
}

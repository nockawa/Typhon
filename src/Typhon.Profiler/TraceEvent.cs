using System.Runtime.InteropServices;

namespace Typhon.Profiler;

/// <summary>
/// A single trace event captured during runtime profiling. Fixed 32-byte blittable struct written to per-thread SPSC ring buffers with minimal
/// overhead (~5-8 ns per write).
/// </summary>
/// <remarks>
/// Design choices (inspired by Tracy Profiler's 32-byte QueueItem):
/// <list type="bullet">
///   <item>Fixed size — no variable-length data, no GC pressure, memcpy-friendly</item>
///   <item>Blittable — can be written directly to memory-mapped files or unmanaged buffers</item>
///   <item>Pack(1) — no padding waste, maximizes cache utilization</item>
///   <item>Fields are unioned by event type — e.g., <see cref="SkipReason"/> only meaningful for
///         <see cref="TraceEventType.SystemSkipped"/>, <see cref="EntitiesProcessed"/> only for
///         <see cref="TraceEventType.ChunkEnd"/></item>
/// </list>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TraceEvent
{
    /// <summary>High-resolution timestamp from <c>Stopwatch.GetTimestamp()</c>.</summary>
    public long TimestampTicks;

    /// <summary>Monotonic tick number (wraps at int.MaxValue — ~414 days at 60 Hz).</summary>
    public int TickNumber;

    /// <summary>System index in the DAG (max 65,535 systems).</summary>
    public ushort SystemIndex;

    /// <summary>Chunk index within a parallel system dispatch (0 for sequential systems).</summary>
    public ushort ChunkIndex;

    /// <summary>Worker thread ID that produced this event (0-255).</summary>
    public byte WorkerId;

    /// <summary>Type of event. Determines which other fields are meaningful.</summary>
    public TraceEventType EventType;

    /// <summary>Tick phase identifier (for <see cref="TraceEventType.PhaseStart"/>/<see cref="TraceEventType.PhaseEnd"/>).</summary>
    public TickPhase Phase;

    /// <summary>Skip reason (for <see cref="TraceEventType.SystemSkipped"/>).</summary>
    public byte SkipReason;

    /// <summary>Entity count (for <see cref="TraceEventType.ChunkEnd"/>).</summary>
    public int EntitiesProcessed;

    /// <summary>
    /// General-purpose payload. Interpretation depends on <see cref="EventType"/>:
    /// <list type="bullet">
    ///   <item><see cref="TraceEventType.TickEnd"/>: overload level (byte) | tick multiplier (byte) in low 16 bits</item>
    ///   <item><see cref="TraceEventType.ChunkStart"/>: total chunks for this system dispatch</item>
    ///   <item><see cref="TraceEventType.SystemReady"/>: predecessor count that was satisfied</item>
    /// </list>
    /// </summary>
    public int Payload;

    /// <summary>Reserved for future use (alignment to 32 bytes).</summary>
    public int Reserved;
}

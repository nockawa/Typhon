using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-tick telemetry snapshot. Recorded at the end of each tick into the <see cref="TickTelemetryRing"/>.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct TickTelemetry
{
    /// <summary>Monotonically increasing tick number (0-based).</summary>
    public long TickNumber;

    /// <summary>Target tick duration based on <see cref="RuntimeOptions.BaseTickRate"/> (e.g., 16.67ms at 60Hz).</summary>
    public float TargetDurationMs;

    /// <summary>Actual wall-clock tick execution time (reset → completion), in milliseconds.</summary>
    public float ActualDurationMs;

    /// <summary>
    /// Ratio of actual to target duration. Values &gt; 1.0 indicate overrun.
    /// Used by overload management (#201) to detect sustained overruns.
    /// </summary>
    public float OverrunRatio;

    /// <summary>
    /// Actual tick-to-tick interval in milliseconds (time from previous tick start to this tick start).
    /// This is the true period seen by the simulation. Compare against <see cref="TargetDurationMs"/> to measure jitter: <c>|TickIntervalMs - TargetDurationMs|</c>.
    /// Zero for the first tick.
    /// </summary>
    public float TickIntervalMs;

    /// <summary>Number of worker threads active during this tick.</summary>
    public int ActiveWorkerCount;

    /// <summary>Number of systems that actually executed (not skipped) this tick.</summary>
    public int ActiveSystemCount;
}

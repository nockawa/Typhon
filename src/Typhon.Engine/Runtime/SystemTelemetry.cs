using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-system-per-tick telemetry metrics. Recorded at the end of each tick by the scheduler.
/// </summary>
/// <remarks>
/// Internal timestamp fields are used during tick execution to compute the public microsecond values.
/// They are reset to 0 at tick start and populated by workers during dispatch.
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct SystemTelemetry
{
    /// <summary>Index of the system in the DAG.</summary>
    public int SystemIndex;

    /// <summary>
    /// Time from when all predecessors completed (system became ready) to when the first chunk was grabbed.
    /// Measures dispatch/discovery overhead. Target: &lt; 1µs.
    /// </summary>
    public float TransitionLatencyUs;

    /// <summary>
    /// Time from first chunk grab to last chunk completion. Measures total system execution time.
    /// </summary>
    public float DurationUs;

    /// <summary>
    /// Difference between actual duration and theoretical optimal parallel duration.
    /// Only computed when <see cref="TelemetryConfig.SchedulerTrackStragglerGap"/> is enabled.
    /// Positive values indicate load imbalance (some workers finished earlier and idled).
    /// </summary>
    public float StragglerGapUs;

    /// <summary>Number of distinct workers that processed chunks for this system.</summary>
    public int WorkersTouched;

    /// <summary>True if this system was skipped this tick (e.g., runIf returned false).</summary>
    public bool WasSkipped;

    // ═══════ Internal timestamps (Stopwatch ticks, not part of public API) ═══════

    /// <summary>Stopwatch timestamp when all predecessors completed and system became ready.</summary>
    internal long ReadyTick;

    /// <summary>Stopwatch timestamp when the first chunk was grabbed (or Callback started executing).</summary>
    internal long FirstChunkGrabTick;

    /// <summary>Stopwatch timestamp when the last chunk completed (or Callback finished).</summary>
    internal long LastChunkDoneTick;
}

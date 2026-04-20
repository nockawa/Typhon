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
    /// Difference between actual wall-clock duration and theoretical optimal parallel duration (<c>sum(chunk_duration) / WorkersTouched</c>).
    /// Only computed when <see cref="TelemetryConfig.SchedulerTrackStragglerGap"/> is enabled AND the system is multi-chunk parallel
    /// (<c>TotalChunks &gt; 1</c> AND (pipeline OR parallel query)).
    /// Positive values indicate load imbalance (some workers finished earlier and idled).
    /// </summary>
    public float StragglerGapUs;

    /// <summary>
    /// Longest single-chunk duration observed this tick. Paired with <see cref="StragglerGapUs"/>:
    /// a large gap with a large <see cref="MaxChunkDurationUs"/> approaching <see cref="DurationUs"/> means one straggler chunk dominated; similar gap with
    /// uniform chunk durations means evenly lumpy load.
    /// Only computed when <see cref="TelemetryConfig.SchedulerTrackStragglerGap"/> is enabled.
    /// </summary>
    public float MaxChunkDurationUs;

    /// <summary>Number of entities this system processed (from <c>TickContext.Entities</c>). Zero for CallbackSystems.</summary>
    public int EntitiesProcessed;

    /// <summary>
    /// Entities in the View but excluded by change filter this tick. Zero if no change filter applied.
    /// Computed as <c>View.Count - EntitiesProcessed</c> for change-filtered systems.
    /// </summary>
    public int EntitiesSkippedByChangeFilter;

    /// <summary>Number of distinct workers that processed chunks for this system.</summary>
    public int WorkersTouched;

    /// <summary>Reason the system was skipped, or <see cref="SkipReason.NotSkipped"/> if it executed normally.</summary>
    public SkipReason SkipReason;

    /// <summary>True if this system was skipped this tick.</summary>
    public readonly bool WasSkipped => SkipReason != SkipReason.NotSkipped;

    // ═══════ Internal timestamps (Stopwatch ticks, not part of public API) ═══════

    /// <summary>Stopwatch timestamp when all predecessors completed and system became ready.</summary>
    internal long ReadyTick;

    /// <summary>Stopwatch timestamp when the first chunk was grabbed (or Callback started executing).</summary>
    internal long FirstChunkGrabTick;

    /// <summary>Stopwatch timestamp when the last chunk completed (or Callback finished).</summary>
    internal long LastChunkDoneTick;

    /// <summary>
    /// Accumulated chunk work time in Stopwatch ticks across all participating workers.
    /// Updated via <c>Interlocked.Add</c> at each chunk completion in multi-threaded dispatch.
    /// Used to derive <see cref="StragglerGapUs"/> at tick end.
    /// </summary>
    internal long TotalChunkWorkTicks;

    /// <summary>
    /// Maximum single-chunk duration in Stopwatch ticks this tick.
    /// Updated via a CAS loop at each chunk completion. Source of <see cref="MaxChunkDurationUs"/>.
    /// </summary>
    internal long MaxChunkWorkTicks;

    /// <summary>
    /// Bitmap of worker IDs that processed at least one chunk for this system this tick.
    /// Each worker OR's <c>1UL &lt;&lt; workerId</c> into this field via <c>Interlocked.Or</c> on its first chunk.
    /// Popcount yields the distinct-worker count stored in <see cref="WorkersTouched"/>.
    /// 64-worker limit: fine for Typhon (thread IDs are 16-bit but worker pools stay small;
    /// if &gt; 64 workers ever ship, upgrade to a multi-word bitmap).
    /// </summary>
    internal ulong WorkerBitmap;
}

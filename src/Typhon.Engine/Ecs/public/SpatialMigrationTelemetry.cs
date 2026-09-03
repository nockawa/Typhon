using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A snapshot of one archetype's spatial-partitioning counters, or of the whole engine's when obtained from
/// <see cref="DatabaseEngine.GetSpatialTelemetryTotal"/>. Obtained from <see cref="DatabaseEngine.GetSpatialTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two clocks, deliberately.</b> The <c>...Count</c> / <c>...Ms</c> members describe the <b>most recently completed tick</b> and are reset at the top of
/// every tick fence; the <c>Total...</c> members only grow. A poll-based consumer (an OTel scrape every few seconds) that reads a per-tick value samples one
/// arbitrary tick out of hundreds — use the cumulative members and differentiate for a rate; use the per-tick members from inside a tick loop, where "this
/// tick" is exactly what you meant.
/// </para>
/// <para>
/// <b>Zero means zero, never "unknown".</b> An archetype with no cluster state, an out-of-range id and a tick in which nothing happened all report zero.
/// <see cref="ClustersScanned"/>, <see cref="DriftersDetected"/> and <see cref="DriftAbsorbedCount"/> gained their producers in step 10 of
/// <c>claude/design/Spatial/vdb-cell-grid-and-migration.md</c>; <see cref="ReclusterBudgetUsedMs"/>, <see cref="RepairedEntityCount"/>,
/// <see cref="RepairUnitCount"/> and <see cref="RepairUnitsRefused"/> in step 12.
/// </para>
/// <para>
/// <b>Cumulative members restart with the archetype's cluster state.</b> <see cref="DatabaseEngine.InitializeArchetypes"/> reallocates the per-archetype state
/// array, so a repeat call — rare, but explicitly tolerated — returns the totals to zero. They measure the life of the cluster state, not of the process.
/// </para>
/// <para>Reading is allocation-free and lock-free: every member is a plain field read of live engine state, torn only across a fence boundary.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialMigrationTelemetry
{
    internal SpatialMigrationTelemetry(int migrationCount, int hysteresisAbsorbedCount, double migrationExecuteMs, int clustersScanned, int driftersDetected,
        int driftAbsorbedCount, double reclusterBudgetUsedMs, int activeClusterCount, long totalMigrations, long totalHysteresisAbsorbed,
        int repairedEntityCount, int repairUnitCount, int repairUnitsRefused)
    {
        RepairedEntityCount = repairedEntityCount;
        RepairUnitCount = repairUnitCount;
        RepairUnitsRefused = repairUnitsRefused;
        DriftAbsorbedCount = driftAbsorbedCount;
        MigrationCount = migrationCount;
        HysteresisAbsorbedCount = hysteresisAbsorbedCount;
        MigrationExecuteMs = migrationExecuteMs;
        ClustersScanned = clustersScanned;
        DriftersDetected = driftersDetected;
        ReclusterBudgetUsedMs = reclusterBudgetUsedMs;
        ActiveClusterCount = activeClusterCount;
        TotalMigrations = totalMigrations;
        TotalHysteresisAbsorbed = totalHysteresisAbsorbed;
    }

    /// <summary>Entities moved to a different cluster because they crossed a spatial cell boundary, during the most recently completed tick.</summary>
    public int MigrationCount { get; }

    /// <summary>
    /// Cell-boundary crossings that did <b>not</b> produce a migration during the most recently completed tick, because the entity was still inside the
    /// hysteresis margin. Read against <see cref="MigrationCount"/>: a high ratio means the margin is doing its job, a near-zero one means it is too narrow to
    /// absorb oscillation around the boundary.
    /// </summary>
    /// <remarks>
    /// <b>The unit differs by write path.</b> An archetype using the spatial write barrier (<c>SetSpatialBarrierOnly</c>) counts one per absorbed <i>write</i>,
    /// so an entity parked in the margin and written twice in a tick contributes two; every other archetype counts one per <i>slot</i> per tick, because its
    /// producer is a once-per-tick scan. The two agree on the overwhelmingly common workload of one spatial write per entity per tick, and diverge above that.
    /// Treat this as a rate signal for tuning, not an exact entity count.
    /// </remarks>
    public int HysteresisAbsorbedCount { get; }

    /// <summary>
    /// Wall-clock milliseconds spent executing migrations during the most recently completed tick, summed across every worker that took a slice.
    /// </summary>
    public double MigrationExecuteMs { get; }

    /// <summary>
    /// Clusters examined by the intra-cell drifter scan during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// Clusters that were WRITTEN this tick, not clusters that exist — a settled world scans nothing, which is the cheap half of the design's promise and the
    /// denominator that makes <see cref="DriftersDetected"/> mean anything.
    /// </remarks>
    public int ClustersScanned { get; }

    /// <summary>
    /// Entities found outside their cluster's target region during the most recently completed tick — candidates for intra-cell relocation.
    /// </summary>
    /// <remarks>
    /// Counts DETECTION, not outcome. An entity is counted here the moment the target-region rule rejects it, whether or not placement then found a better
    /// cluster to put it in — a cell whose every other cluster is full produces drifters and no migrations, and that gap is the signal you want, not noise to
    /// be suppressed. Read against <see cref="MigrationCount"/> to see it.
    /// </remarks>
    public int DriftersDetected { get; }

    /// <summary>
    /// Entities outside their cluster's target region by less than the intra-cell drift margin during the most recently completed tick, and therefore left
    /// alone.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not folded into <see cref="HysteresisAbsorbedCount"/>.</b> That counter is about cell-boundary oscillation and tunes
    /// <c>MigrationHysteresisRatio</c>; this one is about intra-cell drift and tunes <c>ClusterDriftMarginRatio</c>. They answer different questions and their
    /// margins move independently, so a single number would tune neither — which is the whole reason step 10 added a second counter rather than reusing the
    /// first.</para>
    /// <para>Read as a fraction of <c>DriftAbsorbedCount + DriftersDetected</c>: near zero means the margin is too narrow to damp anything, near one means it
    /// is wide enough to be suppressing repairs the step exists to make.</para>
    /// </remarks>
    public int DriftAbsorbedCount { get; }

    /// <summary>
    /// Milliseconds of the per-tick re-clustering budget the repair path committed during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <b>Projected, not measured, and the difference is the design.</b> A repair unit is admitted only if the remaining budget covers its whole cost, so
    /// the estimate has to exist before the work does; reporting the elapsed time instead would report a number that gated nothing. The projection is
    /// <c>entities x SpatialGridConfig.RepairNsPerEntity</c>. Compare it against a measured tick time to find out whether that constant is honest — which is
    /// exactly what step 11's adaptive budget will do automatically.
    /// </remarks>
    public double ReclusterBudgetUsedMs { get; }

    /// <summary>Entities re-packed by the repair path — the full Morton re-sort — during the most recently completed tick.</summary>
    /// <remarks>
    /// Disjoint from <see cref="MigrationCount"/> in intent though not in mechanism: a repair emits ordinary migration requests, so the entities counted
    /// here are also counted there when the requests execute. This is the count the PLANNER committed to; that one is what the Migrate phase actually moved,
    /// and the two differ by the requests whose source slot had emptied in between.
    /// </remarks>
    public int RepairedEntityCount { get; }

    /// <summary>Repair units admitted during the most recently completed tick. A unit is one cell's N worst clusters, or one whole cell.</summary>
    public int RepairUnitCount { get; }

    /// <summary>
    /// Repair units whose projected cost exceeded the remaining budget, and which were therefore never begun.
    /// </summary>
    /// <remarks>
    /// A Morton sort cannot be halved — a partly re-sorted cell has paid the cost and banked only part of the benefit — so the budget admits whole units and
    /// refuses the rest outright. A persistently non-zero reading against a zero <see cref="RepairUnitCount"/> means the budget is below the cost of the
    /// smallest unit on offer and no repair can ever happen; raise <c>ReclusterBudgetMs</c>, or lower <c>RepairWorstClustersPerUnit</c> so a unit is smaller.
    /// </remarks>
    public int RepairUnitsRefused { get; }

    /// <summary>
    /// Clusters currently live. The denominator for every ratio above — a migration count means nothing without the population it came from.
    /// </summary>
    public int ActiveClusterCount { get; }

    /// <summary>Migrations executed since this archetype's cluster state was created. Cumulative; differentiate over time for a rate.</summary>
    public long TotalMigrations { get; }

    /// <summary>
    /// Cell-boundary crossings absorbed by the hysteresis margin since this archetype's cluster state was created. Cumulative twin of
    /// <see cref="HysteresisAbsorbedCount"/>, and subject to the same per-write-path unit caveat.
    /// </summary>
    public long TotalHysteresisAbsorbed { get; }
}

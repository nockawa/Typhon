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
/// <see cref="ClustersScanned"/>, <see cref="DriftersDetected"/> and <see cref="ReclusterBudgetUsedMs"/> additionally have no producer yet — they read zero
/// until intra-cell re-clustering lands (steps 10-11 of <c>claude/design/Spatial/vdb-cell-grid-and-migration.md</c>).
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
        double reclusterBudgetUsedMs, int activeClusterCount, long totalMigrations, long totalHysteresisAbsorbed)
    {
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
    /// <b>Reads zero until intra-cell re-clustering exists</b> — see the remarks on <see cref="SpatialMigrationTelemetry"/>.
    /// </summary>
    public int ClustersScanned { get; }

    /// <summary>
    /// Entities found outside their cluster's target region during the most recently completed tick — candidates for intra-cell relocation.
    /// <b>Reads zero until intra-cell re-clustering exists.</b>
    /// </summary>
    public int DriftersDetected { get; }

    /// <summary>
    /// Milliseconds of the per-tick re-clustering budget consumed during the most recently completed tick.
    /// <b>Reads zero until throttled re-clustering exists.</b>
    /// </summary>
    public double ReclusterBudgetUsedMs { get; }

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

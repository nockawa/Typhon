using JetBrains.Annotations;

namespace Typhon.Engine;

public partial class DatabaseEngine
{
    /// <summary>
    /// Wall-clock milliseconds the most recent <see cref="InitializeArchetypes"/> spent rebuilding cluster-to-cell mappings.
    /// </summary>
    private double _openCellStateRebuildMs;

    /// <summary>Wall-clock milliseconds the most recent <see cref="InitializeArchetypes"/> spent rebuilding per-cluster AABBs and the per-cell index.</summary>
    private double _openClusterAabbRebuildMs;

    /// <summary>
    /// Milliseconds spent reconstructing the cluster-to-cell mapping at open (<c>RebuildCellState</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cell layer is transient — nothing about the grid is persisted, so every open reconstructs it from entity positions in an <c>O(entities)</c> sweep.
    /// This figure and <see cref="OpenClusterAabbRebuildMs"/> are what decide whether that reconstruction stays affordable at target entity counts or the grid
    /// must be persisted. Zero on a database with no cluster-eligible archetypes, or when every archetype opened with no active clusters.
    /// </para>
    /// <para>
    /// Describes the <b>most recent</b> <see cref="InitializeArchetypes"/> call, not the sum of all of them: a repeat call reallocates the per-archetype state
    /// it measures, so every other counter restarts with it and a lifetime sum here would be the one figure that did not.
    /// </para>
    /// </remarks>
    [PublicAPI]
    public double OpenCellStateRebuildMs => _openCellStateRebuildMs;

    /// <summary>
    /// Milliseconds spent recomputing per-cluster AABBs and the per-cell spatial index at open (<c>RebuildClusterAabbs</c>).
    /// </summary>
    /// <remarks>See <see cref="OpenCellStateRebuildMs"/> — the two are halves of the same startup sweep and are read together.</remarks>
    [PublicAPI]
    public double OpenClusterAabbRebuildMs => _openClusterAabbRebuildMs;

    /// <summary>
    /// Reads one archetype's spatial-partitioning counters. Allocation-free; never throws.
    /// </summary>
    /// <param name="archetypeId">The archetype's runtime id. Out of range, or an archetype with no cluster state, yields an all-zero snapshot.</param>
    /// <returns>A snapshot of the archetype's counters. See <see cref="SpatialMigrationTelemetry"/> for which members describe the last tick and which are
    /// cumulative.</returns>
    /// <remarks>
    /// The snapshot is taken field by field without a lock, so a read racing the tick fence can mix values from either side of it. That is deliberate: these
    /// are diagnostic counters, and serializing a reader against the fence would cost more than the inconsistency is worth. Call it from the tick loop —
    /// after the fence, before the next tick — for a coherent view.
    /// </remarks>
    [PublicAPI]
    public SpatialMigrationTelemetry GetSpatialTelemetry(int archetypeId)
    {
        var states = _archetypeStates;
        if (states == null || archetypeId < 0 || archetypeId >= states.Length)
        {
            return default;
        }

        var clusterState = states[archetypeId]?.ClusterState;
        if (clusterState == null)
        {
            return default;
        }

        return new SpatialMigrationTelemetry(
            clusterState.LastTickMigrationCount,
            clusterState.LastTickHysteresisAbsorbedCount,
            clusterState.LastTickMigrationExecuteMs,
            clusterState.LastTickClustersScanned,
            clusterState.LastTickDriftersDetected,
            clusterState.LastTickReclusterBudgetUsedMs,
            clusterState.ActiveClusterCount,
            clusterState.TotalMigrationCount,
            clusterState.TotalHysteresisAbsorbedCount);
    }

    /// <summary>
    /// Sums <see cref="GetSpatialTelemetry"/> across every archetype in this engine. Allocation-free; never throws.
    /// </summary>
    /// <returns>An engine-wide snapshot. <see cref="SpatialMigrationTelemetry.MigrationExecuteMs"/> is a sum across archetypes, not a wall-clock duration —
    /// parallel fence workers overlap, so it can exceed the tick's elapsed time.</returns>
    [PublicAPI]
    public SpatialMigrationTelemetry GetSpatialTelemetryTotal()
    {
        var states = _archetypeStates;
        if (states == null)
        {
            return default;
        }

        var migrations = 0;
        var absorbed = 0;
        var executeMs = 0d;
        var scanned = 0;
        var drifters = 0;
        var budgetMs = 0d;
        var activeClusters = 0;
        var totalMigrations = 0L;
        var totalAbsorbed = 0L;

        for (var i = 0; i < states.Length; i++)
        {
            var clusterState = states[i]?.ClusterState;
            if (clusterState == null)
            {
                continue;
            }

            migrations += clusterState.LastTickMigrationCount;
            absorbed += clusterState.LastTickHysteresisAbsorbedCount;
            executeMs += clusterState.LastTickMigrationExecuteMs;
            scanned += clusterState.LastTickClustersScanned;
            drifters += clusterState.LastTickDriftersDetected;
            budgetMs += clusterState.LastTickReclusterBudgetUsedMs;
            activeClusters += clusterState.ActiveClusterCount;
            totalMigrations += clusterState.TotalMigrationCount;
            totalAbsorbed += clusterState.TotalHysteresisAbsorbedCount;
        }

        return new SpatialMigrationTelemetry(migrations, absorbed, executeMs, scanned, drifters, budgetMs, activeClusters, totalMigrations, totalAbsorbed);
    }
}

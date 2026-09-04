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
    /// Entity slots the AABB refresh pass actually read during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <para>The refresh's own cost, in the only unit that scales with the world: <see cref="ClustersScanned"/> is incremented after the pass has decided a
    /// cluster had something to say, so it cannot distinguish a pass that opened ten clusters from one that opened two thousand.</para>
    /// <para><b>What a healthy value looks like.</b> Roughly the occupied-slot count of the clusters that were WRITTEN this tick. If it tracks the whole
    /// population instead — 63 000 on a 64 000-entity world where 640 entities moved — the pass has lost its dirty gate, which is exactly the regression
    /// this counter was added to make visible.</para>
    /// </remarks>
    public int SlotsScanned { get; init; }

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
    /// The whole cost of the most recently completed tick's migrations in milliseconds — the migrant loop <b>plus</b> the bulk index descent and the bulk
    /// EntityMap patch, summed across every worker.
    /// </summary>
    /// <remarks>
    /// <para><b>Read this, not <see cref="MigrationExecuteMs"/>, for cost per entity.</b> That one brackets the migrant loop alone, and since #872 step 6
    /// the loop merely STAGES the index update — the descent that applies it happens in a later phase. The secondary index was measured at ~48 % of a
    /// migration's cost, so the older field under-reports by roughly half. Step 11 added the two missing timers to produce this one, and it is what the
    /// adaptive budget divides by <see cref="MigrationCount"/>.</para>
    /// <para><b>CPU-milliseconds, not span.</b> W workers each busy for 1 ms report 4, not 1.</para>
    /// </remarks>
    public double MigrationTotalMs { get; init; }

    /// <summary>
    /// Intra-cell relocations the re-clustering budget refused during the most recently completed tick, and therefore dropped (#872 step 11).
    /// </summary>
    /// <remarks>
    /// <para><b>Dropped, not deferred, and that is deliberate.</b> A relocation's chosen destination was the least-enlargement cluster as of the AABBs of
    /// the tick that detected it; carrying it forward would apply a stale choice against bounds this tick's own migrations have moved. It is re-detected
    /// next tick from current data, so what this counts is deferred WORK, not lost work.</para>
    /// <para>Read against <see cref="DriftersDetected"/>: a persistently high ratio means the budget is below the world's drift rate, and equilibrium
    /// tightness — not correctness — is what degrades. That is §5.6's stated failure mode, by design.</para>
    /// </remarks>
    public int RelocationsThrottled { get; init; }

    /// <summary>
    /// Intra-cell relocations dropped because a cell crossing already claimed the same entity during the most recently completed tick (#877).
    /// </summary>
    /// <remarks>
    /// <para><b>Not a budget signal, and that is why it is not folded into <see cref="RelocationsThrottled"/>.</b> Drift detection runs in AabbRefresh, so
    /// its relocations are decided by the next tick's Prep — by which time the crossing detector may have filed a <c>CellCrossing</c> for the same entity.
    /// An entity that drifted to the edge of its cell is precisely the one most likely to leave it, so the overlap is the common case on a moving world,
    /// not a corner. The relocation is dropped because the crossing supersedes it: the entity is migrating regardless, to a cell the relocation's
    /// destination was never chosen for.</para>
    /// <para><b>Before #877 both requests were executed.</b> <c>ExecuteMigrations</c>' stale-source guard is an occupancy test rather than an identity test,
    /// so it only covered the case where the freed slot was still empty when the second request drained; when an unrelated migrant had claimed it, the
    /// relocation moved THAT entity to a destination chosen for someone else. A high reading here is normal and healthy. A high reading that starts
    /// tracking <see cref="RelocationsThrottled"/> is worth a look, because it means drift and crossings are competing for the same entities.</para>
    /// </remarks>
    public int RelocationsSuperseded { get; init; }

    /// <summary>
    /// Prep's internal split for the most recently completed tick, in milliseconds of wall time, in phase order:
    /// snapshot, occupancy mask, index replay, min/max refresh, crossing detection, budget, repair plan, pre-size.
    /// </summary>
    /// <remarks>
    /// <b>Added because the design that proposes optimising Prep could not say which of its steps cost anything.</b> The phase-level spans say Prep is 52 %
    /// of the fence; they do not say whether that is the occupancy mask, the min/max rescan or the decisions at the tail. Ranking the steps from what each
    /// one touches rather than from what each one costs is the same mistake one level down that the phase spans were added to prevent one level up.
    /// </remarks>
    public double PrepSnapshotMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepMaskMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepShadowMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepZoneMapMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepDetectMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepThrottleMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepPlanMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepPreSizeMs { get; init; }

    /// <summary>Clusters whose change word survived the occupancy mask — the size of the domain a sliced Prep would partition.</summary>
    public int PrepDirtyClusters { get; init; }

    /// <summary>
    /// Drifters that were detected but for which placement found no better cluster, during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// The gap <see cref="DriftersDetected"/>'s own remarks point at, now counted rather than inferred: a cell whose every cluster is equally bad produces
    /// drifters and no migrations. Together the identity
    /// <c>DriftersDetected = admitted + RelocationsThrottled + RelocationsSuperseded + DriftersUnplaced</c> holds over a tick,
    /// which is what makes "a drifter is never both absorbed and throttled" checkable rather than merely asserted (<c>AC-11.7</c>).
    /// </remarks>
    public int DriftersUnplaced { get; init; }

    /// <summary>
    /// Repair units admitted by the safety valve during the most recently completed tick — begun despite insufficient remaining budget because the cell's
    /// degradation had reached <c>SpatialGridConfig.ClusterRepairCriticalExtentRatio</c>.
    /// </summary>
    /// <remarks>
    /// <b>The only budget overshoot the engine permits</b>, and it is bounded: the valve caps its unit at <c>RepairWorstClustersPerUnit</c> clusters and
    /// fires at most once per tick per archetype. A persistently non-zero reading means degradation is outrunning the budget — the condition §5.6's valve
    /// exists to bound rather than to hide, so raise <c>ReclusterBudgetMs</c> rather than treating the valve as the steady state.
    /// </remarks>
    public int RepairValveFires { get; init; }

    /// <summary>Cells currently waiting in the repair priority queue. Unlike every other per-tick member this is a LEVEL, not a rate: it persists.</summary>
    public int RepairQueueDepth { get; init; }

    /// <summary>
    /// Candidates evicted from the repair queue since this cluster state was created, because the queue was at
    /// <c>SpatialGridConfig.RepairQueueMaxCells</c>. Cumulative.
    /// </summary>
    /// <remarks>
    /// Read against <see cref="RepairQueueDepth"/>: a growing count while the depth sits at the cap says the cap is below what the world actually
    /// degrades, and cells are being forgotten. Zero at a depth below the cap is the healthy reading.
    /// </remarks>
    public long RepairQueueEvicted { get; init; }

    /// <summary>
    /// Milliseconds spent maintaining the repair queue — absorbing nominations and re-ranking — during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <c>AC-11.5</c>'s numerator: "a queue that costs more to maintain than the work it schedules is a net loss". Read as a fraction of
    /// <see cref="ReclusterBudgetUsedMs"/>. Re-ranking is lazy — triggered by new nominations or by a <c>SpatialGrid.TierVersion</c> change, not by the
    /// tick — so a settled world should report near zero here.
    /// </remarks>
    public double RepairQueueMaintenanceMs { get; init; }

    /// <summary>
    /// The measured per-entity migration cost, in nanoseconds, that this tick's budget was actually spent against (#872 step 11).
    /// </summary>
    /// <remarks>
    /// <para>An EWMA over <see cref="MigrationTotalMs"/> per migrant plus the repair planner's own measured cost, seeded from
    /// <c>SpatialGridConfig.RepairNsPerEntity</c> and clamped to a band around it. It replaces that constant as the operative number: <c>AC-12.7</c>
    /// measured the real cost at 22x to 117x the design's estimate, and a budget calibrated on the wrong constant admits units costing many times what it
    /// thinks they do.</para>
    /// <para><b>Blended across migration kinds.</b> Cell crossings, intra-cell relocations and repair moves all contribute; splitting them needs per-class
    /// attribution inside the apply phases, where the staged records carry no kind. A blended measurement is nonetheless strictly better than a constant
    /// that cannot track the machine at all.</para>
    /// </remarks>
    public double MeasuredNsPerEntity { get; init; }

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

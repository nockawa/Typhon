using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// The re-clustering throttle — one budget, spent by policy, with memory (#872 step 11, design §5.6).
/// </summary>
/// <remarks>
/// <para><b>Why a controller at all.</b> Steps 10 and 12 shipped with their work effectively unbudgeted. Drift detection appended every drifter it found
/// and the fence executed all of them; the repair planner had a budget but compared it against a hand-set constant and forgot whatever it refused.
/// §5.6's decision is that "re-clustering runs to a budget, never to completion", because deferring a pass costs one tick of looser bounds while
/// overrunning the frame costs the frame.</para>
/// <para><b>The two mechanisms budget differently, and §5.6 says why.</b> Delta relocation is resumable — each drifter is independent, so the budget can
/// stop anywhere — and is budgeted in ENTITIES. A repair cannot be halved: a partly re-sorted cell has paid the cost and banked part of the benefit, so
/// it is budgeted in WHOLE UNITS and a unit the remaining budget cannot finish is never begun.</para>
/// <para><b>Everything here runs single-threaded, at the tail of Prep.</b> That is what makes <c>AC-11.6</c> true by construction rather than by making a
/// parallel decision reproducible: one thread, one pass, order-preserving.</para>
/// </remarks>
internal sealed partial class ArchetypeClusterState
{
    /// <summary>Weight of the newest sample in the per-entity cost estimators. See <see cref="ObserveMigrationCost"/>.</summary>
    /// <remarks>
    /// A constant rather than a knob. It sets how fast the model tracks a change in cost, and 0.25 settles a step change inside ten ticks while ignoring
    /// a single anomalous tick — a range where nothing a user could reasonably choose behaves differently enough to be worth a configuration surface.
    /// </remarks>
    private const double CostEwmaAlpha = 0.25d;

    /// <summary>Bounds on the per-entity estimate, as multiples of the configured <c>RepairNsPerEntity</c> seed.</summary>
    /// <remarks>
    /// A clamp, not a safety net for a broken measurement: one pathological tick — a page fault storm, a stop-the-world GC landing inside the bracket —
    /// would otherwise poison the model for the ten ticks the EWMA takes to forget it, and during those ticks the budget admits nothing at all. The band
    /// is wide enough that a genuinely different machine is tracked and narrow enough that an outlier cannot stop the feature.
    /// </remarks>
    private const double CostEstimateFloorFactor = 0.1d;

    /// <inheritdoc cref="CostEstimateFloorFactor"/>
    private const double CostEstimateCeilingFactor = 20d;

    /// <summary>Scale the repair planner's own per-entity cost is clamped against, in nanoseconds.</summary>
    /// <remarks>
    /// Separate from <c>RepairNsPerEntity</c> because the two measure different work by two orders of magnitude: moving an entity copies components,
    /// claims a slot and descends an index, while planning one is a Morton encode, a comparison and an array write. Sharing the migration band put a floor
    /// of a tenth of the migration seed under the planner term, which no measurement could get below.
    /// </remarks>
    private const double PlannerSeedNsPerEntity = 60d;

    /// <summary>
    /// The persistent, ranked set of cells waiting to be repaired. Created on first use, once the grid's knobs are known.
    /// </summary>
    internal CellRepairQueue RepairQueue;

    /// <summary>EWMA of the whole migration cost per entity moved, in nanoseconds. Zero until the first tick that moved something.</summary>
    private double _migrationNsPerEntityEwma;

    /// <summary>EWMA of the repair PLANNER's own cost per entity planned, in nanoseconds — gather, Morton sort, destination allocation.</summary>
    /// <remarks>
    /// Modelled separately because it is charged to a different phase and, unlike the migration cost, is repair's alone: no cell crossing and no
    /// relocation pays it. Step 12's projection ignored it entirely.
    /// </remarks>
    private double _plannerNsPerEntityEwma;

    /// <summary><see cref="Stopwatch"/> ticks the repair planner spent in the most recently completed tick, and the entities it planned.</summary>
    private long _lastTickPlannerTicks;

    /// <inheritdoc cref="_lastTickPlannerTicks"/>
    private int _lastTickPlannedEntities;

    /// <summary>
    /// Relocations lifted out of the pending queue while the mandatory requests are compacted over them.
    /// </summary>
    /// <remarks>
    /// Per-archetype and reused across ticks, so its steady state is one allocation. See <see cref="ApplyMigrationThrottle"/> for why the copy is not
    /// optional: an in-place partition overwrites the entries the second pass has yet to read.
    /// </remarks>
    private MigrationRequest[] _throttleScratch = [];

    /// <summary>Intra-cell relocations dropped by the throttle in the most recently completed tick.</summary>
    internal int LastTickRelocationsThrottled;

    /// <summary>
    /// Drifters detected in the most recently completed tick for which placement found no better cluster, and which were therefore left where they were.
    /// </summary>
    /// <remarks>
    /// The third term of <c>AC-11.7</c>'s identity — <c>DriftersDetected = admitted + throttled + unplaced</c>. Without it the identity cannot close and
    /// "no migration is both absorbed and throttled" stays an assertion rather than a check: a drifter that vanished because nowhere was better is
    /// indistinguishable, from the outside, from one the throttle silently swallowed.
    /// </remarks>
    internal int LastTickDriftersUnplaced;

    /// <summary>
    /// Safety-valve admissions in the most recently completed tick — repair units begun with insufficient budget because the cell was critical.
    /// </summary>
    internal int LastTickRepairValveFires;

    /// <summary>The per-entity cost the budget is being spent against, in nanoseconds. Published every tick, whether or not anything was admitted.</summary>
    /// <remarks>
    /// Published so a reader can tell a budget that bought little because work was dear from one that bought little because none was queued — which needs
    /// the estimate to exist on the quiet ticks too, so it is written by <see cref="ObserveMigrationCost"/> rather than by the admission path.
    /// </remarks>
    internal double LastTickMeasuredNsPerEntity;

    /// <summary>
    /// Fold this tick's nominations into the persistent queue without planning anything — the path an archetype whose Prep found no work takes.
    /// </summary>
    /// <remarks>
    /// Step 12 CLEARED the nominations here. That was correct for a per-tick list (a plan allocates clusters this tick's Migrate and Finalize must
    /// consume, and neither runs for an idle archetype) but it also threw away the evidence, so a cell that degraded on its last busy tick was never
    /// repaired. Absorbing keeps the evidence and plans nothing, which is the distinction step 12 had no structure to express.
    /// </remarks>
    internal void AbsorbRepairNominations(SpatialGrid grid, long tickNumber)
    {
        var nominations = RepairNominations;
        if (nominations.Count == 0)
        {
            return;
        }

        var queue = EnsureRepairQueue(grid);
        if (queue != null && CellClusterPool != null)
        {
            var start = Stopwatch.GetTimestamp();
            queue.Absorb(nominations, grid, this, tickNumber);
            AccrueQueueMaintenance(queue, start);
        }

        nominations.Clear();
    }

    /// <summary>Create the repair queue on first use, once the grid's knobs are known. Returns <c>null</c> when there is no grid to size it from.</summary>
    internal CellRepairQueue EnsureRepairQueue(SpatialGrid grid)
    {
        if (RepairQueue != null || grid == null)
        {
            return RepairQueue;
        }

        ref readonly var cfg = ref grid.Config;
        RepairQueue = new CellRepairQueue(cfg.RepairQueueMaxCells, cfg.RepairAgingRatePerTick);
        return RepairQueue;
    }

    /// <summary>Reset the throttle's per-tick state. Called from the fence's counter-reset block, beside every other per-tick counter.</summary>
    internal void ResetThrottleTickState()
    {
        LastTickRelocationsThrottled = 0;
        LastTickDriftersUnplaced = 0;
        LastTickRepairValveFires = 0;

        // Not LastTickMeasuredNsPerEntity: ObserveMigrationCost republishes it on the very next line of the caller, and zeroing it here would leave it at
        // zero for any archetype that has no grid to observe against — which reads as "the model has no estimate" rather than "there is no model".

        if (RepairQueue != null)
        {
            RepairQueue.LastTickMaintenanceTicks = 0;
        }
    }

    /// <summary>
    /// Fold the previous tick's measurements into the cost model. Called once per archetype, at the top of Prep, BEFORE the counters are zeroed.
    /// </summary>
    /// <remarks>
    /// <para><b>The numerator is the whole migration, and getting that wrong was the trap.</b> <see cref="LastTickMigrationExecuteMs"/> brackets only the
    /// migrant loop — which since step 6 merely STAGES the index update and since step 7 the EntityMap patch. Both applies happen in later phases, and the
    /// secondary index alone was measured at ~48 % of a migration's cost. An estimator built on the loop alone under-admits by about half in the wrong
    /// direction: it thinks migrations are cheap and admits twice the work the budget can pay for. <see cref="LastTickMigrationTotalMs"/> is the corrected
    /// sum, and step 11 added the two missing timers to produce it.</para>
    /// <para><b>Blended across the three migration kinds</b>, and documented as such. Splitting it needs per-class attribution inside the apply phases,
    /// where the staged records carry no kind at all. A blended MEASURED number is nonetheless strictly better than a hand-set constant that was 22x to
    /// 117x off in AC-12.7's own measurement, which is the failure this replaces.</para>
    /// <para><b>Only updated on a tick that moved something.</b> A quiet tick has no sample; folding its zero in would decay the model toward zero and
    /// admit unbounded work on the tick after a lull.</para>
    /// <para><b>An EWMA cannot ring, and that is the point (<c>AC-11.4</c>).</b> The estimate is PER ENTITY, so admitting fewer entities does not change
    /// it — there is no feedback path from the admission decision back into the measurement, and a first-order low-pass with no feedback converges
    /// monotonically. A debt or credit term would create exactly the loop the AC forbids, on an actuator that at the default budget admits one unit or
    /// zero.</para>
    /// </remarks>
    internal void ObserveMigrationCost(in SpatialGridConfig config)
    {
        var seed = config.RepairNsPerEntity > 0f ? config.RepairNsPerEntity : 1500f;

        var migrated = LastTickMigrationCount;
        if (migrated > 0)
        {
            var sample = LastTickMigrationTotalMs * 1_000_000d / migrated;
            _migrationNsPerEntityEwma = Blend(_migrationNsPerEntityEwma, sample, seed);
        }

        if (_lastTickPlannedEntities > 0)
        {
            // Clamped against the PLANNER's own scale, not the migration seed's. Sharing the band floored a planner that costs tens of nanoseconds per
            // entity at a tenth of the migration seed — 150 ns at the default — which is a fixed surcharge on every repair projection that no measurement
            // could ever remove.
            var sample = _lastTickPlannerTicks * 1_000_000_000d / Stopwatch.Frequency / _lastTickPlannedEntities;
            _plannerNsPerEntityEwma = Blend(_plannerNsPerEntityEwma, sample, PlannerSeedNsPerEntity);
        }

        _lastTickPlannerTicks = 0;
        _lastTickPlannedEntities = 0;

        // 🔴 Published HERE, not as a side effect of the admission decision, and the difference is observable.
        //
        // It used to be written inside RepairCostEstimateNs, which the throttle calls only after establishing that it has requests to weigh — so on any
        // tick with an empty pending queue the telemetry reported ZERO rather than the model's current value. A consumer dividing the budget by it to
        // reproduce the decision then gets +Infinity, and in C# a float-to-int conversion saturates rather than throwing, so the bound silently becomes
        // int.MaxValue. This method runs once per archetype per tick unconditionally, which is what the field's documentation already claimed.
        LastTickMeasuredNsPerEntity = RepairCostEstimateNs(in config);
    }

    /// <summary>One EWMA step, clamped to a band around the configured seed.</summary>
    private static double Blend(double current, double sample, double seed)
    {
        var floor = seed * CostEstimateFloorFactor;
        var ceiling = seed * CostEstimateCeilingFactor;
        var clamped = sample < floor ? floor : (sample > ceiling ? ceiling : sample);
        return current <= 0d ? clamped : (current * (1d - CostEwmaAlpha)) + (clamped * CostEwmaAlpha);
    }

    /// <summary>
    /// The per-entity cost a repair unit is projected against: the measured migration cost plus the measured planner cost, falling back to the configured
    /// seed until the first measurement exists.
    /// </summary>
    private double RepairCostEstimateNs(in SpatialGridConfig config) => MigrationCostEstimateNs(in config) + _plannerNsPerEntityEwma;

    /// <summary>
    /// The measured cost of MOVING one entity, without the repair planner's own overhead — what a relocation is charged.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>A relocation must not be charged the planner's gather-and-sort, and charging it was under-admitting relocations on every tick.</b> The planner
    /// term exists because a repair pays for a Morton sort and a destination allocation that no cell crossing and no drift relocation ever touches. Adding
    /// it to the throttle's per-entity price inflated the cost of exactly the mechanism that does not incur it — and the inflation had a floor rather than
    /// tapering away, because <see cref="Blend"/> clamps the planner EWMA into the MIGRATION seed's band and so never lets it fall below a tenth of that
    /// seed however cheap the planner actually is. Two errors pushing the same way.
    /// </remarks>
    private double MigrationCostEstimateNs(in SpatialGridConfig config)
    {
        var seed = config.RepairNsPerEntity > 0f ? config.RepairNsPerEntity : 1500f;
        return _migrationNsPerEntityEwma > 0d ? _migrationNsPerEntityEwma : seed;
    }

    /// <summary>Record one repair-planning pass so the next tick's <see cref="ObserveMigrationCost"/> has a sample.</summary>
    /// <remarks>
    /// <b>Queue maintenance is subtracted, not included.</b> The caller brackets the whole of <c>PlanCellRepairs</c>, which contains the absorb and the
    /// re-rank — and those are already reported separately as <c>RepairQueueMaintenanceMs</c> for <c>AC-11.5</c>. Counting them here as well would inflate
    /// the per-entity planner estimate, which reduces admissions, which is the queue's own overhead quietly making the queue less useful.
    /// </remarks>
    internal void RecordPlannerCost(long elapsedTicks, int entitiesPlanned)
    {
        var maintenance = RepairQueue?.LastTickMaintenanceTicks ?? 0L;
        var net = elapsedTicks - maintenance;
        _lastTickPlannerTicks += net > 0L ? net : 0L;
        _lastTickPlannedEntities += entitiesPlanned;
    }

    /// <summary>Charge the queue's own maintenance to <c>AC-11.5</c>'s counter.</summary>
    private static void AccrueQueueMaintenance(CellRepairQueue queue, long startTimestamp)
    {
        if (queue != null)
        {
            queue.LastTickMaintenanceTicks += Stopwatch.GetTimestamp() - startTimestamp;
        }
    }

    /// <summary>
    /// Spend one tick's re-clustering budget over the pending migration queue: keep every mandatory request, keep as many intra-cell relocations as the
    /// budget affords, and DROP the rest. Returns the budget left for the repair planner, in nanoseconds.
    /// </summary>
    /// <remarks>
    /// <para><b>Truncation, not deferral, and the distinction is the difference between this being a four-line change and a rule rewrite.</b> <c>CR-01</c>
    /// states that Migrate executes exactly <c>PendingMigrations[0 .. P)</c> where <c>P</c> is the count at Prep's return, and records what happens when
    /// that prefix is too small: "executed requests stay queued and re-execute against slots their entities have left, and the queue grows without bound"
    /// — measured at 224 854 migrations on the twentieth tick. Deferring the tail would reopen it, and would also need the serial fence (which passes
    /// <c>PendingMigrationCount</c>, not the prefix) and <c>SortPendingMigrationsByDestCellKey</c> (which sorts the whole array, unstably) both taught
    /// about the split. Lowering the COUNT leaves prefix == count, so nothing downstream changes and <c>CR-01</c> stays true verbatim.</para>
    /// <para><b>A dropped relocation is not a lost one, and is arguably the better outcome.</b> Its <c>DestClusterChunkId</c> was the least-enlargement
    /// choice against the AABBs of the tick that detected it, which this tick's own migrations have since moved; <c>TryClaimPinnedSlot</c> would reject
    /// the stale pin (<c>CR-02</c>) and fall back to the first fit this whole issue exists to repair. Re-detecting next tick recomputes the choice against
    /// bounds that are current.</para>
    /// <para><b>Cell crossings are charged but never refused.</b> §5.7 makes them correctness — an entity whose position left its cell must move — so a
    /// heavy crossing tick starves repair rather than the other way round. That is deliberate: correctness first, selectivity second.</para>
    /// <para><b>Stable within each class.</b> The partition is two passes over the queue writing into a scratch buffer in encounter order, not a sort, so
    /// the surviving order is exactly the order the detectors produced. <c>AC-11.6</c> needs that; a partition that reordered would make the admitted set
    /// depend on the partition's internals.</para>
    /// </remarks>
    internal double ApplyMigrationThrottle(SpatialGrid grid)
    {
        if (grid == null)
        {
            return 0d;
        }

        ref readonly var cfg = ref grid.Config;
        var budgetNs = cfg.ReclusterBudgetMs * 1_000_000d;
        if (budgetNs <= 0d)
        {
            // 🔴 Zero means NO BUDGET ENFORCEMENT, not "do no re-clustering", and the distinction is not a nicety.
            //
            // `ReclusterBudgetMs = 0` already had a documented meaning before step 11 — it disables REPAIR, because a whole-unit admission needs a positive
            // budget to project against — and step-10 fixtures set it precisely to isolate relocation from repair. Extending it to also drop every
            // relocation would silently change what an existing knob does: a user who turned repair off would lose the steady-state path as well, and the
            // engine would quietly revert to the first-fit placement this whole issue exists to repair. Three step-10 tests caught exactly that.
            //
            // AC-11.8 is still satisfied, and by the same reasoning: relocations stay bounded by DETECTION (one per drifter found in clusters written this
            // tick), the repair queue keeps absorbing and evicting at its cap, nothing is deferred, and nothing deadlocks. Throttling to zero is available
            // to anyone who wants it — set a budget below one entity's cost — it is simply not what zero means.
            return 0d;
        }

        var count = PendingMigrationCount;
        if (count == 0)
        {
            return budgetNs;
        }

        // The MIGRATION cost, not the repair estimate: a relocation moves an entity and pays nothing for the planner. See MigrationCostEstimateNs.
        var estimateNs = MigrationCostEstimateNs(in cfg);
        if (estimateNs <= 0d)
        {
            return budgetNs;
        }

        var queue = PendingMigrations;
        var remainingNs = budgetNs;

        // ── Relocations are LIFTED OUT before anything is compacted ─────────────────────────────────────────────────
        //
        // 🔴 The obvious in-place two-pass partition is WRONG, silently, and loses data with an infinite budget. Pass one
        // compacts the mandatory entries to the front; pass two then re-scans the SAME array for relocations — but every
        // index below the compaction cursor has already been overwritten by a crossing. Traced on [R0, C1]: pass one
        // writes C1 over index 0, pass two sees two crossings, and R0 is gone with nothing counted as throttled.
        //
        // The queue's own layout makes that the normal case rather than a corner. AabbRefresh appends this tick's
        // relocations first and DetectClusterMigrations appends next tick's crossings behind them, so relocations occupy
        // the low indices and exactly min(relocations, crossings) of them are destroyed — every one, on any tick with at
        // least as many crossings as drifters, with RelocationsThrottled reading zero throughout. Step 10's placement
        // would revert to first fit on precisely the busy ticks it matters most on.
        //
        // Copying them aside first is O(n) and one per-archetype buffer that reaches its steady size and stays there.
        // Sized to the whole queue up front — relocations cannot outnumber it — so the copy loop below never has to grow mid-flight. Doubling rather than
        // exact, because an exact fit reallocates on every tick a population creeps upward by one.
        if (_throttleScratch.Length < count)
        {
            _throttleScratch = new MigrationRequest[Math.Max(count, Math.Max(16, _throttleScratch.Length * 2))];
        }

        var relocations = _throttleScratch;
        var relocationCount = 0;
        for (var i = 0; i < count; i++)
        {
            if (queue[i].Kind == MigrationKind.Relocation)
            {
                relocations[relocationCount++] = queue[i];
            }
        }

        // Everything that must happen, compacted to the front. A crossing charges the budget even when that drives it
        // negative — it cannot be refused, so the only honest accounting is to let it consume what it costs and leave
        // repair with nothing.
        var mandatory = 0;
        for (var i = 0; i < count; i++)
        {
            if (queue[i].Kind == MigrationKind.Relocation)
            {
                continue;
            }

            queue[mandatory++] = queue[i];
            remainingNs -= estimateNs;
        }

        // Then the relocations, from the copy, in encounter order until the budget runs out.
        var admitted = mandatory;
        var throttled = 0;
        for (var i = 0; i < relocationCount; i++)
        {
            if (remainingNs < estimateNs)
            {
                throttled = relocationCount - i;
                break;
            }

            queue[admitted++] = relocations[i];
            remainingNs -= estimateNs;
        }

        PendingMigrationCount = admitted;
        LastTickRelocationsThrottled = throttled;
        return remainingNs > 0d ? remainingNs : 0d;
    }
}

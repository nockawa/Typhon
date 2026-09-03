using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// The repair path — a full Morton re-sort and re-pack of a degraded cell (#872 step 12, design §5.2 and §5.6).
/// </summary>
/// <remarks>
/// <para><b>Two mechanisms, not one.</b> Step 10's detect-and-relocate is a per-tick <i>delta</i>: it moves the entities that drifted and leaves the rest
/// alone, which is affordable every tick and converges only while the layout is nearly right. This is the <i>repair</i>: it discards the existing layout of
/// a unit entirely, sorts its entities by position and re-packs them in sort order. Optimal tightness for a given cluster size, ~6 ms for a 100 K-entity
/// cell, and therefore rare by construction.</para>
/// <para><b>Why the delta path cannot do this.</b> Relocation is greedy and local — each drifter goes to whichever existing cluster grows least. A cell whose
/// clusters are ALL wrong has no good destination to offer, so the greedy step shuffles entities between bad boxes. Measured on this branch at step 10: a
/// randomly-laid-out cell produced ~26 relocations per entity over 30 ticks and the mean extent still rose. The re-sort is what recovers such a cell, and it
/// is the only thing in the engine that <b>shrinks a zone map</b> — <c>ZoneMapArray.Widen</c> is the sole other writer and it never narrows.</para>
/// <para><b>Where it runs, and why there.</b> Nomination happens in AabbRefresh, off the same <c>cellSize x 1.2</c> extent check the outlier guard already
/// computes (design parameter P7) — the extents are in registers, so nominating costs one list append on a cluster that has already failed a threshold.
/// Planning happens in <b>Prep</b> of the following tick, which is single-threaded per archetype, and the requests it emits are executed by that same tick's
/// Migrate phase. Everything the plan allocates is therefore created and consumed inside one exclusive fence window, so nothing OUTSIDE the fence — no
/// user-system spawn — can claim into a destination the sort reserved, and the plan is produced by exactly one thread, which is what makes <c>AC-12.4</c>
/// true by construction rather than by scheduling luck. Inside the window a same-tick migration that overflows its own pinned cluster can still first-fit
/// into one of these; that costs the repair a slot and is handled by the fallback chain, not by exclusivity.</para>
/// <para><b>Deviation from §5.2, stated deliberately.</b> The design's cost table gives the repair path "claim / release slot: <b>none</b>", on the grounds
/// that a re-pack knows its destinations and need not claim them. This implementation instead emits ordinary <see cref="MigrationRequest"/>s carrying a
/// pinned cluster AND a pinned slot, and lets <c>ExecuteMigrations</c> move them. The claim it pays is one uncontended CAS on a cache line the copy is about
/// to touch anyway. What it buys is that the ~400 lines maintaining <c>EntityMap</c> keying, the C15 cell-relative rebase, the H1 visibility fold, index
/// element ids, zone maps, dirty-bit deltas and orphan rollback are not duplicated for a path that runs rarely — so <c>AC-12.3</c> holds because the moves go
/// through the pipeline whose invariants are already tested, rather than because a second copy was got right. <c>AC-12.7</c>'s measurement is what
/// adjudicates whether the CAS ever mattered.</para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>Bits per axis in an intra-cell Morton key. Three axes at 21 bits fill 63 of a <see cref="ulong"/>'s 64.</summary>
    /// <remarks>
    /// <para><b>This is not the Morton step 8 deleted.</b> That one keyed the GRID — cell keys — and was removed because a 32-bit 3D Morton code caps the
    /// world at 1 024 cells per axis while the root is a hash map that gains nothing from key locality (see <c>SpatialGridConfig</c> and
    /// <c>VdbBlockKey</c>). This one orders entities WITHIN one cell, where the coordinate range is one cell wide by construction and the world-size
    /// objection does not arise. The two share a name and nothing else.</para>
    /// <para>21 bits resolves one cell into 2 097 152 steps per axis. At any cell size a float position is worth encoding at, that is far below the
    /// quantisation the sort could distinguish, so the key never loses an ordering the geometry actually expresses.</para>
    /// </remarks>
    internal const int MortonBitsPerAxis = 21;

    /// <summary>One entity's place in the sort: its intra-cell Morton key, and where it currently lives.</summary>
    /// <remarks>
    /// <b>Ordered by key then by source location, and the tie-break is load-bearing.</b> Two entities at the same quantised position produce the same key,
    /// and an introsort left to resolve that tie picks by pivot placement — deterministic for a given input, but only as a property of the current BCL
    /// implementation. <c>AC-12.4</c> asserts identical output regardless of worker count and scheduling, which deserves a total order defined here rather
    /// than inherited from a sort's internals. <see cref="SourceLocation"/> is unique across the unit by construction — it is <c>chunkId * 64 + slot</c>,
    /// the engine's own <c>ClusterLocation</c> encoding — so (key, location) is total.
    /// </remarks>
    internal readonly struct RepairEntry : IComparable<RepairEntry>
    {
        /// <summary>Intra-cell Morton code of the entity's centre — see <see cref="EncodeIntraCellMorton"/>.</summary>
        internal readonly ulong MortonKey;

        /// <summary>The entity's current <c>clusterChunkId * 64 + slotIndex</c>.</summary>
        /// <remarks>
        /// <b>A <see cref="long"/>, and not for the range it needs today.</b> The engine's own <c>ClusterLocation</c> encoding is a 32-bit
        /// <c>chunkId * 64 + slot</c>, which overflows at chunk id 2^25 — 33.5 M clusters, 2.1 G entities in one archetype. That is a real ceiling rather
        /// than an absurd one at the scale this issue targets, and here it costs nothing to lift: <c>RepairEntry</c> is a <c>ulong</c> plus an <c>int</c>,
        /// which pads to 16 bytes anyway, so the wider field is free. Narrowing back to the 32-bit form happens at the point the request is built, where
        /// the value is a real cluster location again.
        /// </remarks>
        internal readonly long SourceLocation;

        internal RepairEntry(ulong mortonKey, long sourceLocation)
        {
            MortonKey = mortonKey;
            SourceLocation = sourceLocation;
        }

        /// <inheritdoc />
        public int CompareTo(RepairEntry other)
        {
            var byKey = MortonKey.CompareTo(other.MortonKey);
            return byKey != 0 ? byKey : SourceLocation.CompareTo(other.SourceLocation);
        }
    }

    /// <summary>One candidate cluster for a repair unit: how bad it is, and which cluster it is.</summary>
    /// <remarks>
    /// Ranked worst-first on <see cref="MaxExtent"/>, ties broken on <see cref="ChunkId"/> for the same total-order reason as <see cref="RepairEntry"/>.
    /// </remarks>
    private readonly struct RepairCandidate : IComparable<RepairCandidate>
    {
        internal readonly float MaxExtent;

        internal readonly int ChunkId;

        internal RepairCandidate(float maxExtent, int chunkId)
        {
            MaxExtent = maxExtent;
            ChunkId = chunkId;
        }

        /// <inheritdoc />
        public int CompareTo(RepairCandidate other)
        {
            var byExtent = other.MaxExtent.CompareTo(MaxExtent);
            return byExtent != 0 ? byExtent : ChunkId.CompareTo(other.ChunkId);
        }
    }

    /// <summary>
    /// Cells nominated for repair by the AabbRefresh pass, consumed by the next tick's Prep. Appends go through <see cref="EnqueueRepairNominationsBulk"/>.
    /// </summary>
    /// <remarks>
    /// A list rather than a set: nomination fires per CLUSTER and a cell has many, so duplicates are the norm, and de-duplicating on the producer side would
    /// put a hash lookup on the detection path to save a sort key on the rare planning one. The planner sorts and skips repeats, which it must do anyway to
    /// give itself a deterministic cell order.
    /// </remarks>
    internal readonly List<int> RepairNominations = [];

    /// <summary>Entities re-packed by the repair path this tick — the numerator of <c>AC-12.7</c>'s cost per entity.</summary>
    internal int LastTickRepairedEntityCount;

    /// <summary>Repair units admitted this tick. A unit is one cell's N worst clusters, or one whole cell.</summary>
    internal int LastTickRepairUnitCount;

    /// <summary>Repair units the remaining budget could not finish, and which were therefore never begun (<c>AC-12.5</c>).</summary>
    internal int LastTickRepairUnitsRefused;

    /// <summary>Per-archetype planner scratch, reused across ticks. Prep is single-threaded per archetype, so no locking and no thread statics.</summary>
    private RepairEntry[] _repairEntryScratch = [];

    /// <inheritdoc cref="_repairEntryScratch"/>
    private RepairCandidate[] _repairCandidateScratch = [];

    /// <inheritdoc cref="_repairEntryScratch"/>
    private int[] _repairCellScratch = [];

    /// <inheritdoc cref="_repairEntryScratch"/>
    private int[] _repairDestinationScratch = [];

    /// <summary>
    /// Cells whose last repair plan turned out to be a no-op, against the bounds hash that produced that verdict.
    /// </summary>
    /// <remarks>
    /// Bounded by the number of cells that have both nominated and converged, which is the population that would
    /// otherwise re-sort itself every tick. An entry is dropped the moment its cell is genuinely repaired.
    /// </remarks>
    private readonly Dictionary<int, ulong> _repairNoOpGeometry = [];

    // ══════════════════════════════════════════════════════════════════════════════
    // Intra-cell Morton encoding
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Encode a cell-relative position as a 63-bit Morton key, X occupying the low bit of each triple.</summary>
    /// <param name="relX">Position minus cell origin on X. Values outside <c>[0, cellSize)</c> clamp into the cell.</param>
    /// <param name="relY">Position minus cell origin on Y.</param>
    /// <param name="relZ">Position minus cell origin on Z; pass <c>0</c> for a flat archetype.</param>
    /// <param name="inverseCellSize"><c>SpatialGridConfig.InverseCellSize</c>, precomputed.</param>
    /// <remarks>
    /// <para><b>Clamping is not defensive padding.</b> Migration hysteresis (<c>MigrationHysteresisRatio</c>) deliberately leaves an entity in its old cell
    /// for a dead zone past the boundary, so a cell legitimately holds entities slightly outside its own extent. Without the clamp those quantise past the
    /// axis maximum and wrap into the low bits, placing an entity that is just outside one face at the opposite corner of the sort order — which is worse
    /// than not sorting it at all.</para>
    /// <para>A flat archetype passes <paramref name="relZ"/> as <c>0</c>, so every Z bit is constant and the key degenerates to a 2D Morton code
    /// interleaved with zeros. The ordering that produces is exactly the 2D one, so no separate encoder is needed and the sort cannot tell the
    /// difference.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong EncodeIntraCellMorton(float relX, float relY, float relZ, float inverseCellSize)
    {
        var qx = QuantizeAxis(relX * inverseCellSize);
        var qy = QuantizeAxis(relY * inverseCellSize);
        var qz = QuantizeAxis(relZ * inverseCellSize);
        return SpreadBy2(qx) | (SpreadBy2(qy) << 1) | (SpreadBy2(qz) << 2);
    }

    /// <summary>Map a normalised axis coordinate to <c>[0, 2^<see cref="MortonBitsPerAxis"/> - 1]</c>, clamping out-of-cell values into range.</summary>
    /// <remarks>
    /// The NaN case falls out of the comparison order rather than being tested: <c>!(t &gt; 0f)</c> is true for NaN, so a non-finite coordinate maps to 0
    /// instead of to an undefined float-to-integer conversion. Upstream already rejects non-finite positions — <see cref="GatherClusterCentres"/> excludes
    /// them from its valid mask — so this only has to fail safely, not meaningfully.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong QuantizeAxis(float t)
    {
        const uint scale = (1u << MortonBitsPerAxis) - 1u;
        if (!(t > 0f))
        {
            return 0UL;
        }

        return t >= 1f ? scale : (uint)(t * scale + 0.5f);
    }

    /// <summary>Insert two zero bits after each of the low 21 bits, so three spread values interleave into one 63-bit key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SpreadBy2(ulong x)
    {
        x &= 0x1FFFFFUL;
        x = (x | (x << 32)) & 0x001F00000000FFFFUL;
        x = (x | (x << 16)) & 0x001F0000FF0000FFUL;
        x = (x | (x << 8)) & 0x100F00F00F00F00FUL;
        x = (x | (x << 4)) & 0x10C30C30C30C30C3UL;
        x = (x | (x << 2)) & 0x1249249249249249UL;
        return x;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Nomination — the AabbRefresh side
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Append one slice's repair nominations to the archetype-wide list, under <c>_finalizeLock</c>. Called once per slice, never per cluster.
    /// </summary>
    internal void EnqueueRepairNominationsBulk(List<int> nominations)
    {
        if (nominations == null || nominations.Count == 0)
        {
            return;
        }

        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx);
        try
        {
            RepairNominations.AddRange(nominations);
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }

        // Cleared, matching EnqueueMigrationsBulk. Redundant today — every slice allocates its own list — and exactly the
        // assumption that stops being true the day these buffers are pooled.
        nominations.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Planning — the Prep side
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Consume the previous tick's nominations and emit pinned migration requests that re-pack each admitted cell in Morton order. Returns the number of
    /// entities the plan will move; <paramref name="budgetUsedMs"/> receives the projected spend.
    /// </summary>
    /// <remarks>
    /// <para><b>Single-threaded by contract.</b> Called from <c>PrepareArchetypeFenceCore</c>, which runs one work item per archetype. Everything here
    /// mutates archetype-local state and allocates clusters, and none of it is written to tolerate a sibling.</para>
    /// <para><b>The budget admits units; it never stops one (§5.6).</b> A Morton sort cannot be halved — a partly re-sorted cell has paid the cost and
    /// banked only part of the benefit — so a unit whose projected cost exceeds the remaining budget is not begun at all (<c>AC-12.5</c>). Refused units are
    /// counted, not queued: the priority queue that would let a refused cell jump ahead next tick is step 11's, and inventing half of it here would leave
    /// two policies to reconcile.</para>
    /// <para><b>Refusal continues rather than stopping.</b> The cell order is by cell key, which is arbitrary with respect to cost, so breaking out on the
    /// first refusal would let one large cell at a low key permanently block every other cell. Continuing is first-fit-under-budget, which starves large
    /// cells instead. Neither is a policy; choosing between them is exactly what step 11's ranking by expected selectivity gain exists to do, and the
    /// variant that does some work was preferred to the variant that can do none.</para>
    /// </remarks>
    internal int PlanCellRepairs(SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor, out double budgetUsedMs)
    {
        budgetUsedMs = 0d;
        LastTickRepairedEntityCount = 0;
        LastTickRepairUnitCount = 0;
        LastTickRepairUnitsRefused = 0;

        var nominationCount = RepairNominations.Count;
        if (nominationCount == 0)
        {
            return 0;
        }

        // Drained unconditionally, ahead of every early-out below. A nomination describes the tick that produced it, so carrying one forward would repair a
        // cell on evidence that has since been recomputed — and an archetype that stops meeting the preconditions would accumulate them without bound.
        // Doubling, not exact. An exact fit reallocates on every tick a population creeps upward by one, and this runs on Prep — the single-threaded
        // phase every archetype waits on. The three scratch buffers are per-archetype and live for the process, so their steady state is one allocation.
        if (_repairCellScratch.Length < nominationCount)
        {
            _repairCellScratch = new int[Math.Max(nominationCount, Math.Max(16, _repairCellScratch.Length * 2))];
        }
        RepairNominations.CopyTo(_repairCellScratch, 0);
        RepairNominations.Clear();

        if (grid == null || ClusterSegment == null || CellClusterPool == null || ClusterCellMap == null || ClusterAabbs == null)
        {
            return 0;
        }

        if (!SpatialSlot.HasSpatialIndex || SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            return 0;
        }

        ref readonly var cfg = ref grid.Config;
        var budgetNs = cfg.ReclusterBudgetMs * 1_000_000d;
        if (budgetNs <= 0d || cfg.RepairNsPerEntity <= 0f)
        {
            return 0;
        }

        // Sorted so the cell order is a property of the cells, not of the order workers happened to nominate them in (AC-12.4). De-duplication rides along
        // on the sort: nomination fires per cluster, so a badly degraded cell appears once per bad cluster it holds.
        var cellKeys = _repairCellScratch;
        Array.Sort(cellKeys, 0, nominationCount);

        var remainingNs = budgetNs;
        var totalMoved = 0;
        var previousKey = -1;
        for (var i = 0; i < nominationCount; i++)
        {
            var cellKey = cellKeys[i];
            if (cellKey == previousKey)
            {
                continue;
            }
            previousKey = cellKey;

            totalMoved += RepairOneCell(cellKey, grid, ref accessor, ref remainingNs);
        }

        // ── Top up the deferred-drain list for everything the plan added ────────────────────────────────────────────
        //
        // PreSizeMigrationBuffers sizes _drainedClusterIds from PendingMigrationCount, on the premise that one migration
        // releases at most one source slot — and it runs in Prep's CORE, before this. A repair breaks the premise from
        // both ends: it files more migrations, and it has already consumed one drain entry per fresh destination. The
        // Migrate phase would then overflow into RecordClusterDrain's fallback grow, which parallel workers reach and
        // which writes its entry AFTER releasing the lock, re-reading the field — so an entry can be discarded by a
        // concurrent resize, and a discarded drain record is RP-03's cluster that never received an entity and leaks its
        // chunk id.
        //
        // Once, here, over the WHOLE queue rather than per unit. Per unit it would have been `_drainedCount + count`,
        // which under-counts twice over: it ignores the requests other units filed, and it ignores whatever the
        // cell-crossing detectors queued before the planner ran. _drainedCount is every destination this plan allocated
        // (it was zeroed at the top of Prep) and PendingMigrationCount is every request Migrate will drain, so their sum
        // is the true ceiling on drain records for the tick.
        if (totalMoved > 0)
        {
            PreSizeDrainedClusterIds(_drainedCount + PendingMigrationCount);
        }

        LastTickRepairedEntityCount = totalMoved;
        budgetUsedMs = (budgetNs - remainingNs) / 1_000_000d;
        return totalMoved;
    }

    /// <summary>
    /// Plan one cell's repair unit: rank its clusters, admit the unit if the budget covers it, then hand off to <see cref="ExecuteRepairPlan"/>. Returns the
    /// number of entities the unit will move, or <c>0</c> when nothing was begun.
    /// </summary>
    private int RepairOneCell(int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor, ref double remainingNs)
    {
        var clusters = CellClusterPool.GetClusters(cellKey);
        if (clusters.Length < 2)
        {
            return 0; // A single cluster is already its own optimal packing — a sort cannot improve a partition of one.
        }

        ref readonly var cfg = ref grid.Config;
        if (_repairCandidateScratch.Length < clusters.Length)
        {
            _repairCandidateScratch = new RepairCandidate[Math.Max(clusters.Length, Math.Max(16, _repairCandidateScratch.Length * 2))];
        }

        // Ranked on the same quantity the cellSize x 1.2 trigger reads — the largest axis extent of the stored, cell-relative bound. A cluster with no bound
        // (freed, or never populated) scores negative infinity and sorts last, so it is picked only when the unit takes the whole cell, and then it
        // contributes no entities.
        var candidates = _repairCandidateScratch;
        for (var i = 0; i < clusters.Length; i++)
        {
            var chunkId = clusters[i];
            var extent = float.NegativeInfinity;
            if ((uint)chunkId < (uint)ClusterAabbs.Length)
            {
                ref var box = ref ClusterAabbs[chunkId];
                if (!float.IsPositiveInfinity(box.MinX))
                {
                    extent = MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
                    var extentZ = box.MaxZ - box.MinZ;
                    if (float.IsFinite(extentZ))
                    {
                        extent = MathF.Max(extent, extentZ);
                    }
                }
            }
            candidates[i] = new RepairCandidate(extent, chunkId);
        }

        Array.Sort(candidates, 0, clusters.Length);

        // ── Nothing has moved since this cell was found already-packed? Then the sort has the same answer ────────────
        //
        // IsAlreadyPackedInSortOrder stops the MOVES, not the cost of discovering there are none: nomination fires on
        // extent alone and a Morton packing does not in general bring every cluster under the threshold, so a converged
        // cell is nominated again on every subsequent tick and pays a full GatherClusterCentres walk plus an Array.Sort
        // to reach the same verdict — on Prep, single-threaded, and charged to no budget because a unit that emits
        // nothing is never debited. Measured with the whole cell as the unit, that is 2 000 entities gathered and sorted
        // per tick, forever.
        //
        // The memo is the hash of the unit's stored bounds, which the ranking loop above has just read into registers.
        // Same bounds as the last no-op ⇒ the same entity positions produce the same key order ⇒ the same partition.
        //
        // 🔴 A heuristic, in one direction. Entities that shuffle STRICTLY INSIDE their clusters' existing bounds change
        // the Morton order without changing any bound, and this will skip a re-sort that would have helped. That is the
        // same exposure CR-03 already records as a scoped exception, and it is the delta path's population, not repair's.
        var geometry = HashUnitGeometry(candidates, clusters.Length);
        if (_repairNoOpGeometry.TryGetValue(cellKey, out var lastNoOp) && lastNoOp == geometry)
        {
            return 0;
        }

        var perUnit = cfg.RepairWorstClustersPerUnit;
        var unitClusters = perUnit <= 0 ? clusters.Length : Math.Min(perUnit, clusters.Length);
        if (unitClusters < 2)
        {
            return 0;
        }

        // Population first, because the budget decision has to precede every byte of work (AC-12.5). An occupancy popcount is one load per cluster; the
        // entity walk that follows is what costs, and it does not run unless the unit is admitted.
        var population = 0;
        for (var i = 0; i < unitClusters; i++)
        {
            var clusterBase = accessor.GetChunkAddress(candidates[i].ChunkId);
            population += BitOperations.PopCount(*(ulong*)clusterBase & Layout.FullMask);
        }

        if (population < 2)
        {
            return 0;
        }

        var projectedNs = population * (double)cfg.RepairNsPerEntity;
        if (projectedNs > remainingNs)
        {
            LastTickRepairUnitsRefused++;
            return 0;
        }

        var moved = ExecuteRepairPlan(cellKey, grid, ref accessor, candidates, unitClusters, population);
        if (moved == 0)
        {
            // Remembered against the geometry that produced it, so the next tick's nomination costs a dictionary probe
            // rather than a gather and a sort. Recorded here rather than inside ExecuteRepairPlan because this is where
            // the hash is in scope and where the budget decision lives.
            _repairNoOpGeometry[cellKey] = geometry;
            return 0;
        }

        _repairNoOpGeometry.Remove(cellKey);
        remainingNs -= projectedNs;
        LastTickRepairUnitCount++;
        return moved;
    }

    /// <summary>
    /// Gather, sort and emit. Kept separate from the admission decision so that the "never started" half of <c>AC-12.5</c> is one early return in front of a
    /// method that performs the whole unit, rather than a condition threaded through the work.
    /// </summary>
    private int ExecuteRepairPlan(int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor, RepairCandidate[] candidates,
        int unitClusters, int population)
    {
        ref readonly var cfg = ref grid.Config;
        if (_repairEntryScratch.Length < population)
        {
            _repairEntryScratch = new RepairEntry[Math.Max(population, Math.Max(64, _repairEntryScratch.Length * 2))];
        }
        var entries = _repairEntryScratch;

        grid.CellOrigin(cellKey, out var originX, out var originY, out var originZ);
        var flat = SpatialSlot.FieldInfo.FieldType is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F;

        Span<float> centreScratch = stackalloc float[3 * MaxSlotsPerCluster];
        var count = 0;
        for (var i = 0; i < unitClusters && count < population; i++)
        {
            var chunkId = candidates[i].ChunkId;
            var centres = GatherClusterCentres(chunkId, ref accessor, centreScratch);
            var bits = centres.ValidMask;
            while (bits != 0 && count < population)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var key = EncodeIntraCellMorton(centres.X(slot) - originX, centres.Y(slot) - originY,
                    flat ? 0f : centres.Z(slot) - originZ, cfg.InverseCellSize);
                entries[count++] = new RepairEntry(key, (long)chunkId * MaxSlotsPerCluster + slot);
            }
        }

        if (count < 2)
        {
            return 0;
        }

        Array.Sort(entries, 0, count);

        // ── Already sorted? Then moving 2 000 entities buys exactly nothing ──────────────────────────────────────────
        //
        // A repair leaves the unit packed in Morton order. Nothing about that decays on its own, so the NEXT tick's
        // nomination — which fires on extent alone — sees the same geometry and asks for the same re-pack, which produces
        // the identical partition. Without this check the cell is re-packed on every single tick, forever, at full cost
        // and zero gain. Measured before the check: a 2 000-entity cell re-packed all 2 000 on ticks 2 through 6 with the
        // mean extent pinned at 23.0 the whole time.
        //
        // Nomination cannot be the place to stop it. The trigger is a threshold on extent, and a Morton packing does not
        // in general get every cluster under it — at 41 clusters one stayed above 0.75 x cellSize, which is a property of
        // the curve rather than a failure to repair. Whether a re-sort would HELP is a different question from whether the
        // cell looks bad, and this is the cheapest exact answer to it: if every packed group already draws from one source
        // cluster, the partition and the sorted partition coincide, so every resulting bound is the bound the cell already
        // has. One pass over an array that has just been sorted, against a move of the whole unit.
        if (IsAlreadyPackedInSortOrder(entries, count))
        {
            return 0;
        }

        // Fresh destinations, not the unit's own clusters. A re-pack is a PERMUTATION of the unit's slots, and ExecuteMigrations moves one entity at a time
        // into a slot it claims — so a destination still occupied by an entity that has yet to move would fail its claim and fall back to first fit, which
        // is the placement this whole issue exists to repair. Packing into fresh clusters makes every destination free by construction. The cost is one
        // extra cluster set for the width of the fence window: the sources drain empty and are freed by the same Finalize pass that frees any other emptied
        // cluster.
        var capacity = BitOperations.PopCount(Layout.FullMask);

        // ── Every destination first, then every request ──────────────────────────────────────────────────────────────
        //
        // Interleaving them would let an allocation fail partway and leave the unit HALF re-packed, which RP-01 calls
        // strictly worse than untouched: the budget has been charged, some entities have moved into the sorted order and
        // the rest have not, and the resulting bounds are neither the old ones nor the new ones. Allocating up front makes
        // the failure atomic — nothing is emitted, the clusters already taken are on the drain list and Finalize frees
        // them because they are still empty.
        var destinationCount = (count + capacity - 1) / capacity;
        if (_repairDestinationScratch.Length < destinationCount)
        {
            _repairDestinationScratch = new int[Math.Max(destinationCount, Math.Max(8, _repairDestinationScratch.Length * 2))];
        }

        var destinations = _repairDestinationScratch;
        for (var d = 0; d < destinationCount; d++)
        {
            destinations[d] = AllocateEmptyClusterForCell(cellKey, grid, ref accessor);
            if (destinations[d] < 0)
            {
                return 0;
            }
        }

        // Reserved AFTER the destinations exist, so a failed allocation does not leave the queue grown for requests that
        // were never filed. One growth, not log2(count) of them: EnqueueMigration doubles PendingMigrations on overflow,
        // so emitting 2 000 requests one at a time cost about seven Array.Resize copies inside Prep.
        ReservePendingMigrationCapacity(count);

        for (var i = 0; i < count; i++)
        {
            var source = entries[i].SourceLocation;
            EnqueueMigration(new MigrationRequest((int)(source / MaxSlotsPerCluster), (int)(source % MaxSlotsPerCluster), cellKey,
                destinations[i / capacity], i % capacity));
        }

        var emitted = count;

        // No trace event of its own. Adding one means an event id, a DTO, an OpenAPI schema entry and a decoder arm, and the three counters this path
        // maintains already answer the questions step 12 asks — how many entities moved, in how many units, and how many units the budget refused. The
        // per-cluster ClusterCellAssign fired by each destination allocation is what a trace needs to see the re-pack happen.
        return emitted;
    }

    /// <summary>Order-sensitive hash of a cell's ranked cluster bounds — the memo key for a no-op verdict.</summary>
    /// <remarks>
    /// Over the RANKED candidates rather than the raw pool, so a change in which clusters the unit would take is itself a
    /// change. The bit patterns go in raw: two bounds that differ by an ULP must hash differently, and float equality
    /// would be the wrong comparison for a cache key even where it is the right one for geometry.
    /// </remarks>
    private ulong HashUnitGeometry(RepairCandidate[] candidates, int clusterCount)
    {
        var hash = 1469598103934665603UL; // FNV-1a offset basis
        for (var i = 0; i < clusterCount; i++)
        {
            ref var box = ref ClusterAabbs[candidates[i].ChunkId];
            hash = Mix(hash, (uint)candidates[i].ChunkId);
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MinX));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MinY));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MinZ));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MaxX));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MaxY));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MaxZ));
        }

        return hash;
    }

    /// <summary>One FNV-1a round over four bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong hash, uint value) => (hash ^ value) * 1099511628211UL;

    /// <summary>
    /// Would the sorted packing reproduce the partition the unit already has? One pass over the sorted entries.
    /// </summary>
    /// <remarks>
    /// <para>Group <c>g</c> of the packing is <c>entries[g * capacity .. (g + 1) * capacity)</c>, and it becomes one
    /// destination cluster. If every entry in every group already lives in the same source cluster, then each destination
    /// would hold exactly the entities one existing cluster holds — same sets, so the same bounds, so no selectivity
    /// changes hands and the entire move is waste.</para>
    /// <para><b>Conservative in the safe direction.</b> The test asks about SETS, not about slot order, so a unit whose
    /// entities sit in the right clusters but the wrong slots within them is reported as already packed — correctly, since
    /// intra-cluster order is invisible to every query: a cluster is opened or pruned as a whole, on its bound.</para>
    /// <para>It is not an ordering test either. A group drawing from one source cluster is enough; which cluster, and in
    /// what sequence the groups appear, does not matter, because the destination assignment is by group index and the
    /// bounds are what the caller is trying to improve.</para>
    /// </remarks>
    internal bool IsAlreadyPackedInSortOrder(RepairEntry[] entries, int count)
    {
        var capacity = BitOperations.PopCount(Layout.FullMask);
        for (var start = 0; start < count; start += capacity)
        {
            var end = Math.Min(start + capacity, count);
            var groupCluster = entries[start].SourceLocation / MaxSlotsPerCluster;
            for (var i = start + 1; i < end; i++)
            {
                if (entries[i].SourceLocation / MaxSlotsPerCluster != groupCluster)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Allocate a cluster attached to <paramref name="cellKey"/> with <b>no</b> slot claimed, and register it for the Finalize sweep.
    /// </summary>
    /// <remarks>
    /// <para><b>Empty, unlike every other allocation site.</b> <c>ClaimSlotInCell</c>'s slow path allocates a cluster and publishes occupancy <c>1</c> in the
    /// same breath, because it allocates on behalf of a caller who is claiming slot 0. Repair allocates on behalf of a PLAN, and the entities arrive later
    /// in the same fence window through the ordinary migration path — so occupancy starts at zero and each arrival claims its own pinned slot. Pre-setting
    /// the bits would be cheaper, and is what "no claim on the repair path" would literally mean, but an occupancy bit is authoritative: a set bit with no
    /// entity behind it is counted by the unfiltered <c>Count()</c> fast path, which sums occupancy popcounts, and would over-report the database for the
    /// width of a tick.</para>
    /// <para><b><see cref="RecordClusterDrain"/> is the leak guard, and it is exactly the right one.</b> If every request targeting this cluster is skipped
    /// — a source slot emptied by a destroy, or by an earlier queued migration — the cluster stays empty, and nothing would ever release a slot in it, which
    /// is the only event that normally schedules a cluster for freeing. Recording the drain here puts it in front of <c>DrainPendingClusterFinalizations</c>,
    /// whose pass already re-reads occupancy and frees only what is still empty, leaving a populated cluster alone. No new sweep, and no special case.</para>
    /// <para><b>Zone maps are invalidated here, and this is their only narrowing point in the engine.</b> A recycled chunk id inherits the min/max of
    /// whatever lived there before, because <c>ZoneMapArray.Widen</c> is the only writer on the hot path and it never narrows — the class carries an
    /// <c>Invalidate</c> for exactly this and, until now, no caller at all. Stale-wide bounds are conservative (<c>MayContain</c> over-reports, so queries
    /// stay correct and merely open clusters they need not), but they are also what would make <c>AC-12.2</c> unmeasurable: the re-pack's locality-grouped
    /// contents can only be observed to shrink a zone map if the map starts from those contents rather than from a previous tenant's.</para>
    /// </remarks>
    private int AllocateEmptyClusterForCell(int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor)
    {
        var newChunkId = AllocateNewCluster(null);
        if (newChunkId < 0)
        {
            return -1;
        }

        EnsureClusterCellMapCapacity(newChunkId + 1);
        ClusterCellMap[newChunkId] = cellKey;
        CellClusterPool.AddCluster(cellKey, newChunkId);

        ref var cell = ref grid.GetCell(cellKey);
        Interlocked.Increment(ref cell.ClusterCount);

        var newBase = accessor.GetChunkAddress(newChunkId, true);
        Volatile.Write(ref *(ulong*)newBase, 0UL);

        InvalidateClusterZoneMaps(newChunkId);
        RecordClusterDrain(newChunkId);

        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return newChunkId;
    }

    /// <summary>Drop every indexed field's cached min/max for one cluster, so the next <c>Widen</c> rebuilds them from the cluster's actual contents.</summary>
    private void InvalidateClusterZoneMaps(int clusterChunkId)
    {
        InvalidateZoneMapsIn(IndexSlots, clusterChunkId);
        InvalidateZoneMapsIn(TransientIndexSlots, clusterChunkId);
    }

    /// <inheritdoc cref="InvalidateClusterZoneMaps"/>
    private static void InvalidateZoneMapsIn<TStore>(ClusterIndexSlot<TStore>[] ixSlots, int clusterChunkId) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            ref var ixSlot = ref ixSlots[s];
            for (var f = 0; f < ixSlot.Fields.Length; f++)
            {
                ixSlot.Fields[f].ZoneMap?.Invalidate(clusterChunkId);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

// DatabaseEngine — tick-fence commit pipeline (partial). Extracted from DatabaseEngine.cs for file-size / IDE-analysis reasons; behaviour unchanged.
// Serializes dirty component data to the WAL at each tick boundary: WriteTickFence → per-table/per-archetype fence Prepare/Finalize, AABB recompute,
// and dirty-bit delta flushing. See claude/overview/04-data.md and the durability rules (LOG-*) for the fence contract.
public partial class DatabaseEngine
{
    /// <summary>
    /// Closes a tick: publishes the tick's pending work and establishes the crash-recovery boundary. <b>Must be called once per tick.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Four responsibilities</b>, not one:</para>
    /// <list type="bullet">
    ///   <item>serializes dirty <c>SingleVersion</c> component data to the WAL — one TickFence chunk per SV ComponentTable;</item>
    ///   <item>executes the tick's pending <b>cluster migrations</b> (entities that crossed a spatial cell boundary);</item>
    ///   <item>recomputes <b>cluster spatial AABBs</b> and the per-cell spatial index;</item>
    ///   <item>drains <b>zone maps</b> and dirty-bit deltas.</item>
    /// </list>
    /// <para><b>Under <see cref="TyphonRuntime"/> this is automatic</b> — the runtime calls it from its tick-end callback and
    /// application code does not. <b>A host that does NOT use the runtime must call it itself, once per tick</b>, after that tick's writes have been
    /// committed and before the next tick begins. See the embedding guide (<c>doc/guide/embedding-without-the-runtime.md</c>).</para>
    /// <para><b>What happens if it is never called.</b> Nothing throws and nothing is corrupted — the database silently stops maintaining itself:
    /// <c>SingleVersion</c> components degrade to <c>Transient</c>-like durability (no crash recovery), queued cluster migrations never execute, and
    /// spatial AABBs and zone maps go stale, so spatial queries return results computed against old positions.</para>
    /// <para><b>Threading — a caller obligation, not an enforced one</b> (rule <c>EW-01</c>). No other thread may mutate the database while this runs: the
    /// fence rewrites cluster B+Trees, the EntityMap, the per-cell spatial index and the cluster-to-cell map, and a concurrent writer corrupts them silently.
    /// Under <see cref="TyphonRuntime"/> it holds by construction — the fence runs from the scheduler's tick-end callback, after every system has completed,
    /// and each system's Transaction is committed and disposed in its own epilogue. A host driving the fence itself owns the same guarantee; trivially
    /// satisfied single-threaded, unchecked otherwise.</para>
    /// <para><b>The one trap inside the runtime is a side transaction.</b> <c>TickContext.CreateSideTransaction</c> hands back an ORDINARY transaction, so it
    /// can write an indexed field and mutate a B+Tree, and the caller owns its <c>Commit</c> and <c>Dispose</c> — nothing joins it to the tick. A side
    /// transaction held past the end of the system that created it, and committed while the fence runs, is exactly the concurrent mutation this contract
    /// forbids. Commit and dispose it before the system returns.</para>
    /// <para><b>Checkpointing is separate and automatic</b> (timer, dirty-page threshold, back-pressure, graceful shutdown).
    /// <see cref="ForceCheckpoint"/> is not part of the tick loop.</para>
    /// </remarks>
    /// <param name="tickNumber">Monotonic tick identifier. Must increase between calls.</param>
    /// <param name="changeSet">ChangeSet for dirty-page tracking across the whole fence. Under the runtime this is the per-tick UoW's shared ChangeSet
    /// (see <see cref="UnitOfWork.ChangeSet"/>), which consolidates the fence's dirty pages with the rest of the tick. <b>Pass <c>null</c> when driving
    /// the fence from a host without a per-tick UoW</b> — this method then creates and commits a one-shot ChangeSet itself, which is correct but yields
    /// per-fence rather than per-tick consolidation.</param>
    /// <returns>Highest LSN written, or 0 if nothing was serialized.</returns>
    public long WriteTickFence(long tickNumber, ChangeSet changeSet = null)
    {
        // When the caller doesn't supply a ChangeSet (e.g., tests that invoke WriteTickFence outside a UoW), we own the lifecycle: create a fresh
        // ChangeSet, thread it through the per-table tick-fence callees, and commit it ourselves at the end. Production callers (TyphonRuntime)
        // pass _currentUow.ChangeSet so dirty-page tracking is consolidated with everything else this tick — UoW.Flush handles the actual writeback.
        var ownChangeSet = changeSet == null;
        if (ownChangeSet)
        {
            changeSet = MMF.CreateChangeSet();
        }

        long highestLSN;
        try
        {
            // EW-01's window, opened where the rule's scope says it opens. It enrols this thread, so the fence's own serial writes pass; a mutation of a
            // fence-owned structure from any other thread while this runs throws at the mutation site instead of corrupting it silently.
            using var window = EpochManager.FenceWindow.Open();
            highestLSN = WriteTickFenceCore(tickNumber, changeSet);
        }
        finally
        {
            if (ownChangeSet)
            {
                changeSet.SaveChanges();
                changeSet.ReleaseDirtyMarks();
            }
        }

        return highestLSN;
    }

    private long WriteTickFenceCore(long tickNumber, ChangeSet changeSet)
    {
        long highestLSN = 0;
        using var epochGuard = EpochGuard.Enter(EpochManager);

        foreach (var table in _componentTableByType.Values)
        {
            var contributed = ProcessTableFence(table, tickNumber, changeSet);
            if (contributed > highestLSN)
            {
                highestLSN = contributed;
            }
        }

        // Cluster tick fence: serialize dirty cluster-backed entity data to WAL
        WriteClusterTickFence(tickNumber, ref highestLSN, changeSet);

        if (highestLSN > 0)
        {
            Interlocked.Exchange(ref _lastTickFenceLSN, highestLSN);
        }

        return highestLSN;
    }

    /// <summary>Per-thread scratch arena for fence batches — ProcessTableFence is documented safe to call concurrently across distinct tables.</summary>
    [ThreadStatic]
    private static CommitBatchArena _fenceArena;

    /// <summary>Soft cap on a single fence <c>Append</c> frame; larger fences split into multiple Appends (each fence record is individually committed).</summary>
    private const int MaxFenceBatchBytes = 256 * 1024;

    private long AppendFenceBatch(ref CommitBatchBuilder batch)
    {
        var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
        return DurabilityLog.Append(ref batch, ref wc);
    }

    /// <summary>Per-thread descriptor scratch for columnar fence emission — one entry per dirty cluster, payload-free (#559).</summary>
    [ThreadStatic]
    private static RecordCodec.FenceBlockDescriptor[] _fenceBlocks;

    /// <summary>
    /// Per-thread arena for the collection-content batch that accompanies a columnar fence (#389). Separate from <see cref="_fenceArena"/>: the two are
    /// never live at once, but a cluster fence and a flat fence are different call paths and sharing one arena between them buys nothing.
    /// </summary>
    [ThreadStatic]
    private static CommitBatchArena _fenceCollectionArena;

    /// <summary>
    /// Emits the full <c>ComponentCollection</c> content of every dirty entity in a cluster fence, as its own fence batch of CollectionDelta records.
    /// </summary>
    /// <remarks>
    /// A second walk of the same dirty bits the block loop used. It could have been folded into that loop, but the block loop's job is to describe contiguous
    /// slot RANGES for bulk copying, while this one is inherently per-entity and per-field — merging them would put a scalar-rate loop inside a bulk-rate one
    /// for the benefit of the rare archetype that carries a collection at all.
    /// </remarks>
    private unsafe long AppendClusterCollectionContent(
        ArchetypeEngineState engineState,
        long[] dirtyBits,
        ref ChunkAccessor<PersistentStore> accessor,
        int entityIdsOffset,
        ReadOnlySpan<int> slotIndices,
        ReadOnlySpan<int> componentSizes,
        ReadOnlySpan<int> componentOffsets,
        long tickNumber)
    {
        var arena = _fenceCollectionArena ??= new CommitBatchArena();
        arena.Reset();
        var batch = new CommitBatchBuilder(arena, tickNumber, 0, true);
        var batchBytes = 0;
        long highestLSN = 0;

        for (var wi = 0; wi < dirtyBits.Length; wi++)
        {
            var word = dirtyBits[wi];
            while (word != 0)
            {
                var slotIdx = BitOperations.TrailingZeroCount((ulong)word);
                word &= word - 1;

                var clusterBase = accessor.GetChunkAddress(wi);
                var entityId = *(long*)(clusterBase + entityIdsOffset + (slotIdx * sizeof(long)));
                if (entityId == 0)
                {
                    continue;   // unoccupied cluster slot
                }

                for (var c = 0; c < slotIndices.Length; c++)
                {
                    var table = engineState.SlotToComponentTable[slotIndices[c]];
                    if (!table.HasCollections)
                    {
                        continue;
                    }

                    var compSize = componentSizes[c];
                    var value = new ReadOnlySpan<byte>(clusterBase + componentOffsets[c] + (slotIdx * compSize), compSize);
                    batchBytes += CollectionContentEmitter.Emit(ref batch, table, entityId, (ushort)slotIndices[c], value);
                }

                if (batchBytes > MaxFenceBatchBytes)
                {
                    highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                    arena.Reset();
                    batch = new CommitBatchBuilder(arena, tickNumber, 0, true);
                    batchBytes = 0;
                }
            }
        }

        if (!batch.IsEmpty)
        {
            highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
        }

        return highestLSN;
    }

    private long AppendFenceBlockBatch(
        RecordCodec.FenceBlockDescriptor[] blocks,
        int count,
        ushort archetypeId,
        long tickNumber,
        int entityKeysOffset,
        ReadOnlySpan<int> slotIndices,
        ReadOnlySpan<int> componentSizes,
        ReadOnlySpan<int> componentOffsets,
        int totalComponentSize,
        ReadOnlySpan<ulong> columnHandleRanges)
    {
        var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
        return DurabilityLog.AppendFenceBlocks(
            blocks.AsSpan(0, count), archetypeId, tickNumber, entityKeysOffset,
            slotIndices, componentSizes, componentOffsets, totalComponentSize, columnHandleRanges, ref wc);
    }

    /// <summary>
    /// Tick-fence body for a single <see cref="ComponentTable"/>. Encapsulates the per-table work historically inlined in <see cref="WriteTickFenceCore"/>'s
    /// loop: dirty-bitmap snapshot, WAL chunk serialization, shadow + spatial maintenance, dirty-ring archive. Returns the highest LSN published by this table
    /// (0 if none / skipped). Safe to call concurrently across distinct tables — touches only the table's own state plus the MPSC <see cref="WalCommitBuffer"/>.
    /// </summary>
    internal long ProcessTableFence(ComponentTable table, long tickNumber, ChangeSet changeSet)
    {
        if (table.StorageMode == StorageMode.Versioned || table.DirtyBitmap == null)
        {
            return 0;
        }

        if (!table.DirtyBitmap.HasDirty)
        {
            table.PreviousTickDirtyBitmap = null;
            table.PreviousTickHadDirtyEntities = false;
            return 0;
        }

        // Snapshot DirtyBitmap — atomic swap, clears bitmap for next tick
        var dirtyBits = table.DirtyBitmap.Snapshot();

        // The runtime iterates set bits at dispatch time (same pattern as ProcessSpatialEntries).
        table.PreviousTickDirtyBitmap = dirtyBits;
        table.PreviousTickHadDirtyEntities = true;

        // Popcount once — used both by the per-table fence span payload and by the WAL chunk sizing path below.
        var entryCount = 0;
        for (var i = 0; i < dirtyBits.Length; i++)
        {
            entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
        }

        long highestLSN = 0;
        var tableScope = TyphonEvent.BeginWriteTickFenceTable(table.WalTypeId, entryCount);
        try
        {
            var walPublished = false;
            var hasShadow = table.HasShadowableIndexes;
            var hasSpatial = table.SpatialIndex != null && table.SpatialIndex.FieldInfo.Mode == SpatialMode.Dynamic;

            // WAL serialization: SV only — Transient has no WAL persistence, skip straight to shadow processing. Each dirty entity
            // becomes one fence-flagged Slot record through the v2 codec (M3): the entity PK is read from the chunk overhead (offset 0,
            // the same read PipelineExecutor does at :724), so fence records are logical (EntityId, ComponentTypeId), never physical chunk ids.
            if (table.StorageMode == StorageMode.SingleVersion && entryCount > 0)
            {
                var stride = table.ComponentStorageSize;
                var overhead = table.ComponentOverhead;
                var componentTypeId = (ushort)ArchetypeRegistry.GetComponentTypeId(table.Definition.POCOType);
                var recOverhead = RecordHeader.SizeInBytes + SlotRecordBody.FixedSize;

                // One arena per thread — ProcessTableFence is documented safe to call concurrently across distinct tables.
                var fenceArena = _fenceArena ??= new CommitBatchArena();
                fenceArena.Reset();
                var batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, true);
                var batchBytes = 0;

                var accessor = table.ComponentSegment.CreateChunkAccessor();
                try
                {
                    for (var wi = 0; wi < dirtyBits.Length; wi++)
                    {
                        var word = dirtyBits[wi];
                        while (word != 0)
                        {
                            var bit = BitOperations.TrailingZeroCount((ulong)word);
                            word &= word - 1; // clear lowest set bit
                            var chunkId = wi * 64 + bit;

                            var src = accessor.GetChunkAsReadOnlySpan(chunkId);
                            var entityPk = MemoryMarshal.Read<long>(src);

                            // A chunk with no PK in its overhead is not a published entity. Routing ids start at 1, so
                            // EntityId.FromRaw(0).ArchetypeId indexes a null slot and the GetSlot below NREs — which, escaping the fence, freezes the
                            // runtime's tick counter and leaks that tick's UnitOfWork, silently in Release (#837). DIRTY-01 removed the one producer that
                            // could put such a chunk here (a spawn-staging id), but the deref itself was never guarded, so a future producer would
                            // reproduce #837 verbatim. Skip it, exactly as the cluster walker skips an unoccupied slot in AppendClusterCollectionContent.
                            if (entityPk == 0)
                            {
                                continue;
                            }

                            // Flush before the frame would exceed the per-Append cap. Fence records are individually committed, so
                            // splitting across Appends is safe; the codec splits each batch into RecordBatch chunks internally.
                            if (batchBytes > 0 && batchBytes + recOverhead + stride > MaxFenceBatchBytes)
                            {
                                highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                                walPublished = true;
                                fenceArena.Reset();
                                batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, true);
                                batchBytes = 0;
                            }

                            // Wire identity is the per-archetype slot (LOG-06); resolve from this entity's archetype (routing id in the PK).
                            var slot = (ushort)GetMetaByRouting(EntityId.FromRaw(entityPk).ArchetypeId).GetSlot(componentTypeId);
                            var value = src.Slice(overhead, stride);
                            batch.AddSlot(entityPk, slot, value, table.CollectionHandleRanges);
                            batchBytes += recOverhead + stride;
                            if (table.HasCollections)
                            {
                                // Mandatory, not opportunistic: the Slot payload's handle is zeroed (LOG-06), so applying it without a fold to follow would
                                // empty a collection whose content was previously safe on the checkpoint timeline. See CollectionContentEmitter.
                                batchBytes += CollectionContentEmitter.Emit(ref batch, table, entityPk, slot, value);
                            }
                        }
                    }

                    if (!batch.IsEmpty)
                    {
                        highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                        walPublished = true;
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            // The deferred shadow pass that used to run here maintained the per-ComponentTable indexes for non-Versioned components. Those indexes are gone
            // (#629) — a cluster-backed archetype maintains its own trees through the cluster shadow bitmap — and instrumenting this branch across the full
            // suite showed it was never entered. `hasShadow` is still reported on the tick-fence span below.

            // The per-entity spatial pass that used to run here rebuilt the entity-level R-Tree from this table's dirty bitmap. That tree is gone (#872 step
            // 13) — cluster AABBs are refreshed by the fence's own AabbRefresh phase, from cluster storage, in parallel — so there is nothing table-scoped
            // left to do. `hasSpatial` now gates NOTHING: the ring archive below is unconditional, and the flag's only remaining reader is the trace byte on
            // the tick-fence span, where it still answers "is this table spatial at all".

            // Archive dirty bitmap into ring buffer for interest management delta queries
            table.SpatialIndex?.InterestSystem?.DirtyRing.Archive(tickNumber, dirtyBits, dirtyBits.Length);

            tableScope.WalPublished = walPublished ? (byte)1 : (byte)0;
            tableScope.HasShadow = hasShadow ? (byte)1 : (byte)0;
            tableScope.HasSpatial = hasSpatial ? (byte)1 : (byte)0;
        }
        finally
        {
            tableScope.Dispose();
        }

        return highestLSN;
    }

    /// <summary>
    /// Serializes dirty cluster entity data to WAL for all cluster-eligible archetypes.
    /// Called from <see cref="WriteTickFence"/> after per-ComponentTable processing.
    /// </summary>
    /// <summary>Create a fresh CBS&lt;TransientStore&gt; for cluster Transient component storage.</summary>
    /// <summary>
    /// Give a cluster state the means to build its shared per-cell R-Tree segment on first promotion (#872 step 9), and apply the configured thresholds.
    /// </summary>
    /// <remarks>
    /// <para>A factory rather than an eager segment: promotion is opt-in and, at the measured crossover, unreachable for most workloads — so the common case
    /// must cost nothing. A <see cref="ChunkBasedSegment{TStore}"/> is at least two pages, and paying that for every spatial archetype in every database to
    /// serve a case none of them reach is the kind of default that only shows up as a memory graph nobody can explain.</para>
    /// <para>Attached here rather than inside <see cref="ArchetypeClusterState"/> because that type is built by static factory methods holding no services,
    /// and giving it a <see cref="DatabaseEngine"/> reference to reach the allocator would couple the storage state to the engine for one lazy allocation.</para>
    /// </remarks>
    /// <summary>
    /// Refuse to start the parallel fence while per-cell R-Tree promotion is enabled (#872 step 9).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a guard and not just a default.</b> Promotion is off by default, which is what makes the combination unreachable today — but "unreachable
    /// because of a default value" is a property of the configuration, not of the code, and the next person to raise the threshold gets a silent data race
    /// rather than a message. This turns it into a refusal at startup, where it is cheap to read and impossible to miss.</para>
    /// <para><b>What is actually unsafe.</b> The <c>AabbRefresh</c> phase slices on CLUSTER ID (<c>FenceWorkPlan.EmitAabbRefreshSliceItems</c>), and cluster
    /// ids are allocated by the segment with no relation to cells — so one promoted cell's clusters land in different slices and two workers mutate one tree.
    /// <see cref="SpatialRTree{TStore}"/> is single-writer by specification (ADR-044; <c>03-tree-operations.md</c> invariant O2, "at most one writer holds the
    /// lock bit on any node at any time"), and its root-split path writes <c>_rootChunkId</c> and <c>_depth</c> as plain unsynchronised fields. Concurrent
    /// splits orphan a subtree; the clusters in it stop being returned by any query, with nothing raised.</para>
    /// <para><b>Migrate is NOT affected</b> and needs no guard: its slices are carved on <c>DestCellKey</c> boundaries so no two workers share a dest cell,
    /// which is the same per-cell exclusivity this phase lacks.</para>
    /// </remarks>
    internal void AssertCellTreePromotionIsSafeForParallelFence()
    {
        if (AllowCellTreePromotionWithParallelFence)
        {
            return;
        }

        // The per-STATE copy is what governs behaviour — AttachCellTreeFactory snapshots the engine property at InitializeArchetypes, so checking only the
        // property would miss an archetype initialised while it held a different value, and would also miss nothing at all if the property were lowered after
        // the states were built. Check both: the property catches a threshold set before any archetype exists, the states catch everything after.
        int offending = ClusterCellTreePromoteThreshold;
        if (_archetypeStates != null)
        {
            for (int i = 0; i < _archetypeStates.Length; i++)
            {
                var cs = _archetypeStates[i]?.ClusterState;
                if (cs != null && cs.CellTreePromoteThreshold != int.MaxValue)
                {
                    offending = cs.CellTreePromoteThreshold;
                    break;
                }
            }
        }

        if (offending == int.MaxValue)
        {
            return;
        }

        ThrowHelper.ThrowInvalidOp(
            $"A cell-tree promotion threshold of {offending} is active, but RuntimeOptions.EnableParallelFence is true. The AabbRefresh "
            + "phase now defers promoted cells to the serial tail, but the MIGRATE path still resizes ClusterSpatialIndexSlot and grows the shared cluster "
            + "segment from workers, and both are per-ARCHETYPE so cell-disjoint slicing does not protect them (MD-02). Run the serial fence, or leave "
            + "promotion at int.MaxValue until those land (#872 step 9).");
    }

    private ArchetypeClusterState AttachCellTreeFactory(ArchetypeClusterState state)
    {
        state.CellTreeSegmentFactory = stride =>
        {
            CreateTransientClusterSegment(stride, out var treeStore, out var treeSegment);
            return (treeSegment, treeStore.Value);
        };
        state.CellTreePromoteThreshold = ClusterCellTreePromoteThreshold;
        state.CellTreeDemoteThreshold = ClusterCellTreePromoteThreshold == int.MaxValue ? int.MaxValue : ClusterCellTreePromoteThreshold / 2;
        return state;
    }

    /// <summary>
    /// Clusters in one cell at which that cell's linear broadphase is replaced by an R-Tree. <see cref="int.MaxValue"/> — the default — never promotes.
    /// </summary>
    /// <remarks>
    /// Left off by default on measured grounds: below ~512 clusters the linear scan wins a selective query (6x at 80, which is AntHill's densest zone) and the
    /// tree's update path is 22-38x dearer per moved cluster. See <c>ArchetypeClusterState.CellTreePromoteThreshold</c> for the full argument and
    /// <c>BroadphaseCrossoverSweepTests</c> for the numbers.
    /// </remarks>
    internal int ClusterCellTreePromoteThreshold { get; set; } = int.MaxValue;

    /// <summary>
    /// Bypass <see cref="AssertCellTreePromotionIsSafeForParallelFence"/>. <b>Tests only.</b>
    /// </summary>
    /// <remarks>
    /// <para>The AabbRefresh half of the hazard IS fixed — promoted cells are diverted out of the parallel pass and applied in the serial tail
    /// (<c>DrainPromotedAabbApplies</c>). What is NOT fixed is the Migrate path's two PER-ARCHETYPE resources, which cell-disjoint slicing does not protect:
    /// <c>EnsureClusterSpatialIndexSlotCapacity</c>'s <c>Array.Resize</c> plus <c>RebindCellTreeBackPointers</c>, and the shared
    /// <c>ChunkBasedSegment.Grow</c> invalidating a sibling worker's chunk pointer. Those are <c>MD-02</c> and are tracked separately.</para>
    /// <para>So this flag exists for the tests that must drive the parallel fence to prove the divert works, and for nothing else. It is not a supported
    /// configuration, and it stays a bypass rather than becoming a default until the Migrate-path items land.</para>
    /// </remarks>
    internal bool AllowCellTreePromotionWithParallelFence { get; set; }

    private void CreateTransientClusterSegment(int stride, out TransientStore? store, out ChunkBasedSegment<TransientStore> segment)
    {
        store = new TransientStore(TransientOptions, MemoryAllocator, EpochManager, this);
        var tsValue = store.Value;
        // Allocate the initial pages on `tsValue` BEFORE constructing the segment. TransientStore is a struct, so the segment's base LogicalSegment copies it
        // by value in its ctor — if we allocated after construction, the segment's copy would keep _pageCount=0 and the first Grow would re-allocate duplicate
        // page indices (0,1,2,3 again), corrupting the forward chain. Allocating first means base(tsValue) captures _pageCount=4. (See ComponentTable.CreateTransientSegments.)
        Span<int> tsPages = stackalloc int[4];
        tsValue.AllocatePages(ref tsPages, 0, null);
        segment = new ChunkBasedSegment<TransientStore>(EpochManager, tsValue, stride);
        segment.Create(PageBlockType.None, StorageSegmentKind.Cluster, tsPages, false);
    }

    /// <summary>
    /// After reopening a mixed archetype with Transient components, allocate matching chunks in the fresh
    /// TransientSegment so chunk IDs stay synchronized with the persisted PersistentStore segment.
    /// </summary>
    /// <remarks>
    /// <para>Relies on the TransientSegment being freshly created (no prior allocations/frees), which guarantees
    /// sequential chunk ID assignment (1, 2, 3, ...). This is always true because TransientStore data doesn't
    /// survive restart — the segment is created fresh in every reopen path.</para>
    /// </remarks>
    private static void SyncTransientSegmentToActive(ArchetypeClusterState clusterState)
    {
        if (clusterState.TransientSegment == null)
        {
            return;
        }

        // Find max chunk ID among active clusters
        var maxChunkId = 0;
        for (var i = 0; i < clusterState.ActiveClusterCount; i++)
        {
            if (clusterState.ActiveClusterIds[i] > maxChunkId)
            {
                maxChunkId = clusterState.ActiveClusterIds[i];
            }
        }

        // Allocate chunks in TransientStore sequentially up to maxChunkId so IDs match.
        // TransientStore is always fresh — sequential allocation produces IDs 1..maxChunkId.
        for (var id = 1; id <= maxChunkId; id++)
        {
            var allocatedId = clusterState.TransientSegment.AllocateChunk(true);
            Debug.Assert(allocatedId == id, $"TransientSegment sync: expected chunk ID {id}, got {allocatedId}");
        }
    }

    private void WriteClusterTickFence(long tickNumber, ref long highestLSN, ChangeSet changeSet)
    {
        // Issue #233: drain all deferred wake requests collected during parallel system execution. Must run once BEFORE the per-archetype loop so each
        // archetype's DormancySweep (below) sees up-to-date WakePending states and skips those clusters instead of re-sleeping them. The fence parallel
        // path runs this drain in FencePrep (TickDriver) so per-archetype work can be split across workers without coordinating on this global state.
        DormancyReporter.DrainAll(_archetypeStates);

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var contributed = ProcessArchetypeFence(meta, tickNumber, changeSet);
            if (contributed > highestLSN)
            {
                highestLSN = contributed;
            }
        }
    }

    /// <summary>
    /// Serial entry point for one archetype's tick-fence work. Runs Prepare → ExecuteMigrations (no slicing) → Finalize in sequence on the calling thread.
    /// Used by the legacy/opt-out path (<c>EnableParallelFence = false</c>) where the whole fence runs single-threaded. The parallel path calls
    /// <see cref="PrepareArchetypeFence"/>, <see cref="ExecuteMigrationsSlice"/>, and <see cref="FinalizeArchetypeFence"/> directly through their phase-scoped
    /// internal systems.
    /// </summary>
    internal long ProcessArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (!PrepareArchetypeFence(meta, tickNumber, changeSet))
        {
            return 0;
        }
        var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
        if (clusterState.PendingMigrationCount > 0)
        {
            clusterState.IndexUpdates?.BeginTick(1);
            clusterState.EntityMapUpdates?.BeginTick(1);

            // The serial path has no FenceContext to carry RuntimeOptions, so it applies the same rule against the SAME CONSTANT the option defaults to —
            // not a copy of the value. Without this decision the flag keeps its `false` initial value, every serial migration takes the inline path, and
            // ApplyStagedEntityMapUpdates below becomes dead code no test would reach. The gain is real even at one worker: 402 vs 691 ns/migrant at ~1.8
            // entries per bucket. A runtime-less host cannot override the threshold; that is a documented limitation, not a drifted duplicate.
            var emBuckets = _archetypeStates[meta.ArchetypeId].EntityMap?.LiveBucketCount ?? 0;
            clusterState.UseBulkEntityMapUpdate = emBuckets <= 0
                || clusterState.PendingMigrationCount >= (long)(EntityMapUpdateStaging.DefaultMinEntriesPerBucket * emBuckets);
            ExecuteMigrationsSlice(meta, 0, clusterState.PendingMigrationCount, changeSet);

            // The serial path has no IndexMassUpdate phase to drain the staging, so it drains it here, on one part. Leaving this out is not a slow path but a
            // WRONG one: migration now STAGES its index value updates instead of applying them, so without a drain every migrated entity's index entry keeps
            // pointing at the cluster location it just left, and a query answers with a stale one.
            ApplyStagedIndexUpdates(clusterState, changeSet);
            ApplyStagedEntityMapUpdates(meta, clusterState, changeSet);
        }
        // AABB recompute: mirrors the parallel AabbRefresh phase. The wrapper handles bookkeeping clear at its tail —
        // FinalizeArchetypeFence's redundant ClearAabbRefreshBookkeeping then iterates an already-empty bitmap (cheap).
        if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic && clusterState.FenceBranchPath != 0)
        {
            RecomputeArchetypeAabbs(meta);
        }
        return FinalizeArchetypeFence(meta, tickNumber, changeSet);
    }

    /// <summary>
    /// Applies one archetype's staged EntityMap location patches, single-threaded, then folds visibility and rolls back any orphan (#872 step 7).
    /// </summary>
    /// <remarks>
    /// The serial fence's counterpart to <c>FenceEntityMapUpdateExecSystem</c>, and it is not an optimisation but a correctness requirement: migration now
    /// STAGES its location patches instead of applying them, so without this drain every migrated entity's EntityMap record keeps pointing at the cluster slot
    /// it just left — and once a later spawn reclaims that slot, the stale record resolves to an unrelated entity's bytes.
    /// <para>
    /// One part, no bucket partition: with a single worker there is nothing to keep apart, and the partition exists only so concurrent workers never share a
    /// bucket chunk. The batch still has to be SORTED, because that is what the run amortisation reads.
    /// </para>
    /// </remarks>
    internal void ApplyStagedEntityMapUpdates(ArchetypeMetadata meta, ArchetypeClusterState clusterState, ChangeSet changeSet)
    {
        var staging = clusterState?.EntityMapUpdates;
        if (staging == null)
        {
            return;
        }

        // #872 step 11: this phase's cost belongs to the migration that staged the work, and until now nothing measured it. The sort and the merge are
        // inside the bracket deliberately — at small batches they are the majority of the phase, and a cost model that skipped them would under-admit
        // exactly where the bulk path stops paying for itself.
        var applyStart = Stopwatch.GetTimestamp();
        try
        {
            staging.ClearPrepared();
            staging.SortChunk(0);
            var count = staging.MergeAndPartition(1);
            if (count == 0)
            {
                return;
            }

            var state = _archetypeStates[meta.ArchetypeId];
            var slice = staging.Prepared.AsSpan(0, count);
            var accessor = state.EntityMap.Segment.CreateChunkAccessor(changeSet);
            try
            {
                state.EntityMap.UpdateValuesBulk<EntityLocationUpdate, ClusterLocationBulkUpdater>(slice, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }

            for (var i = 0; i < slice.Length; i++)
            {
                ref var entry = ref slice[i];
                if (!entry.Found)
                {
                    clusterState.RollbackOrphanedDestinationSlot(entry.DstChunkId, entry.DstSlot, entry.EntityKey, changeSet);
                    continue;
                }

                clusterState.NoteClusterBorn(entry.DstChunkId, entry.ObservedBornTsn);
                if (entry.ObservedDiedTsn != 0)
                {
                    clusterState.NoteClusterDied(entry.DstChunkId, ArchetypeClusterState.VisibilityUnknown);
                }
            }
        }
        finally
        {
            Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - applyStart);
        }
    }

    /// <summary>
    /// Applies every indexed field's staged value updates for one archetype, single-threaded, in one partitioning descent per field (#872 step 6).
    /// </summary>
    /// <remarks>
    /// The serial fence's counterpart to <c>FenceIndexMassUpdateExecSystem</c>. It takes the same sort but skips the leaf-snapped partition entirely: with
    /// one worker there is nothing to keep apart, and the snap exists only so that concurrent workers never share a leaf.
    /// </remarks>
    internal void ApplyStagedIndexUpdates(ArchetypeClusterState clusterState, ChangeSet changeSet)
    {
        var staging = clusterState?.IndexUpdates;
        if (staging == null || staging.FieldCount == 0 || clusterState.IndexSlots == null)
        {
            return;
        }

        // #872 step 11: the largest single component of a migration's cost — measured at ~48 % — and until now entirely outside every timer.
        var applyStart = Stopwatch.GetTimestamp();
        try
        {
            for (var fieldId = 0; fieldId < staging.FieldCount; fieldId++)
            {
                if (staging.StagedBytes(fieldId) == 0)
                {
                    continue;
                }

                var fieldRef = staging.Field(fieldId);
                ref var field = ref clusterState.IndexSlots[fieldRef.SlotIndex].Fields[fieldRef.FieldIndex];
                var multi = field.AllowMultiple;

                // The parallel path's Migrate workers sort their own chunk before leaving it; this path has no workers, so it sorts its single run here. The
                // merge below then sees one sorted run and copies it, which is the degenerate case it already handles.
                field.Index.SortBulkEntries(staging.ChunkSpan(0, fieldId), multi);

                var merged = staging.MergeSortedRuns(fieldId, field.Index.BulkEntryStride(multi), field.Index, multi, out var byteCount);
                if (byteCount == 0)
                {
                    continue;
                }

                var accessor = field.Index.Segment.CreateChunkAccessor(changeSet);
                try
                {
                    field.Index.ApplyBulkEntries(merged.AsSpan(0, byteCount), multi, ref accessor, out _);
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            staging.BeginTick(1);   // consume: the entries have been applied and must not be applied again next tick
        }
        finally
        {
            Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - applyStart);
        }
    }

    /// <summary>
    /// Serial-path AABB recompute entry: opens a chunk accessor and runs the whole-archetype <see cref="ArchetypeClusterState.RecomputeDirtyClusterAabbs"/>
    /// (which delegates to a single full-range slice and clears bookkeeping at the tail). Used by <see cref="ProcessArchetypeFence"/>.
    /// </summary>
    internal void RecomputeArchetypeAabbs(ArchetypeMetadata meta)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return;
        }

        var clusterState = _archetypeStates[meta.ArchetypeId]?.ClusterState;
        if (clusterState == null || clusterState.ClusterSegment == null)
        {
            return;
        }

        var spatialScope = TyphonEvent.BeginWriteTickFenceClusterSpatial(meta.ArchetypeId, clusterState.FenceDirtyClusterCount);
        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            clusterState.RecomputeDirtyClusterAabbs(clusterState.FenceDirtyBits, ref accessor, _spatialGrid);
            spatialScope.MigrationsExecuted = clusterState.LastTickMigrationCount;
        }
        finally
        {
            accessor.Dispose();
            spatialScope.Dispose();
        }
    }

    /// <summary>
    /// Parallel-path AABB recompute entry: applies a contiguous slice of the archetype's AABB recompute. Safe to call concurrently across DISJOINT slices of
    /// the same archetype. Bookkeeping clear happens once per archetype in <see cref="FinalizeArchetypeFence"/> after the phase barrier.
    /// </summary>
    internal void RecomputeArchetypeAabbsSlice(ArchetypeMetadata meta, int sliceStart, int sliceCount)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return;
        }

        var clusterState = _archetypeStates[meta.ArchetypeId]?.ClusterState;
        if (clusterState == null || clusterState.ClusterSegment == null)
        {
            return;
        }

        if (clusterState.FenceBranchPath == 0)
        {
            return;
        }

        // ClusterScanned = clusters actually considered by this slice. In legacy mode it equals sliceCount (index range count). In barrier mode it's the
        // popcount across the slice's bitmap words — computed inside the slice helper.
        var clustersInSlice = clusterState.CountClustersInAabbSlice(sliceStart, sliceCount);
        var refreshSpan = TyphonEvent.BeginSpatialClusterAabbRefresh(meta.ArchetypeId, clustersInSlice);
        // CreateChunkAccessor is a struct ctor (4 field assigns) and EpochGuard is already entered at chunk level in FencePhaseExecSystemBase.Execute —
        // per-slice accessor cost is sub-microsecond. Not worth caching.
        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        // Worker-local outlier buffer (review D-2): RecomputeDirtyClusterAabbsSlice appends here per-entity without locking; we bulk-enqueue under
        // _finalizeLock once after the slice finishes. List is short-lived per slice (no pooling — outlier fires are rare; allocations are bounded by the
        // AABB-Refresh chunk count per tick).
        var outlierBuffer = new List<MigrationRequest>(0);
        // Worker-local deferral buffer for promoted cells, same shape and lifetime as the outlier buffer above and merged the same way. Allocated only when
        // this archetype actually has a promoted cell — the overwhelmingly common case is none, and an empty List per slice per tick is not free.
        var promotedBuffer = clusterState.PromotedCellCount > 0 ? new List<ArchetypeClusterState.PromotedAabbApply>(0) : null;
        // Worker-local repair nominations (#872 step 12), merged the same way as the outlier buffer above — but held PER WORKER rather than allocated per
        // slice. With ReclusterBudgetMs at its default of 1.0 this path is live out of the box, so a fresh List per slice per tick is a real per-tick
        // allocation on the fence; step 11 also doubled the element width, so each growth doubling costs twice what it did. EnqueueRepairNominationsBulk
        // clears it after the merge, so a reused list starts every slice empty. Still gated on the budget: with repair switched off the planner discards
        // nominations unread, and producing them would be pure cost on the detection path.
        var repairBuffer = _spatialGrid != null && _spatialGrid.Config.ReclusterBudgetMs > 0f
            ? ArchetypeClusterState.NominationScratch ??= [] : null;
        try
        {
            clusterState.RecomputeDirtyClusterAabbsSlice(sliceStart, sliceCount, ref accessor, _spatialGrid, promotedBuffer, outlierBuffer, repairBuffer,
                out var aabbsChanged, out var slotsScanned, out var outlierGuardFires, out var clustersScanned, out var driftersDetected,
                out var driftAbsorbed, out var driftersUnplaced);
            // Interlocked, not plain adds: every AabbRefresh slice of this archetype reaches here, and the three counters are archetype-wide. They are reset
            // once per tick in PrepareArchetypeFence, so a lost add would under-report for that tick only — which is exactly the kind of quiet inaccuracy that
            // makes a measurement useless for tuning P4.
            Interlocked.Add(ref clusterState.LastTickClustersScanned, clustersScanned);
            Interlocked.Add(ref clusterState.LastTickDriftersDetected, driftersDetected);
            Interlocked.Add(ref clusterState.LastTickDriftAbsorbedCount, driftAbsorbed);
            Interlocked.Add(ref clusterState.LastTickDriftersUnplaced, driftersUnplaced);
            clusterState.EnqueueMigrationsBulk(outlierBuffer);
            clusterState.EnqueuePromotedAppliesBulk(promotedBuffer);
            clusterState.EnqueueRepairNominationsBulk(repairBuffer);
            refreshSpan.AabbsChanged = aabbsChanged;
            refreshSpan.SlotsScanned = slotsScanned;
            refreshSpan.OutlierGuardFires = outlierGuardFires;
        }
        finally
        {
            accessor.Dispose();
            refreshSpan.Dispose();
        }
    }

    /// <summary>
    /// Phase 1 of the parallel cluster tick fence: per-archetype prep work that must complete BEFORE any migration apply.
    /// Returns <c>true</c> if subsequent phases (Migrate/Finalize) have work to do for this archetype.
    /// </summary>
    /// <remarks>
    /// <para>Order-tight pipeline:</para>
    /// <list type="number">
    ///   <item>Pure-transient short-circuit: snapshot dirty bitmap (if any), propagate per-table flags, dormancy sweep. Returns false.</item>
    ///   <item>Clean-bitmap path: dormancy sweep with empty bitmap, then on spatial-Dynamic archetypes build local occupancy-only spatialBits and run
    ///         DetectClusterMigrations. Stores branch path = 1 on the cluster state if any migrations queued or spatial refresh needed.</item>
    ///   <item>Dirty-bitmap path: snapshot bitmap, occupancy-mask, ProcessClusterShadowEntries, RecomputeClusterZoneMaps, DetectClusterMigrations.
    ///         Stores branch path = 2 + the snapshot in <see cref="ArchetypeClusterState.FenceDirtyBits"/>.</item>
    /// </list>
    /// <para>Safe to call concurrently across DISTINCT archetypes — touches only this archetype's own cluster state plus the per-archetype B+Tree (OLC-safe)
    /// plus per-cluster shadow buffers (per-cluster). Cell-descriptor mutations are deferred to ExecuteMigrationsSlice (Phase 2) and Finalize (Phase 3);
    /// Prep itself does not bump cell counters.</para>
    /// </remarks>
    internal unsafe bool PrepareArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        var hasWork = PrepareArchetypeFenceCore(meta, tickNumber, changeSet);

        // ── The drain prefix, recorded on the way out and NOWHERE ELSE ────────────────────────────────────────────────
        //
        // Whatever is queued when Prep finishes is exactly what this tick's Migrate phase will execute: the parallel work
        // plan is built from PendingMigrationCount after Prep returns, and the serial path passes that same count straight
        // to ExecuteMigrationsSlice. Requests filed LATER — by FlagOutliersForMigration and by step 10's drift detection,
        // both of which run inside AabbRefresh, after Migrate — land beyond the prefix and must survive to the next tick.
        //
        // 🔴 This lives in a wrapper rather than at the bottom of the body because the body has THREE exits, and taking the
        // one that skips this is not an edge case. An archetype written through the spatial barrier sets ClusterProcessBitmap
        // and leaves ClusterDirtyBitmap clean, so it leaves through the clean-bitmap `return true` on every ordinary tick.
        // With the snapshot inside the body, that archetype recorded a drain prefix of zero while Migrate executed the whole
        // queue, Finalize then compacted nothing, and the queue grew by its drifter count every tick — with the entire
        // backlog re-executing each time. Measured before the fix: 16 000 entities produced 17 234 migrations on the first
        // tick and 224 854 on the twentieth, against ~10 900 genuine drifters per tick.
        //
        // Worth being blunt about, because it is strictly worse than the bug it replaced. The old code reset the count in
        // Finalize, which discarded the AabbRefresh producers' work silently but at least kept the queue bounded; a prefix
        // that is wrong in the other direction re-executes stale requests against slots their entities have already left.
        // Guarded exactly as the core's own preamble guards, and for the same reasons: WriteClusterTickFence walks every
        // archetype from GetAllArchetypes() with no filter, so `meta` may be null or carry an id past the state array — the
        // two conditions the core returns false for. Reading the array before re-testing them made the wrapper throw on the
        // inputs its own body was written to reject.
        if (meta == null || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return hasWork;
        }

        var pending = _archetypeStates[meta.ArchetypeId]?.ClusterState;
        if (pending != null)
        {
            // ── #872 step 12: plan the repair BEFORE the prefix is taken ────────────────────────────────────────────────
            //
            // The requests a repair emits are meant for THIS tick's Migrate phase, not the next one, and the prefix below is
            // what decides that. The whole reason planning lives in Prep rather than beside the nomination that feeds it is
            // that the fresh destination clusters it allocates must be created and filled inside one exclusive window — with
            // a tick in between, an ordinary spawn's first-fit scan would claim into them, and the sorted packing the planner
            // just computed would be handed to the placement policy this issue exists to repair.
            //
            // Gated on hasWork, and the nominations are DISCARDED rather than carried when it is false. An archetype whose
            // Prep found nothing has FenceBranchPath == 0, so neither Migrate nor Finalize runs for it this tick: requests
            // filed here would sit unexecuted and the clusters allocated for them would have no sweep to free them. Dropping
            // the nomination costs one deferred repair — the cluster re-nominates the next time it is written.
            //
            // 🔴 KNOWN GAP, still open after step 11, and narrowed rather than closed. In barrier-only mode the AABB pass visits
            // only clusters WRITTEN this tick, so a cell that degrades and then goes completely still is never re-NOMINATED.
            // Step 11's queue fixes the half that was in reach — a nomination the budget refuses, or one that arrives on a tick
            // that cannot plan, is now REMEMBERED rather than discarded, so a cell nominated once while it was moving is still
            // repaired after it stops. What remains needs `hasWork |= queue.Count > 0`, which re-arms Migrate and Finalize for
            // an otherwise idle archetype and collides head-on with AC-10.8 ("a tick with no movement does no relocation work
            // and allocates nothing"). That trade is bigger than this step and is deliberately not taken here.
            //
            // ── The throttle runs BEFORE the planner, and the ordering is the policy (§5.6) ──────────────────────────────
            //
            // One budget, spent in priority order: cell crossings are correctness and take what they need, intra-cell
            // relocations take what is left, and repair gets the remainder. So the relocation throttle both consumes and
            // reports the budget the planner may then spend. Reversing the two would let a rare repair outbid the steady-state
            // path that keeps cells from needing repair in the first place.
            if (hasWork)
            {
                var tailStart = Stopwatch.GetTimestamp();
                var remainingBudgetNs = pending.ApplyMigrationThrottle(_spatialGrid);
                var throttleEnd = Stopwatch.GetTimestamp();
                pending.PrepThrottleTicks += throttleEnd - tailStart;

                PlanArchetypeRepairs(pending, changeSet, tickNumber, remainingBudgetNs);
                pending.PrepPlanTicks += Stopwatch.GetTimestamp() - throttleEnd;
            }
            else
            {
                // The LIST is per-tick even though the QUEUE is not: it describes the tick that produced it. Absorbed into the
                // persistent queue first — that is exactly the "refused or unplannable nomination is no longer lost" half above
                // — and only then cleared.
                pending.AbsorbRepairNominations(_spatialGrid, tickNumber);
            }

            pending.PendingMigrationDrainCount = hasWork ? pending.PendingMigrationCount : 0;

            // Every producer has now filed: crossings and the outlier guard in the core above, relocations carried from last tick's AabbRefresh, and repair
            // units from the planner. CR-05 is checkable exactly here and nowhere earlier (#877).
            pending.AssertNoDuplicateMigrationSources(tickNumber);
        }

        return hasWork;
    }

    /// <summary>
    /// Run the repair planner for one archetype, renting the accessor it needs (#872 step 12). Separated from the caller only so the accessor's
    /// <c>try/finally</c> does not sit in the middle of the drain-prefix reasoning above.
    /// </summary>
    /// <remarks>
    /// <para>The accessor carries <paramref name="changeSet"/> because the planner allocates clusters and publishes their occupancy word, which is a write
    /// and owes the WAL the same atomicity as every other fence write.</para>
    /// <para><b>Skipped when there is nothing to absorb AND nothing waiting</b> — not merely when nothing was nominated. The queue is persistent since step
    /// 11, so a world that has stopped moving can still hold a backlog worth planning, and the early-out has to ask about both. The cost of that is stated
    /// rather than hidden: an archetype whose queue the budget can never drain rents an accessor and ranks every tick for as long as the backlog lasts.
    /// <c>PlanCellRepairs</c> bounds the per-tick work by stopping its scan once the budget cannot afford another unit; what it cannot avoid is the rent
    /// and the rank themselves, and <c>RepairQueueMaintenanceMs</c> is what makes that visible.</para>
    /// </remarks>
    private void PlanArchetypeRepairs(ArchetypeClusterState clusterState, ChangeSet changeSet, long tickNumber, double remainingBudgetNs)
    {
        // 🔴 A pure-Transient archetype absorbs and DISCARDS rather than returning with the list intact. Returning here left RepairNominations untouched on
        // the hasWork path while the !hasWork path both absorbed and cleared, so the list would grow across ticks. It is unreachable today only because a
        // null cluster segment implies FenceBranchPath == 0 and therefore no AabbRefresh producer — an accidental guarantee, not a designed one.
        if (clusterState.ClusterSegment == null)
        {
            clusterState.AbsorbRepairNominations(_spatialGrid, tickNumber);
            return;
        }

        // The queue can hold candidates when nothing was nominated this tick — that is the whole point of it being persistent — so the early-out is on
        // "nothing to absorb AND nothing waiting", not on the nomination list alone.
        if (clusterState.RepairNominations.Count == 0 && (clusterState.RepairQueue == null || clusterState.RepairQueue.Count == 0))
        {
            return;
        }

        var accessor = clusterState.ClusterSegment.CreateChunkAccessor(changeSet);
        var plannerStart = Stopwatch.GetTimestamp();
        try
        {
            var planned = clusterState.PlanCellRepairs(_spatialGrid, ref accessor, tickNumber, remainingBudgetNs, out var budgetUsedMs);
            clusterState.LastTickReclusterBudgetUsedMs = budgetUsedMs;

            // Timed here rather than inside the planner so the bracket covers the accessor rent too, and fed back on the NEXT tick — the planner's own
            // cost per entity is a term step 12's projection ignored entirely, and it is repair's alone: no crossing and no relocation pays it.
            clusterState.RecordPlannerCost(Stopwatch.GetTimestamp() - plannerStart, planned);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <inheritdoc cref="PrepareArchetypeFence"/>
    private unsafe bool PrepareArchetypeFenceCore(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return false;
        }

        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null)
        {
            return false;
        }

        // Reset fence-tick intermediate state at the top of every Prep so a stale snapshot from a previous tick never leaks into the current tick's
        // Migrate / Finalize phases. The Migrate slices (Phase 2) Interlocked.Add into LastTickMigrationCount / LastTickMigrationExecuteMs — start at zero here.
        clusterState.FenceBranchPath = 0;
        clusterState.FenceDirtyBits = null;

        // The pending-migration queue is deliberately NOT cleared here, nor in Finalize where it used to be. See
        // ArchetypeClusterState.PendingMigrationDrainCount: the queue has producers on both sides of its consumer, so a per-tick reset destroyed
        // everything filed by the AabbRefresh-side detectors. Finalize now compacts away exactly the prefix that was executed.
        clusterState.FenceEntryCount = 0;
        clusterState.FenceDirtyClusterCount = 0;
        clusterState.FenceProcessBitmapClusterCount = -1; // recomputed in Prep when in BarrierOnly mode
        // Snapshot before zeroing: DetectClusterMigrations pre-sizes PendingMigrations from last tick's migration count, and reading the field itself would read
        // the zero written on the line below — same fence, a few hundred lines earlier — so the estimate was always Max(16, 0) and the queue regrew from 16 on
        // every migration-heavy tick (#872).
        // #872 step 11: BEFORE the counters below are zeroed, because it reads them. One sample per tick, folded into the per-entity cost model the repair
        // budget is spent against — which is what makes RepairNsPerEntity a seed rather than the operative constant.
        if (_spatialGrid != null)
        {
            clusterState.ObserveMigrationCost(in _spatialGrid.Config);
        }

        clusterState.ResetThrottleTickState();
        clusterState.ResetPrepSubSpans();
        clusterState.PreviousTickMigrationCount = clusterState.LastTickMigrationCount;
        clusterState.LastTickMigrationCount = 0;
        clusterState.LastTickMigrationExecuteMs = 0d;
        // #872 step 11: the apply phases accumulate into this from their own workers, exactly as the Migrate slices do into the line above — and it is
        // zeroed the same PLAIN way, deliberately. Both counters are published by workers (an Interlocked.Add here, a compare-exchange loop there) and both
        // are reset from Prep, and what orders the reset against those publications is the fence phase barrier, not a release on the store. Giving one of
        // the pair a Volatile.Write and not the other would imply a distinction between them that does not exist.
        clusterState.LastTickMigrationApplyTicks = 0L;
        clusterState.LastTickClustersScanned = 0;
        clusterState.LastTickDriftersDetected = 0;
        clusterState.LastTickDriftAbsorbedCount = 0;

        // LastTickHysteresisAbsorbedCount was NOT reset here until #872, and DetectClusterMigrations only ever ASSIGNED it (=, not +=). A tick in which
        // detection did not run therefore reported the PREVIOUS tick's absorbed count as if it were this tick's — a stale reading indistinguishable from a live
        // one, in the one counter that tunes MigrationHysteresisRatio. Detection is reached on a quiet tick only through the clean-bitmap branch below, which is
        // gated on ActiveClusterCount > 0, so an archetype that empties out stopped updating it entirely.
        //
        // Drain rather than plain-zero: on the SpatialBarrierOnly path the count is accumulated live by ClusterRef.MaybeFlagMigration as writes happen, because
        // that path never reaches the scan that would otherwise count it. Both producers now compose — this drains the live one, and DetectClusterMigrations
        // adds its scan's tally with += rather than clobbering. Exactly one of the two is ever non-zero for a given archetype.
        var absorbedLive = clusterState.HysteresisAbsorbedLive;
        clusterState.HysteresisAbsorbedLive = 0;
        clusterState.LastTickHysteresisAbsorbedCount = absorbedLive;
        clusterState.TotalHysteresisAbsorbedCount += absorbedLive;

        // ReclusterBudgetUsedMs is produced by the step-12 repair planner, which runs in the Prep WRAPPER — after this body returns — so this reset always
        // precedes the write and never clobbers it. It reports the PROJECTED spend of the units admitted this tick, not a measured elapsed time: the budget
        // decides admission before any work happens (AC-12.5), so the number that gates has to be the number that is reported. Step 11 replaces the constant
        // behind the projection with a controller driven by the previous tick's measurement.
        // ClustersScanned / DriftersDetected / DriftAbsorbed used to be reset HERE too, on the same "no producer yet" grounds. Step 10 gave them one, and their
        // reset moved up beside LastTickMigrationCount where the rest of the per-tick counters live — a second zeroing of two of the three was worse than
        // redundant, because it silently excluded the third and would have hidden a producer that ran between the two blocks.
        clusterState.LastTickReclusterBudgetUsedMs = 0d;
        clusterState.LastTickRepairedEntityCount = 0;
        clusterState.LastTickRepairUnitCount = 0;
        clusterState.LastTickRepairUnitsRefused = 0;
        clusterState._drainedCount = 0; // deferred-drain list reset (review C-1 fix)

        // Pure-Transient archetypes have no PersistentStore segment — nothing to persist to WAL, no migrations.
        // Entire flow runs inside Prep; Migrate and Finalize will see FenceBranchPath = 0 and skip.
        if (clusterState.ClusterSegment == null)
        {
            var clusterScopeT = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
            try
            {
                // Drain the archetype's own index shadow buffers (#655). This branch had none: a pure-Transient archetype could not carry per-archetype
                // indexes at all, so an in-place write to an indexed field captured a shadow entry that nothing ever consumed — the index would keep the
                // pre-mutation key forever. Runs before the dormancy sweep, matching the ordering of the cluster branch below.
                if (clusterState.TransientIndexSlots != null)
                {
                    var transientShadowScope = TyphonEvent.BeginWriteTickFenceShadow(meta.ArchetypeId, clusterState.TransientIndexSlots.Length);
                    try
                    {
                        transientShadowScope.TotalShadowEntries = ProcessClusterShadowEntries(clusterState, engineState, null);
                    }
                    finally
                    {
                        transientShadowScope.Dispose();
                    }
                }

                if (clusterState.ClusterDirtyBitmap.HasDirty)
                {
                    var transientDirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();
                    clusterState.PreviousTickDirtySnapshot = transientDirtyBits;
                    var transientDirtyClusterCount = 0;
                    for (var i = 0; i < transientDirtyBits.Length; i++)
                    {
                        transientDirtyClusterCount += BitOperations.PopCount((ulong)transientDirtyBits[i]);
                    }
                    clusterScopeT.DirtyClusterCount = transientDirtyClusterCount;

                    // Zone maps, same as the cluster branch below (#655). Without this a pure-Transient archetype's maps keep the bounds spawn widened them
                    // to, so Path B prunes away any cluster whose indexed field was later mutated OUT of that range — a silently empty query, not a stale
                    // count. Spawn widens eagerly, which is why the miss only shows after an in-place write.
                    clusterState.FenceWrittenSlots = Interlocked.Exchange(ref clusterState.WrittenSlotUnion, 0);
                    RecomputeClusterZoneMaps(clusterState, transientDirtyBits);

                    for (var slot = 0; slot < clusterState.Layout.ComponentCount; slot++)
                    {
                        engineState.SlotToComponentTable[slot].PreviousTickHadDirtyEntities = true;
                        engineState.SlotToComponentTable[slot].PreviousTickDirtyBitmap ??= Array.Empty<long>();
                    }
                    clusterState.DormancySweep(transientDirtyBits, tickNumber);
                }
                else
                {
                    clusterState.PreviousTickDirtySnapshot = null;
                    clusterState.DormancySweep(Array.Empty<long>(), tickNumber);
                }
            }
            finally
            {
                clusterScopeT.Dispose();
            }
            return false;
        }

        // Clean-bitmap branch: spatial-Dynamic archetypes still need a sparse refresh because WriteSpatial-only callers may have moved positions without
        // setting the dirty bitmap. We populate FenceDirtyBits with the local occupancy bits (so DetectClusterMigrations can scan only live slots) and route to
        // branch path 1. Finalize will run the AABB recompute + dormancy sweep; no WAL emit on this branch.
        if (!clusterState.ClusterDirtyBitmap.HasDirty)
        {
            clusterState.PreviousTickDirtySnapshot = null;

            if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic && clusterState.ActiveClusterCount > 0)
            {
                var clusterScopeC = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
                try
                {
                    clusterScopeC.HasSpatial = 1;
                    var accessorLocal = clusterState.ClusterSegment.CreateChunkAccessor();
                    try
                    {
                        var wordCount = clusterState.PrimarySegmentCapacity;
                        var spatialBits = new long[Math.Max(wordCount, 1)];
                        for (var ai = 0; ai < clusterState.ActiveClusterCount; ai++)
                        {
                            var chId = clusterState.ActiveClusterIds[ai];
                            if (chId < 0 || chId >= spatialBits.Length)
                            {
                                continue;
                            }

                            var occB = accessorLocal.GetChunkAddress(chId);
                            var occ = *(ulong*)occB;
                            spatialBits[chId] = (long)occ;
                        }

                        DetectClusterMigrations(clusterState, engineState, meta.ArchetypeId, spatialBits, ref accessorLocal);
                        clusterState.FenceDirtyBits = spatialBits;
                        clusterState.FenceBranchPath = 1; // clean-spatial-refresh: AABB recompute in Finalize, no WAL
                    }
                    finally
                    {
                        accessorLocal.Dispose();
                    }
                }
                finally
                {
                    clusterScopeC.Dispose();
                }
                return true; // Migrate (if pending) + Finalize have work to do
            }

            // No spatial refresh needed — dormancy sweep on empty bitmap here, no migrations, no Finalize work.
            clusterState.DormancySweep(Array.Empty<long>(), tickNumber);
            return false;
        }

        // Dirty-bitmap branch: full snapshot + occupancy mask + shadow + zone-maps + detect. Migrate phase will execute pending migrations (if any) under
        // cell-partitioned worker slices; Finalize will run AABB recompute, dormancy, and WAL emit on the post-migration FenceDirtyBits.
        var clusterScope = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
        try
        {
            var subSpan = Stopwatch.GetTimestamp();
            var dirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();
            clusterState.PrepSnapshotTicks += Stopwatch.GetTimestamp() - subSpan;

            // Snapshot-and-clear the written-slot union in the same step as the dirty bitmap (#559 §4.5), so Finalize reads a
            // stable value while writers for the NEXT tick start from zero.
            clusterState.FenceWrittenSlots = Interlocked.Exchange(ref clusterState.WrittenSlotUnion, 0);

            // Mask dirty bits with live occupancy to skip destroyed entities whose dirty bit remained set.
            var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                var entryCount = 0;
                var dirtyClusterCount = 0;
                subSpan = Stopwatch.GetTimestamp();
                for (var i = 0; i < dirtyBits.Length; i++)
                {
                    if (dirtyBits[i] == 0)
                    {
                        continue;
                    }
                    var occBase = accessor.GetChunkAddress(i);
                    var occupancy = *(ulong*)occBase;
                    dirtyBits[i] &= (long)occupancy;
                    if (dirtyBits[i] != 0)
                    {
                        dirtyClusterCount++;
                    }
                    entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
                }

                clusterState.PrepMaskTicks += Stopwatch.GetTimestamp() - subSpan;
                clusterState.PrepDirtyClusters = dirtyClusterCount;
                clusterScope.DirtyClusterCount = dirtyClusterCount;
                clusterScope.EntryCount = entryCount;

                // Shadow + zone-maps: runs in Prep so the per-archetype B+Tree Move calls happen before any Migrate-phase Remove+Add calls reorder the index.
                // B+Tree itself is OLC-safe across concurrent archetypes (each runs in its own Prep chunk).
                // Gated on EITHER home (#655): an archetype whose only indexed component is Transient has an empty IndexSlots and would otherwise skip both
                // the drain and the zone-map recompute entirely. Both callees dispatch over the two homes themselves.
                if (clusterState.IndexSlots != null || clusterState.TransientIndexSlots != null)
                {
                    clusterScope.HasShadow = 1;
                    var shadowScope = TyphonEvent.BeginWriteTickFenceClusterShadow(meta.ArchetypeId, dirtyClusterCount);
                    subSpan = Stopwatch.GetTimestamp();
                    try
                    {
                        shadowScope.TotalShadowEntries = ProcessClusterShadowEntries(clusterState, engineState, changeSet);
                    }
                    finally
                    {
                        shadowScope.Dispose();
                    }

                    clusterState.PrepShadowTicks += Stopwatch.GetTimestamp() - subSpan;

                    subSpan = Stopwatch.GetTimestamp();
                    RecomputeClusterZoneMaps(clusterState, dirtyBits);
                    clusterState.PrepZoneMapTicks += Stopwatch.GetTimestamp() - subSpan;
                }

                // Detect migrations: populates clusterState.PendingMigrations. Spatial-only — Dynamic mode.
                if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
                {
                    clusterScope.HasSpatial = 1;
                    subSpan = Stopwatch.GetTimestamp();
                    DetectClusterMigrations(clusterState, engineState, meta.ArchetypeId, dirtyBits, ref accessor);
                    clusterState.PrepDetectTicks += Stopwatch.GetTimestamp() - subSpan;
                }
            }
            finally
            {
                accessor.Dispose();
            }

            clusterState.FenceDirtyBits = dirtyBits;
            clusterState.FenceBranchPath = 2;
            clusterState.FenceEntryCount = clusterScope.EntryCount;
            clusterState.FenceDirtyClusterCount = clusterScope.DirtyClusterCount;
        }
        finally
        {
            clusterScope.Dispose();
        }

        // Pre-size FenceDirtyBits + per-cluster arrays to a generous upper bound so the Migrate phase (parallel or serial) doesn't hit ExecuteMigrations'
        // on-demand grow path under normal conditions. The strict bound (PrimarySegmentCapacity + PendingMigrationCount) under-estimates in practice when
        // multiple Migrate workers each allocate new clusters and inter-archetype shadow/index allocations also grow segments — observed dstChunkId values
        // exceeded this bound under AntHill loads. The doubled-plus-buffer bound covers worst-case interleavings; the cost is ~32KB extra per archetype,
        // trivial. On-demand grow under _finalizeLock (ArchetypeClusterState.GrowFenceDirtyBitsForChunkId) remains as a safety net for pathological cases.
        var existingLen = clusterState.FenceDirtyBits?.Length ?? 0;
        var upperBound = Math.Max(clusterState.PrimarySegmentCapacity, existingLen) + 2 * clusterState.PendingMigrationCount + 64;
        var preSizeStart = Stopwatch.GetTimestamp();
        clusterState.PreSizeMigrationBuffers(upperBound);
        clusterState.PrepPreSizeTicks += Stopwatch.GetTimestamp() - preSizeStart;

        // Memoize popcount of ClusterProcessBitmap so the AabbRefresh planner doesn't redo it on TickDriver (D-4).
        // Only meaningful in BarrierOnly mode; Legacy mode reads ActiveClusterCount directly.
        if (clusterState.SpatialBarrierOnly && clusterState.ClusterProcessBitmap != null)
        {
            var total = 0;
            var bm = clusterState.ClusterProcessBitmap;
            for (var w = 0; w < bm.Length; w++)
            {
                total += BitOperations.PopCount((ulong)bm[w]);
            }

            clusterState.FenceProcessBitmapClusterCount = total;
        }

        return true;
    }

    /// <summary>
    /// Phase 2 of the parallel cluster tick fence: apply a contiguous slice of one archetype's <see cref="ArchetypeClusterState.PendingMigrations"/>.
    /// Safe to call concurrently from multiple workers — each worker owns a disjoint slice (sorted by destination cell key) so dst-side mutations
    /// (slot claim, AABB union, per-cell index update) hit worker-exclusive cells. Source-side mutations (occupancy clear, dirtyBits flip,
    /// cell.EntityCount decrement) use <see cref="System.Threading.Interlocked"/> primitives; rare empty-cluster finalization is serialized via
    /// the per-archetype <see cref="ArchetypeClusterState._finalizeLock"/> through
    /// <see cref="ArchetypeClusterState.ReleaseSlot(ref ChunkAccessor{PersistentStore}, int, int, ChangeSet, SpatialGrid, bool)"/>.
    /// </summary>
    /// <remarks>
    /// Callers must ensure (a) <see cref="ArchetypeClusterState.FenceDirtyBits"/> has been pre-sized to at least
    /// <c>PrimarySegmentCapacity + PendingMigrationCount</c> entries by TickDriver before any Migrate-phase worker runs (eliminates parallel
    /// <c>Array.Resize</c>), and (b) the slice <c>[sliceStart, sliceStart+sliceCount)</c> is disjoint from every other worker's slice.
    /// </remarks>
    internal void ExecuteMigrationsSlice(ArchetypeMetadata meta, int sliceStart, int sliceCount, ChangeSet changeSet, List<DirtyBitDelta> dirtyBuffer = null,
        int chunkIndex = 0)
    {
        if (sliceCount <= 0)
        {
            return;
        }

        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || clusterState.PendingMigrationCount == 0)
        {
            return;
        }

        ExecuteMigrations(clusterState, engineState, meta.ArchetypeId, sliceStart, sliceCount, changeSet, dirtyBuffer, chunkIndex);
    }

    /// <summary>
    /// Apply a contiguous run of <see cref="DirtyBitDelta"/> entries to one archetype's <c>FenceDirtyBits</c>. Called from
    /// <c>FenceMigrateExecSystem.OnAfterChunk</c> after sorting the chunk's buffer by archetypeId so a single <c>_finalizeLock</c> acquisition covers the whole
    /// archetype run. Plain non-atomic bit writes are correct under the lock — clears and sets within a chunk operate on distinct (chunkId, slot) pairs by
    /// construction. Grows <c>FenceDirtyBits</c> on-demand under the same lock.
    /// </summary>
    internal void FlushDirtyBitDeltas(ushort archetypeId, List<DirtyBitDelta> buffer, int offset, int count)
    {
        if (count <= 0 || archetypeId >= _archetypeStates.Length)
        {
            return;
        }

        var clusterState = _archetypeStates[archetypeId]?.ClusterState;

        clusterState?.ApplyDirtyBitDeltas(buffer, offset, count);
    }

    /// <summary>
    /// Phase 3 of the parallel cluster tick fence: post-migration AABB recompute, dormancy sweep, dirty-ring archive, ComponentTable flag
    /// propagation, and WAL chunk serialization for the archetype's post-migration <see cref="ArchetypeClusterState.FenceDirtyBits"/>.
    /// Safe to call concurrently across DISTINCT archetypes. Returns the highest LSN published by this archetype's WAL chunks (0 if none).
    /// </summary>
    internal unsafe long FinalizeArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return 0;
        }
        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || clusterState.FenceBranchPath == 0)
        {
            return 0;
        }

        long highestLSN = 0;
        var dirtyBits = clusterState.FenceDirtyBits;
        
        // Drop the prefix this tick executed and keep the rest. This replaced an outright `PendingMigrationCount = 0`, which discarded every request
        // enqueued AFTER the Migrate phase — all of them, for the two detectors that run inside AabbRefresh (FlagOutliersForMigration since #230, and step
        // 10's drift detection). Both are documented as executing "next tick"; neither ever did.
        clusterState.CompactPendingMigrations();

        // Drain pending cluster finalizations (review C-1 fix): ReleaseSlot during Migrate only records the chunkId; actual finalize + FreeChunk happens here,
        // after the Migrate/AabbRefresh phase barriers. By this point no concurrent ClaimSlotInCell can race with us — safe to free clean clusters.
        clusterState.DrainPendingClusterFinalizations(_spatialGrid);

        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {

            // AABB recompute moved out of Finalize into the parallel AabbRefresh phase (FenceAabbRefreshExecSystem). Finalize is now responsible only for
            // the post-AABB bookkeeping clear + dormancy sweep + WAL emit. The serial WriteTickFence wrapper (no-WAL path) calls RecomputeDirtyClusterAabbs
            // directly before reaching FinalizeArchetypeFence, so it works equivalently.
            //
            // The bookkeeping clear lives here (single-threaded, per-archetype) — it ran inside the legacy RecomputeDirtyClusterAabbs tail before and must run
            // AFTER all AABB slices finished, which the phase barrier guarantees.
            if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
            {
                clusterState.ClearAabbRefreshBookkeeping();
            }

            // Deferred promoted-cell applies first, then the refit — in that order, because the refit has to see the tree those applies produce. Both are
            // no-ops when nothing was promoted.
            clusterState.DrainPromotedAabbApplies();

            // ST-07's make-good, in the one place that satisfies both of its constraints: after every AABB slice (the phase barrier above guarantees it) and
            // before any query can run. The in-place update path deliberately leaves leaf MBRs wider than the union of their entries, which is safe for
            // queries and false for ST-01 read literally — so the looseness must not survive the fence.
            clusterState.RefitPromotedCellTrees();

            // Clean-spatial-refresh branch (path 1) stops here — no dormancy sweep change (already swept clean), no WAL emit.
            if (clusterState.FenceBranchPath == 1)
            {
                return 0;
            }

            // Dormancy sweep with the final post-migration dirty bits.
            clusterState.DormancySweep(dirtyBits, tickNumber);

            // Archive dirty bitmap into per-archetype DirtyBitmapRing for spatial interest management.
            clusterState.ClusterDirtyRing?.Archive(tickNumber, dirtyBits, dirtyBits.Length);

            var entryCount = clusterState.FenceEntryCount;
            // Account for any net dirty-bit change from migrations: clears src bits, sets dst bits — net change is zero per migration in the common case, but a
            // destination chunk that was previously not in the snapshot grows it. For simplicity we recompute entryCount by popcount; the migration count is
            // small and this is one quick pass.
            if (clusterState.LastTickMigrationCount > 0)
            {
                var recomputed = 0;
                for (var i = 0; i < dirtyBits.Length; i++)
                {
                    if (dirtyBits[i] != 0)
                    {
                        recomputed += BitOperations.PopCount((ulong)dirtyBits[i]);
                    }
                }
                entryCount = recomputed;
            }

            if (entryCount == 0)
            {
                return highestLSN;
            }

            // Store dirty snapshot for change-filtered runtime dispatch.
            clusterState.PreviousTickDirtySnapshot = dirtyBits;

            // Propagate dirty status to ComponentTables for change-filtered runtime dispatch.
            for (var slot = 0; slot < clusterState.Layout.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];
                table.PreviousTickHadDirtyEntities = true;
                table.PreviousTickDirtyBitmap ??= Array.Empty<long>();
            }

            // #568 — the declared durability window. Checkpoint archetypes emit NO fence WAL records; their SingleVersion values reach disk through the
            // checkpoint, the same path cluster STRUCTURE has always used, so a crash costs up to one checkpoint interval of freshness (not existence).
            //
            // The gate sits HERE, after the dormancy sweep, the dirty-ring archive, PreviousTickDirtySnapshot and the ComponentTable flag propagation above,
            // and NOT at the top of the method. The dirty bitmap has eleven consumers and WAL emit is one of them: zone-map recompute, migration detect and
            // execute, AABB refresh, dormancy, the dirty ring, and both change-filtered dispatch surfaces all read it. Only the emission is optional.
            //
            // ClusterDurabilityTests pins both halves — the emission stops, and every other consumer keeps being fed.
            //
            // Versioned components are unaffected in either setting: their revision chain is logged at commit and is authoritative (see skipMask below), so
            // no ClusterDurability value can lose a Versioned write.
            if (meta.ClusterDurability == ClusterDurability.Checkpoint)
            {
                return highestLSN;
            }

            var layout = clusterState.Layout;
            // Slots excluded from fence emission:
            //   Transient — never persisted at all.
            //   Versioned — the cluster slot holds only a HEAD *cache*; the revision chain is the truth and is logged at commit.
            //     On reopen the HEAD is rebuilt from the chain (ArchetypeClusterState.RebuildVersionedHeadFromChain), so a fence
            //     record for a Versioned slot is written, fsynced, retained and then ignored. Pure waste — do not emit it.
            var skipMask = meta.TransientSlotMask | meta.VersionedSlotMask;
            // Precompute the durable component slots' WAL identity once per archetype. Each becomes one Slot record per dirty
            // entity (M4); the entity PK is read from the cluster's id array, so fence records are logical, never physical.
            // Sizes and offsets are hoisted here too — they are per-archetype constants, and reading them inside the per-entity loop
            // costs one bounds-checked array load per record (100k+ per tick on a large archetype) for a value that never changes.
            Span<int> durableSlots = stackalloc int[layout.ComponentCount];
            Span<int> durableSizes = stackalloc int[layout.ComponentCount];
            Span<int> durableOffsets = stackalloc int[layout.ComponentCount];
            var durableCount = 0;
            for (var slot = 0; slot < layout.ComponentCount; slot++)
            {
                if ((skipMask & (1 << slot)) != 0)
                {
                    continue;
                }

                durableSlots[durableCount] = slot;
                durableSizes[durableCount] = layout.ComponentSize(slot);
                durableOffsets[durableCount] = layout.ComponentOffset(slot);
                durableCount++;
            }

            // Nothing durable to emit (every slot Transient and/or Versioned) — skip the whole walk rather than building empty batches.
            if (durableCount == 0)
            {
                return highestLSN;
            }

            var entityIdsOffset = layout.EntityIdsOffset;

            // #559 §4.5 — narrow the emitted columns to the component slots actually written this tick. The mask is a single union per ARCHETYPE, not one per
            // cluster: per-cluster was implemented and measured first and is slower (+2.1 ms/tick median, ~10 ms spread) because the array is written by every
            // worker on every dirty-marking write and false-shares. The emitter only ever consumes the union, so the finer granularity bought nothing it could
            // use — hence one column set for all clusters of the archetype. The union is fail-safe: a writer that did not identify its component recorded
            // AllSlotsWritten, so the archetype falls back to emitting everything.
            var fenceWritten = clusterState.FenceWrittenSlots;
            var durableMask = 0;
            for (var d = 0; d < durableCount; d++)
            {
                durableMask |= 1 << durableSlots[d];
            }

            var activeMask = fenceWritten & durableMask;
            if (activeMask == 0)
            {
                return highestLSN;   // dirty entities, but nothing durable was written to them
            }

            Span<int> slotIndices = stackalloc int[durableCount];
            Span<int> compSizes = stackalloc int[durableCount];
            Span<int> compOffsets = stackalloc int[durableCount];
            var columnCount = 0;
            var totalCompSize = 0;
            for (var d = 0; d < durableCount; d++)
            {
                if ((activeMask & (1 << durableSlots[d])) == 0)
                {
                    continue;
                }

                slotIndices[columnCount] = durableSlots[d];
                compSizes[columnCount] = durableSizes[d];
                compOffsets[columnCount] = durableOffsets[d];
                totalCompSize += durableSizes[d];
                columnCount++;
            }

            slotIndices = slotIndices[..columnCount];
            compSizes = compSizes[..columnCount];
            compOffsets = compOffsets[..columnCount];

            // LOG-06 for the columnar path: collect the collection-handle byte ranges of every emitted column so the codec can zero them out of the copied
            // SoA bytes. A cluster slot carries no component overhead, so a field's value-relative offset IS its slot-relative one — the same identity that
            // lets ClusterCollectionSlot share the table's descriptor. Almost always empty; the two loops cost nothing when it is.
            var handleRangeCount = 0;
            for (var c = 0; c < columnCount; c++)
            {
                handleRangeCount += engineState.SlotToComponentTable[slotIndices[c]].CollectionFields.Length;
            }

            Span<ulong> columnHandleRanges = handleRangeCount == 0 ? default : stackalloc ulong[handleRangeCount];
            if (handleRangeCount > 0)
            {
                var hr = 0;
                for (var c = 0; c < columnCount; c++)
                {
                    foreach (var f in engineState.SlotToComponentTable[slotIndices[c]].CollectionFields)
                    {
                        columnHandleRanges[hr++] = RecordCodec.PackColumnHandleRange(c, f.OffsetInComponentStorage, f.HandleSize);
                    }
                }
            }

            // Columnar emission (#559): one FenceBlock record per dirty cluster instead of one Slot record per (entity, component).
            // A cluster's entity keys and each component's values are already contiguous in the SoA, so every part of the payload
            // is a single bulk copy — the codec copies straight out of the page into the WAL claim, with no staging arena.
            var blocks = _fenceBlocks ??= new RecordCodec.FenceBlockDescriptor[64];
            var blockCount = 0;
            var batchBytes = 0;

            for (var wi = 0; wi < dirtyBits.Length; wi++)
            {
                var word = dirtyBits[wi];
                if (word == 0)
                {
                    continue;
                }

                // Emit the contiguous slot RANGE that spans the dirty bits, not the whole cluster and not a gather: an all-dirty
                // cluster degenerates to one copy per column, a single dirty entity to one entity. Clean entities inside the
                // range ride along — redundant, never wrong — and DirtyMask records which ones actually changed.
                var firstSlot = BitOperations.TrailingZeroCount((ulong)word);
                var lastSlot = 63 - BitOperations.LeadingZeroCount((ulong)word);
                var slotSpan = lastSlot - firstSlot + 1;
                var recWire = RecordCodec.FenceBlockWireSize(columnCount, slotSpan, totalCompSize);

                if (blockCount > 0 && (batchBytes + recWire > MaxFenceBatchBytes || blockCount == blocks.Length))
                {
                    highestLSN = Math.Max(highestLSN, AppendFenceBlockBatch(blocks, blockCount, meta.ArchetypeId, tickNumber,
                        entityIdsOffset, slotIndices, compSizes, compOffsets, totalCompSize, columnHandleRanges));
                    blockCount = 0;
                    batchBytes = 0;
                }

                blocks[blockCount++] = new RecordCodec.FenceBlockDescriptor(
                    (nint)accessor.GetChunkAddress(wi), wi, (byte)firstSlot, (byte)slotSpan, (ulong)word >> firstSlot);
                batchBytes += recWire;
            }

            if (blockCount > 0)
            {
                highestLSN = Math.Max(highestLSN, AppendFenceBlockBatch(blocks, blockCount, meta.ArchetypeId, tickNumber,
                    entityIdsOffset, slotIndices, compSizes, compOffsets, totalCompSize, columnHandleRanges));
            }

            // #389: the columnar record carries the SoA bytes with every collection handle zeroed, so on its own it would RESTORE a collection as empty —
            // including one whose content was already safe on the checkpoint timeline. The content therefore rides alongside, in its own fence batch of
            // CollectionDelta records. A separate Append rather than a new record kind inside the block: fence records are individually committed (LOG-04),
            // so ordering between the two batches is by LSN, and recovery folds per (entity, slot, field) and flushes after the Slot apply regardless.
            if (handleRangeCount > 0)
            {
                highestLSN = Math.Max(highestLSN, AppendClusterCollectionContent(
                    engineState, dirtyBits, ref accessor, entityIdsOffset, slotIndices, compSizes, compOffsets, tickNumber));
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return highestLSN;
    }
}

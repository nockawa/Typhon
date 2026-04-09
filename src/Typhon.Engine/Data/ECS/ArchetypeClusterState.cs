using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Per-archetype runtime state for cluster storage. Manages the cluster segment, active cluster tracking, and slot claiming for entity spawn/destroy.
/// </summary>
/// <remarks>
/// <para>Each cluster-eligible archetype gets one <see cref="ArchetypeClusterState"/> instance, created during <c>DatabaseEngine.InitializeArchetypes</c>.</para>
/// <para>Active clusters are tracked in a compact array for O(N_clusters) iteration.
/// Free slot discovery uses bitmask TZCNT on OccupancyBits.</para>
/// </remarks>
internal sealed unsafe class ArchetypeClusterState
{
    /// <summary>ChunkBasedSegment backing cluster data (SV + V components). Null for pure-Transient archetypes.</summary>
    public ChunkBasedSegment<PersistentStore> ClusterSegment;

    /// <summary>ChunkBasedSegment backing Transient component data. Null if archetype has no Transient components.
    /// Uses identical layout as <see cref="ClusterSegment"/> (same stride, same offsets). Chunk IDs are synchronized
    /// via lockstep allocation/free.</summary>
    public ChunkBasedSegment<TransientStore> TransientSegment;

    /// <summary>TransientStore instance kept alive for heap-backed TransientSegment. Null if no Transient components.</summary>
    internal TransientStore? TransientClusterStore;

    /// <summary>Precomputed layout info (offsets, sizes, cluster size N).</summary>
    public ArchetypeClusterInfo Layout;

    /// <summary>Compact array of chunk IDs for clusters with occupancy > 0.</summary>
    public int[] ActiveClusterIds;

    /// <summary>Number of active clusters (valid entries in <see cref="ActiveClusterIds"/>).</summary>
    public int ActiveClusterCount;

    /// <summary>Chunk ID of first cluster with at least one free slot. -1 = none (allocate new).</summary>
    public int FreeClusterHead;

    /// <summary>
    /// Per-cluster cell membership for spatial archetypes (issue #229 Phase 1+2). Flat array indexed by <c>clusterChunkId</c>, value is the spatial
    /// grid <c>cellKey</c> the cluster is attached to, or <c>-1</c> if unmapped (cluster not yet allocated, or archetype is not opted into the grid).
    /// </summary>
    /// <remarks>
    /// Lazily allocated by <see cref="ClaimSlotInCell"/> or <see cref="RebuildCellState"/>. Non-spatial archetypes and spatial archetypes running without a
    /// configured <see cref="SpatialGrid"/> leave this field <c>null</c> — the existing <see cref="ClaimSlot"/> path is unchanged for them.
    /// </remarks>
    public int[] ClusterCellMap;

    // ═══════════════════════════════════════════════════════════════════════
    // Migration queue (issue #229 Phase 3). Lazily allocated; only used when
    // SpatialSlot.Tree != null AND a SpatialGrid is configured AND cell crossings
    // actually occur. Population is sequential (detection loop runs single-threaded
    // inside WriteClusterTickFence), drained by ExecuteMigrations in the same loop.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype pending migration queue. Populated by cell-crossing detection in
    /// <c>ProcessClusterSpatialEntries</c>, drained by <see cref="ExecuteMigrations"/> at the tick fence.
    /// Null until the first cell-crossing is detected.</summary>
    internal MigrationRequest[] PendingMigrations;

    /// <summary>Number of valid entries in <see cref="PendingMigrations"/>. Reset to zero at the start
    /// of every <see cref="ExecuteMigrations"/> call.</summary>
    internal int PendingMigrationCount;

    /// <summary>Telemetry counter: number of migrations executed in the most recently completed tick.</summary>
    public int LastTickMigrationCount;

    /// <summary>Telemetry counter: number of position changes that crossed the raw cell boundary but were
    /// absorbed by the hysteresis margin (no migration queued). Useful for tuning
    /// <see cref="SpatialGridConfig.MigrationHysteresisRatio"/>.</summary>
    public int LastTickHysteresisAbsorbedCount;

    /// <summary>Telemetry counter: wall-clock duration of <see cref="ExecuteMigrations"/> in milliseconds,
    /// for the most recently completed tick.</summary>
    public double LastTickMigrationExecuteMs;

    /// <summary>
    /// Test observation hook: length (in long words) of the <c>dirtyBits</c> snapshot at the end of <c>ExecuteMigrations</c>. Used by regression tests to
    /// verify the snapshot was grown when migration allocated a brand-new destination cluster whose chunk id exceeded the pre-migration length.
    /// Zero when no migrations ran.
    /// </summary>
    public int LastMigrationDirtyBitsWordCount;

    /// <summary>Per-entity dirty tracking for tick fence WAL serialization. Index = clusterChunkId * 64 + slotIndex.</summary>
    public DirtyBitmap ClusterDirtyBitmap;

    /// <summary>
    /// Per-cluster tight 2D AABB plus category mask for spatially-active clusters (issue #230).
    /// Indexed by clusterChunkId. Populated by spawn/destroy/migration hooks and the tick-fence recompute pass. Null for non-spatial archetypes or before the
    /// first spatial write. In-memory only — rebuilt at startup via <see cref="RebuildClusterAabbs"/> from entity positions (Q2/Q6 transient-state decision).
    /// Phase 1 is 2D f32 only.
    /// </summary>
    internal ClusterSpatialAabb[] ClusterAabbs;

    /// <summary>
    /// Per-cluster back-pointer into its cell's <see cref="CellSpatialIndex.ClusterIds"/> SoA array.
    /// <c>-1</c> for clusters not currently in the per-cell index (non-spatial archetypes, Static-mode archetypes in Phase 1, or before the first insertion).
    /// Indexed by clusterChunkId.
    /// </summary>
    internal int[] ClusterSpatialIndexSlot;

    /// <summary>
    /// Per-archetype per-cell spatial slot, indexed by cellKey. Null entries for cells where this archetype has no clusters. Lazy-allocated:
    /// the <see cref="PerCellSpatialSlot"/> is created on first cluster insertion into that cell. The DynamicIndex inside is also lazy (created on first
    /// <see cref="CellSpatialIndex.Add"/>). Null entirely for non-spatial archetypes or before grid opt-in.
    /// </summary>
    internal PerCellSpatialSlot[] PerCellIndex;

    /// <summary>
    /// Snapshot of the previous tick's dirty bitmap (occupancy-masked). Set during <c>WriteClusterTickFence</c>, consumed
    /// by <c>TyphonRuntime.BuildFilteredClusterEntities</c> for change-filtered parallel dispatch.
    /// Word index = clusterChunkId, bit position = slotIndex. Null when no entities were dirty.
    /// </summary>
    public long[] PreviousTickDirtySnapshot;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype B+Tree indexes. Null if archetype has no indexed fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype B+Tree index slots, one per component slot with indexed fields. Null if no indexed fields.</summary>
    public ClusterIndexSlot[] IndexSlots;

    /// <summary>Shadow guard bitmap. Guards first-write-per-tick shadow capture. Same index semantics as <see cref="ClusterDirtyBitmap"/>.</summary>
    public DirtyBitmap ClusterShadowBitmap;

    /// <summary>Shared <see cref="ChunkBasedSegment{TStore}"/> backing all per-archetype B+Trees for this archetype.</summary>
    public ChunkBasedSegment<PersistentStore> IndexSegment;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype Spatial R-Tree. Null if archetype has no spatial fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype spatial R-Tree state. Check <c>SpatialSlot.Tree != null</c> for presence.</summary>
    public ClusterSpatialSlot SpatialSlot;

    private ArchetypeClusterState() { }

    /// <summary>Chunk capacity of the primary (non-null) segment.</summary>
    internal int PrimarySegmentCapacity => ClusterSegment?.ChunkCapacity ?? TransientSegment.ChunkCapacity;

    /// <summary>Mark an entity slot as dirty for tick fence processing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDirty(int clusterChunkId, int slotIndex)
    {
        int entityIndex = clusterChunkId * 64 + slotIndex;
        ClusterDirtyBitmap.Set(entityIndex);
    }

    /// <summary>
    /// Create a new ArchetypeClusterState for a cluster-eligible archetype (fresh database).
    /// </summary>
    /// <param name="layout">Precomputed cluster layout (shared by both segments).</param>
    /// <param name="segment">PersistentStore backing segment for SV+V components. Null for pure-Transient archetypes.</param>
    /// <param name="transientSegment">TransientStore backing segment for Transient components. Default (null) if no Transient.</param>
    /// <param name="transientStore">TransientStore instance to keep alive. Null if no Transient.</param>
    public static ArchetypeClusterState Create(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment,
        ChunkBasedSegment<TransientStore> transientSegment = null, TransientStore? transientStore = null)
    {
        Debug.Assert(segment != null || transientSegment != null, "At least one cluster segment must be provided");
        int capacity = segment?.ChunkCapacity ?? transientSegment.ChunkCapacity;
        return new ArchetypeClusterState
        {
            ClusterSegment = segment,
            TransientSegment = transientSegment,
            TransientClusterStore = transientStore,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, capacity * 64)),
        };
    }

    /// <summary>
    /// Create an ArchetypeClusterState from an existing persisted segment (database reopen).
    /// Scans cluster occupancy bitmaps to rebuild <see cref="ActiveClusterIds"/> and <see cref="FreeClusterHead"/>.
    /// </summary>
    public static ArchetypeClusterState CreateFromExisting(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment,
        ChunkBasedSegment<TransientStore> transientSegment = null, TransientStore? transientStore = null)
    {
        Debug.Assert(segment != null || transientSegment != null, "At least one cluster segment must be provided");
        int capacity = segment?.ChunkCapacity ?? transientSegment.ChunkCapacity;
        var state = new ArchetypeClusterState
        {
            ClusterSegment = segment,
            TransientSegment = transientSegment,
            TransientClusterStore = transientStore,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, capacity * 64)),
        };

        state.RebuildActiveList();
        return state;
    }

    /// <summary>
    /// Scan all allocated chunks in the segment, read OccupancyBits, and rebuild <see cref="ActiveClusterIds"/>,
    /// <see cref="ActiveClusterCount"/>, and <see cref="FreeClusterHead"/> from persisted data.
    /// </summary>
    private void RebuildActiveList()
    {
        ActiveClusterCount = 0;
        FreeClusterHead = -1;

        // Scan primary segment (PersistentStore for mixed/SV, TransientStore for pure-Transient)
        if (ClusterSegment != null)
        {
            var accessor = ClusterSegment.CreateChunkAccessor();
            try
            {
                ScanActiveChunks(ref accessor, ClusterSegment.ChunkCapacity);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else if (TransientSegment != null)
        {
            var accessor = TransientSegment.CreateChunkAccessor();
            try
            {
                ScanActiveChunksTransient(ref accessor, TransientSegment.ChunkCapacity);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    private void ScanActiveChunks(ref ChunkAccessor<PersistentStore> accessor, int capacity)
    {
        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!ClusterSegment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            byte* clusterBase = accessor.GetChunkAddress(chunkId);
            ulong occupancy = *(ulong*)clusterBase;

            if (occupancy == 0)
            {
                continue;
            }

            AddToActiveList(chunkId);

            if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
            {
                FreeClusterHead = chunkId;
            }
        }
    }

    private void ScanActiveChunksTransient(ref ChunkAccessor<TransientStore> accessor, int capacity)
    {
        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!TransientSegment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            byte* clusterBase = accessor.GetChunkAddress(chunkId);
            ulong occupancy = *(ulong*)clusterBase;

            if (occupancy == 0)
            {
                continue;
            }

            AddToActiveList(chunkId);

            if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
            {
                FreeClusterHead = chunkId;
            }
        }
    }

    /// <summary>
    /// Claim a free slot in an existing cluster, or allocate a new cluster.
    /// Returns the cluster chunk ID and the slot index within the cluster.
    /// </summary>
    /// <remarks>
    /// <para>Uses CAS on OccupancyBits for correctness under future concurrent commit scenarios.
    /// FinalizeSpawns is single-writer (no concurrent commit), so CAS always succeeds on first try.</para>
    /// <para>The OccupancyBit is set immediately by this method. The caller MUST write component data and EntityKey before the next iteration boundary to
    /// maintain the invariant that occupied slots contain valid data.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlot(ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet)
    {
        // Try existing cluster with free slots (O(1) when FreeClusterHead is valid)
        if (FreeClusterHead >= 0)
        {
            int clusterId = FreeClusterHead;
            byte* clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref ulong occupancy = ref *(ulong*)clusterBase;

            ulong current = occupancy;
            ulong available = ~current & Layout.FullMask;
            if (available != 0)
            {
                int slot = BitOperations.TrailingZeroCount(available);
                ulong desired = current | (1UL << slot);

                // CAS for future-proof concurrent commit. Single-writer (no concurrent commit).
                if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
                {
                    // If cluster is now full, reset head — next call allocates new (O(1))
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }

                    return (clusterId, slot);
                }

                // CAS failed (concurrent writer took a different slot) — reread once
                current = occupancy;
                available = ~current & Layout.FullMask;
                if (available != 0)
                {
                    slot = BitOperations.TrailingZeroCount(available);
                    desired = current | (1UL << slot);
                    occupancy = desired; // Direct write — single-writer (no concurrent commit)
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }

                    return (clusterId, slot);
                }
            }

            // Current free cluster is actually full — reset and fall through to allocate
            FreeClusterHead = -1;
        }

        // No free clusters — allocate new one (O(1))
        int newClusterId = AllocateNewCluster(changeSet);
        byte* newBase = accessor.GetChunkAddress(newClusterId, true);

        // Claim slot 0 in the fresh cluster
        *(ulong*)newBase = 1UL; // OccupancyBit 0 set
        FreeClusterHead = Layout.ClusterSize > 1 ? newClusterId : -1;

        return (newClusterId, 0);
    }

    /// <summary>
    /// Claim a free slot for pure-Transient archetypes (no PersistentStore segment).
    /// Same logic as the PersistentStore overload but using TransientStore accessor.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlot(ref ChunkAccessor<TransientStore> accessor)
    {
        if (FreeClusterHead >= 0)
        {
            int clusterId = FreeClusterHead;
            byte* clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref ulong occupancy = ref *(ulong*)clusterBase;

            ulong current = occupancy;
            ulong available = ~current & Layout.FullMask;
            if (available != 0)
            {
                int slot = BitOperations.TrailingZeroCount(available);
                ulong desired = current | (1UL << slot);

                if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
                {
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }
                    return (clusterId, slot);
                }

                current = occupancy;
                available = ~current & Layout.FullMask;
                if (available != 0)
                {
                    slot = BitOperations.TrailingZeroCount(available);
                    desired = current | (1UL << slot);
                    occupancy = desired;
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }
                    return (clusterId, slot);
                }
            }

            FreeClusterHead = -1;
        }

        int newClusterId = AllocateNewCluster(null);
        byte* newBase = accessor.GetChunkAddress(newClusterId, true);
        *(ulong*)newBase = 1UL;
        FreeClusterHead = Layout.ClusterSize > 1 ? newClusterId : -1;

        return (newClusterId, 0);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Phase 1+2 of issue #229 — spatially coherent slot claiming. Only used when the
    // engine has a configured SpatialGrid AND this archetype has a spatial field.
    // All entities in a given cluster will share the same grid cell.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Claim a free slot in a cluster belonging to the given spatial <paramref name="cellKey"/>, allocating a new cluster attached to the cell if none of
    /// its existing clusters has a free slot.
    /// </summary>
    /// <remarks>
    /// <para>This is the spatial-aware counterpart of <see cref="ClaimSlot"/>. Unlike <c>ClaimSlot</c> it ignores <see cref="FreeClusterHead"/> — that hint
    /// is a global free-slot cache that cannot distinguish cells, so it's useless once spatial coherence is required. Instead, we scan the cell's cluster
    /// list (typically ≤80 entries for AntHill-scale density, ≤15-30 ns scan cost).</para>
    /// <para>Every successful claim bumps <see cref="CellDescriptor.EntityCount"/>. Allocation of a new cluster additionally bumps
    /// <see cref="CellDescriptor.ClusterCount"/>, attaches the cluster to the cell's pool segment, and records the mapping in <see cref="ClusterCellMap"/>.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet, SpatialGrid grid)
    {
        ref var cell = ref grid.GetCell(cellKey);
        var clusters = grid.CellClusterPool.GetClusters(in cell);

        // Scan existing clusters attached to this cell for a free slot.
        for (int i = 0; i < clusters.Length; i++)
        {
            int clusterId = clusters[i];
            byte* clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref ulong occupancy = ref *(ulong*)clusterBase;

            ulong current = occupancy;
            ulong available = ~current & Layout.FullMask;
            if (available == 0)
            {
                continue;
            }

            int slot = BitOperations.TrailingZeroCount(available);
            ulong desired = current | (1UL << slot);

            // CAS for future-proof concurrent commit (matches ClaimSlot semantics). Single-writer today.
            if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
            {
                cell.EntityCount++;
                return (clusterId, slot);
            }

            // CAS failed — another writer took the slot. Retry with a direct read+write.
            current = occupancy;
            available = ~current & Layout.FullMask;
            if (available != 0)
            {
                slot = BitOperations.TrailingZeroCount(available);
                occupancy = current | (1UL << slot);
                cell.EntityCount++;
                return (clusterId, slot);
            }

            // Cluster became full between reads — fall through to the next candidate.
        }

        // No free slot in any cluster of this cell — allocate a new cluster and attach it to the cell.
        // CellClusterPool.AddCluster bumps cell.ClusterCount for us; we only bump cell.EntityCount here.
        int newChunkId = AllocateNewCluster(changeSet);
        EnsureClusterCellMapCapacity(newChunkId + 1);
        ClusterCellMap[newChunkId] = cellKey;
        grid.CellClusterPool.AddCluster(ref cell, cellKey, newChunkId);
        cell.EntityCount++;

        byte* newBase = accessor.GetChunkAddress(newChunkId, true);
        *(ulong*)newBase = 1UL; // occupancy bit 0
        return (newChunkId, 0);
    }

    /// <summary>
    /// Pure-Transient overload of <see cref="ClaimSlotInCell"/>. Identical logic, different accessor type.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, ref ChunkAccessor<TransientStore> accessor, SpatialGrid grid)
    {
        ref var cell = ref grid.GetCell(cellKey);
        var clusters = grid.CellClusterPool.GetClusters(in cell);

        for (int i = 0; i < clusters.Length; i++)
        {
            int clusterId = clusters[i];
            byte* clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref ulong occupancy = ref *(ulong*)clusterBase;

            ulong current = occupancy;
            ulong available = ~current & Layout.FullMask;
            if (available == 0)
            {
                continue;
            }

            int slot = BitOperations.TrailingZeroCount(available);
            ulong desired = current | (1UL << slot);

            if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
            {
                cell.EntityCount++;
                return (clusterId, slot);
            }

            current = occupancy;
            available = ~current & Layout.FullMask;
            if (available != 0)
            {
                slot = BitOperations.TrailingZeroCount(available);
                occupancy = current | (1UL << slot);
                cell.EntityCount++;
                return (clusterId, slot);
            }
        }

        // CellClusterPool.AddCluster bumps cell.ClusterCount for us; we only bump cell.EntityCount here.
        int newChunkId = AllocateNewCluster(null);
        EnsureClusterCellMapCapacity(newChunkId + 1);
        ClusterCellMap[newChunkId] = cellKey;
        grid.CellClusterPool.AddCluster(ref cell, cellKey, newChunkId);
        cell.EntityCount++;

        byte* newBase = accessor.GetChunkAddress(newChunkId, true);
        *(ulong*)newBase = 1UL;
        return (newChunkId, 0);
    }

    /// <summary>
    /// Reconstruct <see cref="ClusterCellMap"/> and the grid's per-cell state from the current active clusters' entity positions. Called at startup for
    /// spatial archetypes after the <see cref="SpatialGrid"/> is configured — on a fresh database this is a no-op (no active clusters); on a reopened
    /// database it re-derives the cluster→cell mapping from persisted data.
    /// </summary>
    /// <remarks>
    /// <para>Reads the first occupied entity's spatial field from each active cluster and uses
    /// <see cref="SpatialGrid.WorldToCellKeyFromSpatialField"/> to compute its cell. This relies on the spatial coherence invariant (all entities in a
    /// cluster belong to the same cell) — reading only the first entity is sufficient.</para>
    /// <para>Non-spatial archetypes and archetypes without a configured grid are no-ops. Pure-Transient archetypes are also skipped since their data doesn't
    /// survive restart.</para>
    /// <para><b>Precondition — NOT idempotent on a dirty grid.</b> This method ADDS to
    /// <see cref="CellDescriptor.EntityCount"/> and appends cluster IDs to each cell's pool segment.
    /// Callers MUST pass either a fresh <see cref="SpatialGrid"/> or one that has been reset via
    /// <see cref="SpatialGrid.ResetCellState"/> — calling twice without a reset double-counts entities
    /// and duplicates cluster IDs in the pool. The single caller today
    /// (<c>DatabaseEngine.InitializeArchetypes</c>) constructs a fresh grid immediately before this
    /// loop, satisfying the precondition.</para>
    /// </remarks>
    public void RebuildCellState(SpatialGrid grid)
    {
        if (grid == null || SpatialSlot.Tree == null || ClusterSegment == null)
        {
            return;
        }
        if (ActiveClusterCount == 0)
        {
            return;
        }

        EnsureClusterCellMapCapacity(PrimarySegmentCapacity);
        Array.Fill(ClusterCellMap, -1);

        var ss = SpatialSlot;
        int componentOffset = Layout.ComponentOffset(ss.Slot);
        int compStride = Layout.ComponentSize(ss.Slot);
        var fieldType = ss.FieldInfo.FieldType;

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < ActiveClusterCount; i++)
            {
                int chunkId = ActiveClusterIds[i];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                ulong occupancy = *(ulong*)clusterBase;
                if (occupancy == 0)
                {
                    continue;
                }

                int firstSlot = BitOperations.TrailingZeroCount(occupancy);
                byte* fieldPtr = clusterBase + componentOffset + firstSlot * compStride + ss.FieldOffset;
                int cellKey = grid.WorldToCellKeyFromSpatialField(fieldPtr, fieldType);

                ClusterCellMap[chunkId] = cellKey;
                ref var cell = ref grid.GetCell(cellKey);
                grid.CellClusterPool.AddCluster(ref cell, cellKey, chunkId);
                cell.EntityCount += BitOperations.PopCount(occupancy);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Grow <see cref="ClusterCellMap"/> to hold at least <paramref name="requiredLength"/> entries, initializing new slots to <c>-1</c> (unmapped).
    /// Called lazily by <see cref="ClaimSlotInCell"/> when a new cluster chunk ID lands beyond the current bounds.
    /// </summary>
    internal void EnsureClusterCellMapCapacity(int requiredLength)
    {
        if (ClusterCellMap == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterCellMap = new int[initial];
            Array.Fill(ClusterCellMap, -1);
            return;
        }
        if (ClusterCellMap.Length >= requiredLength)
        {
            return;
        }
        // Defensive: if ClusterCellMap.Length is ever 0 (shouldn't happen through normal
        // construction — we always allocate >= 16 — but a future constructor path could regress)
        // start the doubling from 1 instead of 0 to avoid an infinite loop.
        int newLen = Math.Max(ClusterCellMap.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        int oldLen = ClusterCellMap.Length;
        Array.Resize(ref ClusterCellMap, newLen);
        Array.Fill(ClusterCellMap, -1, oldLen, newLen - oldLen);
    }

    /// <summary>
    /// Grow <see cref="ClusterAabbs"/> to hold at least <paramref name="requiredLength"/> entries. Issue #230.
    /// New slots are left at <see cref="ClusterSpatialAabb.Empty"/> (neutral seed for subsequent unions).
    /// </summary>
    internal void EnsureClusterAabbsCapacity(int requiredLength)
    {
        if (ClusterAabbs == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterAabbs = new ClusterSpatialAabb[initial];
            for (int i = 0; i < initial; i++)
            {
                ClusterAabbs[i] = ClusterSpatialAabb.Empty;
            }
            return;
        }
        if (ClusterAabbs.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(ClusterAabbs.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        int oldLen = ClusterAabbs.Length;
        Array.Resize(ref ClusterAabbs, newLen);
        for (int i = oldLen; i < newLen; i++)
        {
            ClusterAabbs[i] = ClusterSpatialAabb.Empty;
        }
    }

    /// <summary>
    /// Grow <see cref="ClusterSpatialIndexSlot"/> to hold at least <paramref name="requiredLength"/> entries, initializing new slots to <c>-1</c> (not in
    /// the per-cell index). Issue #230.
    /// </summary>
    internal void EnsureClusterSpatialIndexSlotCapacity(int requiredLength)
    {
        if (ClusterSpatialIndexSlot == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterSpatialIndexSlot = new int[initial];
            Array.Fill(ClusterSpatialIndexSlot, -1);
            return;
        }
        if (ClusterSpatialIndexSlot.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(ClusterSpatialIndexSlot.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        int oldLen = ClusterSpatialIndexSlot.Length;
        Array.Resize(ref ClusterSpatialIndexSlot, newLen);
        Array.Fill(ClusterSpatialIndexSlot, -1, oldLen, newLen - oldLen);
    }

    /// <summary>
    /// Grow <see cref="PerCellIndex"/> to hold at least <paramref name="requiredLength"/> entries. New slots are left <c>null</c> —
    /// each <see cref="PerCellSpatialSlot"/> is lazily allocated on first cluster insertion into that cell via <see cref="AddClusterToPerCellIndex"/>.
    /// Issue #230.
    /// </summary>
    internal void EnsurePerCellIndexCapacity(int requiredLength)
    {
        if (PerCellIndex == null)
        {
            int initial = Math.Max(16, requiredLength);
            PerCellIndex = new PerCellSpatialSlot[initial];
            return;
        }
        if (PerCellIndex.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(PerCellIndex.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        Array.Resize(ref PerCellIndex, newLen);
    }

    /// <summary>
    /// Recompute the tight 2D AABB and category-mask union of a cluster by scanning its occupied slots. The spatial field is read
    /// via <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/> which dispatches on the archetype's <see cref="SpatialFieldInfo.FieldType"/>.
    /// Degenerate entities (NaN/Inf bounds) are skipped. Issue #230.
    /// </summary>
    /// <remarks>
    /// Cost: one pass over <see cref="ArchetypeClusterInfo.ClusterSize"/> occupancy bits, ~50-100 ns per occupied entity on the L1-hot common path.
    /// Category mask is the OR of per-entity masks; in Phase 1 all entities use the default <c>uint.MaxValue</c> mask, so this collapses to <c>uint.MaxValue</c>.
    /// </remarks>
    internal ClusterSpatialAabb RecomputeClusterAabb(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor)
    {
        var ss = SpatialSlot;
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ulong occupancy = *(ulong*)clusterBase;
        int componentOffset = Layout.ComponentOffset(ss.Slot);
        int componentStride = Layout.ComponentSize(ss.Slot);

        var aabb = ClusterSpatialAabb.Empty;
        Span<double> coords = stackalloc double[4]; // Phase 1 is 2D only → 4 doubles

        ulong bits = occupancy;
        while (bits != 0)
        {
            int slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            byte* fieldPtr = clusterBase + componentOffset + slot * componentStride + ss.FieldOffset;
            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, ss.FieldInfo, coords, ss.Descriptor))
            {
                continue; // skip degenerate slot
            }

            aabb.Union(entityMinX: (float)coords[0], (float)coords[1], (float)coords[2], (float)coords[3], uint.MaxValue);
        }

        return aabb;
    }

    /// <summary>
    /// Startup rebuild of per-cluster AABBs from entity positions. Mirrors <see cref="RebuildCellState"/>:
    /// both derive transient state from persistent cluster data on database reopen. Iterates all active clusters, recomputes each AABB, stores it
    /// in <see cref="ClusterAabbs"/>, and adds the cluster to its cell's <see cref="PerCellSpatialSlot.DynamicIndex"/> (lazy-allocated).
    /// Back-pointer recorded in <see cref="ClusterSpatialIndexSlot"/> so subsequent updates are O(1). Issue #230.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 1 supports Dynamic mode only. Static-mode archetypes are skipped — they keep using the existing per-archetype R-Tree path.
    /// </para>
    /// <para>
    /// Precondition: <see cref="RebuildCellState"/> has already run, so <see cref="ClusterCellMap"/> is populated and every active cluster's cell is known.
    /// </para>
    /// </remarks>
    public void RebuildClusterAabbs()
    {
        if (SpatialSlot.Tree == null || ClusterSegment == null)
        {
            return;
        }
        if (SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            return; // Phase 1: dynamic mode only
        }
        if (ActiveClusterCount == 0)
        {
            return;
        }

        EnsureClusterAabbsCapacity(PrimarySegmentCapacity);
        EnsureClusterSpatialIndexSlotCapacity(PrimarySegmentCapacity);

        // Reset the per-cell index before rebuilding so repeated calls to RebuildClusterAabbs (e.g. a startup reopen of a database that was reopened in the
        // same process) do not double-count clusters that already have entries in the index from a prior spawn/migration path.
        if (PerCellIndex != null)
        {
            Array.Clear(PerCellIndex);
        }
        Array.Fill(ClusterSpatialIndexSlot, -1);

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < ActiveClusterCount; i++)
            {
                int chunkId = ActiveClusterIds[i];
                ClusterSpatialAabb aabb = RecomputeClusterAabb(chunkId, ref clusterAccessor);
                ClusterAabbs[chunkId] = aabb;

                // Add to the per-cell index. The cell key was already written into ClusterCellMap by RebuildCellState. Skip clusters whose cell is unknown
                // (ClusterCellMap[chunkId] == -1) or whose AABB is degenerate (all entities were skipped).
                if (ClusterCellMap == null || chunkId >= ClusterCellMap.Length)
                {
                    continue;
                }
                int cellKey = ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }
                if (float.IsPositiveInfinity(aabb.MinX))
                {
                    continue; // empty — no valid entities
                }

                AddClusterToPerCellIndex(chunkId, cellKey, aabb);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Add a cluster to its cell's <see cref="PerCellSpatialSlot.DynamicIndex"/>, lazily allocating the <see cref="PerCellSpatialSlot"/> and
    /// <see cref="CellSpatialIndex"/> as needed. Records the back-pointer in <see cref="ClusterSpatialIndexSlot"/> for O(1) subsequent updates. Issue #230.
    /// </summary>
    internal void AddClusterToPerCellIndex(int clusterChunkId, int cellKey, in ClusterSpatialAabb aabb)
    {
        EnsurePerCellIndexCapacity(cellKey + 1);
        EnsureClusterSpatialIndexSlotCapacity(clusterChunkId + 1);

        var slot = PerCellIndex[cellKey];
        if (slot == null)
        {
            slot = new PerCellSpatialSlot();
            PerCellIndex[cellKey] = slot;
        }
        if (slot.DynamicIndex == null)
        {
            slot.DynamicIndex = new CellSpatialIndex();
        }
        int indexSlot = slot.DynamicIndex.Add(clusterChunkId, aabb);
        ClusterSpatialIndexSlot[clusterChunkId] = indexSlot;
    }

    /// <summary>
    /// Remove a cluster from its cell's <see cref="PerCellSpatialSlot.DynamicIndex"/>. Fixes up the back-pointer of any cluster that was swapped into the
    /// removed slot by the SoA swap-with-last. Clears <see cref="ClusterSpatialIndexSlot"/> for the removed cluster. Issue #230.
    /// </summary>
    internal void RemoveClusterFromPerCellIndex(int clusterChunkId, int cellKey)
    {
        if (PerCellIndex == null || cellKey < 0 || cellKey >= PerCellIndex.Length)
        {
            return;
        }
        var slot = PerCellIndex[cellKey];
        if (slot == null || slot.DynamicIndex == null)
        {
            return;
        }
        if (ClusterSpatialIndexSlot == null || clusterChunkId >= ClusterSpatialIndexSlot.Length)
        {
            return;
        }
        int indexSlot = ClusterSpatialIndexSlot[clusterChunkId];
        if (indexSlot < 0)
        {
            return; // not in the index
        }

        int swappedClusterId = slot.DynamicIndex.RemoveAt(indexSlot);
        if (swappedClusterId >= 0 && swappedClusterId < ClusterSpatialIndexSlot.Length)
        {
            // The swapped cluster now lives at indexSlot; fix its back-pointer.
            ClusterSpatialIndexSlot[swappedClusterId] = indexSlot;
        }
        ClusterSpatialIndexSlot[clusterChunkId] = -1;
    }

    /// <summary>
    /// Append a migration request to the per-archetype queue. Lazily allocates the backing array on first use
    /// and doubles its capacity on overflow. Issue #229 Phase 3.
    /// </summary>
    /// <remarks>
    /// Called only from the cell-crossing detection loop in <c>ProcessClusterSpatialEntries</c> — single-threaded,
    /// no synchronization needed. The typical hot path writes a handful of entries per tick; even on a busy tick
    /// with thousands of migrations the array doubles ~10-12 times total (initial 16 -> 32K).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnqueueMigration(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey)
    {
        if (PendingMigrations == null)
        {
            PendingMigrations = new MigrationRequest[16];
        }
        else if (PendingMigrationCount == PendingMigrations.Length)
        {
            Array.Resize(ref PendingMigrations, PendingMigrations.Length * 2);
        }
        PendingMigrations[PendingMigrationCount++] = new MigrationRequest(sourceClusterChunkId, sourceSlotIndex, destCellKey);
    }

    /// <summary>
    /// Allocate a new cluster from both segments (lockstep). Initializes to zero and adds to active list.
    /// </summary>
    public int AllocateNewCluster(ChangeSet changeSet)
    {
        int chunkId;
        if (ClusterSegment != null)
        {
            chunkId = ClusterSegment.AllocateChunk(true, changeSet);
        }
        else
        {
            // Pure-Transient: allocate from TransientStore only
            chunkId = TransientSegment.AllocateChunk(true);
        }

        // Dual-segment: allocate matching chunk in TransientSegment (lockstep ensures same chunk IDs)
        if (TransientSegment != null && ClusterSegment != null)
        {
            int transientChunkId = TransientSegment.AllocateChunk(true);
            Debug.Assert(transientChunkId == chunkId, $"Dual-segment chunk ID mismatch: PS={chunkId}, TS={transientChunkId}");
        }

        AddToActiveList(chunkId);
        return chunkId;
    }

    /// <summary>Add a cluster chunk ID to the active list.</summary>
    public void AddToActiveList(int chunkId)
    {
        if (ActiveClusterCount >= ActiveClusterIds.Length)
        {
            Array.Resize(ref ActiveClusterIds, ActiveClusterIds.Length * 2);
        }
        ActiveClusterIds[ActiveClusterCount++] = chunkId;
    }

    /// <summary>Remove a cluster chunk ID from the active list (swap-with-last, O(1)).</summary>
    public void RemoveFromActiveList(int chunkId)
    {
        for (int i = 0; i < ActiveClusterCount; i++)
        {
            if (ActiveClusterIds[i] == chunkId)
            {
                ActiveClusterIds[i] = ActiveClusterIds[ActiveClusterCount - 1];
                ActiveClusterCount--;

                // If the removed cluster was the free head, reset
                if (FreeClusterHead == chunkId)
                {
                    FreeClusterHead = -1;
                }

                return;
            }
        }
    }

    /// <summary>
    /// Release a slot in a cluster: clear OccupancyBit, EnabledBits, EntityKey on the primary segment.
    /// If the cluster becomes empty, free it from both segments.
    /// </summary>
    /// <param name="grid">
    /// Optional spatial grid. When non-null <em>and</em> <see cref="ClusterCellMap"/> is populated for the released cluster, this method maintains the cell
    /// descriptor: <c>EntityCount</c> always decrements, and a going-empty cluster is removed from its cell's pool segment.
    /// </param>
    public void ReleaseSlot(ref ChunkAccessor<PersistentStore> accessor, int clusterChunkId, int slotIndex, ChangeSet changeSet, SpatialGrid grid = null)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

        // Read occupancy BEFORE clearing the slot. This keeps cell.EntityCount correct on double-release (the slot's bit is already zero, so no decrement
        // should happen).
        // Phase 1+2 never releases the same slot twice, but the Phase 3 migration fence will batch-release slots and correctness here is worth 3 lines of insurance.
        ulong slotMask = 1UL << slotIndex;
        bool wasOccupied = (*(ulong*)clusterBase & slotMask) != 0;

        ClearSlotMetadata(clusterBase, slotIndex);

        if (wasOccupied)
        {
            DecrementCellEntityCountOnRelease(grid, clusterChunkId);
        }

        ref ulong occupancy = ref *(ulong*)clusterBase;
        if (BitOperations.PopCount(occupancy) == 0)
        {
            FinaliseEmptyClusterCellState(grid, clusterChunkId);
            RemoveFromActiveList(clusterChunkId);
            ClusterSegment.FreeChunk(clusterChunkId);
            TransientSegment?.FreeChunk(clusterChunkId);
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>
    /// Release a slot for pure-Transient archetypes (no PersistentStore segment).
    /// </summary>
    public void ReleaseSlot(ref ChunkAccessor<TransientStore> accessor, int clusterChunkId, int slotIndex, SpatialGrid grid = null)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

        // See comment in the PersistentStore overload — read occupancy before clearing.
        ulong slotMask = 1UL << slotIndex;
        bool wasOccupied = (*(ulong*)clusterBase & slotMask) != 0;

        ClearSlotMetadata(clusterBase, slotIndex);

        if (wasOccupied)
        {
            DecrementCellEntityCountOnRelease(grid, clusterChunkId);
        }

        ref ulong occupancy = ref *(ulong*)clusterBase;
        if (BitOperations.PopCount(occupancy) == 0)
        {
            FinaliseEmptyClusterCellState(grid, clusterChunkId);
            RemoveFromActiveList(clusterChunkId);
            TransientSegment.FreeChunk(clusterChunkId);
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>Decrement the cell's entity count when a slot is released. No-op if cluster is unmapped.</summary>
    private void DecrementCellEntityCountOnRelease(SpatialGrid grid, int clusterChunkId)
    {
        if (grid == null || ClusterCellMap == null || clusterChunkId >= ClusterCellMap.Length)
        {
            return;
        }
        int cellKey = ClusterCellMap[clusterChunkId];
        if (cellKey < 0)
        {
            return;
        }
        grid.GetCell(cellKey).EntityCount--;
    }

    /// <summary>Detach an empty cluster from its cell's pool and clear its cell mapping.</summary>
    private void FinaliseEmptyClusterCellState(SpatialGrid grid, int clusterChunkId)
    {
        if (grid == null || ClusterCellMap == null || clusterChunkId >= ClusterCellMap.Length)
        {
            return;
        }
        int cellKey = ClusterCellMap[clusterChunkId];
        if (cellKey < 0)
        {
            return;
        }
        // CellClusterPool.RemoveCluster decrements cell.ClusterCount for us.
        ref var cell = ref grid.GetCell(cellKey);
        grid.CellClusterPool.RemoveCluster(ref cell, clusterChunkId);

        // Issue #230 Phase 1: also remove from the per-cell cluster AABB index and reset the cluster's stored AABB. Runs before we clear ClusterCellMap so
        // RemoveClusterFromPerCellIndex can look up the cell key internally.
        RemoveClusterFromPerCellIndex(clusterChunkId, cellKey);
        if (ClusterAabbs != null && clusterChunkId < ClusterAabbs.Length)
        {
            ClusterAabbs[clusterChunkId] = ClusterSpatialAabb.Empty;
        }

        ClusterCellMap[clusterChunkId] = -1;
    }

    /// <summary>Clear EnabledBits, OccupancyBit, and EntityId for a slot (store-agnostic pointer math).</summary>
    private void ClearSlotMetadata(byte* clusterBase, int slotIndex)
    {
        ulong slotMask = 1UL << slotIndex;

        for (int slot = 0; slot < Layout.ComponentCount; slot++)
        {
            ref ulong enabledBits = ref *(ulong*)(clusterBase + Layout.EnabledBitsOffset(slot));
            enabledBits &= ~slotMask;
        }

        ref ulong occupancy = ref *(ulong*)clusterBase;
        occupancy &= ~slotMask;

        *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8) = 0;
    }

    /// <summary>
    /// Initialize per-archetype B+Tree index infrastructure from the component tables.
    /// Called after cluster state creation for archetypes with <see cref="ArchetypeMetadata.HasClusterIndexes"/>.
    /// </summary>
    public void InitializeIndexes(ComponentTable[] slotToTable, ChunkBasedSegment<PersistentStore> indexSegment, bool load, ChangeSet changeSet)
    {
        IndexSegment = indexSegment;

        int slotCount = 0;
        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            // Skip Transient slots — their indexes use per-ComponentTable TransientIndex (BTree<TransientStore>)
            if (slotToTable[slot].StorageMode == StorageMode.Transient)
            {
                continue;
            }
            var infos = slotToTable[slot].IndexedFieldInfos;
            if (infos != null && infos.Length > 0)
            {
                slotCount++;
            }
        }

        IndexSlots = new ClusterIndexSlot[slotCount];
        int idx = 0;
        // Sequential counter for AllowMultiple indexed fields across ALL component slots in this archetype.
        // Drives each field's MultiFieldIndex, which selects the corresponding section in the cluster layout's elementId tail
        // (see ArchetypeClusterInfo.IndexElementIdOffset). Must match the flat count passed to ArchetypeClusterInfo.Compute at archetype registration time.
        int multiFieldCounter = 0;
        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            // Skip Transient slots — indexes maintained per-ComponentTable, not per-archetype
            if (table.StorageMode == StorageMode.Transient)
            {
                continue;
            }
            var infos = table.IndexedFieldInfos;
            if (infos == null || infos.Length == 0)
            {
                continue;
            }

            var fields = new ClusterIndexField[infos.Length];
            var shadowBuffers = new FieldShadowBuffer[infos.Length];

            // Iterate component definition fields to find indexed ones (in stable order matching IndexedFieldInfos)
            int fi = 0;
            for (int i = 0; i < table.Definition.MaxFieldId && fi < infos.Length; i++)
            {
                var fieldDef = table.Definition[i];
                if (fieldDef == null || !fieldDef.HasIndex)
                {
                    continue;
                }

                ref var ifi = ref infos[fi];
                // FieldOffset in cluster = field offset within pure component data (no ComponentOverhead in clusters)
                int clusterFieldOffset = ifi.OffsetToField - table.ComponentOverhead;
                var btree = ComponentTable.CreateIndexForFieldCore(fieldDef, (short)fieldDef.FieldId, load, indexSegment, changeSet);
                // AllowMultiple fields claim the next sequential slot in the cluster's elementId tail.
                // Single-value fields don't allocate tail space and use MultiFieldIndex = -1.
                int multiFieldIndex = ifi.AllowMultiple ? multiFieldCounter++ : -1;
                fields[fi] = new ClusterIndexField
                {
                    FieldOffset = clusterFieldOffset,
                    FieldSize = ifi.Size,
                    Index = btree,
                    AllowMultiple = ifi.AllowMultiple,
                    ZoneMap = new ZoneMapArray(PrimarySegmentCapacity, ifi.Size,
                        isFloat: fieldDef.Type == FieldType.Float, isDouble: fieldDef.Type == FieldType.Double,
                        isUnsigned: (fieldDef.Type & FieldType.Unsigned) != 0),
                    MultiFieldIndex = multiFieldIndex,
                };
                shadowBuffers[fi] = new FieldShadowBuffer();
                fi++;
            }

            IndexSlots[idx++] = new ClusterIndexSlot
            {
                Slot = slot,
                Fields = fields,
                ShadowBuffers = shadowBuffers,
            };
        }

        // Sanity: the MultiFieldIndex counter must match the count supplied to ArchetypeClusterInfo.Compute.
        // A mismatch means the cluster layout tail is mis-sized or fields will read the wrong slots.
        Debug.Assert(multiFieldCounter == Layout.MultipleIndexedFieldCount,
            $"Cluster elementId tail: InitializeIndexes counted {multiFieldCounter} AllowMultiple fields but Layout reserves {Layout.MultipleIndexedFieldCount}");

        ClusterShadowBitmap = new DirtyBitmap(Math.Max(64, PrimarySegmentCapacity * 64));
    }

    /// <summary>
    /// Rebuild per-archetype B+Tree indexes from cluster data (scan all occupied entities).
    /// Used on reopen when index segment is not persisted or is corrupted.
    /// </summary>
    public void RebuildIndexesFromData(ChangeSet changeSet)
    {
        if (IndexSlots == null || IndexSlots.Length == 0)
        {
            return;
        }

        // Index rebuild reads from primary segment (SV/V data — Transient excluded from IndexSlots)
        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var idxAccessor = IndexSegment.CreateChunkAccessor(changeSet);
        try
        {
            for (int c = 0; c < ActiveClusterCount; c++)
            {
                int chunkId = ActiveClusterIds[c];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                ulong occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    int clusterLocation = chunkId * 64 + slotIndex;

                    for (int s = 0; s < IndexSlots.Length; s++)
                    {
                        ref var ixSlot = ref IndexSlots[s];
                        byte* compBase = clusterBase + Layout.ComponentOffset(ixSlot.Slot);
                        int compSize = Layout.ComponentSize(ixSlot.Slot);
                        for (int f = 0; f < ixSlot.Fields.Length; f++)
                        {
                            ref var field = ref ixSlot.Fields[f];
                            byte* fieldPtr = compBase + slotIndex * compSize + field.FieldOffset;
                            int elementId = field.Index.Add(fieldPtr, clusterLocation, ref idxAccessor);
                            // Rebuild writes a fresh elementId into the cluster tail, overwriting any stale
                            // value from the previous (torn-down) BTree state. Issue #229 Phase 3.
                            if (field.AllowMultiple)
                            {
                                *(int*)(clusterBase + Layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex)) = elementId;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            idxAccessor.Dispose();
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Initialize per-archetype spatial R-Tree infrastructure from the component tables.
    /// Called after cluster state creation for archetypes with <see cref="ArchetypeMetadata.HasClusterSpatial"/>.
    /// </summary>
    public void InitializeSpatial(ComponentTable[] slotToTable, ChunkBasedSegment<PersistentStore> treeSeg, ChunkBasedSegment<PersistentStore> bpSeg, bool load)
    {
        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table.SpatialIndex == null)
            {
                continue;
            }

            var tableFi = table.SpatialIndex.FieldInfo;
            // FieldOffset in cluster = field offset within pure component data (no ComponentOverhead in clusters)
            int clusterFieldOffset = tableFi.FieldOffset - table.ComponentOverhead;
            var variant = tableFi.ToVariant();
            var descriptor = load ? SpatialNodeDescriptor.FromVariant(variant, treeSeg.Stride) : SpatialNodeDescriptor.ForVariant(variant);
            var tree = new SpatialRTree<PersistentStore>(treeSeg, variant, load);
            tree.BackPointerSegment = bpSeg;

            // Create a modified SpatialFieldInfo with cluster-relative offset
            var fi = new SpatialFieldInfo(clusterFieldOffset, tableFi.FieldSize, tableFi.FieldType, tableFi.Margin, tableFi.CellSize, tableFi.Mode);

            SpatialSlot = new ClusterSpatialSlot
            {
                Slot = slot,
                FieldOffset = clusterFieldOffset,
                FieldInfo = fi,
                Descriptor = descriptor,
                Tree = tree,
                BackPointerSegment = bpSeg,
                DirtyRing = new DirtyBitmapRing(Math.Max(4, ClusterSegment.ChunkCapacity)),
            };
            break; // Only one spatial field per archetype
        }
    }

    /// <summary>
    /// Rebuild per-archetype spatial R-Tree from cluster data (scan all occupied entities).
    /// Used on reopen when spatial segment is not persisted or is corrupted.
    /// </summary>
    public void RebuildSpatialFromData(ChangeSet changeSet)
    {
        if (SpatialSlot.Tree == null)
        {
            return;
        }

        ref var ss = ref SpatialSlot;
        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var treeAccessor = ss.Tree.Segment.CreateChunkAccessor(changeSet);
        var bpAccessor = ss.BackPointerSegment.CreateChunkAccessor(changeSet);
        try
        {
            int compSlot = ss.Slot;
            int compSize = Layout.ComponentSize(compSlot);
            int compOffset = Layout.ComponentOffset(compSlot);

            for (int c = 0; c < ActiveClusterCount; c++)
            {
                int chunkId = ActiveClusterIds[c];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                ulong occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    int clusterLocation = chunkId * 64 + slotIndex;
                    long entityPK = *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8);

                    byte* fieldPtr = clusterBase + compOffset + slotIndex * compSize + ss.FieldOffset;
                    SpatialMaintainer.InsertSpatialCluster(entityPK, clusterLocation, fieldPtr,
                        ref ss, ref treeAccessor, ref bpAccessor, changeSet);
                }
            }
        }
        finally
        {
            bpAccessor.Dispose();
            treeAccessor.Dispose();
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Rebuild Versioned component HEAD values in cluster slots from revision chains.
    /// Called on database reopen when the cluster slot WAL might be stale (crash between commit and tick fence).
    /// For each occupied entity, walks the revision chain to find the HEAD and copies its value to the cluster slot.
    /// </summary>
    public void RebuildVersionedHeadFromChain(ArchetypeMetadata meta, ArchetypeEngineState engineState, ChangeSet changeSet)
    {
        if (meta.VersionedSlotMask == 0)
        {
            return;
        }

        // Invariant: VersionedSlotMask != 0 implies ArchetypeClusterInfo.Compute allocated a non-null
        // SlotToVersionedIndex array (see ArchetypeClusterInfo.cs — the array is only allocated when
        // versionedSlotMask != 0). Cache the reference in a local so the null check is expressed once
        // at the top of the method instead of at every indexing site, and the compiler's nullability
        // analysis sees a non-null local for the rest of the body.
        var slotToVi = Layout.SlotToVersionedIndex;
        if (slotToVi == null)
        {
            return;
        }

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var mapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();
        int recordSize = meta._entityRecordSize;
        byte* recordBuf = stackalloc byte[recordSize];

        // Pre-create accessors for each Versioned slot's tables (hoisted out of entity/slot loops)
        var compRevAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        var contentAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (slotToVi[slot] >= 0)
            {
                var table = engineState.SlotToComponentTable[slot];
                compRevAccessors[slot] = table.CompRevTableSegment.CreateChunkAccessor();
                contentAccessors[slot] = table.ComponentSegment.CreateChunkAccessor();
            }
        }

        try
        {
            for (int c = 0; c < ActiveClusterCount; c++)
            {
                int chunkId = ActiveClusterIds[c];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId, true);
                ulong occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    // Read entity key from cluster
                    long entityPK = *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8);
                    long entityKey = EntityId.FromRaw(entityPK).EntityKey;

                    // Read ClusterEntityRecord from EntityMap to get compRevFirstChunkId
                    if (!engineState.EntityMap.TryGet(entityKey, recordBuf, ref mapAccessor))
                    {
                        continue;
                    }

                    // For each Versioned slot: walk chain → find HEAD → copy to cluster slot
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int vi = slotToVi[slot];
                        if (vi < 0)
                        {
                            continue;
                        }

                        int compRevFirstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, vi);
                        if (compRevFirstChunkId == 0)
                        {
                            continue;
                        }

                        // Walk chain to find HEAD (latest committed entry)
                        ref var compRevAccessor = ref compRevAccessors[slot];
                        var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, long.MaxValue);
                        if (chainResult.IsFailure)
                        {
                            continue;
                        }

                        // Read HEAD value from content chunk and copy to cluster slot
                        int headChunkId = chainResult.Value.CurCompContentChunkId;
                        ref var contentAccessor = ref contentAccessors[slot];
                        byte* srcAddr = contentAccessor.GetChunkAddress(headChunkId);
                        int compSize = Layout.ComponentSize(slot);
                        byte* dstSlot = clusterBase + Layout.ComponentOffset(slot) + slotIndex * compSize;
                        Unsafe.CopyBlockUnaligned(dstSlot, srcAddr + engineState.SlotToComponentTable[slot].ComponentOverhead, (uint)compSize);
                    }
                }
            }
        }
        finally
        {
            // Dispose all hoisted accessors
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (slotToVi[slot] >= 0)
                {
                    compRevAccessors[slot].Dispose();
                    contentAccessors[slot].Dispose();
                }
            }

            mapAccessor.Dispose();
            clusterAccessor.Dispose();
        }
    }
}

/// <summary>
/// Per-component-slot index state for a cluster-eligible archetype. One per component slot that has indexed fields.
/// </summary>
internal struct ClusterIndexSlot
{
    /// <summary>Component slot index within the archetype.</summary>
    public int Slot;

    /// <summary>Per-indexed-field B+Tree instances (per-archetype ownership).</summary>
    public ClusterIndexField[] Fields;

    /// <summary>Per-indexed-field shadow buffers for old value capture before mutation.</summary>
    public FieldShadowBuffer[] ShadowBuffers;
}

/// <summary>
/// Per-archetype spatial R-Tree state for a cluster-eligible archetype with a <c>[SpatialIndex]</c> field.
/// </summary>
internal struct ClusterSpatialSlot
{
    /// <summary>Component slot index that has the spatial field.</summary>
    public int Slot;

    /// <summary>Byte offset of spatial field within cluster component SoA (no ComponentOverhead).</summary>
    public int FieldOffset;

    /// <summary>Spatial field metadata (margin, mode, field type).</summary>
    public SpatialFieldInfo FieldInfo;

    /// <summary>Node layout descriptor.</summary>
    public SpatialNodeDescriptor Descriptor;

    /// <summary>Per-archetype R-Tree (Dynamic mode). Value stored in leaf = ClusterLocation.</summary>
    public SpatialRTree<PersistentStore> Tree;

    /// <summary>Per-archetype back-pointer CBS keyed by ClusterLocation (clusterChunkId * 64 + slotIndex).</summary>
    public ChunkBasedSegment<PersistentStore> BackPointerSegment;

    /// <summary>Per-archetype DirtyBitmapRing for interest management delta queries.</summary>
    public DirtyBitmapRing DirtyRing;
}

/// <summary>
/// Per-indexed-field B+Tree state within a cluster-eligible archetype.
/// </summary>
internal struct ClusterIndexField
{
    /// <summary>Byte offset of this field within the pure component data (no ComponentOverhead — clusters have no overhead).</summary>
    public int FieldOffset;

    /// <summary>Field size in bytes.</summary>
    public int FieldSize;

    /// <summary>Per-archetype B+Tree instance. Value = ClusterLocation (clusterChunkId * 64 + slotIndex).</summary>
    public BTreeBase<PersistentStore> Index;

    /// <summary>Whether index allows multiple values per key.</summary>
    public bool AllowMultiple;

    /// <summary>Zone map for cluster-level query pruning. Non-null for numeric field types.</summary>
    public ZoneMapArray ZoneMap;

    /// <summary>
    /// Sequential index into the cluster's elementId tail section (0..<see cref="ArchetypeClusterInfo.MultipleIndexedFieldCount"/>-1),
    /// or <c>-1</c> when <see cref="AllowMultiple"/> is false (no tail section allocated for this field).
    /// Used by the cluster destroy/migrate path to locate the per-entity elementId via
    /// <see cref="ArchetypeClusterInfo.IndexElementIdOffset"/> and pass it to
    /// <see cref="BTreeBase{TStore}.RemoveValue"/>, so that only this entity's specific
    /// <c>(key, clusterLocation)</c> entry is removed — not the entire buffer at the key.
    /// </summary>
    public int MultiFieldIndex;
}

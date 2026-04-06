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

    /// <summary>Per-entity dirty tracking for tick fence WAL serialization. Index = clusterChunkId * 64 + slotIndex.</summary>
    public DirtyBitmap ClusterDirtyBitmap;

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
        ChunkBasedSegment<TransientStore> transientSegment = default, TransientStore? transientStore = null)
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
        ChunkBasedSegment<TransientStore> transientSegment = default, TransientStore? transientStore = null)
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
    public void ReleaseSlot(ref ChunkAccessor<PersistentStore> accessor, int clusterChunkId, int slotIndex, ChangeSet changeSet)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);
        ClearSlotMetadata(clusterBase, clusterChunkId, slotIndex);

        ref ulong occupancy = ref *(ulong*)clusterBase;
        if (BitOperations.PopCount(occupancy) == 0)
        {
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
    public void ReleaseSlot(ref ChunkAccessor<TransientStore> accessor, int clusterChunkId, int slotIndex)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);
        ClearSlotMetadata(clusterBase, clusterChunkId, slotIndex);

        ref ulong occupancy = ref *(ulong*)clusterBase;
        if (BitOperations.PopCount(occupancy) == 0)
        {
            RemoveFromActiveList(clusterChunkId);
            TransientSegment.FreeChunk(clusterChunkId);
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>Clear EnabledBits, OccupancyBit, and EntityId for a slot (store-agnostic pointer math).</summary>
    private void ClearSlotMetadata(byte* clusterBase, int clusterChunkId, int slotIndex)
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
                fields[fi] = new ClusterIndexField
                {
                    FieldOffset = clusterFieldOffset,
                    FieldSize = ifi.Size,
                    Index = btree,
                    AllowMultiple = ifi.AllowMultiple,
                    ZoneMap = new ZoneMapArray(PrimarySegmentCapacity, ifi.Size,
                        isFloat: fieldDef.Type == FieldType.Float, isDouble: fieldDef.Type == FieldType.Double,
                        isUnsigned: (fieldDef.Type & FieldType.Unsigned) != 0),
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
                            field.Index.Add(fieldPtr, clusterLocation, ref idxAccessor);
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
    public void InitializeSpatial(ComponentTable[] slotToTable, ChunkBasedSegment<PersistentStore> treeSeg, ChunkBasedSegment<PersistentStore> bpSeg, bool load, 
        ChangeSet changeSet)
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

            // Occupancy map (Layer 1) if CellSize > 0
            PagedHashMap<long, int, PersistentStore> occupancyMap = null;
            // For now skip occupancy map for cluster spatial — the R-Tree is the primary query path

            SpatialSlot = new ClusterSpatialSlot
            {
                Slot = slot,
                FieldOffset = clusterFieldOffset,
                FieldInfo = fi,
                Descriptor = descriptor,
                Tree = tree,
                BackPointerSegment = bpSeg,
                OccupancyMap = occupancyMap,
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

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var mapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();
        int recordSize = meta._entityRecordSize;
        byte* recordBuf = stackalloc byte[recordSize];

        // Pre-create accessors for each Versioned slot's tables (hoisted out of entity/slot loops)
        var compRevAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        var contentAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (Layout.SlotToVersionedIndex != null && Layout.SlotToVersionedIndex[slot] >= 0)
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
                        int vi = Layout.SlotToVersionedIndex[slot];
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
                if (Layout.SlotToVersionedIndex != null && Layout.SlotToVersionedIndex[slot] >= 0)
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

    /// <summary>Per-archetype occupancy map (null if CellSize == 0).</summary>
    public PagedHashMap<long, int, PersistentStore> OccupancyMap;

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
}

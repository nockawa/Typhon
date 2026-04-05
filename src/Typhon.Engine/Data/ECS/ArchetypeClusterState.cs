using System;
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
    /// <summary>ChunkBasedSegment backing cluster data. Stride = cluster total size.</summary>
    public ChunkBasedSegment<PersistentStore> ClusterSegment;

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

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype B+Tree indexes (Phase 3a). Null if archetype has no indexed fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype B+Tree index slots, one per component slot with indexed fields. Null if no indexed fields.</summary>
    public ClusterIndexSlot[] IndexSlots;

    /// <summary>Shadow guard bitmap. Guards first-write-per-tick shadow capture. Same index semantics as <see cref="ClusterDirtyBitmap"/>.</summary>
    public DirtyBitmap ClusterShadowBitmap;

    /// <summary>Shared <see cref="ChunkBasedSegment{TStore}"/> backing all per-archetype B+Trees for this archetype.</summary>
    public ChunkBasedSegment<PersistentStore> IndexSegment;

    private ArchetypeClusterState() { }

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
    public static ArchetypeClusterState Create(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment) =>
        new()
        {
            ClusterSegment = segment,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, segment.ChunkCapacity * 64)),
        };

    /// <summary>
    /// Create an ArchetypeClusterState from an existing persisted segment (database reopen).
    /// Scans cluster occupancy bitmaps to rebuild <see cref="ActiveClusterIds"/> and <see cref="FreeClusterHead"/>.
    /// </summary>
    public static ArchetypeClusterState CreateFromExisting(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment)
    {
        var state = new ArchetypeClusterState
        {
            ClusterSegment = segment,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, segment.ChunkCapacity * 64)),
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

        var accessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            int capacity = ClusterSegment.ChunkCapacity;
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
                    continue; // Empty cluster — shouldn't exist normally, skip defensively
                }

                AddToActiveList(chunkId);

                // If cluster has free slots, set as free head (first-fit)
                if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
                {
                    FreeClusterHead = chunkId;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Claim a free slot in an existing cluster, or allocate a new cluster.
    /// Returns the cluster chunk ID and the slot index within the cluster.
    /// </summary>
    /// <remarks>
    /// <para>Uses CAS on OccupancyBits for correctness under future concurrent commit scenarios.
    /// In Phase 1, FinalizeSpawns is single-writer, so CAS always succeeds on first try.</para>
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

                // CAS for future-proof concurrent commit. Single-writer in Phase 1.
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
                    occupancy = desired; // Direct write — single-writer in Phase 1
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
    /// Allocate a new cluster from the segment. Initializes to zero and adds to active list.
    /// </summary>
    public int AllocateNewCluster(ChangeSet changeSet)
    {
        int chunkId = ClusterSegment.AllocateChunk(true, changeSet);
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
    /// Release a slot in a cluster: clear OccupancyBit, EnabledBits, EntityKey.
    /// If the cluster becomes empty, free it to the segment.
    /// </summary>
    public void ReleaseSlot(ref ChunkAccessor<PersistentStore> accessor, int clusterChunkId, int slotIndex, ChangeSet changeSet)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);
        ulong slotMask = 1UL << slotIndex;

        // Clear EnabledBits for all component slots
        for (int slot = 0; slot < Layout.ComponentCount; slot++)
        {
            ref ulong enabledBits = ref *(ulong*)(clusterBase + Layout.EnabledBitsOffset(slot));
            enabledBits &= ~slotMask;
        }

        // Clear OccupancyBit
        ref ulong occupancy = ref *(ulong*)clusterBase;
        occupancy &= ~slotMask;

        // Clear EntityKey
        *(long*)(clusterBase + Layout.EntityKeysOffset + slotIndex * 8) = 0;

        // If cluster now empty, free it
        if (BitOperations.PopCount(occupancy) == 0)
        {
            RemoveFromActiveList(clusterChunkId);
            ClusterSegment.FreeChunk(clusterChunkId);
        }
        else
        {
            // Cluster has free space — make it the free head if nothing better
            if (FreeClusterHead < 0)
            {
                FreeClusterHead = clusterChunkId;
            }
        }
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
                    ZoneMap = new ZoneMapArray(ClusterSegment.ChunkCapacity, ifi.Size,
                        isFloat: fieldDef.Type == FieldType.Float, isDouble: fieldDef.Type == FieldType.Double),
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

        ClusterShadowBitmap = new DirtyBitmap(Math.Max(64, ClusterSegment.ChunkCapacity * 64));
    }

    /// <summary>
    /// Rebuild per-archetype B+Tree indexes from cluster data (scan all occupied entities).
    /// Used on reopen when index segment is not persisted or is corrupted.
    /// </summary>
    public void RebuildIndexesFromData(ChangeSet changeSet)
    {
        if (IndexSlots == null)
        {
            return;
        }

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

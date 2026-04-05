using System;
using System.Numerics;
using System.Threading;

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

    private ArchetypeClusterState() { }

    /// <summary>
    /// Create a new ArchetypeClusterState for a cluster-eligible archetype.
    /// </summary>
    public static ArchetypeClusterState Create(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment) =>
        new()
        {
            ClusterSegment = segment,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
        };

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

    /// <summary>Find the next active cluster with free slots after the given cluster, or -1 if none.</summary>
    private int FindNextFreeCluster(ref ChunkAccessor<PersistentStore> accessor, int afterChunkId)
    {
        for (int i = 0; i < ActiveClusterCount; i++)
        {
            int id = ActiveClusterIds[i];
            if (id == afterChunkId)
            {
                continue;
            }
            byte* clusterBase = accessor.GetChunkAddress(id, false);
            ulong occupancy = *(ulong*)clusterBase;
            if ((~occupancy & Layout.FullMask) != 0)
            {
                return id;
            }
        }
        return -1;
    }
}

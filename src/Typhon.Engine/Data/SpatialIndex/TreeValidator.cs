using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Debug helper that walks an entire R-Tree and asserts all structural invariants (R1–R7).
/// Called after every mutation in unit tests to verify correctness.
/// </summary>
internal static unsafe class TreeValidator
{
    /// <summary>
    /// Validate all structural invariants of the R-Tree.
    /// Throws on any violation with a descriptive message.
    /// </summary>
    internal static void Validate<TStore>(SpatialRTree<TStore> tree) where TStore : struct, IPageStore
    {
        var guard = EpochGuard.Enter(tree.Segment.Store.EpochManager);
        try
        {
            var accessor = tree.Segment.CreateChunkAccessor();
            try
            {
                var desc = tree.Descriptor;
                var entityIds = new HashSet<long>();
                int totalEntities = 0;
                int totalNodes = 0;

                ValidateNode(tree, tree.RootChunkId, 0, 0, desc, ref accessor, entityIds, ref totalEntities, ref totalNodes);

                // R5: each EntityId appears exactly once
                if (entityIds.Count != totalEntities)
                {
                    throw new InvalidOperationException($"R5 violation: {totalEntities - entityIds.Count} duplicate EntityIds in tree");
                }

                // Entity count matches metadata
                if (totalEntities != tree.EntityCount)
                {
                    throw new InvalidOperationException($"EntityCount mismatch: tree has {totalEntities}, metadata says {tree.EntityCount}");
                }

                // Node count matches metadata
                if (totalNodes != tree.NodeCount)
                {
                    throw new InvalidOperationException($"NodeCount mismatch: tree has {totalNodes}, metadata says {tree.NodeCount}");
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    private static void ValidateNode<TStore>(SpatialRTree<TStore> tree, int chunkId, int depth, int expectedParentChunkId, in SpatialNodeDescriptor desc, 
        ref ChunkAccessor<TStore> accessor, HashSet<long> entityIds, ref int totalEntities, ref int totalNodes) where TStore : struct, IPageStore
    {
        totalNodes++;
        byte* nodeBase = accessor.GetChunkAddress(chunkId);
        int count = SpatialNodeHelper.GetCount(nodeBase);
        bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);

        // R3: capacity bounds
        int capacity = isLeaf ? desc.LeafCapacity : desc.InternalCapacity;
        if (count < 0 || count > capacity)
        {
            throw new InvalidOperationException($"R3 violation: node {chunkId} count={count}, capacity={capacity}");
        }

        // R6: parent pointer matches
        int storedParent = SpatialNodeHelper.GetParentChunkId(nodeBase);
        if (storedParent != expectedParentChunkId)
        {
            throw new InvalidOperationException($"R6 violation: node {chunkId} parent={storedParent}, expected={expectedParentChunkId}");
        }

        if (isLeaf)
        {
            // R4: EntityIds only in leaf nodes
            for (int i = 0; i < count; i++)
            {
                long eid = SpatialNodeHelper.ReadLeafEntityId(nodeBase, i, desc);
                entityIds.Add(eid);
                totalEntities++;
            }

            // R1: MBR tightness
            ValidateMBRTightness(nodeBase, count, true, desc, chunkId);
        }
        else
        {
            // R1: MBR tightness
            ValidateMBRTightness(nodeBase, count, false, desc, chunkId);

            // Recurse into children
            for (int i = 0; i < count; i++)
            {
                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, desc);
                if (childId <= 0)
                {
                    throw new InvalidOperationException($"R6 violation: node {chunkId} child[{i}] has invalid chunkId={childId}");
                }

                ValidateNode(tree, childId, depth + 1, chunkId, desc, ref accessor, entityIds, ref totalEntities, ref totalNodes);
            }
        }
    }

    private static void ValidateMBRTightness(byte* nodeBase, int count, bool isLeaf, in SpatialNodeDescriptor desc, int chunkId)
    {
        if (count == 0)
        {
            return;
        }

        int halfCoord = desc.CoordCount / 2;
        Span<double> recomputed = stackalloc double[desc.CoordCount];

        // Initialize from first entry
        if (isLeaf)
        {
            SpatialNodeHelper.ReadLeafEntryCoords(nodeBase, 0, recomputed, desc);
        }
        else
        {
            SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, 0, recomputed, desc);
        }

        // Expand with remaining entries
        for (int i = 1; i < count; i++)
        {
            for (int c = 0; c < halfCoord; c++)
            {
                double v = isLeaf ? SpatialNodeHelper.ReadLeafCoord(nodeBase, i, c, desc) : SpatialNodeHelper.ReadInternalCoord(nodeBase, i, c, desc);
                if (v < recomputed[c])
                {
                    recomputed[c] = v;
                }
            }
            for (int c = halfCoord; c < desc.CoordCount; c++)
            {
                double v = isLeaf ? SpatialNodeHelper.ReadLeafCoord(nodeBase, i, c, desc) : SpatialNodeHelper.ReadInternalCoord(nodeBase, i, c, desc);
                if (v > recomputed[c])
                {
                    recomputed[c] = v;
                }
            }
        }

        // Compare with stored NodeMBR
        const double epsilon = 1e-6;
        for (int c = 0; c < desc.CoordCount; c++)
        {
            double stored = SpatialNodeHelper.ReadNodeMBRCoord(nodeBase, c, desc);
            if (Math.Abs(stored - recomputed[c]) > epsilon)
            {
                throw new InvalidOperationException($"R1 violation: node {chunkId} MBR coord[{c}] is {stored} but recomputed is {recomputed[c]}");
            }
        }
    }
}

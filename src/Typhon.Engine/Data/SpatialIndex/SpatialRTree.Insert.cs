using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Insert an entity with its fat AABB coordinates into the tree.
    /// </summary>
    /// <param name="entityId">Raw EntityId value (64-bit)</param>
    /// <param name="componentChunkId">Component CBS chunk ID for back-pointer storage (0 for standalone tests)</param>
    /// <param name="coords">CoordCount doubles ordered [min0, min1, ..., max0, max1, ...]</param>
    /// <param name="accessor">ChunkAccessor for page access</param>
    /// <param name="changeSet">ChangeSet for WAL participation</param>
    /// <returns>(leafChunkId, slotIndex) for back-pointer storage.</returns>
    internal (int leafChunkId, int slotIndex) Insert(long entityId, int componentChunkId, ReadOnlySpan<double> coords, ref ChunkAccessor<TStore> accessor,
        ChangeSet changeSet = null)
    {
        while (true)
        {
            var result = TryInsert(entityId, componentChunkId, coords, ref accessor, changeSet);
            if (result.success)
            {
                return (result.leafChunkId, result.slotIndex);
            }
            // OLC restart — spin briefly then retry descent
        }
    }

    /// <summary>Backward-compatible overload for standalone tree tests (no back-pointer tracking).</summary>
    internal (int leafChunkId, int slotIndex) Insert(long entityId, ReadOnlySpan<double> coords, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet = null)
        => Insert(entityId, 0, coords, ref accessor, changeSet);

    private (bool success, int leafChunkId, int slotIndex) TryInsert(long entityId, int componentChunkId, ReadOnlySpan<double> coords,
        ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        DescentPath path = default;
        int nodeChunkId = _rootChunkId;

        // ── Descent to best leaf ──
        while (true)
        {
            byte* nodeBase = accessor.GetChunkAddress(nodeChunkId);
            if (SpatialNodeHelper.IsLeaf(nodeBase))
            {
                break;
            }

            var latch = GetLatch(nodeBase);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return default; // locked/obsolete → restart
            }

            int count = SpatialNodeHelper.GetCount(nodeBase);
            int bestChild = ChooseBestChild(nodeBase, coords, count);
            int childChunkId = SpatialNodeHelper.ReadInternalChildId(nodeBase, bestChild, _desc);

            if (!latch.ValidateVersion(version))
            {
                return default; // concurrent modification → restart
            }

            path.Push(nodeChunkId, bestChild, version);
            nodeChunkId = childChunkId;
        }

        // ── Insert into leaf ──
        byte* leafBase = accessor.GetChunkAddress(nodeChunkId, dirty: true);
        SpinWriteLock(leafBase, out var leafLatch);

        int leafCount = SpatialNodeHelper.GetCount(leafBase);

        if (leafCount < _desc.LeafCapacity)
        {
            // Room available: append at leafCount position
            WriteLeafEntry(leafBase, leafCount, entityId, componentChunkId, coords);
            SpatialNodeHelper.SetCount(leafBase, leafCount + 1);
            SpatialNodeHelper.RefitLeafMBR(leafBase, _desc);
            leafLatch.WriteUnlock();

            _entityCount++;
            RefitAncestors(ref path, ref accessor);
            SyncMetadata(ref accessor);
            return (true, nodeChunkId, leafCount);
        }

        // Leaf full: need split
        leafLatch.WriteUnlock();
        return InsertWithSplit(entityId, componentChunkId, coords, nodeChunkId, ref path, ref accessor, changeSet);
    }

    /// <summary>
    /// Find the child whose MBR requires minimum enlargement to include the given coords.
    /// Tie-break: prefer child with smallest existing area/volume.
    /// </summary>
    private int ChooseBestChild(byte* nodeBase, ReadOnlySpan<double> coords, int count)
    {
        int halfCoord = _desc.CoordCount / 2;
        int bestChild = 0;
        double bestEnlargement = double.MaxValue;
        double bestArea = double.MaxValue;

        for (int i = 0; i < count; i++)
        {
            double area = 1.0;
            double enlargedArea = 1.0;

            for (int d = 0; d < halfCoord; d++)
            {
                double cMin = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, d, _desc);
                double cMax = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, d + halfCoord, _desc);
                double eMin = Math.Min(cMin, coords[d]);
                double eMax = Math.Max(cMax, coords[d + halfCoord]);
                area *= (cMax - cMin);
                enlargedArea *= (eMax - eMin);
            }

            double enlargement = enlargedArea - area;
            if (enlargement < bestEnlargement || (enlargement == bestEnlargement && area < bestArea))
            {
                bestChild = i;
                bestEnlargement = enlargement;
                bestArea = area;
            }
        }

        return bestChild;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteLeafEntry(byte* nodeBase, int index, long entityId, int componentChunkId, ReadOnlySpan<double> coords)
    {
        SpatialNodeHelper.WriteLeafEntryCoords(nodeBase, index, coords, _desc);
        SpatialNodeHelper.WriteLeafEntityId(nodeBase, index, entityId, _desc);
        SpatialNodeHelper.WriteLeafCompChunkId(nodeBase, index, componentChunkId, _desc);
    }

    private void WriteInternalEntry(byte* nodeBase, int index, int childChunkId, ref ChunkAccessor<TStore> accessor)
    {
        byte* childBase = accessor.GetChunkAddress(childChunkId);
        for (int c = 0; c < _desc.CoordCount; c++)
        {
            SpatialNodeHelper.WriteInternalCoord(nodeBase, index, c, SpatialNodeHelper.ReadNodeMBRCoord(childBase, c, _desc), _desc);
        }
        SpatialNodeHelper.WriteInternalChildId(nodeBase, index, childChunkId, _desc);
    }
}

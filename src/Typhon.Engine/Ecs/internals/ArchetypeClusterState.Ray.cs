using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Cluster-level ray query — <c>AC-9.6</c> of #872 step 9.
/// </summary>
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>
    /// Entities whose tight AABB the ray segment enters, written to <paramref name="results"/> in front-to-back order. Returns how many were written.
    /// </summary>
    /// <remarks>
    /// <para><b>The tree's own ray query is reused, not reimplemented</b> — §4.1 lists ray and frustum under "reuse as-is", and they are already oracle-tested
    /// against brute force at the entity level. What this adds is the cell walk and the frame conversion.</para>
    /// <para><b>The frame conversion is exact.</b> Cluster bounds are <c>C15</c> cell-relative, so the ray has to be expressed in each cell's frame before the
    /// tree sees it — and a change of frame here is a pure TRANSLATION, so the origin shifts by the cell origin and the direction is untouched. Distances
    /// along the ray are therefore unchanged too, which is what lets results from different cells be merged on <c>t</c> without rescaling.</para>
    /// <para><b>Cells come from the segment's bounding box.</b> Every cell the ray passes through overlaps that box, so the walk is complete; some cells it
    /// visits will not be touched by the ray, and their clusters simply fail the slab test. A DDA traversal would visit fewer, and is the obvious follow-up if
    /// this ever shows up in a profile — but a broadphase that is merely wasteful is a different class of thing from one that is wrong.</para>
    /// </remarks>
    public int QueryRay(SpatialGrid grid, float originX, float originY, float originZ, float dirX, float dirY, float dirZ, float maxDistance,
        Span<(long entityId, float distance)> results, uint categoryMask = uint.MaxValue)
    {
        if (results.Length == 0 || !SpatialSlot.HasSpatialIndex || PerCellIndex == null || ClusterSegment == null || ClusterAabbs == null)
        {
            return 0;
        }

        float length = MathF.Sqrt((dirX * dirX) + (dirY * dirY) + (dirZ * dirZ));
        if (length <= 0f || !float.IsFinite(maxDistance) || maxDistance <= 0f)
        {
            return 0;
        }

        // Normalised, so `t` is a distance in world units and results from different cells are directly comparable.
        dirX /= length;
        dirY /= length;
        dirZ /= length;

        var ss = SpatialSlot;
        bool is3D = ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F;
        if (!is3D)
        {
            // A 2D archetype lives in the plane containing world Z = 0. A ray with a Z component would otherwise select cell layers that hold nothing.
            originZ = 0f;
            dirZ = 0f;
        }

        float endX = originX + (dirX * maxDistance);
        float endY = originY + (dirY * maxDistance);
        float endZ = originZ + (dirZ * maxDistance);

        grid.WorldToCellRange(MathF.Min(originX, endX), MathF.Min(originY, endY), is3D ? MathF.Min(originZ, endZ) : float.NegativeInfinity,
            MathF.Max(originX, endX), MathF.Max(originY, endY), is3D ? MathF.Max(originZ, endZ) : float.PositiveInfinity,
            out int cellMinX, out int cellMinY, out int cellMinZ, out int cellMaxX, out int cellMaxY, out int cellMaxZ);

        if (!is3D)
        {
            grid.WorldToCellCoords(0f, 0f, 0f, out _, out _, out int planeZ);
            cellMinZ = planeZ;
            cellMaxZ = planeZ;
        }

        int count = 0;
        var accessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
            {
                for (int cy = cellMinY; cy <= cellMaxY; cy++)
                {
                    for (int cx = cellMinX; cx <= cellMaxX; cx++)
                    {
                        if (!grid.TryGetCellKey(cx, cy, cz, out int cellKey) || cellKey >= PerCellIndex.Length)
                        {
                            continue;
                        }

                        var slot = PerCellIndex[cellKey];
                        if (slot == null)
                        {
                            continue;
                        }

                        grid.CellOrigin(cellKey, out float cellOriginX, out float cellOriginY, out float cellOriginZ);
                        RayScanHalf(slot, isStatic: false, ref accessor, cellOriginX, cellOriginY, cellOriginZ,
                            originX, originY, originZ, dirX, dirY, dirZ, maxDistance, is3D, categoryMask, results, ref count);
                        RayScanHalf(slot, isStatic: true, ref accessor, cellOriginX, cellOriginY, cellOriginZ,
                            originX, originY, originZ, dirX, dirY, dirZ, maxDistance, is3D, categoryMask, results, ref count);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        // Front-to-back is part of the contract, and cells are walked in grid order rather than along the ray, so the merge happens here.
        for (int i = 1; i < count; i++)
        {
            var pending = results[i];
            int j = i - 1;
            while (j >= 0 && results[j].distance > pending.distance)
            {
                results[j + 1] = results[j];
                j--;
            }
            results[j + 1] = pending;
        }

        return count;
    }

    private void RayScanHalf(PerCellSpatialSlot slot, bool isStatic, ref ChunkAccessor<PersistentStore> accessor, float cellOriginX, float cellOriginY, 
        float cellOriginZ, float originX, float originY, float originZ, float dirX, float dirY, float dirZ, float maxDistance, bool is3D, uint categoryMask,
        Span<(long entityId, float distance)> results, ref int count)
    {
        var tree = slot.ReadTree(isStatic);   // acquire — see PerCellSpatialSlot.PublishDynamicTree
        if (tree != null)
        {
            // Reuse of the tree's own ray traversal, in the cell's frame. The Z sentinel is collapsed onto the flat slab the tree stores 2D clusters on.
            Span<double> rayOrigin = stackalloc double[3];
            Span<double> rayDir = stackalloc double[3];
            rayOrigin[0] = originX - cellOriginX;
            rayOrigin[1] = originY - cellOriginY;
            rayOrigin[2] = is3D ? originZ - cellOriginZ : 0.5d;
            rayDir[0] = dirX;
            rayDir[1] = dirY;
            rayDir[2] = is3D ? dirZ : 0d;

            foreach (var hit in tree.Tree.QueryRay(rayOrigin, rayDir, maxDistance, null, 0))
            {
                RayScanCluster((int)hit.PayloadId, ref accessor, originX, originY, originZ, dirX, dirY, dirZ, maxDistance, is3D, categoryMask,
                    results, ref count);
            }
            return;
        }

        var linear = slot.ReadIndex(isStatic);
        if (linear == null)
        {
            return;
        }

        for (int i = 0; i < linear.ClusterCount; i++)
        {
            // Directed OUTWARD: a box rounded inward rejects a ray grazing its face, which is an SQ-01 false negative on the linear path only — the tree
            // path translates the ray into the cell frame instead, which is exact.
            float minX = ClusterSpatialAabb.ToWorldMin(linear.MinX[i], cellOriginX);
            float minY = ClusterSpatialAabb.ToWorldMin(linear.MinY[i], cellOriginY);
            float maxX = ClusterSpatialAabb.ToWorldMax(linear.MaxX[i], cellOriginX);
            float maxY = ClusterSpatialAabb.ToWorldMax(linear.MaxY[i], cellOriginY);
            float minZ = float.NegativeInfinity;
            float maxZ = float.PositiveInfinity;
            if (is3D)
            {
                minZ = ClusterSpatialAabb.ToWorldMin(linear.MinZ[i], cellOriginZ);
                maxZ = ClusterSpatialAabb.ToWorldMax(linear.MaxZ[i], cellOriginZ);
            }

            if (!RayHitsBox(originX, originY, originZ, dirX, dirY, dirZ, maxDistance, minX, minY, minZ, maxX, maxY, maxZ, out _))
            {
                continue;
            }

            RayScanCluster(linear.ClusterIds[i], ref accessor, originX, originY, originZ, dirX, dirY, dirZ, maxDistance, is3D, categoryMask,
                results, ref count);
        }
    }

    private void RayScanCluster(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor, float originX, float originY, float originZ, float dirX, 
        float dirY, float dirZ, float maxDistance, bool is3D, uint categoryMask, Span<(long entityId, float distance)> results, ref int count)
    {
        if ((uint)clusterChunkId >= (uint)ClusterAabbs.Length)
        {
            return;
        }
        if (categoryMask != 0 && (ClusterAabbs[clusterChunkId].CategoryMask & categoryMask) == 0)
        {
            return;
        }


        var ss = SpatialSlot;
        int compOffset = Layout.ComponentOffset(ss.Slot);
        int compSize = Layout.ComponentSize(ss.Slot);
        int fieldOffset = ss.FieldOffset;

        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ulong occupancy = *(ulong*)clusterBase;

        Span<double> entityCoords = stackalloc double[6];
        while (occupancy != 0UL)
        {
            int slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;

            byte* fieldPtr = clusterBase + compOffset + (slot * compSize) + fieldOffset;
            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, ss.FieldInfo, entityCoords, ss.Descriptor))
            {
                continue;
            }

            float eMinX = (float)entityCoords[0];
            float eMinY = (float)entityCoords[1];
            float eMaxX = is3D ? (float)entityCoords[3] : (float)entityCoords[2];
            float eMaxY = is3D ? (float)entityCoords[4] : (float)entityCoords[3];
            float eMinZ = is3D ? (float)entityCoords[2] : float.NegativeInfinity;
            float eMaxZ = is3D ? (float)entityCoords[5] : float.PositiveInfinity;

            if (!RayHitsBox(originX, originY, originZ, dirX, dirY, dirZ, maxDistance, eMinX, eMinY, eMinZ, eMaxX, eMaxY, eMaxZ, out float t))
            {
                continue;
            }

            long entityId = *(long*)(clusterBase + Layout.EntityIdsOffset + (slot * 8));

            // A cluster can be reached through both halves of a cell only if it is in both, which C13 forbids — but the tree path and the linear path can
            // both be walked for one cell when the two halves use different structures, so the guard against a duplicate id stays cheap and explicit.
            bool already = false;
            for (int i = 0; i < count; i++)
            {
                if (results[i].entityId == entityId)
                {
                    already = true;
                    break;
                }
            }
            if (already)
            {
                continue;
            }

            if (count < results.Length)
            {
                results[count++] = (entityId, t);
            }
        }
    }

    /// <summary>
    /// Slab test: does the ray segment <c>[0, maxDistance]</c> enter the box, and at what distance.
    /// </summary>
    /// <remarks>
    /// Written against a NORMALISED direction, so <paramref name="tEntry"/> is a world-space distance. An axis with zero direction is handled by the
    /// infinities the division produces — the comparisons below reject correctly when the origin is outside the slab on that axis and leave the interval
    /// untouched when it is inside, which is why there is no special case for it.
    /// </remarks>
    private static bool RayHitsBox(float ox, float oy, float oz, float dx, float dy, float dz, float maxDistance, float minX, float minY, float minZ, 
        float maxX, float maxY, float maxZ, out float tEntry)
    {
        float tMin = 0f;
        float tMax = maxDistance;

        if (!SlabClip(ox, dx, minX, maxX, ref tMin, ref tMax)
            || !SlabClip(oy, dy, minY, maxY, ref tMin, ref tMax)
            || !SlabClip(oz, dz, minZ, maxZ, ref tMin, ref tMax))
        {
            tEntry = 0f;
            return false;
        }

        tEntry = tMin;
        return true;
    }

    private static bool SlabClip(float origin, float dir, float min, float max, ref float tMin, ref float tMax)
    {
        if (dir == 0f)
        {
            return origin >= min && origin <= max;
        }

        float inv = 1f / dir;
        float t1 = (min - origin) * inv;
        float t2 = (max - origin) * inv;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tMin = MathF.Max(tMin, t1);
        tMax = MathF.Min(tMax, t2);
        return tMin <= tMax;
    }
}

using System;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation spatial query handle for hot-loop usage (physics, AI, tick-frequency queries).
/// Obtained from <c>tx.SpatialQuery&lt;T&gt;()</c>. All methods return <c>ref struct</c> enumerators.
/// </summary>
internal readonly ref struct SpatialQuery<T> where T : unmanaged
{
    private readonly SpatialRTree<PersistentStore> _tree;

    internal SpatialQuery(SpatialRTree<PersistentStore> tree) => _tree = tree;

    /// <summary>AABB overlap query. Coords: [min0, min1, ..., max0, max1, ...].</summary>
    public SpatialRTree<PersistentStore>.AABBQueryEnumerator AABB(ReadOnlySpan<double> coords) => _tree.QueryAABB(coords);

    /// <summary>Radius (sphere) query. Returns entities whose fat AABB overlaps the bounding box of the sphere. Caller post-filters by distance.</summary>
    public SpatialRTree<PersistentStore>.RadiusEnumerator Radius(ReadOnlySpan<double> center, double radius) => _tree.QueryRadius(center, radius);

    /// <summary>Ray query with front-to-back ordering. Origin + direction + maxDist.</summary>
    public SpatialRTree<PersistentStore>.RayEnumerator Ray(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist) =>
        _tree.QueryRay(origin, direction, maxDist);

    /// <summary>Frustum query. Planes packed as (normalX, normalY, [normalZ,] distance), dimCount+1 doubles per plane.</summary>
    public SpatialRTree<PersistentStore>.FrustumEnumerator Frustum(ReadOnlySpan<double> planes, int planeCount) =>
        _tree.QueryFrustum(planes, planeCount);

    /// <summary>k-nearest-neighbors query. Results written to caller-provided buffer sorted by ascending squared distance.</summary>
    public int Nearest(ReadOnlySpan<double> center, int k, Span<(long entityId, double distSq)> results) => _tree.QueryKNN(center, k, results);
}

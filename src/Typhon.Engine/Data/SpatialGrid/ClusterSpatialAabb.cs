using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster tight 2D AABB plus category mask, used by the per-cell cluster R-Tree (issue #230).
/// One instance per spatially-active cluster, indexed by clusterChunkId. Stored in-memory only on
/// <see cref="ArchetypeClusterState"/> and rebuilt at startup via <c>RebuildClusterAabbs</c> from
/// entity positions (Q2/Q6 transient-state decision).
/// </summary>
/// <remarks>
/// <para>
/// 20 bytes, matches the f32 AABB layout used elsewhere in the spatial subsystem. 2D-only in Phase 1;
/// Phase 2 will generalize to 3D and f64 if profiling shows a need.
/// </para>
/// <para>
/// The <see cref="CategoryMask"/> is the OR of all entity category masks in the cluster — it lets the
/// per-cell broadphase skip entire clusters when the query's category mask does not intersect. Maintained
/// incrementally on spawn; tightened on the next full recompute pass at the tick fence.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ClusterSpatialAabb
{
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
    public uint CategoryMask;

    /// <summary>Create an empty AABB suitable as the seed for incremental unions (min = +inf, max = -inf).</summary>
    public static ClusterSpatialAabb Empty => new()
    {
        MinX = float.PositiveInfinity,
        MinY = float.PositiveInfinity,
        MaxX = float.NegativeInfinity,
        MaxY = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>Union an entity's tight AABB + category mask into this cluster AABB in place.</summary>
    public void Union(float entityMinX, float entityMinY, float entityMaxX, float entityMaxY, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        CategoryMask |= entityCategoryMask;
    }
}

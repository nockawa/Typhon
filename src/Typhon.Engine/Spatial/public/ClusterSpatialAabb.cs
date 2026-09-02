using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster tight AABB plus category mask, used by the per-cell cluster spatial index (issue #230).
/// One instance per spatially-active cluster, indexed by clusterChunkId. Stored in-memory only on
/// <see cref="ArchetypeClusterState"/> and rebuilt at startup via <c>RebuildClusterAabbs</c> from
/// entity positions (Q2/Q6 transient-state decision).
/// </summary>
/// <remarks>
/// <para><b>The six bounds are CELL-RELATIVE, not world-space</b> (#872 step 9, decision <c>C15</c>). They are offsets from the world-space minimum corner of
/// the cell the cluster belongs to, which <c>SpatialGrid.CellOrigin</c> derives from the cluster's entry in <c>ClusterCellMap</c>. A cluster lives wholly
/// inside one cell (<c>C13</c>), so that origin is unambiguous — and it is why a cluster's bounds must be REBASED when the cluster migrates to another cell.
/// A bound left un-rebased is off by exactly one cell size, which is a silent <c>SQ-01</c> false negative rather than an error.</para>
/// <para><b>Why not world space.</b> f32 across a ±10⁹ world resolves to ~64 units; measured against a cell the magnitude is bounded by the cell size, and
/// the same 24 mantissa bits resolve ~6 × 10⁻⁵. Note the limit this does NOT lift: the entity's own spatial component is world-space f32 too, so at extreme
/// magnitudes the source coordinate is already coarse and cell-relative storage cannot recover precision the input never carried. <c>C15</c> buys resolution
/// for DERIVED bounds — this AABB and the R-Tree's node bounds — not for the component field they are computed from.</para>
/// <para>Conversion goes through <see cref="ToCellRelativeMin"/> / <see cref="ToCellRelativeMax"/>, never a bare subtraction: the narrowing to f32 rounds,
/// and rounding the wrong way puts a bound inside the entity it must contain.</para>
/// <para>
/// <b>Storage shape.</b> 28 bytes: six f32 bounds components (XYZ min/max) plus a 4-byte category mask.
/// 2D archetypes leave <see cref="MinZ"/>/<see cref="MaxZ"/> at the <see cref="Empty"/> sentinel (+inf/-inf);
/// 2D queries use an infinite Z range which trivially passes the Z overlap test, so 2D clusters match
/// correctly. 3D archetypes populate all six bounds. The unified 3D storage adds ~8 bytes per cluster
/// versus a 2D-only design, in exchange for a single cluster-index code path that handles both tiers.
/// f64 variants (AABB2D/AABB3D) are deferred to a follow-up sub-issue of #228.
/// </para>
/// <para>
/// The <see cref="CategoryMask"/> is the OR of all entity category masks in the cluster — it lets the
/// per-cell broadphase skip entire clusters when the query's category mask does not intersect. Maintained
/// incrementally on spawn; tightened on the next full recompute pass at the tick fence.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[JetBrains.Annotations.PublicAPI]
public struct ClusterSpatialAabb
{
    /// <summary>Minimum X bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MinX;

    /// <summary>Minimum Y bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MinY;

    /// <summary>Minimum Z bound, RELATIVE to its cell's world-space minimum corner (<c>C15</c>). Left at the <see cref="Empty"/> sentinel (+inf) for
    /// 2D archetypes.</summary>
    public float MinZ;

    /// <summary>Maximum X bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MaxX;

    /// <summary>Maximum Y bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MaxY;

    /// <summary>Maximum Z bound, RELATIVE to its cell's world-space minimum corner (<c>C15</c>). Left at the <see cref="Empty"/> sentinel (-inf) for
    /// 2D archetypes.</summary>
    public float MaxZ;

    /// <summary>
    /// OR of every entity category mask in the cluster. Lets the per-cell broadphase skip the whole cluster when the query's category mask does not intersect.
    /// </summary>
    public uint CategoryMask;

    /// <summary>Static empty sentinel for ref-returning properties when no spatial data exists.</summary>
    internal static ClusterSpatialAabb s_empty = new()
    {
        MinX = float.PositiveInfinity, MinY = float.PositiveInfinity, MinZ = float.PositiveInfinity,
        MaxX = float.NegativeInfinity, MaxY = float.NegativeInfinity, MaxZ = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>Create an empty AABB suitable as the seed for incremental unions (min = +inf, max = -inf on all axes).</summary>
    public static ClusterSpatialAabb Empty => new()
    {
        MinX = float.PositiveInfinity,
        MinY = float.PositiveInfinity,
        MinZ = float.PositiveInfinity,
        MaxX = float.NegativeInfinity,
        MaxY = float.NegativeInfinity,
        MaxZ = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>
    /// Union a 2D entity's tight AABB + category mask into this cluster AABB in place. Leaves <see cref="MinZ"/>/<see cref="MaxZ"/> at their initial
    /// values; 2D cluster archetypes never populate Z bounds, and 2D queries against those clusters use an infinite Z range that trivially passes the Z
    /// overlap test regardless of the stored Z values.
    /// </summary>
    public void Union2F(float entityMinX, float entityMinY, float entityMaxX, float entityMaxY, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        CategoryMask |= entityCategoryMask;
    }

    /// <summary>
    /// Union a 3D entity's tight AABB + category mask into this cluster AABB in place. Updates all six bounds components.
    /// </summary>
    public void Union3F(float entityMinX, float entityMinY, float entityMinZ, float entityMaxX, float entityMaxY, float entityMaxZ, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMinZ < MinZ) MinZ = entityMinZ;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        if (entityMaxZ > MaxZ) MaxZ = entityMaxZ;
        CategoryMask |= entityCategoryMask;
    }

    /// <summary>
    /// Convert a world-space LOWER bound to cell-relative space, rounding AWAY from the entity so the result can only ever be too low (<c>C15</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>The subtraction is done in double and the narrowing is what rounds.</b> That is not defensive symmetry — it is where the error actually is.
    /// The bounds reaching this method come from <c>SpatialMaintainer.ReadAndValidateBoundsFromPtr</c>, which produces <see cref="double"/>, and even for an
    /// f32 source the exact difference need not be representable in f32: with an origin of 0.1 and a coordinate of 10⁷ the true offset needs more mantissa
    /// than f32 has, so <c>(float)</c> rounds it — and round-to-nearest rounds UP half the time, putting the lower bound INSIDE the entity it is supposed to
    /// contain.</para>
    /// <para>That is a <c>CA-01</c> violation, and <c>CA-01</c>'s own <c>on_violation</c> says what it looks like from outside: <i>"AABB too tight → per-cell
    /// cluster spatial queries miss entities (false negatives, silent)"</i>. One conditional <see cref="MathF.BitDecrement"/> removes the whole class. The
    /// test that pins it is an ablation to plain <c>(float)</c> narrowing at large magnitudes.</para>
    /// </remarks>
    public static float ToCellRelativeMin(double worldValue, double cellOrigin)
    {
        double relative = worldValue - cellOrigin;
        float narrowed = (float)relative;

        // Only when the narrowing moved the bound the WRONG way. Widening unconditionally would also be correct, but it would compound: a bound that is read
        // and re-stored every tick would drift outward by an ULP each time, decaying the tightness this design exists to buy with nothing to show for it.
        return narrowed > relative ? MathF.BitDecrement(narrowed) : narrowed;
    }

    /// <summary>Convert a world-space UPPER bound to cell-relative space, rounding away from the entity. See <see cref="ToCellRelativeMin"/>.</summary>
    public static float ToCellRelativeMax(double worldValue, double cellOrigin)
    {
        double relative = worldValue - cellOrigin;
        float narrowed = (float)relative;
        return narrowed < relative ? MathF.BitIncrement(narrowed) : narrowed;
    }

    /// <summary>Convert a cell-relative bound back to world space. For DISPLAY and for callers that do not test containment.</summary>
    /// <remarks>
    /// <para><b>Not exact, and deliberately not corrected.</b> <c>float + float</c> rounds to nearest, and it can round INWARD — so the world value this
    /// returns may be a hair tighter than the stored bound. Every caller today is display or diagnostics (<c>ClusterRef.SpatialBounds</c>,
    /// <c>DatabaseEngine.StorageIntrospection</c>), where a half-ULP is invisible.</para>
    /// <para>Directed rounding is withheld on purpose rather than forgotten: widening on every read would COMPOUND, so a bound read and re-stored across N
    /// ticks would grow without limit and the tightness this design exists to buy would decay with nothing to show for it. A future caller that tests
    /// containment in world space needs its own directed variant, not a change here.</para>
    /// </remarks>
    public static float ToWorld(float cellRelativeValue, float cellOrigin) => cellRelativeValue + cellOrigin;

}

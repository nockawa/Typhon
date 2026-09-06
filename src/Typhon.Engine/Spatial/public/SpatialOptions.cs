using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Tuning for the spatial layer's per-cell broadphase — the structure a cell uses to answer "which clusters overlap this box".
/// </summary>
/// <remarks>
/// <para><b>The engine picks the structure; this only moves the boundary.</b> Every cell starts on a linear SoA scan, which is the right answer while the
/// cell holds few clusters: six float compares over contiguous memory with no pointer chasing beats any tree until the tree's pruning pays for its
/// indirection. A cell that keeps filling crosses that boundary, and the engine promotes it to a per-cell R-Tree on its own. A cell that empties out again
/// falls back. Neither transition is something an application asks for, and there is no per-cell override: density is a property of the world, and the
/// engine observes it directly.</para>
/// <para><b>Why it is exposed at all.</b> The crossover is a function of query selectivity and of the query-to-update ratio, and those are properties of the
/// application rather than of the engine. A workload that queries a dense cell far more often than it moves things in it wants to promote earlier; one that
/// moves everything every tick and queries rarely wants to promote later, because the tree's update path is dearer per moved cluster than six float stores.
/// The shipped default is measured at the middle of that range — see the guide for how to tell which side of it you are on.</para>
/// </remarks>
[PublicAPI]
public sealed class SpatialOptions
{
    /// <summary>
    /// The default <see cref="CellTreePromoteThreshold"/>: clusters in one cell at which the engine replaces that cell's linear scan with an R-Tree.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, not chosen.</b> <c>BroadphaseCrossoverSweepTests</c> sweeps both structures over cluster count and query selectivity. On a
    /// Ryzen 7950X the tree loses EVERY query column at 512 clusters per cell (best case 0.70x) and first wins at 1563, where a selective query is 2.03x
    /// faster and a very selective one 4.24x. The update side moves the other way — a tree update is 20.8x dearer than six float stores at 512 and 29.9x at
    /// 1563 — but the motion hysteresis absorbs 97% of moves at the shipped margin, which brings the real cost to 61 ns against 23 ns per move.</para>
    /// <para><b>So the boundary sits between those two measured points, and this is the conservative side of it.</b> A cell holding 1024 clusters of one
    /// archetype carries up to 65 536 entities, and its linear scan is already costing about a microsecond per query — that is a cell whose density the grid
    /// was not configured for, and the point of promoting is to stop the scan growing with it. Below that the scan is simply the better structure, and
    /// promoting early would make every ordinary database pay the tree's update cost for a query win it never collects.</para>
    /// <para><b>The fall-back at half this value is deliberately in the region where the tree loses.</b> That is what hysteresis costs: a cell oscillating
    /// between 512 and 1024 clusters stays on the tree throughout rather than rebuilding itself in both directions every tick, and one rebuild per tick is
    /// far dearer than the gap it is holding open.</para>
    /// </remarks>
    public const int DefaultCellTreePromoteThreshold = 1024;

    /// <summary>
    /// Clusters in one cell half (Static or Dynamic) at which the engine promotes that half from a linear scan to a per-cell R-Tree. Set to
    /// <see cref="int.MaxValue"/> to keep every cell on the linear scan whatever its density.
    /// </summary>
    /// <remarks>
    /// <para>Counted in CLUSTERS, not entities. A cluster holds up to 64 entities of one archetype that share a cell, so the default corresponds to a cell
    /// carrying on the order of sixty thousand entities of one archetype before anything changes shape.</para>
    /// <para>Promotion is evaluated when a cluster is added to a cell, and it rebuilds that cell half in <c>O(C)</c>. The fall-back is at half this value, and
    /// the gap is what stops a cell hovering on the boundary from rebuilding itself twice per tick.</para>
    /// </remarks>
    public int CellTreePromoteThreshold { get; set; } = DefaultCellTreePromoteThreshold;
}

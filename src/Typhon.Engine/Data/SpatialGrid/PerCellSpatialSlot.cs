namespace Typhon.Engine;

/// <summary>
/// Per-archetype per-cell spatial slot holding one <see cref="CellSpatialIndex"/> for each of the
/// static/dynamic splits. Lazily allocated — an entry in
/// <c>ArchetypeClusterState.PerCellIndex</c> is null for any cell where this archetype has no clusters
/// (issue #230, Decision Q10).
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 supports Dynamic mode only; <see cref="StaticIndex"/> is reserved for Phase 2 when the
/// static-mode recompute pattern is designed. Wrapping a single index in a class rather than inlining
/// it into <c>CellSpatialIndex</c> directly keeps the Phase 2 expansion additive (no storage-shape
/// change on <c>ArchetypeClusterState</c> when <see cref="StaticIndex"/> gets populated).
/// </para>
/// </remarks>
internal sealed class PerCellSpatialSlot
{
    /// <summary>Dynamic-mode cluster index. Populated when clusters with <c>SpatialMode.Dynamic</c> spatial fields are in this cell.</summary>
    public CellSpatialIndex DynamicIndex;

    // Phase 2: public CellSpatialIndex StaticIndex;
}

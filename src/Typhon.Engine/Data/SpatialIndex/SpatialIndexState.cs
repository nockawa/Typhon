namespace Typhon.Engine;

/// <summary>
/// Encapsulates all spatial index state for a single ComponentTable. Null on ComponentTable if no <c>[SpatialIndex]</c> attribute is present.
/// Owns the R-Tree, back-pointer segment, field metadata, and (Phase 4) optional occupancy hashmap.
/// </summary>
internal class SpatialIndexState
{
    public SpatialRTree<PersistentStore> Tree { get; }
    public ChunkBasedSegment<PersistentStore> BackPointerSegment { get; }
    public SpatialFieldInfo FieldInfo { get; }
    public SpatialNodeDescriptor Descriptor { get; }

    /// <summary>Layer 1 coarse occupancy filter. Null when CellSize == 0 (default — queries go straight to tree).</summary>
    public PagedHashMap<long, int, PersistentStore> OccupancyMap { get; }

    internal SpatialIndexState(SpatialRTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> backPointerSegment, SpatialFieldInfo fieldInfo,
        SpatialNodeDescriptor descriptor, PagedHashMap<long, int, PersistentStore> occupancyMap = null)
    {
        Tree = tree;
        BackPointerSegment = backPointerSegment;
        FieldInfo = fieldInfo;
        Descriptor = descriptor;
        OccupancyMap = occupancyMap;
    }
}

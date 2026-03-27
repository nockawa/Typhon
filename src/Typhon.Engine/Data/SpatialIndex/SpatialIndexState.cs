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

    // Layer 1 occupancy map — deferred to Phase 4
    // public PagedHashMap<long, int, PersistentStore> OccupancyMap { get; }

    internal SpatialIndexState(SpatialRTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> backPointerSegment, SpatialFieldInfo fieldInfo, 
        SpatialNodeDescriptor descriptor)
    {
        Tree = tree;
        BackPointerSegment = backPointerSegment;
        FieldInfo = fieldInfo;
        Descriptor = descriptor;
    }
}

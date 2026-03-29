using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Encapsulates all spatial index state for a single ComponentTable. Null on ComponentTable if no <c>[SpatialIndex]</c> attribute is present.
/// Holds up to two R-Trees (static + dynamic), back-pointer segment, field metadata, and optional occupancy hashmap.
/// With per-component-type SpatialMode, exactly one tree is non-null.
/// </summary>
internal class SpatialIndexState
{
    /// <summary>R-Tree for static entities (bulk-loaded, skipped by tick fence). Null when Mode == Dynamic.</summary>
    public SpatialRTree<PersistentStore> StaticTree { get; }

    /// <summary>R-Tree for dynamic entities (fat AABBs, tick-fence updates). Null when Mode == Static.</summary>
    public SpatialRTree<PersistentStore> DynamicTree { get; }

    public ChunkBasedSegment<PersistentStore> BackPointerSegment { get; }
    public SpatialFieldInfo FieldInfo { get; }
    public SpatialNodeDescriptor Descriptor { get; }

    /// <summary>Layer 1 coarse occupancy filter. Null when CellSize == 0 (default — queries go straight to tree).</summary>
    public PagedHashMap<long, int, PersistentStore> OccupancyMap { get; }

    /// <summary>Trigger volume system for this spatial index. Null until first CreateTriggerSystem() call.</summary>
    public SpatialTriggerSystem TriggerSystem { get; internal set; }

    /// <summary>The active tree based on FieldInfo.Mode. Exactly one of StaticTree/DynamicTree is non-null.</summary>
    public SpatialRTree<PersistentStore> ActiveTree => FieldInfo.Mode == SpatialMode.Static ? StaticTree : DynamicTree;

    /// <summary>Route to the correct tree by back-pointer's TreeSelector value (which equals (byte)SpatialMode).</summary>
    public SpatialRTree<PersistentStore> GetTree(byte treeSelector) => treeSelector == (byte)SpatialMode.Static ? StaticTree : DynamicTree;

    /// <summary>Get or create the trigger system for this spatial index.</summary>
    internal SpatialTriggerSystem GetOrCreateTriggerSystem(ComponentTable table)
    {
        TriggerSystem ??= new SpatialTriggerSystem(table, this);
        return TriggerSystem;
    }

    internal SpatialIndexState(SpatialRTree<PersistentStore> staticTree, SpatialRTree<PersistentStore> dynamicTree,
        ChunkBasedSegment<PersistentStore> backPointerSegment, SpatialFieldInfo fieldInfo, SpatialNodeDescriptor descriptor,
        PagedHashMap<long, int, PersistentStore> occupancyMap = null)
    {
        StaticTree = staticTree;
        DynamicTree = dynamicTree;
        BackPointerSegment = backPointerSegment;
        FieldInfo = fieldInfo;
        Descriptor = descriptor;
        OccupancyMap = occupancyMap;
    }
}

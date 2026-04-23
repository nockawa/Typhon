namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// One archetype containing a focused component. Powers the Schema Inspector's Archetype panel — columns for storage
/// mode (cluster vs. legacy), entity count, chunk count, occupancy, and the component set sharing this archetype.
/// </summary>
/// <param name="StorageMode">"cluster" — fixed-size SoA chunks; "legacy" — per-ComponentTable segment storage.</param>
/// <param name="ComponentSize">Bytes occupied by this component per entity in this archetype. 0 if not cluster-backed.</param>
/// <param name="ChunkCount">Active cluster chunks for cluster archetypes. 0 for legacy (not applicable).</param>
/// <param name="ChunkCapacity">Entities per chunk for cluster archetypes. 0 for legacy.</param>
/// <param name="OccupancyPct">Cluster-only occupancy as a percentage in [0, 100]. 0 when not cluster or chunks=0.</param>
public record ArchetypeInfoDto(
    string ArchetypeId,
    string[] ComponentTypes,
    long EntityCount,
    int ComponentSize,
    string StorageMode,
    int ChunkCount,
    int ChunkCapacity,
    double OccupancyPct);

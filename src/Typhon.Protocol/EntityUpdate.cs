using MemoryPack;

namespace Typhon.Protocol;

/// <summary>
/// Incremental update for a Modified entity. Contains only the components that changed.
/// </summary>
[MemoryPackable]
public partial struct EntityUpdate
{
    /// <summary>Entity identifier (raw value of EntityId).</summary>
    public long Id;

    /// <summary>Components that changed this tick, each with field-level dirty bits and values.</summary>
    public ComponentFieldUpdate[] ChangedComponents;
}

using MemoryPack;

namespace Typhon.Protocol;

/// <summary>
/// Full entity snapshot for an Added entity. Contains all enabled components with complete data.
/// </summary>
[MemoryPackable]
public partial struct EntityDelta
{
    /// <summary>Entity identifier (raw value of EntityId).</summary>
    public long Id;

    /// <summary>All enabled components on this entity, each with full raw bytes.</summary>
    public ComponentSnapshot[] Components;
}

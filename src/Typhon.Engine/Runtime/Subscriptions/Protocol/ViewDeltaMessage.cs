using MemoryPack;

namespace Typhon.Engine;

/// <summary>
/// Per-View changes for a single tick. Absent from <see cref="TickDeltaMessage.Views"/> if the View had no changes.
/// </summary>
[MemoryPackable]
public partial struct ViewDeltaMessage
{
    /// <summary>Published View identifier.</summary>
    public ushort ViewId;

    /// <summary>Entities that entered the View this tick (full component snapshots).</summary>
    public EntityDelta[] Added;

    /// <summary>Entity IDs that left the View this tick (client destroys local copy).</summary>
    public long[] Removed;

    /// <summary>Entities that remained in the View but had component data change.</summary>
    public EntityUpdate[] Modified;
}

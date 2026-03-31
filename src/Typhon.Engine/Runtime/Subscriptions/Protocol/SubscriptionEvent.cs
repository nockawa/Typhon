using MemoryPack;

namespace Typhon.Engine;

/// <summary>
/// Subscription lifecycle event: subscribe, unsubscribe, sync complete, or resync.
/// Included in <see cref="TickDeltaMessage.Events"/>.
/// </summary>
[MemoryPackable]
public partial struct SubscriptionEvent
{
    /// <summary>Identifies the published View this event relates to.</summary>
    public ushort ViewId;

    /// <summary>Event type (Subscribed, Unsubscribed, SyncComplete, Resync).</summary>
    public EventType Type;

    /// <summary>Human-readable View name. Only populated on <see cref="EventType.Subscribed"/>; null otherwise.</summary>
    public string ViewName;
}

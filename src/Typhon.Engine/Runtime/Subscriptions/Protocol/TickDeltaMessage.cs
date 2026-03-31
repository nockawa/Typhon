using MemoryPack;

namespace Typhon.Engine;

/// <summary>
/// Top-level message pushed to each client once per tick. Contains subscription lifecycle events and per-View data deltas.
/// </summary>
/// <remarks>
/// Wire format: <c>[4-byte payload length LE][MemoryPack-serialized TickDeltaMessage]</c>.
/// One message per tick per client. Empty ticks (no events, no View changes) are not sent.
/// </remarks>
[MemoryPackable]
public partial struct TickDeltaMessage
{
    /// <summary>Monotonically increasing tick number.</summary>
    public long TickNumber;

    /// <summary>Subscription lifecycle events (Subscribed, Unsubscribed, SyncComplete, Resync). Null if none.</summary>
    public SubscriptionEvent[] Events;

    /// <summary>Per-View data deltas. Only Views with changes are included. Null if no View changes.</summary>
    public ViewDeltaMessage[] Views;
}

namespace Typhon.Protocol;

/// <summary>
/// Type of subscription lifecycle event sent to a client.
/// </summary>
public enum EventType : byte
{
    /// <summary>A new View subscription was added. Incremental sync will follow.</summary>
    Subscribed,

    /// <summary>A View subscription was removed. Client should tear down local cache.</summary>
    Unsubscribed,

    /// <summary>Incremental sync for a View is complete. Normal delta flow begins.</summary>
    SyncComplete,

    /// <summary>Backpressure overflow — this tick's delta is a full snapshot replacing the local cache.</summary>
    Resync
}

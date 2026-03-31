namespace Typhon.Client;

/// <summary>
/// Current state of a <see cref="TyphonConnection"/>.
/// </summary>
public enum ConnectionState : byte
{
    /// <summary>TCP connection is being established.</summary>
    Connecting,

    /// <summary>Connected and receiving tick deltas.</summary>
    Connected,

    /// <summary>Connection lost. If <see cref="TyphonConnectionOptions.AutoReconnect"/> is false, this is terminal.</summary>
    Disconnected,

    /// <summary>Connection lost; automatic reconnection is in progress.</summary>
    Reconnecting
}

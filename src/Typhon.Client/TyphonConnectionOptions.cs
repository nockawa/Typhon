using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Client;

/// <summary>
/// Configuration for a <see cref="TyphonConnection"/>.
/// </summary>
public sealed class TyphonConnectionOptions
{
    /// <summary>
    /// Initial receive buffer size in bytes. The buffer grows automatically when a frame exceeds this size.
    /// Default: 65536 (64 KB).
    /// </summary>
    public int ReceiveBufferSize { get; set; } = 65536;

    /// <summary>
    /// Whether to automatically reconnect on connection loss. Default: true.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Maximum delay between reconnection attempts. Backoff starts at 1 second and doubles up to this cap.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional logger. Null disables logging (zero cost).
    /// </summary>
    public ILogger Logger { get; set; }
}

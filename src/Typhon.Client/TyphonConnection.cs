using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Typhon.Client.Internal;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// A connection to a running Typhon server. Receives tick-based deltas on a dedicated background thread and dispatches them to locally
/// registered <see cref="ViewSubscription"/>s.
/// </summary>
/// <remarks>
/// <para>In v1, subscriptions are server-driven: the client registers local interest via <see cref="Subscribe"/>, and callbacks fire when the server pushes
/// matching Views.</para>
/// <para>All callbacks fire on the receive thread. Do not block in callbacks.</para>
/// </remarks>
public sealed partial class TyphonConnection : IDisposable
{
    private readonly IPEndPoint _endpoint;
    private readonly TyphonConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly ComponentRegistry _componentRegistry = new();

    // Subscriptions by name (written by user thread, read by receive thread under lock)
    private readonly Lock _subscriptionLock = new();
    private readonly Dictionary<string, ViewSubscription> _subscriptionsByName = new();

    // Subscriptions by server-assigned ViewId (written/read only by receive thread — no lock needed)
    private readonly Dictionary<ushort, ViewSubscription> _subscriptionsByViewId = new();

    // Socket and receive loop
    private Socket _socket;
    private NetworkStream _stream;
    private FrameReader _frameReader;
    private Thread _receiveThread;
    private bool _shutdown;
    private ConnectionState _state;
    private long _lastTickNumber;
    private int _disposed;

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Current connection state.</summary>
    public ConnectionState State => _state;

    /// <summary>Tick number of the last received <see cref="TickDeltaMessage"/>.</summary>
    public long LastTickNumber => Interlocked.Read(ref _lastTickNumber);

    /// <summary>Fires after TCP connection is established and receive loop starts.</summary>
    public event Action<TyphonConnection> OnConnected;

    /// <summary>Fires when the connection is lost. The exception is null for clean disconnects.</summary>
    public event Action<TyphonConnection, Exception> OnDisconnected;

    /// <summary>Fires after a successful automatic reconnection.</summary>
    public event Action<TyphonConnection> OnReconnected;

    /// <summary>Fires for each <see cref="TickDeltaMessage"/> received. Argument is the tick number.</summary>
    public event Action<TyphonConnection, long> OnTick;

    internal TyphonConnection(IPEndPoint endpoint, TyphonConnectionOptions options)
    {
        _endpoint = endpoint;
        _options = options ?? new TyphonConnectionOptions();
        _logger = _options.Logger ?? NullLogger.Instance;
        _frameReader = new FrameReader(_options.ReceiveBufferSize);
    }

    /// <summary>
    /// Register a local subscription to a named View. Returns a <see cref="ViewSubscription"/> that holds the entity cache and callbacks.
    /// The subscription becomes active when the server pushes a matching View.
    /// </summary>
    /// <param name="viewName">Published View name (must match the name used in <c>PublishView</c> on the server).</param>
    /// <returns>A <see cref="ViewSubscription"/> to attach callbacks to.</returns>
    public ViewSubscription Subscribe(string viewName)
    {
        ArgumentNullException.ThrowIfNull(viewName);
        lock (_subscriptionLock)
        {
            if (_subscriptionsByName.TryGetValue(viewName, out var existing))
            {
                return existing;
            }

            var sub = new ViewSubscription(viewName);
            _subscriptionsByName[viewName] = sub;
            return sub;
        }
    }

    /// <summary>
    /// Remove a local subscription by View name. The entity cache is cleared and callbacks are detached.
    /// </summary>
    public void Unsubscribe(string viewName)
    {
        ArgumentNullException.ThrowIfNull(viewName);
        lock (_subscriptionLock)
        {
            if (_subscriptionsByName.Remove(viewName, out var sub))
            {
                sub.Clear();
            }
        }
    }

    /// <summary>
    /// Get an existing subscription by View name, or null if not subscribed.
    /// </summary>
    public ViewSubscription GetSubscription(string viewName)
    {
        lock (_subscriptionLock)
        {
            return _subscriptionsByName.TryGetValue(viewName, out var sub) ? sub : null;
        }
    }

    /// <summary>
    /// Register a component type for typed access via <see cref="CachedEntity.Get{T}"/>.
    /// </summary>
    public void RegisterComponent<T>(ushort componentId) where T : unmanaged => _componentRegistry.Register<T>(componentId);

    /// <summary>
    /// Cleanly disconnect from the server. The receive thread is stopped and all caches are cleared.
    /// </summary>
    public void Disconnect()
    {
        _shutdown = true;
        CloseSocket();

        _receiveThread?.Join(TimeSpan.FromSeconds(5));
        _receiveThread = null;
        _state = ConnectionState.Disconnected;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown = true;
        CloseSocket();

        _receiveThread?.Join(TimeSpan.FromSeconds(5));
        _receiveThread = null;
        _state = ConnectionState.Disconnected;
    }

    // ═══════════════════════════════════════════════════════════════
    // Connection establishment (called by TyphonClient factory)
    // ═══════════════════════════════════════════════════════════════

    internal void Connect()
    {
        _state = ConnectionState.Connecting;
        EstablishConnection();
        StartReceiveThread();
        _state = ConnectionState.Connected;
        OnConnected?.Invoke(this);
        LogConnected(_endpoint.ToString());
    }

    private void EstablishConnection()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.NoDelay = true; // Match server's TCP_NODELAY
        _socket.Connect(_endpoint);
        _stream = new NetworkStream(_socket, ownsSocket: false);
    }

    private void StartReceiveThread()
    {
        _receiveThread = new Thread(ReceiveLoop)
        {
            Name = "Typhon.Client.Receive",
            IsBackground = true
        };
        _receiveThread.Start();
    }

    // ═══════════════════════════════════════════════════════════════
    // Receive loop (dedicated background thread)
    // ═══════════════════════════════════════════════════════════════

    private void ReceiveLoop()
    {
        try
        {
            while (!_shutdown)
            {
                var payload = _frameReader.ReadFrame(_stream);
                var delta = MemoryPackSerializer.Deserialize<TickDeltaMessage>(payload.Span);
                ProcessTickDelta(ref delta);
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or SocketException or ObjectDisposedException)
        {
            // During shutdown, silently exit — the socket was closed intentionally
            if (_shutdown)
            {
                return;
            }

            // Connection lost unexpectedly
            HandleConnectionLoss(ex is EndOfStreamException ? null : ex);
        }
        catch (InvalidDataException ex) when (!_shutdown)
        {
            // Corrupt frame — treat as connection loss
            LogFrameError(ex);
            HandleConnectionLoss(ex);
        }
    }

    private void HandleConnectionLoss(Exception ex)
    {
        _state = ConnectionState.Disconnected;
        LogDisconnected(_endpoint.ToString(), ex);
        OnDisconnected?.Invoke(this, ex);

        if (_options.AutoReconnect && !_shutdown)
        {
            ReconnectLoop();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Reconnection (exponential backoff)
    // ═══════════════════════════════════════════════════════════════

    private void ReconnectLoop()
    {
        _state = ConnectionState.Reconnecting;
        ClearAllCaches();
        CloseSocket();

        var delay = TimeSpan.FromSeconds(1);

        while (!_shutdown)
        {
            LogReconnecting(_endpoint.ToString(), (int)delay.TotalMilliseconds);

            try
            {
                Thread.Sleep(delay);
                if (_shutdown)
                {
                    break;
                }

                EstablishConnection();
                _frameReader = new FrameReader(_options.ReceiveBufferSize);
                _state = ConnectionState.Connected;
                LogReconnected(_endpoint.ToString());
                OnReconnected?.Invoke(this);

                // Re-enter receive loop on this same thread
                ReceiveLoop();
                return;
            }
            catch (SocketException) when (!_shutdown)
            {
                CloseSocket();
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _options.ReconnectMaxDelay.TotalMilliseconds));
            }
            catch (ObjectDisposedException) when (_shutdown)
            {
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Delta processing
    // ═══════════════════════════════════════════════════════════════

    private void ProcessTickDelta(ref TickDeltaMessage delta)
    {
        // 1. Process subscription lifecycle events
        if (delta.Events != null)
        {
            ProcessEvents(delta.Events);
        }

        // 2. Process per-View data deltas
        if (delta.Views != null)
        {
            ProcessViewDeltas(delta.Views);
        }

        // 3. Update tick number and fire callback
        Interlocked.Exchange(ref _lastTickNumber, delta.TickNumber);
        OnTick?.Invoke(this, delta.TickNumber);
    }

    private void ProcessEvents(SubscriptionEvent[] events)
    {
        for (var i = 0; i < events.Length; i++)
        {
            ref var evt = ref events[i];
            switch (evt.Type)
            {
                case EventType.Subscribed:
                    HandleSubscribed(evt.ViewId, evt.ViewName);
                    break;

                case EventType.Unsubscribed:
                    HandleUnsubscribed(evt.ViewId);
                    break;

                case EventType.SyncComplete:
                    if (_subscriptionsByViewId.TryGetValue(evt.ViewId, out var syncSub))
                    {
                        syncSub.FireSyncComplete();
                    }
                    break;

                case EventType.Resync:
                    if (_subscriptionsByViewId.TryGetValue(evt.ViewId, out var resyncSub))
                    {
                        resyncSub.FireResync();
                    }
                    break;
            }
        }
    }

    private void HandleSubscribed(ushort viewId, string viewName)
    {
        ViewSubscription sub;
        lock (_subscriptionLock)
        {
            if (!_subscriptionsByName.TryGetValue(viewName, out sub))
            {
                // Server pushed a View we didn't locally subscribe to — create one lazily
                sub = new ViewSubscription(viewName);
                _subscriptionsByName[viewName] = sub;
            }
        }

        sub.ViewId = viewId;
        _subscriptionsByViewId[viewId] = sub;
    }

    private void HandleUnsubscribed(ushort viewId)
    {
        if (_subscriptionsByViewId.Remove(viewId, out var sub))
        {
            sub.Clear();
            sub.ViewId = 0;
            sub.IsSynced = false;
        }
    }

    private void ProcessViewDeltas(ViewDeltaMessage[] views)
    {
        for (var i = 0; i < views.Length; i++)
        {
            ref var viewDelta = ref views[i];

            if (!_subscriptionsByViewId.TryGetValue(viewDelta.ViewId, out var sub))
            {
                continue; // No local subscription for this View
            }

            // Added entities
            if (viewDelta.Added != null)
            {
                for (var j = 0; j < viewDelta.Added.Length; j++)
                {
                    ref var added = ref viewDelta.Added[j];
                    var entity = new CachedEntity(added.Id, added.Components);
                    sub.AddEntity(entity);
                }
            }

            // Modified entities
            if (viewDelta.Modified != null)
            {
                for (var j = 0; j < viewDelta.Modified.Length; j++)
                {
                    ref var modified = ref viewDelta.Modified[j];
                    sub.ModifyEntity(modified.Id, modified.ChangedComponents);
                }
            }

            // Removed entities
            if (viewDelta.Removed != null)
            {
                for (var j = 0; j < viewDelta.Removed.Length; j++)
                {
                    sub.RemoveEntity(viewDelta.Removed[j]);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void ClearAllCaches()
    {
        // Clear ViewId mappings (will be re-established on reconnect when server re-pushes subscriptions)
        _subscriptionsByViewId.Clear();

        lock (_subscriptionLock)
        {
            foreach (var sub in _subscriptionsByName.Values)
            {
                sub.Clear();
                sub.ViewId = 0;
                sub.IsSynced = false;
            }
        }
    }

    private void CloseSocket()
    {
        try { _stream?.Dispose(); } catch { /* best-effort */ }
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { /* best-effort */ }
        try { _socket?.Dispose(); } catch { /* best-effort */ }
        _stream = null;
        _socket = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Logging
    // ═══════════════════════════════════════════════════════════════

    [LoggerMessage(LogLevel.Information, "Connected to Typhon server at {Endpoint}")]
    private partial void LogConnected(string endpoint);

    [LoggerMessage(LogLevel.Warning, "Disconnected from Typhon server at {Endpoint}")]
    private partial void LogDisconnected(string endpoint, Exception ex);

    [LoggerMessage(LogLevel.Information, "Reconnecting to {Endpoint} in {DelayMs}ms")]
    private partial void LogReconnecting(string endpoint, int delayMs);

    [LoggerMessage(LogLevel.Information, "Reconnected to Typhon server at {Endpoint}")]
    private partial void LogReconnected(string endpoint);

    [LoggerMessage(LogLevel.Error, "Invalid frame received")]
    private partial void LogFrameError(Exception ex);
}

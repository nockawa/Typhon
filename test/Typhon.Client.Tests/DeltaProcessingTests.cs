using MemoryPack;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// Tests for the TyphonConnection delta processing pipeline.
/// Uses a fake TCP server that pushes canned TickDeltaMessages to a real TyphonConnection.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DeltaProcessingTests
{
    private const int TestPort = 19877;

    /// <summary>
    /// Start a TCP listener, accept one client, send canned frames, then dispose.
    /// </summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private Socket _clientSocket;

        public FakeServer(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start(1);
        }

        public void AcceptClient()
        {
            _clientSocket = _listener.AcceptSocket();
            _clientSocket.NoDelay = true;
        }

        public void SendTickDelta(TickDeltaMessage message)
        {
            var payload = MemoryPackSerializer.Serialize(message);
            Span<byte> lengthPrefix = stackalloc byte[4];
            BitConverter.TryWriteBytes(lengthPrefix, payload.Length);
            _clientSocket.Send(lengthPrefix);
            _clientSocket.Send(payload);
        }

        public void Dispose()
        {
            try { _clientSocket?.Shutdown(SocketShutdown.Both); } catch { }
            _clientSocket?.Dispose();
            _listener.Stop();
        }
    }

    [Test]
    public void Subscribe_And_ReceiveAdded_CachePopulated()
    {
        using var server = new FakeServer(TestPort);

        // Connect client in background
        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));
        Assert.That(conn, Is.Not.Null);

        try
        {
            var sub = conn.Subscribe("world_npcs");
            var addedEntities = new List<CachedEntity>();
            sub.OnEntityAdded += e => addedEntities.Add(e);

            // Send Subscribed event
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 1,
                Events = [new SubscriptionEvent { ViewId = 10, Type = EventType.Subscribed, ViewName = "world_npcs" }]
            });

            // Send Added entity
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 2,
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 10,
                        Added =
                        [
                            new EntityDelta
                            {
                                Id = 42,
                                Components = [new ComponentSnapshot { ComponentId = 1, Data = [10, 20, 30] }]
                            }
                        ]
                    }
                ]
            });

            // Wait for callbacks
            SpinWait.SpinUntil(() => addedEntities.Count > 0, TimeSpan.FromSeconds(3));

            Assert.That(addedEntities.Count, Is.EqualTo(1));
            Assert.That(addedEntities[0].Id, Is.EqualTo(42));
            Assert.That(sub.Entities.ContainsKey(42), Is.True);
            Assert.That(sub.ViewId, Is.EqualTo(10));
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void Modified_UpdatesCachedComponentData()
    {
        using var server = new FakeServer(TestPort + 1);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 1, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        try
        {
            var sub = conn.Subscribe("test");
            ComponentFieldUpdate[] receivedUpdates = null;
            sub.OnEntityModified += (_, u) => receivedUpdates = u;

            // Subscribed + initial entity
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 1,
                Events = [new SubscriptionEvent { ViewId = 5, Type = EventType.Subscribed, ViewName = "test" }],
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 5,
                        Added = [new EntityDelta { Id = 100, Components = [new ComponentSnapshot { ComponentId = 1, Data = [1, 2, 3, 4] }] }]
                    }
                ]
            });

            // Wait for entity to be cached
            SpinWait.SpinUntil(() => sub.Entities.ContainsKey(100), TimeSpan.FromSeconds(3));

            // Send Modified
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 2,
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 5,
                        Modified =
                        [
                            new EntityUpdate
                            {
                                Id = 100,
                                ChangedComponents =
                                [
                                    new ComponentFieldUpdate { ComponentId = 1, FieldDirtyBits = ~0UL, FieldValues = [99, 88, 77, 66] }
                                ]
                            }
                        ]
                    }
                ]
            });

            SpinWait.SpinUntil(() => receivedUpdates != null, TimeSpan.FromSeconds(3));

            Assert.That(receivedUpdates, Is.Not.Null);
            Assert.That(sub.Entities[100].Components[0].Data, Is.EqualTo(new byte[] { 99, 88, 77, 66 }));
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void Removed_RemovesFromCache()
    {
        using var server = new FakeServer(TestPort + 2);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 2, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        try
        {
            var sub = conn.Subscribe("test");
            long removedId = 0;
            sub.OnEntityRemoved += id => removedId = id;

            // Subscribed + entity
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 1,
                Events = [new SubscriptionEvent { ViewId = 5, Type = EventType.Subscribed, ViewName = "test" }],
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 5,
                        Added = [new EntityDelta { Id = 100, Components = [new ComponentSnapshot { ComponentId = 1, Data = [1] }] }]
                    }
                ]
            });

            SpinWait.SpinUntil(() => sub.Entities.ContainsKey(100), TimeSpan.FromSeconds(3));

            // Remove entity
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 2,
                Views = [new ViewDeltaMessage { ViewId = 5, Removed = [100] }]
            });

            SpinWait.SpinUntil(() => removedId != 0, TimeSpan.FromSeconds(3));

            Assert.That(removedId, Is.EqualTo(100));
            Assert.That(sub.Entities.ContainsKey(100), Is.False);
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void SyncComplete_SetsFlagAndFiresCallback()
    {
        using var server = new FakeServer(TestPort + 3);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 3, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        try
        {
            var sub = conn.Subscribe("test");
            var syncComplete = false;
            sub.OnSyncComplete += () => syncComplete = true;

            // Subscribed
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 1,
                Events = [new SubscriptionEvent { ViewId = 5, Type = EventType.Subscribed, ViewName = "test" }]
            });

            // SyncComplete
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 2,
                Events = [new SubscriptionEvent { ViewId = 5, Type = EventType.SyncComplete }]
            });

            SpinWait.SpinUntil(() => syncComplete, TimeSpan.FromSeconds(3));

            Assert.That(sub.IsSynced, Is.True);
            Assert.That(syncComplete, Is.True);
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void Resync_ClearsCacheAndFiresCallback()
    {
        using var server = new FakeServer(TestPort + 4);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 4, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        try
        {
            var sub = conn.Subscribe("test");
            var resyncFired = false;
            sub.OnResync += () => resyncFired = true;

            // Subscribed + entity
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 1,
                Events = [new SubscriptionEvent { ViewId = 5, Type = EventType.Subscribed, ViewName = "test" }],
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 5,
                        Added = [new EntityDelta { Id = 100, Components = [new ComponentSnapshot { ComponentId = 1, Data = [1] }] }]
                    }
                ]
            });

            SpinWait.SpinUntil(() => sub.Entities.ContainsKey(100), TimeSpan.FromSeconds(3));

            // Resync
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 2,
                Events = [new SubscriptionEvent { ViewId = 5, Type = EventType.Resync }],
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 5,
                        Added = [new EntityDelta { Id = 200, Components = [new ComponentSnapshot { ComponentId = 1, Data = [2] }] }]
                    }
                ]
            });

            SpinWait.SpinUntil(() => resyncFired, TimeSpan.FromSeconds(3));

            Assert.That(resyncFired, Is.True);
            // Old entity 100 gone, new entity 200 from resync snapshot
            Assert.That(sub.Entities.ContainsKey(100), Is.False);
            Assert.That(sub.Entities.ContainsKey(200), Is.True);
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void OnTick_FiresWithCorrectTickNumber()
    {
        using var server = new FakeServer(TestPort + 5);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 5, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        try
        {
            var ticks = new List<long>();
            conn.OnTick += (_, tick) => ticks.Add(tick);

            server.SendTickDelta(new TickDeltaMessage { TickNumber = 100 });
            server.SendTickDelta(new TickDeltaMessage { TickNumber = 101 });
            server.SendTickDelta(new TickDeltaMessage { TickNumber = 102 });

            SpinWait.SpinUntil(() => ticks.Count >= 3, TimeSpan.FromSeconds(3));

            Assert.That(ticks, Is.EqualTo(new long[] { 100, 101, 102 }));
            Assert.That(conn.LastTickNumber, Is.EqualTo(102));
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void ServerPushesUnsubscribedView_CreatesLazySubscription()
    {
        using var server = new FakeServer(TestPort + 6);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 6, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        try
        {
            // Don't call Subscribe() — let the server push a View we didn't locally register
            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 1,
                Events = [new SubscriptionEvent { ViewId = 7, Type = EventType.Subscribed, ViewName = "surprise_view" }]
            });

            server.SendTickDelta(new TickDeltaMessage
            {
                TickNumber = 2,
                Views =
                [
                    new ViewDeltaMessage
                    {
                        ViewId = 7,
                        Added = [new EntityDelta { Id = 500, Components = [new ComponentSnapshot { ComponentId = 1, Data = [1] }] }]
                    }
                ]
            });

            // The lazy subscription should have been created
            SpinWait.SpinUntil(() => conn.GetSubscription("surprise_view") != null, TimeSpan.FromSeconds(3));

            var sub = conn.GetSubscription("surprise_view");
            Assert.That(sub, Is.Not.Null);

            SpinWait.SpinUntil(() => sub.Entities.ContainsKey(500), TimeSpan.FromSeconds(3));
            Assert.That(sub.Entities.ContainsKey(500), Is.True);
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Test]
    public void Disconnect_FiresCallback_StateUpdated()
    {
        using var server = new FakeServer(TestPort + 7);

        TyphonConnection conn = null;
        var connected = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            conn = TyphonClient.Connect("127.0.0.1", TestPort + 7, new TyphonConnectionOptions { AutoReconnect = false });
            connected.Set();
        });
        thread.Start();

        server.AcceptClient();
        connected.Wait(TimeSpan.FromSeconds(5));

        Exception disconnectEx = null;
        var disconnected = new ManualResetEventSlim();
        conn.OnDisconnected += (_, ex) =>
        {
            disconnectEx = ex;
            disconnected.Set();
        };

        // Server closes connection
        server.Dispose();

        var gotDisconnect = disconnected.Wait(TimeSpan.FromSeconds(5));
        Assert.That(gotDisconnect, Is.True, "OnDisconnected should fire");
        Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));

        conn.Dispose();
    }
}

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Pins the reconnect state machine on <see cref="AttachSessionRuntime"/>. When the engine drops
/// its end of the TCP socket, the runtime must:
///
///   1. Emit a <c>"reconnecting"</c> status via <see cref="AttachSessionRuntime.ConnectionStateChanged"/>.
///   2. Keep the runtime alive (no exceptions surfaced to the caller, no subscriber crash).
///
/// This is the contract the profiler panel's StatusPill watches — a regression that silently
/// stopped firing the "reconnecting" event would leave the pill stuck on "Connected" while the
/// underlying socket was dead, and the user would have no way to tell.
/// </summary>
[TestFixture]
public sealed class AttachSessionRuntimeReconnectTests
{
    [Test]
    public async Task ServerDropsConnection_RuntimeFiresReconnectingState()
    {
        var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(30),
            MaxBlocks = 100,
        };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);

        var stateLog = new ConcurrentQueue<string>();
        runtime.ConnectionStateChanged += status => stateLog.Enqueue(status);

        // Wait for at least one tick to confirm the stream is live before we cut it.
        var tickDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (runtime.TickCount < 1 && DateTime.UtcNow < tickDeadline)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.That(runtime.TickCount, Is.GreaterThanOrEqualTo(1),
            "mock server must emit at least one tick before we yank the connection");

        // Kill the server — the runtime's ReadLoopAsync sees an IOException / SocketException and
        // transitions to "reconnecting".
        await server.DisposeAsync();

        var reconnectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < reconnectDeadline)
        {
            if (stateLog.Contains("reconnecting") || runtime.ConnectionStatus == "reconnecting")
            {
                return;
            }
            await Task.Delay(25, cts.Token);
        }

        Assert.Fail(
            $"runtime did not transition to 'reconnecting' within 5 s. "
          + $"current status: {runtime.ConnectionStatus}, "
          + $"observed events: {string.Join(",", stateLog)}");
    }

    [Test]
    public async Task DisposeDuringReconnect_DoesNotHang()
    {
        // Start runtime → kill server → runtime is in reconnect loop (2 s delay). Calling
        // Dispose during that delay must cancel immediately, not block on the next Task.Delay
        // cycle (6 s worst case if cancellation isn't plumbed correctly). Catches a regression
        // where ReconnectDelayMs wasn't awaited with the cts token.
        var server = new MockTcpProfilerServer { MaxBlocks = 1 };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);

        // Let the stream briefly — enough for the single Init to land, then we dispose the server
        // and wait for the reconnect state to fire.
        await Task.Delay(200, cts.Token);
        await server.DisposeAsync();

        // Give the runtime ~1 s to enter reconnect mode, then dispose and time the return.
        await Task.Delay(500, cts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        runtime.Dispose();
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)),
            "Dispose must unblock the reconnect-delay wait promptly — a regression here would freeze session cleanup");
    }
}

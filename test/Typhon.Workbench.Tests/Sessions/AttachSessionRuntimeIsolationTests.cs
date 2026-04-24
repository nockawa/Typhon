using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Sessions.Profiler;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Isolation guard for <see cref="AttachSessionRuntime"/>. Before the runtime was rebuilt from a
/// singleton <c>LiveSessionService</c> into a per-session instance, a second attach would have
/// clobbered the first's tick counter / decoder / subscribers. This test spins up two independent
/// engine-like endpoints (two <see cref="MockTcpProfilerServer"/>s on different ports) and asserts
/// each runtime only sees its own ticks — a regression here would silently bleed data between
/// sessions.
/// </summary>
[TestFixture]
public sealed class AttachSessionRuntimeIsolationTests
{
    [Test]
    public async Task TwoRuntimes_BoundToDifferentEndpoints_HaveIndependentTickCountersAndSubscribers()
    {
        await using var serverA = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(20),
            MaxBlocks = 100,
        };
        await using var serverB = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(20),
            MaxBlocks = 100,
        };
        serverA.Start();
        serverB.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var a = await AttachSessionRuntime.StartAsync($"127.0.0.1:{serverA.Port}", NullLogger.Instance, cts.Token);
        using var b = await AttachSessionRuntime.StartAsync($"127.0.0.1:{serverB.Port}", NullLogger.Instance, cts.Token);

        var subscriberA = 0;
        var subscriberB = 0;
        LiveTickBatch latestA = null;
        LiveTickBatch latestB = null;
        a.TickReceived += batch => { Interlocked.Increment(ref subscriberA); latestA = batch; };
        b.TickReceived += batch => { Interlocked.Increment(ref subscriberB); latestB = batch; };

        // Both runtimes must independently pass 3 ticks. Use a shared deadline so a stuck runtime
        // doesn't mask the other's progress.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((a.TickCount < 3 || b.TickCount < 3) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, cts.Token);
        }

        Assert.That(a.TickCount, Is.GreaterThanOrEqualTo(3), "runtime A must receive ticks independently of B");
        Assert.That(b.TickCount, Is.GreaterThanOrEqualTo(3), "runtime B must receive ticks independently of A");

        Assert.That(subscriberA, Is.GreaterThanOrEqualTo(3));
        Assert.That(subscriberB, Is.GreaterThanOrEqualTo(3));

        Assert.That(latestA, Is.Not.Null);
        Assert.That(latestB, Is.Not.Null);
        Assert.That(ReferenceEquals(latestA, latestB), Is.False,
            "a shared singleton would have both subscribers seeing the same batch instance — this proves they don't");

        // Metadata references must be distinct — confirms BuildMetadataDto is per-instance, not a static cached singleton.
        Assert.That(ReferenceEquals(a.Metadata, b.Metadata), Is.False);
    }

    [Test]
    public async Task DisposingOneRuntime_DoesNotAffectTheOther()
    {
        await using var serverA = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(20),
            MaxBlocks = 100,
        };
        await using var serverB = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(20),
            MaxBlocks = 100,
        };
        serverA.Start();
        serverB.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var a = await AttachSessionRuntime.StartAsync($"127.0.0.1:{serverA.Port}", NullLogger.Instance, cts.Token);
        using var b = await AttachSessionRuntime.StartAsync($"127.0.0.1:{serverB.Port}", NullLogger.Instance, cts.Token);

        // Wait until both have at least one tick.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((a.TickCount < 1 || b.TickCount < 1) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.That(a.TickCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(b.TickCount, Is.GreaterThanOrEqualTo(1));

        // Tear down A. B must keep broadcasting and incrementing its counter.
        var bTickBeforeDispose = b.TickCount;
        a.Dispose();

        var bDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (b.TickCount <= bTickBeforeDispose && DateTime.UtcNow < bDeadline)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.That(b.TickCount, Is.GreaterThan(bTickBeforeDispose),
            "disposing A must not stall B's read loop");
    }
}

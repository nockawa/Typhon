using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Integration coverage for <see cref="AttachSessionRuntime"/> against the in-process
/// <see cref="MockTcpProfilerServer"/>. Binds the two ends of the live-profiler pipeline — real wire
/// framing, real decoder — so serialization bugs or missing projection fields surface in tests.
///
/// The three concerns locked down here:
/// <list type="bullet">
///   <item>First Init frame projects header fields onto the metadata DTO with the right values.</item>
///   <item>Block frames advance <see cref="AttachSessionRuntime.TickCount"/> and raise <c>TickReceived</c>.</item>
///   <item>Dispose tears down the read loop and socket without deadlocking on subscribers.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class AttachSessionRuntimeTests
{
    [Test]
    public async Task InitFrame_ProjectsHeaderFields_IntoMetadataDto()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0 };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);

        var metadata = await runtime.MetadataReady.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(metadata, Is.Not.Null);
        // MockTcpProfilerServer's Init uses TimestampFrequency = 10 MHz, BaseTickRate = 1 kHz, WorkerCount = 1.
        Assert.That(metadata.Header.TimestampFrequency, Is.EqualTo(10_000_000));
        Assert.That(metadata.Header.BaseTickRate, Is.EqualTo(1_000f));
        Assert.That(metadata.Header.WorkerCount, Is.EqualTo(1));
        Assert.That(metadata.Header.SystemCount, Is.EqualTo(0), "mock emits no system-definition table");
        Assert.That(metadata.Fingerprint, Is.Empty, "attach sessions never have a file-hash fingerprint");
    }

    [Test]
    public async Task BlockFrames_IncrementTickSummaries_AndFireTickSummaryAdded()
    {
        // #289: post-unification, "ticks received" means "TickSummaries the IncrementalCacheBuilder finalized". The
        // builder finalizes tick N when TickStart(N+1) arrives — so 5 blocks → 4 finalized + 1 trailing (closed by the
        // runtime's 250 ms trailing-tick timer once the stream goes quiet).
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(25),
            MaxBlocks = 5,
        };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);

        var tickEventCount = 0;
        runtime.TickSummaryAdded += _ => Interlocked.Increment(ref tickEventCount);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (runtime.TickCount < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }

        Assert.That(runtime.TickCount, Is.GreaterThanOrEqualTo(3),
            "mock emits 5 blocks at 25 ms cadence; at least 3 must finalize within 5 s");
        Assert.That(tickEventCount, Is.GreaterThanOrEqualTo(3),
            "TickSummaryAdded event must fire for every finalized tick");
    }

    [Test]
    public async Task Dispose_ClosesSocketAndCompletesMetadata_WithoutHanging()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 100, BlockInterval = TimeSpan.FromMilliseconds(50) };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);

        // Wait for at least one tick so the read loop is provably running.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (runtime.TickCount < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cts.Token);
        }
        Assert.That(runtime.TickCount, Is.GreaterThanOrEqualTo(1));

        // Dispose must return promptly — if the read loop holds a lock or awaits forever this
        // hangs. Guard with a hard wall-clock budget.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        runtime.Dispose();
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
            "Dispose must return quickly — a regression here would freeze session cleanup");

        // MetadataReady had already resolved before dispose; subsequent access must not throw.
        Assert.That(runtime.Metadata, Is.Not.Null);
    }
}

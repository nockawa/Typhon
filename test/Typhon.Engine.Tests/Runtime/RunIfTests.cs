using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests for runIf predicate integration — verifying that systems are properly skipped
/// when their runIf returns false, and that successors still dispatch.
/// </summary>
[TestFixture]
public class RunIfTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "RunIfTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    [Test]
    public void RunIfFalse_SystemSkipped_SuccessorsStillDispatch()
    {
        // A → B(runIf: false) → C
        // B should be skipped, but C must still execute
        var executed = new ConcurrentBag<string>();
        var captured = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Callback("A", _ => { if (captured == 0) { executed.Add("A"); } })
            .Callback("B", _ => { if (captured == 0) { executed.Add("B"); } },
                after: "A", runIf: () => false)
            .Callback("C", _ =>
            {
                if (captured == 0)
                {
                    executed.Add("C");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, after: "B")
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(executed, Does.Contain("A"), "A should execute");
        Assert.That(executed, Does.Not.Contain("B"), "B should be skipped (runIf: false)");
        Assert.That(executed, Does.Contain("C"), "C should execute (successor of skipped B)");
    }

    [Test]
    public void RunIfTrue_SystemExecutesNormally()
    {
        var executed = new ConcurrentBag<string>();
        var captured = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Callback("A", _ =>
            {
                if (captured == 0)
                {
                    executed.Add("A");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, runIf: () => true)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(executed, Does.Contain("A"));
    }

    [Test]
    public void RunIfNull_SystemAlwaysRuns()
    {
        var executed = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .Callback("A", _ => Interlocked.Increment(ref executed))
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(executed, Is.GreaterThanOrEqualTo(5));
    }

    [Test]
    public void RunIfFalse_TelemetryRecordsSkipped()
    {
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Callback("A", _ => { })
            .Callback("B", _ => { }, after: "A", runIf: () => false)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var systems = ring.GetSystemMetrics(ring.NewestTick);

        // System B (index 1) should be marked as skipped
        Assert.That(systems[1].WasSkipped, Is.True, "Skipped system should have WasSkipped=true");
    }

    [Test]
    public void RunIfFalse_PatateSystem_Skipped()
    {
        var chunkCount = 0;
        var outputExecuted = new ConcurrentBag<string>();
        var captured = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 4, BaseTickRate = 1000 })
            .Callback("Input", _ => { })
            .Patate("Physics", (_, chunk, total) =>
            {
                Interlocked.Increment(ref chunkCount);
            }, 100, after: "Input", runIf: () => false)
            .Callback("Output", _ =>
            {
                if (captured == 0)
                {
                    outputExecuted.Add("Output");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, after: "Physics")
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(chunkCount, Is.EqualTo(0), "Patate system with runIf:false should process zero chunks");
        Assert.That(outputExecuted, Does.Contain("Output"), "Output should execute after skipped Patate");
    }

    [Test]
    public void RunIf_DynamicPredicate_ChangesPerTick()
    {
        var ticksSeen = 0;
        var bExecutions = 0;

        // B runs only on odd ticks
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .Callback("A", _ => Interlocked.Increment(ref ticksSeen))
            .Callback("B", _ => Interlocked.Increment(ref bExecutions),
                after: "A", runIf: () => ticksSeen % 2 == 1)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 10, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // B should have executed roughly half the ticks
        Assert.That(bExecutions, Is.GreaterThan(0), "B should execute on some ticks");
        Assert.That(bExecutions, Is.LessThan(ticksSeen), "B should not execute on every tick");
    }
}

using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[NonParallelizable]
[TestFixture]
class ExceptionHandlingTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ExceptionTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    [Test]
    public void SystemException_WorkerSurvives_TickContinues()
    {
        var afterSystemCount = 0;

        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("test"))
            .CallbackSystem("After", _ => Interlocked.Increment(ref afterSystemCount));
        // "After" has no dependency on "Thrower", so it should still run

        using var scheduler = schedule.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(afterSystemCount, Is.GreaterThanOrEqualTo(3),
            "Independent system should continue executing after another system throws");
    }

    [Test]
    public void SystemException_SuccessorsSkipped()
    {
        var successorCount = 0;

        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("test"))
            .CallbackSystem("Successor", _ => Interlocked.Increment(ref successorCount), after: "Thrower");

        using var scheduler = schedule.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(successorCount, Is.EqualTo(0),
            "Successor of a throwing system should be skipped");
    }

    [Test]
    public void SystemException_TelemetryRecordsException()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule
            .CallbackSystem("Thrower", _ => throw new InvalidOperationException("test"))
            .CallbackSystem("Successor", _ => { }, after: "Thrower");

        using var scheduler = schedule.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 2, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        if (ring.TotalTicksRecorded > 0)
        {
            var systems = ring.GetSystemMetrics(ring.NewestTick);
            Assert.That(systems[0].SkipReason, Is.EqualTo(SkipReason.Exception),
                "Throwing system should have SkipReason.Exception");
            Assert.That(systems[1].SkipReason, Is.EqualTo(SkipReason.DependencyFailed),
                "Successor should have SkipReason.DependencyFailed");
        }
    }

    [Test]
    public void SystemException_IndependentBranchContinues()
    {
        var branch1Count = 0;
        var branch2Count = 0;
        var joinCount = 0;

        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule
            .CallbackSystem("Root", _ => { })
            .CallbackSystem("Branch1", _ =>
            {
                Interlocked.Increment(ref branch1Count);
                throw new InvalidOperationException("branch1 fails");
            }, after: "Root")
            .CallbackSystem("Branch2", _ => Interlocked.Increment(ref branch2Count), after: "Root")
            .CallbackSystem("Join", _ => Interlocked.Increment(ref joinCount), afterAll: ["Branch1", "Branch2"]);

        using var scheduler = schedule.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(branch1Count, Is.GreaterThanOrEqualTo(3), "Branch1 executes (then throws)");
        Assert.That(branch2Count, Is.GreaterThanOrEqualTo(3), "Branch2 should continue despite Branch1 failure");
        Assert.That(joinCount, Is.EqualTo(0), "Join should be skipped (Branch1 failed)");
    }
}

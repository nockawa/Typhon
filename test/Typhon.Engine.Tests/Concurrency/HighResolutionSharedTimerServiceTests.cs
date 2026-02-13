using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="HighResolutionSharedTimerService"/> (multi-callback, shared thread).
/// Covers: registration, dynamic next-tick, callback execution, cleanup, concurrency.
/// </summary>
[TestFixture]
public class HighResolutionSharedTimerServiceTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void Setup()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "SharedTimerTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry.Dispose();
    }

    [Test]
    public void Register_ReturnsActiveHandle()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        var reg = shared.Register("Test", Stopwatch.Frequency / 10, (_, _) => { });

        Assert.That(reg.IsActive, Is.True);
        Assert.That(reg.Name, Is.EqualTo("Test"));
        Assert.That(reg.IntervalTicks, Is.EqualTo(Stopwatch.Frequency / 10));

        reg.Dispose();
    }

    [Test]
    public void Dispose_DeactivatesHandle()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        var reg = shared.Register("Test", Stopwatch.Frequency / 10, (_, _) => { });

        Assert.That(reg.IsActive, Is.True);

        reg.Dispose();

        Assert.That(reg.IsActive, Is.False);
    }

    [Test]
    public void NoRegistrations_ThreadIdles()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        // No registrations → ActiveRegistrations should be 0
        Assert.That(shared.ActiveRegistrations, Is.EqualTo(0));

        // Timer should not be running before any registration
        Assert.That(shared.IsRunning, Is.False);
    }

    [Test]
    public void Register_StartsThread()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        var reg = shared.Register("LazyStart", Stopwatch.Frequency / 10, (_, _) => { });
        Thread.Sleep(50); // Give thread time to start

        Assert.That(shared.IsRunning, Is.True);
        Assert.That(shared.ActiveRegistrations, Is.EqualTo(1));

        reg.Dispose();
    }

    [Test]
    public void AllDisposed_CountDropsToZero()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        var reg1 = shared.Register("A", Stopwatch.Frequency / 10, (_, _) => { });
        var reg2 = shared.Register("B", Stopwatch.Frequency / 10, (_, _) => { });

        Assert.That(shared.ActiveRegistrations, Is.EqualTo(2));

        reg1.Dispose();
        reg2.Dispose();

        Assert.That(shared.ActiveRegistrations, Is.EqualTo(0));
    }

    [Test]
    public void Register_NullName_Throws()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        Assert.Throws<ArgumentNullException>(() => shared.Register(null, 1000, (_, _) => { }));
    }

    [Test]
    public void Register_NullCallback_Throws()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        Assert.Throws<ArgumentNullException>(() => shared.Register("Bad", 1000, null));
    }

    [Test]
    public void Register_ZeroInterval_Throws()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        Assert.Throws<ArgumentOutOfRangeException>(() => shared.Register("Bad", 0, (_, _) => { }));
    }

    [Test]
    [Category("Timing")]
    public void Shared_MultipleCallbacksFireIndependently()
    {
        var fastCount = 0L;
        var slowCount = 0L;
        using var ready = new ManualResetEventSlim(false);

        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        var fastReg = shared.Register("Fast", Stopwatch.Frequency / 100, // 10ms
            (_, _) =>
            {
                Interlocked.Increment(ref fastCount);
                if (Interlocked.Read(ref fastCount) >= 20 && Interlocked.Read(ref slowCount) >= 2)
                {
                    ready.Set();
                }
            });
        var slowReg = shared.Register("Slow", Stopwatch.Frequency / 10, // 100ms
            (_, _) =>
            {
                Interlocked.Increment(ref slowCount);
                if (Interlocked.Read(ref fastCount) >= 20 && Interlocked.Read(ref slowCount) >= 2)
                {
                    ready.Set();
                }
            });

        // Wait until both thresholds are met, with a safety timeout
        Assert.That(ready.Wait(2000), Is.True, "Callbacks did not reach thresholds within 2s");

        var fast = Interlocked.Read(ref fastCount);
        var slow = Interlocked.Read(ref slowCount);

        // Fast (10ms) should fire more often than Slow (100ms)
        Assert.That(fast, Is.GreaterThan(slow), "Fast callback should fire more often than slow");
        Assert.That(fast, Is.GreaterThanOrEqualTo(20), "Fast callback should fire at least 20 times");
        Assert.That(slow, Is.GreaterThanOrEqualTo(2), "Slow callback should fire at least twice");

        fastReg.Dispose();
        slowReg.Dispose();
    }

    [Test]
    [Category("Timing")]
    public void Shared_CallbackInvocationCountTracked()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        var reg = shared.Register("Tracked", Stopwatch.Frequency / 100, (_, _) => { }); // 10ms

        Thread.Sleep(200); // ~20 invocations

        Assert.That(reg.InvocationCount, Is.GreaterThan(0));

        reg.Dispose();
    }

    [Test]
    public void Callback_ExceptionDoesNotKillSharedTimer()
    {
        var goodCount = 0L;

        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        var badReg = shared.Register("Bad", Stopwatch.Frequency / 100,
            (_, _) => throw new InvalidOperationException("Test"));
        var goodReg = shared.Register("Good", Stopwatch.Frequency / 100,
            (_, _) => Interlocked.Increment(ref goodCount));

        Thread.Sleep(100);

        // Good callback should still fire despite bad callback throwing
        Assert.That(Interlocked.Read(ref goodCount), Is.GreaterThan(0), "Good callback should survive bad callback's exceptions");
        Assert.That(shared.IsRunning, Is.True);

        badReg.Dispose();
        goodReg.Dispose();
    }

    [Test]
    public void Registrations_SnapshotReturnsActiveOnly()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        var reg1 = shared.Register("A", Stopwatch.Frequency / 10, (_, _) => { });
        var reg2 = shared.Register("B", Stopwatch.Frequency / 10, (_, _) => { });
        var reg3 = shared.Register("C", Stopwatch.Frequency / 10, (_, _) => { });

        reg2.Dispose(); // Deactivate B

        var snapshot = shared.Registrations;

        Assert.That(snapshot.Count, Is.EqualTo(2));

        reg1.Dispose();
        reg3.Dispose();
    }

    [Test]
    public void ResourceTree_RegistersUnderTimer()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        Assert.That(shared.Parent, Is.SameAs(_registry.Timer));
        Assert.That(shared.Type, Is.EqualTo(ResourceType.Service));
        Assert.That(shared.Id, Is.EqualTo("SharedTimer"));
    }

    [Test]
    public void ConcurrentRegistration_NoExceptions()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        var registrations = new List<ITimerRegistration>();
        var lockObj = new object();

        // 10 threads registering concurrently
        var threads = new Thread[10];
        for (var i = 0; i < threads.Length; i++)
        {
            var idx = i;
            threads[i] = new Thread(() =>
            {
                var reg = shared.Register($"Thread{idx}", Stopwatch.Frequency / 10, (_, _) => { });
                lock (lockObj)
                {
                    registrations.Add(reg);
                }
            });
        }

        foreach (var t in threads)
        {
            t.Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(registrations.Count, Is.EqualTo(10));
        Assert.That(shared.ActiveRegistrations, Is.EqualTo(10));

        // Dispose all
        foreach (var reg in registrations)
        {
            reg.Dispose();
        }
    }

    [Test]
    public void RapidRegisterDispose_NoCrash()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);

        // Rapid register + dispose cycles
        for (var i = 0; i < 100; i++)
        {
            var reg = shared.Register($"Rapid{i}", Stopwatch.Frequency / 10, (_, _) => { });
            reg.Dispose();
        }

        // Should end with 0 active registrations
        Assert.That(shared.ActiveRegistrations, Is.EqualTo(0));
    }

    [Test]
    [Category("Timing")]
    public void SlowInvocationCount_Tracked()
    {
        using var shared = new HighResolutionSharedTimerService(_registry.Timer);
        using var ready = new ManualResetEventSlim(false);

        // Register a callback that deliberately takes >100µs
        ITimerRegistration reg = null;
        reg = shared.Register("SlowCallback", Stopwatch.Frequency / 100, // 10ms
            (_, _) =>
            {
                Thread.Sleep(1); // 1ms > 100µs threshold
                if (reg?.SlowInvocationCount > 0)
                {
                    ready.Set();
                }
            });

        Assert.That(ready.Wait(2000), Is.True, "Slow invocations were not tracked within 2s");
        Assert.That(reg.SlowInvocationCount, Is.GreaterThan(0), "Slow invocations should be tracked");

        reg.Dispose();
    }
}

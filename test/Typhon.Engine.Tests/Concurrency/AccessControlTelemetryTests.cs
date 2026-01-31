using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[TestFixture]
public class AccessControlTelemetryTests
{
    /// <summary>
    /// Mock implementation of IContentionTarget for testing telemetry callbacks.
    /// </summary>
    private class MockContentionTarget : IContentionTarget
    {
        private volatile TelemetryLevel _telemetryLevel;

        public TelemetryLevel TelemetryLevel
        {
            get => _telemetryLevel;
            set => _telemetryLevel = value;
        }

        public IResource OwningResource => null;

        public long ContentionCount;
        public long TotalWaitUs;
        public readonly List<(LockOperation Op, long DurationUs)> Operations = new();
        private readonly object _operationsLock = new();

        public void RecordContention(long waitUs)
        {
            Interlocked.Increment(ref ContentionCount);
            Interlocked.Add(ref TotalWaitUs, waitUs);
        }

        public void LogLockOperation(LockOperation operation, long durationUs)
        {
            lock (_operationsLock)
            {
                Operations.Add((operation, durationUs));
            }
        }

        public void Reset()
        {
            ContentionCount = 0;
            TotalWaitUs = 0;
            lock (_operationsLock)
            {
                Operations.Clear();
            }
        }
    }

    [Test]
    public void EnterExclusiveAccess_WithNullTarget_NoTelemetry()
    {
        var control = new AccessControl();

        // Should work without exceptions when target is null
        control.EnterExclusiveAccess(ref WaitContext.Null, target: null);
        control.ExitExclusiveAccess(target: null);
    }

    [Test]
    public void EnterSharedAccess_WithNullTarget_NoTelemetry()
    {
        var control = new AccessControl();

        // Should work without exceptions when target is null
        control.EnterSharedAccess(ref WaitContext.Null, target: null);
        control.ExitSharedAccess(target: null);
    }

    [Test]
    public void TelemetryLevel_None_NoCallbacks()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.None };

        control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
        control.ExitExclusiveAccess(target: target);

        Assert.That(target.ContentionCount, Is.EqualTo(0));
        Assert.That(target.Operations.Count, Is.EqualTo(0));
    }

    [Test]
    public void EnterExclusiveAccess_WithDeepMode_LogsOperations()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
        control.ExitExclusiveAccess(target: target);

        Assert.That(target.Operations.Count, Is.EqualTo(2));
        Assert.That(target.Operations[0].Op, Is.EqualTo(LockOperation.ExclusiveAcquired));
        Assert.That(target.Operations[1].Op, Is.EqualTo(LockOperation.ExclusiveReleased));
    }

    [Test]
    public void EnterSharedAccess_WithDeepMode_LogsOperations()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        control.EnterSharedAccess(ref WaitContext.Null, target: target);
        control.ExitSharedAccess(target: target);

        Assert.That(target.Operations.Count, Is.EqualTo(2));
        Assert.That(target.Operations[0].Op, Is.EqualTo(LockOperation.SharedAcquired));
        Assert.That(target.Operations[1].Op, Is.EqualTo(LockOperation.SharedReleased));
    }

    [Test]
    public void TryEnterExclusiveAccess_WithDeepMode_LogsAcquired()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        var result = control.TryEnterExclusiveAccess(target: target);
        Assert.That(result, Is.True);

        control.ExitExclusiveAccess(target: target);

        Assert.That(target.Operations.Count, Is.EqualTo(2));
        Assert.That(target.Operations[0].Op, Is.EqualTo(LockOperation.ExclusiveAcquired));
        Assert.That(target.Operations[1].Op, Is.EqualTo(LockOperation.ExclusiveReleased));
    }

    [Test]
    public void TryEnterExclusiveAccess_WhenLocked_NoDeepModeCallbacks()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        // Lock first
        control.EnterExclusiveAccess(ref WaitContext.Null);

        // Try should fail
        var result = control.TryEnterExclusiveAccess(target: target);
        Assert.That(result, Is.False);

        // No callbacks since we didn't acquire
        Assert.That(target.Operations.Count, Is.EqualTo(0));

        control.ExitExclusiveAccess();
    }

    [Test]
    public void DemoteFromExclusiveAccess_WithDeepMode_LogsDemote()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        control.EnterSharedAccess(ref WaitContext.Null, target: target);
        var promoted = control.TryPromoteToExclusiveAccess(ref WaitContext.Null, target: target);

        if (promoted)
        {
            control.DemoteFromExclusiveAccess(target: target);
            control.ExitSharedAccess(target: target);

            // Should contain: SharedAcquired, PromoteToExclusiveAcquired, DemoteToShared, SharedReleased
            Assert.That(target.Operations, Has.Some.Matches<(LockOperation Op, long DurationUs)>(x => x.Op == LockOperation.SharedAcquired));
            Assert.That(target.Operations, Has.Some.Matches<(LockOperation Op, long DurationUs)>(x => x.Op == LockOperation.PromoteToExclusiveAcquired));
            Assert.That(target.Operations, Has.Some.Matches<(LockOperation Op, long DurationUs)>(x => x.Op == LockOperation.DemoteToShared));
            Assert.That(target.Operations, Has.Some.Matches<(LockOperation Op, long DurationUs)>(x => x.Op == LockOperation.SharedReleased));
        }
        else
        {
            control.ExitSharedAccess(target: target);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithLightMode_RecordsContention()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Light };
        var barrier = new Barrier(2);

        // Thread 1 holds exclusive, Thread 2 waits
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
            barrier.SignalAndWait();
            Thread.Sleep(50);  // Hold lock
            control.ExitExclusiveAccess(target: target);
        });

        barrier.SignalAndWait();
        Thread.Sleep(10);  // Ensure T1 has lock

        control.EnterExclusiveAccess(ref WaitContext.Null, target: target);  // Should wait and record contention
        control.ExitExclusiveAccess(target: target);

        t1.Wait();

        // At least one thread should have recorded contention
        Console.WriteLine($"Contention count: {target.ContentionCount}");
        Console.WriteLine($"Total wait: {target.TotalWaitUs} µs");

        Assert.That(target.ContentionCount, Is.GreaterThan(0));
        Assert.That(target.TotalWaitUs, Is.GreaterThan(0));
    }

    [Test]
    [CancelAfter(5000)]
    public void EnterSharedAccess_WhenExclusiveHeld_WithLightMode_RecordsContention()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Light };
        var barrier = new Barrier(2);

        // Thread 1 holds exclusive, Thread 2 tries shared
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
            barrier.SignalAndWait();
            Thread.Sleep(50);  // Hold lock
            control.ExitExclusiveAccess(target: target);
        });

        barrier.SignalAndWait();
        Thread.Sleep(10);  // Ensure T1 has lock

        control.EnterSharedAccess(ref WaitContext.Null, target: target);  // Should wait and record contention
        control.ExitSharedAccess(target: target);

        t1.Wait();

        Console.WriteLine($"Contention count: {target.ContentionCount}");
        Console.WriteLine($"Total wait: {target.TotalWaitUs} µs");

        Assert.That(target.ContentionCount, Is.GreaterThan(0));
        Assert.That(target.TotalWaitUs, Is.GreaterThan(0));
    }

    [Test]
    [CancelAfter(10000)]
    public void ConcurrentAccess_WithLightModeTelemetry_RecordsContention()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Light };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; j < 100 && !cts.Token.IsCancellationRequested; j++)
            {
                var ctx = WaitContext.FromToken(cts.Token);
                control.EnterExclusiveAccess(ref ctx, target: target);
                Thread.SpinWait(10);
                control.ExitExclusiveAccess(target: target);
            }
        });

        // With 10 threads × 100 ops, there should be significant contention
        Console.WriteLine($"Contention count: {target.ContentionCount}");
        Console.WriteLine($"Total wait: {target.TotalWaitUs} µs");

        Assert.That(target.ContentionCount, Is.GreaterThan(0), "With concurrent exclusive access, there should be contention");
    }

    [Test]
    [CancelAfter(10000)]
    public void ConcurrentAccess_WithDeepModeTelemetry_LogsAllOperations()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var operationCount = 0;

        Parallel.For(0, 4, i =>
        {
            for (int j = 0; j < 25 && !cts.Token.IsCancellationRequested; j++)
            {
                var ctx = WaitContext.FromToken(cts.Token);
                control.EnterExclusiveAccess(ref ctx, target: target);
                Thread.SpinWait(10);
                control.ExitExclusiveAccess(target: target);
                Interlocked.Increment(ref operationCount);
            }
        });

        Console.WriteLine($"Operations performed: {operationCount}");
        Console.WriteLine($"Operations logged: {target.Operations.Count}");

        // Each operation cycle logs 2 events (Acquired + Released), plus potential wait events
        Assert.That(target.Operations.Count, Is.GreaterThanOrEqualTo(operationCount * 2),
            "Deep mode should log at least Acquired+Released for each operation");
    }

    [Test]
    public void MultipleSharedAccess_WithDeepMode_LogsAllOperations()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        // Multiple shared readers should all succeed
        control.EnterSharedAccess(ref WaitContext.Null, target: target);
        control.EnterSharedAccess(ref WaitContext.Null, target: target);
        control.EnterSharedAccess(ref WaitContext.Null, target: target);
        control.ExitSharedAccess(target: target);
        control.ExitSharedAccess(target: target);
        control.ExitSharedAccess(target: target);

        // Should have 6 operations: 3 acquired + 3 released
        Assert.That(target.Operations.Count, Is.EqualTo(6));

        var acquired = target.Operations.FindAll(o => o.Op == LockOperation.SharedAcquired);
        var released = target.Operations.FindAll(o => o.Op == LockOperation.SharedReleased);

        Assert.That(acquired.Count, Is.EqualTo(3));
        Assert.That(released.Count, Is.EqualTo(3));
    }

    [Test]
    public void MixedTelemetryLevels_OnlyDeepLogsOperations()
    {
        var control = new AccessControl();
        var lightTarget = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Light };
        var deepTarget = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };

        // Light mode: no operations logged
        control.EnterExclusiveAccess(ref WaitContext.Null, target: lightTarget);
        control.ExitExclusiveAccess(target: lightTarget);

        Assert.That(lightTarget.Operations.Count, Is.EqualTo(0), "Light mode should not log operations");

        // Deep mode: operations logged
        control.EnterExclusiveAccess(ref WaitContext.Null, target: deepTarget);
        control.ExitExclusiveAccess(target: deepTarget);

        Assert.That(deepTarget.Operations.Count, Is.EqualTo(2), "Deep mode should log operations");
    }

    [Test]
    public void TelemetryLevelChange_DynamicallyRespected()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.None };

        // Start with None - no logging
        control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
        control.ExitExclusiveAccess(target: target);
        Assert.That(target.Operations.Count, Is.EqualTo(0));

        // Change to Deep - should log
        target.TelemetryLevel = TelemetryLevel.Deep;
        control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
        control.ExitExclusiveAccess(target: target);
        Assert.That(target.Operations.Count, Is.EqualTo(2));

        // Change back to None - no more logging
        target.Reset();
        target.TelemetryLevel = TelemetryLevel.None;
        control.EnterExclusiveAccess(ref WaitContext.Null, target: target);
        control.ExitExclusiveAccess(target: target);
        Assert.That(target.Operations.Count, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(5000)]
    public void TimeoutWithTelemetry_LogsTimedOut()
    {
        var control = new AccessControl();
        var target = new MockContentionTarget { TelemetryLevel = TelemetryLevel.Deep };
        var barrier = new Barrier(2);

        // Thread 1 holds exclusive indefinitely
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref WaitContext.Null);
            barrier.SignalAndWait();
            Thread.Sleep(500);  // Hold lock longer than timeout
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();
        Thread.Sleep(10);  // Ensure T1 has lock

        // Thread 2 should timeout
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
        var result = control.EnterExclusiveAccess(ref ctx, target: target);

        Assert.That(result, Is.False, "Should have timed out");
        Assert.That(target.Operations, Has.Some.Matches<(LockOperation Op, long DurationUs)>(x => x.Op == LockOperation.ExclusiveWaitStart));
        Assert.That(target.Operations, Has.Some.Matches<(LockOperation Op, long DurationUs)>(x => x.Op == LockOperation.TimedOut));

        t1.Wait();
    }
}

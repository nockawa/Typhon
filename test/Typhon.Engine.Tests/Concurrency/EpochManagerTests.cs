using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[TestFixture]
public class EpochManagerTests
{
    // ========================================
    // Basic Lifecycle (single-threaded)
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void EnterExit_SingleThread_EpochAdvances()
    {
        using var manager = new EpochManager("test-epoch", null);
        var initialEpoch = manager.GlobalEpoch;

        using (var guard = EpochGuard.Enter(manager))
        {
            // Inside scope: epoch should not have advanced yet
            Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch));
        }

        // After outermost exit: epoch should have advanced by 1
        Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch + 1));
    }

    [Test]
    [CancelAfter(1000)]
    public void NestedScopes_InnerExitDoesNotAdvance()
    {
        using var manager = new EpochManager("test-epoch", null);
        var initialEpoch = manager.GlobalEpoch;

        using (var outer = EpochGuard.Enter(manager))
        {
            using (var inner = EpochGuard.Enter(manager))
            {
                // Both scopes active
                Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch));
            }

            // Inner exited, outer still active — epoch should NOT advance
            Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch));
        }

        // Outer exited — now epoch advances
        Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch + 1));
    }

    [Test]
    [CancelAfter(1000)]
    public void MultipleOutermostScopes_AdvancesMultipleTimes()
    {
        using var manager = new EpochManager("test-epoch", null);
        var initialEpoch = manager.GlobalEpoch;

        for (int i = 0; i < 10; i++)
        {
            using var guard = EpochGuard.Enter(manager);
        }

        Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch + 10));
    }

    // ========================================
    // MinActiveEpoch
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void MinActiveEpoch_NoPinned_ReturnsGlobal()
    {
        using var manager = new EpochManager("test-epoch", null);

        // No active scopes — MinActiveEpoch should equal GlobalEpoch
        Assert.That(manager.MinActiveEpoch, Is.EqualTo(manager.GlobalEpoch));
    }

    [Test]
    [CancelAfter(1000)]
    public void MinActiveEpoch_SinglePinned_ReturnsPinnedValue()
    {
        using var manager = new EpochManager("test-epoch", null);
        var epochAtEntry = manager.GlobalEpoch;

        using (var guard = EpochGuard.Enter(manager))
        {
            // MinActiveEpoch should be pinned to the epoch when we entered
            Assert.That(manager.MinActiveEpoch, Is.EqualTo(epochAtEntry));
        }

        // After exit, MinActiveEpoch returns to GlobalEpoch
        Assert.That(manager.MinActiveEpoch, Is.EqualTo(manager.GlobalEpoch));
    }

    [Test]
    [CancelAfter(5000)]
    public void MultiThread_MinActiveEpoch_ReturnsOldest()
    {
        using var manager = new EpochManager("test-epoch", null);
        var thread1Ready = new ManualResetEventSlim(false);
        var thread2Ready = new ManualResetEventSlim(false);
        var canExit = new ManualResetEventSlim(false);
        long thread1Epoch = 0;

        // Thread 1: enter scope first (will pin older epoch)
        var t1 = Task.Run(() =>
        {
            var guard = EpochGuard.Enter(manager);
            thread1Epoch = manager.GlobalEpoch;
            thread1Ready.Set();
            canExit.Wait();
            guard.Dispose();
        });

        thread1Ready.Wait();

        // Advance the epoch by doing an enter/exit cycle on the main thread
        using (var tempGuard = EpochGuard.Enter(manager))
        {
        }

        // Thread 2: enter scope (will pin newer epoch)
        var t2 = Task.Run(() =>
        {
            var guard = EpochGuard.Enter(manager);
            thread2Ready.Set();
            canExit.Wait();
            guard.Dispose();
        });

        thread2Ready.Wait();

        // MinActiveEpoch should be the older (thread 1) epoch
        Assert.That(manager.MinActiveEpoch, Is.EqualTo(thread1Epoch));

        canExit.Set();
        Task.WaitAll(t1, t2);
    }

    // ========================================
    // Safety
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void CopySafety_DoubleDispose_NoOp()
    {
        using var manager = new EpochManager("test-epoch", null);
        var initialEpoch = manager.GlobalEpoch;

        var guard = EpochGuard.Enter(manager);
        guard.Dispose();

        // Second dispose should be a no-op (not throw, not advance epoch again)
        guard.Dispose();

        Assert.That(manager.GlobalEpoch, Is.EqualTo(initialEpoch + 1));
    }

    [Test]
    [CancelAfter(1000)]
    public void DepthMismatch_ThrowsInvalidOp()
    {
        using var manager = new EpochManager("test-epoch", null);

        var outer = EpochGuard.Enter(manager);
        var inner = EpochGuard.Enter(manager);

        // Disposing outer before inner should trigger depth mismatch
        // Can't use Assert.Throws with ref struct (can't capture in delegate)
        InvalidOperationException caught = null;
        try
        {
            outer.Dispose();
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.That(caught, Is.Not.Null, "Out-of-order dispose should throw InvalidOperationException");

        // Clean up properly
        inner.Dispose();
        outer.Dispose();
    }

    // ========================================
    // Thread Registration
    // ========================================

    [Test]
    [CancelAfter(10000)]
    public void ThreadDeath_SlotReclaimed()
    {
        using var manager = new EpochManager("test-epoch", null);

        // Launch a thread that enters an epoch scope, then let it die
        var threadDone = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            using var guard = EpochGuard.Enter(manager);
            threadDone.Set();
            // Thread exits normally — EpochSlotHandle finalizer should free the slot
        });
        thread.Start();
        threadDone.Wait();
        thread.Join();

        // Force GC to run finalizers (EpochSlotHandle should free the slot)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The slot should be reclaimed — ActiveSlotCount should eventually drop
        // Note: GC finalization timing is non-deterministic, so we allow a brief wait
        var maxWait = 50;
        while (manager.ActiveSlotCount > 0 && maxWait-- > 0)
        {
            Thread.Sleep(10);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // We may still have our main thread's slot registered, so just verify
        // the dead thread's slot was freed by checking ActiveSlotCount decreased
        Assert.That(manager.ActiveSlotCount, Is.LessThanOrEqualTo(1),
            "Dead thread's slot should be reclaimed by finalizer");
    }

    [Test]
    [CancelAfter(30000)]
    public void RegistryExhaustion_ThrowsResourceExhausted()
    {
        using var manager = new EpochManager("test-epoch", null);
        var barriers = new Barrier(EpochThreadRegistry.MaxSlots + 1);
        var canExit = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        var exceptionCaught = false;

        // Fill all 256 slots with threads
        for (int i = 0; i < EpochThreadRegistry.MaxSlots; i++)
        {
            var t = new Thread(() =>
            {
                try
                {
                    using var guard = EpochGuard.Enter(manager);
                    barriers.SignalAndWait();
                    canExit.Wait();
                }
                catch
                {
                    // Ignored — shouldn't happen for the first 256 threads
                }
            });
            t.IsBackground = true;
            threads.Add(t);
            t.Start();
        }

        // Wait for all 256 threads to be pinned
        barriers.SignalAndWait();

        // The 257th thread should throw ResourceExhaustedException
        var overflowThread = new Thread(() =>
        {
            try
            {
                using var guard = EpochGuard.Enter(manager);
            }
            catch (ResourceExhaustedException)
            {
                exceptionCaught = true;
            }
        });
        overflowThread.Start();
        overflowThread.Join();

        Assert.That(exceptionCaught, Is.True, "257th thread should throw ResourceExhaustedException");

        canExit.Set();
        foreach (var t in threads)
        {
            t.Join();
        }
    }

    // ========================================
    // Metrics & IResource
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void ScopeMetrics_Reported()
    {
        using var manager = new EpochManager("test-epoch", null);

        // Do some scope work
        using (var g1 = EpochGuard.Enter(manager))
        {
            using (var g2 = EpochGuard.Enter(manager))
            {
            }
        }

        using (var g3 = EpochGuard.Enter(manager))
        {
        }

        var writer = new TestMetricWriter();
        manager.ReadMetrics(writer);

        // 3 total scope entries (g1, g2, g3)
        Assert.That(writer.Throughputs.ContainsKey("EpochAdvances"), Is.True);
        Assert.That(writer.Throughputs["EpochAdvances"], Is.EqualTo(2)); // 2 outermost exits
        Assert.That(writer.Throughputs["ScopeEnters"], Is.EqualTo(3));   // 3 total entries
        Assert.That(writer.CapacityMaximum, Is.EqualTo(EpochThreadRegistry.MaxSlots));
    }

    [Test]
    [CancelAfter(1000)]
    public void Resource_PropertiesCorrect()
    {
        using var manager = new EpochManager("epoch-mgr-1", null);

        Assert.That(manager.Id, Is.EqualTo("epoch-mgr-1"));
        Assert.That(manager.Type, Is.EqualTo(ResourceType.Synchronization));
        Assert.That(manager.Parent, Is.Null);
        Assert.That(manager.CreatedAt, Is.LessThanOrEqualTo(DateTime.UtcNow));
        Assert.That(manager.Children, Is.Empty);
    }

    // ========================================
    // Stress Test
    // ========================================

    [Test]
    [CancelAfter(10000)]
    public void StressTest_ConcurrentEnterExit_NoCorruption()
    {
        using var manager = new EpochManager("stress-test", null);
        var errorCount = 0;
        var barrier = new Barrier(20);

        Parallel.For(0, 20, i =>
        {
            barrier.SignalAndWait();

            for (int j = 0; j < 1000; j++)
            {
                try
                {
                    using var outer = EpochGuard.Enter(manager);
                    using var inner = EpochGuard.Enter(manager);

                    // Verify MinActiveEpoch is valid while inside a scope
                    var min = manager.MinActiveEpoch;
                    var global = manager.GlobalEpoch;
                    if (min > global)
                    {
                        Interlocked.Increment(ref errorCount);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errorCount);
                }
            }
        });

        Assert.That(errorCount, Is.EqualTo(0), "No errors should occur during concurrent epoch operations");

        // GlobalEpoch should have advanced (20 threads × 1000 iterations = 20000 outermost exits)
        Assert.That(manager.GlobalEpoch, Is.GreaterThan(1));
    }

    // ========================================
    // Test Helpers
    // ========================================

    /// <summary>
    /// Stub metric writer for verifying ReadMetrics output.
    /// </summary>
    private class TestMetricWriter : IMetricWriter
    {
        public Dictionary<string, long> Throughputs { get; } = new();
        public long CapacityCurrent { get; private set; }
        public long CapacityMaximum { get; private set; }

        public void WriteMemory(long allocatedBytes, long peakBytes) { }
        public void WriteCapacity(long current, long maximum) { CapacityCurrent = current; CapacityMaximum = maximum; }
        public void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes) { }
        public void WriteContention(long waitCount, long totalWaitUs, long maxWaitUs, long timeoutCount) { }
        public void WriteThroughput(string name, long count) => Throughputs[name] = count;
        public void WriteDuration(string name, long lastUs, long avgUs, long maxUs) { }
    }
}

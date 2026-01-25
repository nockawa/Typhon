using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Misc;

class AccessControlTests
{
    [Test]
    public void AccessControl_DiagnosticDeadlock()
    {
        var control = new AccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 100; j++)
                {
                    if (i % 2 == 0)
                    {
                        control.EnterExclusiveAccess();
                        Thread.SpinWait(10);
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        Thread.SpinWait(10);
                        control.ExitSharedAccess();
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected");
    }

    /// <summary>
    /// Simulates the TransactionChain pattern: rapid exclusive access (CreateTransaction/Remove)
    /// with occasional shared access (WalkHeadToTail)
    /// </summary>
    [Test]
    public void AccessControl_TransactionChainPattern()
    {
        var control = new AccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;
        var operationCount = 0;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 1000; j++)
                {
                    if (j % 10 == 0)
                    {
                        // Occasional shared access (like WalkHeadToTail)
                        control.EnterSharedAccess();
                        Thread.SpinWait(5);
                        control.ExitSharedAccess();
                    }
                    else
                    {
                        // Frequent exclusive access (like CreateTransaction/Remove)
                        control.EnterExclusiveAccess();
                        Thread.SpinWait(2);
                        control.ExitExclusiveAccess();
                    }

                    Interlocked.Increment(ref operationCount);
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE after {operationCount} operations:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Console.WriteLine($"Completed {operationCount} operations");
        Assert.That(hangDetected, Is.False, "Deadlock detected in TransactionChain pattern");
    }

    /// <summary>
    /// Heavy contention test - all threads trying to acquire locks simultaneously
    /// </summary>
    [Test]
    public void AccessControl_HeavyContention()
    {
        var control = new AccessControl();
        var barrier = new Barrier(20);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 100; j++)
                {
                    // Synchronize all threads to maximize contention
                    barrier.SignalAndWait(cts.Token);

                    if (i % 3 == 0)
                    {
                        control.EnterExclusiveAccess();
                        Thread.SpinWait(1);
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        Thread.SpinWait(1);
                        control.ExitSharedAccess();
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected under heavy contention");
    }

    /// <summary>
    /// Test with very rapid lock cycling to stress the double-check pattern
    /// </summary>
    [Test]
    public void AccessControl_RapidCycling()
    {
        var control = new AccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 10000; j++)
                {
                    if (i % 2 == 0)
                    {
                        control.EnterExclusiveAccess();
                        // No delay - immediate release
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        // No delay - immediate release
                        control.ExitSharedAccess();
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected during rapid cycling");
    }

    // ========================================
    // AccessControl2 Tests (Corrected Version)
    // ========================================

    [Test]
    public void AccessControl2_DiagnosticDeadlock()
    {
        var control = new AccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 100; j++)
                {
                    if (i % 2 == 0)
                    {
                        control.EnterExclusiveAccess();
                        Thread.SpinWait(10);
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        Thread.SpinWait(10);
                        control.ExitSharedAccess();
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected in AccessControl2");
    }

    [Test]
    public void AccessControl2_TransactionChainPattern()
    {
        var control = new AccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;
        var operationCount = 0;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 1000; j++)
                {
                    if (j % 10 == 0)
                    {
                        // Occasional shared access (like WalkHeadToTail)
                        control.EnterSharedAccess();
                        Thread.SpinWait(5);
                        control.ExitSharedAccess();
                    }
                    else
                    {
                        // Frequent exclusive access (like CreateTransaction/Remove)
                        control.EnterExclusiveAccess();
                        Thread.SpinWait(2);
                        control.ExitExclusiveAccess();
                    }

                    Interlocked.Increment(ref operationCount);
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE after {operationCount} operations:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Console.WriteLine($"Completed {operationCount} operations");
        Assert.That(hangDetected, Is.False, "Deadlock detected in AccessControl2 TransactionChain pattern");
    }

    [Test]
    public unsafe void AccessControl2_HeavyContention()
    {
        var control = new AccessControl();
        var barrier = new Barrier(20);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 100; j++)
                {
                    // Synchronize all threads to maximize contention
                    barrier.SignalAndWait(cts.Token);

                    if (i % 3 == 0)
                    {
                        control.EnterExclusiveAccess();
                        Thread.SpinWait(1);
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        Thread.SpinWait(1);
                        control.ExitSharedAccess();
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected in AccessControl2 under heavy contention");
    }

    [Test]
    public void AccessControl2_RapidCycling()
    {
        var control = new NewAccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 10, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 1500; j++)
                {
                    if (i % 2 == 0)
                    {
                        control.EnterExclusiveAccess();
                        // No delay - immediate release
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        // No delay - immediate release
                        control.ExitSharedAccess();
                    }
                    //Thread.Sleep(1);
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: {control.LockedByThreadId}");
            Console.WriteLine($"  SharedUsedCounter: {control.SharedUsedCounter}");
        }
        
#if TELEMETRY
        Console.WriteLine($"Allocator {AccessControlImpl.Allocator.AllocatedCount} blocks");
#endif

        Assert.That(hangDetected, Is.False, "Deadlock detected in AccessControl2 during rapid cycling");
    }
    
    [Test]
    public unsafe void NewAccessControl_HeavyContention()
    {
        var control = new NewAccessControl();
        var barrier = new Barrier(10);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 10, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 100; j++)
                {
                    // Synchronize all threads to maximize contention
                    barrier.SignalAndWait(cts.Token);

                    if (i % 3 == 0)
                    {
                        control.EnterExclusiveAccess(token: cts.Token);
                        Thread.SpinWait(1);
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess(token: cts.Token);
                        Thread.SpinWait(1);
                        control.ExitSharedAccess();
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: XXX");
            Console.WriteLine($"  SharedUsedCounter: XXX");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected in AccessControl2 under heavy contention");
    }

    [Test]
    public void NewAccessControl_RapidCycling()
    {
        var control = new AccessControl();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hangDetected = false;

        try
        {
            Parallel.For(0, 20, new ParallelOptions { CancellationToken = cts.Token }, i =>
            {
                for (int j = 0; j < 10; j++)
                {
                    if (i % 2 == 0)
                    {
                        control.EnterExclusiveAccess();
                        // No delay - immediate release
                        control.ExitExclusiveAccess();
                    }
                    else
                    {
                        control.EnterSharedAccess();
                        // No delay - immediate release
                        control.ExitSharedAccess();
                    }
                    Thread.Sleep(0);
                }
            });
        }
        catch (OperationCanceledException)
        {
            hangDetected = true;
            Console.WriteLine($"HUNG STATE:");
            Console.WriteLine($"  LockedByThreadId: XXX");
            Console.WriteLine($"  SharedUsedCounter: XXX");
        }

        Assert.That(hangDetected, Is.False, "Deadlock detected in AccessControl2 during rapid cycling");
    }

    [Test]
    public void NewAccessControl_Simple()
    {
        var control = new AccessControl();

        control.EnterSharedAccess();
        control.EnterSharedAccess();
        control.ExitSharedAccess();
        control.EnterSharedAccess();
        control.ExitSharedAccess();
        control.ExitSharedAccess();
    }

    // ========================================
    // LockData Tests (TELEMETRY version)
    // ========================================

#if TELEMETRY
    [Test]
    public void LockData_PromoterWaiters_GetSet()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        lockData.PromoterWaiters = 0;
        Assert.That(lockData.PromoterWaiters, Is.EqualTo(0));

        lockData.PromoterWaiters = 1;
        Assert.That(lockData.PromoterWaiters, Is.EqualTo(1));

        lockData.PromoterWaiters = 255; // Max value for 8 bits
        Assert.That(lockData.PromoterWaiters, Is.EqualTo(255));
    }

    [Test]
    public void LockData_ThreadId_GetSet()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        lockData.ThreadId = 0;
        Assert.That(lockData.ThreadId, Is.EqualTo(0));

        lockData.ThreadId = 1;
        Assert.That(lockData.ThreadId, Is.EqualTo(1));

        lockData.ThreadId = 1023; // Max value for 10 bits
        Assert.That(lockData.ThreadId, Is.EqualTo(1023));
    }

    [Test]
    public void LockData_State_GetSet()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        // Test all three states
        lockData.State = 0x0000_0000_0000_0000; // IdleState
        Assert.That(lockData.State, Is.EqualTo(0x0000_0000_0000_0000UL));

        lockData.State = 0x8000_0000_0000_0000; // SharedState
        Assert.That(lockData.State, Is.EqualTo(0x8000_0000_0000_0000UL));

        lockData.State = 0x4000_0000_0000_0000; // ExclusiveState
        Assert.That(lockData.State, Is.EqualTo(0x4000_0000_0000_0000UL));

        // Verify other fields are not affected
        lockData.SharedCounter = 100;
        lockData.State = 0x8000_0000_0000_0000;
        Assert.That(lockData.SharedCounter, Is.EqualTo(100), "State modification should not affect Counter");
    }

    [Test]
    public void LockData_AllFields_IndependentModification()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        // Set all fields to distinct values
        lockData.SharedCounter = 42;
        lockData.ExclusiveWaiters = 200;
        lockData.PromoterWaiters = 150;
        lockData.ThreadId = 500;
        lockData.State = 0x8000_0000_0000_0000; // SharedState

        // Verify all values are preserved
        Assert.That(lockData.SharedCounter, Is.EqualTo(42));
        Assert.That(lockData.ExclusiveWaiters, Is.EqualTo(200));
        Assert.That(lockData.PromoterWaiters, Is.EqualTo(150));
        Assert.That(lockData.ThreadId, Is.EqualTo(500));
        Assert.That(lockData.State, Is.EqualTo(0x8000_0000_0000_0000UL));
    }

    [Test]
    public void LockData_TryUpdate_SucceedsWhenUnchanged()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        // Modify staging and try to update
        lockData.SharedCounter = 1;
        lockData.State = 0x8000_0000_0000_0000;

        bool result = lockData.TryUpdate();

        Assert.That(result, Is.True, "TryUpdate should succeed when underlying data hasn't changed");
        Assert.That(data, Is.EqualTo(lockData.Staging), "Underlying data should match staging after successful update");
    }

    [Test]
    public void LockData_TryUpdate_FailsWhenConcurrentlyModified()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        // Modify staging
        lockData.SharedCounter = 1;

        // Simulate concurrent modification
        data = 0x8000_0000_0000_0001; // Different value

        bool result = lockData.TryUpdate();

        Assert.That(result, Is.False, "TryUpdate should fail when underlying data was modified");
    }

    [Test]
    public void LockData_OperationsBlockId_AllocatesOnFirstAccess()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        // First access should allocate
        int blockId = lockData.OperationsBlockId;

        Assert.That(blockId, Is.GreaterThan(0), "Block ID should be allocated (> 0)");

        // Second access should return same value (from staging)
        int blockId2 = lockData.OperationsBlockId;
        Assert.That(blockId2, Is.EqualTo(blockId), "Subsequent access should return same block ID");
    }

    [Test]
    public void LockData_OperationsBlockId_FreesOnFailedUpdate()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;

        var initialAllocatedCount = allocator.AllocatedCount;

        var lockData = new AccessControlImpl.LockData(allocator, ref data);
        
        Assert.That(allocator.AllocatedCount, Is.GreaterThan(initialAllocatedCount),
            "Block should be allocated");

        // Simulate concurrent modification to cause TryUpdate to fail
        data = 0xFFFF_FFFF_FFFF_FFFF;

        lockData.TryUpdate();

        // The allocated block should be freed
        Assert.That(allocator.AllocatedCount, Is.EqualTo(initialAllocatedCount),
            "Block should be freed after failed update");
    }

    [Test]
    public void LockData_IsIdleNoWaiters_DetectsCorrectly()
    {
        var allocator = new ChainedBlockAllocator<AccessOperations>(1024);
        ulong data = 0;
        var lockData = new AccessControlImpl.LockData(allocator, ref data);

        // Initially should be idle with no waiters
        Assert.That(lockData.IsIdleNoWaiters, Is.True);

        // Add counter - should no longer be idle
        lockData.SharedCounter = 1;
        Assert.That(lockData.IsIdleNoWaiters, Is.False);

        // Reset and add shared waiters
        lockData = new AccessControlImpl.LockData(allocator, ref data);
        lockData.SharedCounter = 1;
        Assert.That(lockData.IsIdleNoWaiters, Is.False);

        // Reset and change state
        lockData = new AccessControlImpl.LockData(allocator, ref data);
        lockData.State = 0x8000_0000_0000_0000;
        Assert.That(lockData.IsIdleNoWaiters, Is.False);

        // OperationsBlockId should not affect idle status (it's excluded from the check)
        lockData = new AccessControlImpl.LockData(allocator, ref data);
        _ = lockData.OperationsBlockId; // Trigger allocation
        Assert.That(lockData.IsIdleNoWaiters, Is.True,
            "OperationsBlockId should not affect IsIdleNoWaiters");
    }

#endif

    // ========================================
    // NewAccessControl Comprehensive Tests
    // ========================================

    [Test]
    public void NewAccessControl_BasicSharedAccess()
    {
        var control = new AccessControl();

        // Single shared access
        control.EnterSharedAccess();
        control.ExitSharedAccess();

        // Multiple nested shared access
        control.EnterSharedAccess();
        control.EnterSharedAccess();
        control.EnterSharedAccess();
        control.ExitSharedAccess();
        control.ExitSharedAccess();
        control.ExitSharedAccess();
    }

    [Test]
    public void NewAccessControl_BasicExclusiveAccess()
    {
        var control = new AccessControl();

        // Single exclusive access
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();

        // Multiple times in sequence
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();
    }

    [Test]
    public void NewAccessControl_AlternatingAccess()
    {
        var control = new AccessControl();

        // Alternate between shared and exclusive
        control.EnterSharedAccess();
        control.ExitSharedAccess();

        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();

        control.EnterSharedAccess();
        control.ExitSharedAccess();

        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();
    }

    [Test]
    [CancelAfter(5000)]
    public void NewAccessControl_ConcurrentSharedAccess(CancellationToken token)
    {
        var control = new NewAccessControl();

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; (j < 100) && !token.IsCancellationRequested; j++)
            {
                if (!control.EnterSharedAccess(token: token))
                {
                    return;
                }
                Thread.SpinWait(10);
                control.ExitSharedAccess();
            }
        });
    }

    [Test]
    [CancelAfter(5000)]
    public void NewAccessControl_ConcurrentExclusiveAccess(CancellationToken token)
    {
        var control = new NewAccessControl();
        var counter = 0;
        
        Parallel.For(0, 10, i =>
        {
            for (int j = 0; (j < 100) && !token.IsCancellationRequested; j++)
            {
                if (!control.EnterExclusiveAccess(token: token))
                {
                    return;
                }

                var temp = counter;
                Thread.SpinWait(5);
                counter = temp + 1;
                
                control.ExitExclusiveAccess();
            }
        });

        Assert.That(counter, Is.EqualTo(1000), "Exclusive access should prevent race conditions");
    }

    [Test]
    [CancelAfter(10000)]
    public void NewAccessControl_MixedConcurrentAccess(CancellationToken token)
    {
        var control = new NewAccessControl();
        var sharedCounter = 0;
        var exclusiveCounter = 0;

        Parallel.For(0, 20, i =>
        {
            for (int j = 0; j < 50; j++)
            {
                if (i % 2 == 0)
                {
                    // Even threads use exclusive access
                    if (!control.EnterExclusiveAccess(token: token))
                    {
                        break;
                    }
                    Thread.SpinWait(5);
                    Interlocked.Increment(ref exclusiveCounter);
                    control.ExitExclusiveAccess();
                }
                else
                {
                    // Odd threads use shared access
                    if (!control.EnterSharedAccess(token: token))
                    {
                        break;
                    }
                    Thread.SpinWait(5);
                    Interlocked.Increment(ref sharedCounter);
                    control.ExitSharedAccess();
                }
            }
        });

        Assert.That(exclusiveCounter, Is.EqualTo(500), "Expected 500 exclusive operations");
        Assert.That(sharedCounter, Is.EqualTo(500), "Expected 500 shared operations");
    }

    [Test]
    [CancelAfter(2000)]
    public void NewAccessControl_StressTest_RapidAcquisitionRelease(CancellationToken token)
    {
        var control = new NewAccessControl();
        var operationCount = 0;

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; j < 200; j++)
            {
                if (j % 2 == 0)
                {
                    if (!control.EnterExclusiveAccess(token: token))
                    {
                        break;
                    }
                    control.ExitExclusiveAccess();
                    //Thread.Sleep(0);
                }
                else
                {
                    if (!control.EnterSharedAccess(token: token))
                    {
                        break;
                    }
                    control.ExitSharedAccess();
                }

                Interlocked.Increment(ref operationCount);
            }
        });

        Console.WriteLine($"Completed {operationCount} operations");
        Assert.That(operationCount, Is.EqualTo(2000), "Expected all operations to complete");
    }

    [Test]
    [CancelAfter(1000)]
    public void NewAccessControl_HighContentionBarrier()
    {
        var control = new AccessControl();
        var barrier = new Barrier(10);

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; j < 50; j++)
            {
                // All threads arrive at the same time for maximum contention
                barrier.SignalAndWait();

                if (i % 3 == 0)
                {
                    control.EnterExclusiveAccess();
                    Thread.SpinWait(1);
                    control.ExitExclusiveAccess();
                }
                else
                {
                    control.EnterSharedAccess();
                    Thread.SpinWait(1);
                    control.ExitSharedAccess();
                }
            }
        });
    }

    [Test]
    public void NewAccessControl_NestedSharedAccess_Sequential()
    {
        var control = new AccessControl();

        // Test that shared access can be nested on the same thread
        control.EnterSharedAccess();
        control.EnterSharedAccess();
        control.EnterSharedAccess();

        control.ExitSharedAccess();
        control.ExitSharedAccess();
        control.ExitSharedAccess();
    }

    [Test]
    [CancelAfter(5000)]
    public void NewAccessControl_TransitionFromSharedToExclusive()
    {
        var control = new AccessControl();
        var success = true;

        var task = Task.Run(() =>
        {
            try
            {
                // Thread 1: Hold shared access
                control.EnterSharedAccess();
                Thread.Sleep(100); // Hold it for a bit
                control.ExitSharedAccess();
            }
            catch (Exception)
            {
                success = false;
            }
        });

        // Thread 2: Wait a bit, then try to get exclusive access
        Thread.Sleep(50);
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();

        task.Wait();

        Assert.That(success, Is.True, "Transition from shared to exclusive should work correctly");
    }

    [Test]
    [CancelAfter(5000)]
    public void NewAccessControl_StateTransitions()
    {
        var control = new AccessControl();

        // Idle -> Shared -> Idle
        control.EnterSharedAccess();
        control.ExitSharedAccess();

        // Idle -> Exclusive -> Idle
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();

        // Idle -> Shared (x2) -> Exclusive (should wait) -> Idle
        control.EnterSharedAccess();
        control.EnterSharedAccess();

        var t1 = Task.Run(() =>
        {
            Thread.Sleep(50);
            control.ExitSharedAccess();
            control.ExitSharedAccess();
        });

        Thread.Sleep(25);
        control.EnterExclusiveAccess(); // Should wait for shared to clear
        control.ExitExclusiveAccess();

        t1.Wait();
    }

    [Test]
    public void NewAccessControl_Reset()
    {
        var control = new AccessControl();

        // Use the control
        control.EnterSharedAccess();
        control.ExitSharedAccess();
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();

        // Reset it
        control.Reset();

        // Should work fine after reset
        control.EnterSharedAccess();
        control.ExitSharedAccess();
        control.EnterExclusiveAccess();
        control.ExitExclusiveAccess();
    }
}
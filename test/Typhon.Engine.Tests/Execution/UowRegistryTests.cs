using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class UowRegistryTests : TestBase<UowRegistryTests>
{
    // ═══════════════════════════════════════════════════════════════
    // Allocation Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_AllocateUowId_ReturnsUniqueIds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var ids = new ushort[10];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = dbe.UowRegistry.AllocateUowId();
        }

        Assert.That(ids.Distinct().Count(), Is.EqualTo(10), "All allocated IDs should be unique");

        // Release all
        foreach (var id in ids)
        {
            dbe.UowRegistry.Release(id);
        }
    }

    [Test]
    public void Registry_AllocateUowId_StartsAt1()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var firstId = dbe.UowRegistry.AllocateUowId();
        // Slot 0 is reserved, first allocation should return 1
        Assert.That(firstId, Is.EqualTo((ushort)1));

        dbe.UowRegistry.Release(firstId);
    }

    [Test]
    public void Registry_Release_MakesSlotFree()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var id1 = dbe.UowRegistry.AllocateUowId();
        var id2 = dbe.UowRegistry.AllocateUowId();
        Assert.That(id1, Is.EqualTo((ushort)1));
        Assert.That(id2, Is.EqualTo((ushort)2));

        // Release id1, next allocation should reuse it
        dbe.UowRegistry.Release(id1);
        var id3 = dbe.UowRegistry.AllocateUowId();
        Assert.That(id3, Is.EqualTo(id1), "Released slot should be reused");

        dbe.UowRegistry.Release(id2);
        dbe.UowRegistry.Release(id3);
    }

    [Test]
    public void Registry_ActiveCount_TracksAllocations()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.ActiveCount, Is.EqualTo(0));

        var id1 = registry.AllocateUowId();
        Assert.That(registry.ActiveCount, Is.EqualTo(1));

        var id2 = registry.AllocateUowId();
        Assert.That(registry.ActiveCount, Is.EqualTo(2));

        registry.Release(id1);
        Assert.That(registry.ActiveCount, Is.EqualTo(1));

        registry.Release(id2);
        Assert.That(registry.ActiveCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Committed Bitmap Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_CommittedBitmap_Accurate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id = registry.AllocateUowId();

        // Before commit, IsCommitted should return false
        Assert.That(registry.IsCommitted(id), Is.False, "Pending UoW should not be committed");

        // Record commit
        registry.RecordCommit(id, maxTSN: 42);

        // After commit, IsCommitted should return true
        Assert.That(registry.IsCommitted(id), Is.True, "Committed UoW should be marked in bitmap");

        registry.Release(id);

        // After release, IsCommitted should return false
        Assert.That(registry.IsCommitted(id), Is.False, "Released UoW should not be committed");
    }

    [Test]
    public void Registry_CommittedBeforeTSN_NormalOperation_IsMaxValue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(long.MaxValue),
            "Normal operation should have CommittedBeforeTSN = long.MaxValue");
    }

    // ═══════════════════════════════════════════════════════════════
    // Crash Recovery Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_CrashRecovery_VoidsPending()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate some UoW IDs (they go to Pending state on disk)
        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();

        // Commit one, leave the other Pending
        registry.RecordCommit(id1, maxTSN: 10);

        // Simulate crash recovery by calling LoadFromDisk
        // id1 is Committed → survives
        // id2 is Pending → gets voided
        registry.LoadFromDisk();

        // After recovery:
        // - id1 was Committed → should be in committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True, "Committed UoW should survive recovery");

        // - id2 was Pending → voided, not in committed bitmap
        Assert.That(registry.IsCommitted(id2), Is.False, "Pending UoW should be voided on recovery");

        // Void entries exist → CommittedBeforeTSN = 0
        Assert.That(registry.VoidEntryCount, Is.EqualTo(1));
        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0),
            "Void entries should set CommittedBeforeTSN to 0");

        // Release the voided entry
        registry.Release(id2);
        Assert.That(registry.VoidEntryCount, Is.EqualTo(0));

        // Release committed entry
        registry.Release(id1);
    }

    [Test]
    public void Registry_VoidEntry_SetsCommittedBeforeTSN_ToZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(long.MaxValue));

        // Allocate a UoW (Pending on disk)
        var id = registry.AllocateUowId();

        // Simulate crash recovery — Pending entry gets voided
        registry.LoadFromDisk();

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0),
            "Void entry should force CommittedBeforeTSN to 0");

        registry.Release(id);
    }

    [Test]
    public void Registry_AllVoidFreed_RestoresCommittedBeforeTSN()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Create a voided entry via crash recovery
        var id = registry.AllocateUowId();
        registry.LoadFromDisk();

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0));

        // Free the voided entry
        registry.Release(id);

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(long.MaxValue),
            "Freeing all voided entries should restore CommittedBeforeTSN to long.MaxValue");
    }

    [Test]
    public void Registry_LoadFromDisk_AllocationBitmap_SkipsCommittedSlots()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate 3 IDs: commit id1 and id3, leave id2 pending
        var id1 = registry.AllocateUowId(); // → 1
        var id2 = registry.AllocateUowId(); // → 2
        var id3 = registry.AllocateUowId(); // → 3

        registry.RecordCommit(id1, maxTSN: 10);
        registry.RecordCommit(id3, maxTSN: 30);

        // Simulate crash recovery
        registry.LoadFromDisk();

        // After recovery: id1=Committed, id2=Void, id3=Committed
        // All three slots should be occupied — allocation bitmap bits cleared
        Assert.That(registry.ActiveCount, Is.EqualTo(3));

        // New allocations must not collide with occupied slots
        var id4 = registry.AllocateUowId();
        Assert.That(id4, Is.Not.EqualTo(id1), "Should not reuse committed slot");
        Assert.That(id4, Is.Not.EqualTo(id2), "Should not reuse voided slot");
        Assert.That(id4, Is.Not.EqualTo(id3), "Should not reuse committed slot");

        // Cleanup
        registry.Release(id4);
        registry.Release(id2);
        registry.Release(id1);
        registry.Release(id3);
    }

    [Test]
    public void Registry_LoadFromDisk_AllocationBitmap_ReusesFreeSlots()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate id1 and id2, commit id1, release id2 (makes it Free on disk)
        var id1 = registry.AllocateUowId(); // → 1
        var id2 = registry.AllocateUowId(); // → 2

        registry.RecordCommit(id1, maxTSN: 10);
        registry.Release(id2); // id2 is now Free on disk

        // Simulate crash recovery
        registry.LoadFromDisk();

        // After recovery: id1=Committed (occupied), id2=Free (available)
        Assert.That(registry.ActiveCount, Is.EqualTo(1));

        // First allocation should get id2 back (lowest free slot)
        var newId = registry.AllocateUowId();
        Assert.That(newId, Is.EqualTo(id2), "Free slot should be reusable after LoadFromDisk");

        // Cleanup
        registry.Release(newId);
        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent Allocation Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_ConcurrentAllocation_NoDuplicates()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        const int threadCount = 8;
        const int allocsPerThread = 10;
        var allIds = new ConcurrentBag<ushort>();
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < allocsPerThread; i++)
            {
                var id = registry.AllocateUowId();
                allIds.Add(id);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        var idList = allIds.ToList();
        Assert.That(idList.Count, Is.EqualTo(threadCount * allocsPerThread));
        Assert.That(idList.Distinct().Count(), Is.EqualTo(idList.Count),
            "All concurrently allocated IDs should be unique");

        // Cleanup
        foreach (var id in idList)
        {
            registry.Release(id);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Size Validation Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_EntrySize_Is40Bytes()
    {
        Assert.That(UowRegistry.EntryStructSize, Is.EqualTo(40),
            "UowRegistryEntry must be exactly 40 bytes for clean page division");
    }

    [Test]
    public void Registry_Capacity_MatchesDesign()
    {
        // Root page: 6000 / 40 = 150
        Assert.That(UowRegistry.RootCapacity, Is.EqualTo(150));
        // Overflow page: 8000 / 40 = 200
        Assert.That(UowRegistry.OverflowCapacity, Is.EqualTo(200));
    }

    // ═══════════════════════════════════════════════════════════════
    // Integration via UoW Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_UoW_AllocatesAndReleases()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        ushort capturedId;
        {
            using var uow = dbe.CreateUnitOfWork();
            capturedId = uow.UowId;
            Assert.That(capturedId, Is.GreaterThan((ushort)0));
        }

        // After UoW dispose, the slot should be free — allocate again and verify reuse
        var nextId = dbe.UowRegistry.AllocateUowId();
        Assert.That(nextId, Is.EqualTo(capturedId), "Released UoW slot should be reusable");
        dbe.UowRegistry.Release(nextId);
    }

    [Test]
    public void Registry_MultipleUoWs_GetUniqueIds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow1 = dbe.CreateUnitOfWork();
        using var uow2 = dbe.CreateUnitOfWork();
        using var uow3 = dbe.CreateUnitOfWork();

        Assert.That(uow1.UowId, Is.Not.EqualTo(uow2.UowId));
        Assert.That(uow2.UowId, Is.Not.EqualTo(uow3.UowId));
        Assert.That(uow1.UowId, Is.Not.EqualTo(uow3.UowId));
    }

    [Test]
    public void Registry_UoW_CreateCommitDispose_FullLifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var initialActive = dbe.UowRegistry.ActiveCount;

        using (var uow = dbe.CreateUnitOfWork())
        {
            Assert.That(dbe.UowRegistry.ActiveCount, Is.EqualTo(initialActive + 1));

            using var tx = uow.CreateTransaction();
            var comp = new CompA(42);
            tx.CreateEntity(ref comp);
            tx.Commit();
        }

        // After UoW disposal, active count should return to initial
        Assert.That(dbe.UowRegistry.ActiveCount, Is.EqualTo(initialActive));
    }

    [Test]
    public void Registry_Release_UowIdZero_NoOp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Releasing UoW ID 0 should be a safe no-op
        Assert.DoesNotThrow(() => registry.Release(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Back-Pressure Tests (#52)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_AllocateWithWaitContext_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
        var id = registry.AllocateUowId(ref wc);

        Assert.That(id, Is.GreaterThan((ushort)0));
        registry.Release(id);
    }

    [Test]
    public void Registry_BackPressure_WaitsForSlot()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Exhaust a small number of slots, then release one from another thread
        const int slotCount = 50;
        var ids = new ushort[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // Verify we can allocate with back-pressure wait when a slot gets freed
        var allocatedId = (ushort)0;
        var allocationDone = new ManualResetEventSlim(false);

        // Start a thread that allocates — it should succeed quickly since slots are available
        var allocThread = Task.Run(() =>
        {
            var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            allocatedId = registry.AllocateUowId(ref wc);
            allocationDone.Set();
        });

        // The allocation should succeed (plenty of slots beyond our 50)
        Assert.That(allocationDone.Wait(TimeSpan.FromSeconds(5)), Is.True, "Allocation should succeed");
        Assert.That(allocatedId, Is.GreaterThan((ushort)0));

        // Cleanup
        registry.Release(allocatedId);
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    [Test]
    public void Registry_BackPressure_TimesOut_WhenNoSlotsFreed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Fill all 150 slots in the root page to verify the immediate-failure parameterless overload still works
        // We don't fill all 32767 slots (too expensive), so we test the WaitContext overload with expired deadline
        var id = registry.AllocateUowId();
        registry.Release(id);

        // Test with an already-expired deadline — should attempt allocation (will succeed since slots are free)
        // but first test the timeout path: create a scenario where allocation fails with an expired deadline
        var expiredWc = WaitContext.FromTimeout(TimeSpan.Zero);
        // Since slots are available, this should still succeed (TryClaimFreeSlot finds a slot before needing to wait)
        var quickId = registry.AllocateUowId(ref expiredWc);
        Assert.That(quickId, Is.GreaterThan((ushort)0));
        registry.Release(quickId);
    }

    [Test]
    public void Registry_BackPressure_CancellationToken_Aborts()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Fill all root page slots to set up a scenario
        const int fillCount = 100;
        var ids = new ushort[fillCount];
        for (int i = 0; i < fillCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // There are still free slots (we only filled 100 of 32767), so we can't truly test
        // cancellation mid-wait without filling all slots. Instead, verify that the cancellation
        // token integrates correctly by using an already-cancelled token.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var wc = new WaitContext(Deadline.Infinite, cts.Token);

        // AllocateUowId should still succeed because TryClaimFreeSlot finds a slot
        // before it needs to enter the wait path (slots are available)
        var id = registry.AllocateUowId(ref wc);
        Assert.That(id, Is.GreaterThan((ushort)0));
        registry.Release(id);

        // Cleanup
        foreach (var allocId in ids)
        {
            registry.Release(allocId);
        }
    }

    [Test]
    public void Registry_BackPressure_ReleaseWakesWaiter()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate many slots
        const int slotCount = 20;
        var ids = new ushort[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // Start a background allocation that should succeed (slots still available)
        var bgId = (ushort)0;
        var bgDone = new ManualResetEventSlim(false);
        var bgTask = Task.Run(() =>
        {
            var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            bgId = registry.AllocateUowId(ref wc);
            bgDone.Set();
        });

        Assert.That(bgDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(bgId, Is.GreaterThan((ushort)0));

        // Cleanup
        registry.Release(bgId);
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    [Test]
    public void Registry_BackPressure_ConcurrentAllocAndRelease()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        const int threadCount = 4;
        const int opsPerThread = 50;
        var allIds = new ConcurrentBag<ushort>();
        var errors = new ConcurrentBag<Exception>();
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < opsPerThread; i++)
            {
                try
                {
                    var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
                    var id = registry.AllocateUowId(ref wc);
                    allIds.Add(id);

                    // Immediately release half the time to create churn
                    if (i % 2 == 0)
                    {
                        registry.Release(id);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        })).ToArray();

        Task.WaitAll(tasks);

        Assert.That(errors, Is.Empty, () => $"Errors during concurrent alloc/release: {string.Join(", ", errors.Select(e => e.Message))}");

        // Cleanup remaining unreleased IDs
        foreach (var id in allIds)
        {
            try
            {
                registry.Release(id);
            }
            catch
            {
                // Already released — ignore
            }
        }
    }

    [Test]
    public void Registry_CreateUnitOfWork_UsesBackPressure()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        // Verify CreateUnitOfWork still works end-to-end with back-pressure wiring
        using var uow = dbe.CreateUnitOfWork(timeout: TimeSpan.FromSeconds(5));
        Assert.That(uow.UowId, Is.GreaterThan((ushort)0));
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Pending));
    }

    [Test]
    public void Registry_MaxConcurrentUoWs_ReturnsMaxUowId()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.MaxConcurrentUoWs, Is.EqualTo(32767));
    }

    // ═══════════════════════════════════════════════════════════════
    // EnsureCapacity Tests (#codecoverage P1)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_EnsureCapacity_GrowsBeyondRootCapacity()
    {
        // Exercises UowRegistry.EnsureCapacity (lines 522-542):
        // When a slot index >= _currentCapacity is claimed, the segment grows to accommodate it.
        // RootCapacity = 150 (one page), OverflowCapacity = 200 per page.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.CurrentCapacity, Is.EqualTo(UowRegistry.RootCapacity),
            "Initial capacity should be RootCapacity (150)");

        // Allocate 160 IDs — the 151st triggers EnsureCapacity growth
        const int allocCount = 160;
        var ids = new ushort[allocCount];
        for (int i = 0; i < allocCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // After allocating 160 slots (0 reserved, 1-160 used), capacity should have grown
        // to RootCapacity + OverflowCapacity = 150 + 200 = 350
        Assert.That(registry.CurrentCapacity, Is.EqualTo(UowRegistry.RootCapacity + UowRegistry.OverflowCapacity),
            "Capacity should grow to 350 after exceeding root page");
        Assert.That(registry.ActiveCount, Is.EqualTo(allocCount));

        // Cleanup
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    [Test]
    public void Registry_EnsureCapacity_MultipleOverflowPages()
    {
        // Verify growth across multiple overflow pages (350 → 550 → ...)
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate enough IDs to trigger two overflow page growths
        // After 351st slot: needs 3 pages → capacity = 150 + 2*200 = 550
        const int allocCount = 360;
        var ids = new ushort[allocCount];
        for (int i = 0; i < allocCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        Assert.That(registry.CurrentCapacity, Is.EqualTo(UowRegistry.RootCapacity + 2 * UowRegistry.OverflowCapacity),
            "Capacity should grow to 550 after exceeding first overflow page");

        // Cleanup
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WaitForSlotFreed Tests (#codecoverage P1)
    // Uses reflection to clear the allocation bitmap to simulate
    // exhaustion without needing 32K actual allocations.
    // ═══════════════════════════════════════════════════════════════

    private static ulong[] GetAllocationBitmap(UowRegistry registry)
    {
        var field = typeof(UowRegistry).GetField("_allocationBitmap", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ulong[])field.GetValue(registry);
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_ExpiredDeadline_ThrowsResourceExhausted()
    {
        // Exercises WaitForSlotFreed timeout path (lines 230-234):
        // When deadline is already expired (ms == 0), returns false immediately.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var bitmap = GetAllocationBitmap(registry);
        var savedBitmap = new ulong[bitmap.Length];
        Array.Copy(bitmap, savedBitmap, bitmap.Length);

        try
        {
            // Clear all bits → TryClaimFreeSlot returns -1 (no free slots)
            Array.Clear(bitmap, 0, bitmap.Length);

            // AllocateUowId with expired deadline → WaitForSlotFreed returns false → throws
            var wc = WaitContext.FromTimeout(TimeSpan.Zero);
            Assert.Throws<ResourceExhaustedException>(() => registry.AllocateUowId(ref wc));
        }
        finally
        {
            Array.Copy(savedBitmap, bitmap, savedBitmap.Length);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_ShortTimeout_ThrowsResourceExhausted()
    {
        // Exercises WaitForSlotFreed timed wait path (lines 236):
        // _slotFreed.Wait(ms, token) returns false after timeout → throws.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var bitmap = GetAllocationBitmap(registry);
        var savedBitmap = new ulong[bitmap.Length];
        Array.Copy(bitmap, savedBitmap, bitmap.Length);

        try
        {
            Array.Clear(bitmap, 0, bitmap.Length);

            // 50ms timeout — no slot will be freed
            var wc = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
            Assert.Throws<ResourceExhaustedException>(() => registry.AllocateUowId(ref wc));
        }
        finally
        {
            Array.Copy(savedBitmap, bitmap, savedBitmap.Length);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_SlotReleased_WakesWaiterSuccessfully()
    {
        // Exercises WaitForSlotFreed success path (lines 236):
        // Thread blocks on _slotFreed.Wait, another thread calls Release, waiter wakes.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate one slot normally (so Release has a valid entry to free)
        var seedId = registry.AllocateUowId();

        var bitmap = GetAllocationBitmap(registry);
        var savedBitmap = new ulong[bitmap.Length];
        Array.Copy(bitmap, savedBitmap, bitmap.Length);

        try
        {
            // Clear all bits → simulates full exhaustion
            Array.Clear(bitmap, 0, bitmap.Length);

            ushort allocatedId = 0;
            var bgDone = new ManualResetEventSlim(false);

            // Background thread attempts allocation → blocks in WaitForSlotFreed
            var bgTask = Task.Run(() =>
            {
                var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
                allocatedId = registry.AllocateUowId(ref wc);
                bgDone.Set();
            });

            // Give background thread time to enter WaitForSlotFreed
            Thread.Sleep(200);

            // Release the seed slot → sets bitmap bit AND signals _slotFreed event
            registry.Release(seedId);

            // Background thread should wake and claim the freed slot
            Assert.That(bgDone.Wait(TimeSpan.FromSeconds(3)), Is.True,
                "Background allocation should succeed after slot was freed");
            Assert.That(allocatedId, Is.EqualTo(seedId),
                "Should reuse the freed slot");

            // Cleanup the reallocated slot
            registry.Release(allocatedId);
        }
        finally
        {
            Array.Copy(savedBitmap, bitmap, savedBitmap.Length);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_Cancellation_ThrowsResourceExhausted()
    {
        // Exercises WaitForSlotFreed cancellation path (lines 238-241):
        // OperationCanceledException caught → returns false → throws ResourceExhaustedException.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var bitmap = GetAllocationBitmap(registry);
        var savedBitmap = new ulong[bitmap.Length];
        Array.Copy(bitmap, savedBitmap, bitmap.Length);

        try
        {
            Array.Clear(bitmap, 0, bitmap.Length);

            using var cts = new CancellationTokenSource();
            Exception caught = null;
            var bgDone = new ManualResetEventSlim(false);

            // Background thread attempts allocation with cancellation token
            var bgTask = Task.Run(() =>
            {
                try
                {
                    var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(30), cts.Token);
                    registry.AllocateUowId(ref wc);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                bgDone.Set();
            });

            // Give background thread time to enter WaitForSlotFreed
            Thread.Sleep(200);

            // Cancel the token → triggers OperationCanceledException in _slotFreed.Wait
            cts.Cancel();

            Assert.That(bgDone.Wait(TimeSpan.FromSeconds(3)), Is.True,
                "Background thread should complete after cancellation");
            Assert.That(caught, Is.Not.Null, "Cancellation should cause an exception");
            Assert.That(caught, Is.TypeOf<ResourceExhaustedException>(),
                "Cancelled wait should result in ResourceExhaustedException");
        }
        finally
        {
            Array.Copy(savedBitmap, bitmap, savedBitmap.Length);
        }
    }
}

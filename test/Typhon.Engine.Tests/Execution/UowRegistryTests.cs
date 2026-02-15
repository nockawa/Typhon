using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
}

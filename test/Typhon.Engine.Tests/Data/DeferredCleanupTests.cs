using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class DeferredCleanupTests : TestBase<DeferredCleanupTests>
{
    [Test]
    public void LongRunningTail_UntouchedEntities_RevisionsCleanedOnTailCommit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 starts — becomes the tail (oldest active transaction)
        using var t1 = dbe.CreateTransaction();

        // T2 creates entity E, commits and disposes
        long pk;
        {
            using var t2 = dbe.CreateTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        // T3 updates entity E, commits and disposes
        {
            using var t3 = dbe.CreateTransaction();
            var a = new CompA(20);
            t3.UpdateEntity(pk, ref a);
            t3.Commit();
        }

        // Entity E should have 2 revisions (create + update), because T1 is blocking cleanup
        {
            using var tCheck = dbe.CreateTransaction();
            var revCount = tCheck.GetRevisionCount<CompA>(pk);
            Assert.That(revCount, Is.EqualTo(2), "Entity should have 2 revisions while tail is blocking cleanup");
        }

        // T1 commits — never accessed E, but deferred cleanup should process it
        t1.Commit();

        // After T1 commits, deferred cleanup should have removed old revisions
        {
            using var tVerify = dbe.CreateTransaction();
            var revCount = tVerify.GetRevisionCount<CompA>(pk);
            Assert.That(revCount, Is.EqualTo(1), "Entity should have 1 revision after tail commits and deferred cleanup runs");
        }
    }

    [Test]
    public void DeferredCleanup_QueueMechanics_EnqueueAndProcess()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateTransaction();

        // T2 creates entity E, commits
        long pk;
        {
            using var t2 = dbe.CreateTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        // T3 updates entity E, commits
        {
            using var t3 = dbe.CreateTransaction();
            var a = new CompA(20);
            t3.UpdateEntity(pk, ref a);
            t3.Commit();
        }

        // Queue should have entries
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0), "Queue should have entries after non-tail commits");

        // T1 commits — triggers deferred cleanup
        t1.Commit();

        // Queue should be empty after processing
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after tail commits");
    }

    [Test]
    public void DeferredCleanup_DuplicateEntity_SingleQueueEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateTransaction();

        // T2 creates entity E, commits
        long pk;
        {
            using var t2 = dbe.CreateTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        var queueSizeAfterCreate = dbe.DeferredCleanupManager.QueueSize;

        // T3 updates the same entity E, commits
        {
            using var t3 = dbe.CreateTransaction();
            var a = new CompA(20);
            t3.UpdateEntity(pk, ref a);
            t3.Commit();
        }

        // Dedup: queue size should not have grown beyond what T2 enqueued for this entity
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(queueSizeAfterCreate),
            "Queue should have only 1 entry per entity due to dedup");

        t1.Commit();
    }

    [Test]
    public void DeferredCleanup_EntityDeletedBeforeProcess_NoError()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateTransaction();

        // T2 creates entity E, commits
        long pk;
        {
            using var t2 = dbe.CreateTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        // T3 deletes entity E, commits
        {
            using var t3 = dbe.CreateTransaction();
            t3.DeleteEntity<CompA>(pk);
            t3.Commit();
        }

        // T1 commits — should not crash even though entity was deleted
        Assert.DoesNotThrow(() => t1.Commit(), "Processing deferred cleanup for a deleted entity should not throw");

        // Queue should be empty
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after processing");
    }

    [Test]
    public void DeferredCleanup_ManyEntities_AllCleaned()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        const int entityCount = 50;

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateTransaction();

        // Create many entities in separate transactions
        var pks = new long[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateTransaction();
            var a = new CompA(i);
            pks[i] = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update each entity once more
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateTransaction();
            var a = new CompA(i + 1000);
            t.UpdateEntity(pks[i], ref a);
            t.Commit();
        }

        // All entities should have 2 revisions (blocked by T1)
        {
            using var tCheck = dbe.CreateTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                var revCount = tCheck.GetRevisionCount<CompA>(pks[i]);
                Assert.That(revCount, Is.EqualTo(2), $"Entity {i} should have 2 revisions while tail is blocking");
            }
        }

        // T1 commits — triggers cleanup of all entities
        t1.Commit();

        // All entities should now have 1 revision
        {
            using var tVerify = dbe.CreateTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                var revCount = tVerify.GetRevisionCount<CompA>(pks[i]);
                Assert.That(revCount, Is.EqualTo(1), $"Entity {i} should have 1 revision after deferred cleanup");
            }
        }

        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after processing all entities");
    }

    [Test]
    public void DeferredCleanup_NoTail_NoEnqueue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Single transaction creates and commits — is its own tail
        {
            using var t = dbe.CreateTransaction();
            var a = new CompA(42);
            t.CreateEntity(ref a);
            t.Commit();
        }

        // No deferred cleanup needed — the transaction was the tail and cleaned directly
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should stay empty when transaction is its own tail");
    }
}

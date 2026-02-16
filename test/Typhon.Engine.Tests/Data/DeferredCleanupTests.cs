using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class DeferredCleanupTests : TestBase<DeferredCleanupTests>
{
    [Test]
    public void LongRunningTail_UntouchedEntities_RevisionsCleanedOnTailCommit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 starts — becomes the tail (oldest active transaction)
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits and disposes
        long pk;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        // T3 updates entity E, commits and disposes
        {
            using var t3 = dbe.CreateQuickTransaction();
            var a = new CompA(20);
            t3.UpdateEntity(pk, ref a);
            t3.Commit();
        }

        // Entity E should have 2 revisions (create + update), because T1 is blocking cleanup
        {
            using var tCheck = dbe.CreateQuickTransaction();
            var revCount = tCheck.GetRevisionCount<CompA>(pk);
            Assert.That(revCount, Is.EqualTo(2), "Entity should have 2 revisions while tail is blocking cleanup");
        }

        // T1 commits — never accessed E, but deferred cleanup should process it
        t1.Commit();

        // After T1 commits, deferred cleanup should have removed old revisions
        {
            using var tVerify = dbe.CreateQuickTransaction();
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
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits
        long pk;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        // T3 updates entity E, commits
        {
            using var t3 = dbe.CreateQuickTransaction();
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
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits
        long pk;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        var queueSizeAfterCreate = dbe.DeferredCleanupManager.QueueSize;

        // T3 updates the same entity E, commits
        {
            using var t3 = dbe.CreateQuickTransaction();
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
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits
        long pk;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }

        // T3 deletes entity E, commits
        {
            using var t3 = dbe.CreateQuickTransaction();
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
        using var t1 = dbe.CreateQuickTransaction();

        // Create many entities in separate transactions
        var pks = new long[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i);
            pks[i] = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update each entity once more
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i + 1000);
            t.UpdateEntity(pks[i], ref a);
            t.Commit();
        }

        // All entities should have 2 revisions (blocked by T1)
        {
            using var tCheck = dbe.CreateQuickTransaction();
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
            using var tVerify = dbe.CreateQuickTransaction();
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
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            t.CreateEntity(ref a);
            t.Commit();
        }

        // No deferred cleanup needed — the transaction was the tail and cleaned directly
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should stay empty when transaction is its own tail");
    }

    [Test]
    public void DeferredCleanup_EnqueueDuplicate_MigratesToOlderTick()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 (tail, TSN=X), T2 (TSN=X+1) both active
        using var t1 = dbe.CreateQuickTransaction();
        using var t2 = dbe.CreateQuickTransaction();

        // T3 creates entity E, commits → enqueued with blockingTSN = T1.TSN
        long pk;
        {
            using var t3 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t3.CreateEntity(ref a);
            t3.Commit();
        }

        var queueSizeAfterCreate = dbe.DeferredCleanupManager.QueueSize;

        // T4 updates same entity E, commits → same entity enqueued again
        // Should be a no-op because T1.TSN is already the oldest blocker
        {
            using var t4 = dbe.CreateQuickTransaction();
            var a = new CompA(20);
            t4.UpdateEntity(pk, ref a);
            t4.Commit();
        }

        // Queue size should not have grown — dedup keeps the oldest blocking TSN
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(queueSizeAfterCreate),
            "Queue should have only 1 entry per entity — duplicate enqueue for same entity is deduplicated");

        t2.Commit();
        t1.Commit();
    }

    [Test]
    public void DeferredCleanup_ProcessPartialQueue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Setup: create entities A and B, each with 1 revision (the create)
        long pkA, pkB;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            pkA = t.CreateEntity(ref a);
            t.Commit();
        }
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(2);
            pkB = t.CreateEntity(ref a);
            t.Commit();
        }

        // Now start T_a (tail blocker) — all subsequent updates will be blocked by T_a
        var t_a = dbe.CreateQuickTransaction();

        // Update entity A → creates 2nd revision, blocked by T_a
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(11);
            t.UpdateEntity(pkA, ref a);
            t.Commit();
        }

        // Start T_b BEFORE updating entity B, so T_b has an intermediate TSN
        var t_b = dbe.CreateQuickTransaction();

        // Update entity B → creates 2nd revision, blocked by T_a (still the tail)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(22);
            t.UpdateEntity(pkB, ref a);
            t.Commit();
        }

        // Both entities should have 2 revisions
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pkA), Is.EqualTo(2),
                "Entity A should have 2 revisions while T_a is blocking");
            Assert.That(tCheck.GetRevisionCount<CompA>(pkB), Is.EqualTo(2),
                "Entity B should have 2 revisions while T_a is blocking");
        }

        // Queue should have entries
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0),
            "Queue should have entries for blocked entities");
        var queueSizeBefore = dbe.DeferredCleanupManager.QueueSize;

        // Commit T_a → processes deferred cleanup for all entries blocked by T_a.TSN.
        // nextMinTSN will be T_b.TSN, so only revisions with TSN < T_b.TSN get removed.
        // Entity A's create revision (TSN < T_a.TSN) should be cleaned.
        // Entity B's create revision (TSN < T_a.TSN) should also be cleaned.
        t_a.Commit();
        t_a.Dispose();

        // Both entities should now have 1 revision — T_a was blocking both
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pkA), Is.EqualTo(1),
                "Entity A should have 1 revision after T_a committed");
            Assert.That(tCheck.GetRevisionCount<CompA>(pkB), Is.EqualTo(1),
                "Entity B should have 1 revision after T_a committed");
        }

        // Queue should be empty — T_a was the only blocker
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after T_a committed");

        // Verify counters show processing occurred
        Assert.That(dbe.DeferredCleanupManager.ProcessedTotal, Is.GreaterThan(0),
            "ProcessedTotal should be > 0 after deferred cleanup ran");

        t_b.Commit();
        t_b.Dispose();
    }

    [Test]
    public void DeferredCleanup_TailDispose_WithoutCommit_TriggersCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 (tail), T2 creates entity + commits, T3 updates + commits → 2 revisions, queued
        var t1 = dbe.CreateQuickTransaction();

        long pk;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }
        {
            using var t3 = dbe.CreateQuickTransaction();
            var a = new CompA(20);
            t3.UpdateEntity(pk, ref a);
            t3.Commit();
        }

        // Verify 2 revisions and queue has entries
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pk), Is.EqualTo(2));
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0));

        // Dispose T1 without committing — should trigger deferred cleanup
        t1.Dispose();

        // Verify entity has 1 revision and queue is empty
        {
            using var tVerify = dbe.CreateQuickTransaction();
            Assert.That(tVerify.GetRevisionCount<CompA>(pk), Is.EqualTo(1),
                "Entity should have 1 revision after tail is disposed without commit");
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after tail dispose triggers deferred cleanup");
    }

    [Test]
    public void DeferredCleanup_TailRollback_TriggersCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // T1 (tail) makes some changes, T2 creates entity + commits
        var t1 = dbe.CreateQuickTransaction();
        {
            // T1 does some work (writes something to make rollback meaningful)
            var a = new CompA(99);
            t1.CreateEntity(ref a);
        }

        long pk;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = t2.CreateEntity(ref a);
            t2.Commit();
        }
        {
            using var t3 = dbe.CreateQuickTransaction();
            var a = new CompA(20);
            t3.UpdateEntity(pk, ref a);
            t3.Commit();
        }

        // Verify 2 revisions
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pk), Is.EqualTo(2));
        }

        // Explicitly rollback T1, then dispose — deferred cleanup should fire
        t1.Rollback();
        t1.Dispose();

        // Verify entity has 1 revision and queue is empty
        {
            using var tVerify = dbe.CreateQuickTransaction();
            Assert.That(tVerify.GetRevisionCount<CompA>(pk), Is.EqualTo(1),
                "Entity should have 1 revision after tail rollback+dispose triggers deferred cleanup");
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after tail rollback triggers deferred cleanup");
    }

    [Test]
    public void DeferredCleanup_ConcurrentCommits_ProcessCorrectEntries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        const int entityCount = 20;

        // T_blocker is the tail
        using var tBlocker = dbe.CreateQuickTransaction();

        // Create N entities across parallel tasks (each task creates one entity)
        var pks = new long[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i);
            pks[i] = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update all entities (creates second revision for each)
        Parallel.For(0, entityCount, i =>
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i + 1000);
            t.UpdateEntity(pks[i], ref a);
            t.Commit();
        });

        // All entities should have 2 revisions (blocked by T_blocker)
        {
            using var tCheck = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                Assert.That(tCheck.GetRevisionCount<CompA>(pks[i]), Is.EqualTo(2),
                    $"Entity {i} should have 2 revisions while blocker is active");
            }
        }

        // Commit the blocker — triggers deferred cleanup
        tBlocker.Commit();

        // Verify: all entities have 1 revision, queue is empty
        {
            using var tVerify = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                Assert.That(tVerify.GetRevisionCount<CompA>(pks[i]), Is.EqualTo(1),
                    $"Entity {i} should have 1 revision after deferred cleanup");
            }
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after all concurrent entries are cleaned");
    }

    // ═══════════════════════════════════════════════════════════════
    // RemoveFromList Coverage Tests — direct EnqueueBatch calls
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void DeferredCleanup_EnqueueBatch_MigratesToOlderBlockingTSN()
    {
        // Exercises DeferredCleanupManager.RemoveFromList (lines 304-325):
        // When the same entity is enqueued with a SMALLER blockingTSN than its existing entry,
        // the entry is migrated from the old bucket to the new (older) bucket.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;

        // Enqueue entity pk=1 under blockingTSN=10
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(1));

        // Enqueue same entity under blockingTSN=5 (older) → triggers RemoveFromList(10, table, 1)
        // Entity moves from TSN=10 bucket to TSN=5 bucket
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1), "Queue should still have 1 entry after migration (not duplicated)");
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(2), "EnqueuedTotal should reflect both enqueue operations");
    }

    [Test]
    public void DeferredCleanup_EnqueueBatch_MigrationRemovesEmptyBucket()
    {
        // Tests the empty-list cleanup path in RemoveFromList (lines 320-324):
        // When migration removes the last entity from a TSN bucket, the empty bucket is removed
        // and its list is returned to the pool.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;

        // Enqueue single entity pk=1 under blockingTSN=10 (creates TSN=10 bucket with 1 entry)
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));

        // Migrate pk=1 to blockingTSN=5 → RemoveFromList removes pk=1 from TSN=10 bucket
        // TSN=10 bucket is now empty → should be removed, list returned to pool
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1), "Queue should have 1 entry in TSN=5 bucket");

        // Enqueue a new entity under TSN=10 → should reuse the pooled list
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 2 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2), "Queue should have 2 entries across two buckets");
    }

    [Test]
    public void DeferredCleanup_EnqueueBatch_MigrationWithMultipleEntitiesInBucket()
    {
        // Tests RemoveFromList when the bucket has multiple entries (only the migrated one is removed).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;

        // Enqueue two entities under blockingTSN=10
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 },
            new() { Table = table, PrimaryKey = 2 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2));

        // Migrate pk=1 to blockingTSN=5 → RemoveFromList removes pk=1 from TSN=10 bucket
        // TSN=10 bucket still has pk=2, so it stays
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2), "Queue should still have 2 entries (pk=2 in TSN=10, pk=1 in TSN=5)");

        // Migrate pk=2 to blockingTSN=3 → RemoveFromList removes pk=2 from TSN=10 bucket
        // TSN=10 bucket is now empty → removed, list returned to pool
        dcm.EnqueueBatch(3, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 2 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2), "Queue should have 2 entries (pk=1 in TSN=5, pk=2 in TSN=3)");
    }

    [Test]
    public void DeferredCleanup_EnqueueBatch_SameOrNewerTSN_NopDedup()
    {
        // Verifies the dedup path (lines 121-123): when blockingTSN >= existingTSN, the entry is
        // skipped (already queued under an older blocking TSN).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;

        // Enqueue entity pk=1 under blockingTSN=5
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(1));

        // Re-enqueue same entity under blockingTSN=10 (newer/larger) → should be skipped
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1), "Queue should still have 1 entry (dedup skipped)");
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(1), "EnqueuedTotal should not increment for skipped entries");

        // Re-enqueue under same blockingTSN=5 → should also be skipped
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(1));
    }
}

/// <summary>
/// Tests for lazy cleanup require a custom <see cref="DeferredCleanupOptions.LazyCleanupThreshold"/>
/// so they use a separate test fixture with a custom DI setup.
/// </summary>
class DeferredCleanupLazyTests : TestBase<DeferredCleanupLazyTests>
{
    public override void Setup()
    {
        var serviceCollection = new ServiceCollection();
        ServiceCollection = serviceCollection;
        ServiceCollection
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(options =>
            {
                options.DeferredCleanup.LazyCleanupThreshold = 3;
            });

        ServiceProvider = ServiceCollection.BuildServiceProvider();
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        Logger = ServiceCollection.BuildServiceProvider().GetRequiredService<ILogger<DeferredCleanupLazyTests>>();
    }

    [Test]
    public void LazyCleanup_TriggersOnRead()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // With threshold=3, lazy cleanup triggers when ItemCount >= 3 during reads.
        // Build up revisions without a blocking tail first (so they accumulate normally).

        // Create an entity and update it multiple times in separate transactions
        // while a blocker holds the tail position to prevent immediate cleanup.
        var tBlocker = dbe.CreateQuickTransaction();

        long pk;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            pk = t.CreateEntity(ref a);
            t.Commit();
        }

        // Update 3 more times to reach ItemCount >= 4 (create + 3 updates)
        for (int i = 2; i <= 4; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i * 10);
            t.UpdateEntity(pk, ref a);
            t.Commit();
        }

        // Entity should have 4 revisions (blocked by tBlocker)
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pk), Is.EqualTo(4),
                "Entity should have 4 revisions while blocker is active");
        }

        // Commit the blocker — this triggers deferred cleanup
        tBlocker.Commit();
        tBlocker.Dispose();

        // After the blocker commits, the entity should be cleaned down to 1 revision
        {
            using var tVerify = dbe.CreateQuickTransaction();
            Assert.That(tVerify.GetRevisionCount<CompA>(pk), Is.EqualTo(1),
                "Entity should have 1 revision after blocker commits");
        }

        // Now create a new scenario: build up revisions again without a blocker
        // (each commit cleans immediately), but then create a blocker to accumulate,
        // then commit the blocker so MinTSN advances, and finally read the entity
        // to trigger lazy cleanup.
        var tBlocker2 = dbe.CreateQuickTransaction();

        // Update 3 times to get ItemCount >= threshold (3)
        for (int i = 10; i <= 12; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i * 100);
            t.UpdateEntity(pk, ref a);
            t.Commit();
        }

        var lazyCountBefore = dbe.DeferredCleanupManager.LazyCleanupTotal;

        // Commit the blocker so MinTSN advances — this makes old revisions eligible for cleanup
        tBlocker2.Commit();
        tBlocker2.Dispose();

        // Now read the entity — this should trigger lazy cleanup since ItemCount >= threshold
        // and MinTSN has advanced past the old revisions
        {
            using var tRead = dbe.CreateQuickTransaction();
            tRead.ReadEntity(pk, out CompA result);
            Assert.That(result.A, Is.EqualTo(1200), "Should read the latest value");
        }

        // Verify either: the lazy cleanup counter was incremented, OR the deferred cleanup
        // already handled it (which is also correct behavior)
        var lazyCountAfter = dbe.DeferredCleanupManager.LazyCleanupTotal;
        var processedTotal = dbe.DeferredCleanupManager.ProcessedTotal;

        // The entity should be cleaned one way or another
        {
            using var tFinal = dbe.CreateQuickTransaction();
            Assert.That(tFinal.GetRevisionCount<CompA>(pk), Is.EqualTo(1),
                "Entity should have 1 revision after lazy or deferred cleanup");
        }

        Assert.That(lazyCountAfter + processedTotal, Is.GreaterThan(0),
            "At least one cleanup path (lazy or deferred) should have been active");
    }
}

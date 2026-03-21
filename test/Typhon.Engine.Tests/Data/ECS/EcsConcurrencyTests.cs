using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Multi-threaded ECS operation tests. Verifies correctness under concurrent access:
/// parallel spawns, concurrent reads, read-while-commit, and parallel queries.
/// Each thread uses its own Transaction (thread affinity requirement).
/// </summary>
[TestFixture]
[NonParallelizable]
class EcsConcurrencyTests : TestBase<EcsConcurrencyTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
        Archetype<EcsSoldier>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel Spawn
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelSpawn_SameArchetype_AllEntitiesUnique()
    {
        using var dbe = SetupEngine();
        const int threadCount = 8;
        const int entitiesPerThread = 200;

        var allIds = new ConcurrentBag<EntityId>();
        var barrier = new Barrier(threadCount);

        Parallel.For(0, threadCount, i =>
        {
            barrier.SignalAndWait();
            using var t = dbe.CreateQuickTransaction();
            for (int j = 0; j < entitiesPerThread; j++)
            {
                var pos = new EcsPosition(i * 1000 + j, 0, 0);
                var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                allIds.Add(id);
            }
            t.Commit();
        });

        // All entity IDs must be unique (Interlocked.Increment on EntityKey)
        var uniqueIds = new HashSet<EntityId>(allIds);
        Assert.That(uniqueIds.Count, Is.EqualTo(threadCount * entitiesPerThread),
            "All spawned entity IDs must be unique across threads");
    }

    [Test]
    [CancelAfter(15000)]
    public void ParallelSpawn_AllEntitiesReadableAfterCommit()
    {
        using var dbe = SetupEngine();
        const int threadCount = 4;
        const int entitiesPerThread = 100;

        var allIds = new ConcurrentBag<(EntityId Id, float X)>();

        // Phase 1: Parallel spawns
        Parallel.For(0, threadCount, i =>
        {
            using var t = dbe.CreateQuickTransaction();
            for (int j = 0; j < entitiesPerThread; j++)
            {
                float x = i * 1000 + j;
                var pos = new EcsPosition(x, 0, 0);
                var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                allIds.Add((id, x));
            }
            t.Commit();
        });

        // Phase 2: Verify all entities are readable from a new transaction
        using var readTx = dbe.CreateQuickTransaction();
        foreach (var (id, expectedX) in allIds)
        {
            var entity = readTx.Open(id);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.EqualTo(expectedX));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent Read
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ConcurrentReads_SameEntity_AllSeeConsistentData()
    {
        using var dbe = SetupEngine();

        // Pre-populate
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(42, 43, 44);
            var vel = new EcsVelocity(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Parallel reads — all threads should see the same data
        const int threadCount = 8;
        var errors = new ConcurrentBag<string>();

        Parallel.For(0, threadCount, i =>
        {
            using var t = dbe.CreateQuickTransaction();
            var entity = t.Open(id);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            ref readonly var vel = ref entity.Read(EcsUnit.Velocity);

            if (pos.X != 42 || pos.Y != 43 || pos.Z != 44)
            {
                errors.Add($"Thread {i}: Position mismatch ({pos.X}, {pos.Y}, {pos.Z})");
            }
            if (vel.Dx != 1 || vel.Dy != 2 || vel.Dz != 3)
            {
                errors.Add($"Thread {i}: Velocity mismatch ({vel.Dx}, {vel.Dy}, {vel.Dz})");
            }
        });

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    // ═══════════════════════════════════════════════════════════════
    // Read while Write (MVCC isolation)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ReadWhileWrite_SnapshotIsolation_ReaderSeesOldValue()
    {
        using var dbe = SetupEngine();

        // Create entity with initial value
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(100, 0, 0);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Open a read transaction BEFORE the write (snapshot at current TSN)
        using var readerTx = dbe.CreateQuickTransaction();
        var beforeEntity = readerTx.Open(id);
        ref readonly var beforePos = ref beforeEntity.Read(EcsUnit.Position);
        Assert.That(beforePos.X, Is.EqualTo(100f));

        // Write new value and commit on another transaction
        using (var writerTx = dbe.CreateQuickTransaction())
        {
            var mut = writerTx.OpenMut(id);
            mut.Write(EcsUnit.Position).X = 999;
            writerTx.Commit();
        }

        // Reader should still see old value (MVCC snapshot isolation)
        var afterEntity = readerTx.Open(id);
        ref readonly var afterPos = ref afterEntity.Read(EcsUnit.Position);
        Assert.That(afterPos.X, Is.EqualTo(100f), "Reader with older TSN should see pre-write value (MVCC)");
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel Queries
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelQueries_SameArchetype_AllReturnConsistentResults()
    {
        using var dbe = SetupEngine();
        const int entityCount = 200;

        // Pre-populate
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            }
            t.Commit();
        }

        // Parallel queries — all should return the same count
        const int threadCount = 8;
        var counts = new ConcurrentBag<int>();

        Parallel.For(0, threadCount, _ =>
        {
            using var t = dbe.CreateQuickTransaction();
            counts.Add(t.Query<EcsUnit>().Count());
        });

        foreach (var count in counts)
        {
            Assert.That(count, Is.EqualTo(entityCount),
                "All parallel queries should return the same count");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Spawn + Destroy interleaved
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void ParallelSpawnAndDestroy_NoCorruption()
    {
        using var dbe = SetupEngine();
        const int iterations = 50;

        // Pre-populate entities to destroy
        var idsToDestroy = new EntityId[iterations];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < iterations; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                idsToDestroy[i] = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            }
            t.Commit();
        }

        var spawnedIds = new ConcurrentBag<EntityId>();
        var destroyedIds = new ConcurrentBag<EntityId>();

        // Thread 1: spawn new entities
        // Thread 2: destroy existing entities
        var tasks = new Task[2];
        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                using var t = dbe.CreateQuickTransaction();
                var pos = new EcsPosition(1000 + i, 0, 0);
                var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                spawnedIds.Add(id);
                t.Commit();
            }
        });
        tasks[1] = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                using var t = dbe.CreateQuickTransaction();
                t.Destroy(idsToDestroy[i]);
                destroyedIds.Add(idsToDestroy[i]);
                t.Commit();
            }
        });

        Task.WaitAll(tasks);

        // Verify: all spawned entities are alive, all destroyed are dead
        using var verifyTx = dbe.CreateQuickTransaction();
        foreach (var id in spawnedIds)
        {
            Assert.That(verifyTx.IsAlive(id), Is.True, $"Spawned entity {id} should be alive");
        }
        foreach (var id in destroyedIds)
        {
            Assert.That(verifyTx.IsAlive(id), Is.False, $"Destroyed entity {id} should be dead");
        }
    }
}

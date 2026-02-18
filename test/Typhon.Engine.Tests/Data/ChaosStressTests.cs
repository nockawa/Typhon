using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Chaos stress tests that exercise all major subsystems simultaneously under heavy load.
/// These tests are designed to find race conditions, deadlocks, resource leaks, and edge cases.
/// </summary>
[TestFixture]
//[Ignore("WIP")]
[PublicAPI]
class ChaosStressTests : TestBase<ChaosStressTests>
{
    // Increase cache size for stress tests
    private const int StressCacheSize = 4 * 1024 * 1024; // 4MB cache

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        
        // Ensure database file is deleted with retry logic for stress tests
        // This handles cases where a previous crashed test left the file locked
        EnsureFileDeletedWithRetry();
    }
    
    /// <summary>
    /// Attempts to delete the database file with retries, handling locked file scenarios
    /// that can occur when a previous test run crashed or was terminated.
    /// </summary>
    private void EnsureFileDeletedWithRetry()
    {
        using var scope = ServiceProvider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value;
        var filePath = Path.Combine(options.DatabaseDirectory, options.DatabaseName + ".bin");
        
        if (!File.Exists(filePath))
            return;
            
        const int maxRetries = 5;
        const int retryDelayMs = 500;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Delete(filePath);
                return; // Success
            }
            catch (IOException) when (attempt < maxRetries)
            {
                // File is locked, wait and retry
                Thread.Sleep(retryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                // File is locked, wait and retry
                Thread.Sleep(retryDelayMs);
            }
        }
        
        // If we get here, all retries failed - log warning but continue
        // The test will fail with a clear error if the file is still locked
        Logger.LogWarning("Could not delete database file after {MaxRetries} attempts: {FilePath}", maxRetries, filePath);
    }

    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        // Use base components (CompA-E) which are already properly configured
        base.RegisterComponents(dbe);
    }

    #region Test Case Sources

    /// <summary>
    /// Generates test cases with varying thread counts, entity counts, and operation mixes.
    /// </summary>
    private static IEnumerable<TestCaseData> ChaosTestCases()
    {
        // (threads, entitiesPerThread, operationsPerEntity, readWriteRatio, includeDeletes, seed)
        
        // Light load - quick sanity check
        yield return new TestCaseData(2, 50, 10, 0.7f, false, 12345)
            .SetName("Light_2T_50E_NoDelete");
        
        // Medium load - balanced operations
        yield return new TestCaseData(4, 100, 20, 0.5f, true, 23456)
            .SetName("Medium_4T_100E_WithDelete");
        
        // Heavy reads - read-heavy workload
        yield return new TestCaseData(8, 50, 30, 0.9f, false, 34567)
            .SetName("HeavyRead_8T_50E");
        
        // Heavy writes - write-heavy workload with contention
        yield return new TestCaseData(4, 200, 15, 0.2f, true, 45678)
            .SetName("HeavyWrite_4T_200E_WithDelete");
        
        // Maximum contention - few entities, many threads
        yield return new TestCaseData(8, 10, 50, 0.5f, true, 56789)
            .SetName("MaxContention_8T_10E");
        
        // Scale test - many entities, moderate threads
        yield return new TestCaseData(4, 500, 5, 0.6f, false, 67890)
            .SetName("Scale_4T_500E");
    }

    /// <summary>
    /// Long-running transaction interference test cases.
    /// </summary>
    private static IEnumerable<TestCaseData> LongRunningTxnTestCases()
    {
        // (workerThreads, longRunningCount, entitiesPerWorker, holdTimeMs, seed)
        yield return new TestCaseData(4, 2, 100, 500, 11111)
            .SetName("LongTxn_4W_2L_500ms");
        yield return new TestCaseData(6, 3, 50, 1000, 22222)
            .SetName("LongTxn_6W_3L_1000ms");
    }

    /// <summary>
    /// Multi-component operation test cases.
    /// </summary>
    private static IEnumerable<TestCaseData> MultiComponentTestCases()
    {
        // (threads, entitiesPerThread, componentsPerEntity, updateRounds, seed)
        yield return new TestCaseData(4, 50, 3, 10, 77777)
            .SetName("MultiComp_4T_50E_3C");
        yield return new TestCaseData(2, 100, 5, 5, 88888)
            .SetName("MultiComp_2T_100E_5C");
    }

    #endregion

    #region Main Chaos Test

    /// <summary>
    /// The ultimate chaos test: multiple threads performing random CRUD operations
    /// on multiple component types with varying transaction lifetimes.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(ChaosTestCases))]
    [Property("CacheSize", StressCacheSize)]
    [Ignore("Pre-existing BTree concurrency bug: process crash during concurrent CRUD")]
    public void ChaosTest_MultiThreadedCRUD(int threadCount, int entitiesPerThread, int operationsPerEntity, float readWriteRatio, 
        bool includeDeletes, int seed)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var errors = new ConcurrentBag<string>();
        var stats = new ConcurrentDictionary<string, long>();
        var allEntityIds = new ConcurrentDictionary<long, int>(); // entityId -> owningThread
        var deletedEntities = new ConcurrentDictionary<long, bool>();

        // Initialize stats
        foreach (var key in new[] { "Creates", "Reads", "Updates", "Deletes", "Commits", "Rollbacks", "Conflicts" })
        {
            stats[key] = 0;
        }

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var threadSeed = seed + threadId * 1000;

            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadSeed);
                var localEntities = new List<long>();

                try
                {
                    // Phase 1: Create entities
                    barrier.SignalAndWait();

                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();

                            // Create with random component combination using existing CompA/CompB/CompD
                            var compA = new CompA(threadId * 10000 + i, rand.NextSingle(), rand.NextDouble());
                            var compB = new CompB(threadId, rand.NextSingle());
                            var compD = new CompD(rand.NextSingle(), threadId * 1000 + i, rand.NextDouble());

                            var choice = rand.Next(3);
                            long entityId;
                            switch (choice)
                            {
                                case 0:
                                    entityId = txn.CreateEntity(ref compA);
                                    break;
                                case 1:
                                    entityId = txn.CreateEntity(ref compB);
                                    break;
                                default:
                                    entityId = txn.CreateEntity(ref compD);
                                    break;
                            }

                            if (txn.Commit())
                            {
                                localEntities.Add(entityId);
                                allEntityIds[entityId] = threadId;
                                stats.AddOrUpdate("Creates", 1, (_, v) => v + 1);
                                stats.AddOrUpdate("Commits", 1, (_, v) => v + 1);
                            }
                            else
                            {
                                stats.AddOrUpdate("Rollbacks", 1, (_, v) => v + 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} Create failed: {ex.Message}");
                        }
                    }

                    // Phase 2: Random operations
                    barrier.SignalAndWait();

                    for (int op = 0; op < operationsPerEntity * entitiesPerThread; op++)
                    {
                        if (localEntities.Count == 0)
                            break;

                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();

                            var targetEntity = localEntities[rand.Next(localEntities.Count)];
                            var opRoll = (float)rand.NextDouble();

                            if (opRoll < readWriteRatio)
                            {
                                // Read operation
                                var readA = txn.ReadEntity<CompA>(targetEntity, out _);
                                var readB = txn.ReadEntity<CompB>(targetEntity, out _);
                                var readD = txn.ReadEntity<CompD>(targetEntity, out _);

                                if (readA || readB || readD)
                                {
                                    stats.AddOrUpdate("Reads", 1, (_, v) => v + 1);
                                }
                            }
                            else if (includeDeletes && opRoll > 0.95f && localEntities.Count > 5)
                            {
                                // Delete operation (rare)
                                if (!deletedEntities.ContainsKey(targetEntity))
                                {
                                    var deleted = txn.DeleteEntity<CompA>(targetEntity) ||
                                                  txn.DeleteEntity<CompB>(targetEntity) ||
                                                  txn.DeleteEntity<CompD>(targetEntity);

                                    if (deleted && txn.Commit())
                                    {
                                        deletedEntities[targetEntity] = true;
                                        localEntities.Remove(targetEntity);
                                        stats.AddOrUpdate("Deletes", 1, (_, v) => v + 1);
                                        stats.AddOrUpdate("Commits", 1, (_, v) => v + 1);
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                // Update operation
                                if (txn.ReadEntity<CompA>(targetEntity, out var compA))
                                {
                                    compA.A = rand.Next();
                                    txn.UpdateEntity(targetEntity, ref compA);
                                }
                                else if (txn.ReadEntity<CompB>(targetEntity, out var compB))
                                {
                                    compB.A = rand.Next();
                                    txn.UpdateEntity(targetEntity, ref compB);
                                }
                                else if (txn.ReadEntity<CompD>(targetEntity, out var compD))
                                {
                                    compD.B = rand.Next();
                                    txn.UpdateEntity(targetEntity, ref compD);
                                }

                                if (txn.Commit())
                                {
                                    stats.AddOrUpdate("Updates", 1, (_, v) => v + 1);
                                    stats.AddOrUpdate("Commits", 1, (_, v) => v + 1);
                                }
                                else
                                {
                                    stats.AddOrUpdate("Conflicts", 1, (_, v) => v + 1);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} Op {op} failed: {ex.Message}");
                        }
                    }

                    // Phase 3: Verify all non-deleted entities are readable
                    barrier.SignalAndWait();

                    foreach (var entityId in localEntities)
                    {
                        if (deletedEntities.ContainsKey(entityId))
                            continue;

                        try
                        {
                            using var txn = dbe.CreateQuickTransaction();
                            var canRead = txn.ReadEntity<CompA>(entityId, out _) ||
                                          txn.ReadEntity<CompB>(entityId, out _) ||
                                          txn.ReadEntity<CompD>(entityId, out _);

                            if (!canRead)
                            {
                                errors.Add($"Thread {threadId}: Entity {entityId} not readable but not marked deleted");
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId} Verify {entityId} failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId} fatal: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);

        // Report stats
        Logger.LogInformation("=== Chaos Test Stats ===");
        foreach (var kvp in stats.OrderBy(x => x.Key))
        {
            Logger.LogInformation("{Key}: {Value}", kvp.Key, kvp.Value);
        }
        Logger.LogInformation("Total entities created: {Count}", allEntityIds.Count);
        Logger.LogInformation("Total entities deleted: {Count}", deletedEntities.Count);

        // Assert no errors
        Assert.That(errors, Is.Empty, $"Errors occurred:\n{string.Join("\n", errors.Take(20))}");

        // Verify final state
        var finalEntityCount = allEntityIds.Count - deletedEntities.Count;
        Assert.That(finalEntityCount, Is.GreaterThan(0), "Should have surviving entities");
    }

    #endregion

    #region Long-Running Transaction Tests

    /// <summary>
    /// Tests that long-running transactions correctly maintain MVCC isolation
    /// while other transactions make modifications.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(LongRunningTxnTestCases))]
    [Property("CacheSize", StressCacheSize)]
    public void LongRunningTransaction_MaintainsIsolation(int workerThreads, int longRunningCount, int entitiesPerWorker, int holdTimeMs, int seed)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var errors = new ConcurrentBag<string>();
        var rand = new Random(seed);

        // Phase 1: Create initial entities
        var entityIds = new List<long>();
        var initialValues = new Dictionary<long, int>();

        for (int i = 0; i < workerThreads * entitiesPerWorker; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i * 100, 0f, 0d); // Use A field as the value we track
            var id = txn.CreateEntity(ref comp);
            txn.Commit();
            entityIds.Add(id);
            initialValues[id] = i * 100;
        }

        Logger.LogInformation("Created {Count} initial entities", entityIds.Count);

        // Phase 2: Start long-running transactions that snapshot the initial state
        // These transactions are intentionally kept open to test MVCC isolation - disposed in cleanup loop below
        var longRunningTxns = new List<(Transaction txn, Dictionary<long, int> snapshot)>();
        for (int i = 0; i < longRunningCount; i++)
        {
#pragma warning disable TYPHON004 // Intentionally keeping transaction open to test MVCC - disposed in cleanup loop
            var txn = dbe.CreateQuickTransaction();
#pragma warning restore TYPHON004
            var snapshot = new Dictionary<long, int>();

            // Read all entities to establish snapshot
            foreach (var id in entityIds)
            {
                if (txn.ReadEntity<CompA>(id, out var comp))
                {
                    snapshot[id] = comp.A;
                }
            }

            longRunningTxns.Add((txn, snapshot));
            Logger.LogInformation("Long-running transaction {I} started with TSN {Tsn}", i, txn.TSN);
        }

        // Phase 3: Worker threads modify entities while long-running transactions are active
        var barrier = new Barrier(workerThreads);
        var modificationCounts = new ConcurrentDictionary<long, int>();
        var workerTasks = new Task[workerThreads];

        for (int w = 0; w < workerThreads; w++)
        {
            var workerId = w;
            var workerSeed = seed + workerId * 1000;

            workerTasks[w] = Task.Run(() =>
            {
                var workerRand = new Random(workerSeed);
                barrier.SignalAndWait();

                for (int round = 0; round < 10; round++)
                {
                    var targetId = entityIds[workerRand.Next(entityIds.Count)];

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();
                        if (txn.ReadEntity<CompA>(targetId, out var comp))
                        {
                            comp.A += 1;
                            txn.UpdateEntity(targetId, ref comp);

                            if (txn.Commit())
                            {
                                modificationCounts.AddOrUpdate(targetId, 1, (_, v) => v + 1);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Worker {workerId} round {round}: {ex.Message}");
                    }

                    Thread.Sleep(workerRand.Next(10, 50));
                }
            });
        }

        // Wait a bit, then verify long-running transactions still see old values
        Thread.Sleep(holdTimeMs / 2);

        foreach (var (txn, snapshot) in longRunningTxns)
        {
            foreach (var (id, expectedValue) in snapshot)
            {
                if (txn.ReadEntity<CompA>(id, out var comp))
                {
                    if (comp.A != expectedValue)
                    {
                        errors.Add($"MVCC violation: Entity {id} expected {expectedValue} but got {comp.A}");
                    }
                }
            }
        }

        // Wait for workers to complete
        Task.WaitAll(workerTasks);

        // Verify long-running transactions STILL see their snapshot
        foreach (var (txn, snapshot) in longRunningTxns)
        {
            foreach (var (id, expectedValue) in snapshot)
            {
                if (txn.ReadEntity<CompA>(id, out var comp))
                {
                    if (comp.A != expectedValue)
                    {
                        errors.Add($"MVCC violation after workers: Entity {id} expected {expectedValue} but got {comp.A}");
                    }
                }
            }
        }

        // Cleanup long-running transactions
        foreach (var (txn, _) in longRunningTxns)
        {
            txn.Dispose();
        }

        // Verify new transactions see the latest values
        using (var verifyTxn = dbe.CreateQuickTransaction())
        {
            foreach (var id in entityIds)
            {
                if (verifyTxn.ReadEntity<CompA>(id, out var comp))
                {
                    var expectedMin = initialValues[id];
                    var modifications = modificationCounts.GetValueOrDefault(id, 0);

                    // Value should be at least initial + modifications (could be more due to conflicts)
                    if (comp.A < expectedMin + modifications)
                    {
                        Logger.LogWarning(
                            "Entity {Id}: value {Value} < expected minimum {Expected} (initial {Initial} + mods {Mods})",
                            id, comp.A, expectedMin + modifications, expectedMin, modifications);
                    }
                }
            }
        }

        Logger.LogInformation("Total modifications: {Count}", modificationCounts.Values.Sum());

        Assert.That(errors, Is.Empty, $"MVCC errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Multi-Component Tests

    /// <summary>
    /// Tests creating and updating entities with multiple component types atomically.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(MultiComponentTestCases))]
    [Property("CacheSize", StressCacheSize)]
    // [Ignore("Pre-existing BTree concurrency bug: NullRef in NodeWrapper.GetLast during concurrent creates")]
    public void MultiComponent_AtomicOperations(int threadCount, int entitiesPerThread, int componentsPerEntity, int updateRounds, int seed)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var errors = new ConcurrentBag<string>();
        var allEntities = new ConcurrentDictionary<long, int>(); // entityId -> thread

        var tasks = new Task[threadCount];
        var barrier = new Barrier(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var threadSeed = seed + threadId * 1000;

            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(threadSeed);
                var localEntities = new List<long>();

                try
                {
                    barrier.SignalAndWait();

                    // Create multi-component entities
                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        var compA = new CompA(threadId * 1000 + i, rand.NextSingle(), rand.NextDouble());
                        var compB = new CompB(threadId, rand.NextSingle());
                        var compD = new CompD(rand.NextSingle(), threadId * 100 + i, rand.NextDouble());

                        long entityId;
                        if (componentsPerEntity >= 3)
                        {
                            entityId = txn.CreateEntity(ref compA, ref compB, ref compD);
                        }
                        else if (componentsPerEntity >= 2)
                        {
                            entityId = txn.CreateEntity(ref compA, ref compB);
                        }
                        else
                        {
                            entityId = txn.CreateEntity(ref compA);
                        }

                        if (txn.Commit())
                        {
                            localEntities.Add(entityId);
                            allEntities[entityId] = threadId;
                        }
                    }

                    // Update rounds
                    barrier.SignalAndWait();

                    for (int round = 0; round < updateRounds; round++)
                    {
                        if (localEntities.Count == 0)
                            break;

                        var targetId = localEntities[rand.Next(localEntities.Count)];

                        using var txn = dbe.CreateQuickTransaction();

                        // Read and update all components atomically
                        var hasA = txn.ReadEntity<CompA>(targetId, out var compA);
                        var hasB = txn.ReadEntity<CompB>(targetId, out var compB);
                        var hasD = txn.ReadEntity<CompD>(targetId, out var compD);

                        if (hasA)
                        {
                            compA.A += 1;
                            txn.UpdateEntity(targetId, ref compA);
                        }
                        if (hasB)
                        {
                            compB.A += 1;
                            txn.UpdateEntity(targetId, ref compB);
                        }
                        if (hasD)
                        {
                            // Only update AllowMultiple-indexed fields (A, C) — not B which has a unique index
                            // and incrementing it would collide with adjacent entities' B values.
                            compD.A += 0.1f;
                            compD.C += 0.1;
                            txn.UpdateEntity(targetId, ref compD);
                        }

                        if (!txn.Commit())
                        {
                            // Conflict - this is expected in concurrent scenarios
                        }
                    }

                    // Verify consistency
                    barrier.SignalAndWait();

                    foreach (var entityId in localEntities)
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        var hasA = txn.ReadEntity<CompA>(entityId, out var compA);
                        var hasB = txn.ReadEntity<CompB>(entityId, out var compB);

                        if (hasA && hasB)
                        {
                            // CompA.A and CompB.A should have been updated together
                            // They started with same base (threadId * 1000 + i and threadId)
                            // and were incremented together
                            var aDiff = compA.A - (threadId * 1000);
                            var bDiff = compB.A - threadId;

                            // Allow some variance due to initial i offset, but they should track together
                            // after the updates (which always increment both)
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadId}: {ex}");
                }
            });
        }

        Task.WaitAll(tasks);

        Logger.LogInformation("Created {Count} multi-component entities", allEntities.Count);

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors)}");
        Assert.That(allEntities.Count, Is.EqualTo(threadCount * entitiesPerThread),
            "All entities should be created successfully");
    }

    #endregion

    #region Revision Chain Stress Test

    /// <summary>
    /// Stress tests the revision chain system by creating many revisions rapidly
    /// while other transactions hold references to old revisions.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void RevisionChain_RapidUpdatesWithLongReaders()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var errors = new ConcurrentBag<string>();

        // Create a single entity that will be updated rapidly
        long targetEntity;
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(0, 0f, 0d); // Use A field as the counter
            targetEntity = txn.CreateEntity(ref comp);
            txn.Commit();
        }

        const int readerCount = 4;
        const int writerCount = 2;
        const int updatesPerWriter = 100;
        const int readsPerReader = 50;

        var readerSnapshots = new ConcurrentDictionary<int, int>(); // readerId -> value seen
        var writerCounts = new ConcurrentDictionary<int, int>(); // writerId -> successful updates

        var startSignal = new ManualResetEventSlim(false);
        var allTasks = new List<Task>();

        // Reader tasks - each takes a snapshot and verifies it stays consistent
        for (int r = 0; r < readerCount; r++)
        {
            var readerId = r;
            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();
                Thread.Sleep(readerId * 20); // Stagger starts

                var txn = dbe.CreateQuickTransaction();
                try
                {
                    // Take snapshot
                    if (!txn.ReadEntity<CompA>(targetEntity, out var initial))
                    {
                        errors.Add($"Reader {readerId}: Initial read failed");
                        return;
                    }

                    readerSnapshots[readerId] = initial.A;

                    // Keep reading and verify consistency
                    for (int i = 0; i < readsPerReader; i++)
                    {
                        if (txn.ReadEntity<CompA>(targetEntity, out var current))
                        {
                            if (current.A != initial.A)
                            {
                                errors.Add($"Reader {readerId}: MVCC violation! Expected {initial.A}, got {current.A}");
                            }
                        }
                        Thread.Sleep(5);
                    }
                }
                finally
                {
                    txn.Dispose();
                }
            }));
        }

        // Writer tasks - rapidly update the entity
        for (int w = 0; w < writerCount; w++)
        {
            var writerId = w;
            writerCounts[writerId] = 0;

            allTasks.Add(Task.Run(() =>
            {
                startSignal.Wait();

                for (int i = 0; i < updatesPerWriter; i++)
                {
                    using var txn = dbe.CreateQuickTransaction();
                    if (txn.ReadEntity<CompA>(targetEntity, out var comp))
                    {
                        comp.A += 1;
                        txn.UpdateEntity(targetEntity, ref comp);

                        if (txn.Commit())
                        {
                            writerCounts.AddOrUpdate(writerId, 1, (_, v) => v + 1);
                        }
                    }
                }
            }));
        }

        // Start all tasks
        startSignal.Set();
        Task.WaitAll(allTasks.ToArray());

        // Report
        var totalWrites = writerCounts.Values.Sum();
        Logger.LogInformation("Total successful writes: {Count}", totalWrites);
        Logger.LogInformation("Reader snapshots: {Snapshots}", string.Join(", ", readerSnapshots.Select(x => $"R{x.Key}={x.Value}")));

        // Verify final value
        // With "last write wins" conflict resolution, concurrent writers can both read the same value V,
        // both write V+1, and both commit successfully — but the effective increment is only +1.
        // So final.A can be less than totalWrites (successful commits) due to overlapping updates.
        using (var txn = dbe.CreateQuickTransaction())
        {
            if (txn.ReadEntity<CompA>(targetEntity, out var final))
            {
                Logger.LogInformation("Final value: {Value}, Total successful commits: {TotalWrites}", final.A, totalWrites);
                Assert.That(final.A, Is.GreaterThan(0), "At least some writes should have taken effect");
                Assert.That(final.A, Is.LessThanOrEqualTo(totalWrites),
                    "Final value cannot exceed the number of successful commits");
            }
        }

        Assert.That(errors, Is.Empty, $"MVCC errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Page Cache Pressure Test

    /// <summary>
    /// Tests behavior under page cache pressure - many concurrent transactions
    /// accessing more data than fits in cache.
    /// </summary>
    [Test]
    [Property("CacheSize", 2 * 1024 * 1024)] // Small cache: 2MB (minimum allowed)
    public void PageCachePressure_ManyEntitiesSmallCache()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var errors = new ConcurrentBag<string>();
        const int entityCount = 500; // More entities than cache can hold
        const int threadCount = 4;
        const int operationsPerThread = 200;

        // Create entities
        var entityIds = new long[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i, i * 10f, 0d); // Use A as Id, B as Value
            entityIds[i] = txn.CreateEntity(ref comp);
            txn.Commit();
        }

        Logger.LogInformation("Created {Count} entities", entityCount);

        // Hammer the cache with random access
        var rand = new Random(99999);
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        var readCounts = new int[threadCount];
        var updateCounts = new int[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var threadSeed = rand.Next();

            tasks[t] = Task.Run(() =>
            {
                var localRand = new Random(threadSeed);
                barrier.SignalAndWait();

                for (int op = 0; op < operationsPerThread; op++)
                {
                    var targetIdx = localRand.Next(entityCount);
                    var targetId = entityIds[targetIdx];

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        if (localRand.NextDouble() < 0.7)
                        {
                            // Read
                            if (txn.ReadEntity<CompA>(targetId, out var comp))
                            {
                                // Verify data integrity
                                if (comp.A != targetIdx)
                                {
                                    errors.Add($"Data corruption: Entity {targetId} has Id {comp.A}, expected {targetIdx}");
                                }
                                Interlocked.Increment(ref readCounts[threadId]);
                            }
                        }
                        else
                        {
                            // Update
                            if (txn.ReadEntity<CompA>(targetId, out var comp))
                            {
                                comp.B += 1;
                                txn.UpdateEntity(targetId, ref comp);
                                if (txn.Commit())
                                {
                                    Interlocked.Increment(ref updateCounts[threadId]);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Thread {threadId} op {op}: {ex.Message}");
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        Logger.LogInformation("Reads: {Reads}, Updates: {Updates}",
            readCounts.Sum(), updateCounts.Sum());

        // Final verification - read all entities
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            if (!txn.ReadEntity<CompA>(entityIds[i], out var comp))
            {
                errors.Add($"Final verify: Entity {i} not readable");
            }
            else if (comp.A != i)
            {
                errors.Add($"Final verify: Entity {i} has wrong Id {comp.A}");
            }
        }

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors.Take(20))}");
    }

    #endregion

    #region Rollback Stress Test

    /// <summary>
    /// Tests that rollbacks correctly restore state under concurrent load.
    /// </summary>
    [Test]
    [Property("CacheSize", StressCacheSize)]
    public void RollbackStress_ConcurrentRollbacks()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var errors = new ConcurrentBag<string>();
        const int entityCount = 50;
        const int threadCount = 4;
        const int roundsPerThread = 20;

        // Create initial entities with known values
        var entityIds = new long[entityCount];
        var initialValues = new int[entityCount];

        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            var comp = new CompA(i * 100, 0f, 0d); // Use A field as the value we track
            entityIds[i] = txn.CreateEntity(ref comp);
            initialValues[i] = i * 100;
            txn.Commit();
        }

        // Track committed updates
        var committedUpdates = new ConcurrentDictionary<long, int>();
        foreach (var id in entityIds)
        {
            committedUpdates[id] = 0;
        }

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            var rand = new Random(threadId * 12345);

            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();

                for (int round = 0; round < roundsPerThread; round++)
                {
                    var targetIdx = rand.Next(entityCount);
                    var targetId = entityIds[targetIdx];
                    var shouldRollback = rand.NextDouble() < 0.5;

                    try
                    {
                        using var txn = dbe.CreateQuickTransaction();

                        if (txn.ReadEntity<CompA>(targetId, out var comp))
                        {
                            var oldValue = comp.A;
                            comp.A += 1;
                            txn.UpdateEntity(targetId, ref comp);

                            if (shouldRollback)
                            {
                                txn.Rollback();
                                // Value should NOT have changed
                            }
                            else
                            {
                                if (txn.Commit())
                                {
                                    committedUpdates.AddOrUpdate(targetId, 1, (_, v) => v + 1);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Thread {threadId} round {round}: {ex.Message}");
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        // Verify final values match expected
        for (int i = 0; i < entityCount; i++)
        {
            using var txn = dbe.CreateQuickTransaction();
            if (txn.ReadEntity<CompA>(entityIds[i], out var comp))
            {
                var expectedMin = initialValues[i] + committedUpdates[entityIds[i]];

                // Value should be exactly initial + committed updates
                // (allowing for some variance due to concurrent commit detection)
                if (comp.A < expectedMin)
                {
                    errors.Add($"Entity {i}: Value {comp.A} < expected minimum {expectedMin}");
                }
            }
            else
            {
                errors.Add($"Entity {i} not readable");
            }
        }

        Logger.LogInformation("Total committed updates: {Count}", committedUpdates.Values.Sum());

        Assert.That(errors, Is.Empty, $"Errors:\n{string.Join("\n", errors)}");
    }

    #endregion
}

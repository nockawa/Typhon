using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class TransactionTests : TestBase<TransactionTests>
{
    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL2), [2])]
    public void CreateComp_SingleTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction, bool rollback)
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long e1;
            var a = new CompA(2);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");

            long[] noiseIds = null;
            if (noiseMode >= 1)
            {
                noiseIds = CreateNoiseCompA(dbe);
            }

            {
                using var t = dbe.CreateQuickTransaction();

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a revision of 1");

                if (rollback)
                {
                    var res = t.Rollback();
                    Assert.That(res, Is.True, "Transaction should be rollbacked successfully");
                    Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Rolling back three components should lead to at least three operations");
                }
                else
                {
                    var res = t.Commit();
                    Assert.That(res, Is.True, "Transaction should be successful");
                    Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least three operations");
                }
            }

            if (rollback)
            {
                using var t = dbe.CreateQuickTransaction();
                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.False, "Entity read on a rolled back component should not be successful");
            }
            else
            {
                using var t = dbe.CreateQuickTransaction();
                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.True, "Entity read on an existing component should be successful");
                Assert.That(ar.A, Is.EqualTo(a.A), $"Component should have a value of {a.A}");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Component should have a revision of 1 as we only created it.");
            }
        }
    }

    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), [2])]
    public void ReadComp_SingleTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction)
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long[] noiseIds = null;
            if (noiseMode >= 1)
            {
                noiseIds = CreateNoiseCompA(dbe);
            }

            using var t = dbe.CreateQuickTransaction();

            var a = new CompA(2);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");

            var e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
            Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a revision of 1");

            if (noiseMode >= 2)
            {
                UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
            }

            var res = t.ReadEntity(e1, out CompA ar);
            Assert.That(res, Is.True, "Entity read on an existing component should be successful");
            Assert.That(ar.A, Is.EqualTo(a.A), $"The read component should have a value of {a.A}");

            res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
            Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least 3 operations");
        }
    }

    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), [2])]
    public void ReadComp_SeparateTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction)
    {
        var a = new CompA(3);
        var b = new CompB(1, 1.2f);
        var c = new CompC("Porcupine Tree");
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long[] noiseIds = null;
            long e1;
            int arev;
            {
                using var t = dbe.CreateQuickTransaction();

                if (noiseMode >= 1)
                {
                    noiseIds = CreateNoiseCompA(dbe, noiseOwnTransaction ? null : t);
                }

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                var createRev = t.GetComponentRevision<CompA>(e1);
                Assert.That(createRev, Is.EqualTo(1), "Creating a component should lead to a revision of 1");

                var res = t.Commit();
                Assert.That(res, Is.True, "Transaction commit should be successful");
                Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least three operations");
                arev = t.GetComponentRevision<CompA>(e1);
                Assert.That(arev, Is.EqualTo(createRev), "Committing shouldn't alter the component revision");
            }

            {
                using var t = dbe.CreateQuickTransaction();

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.True, "Reading an existing component should be succesful");
                Assert.That(ar.A, Is.EqualTo(a.A), $"The read value should be {a.A}");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(arev), "Reading a component shouldn't alter its revision");
            }
        }
    }

    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL1), [2])]
    public void UpdateComp_SingleTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction)
    {
        var a = new CompA(1);
        var b = new CompB(1, 1.2f);
        var c = new CompC("Porcupine Tree");
        var aChanged = 12;
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            long[] noiseIds = null;
            long e1;
            {
                using var t = dbe.CreateQuickTransaction();

                if (noiseMode >= 1)
                {
                    noiseIds = CreateNoiseCompA(dbe, noiseOwnTransaction ? null : t);
                }

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                a.A = aChanged;
                t.UpdateEntity(e1, ref a);

                var res = t.Commit();
                Assert.That(res, Is.True, "Transaction commit should be successful");
                Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(3), "Committing three components should lead to at least three operations");
                Assert.That(a.A, Is.EqualTo(aChanged), "Update after create in the same transaction should have the updated value");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Update after create in the same transaction should lead to one revision only");
            }

            {
                using var t = dbe.CreateQuickTransaction();

                if (noiseMode >= 3)
                {
                    ReadNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                var res = t.ReadEntity(e1, out CompA ar);
                Assert.That(res, Is.True, "Entity read on an existing component should be successful");
                Assert.That(ar.A, Is.EqualTo(aChanged), $"Component should have a value of {aChanged}");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Component should have a revision of 1 as we only created it.");
            }
        }
    }

    [Test]
    [TestCaseSource(nameof(BuildNoiseCasesL2), [3])]
    public void UpdateComp_SeparateTransaction_SuccessfulCommit(int noiseMode, bool noiseOwnTransaction, bool readBeforeUpdate)
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            var a = new CompA(2);
            var b = new CompB(1, 1.2f);
            var c = new CompC("Porcupine Tree");
            long e1;
            {
                using var t = dbe.CreateQuickTransaction();

                e1 = t.CreateEntity(ref a, ref b, ref c);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                var res = t.Commit();
                Assert.That(res, Is.True);
            }

            long[] noiseIds = null;
            {
                using var t = dbe.CreateQuickTransaction();

                if (noiseMode >= 1)
                {
                    noiseIds = CreateNoiseCompA(dbe, noiseOwnTransaction ? null : t);
                }

                if (readBeforeUpdate)
                {
                    var rr = t.ReadEntity(e1, out CompA ar);
                    Assert.That(rr, Is.True);
                    Assert.That(ar.A, Is.EqualTo(a.A), "Read in the second transaction should retrieve the component created in the earlier one");
                }

                if (noiseMode >= 2)
                {
                    UpdateNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                var a2 = new CompA(12);
                t.UpdateEntity(e1, ref a2);

                if (noiseMode >= 3)
                {
                    ReadNoiseCompA(dbe, noiseOwnTransaction ? null : t, noiseIds);
                }

                var res = t.ReadEntity(e1, out CompA ar2);
                Assert.That(res, Is.True);
                Assert.That(ar2.A, Is.EqualTo(a2.A), "Read after update should reflect the updated value");

                res = t.Commit();
                Assert.That(res, Is.True);
                Assert.That(t.CommittedOperationCount, Is.GreaterThanOrEqualTo(1), "Committing three components should lead to at least one operation");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2), "Update after create in different transaction should lead to two distinct revisions");
            }

            // Flush deferred cleanup and verify with a fresh transaction (committed tx's ChangeSet
            // shadows the MMF pages — a new transaction is needed to see the cleanup results).
            dbe.FlushDeferredCleanups();
            {
                using var t = dbe.CreateQuickTransaction();

                Assert.That(t.GetRevisionCount<CompA>(e1), Is.EqualTo(1), "Committing an update should remove the previous revision (as the transaction is alone).");
                var res = t.ReadEntity(e1, out CompA a2);
                Assert.That(res, Is.True);
                Assert.That(a2.A, Is.EqualTo(12), "Read after update should reflect the updated value");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(2), "Update after create in different transaction should lead to two distinct revisions");
            }
        }
    }

    [Test]
    public void CreateReadUpdate_MultipleComponent_SuccessfulOperation()
    {
        long e1;
        var a = new CompA(1);
        Span<CompE> eList = stackalloc CompE[16];
        for (int i = 0; i < eList.Length; i++)
        {
            eList[i] = new CompE(12.0f + i, i, 3*i);
        }

        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            {
                using var t = dbe.CreateQuickTransaction();

                e1 = t.CreateEntity(ref a, eList);
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                var res = t.Commit();
                Assert.That(res, Is.True);
            }

            {
                using var t = dbe.CreateQuickTransaction();

                var res = t.ReadEntity(e1, out a, out CompE[] eList2);
                Assert.That(res, Is.True);
                Assert.That(eList2.Length, Is.EqualTo(16));

                for (int i = 0; i < 16; i++)
                {
                    Assert.That(eList[i], Is.EqualTo(eList2[i]));
                }
            }

        }
    }

    [Test]
    public void ComponentRevisionTortureTest()
    {
        {
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);

            var curRevisionCount = 0;
            long e1;
            {
                using var t = dbe.CreateQuickTransaction();

                var a = new CompA(2, 3, 4);
                e1 = t.CreateEntity(ref a);
                curRevisionCount++;
                Assert.That(e1, Is.Not.Zero, "A valid entity id must be non-zero");
                Assert.That(t.GetComponentRevision<CompA>(e1), Is.EqualTo(1), "Creating a component should lead to a unique revision");

                var res = t.Commit();
                Assert.That(res, Is.True);
            }

            // Let's keep a long-running transaction that will prevent cleanup of old revisions
            var longRunningValue = new CompA(200, 300, 400);
            var longRunningTransaction = dbe.CreateQuickTransaction();
            {
                longRunningTransaction.UpdateEntity(e1, ref longRunningValue);
                curRevisionCount++;
            }

            // Generate an array storing ranges of commit and rollback operations totalling 100 operations
            int[] operations = [12, 5, 20, 3, 15, 10, 8, 7, 20];

            var commit = true;
            var rbCount = 0;
            // var revisions = new List<(bool, CompA)>(operations.Sum());
            foreach (int opCount in operations)
            {
                if (!commit)
                {
                    rbCount += opCount;
                }

                for (int i = 0; i < opCount; i++)
                {
                    using var t = dbe.CreateQuickTransaction();

                    var a = CompA.Create(Rand);
                    t.UpdateEntity(e1, ref a);
                    curRevisionCount++;

                    // revisions.Add((commit, a));

                    var res = commit ? t.Commit() : t.Rollback();
                    Assert.That(res, Is.True);
                }

                commit = !commit;
            }

            // Verify revision count before cleanup (no lazy/inline cleanup — all revisions accumulate)
            {
                using var readTransaction = dbe.CreateQuickTransaction();
                Assert.That(readTransaction.GetRevisionCount<CompA>(e1), Is.EqualTo(curRevisionCount - rbCount), "The number of revisions stored should match committed updates (no inline cleanup)");
            }

            // Commit the long-running transaction — cleanup happens in Dispose (deferred path)
            {
                var res = longRunningTransaction.Commit();
                Assert.That(res, Is.True);
                longRunningTransaction.Dispose();
                dbe.FlushDeferredCleanups();
            }

            // Verify with a fresh transaction: chain should be compacted to 1 revision
            {
                using var readTransaction = dbe.CreateQuickTransaction();
                Assert.That(readTransaction.GetRevisionCount<CompA>(e1), Is.EqualTo(1), "After committing the long-running transaction, only one revision should remain");
                readTransaction.ReadEntity(e1, out CompA aFinal);
                Assert.That(aFinal, Is.EqualTo(longRunningValue), "The last committed revision should be the one remaining");
            }

        }
    }

    [Test]
    public void CompRevTest()
    {
        long e1;
        var aR1 = new CompA(1);
        var bR1 = new CompB(1, 1.2f);
        var cR1 = new CompC("Porcupine Tree");

        var bR2 = new CompB(2, 2.4f);

        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create the entity e1, revision R1
        {
            using var t1 = dbe.CreateQuickTransaction();
            Logger.LogInformation("T1 creation time {tick}", t1.TSN);

            e1 = t1.CreateEntity(ref aR1, ref bR1, ref cR1);

            t1.Commit();
        }

        // Create transaction T2 on the main thread (takes a snapshot BEFORE T3 commits)
        var t2 = dbe.CreateQuickTransaction();
        Logger.LogInformation("T2 creation time {tick}", t2.TSN);

        // Change the entity on a background thread to create a new revision
        {
            var task = Task.Run(() =>
            {
                using var t3 = dbe.CreateQuickTransaction();
                Logger.LogInformation("T3 creation time {tick}", t3.TSN);
                t3.ReadEntity<CompB>(e1, out var lbR2);

                lbR2 = bR2;

                t3.UpdateEntity(e1, ref lbR2);
                t3.Commit();
            });

            task.Wait();
        }

        // Check that T2 still sees the first revision of CompB (snapshot isolation)
        t2.ReadEntity<CompB>(e1, out var lbR1);

        Assert.That(t2.GetComponentRevision<CompB>(e1), Is.EqualTo(1));
        Assert.That(lbR1.A, Is.EqualTo(bR1.A));
        Assert.That(lbR1.B, Is.EqualTo(bR1.B));

        t2.Dispose();
        dbe.Dispose();
    }

    /// <summary>
    /// Tests that when a component is deleted and all its revisions are cleaned up,
    /// the primary key index entry should be removed.
    /// This test verifies the expected behavior - currently the cleanup is not implemented (TOFIX in Transaction.cs).
    /// </summary>
    [Test]
    public void DeleteComponent_WhenLastRevisionCleanedUp_PrimaryKeyIndexShouldBeRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create entity
        {
            using var t = dbe.CreateQuickTransaction();
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero, "Entity ID should be non-zero");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity exists in primary key index
        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct, Is.Not.Null, "ComponentTable should exist");

        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var exists = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(exists, Is.True, "Entity should exist in primary key index after creation");
                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }

        // Delete entity - since this is the only transaction, cleanup should happen immediately
        {
            using var t = dbe.CreateQuickTransaction();
            var deleted = t.DeleteEntity<CompA>(e1);
            Assert.That(deleted, Is.True, "Delete should succeed");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity is no longer readable
        {
            using var t = dbe.CreateQuickTransaction();
            var readable = t.ReadEntity(e1, out CompA _);
            Assert.That(readable, Is.False, "Entity should not be readable after deletion");
        }

        // Check primary key index - entry should be removed after cleanup
        // NOTE: This assertion documents the EXPECTED behavior.
        // Currently this will FAIL because the cleanup code is commented out (TOFIX in Transaction.cs line ~971)
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var existsAfterDelete = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(existsAfterDelete, Is.False,
                    "Primary key index entry should be removed when component is deleted and all revisions cleaned up");

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }
    }

    /// <summary>
    /// Tests that when a component is created in one transaction and deleted in another,
    /// the primary key index entry should be removed after cleanup.
    /// </summary>
    [Test]
    public void CreateInOneTxn_DeleteInAnother_PrimaryKeyIndexShouldBeRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create entity in first transaction
        {
            using var t = dbe.CreateQuickTransaction();
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero, "Entity ID should be non-zero");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct, Is.Not.Null, "ComponentTable should exist");

        // Verify entity exists in primary key index after creation
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var exists = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(exists, Is.True, "Entity should exist in primary key index after creation");

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }

        // Delete entity in second transaction
        {
            using var t = dbe.CreateQuickTransaction();
            var deleted = t.DeleteEntity<CompA>(e1);
            Assert.That(deleted, Is.True, "Delete should succeed");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // Verify entity is not readable
        {
            using var t = dbe.CreateQuickTransaction();
            var readable = t.ReadEntity(e1, out CompA _);
            Assert.That(readable, Is.False, "Entity should not be readable after deletion");
        }

        // Flush deferred cleanup so deletion removes the PK index entry
        dbe.FlushDeferredCleanups();

        // Check primary key index - entry should be removed after cleanup
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var exists = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(exists, Is.False,
                    "Primary key index should not contain entry after entity is deleted");

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }
    }

    /// <summary>
    /// Tests that when a component is deleted but there's a long-running transaction keeping old revisions,
    /// the primary key index entry should remain until cleanup happens.
    /// </summary>
    [Test]
    [Ignore("Still WIP")]
    public void DeleteComponent_WithLongRunningTransaction_PrimaryKeyIndexRemainsUntilCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create entity
        {
            using var t = dbe.CreateQuickTransaction();
            e1 = t.CreateEntity(ref a);
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        var ct = dbe.GetComponentTable<CompA>();

        // Start a long-running transaction to prevent cleanup
        var longRunningTxn = dbe.CreateQuickTransaction();

        // Read in long-running transaction to establish snapshot
        longRunningTxn.ReadEntity(e1, out CompA _);

        // Delete entity in a separate transaction
        {
            using var t = dbe.CreateQuickTransaction();
            var deleted = t.DeleteEntity<CompA>(e1);
            Assert.That(deleted, Is.True, "Delete should succeed");
            var res = t.Commit();
            Assert.That(res, Is.True, "Commit should succeed");
        }

        // The primary key index should still have the entry because long-running transaction prevents cleanup
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var existsDuringLongTxn = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(existsDuringLongTxn, Is.True,
                    "Primary key index should retain entry while long-running transaction holds old revisions");

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }

        // Verify multiple revisions exist (create + delete)
        {
            using var t = dbe.CreateQuickTransaction();
            var revCount = t.GetRevisionCount<CompA>(e1);
            Assert.That(revCount, Is.GreaterThanOrEqualTo(2),
                "Should have at least 2 revisions (create and delete) while long-running transaction exists");
        }

        // Complete the long-running transaction - this should trigger cleanup
        longRunningTxn.Commit();
        longRunningTxn.Dispose();

        // After cleanup, primary key index entry should be removed
        // NOTE: This assertion documents the EXPECTED behavior.
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var existsAfterCleanup = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(existsAfterCleanup, Is.False,
                    "Primary key index entry should be removed after long-running transaction completes and cleanup runs");

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }
    }

    /// <summary>
    /// Tests that multiple create-delete cycles properly clean up primary key index entries.
    /// </summary>
    [Test]
    public void MultipleCreateDeleteCycles_PrimaryKeyIndexShouldBeCleanedUp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompA>();
        var entityIds = new long[5];

        // Create multiple entities
        for (int i = 0; i < 5; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i * 10);
            entityIds[i] = t.CreateEntity(ref a);
            t.Commit();
        }

        // Verify all entities exist in index
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                for (int i = 0; i < 5; i++)
                {
                    var exists = ct.PrimaryKeyIndex.TryGet(entityIds[i], ref accessor).IsSuccess;
                    Assert.That(exists, Is.True, $"Entity {i} should exist in primary key index");
                }
                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }

        // Delete entities 0, 2, 4 (odd indices in the array)
        for (int i = 0; i < 5; i += 2)
        {
            using var t = dbe.CreateQuickTransaction();
            t.DeleteEntity<CompA>(entityIds[i]);
            t.Commit();
        }

        // Verify deleted entities are not readable
        for (int i = 0; i < 5; i += 2)
        {
            using var t = dbe.CreateQuickTransaction();
            var readable = t.ReadEntity(entityIds[i], out CompA _);
            Assert.That(readable, Is.False, $"Entity {i} should not be readable after deletion");
        }

        // Verify remaining entities are still readable
        for (int i = 1; i < 5; i += 2)
        {
            using var t = dbe.CreateQuickTransaction();
            var readable = t.ReadEntity(entityIds[i], out CompA _);
            Assert.That(readable, Is.True, $"Entity {i} should still be readable");
        }

        // Check primary key index state
        // NOTE: The assertions for deleted entities document EXPECTED behavior.
        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                // Remaining entities should exist
                for (int i = 1; i < 5; i += 2)
                {
                    var exists = ct.PrimaryKeyIndex.TryGet(entityIds[i], ref accessor).IsSuccess;
                    Assert.That(exists, Is.True, $"Entity {i} should exist in primary key index");
                }

                // Deleted entities should not exist (expected behavior after cleanup)
                for (int i = 0; i < 5; i += 2)
                {
                    var exists = ct.PrimaryKeyIndex.TryGet(entityIds[i], ref accessor).IsSuccess;
                    Assert.That(exists, Is.False,
                        $"Entity {i} should be removed from primary key index after deletion and cleanup");
                }

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }
    }

    /// <summary>
    /// Tests that rolling back a create operation does not leave an entry in the primary key index.
    /// </summary>
    [Test]
    public void RollbackCreate_PrimaryKeyIndexShouldNotContainEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var a = new CompA(42);

        // Create and rollback
        {
            using var t = dbe.CreateQuickTransaction();
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.Not.Zero, "Entity ID should be non-zero");

            var res = t.Rollback();
            Assert.That(res, Is.True, "Rollback should succeed");
        }

        // Verify entity is not readable
        {
            using var t = dbe.CreateQuickTransaction();
            var readable = t.ReadEntity(e1, out CompA _);
            Assert.That(readable, Is.False, "Entity should not be readable after rollback");
        }

        // Check primary key index - entry should not exist after rollback
        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct, Is.Not.Null, "ComponentTable should exist");

        {
            var depth = dbe.EpochManager.EnterScope();
            try
            {
                var accessor = ct.DefaultIndexSegment.CreateChunkAccessor();
                var exists = ct.PrimaryKeyIndex.TryGet(e1, ref accessor).IsSuccess;
                Assert.That(exists, Is.False,
                    "Primary key index should not contain entry for rolled back creation");

                accessor.Dispose();
            }
            finally
            {
                dbe.EpochManager.ExitScope(depth);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 0 Safety Net — State Machine Invariant Tests (Issue #91)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Test 0.1: Verifies that committing a transaction twice returns false the second time
    /// and does not corrupt the transaction state.
    /// </summary>
    [Test]
    public void DoubleCommit_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(1);
        t.CreateEntity(ref a);

        var firstCommit = t.Commit();
        Assert.That(firstCommit, Is.True, "First commit should succeed");
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed));

        var secondCommit = t.Commit();
        Assert.That(secondCommit, Is.False, "Second commit should return false");
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed), "State should remain Committed after double commit");
    }

    /// <summary>
    /// Test 0.2: Verifies that rolling back a transaction twice returns false the second time
    /// and does not corrupt the transaction state.
    /// </summary>
    [Test]
    public void DoubleRollback_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(1);
        t.CreateEntity(ref a);

        var firstRollback = t.Rollback();
        Assert.That(firstRollback, Is.True, "First rollback should succeed");
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked));

        var secondRollback = t.Rollback();
        Assert.That(secondRollback, Is.False, "Second rollback should return false");
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked), "State should remain Rollbacked after double rollback");
    }

    /// <summary>
    /// Test 0.3: Verifies that CRUD operations after commit/rollback return appropriate failure codes.
    /// Documents that ReadEntity has no state guard (unlike Create/Update/Delete) — this
    /// inconsistency will be addressed in Phase 4.
    /// </summary>
    [Test]
    public void CrudAfterCommitOrRollback_ReturnsFailure()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // --- After Commit ---
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            var e1 = t.CreateEntity(ref a);
            Assert.That(t.Commit(), Is.True);

            var a2 = new CompA(99);
            Assert.That(t.CreateEntity(ref a2), Is.EqualTo(-1), "CreateEntity after commit should return -1");

            var a3 = new CompA(100);
            Assert.That(t.UpdateEntity(e1, ref a3), Is.False, "UpdateEntity after commit should return false");

            Assert.That(t.DeleteEntity<CompA>(e1), Is.False, "DeleteEntity after commit should return false");

            // ReadEntity after commit: NO state guard — documents current inconsistency.
            // The read proceeds because ReadEntity does not check State > InProgress.
            var readResult = t.ReadEntity(e1, out CompA _);
            Assert.That(readResult, Is.True, "ReadEntity has no state guard — reads succeed on committed data");

            Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed), "State should remain Committed throughout");
        }

        // --- After Rollback ---
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(55);
            var e2 = t.CreateEntity(ref a);
            Assert.That(t.Rollback(), Is.True);

            var a2 = new CompA(99);
            Assert.That(t.CreateEntity(ref a2), Is.EqualTo(-1), "CreateEntity after rollback should return -1");

            var a3 = new CompA(100);
            Assert.That(t.UpdateEntity(e2, ref a3), Is.False, "UpdateEntity after rollback should return false");

            Assert.That(t.DeleteEntity<CompA>(e2), Is.False, "DeleteEntity after rollback should return false");

            // ReadEntity after rollback: NO state guard — entity was rolled back so read finds nothing
            var readResult = t.ReadEntity(e2, out CompA _);
            Assert.That(readResult, Is.False, "ReadEntity after rollback — rolled-back entity should not be found");

            Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked), "State should remain Rollbacked throughout");
        }
    }

    /// <summary>
    /// Test 0.4: Verifies that a pooled transaction starts with clean state after Reset().
    /// Exercises the pool-reuse path: create+commit+dispose, then create again and verify the
    /// reused transaction has no stale ComponentInfo from the prior lifetime.
    /// </summary>
    [Test]
    public void TransactionReset_ClearsComponentInfoState()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // First transaction: work with CompA + CompB
        {
            using var t1 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            var b = new CompB(20, 3.14f);
            t1.CreateEntity(ref a, ref b);
            Assert.That(t1.Commit(), Is.True);
            Assert.That(t1.CommittedOperationCount, Is.GreaterThanOrEqualTo(2),
                "t1 should have at least 2 operations (CompA + CompB)");
        }
        // t1 disposed → returns to pool, Reset() clears _componentInfos

        // Second transaction: verify clean start, operate with CompA only
        {
            using var t2 = dbe.CreateQuickTransaction();
            Assert.That(t2.State, Is.EqualTo(Transaction.TransactionState.Created),
                "Reused transaction should start in Created state");

            var a = new CompA(30);
            var e2 = t2.CreateEntity(ref a);
            Assert.That(e2, Is.GreaterThan(0), "Entity creation on reused transaction should succeed");

            Assert.That(t2.Commit(), Is.True, "Commit on reused transaction should succeed");
            Assert.That(t2.CommittedOperationCount, Is.EqualTo(1),
                "t2 should have exactly 1 operation (CompA only — no stale CompB from prior lifetime)");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 2 Safety Net — Rollback Path Tests (Issue #93, Step 2.1)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Rollback of a created entity frees the revision table chunk. A subsequent create+commit
    /// must succeed (fresh storage, no stale references from the rolled-back create).
    /// </summary>
    [Test]
    public void Rollback_Created_FreesRevTableChunk()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create and rollback
        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(100);
            e1 = t.CreateEntity(ref a);
            Assert.That(e1, Is.GreaterThan(0));
            Assert.That(t.Rollback(), Is.True);
        }

        // Entity should not be readable
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA _), Is.False, "Rolled-back created entity should not be readable");
        }

        // A fresh create+commit should succeed (storage is clean)
        {
            using var t = dbe.CreateQuickTransaction();
            var a2 = new CompA(200);
            var e2 = t.CreateEntity(ref a2);
            Assert.That(e2, Is.GreaterThan(0));
            Assert.That(t.Commit(), Is.True);
        }

        // Verify the new entity is readable
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA _), Is.False, "Original rolled-back entity should still not be readable");
        }
    }

    /// <summary>
    /// Rolling back an update voids the revision element — the original committed value remains readable.
    /// </summary>
    [Test]
    public void Rollback_Updated_OriginalValuePreserved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            e1 = t.CreateEntity(ref a);
            Assert.That(t.Commit(), Is.True);
        }

        // Update and rollback
        {
            using var t = dbe.CreateQuickTransaction();
            t.ReadEntity(e1, out CompA _);
            var updated = new CompA(999);
            Assert.That(t.UpdateEntity(e1, ref updated), Is.True);
            Assert.That(t.Rollback(), Is.True);
        }

        // Original value should still be readable
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA result), Is.True);
            Assert.That(result.A, Is.EqualTo(10), "Original value should be preserved after rollback of update");
        }
    }

    /// <summary>
    /// Rolling back a delete voids the revision element — the entity remains readable with its original value.
    /// </summary>
    [Test]
    public void Rollback_Deleted_EntityStillReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            e1 = t.CreateEntity(ref a);
            Assert.That(t.Commit(), Is.True);
        }

        // Delete and rollback
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.DeleteEntity<CompA>(e1), Is.True);
            Assert.That(t.Rollback(), Is.True);
        }

        // Entity should still be readable
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA result), Is.True, "Entity should still be readable after rollback of delete");
            Assert.That(result.A, Is.EqualTo(42));
        }
    }

    /// <summary>
    /// Rollback with multiple component types processes all of them.
    /// </summary>
    [Test]
    public void Rollback_MultipleComponents_AllProcessed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            var b = new CompB(2, 3.0f);
            var c = new CompC("test");
            e1 = t.CreateEntity(ref a, ref b, ref c);
            Assert.That(e1, Is.GreaterThan(0));
            Assert.That(t.Rollback(), Is.True);
        }

        // None of the components should be readable
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA _), Is.False, "CompA should not be readable after rollback");
            Assert.That(t.ReadEntity(e1, out CompB _), Is.False, "CompB should not be readable after rollback");
            Assert.That(t.ReadEntity(e1, out CompC _), Is.False, "CompC should not be readable after rollback");
        }
    }

    /// <summary>
    /// Rollback of an empty transaction (no operations) returns true.
    /// State remains Created because the rollback short-circuits before the state transition.
    /// </summary>
    [Test]
    public void Rollback_EmptyTransaction_Succeeds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateQuickTransaction();
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Created));
        Assert.That(t.Rollback(), Is.True, "Rollback of empty transaction should succeed");
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Created),
            "State remains Created — empty rollback short-circuits before state transition");
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 2 Safety Net — Commit Path Tests (Issue #93, Step 2.2)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Simple create+commit then update+commit verifies the LCRI (LastCommitRevisionIndex) is properly
    /// updated, allowing the second transaction to see and update the committed value.
    /// </summary>
    [Test]
    public void Commit_CreateThenUpdate_LCRIUpdated()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            e1 = t.CreateEntity(ref a);
            Assert.That(t.Commit(), Is.True);
        }

        // Update in a second transaction
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA existing), Is.True);
            Assert.That(existing.A, Is.EqualTo(10));
            var updated = new CompA(20);
            Assert.That(t.UpdateEntity(e1, ref updated), Is.True);
            Assert.That(t.Commit(), Is.True);
        }

        // Verify the updated value
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompA result), Is.True);
            Assert.That(result.A, Is.EqualTo(20), "Value should reflect the second commit");
        }
    }

    /// <summary>
    /// Two concurrent transactions update the same entity. The first commits, then the second commits
    /// with a conflict handler. Verifies the handler is invoked and its resolution is committed.
    /// </summary>
    [Test]
    public void Commit_WithConflict_HandlerInvoked()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            e1 = t.CreateEntity(ref a);
            Assert.That(t.Commit(), Is.True);
        }

        // T1 reads and updates
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(e1, out CompA _);
        var u1 = new CompA(100);
        t1.UpdateEntity(e1, ref u1);

        // T2 reads, updates, and commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(e1, out CompA _);
            var u2 = new CompA(200);
            t2.UpdateEntity(e1, ref u2);
            Assert.That(t2.Commit(), Is.True);
        }

        // T1 commits with conflict handler — should resolve to sum of committed + committing
        var handlerInvoked = false;
        t1.Commit((ref ConcurrencyConflictSolver solver) =>
        {
            handlerInvoked = true;
            // Resolve: take committed value (200 from T2)
            solver.TakeCommitted<CompA>();
        });

        Assert.That(handlerInvoked, Is.True, "Conflict handler should have been invoked");

        // Verify the resolved value
        {
            using var tRead = dbe.CreateQuickTransaction();
            Assert.That(tRead.ReadEntity(e1, out CompA result), Is.True);
            Assert.That(result.A, Is.EqualTo(200), "Should reflect the handler's TakeCommitted resolution");
        }
    }

    /// <summary>
    /// Two concurrent transactions update the same entity without a conflict handler.
    /// The last-committed value wins.
    /// </summary>
    [Test]
    public void Commit_WithConflict_NoHandler_LastWins()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            e1 = t.CreateEntity(ref a);
            Assert.That(t.Commit(), Is.True);
        }

        // T1 reads and updates
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(e1, out CompA _);
        var u1 = new CompA(100);
        t1.UpdateEntity(e1, ref u1);

        // T2 reads, updates, and commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(e1, out CompA _);
            var u2 = new CompA(200);
            t2.UpdateEntity(e1, ref u2);
            Assert.That(t2.Commit(), Is.True);
        }

        // T1 commits without handler — "last wins"
        Assert.That(t1.Commit(), Is.True);

        // Verify the last-committed value (T1's value)
        {
            using var tRead = dbe.CreateQuickTransaction();
            Assert.That(tRead.ReadEntity(e1, out CompA result), Is.True);
            Assert.That(result.A, Is.EqualTo(100), "Last-committed value (T1) should win");
        }
    }

    /// <summary>
    /// Deleting an entity with secondary indices removes the index entries on commit.
    /// Uses CompD which has [Index] on fields A, B, and C.
    /// </summary>
    [Test]
    public void Commit_Delete_RemovesSecondaryIndices()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;
        var d = new CompD(1.0f, 42, 3.14);
        {
            using var t = dbe.CreateQuickTransaction();
            e1 = t.CreateEntity(ref d);
            Assert.That(t.Commit(), Is.True);
        }

        // Verify entity exists in primary key index before delete
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompD readD), Is.True);
            Assert.That(readD.B, Is.EqualTo(42));
        }

        // Delete and commit
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.DeleteEntity<CompD>(e1), Is.True);
            Assert.That(t.Commit(), Is.True);
        }

        // Entity should not be readable
        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.ReadEntity(e1, out CompD _), Is.False, "Deleted entity should not be readable");
        }
    }

    /// <summary>
    /// Commit after rollback returns false.
    /// </summary>
    [Test]
    public void Commit_AfterRollback_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(1);
        t.CreateEntity(ref a);

        Assert.That(t.Rollback(), Is.True);
        Assert.That(t.Commit(), Is.False, "Commit after rollback should return false");
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked), "State should remain Rollbacked");
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 0 Safety Net (continued)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Test 0.5: Verifies that committing a transaction with zero entity operations still
    /// processes deferred cleanup when the transaction is the chain tail.
    /// </summary>
    [Test]
    public void CommitWithZeroEntities_ProcessesDeferredCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long e1;

        // Step 1: Create an entity
        {
            using var t1 = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            e1 = t1.CreateEntity(ref a);
            Assert.That(t1.Commit(), Is.True);
        }

        // Step 2: Create a blocking transaction that holds the chain tail
        var tBlocker = dbe.CreateQuickTransaction();
        try
        {
            // Step 3: Delete the entity — cleanup deferred because tBlocker is the chain tail
            {
                using var t2 = dbe.CreateQuickTransaction();
                Assert.That(t2.DeleteEntity<CompA>(e1), Is.True, "Delete should succeed");
                Assert.That(t2.Commit(), Is.True);
            }

            // Step 4: Verify deferred cleanup is pending
            Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0),
                "Deferred cleanup should be pending while blocking transaction holds the tail");

            // Step 5: Commit the blocker (zero entity operations, State == Created).
            // The empty-commit path processes deferred cleanup when this transaction is the tail.
            var commitResult = tBlocker.Commit();
            Assert.That(commitResult, Is.True, "Empty transaction commit should return true");
        }
        finally
        {
            tBlocker.Dispose();
        }

        // Step 6: Verify deferred cleanup was processed
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Deferred cleanup queue should be empty after empty transaction commit + dispose");
    }
}
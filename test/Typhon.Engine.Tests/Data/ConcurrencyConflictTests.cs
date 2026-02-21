using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Canonical tests for write-write conflict detection and resolution via <see cref="ConcurrencyConflictHandler"/>.
/// Covers: no-conflict path, "last wins" without handler, handler-based resolution (delta rebase, take-committed, take-read), multi-entity conflicts,
/// and concurrent thread scenarios.
/// </summary>
class ConcurrencyConflictTests : TestBase<ConcurrencyConflictTests>
{
    // ═══════════════════════════════════════════════════════════════
    // 1. No Conflict — handler provided but never invoked
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void NoConflict_WithHandler_CommitsNormally()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // Single writer, no concurrent update — handler should NOT be called
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var update = new CompA(42);
        t1.UpdateEntity(pk, ref update);

        var handlerCalled = false;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCalled = true;
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCalled, Is.False, "Handler should not be invoked when there is no conflict");

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(42));
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. "Last Wins" — no handler, conflicting update overwrites
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_NoHandler_LastWins()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // T1 reads and updates
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var u1 = new CompA(20);
        t1.UpdateEntity(pk, ref u1);

        // T2 updates and commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            var u2 = new CompA(30);
            t2.UpdateEntity(pk, ref u2);
            t2.Commit();
        }

        // T1 commits without handler — "last wins", T1's value should overwrite
        Assert.That(t1.Commit(), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(20), "Last writer wins: T1's value should be visible");
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. Handler — Delta Rebase (operational transform)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_DeltaRebase()
    {
        // Entity starts at 100. T2 sets it to 130 (delta +30). T1 sets it to 90 (delta -10).
        // After conflict resolution: committed(130) + delta(-10) = 120.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(100);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // T1 reads (100), will set to 90
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var u1 = new CompA(90);
        t1.UpdateEntity(pk, ref u1);

        // T2 reads (100), sets to 130, commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk, out CompA _);
            var u2 = new CompA(130);
            t2.UpdateEntity(pk, ref u2);
            t2.Commit();
        }

        // T1 commits with delta rebase handler
        var handlerCalled = false;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCalled = true;
            Assert.That(solver.HasConflict, Is.True);
            Assert.That(solver.PrimaryKey, Is.EqualTo(pk));

            var read = solver.ReadData<CompA>();
            var committed = solver.CommittedData<CompA>();
            var committing = solver.CommittingData<CompA>();

            Assert.That(read.A, Is.EqualTo(100), "Read should be the original snapshot value");
            Assert.That(committed.A, Is.EqualTo(130), "Committed should be T2's value");
            Assert.That(committing.A, Is.EqualTo(90), "Committing should be T1's dirty write");

            // Delta rebase: apply our intent on top of latest committed
            var delta = committing.A - read.A; // -10
            solver.ToCommitData<CompA>().A = committed.A + delta; // 130 + (-10) = 120
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCalled, Is.True, "Handler must be called when a conflict exists");

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(120), "Result should be committed(130) + delta(-10) = 120");
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. Handler — TakeCommitted (accept other transaction's value)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_TakeCommitted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var u1 = new CompA(20);
        t1.UpdateEntity(pk, ref u1);

        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk, out CompA _);
            var u2 = new CompA(30);
            t2.UpdateEntity(pk, ref u2);
            t2.Commit();
        }

        // Handler accepts the committed (T2's) value
        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            solver.TakeCommitted<CompA>();
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(30), "TakeCommitted should preserve T2's value");
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. Handler — TakeRead (revert to snapshot, discard all changes)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_TakeRead()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var u1 = new CompA(20);
        t1.UpdateEntity(pk, ref u1);

        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk, out CompA _);
            var u2 = new CompA(30);
            t2.UpdateEntity(pk, ref u2);
            t2.Commit();
        }

        // Handler reverts to original read snapshot
        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            solver.TakeRead<CompA>();
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(10), "TakeRead should revert to the original value");
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. Multi-Entity — handler called once per conflicting entity
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_MultipleEntities_HandlerCalledPerEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk1, pk2;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a1 = new CompA(100);
            var a2 = new CompA(200);
            pk1 = tCreate.CreateEntity(ref a1);
            pk2 = tCreate.CreateEntity(ref a2);
            tCreate.Commit();
        }

        // T1 reads both, applies deltas: pk1 -= 10, pk2 += 10
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk1, out CompA _);
        t1.ReadEntity(pk2, out CompA _);
        var u1 = new CompA(90);  // 100 - 10
        var u2 = new CompA(210); // 200 + 10
        t1.UpdateEntity(pk1, ref u1);
        t1.UpdateEntity(pk2, ref u2);

        // T2 modifies both, commits first: pk1 = 150, pk2 = 250
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk1, out CompA _);
            t2.ReadEntity(pk2, out CompA _);
            var v1 = new CompA(150);
            var v2 = new CompA(250);
            t2.UpdateEntity(pk1, ref v1);
            t2.UpdateEntity(pk2, ref v2);
            t2.Commit();
        }

        // T1 commits with delta rebase handler — should be called twice (once per entity)
        var handlerCallCount = 0;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCallCount++;
            var read = solver.ReadData<CompA>();
            var committed = solver.CommittedData<CompA>();
            var committing = solver.CommittingData<CompA>();
            var delta = committing.A - read.A;
            solver.ToCommitData<CompA>().A = committed.A + delta;
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCallCount, Is.EqualTo(2), "Handler should be called once per conflicting entity");

        // Verify: pk1 = 150 + (-10) = 140, pk2 = 250 + (+10) = 260
        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk1, out CompA r1);
        tRead.ReadEntity(pk2, out CompA r2);
        Assert.That(r1.A, Is.EqualTo(140), "pk1: committed(150) + delta(-10) = 140");
        Assert.That(r2.A, Is.EqualTo(260), "pk2: committed(250) + delta(+10) = 260");
    }

    // ═══════════════════════════════════════════════════════════════
    // 7. Multi-Entity — partial conflict (only one entity conflicts)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_MultipleEntities_PartialConflict()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk1, pk2;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a1 = new CompA(100);
            var a2 = new CompA(200);
            pk1 = tCreate.CreateEntity(ref a1);
            pk2 = tCreate.CreateEntity(ref a2);
            tCreate.Commit();
        }

        // T1 modifies both entities
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk1, out CompA _);
        t1.ReadEntity(pk2, out CompA _);
        var u1 = new CompA(110); // +10
        var u2 = new CompA(220); // +20
        t1.UpdateEntity(pk1, ref u1);
        t1.UpdateEntity(pk2, ref u2);

        // T2 modifies ONLY pk1, commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk1, out CompA _);
            var v1 = new CompA(150);
            t2.UpdateEntity(pk1, ref v1);
            t2.Commit();
        }

        // Handler should only be called for pk1 (conflict), not pk2 (no conflict)
        var handlerCallCount = 0;
        long conflictedPk = 0;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCallCount++;
            conflictedPk = solver.PrimaryKey;
            var read = solver.ReadData<CompA>();
            var committed = solver.CommittedData<CompA>();
            var committing = solver.CommittingData<CompA>();
            var delta = committing.A - read.A;
            solver.ToCommitData<CompA>().A = committed.A + delta;
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCallCount, Is.EqualTo(1), "Handler should only be called for the conflicting entity");
        Assert.That(conflictedPk, Is.EqualTo(pk1), "The conflicting entity should be pk1");

        // pk1 = 150 + 10 = 160, pk2 = 220 (no conflict, direct commit)
        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk1, out CompA r1);
        tRead.ReadEntity(pk2, out CompA r2);
        Assert.That(r1.A, Is.EqualTo(160), "pk1: committed(150) + delta(+10) = 160");
        Assert.That(r2.A, Is.EqualTo(220), "pk2: no conflict, T1's value committed directly");
    }

    // ═══════════════════════════════════════════════════════════════
    // 8. Concurrent — two threads, both with handlers
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_ConcurrentThreads_DeltaRebase_BothSucceed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(100);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        var barrier = new Barrier(2);
        var result1 = false;
        var result2 = false;

        // T1: delta +10, T2: delta +20. One commits first (no conflict), the other rebases.
        // Final value should be 100 + 10 + 20 = 130 regardless of commit order.
        var t1Task = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.ReadEntity(pk, out CompA read);
            var update = new CompA(read.A + 10);
            tx.UpdateEntity(pk, ref update);
            barrier.SignalAndWait();

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
                var r = solver.ReadData<CompA>();
                var c = solver.CommittedData<CompA>();
                var m = solver.CommittingData<CompA>();
                solver.ToCommitData<CompA>().A = c.A + (m.A - r.A);
            }

            result1 = tx.Commit(ConcurrencyConflictHandler);
        });

        var t2Task = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.ReadEntity(pk, out CompA read);
            var update = new CompA(read.A + 20);
            tx.UpdateEntity(pk, ref update);
            barrier.SignalAndWait();

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
                var r = solver.ReadData<CompA>();
                var c = solver.CommittedData<CompA>();
                var m = solver.CommittingData<CompA>();
                solver.ToCommitData<CompA>().A = c.A + (m.A - r.A);
            }

            result2 = tx.Commit(ConcurrencyConflictHandler);
        });

        Task.WaitAll(t1Task, t2Task);

        Assert.That(result1, Is.True, "T1 should commit successfully");
        Assert.That(result2, Is.True, "T2 should commit successfully");

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA final);
        Assert.That(final.A, Is.EqualTo(130), "100 + 10 + 20 = 130 (both deltas applied)");
    }

    // ═══════════════════════════════════════════════════════════════
    // 9. Solver reuse — thread-local instance is reused across commits
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_SolverReuse_ThreadLocal()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // First commit with handler — creates ThreadLocalConflictSolver
        {
            using var t1 = dbe.CreateQuickTransaction();
            var update = new CompA(2);
            t1.UpdateEntity(pk, ref update);

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
            }

            t1.Commit(ConcurrencyConflictHandler);
        }

        // Second commit with handler — reuses solver (Reset path)
        {
            using var t2 = dbe.CreateQuickTransaction();
            var update = new CompA(3);
            t2.UpdateEntity(pk, ref update);

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
            }

            Assert.That(t2.Commit(ConcurrencyConflictHandler), Is.True, "Second commit should succeed (solver reuse via Reset)");
        }

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(3));
    }

    // ═══════════════════════════════════════════════════════════════
    // 10. Default handler behavior — TakeCommitting (last writer wins)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_DefaultIsLastWins()
    {
        // If the handler does nothing, the default ToCommitData is a copy of CommittingData (last wins).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var u1 = new CompA(50);
        t1.UpdateEntity(pk, ref u1);

        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk, out CompA _);
            var u2 = new CompA(99);
            t2.UpdateEntity(pk, ref u2);
            t2.Commit();
        }

        // No-op handler: ToCommitData is pre-initialized with CommittingData (T1's dirty write = 50)
        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            // Intentionally empty — default behavior should be "last wins" (T1's value = 50)
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        tRead.ReadEntity(pk, out CompA result);
        Assert.That(result.A, Is.EqualTo(50), "Default (no-op handler) should be last writer wins: T1's value");
    }
}

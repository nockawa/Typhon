using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the ConcurrencyConflictSolver / ConcurrencyConflictHandler write-write conflict detection path.
/// Covers: Transaction.GetConflictSolver, ConcurrencyConflictSolver.Reset/Constructor/AddEntry,
///         and the conflict detection logic in Transaction.CommitComponent.
/// </summary>
class ConcurrencyConflictTests : TestBase<ConcurrencyConflictTests>
{
    // ═══════════════════════════════════════════════════════════════
    // Write-Write Conflict Detection via ConcurrencyConflictHandler
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WriteWrite_WithHandler_CommitSucceeds()
    {
        // Two transactions update the same entity. The second committer provides a
        // ConcurrencyConflictHandler, triggering the conflict detection path:
        // GetConflictSolver() → AddEntry() → EntryCount check.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entity E with initial value
        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // T1 reads and updates entity E (doesn't commit yet)
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var update1 = new CompA(20);
        t1.UpdateEntity(pk, ref update1);

        // T2 reads and updates entity E, commits first — creates a newer committed revision
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.ReadEntity(pk, out CompA _);
            var update2 = new CompA(30);
            t2.UpdateEntity(pk, ref update2);
            Assert.That(t2.Commit(), Is.True, "T2 should commit successfully (first committer, no conflict)");
        }

        // T1 commits with a conflict handler — T2 committed between T1's read and T1's commit
        // This exercises: GetConflictSolver() → conflict detection → AddEntry() → EntryCount > 0
        Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) =>
        {
            // Handler is scaffolding — not invoked in current implementation.
            // The conflict solver collects entries during the build phase.
        };

        var result = t1.Commit(handler);
        Assert.That(result, Is.True, "T1 commit should succeed even with conflict detected");
    }

    [Test]
    public void Conflict_WriteWrite_WithoutHandler_LastWins()
    {
        // Without a handler, conflict resolution uses default "last wins" — the committing
        // transaction's data overwrites the committed data. This is the baseline behavior.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entity E
        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // T1 reads and updates E
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk, out CompA _);
        var update1 = new CompA(20);
        t1.UpdateEntity(pk, ref update1);

        // T2 updates E, commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            var update2 = new CompA(30);
            t2.UpdateEntity(pk, ref update2);
            t2.Commit();
        }

        // T1 commits without handler — default "last wins", T1's data overwrites T2's
        Assert.That(t1.Commit(), Is.True);

        // Verify: latest committed data should be T1's value (20) — "last wins"
        {
            using var tRead = dbe.CreateQuickTransaction();
            tRead.ReadEntity(pk, out CompA result);
            Assert.That(result.A, Is.EqualTo(20), "Last writer wins: T1's value should be visible");
        }
    }

    [Test]
    public void Conflict_NoConflict_WithHandler_NoEntries()
    {
        // When a handler is provided but no conflict exists (single writer),
        // GetConflictSolver() is called but AddEntry() is never invoked.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entity E
        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // Single transaction updates E — no concurrent writer, so no conflict
        using var t1 = dbe.CreateQuickTransaction();
        var update1 = new CompA(42);
        t1.UpdateEntity(pk, ref update1);

        Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) =>
        {
            // Should not be called (no conflict)
        };

        Assert.That(t1.Commit(handler), Is.True, "Commit with handler but no conflict should succeed");

        // Verify the update applied
        {
            using var tRead = dbe.CreateQuickTransaction();
            tRead.ReadEntity(pk, out CompA result);
            Assert.That(result.A, Is.EqualTo(42));
        }
    }

    [Test]
    public void Conflict_MultipleEntities_WithHandler_MultipleConflicts()
    {
        // Tests conflict detection across multiple entities in the same commit.
        // Two transactions each update entities E1 and E2 — both should be detected as conflicts.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create two entities
        long pk1, pk2;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a1 = new CompA(10);
            var a2 = new CompA(20);
            pk1 = tCreate.CreateEntity(ref a1);
            pk2 = tCreate.CreateEntity(ref a2);
            tCreate.Commit();
        }

        // T1 reads and updates both entities
        using var t1 = dbe.CreateQuickTransaction();
        t1.ReadEntity(pk1, out CompA _);
        t1.ReadEntity(pk2, out CompA _);
        var u1 = new CompA(100);
        var u2 = new CompA(200);
        t1.UpdateEntity(pk1, ref u1);
        t1.UpdateEntity(pk2, ref u2);

        // T2 updates both entities, commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            var v1 = new CompA(300);
            var v2 = new CompA(400);
            t2.UpdateEntity(pk1, ref v1);
            t2.UpdateEntity(pk2, ref v2);
            Assert.That(t2.Commit(), Is.True);
        }

        // T1 commits with handler — both entities should have conflicts
        Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) => { };

        Assert.That(t1.Commit(handler), Is.True,
            "Commit with multiple conflicting entities should succeed");
    }

    [Test]
    public void Conflict_ConcurrentThreads_WithHandler_BothSucceed()
    {
        // Verifies that write-write conflict detection works under real concurrent execution.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entity E
        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(0);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        var barrier = new System.Threading.Barrier(2);
        var result1 = false;
        var result2 = false;

        // Two threads race to update the same entity
        var t1Task = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.ReadEntity(pk, out CompA _);
            var update = new CompA(111);
            tx.UpdateEntity(pk, ref update);
            barrier.SignalAndWait(); // Synchronize before committing
            Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) => { };
            result1 = tx.Commit(handler);
        });

        var t2Task = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.ReadEntity(pk, out CompA _);
            var update = new CompA(222);
            tx.UpdateEntity(pk, ref update);
            barrier.SignalAndWait(); // Synchronize before committing
            Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) => { };
            result2 = tx.Commit(handler);
        });

        Task.WaitAll(t1Task, t2Task);

        // Both commits should succeed (conflict detected but commit proceeds)
        Assert.That(result1, Is.True, "T1 should commit successfully");
        Assert.That(result2, Is.True, "T2 should commit successfully");
    }

    [Test]
    public void Conflict_GetConflictSolver_ThreadLocal_ReusesInstance()
    {
        // Exercises the GetConflictSolver thread-local reuse path:
        // First call creates a new solver; subsequent calls on the same thread reuse and Reset().
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entity
        long pk;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            pk = tCreate.CreateEntity(ref a);
            tCreate.Commit();
        }

        // First commit with handler on this thread → creates ThreadLocalConflictSolver
        {
            using var t1 = dbe.CreateQuickTransaction();
            var update = new CompA(2);
            t1.UpdateEntity(pk, ref update);
            Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) => { };
            t1.Commit(handler);
        }

        // Second commit with handler on same thread → reuses solver (Reset path)
        {
            using var t2 = dbe.CreateQuickTransaction();
            var update = new CompA(3);
            t2.UpdateEntity(pk, ref update);
            Transaction.ConcurrencyConflictHandler handler = (ref Transaction.ConcurrencyConflictSolver solver) => { };
            Assert.That(t2.Commit(handler), Is.True,
                "Second commit with handler should succeed (exercises solver Reset)");
        }
    }
}

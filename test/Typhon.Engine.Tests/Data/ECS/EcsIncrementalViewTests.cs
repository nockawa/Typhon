using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the incremental refresh path of EcsView via WhereField (Expression-based WHERE with FieldEvaluators).
/// Uses CompD (indexed fields: [Index] int B, [Index(AllowMultiple)] float A, [Index(AllowMultiple)] double C)
/// and CompDArch (archetype 201 with single component CompD).
/// </summary>
[NonParallelizable]
class EcsIncrementalViewTests : TestBase<EcsIncrementalViewTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompDArch>.Touch();
        Archetype<CompFArch>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.RegisterComponentFromAccessor<CompF>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Basic incremental view creation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_ToView_PopulatesInitialSet()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var d1 = new CompD(1.0f, 100, 2.0);  // B=100 → passes B >= 50
        var d2 = new CompD(2.0f, 30, 3.0);   // B=30  → fails  B >= 50
        var d3 = new CompD(3.0f, 75, 4.0);   // B=75  → passes B >= 50
        var id1 = tx.Spawn<CompDArch>(CompDArch.D.Set(in d1));
        tx.Spawn<CompDArch>(CompDArch.D.Set(in d2));
        var id3 = tx.Spawn<CompDArch>(CompDArch.D.Set(in d3));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();

        Assert.That(view.Count, Is.EqualTo(2), "Only entities with B >= 50 should be in view");
        Assert.That(view.Contains(id1), Is.True);
        Assert.That(view.Contains(id3), Is.True);
    }

    [Test]
    public void WhereField_ToView_RegistersWithViewRegistry()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var d = new CompD(1.0f, 100, 2.0);
        tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        tx.Commit();

        var table = dbe.GetComponentTable<CompD>();
        int viewsBefore = table.ViewRegistry.ViewCount;

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();

        Assert.That(table.ViewRegistry.ViewCount, Is.EqualTo(viewsBefore + 1),
            "View should register with ComponentTable's ViewRegistry");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Boundary crossing: field value crosses predicate threshold
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void IncrementalRefresh_FieldCrossesIn_EntityEntersView()
    {
        using var dbe = SetupEngine();

        // Spawn entity with B=30 (below threshold of 50)
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 30, 2.0);
            id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(0), "B=30 doesn't pass B >= 50");

        // Update B to 60 (crosses threshold)
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(CompDArch.D).B = 60;
            tx2.Commit();
        }

        // Incremental refresh should detect the boundary crossing
        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(id), Is.True);
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Added[0], Is.EqualTo(id));
    }

    [Test]
    public void IncrementalRefresh_FieldCrossesOut_EntityLeavesView()
    {
        using var dbe = SetupEngine();

        // Spawn entity with B=60 (above threshold)
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Update B to 30 (crosses below threshold)
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(CompDArch.D).B = 30;
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
        Assert.That(view.Removed[0], Is.EqualTo(id));
    }

    [Test]
    public void IncrementalRefresh_FieldStaysIn_NoChange()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Update B from 60 to 70 — still above threshold, no boundary crossing
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(CompDArch.D).B = 70;
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(id), Is.True);
        Assert.That(view.Added, Has.Count.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Creation / deletion via commit (isCreation / isDeletion flags)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void IncrementalRefresh_NewEntityMatchesPredicate_EntersView()
    {
        using var dbe = SetupEngine();

        // Initial: one matching entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Spawn new entity that matches predicate
        EntityId newId;
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var d = new CompD(2.0f, 80, 3.0);
            newId = tx2.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(newId), Is.True);
        Assert.That(view.Added, Has.Count.EqualTo(1));
    }

    [Test]
    public void IncrementalRefresh_NewEntityDoesNotMatch_StaysOut()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Spawn entity that does NOT match predicate (B=10)
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var d = new CompD(2.0f, 10, 3.0);
            tx2.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1), "New entity with B=10 should not enter view");
        Assert.That(view.HasChanges, Is.False);
    }

    [Test]
    public void IncrementalRefresh_DeleteEntity_LeavesView()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Destroy the entity
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.Destroy(id);
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Overflow recovery
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void IncrementalRefresh_Overflow_RecoversByFullRequery()
    {
        using var dbe = SetupEngine();

        // Spawn many entities that match
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 20; i++)
            {
                var d = new CompD(1.0f, 50 + i, 2.0);
                tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            tx.Commit();
        }

        // Create view with very small buffer to force overflow
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView(bufferCapacity: 4);
        Assert.That(view.Count, Is.EqualTo(20));

        // Generate many changes to overflow the small buffer
        for (int round = 0; round < 3; round++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 5; i++)
            {
                var d = new CompD(1.0f, 100 + round * 10 + i, 2.0);
                tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            tx.Commit();
        }

        // Refresh should recover via full re-query
        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(35), "All 20 + 15 entities with B >= 50");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Archetype mask filtering in incremental mode
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void IncrementalRefresh_IgnoresOtherArchetypeChanges()
    {
        using var dbe = SetupEngine();

        // Spawn a CompD entity (CompDArch) with B=60
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        // View on CompDArch with B >= 50
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Spawn CompF entity (different archetype) — should not affect the CompD view
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var f = new CompF(999, 1);
            tx2.Spawn<CompFArch>(CompFArch.F.Set(in f));
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1), "CompF entity should not affect CompD view");
        Assert.That(view.HasChanges, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose deregisters from ViewRegistry
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Dispose_DeregistersFromViewRegistry()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD(1.0f, 60, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }

        var table = dbe.GetComponentTable<CompD>();
        int viewsBefore = table.ViewRegistry.ViewCount;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();

        Assert.That(table.ViewRegistry.ViewCount, Is.EqualTo(viewsBefore + 1));

        view.Dispose();

        Assert.That(table.ViewRegistry.ViewCount, Is.EqualTo(viewsBefore),
            "Dispose should deregister from ViewRegistry");
    }
}

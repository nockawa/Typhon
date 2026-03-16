using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

class IntegrationQueryTests : TestBase<IntegrationQueryTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompDArch>.Touch();
        Archetype<CompDFArch>.Touch();
        Archetype<CompFArch>.Touch();
    }

    /// <summary>Reconstructs an EntityId from a raw pk value (test-only, uses InternalsVisibleTo).</summary>
    private static EntityId ToEntityId(long pk) =>
        Unsafe.As<long, EntityId>(ref pk);

    private static long CreateAndCommit(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
        t.Commit();
        return (long)id.RawValue;
    }

    private static long CreateBothAndCommit(DatabaseEngine dbe, float a, int b, double c, int gold, int rank)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var f = new CompF(gold, rank);
        var id = t.Spawn<CompDFArch>(CompDFArch.D.Set(in d), CompDFArch.F.Set(in f));
        t.Commit();
        return (long)id.RawValue;
    }

    private static void RefreshView(DatabaseEngine dbe, ViewBase view)
    {
        using var t = dbe.CreateQuickTransaction();
        view.Refresh(t);
    }

    private static void RefreshView(DatabaseEngine dbe, View<CompD, CompF> view)
    {
        using var t = dbe.CreateQuickTransaction();
        view.Refresh(t);
    }

    #region Chained Where

    [Test]
    public void ChainedWhere_SameAsCombined()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 5.0f, 50, 2.0);  // A>3 AND B>40 → match
        CreateAndCommit(dbe, 5.0f, 30, 2.0);  // A>3 but B≤40 → no
        CreateAndCommit(dbe, 2.0f, 45, 2.0);  // A≤3 but B>40 → no
        CreateAndCommit(dbe, 2.0f, 20, 2.0);  // both fail → no

        // Chained Where
        using var viewChained = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .Where(p => p.A > 3.0f)
            .ToView();

        // Combined Where
        using var viewCombined = dbe.Query<CompD>()
            .Where(p => p.A > 3.0f && p.B > 40)
            .ToView();

        Assert.That(viewChained.Count, Is.EqualTo(1));
        Assert.That(viewCombined.Count, Is.EqualTo(1));
        // Both views should contain the same entity
        foreach (var pk in viewChained)
        {
            Assert.That(viewCombined.Contains(pk), Is.True);
        }
    }

    [Test]
    public void ChainedWhere_ThreePredicates()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 5.0f, 50, 10.0);  // all pass
        CreateAndCommit(dbe, 5.0f, 45, 1.0);   // B>40 but C<=5 → fail
        CreateAndCommit(dbe, 5.0f, 30, 10.0);  // B<=40 → fail
        CreateAndCommit(dbe, 2.0f, 60, 10.0);  // A<=3 → fail

        using var view = dbe.Query<CompD>()
            .Where(p => p.A > 3.0f)
            .Where(p => p.B > 40)
            .Where(p => p.C > 5.0)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
    }

    #endregion

    #region View creation pipeline

    [Test]
    public void ToView_UsesPipeline_CorrectPopulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);  // B>40 → match
        var pk2 = CreateAndCommit(dbe, 1.0f, 30, 2.0);  // B≤40 → no
        var pk3 = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B>40 → match

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);
        Assert.That(view.Contains(pk3), Is.True);
    }

    [Test]
    public void ToView_CachesPlanOnView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view.HasCachedPlan, Is.True);
        Assert.That(view.ExecutionPlan.OrderedEvaluators, Is.Not.Null);
        Assert.That(view.ExecutionPlan.OrderedEvaluators.Length, Is.GreaterThan(0));
    }

    #endregion

    #region Overflow recovery

    [Test]
    public void OverflowRecovery_UsesCachedPlan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create entities before view
        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);

        // Small buffer to trigger overflow easily
        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView(bufferCapacity: 4);
        Assert.That(view.HasCachedPlan, Is.True);
        Assert.That(view.Contains(pk1), Is.True);

        // Create enough entities to overflow the buffer
        var matchingPks = new List<long> { pk1 };
        for (var i = 0; i < 5; i++)
        {
            matchingPks.Add(CreateAndCommit(dbe, 1.0f, 50 + i + 1, 2.0));
        }

        Assert.That(view.DeltaBuffer.HasOverflow, Is.True);

        // Refresh triggers overflow recovery using cached plan
        RefreshView(dbe, view);

        Assert.That(view.HasOverflow, Is.False);
        Assert.That(view.Count, Is.EqualTo(matchingPks.Count));
        foreach (var pk in matchingPks)
        {
            Assert.That(view.Contains(pk), Is.True);
        }
    }

    #endregion

    #region GetExecutionPlan

    [Test]
    public void GetExecutionPlan_ReturnsValidPlan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);

        var plan = dbe.Query<CompD>().Where(p => p.B > 40).GetExecutionPlan();

        // B is a unique index → should use secondary index
        Assert.That(plan.UsesSecondaryIndex, Is.True);
        Assert.That(plan.OrderedEvaluators, Is.Not.Null);
    }

    [Test]
    public void GetExecutionPlan_MatchesViewPlan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);

        var builderPlan = dbe.Query<CompD>().Where(p => p.B > 40).GetExecutionPlan();
        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view.ExecutionPlan.UsesSecondaryIndex, Is.EqualTo(builderPlan.UsesSecondaryIndex));
        Assert.That(view.ExecutionPlan.PrimaryFieldIndex, Is.EqualTo(builderPlan.PrimaryFieldIndex));
    }

    #endregion

    #region One-shot Execute

    [Test]
    public void Execute_Unordered_MatchesViewPopulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateAndCommit(dbe, 1.0f, 30, 2.0);
        var pk3 = CreateAndCommit(dbe, 1.0f, 60, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var results = dbe.Query<CompD>().Where(p => p.B > 40).Execute(tx);

        Assert.That(results.Count, Is.EqualTo(2));
        Assert.That(results, Does.Contain(pk1));
        Assert.That(results, Does.Not.Contain(pk2));
        Assert.That(results, Does.Contain(pk3));
    }

    [Test]
    public void Execute_NoSideEffects()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);

        var ct = dbe.GetComponentTable<CompD>();
        var viewCountBefore = ct.ViewRegistry.ViewCount;

        using var tx = dbe.CreateQuickTransaction();
        dbe.Query<CompD>().Where(p => p.B > 40).Execute(tx);

        Assert.That(ct.ViewRegistry.ViewCount, Is.EqualTo(viewCountBefore), "Execute should not register a view");
    }

    #endregion

    #region ExecuteOrdered

    [Test]
    public void ExecuteOrdered_OrderByB_Sorted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 70, 2.0);
        CreateAndCommit(dbe, 1.0f, 50, 2.0);
        CreateAndCommit(dbe, 1.0f, 60, 2.0);
        CreateAndCommit(dbe, 1.0f, 30, 2.0);  // doesn't match B > 40

        using var tx = dbe.CreateQuickTransaction();
        var results = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderBy<int>(p => p.B)
            .ExecuteOrdered(tx);

        Assert.That(results, Has.Count.EqualTo(3));
        // Verify ordering: should be sorted by B ascending
        for (var i = 1; i < results.Count; i++)
        {
            var prev = tx.Open(ToEntityId(results[i - 1])).Read(CompDArch.D);
            var curr = tx.Open(ToEntityId(results[i])).Read(CompDArch.D);
            Assert.That(prev.B, Is.LessThanOrEqualTo(curr.B), $"B values should be ascending at index {i}");
        }
    }

    [Test]
    public void ExecuteOrdered_Descending_ReverseSorted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 70, 2.0);
        CreateAndCommit(dbe, 1.0f, 50, 2.0);
        CreateAndCommit(dbe, 1.0f, 60, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var results = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderByDescending<int>(p => p.B)
            .ExecuteOrdered(tx);

        Assert.That(results, Has.Count.EqualTo(3));
        for (var i = 1; i < results.Count; i++)
        {
            var prev = tx.Open(ToEntityId(results[i - 1])).Read(CompDArch.D);
            var curr = tx.Open(ToEntityId(results[i])).Read(CompDArch.D);
            Assert.That(prev.B, Is.GreaterThanOrEqualTo(curr.B), $"B values should be descending at index {i}");
        }
    }

    [Test]
    public void ExecuteOrdered_OrderByPK_PKOrder()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateAndCommit(dbe, 1.0f, 60, 2.0);
        var pk3 = CreateAndCommit(dbe, 1.0f, 70, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var results = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderByPK()
            .ExecuteOrdered(tx);

        Assert.That(results, Has.Count.EqualTo(3));
        // PKs should be in ascending order
        for (var i = 1; i < results.Count; i++)
        {
            Assert.That(results[i], Is.GreaterThan(results[i - 1]));
        }
    }

    [Test]
    public void ExecuteOrdered_SkipTake_CorrectWindow()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create 5 matching entities with distinct B values
        for (var i = 0; i < 5; i++)
        {
            CreateAndCommit(dbe, 1.0f, 50 + i, 2.0);
        }

        using var tx = dbe.CreateQuickTransaction();

        // Get all results ordered by PK for reference
        var allResults = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderByPK()
            .ExecuteOrdered(tx);

        // Skip 1, take 2
        var paged = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderByPK()
            .Skip(1)
            .Take(2)
            .ExecuteOrdered(tx);

        Assert.That(paged, Has.Count.EqualTo(2));
        Assert.That(paged[0], Is.EqualTo(allResults[1]));
        Assert.That(paged[1], Is.EqualTo(allResults[2]));
    }

    [Test]
    public void ExecuteOrdered_SkipBeyondCount_Empty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);
        CreateAndCommit(dbe, 1.0f, 60, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var results = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderByPK()
            .Skip(100)
            .Take(10)
            .ExecuteOrdered(tx);

        Assert.That(results, Is.Empty);
    }

    #endregion

    #region Count / Any

    [Test]
    public void Count_MatchesExecuteCount()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);
        CreateAndCommit(dbe, 1.0f, 60, 2.0);
        CreateAndCommit(dbe, 1.0f, 30, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var count = dbe.Query<CompD>().Where(p => p.B > 40).Count(tx);
        var executeCount = dbe.Query<CompD>().Where(p => p.B > 40).Execute(tx).Count;

        Assert.That(count, Is.EqualTo(2));
        Assert.That(count, Is.EqualTo(executeCount));
    }

    [Test]
    public void Count_NoMatches_Zero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 30, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var count = dbe.Query<CompD>().Where(p => p.B > 40).Count(tx);

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Any_HasMatches_True()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 50, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var any = dbe.Query<CompD>().Where(p => p.B > 40).Any(tx);

        Assert.That(any, Is.True);
    }

    [Test]
    public void Any_NoMatches_False()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 30, 2.0);

        using var tx = dbe.CreateQuickTransaction();
        var any = dbe.Query<CompD>().Where(p => p.B > 40).Any(tx);

        Assert.That(any, Is.False);
    }

    #endregion

    #region Validation

    [Test]
    public void SkipWithoutOrderBy_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(() => dbe.Query<CompD>().Where(p => p.B > 40).Skip(1),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void TakeWithoutOrderBy_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(() => dbe.Query<CompD>().Where(p => p.B > 40).Take(1),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void OrderByWithToView_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(() => dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .OrderBy<int>(p => p.B)
            .ToView(),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void OrderByNonIndexedField_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        // CompE has no indexed fields
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(() => dbe.Query<CompE>()
            .Where(p => p.B > 40)
            .OrderBy<int>(p => p.B),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ExecuteOrderedWithoutOrderBy_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(() => dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .ExecuteOrdered(tx),
            Throws.TypeOf<InvalidOperationException>());
    }

    #endregion

    #region Two-component

    [Test]
    public void TwoComponent_Execute_UsesSecondaryIndex()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Need entities so the indexes are populated
        CreateBothAndCommit(dbe, 5.0f, 50, 2.0, 20000, 1);

        var plan = dbe.Query<CompD, CompF>()
            .Where((d, f) => d.B > 40 && f.Rank > 0)
            .GetExecutionPlan();

        // B is unique index on CompD, Rank is unique index on CompF
        // Plan should pick one of them as secondary index
        Assert.That(plan.UsesSecondaryIndex, Is.True);
    }

    [Test]
    public void TwoComponent_ToView_CorrectPopulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateBothAndCommit(dbe, 5.0f, 50, 2.0, 20000, 1);  // B>40 AND Gold>10000 → match
        var pk2 = CreateBothAndCommit(dbe, 5.0f, 30, 2.0, 20000, 2);  // B≤40 → no
        var pk3 = CreateBothAndCommit(dbe, 5.0f, 60, 2.0, 5000, 3);   // Gold≤10000 → no
        var pk4 = CreateBothAndCommit(dbe, 5.0f, 70, 2.0, 15000, 4);  // both pass → match

        using var view = dbe.Query<CompD, CompF>()
            .Where((d, f) => d.B > 40 && f.Gold > 10000)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);
        Assert.That(view.Contains(pk3), Is.False);
        Assert.That(view.Contains(pk4), Is.True);
        Assert.That(view.HasCachedPlan, Is.True);
    }

    [Test]
    public void TwoComponent_OverflowRecovery()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);

        using var view = dbe.Query<CompD, CompF>()
            .Where((d, f) => d.B > 40 && f.Gold > 10000)
            .ToView(bufferCapacity: 4);

        Assert.That(view.Contains(pk1), Is.True);

        // Overflow the buffer
        var matchingPks = new List<long> { pk1 };
        for (var i = 0; i < 5; i++)
        {
            matchingPks.Add(CreateBothAndCommit(dbe, 1.0f, 50 + i + 1, 2.0, 20000 + i, 10 + i));
        }

        Assert.That(view.DeltaBuffer.HasOverflow, Is.True);

        RefreshView(dbe, view);

        Assert.That(view.HasOverflow, Is.False);
        Assert.That(view.Count, Is.EqualTo(matchingPks.Count));
        foreach (var pk in matchingPks)
        {
            Assert.That(view.Contains(pk), Is.True);
        }
    }

    #endregion

    #region End-to-end

    [Test]
    public void EndToEnd_Schema_Insert_Query_Refresh_Overflow_Recovery()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Phase 1: Insert entities
        var pk1 = CreateAndCommit(dbe, 5.0f, 50, 10.0);  // all pass
        var pk2 = CreateAndCommit(dbe, 5.0f, 30, 10.0);  // B≤40
        var pk3 = CreateAndCommit(dbe, 2.0f, 60, 10.0);  // A≤3

        // Phase 2: Create view with chained predicates
        using var view = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .Where(p => p.A > 3.0f)
            .ToView(bufferCapacity: 4);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.HasCachedPlan, Is.True);

        // Phase 3: One-shot query matches view
        using var tx1 = dbe.CreateQuickTransaction();
        var executeResult = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .Where(p => p.A > 3.0f)
            .Execute(tx1);
        Assert.That(executeResult.Count, Is.EqualTo(view.Count));

        // Phase 4: Add matching entities and refresh
        var pk4 = CreateAndCommit(dbe, 4.0f, 55, 2.0);
        RefreshView(dbe, view);
        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Count.EqualTo(1));
        Assert.That(view.Contains(pk4), Is.True);
        view.ClearDelta();

        // Phase 5: Overflow and recovery
        for (var i = 0; i < 5; i++)
        {
            CreateAndCommit(dbe, 4.0f, 100 + i, 2.0);
        }

        Assert.That(view.DeltaBuffer.HasOverflow, Is.True);
        RefreshView(dbe, view);
        Assert.That(view.HasOverflow, Is.False);

        // Verify correct count: pk1, pk4, + 5 new = 7
        Assert.That(view.Count, Is.EqualTo(7));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk4), Is.True);

        // Phase 6: Verify Count/Any
        using var tx2 = dbe.CreateQuickTransaction();
        var count = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .Where(p => p.A > 3.0f)
            .Count(tx2);
        Assert.That(count, Is.EqualTo(7));

        var any = dbe.Query<CompD>()
            .Where(p => p.B > 40)
            .Where(p => p.A > 3.0f)
            .Any(tx2);
        Assert.That(any, Is.True);
    }

    #endregion
}

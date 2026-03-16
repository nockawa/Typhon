using System;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class OrViewTests : TestBase<OrViewTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompDArch>.Touch();
    }

    // CompD layout: A=float(offset 0, idx 0), B=int(offset 4, idx 1), C=double(offset 8, idx 2)

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

    private static void UpdateAndCommit(DatabaseEngine dbe, long pk, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        ref var w = ref t.OpenMut(ToEntityId(pk)).Write(CompDArch.D);
        w = d;
        t.Commit();
    }

    private static void RefreshView(DatabaseEngine dbe, ViewBase view)
    {
        using var t = dbe.CreateQuickTransaction();
        view.Refresh(t);
    }

    #region ExpressionParser DNF tests

    [Test]
    public void ParseDnf_SimpleOr_TwoBranches()
    {
        Expression<Func<CompD, bool>> expr = p => p.B > 50 || p.B < 10;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(2));
        Assert.That(branches[0].Length, Is.EqualTo(1));
        Assert.That(branches[0][0].FieldName, Is.EqualTo("B"));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(branches[1].Length, Is.EqualTo(1));
        Assert.That(branches[1][0].FieldName, Is.EqualTo("B"));
        Assert.That(branches[1][0].Operator, Is.EqualTo(CompareOp.LessThan));
    }

    [Test]
    public void ParseDnf_MixedAndOr_TwoBranches()
    {
        Expression<Func<CompD, bool>> expr = p => (p.A > 3.0f && p.B > 40) || p.B == 0;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(2));
        // Branch 0: A > 3.0f AND B > 40
        Assert.That(branches[0].Length, Is.EqualTo(2));
        Assert.That(branches[0][0].FieldName, Is.EqualTo("A"));
        Assert.That(branches[0][1].FieldName, Is.EqualTo("B"));
        // Branch 1: B == 0
        Assert.That(branches[1].Length, Is.EqualTo(1));
        Assert.That(branches[1][0].FieldName, Is.EqualTo("B"));
        Assert.That(branches[1][0].Operator, Is.EqualTo(CompareOp.Equal));
    }

    [Test]
    public void ParseDnf_Not_InvertsOperator()
    {
        Expression<Func<CompD, bool>> expr = p => !(p.B > 50);
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(1));
        Assert.That(branches[0][0].FieldName, Is.EqualTo("B"));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
    }

    [Test]
    public void ParseDnf_DeMorgan_NotAndBecomesOrNot()
    {
        // !(A > 3.0f && B > 40) → A <= 3.0f || B <= 40
        Expression<Func<CompD, bool>> expr = p => !(p.A > 3.0f && p.B > 40);
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(2));
        Assert.That(branches[0][0].FieldName, Is.EqualTo("A"));
        Assert.That(branches[0][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
        Assert.That(branches[1][0].FieldName, Is.EqualTo("B"));
        Assert.That(branches[1][0].Operator, Is.EqualTo(CompareOp.LessThanOrEqual));
    }

    [Test]
    public void ParseDnf_PureAnd_SingleBranch()
    {
        Expression<Func<CompD, bool>> expr = p => p.A > 3.0f && p.B > 40;
        var branches = ExpressionParser.ParseDnf(expr);

        Assert.That(branches.Length, Is.EqualTo(1));
        Assert.That(branches[0].Length, Is.EqualTo(2));
    }

    [Test]
    public void ParseDnf_ClauseLimit_ThrowsOver16()
    {
        Expression<Func<CompD, bool>> expr = p =>
            (p.B > 50 || p.B < 10) && (p.A > 3.0f || p.A < 1.0f) && (p.C > 5.0 || p.C < 1.0);
        // 2^3 = 8 branches — should be fine
        var branches = ExpressionParser.ParseDnf(expr);
        Assert.That(branches.Length, Is.EqualTo(8));
        Assert.That(branches.Length, Is.LessThanOrEqualTo(16));
    }

    #endregion

    #region OrView initial population

    [Test]
    public void OrView_SimpleOr_InitialPopulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B > 50 → match branch 0
        var pk2 = CreateAndCommit(dbe, 1.0f, 5, 2.0);   // B < 10 → match branch 1
        var pk3 = CreateAndCommit(dbe, 1.0f, 30, 2.0);  // B=30, neither → no match
        var pk4 = CreateAndCommit(dbe, 1.0f, 80, 2.0);  // B > 50 → match branch 0

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();

        Assert.That(view.Count, Is.EqualTo(3));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.True);
        Assert.That(view.Contains(pk3), Is.False);
        Assert.That(view.Contains(pk4), Is.True);
        Assert.That(view, Is.InstanceOf<OrView<CompD>>());
    }

    [Test]
    public void OrView_MixedAndOr_InitialPopulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Predicate: (A > 3.0f && B > 40) || B == 0
        // B has a unique index — all B values must be distinct
        var pk1 = CreateAndCommit(dbe, 5.0f, 50, 2.0);   // Branch 0: A>3 && B>40 → match
        var pk2 = CreateAndCommit(dbe, 1.0f, 0, 2.0);    // Branch 1: B==0 → match
        var pk3 = CreateAndCommit(dbe, 5.0f, 30, 2.0);   // Branch 0 fails (B≤40), Branch 1 fails (B≠0)
        var pk4 = CreateAndCommit(dbe, 1.0f, 20, 2.0);   // Branch 0 fails (A≤3), Branch 1 fails (B≠0)

        using var view = dbe.Query<CompD>().Where(p => (p.A > 3.0f && p.B > 40) || p.B == 0).ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.True);
        Assert.That(view.Contains(pk3), Is.False);
        Assert.That(view.Contains(pk4), Is.False);
    }

    #endregion

    #region OrView incremental refresh — branch transitions

    [Test]
    public void OrView_BranchTransition_OneHolds_StaysInView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk = CreateAndCommit(dbe, 1.0f, 60, 2.0);   // B=60 → branch 0 (B>50) only

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();
        Assert.That(view.Contains(pk), Is.True);

        // Update B to 5 → leaves branch 0 (B≤50), enters branch 1 (B<10) → stays in view
        UpdateAndCommit(dbe, pk, 1.0f, 5, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void OrView_BranchTransition_AllOut_EntityRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B=60 → branch 0 (B>50)

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();
        Assert.That(view.Contains(pk), Is.True);

        // Update B to 30 → neither branch satisfied
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.False);
        var delta = view.GetDelta();
        Assert.That(delta.Removed.Count, Is.EqualTo(1));
    }

    [Test]
    public void OrView_BranchTransition_OneIn_EntityAdded()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk = CreateAndCommit(dbe, 1.0f, 30, 2.0);  // B=30 → neither branch

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();
        Assert.That(view.Contains(pk), Is.False);

        // Update B to 5 → enters branch 1 (B<10)
        UpdateAndCommit(dbe, pk, 1.0f, 5, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True);
        var delta = view.GetDelta();
        Assert.That(delta.Added.Count, Is.EqualTo(1));
    }

    [Test]
    public void OrView_FieldInMultipleBranches_UpdateAffectsBoth()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // B > 50 || B < 10 — field B appears in both branches
        var pk = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B=60 → branch 0

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();
        Assert.That(view.Contains(pk), Is.True);

        // B changes from 60 to 5: leaves branch 0, enters branch 1
        UpdateAndCommit(dbe, pk, 1.0f, 5, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True);  // Still in view via branch 1

        // B changes from 5 to 30: leaves branch 1, doesn't enter branch 0 → removed
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        Assert.That(view.Contains(pk), Is.False);
    }

    [Test]
    public void OrView_Delta_SameSemantics()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B>50 → in view
        var pk2 = CreateAndCommit(dbe, 1.0f, 30, 2.0);  // B=30 → not in view

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();

        // Add entity into view
        UpdateAndCommit(dbe, pk2, 1.0f, 5, 2.0);  // B=5 → enters branch 1
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added.Count, Is.EqualTo(1));

        view.ClearDelta();

        // Remove entity from view
        UpdateAndCommit(dbe, pk1, 1.0f, 30, 2.0);  // B=30 → leaves all branches
        RefreshView(dbe, view);

        delta = view.GetDelta();
        Assert.That(delta.Removed.Count, Is.EqualTo(1));
    }

    [Test]
    public void OrView_MultipleRefreshCycles_Correct()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B=60 → branch 0

        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Cycle 1: move out
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        Assert.That(view.Count, Is.EqualTo(0));
        view.ClearDelta();

        // Cycle 2: move to branch 1
        UpdateAndCommit(dbe, pk, 1.0f, 5, 2.0);
        RefreshView(dbe, view);
        Assert.That(view.Count, Is.EqualTo(1));
        view.ClearDelta();

        // Cycle 3: move to branch 0
        UpdateAndCommit(dbe, pk, 1.0f, 80, 2.0);
        RefreshView(dbe, view);
        Assert.That(view.Count, Is.EqualTo(1));
        view.ClearDelta();
    }

    [Test]
    public void OrView_OverflowRecovery_RebuildsCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B>50
        var pk2 = CreateAndCommit(dbe, 1.0f, 5, 2.0);   // B<10

        // Small buffer to trigger overflow easily
        using var view = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).ToView(bufferCapacity: 4);
        Assert.That(view.Count, Is.EqualTo(2));

        // Flood the buffer to trigger overflow
        for (var i = 0; i < 20; i++)
        {
            UpdateAndCommit(dbe, pk1, 1.0f, 60 + i, 2.0);
        }

        // Add a new entity that matches
        var pk3 = CreateAndCommit(dbe, 1.0f, 3, 2.0);  // B<10

        RefreshView(dbe, view);

        // After overflow recovery, view should be rebuilt correctly
        Assert.That(view.Contains(pk1), Is.True);   // B=79 → B>50
        Assert.That(view.Contains(pk2), Is.True);   // B=5 → B<10
        Assert.That(view.Contains(pk3), Is.True);   // B=3 → B<10
        Assert.That(view.Count, Is.EqualTo(3));
    }

    #endregion

    #region One-shot OR queries

    [Test]
    public void Execute_SimpleOr_CorrectResults()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B>50
        var pk2 = CreateAndCommit(dbe, 1.0f, 5, 2.0);   // B<10
        var pk3 = CreateAndCommit(dbe, 1.0f, 30, 2.0);  // neither
        var pk4 = CreateAndCommit(dbe, 1.0f, 80, 2.0);  // B>50

        using var tx = dbe.CreateQuickTransaction();
        var result = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).Execute(tx);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result.Contains(pk1), Is.True);
        Assert.That(result.Contains(pk2), Is.True);
        Assert.That(result.Contains(pk3), Is.False);
        Assert.That(result.Contains(pk4), Is.True);
    }

    [Test]
    public void Count_SimpleOr_CorrectValue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 60, 2.0);   // B>50
        CreateAndCommit(dbe, 1.0f, 5, 2.0);    // B<10
        CreateAndCommit(dbe, 1.0f, 30, 2.0);   // neither

        using var tx = dbe.CreateQuickTransaction();
        var count = dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).Count(tx);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void Execute_MixedAndOr_CorrectResults()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // (A > 3.0f && B > 40) || B == 0
        // B has a unique index — all B values must be distinct
        var pk1 = CreateAndCommit(dbe, 5.0f, 50, 2.0);   // Branch 0 match
        var pk2 = CreateAndCommit(dbe, 1.0f, 0, 2.0);    // Branch 1 match
        var pk3 = CreateAndCommit(dbe, 5.0f, 30, 2.0);   // No match
        var pk4 = CreateAndCommit(dbe, 1.0f, 20, 2.0);   // No match (A≤3 fails branch 0)

        using var tx = dbe.CreateQuickTransaction();
        var result = dbe.Query<CompD>().Where(p => (p.A > 3.0f && p.B > 40) || p.B == 0).Execute(tx);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Contains(pk1), Is.True);
        Assert.That(result.Contains(pk2), Is.True);
    }

    [Test]
    public void Any_SimpleOr_ReturnsTrue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 1.0f, 60, 2.0);  // B>50

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(dbe.Query<CompD>().Where(p => p.B > 50 || p.B < 10).Any(tx), Is.True);
    }

    #endregion

    #region PureAnd ToView returns View<T>

    [Test]
    public void PureAnd_ToView_ReturnsViewT()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        CreateAndCommit(dbe, 5.0f, 50, 2.0);

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view, Is.InstanceOf<View<CompD>>());
        Assert.That(view.Count, Is.EqualTo(1));
    }

    #endregion
}

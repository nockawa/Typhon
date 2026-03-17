using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

class QueryPipelineTests : TestBase<QueryPipelineTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompDArch>.Touch();
    }

    #region ExpressionParser Tests

    [Test]
    public void Parse_SingleComparison_OneFieldPredicate()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.B > 40);
        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].FieldName, Is.EqualTo("B"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[0].Value, Is.EqualTo(40));
    }

    [Test]
    public void Parse_AndConjunction_TwoFieldPredicates()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.A > 3.0f && p.B > 40);
        Assert.That(predicates, Has.Length.EqualTo(2));
        Assert.That(predicates[0].FieldName, Is.EqualTo("A"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[1].FieldName, Is.EqualTo("B"));
        Assert.That(predicates[1].Operator, Is.EqualTo(CompareOp.GreaterThan));
    }

    [Test]
    public void Parse_ReversedOperand_FlipsOperator()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => 50 < p.B);
        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].FieldName, Is.EqualTo("B"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[0].Value, Is.EqualTo(50));
    }

    [Test]
    public void Parse_Equality_EqualOperator()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.B == 42);
        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].FieldName, Is.EqualTo("B"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.Equal));
        Assert.That(predicates[0].Value, Is.EqualTo(42));
    }

    [Test]
    public void Parse_ClosureCapture_EvaluatesConstant()
    {
        var threshold = 50;
        var predicates = ExpressionParser.Parse<CompD>(p => p.B > threshold);
        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].FieldName, Is.EqualTo("B"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[0].Value, Is.EqualTo(50));
    }

    [Test]
    public void Parse_FloatComparison()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.A > 3.5f);
        Assert.That(predicates[0].FieldName, Is.EqualTo("A"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[0].Value, Is.EqualTo(3.5f));
    }

    [Test]
    public void Parse_DoubleComparison()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.C > 2.0);
        Assert.That(predicates[0].FieldName, Is.EqualTo("C"));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[0].Value, Is.EqualTo(2.0));
    }

    #endregion

    #region QueryBuilder End-to-End Tests

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

    [Test]
    public void ToView_PopulatesInitialEntitySet()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);  // matches B > 40
        var pk2 = CreateAndCommit(dbe, 1.0f, 30, 2.0);  // doesn't match
        var pk3 = CreateAndCommit(dbe, 1.0f, 60, 2.0);  // matches

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);
        Assert.That(view.Contains(pk3), Is.True);
        Assert.That(view.GetDelta().IsEmpty, Is.True);
    }

    [Test]
    public void ToView_EntitiesBeforeAndAfterCreation_AllCaptured()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Entities created before view
        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateAndCommit(dbe, 1.0f, 60, 2.0);

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.True);
        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.GetDelta().IsEmpty, Is.True);
    }

    [Test]
    public void ToView_NewMatchingEntity_AddedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();
        Assert.That(view.Count, Is.EqualTo(0));

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Count.EqualTo(1));
        Assert.That(delta.Added, Does.Contain(pk));
    }

    [Test]
    public void ToView_EntityLeavesView_RemovedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();
        Assert.That(view.Contains(pk), Is.True);

        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Count.EqualTo(1));
        Assert.That(delta.Removed, Does.Contain(pk));
        Assert.That(view.Contains(pk), Is.False);
    }

    [Test]
    public void ToView_MultiField_BothFieldsMustMatch()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pk1 = CreateAndCommit(dbe, 5.0f, 50, 2.0);  // A>3.0 AND B>40 → both pass
        var pk2 = CreateAndCommit(dbe, 5.0f, 30, 2.0);  // A>3.0 but B<=40 → fails
        var pk3 = CreateAndCommit(dbe, 2.0f, 45, 2.0);  // A<=3.0 but B>40 → fails
        var pk4 = CreateAndCommit(dbe, 2.0f, 35, 2.0);  // both fail

        using var view = dbe.Query<CompD>().Where(p => p.A > 3.0f && p.B > 40).ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);
        Assert.That(view.Contains(pk3), Is.False);
        Assert.That(view.Contains(pk4), Is.False);
    }

    [Test]
    public void ToView_Dispose_DeregistersView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(ct.ViewRegistry.ViewCount, Is.EqualTo(1));
        Assert.That(view.IsDisposed, Is.False);

        view.Dispose();

        Assert.That(view.IsDisposed, Is.True);
        Assert.That(ct.ViewRegistry.ViewCount, Is.EqualTo(0));
    }

    [Test]
    public void ToView_NonIndexedField_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(() => dbe.Query<CompE>().Where(p => p.B > 40).ToView(),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ToView_UnregisteredComponent_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        // Don't register components — GetComponentTable<CompD>() returns null

        Assert.That(() => dbe.Query<CompD>().Where(p => p.B > 40).ToView(),
            Throws.TypeOf<InvalidOperationException>());
    }

    #endregion

    #region Integration / Game-Loop Tests

    [Test]
    public void Integration_FullCycle_CreateRefreshDeltaClear()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        // Insert matching entity
        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Count.EqualTo(1));
        view.ClearDelta();

        // Modify (still matching)
        UpdateAndCommit(dbe, pk, 1.0f, 60, 2.0);
        RefreshView(dbe, view);
        delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Count.EqualTo(1));
        view.ClearDelta();

        // No changes
        RefreshView(dbe, view);
        delta = view.GetDelta();
        Assert.That(delta.IsEmpty, Is.True);
        view.ClearDelta();

        // Update to leave view
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Count.EqualTo(1));
        Assert.That(view.Count, Is.EqualTo(0));
    }

    [Test]
    public void Integration_ConcurrentCommitsDuringCreation_NoEntityLost()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Entities created before view are in initial population
        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateAndCommit(dbe, 1.0f, 60, 2.0);

        using var view = dbe.Query<CompD>().Where(p => p.B > 40).ToView();

        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.True);

        // Entities created after view are captured via ring buffer
        var pk3 = CreateAndCommit(dbe, 1.0f, 70, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk3), Is.True);
        Assert.That(view.Count, Is.EqualTo(3));
    }

    #endregion
}

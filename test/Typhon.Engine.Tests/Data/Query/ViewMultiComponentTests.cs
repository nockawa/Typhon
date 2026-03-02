using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

class ViewMultiComponentTests : TestBase<ViewMultiComponentTests>
{
    // CompD layout: A=float(offset 0, idx 0), B=int(offset 4, idx 1), C=double(offset 8, idx 2)
    // CompF layout: Gold=int(offset 0, idx 0), Rank=int(offset 4, idx 1)

    private static FieldEvaluator MakeEvaluator(int fieldIndex, int fieldOffset, byte fieldSize,
        KeyType keyType, CompareOp compareOp, long threshold, byte componentTag = 0) =>
        new()
        {
            FieldIndex = fieldIndex,
            FieldOffset = fieldOffset,
            FieldSize = fieldSize,
            KeyType = keyType,
            CompareOp = compareOp,
            Threshold = threshold,
            ComponentTag = componentTag
        };

    /// <summary>Creates a cross-component view: CompD.B > 40 AND CompF.Gold > 10000.</summary>
    private static View<CompD, CompF> CreateCrossComponentView(ComponentTable ctD, ComponentTable ctF, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        var evaluators = new[]
        {
            MakeEvaluator(1, 4, 4, KeyType.Int, CompareOp.GreaterThan, 40, componentTag: 0),    // CompD.B > 40
            MakeEvaluator(0, 0, 4, KeyType.Int, CompareOp.GreaterThan, 10000, componentTag: 1)   // CompF.Gold > 10000
        };
        var view = new View<CompD, CompF>(evaluators, ctD.ViewRegistry, ctF.ViewRegistry, ctD, ctF, bufferCapacity);
        ctD.ViewRegistry.RegisterView(view, [1], 0);    // CompD field index 1 (B)
        ctF.ViewRegistry.RegisterView(view, [0], 1);    // CompF field index 0 (Gold)
        return view;
    }

    private static long CreateBothAndCommit(DatabaseEngine dbe, float a, int b, double c, int gold, int rank)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var f = new CompF(gold, rank);
        var pk = t.CreateEntity(ref d, ref f);
        t.Commit();
        return pk;
    }

    private static void UpdateCompDAndCommit(DatabaseEngine dbe, long pk, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        t.UpdateEntity(pk, ref d);
        t.Commit();
    }

    private static void UpdateCompFAndCommit(DatabaseEngine dbe, long pk, int gold, int rank)
    {
        using var t = dbe.CreateQuickTransaction();
        var f = new CompF(gold, rank);
        t.UpdateEntity(pk, ref f);
        t.Commit();
    }

    private static void DeleteBothAndCommit(DatabaseEngine dbe, long pk)
    {
        using var t = dbe.CreateQuickTransaction();
        t.DeleteEntity<CompD>(pk);
        t.DeleteEntity<CompF>(pk);
        t.Commit();
    }

    private static void RefreshView(DatabaseEngine dbe, View<CompD, CompF> view)
    {
        using var t = dbe.CreateQuickTransaction();
        view.Refresh(t);
    }

    #region Cross-component view tests

    [Test]
    public void CrossComponent_BothFieldsPass_Added()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // CompD.B=50 > 40 (pass), CompF.Gold=20000 > 10000 (pass)
        var pk = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        Assert.That(delta.Added[0], Is.EqualTo(pk));
        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void CrossComponent_OneFieldFails_NotInView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // CompD.B=50 > 40 (pass), CompF.Gold=5000 <= 10000 (fail)
        var pk = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 5000, 1);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.False);
        Assert.That(view.GetDelta().IsEmpty, Is.True);
    }

    [Test]
    public void CrossComponent_BothFieldsChangeInSameCommit_CorrectNetDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Start outside: B=30 (fail), Gold=5000 (fail)
        var pk = CreateBothAndCommit(dbe, 1.0f, 30, 2.0, 5000, 1);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update both in one commit: B=50 (pass), Gold=20000 (pass)
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 50, 2.0);
            t.UpdateEntity(pk, ref d);
            var f = new CompF(20000, 1);
            t.UpdateEntity(pk, ref f);
            t.Commit();
        }
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True, "Entity should be in view after both fields pass");
        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
    }

    [Test]
    public void CrossComponent_FieldsChangeInDifferentCommits_TSNCorrectEvaluation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Start outside: B=30 (fail), Gold=5000 (fail)
        var pk = CreateBothAndCommit(dbe, 1.0f, 30, 2.0, 5000, 1);
        RefreshView(dbe, view);
        view.ClearDelta();

        // First commit: update B=50 (pass), Gold still 5000 (fail) → not in view yet
        UpdateCompDAndCommit(dbe, pk, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        Assert.That(view.Contains(pk), Is.False, "Only B passes, Gold still fails");

        // Second commit: update Gold=20000 (pass), B still 50 (pass) → now in view
        UpdateCompFAndCommit(dbe, pk, 20000, 1);
        RefreshView(dbe, view);
        Assert.That(view.Contains(pk), Is.True, "Both fields now pass");
    }

    [Test]
    public void CrossComponent_BoundaryCrossingIN_ComponentReadHappens()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Start with CompD.B=30 (fail), CompF.Gold=20000 (pass)
        var pk = CreateBothAndCommit(dbe, 1.0f, 30, 2.0, 20000, 1);
        RefreshView(dbe, view);
        view.ClearDelta();
        Assert.That(view.Contains(pk), Is.False);

        // Update B to 50 → B crosses IN, CheckOtherFields reads CompF.Gold=20000 (pass) → Added
        UpdateCompDAndCommit(dbe, pk, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True);
        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
    }

    [Test]
    public void CrossComponent_BoundaryCrossingOUT_ImmediateRemoval()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Start in view: B=50 (pass), Gold=20000 (pass)
        var pk = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);
        RefreshView(dbe, view);
        view.ClearDelta();
        Assert.That(view.Contains(pk), Is.True);

        // Update B to 30 → B crosses OUT → Removed immediately (no component read needed)
        UpdateCompDAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.False);
        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
    }

    [Test]
    public void CrossComponent_NonBoundaryChange_NoComponentRead()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Start in view: B=50 (pass), Gold=20000 (pass)
        var pk = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 60 → stays passing, entity in view → Modified (no cross-component read)
        UpdateCompDAndCommit(dbe, pk, 1.0f, 60, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True);
        var delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Length.EqualTo(1));
    }

    [Test]
    public void CrossComponent_EntityMissingOneComponent_NotInView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Create entity with only CompD (no CompF)
        long pk;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 50, 2.0); // B=50 passes
            pk = t.CreateEntity(ref d);
            t.Commit();
        }
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.False, "Entity missing CompF should not be in view");
    }

    [Test]
    public void CrossComponent_FullCRUDCycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        using var view = CreateCrossComponentView(ctD, ctF);

        // Create matching entity
        var pk1 = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);
        // Create non-matching entity
        var pk2 = CreateBothAndCommit(dbe, 1.0f, 30, 2.0, 5000, 2);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);
        view.ClearDelta();

        // Update pk2 to matching
        UpdateCompDAndCommit(dbe, pk2, 1.0f, 60, 2.0);
        UpdateCompFAndCommit(dbe, pk2, 15000, 3);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(pk2), Is.True);
        view.ClearDelta();

        // Delete pk1
        DeleteBothAndCommit(dbe, pk1);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.False);
        Assert.That(view.Contains(pk2), Is.True);
        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
    }

    [Test]
    public void CrossComponent_FluentAPI_Integration()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create entities before the view
        var pk1 = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);  // matching
        var pk2 = CreateBothAndCommit(dbe, 1.0f, 30, 2.0, 5000, 2);   // non-matching

        // Build view via fluent API
        using var tx = dbe.CreateQuickTransaction();
        using var view = dbe.Query<CompD, CompF>()
            .Where((d, f) => d.B > 40 && f.Gold > 10000)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);

        // Add matching entity after view creation
        var pk3 = CreateBothAndCommit(dbe, 2.0f, 60, 3.0, 15000, 3);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(pk3), Is.True);
    }

    [Test]
    public void CrossComponent_Dispose_DeregistersFromBothRegistries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        var view = CreateCrossComponentView(ctD, ctF);

        Assert.That(ctD.ViewRegistry.ViewCount, Is.EqualTo(1));
        Assert.That(ctF.ViewRegistry.ViewCount, Is.EqualTo(1));

        view.Dispose();

        Assert.That(view.IsDisposed, Is.True);
        Assert.That(ctD.ViewRegistry.ViewCount, Is.EqualTo(0));
        Assert.That(ctF.ViewRegistry.ViewCount, Is.EqualTo(0));
    }

    #endregion

    #region Overflow recovery tests

    [Test]
    public void CrossComponent_OverflowRecovery_CorrectEntitySet()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ctD = dbe.GetComponentTable<CompD>();
        var ctF = dbe.GetComponentTable<CompF>();
        // Small buffer: capacity=4
        using var view = CreateCrossComponentView(ctD, ctF, bufferCapacity: 4);

        // Pre-populate one matching entity
        var pkOld = CreateBothAndCommit(dbe, 1.0f, 50, 2.0, 20000, 1);
        RefreshView(dbe, view);
        view.ClearDelta();
        Assert.That(view.Contains(pkOld), Is.True);

        // Create enough entities to overflow the buffer
        var pkNew1 = CreateBothAndCommit(dbe, 1.0f, 60, 2.0, 15000, 2); // matching
        CreateBothAndCommit(dbe, 1.0f, 30, 2.0, 5000, 3);  // non-matching
        var pkNew2 = CreateBothAndCommit(dbe, 1.0f, 70, 2.0, 25000, 4); // matching
        CreateBothAndCommit(dbe, 1.0f, 80, 2.0, 30000, 5); // matching
        CreateBothAndCommit(dbe, 1.0f, 90, 2.0, 40000, 6); // matching

        Assert.That(view.DeltaBuffer.HasOverflow, Is.True);

        RefreshView(dbe, view);

        Assert.That(view.HasOverflow, Is.False, "Should have recovered from overflow");
        Assert.That(view.Contains(pkOld), Is.True, "Old entity should still be in view");
        Assert.That(view.Contains(pkNew1), Is.True, "New matching entity should be in view");
        Assert.That(view.Contains(pkNew2), Is.True, "New matching entity should be in view");
        Assert.That(view.Count, Is.EqualTo(5), "5 matching entities total");
    }

    #endregion
}

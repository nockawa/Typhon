using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

class ViewTests : TestBase<ViewTests>
{
    // CompD layout: A=float(offset 0, idx 0), B=int(offset 4, idx 1), C=double(offset 8, idx 2)

    private static FieldEvaluator MakeEvaluator(int fieldIndex, int fieldOffset, byte fieldSize,
        KeyType keyType, CompareOp compareOp, long threshold) =>
        new()
        {
            FieldIndex = fieldIndex,
            FieldOffset = fieldOffset,
            FieldSize = fieldSize,
            KeyType = keyType,
            CompareOp = compareOp,
            Threshold = threshold
        };

    private static long FloatThreshold(float v)
    {
        var bits = Unsafe.As<float, int>(ref v);
        return (long)bits;
    }

    private static long DoubleThreshold(double v) => Unsafe.As<double, long>(ref v);

    /// <summary>Creates a single-field view on B: B > 40.</summary>
    private static View<CompD> CreateSingleFieldView(ViewRegistry registry,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        var evaluators = new[] { MakeEvaluator(1, 4, 4, KeyType.Int, CompareOp.GreaterThan, 40) };
        var view = new View<CompD>(evaluators, registry, bufferCapacity);
        registry.RegisterView(view);
        return view;
    }

    /// <summary>Creates a multi-field view on A > 3.0f AND B > 40.</summary>
    private static View<CompD> CreateMultiFieldView(ViewRegistry registry,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity)
    {
        var evaluators = new[]
        {
            MakeEvaluator(0, 0, 4, KeyType.Float, CompareOp.GreaterThan, FloatThreshold(3.0f)),
            MakeEvaluator(1, 4, 4, KeyType.Int, CompareOp.GreaterThan, 40)
        };
        var view = new View<CompD>(evaluators, registry, bufferCapacity);
        registry.RegisterView(view);
        return view;
    }

    private static long CreateAndCommit(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var pk = t.CreateEntity(ref d);
        t.Commit();
        return pk;
    }

    private static void UpdateAndCommit(DatabaseEngine dbe, long pk, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        t.UpdateEntity(pk, ref d);
        t.Commit();
    }

    private static void DeleteAndCommit(DatabaseEngine dbe, long pk)
    {
        using var t = dbe.CreateQuickTransaction();
        t.DeleteEntity<CompD>(pk);
        t.Commit();
    }

    private static void RefreshView(DatabaseEngine dbe, View<CompD> view)
    {
        using var t = dbe.CreateQuickTransaction();
        view.Refresh(t);
    }

    #region Single-field tests (B > 40)

    [Test]
    public void SingleField_EntityEntersView_AddedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create entity outside view (B=30), refresh to drain creation
        var pk = CreateAndCommit(dbe, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 50 → enters view
        UpdateAndCommit(dbe, pk, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        Assert.That(delta.Added[0], Is.EqualTo(pk));
        Assert.That(delta.Removed, Is.Empty);
        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void SingleField_EntityLeavesView_RemovedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create entity in view (B=50), refresh
        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 30 → leaves view
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
        Assert.That(delta.Removed[0], Is.EqualTo(pk));
        Assert.That(delta.Added, Is.Empty);
        Assert.That(view.Contains(pk), Is.False);
    }

    [Test]
    public void SingleField_EntityStaysInView_ModifiedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create entity in view (B=50), refresh
        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 60 → stays in view
        UpdateAndCommit(dbe, pk, 1.0f, 60, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Length.EqualTo(1));
        Assert.That(delta.Modified[0], Is.EqualTo(pk));
        Assert.That(delta.Added, Is.Empty);
        Assert.That(delta.Removed, Is.Empty);
        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void SingleField_EntityStaysOutside_NoDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create entity outside view (B=30), refresh
        var pk = CreateAndCommit(dbe, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 35 → still outside
        UpdateAndCommit(dbe, pk, 1.0f, 35, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.IsEmpty, Is.True);
        Assert.That(view.Contains(pk), Is.False);
    }

    [Test]
    public void SingleField_CreateMatchingEntity_AddedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        Assert.That(delta.Added[0], Is.EqualTo(pk));
        Assert.That(view.Count, Is.EqualTo(1));
    }

    [Test]
    public void SingleField_CreateNonMatchingEntity_NoDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        CreateAndCommit(dbe, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.IsEmpty, Is.True);
        Assert.That(view.Count, Is.EqualTo(0));
    }

    [Test]
    public void SingleField_DeleteEntityInView_RemovedDelta()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();
        Assert.That(view.Contains(pk), Is.True);

        DeleteAndCommit(dbe, pk);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
        Assert.That(delta.Removed[0], Is.EqualTo(pk));
        Assert.That(view.Contains(pk), Is.False);
    }

    [Test]
    public void SingleField_TSN_Filtering_EntriesBeyondTargetStayInBuffer()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create first entity
        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);

        // Create read transaction BEFORE second entity — its TSN should not see future commits
        using var readTx = dbe.CreateQuickTransaction();

        // Create second entity (commits at higher TSN)
        var pk2 = CreateAndCommit(dbe, 1.0f, 60, 2.0);

        // Refresh with readTx → should only drain pk1's entry
        view.Refresh(readTx);

        Assert.That(view.Contains(pk1), Is.True, "pk1 should be in view (TSN within range)");
        Assert.That(view.Contains(pk2), Is.False, "pk2 should NOT be in view (TSN beyond target)");
        Assert.That(view.DeltaBuffer.Count, Is.GreaterThan(0), "Buffer should still have pk2's entry");
    }

    [Test]
    public void SingleField_MultipleRefreshes_DeltaAccumulates()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // First commit + refresh → Added
        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        // Second commit + refresh (without ClearDelta) → should accumulate
        var pk2 = CreateAndCommit(dbe, 1.0f, 60, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(2));
    }

    [Test]
    public void SingleField_CompactDelta_AddedThenRemoved_Cancel()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create matching entity → Added
        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        // Do NOT clear delta

        // Update to non-matching → Removed
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        // Added + Removed = cancel
        var delta = view.GetDelta();
        Assert.That(delta.IsEmpty, Is.True, "Added + Removed should cancel out");
    }

    [Test]
    public void SingleField_CompactDelta_ModifiedThenRemoved_Removed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create matching, clear initial delta
        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update still matching → Modified
        UpdateAndCommit(dbe, pk, 1.0f, 60, 2.0);
        RefreshView(dbe, view);
        // Do NOT clear delta

        // Update to non-matching → Removed
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);

        // Modified + Removed = Removed
        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
        Assert.That(delta.Removed[0], Is.EqualTo(pk));
        Assert.That(delta.Added, Is.Empty);
        Assert.That(delta.Modified, Is.Empty);
    }

    [Test]
    public void SingleField_CompactDelta_RemovedThenAdded_Modified()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create matching, clear initial delta
        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update to non-matching → Removed
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        // Do NOT clear delta

        // Update back to matching → Added
        UpdateAndCommit(dbe, pk, 1.0f, 60, 2.0);
        RefreshView(dbe, view);

        // Removed + Added = Modified
        var delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Length.EqualTo(1));
        Assert.That(delta.Modified[0], Is.EqualTo(pk));
        Assert.That(delta.Added, Is.Empty);
        Assert.That(delta.Removed, Is.Empty);
    }

    [Test]
    public void SingleField_ClearDelta_ResetsAllSets()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.GetDelta().IsEmpty, Is.False, "Should have delta before clear");
        view.ClearDelta();
        Assert.That(view.GetDelta().IsEmpty, Is.True, "Should be empty after clear");
    }

    [Test]
    public void SingleField_Overflow_FreezeView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        // Small buffer: capacity=4
        using var view = CreateSingleFieldView(ct.ViewRegistry, bufferCapacity: 4);

        // Create 5 entities to overflow the capacity-4 buffer
        for (var i = 0; i < 5; i++)
        {
            CreateAndCommit(dbe, 1.0f, 50 + i, 2.0);
        }

        Assert.That(view.DeltaBuffer.HasOverflow, Is.True, "Buffer should have overflowed");
        Assert.That(view.HasOverflow, Is.False, "View hasn't detected overflow until Refresh");

        RefreshView(dbe, view);

        Assert.That(view.HasOverflow, Is.True, "View should detect overflow on Refresh");
        Assert.That(view.Count, Is.EqualTo(0), "Frozen view should not have processed any entries");
    }

    #endregion

    #region Multi-field tests (A > 3.0f AND B > 40)

    [Test]
    public void MultiField_BothFieldsPassOnCreation_Added()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // A=5.0 > 3.0, B=50 > 40 → both pass
        var pk = CreateAndCommit(dbe, 5.0f, 50, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        Assert.That(delta.Added[0], Is.EqualTo(pk));
        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void MultiField_OneFieldFailsOnCreation_NotInView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // A=5.0 > 3.0 (pass), B=30 <= 40 (fail) → not in view
        var pk = CreateAndCommit(dbe, 5.0f, 30, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.False);
        Assert.That(view.GetDelta().IsEmpty, Is.True);
    }

    [Test]
    public void MultiField_FieldCrossesIn_OtherFieldPasses_Added()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // Create with A=5.0 (pass), B=30 (fail) → not in view
        var pk = CreateAndCommit(dbe, 5.0f, 30, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 50 → B crosses IN, A still passes via CheckOtherFields → Added
        UpdateAndCommit(dbe, pk, 5.0f, 50, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void MultiField_FieldCrossesIn_OtherFieldFails_NotAdded()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // Create with A=2.0 (fail), B=30 (fail) → not in view
        var pk = CreateAndCommit(dbe, 2.0f, 30, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 50 → B crosses IN, but A=2.0 still fails via CheckOtherFields → not added
        UpdateAndCommit(dbe, pk, 2.0f, 50, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.False);
        Assert.That(view.GetDelta().IsEmpty, Is.True);
    }

    [Test]
    public void MultiField_FieldCrossesOut_Removed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // Create with both passing: A=5.0, B=50 → in view
        var pk = CreateAndCommit(dbe, 5.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();
        Assert.That(view.Contains(pk), Is.True);

        // Update B to 30 → B crosses OUT → Removed
        UpdateAndCommit(dbe, pk, 5.0f, 30, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
        Assert.That(view.Contains(pk), Is.False);
    }

    [Test]
    public void MultiField_FieldStaysSameSideInView_Modified()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // Create with both passing: A=5.0, B=50 → in view
        var pk = CreateAndCommit(dbe, 5.0f, 50, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update B to 60 → B stays passing, entity in view → Modified
        UpdateAndCommit(dbe, pk, 5.0f, 60, 2.0);
        RefreshView(dbe, view);

        var delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Length.EqualTo(1));
        Assert.That(view.Contains(pk), Is.True);
    }

    [Test]
    public void MultiField_BothFieldsChangeInSameCommit_CorrectNetResult()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateMultiFieldView(ct.ViewRegistry);

        // Create with A=2.0 (fail), B=30 (fail) → not in view
        var pk = CreateAndCommit(dbe, 2.0f, 30, 2.0);
        RefreshView(dbe, view);
        view.ClearDelta();

        // Update both: A=5.0 (pass), B=50 (pass) → should enter view
        UpdateAndCommit(dbe, pk, 5.0f, 50, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Contains(pk), Is.True, "Entity should be in view after both fields pass");
        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
    }

    #endregion

    #region Delta API & lifecycle tests

    [Test]
    public void GetDelta_ReturnsCopyArrays()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        RefreshView(dbe, view);

        var delta1 = view.GetDelta();
        var delta2 = view.GetDelta();

        Assert.That(delta1.Added, Is.Not.SameAs(delta2.Added), "GetDelta should return defensive copies");
        Assert.That(delta1.Added, Has.Length.EqualTo(1));
        Assert.That(delta2.Added, Has.Length.EqualTo(1));
    }

    [Test]
    public void Foreach_ReturnsAllPKs()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0);
        var pk2 = CreateAndCommit(dbe, 1.0f, 60, 2.0);
        RefreshView(dbe, view);

        var collected = new HashSet<long>();
        foreach (var pk in view)
        {
            collected.Add(pk);
        }

        Assert.That(collected, Has.Count.EqualTo(2));
        Assert.That(collected, Does.Contain(pk1));
        Assert.That(collected, Does.Contain(pk2));
    }

    [Test]
    public void Dispose_SetsIsDisposed_DeregistersFromRegistry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var view = CreateSingleFieldView(ct.ViewRegistry);

        Assert.That(ct.ViewRegistry.ViewCount, Is.EqualTo(1));
        Assert.That(view.IsDisposed, Is.False);

        view.Dispose();

        Assert.That(view.IsDisposed, Is.True);
        Assert.That(ct.ViewRegistry.ViewCount, Is.EqualTo(0));
    }

    [Test]
    public void Refresh_AfterDispose_ThrowsObjectDisposedException()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        var view = CreateSingleFieldView(ct.ViewRegistry);
        view.Dispose();

        using var t = dbe.CreateQuickTransaction();
        Assert.That(() => view.Refresh(t), Throws.TypeOf<ObjectDisposedException>());
    }

    #endregion

    #region Integration tests

    [Test]
    public void Integration_FullCRUDCycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        // Create matching and non-matching entities
        var pk1 = CreateAndCommit(dbe, 1.0f, 50, 2.0); // matching
        var pk2 = CreateAndCommit(dbe, 1.0f, 30, 2.0); // non-matching
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.True);
        Assert.That(view.Contains(pk2), Is.False);

        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        view.ClearDelta();

        // Update pk1 (still matching), pk2 (now matching)
        UpdateAndCommit(dbe, pk1, 1.0f, 60, 2.0);
        UpdateAndCommit(dbe, pk2, 1.0f, 55, 2.0);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(2));
        delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Length.EqualTo(1));
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        view.ClearDelta();

        // Delete pk1
        DeleteAndCommit(dbe, pk1);
        RefreshView(dbe, view);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(pk1), Is.False);
        Assert.That(view.Contains(pk2), Is.True);
        delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
    }

    [Test]
    public void Integration_FloatFieldView_BoundaryDetection()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // View on field A: A > 3.0f (single evaluator on float field)
        var evaluators = new[] { MakeEvaluator(0, 0, 4, KeyType.Float, CompareOp.GreaterThan, FloatThreshold(3.0f)) };
        using var view = new View<CompD>(evaluators, ct.ViewRegistry);
        ct.ViewRegistry.RegisterView(view);

        var pkExact = CreateAndCommit(dbe, 3.0f, 10, 2.0);  // A=3.0, not > 3.0
        var pkAbove = CreateAndCommit(dbe, 3.01f, 11, 2.0);  // A=3.01, passes
        var pkBelow = CreateAndCommit(dbe, 2.99f, 12, 2.0);  // A=2.99, fails
        RefreshView(dbe, view);

        Assert.That(view.Contains(pkExact), Is.False, "A=3.0 should not match A > 3.0");
        Assert.That(view.Contains(pkAbove), Is.True, "A=3.01 should match A > 3.0");
        Assert.That(view.Contains(pkBelow), Is.False, "A=2.99 should not match A > 3.0");
        Assert.That(view.Count, Is.EqualTo(1));
    }

    [Test]
    public void Integration_GameLoopPattern()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();
        using var view = CreateSingleFieldView(ct.ViewRegistry);

        var pk = CreateAndCommit(dbe, 1.0f, 50, 2.0);

        // Frame 1: refresh → Added
        RefreshView(dbe, view);
        var delta = view.GetDelta();
        Assert.That(delta.Added, Has.Length.EqualTo(1));
        view.ClearDelta();

        // Frame 2: modify → Modified
        UpdateAndCommit(dbe, pk, 1.0f, 60, 2.0);
        RefreshView(dbe, view);
        delta = view.GetDelta();
        Assert.That(delta.Modified, Has.Length.EqualTo(1));
        Assert.That(delta.Added, Is.Empty);
        Assert.That(delta.Removed, Is.Empty);
        view.ClearDelta();

        // Frame 3: no changes → empty delta
        RefreshView(dbe, view);
        delta = view.GetDelta();
        Assert.That(delta.IsEmpty, Is.True);
        view.ClearDelta();

        // Frame 4: remove → Removed
        UpdateAndCommit(dbe, pk, 1.0f, 30, 2.0);
        RefreshView(dbe, view);
        delta = view.GetDelta();
        Assert.That(delta.Removed, Has.Length.EqualTo(1));
        Assert.That(view.Count, Is.EqualTo(0));
    }

    #endregion
}

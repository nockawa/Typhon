using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

public unsafe class View<T> : ViewBase where T : unmanaged
{
    private readonly ViewRegistry _registry;
    private readonly ComponentTable _componentTable;
    private readonly int[] _evaluatorLookup;

    internal View(FieldEvaluator[] evaluators, ViewRegistry registry, ComponentTable componentTable, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity,
        long baseTSN = 0) : base(evaluators, BuildFieldDependencies(evaluators), componentTable.DBE.MemoryAllocator, componentTable, bufferCapacity, baseTSN)
    {
        _registry = registry;
        _componentTable = componentTable;
        _evaluatorLookup = BuildEvaluatorLookup(evaluators);
    }

    internal View(FieldEvaluator[] evaluators, ViewRegistry registry, ComponentTable componentTable, ExecutionPlan plan,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(evaluators, BuildFieldDependencies(evaluators), componentTable.DBE.MemoryAllocator, componentTable, [plan], bufferCapacity, baseTSN)
    {
        _registry = registry;
        _componentTable = componentTable;
        _evaluatorLookup = BuildEvaluatorLookup(evaluators);
    }

    protected override void DeregisterFromRegistries() => _registry.DeregisterView(this);

    /// <summary>
    /// Drain the ring buffer up to the transaction's snapshot TSN, evaluate field predicates,
    /// and update the entity set and delta tracking sets.
    /// </summary>
    public override void Refresh(Transaction tx)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(View<T>));
        }

        if (DeltaBuffer.HasOverflow)
        {
            SetOverflowDetected(true);
            RefreshFull(tx);
            return;
        }

        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out _))
        {
            DeltaBuffer.Advance();
            ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, tx);
            SetLastRefreshTSN(tsn);
        }
    }

    private bool EvaluateAllFields(ref T comp)
    {
        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < _evaluators.Length; i++)
        {
            ref var eval = ref _evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }

    private void RefreshFull(Transaction tx)
    {
        // Snapshot old entity set for delta computation
        var oldEntities = new HashSet<long>(_entityIds);

        // Reset buffer — clears overflow flag, reanchors base TSN
        DeltaBuffer.Reset(tx.TSN);

        // Clear and rebuild entity set
        _entityIds.Clear();
        if (HasCachedPlanInternal)
        {
            // For single-component views, plan.OrderedEvaluators == the full evaluator set (selectivity-ordered)
            PipelineExecutor.Instance.Execute<T>(CachedPlan, CachedPlan.OrderedEvaluators, _componentTable, tx, _entityIds);
        }
        else
        {
            // Fallback for views constructed without a plan (e.g., test harness)
            var pkIndex = _componentTable.PrimaryKeyIndex;
            foreach (var kv in pkIndex.EnumerateLeaves())
            {
                if (tx.QueryRead<T>(kv.Key, out var comp))
                {
                    if (EvaluateAllFields(ref comp))
                    {
                        _entityIds.Add(kv.Key);
                    }
                }
            }
        }

        DrainBufferAfterRefreshFull(tx.TSN);
        ComputeRefreshFullDeltas(oldEntities);

        SetOverflowDetected(false);
        SetLastRefreshTSN(tx.TSN);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntry(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        ref var eval = ref FindEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        var wasInView = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
        var shouldBeInView = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

        if (_evaluators.Length == 1)
        {
            ApplyDelta(entry.EntityPK, wasInView, shouldBeInView);
        }
        else
        {
            ProcessMultiField(entry.EntityPK, fieldIndex, wasInView, shouldBeInView, tx);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EvaluateKey(ref FieldEvaluator eval, ref KeyBytes8 key) => FieldEvaluator.Evaluate(ref eval, (byte*)Unsafe.AsPointer(ref key));

    private void ProcessMultiField(long pk, int fieldIndex, bool wasInView, bool shouldBeInView, Transaction tx)
    {
        if (wasInView == shouldBeInView)
        {
            // Field didn't cross boundary — if entity is in view and field still passes, mark Modified
            if (shouldBeInView && _entityIds.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Modified);
            }
            return;
        }

        if (!wasInView)
        {
            // OUT→IN: verify all other fields pass before adding
            if (CheckOtherFields(pk, fieldIndex, tx))
            {
                ApplyDelta(pk, false, true);
            }
        }
        else
        {
            // IN→OUT: remove if entity was in view
            if (_entityIds.Contains(pk))
            {
                ApplyDelta(pk, true, false);
            }
        }
    }

    private bool CheckOtherFields(long pk, int changedFieldIndex, Transaction tx)
    {
        if (!tx.QueryRead<T>(pk, out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < _evaluators.Length; i++)
        {
            if (_evaluators[i].FieldIndex == changedFieldIndex)
            {
                continue;
            }
            ref var eval = ref _evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldEvaluator FindEvaluator(int fieldIndex)
    {
        if ((uint)fieldIndex < (uint)_evaluatorLookup.Length)
        {
            var idx = _evaluatorLookup[fieldIndex];
            if (idx >= 0)
            {
                return ref _evaluators[idx];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }

    private static int[] BuildEvaluatorLookup(FieldEvaluator[] evaluators)
    {
        var maxField = -1;
        for (var i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex > maxField)
            {
                maxField = evaluators[i].FieldIndex;
            }
        }
        if (maxField < 0)
        {
            return [];
        }
        var lookup = new int[maxField + 1];
        Array.Fill(lookup, -1);
        for (var i = 0; i < evaluators.Length; i++)
        {
            lookup[evaluators[i].FieldIndex] = i;
        }
        return lookup;
    }

    private static int[] BuildFieldDependencies(FieldEvaluator[] evaluators)
    {
        var fieldIndices = new HashSet<int>();
        for (var i = 0; i < evaluators.Length; i++)
        {
            fieldIndices.Add(evaluators[i].FieldIndex);
        }
        var deps = new int[fieldIndices.Count];
        fieldIndices.CopyTo(deps);
        Array.Sort(deps);
        return deps;
    }
}

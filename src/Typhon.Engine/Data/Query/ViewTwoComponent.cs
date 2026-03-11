using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

public unsafe class View<T1, T2> : ViewBase where T1 : unmanaged where T2 : unmanaged
{
    private readonly ViewRegistry _registry1;
    private readonly ViewRegistry _registry2;
    private readonly ComponentTable _componentTable1;
    private readonly ComponentTable _componentTable2;
    private readonly ComponentTable _planTable;
    private readonly int[] _evalLookupTag0;
    private readonly int[] _evalLookupTag1;

    internal View(FieldEvaluator[] evaluators, ViewRegistry registry1, ViewRegistry registry2, ComponentTable componentTable1, ComponentTable componentTable2,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(evaluators, [], componentTable1.DBE.MemoryAllocator, componentTable1, bufferCapacity, baseTSN)
    {
        _registry1 = registry1;
        _registry2 = registry2;
        _componentTable1 = componentTable1;
        _componentTable2 = componentTable2;
        (_evalLookupTag0, _evalLookupTag1) = BuildEvaluatorLookupByTag(evaluators);
    }

    internal View(FieldEvaluator[] evaluators, ViewRegistry registry1, ViewRegistry registry2, ComponentTable componentTable1, ComponentTable componentTable2,
        ExecutionPlan plan, ComponentTable planTable, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(evaluators, [], componentTable1.DBE.MemoryAllocator, componentTable1, [plan], bufferCapacity, baseTSN)
    {
        _registry1 = registry1;
        _registry2 = registry2;
        _componentTable1 = componentTable1;
        _componentTable2 = componentTable2;
        _planTable = planTable;
        (_evalLookupTag0, _evalLookupTag1) = BuildEvaluatorLookupByTag(evaluators);
    }

    protected override void DeregisterFromRegistries()
    {
        _registry1.DeregisterView(this);
        _registry2.DeregisterView(this);
    }

    public override void Refresh(Transaction tx)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(View<T1, T2>));
        }

        if (DeltaBuffer.HasOverflow)
        {
            SetOverflowDetected(true);
            RefreshFull(tx);
            return;
        }

        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out var componentTag))
        {
            DeltaBuffer.Advance();
            ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, componentTag, tx);
            SetLastRefreshTSN(tsn);
        }
    }

    private bool EvaluateAllFields(ref T1 comp1, ref T2 comp2)
    {
        var comp1Ptr = (byte*)Unsafe.AsPointer(ref comp1);
        var comp2Ptr = (byte*)Unsafe.AsPointer(ref comp2);
        for (var i = 0; i < _evaluators.Length; i++)
        {
            ref var eval = ref _evaluators[i];
            var ptr = eval.ComponentTag == 0 ? comp1Ptr : comp2Ptr;
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
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
            // Use _evaluators (combined T1+T2 evaluators) rather than CachedPlan.OrderedEvaluators, which only contains the driving table's evaluators.
            // The two-component executor needs evaluators from both components to filter correctly via ComponentTag dispatch.
            PipelineExecutor.Instance.Execute<T1, T2>(CachedPlan, _evaluators, _planTable, tx, _entityIds);
        }
        else
        {
            // Fallback for views constructed without a plan (e.g., test harness)
            var pkIndex = _componentTable1.PrimaryKeyIndex;
            foreach (var kv in pkIndex.EnumerateLeaves())
            {
                if (tx.ReadEntity<T1>(kv.Key, out var comp1) && tx.ReadEntity<T2>(kv.Key, out var comp2))
                {
                    if (EvaluateAllFields(ref comp1, ref comp2))
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
    private void ProcessEntry(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, byte componentTag, Transaction tx)
    {
        ref var eval = ref FindEvaluator(fieldIndex, componentTag);
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
            ProcessMultiField(entry.EntityPK, fieldIndex, componentTag, wasInView, shouldBeInView, tx);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EvaluateKey(ref FieldEvaluator eval, ref KeyBytes8 key) => FieldEvaluator.Evaluate(ref eval, (byte*)Unsafe.AsPointer(ref key));

    private void ProcessMultiField(long pk, int fieldIndex, byte componentTag, bool wasInView, bool shouldBeInView, Transaction tx)
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
            if (CheckOtherFields(pk, fieldIndex, componentTag, tx))
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

    private bool CheckOtherFields(long pk, int changedFieldIndex, byte changedComponentTag, Transaction tx)
    {
        // Declare at method scope so pointers remain valid for the entire loop (Issue #7 fix)
        byte* comp1Ptr = null;
        byte* comp2Ptr = null;
        var read1 = false;
        var read2 = false;
        // ReSharper disable TooWideLocalVariableScope
        // ReSharper disable InlineOutVariableDeclaration
        T1 comp1 = default;
        T2 comp2 = default;
        // ReSharper restore InlineOutVariableDeclaration
        // ReSharper restore TooWideLocalVariableScope

        for (var i = 0; i < _evaluators.Length; i++)
        {
            ref var eval = ref _evaluators[i];
            if (eval.FieldIndex == changedFieldIndex && eval.ComponentTag == changedComponentTag)
            {
                continue;
            }

            if (eval.ComponentTag == 0)
            {
                if (!read1)
                {
                    if (!tx.ReadEntity(pk, out comp1))
                    {
                        return false;
                    }
                    comp1Ptr = (byte*)Unsafe.AsPointer(ref comp1);
                    read1 = true;
                }
                if (!FieldEvaluator.Evaluate(ref eval, comp1Ptr + eval.FieldOffset))
                {
                    return false;
                }
            }
            else
            {
                if (!read2)
                {
                    if (!tx.ReadEntity(pk, out comp2))
                    {
                        return false;
                    }
                    comp2Ptr = (byte*)Unsafe.AsPointer(ref comp2);
                    read2 = true;
                }
                if (!FieldEvaluator.Evaluate(ref eval, comp2Ptr + eval.FieldOffset))
                {
                    return false;
                }
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldEvaluator FindEvaluator(int fieldIndex, byte componentTag)
    {
        var lookup = componentTag == 0 ? _evalLookupTag0 : _evalLookupTag1;
        if ((uint)fieldIndex < (uint)lookup.Length)
        {
            var idx = lookup[fieldIndex];
            if (idx >= 0)
            {
                return ref _evaluators[idx];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }

    private static (int[], int[]) BuildEvaluatorLookupByTag(FieldEvaluator[] evaluators)
    {
        var maxField0 = -1;
        var maxField1 = -1;
        for (var i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].ComponentTag == 0)
            {
                if (evaluators[i].FieldIndex > maxField0)
                {
                    maxField0 = evaluators[i].FieldIndex;
                }
            }
            else
            {
                if (evaluators[i].FieldIndex > maxField1)
                {
                    maxField1 = evaluators[i].FieldIndex;
                }
            }
        }
        var lookup0 = maxField0 >= 0 ? new int[maxField0 + 1] : [];
        var lookup1 = maxField1 >= 0 ? new int[maxField1 + 1] : [];
        if (maxField0 >= 0)
        {
            Array.Fill(lookup0, -1);
        }
        if (maxField1 >= 0)
        {
            Array.Fill(lookup1, -1);
        }
        for (var i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].ComponentTag == 0)
            {
                lookup0[evaluators[i].FieldIndex] = i;
            }
            else
            {
                lookup1[evaluators[i].FieldIndex] = i;
            }
        }
        return (lookup0, lookup1);
    }
}

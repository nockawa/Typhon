using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// OR-disjunction view: tracks entities matching any branch of a DNF predicate.
/// Each branch is an AND conjunction; the view is the union of all branches.
/// Per-entity branch bitmaps track which branches are currently satisfied, enabling efficient incremental refresh on field changes.
/// </summary>
public unsafe class OrView<T> : ViewBase where T : unmanaged
{
    private readonly ViewRegistry _registry;
    private readonly ComponentTable _componentTable;
    private readonly Dictionary<long, ushort> _branchBitmaps = new();
    private readonly FieldEvaluator[][] _branchEvaluators;

    internal OrView(FieldEvaluator[][] branchEvaluators, ExecutionPlan[] plans, ViewRegistry registry, ComponentTable componentTable, 
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) : 
        base(FlattenEvaluators(branchEvaluators), BuildFieldDependencies(branchEvaluators), componentTable.DBE.MemoryAllocator, componentTable, plans,
            bufferCapacity, baseTSN)
    {
        _branchEvaluators = branchEvaluators;
        _registry = registry;
        _componentTable = componentTable;
    }

    protected override void DeregisterFromRegistries() => _registry.DeregisterView(this);

    public override void Refresh(Transaction tx)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(OrView<T>));
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

    /// <summary>
    /// Populates the view's initial entity set by executing each branch plan and unioning results.
    /// Called by QueryBuilder during ToView() construction.
    /// </summary>
    internal void PopulateInitial(Transaction tx)
    {
        var plans = CachedPlans;
        if (plans == null)
        {
            return;
        }

        for (var b = 0; b < plans.Length; b++)
        {
            var branchResult = new HashSet<long>();
            PipelineExecutor.Instance.Execute<T>(plans[b], plans[b].OrderedEvaluators, _componentTable, tx, branchResult);
            var bit = (ushort)(1 << b);
            foreach (var pk in branchResult)
            {
                _entityIds.Add(pk);
                _branchBitmaps.TryGetValue(pk, out var existing);
                _branchBitmaps[pk] = (ushort)(existing | bit);
            }
        }
    }

    private void RefreshFull(Transaction tx)
    {
        var oldEntities = new HashSet<long>(_entityIds);

        DeltaBuffer.Reset(tx.TSN);

        _entityIds.Clear();
        _branchBitmaps.Clear();

        if (HasCachedPlanInternal)
        {
            var plans = CachedPlans;
            for (var b = 0; b < plans.Length; b++)
            {
                var branchResult = new HashSet<long>();
                PipelineExecutor.Instance.Execute<T>(plans[b], plans[b].OrderedEvaluators, _componentTable, tx, branchResult);
                var bit = (ushort)(1 << b);
                foreach (var pk in branchResult)
                {
                    _entityIds.Add(pk);
                    _branchBitmaps.TryGetValue(pk, out var existing);
                    _branchBitmaps[pk] = (ushort)(existing | bit);
                }
            }
        }
        else
        {
            // Fallback: brute-force PK scan
            var pkIndex = _componentTable.PrimaryKeyIndex;
            foreach (var kv in pkIndex.EnumerateLeaves())
            {
                if (tx.ReadEntity<T>(kv.Key, out var comp))
                {
                    var bitmap = EvaluateAllBranches(ref comp);
                    if (bitmap != 0)
                    {
                        _entityIds.Add(kv.Key);
                        _branchBitmaps[kv.Key] = bitmap;
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
        var pk = entry.EntityPK;

        // Get current bitmap (0 if not tracked)
        _branchBitmaps.TryGetValue(pk, out var oldBitmap);
        if (isCreation)
        {
            oldBitmap = 0;
        }

        var wasInView = oldBitmap != 0;
        var newBitmap = oldBitmap;

        // For each branch that references this field, evaluate the boundary crossing
        for (var b = 0; b < _branchEvaluators.Length; b++)
        {
            var branchEvals = _branchEvaluators[b];
            var evalIndex = FindEvaluatorInBranch(branchEvals, fieldIndex);
            if (evalIndex < 0)
            {
                continue;
            }

            ref var eval = ref branchEvals[evalIndex];
            var bit = (ushort)(1 << b);
            var branchWasSet = (oldBitmap & bit) != 0;

            var fieldWasIn = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
            var fieldIsIn = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

            if (fieldWasIn == fieldIsIn)
            {
                // No boundary crossing on this field for this branch — branch bit unchanged
                continue;
            }

            if (!fieldWasIn)
            {
                // OUT→IN: check all other fields in this branch to confirm the branch is now fully satisfied
                if (CheckOtherFieldsInBranch(pk, branchEvals, fieldIndex, tx))
                {
                    newBitmap |= bit;
                }
            }
            else
            {
                // IN→OUT: this field no longer passes, so the branch is definitely unsatisfied
                newBitmap &= (ushort)~bit;
            }
        }

        // Handle deletion: all branches off
        if (isDeletion)
        {
            newBitmap = 0;
        }

        var shouldBeInView = newBitmap != 0;

        // Update bitmap storage
        if (newBitmap != 0)
        {
            _branchBitmaps[pk] = newBitmap;
        }
        else if (oldBitmap != 0)
        {
            _branchBitmaps.Remove(pk);
        }

        // Track modified: entity stays in view but bitmap changed
        if (wasInView && shouldBeInView && oldBitmap != newBitmap)
        {
            CompactDelta(pk, DeltaKind.Modified);
        }
        else
        {
            ApplyDelta(pk, wasInView, shouldBeInView);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EvaluateKey(ref FieldEvaluator eval, ref KeyBytes8 key) => FieldEvaluator.Evaluate(ref eval, (byte*)Unsafe.AsPointer(ref key));

    private bool CheckOtherFieldsInBranch(long pk, FieldEvaluator[] branchEvals, int changedFieldIndex, Transaction tx)
    {
        if (branchEvals.Length == 1)
        {
            return true; // Only one field in this branch, and it already passed
        }

        if (!tx.ReadEntity<T>(pk, out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < branchEvals.Length; i++)
        {
            if (branchEvals[i].FieldIndex == changedFieldIndex)
            {
                continue;
            }
            ref var eval = ref branchEvals[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    private static int FindEvaluatorInBranch(FieldEvaluator[] branchEvals, int fieldIndex)
    {
        for (var i = 0; i < branchEvals.Length; i++)
        {
            if (branchEvals[i].FieldIndex == fieldIndex)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Evaluates all branches against a component, returning a bitmap of satisfied branches.</summary>
    private ushort EvaluateAllBranches(ref T comp)
    {
        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        ushort bitmap = 0;

        for (var b = 0; b < _branchEvaluators.Length; b++)
        {
            var evals = _branchEvaluators[b];
            var allPass = true;
            for (var i = 0; i < evals.Length; i++)
            {
                ref var eval = ref evals[i];
                if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
                {
                    allPass = false;
                    break;
                }
            }
            if (allPass)
            {
                bitmap |= (ushort)(1 << b);
            }
        }

        return bitmap;
    }

    private static FieldEvaluator[] FlattenEvaluators(FieldEvaluator[][] branchEvaluators)
    {
        var total = 0;
        for (var i = 0; i < branchEvaluators.Length; i++)
        {
            total += branchEvaluators[i].Length;
        }

        var result = new FieldEvaluator[total];
        var offset = 0;
        for (var i = 0; i < branchEvaluators.Length; i++)
        {
            Array.Copy(branchEvaluators[i], 0, result, offset, branchEvaluators[i].Length);
            offset += branchEvaluators[i].Length;
        }
        return result;
    }

    private static int[] BuildFieldDependencies(FieldEvaluator[][] branchEvaluators)
    {
        var fieldIndices = new HashSet<int>();
        for (var b = 0; b < branchEvaluators.Length; b++)
        {
            for (var i = 0; i < branchEvaluators[b].Length; i++)
            {
                fieldIndices.Add(branchEvaluators[b][i].FieldIndex);
            }
        }
        var deps = new int[fieldIndices.Count];
        fieldIndices.CopyTo(deps);
        Array.Sort(deps);
        return deps;
    }
}

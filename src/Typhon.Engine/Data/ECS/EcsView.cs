using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Reactive ECS View with incremental refresh via <see cref="ViewDeltaRingBuffer"/>.
/// Inherits <see cref="ViewBase"/> for entity set management, delta tracking, and ring buffer lifecycle.
/// When FieldEvaluators are present (Expression-based WHERE), registers with <see cref="ViewRegistry"/> for push-based delta notifications.
/// Otherwise, falls back to pull-model (full re-query on each Refresh).
/// </summary>
[PublicAPI]
public unsafe class EcsView<TArchetype> : ViewBase where TArchetype : class
{
    private EcsQuery<TArchetype> _query;
    private readonly ComponentTable _componentTable;
    private readonly ViewRegistry _registry;
    private readonly int[] _evaluatorLookup;

    // Typed delegate for reading component data + evaluating fields (captures component type T at construction)
    private readonly EcsViewFieldReader _fieldReader;

    // OR branch state (null for single-branch / pull mode)
    private readonly FieldEvaluator[][] _branchEvaluators;
    private readonly int[][] _branchEvalLookup;
    private readonly Dictionary<long, ushort> _branchBitmaps;
    private bool IsOrMode => _branchEvaluators != null;

    /// <summary>Incremental mode: created with FieldEvaluators from Expression-based WHERE.</summary>
    internal EcsView(EcsQuery<TArchetype> query, FieldEvaluator[] evaluators, ComponentTable componentTable,
        EcsViewFieldReader fieldReader, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(evaluators, BuildFieldDependencies(evaluators), componentTable.DBE.MemoryAllocator, componentTable, bufferCapacity, baseTSN)
    {
        _query = query;
        _componentTable = componentTable;
        _registry = componentTable.ViewRegistry;
        _evaluatorLookup = BuildEvaluatorLookup(evaluators);
        _fieldReader = fieldReader;
    }

    /// <summary>Incremental mode with cached execution plan.</summary>
    internal EcsView(EcsQuery<TArchetype> query, FieldEvaluator[] evaluators, ComponentTable componentTable,
        EcsViewFieldReader fieldReader, ExecutionPlan plan,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(evaluators, BuildFieldDependencies(evaluators), componentTable.DBE.MemoryAllocator, componentTable, [plan], bufferCapacity, baseTSN)
    {
        _query = query;
        _componentTable = componentTable;
        _registry = componentTable.ViewRegistry;
        _evaluatorLookup = BuildEvaluatorLookup(evaluators);
        _fieldReader = fieldReader;
    }

    /// <summary>OR mode: multiple branches with per-entity branch bitmaps.</summary>
    internal EcsView(EcsQuery<TArchetype> query, FieldEvaluator[][] branchEvaluators, ExecutionPlan[] plans, ComponentTable componentTable, 
        EcsViewFieldReader fieldReader, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0) :
        base(FlattenEvaluators(branchEvaluators), BuildFieldDependenciesMulti(branchEvaluators), componentTable.DBE.MemoryAllocator, 
            componentTable, plans, bufferCapacity, baseTSN)
    {
        if (branchEvaluators.Length > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(branchEvaluators),
                $"OR views support at most 16 branches (got {branchEvaluators.Length}). Branch bitmaps use ushort.");
        }

        _query = query;
        _componentTable = componentTable;
        _registry = componentTable.ViewRegistry;
        _fieldReader = fieldReader;
        _branchEvaluators = branchEvaluators;
        _branchEvalLookup = BuildBranchEvalLookup(branchEvaluators);
        _branchBitmaps = new Dictionary<long, ushort>();
    }

    /// <summary>Pull mode: created without FieldEvaluators (opaque WHERE or no WHERE).</summary>
    internal EcsView(EcsQuery<TArchetype> query, IMemoryAllocator allocator, IResource resourceParent, int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, 
        long baseTSN = 0) : base([], [], allocator, resourceParent, bufferCapacity, baseTSN)
    {
        _query = query;
    }

    protected override void DeregisterFromRegistries() => _registry?.DeregisterView(this);

    // ═══════════════════════════════════════════════════════════════════════
    // EntityId convenience API (converts from internal long representation)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Test if an entity is currently in the View.</summary>
    public bool Contains(EntityId id) => Contains((long)id.RawValue);

    // EntityId-based delta caches (rebuilt on each Refresh for backward compat)
    private readonly List<EntityId> _addedCache = new();
    private readonly List<EntityId> _removedCache = new();

    /// <summary>True if any entities were added or removed since the last Refresh.</summary>
    public bool HasChanges => _addedCache.Count > 0 || _removedCache.Count > 0;

    /// <summary>Entities that entered the View since the last Refresh.</summary>
    public IReadOnlyList<EntityId> Added => _addedCache;

    /// <summary>Entities that left the View since the last Refresh.</summary>
    public IReadOnlyList<EntityId> Removed => _removedCache;

    /// <summary>Build EntityId caches from ViewBase's internal delta dictionary.</summary>
    private void BuildEntityIdCaches()
    {
        var delta = GetDelta();
        foreach (var pk in delta.Added)
        {
            _addedCache.Add(EntityId.FromRaw(pk));
        }
        foreach (var pk in delta.Removed)
        {
            _removedCache.Add(EntityId.FromRaw(pk));
        }
    }

    /// <summary>Iterate EntityIds in the view.</summary>
    public EntityIdEnumerator GetEntityEnumerator() => new(GetEnumerator());

    /// <summary>Enumerator that wraps HashSet&lt;long&gt;.Enumerator and yields EntityId.</summary>
    [PublicAPI]
    public struct EntityIdEnumerator : IEnumerator<EntityId>
    {
        private HashSet<long>.Enumerator _inner;

        internal EntityIdEnumerator(HashSet<long>.Enumerator inner) => _inner = inner;

        public EntityId Current => EntityId.FromRaw(_inner.Current);
        object IEnumerator.Current => Current;
        public bool MoveNext() => _inner.MoveNext();
        public void Reset() { }
        public void Dispose() => _inner.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drain the ring buffer up to the transaction's snapshot TSN, evaluate field predicates,
    /// and update the entity set and delta tracking.
    /// Falls back to full re-query when no FieldEvaluators are present (pull mode).
    /// </summary>
    public override void Refresh(Transaction tx)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(EcsView<TArchetype>));
        }

        // Clear previous delta state
        ClearDelta();
        _addedCache.Clear();
        _removedCache.Clear();

        // Pull mode: no FieldEvaluators → full re-query every time
        if (_evaluators.Length == 0)
        {
            RefreshPull(tx);
            BuildEntityIdCaches();
            return;
        }

        // Incremental mode: drain ring buffer
        if (DeltaBuffer.HasOverflow)
        {
            SetOverflowDetected(true);
            if (IsOrMode) { RefreshFullOr(tx); } else { RefreshFull(tx); }
            BuildEntityIdCaches();
            return;
        }

        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out _))
        {
            DeltaBuffer.Advance();
            if (IsOrMode)
            {
                ProcessEntryOr(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, tx);
            }
            else
            {
                ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, tx);
            }
            SetLastRefreshTSN(tsn);
        }

        BuildEntityIdCaches();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pull mode (no FieldEvaluators — re-query + diff)
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshPull(Transaction tx)
    {
        _query.UpdateTransaction(tx);
        var newSet = _query.Execute();

        // Compute deltas: Added/Removed
        foreach (var id in newSet)
        {
            var pk = (long)id.RawValue;
            if (_entityIds.Add(pk))
            {
                CompactDelta(pk, DeltaKind.Added);
            }
        }

        // Check for removals
        var toRemove = new List<long>();
        foreach (var pk in _entityIds)
        {
            if (!newSet.Contains(EntityId.FromRaw(pk)))
            {
                toRemove.Add(pk);
            }
        }

        for (var i = 0; i < toRemove.Count; i++)
        {
            _entityIds.Remove(toRemove[i]);
            CompactDelta(toRemove[i], DeltaKind.Removed);
        }

        SetLastRefreshTSN(tx.TSN);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Incremental mode — delta processing (ported from View<T>)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntry(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        ref var eval = ref FindEvaluator(fieldIndex);
        if (Unsafe.IsNullRef(ref eval))
        {
            return;
        }

        // Check archetype mask: only process entities from matching archetypes
        var entityId = EntityId.FromRaw(entry.EntityPK);
        if (!_query.MaskTestPublic(entityId.ArchetypeId))
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
            if (shouldBeInView && _entityIds.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Modified);
            }
            return;
        }

        if (!wasInView)
        {
            // OUT→IN: verify all other fields pass
            if (_fieldReader != null && _fieldReader.CheckOtherFields(pk, _evaluators, fieldIndex, tx))
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

    // ═══════════════════════════════════════════════════════════════════════
    // Full refresh (overflow recovery — uses EcsQuery broad scan)
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshFull(Transaction tx)
    {
        var oldEntities = new HashSet<long>(_entityIds);

        DeltaBuffer.Reset(tx.TSN);
        _entityIds.Clear();

        if (HasCachedPlanInternal && _fieldReader != null)
        {
            // Use PipelineExecutor with cached plan for fast re-population
            _fieldReader.ExecuteFullScan(CachedPlan, CachedPlan.OrderedEvaluators, _componentTable, tx, _entityIds);
        }
        else if (_fieldReader != null)
        {
            // Fallback: broad scan via EcsQuery + per-entity field evaluation
            _query.UpdateTransaction(tx);
            foreach (var id in _query.Execute())
            {
                var pk = (long)id.RawValue;
                if (_fieldReader.EvaluateAllFields(pk, _evaluators, tx))
                {
                    _entityIds.Add(pk);
                }
            }
        }
        else
        {
            // Pull mode: just re-query
            _query.UpdateTransaction(tx);
            foreach (var id in _query.Execute())
            {
                _entityIds.Add((long)id.RawValue);
            }
        }

        DrainBufferAfterRefreshFull(tx.TSN);
        ComputeRefreshFullDeltas(oldEntities);

        SetOverflowDetected(false);
        SetLastRefreshTSN(tx.TSN);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OR mode — branch bitmap processing (ported from OrView<T>)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Populate initial entity set for OR mode — executes each branch plan and unions results with bitmap tracking.</summary>
    internal void PopulateInitialOr(Transaction tx)
    {
        var plans = CachedPlans;
        if (plans == null) return;

        for (var b = 0; b < plans.Length; b++)
        {
            var branchResult = new HashSet<long>();
            _fieldReader.ExecuteFullScan(plans[b], plans[b].OrderedEvaluators, _componentTable, tx, branchResult);
            var bit = (ushort)(1 << b);
            foreach (var pk in branchResult)
            {
                var entityId = EntityId.FromRaw(pk);
                if (!_query.MaskTestPublic(entityId.ArchetypeId)) continue;
                _entityIds.Add(pk);
                ref var bitmapRef = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_branchBitmaps, pk, out _);
                bitmapRef |= bit;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessEntryOr(ref ViewDeltaEntry entry, int fieldIndex, bool isCreation, bool isDeletion, Transaction tx)
    {
        var entityId = EntityId.FromRaw(entry.EntityPK);
        if (!_query.MaskTestPublic(entityId.ArchetypeId))
        {
            return;
        }

        var pk = entry.EntityPK;
        _branchBitmaps.TryGetValue(pk, out var oldBitmap);
        if (isCreation)
        {
            oldBitmap = 0;
        }

        var wasInView = oldBitmap != 0;
        var newBitmap = oldBitmap;

        for (var b = 0; b < _branchEvaluators.Length; b++)
        {
            var branchEvals = _branchEvaluators[b];
            var evalIndex = FindEvaluatorInBranch(b, fieldIndex);
            if (evalIndex < 0)
            {
                continue;
            }

            ref var eval = ref branchEvals[evalIndex];
            var bit = (ushort)(1 << b);

            var fieldWasIn = !isCreation && EvaluateKey(ref eval, ref entry.BeforeKey);
            var fieldIsIn = !isDeletion && EvaluateKey(ref eval, ref entry.AfterKey);

            if (fieldWasIn == fieldIsIn)
            {
                continue;
            }

            if (!fieldWasIn)
            {
                if (_fieldReader.CheckOtherFieldsInBranch(pk, branchEvals, fieldIndex, tx))
                {
                    newBitmap |= bit;
                }
            }
            else
            {
                newBitmap &= (ushort)~bit;
            }
        }

        if (isDeletion)
        {
            newBitmap = 0;
        }

        var shouldBeInView = newBitmap != 0;

        if (newBitmap != 0)
        {
            _branchBitmaps[pk] = newBitmap;
        }
        else if (oldBitmap != 0)
        {
            _branchBitmaps.Remove(pk);
        }

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
    private int FindEvaluatorInBranch(int branchIndex, int fieldIndex)
    {
        var lookup = _branchEvalLookup[branchIndex];
        return (uint)fieldIndex < (uint)lookup.Length ? lookup[fieldIndex] : -1;
    }

    private void RefreshFullOr(Transaction tx)
    {
        var oldEntities = new HashSet<long>(_entityIds);
        DeltaBuffer.Reset(tx.TSN);
        _entityIds.Clear();
        _branchBitmaps.Clear();

        var plans = CachedPlans;
        if (plans != null)
        {
            for (var b = 0; b < plans.Length; b++)
            {
                var branchResult = new HashSet<long>();
                _fieldReader.ExecuteFullScan(plans[b], plans[b].OrderedEvaluators, _componentTable, tx, branchResult);
                var bit = (ushort)(1 << b);
                foreach (var pk in branchResult)
                {
                    var eid = EntityId.FromRaw(pk);
                    if (!_query.MaskTestPublic(eid.ArchetypeId))
                    {
                        continue;
                    }

                    _entityIds.Add(pk);
                    ref var bitmapRef = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_branchBitmaps, pk, out _);
                    bitmapRef |= bit;
                }
            }
        }

        DrainBufferAfterRefreshFull(tx.TSN);
        ComputeRefreshFullDeltas(oldEntities);
        SetOverflowDetected(false);
        SetLastRefreshTSN(tx.TSN);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Evaluator lookup
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldEvaluator FindEvaluator(int fieldIndex)
    {
        if (_evaluatorLookup != null && (uint)fieldIndex < (uint)_evaluatorLookup.Length)
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
        if (evaluators.Length == 0)
        {
            return null;
        }

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
            return null;
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
        if (evaluators.Length == 0)
        {
            return [];
        }

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

    // ── Multi-branch helpers ──

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

    private static int[] BuildFieldDependenciesMulti(FieldEvaluator[][] branchEvaluators)
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

    private static int[][] BuildBranchEvalLookup(FieldEvaluator[][] branchEvaluators)
    {
        var result = new int[branchEvaluators.Length][];
        for (var b = 0; b < branchEvaluators.Length; b++)
        {
            var evals = branchEvaluators[b];
            var maxField = -1;
            for (var i = 0; i < evals.Length; i++)
            {
                if (evals[i].FieldIndex > maxField)
                {
                    maxField = evals[i].FieldIndex;
                }
            }

            if (maxField < 0) { result[b] = []; continue; }
            var lookup = new int[maxField + 1];
            Array.Fill(lookup, -1);
            for (var i = 0; i < evals.Length; i++)
            {
                lookup[evals[i].FieldIndex] = i;
            }

            result[b] = lookup;
        }
        return result;
    }
}

/// <summary>
/// Abstracts typed component reading for <see cref="EcsView{TArchetype}"/>.
/// Created by a generic factory that captures the component type T, allowing EcsView to remain parameterized only by TArchetype.
/// </summary>
internal abstract class EcsViewFieldReader
{
    public abstract bool CheckOtherFields(long pk, FieldEvaluator[] evaluators, int skipFieldIndex, Transaction tx);
    public abstract bool CheckOtherFieldsInBranch(long pk, FieldEvaluator[] branchEvals, int skipFieldIndex, Transaction tx);
    public abstract bool EvaluateAllFields(long pk, FieldEvaluator[] evaluators, Transaction tx);
    public abstract void ExecuteFullScan(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> result);
    public abstract void ExecuteOrderedScan(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result,
        int skip = 0, int take = int.MaxValue);
    public abstract int CountScan(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx);
}

/// <summary>
/// Typed implementation that reads component <typeparamref name="T"/> via <see cref="Transaction.Open"/> + <see cref="EntityRef.TryRead{T}"/>.
/// </summary>
internal sealed unsafe class EcsViewFieldReader<T> : EcsViewFieldReader where T : unmanaged
{
    public static readonly EcsViewFieldReader<T> Instance = new();

    public override bool CheckOtherFields(long pk, FieldEvaluator[] evaluators, int skipFieldIndex, Transaction tx)
    {
        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.TryRead<T>(out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex == skipFieldIndex)
            {
                continue;
            }
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    public override bool CheckOtherFieldsInBranch(long pk, FieldEvaluator[] branchEvals, int skipFieldIndex, Transaction tx)
    {
        if (branchEvals.Length == 1)
        {
            return true; // Only one field in this branch, and it already passed
        }

        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.TryRead<T>(out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < branchEvals.Length; i++)
        {
            if (branchEvals[i].FieldIndex == skipFieldIndex)
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

    public override bool EvaluateAllFields(long pk, FieldEvaluator[] evaluators, Transaction tx)
    {
        var entityId = EntityId.FromRaw(pk);
        var entity = tx.Open(entityId);
        if (!entity.TryRead<T>(out var comp))
        {
            return false;
        }

        var compPtr = (byte*)Unsafe.AsPointer(ref comp);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, compPtr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    public override void ExecuteFullScan(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> result) =>
        PipelineExecutor.Instance.Execute(plan, evaluators, table, tx, result);

    public override void ExecuteOrderedScan(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result,
        int skip = 0, int take = int.MaxValue) =>
        PipelineExecutor.Instance.ExecuteOrdered(plan, evaluators, table, tx, result, skip, take);

    public override int CountScan(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx) =>
        PipelineExecutor.Instance.Count(plan, evaluators, table, tx);
}

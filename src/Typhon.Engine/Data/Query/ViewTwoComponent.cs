using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

public unsafe class View<T1, T2> : IView, IDisposable, IEnumerable<long> where T1 : unmanaged where T2 : unmanaged
{
    private static int _nextViewId;

    private readonly HashSet<long> _entityIds = new();
    private readonly HashSet<long> _added = new();
    private readonly HashSet<long> _removed = new();
    private readonly HashSet<long> _modified = new();
    private readonly FieldEvaluator[] _evaluators;
    private readonly ViewRegistry _registry1;
    private readonly ViewRegistry _registry2;
    private readonly ComponentTable _componentTable1;
    private readonly ComponentTable _componentTable2;
    private long _lastRefreshTSN;
    private int _disposed;
    private bool _overflowDetected;

    internal View(FieldEvaluator[] evaluators, ViewRegistry registry1, ViewRegistry registry2, ComponentTable componentTable1, ComponentTable componentTable2,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0)
    {
        _evaluators = evaluators;
        _registry1 = registry1;
        _registry2 = registry2;
        _componentTable1 = componentTable1;
        _componentTable2 = componentTable2;
        ViewId = Interlocked.Increment(ref _nextViewId);
        DeltaBuffer = new ViewDeltaRingBuffer(bufferCapacity, baseTSN);

        // FieldDependencies is not used for multi-component views (explicit registration via overload)
        FieldDependencies = [];
    }

    public int ViewId { get; }
    public int[] FieldDependencies { get; }
    public bool IsDisposed => _disposed != 0;
    internal ViewDeltaRingBuffer DeltaBuffer { get; }
    ViewDeltaRingBuffer IView.DeltaBuffer => DeltaBuffer;
    public int Count => _entityIds.Count;
    public long LastRefreshTSN => _lastRefreshTSN;
    public bool HasOverflow => _overflowDetected;

    public bool Contains(long pk) => _entityIds.Contains(pk);

    internal void AddEntityDirect(long pk) => _entityIds.Add(pk);

    public void Refresh(Transaction tx)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(View<T1, T2>));
        }

        if (DeltaBuffer.HasOverflow)
        {
            RefreshFull(tx);
            return;
        }

        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out var componentTag))
        {
            DeltaBuffer.Advance();
            ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, componentTag, tx);
            _lastRefreshTSN = tsn;
        }
    }

    public ViewDelta GetDelta() => new(_added, _removed, _modified);

    public void ClearDelta()
    {
        _added.Clear();
        _removed.Clear();
        _modified.Clear();
    }

    public HashSet<long>.Enumerator GetEnumerator() => _entityIds.GetEnumerator();

    IEnumerator<long> IEnumerable<long>.GetEnumerator() => _entityIds.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entityIds.GetEnumerator();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registry1.DeregisterView(this);
        _registry2.DeregisterView(this);
        // Safety fence: allow in-flight producers to complete TryAppend before freeing buffer memory
        Thread.SpinWait(100);
        DeltaBuffer.Dispose();
        _entityIds.Clear();
        _added.Clear();
        _removed.Clear();
        _modified.Clear();
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

        // Reset buffer — clears overflow flag, allows new appends during re-scan
        DeltaBuffer.Reset();

        // Clear and rebuild entity set from PK index scan on T1
        _entityIds.Clear();
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

        // Drain concurrent entries that arrived during re-scan
        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn, out var componentTag))
        {
            DeltaBuffer.Advance();
            ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, componentTag, tx);
            _lastRefreshTSN = tsn;
        }

        // Compute delta: new-only → Added, old-only → Removed
        foreach (var pk in _entityIds)
        {
            if (!oldEntities.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Added);
            }
        }
        foreach (var pk in oldEntities)
        {
            if (!_entityIds.Contains(pk))
            {
                CompactDelta(pk, DeltaKind.Removed);
            }
        }

        _overflowDetected = false;
        _lastRefreshTSN = tx.TSN;
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

        if (!wasInView && shouldBeInView)
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
        // Cache component reads to avoid reading the same component twice
        byte* comp1Ptr = null;
        byte* comp2Ptr = null;
        T1 comp1 = default;
        T2 comp2 = default;
        var read1 = false;
        var read2 = false;

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
                    if (!tx.ReadEntity<T1>(pk, out comp1))
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
                    if (!tx.ReadEntity<T2>(pk, out comp2))
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
    private void ApplyDelta(long pk, bool wasInView, bool shouldBeInView)
    {
        if (!wasInView && shouldBeInView)
        {
            _entityIds.Add(pk);
            CompactDelta(pk, DeltaKind.Added);
        }
        else if (wasInView && !shouldBeInView)
        {
            _entityIds.Remove(pk);
            CompactDelta(pk, DeltaKind.Removed);
        }
        else if (wasInView && shouldBeInView)
        {
            CompactDelta(pk, DeltaKind.Modified);
        }
    }

    private void CompactDelta(long pk, DeltaKind newKind)
    {
        if (_added.Contains(pk))
        {
            switch (newKind)
            {
                case DeltaKind.Modified:
                    return; // Added + Modified → Added
                case DeltaKind.Removed:
                    _added.Remove(pk);
                    return; // Added + Removed → cancel
                default:
                    return; // Added + Added → Added
            }
        }

        if (_modified.Contains(pk))
        {
            switch (newKind)
            {
                case DeltaKind.Modified:
                    return; // Modified + Modified → Modified
                case DeltaKind.Removed:
                    _modified.Remove(pk);
                    _removed.Add(pk);
                    return; // Modified + Removed → Removed
                default:
                    return;
            }
        }

        if (_removed.Contains(pk))
        {
            if (newKind == DeltaKind.Added)
            {
                _removed.Remove(pk);
                _modified.Add(pk); // Removed + Added → Modified
            }
            return;
        }

        // No existing delta for this pk
        switch (newKind)
        {
            case DeltaKind.Added:
                _added.Add(pk);
                break;
            case DeltaKind.Removed:
                _removed.Add(pk);
                break;
            case DeltaKind.Modified:
                _modified.Add(pk);
                break;
        }
    }

    private ref FieldEvaluator FindEvaluator(int fieldIndex, byte componentTag)
    {
        for (var i = 0; i < _evaluators.Length; i++)
        {
            if (_evaluators[i].FieldIndex == fieldIndex && _evaluators[i].ComponentTag == componentTag)
            {
                return ref _evaluators[i];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }
}

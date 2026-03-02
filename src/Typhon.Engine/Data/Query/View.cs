using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

internal unsafe class View<T> : IView, IDisposable, IEnumerable<long> where T : unmanaged
{
    private static int _nextViewId;

    private readonly HashSet<long> _entityIds = new();
    private readonly HashSet<long> _added = new();
    private readonly HashSet<long> _removed = new();
    private readonly HashSet<long> _modified = new();
    private readonly FieldEvaluator[] _evaluators;
    private readonly ViewRegistry _registry;
    private long _lastRefreshTSN;
    private int _disposed;
    private bool _overflowDetected;

    public View(FieldEvaluator[] evaluators, ViewRegistry registry,
        int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0)
    {
        _evaluators = evaluators;
        _registry = registry;
        ViewId = Interlocked.Increment(ref _nextViewId);
        DeltaBuffer = new ViewDeltaRingBuffer(bufferCapacity, baseTSN);

        // Build FieldDependencies from evaluator FieldIndex values (distinct, sorted)
        var fieldIndices = new HashSet<int>();
        for (var i = 0; i < evaluators.Length; i++)
        {
            fieldIndices.Add(evaluators[i].FieldIndex);
        }
        FieldDependencies = [.. fieldIndices];
        Array.Sort(FieldDependencies);
    }

    public int ViewId { get; }
    public int[] FieldDependencies { get; }
    public bool IsDisposed => _disposed != 0;
    public ViewDeltaRingBuffer DeltaBuffer { get; }
    public int Count => _entityIds.Count;
    public long LastRefreshTSN => _lastRefreshTSN;
    public bool HasOverflow => _overflowDetected;

    public bool Contains(long pk) => _entityIds.Contains(pk);

    /// <summary>
    /// Drain the ring buffer up to the transaction's snapshot TSN, evaluate field predicates,
    /// and update the entity set and delta tracking sets.
    /// </summary>
    public void Refresh(Transaction tx)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(View<T>));
        }

        if (DeltaBuffer.HasOverflow)
        {
            _overflowDetected = true;
            return;
        }

        var targetTSN = tx.TSN;
        while (DeltaBuffer.TryPeek(targetTSN, out var entry, out var flags, out var tsn))
        {
            DeltaBuffer.Advance();
            ProcessEntry(ref entry, flags & 0x3F, (flags & 0x40) != 0, (flags & 0x80) != 0, tx);
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

        _registry.DeregisterView(this);
        DeltaBuffer.Dispose();
        _entityIds.Clear();
        _added.Clear();
        _removed.Clear();
        _modified.Clear();
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

        if (!wasInView && shouldBeInView)
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
        if (!tx.ReadEntity<T>(pk, out var comp))
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

    private ref FieldEvaluator FindEvaluator(int fieldIndex)
    {
        for (var i = 0; i < _evaluators.Length; i++)
        {
            if (_evaluators[i].FieldIndex == fieldIndex)
            {
                return ref _evaluators[i];
            }
        }
        return ref Unsafe.NullRef<FieldEvaluator>();
    }
}

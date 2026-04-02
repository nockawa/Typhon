using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thread-safe, lightweight snapshot accessor for parallel entity access.
/// Created once in the prepare phase and shared across all parallel workers.
/// Each worker thread gets its own <see cref="EntityAccessor"/> instance (lazily created on first use), providing per-thread component accessor caches and
/// epoch scopes with zero contention.
/// <para>
/// Supports reading all storage modes (Versioned via MVCC chain walk, SingleVersion/Transient direct).
/// Supports writing SingleVersion and Transient components. Throws on Versioned writes.
/// Does not support Spawn, Destroy, Commit, or Rollback.
/// </para>
/// <para>
/// <b>Long-lived reuse:</b> Call <see cref="AdvanceSnapshot"/> at each tick to update the MVCC snapshot without reallocating per-thread EntityAccessors.
/// ChunkAccessor page caches stay warm across ticks — zero allocation after the first tick's warmup.
/// </para>
/// </summary>
[PublicAPI]
public sealed class PointInTimeAccessor : IDisposable
{
    private readonly DatabaseEngine _dbe;
    private readonly ConcurrentDictionary<int, EntityAccessor> _threadAccessors = new();
    private bool _disposed;

    private PointInTimeAccessor(DatabaseEngine dbe, long tsn)
    {
        _dbe = dbe;
        TSN = tsn;
    }

    /// <summary>
    /// Creates a PointInTimeAccessor with a frozen MVCC snapshot at the current TSN.
    /// Thread-safe: multiple workers can call Open/OpenMut concurrently after creation.
    /// </summary>
    public static PointInTimeAccessor Create(DatabaseEngine dbe)
    {
        var tsn = dbe.TransactionChain.AllocateTSN();
        return new PointInTimeAccessor(dbe, tsn);
    }

    /// <summary>The frozen MVCC snapshot timestamp. All workers see the same snapshot.</summary>
    public long TSN { get; private set; }

    /// <summary>Read any storage mode (Versioned via MVCC chain walk, SV/Transient direct).</summary>
    public EntityRef Open(EntityId id) => GetThreadAccessor().Open(id);

    /// <summary>Write SV/Transient only. Throws for Versioned components at write time.</summary>
    public EntityRef OpenMut(EntityId id) => GetThreadAccessor().OpenMut(id);

    /// <summary>Try-pattern: returns false if entity not found or not visible.</summary>
    public bool TryOpen(EntityId id, out EntityRef entity) => GetThreadAccessor().TryOpen(id, out entity);

    /// <summary>
    /// Advance to a new MVCC snapshot for the next tick. Flushes pending dirty state from the previous tick and updates the TSN on all existing per-thread EntityAccessors.
    /// ChunkAccessor page caches are preserved — zero allocation, warm caches.
    /// <para>
    /// Must be called from a single thread (the prepare phase), NOT concurrently with Open/OpenMut/TryOpen calls from worker threads.
    /// </para>
    /// </summary>
    public void AdvanceSnapshot()
    {
        Debug.Assert(!_disposed, "PointInTimeAccessor used after disposal");
        var newTsn = _dbe.TransactionChain.AllocateTSN();
        TSN = newTsn;

        foreach (var acc in _threadAccessors.Values)
        {
            acc.ResetForNewSnapshot(newTsn);
        }
    }

    private EntityAccessor GetThreadAccessor()
    {
        Debug.Assert(!_disposed, "PointInTimeAccessor used after disposal");
        var threadId = Environment.CurrentManagedThreadId;
        if (_threadAccessors.TryGetValue(threadId, out var acc))
        {
            return acc;
        }

        acc = new EntityAccessor();
        acc.InitLightweight(_dbe, TSN);
        _threadAccessors[threadId] = acc;
        return acc;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var acc in _threadAccessors.Values)
        {
            acc.Dispose();
        }
        _threadAccessors.Clear();
    }
}

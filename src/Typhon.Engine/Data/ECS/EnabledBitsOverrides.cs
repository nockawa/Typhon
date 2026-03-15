using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Engine-level MVCC override dictionary for EnabledBits.
/// Fast path: when <see cref="_overrideCount"/> == 0, the inline EntityRecord.EnabledBits is correct (zero overhead).
/// Slow path: lookup EntityKey in the dictionary, resolve at transaction TSN.
/// </summary>
internal class EnabledBitsOverrides
{
    private readonly ConcurrentDictionary<long, EnabledBitsHistory> _overrides = new();

    /// <summary>Number of entities with active overrides. Zero = fast path (no dictionary lookup needed).</summary>
    internal volatile int _overrideCount;

    /// <summary>
    /// Resolve the EnabledBits for an entity at the given transaction TSN.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ResolveEnabledBits(long entityKey, ushort inlineBits, long txTsn)
    {
        // Fast path: no overrides exist anywhere → use inline bits
        if (_overrideCount == 0)
        {
            return inlineBits;
        }

        // Check if this specific entity has overrides
        if (!_overrides.TryGetValue(entityKey, out var history))
        {
            return inlineBits;
        }

        return history.ResolveAt(txTsn, inlineBits);
    }

    /// <summary>
    /// Record an EnabledBits change for MVCC. Called at commit time when older transactions exist.
    /// </summary>
    public void Record(long entityKey, long changeTSN, ushort oldBits)
    {
        var history = _overrides.GetOrAdd(entityKey, _ =>
        {
            Interlocked.Increment(ref _overrideCount);
            return new EnabledBitsHistory();
        });
        history.Record(changeTSN, oldBits);
    }

    /// <summary>
    /// Prune all entries whose changeTSN is at or below minTSN. Called when MinTSN advances.
    /// </summary>
    public void Prune(long minTSN)
    {
        if (_overrideCount == 0)
        {
            return;
        }

        var toRemove = new List<long>();
        foreach (var kvp in _overrides)
        {
            if (kvp.Value.TryPrune(minTSN))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            if (_overrides.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _overrideCount);
            }
        }
    }
}

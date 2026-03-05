// unset

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Selectivity estimator using uniform distribution assumption for range predicates and exact B+Tree point lookups for equality predicates.
/// </summary>
internal class BasicSelectivityEstimator : ISelectivityEstimator
{
    public static readonly BasicSelectivityEstimator Instance = new();

    private BasicSelectivityEstimator() { }

    public long EstimateCardinality(ComponentTable table, int fieldIndex, CompareOp op, long threshold)
    {
        var stats = table.IndexStats[fieldIndex];
        var entryCount = stats.EntryCount;
        if (entryCount == 0)
        {
            return 0;
        }

        var index = stats.Index;
        long min = stats.MinValue;
        long max = stats.MaxValue;

        // For range estimation we need the total entity count, not just distinct keys.
        // For unique indexes EntryCount == entity count. For AllowMultiple, use the histogram's
        // TotalCount if available (accurate from last rebuild), otherwise fall back to EntryCount
        // (distinct keys — underestimates, but better than nothing).
        var histogram = stats.Histogram;
        int total = (index.AllowMultiple && histogram != null) ? histogram.TotalCount : entryCount;

        switch (op)
        {
            case CompareOp.Equal:               return ExactEqualityCount(index, threshold);
            case CompareOp.NotEqual:            return Math.Max(0, total - ExactEqualityCount(index, threshold));
            case CompareOp.GreaterThan:         return EstimateUniformRange(total, min, max, threshold + 1, max);
            case CompareOp.GreaterThanOrEqual:  return EstimateUniformRange(total, min, max, threshold, max);
            case CompareOp.LessThan:            return EstimateUniformRange(total, min, max, min, threshold - 1);
            case CompareOp.LessThanOrEqual:     return EstimateUniformRange(total, min, max, min, threshold);
            default:                            throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
    }

    /// <summary>
    /// Returns the exact count of entries matching <paramref name="key"/> via B+Tree point lookup.
    /// For unique indexes: 0 or 1. For multi-value indexes: the buffer's TotalCount.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ExactEqualityCount(BTreeBase index, long key)
    {
        using var guard = EpochGuard.Enter(index.Segment.Manager.EpochManager);
        var accessor = index.Segment.CreateChunkAccessor();
        try
        {
            // stackalloc 8 bytes — on little-endian x64 the lower bytes contain the correct
            // key value for all key sizes (BTree reads only sizeof(TKey) bytes)
            var buf = stackalloc byte[8];
            *(long*)buf = key;

            if (!index.AllowMultiple)
            {
                var result = index.TryGet(buf, ref accessor);
                return result.IsSuccess ? 1 : 0;
            }

            // Multi-value index: TryGetMultiple returns all values for the key
            var multiResult = index.TryGetMultiple(buf, ref accessor);
            if (!multiResult.IsValid)
            {
                return 0;
            }

            var count = multiResult.TotalCount;
            multiResult.Dispose();
            return count;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Estimates cardinality assuming uniform distribution in [min, max].
    /// Degenerate case (min == max): returns total if the single value is in [lo, hi], else 0.
    /// </summary>
    private static long EstimateUniformRange(int total, long min, long max, long lo, long hi)
    {
        if (lo > hi || lo > max || hi < min)
        {
            return 0;
        }

        if (min == max)
        {
            return (lo <= min && min <= hi) ? total : 0;
        }

        // Clamp to actual range
        lo = Math.Max(lo, min);
        hi = Math.Min(hi, max);

        long fullRange = max - min;
        long queryRange = hi - lo;

        return Math.Max(0, (long)total * queryRange / fullRange);
    }
}

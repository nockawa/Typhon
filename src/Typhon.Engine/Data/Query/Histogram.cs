// unset

using System;

namespace Typhon.Engine;

/// <summary>
/// Equi-width histogram for selectivity estimation. Each histogram has <see cref="BucketCount"/> buckets spanning [<see cref="MinValue"/>,
/// <see cref="MaxValue"/>]. Bucket lookup is O(1).
/// </summary>
/// <remarks>
/// Memory footprint: ~1.6 KB per indexed field (100 × 4-byte ints + metadata).
/// Built during <see cref="IndexStatistics.RebuildHistogram"/> by scanning all leaf entries.
/// </remarks>
internal class Histogram
{
    internal const int BucketCount = 100;

    public long MinValue { get; }
    public long MaxValue { get; }
    public int TotalCount { get; }
    internal int[] BucketCounts { get; }

    /// <summary>Width of each bucket. Zero when all keys have the same value (degenerate case).</summary>
    internal long BucketWidth { get; }

    public Histogram(long min, long max, int[] bucketCounts, int totalCount)
    {
        MinValue = min;
        MaxValue = max;
        BucketCounts = bucketCounts;
        TotalCount = totalCount;
        BucketWidth = (max == min) ? 0 : Math.Max(1, (max - min) / BucketCount);
    }

    /// <summary>Returns the 0-based bucket index for <paramref name="value"/>, clamped to [0, BucketCount-1].</summary>
    public int GetBucket(long value)
    {
        if (BucketWidth == 0)
        {
            return 0;
        }

        var offset = value - MinValue;
        var bucket = offset / BucketWidth;
        if (bucket < 0)
        {
            return 0;
        }
        if (bucket >= BucketCount)
        {
            return BucketCount - 1;
        }

        return (int)bucket;
    }

    /// <summary>
    /// Estimates the number of entries in [<paramref name="lo"/>, <paramref name="hi"/>] using linear interpolation
    /// for boundary buckets and full counts for interior buckets.
    /// </summary>
    public long EstimateRange(long lo, long hi)
    {
        if (TotalCount == 0 || lo > hi)
        {
            return 0;
        }

        // Degenerate: all keys have the same value
        if (BucketWidth == 0)
        {
            return (lo <= MinValue && MinValue <= hi) ? TotalCount : 0;
        }

        var loBucket = GetBucket(lo);
        var hiBucket = GetBucket(hi);

        if (loBucket == hiBucket)
        {
            // Both endpoints fall in the same bucket — interpolate the fraction
            var bucketLo = MinValue + loBucket * BucketWidth;
            var bucketHi = bucketLo + BucketWidth;
            var rangeInBucket = Math.Min(hi, bucketHi) - Math.Max(lo, bucketLo);
            return Math.Max(1, BucketCounts[loBucket] * rangeInBucket / BucketWidth);
        }

        long estimate = 0;

        // Partial low bucket
        {
            var bucketLo = MinValue + loBucket * BucketWidth;
            var bucketHi = bucketLo + BucketWidth;
            var overlap = bucketHi - Math.Max(lo, bucketLo);
            estimate += BucketCounts[loBucket] * overlap / BucketWidth;
        }

        // Full interior buckets
        for (var i = loBucket + 1; i < hiBucket; i++)
        {
            estimate += BucketCounts[i];
        }

        // Partial high bucket
        {
            var bucketLo = MinValue + hiBucket * BucketWidth;
            var overlap = Math.Min(hi, bucketLo + BucketWidth) - bucketLo;
            estimate += BucketCounts[hiBucket] * overlap / BucketWidth;
        }

        return Math.Max(estimate, 0);
    }

    /// <summary>
    /// Estimates the number of entries equal to <paramref name="value"/>. Returns the average count per
    /// distinct key in the bucket (BucketCount[i] / BucketWidth), floored to 1 for non-empty buckets.
    /// </summary>
    public long EstimateEquality(long value)
    {
        if (TotalCount == 0)
        {
            return 0;
        }

        // Degenerate: all keys are the same value
        if (BucketWidth == 0)
        {
            return (value == MinValue) ? TotalCount : 0;
        }

        var bucket = GetBucket(value);
        var count = BucketCounts[bucket];
        if (count == 0)
        {
            return 0;
        }

        var estimate = count / BucketWidth;
        return Math.Max(1, estimate);
    }
}

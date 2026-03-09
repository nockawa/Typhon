// unset

using System;

namespace Typhon.Engine;

/// <summary>
/// Selectivity estimator that uses per-index histograms for more accurate cardinality estimation.
/// Falls back to <see cref="BasicSelectivityEstimator"/> when no histogram is available.
/// </summary>
internal class HistogramSelectivityEstimator : ISelectivityEstimator
{
    public static readonly HistogramSelectivityEstimator Instance = new();

    private HistogramSelectivityEstimator() { }

    public long EstimateCardinality(ComponentTable table, int fieldIndex, CompareOp op, long threshold)
    {
        var stats = table.IndexStats[fieldIndex];
        var histogram = stats.Histogram;

        // No histogram available — delegate to basic uniform estimator
        if (histogram == null)
        {
            return BasicSelectivityEstimator.Instance.EstimateCardinality(table, fieldIndex, op, threshold);
        }

        switch (op)
        {
            case CompareOp.Equal:
                return histogram.EstimateEquality(threshold);

            case CompareOp.NotEqual:
                return Math.Max(0, histogram.TotalCount - histogram.EstimateEquality(threshold));

            case CompareOp.GreaterThan:
            {
                // Guard against overflow: threshold + 1 could wrap for long.MaxValue
                if (threshold >= histogram.MaxValue)
                {
                    return 0;
                }
                return histogram.EstimateRange(threshold + 1, histogram.MaxValue);
            }

            case CompareOp.GreaterThanOrEqual:
                return histogram.EstimateRange(threshold, histogram.MaxValue);

            case CompareOp.LessThan:
            {
                // Guard against underflow: threshold - 1 could wrap for long.MinValue
                if (threshold <= histogram.MinValue)
                {
                    return 0;
                }
                return histogram.EstimateRange(histogram.MinValue, threshold - 1);
            }

            case CompareOp.LessThanOrEqual:
                return histogram.EstimateRange(histogram.MinValue, threshold);

            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
    }
}

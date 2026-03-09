using System;

namespace Typhon.Engine;

/// <summary>
/// Builds an <see cref="ExecutionPlan"/> from evaluators and index statistics.
/// Selects the most selective unique secondary index as the primary scan stream when possible, falling back to a full PK index scan otherwise.
/// </summary>
internal class PlanBuilder
{
    public static readonly PlanBuilder Instance = new();

    private PlanBuilder() { }

    /// <summary>
    /// Builds a selectivity-ordered plan. Evaluators are reordered by ascending estimated cardinality so the most selective predicate is evaluated first
    /// (short-circuit optimization). Attempts to select a unique secondary index as the primary scan stream.
    /// </summary>
    public ExecutionPlan BuildPlan(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator)
    {
        var (ordered, estimates) = OrderBySelectivity(evaluators, table, estimator);
        return BuildPlanWithPrimarySelection(ordered, estimates, table, false);
    }

    /// <summary>
    /// Builds a plan with OrderBy support. Sets the iteration direction based on <paramref name="orderBy"/>.
    /// Secondary index selection is only used when OrderBy is by the same field as the primary predicate, or when OrderBy is by PK (falls back to PK scan).
    /// </summary>
    public ExecutionPlan BuildPlan(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator, OrderByField orderBy)
    {
        var (ordered, estimates) = OrderBySelectivity(evaluators, table, estimator);

        // Only use secondary index as primary if OrderBy matches the candidate primary field
        var plan = BuildPlanWithPrimarySelection(ordered, estimates, table, orderBy.Descending, orderBy.FieldIndex);
        return plan;
    }

    private static ExecutionPlan BuildPlanWithPrimarySelection(FieldEvaluator[] orderedEvaluators, long[] estimates, ComponentTable table, bool descending, 
        int orderByFieldIndex = int.MinValue)
    {
        // Try to find a secondary index for the primary stream
        var (primaryFieldIndex, primaryKeyType, scanMin, scanMax) = SelectPrimaryStream(orderedEvaluators, table, orderByFieldIndex);

        if (primaryFieldIndex < 0)
        {
            // Fall back to PK scan
            var pkIndex = table.PrimaryKeyIndex;
            scanMin = pkIndex.EntryCount > 0 ? pkIndex.GetMinKeyAsLong() : long.MinValue;
            scanMax = pkIndex.EntryCount > 0 ? pkIndex.GetMaxKeyAsLong() : long.MaxValue;
        }

        return new ExecutionPlan(primaryFieldIndex, primaryKeyType, scanMin, scanMax, descending, orderedEvaluators, estimates);
    }

    /// <summary>
    /// Selects the most selective unique secondary index as the primary scan stream.
    /// Only considers unique indexes (AllowMultiple = false) and operators that can narrow a range (not NE).
    /// </summary>
    /// <param name="orderedEvaluators">Evaluators sorted by ascending selectivity.</param>
    /// <param name="table">Component table with index metadata.</param>
    /// <param name="orderByFieldIndex">
    /// When set (not int.MinValue), only select a secondary index if it matches this field index.
    /// Prevents using a secondary index when OrderBy requires a different iteration order.
    /// int.MinValue = no OrderBy constraint, -1 = OrderBy PK (forces PK scan).
    /// </param>
    private static (int FieldIndex, KeyType KeyType, long ScanMin, long ScanMax) SelectPrimaryStream(FieldEvaluator[] orderedEvaluators, ComponentTable table,
        int orderByFieldIndex)
    {
        // OrderBy PK → must use PK scan
        if (orderByFieldIndex == -1)
        {
            return (-1, default, 0, 0);
        }

        var indexedFieldInfos = table.IndexedFieldInfos;

        for (var i = 0; i < orderedEvaluators.Length; i++)
        {
            ref var eval = ref orderedEvaluators[i];

            // NE cannot narrow a range
            if (eval.CompareOp == CompareOp.NotEqual)
            {
                continue;
            }

            // Must reference a valid indexed field
            if (eval.FieldIndex < 0 || eval.FieldIndex >= indexedFieldInfos.Length)
            {
                continue;
            }

            ref var ifi = ref indexedFieldInfos[eval.FieldIndex];

            // Only unique indexes for primary stream (AllowMultiple requires VSBS buffer expansion)
            if (ifi.Index.AllowMultiple)
            {
                continue;
            }

            // If OrderBy is specified, only select this field if it matches
            if (orderByFieldIndex != int.MinValue && orderByFieldIndex != eval.FieldIndex)
            {
                continue;
            }

            // Empty index → no benefit
            if (ifi.Index.EntryCount == 0)
            {
                continue;
            }

            var minKey = ifi.Index.GetMinKeyAsLong();
            var maxKey = ifi.Index.GetMaxKeyAsLong();

            // Compute narrowed scan range from operator
            var (scanMin, scanMax) = eval.CompareOp switch
            {
                CompareOp.Equal => (eval.Threshold, eval.Threshold),
                CompareOp.GreaterThan => (eval.Threshold, maxKey),
                CompareOp.GreaterThanOrEqual => (eval.Threshold, maxKey),
                CompareOp.LessThan => (minKey, eval.Threshold),
                CompareOp.LessThanOrEqual => (minKey, eval.Threshold),
                _ => (minKey, maxKey)
            };

            return (eval.FieldIndex, eval.KeyType, scanMin, scanMax);
        }

        return (-1, default, 0, 0);
    }

    private static (FieldEvaluator[] Ordered, long[] Estimates) OrderBySelectivity(FieldEvaluator[] evaluators, ComponentTable table, ISelectivityEstimator estimator)
    {
        if (evaluators.Length == 0)
        {
            return ([], []);
        }

        // Estimate cardinality for each predicate
        var estimates = new long[evaluators.Length];
        var indices = new int[evaluators.Length];
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            estimates[i] = estimator.EstimateCardinality(table, eval.FieldIndex, eval.CompareOp, eval.Threshold);
            indices[i] = i;
        }

        // Sort by ascending cardinality, tie-break by lower FieldIndex
        Array.Sort(indices, (a, b) =>
        {
            var cmp = estimates[a].CompareTo(estimates[b]);
            return cmp != 0 ? cmp : evaluators[a].FieldIndex.CompareTo(evaluators[b].FieldIndex);
        });

        // Build ordered arrays
        var ordered = new FieldEvaluator[evaluators.Length];
        var orderedEstimates = new long[evaluators.Length];
        for (var i = 0; i < evaluators.Length; i++)
        {
            ordered[i] = evaluators[indices[i]];
            orderedEstimates[i] = estimates[indices[i]];
        }

        return (ordered, orderedEstimates);
    }
}

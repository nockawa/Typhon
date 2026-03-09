using System;
using System.Runtime.CompilerServices;

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
            // Fall back to PK scan — use full long range so the plan remains valid
            // when reused after new entities are inserted (e.g., overflow recovery).
            scanMin = long.MinValue;
            scanMax = long.MaxValue;
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

            // Use type-appropriate max/min for unbounded ranges so the plan remains valid when reused after new keys are inserted (e.g., overflow recovery).
            // long.MaxValue/MinValue cannot be used because LongToKey truncates to the target type (e.g., (int)long.MaxValue = -1), creating invalid scan ranges.
            var typeMin = TypeMinAsLong(eval.KeyType);
            var typeMax = TypeMaxAsLong(eval.KeyType);
            var (scanMin, scanMax) = eval.CompareOp switch
            {
                CompareOp.Equal => (eval.Threshold, eval.Threshold),
                CompareOp.GreaterThan => (eval.Threshold, typeMax),
                CompareOp.GreaterThanOrEqual => (eval.Threshold, typeMax),
                CompareOp.LessThan => (typeMin, eval.Threshold),
                CompareOp.LessThanOrEqual => (typeMin, eval.Threshold),
                _ => (typeMin, typeMax)
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

    private static long TypeMaxAsLong(KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool: return 1L;
            case KeyType.SByte: return sbyte.MaxValue;
            case KeyType.Byte: return (long)(ulong)byte.MaxValue;
            case KeyType.Short: return short.MaxValue;
            case KeyType.UShort: return (long)(ulong)ushort.MaxValue;
            case KeyType.Int: return int.MaxValue;
            case KeyType.UInt: return (long)(ulong)uint.MaxValue;
            case KeyType.Long: return long.MaxValue;
            case KeyType.ULong: return unchecked((long)ulong.MaxValue);
            case KeyType.Float:
            {
                var f = float.MaxValue;
                return (long)Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = double.MaxValue;
                return Unsafe.As<double, long>(ref d);
            }
            default: return long.MaxValue;
        }
    }

    private static long TypeMinAsLong(KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool: return 0L;
            case KeyType.SByte: return sbyte.MinValue;
            case KeyType.Byte: return 0L;
            case KeyType.Short: return short.MinValue;
            case KeyType.UShort: return 0L;
            case KeyType.Int: return int.MinValue;
            case KeyType.UInt: return 0L;
            case KeyType.Long: return long.MinValue;
            case KeyType.ULong: return 0L;
            case KeyType.Float:
            {
                var f = float.MinValue;
                return (long)Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = double.MinValue;
                return Unsafe.As<double, long>(ref d);
            }
            default: return long.MinValue;
        }
    }
}

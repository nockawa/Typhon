using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> by streaming entity PKs from either a secondary index (when <see cref="ExecutionPlan.UsesSecondaryIndex"/>) or
/// the PK index, batching them (256 at a time for L1 cache efficiency), and evaluating predicates via component reads.
/// Predicates are ordered by ascending selectivity for short-circuit efficiency.
/// </summary>
internal class PipelineExecutor
{
    private const int BatchSize = 256;

    public static readonly PipelineExecutor Instance = new();

    private PipelineExecutor() { }

    /// <summary>
    /// Executes the plan and returns matching entity PKs as a <see cref="HashSet{T}"/> (unordered).
    /// Used by <see cref="View{T}"/> for initial population and overflow recovery.
    /// </summary>
    /// <param name="plan">The execution plan built by <see cref="PlanBuilder"/>.</param>
    /// <param name="evaluators">All field evaluators, ordered by ascending selectivity (most selective first).</param>
    /// <param name="table">The component table to read entities from.</param>
    /// <param name="tx">Transaction for MVCC-consistent reads.</param>
    public HashSet<long> Execute<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx) where T : unmanaged
    {
        var result = new HashSet<long>();
        ExecuteCore<T>(plan, evaluators, table, tx, result, null, 0, int.MaxValue);
        return result;
    }

    /// <summary>
    /// Executes the plan and returns matching entity PKs as a <see cref="List{T}"/> preserving iteration order.
    /// Supports Skip/Take with early termination.
    /// </summary>
    public List<long> ExecuteOrdered<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, int skip = 0, 
        int take = int.MaxValue) where T : unmanaged
    {
        var result = new List<long>();
        ExecuteCore<T>(plan, evaluators, table, tx, null, result, skip, take);
        return result;
    }

    private void ExecuteCore<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> unorderedResult, 
        List<long> orderedResult, int skip, int take) where T : unmanaged
    {
        if (take == 0)
        {
            return;
        }

        if (plan.UsesSecondaryIndex)
        {
            ExecuteCoreSecondaryIndex<T>(plan, evaluators, table, tx, unorderedResult, orderedResult, skip, take);
            return;
        }

        // PK scan path — scan the PK index and evaluate all predicates per entity
        var pkIndex = table.PrimaryKeyIndex;
        var hasFilters = evaluators.Length > 0;

        var collected = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;

        var enumerator = plan.Descending ? pkIndex.EnumerateRangeDescending(plan.PrimaryScanMin, plan.PrimaryScanMax) : 
            pkIndex.EnumerateRange(plan.PrimaryScanMin, plan.PrimaryScanMax);

        foreach (var kv in enumerator)
        {
            batch[batchCount++] = kv.Key; // PK index: key = entity PK
            if (batchCount < BatchSize)
            {
                continue;
            }

            if (ProcessBatch<T>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected))
            {
                return;
            }
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            ProcessBatch<T>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected);
        }
    }

    /// <summary>
    /// Secondary index scan path: scans a unique secondary index for matching key values, recovers entity PKs via <see cref="CompRevStorageHeader.EntityPK"/>,
    /// then evaluates remaining predicates via component reads.
    /// </summary>
    private void ExecuteCoreSecondaryIndex<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, 
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T : unmanaged
    {
        // Step 1: Collect entity PKs from secondary index range scan
        var pks = CollectPKsFromSecondaryIndex(plan, table);

        // Step 2: Process collected PKs in batches with filter evaluation
        var hasFilters = evaluators.Length > 0;
        var collected = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;

        for (var i = 0; i < pks.Count; i++)
        {
            batch[batchCount++] = pks[i];
            if (batchCount < BatchSize)
            {
                continue;
            }

            if (ProcessBatch<T>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected))
            {
                return;
            }
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            ProcessBatch<T>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected);
        }
    }

    /// <summary>
    /// Scans the secondary index specified by the plan, resolves each entry's compRevFirstChunkId to an entity PK via <see cref="CompRevStorageHeader.EntityPK"/>.
    /// </summary>
    private static List<long> CollectPKsFromSecondaryIndex(ExecutionPlan plan, ComponentTable table)
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];
        return plan.PrimaryKeyType switch
        {
            KeyType.Byte => CollectPKsTyped((BTree<byte>)ifi.Index, plan, table),
            KeyType.SByte => CollectPKsTyped((BTree<sbyte>)ifi.Index, plan, table),
            KeyType.Short => CollectPKsTyped((BTree<short>)ifi.Index, plan, table),
            KeyType.UShort => CollectPKsTyped((BTree<ushort>)ifi.Index, plan, table),
            KeyType.Int => CollectPKsTyped((BTree<int>)ifi.Index, plan, table),
            KeyType.UInt => CollectPKsTyped((BTree<uint>)ifi.Index, plan, table),
            KeyType.Long => CollectPKsTyped((BTree<long>)ifi.Index, plan, table),
            KeyType.ULong => CollectPKsTyped((BTree<ulong>)ifi.Index, plan, table),
            KeyType.Float => CollectPKsTyped((BTree<float>)ifi.Index, plan, table),
            KeyType.Double => CollectPKsTyped((BTree<double>)ifi.Index, plan, table),
            _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
        };
    }

    private static List<long> CollectPKsTyped<TKey>(BTree<TKey> index, ExecutionPlan plan, ComponentTable table) where TKey : unmanaged
    {
        var minKey = BTree<TKey>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey>.LongToKey(plan.PrimaryScanMax);

        var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);

        var result = new List<long>();
        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor(null);
        try
        {
            foreach (var kv in enumerator)
            {
                // kv.Value = compRevFirstChunkId (revision chain start)
                // Read CompRevStorageHeader to recover entity PK
                ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(kv.Value);
                result.Add(header.EntityPK);
            }
        }
        finally
        {
            compRevAccessor.Dispose();
        }

        return result;
    }

    private static unsafe bool ProcessBatch<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, bool hasFilters, HashSet<long> unorderedResult, 
        List<long> orderedResult, ref int skip, ref int take, ref int collected) where T : unmanaged
    {
        for (var i = 0; i < batch.Length; i++)
        {
            var pk = batch[i];

            if (hasFilters && !EvaluateFilters<T>(evaluators, tx, pk))
            {
                continue;
            }

            if (skip > 0)
            {
                skip--;
                continue;
            }

            if (unorderedResult != null)
            {
                unorderedResult.Add(pk);
            }
            else
            {
                orderedResult.Add(pk);
            }
            collected++;
            if (collected >= take)
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool EvaluateFilters<T>(FieldEvaluator[] evaluators, Transaction tx, long pk) where T : unmanaged
    {
        if (!tx.ReadEntity<T>(pk, out var comp))
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
}

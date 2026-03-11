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
    /// Executes the plan and adds matching entity PKs to the caller-provided <paramref name="result"/> set (unordered).
    /// The caller owns the collection lifecycle — one-shot queries pass a fresh instance, Views reuse and clear theirs.
    /// </summary>
    /// <param name="plan">The execution plan built by <see cref="PlanBuilder"/>.</param>
    /// <param name="evaluators">All field evaluators, ordered by ascending selectivity (most selective first).</param>
    /// <param name="table">The component table to read entities from.</param>
    /// <param name="tx">Transaction for MVCC-consistent reads.</param>
    /// <param name="result">Caller-provided set to populate. Must be empty (or pre-cleared by caller).</param>
    public void Execute<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> result) where T : unmanaged 
        => ExecuteCore<T>(plan, evaluators, table, tx, result, null, 0, int.MaxValue);

    /// <summary>
    /// Executes the plan and adds matching entity PKs to the caller-provided <paramref name="result"/> list preserving iteration order.
    /// Supports Skip/Take with early termination.
    /// </summary>
    public void ExecuteOrdered<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result, int skip = 0,
        int take = int.MaxValue) where T : unmanaged => ExecuteCore<T>(plan, evaluators, table, tx, null, result, skip, take);

    /// <summary>
    /// Counts matching entities without allocating a result collection. Runs the same scan + filter pipeline but only increments a counter.
    /// </summary>
    public int Count<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx) where T : unmanaged
    {
        if (plan.UsesSecondaryIndex)
        {
            return CountCoreSecondaryIndex<T>(plan, evaluators, table, tx);
        }

        var pkIndex = table.PrimaryKeyIndex;
        var hasFilters = evaluators.Length > 0;
        var count = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;

        var enumerator = plan.Descending ? pkIndex.EnumerateRangeDescending(plan.PrimaryScanMin, plan.PrimaryScanMax) :
            pkIndex.EnumerateRange(plan.PrimaryScanMin, plan.PrimaryScanMax);

        foreach (var kv in enumerator)
        {
            batch[batchCount++] = kv.Key;
            if (batchCount < BatchSize)
            {
                continue;
            }

            count += CountBatch<T>(batch[..batchCount], evaluators, tx, hasFilters);
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            count += CountBatch<T>(batch[..batchCount], evaluators, tx, hasFilters);
        }

        return count;
    }

    /// <summary>
    /// Streams PKs from the secondary index directly into batch counting, avoiding the intermediate <see cref="List{T}"/> allocation
    /// that <see cref="CollectPKsFromSecondaryIndex"/> would produce.
    /// </summary>
    private int CountCoreSecondaryIndex<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx) where T : unmanaged
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];
        return plan.PrimaryKeyType switch
        {
            KeyType.Byte => CountPKsTyped<T, byte>((BTree<byte>)ifi.Index, plan, table, evaluators, tx),
            KeyType.SByte => CountPKsTyped<T, sbyte>((BTree<sbyte>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Short => CountPKsTyped<T, short>((BTree<short>)ifi.Index, plan, table, evaluators, tx),
            KeyType.UShort => CountPKsTyped<T, ushort>((BTree<ushort>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Int => CountPKsTyped<T, int>((BTree<int>)ifi.Index, plan, table, evaluators, tx),
            KeyType.UInt => CountPKsTyped<T, uint>((BTree<uint>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Long => CountPKsTyped<T, long>((BTree<long>)ifi.Index, plan, table, evaluators, tx),
            KeyType.ULong => CountPKsTyped<T, long>((BTree<long>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Float => CountPKsTyped<T, float>((BTree<float>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Double => CountPKsTyped<T, double>((BTree<double>)ifi.Index, plan, table, evaluators, tx),
            _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
        };
    }

    private static int CountPKsTyped<T, TKey>(BTree<TKey> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators, Transaction tx)
        where T : unmanaged where TKey : unmanaged
    {
        var minKey = BTree<TKey>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey>.LongToKey(plan.PrimaryScanMax);

        var hasFilters = evaluators.Length > 0;
        var count = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;
        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();

        if (index.AllowMultiple)
        {
            var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                            batch[batchCount++] = header.EntityPK;
                            if (batchCount >= BatchSize)
                            {
                                count += CountBatch<T>(batch[..batchCount], evaluators, tx, hasFilters);
                                batchCount = 0;
                            }
                        }
                    } while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
                compRevAccessor.Dispose();
            }
        }
        else
        {
            var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
            try
            {
                foreach (var kv in enumerator)
                {
                    ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(kv.Value);
                    batch[batchCount++] = header.EntityPK;
                    if (batchCount >= BatchSize)
                    {
                        count += CountBatch<T>(batch[..batchCount], evaluators, tx, hasFilters);
                        batchCount = 0;
                    }
                }
            }
            finally
            {
                compRevAccessor.Dispose();
            }
        }

        if (batchCount > 0)
        {
            count += CountBatch<T>(batch[..batchCount], evaluators, tx, hasFilters);
        }

        return count;
    }

    private static int CountBatch<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, bool hasFilters) where T : unmanaged
    {
        var count = 0;
        for (var i = 0; i < batch.Length; i++)
        {
            if (!hasFilters || EvaluateFilters<T>(evaluators, tx, batch[i]))
            {
                count++;
            }
        }
        return count;
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
    /// Secondary index scan path: scans a secondary index (unique or AllowMultiple) for matching key values, recovers entity PKs via
    /// <see cref="CompRevStorageHeader.EntityPK"/>, then evaluates remaining predicates via component reads.
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
    /// Scans the secondary index specified by the plan, resolves each entry to entity PKs via <see cref="CompRevStorageHeader.EntityPK"/>.
    /// For unique indexes, each leaf entry's Value is a compRevFirstChunkId. For AllowMultiple indexes, each leaf entry's Value is a VSBS buffer root chunk
    /// ID containing N compRevFirstChunkIds — these are expanded inline.
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
            KeyType.ULong => CollectPKsTyped((BTree<long>)ifi.Index, plan, table),
            KeyType.Float => CollectPKsTyped((BTree<float>)ifi.Index, plan, table),
            KeyType.Double => CollectPKsTyped((BTree<double>)ifi.Index, plan, table),
            _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
        };
    }

    private static List<long> CollectPKsTyped<TKey>(BTree<TKey> index, ExecutionPlan plan, ComponentTable table) where TKey : unmanaged
    {
        var minKey = BTree<TKey>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey>.LongToKey(plan.PrimaryScanMax);

        var capacityHint = plan.EstimatedCounts is { Length: > 0 } ? (int)Math.Min(plan.EstimatedCounts[0], 65536) : 16;
        var result = new List<long>(capacityHint);
        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();

        if (index.AllowMultiple)
        {
            var enumerator = plan.Descending ? index.EnumerateRangeMultipleDescending(minKey, maxKey) : index.EnumerateRangeMultiple(minKey, maxKey);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                            result.Add(header.EntityPK);
                        }
                    } while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
                compRevAccessor.Dispose();
            }
        }
        else
        {
            var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
            try
            {
                foreach (var kv in enumerator)
                {
                    ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(kv.Value);
                    result.Add(header.EntityPK);
                }
            }
            finally
            {
                compRevAccessor.Dispose();
            }
        }

        return result;
    }

    private static bool ProcessBatch<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, bool hasFilters, HashSet<long> unorderedResult, 
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

    #region Two-component overloads

    /// <summary>
    /// Executes a two-component query plan and adds matching entity PKs to the caller-provided <paramref name="result"/> set (unordered).
    /// Reads both components per entity and dispatches evaluator checks on <see cref="FieldEvaluator.ComponentTag"/>.
    /// </summary>
    public void Execute<T1, T2>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> result)
        where T1 : unmanaged where T2 : unmanaged => ExecuteCoreTwo<T1, T2>(plan, evaluators, table, tx, result, null, 0, int.MaxValue);

    /// <summary>
    /// Executes a two-component query plan and adds matching entity PKs to the caller-provided <paramref name="result"/> list preserving
    /// iteration order. Supports Skip/Take with early termination.
    /// </summary>
    public void ExecuteOrderedTwo<T1, T2>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result, 
        int skip = 0, int take = int.MaxValue) where T1 : unmanaged where T2 : unmanaged 
        => ExecuteCoreTwo<T1, T2>(plan, evaluators, table, tx, null, result, skip, take);

    private void ExecuteCoreTwo<T1, T2>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T1 : unmanaged where T2 : unmanaged
    {
        if (take == 0)
        {
            return;
        }

        if (plan.UsesSecondaryIndex)
        {
            ExecuteCoreSecondaryIndexTwo<T1, T2>(plan, evaluators, table, tx, unorderedResult, orderedResult, skip, take);
            return;
        }

        // PK scan path
        var pkIndex = table.PrimaryKeyIndex;
        var hasFilters = evaluators.Length > 0;

        var collected = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;

        var enumerator = plan.Descending ? pkIndex.EnumerateRangeDescending(plan.PrimaryScanMin, plan.PrimaryScanMax) :
            pkIndex.EnumerateRange(plan.PrimaryScanMin, plan.PrimaryScanMax);

        foreach (var kv in enumerator)
        {
            batch[batchCount++] = kv.Key;
            if (batchCount < BatchSize)
            {
                continue;
            }

            if (ProcessBatchTwo<T1, T2>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected))
            {
                return;
            }
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            ProcessBatchTwo<T1, T2>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected);
        }
    }

    private void ExecuteCoreSecondaryIndexTwo<T1, T2>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T1 : unmanaged where T2 : unmanaged
    {
        var pks = CollectPKsFromSecondaryIndex(plan, table);

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

            if (ProcessBatchTwo<T1, T2>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected))
            {
                return;
            }
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            ProcessBatchTwo<T1, T2>(batch[..batchCount], evaluators, tx, hasFilters, unorderedResult, orderedResult, ref skip, ref take, ref collected);
        }
    }

    private static bool ProcessBatchTwo<T1, T2>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, bool hasFilters,
        HashSet<long> unorderedResult, List<long> orderedResult, ref int skip, ref int take, ref int collected)
        where T1 : unmanaged where T2 : unmanaged
    {
        for (var i = 0; i < batch.Length; i++)
        {
            var pk = batch[i];

            if (hasFilters && !EvaluateFiltersTwo<T1, T2>(evaluators, tx, pk))
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
    private static unsafe bool EvaluateFiltersTwo<T1, T2>(FieldEvaluator[] evaluators, Transaction tx, long pk)
        where T1 : unmanaged where T2 : unmanaged
    {
        if (!tx.ReadEntity<T1>(pk, out var comp1) || !tx.ReadEntity<T2>(pk, out var comp2))
        {
            return false;
        }

        var ptr1 = (byte*)Unsafe.AsPointer(ref comp1);
        var ptr2 = (byte*)Unsafe.AsPointer(ref comp2);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            var ptr = eval.ComponentTag == 0 ? ptr1 : ptr2;
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Navigation overloads

    /// <summary>
    /// Target-first navigation: scans the target PK index, evaluates target predicates, then reverse-lookups source entities
    /// via the FK index (AllowMultiple) and evaluates source predicates.
    /// </summary>
    public unsafe void ExecuteNavigationTargetFirst<TSource, TTarget>(FieldEvaluator[] sourceEvals, FieldEvaluator[] targetEvals, ComponentTable sourceCT, 
        ComponentTable targetCT, int fkFieldOffset, Transaction tx, HashSet<long> result) where TSource : unmanaged where TTarget : unmanaged
    {
        // Find the FK index on the source table (long, AllowMultiple)
        var fkIndexInfo = FindFKIndex(sourceCT, fkFieldOffset);
        var fkIndex = (BTree<long>)fkIndexInfo.Index;
        var compRevAccessor = sourceCT.CompRevTableSegment.CreateChunkAccessor();

        try
        {
            // Scan target PK index for qualifying targets
            var targetPKIndex = targetCT.PrimaryKeyIndex;
            foreach (var kv in targetPKIndex.EnumerateLeaves())
            {
                var targetPK = kv.Key;

                // Evaluate target predicates
                if (targetEvals.Length > 0 && !EvaluateFilters<TTarget>(targetEvals, tx, targetPK))
                {
                    continue;
                }

                // Reverse lookup: find source entities that have FK == targetPK
                var enumerator = fkIndex.EnumerateRangeMultiple(targetPK, targetPK);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                                var sourcePK = header.EntityPK;

                                // Evaluate source predicates
                                if (sourceEvals.Length > 0 && !EvaluateFilters<TSource>(sourceEvals, tx, sourcePK))
                                {
                                    continue;
                                }

                                result.Add(sourcePK);
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
        }
        finally
        {
            compRevAccessor.Dispose();
        }
    }

    /// <summary>
    /// Source-first navigation: scans source entities, extracts FK value, reads target, evaluates target predicates.
    /// </summary>
    public unsafe void ExecuteNavigationSourceFirst<TSource, TTarget>(FieldEvaluator[] sourceEvals, FieldEvaluator[] targetEvals, ComponentTable sourceCT, 
        ComponentTable targetCT, int fkFieldOffset, Transaction tx, HashSet<long> result) where TSource : unmanaged where TTarget : unmanaged
    {
        var sourcePKIndex = sourceCT.PrimaryKeyIndex;

        foreach (var kv in sourcePKIndex.EnumerateLeaves())
        {
            var sourcePK = kv.Key;

            // Read source and evaluate source predicates
            if (!tx.ReadEntity<TSource>(sourcePK, out var sourceComp))
            {
                continue;
            }

            if (sourceEvals.Length > 0)
            {
                var sourcePtr = (byte*)Unsafe.AsPointer(ref sourceComp);
                var allPass = true;
                for (var i = 0; i < sourceEvals.Length; i++)
                {
                    ref var eval = ref sourceEvals[i];
                    if (!FieldEvaluator.Evaluate(ref eval, sourcePtr + eval.FieldOffset))
                    {
                        allPass = false;
                        break;
                    }
                }
                if (!allPass)
                {
                    continue;
                }
            }

            // Extract FK value
            var fkValue = *(long*)((byte*)Unsafe.AsPointer(ref sourceComp) + fkFieldOffset);
            if (fkValue == 0)
            {
                continue; // No target reference
            }

            // Read and evaluate target
            if (targetEvals.Length > 0 && !EvaluateFilters<TTarget>(targetEvals, tx, fkValue))
            {
                continue;
            }

            if (targetEvals.Length == 0)
            {
                // No target predicates — just verify the target exists
                if (!tx.ReadEntity<TTarget>(fkValue, out _))
                {
                    continue;
                }
            }

            result.Add(sourcePK);
        }
    }

    /// <summary>
    /// Finds the IndexedFieldInfo for the FK field by matching its offset.
    /// </summary>
    internal static IndexedFieldInfo FindFKIndex(ComponentTable ct, int fkFieldOffset)
    {
        var componentOverhead = ct.Definition.MultipleIndicesCount * sizeof(int);
        var expectedOffset = componentOverhead + fkFieldOffset;

        for (var i = 0; i < ct.IndexedFieldInfos.Length; i++)
        {
            if (ct.IndexedFieldInfos[i].OffsetToField == expectedOffset)
            {
                return ct.IndexedFieldInfos[i];
            }
        }

        throw new InvalidOperationException("FK field index not found. Ensure the FK field has [Index(AllowMultiple = true)].");
    }

    #endregion
}

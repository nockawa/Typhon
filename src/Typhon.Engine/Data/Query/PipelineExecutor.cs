using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

        // PK B+Tree removed — PK scan path is dead. Only caller (EcsViewTyped.CountScan) is never invoked;
        // ECS queries use EcsQuery.Count() which bypasses PipelineExecutor entirely.
        return 0;
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
            KeyType.Byte => CountPKsTyped<T, byte>((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.SByte => CountPKsTyped<T, sbyte>((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Short => CountPKsTyped<T, short>((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.UShort => CountPKsTyped<T, ushort>((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Int => CountPKsTyped<T, int>((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.UInt => CountPKsTyped<T, uint>((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Long => CountPKsTyped<T, long>((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.ULong => CountPKsTyped<T, long>((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Float => CountPKsTyped<T, float>((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Double => CountPKsTyped<T, double>((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
        };
    }

    private static int CountPKsTyped<T, TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators, Transaction tx)
        where T : unmanaged where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);

        // Filter out primary-field evaluators — their conditions are guaranteed by the index range scan.
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
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
                                count += CountBatch<T>(batch[..batchCount], nonPrimaryEvals, tx, hasFilters);
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
                        count += CountBatch<T>(batch[..batchCount], nonPrimaryEvals, tx, hasFilters);
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
            count += CountBatch<T>(batch[..batchCount], nonPrimaryEvals, tx, hasFilters);
        }

        return count;
    }

    private static int CountBatch<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, bool hasFilters) where T : unmanaged
    {
        var count = 0;
        if (!hasFilters)
        {
            // Primary-only: lightweight MVCC visibility check (no QueryRead, no field eval)
            for (var i = 0; i < batch.Length; i++)
            {
                if (tx.IsEntityVisible(batch[i]))
                {
                    count++;
                }
            }
        }
        else
        {
            for (var i = 0; i < batch.Length; i++)
            {
                if (EvaluateFilters<T>(evaluators, tx, batch[i]))
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Filters out evaluators whose FieldIndex matches the primary scan field — those conditions are already guaranteed by the B+Tree range scan.
    /// Returns the original array if no filtering needed.
    /// </summary>
    private static FieldEvaluator[] ComputeNonPrimaryEvaluators(FieldEvaluator[] evaluators, int primaryFieldIndex)
    {
        if (primaryFieldIndex < 0 || evaluators.Length == 0)
        {
            return evaluators;
        }

        int nonPrimaryCount = 0;
        for (int i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex != primaryFieldIndex)
            {
                nonPrimaryCount++;
            }
        }

        if (nonPrimaryCount == evaluators.Length)
        {
            return evaluators; // No primary evaluators to filter
        }

        if (nonPrimaryCount == 0)
        {
            return []; // All evaluators covered by index scan
        }

        var result = new FieldEvaluator[nonPrimaryCount];
        int j = 0;
        for (int i = 0; i < evaluators.Length; i++)
        {
            if (evaluators[i].FieldIndex != primaryFieldIndex)
            {
                result[j++] = evaluators[i];
            }
        }
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

        // Step 2: Compute non-primary evaluators (fields NOT covered by the index range scan).
        // When all evaluators are primary-covered (single-predicate queries), we skip full QueryRead and use a lightweight EntityMap visibility check instead.
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);

        // Step 3: Process collected PKs in batches
        var pkSpan = CollectionsMarshal.AsSpan(pks);
        var collected = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;

        for (var i = 0; i < pkSpan.Length; i++)
        {
            batch[batchCount++] = pkSpan[i];
            if (batchCount < BatchSize)
            {
                continue;
            }

            if (FlushBatch<T>(batch[..batchCount], nonPrimaryEvals, tx, unorderedResult, orderedResult, ref skip, ref take, ref collected))
            {
                return;
            }
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            FlushBatch<T>(batch[..batchCount], nonPrimaryEvals, tx, unorderedResult, orderedResult, ref skip, ref take, ref collected);
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
            KeyType.Byte => CollectPKsTyped((BTree<byte, PersistentStore>)ifi.Index, plan, table),
            KeyType.SByte => CollectPKsTyped((BTree<sbyte, PersistentStore>)ifi.Index, plan, table),
            KeyType.Short => CollectPKsTyped((BTree<short, PersistentStore>)ifi.Index, plan, table),
            KeyType.UShort => CollectPKsTyped((BTree<ushort, PersistentStore>)ifi.Index, plan, table),
            KeyType.Int => CollectPKsTyped((BTree<int, PersistentStore>)ifi.Index, plan, table),
            KeyType.UInt => CollectPKsTyped((BTree<uint, PersistentStore>)ifi.Index, plan, table),
            KeyType.Long => CollectPKsTyped((BTree<long, PersistentStore>)ifi.Index, plan, table),
            KeyType.ULong => CollectPKsTyped((BTree<long, PersistentStore>)ifi.Index, plan, table),
            KeyType.Float => CollectPKsTyped((BTree<float, PersistentStore>)ifi.Index, plan, table),
            KeyType.Double => CollectPKsTyped((BTree<double, PersistentStore>)ifi.Index, plan, table),
            _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
        };
    }

    private static List<long> CollectPKsTyped<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);

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

    /// <summary>Dispatches to set or list variant based on which result collection is provided. Branching is per-batch, not per-item.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FlushBatch<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, HashSet<long> unorderedResult,
        List<long> orderedResult, ref int skip, ref int take, ref int collected) where T : unmanaged
    {
        if (unorderedResult != null)
        {
            ProcessBatchToSet<T>(batch, evaluators, tx, unorderedResult);
            return false;
        }
        return ProcessBatchToList<T>(batch, evaluators, tx, orderedResult, ref skip, ref take, ref collected);
    }

    private static void ProcessBatchToSet<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, HashSet<long> result) where T : unmanaged
    {
        if (evaluators.Length == 0)
        {
            // Primary-only: all field predicates covered by index scan. Lightweight MVCC visibility check.
            for (var i = 0; i < batch.Length; i++)
            {
                if (tx.IsEntityVisible(batch[i]))
                {
                    result.Add(batch[i]);
                }
            }
        }
        else
        {
            for (var i = 0; i < batch.Length; i++)
            {
                if (EvaluateFilters<T>(evaluators, tx, batch[i]))
                {
                    result.Add(batch[i]);
                }
            }
        }
    }

    private static bool ProcessBatchToList<T>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, List<long> result, ref int skip, ref int take,
        ref int collected) where T : unmanaged
    {
        for (var i = 0; i < batch.Length; i++)
        {
            if (evaluators.Length > 0)
            {
                if (!EvaluateFilters<T>(evaluators, tx, batch[i]))
                {
                    continue;
                }
            }
            else
            {
                // Primary-only: lightweight MVCC visibility check
                if (!tx.IsEntityVisible(batch[i]))
                {
                    continue;
                }
            }

            if (skip > 0)
            {
                skip--;
                continue;
            }

            result.Add(batch[i]);
            collected++;
            if (collected >= take)
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool EvaluateFilters<T>(FieldEvaluator[] evaluators, Transaction tx, long pk) where T : unmanaged
    {
        if (!tx.QueryRead<T>(pk, out var comp))
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
        }
    }

    private void ExecuteCoreSecondaryIndexTwo<T1, T2>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T1 : unmanaged where T2 : unmanaged
    {
        var pks = CollectPKsFromSecondaryIndex(plan, table);

        var pkSpan = CollectionsMarshal.AsSpan(pks);
        var collected = 0;
        Span<long> batch = stackalloc long[BatchSize];
        var batchCount = 0;

        for (var i = 0; i < pkSpan.Length; i++)
        {
            batch[batchCount++] = pkSpan[i];
            if (batchCount < BatchSize)
            {
                continue;
            }

            if (FlushBatchTwo<T1, T2>(batch[..batchCount], evaluators, tx, unorderedResult, orderedResult, ref skip, ref take, ref collected))
            {
                return;
            }
            batchCount = 0;
        }

        if (batchCount > 0)
        {
            FlushBatchTwo<T1, T2>(batch[..batchCount], evaluators, tx, unorderedResult, orderedResult, ref skip, ref take, ref collected);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FlushBatchTwo<T1, T2>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, HashSet<long> unorderedResult,
        List<long> orderedResult, ref int skip, ref int take, ref int collected) where T1 : unmanaged where T2 : unmanaged
    {
        if (unorderedResult != null)
        {
            ProcessBatchTwoToSet<T1, T2>(batch, evaluators, tx, unorderedResult);
            return false;
        }
        return ProcessBatchTwoToList<T1, T2>(batch, evaluators, tx, orderedResult, ref skip, ref take, ref collected);
    }

    private static void ProcessBatchTwoToSet<T1, T2>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, HashSet<long> result)
        where T1 : unmanaged where T2 : unmanaged
    {
        if (evaluators.Length == 0)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                result.Add(batch[i]);
            }
        }
        else
        {
            for (var i = 0; i < batch.Length; i++)
            {
                if (EvaluateFiltersTwo<T1, T2>(evaluators, tx, batch[i]))
                {
                    result.Add(batch[i]);
                }
            }
        }
    }

    private static bool ProcessBatchTwoToList<T1, T2>(Span<long> batch, FieldEvaluator[] evaluators, Transaction tx, List<long> result, ref int skip, 
        ref int take, ref int collected) where T1 : unmanaged where T2 : unmanaged
    {
        for (var i = 0; i < batch.Length; i++)
        {
            if (evaluators.Length > 0 && !EvaluateFiltersTwo<T1, T2>(evaluators, tx, batch[i]))
            {
                continue;
            }

            if (skip > 0)
            {
                skip--;
                continue;
            }

            result.Add(batch[i]);
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
        if (!tx.QueryRead<T1>(pk, out var comp1) || !tx.QueryRead<T2>(pk, out var comp2))
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

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> by scanning secondary indexes and evaluating field predicates inline.
/// For Versioned components, walks the MVCC revision chain directly from the index value (no EntityMap re-lookup).
/// For SingleVersion components, reads component data and entityPK from the inline chunk overhead.
/// Predicates are ordered by ascending selectivity for short-circuit efficiency.
/// </summary>
internal class PipelineExecutor
{
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
    public void Execute(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> result) 
        => ExecuteCore(plan, evaluators, table, tx, result, null, 0, int.MaxValue);

    /// <summary>
    /// Executes the plan and adds matching entity PKs to the caller-provided <paramref name="result"/> list preserving iteration order.
    /// Supports Skip/Take with early termination.
    /// </summary>
    public void ExecuteOrdered(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result, int skip = 0,
        int take = int.MaxValue) => ExecuteCore(plan, evaluators, table, tx, null, result, skip, take);

    /// <summary>
    /// Counts matching entities without allocating a result collection. Runs the same scan + filter pipeline but only increments a counter.
    /// </summary>
    public int Count(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx)
    {
        if (plan.UsesSecondaryIndex)
        {
            return CountCoreSecondaryIndex(plan, evaluators, table, tx);
        }

        // PK B+Tree removed — non-secondary-index count path returns 0.
        // All current callers (EcsQuery.Count via WhereField, EcsView.CountScan) use secondary indexes.
        return 0;
    }

    /// <summary>
    /// Dispatches to the typed count method based on the primary key type and storage mode.
    /// </summary>
    private int CountCoreSecondaryIndex(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx)
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];
        return plan.PrimaryKeyType switch
        {
            KeyType.Byte => CountPKsTyped((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.SByte => CountPKsTyped((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Short => CountPKsTyped((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.UShort => CountPKsTyped((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Int => CountPKsTyped((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.UInt => CountPKsTyped((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Long => CountPKsTyped((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.ULong => CountPKsTyped((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Float => CountPKsTyped((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            KeyType.Double => CountPKsTyped((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx),
            _ => throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for index scan")
        };
    }

    private static int CountPKsTyped<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators, Transaction tx) where TKey : unmanaged
    {
        // SingleVersion: index values are component chunkIds — read data directly, no CompRevTable.
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            return CountPKsTypedSV(index, plan, table, evaluators);
        }

        // Combined scan + chain walk + evaluate: the index value IS the compRevFirstChunkId.
        // Walk the revision chain directly from here — no EntityMap re-lookup (eliminates the
        // double CompRevTable walk that QueryRead would perform via GetCompRevInfoFromIndex).
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var count = 0;

        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();
        var compContentAccessor = hasFilters ? table.ComponentSegment.CreateChunkAccessor() : default;
        try
        {
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
                                if (CountOneVersioned(values[j], ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx))
                                {
                                    count++;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (CountOneVersioned(kv.Value, ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx))
                    {
                        count++;
                    }
                }
            }
        }
        finally
        {
            compRevAccessor.Dispose();
            if (hasFilters)
            {
                compContentAccessor.Dispose();
            }
        }

        return count;
    }

    /// <summary>
    /// Evaluate one Versioned entity from its compRevFirstChunkId: walk revision chain for MVCC visibility,
    /// optionally read component data and evaluate non-primary filters. No EntityMap lookup needed.
    /// </summary>
    private static unsafe bool CountOneVersioned(int compRevFirstChunkId, ref ChunkAccessor<PersistentStore> compRevAccessor,
        ref ChunkAccessor<PersistentStore> compContentAccessor, ComponentTable table, FieldEvaluator[] nonPrimaryEvals, bool hasFilters, Transaction tx)
    {
        if (!hasFilters)
        {
            // Primary-only: lightweight MVCC visibility check via EntityMap (no chain walk needed).
            // Read entityPK from CompRevStorageHeader, then check IsEntityVisible.
            ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
            return tx.IsEntityVisible(header.EntityPK);
        }

        // Non-primary filters present: walk revision chain to resolve CurCompContentChunkId for field evaluation.
        var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, tx.TSN);
        if (chainResult.IsFailure || chainResult.Value.CurCompContentChunkId == 0)
        {
            return false;
        }

        byte* ptr = compContentAccessor.GetChunkAddress(chainResult.Value.CurCompContentChunkId) + table.ComponentOverhead;
        for (var i = 0; i < nonPrimaryEvals.Length; i++)
        {
            ref var eval = ref nonPrimaryEvals[i];
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// SV-specific count: iterates the secondary index directly, reading component data from chunkIds
    /// without CompRevTable resolution (SV has no revision chains). For primary-only queries,
    /// counts index entries directly. For non-primary filters, reads component data from the chunk.
    /// </summary>
    private static int CountPKsTypedSV<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, FieldEvaluator[] evaluators) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var count = 0;

        // For SV, component data is read directly from the component segment (no revision chain).
        var compAccessor = table.ComponentSegment.CreateChunkAccessor();
        try
        {
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
                                if (!hasFilters || EvaluateFiltersSV(nonPrimaryEvals, table, values[j], ref compAccessor))
                                {
                                    count++;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (!hasFilters || EvaluateFiltersSV(nonPrimaryEvals, table, kv.Value, ref compAccessor))
                    {
                        count++;
                    }
                }
            }
        }
        finally
        {
            compAccessor.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Evaluates non-primary field predicates directly on SV component data.
    /// No MVCC resolution — SV writes are in-place, index entries are current.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool EvaluateFiltersSV(FieldEvaluator[] evaluators, ComponentTable table, int chunkId, ref ChunkAccessor<PersistentStore> compAccessor)
    {
        byte* ptr = compAccessor.GetChunkAddress(chunkId) + table.ComponentOverhead;
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }
        return true;
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

    private void ExecuteCore(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, HashSet<long> unorderedResult,
        List<long> orderedResult, int skip, int take)
    {
        if (take == 0)
        {
            return;
        }

        if (plan.UsesSecondaryIndex)
        {
            ExecuteCoreSecondaryIndex(plan, evaluators, table, tx, unorderedResult, orderedResult, skip, take);
        }
    }

    /// <summary>
    /// Secondary index scan path: scans a secondary index (unique or AllowMultiple) for matching key values, recovers entity PKs via
    /// <see cref="CompRevStorageHeader.EntityPK"/>, then evaluates remaining predicates via component reads.
    /// </summary>
    private void ExecuteCoreSecondaryIndex(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take)
    {
        // SingleVersion: combined scan+evaluate in one pass (no QueryRead, no CompRevTable).
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            ExecuteCoreSecondaryIndexSV(plan, evaluators, table, unorderedResult, orderedResult, skip, take);
            return;
        }

        // Combined scan + chain walk + evaluate: same optimization as Count —
        // walk revision chain directly from index value, skip EntityMap re-lookup.
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];

        switch (plan.PrimaryKeyType)
        {
            case KeyType.Byte:   ExecutePKsTypedVersioned((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.SByte:  ExecutePKsTypedVersioned((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Short:  ExecutePKsTypedVersioned((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UShort: ExecutePKsTypedVersioned((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Int:    ExecutePKsTypedVersioned((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UInt:   ExecutePKsTypedVersioned((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Long:   ExecutePKsTypedVersioned((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.ULong:  ExecutePKsTypedVersioned((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Float:  ExecutePKsTypedVersioned((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Double: ExecutePKsTypedVersioned((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for Versioned index scan");
        }
    }

    /// <summary>
    /// Versioned combined Execute: iterates index, walks revision chain directly from compRevFirstChunkId (no EntityMap re-lookup),
    /// evaluates non-primary filters on the resolved component data, collects entity PKs.
    /// </summary>
    private static void ExecutePKsTypedVersioned<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table,
        FieldEvaluator[] evaluators, Transaction tx, HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var collected = 0;

        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();
        var compContentAccessor = hasFilters ? table.ComponentSegment.CreateChunkAccessor() : default;
        try
        {
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
                                if (ExecuteOneVersioned(values[j], ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx,
                                        unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                                {
                                    return;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (ExecuteOneVersioned(kv.Value, ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, hasFilters, tx,
                            unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            compRevAccessor.Dispose();
            if (hasFilters)
            {
                compContentAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Process one Versioned entity for Execute: chain walk, MVCC check, filter evaluation, PK collection.
    /// Returns true if entity was collected (for take limit tracking).
    /// </summary>
    private static unsafe bool ExecuteOneVersioned(int compRevFirstChunkId, ref ChunkAccessor<PersistentStore> compRevAccessor,
        ref ChunkAccessor<PersistentStore> compContentAccessor, ComponentTable table, FieldEvaluator[] nonPrimaryEvals, bool hasFilters, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, ref int skip, ref int collected)
    {
        // Read entityPK from CompRevStorageHeader
        ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
        long entityPK = header.EntityPK;

        if (!hasFilters)
        {
            // Primary-only: lightweight MVCC visibility check via EntityMap (no chain walk needed)
            if (!tx.IsEntityVisible(entityPK))
            {
                return false;
            }
        }
        else
        {
            // Non-primary filters: walk chain to resolve component data for field evaluation
            var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, tx.TSN);
            if (chainResult.IsFailure || chainResult.Value.CurCompContentChunkId == 0)
            {
                return false;
            }

            byte* ptr = compContentAccessor.GetChunkAddress(chainResult.Value.CurCompContentChunkId) + table.ComponentOverhead;
            for (var i = 0; i < nonPrimaryEvals.Length; i++)
            {
                ref var eval = ref nonPrimaryEvals[i];
                if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
                {
                    return false;
                }
            }
        }

        if (skip > 0) { skip--; return false; }

        if (unorderedResult != null) { unorderedResult.Add(entityPK); }
        else { orderedResult?.Add(entityPK); }

        collected++;
        return true;
    }

    /// <summary>
    /// SV-specific secondary index Execute: dispatches to typed method for index iteration.
    /// Combines scan + evaluate + collect in one pass — no QueryRead, no CompRevTable.
    /// </summary>
    private void ExecuteCoreSecondaryIndexSV(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, HashSet<long> unorderedResult, 
        List<long> orderedResult, int skip, int take)
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];

        switch (plan.PrimaryKeyType)
        {
            case KeyType.Byte:   ExecutePKsTypedSV((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.SByte:  ExecutePKsTypedSV((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Short:  ExecutePKsTypedSV((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UShort: ExecutePKsTypedSV((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Int:    ExecutePKsTypedSV((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UInt:   ExecutePKsTypedSV((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Long:   ExecutePKsTypedSV((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.ULong:  ExecutePKsTypedSV((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Float:  ExecutePKsTypedSV((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Double: ExecutePKsTypedSV((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, unorderedResult, orderedResult, skip, take); break;
            default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported for SV index scan");
        }
    }

    /// <summary>
    /// SV typed Execute: iterates index range, reads entityPK from inline chunk overhead (offset 0), evaluates non-primary filters from component data,
    /// collects matching PKs.
    /// </summary>
    private static unsafe void ExecutePKsTypedSV<TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table, 
        FieldEvaluator[] evaluators, HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var hasFilters = nonPrimaryEvals.Length > 0;
        var collected = 0;

        var compAccessor = table.ComponentSegment.CreateChunkAccessor();
        try
        {
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
                                if (hasFilters && !EvaluateFiltersSV(nonPrimaryEvals, table, values[j], ref compAccessor))
                                {
                                    continue;
                                }

                                long entityPK = *(long*)compAccessor.GetChunkAddress(values[j]);
                                if (skip > 0)
                                {
                                    skip--; continue;
                                }

                                if (unorderedResult != null)
                                {
                                    unorderedResult.Add(entityPK);
                                }
                                else
                                {
                                    orderedResult?.Add(entityPK);
                                }

                                if (++collected >= take) { return; }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (hasFilters && !EvaluateFiltersSV(nonPrimaryEvals, table, kv.Value, ref compAccessor))
                    {
                        continue;
                    }

                    long entityPK = *(long*)compAccessor.GetChunkAddress(kv.Value);
                    if (skip > 0)
                    {
                        skip--; continue;
                    }

                    if (unorderedResult != null)
                    {
                        unorderedResult.Add(entityPK);
                    }
                    else
                    {
                        orderedResult?.Add(entityPK);
                    }

                    if (++collected >= take)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            compAccessor.Dispose();
        }
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
    /// Executes a two-component query plan and adds matching entity PKs to the caller-provided <paramref name="result"/> list preserving
    /// iteration order. Supports Skip/Take with early termination.
    /// <typeparamref name="T"/> is the secondary component type, read via <see cref="Transaction.QueryRead{T}"/>.
    /// The primary component (T1) is resolved inline from the index — no generic type needed.
    /// </summary>
    public void ExecuteOrderedTwo<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx, List<long> result,
        int skip = 0, int take = int.MaxValue) where T : unmanaged 
        => ExecuteCoreTwo<T>(plan, evaluators, table, tx, null, result, skip, take);

    private void ExecuteCoreTwo<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T : unmanaged
    {
        if (take == 0)
        {
            return;
        }

        if (plan.UsesSecondaryIndex)
        {
            ExecuteCoreSecondaryIndexTwo<T>(plan, evaluators, table, tx, unorderedResult, orderedResult, skip, take);
        }
    }

    /// <summary>
    /// Two-component combined scan: resolves the primary component directly from the index value (no EntityMap re-lookup),
    /// reads the secondary component <typeparamref name="T"/> via QueryRead (different component, needs its own lookup).
    /// Dispatches to typed method for the primary component's index key type.
    /// </summary>
    private void ExecuteCoreSecondaryIndexTwo<T>(ExecutionPlan plan, FieldEvaluator[] evaluators, ComponentTable table, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T : unmanaged
    {
        var ifi = table.IndexedFieldInfos[plan.PrimaryFieldIndex];

        // SV path for T1: use inline entityPK from chunk overhead
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            switch (plan.PrimaryKeyType)
            {
                case KeyType.Byte:   ExecutePKsTypedTwoSV<T, byte>((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.SByte:  ExecutePKsTypedTwoSV<T, sbyte>((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.Short:  ExecutePKsTypedTwoSV<T, short>((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.UShort: ExecutePKsTypedTwoSV<T, ushort>((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.Int:    ExecutePKsTypedTwoSV<T, int>((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.UInt:   ExecutePKsTypedTwoSV<T, uint>((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.Long:   ExecutePKsTypedTwoSV<T, long>((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.ULong:  ExecutePKsTypedTwoSV<T, long>((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.Float:  ExecutePKsTypedTwoSV<T, float>((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                case KeyType.Double: ExecutePKsTypedTwoSV<T, double>((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
                default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported");
            }
            return;
        }

        // Versioned path for T1: resolve chain directly from index value
        switch (plan.PrimaryKeyType)
        {
            case KeyType.Byte:   ExecutePKsTypedTwoVersioned<T, byte>((BTree<byte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.SByte:  ExecutePKsTypedTwoVersioned<T, sbyte>((BTree<sbyte, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Short:  ExecutePKsTypedTwoVersioned<T, short>((BTree<short, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UShort: ExecutePKsTypedTwoVersioned<T, ushort>((BTree<ushort, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Int:    ExecutePKsTypedTwoVersioned<T, int>((BTree<int, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.UInt:   ExecutePKsTypedTwoVersioned<T, uint>((BTree<uint, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Long:   ExecutePKsTypedTwoVersioned<T, long>((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.ULong:  ExecutePKsTypedTwoVersioned<T, long>((BTree<long, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Float:  ExecutePKsTypedTwoVersioned<T, float>((BTree<float, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            case KeyType.Double: ExecutePKsTypedTwoVersioned<T, double>((BTree<double, PersistentStore>)ifi.Index, plan, table, evaluators, tx, unorderedResult, orderedResult, skip, take); break;
            default: throw new NotSupportedException($"KeyType {plan.PrimaryKeyType} not supported");
        }
    }

    /// <summary>
    /// Two-component Versioned: resolves the primary component from the index chain walk,
    /// reads secondary component <typeparamref name="T"/> via QueryRead.
    /// Eliminates EntityMap re-lookup and intermediate List allocation for the primary component.
    /// </summary>
    private static void ExecutePKsTypedTwoVersioned<T, TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table,
        FieldEvaluator[] evaluators, Transaction tx, HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T : unmanaged where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var collected = 0;

        var compRevAccessor = table.CompRevTableSegment.CreateChunkAccessor();
        var compContentAccessor = table.ComponentSegment.CreateChunkAccessor();
        try
        {
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
                                if (ExecuteOneTwoVersioned<T>(values[j], ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, tx,
                                        unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                                {
                                    return;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (ExecuteOneTwoVersioned<T>(kv.Value, ref compRevAccessor, ref compContentAccessor, table, nonPrimaryEvals, tx,
                            unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            compRevAccessor.Dispose();
            compContentAccessor.Dispose();
        }
    }

    /// <summary>
    /// Process one entity for two-component Versioned Execute: primary component resolved from chain walk (no EntityMap re-lookup),
    /// secondary component <typeparamref name="T"/> via QueryRead. Evaluates interleaved predicates via ComponentTag dispatch.
    /// </summary>
    private static unsafe bool ExecuteOneTwoVersioned<T>(int compRevFirstChunkId, ref ChunkAccessor<PersistentStore> compRevAccessor,
        ref ChunkAccessor<PersistentStore> compContentAccessor, ComponentTable table, FieldEvaluator[] evaluators, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, ref int skip, ref int collected) where T : unmanaged
    {
        // Resolve T1: walk chain directly from index value
        ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
        long entityPK = header.EntityPK;

        var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, tx.TSN);
        if (chainResult.IsFailure || chainResult.Value.CurCompContentChunkId == 0)
        {
            return false;
        }

        // Read T1 component data directly from resolved chunk
        byte* t1Ptr = compContentAccessor.GetChunkAddress(chainResult.Value.CurCompContentChunkId) + table.ComponentOverhead;

        // Read T2 via QueryRead (different component, needs its own EntityMap lookup)
        if (!tx.QueryRead<T>(entityPK, out var comp2))
        {
            return false;
        }

        // Evaluate interleaved predicates (ComponentTag selects T1 or T2)
        var ptr2 = (byte*)Unsafe.AsPointer(ref comp2);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            var ptr = eval.ComponentTag == 0 ? t1Ptr : ptr2;
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }

        if (skip > 0) { skip--; return false; }

        if (unorderedResult != null) { unorderedResult.Add(entityPK); }
        else { orderedResult?.Add(entityPK); }

        collected++;
        return true;
    }

    /// <summary>
    /// Two-component SV: reads primary component data and entityPK from inline chunk overhead,
    /// reads secondary component <typeparamref name="T"/> via QueryRead.
    /// </summary>
    private static void ExecutePKsTypedTwoSV<T, TKey>(BTree<TKey, PersistentStore> index, ExecutionPlan plan, ComponentTable table,
        FieldEvaluator[] evaluators, Transaction tx, HashSet<long> unorderedResult, List<long> orderedResult, int skip, int take) where T : unmanaged where TKey : unmanaged
    {
        var minKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMin);
        var maxKey = BTree<TKey, PersistentStore>.LongToKey(plan.PrimaryScanMax);
        var nonPrimaryEvals = ComputeNonPrimaryEvaluators(evaluators, plan.PrimaryFieldIndex);
        var collected = 0;

        var compAccessor = table.ComponentSegment.CreateChunkAccessor();
        try
        {
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
                                if (ExecuteOneTwoSV<T>(values[j], ref compAccessor, table, nonPrimaryEvals, tx,
                                        unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                                {
                                    return;
                                }
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
            else
            {
                var enumerator = plan.Descending ? index.EnumerateRangeDescending(minKey, maxKey) : index.EnumerateRange(minKey, maxKey);
                foreach (var kv in enumerator)
                {
                    if (ExecuteOneTwoSV<T>(kv.Value, ref compAccessor, table, nonPrimaryEvals, tx,
                            unorderedResult, orderedResult, ref skip, ref collected) && collected >= take)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            compAccessor.Dispose();
        }
    }

    private static unsafe bool ExecuteOneTwoSV<T>(int chunkId, ref ChunkAccessor<PersistentStore> compAccessor,
        ComponentTable table, FieldEvaluator[] evaluators, Transaction tx,
        HashSet<long> unorderedResult, List<long> orderedResult, ref int skip, ref int collected) where T : unmanaged
    {
        byte* chunkPtr = compAccessor.GetChunkAddress(chunkId);
        long entityPK = *(long*)chunkPtr;
        byte* t1Ptr = chunkPtr + table.ComponentOverhead;

        if (!tx.QueryRead<T>(entityPK, out var comp2))
        {
            return false;
        }

        var ptr2 = (byte*)Unsafe.AsPointer(ref comp2);
        for (var i = 0; i < evaluators.Length; i++)
        {
            ref var eval = ref evaluators[i];
            var ptr = eval.ComponentTag == 0 ? t1Ptr : ptr2;
            if (!FieldEvaluator.Evaluate(ref eval, ptr + eval.FieldOffset))
            {
                return false;
            }
        }

        if (skip > 0) { skip--; return false; }

        if (unorderedResult != null) { unorderedResult.Add(entityPK); }
        else { orderedResult?.Add(entityPK); }

        collected++;
        return true;
    }

    #endregion

    #region Navigation overloads

    /// <summary>
    /// Finds the IndexedFieldInfo for the FK field by matching its offset.
    /// </summary>
    internal static IndexedFieldInfo FindFKIndex(ComponentTable ct, int fkFieldOffset)
    {
        var expectedOffset = ct.ComponentOverhead + fkFieldOffset;

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

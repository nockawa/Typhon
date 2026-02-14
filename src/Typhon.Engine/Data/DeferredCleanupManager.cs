using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Tracks entities whose revision cleanup was deferred because a long-running transaction (the "tail")
/// was blocking cleanup at commit time. When the blocking transaction completes, queued entries are
/// processed and old revisions are removed.
/// </summary>
/// <remarks>
/// <para>
/// The queue is keyed by the blocking transaction's TSN. When a tail commits, all entries with
/// <c>blockingTSN ≤ completedTSN</c> are collected and cleaned up outside the lock.
/// </para>
/// <para>
/// A reverse index ensures each (ComponentTable, PrimaryKey) pair appears at most once in the queue,
/// regardless of how many concurrent transactions update the same entity while blocked.
/// </para>
/// </remarks>
internal class DeferredCleanupManager
{
    internal struct CleanupEntry
    {
        public ComponentTable Table;
        public long PrimaryKey;
    }

    // Primary storage: sorted by blocking TSN for efficient range queries
    private readonly SortedDictionary<long, List<CleanupEntry>> _pendingCleanups;

    // Reverse index: (table, pk) → blockingTSN for O(1) dedup
    private readonly Dictionary<(ComponentTable, long), long> _entityToBlockingTSN;

    // Pool for reusing List<CleanupEntry> instances across TSN buckets — avoids GC alloc per bucket.
    // All access is under _lock, so no additional synchronization needed.
    private const int ListPoolMaxSize = 16;
    private readonly Stack<List<CleanupEntry>> _listPool = new();

    // Thread safety
    private AccessControl _lock;

    // Configuration
    private readonly DeferredCleanupOptions _options;

    // Logger (nullable — no nullable annotations per project conventions)
    private readonly ILogger _log;

    // Observability counters

    /// <summary>
    /// Current number of entities in the deferred cleanup queue. Updated atomically for observability.
    /// </summary>
    public int QueueSize;

    /// <summary>Total entities enqueued for deferred cleanup.</summary>
    public long EnqueuedTotal { get; private set; }

    /// <summary>Total entities cleaned via the deferred path.</summary>
    public long ProcessedTotal { get; private set; }

    /// <summary>Total lazy cleanups performed during entity reads.</summary>
    public long LazyCleanupTotal { get; private set; }

    /// <summary>Total lazy cleanups skipped because the revision chain lock was unavailable.</summary>
    public long LazyCleanupSkipped { get; private set; }

    /// <summary>Minimum ItemCount before lazy cleanup is attempted.</summary>
    internal int LazyCleanupThreshold => _options.LazyCleanupThreshold;

    public DeferredCleanupManager(DeferredCleanupOptions options, ILogger log = null)
    {
        _options = options;
        _log = log;
        _pendingCleanups = new SortedDictionary<long, List<CleanupEntry>>();
        _entityToBlockingTSN = new Dictionary<(ComponentTable, long), long>();
    }

    /// <summary>Record a successful lazy cleanup operation.</summary>
    internal void RecordLazyCleanup() => LazyCleanupTotal++;

    /// <summary>Record a lazy cleanup that was skipped (lock unavailable).</summary>
    internal void RecordLazyCleanupSkipped() => LazyCleanupSkipped++;

    /// <summary>
    /// Enqueue a batch of entities for deferred cleanup under a single lock acquisition.
    /// All entries share the same <paramref name="blockingTSN"/> (the tail TSN at the time of commit).
    /// </summary>
    /// <param name="blockingTSN">The TSN of the oldest active transaction blocking cleanup</param>
    /// <param name="entries">The batch of entities to enqueue. Must not be null or empty.</param>
    public void EnqueueBatch(long blockingTSN, List<CleanupEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/EnqueueBatch", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        // Get-or-create the TSN bucket once for the whole batch
        if (!_pendingCleanups.TryGetValue(blockingTSN, out var list))
        {
            list = RentList();
            _pendingCleanups[blockingTSN] = list;
        }

        var added = 0;
        var span = CollectionsMarshal.AsSpan(entries);
        for (int i = 0; i < span.Length; i++)
        {
            ref var entry = ref span[i];
            var key = (entry.Table, entry.PrimaryKey);

            if (_entityToBlockingTSN.TryGetValue(key, out var existingTSN))
            {
                if (blockingTSN >= existingTSN)
                {
                    continue; // Already queued under an older (or same) TSN
                }

                // New blocking TSN is older — migrate to new bucket
                RemoveFromList(existingTSN, entry.Table, entry.PrimaryKey);
            }

            list.Add(entry);
            _entityToBlockingTSN[key] = blockingTSN;
            added++;
        }

        var currentSize = _entityToBlockingTSN.Count;
        QueueSize = currentSize;
        _lock.ExitExclusiveAccess();

        EnqueuedTotal += added;

        // High-water-mark logging — only at exact crossing points to avoid spam
        if (currentSize == _options.CriticalThreshold)
        {
            _log?.LogWarning("Deferred cleanup queue reached critical threshold ({Size} entities)", currentSize);
        }
        else if (currentSize == _options.HighWaterMark)
        {
            _log?.LogWarning("Deferred cleanup queue reached high water mark ({Size} entities)", currentSize);
        }
    }

    /// <summary>
    /// Process all deferred cleanups for entities blocked by transactions with TSN ≤ <paramref name="completedTSN"/>.
    /// </summary>
    /// <param name="completedTSN">The TSN of the tail transaction that just committed</param>
    /// <param name="nextMinTSN">The minimum TSN to keep revisions for (the next oldest active transaction)</param>
    /// <param name="dbe">The database engine (for epoch manager and MMF access)</param>
    /// <param name="changeSet">The change set for tracking dirty pages</param>
    /// <returns>The number of entities cleaned up</returns>
    public int ProcessDeferredCleanups(long completedTSN, long nextMinTSN, DatabaseEngine dbe, ChangeSet changeSet)
    {
        // Lockless early-exit: Count is a plain int field read (atomic on x64).
        // Stale zero → skip (entries caught on next call). Stale non-zero → acquire lock, find nothing, exit.
        if (_pendingCleanups.Count == 0)
        {
            return 0;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/Process", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        // Peek at the smallest key — if it's beyond completedTSN, nothing to collect
        using (var peek = _pendingCleanups.GetEnumerator())
        {
            if (!peek.MoveNext() || peek.Current.Key > completedTSN)
            {
                _lock.ExitExclusiveAccess();
                return 0;
            }
        }

        // We have work to do — allocate collection lists
        var toCleanup = new List<CleanupEntry>();
        var tsnsToRemove = new List<long>();

        foreach (var kvp in _pendingCleanups)
        {
            if (kvp.Key > completedTSN)
            {
                break; // SortedDictionary — no more relevant entries
            }

            toCleanup.AddRange(kvp.Value);
            tsnsToRemove.Add(kvp.Key);

            // Remove from reverse lookup, then return the list to the pool
            foreach (var entry in kvp.Value)
            {
                _entityToBlockingTSN.Remove((entry.Table, entry.PrimaryKey));
            }
            ReturnList(kvp.Value);
        }

        // Remove processed TSN buckets
        foreach (var tsn in tsnsToRemove)
        {
            _pendingCleanups.Remove(tsn);
        }

        QueueSize = _entityToBlockingTSN.Count;
        _lock.ExitExclusiveAccess();

        // Perform cleanup OUTSIDE the lock (I/O operations)
        var cleanedCount = 0;
        foreach (var entry in toCleanup)
        {
            if (CleanupEntityRevisions(entry.Table, entry.PrimaryKey, nextMinTSN, dbe, changeSet))
            {
                cleanedCount++;
            }
        }

        if (cleanedCount > 0)
        {
            ProcessedTotal += cleanedCount;
        }

        return cleanedCount;
    }

    /// <summary>
    /// Clean up old revisions for a single entity. Creates its own accessors within an epoch scope.
    /// </summary>
    private static bool CleanupEntityRevisions(ComponentTable table, long pk, long nextMinTSN, DatabaseEngine dbe, ChangeSet changeSet)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);

        // Look up the entity's revision chain from the PK index
        var pkIndexAccessor = table.PrimaryKeyIndex.Segment.CreateChunkAccessor(changeSet);
        var lookupResult = table.PrimaryKeyIndex.TryGet(pk, ref pkIndexAccessor);
        pkIndexAccessor.Dispose();

        if (lookupResult.IsFailure)
        {
            return false; // Entity no longer exists — already cleaned up by deletion
        }

        var firstChunkId = lookupResult.Value;

        // Create accessors for the cleanup operation
        var compRevTableAccessor = table.CompRevTableSegment.CreateChunkAccessor(changeSet);
        var compContentAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);

        var isDeleted = ComponentRevisionManager.CleanUpUnusedEntriesCore(
            table, firstChunkId, nextMinTSN, ref compRevTableAccessor, ref compContentAccessor);

        if (isDeleted)
        {
            // Entity is fully deleted — remove from PK index and free revision chain
            var accessor = table.PrimaryKeyIndex.Segment.CreateChunkAccessor(changeSet);
            table.PrimaryKeyIndex.Remove(pk, out _, ref accessor);
            accessor.Dispose();

            table.CompRevTableSegment.FreeChunk(firstChunkId);
        }
        else
        {
            compRevTableAccessor.DirtyChunk(firstChunkId);
        }

        compRevTableAccessor.Dispose();
        compContentAccessor.Dispose();

        return true;
    }

    /// <summary>Rent a list from the pool or create a new one. Must be called under lock.</summary>
    private List<CleanupEntry> RentList()
    {
        if (_listPool.TryPop(out var list))
        {
            return list;
        }
        return [];
    }

    /// <summary>Clear and return a list to the pool. Must be called under lock.</summary>
    private void ReturnList(List<CleanupEntry> list)
    {
        list.Clear();
        if (_listPool.Count < ListPoolMaxSize)
        {
            _listPool.Push(list);
        }
    }

    /// <summary>
    /// Remove a specific entity from a TSN bucket in the pending cleanups list.
    /// Must be called under lock.
    /// </summary>
    private void RemoveFromList(long tsn, ComponentTable table, long pk)
    {
        if (!_pendingCleanups.TryGetValue(tsn, out var list))
        {
            return;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Table == table && list[i].PrimaryKey == pk)
            {
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
        {
            ReturnList(list);
            _pendingCleanups.Remove(tsn);
        }
    }
}

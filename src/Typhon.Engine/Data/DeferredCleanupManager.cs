using Microsoft.Extensions.Logging;
using System.Collections.Generic;

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
    private struct CleanupEntry
    {
        public ComponentTable Table;
        public long PrimaryKey;
    }

    // Primary storage: sorted by blocking TSN for efficient range queries
    private readonly SortedDictionary<long, List<CleanupEntry>> _pendingCleanups;

    // Reverse index: (table, pk) → blockingTSN for O(1) dedup
    private readonly Dictionary<(ComponentTable, long), long> _entityToBlockingTSN;

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
    /// Enqueue an entity for deferred cleanup, to be processed when the blocking transaction completes.
    /// </summary>
    /// <param name="blockingTSN">The TSN of the oldest active transaction blocking cleanup</param>
    /// <param name="table">The component table owning the entity</param>
    /// <param name="pk">The primary key of the entity</param>
    public void Enqueue(long blockingTSN, ComponentTable table, long pk)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/Enqueue", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        var key = (table, pk);

        // Check if already queued
        if (_entityToBlockingTSN.TryGetValue(key, out var existingTSN))
        {
            // Keep the OLDEST blocking TSN — cleanup should happen when the oldest blocker completes
            if (blockingTSN >= existingTSN)
            {
                _lock.ExitExclusiveAccess();
                return;
            }

            // New blocking TSN is older — migrate to new bucket
            RemoveFromList(existingTSN, table, pk);
        }

        // Add to appropriate TSN bucket
        if (!_pendingCleanups.TryGetValue(blockingTSN, out var list))
        {
            list = new List<CleanupEntry>();
            _pendingCleanups[blockingTSN] = list;
        }

        list.Add(new CleanupEntry { Table = table, PrimaryKey = pk });
        _entityToBlockingTSN[key] = blockingTSN;
        var currentSize = _entityToBlockingTSN.Count;
        QueueSize = currentSize;

        _lock.ExitExclusiveAccess();

        EnqueuedTotal++;

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
        // Collect entries under lock
        var toCleanup = new List<CleanupEntry>();
        var tsnsToRemove = new List<long>();

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/Process", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        foreach (var kvp in _pendingCleanups)
        {
            if (kvp.Key > completedTSN)
            {
                break; // SortedDictionary — no more relevant entries
            }

            toCleanup.AddRange(kvp.Value);
            tsnsToRemove.Add(kvp.Key);

            // Remove from reverse lookup
            foreach (var entry in kvp.Value)
            {
                _entityToBlockingTSN.Remove((entry.Table, entry.PrimaryKey));
            }
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
            _pendingCleanups.Remove(tsn);
        }
    }
}

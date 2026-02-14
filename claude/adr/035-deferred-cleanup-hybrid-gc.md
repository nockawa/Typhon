# ADR-035: Hybrid Deferred + Lazy MVCC Revision Cleanup

**Date:** 2026-02-14
**Status:** Accepted
**Issue:** #46 (Tier 3: MVCC Revision Leak Fix)

## Context

Typhon's MVCC model creates revision chains for each entity-component pair. The original GC strategy was **inline-only**: when the oldest active transaction (the "tail") completes, it cleans up all revisions with TSN below the new MinTSN.

This creates a **revision leak** when long-running transactions coexist with write activity:

```
Tail (TSN=95) is a long-running reader
Meanwhile, 1000 write transactions commit (TSN=96..1095)
Each commit adds revisions, but none can be cleaned:
  - Non-tail commits can't advance MinTSN
  - Tail hasn't completed yet
  - Revision chains grow unboundedly → memory/storage leak
```

The problem is well-known in MVCC databases (PostgreSQL calls this "transaction ID wraparound"; Typhon's variant is bounded by the tail transaction's lifetime rather than TXID space).

## Decision

Implement a **three-tier hybrid cleanup** strategy:

### Tier 1: Inline Cleanup (unchanged)
When the tail transaction completes, clean up all revision chains inline. This handles the common case where transactions are short-lived.

### Tier 2: Deferred Cleanup (new)
When a non-tail transaction commits and finds entities that need revision cleanup, **enqueue** the (table, primaryKey) pair into a `DeferredCleanupManager` keyed by the blocking tail's TSN. When the tail later completes, process all queued entries whose blockingTSN ≤ completedTSN.

### Tier 3: Lazy Cleanup (new)
During entity reads, if the revision chain exceeds a configurable threshold (`LazyCleanupThreshold`, default 8), attempt a non-blocking cleanup. If the chain lock is unavailable, skip (no blocking on reads).

## Key Design Choices

### Queue Structure: SortedDictionary keyed by blocking TSN

**Alternatives considered:**
- **ConcurrentQueue**: Simple but no range-query capability; processing completedTSN would require scanning all entries
- **ConcurrentDictionary**: No ordering; same scanning problem
- **SortedDictionary + explicit lock**: Efficient range queries via ordered iteration; break when key > completedTSN

**Chosen**: SortedDictionary with a dedicated AccessControl lock. The queue is mutated infrequently (once per non-tail commit batch) and the ordered structure enables O(k) processing where k = entries to clean, not total queue size.

### Dedup via Reverse Index

A reverse index `Dictionary<(ComponentTable, long), long>` maps each (table, pk) pair to its current blockingTSN. This ensures:
- Same entity appears at most once in the queue regardless of how many concurrent transactions update it
- If a newer commit finds the entity already queued under a higher TSN, it migrates it to the correct (lower) TSN bucket

### Batch Enqueue with Bounded Buffer

Transaction-local buffer collects cleanup entries during the commit loop, flushed in batches of 256 to the DeferredCleanupManager. This provides:
- **Lock reduction**: One exclusive lock per 256 entities instead of per-entity
- **Memory bounding**: Buffer never exceeds 256 entries (flushed mid-loop)
- **Zero GC alloc**: Buffer list is reused across commits via `Clear()`; initial capacity 16 grows to steady-state once

### Hoisted Tail Check

The tail check (shared lock on TransactionChain) is performed **once** before the commit loop, not per-entity. This reduces N shared lock operations to 1 for the entire commit. The result is stored in a `CommitContext` ref struct (zero-cost stack-only value).

### List Pooling for TSN Buckets

TSN bucket lists (`List<CleanupEntry>`) are pooled via a `Stack<List<CleanupEntry>>` (cap 16, under the same lock). This eliminates GC pressure from dictionary value allocation in the steady state.

### Lockless Early-Exit

`ProcessDeferredCleanups` checks `_pendingCleanups.Count == 0` **before** acquiring the exclusive lock. On x64, `SortedDictionary.Count` is a plain int field read (atomic, naturally aligned). A stale zero may cause a missed cleanup (caught on the next call); a stale non-zero acquires the lock and finds nothing (harmless).

## Consequences

### Positive
- **Bounded revision growth**: Even with long-running readers, revision chains are cleaned as soon as the blocking transaction completes
- **Minimal hot-path overhead**: Non-tail commits pay ~50-200ns per batch flush (amortized over 256 entities)
- **No regression**: Benchmark confirms no commit path latency regression (24.67ms vs 24.68ms baseline on CheckMultipleTreeBigAmount)
- **Lazy cleanup safety net**: Hot entities get cleaned during reads without waiting for tail completion

### Negative
- **Additional complexity**: DeferredCleanupManager is ~325 lines of new code with its own lock and data structures
- **Memory for the queue**: In worst case (very long tail + heavy write activity), the queue grows proportionally to unique entities modified — bounded by the reverse index dedup
- **Deferred cleanup is eventual**: Entities aren't cleaned instantly; cleanup happens when the blocking tail completes

### Neutral
- **Thread safety model**: DeferredCleanupManager uses its own AccessControl lock, independent of TransactionChain. The two locks are never held simultaneously (enqueue happens after TransactionChain shared lock is released)
- **Observability**: QueueSize, EnqueuedTotal, ProcessedTotal, LazyCleanupTotal, and LazyCleanupSkipped counters are exposed for monitoring

## Code Locations

- `src/Typhon.Engine/Data/DeferredCleanupManager.cs` — Queue, batch processing, list pooling
- `src/Typhon.Engine/Data/Transaction/Transaction.cs` — Batch collection in CommitComponent, hoisted tail check
- `src/Typhon.Engine/Data/ComponentRevision/ComponentRevisionManager.cs` — Lazy cleanup in read path

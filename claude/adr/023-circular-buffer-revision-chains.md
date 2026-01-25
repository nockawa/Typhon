# ADR-023: Circular Buffer for MVCC Revision Chains

**Status**: Accepted
**Date**: 2024-01 (project inception)
**Deciders**: Developer

## Context

MVCC requires storing multiple versions of each component. As transactions commit new revisions, the chain grows. Without cleanup, memory usage grows unbounded. The revision storage must:

1. Support fast append (new revision on commit)
2. Support fast lookup by timestamp (snapshot reads)
3. Allow garbage collection of old revisions no longer visible to any transaction
4. Minimize memory overhead per entity

## Decision

Store revisions in **circular buffer chains** (CompRevStorageHeader):

```
Chain Chunk (fixed-size, e.g., 8 slots):
┌─────────────────────────────────────────┐
│ Header: NextChunkId, FirstItemRevision, │
│         FirstItemIndex, ItemCount,       │
│         ChainLength, LastCommitRevIndex   │
├─────────────────────────────────────────┤
│ Slot 0: [ComponentChunkId, DateTime, Epoch, IsolationFlag] │
│ Slot 1: ...                                                 │
│ ...                                                         │
│ Slot 7: ...                                                 │
└─────────────────────────────────────────┘
         ↓ NextChunkId (if chain grows)
┌─────────────────────────────────────────┐
│ Next chunk (overflow)                    │
└─────────────────────────────────────────┘
```

- **Circular**: New revisions overwrite oldest slots when buffer is full
- **Chained**: Multiple chunks linked for entities with many concurrent revisions
- **GC trigger**: When oldest active transaction's tick > oldest revision's tick, that revision is safe to reclaim

Each revision element is now 12 bytes: `ComponentChunkId (4B) + DateTime (4B) + UowEpoch (2B) + Flags (2B)`.

## Alternatives Considered

1. **Linked list of revisions** — Simple append, but O(n) lookup by timestamp and poor cache locality (pointer chasing).
2. **Append-only log per entity** — Easy to write, but requires compaction and wastes space for entities with few updates.
3. **Copy-on-write tree** — Structural sharing, but complex and overkill for bounded revision counts.
4. **Fixed array (no overflow)** — Simpler, but hard limit on concurrent transactions viewing same entity.

## Consequences

**Positive:**
- Bounded memory per entity (circular naturally reclaims oldest)
- Fast append (write to next slot, increment counter)
- Fast GC (just advance FirstItemIndex, no deallocation)
- Chaining handles burst scenarios (many concurrent snapshots of same entity)
- Cache-friendly: sequential slots in same chunk

**Negative:**
- Old revisions lost when circular buffer wraps (fine — GC proves they're invisible)
- Chain traversal for deep history (multiple chunks if many concurrent snapshots)
- Fixed overhead per entity even if rarely updated (minimum one chunk allocated)
- ChainLength management: must track overflow chunks for cleanup

**Cross-references:**
- [04-data.md](../overview/04-data.md) §4.5 — Revision element structure
- [ADR-003](003-mvcc-snapshot-isolation.md) — Why multiple revisions exist
- [design/CompRevDeferredCleanup.md](../design/CompRevDeferredCleanup.md) — GC strategy

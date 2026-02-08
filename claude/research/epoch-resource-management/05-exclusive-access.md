# Exclusive Access Model — Decoupled from Lifetime

**Parent:** [README.md](./README.md)
**Prerequisite:** [02 — Typhon Epoch System](./02-typhon-epoch-system.md)

---

## The Core Problem

Epochs solve the **lifetime** problem (when can a page cache slot be reused?) but not the **exclusivity** problem (how do I ensure I'm the only writer?).

Currently, these two concerns are entangled in `ConcurrentSharedCounter`:
- Counter > 0 → page cannot be evicted (lifetime)
- Counter == 1 + same thread → can promote to exclusive (exclusivity)

We must **decouple** them: epochs handle lifetime, a separate mechanism handles exclusivity.

---

## Insight: Lifetime and Exclusivity Are Orthogonal

```
┌──────────────────────────────────────────────────────────────┐
│                    TWO INDEPENDENT CONCERNS                  │
│                                                              │
│  LIFETIME (Cache Residency)        EXCLUSIVITY (Access Mode) │
│  ─────────────────────────         ───────────────────────── │
│  "How long does this page          "Can I write to this page │
│   stay in the cache?"               without interference?"   │
│                                                              │
│  Managed by: EPOCH SYSTEM          Managed by: ACCESS LATCH  │
│                                                              │
│  Granularity: coarse               Granularity: precise      │
│  (per-scope, batch release)        (per-operation, explicit) │
│                                                              │
│  Failure mode: delayed eviction    Failure mode: contention  │
│  (soft, self-correcting)           (timeout, retry)          │
│                                                              │
│  Thread interaction: none          Thread interaction: yes   │
│  (no blocking between threads)     (writers block readers)   │
└──────────────────────────────────────────────────────────────┘
```

---

## The Access Latch

Each page gets a lightweight reader-writer latch for access control. This is **separate from the epoch system**.

### Structure

Use the existing `AccessControlSmall` (4 bytes) already present in Typhon:

```
AccessControlSmall per PageInfo:
  Bits 0-14:   Shared reader count (0 = no readers)
  Bit 15:      Contention flag
  Bits 16-31:  Exclusive owner thread ID (0 = no exclusive holder)
```

### But Wait — Doesn't a Shared Reader Count Bring Back Ref Counting?

**No**, for a critical reason: **the latch's reader count is for mutual exclusion, not for lifetime management.**

| Concern | Purpose of counter | What happens if you "forget" |
|---------|-------------------|------------------------------|
| **Lifetime (old model)** | Prevent eviction | Page cache exhaustion → crash |
| **Exclusivity (new model)** | Prevent concurrent writers | Writer can't acquire exclusive → timeout, retry |

In the old model, a leaked ref count means the page is **permanently non-evictable** — a resource leak that accumulates and eventually crashes the engine.

In the new model, a leaked latch reader count means exclusive writers **can't acquire the page** — a liveness issue, not a safety issue. The epoch system still handles eviction correctly regardless of the latch state.

### Wait, Do We Even Need a Shared Reader Latch?

For **most read operations**, the answer is **no**. Here's why:

Typhon uses MVCC. Readers see a snapshot — they read old revisions, not the live data being modified by writers. B+Tree reads traverse immutable-from-their-perspective node states. The only time a reader and writer conflict on the SAME page is during B+Tree structural modifications (splits/merges), where the tree's physical structure changes.

For B+Tree SMOs (Structural Modifications):
- The current B+Tree uses **latch-coupling**: acquire child latch, release parent latch, descend
- This is already an exclusive-only pattern at the node level
- Readers that encounter a node being split can **retry** (optimistic approach)

**Proposed simplification: Use only exclusive latches, no shared reader latch.**

```
Page Access Modes:
  1. UNLATCHED READ (default): No latch acquired. Epoch protects lifetime.
     Used by: All MVCC reads, B+Tree traversals, revision chain walks.
     Cost: Zero synchronization overhead.

  2. EXCLUSIVE WRITE: Acquire exclusive latch. Epoch protects lifetime.
     Used by: B+Tree splits/merges, segment growth, metadata updates.
     Cost: One CAS operation to acquire, one write to release.
```

This eliminates the reader-side latch entirely for the common case.

---

## When Is Exclusive Access Needed?

| Operation | Needs Exclusive? | Why |
|-----------|-----------------|-----|
| B+Tree lookup | No | MVCC snapshot, reads immutable revisions |
| B+Tree insert (no split) | No | New revision in new chunk, no in-place modification |
| B+Tree split/merge | **Yes** | Modifies node structure in-place |
| CRUD read | No | MVCC snapshot |
| CRUD create | No | Allocates new chunks |
| CRUD update | No | Creates new revision (copy-on-write) |
| CRUD delete | No | Marks revision as deleted |
| Segment growth | **Yes** | Modifies segment metadata (bitmap pages) |
| Page 0 metadata | **Yes** | System-wide metadata update |

Exclusive access is **rare** — only structural modifications require it.

---

## Exclusive Access Protocol

### Acquiring Exclusive Access

```
AcquireExclusive(PageInfo page):
  1. Attempt CAS: page.ExclusiveOwnerThreadId = 0 → currentThreadId
     (atomic compare-and-swap, if current value is 0, set to our thread ID)

  2. If CAS succeeds:
     → We have exclusive access
     → Set page.AccessEpoch = max(page.AccessEpoch, GlobalEpoch)
     → Proceed with modification

  3. If CAS fails:
     → Another thread holds exclusive access
     → Spin briefly (AdaptiveWaiter), then retry
     → If timeout: return failure (caller retries at higher level)
```

### Releasing Exclusive Access

```
ReleaseExclusive(PageInfo page):
  1. page.ExclusiveOwnerThreadId = 0  (plain write, x64 atomic)
  → Exclusive access released
  → Page remains in cache (epoch protection unchanged)
```

### Reentrant Exclusive Access

```
AcquireExclusive(PageInfo page):
  If page.ExclusiveOwnerThreadId == currentThreadId:
    → Already own exclusive, just increment a depth counter
    → Return success

ReleaseExclusive(PageInfo page):
  Decrement depth counter
  If depth == 0:
    → page.ExclusiveOwnerThreadId = 0
```

This handles the case where a function acquires exclusive on a page, then calls a sub-function that also needs exclusive on the same page (e.g., B+Tree operations).

---

## How Exclusive Interacts with Unlatched Reads

### The Concern

If Thread A is reading a page (unlatched) and Thread B starts writing (exclusive), could Thread A see a torn/inconsistent read?

### Why This Is Safe in Typhon

1. **MVCC**: Readers and writers operate on different revisions. A reader accessing revision R5 is reading data in chunks that the writer isn't modifying (the writer creates revision R6 in new chunks).

2. **B+Tree structural safety**: During a split, the writer modifies the node's internal structure. But MVCC means the reader is traversing the tree at a specific snapshot TSN, following pointers that are valid for that snapshot. The split creates new pointers for the new TSN.

3. **Copy-on-write semantics**: Updates create new component chunks, not modify existing ones. The old chunks remain valid and readable.

The only scenario where a torn read could occur is if two threads modify the SAME B+Tree node simultaneously. But B+Tree modifications already use exclusive latches (the latch-coupling protocol), so this is prevented.

### Validation Pattern (Optional Extra Safety)

For operations where torn reads are a concern (e.g., reading a B+Tree node that might be splitting), use a **sequence counter**:

```
Before modification:
  node.SequenceCounter++  (odd = modification in progress)

Perform modification:
  ... modify node data ...

After modification:
  node.SequenceCounter++  (even = modification complete)

Reader pattern:
  seq1 = node.SequenceCounter
  if (seq1 & 1) != 0: retry  // modification in progress
  ... read node data ...
  seq2 = node.SequenceCounter
  if (seq1 != seq2): retry   // data changed during read
```

This is the **seqlock** pattern, used in Linux for `gettimeofday()` and similar. It adds ~2 cycles to the read path (two integer reads and a branch) and zero overhead to the writer path (two integer writes that are needed anyway for version tracking).

**This is optional** — the existing MVCC + latch-coupling may already provide sufficient safety. Include it only if analysis shows torn reads are possible in specific B+Tree scenarios.

---

## Exclusive Access at the ChunkAccessor Level

### Before: TryPromoteChunk / DemoteChunk

The current ChunkAccessor manages promotion per-slot with a `PromoteCounter`:

```
Before:
  accessor.TryPromoteChunk(chunkId)   → PromoteCounter++, PageAccessor.TryPromoteToExclusive()
  ... modify chunk ...
  accessor.DemoteChunk(chunkId)       → PromoteCounter--, PageAccessor.DemoteExclusive()

  Problems:
  - Must match exactly (ref-counting again!)
  - PromoteCounter leak blocks slot eviction
  - Multiple promotions of same page require careful tracking
```

### After: Direct Page Latch

```
After:
  mmf.AcquireExclusive(pageIndex)     → CAS on page's ExclusiveOwnerThreadId
  ... modify chunk (accessed via accessor or directly) ...
  mmf.ReleaseExclusive(pageIndex)     → Clear ExclusiveOwnerThreadId

  Advantages:
  - No per-slot tracking in ChunkAccessor
  - No PromoteCounter
  - ChunkAccessor doesn't need to know about exclusive access at all
  - Exclusive access is orthogonal to chunk caching
```

The ChunkAccessor becomes a pure read cache. When exclusive access is needed, the caller interacts directly with the page cache latch, not through the ChunkAccessor.

---

## Exclusive Access and Eviction

A page with `ExclusiveOwnerThreadId != 0` cannot be evicted, regardless of its AccessEpoch. This is a hard rule in the eviction predicate:

```
CanEvict(page):
  return page.AccessEpoch < MinActiveEpoch
         && page.ExclusiveOwnerThreadId == 0    // ← cannot evict while exclusively held
         && page.DirtyCounter == 0
```

This is analogous to the current `PageState != Exclusive` check. Exclusive holds are always short (microseconds for B+Tree splits), so this doesn't create eviction pressure.

---

## Comparison with Current Model

| Aspect | Before (Ref-Counted) | After (Epoch + Latch) |
|--------|---------------------|----------------------|
| Shared read overhead | Lock + increment + decrement + unlock (~25-60ns) | Epoch tag update (~1-5ns) |
| Exclusive acquire | Lock + check counter==1 + state transition (~20-40ns) | CAS on thread ID (~5-10ns) |
| Exclusive release | Lock + decrement + state transition (~15-25ns) | Plain write (~1ns) |
| Shared+Exclusive coupling | Tightly coupled in ConcurrentSharedCounter | Fully decoupled |
| Promotion from shared | Complex (counter must be 1, same thread) | Simple CAS (just acquire exclusive) |
| ChunkAccessor involvement | Manages PromoteCounter per slot | None — exclusive is page-level |
| Failure mode | Counter mismatch → leak/crash | CAS timeout → retry (self-healing) |

---

## Summary

1. **Exclusive access uses a simple CAS-based latch** on each page's `ExclusiveOwnerThreadId`
2. **No shared reader latch needed** — MVCC and copy-on-write provide read safety
3. **Completely decoupled from ChunkAccessor** — exclusive is a page-level concern
4. **Reentrant** via depth counting for same-thread nesting
5. **Eviction respects exclusive** — pages can't be evicted while exclusively held
6. **Optional seqlock** for extra safety on structural modifications

---

**Next:** [06 — Operations Walkthrough](./06-operations-walkthrough.md) — Step-by-step examples.

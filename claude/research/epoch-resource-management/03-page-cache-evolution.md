# Page Cache Evolution — Minimal Changes

**Parent:** [README.md](./README.md)
**Prerequisite:** [02 — Typhon Epoch System](./02-typhon-epoch-system.md)

---

## Philosophy

The page cache (PagedMMF) is well-encapsulated and its current complexity is manageable. We make **targeted, minimal changes** — replacing the eviction predicate and simplifying PageAccessor — without redesigning the cache architecture.

---

## Current PageInfo State (Before)

```csharp
class PageInfo {
    int MemPageIndex;                    // Fixed: index in cache array
    int FilePageIndex;                   // Which file page is cached (-1 if free)
    PageState PageState;                 // Free/Allocating/Shared/Exclusive/Idle/IdleAndDirty
    int LockedByThreadId;                // Thread holding exclusive access
    int ConcurrentSharedCounter;         // ← REMOVED: ref count for shared readers
    int DirtyCounter;                    // Pending write-backs
    int ClockSweepCounter;               // 0-5, eviction priority
    AccessControlSmall StateSyncRoot;    // Per-page state lock
    Lazy<Task<int>> IOReadTask;          // Pending disk read
}
```

## New PageInfo State (After)

```csharp
class PageInfo {
    int MemPageIndex;                    // Unchanged
    int FilePageIndex;                   // Unchanged
    PageState PageState;                 // SIMPLIFIED (see below)
    int ExclusiveOwnerThreadId;          // Renamed for clarity (0 = no exclusive)
    long AccessEpoch;                    // ← NEW: epoch when last accessed
    int DirtyCounter;                    // Unchanged
    int ClockSweepCounter;               // Unchanged
    AccessControlSmall AccessLatch;      // REPURPOSED: shared/exclusive access latch
    Lazy<Task<int>> IOReadTask;          // Unchanged
}
```

### What Changed

| Field | Before | After | Reason |
|-------|--------|-------|--------|
| `ConcurrentSharedCounter` | Ref count (int) | **Removed** | Replaced by epoch protection |
| `LockedByThreadId` | Multi-purpose (shared + exclusive) | `ExclusiveOwnerThreadId` (exclusive only) | Shared access no longer tracked per-thread |
| `StateSyncRoot` | State transition lock | `AccessLatch` | Repurposed for shared/exclusive access control |
| `AccessEpoch` | N/A | **New** (long) | Tags page with access epoch for eviction |

---

## Simplified Page State Machine

### Before: 6 States

```
Free → Allocating → Shared ⇄ Exclusive → Idle/IdleAndDirty → Free
                     ↑                      │
                     └──────────────────────┘
```

- `Shared` and `Exclusive` were the "active" states, gated by `ConcurrentSharedCounter`
- Transition from `Shared`/`Exclusive` → `Idle` required counter reaching 0

### After: 4 States

```
Free → Allocating → Active → Idle/IdleAndDirty → Free
                      ↑            │
                      └────────────┘
```

- `Shared` and `Exclusive` merge into `Active` (the page is in cache and accessible)
- Exclusive access is managed by the `AccessLatch`, not the page state
- Transition to `Idle` happens when `AccessEpoch < MinActiveEpoch`

```
enum PageState : ushort {
    Free         = 0,   // Not allocated to any file page
    Allocating   = 1,   // Being loaded from disk
    Active       = 2,   // In cache, accessible (may be shared or exclusive via latch)
    IdleAndDirty = 3,   // Has unflushed writes, not evictable until flushed
}
```

**Why no `Idle` state?** The distinction between `Active` and `Idle` was based on the ref count reaching 0. With epochs, there's no ref count. A page in cache is either `Active` (loaded, accessible) or `IdleAndDirty` (needs flush before eviction). The eviction predicate uses `AccessEpoch` instead of state to determine evictability.

Actually, we can keep `Idle` as a soft state for the clock-sweep to quickly identify candidates:

```
enum PageState : ushort {
    Free         = 0,   // Not allocated
    Allocating   = 1,   // Loading from disk
    Active       = 2,   // Recently accessed (AccessEpoch ≥ MinActiveEpoch)
    Idle         = 3,   // In cache, AccessEpoch < MinActiveEpoch, evictable
    IdleAndDirty = 4,   // Needs flush before eviction
}
```

The `Active → Idle` transition can be lazy: the clock-sweep sets pages to `Idle` when it detects `AccessEpoch < MinActiveEpoch`. Or we skip `Idle` entirely and just check the epoch directly. Either works — the epoch check is the source of truth.

---

## Eviction Predicate Change

### Before

```
CanEvict(page):
  return page.PageState == PageState.Idle
         // which means ConcurrentSharedCounter == 0 && DirtyCounter == 0
```

### After

```
CanEvict(page):
  return page.AccessEpoch < CachedMinActiveEpoch
         && page.ExclusiveOwnerThreadId == 0
         && page.DirtyCounter == 0
```

This check is **the same cost** as before: three field reads and two comparisons. No performance change.

---

## Clock-Sweep Algorithm Changes

The clock-sweep algorithm remains fundamentally the same. The only change is in `TryAcquire` (the eviction attempt):

### Before (PagedMMF.cs:681-731)

```
TryAcquire(PageInfo):
  Quick check: state must be Free or Idle
  Under lock:
    Re-check state is Free or Idle
    If Idle: remove from cache directory
    Set state to Allocating
    Return true
```

### After

```
TryAcquire(PageInfo):
  Quick check:
    state must be Free
    OR (AccessEpoch < CachedMinActiveEpoch
        && ExclusiveOwnerThreadId == 0
        && DirtyCounter == 0)
  Under lock:
    Re-check conditions
    If evictable: remove from cache directory
    Set state to Allocating, reset AccessEpoch
    Return true
```

The three-phase clock-sweep (sequential optimization → sweep → emergency) is unchanged.

---

## PageAccessor Changes

### Before: Dispose Is Critical

```
PageAccessor (before):
  Construction: increments ConcurrentSharedCounter
  Dispose(): decrements ConcurrentSharedCounter → transitions to Idle
  TryPromoteToExclusive(): changes state Shared → Exclusive
  DemoteExclusive(): changes state Exclusive → Shared

  MUST dispose to avoid ref count leak.
```

### After: Dispose Is Optional for Shared Access

```
PageAccessor (after):
  Construction: no counter increment; just stores pointer + PageInfo ref
  Dispose(): releases exclusive latch if held; otherwise no-op
  TryPromoteToExclusive(): acquires exclusive on AccessLatch
  DemoteExclusive(): releases exclusive on AccessLatch

  Shared access: Dispose is a no-op. Safe to skip.
  Exclusive access: Dispose releases the latch. Still must be called.
```

**Key insight**: For shared (read-only) page access, PageAccessor becomes a **trivial wrapper** — it just holds a pointer. No constructor overhead, no disposal obligation. The epoch protects the page.

For exclusive access, the PageAccessor acquires the `AccessLatch` in exclusive mode and must release it on Dispose. This is the same obligation as today, but only for the exclusive path (which is rare — only B+Tree splits and structural modifications).

### Impact on Callers

```
Before (every page access):
  using var pa = mmf.RequestPage(pageIndex, shared);
  // MUST dispose, or page leaks

After (shared access):
  var pa = mmf.RequestPage(pageIndex);
  // No using needed for shared. Epoch handles lifetime.

After (exclusive access):
  using var pa = mmf.RequestPage(pageIndex, exclusive: true);
  // MUST dispose to release exclusive latch (same as today)
```

---

## Page Access Flow — Shared

### Before

```
RequestPage(filePageIndex, exclusive=false):
  1. FetchPageToMemory (load if not cached)
  2. TransitionPageToAccess:
     a. Lock StateSyncRoot
     b. Increment ConcurrentSharedCounter
     c. Set state to Shared
     d. Set LockedByThreadId
     e. Increment ClockSweepCounter
     f. Unlock StateSyncRoot
  3. Return PageAccessor (which will decrement on Dispose)
```

### After

```
RequestPage(filePageIndex):
  1. FetchPageToMemory (load if not cached — unchanged)
  2. Tag page with epoch:
     a. page.AccessEpoch = max(page.AccessEpoch, GlobalEpoch)  // atomic update
     b. Increment ClockSweepCounter  // unchanged
  3. Return PageAccessor (lightweight — no dispose obligation)
```

**Eliminated**: StateSyncRoot lock acquisition for shared access, counter increment, counter decrement, state transition.

**Cost comparison** (shared access):

| Step | Before | After |
|------|--------|-------|
| Lock acquisition | ~5-20ns (AccessControlSmall) | None |
| Counter increment | ~1ns (under lock) | None |
| Epoch tag update | None | ~1ns (Interlocked.Max or CAS) |
| ClockSweep increment | ~5-10ns (CAS) | ~5-10ns (CAS) — unchanged |
| Lock release | ~3-5ns | None |
| **On dispose** | **~10-25ns** (lock + decrement + state transition) | **None** (no-op) |
| **Total overhead** | **~25-60ns per access** | **~6-11ns per access** |

The shared access hot path gets **2-5x faster** by eliminating the lock acquire/release cycle.

---

## Page Access Flow — Exclusive

### Before

```
TryPromoteToExclusive(pi):
  1. Lock StateSyncRoot
  2. Check LockedByThreadId == currentThread
  3. Check ConcurrentSharedCounter == 1 (only me)
  4. Increment ConcurrentSharedCounter
  5. Set state to Exclusive
  6. Unlock StateSyncRoot

DemoteExclusive(pi):
  1. Lock StateSyncRoot
  2. Decrement ConcurrentSharedCounter
  3. Set state to previousMode
  4. Unlock StateSyncRoot
```

### After

```
AcquireExclusive(pi):
  1. AccessLatch.EnterExclusiveAccess()
     (blocks until all shared readers exit the latch)
  2. Set ExclusiveOwnerThreadId = currentThread
  3. Epoch tag update (same as shared)

ReleaseExclusive(pi):
  1. ExclusiveOwnerThreadId = 0
  2. AccessLatch.ExitExclusiveAccess()
```

See [05 — Exclusive Access Model](./05-exclusive-access.md) for the full design.

---

## Dirty Page Handling — Unchanged

The dirty tracking mechanism (`DirtyCounter`, `IncrementDirty`, `DecrementDirty`, `IdleAndDirty` state) is **completely unchanged**. It operates independently of how page lifetime is managed.

```
Before: Dirty page → IdleAndDirty → flush → Idle → evictable
After:  Dirty page → IdleAndDirty → flush → evictable (via epoch check)
```

---

## Summary of PagedMMF Changes

| Aspect | Change Level | Description |
|--------|-------------|-------------|
| PageInfo fields | Minor | Remove `ConcurrentSharedCounter`, add `AccessEpoch`, rename thread ID field |
| PageState enum | Minor | Simplify states (optional — can keep existing if easier) |
| Eviction predicate | Minor | Replace counter check with epoch check |
| Clock-sweep | Minimal | Change `TryAcquire` condition |
| Shared access path | Simplify | Remove lock acquire/release cycle |
| Exclusive access path | Restructure | Decouple to latch (see doc 05) |
| Dirty tracking | None | Unchanged |
| Sequential allocation | None | Unchanged |
| Async I/O | None | Unchanged |
| Cache sizing | None | Unchanged |
| Metrics | Minor | Remove shared counter metrics, add epoch metrics |

**Estimated lines of change in PagedMMF**: ~100-150 lines modified, ~50 lines removed, ~20 lines added.

---

**Next:** [04 — ChunkAccessor Redesign](./04-chunk-accessor-redesign.md) — The major simplification.

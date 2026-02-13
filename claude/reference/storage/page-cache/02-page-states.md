# Page States and Lifecycle

This document describes the page state machine that governs how pages transition between different access modes in the cache.

## State Enum

```csharp
internal enum PageState : ushort
{
    Free         = 0,   // Page not yet allocated to any file page
    Allocating   = 1,   // Being assigned by AllocateMemoryPage
    Idle         = 2,   // Allocated but not in active exclusive use; protected from eviction by epoch tag and/or DirtyCounter > 0
    Exclusive    = 4,   // Write access — single thread holds PageExclusiveLatch
}
```

> **Note**: The `Shared` and `IdleAndDirty` states were removed in the epoch-based resource management migration (#69). Read-access protection is now handled by epoch tags (`AccessEpoch`), and dirty tracking is orthogonal to page state via `DirtyCounter`.

## Key PageInfo Properties

Each memory page slot is tracked by a `PageInfo` instance with these state-related properties:

| Property | Type | Description |
|----------|------|-------------|
| `MemPageIndex` | `int` | Immutable index into the memory cache array (0 to N-1) |
| `FilePageIndex` | `int` | Which file page is cached here (-1 if unmapped) |
| `PageState` | `PageState` | Current state in the lifecycle (see enum above) |
| `PageExclusiveLatch` | `AccessControlSmall` | Thread ownership for exclusive latch (replaces old `LockedByThreadId`) |
| `ExclusiveLatchDepth` | `short` | Re-entrance depth for exclusive latching (multiple chunks on same page) |
| `AccessEpoch` | `long` | Epoch tag when page was last accessed; pages with `AccessEpoch >= MinActiveEpoch` cannot be evicted |
| `DirtyCounter` | `int` | Pending write-backs; >0 prevents eviction |
| `ClockSweepCounter` | `int` | Eviction priority (0-5); higher = more recently used |
| `StateSyncRoot` | `AccessControlSmall` | 4-byte lock protecting state transitions |

## State Machine Diagram

```
                    TryAcquire()         AllocateMemoryPageCore()
    ┌────────┐  ─────────────────►┌────────────┐
    │  Free  │                    │ Allocating │
    └────────┘                    └─────┬──────┘
         ▲                              │
         │                     RequestPageEpoch()
         │                     (sets AccessEpoch)
         │                              │
         │                              ▼
         │                        ┌───────────┐
         │                        │    Idle   │◄──── UnlatchPageExclusive()
         │                        └─────┬─────┘      (resets AccessEpoch=0)
         │                              │
         │              TryLatchPageExclusive()
         │                              │
         │                              ▼
         │                        ┌────────────┐
         │                        │  Exclusive │
         │                        └─────┬──────┘
         │                              │
         │       (eviction, when Idle   │
         │        + AccessEpoch < Min   │
         │        + DirtyCounter == 0)  │
         └──────────────────────────────┘
```

## State Descriptions

### Free (0)

**Meaning**: Memory page slot has never been used or was fully evicted.

**Properties**:
- `FilePageIndex = -1`
- `ClockSweepCounter = 0`

**Transitions**:
- → `Allocating`: When `TryAcquire()` claims this slot for a file page

### Allocating (1)

**Meaning**: Memory page is being assigned to a file page. Brief transitional state.

**Properties**:
- `FilePageIndex` being set
- May have pending I/O read task

**Transitions**:
- → `Idle`: When `RequestPageEpoch()` completes (I/O finished, epoch tag set)

### Idle (2)

**Meaning**: Page is loaded in cache but not currently under exclusive access. May or may not be evictable depending on epoch tag and dirty state.

**Eviction eligibility**: `AccessEpoch < MinActiveEpoch AND DirtyCounter == 0`

**Properties**:
- `FilePageIndex` is valid (page is cached)
- `AccessEpoch` may be set (epoch-protected) or 0 (unprotected)
- `DirtyCounter` may be > 0 (dirty, not evictable)

**Transitions**:
- → `Exclusive`: When `TryLatchPageExclusive()` succeeds
- → `Allocating`: When evicted and reassigned to a different file page (only if evictable)

### Exclusive (4)

**Meaning**: Page has exclusive (write) access by one thread via `PageExclusiveLatch`.

**Properties**:
- `PageExclusiveLatch.IsLockedByCurrentThread` is true for the owning thread
- `ExclusiveLatchDepth >= 0` (tracks re-entrant access from same thread)

**Transitions**:
- → `Exclusive` (stay): Re-entrant latch from same thread (`ExclusiveLatchDepth++`)
- → `Idle`: When `UnlatchPageExclusive()` releases (depth reaches 0); `AccessEpoch` reset to 0

## Transition Rules

### Entering Exclusive Access

`TryLatchPageExclusive()` acquires exclusive access:

```csharp
internal bool TryLatchPageExclusive(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];

    // Re-entrant: already latched by this thread
    if (pi.PageExclusiveLatch.IsLockedByCurrentThread)
    {
        pi.ExclusiveLatchDepth++;
        return true;
    }

    // New acquisition: check page state under StateSyncRoot
    if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wc))
        ThrowHelper.ThrowLockTimeout(...);

    try
    {
        if (pi.PageState != PageState.Idle)
            return false;
        pi.PageState = PageState.Exclusive;
    }
    finally
    {
        pi.StateSyncRoot.ExitExclusiveAccess();
    }

    // Acquire the latch (records thread ownership)
    pi.PageExclusiveLatch.EnterExclusiveAccess(ref WaitContext.Null);
    pi.ExclusiveLatchDepth = 0;
    return true;
}
```

### Exiting Exclusive Access

`UnlatchPageExclusive()` releases the latch:

```csharp
internal void UnlatchPageExclusive(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];

    // Re-entrant depth: just decrement
    if (pi.ExclusiveLatchDepth > 0)
    {
        pi.ExclusiveLatchDepth--;
        return;
    }

    // Release latch and transition back to Idle
    pi.PageExclusiveLatch.ExitExclusiveAccess();
    pi.AccessEpoch = 0;  // No longer epoch-protected after unlatch

    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    pi.PageState = PageState.Idle;
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

## Epoch-Based Protection

Instead of per-page reference counting (`ConcurrentSharedCounter`), pages are protected from eviction by epoch tags:

1. **Enter scope**: `EpochGuard.Enter(epochManager)` pins the current thread at the global epoch
2. **Access page**: `RequestPageEpoch(pageIndex, epoch, out memPageIndex)` tags the page with `AccessEpoch = epoch`
3. **Exit scope**: `guard.Dispose()` unpins the thread; the global epoch may advance
4. **Eviction check**: A page is only evictable when `AccessEpoch < MinActiveEpoch` (no active thread is in a scope that could reference it)

This replaces the old `Shared` state — multiple readers simply access Idle pages through epoch-protected raw pointers without changing page state.

## Dirty Page Tracking

Dirty tracking is **orthogonal** to page state. A page in `Idle` state can have `DirtyCounter > 0`:

### IncrementDirty

Called when a page is marked for write-back via `ChangeSet.AddByMemPageIndex()`:

```csharp
internal void IncrementDirty(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    ++pi.DirtyCounter;
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

### DecrementDirty

Called after page is written to disk (in `SavePages` continuation):

```csharp
internal void DecrementDirty(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    --pi.DirtyCounter;
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

> **Note**: Unlike the old model, `DecrementDirty` no longer transitions state. Dirty pages stay `Idle` — they're simply non-evictable while `DirtyCounter > 0`.

## Eviction Eligibility

A page can only be evicted (via clock-sweep) if:

| State | Evictable? | Reason |
|-------|------------|--------|
| Free | Yes (trivially) | Already free |
| Allocating | No | In use |
| Idle (epoch-protected) | No | `AccessEpoch >= MinActiveEpoch` |
| Idle (dirty) | No | `DirtyCounter > 0` |
| Idle (clean, stale epoch) | Yes | `AccessEpoch < MinActiveEpoch AND DirtyCounter == 0` |
| Exclusive | No | Active writer |

```csharp
// Eviction predicate (simplified from TryAcquire)
bool evictable = (info.PageState == PageState.Free) ||
    (info.PageState == PageState.Idle &&
     info.AccessEpoch < minActiveEpoch &&
     info.DirtyCounter == 0);
```

## PageInfo Structure

Each memory page slot has associated metadata:

```csharp
internal class PageInfo
{
    // Identity (immutable)
    public readonly int MemPageIndex;

    // Current mapping
    public int FilePageIndex;           // -1 if not mapped

    // State machine
    public PageState PageState;
    public AccessControlSmall StateSyncRoot;  // 4-byte lock protecting state transitions

    // Exclusive latching
    public AccessControlSmall PageExclusiveLatch;  // Thread ownership via built-in LockedByThreadId
    public short ExclusiveLatchDepth;              // Re-entrance depth

    // Epoch protection
    public long AccessEpoch;            // Epoch tag; >= MinActiveEpoch prevents eviction

    // Dirty tracking
    public int DirtyCounter;            // Pending write-backs; > 0 prevents eviction

    // Eviction priority
    private int _clockSweepCounter;     // 0-5, higher = more recently used

    // Async I/O
    private Lazy<Task<int>> _ioReadTask;
}
```

## Thread Safety

All state transitions are protected by `PageInfo.StateSyncRoot`:

```csharp
// Always acquire lock before modifying state
pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
try
{
    // Modify state atomically
    pi.PageState = newState;
}
finally
{
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

Epoch tagging (`AccessEpoch`) uses lock-free atomic-max via `Interlocked.CompareExchange` — no lock needed since epoch values only increase.

## Summary

| State | Active Access | Dirty | Evictable | Typical Duration |
|-------|---------------|-------|-----------|------------------|
| Free | No | No | Yes | Until first use |
| Allocating | Transitioning | No | No | Microseconds |
| Idle | Epoch-protected reads | Maybe | Only if epoch stale + clean | Variable |
| Exclusive | Write (single thread) | Maybe | No | Duration of write operation |

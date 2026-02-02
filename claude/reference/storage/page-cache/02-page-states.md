# Page States and Lifecycle

This document describes the page state machine that governs how pages transition between different access modes in the cache.

## State Enum

```csharp
internal enum PageState : ushort
{
    Free         = 0,   // Page not yet allocated to any file page
    Allocating   = 1,   // Being assigned by AllocateMemoryPage
    Idle         = 2,   // Allocated but not in use, can be evicted
    Shared       = 3,   // Read access (one or more concurrent threads)
    Exclusive    = 4,   // Write access (single thread only)
    IdleAndDirty = 5,   // Released but needs disk flush
}
```

## Key PageInfo Properties

Each memory page slot is tracked by a `PageInfo` instance with these state-related properties:

| Property | Type | Description |
|----------|------|-------------|
| `MemPageIndex` | `int` | Immutable index into the memory cache array (0 to N-1) |
| `FilePageIndex` | `int` | Which file page is cached here (-1 if unmapped) |
| `PageState` | `PageState` | Current state in the lifecycle (see enum above) |
| `LockedByThreadId` | `int` | Thread ID holding access (0 if none or multiple readers) |
| `ConcurrentSharedCounter` | `int` | Number of active accessors (shared or re-entrant exclusive) |
| `DirtyCounter` | `int` | Pending write-backs; >0 prevents eviction |
| `ClockSweepCounter` | `int` | Eviction priority (0-5); higher = more recently used |
| `StateSyncRoot` | `AccessControlSmall` | 4-byte reader-writer lock protecting state transitions |

## State Machine Diagram

```
                         ┌────────────────────────────────────────────┐
                         │                                            │
                         ▼                                            │
    ┌────────┐    TryAcquire()    ┌────────────┐                      │
    │  Free  │───────────────────►│ Allocating │                      │
    └────────┘                    └─────┬──────┘                      │
         ▲                              │                             │
         │                    TransitionPageToAccess()                │
         │                              │                             │
         │              ┌───────────────┴───────────────┐             │
         │              ▼                               ▼             │
         │        ┌──────────┐                  ┌───────────┐         │
         │        │  Shared  │◄────────────────►│ Exclusive │         │
         │        └────┬─────┘    promote/      └─────┬─────┘         │
         │             │          demote              │               │
         │             │                              │               │
         │             └──────────┬───────────────────┘               │
         │                        │                                   │
         │          TransitionPageFromAccessToIdle()                  │
         │                        │                                   │
         │              ┌─────────┴─────────┐                         │
         │              ▼                   ▼                         │
         │        ┌──────────┐       ┌──────────────┐                 │
         │        │   Idle   │       │ IdleAndDirty │                 │
         │        └────┬─────┘       └──────┬───────┘                 │
         │             │                    │                         │
         │   (eviction)│     DecrementDirty()                         │
         │             │     (DirtyCounter=0)                         │
         └─────────────┴────────────────────┴─────────────────────────┘
```

## State Descriptions

### Free (0)

**Meaning**: Memory page slot has never been used or was fully evicted.

**Properties**:
- `FilePageIndex = -1`
- `ConcurrentSharedCounter = 0`
- `LockedByThreadId = 0`
- `ClockSweepCounter = 0`

**Transitions**:
- → `Allocating`: When `TryAcquire()` claims this slot for a file page

### Allocating (1)

**Meaning**: Memory page is being assigned to a file page. Brief transitional state.

**Properties**:
- `FilePageIndex` being set
- May have pending I/O read task

**Transitions**:
- → `Shared`: When `TransitionPageToAccess(exclusive: false)` succeeds
- → `Exclusive`: When `TransitionPageToAccess(exclusive: true)` succeeds

### Idle (2)

**Meaning**: Page is loaded in cache but not currently accessed. **Can be evicted**.

**Properties**:
- `FilePageIndex` is valid (page is cached)
- `ConcurrentSharedCounter = 0`
- `DirtyCounter = 0` (no pending writes)

**Transitions**:
- → `Shared`: When `TransitionPageToAccess(exclusive: false)` succeeds
- → `Exclusive`: When `TransitionPageToAccess(exclusive: true)` succeeds
- → `Allocating`: When evicted and reassigned to different file page

### Shared (3)

**Meaning**: Page has one or more concurrent readers.

**Properties**:
- `ConcurrentSharedCounter >= 1`
- If counter = 1: `LockedByThreadId` = owner thread
- If counter > 1: `LockedByThreadId = 0` (multiple threads)

**Transitions**:
- → `Shared` (stay): Additional shared access (counter++)
- → `Exclusive`: Promotion when single reader on same thread
- → `Idle` or `IdleAndDirty`: When last reader exits

### Exclusive (4)

**Meaning**: Page has exclusive (write) access by one thread.

**Properties**:
- `ConcurrentSharedCounter >= 1` (counts re-entrant access)
- `LockedByThreadId` = owning thread

**Transitions**:
- → `Exclusive` (stay): Re-entrant access from same thread (counter++)
- → `Shared`: Demotion (explicit or via accessor disposal)
- → `Idle` or `IdleAndDirty`: When all access released

### IdleAndDirty (5)

**Meaning**: Page was modified but not yet flushed to disk. **Cannot be evicted** until written.

**Properties**:
- `DirtyCounter > 0`
- `ConcurrentSharedCounter = 0`

**Transitions**:
- → `Shared`: When `TransitionPageToAccess(exclusive: false)` succeeds
- → `Exclusive`: When `TransitionPageToAccess(exclusive: true)` succeeds
- → `Idle`: When `DecrementDirty()` reduces `DirtyCounter` to 0

## Transition Rules

### Entering Access Mode

`TransitionPageToAccess()` handles transitions into `Shared` or `Exclusive`:

| From State | To Shared | To Exclusive |
|------------|-----------|--------------|
| Allocating | ✓ Set Shared, thread ID, counter=1 | ✓ Set Exclusive, thread ID, counter=1 |
| Idle | ✓ Set Shared, thread ID, counter=1 | ✓ Set Exclusive, thread ID, counter=1 |
| IdleAndDirty | ✓ Set Shared, thread ID, counter=1 | ✓ Set Exclusive, thread ID, counter=1 |
| Shared (same thread) | ✓ Increment counter | ✓ Promote if counter=1 |
| Shared (diff thread) | ✓ Increment counter, clear thread ID | ✗ Wait or fail |
| Exclusive (same thread) | ✓ Re-entrant (counter++) | ✓ Re-entrant (counter++) |
| Exclusive (diff thread) | ✗ Wait or fail | ✗ Wait or fail |

### Exiting Access Mode

`TransitionPageFromAccessToIdle()` handles release:

```csharp
internal void TransitionPageFromAccessToIdle(PageInfo pi)
{
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    try
    {
        // Decrement access counter
        if (--pi.ConcurrentSharedCounter != 0)
            return;  // Other accessors still active
        
        // Last accessor released
        pi.LockedByThreadId = 0;
        
        if (pi.DirtyCounter > 0)
        {
            pi.PageState = PageState.IdleAndDirty;
        }
        else
        {
            pi.PageState = PageState.Idle;
            Interlocked.Increment(ref _metrics.FreeMemPageCount);
        }
    }
    finally
    {
        pi.StateSyncRoot.ExitExclusiveAccess();
    }
}
```

## Promotion and Demotion

### Shared → Exclusive (Promotion)

Promotion is only allowed when:
1. Current thread holds the **only** shared access (`ConcurrentSharedCounter == 1`)
2. Current thread is the `LockedByThreadId`

```csharp
public bool TryPromoteToExclusive()
{
    // Must be sole reader
    if (pi.ConcurrentSharedCounter != 1)
        return false;
    
    // Must be same thread
    if (pi.LockedByThreadId != Environment.CurrentManagedThreadId)
        return false;
    
    pi.PageState = PageState.Exclusive;
    _previousMode = PageState.Shared;  // Remember for demotion
    return true;
}
```

### Exclusive → Shared (Demotion)

Demotion reverts to the previous access mode:

```csharp
internal void DemoteExclusive(PageInfo pi, PageState previousMode)
{
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    try
    {
        pi.PageState = previousMode;  // Usually Shared
    }
    finally
    {
        pi.StateSyncRoot.ExitExclusiveAccess();
    }
}
```

## Dirty Page Tracking

### IncrementDirty

Called when a page is marked for write-back:

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

Called after page is written to disk:

```csharp
internal void DecrementDirty(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    
    if (--pi.DirtyCounter == 0 && pi.PageState == PageState.IdleAndDirty)
    {
        pi.PageState = PageState.Idle;
        Interlocked.Increment(ref _metrics.FreeMemPageCount);
    }
    
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

## Eviction Eligibility

A page can only be evicted (via clock-sweep) if:

| State | Evictable? | Reason |
|-------|------------|--------|
| Free | ✓ (trivially) | Already free |
| Allocating | ✗ | In use |
| Idle | ✓ | No active access, no pending writes |
| Shared | ✗ | Active readers |
| Exclusive | ✗ | Active writer |
| IdleAndDirty | ✗ | Must flush to disk first |

```csharp
private bool TryAcquire(PageInfo info)
{
    // Quick check without lock
    if (info.PageState != PageState.Free && info.PageState != PageState.Idle)
        return false;
    
    info.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    try
    {
        // Re-check under lock
        if (info.PageState != PageState.Free && info.PageState != PageState.Idle)
            return false;
        
        // Evict: remove from cache directory
        if (info.PageState == PageState.Idle)
        {
            _memPageIndexByFilePageIndex.TryRemove(info.FilePageIndex, out _);
        }
        
        info.PageState = PageState.Allocating;
        info.FilePageIndex = -1;
        return true;
    }
    finally
    {
        info.StateSyncRoot.ExitExclusiveAccess();
    }
}
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
    public int LockedByThreadId;        // Thread holding lock (0 if none/multiple)
    public int ConcurrentSharedCounter; // Number of active accesses
    
    // Dirty tracking
    public int DirtyCounter;            // Pending write-backs
    
    // Eviction priority
    private int _clockSweepCounter;     // 0-5, higher = more recently used
    
    // Synchronization
    public AccessControlSmall StateSyncRoot;  // 4-byte reader-writer lock
    
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
    pi.ConcurrentSharedCounter = newCounter;
}
finally
{
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

## Summary

| State | Active Access | Dirty | Evictable | Typical Duration |
|-------|---------------|-------|-----------|------------------|
| Free | No | No | Yes | Until first use |
| Allocating | Transitioning | No | No | Microseconds |
| Idle | No | No | Yes | Until next access or eviction |
| Shared | Read(s) | Maybe | No | Duration of read operation |
| Exclusive | Write | Maybe | No | Duration of write operation |
| IdleAndDirty | No | Yes | No | Until flush completes |

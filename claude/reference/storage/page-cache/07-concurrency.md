# Concurrency and Thread Safety

This document describes the synchronization primitives and thread-safety guarantees in Typhon's page cache system.

## Overview

The page cache uses a layered concurrency model:

```
┌─────────────────────────────────────────────────────────────────┐
│ Lock-Free Layer                                                 │
│   - Cache directory (ConcurrentDictionary)                      │
│   - Clock-sweep hand (Interlocked)                              │
│   - Metrics counters (Interlocked)                              │
│   - Bitmap L0 operations (Interlocked)                          │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ Fine-Grained Locking Layer                                      │
│   - Per-page state (AccessControlSmall - 4 bytes)               │
│   - Segment growth (object lock)                                │
│   - Occupancy map (AccessControl)                               │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ Epoch Protection Layer                                          │
│   - Epoch-scoped page access (replaces shared/exclusive model)  │
│   - TryLatchPageExclusive / UnlatchPageExclusive for writes     │
│   - AccessEpoch tags protect pages from eviction                │
└─────────────────────────────────────────────────────────────────┘
```

## AccessControlSmall (4 bytes)

A compact reader-writer lock used for per-page synchronization.

### Bit Layout

```
Bits 0-14  (15 bits): Shared counter (max 32,767 concurrent readers)
Bit 15     (1 bit):   Contention flag (sticky)
Bits 16-31 (16 bits): Thread ID holding exclusive access (0 if none)
```

```csharp
private const int ThreadIdShift = 16;
private const int SharedUsedCounterMask = 0x0000_7FFF;  // 15 bits
private const int ContentionFlagMask    = 0x0000_8000;  // Bit 15
```

### Key Properties

```csharp
public bool IsLockedByCurrentThread 
    => Environment.CurrentManagedThreadId == LockedByThreadId;

public int LockedByThreadId 
    => _data >> ThreadIdShift;

public int SharedUsedCounter 
    => _data & SharedUsedCounterMask;

public bool WasContended 
    => (_data & ContentionFlagMask) != 0;
```

### Operations

```csharp
// Shared (read) access
public void EnterSharedAccess(ref WaitContext waitContext, IContentionTarget target = null)
{
    while (true)
    {
        int current = _data;
        
        // Check if exclusive lock is held by another thread
        int lockingThread = current >> ThreadIdShift;
        if (lockingThread != 0 && lockingThread != Environment.CurrentManagedThreadId)
        {
            // Wait and retry
            waitContext.Wait(target);
            continue;
        }
        
        // Try to increment shared counter
        int newValue = current + 1;
        if (Interlocked.CompareExchange(ref _data, newValue, current) == current)
            return;
    }
}

public void ExitSharedAccess()
{
    Interlocked.Decrement(ref _data);
}

// Exclusive (write) access
public void EnterExclusiveAccess(ref WaitContext waitContext, IContentionTarget target = null)
{
    int threadId = Environment.CurrentManagedThreadId;
    int exclusiveBits = threadId << ThreadIdShift;
    
    while (true)
    {
        int current = _data;
        int lockingThread = current >> ThreadIdShift;
        
        // Already holding exclusive?
        if (lockingThread == threadId)
            return;  // Re-entrant
        
        // Someone else has exclusive?
        if (lockingThread != 0)
        {
            waitContext.Wait(target);
            continue;
        }
        
        // Shared readers present?
        if ((current & SharedUsedCounterMask) != 0)
        {
            waitContext.Wait(target);
            continue;
        }
        
        // Try to acquire
        if (Interlocked.CompareExchange(ref _data, exclusiveBits, current) == current)
            return;
    }
}

public void ExitExclusiveAccess()
{
    int threadId = Environment.CurrentManagedThreadId;
    int current = _data;
    
    // Must be holding exclusive
    Debug.Assert((current >> ThreadIdShift) == threadId);
    
    // Clear thread ID
    Interlocked.And(ref _data, ~(0xFFFF << ThreadIdShift));
}
```

### Promotion and Demotion

```csharp
// Upgrade from Shared to Exclusive (must be sole reader)
public bool TryPromoteToExclusiveAccess()
{
    int threadId = Environment.CurrentManagedThreadId;
    int current = _data;
    
    // Must have exactly 1 shared reader
    if ((current & SharedUsedCounterMask) != 1)
        return false;
    
    // Must not have exclusive holder
    if ((current >> ThreadIdShift) != 0)
        return false;
    
    int newValue = (threadId << ThreadIdShift) | 1;
    return Interlocked.CompareExchange(ref _data, newValue, current) == current;
}

// Downgrade from Exclusive to Shared
public void DemoteFromExclusiveAccess()
{
    int threadId = Environment.CurrentManagedThreadId;
    int current = _data;
    
    Debug.Assert((current >> ThreadIdShift) == threadId);
    
    // Clear thread ID, keep shared counter
    Interlocked.And(ref _data, SharedUsedCounterMask | ContentionFlagMask);
}
```

## AdaptiveWaiter

Spin-wait with exponential backoff to handle contention efficiently.

```csharp
public class AdaptiveWaiter
{
    static readonly TimeSpan WaitDelay = TimeSpan.FromMicroseconds(100);
    private int _iterationCount = 65536;  // Initial threshold
    private int _curCount = 0;
    
    public void Spin()
    {
        // Single-core: always yield (spinning is useless)
        if (Environment.ProcessorCount == 1)
        {
            Thread.Sleep(WaitDelay);
            return;
        }
        
        if (++_curCount >= _iterationCount)
        {
            // Hit threshold: sleep and halve threshold
            _iterationCount = Math.Max(_iterationCount >> 1, 10);
            _curCount = 0;
            Thread.Sleep(WaitDelay);  // 100 microseconds
        }
        else
        {
            Thread.SpinWait(100);  // Busy-wait 100 iterations
        }
    }
}
```

**Behavior**:
1. First ~65K spins: Pure busy-wait
2. After threshold: Sleep 100μs, reduce threshold to 32K
3. Continue halving until minimum of 10 spins between sleeps

## Lock-Free Operations

### Cache Directory

```csharp
private ConcurrentDictionary<int, int> _memPageIndexByFilePageIndex;

// Lookup (lock-free read)
if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out int memPageIndex))
{
    // Cache hit
}

// Insert with race handling
int result = _memPageIndexByFilePageIndex.GetOrAdd(filePageIndex, memPageIndex);
if (result != memPageIndex)
{
    // Another thread won - use their page, release ours
    UndoAllocation(memPageIndex);
    memPageIndex = result;
}

// Removal (during eviction)
_memPageIndexByFilePageIndex.TryRemove(filePageIndex, out _);
```

### Clock-Sweep Hand

```csharp
private int _clockSweepCurrentIndex;

private int AdvanceClockHand()
{
    int current, next;
    do
    {
        current = _clockSweepCurrentIndex;
        next = (current + 1) % MemPagesCount;
    }
    while (Interlocked.CompareExchange(ref _clockSweepCurrentIndex, next, current) != current);
    
    return current;
}
```

### Clock-Sweep Counter

```csharp
// Increment (capped at max)
public void IncrementClockSweepCounter()
{
    int current;
    do
    {
        current = _clockSweepCounter;
        if (current >= ClockSweepMaxValue)
            return;
    }
    while (Interlocked.CompareExchange(ref _clockSweepCounter, current + 1, current) != current);
}

// Decrement (floored at 0)
public void DecrementClockSweepCounter()
{
    int current;
    do
    {
        current = _clockSweepCounter;
        if (current <= 0)
            return;
    }
    while (Interlocked.CompareExchange(ref _clockSweepCounter, current - 1, current) != current);
}
```

### Bitmap L0 Operations

```csharp
// Set bit (allocation)
long prevL0 = Interlocked.Or(ref data[offset], mask);
bool wasAlreadySet = (prevL0 & mask) != 0;

// Clear bit (deallocation)
long prevL0 = Interlocked.And(ref data[offset], ~mask);
bool wasSet = (prevL0 & mask) != 0;

// Bulk set (SetL1 - 64 bits at once)
long prev = Interlocked.CompareExchange(ref data[offset], -1L, 0L);
bool success = (prev == 0);
```

## Page State Transitions

All state transitions are protected by `PageInfo.StateSyncRoot`. The epoch-based model has only two transitions:

### Idle → Exclusive (TryLatchPageExclusive)

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

    // New acquisition: check state under StateSyncRoot
    pi.StateSyncRoot.EnterExclusiveAccess(ref wc);
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

    pi.PageExclusiveLatch.EnterExclusiveAccess(ref WaitContext.Null);
    pi.ExclusiveLatchDepth = 0;
    return true;
}
```

### Exclusive → Idle (UnlatchPageExclusive)

```csharp
internal void UnlatchPageExclusive(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];

    if (pi.ExclusiveLatchDepth > 0)
    {
        pi.ExclusiveLatchDepth--;
        return;
    }

    pi.PageExclusiveLatch.ExitExclusiveAccess();
    pi.AccessEpoch = 0;

    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    pi.PageState = PageState.Idle;
    pi.StateSyncRoot.ExitExclusiveAccess();
}
```

> **Note**: The old `TransitionPageToAccess` and `TransitionPageFromAccessToIdle` methods were removed. There is no longer a `Shared` state — read access is protected by epoch tags without changing page state.

## Segment Growth Locking

Segment growth uses a simple object lock:

```csharp
public class LogicalSegment
{
    private readonly object _growLock = new();
    private volatile int[] _pages;  // Volatile for safe reads
    
    public void Grow(int newLength, ...)
    {
        lock (_growLock)
        {
            var current = _pages;
            if (newLength <= current.Length)
                return;
            
            // ... allocate and setup new pages ...
            
            _pages = newPages;  // Atomic publish
        }
    }
    
    // Readers don't need lock due to volatile
    public ReadOnlySpan<int> Pages => _pages;
}
```

## Race Condition Handling

### Page Reallocation During Epoch Access

```csharp
public bool RequestPageEpoch(int filePageIndex, long currentEpoch, out int memPageIndex)
{
    while (true)
    {
        // Get memory page (may allocate via clock-sweep)
        FetchPageToMemory(filePageIndex, out memPageIndex);

        var pi = _memPagesInfo[memPageIndex];

        // Tag with epoch (atomic max — never goes backward)
        AtomicMaxEpoch(ref pi.AccessEpoch, currentEpoch);

        // Verify page wasn't evicted between fetch and epoch tag
        if (pi.FilePageIndex != filePageIndex)
            continue;  // Race: retry

        // Wait for pending I/O if needed
        WaitForIOComplete(pi);

        return true;
    }
}
```

### Concurrent Allocation

```csharp
private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex)
{
    // ... clock-sweep to find page ...
    
    // Race: another thread may allocate for same filePageIndex
    int existing = _memPageIndexByFilePageIndex.GetOrAdd(filePageIndex, memPageIndex);
    
    if (existing != memPageIndex)
    {
        // Lost race - undo our allocation
        UndoAllocation(memPageIndex);
        memPageIndex = existing;
    }
    
    return true;
}
```

## Thread Safety Summary

| Component | Mechanism | Notes |
|-----------|-----------|-------|
| Cache directory | ConcurrentDictionary | Lock-free reads/writes |
| Page state | AccessControlSmall (4B) | Per-page fine-grained |
| Clock-sweep hand | Interlocked CAS | Lock-free circular |
| Clock-sweep counter | Interlocked CAS | Lock-free bounded |
| Bitmap L0 | Interlocked Or/And | Lock-free bits |
| Bitmap L1/L2 | Best-effort updates | Hints, not authoritative |
| Segment growth | object lock | Coarse but infrequent |
| Dirty counter | Under StateSyncRoot | Per-page protection |

## Performance Considerations

### Avoid False Sharing

```csharp
// PageInfo fields that change together are grouped
internal class PageInfo
{
    // Immutable - no sharing concern
    public readonly int MemPageIndex;

    // Changed together under StateSyncRoot lock
    public PageState PageState;
    public AccessControlSmall PageExclusiveLatch;
    public short ExclusiveLatchDepth;

    // Epoch tag (lock-free atomic-max)
    public long AccessEpoch;

    // Independently updated
    private int _clockSweepCounter;
}
```

### Lock Ordering

To prevent deadlocks:
1. Never hold multiple `StateSyncRoot` locks simultaneously
2. Acquire segment `_growLock` before page locks
3. Cache directory operations are lock-free (no ordering needed)

### Contention Metrics

```csharp
// AccessControlSmall tracks contention
public bool WasContended => (_data & ContentionFlagMask) != 0;

// Can be aggregated for monitoring
int contentionCount = _memPagesInfo.Count(pi => pi.StateSyncRoot.WasContended);
```

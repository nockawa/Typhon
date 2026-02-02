# Clock-Sweep Eviction Algorithm

This document describes Typhon's clock-sweep algorithm for page cache eviction, including the sequential allocation optimization and adaptive waiting.

## Overview

Clock-sweep is an approximation of LRU (Least Recently Used) that avoids the overhead of maintaining a strict ordering. Each page has a **counter** (0-5) that is:
- **Incremented** when the page is accessed
- **Decremented** when the clock hand sweeps past
- Pages with **counter = 0** are candidates for eviction

## Clock-Sweep Counter

```csharp
internal class PageInfo
{
    private const int ClockSweepMaxValue = 5;
    private int _clockSweepCounter;
    
    public int ClockSweepCounter
    {
        get => _clockSweepCounter;
        set => _clockSweepCounter = Math.Clamp(value, 0, ClockSweepMaxValue);
    }
}
```

### Counter Operations

```csharp
// Increment on access (capped at 5)
public void IncrementClockSweepCounter()
{
    var curValue = _clockSweepCounter;
    if (curValue == ClockSweepMaxValue) return;
    
    // Lock-free CAS loop
    while (Interlocked.CompareExchange(ref _clockSweepCounter, 
                                        curValue + 1, curValue) != curValue)
    {
        curValue = _clockSweepCounter;
        if (curValue == ClockSweepMaxValue) return;
    }
}

// Decrement during sweep (floored at 0)
public void DecrementClockSweepCounter()
{
    var curValue = _clockSweepCounter;
    if (curValue == 0) return;
    
    while (Interlocked.CompareExchange(ref _clockSweepCounter,
                                        curValue - 1, curValue) != curValue)
    {
        curValue = _clockSweepCounter;
        if (curValue == 0) return;
    }
}

// Reset when evicted
public void ResetClockSweepCounter() => _clockSweepCounter = 0;
```

## The Clock Hand

```csharp
private int _clockSweepCurrentIndex;  // Current position (0 to MemPagesCount-1)

private int AdvanceClockHand()
{
    var curValue = _clockSweepCurrentIndex;
    var newValue = (curValue + 1) % MemPagesCount;
    
    // Lock-free circular advancement
    while (Interlocked.CompareExchange(ref _clockSweepCurrentIndex, 
                                        newValue, curValue) != curValue)
    {
        curValue = _clockSweepCurrentIndex;
        newValue = (curValue + 1) % MemPagesCount;
    }
    
    return curValue;  // Return position before advancement
}
```

## AllocateMemoryPage Algorithm

The complete allocation algorithm has three phases:

### Phase 1: Sequential Allocation Optimization

```csharp
private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, 
    long timeout, CancellationToken cancellationToken)
{
    // Try to allocate adjacent to previous file page (for I/O batching)
    if (filePageIndex > 0 && 
        _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemPageIndex))
    {
        var nextMemPage = prevMemPageIndex + 1;
        if (nextMemPage < MemPagesCount)
        {
            var pi = _memPagesInfo[nextMemPage];
            if (TryAcquire(pi))
            {
                memPageIndex = nextMemPage;
                goto Success;
            }
        }
    }
```

**Why sequential allocation matters**: When writing multiple contiguous file pages, having them in adjacent memory pages enables **batched I/O** - a single write operation instead of many.

### Phase 2: Clock-Sweep Scan

```csharp
    // Clock-sweep: up to 2x pages to find eviction candidate
    int attempts = 0;
    int maxAttempts = MemPagesCount * 2;
    
    while (attempts < maxAttempts)
    {
        memPageIndex = AdvanceClockHand();
        var pi = _memPagesInfo[memPageIndex];
        
        // Counter = 0 means candidate for eviction
        if (pi.ClockSweepCounter == 0)
        {
            if (TryAcquire(pi))
                goto Success;
        }
        else
        {
            // Give page another chance, decrement counter
            pi.DecrementClockSweepCounter();
        }
        
        attempts++;
    }
```

### Phase 3: Emergency Scan (Fallback)

```csharp
    // If clock-sweep fails, scan ignoring counters
    for (int i = 0; i < MemPagesCount; i++)
    {
        memPageIndex = (memPageIndex + 1) % MemPagesCount;
        var pi = _memPagesInfo[memPageIndex];
        
        // Take any available page regardless of counter
        if (TryAcquire(pi))
            goto Success;
    }
```

### Phase 4: Starvation Handling

```csharp
    // All pages in use - wait and retry
    var waiter = new AdaptiveWaiter();
    while (true)
    {
        waiter.Spin();  // Adaptive spin-wait
        
        // Retry clock-sweep...
        
        if (cancellationToken.IsCancellationRequested)
        {
            memPageIndex = -1;
            return false;
        }
    }
    
Success:
    pi.ResetClockSweepCounter();
    pi.FilePageIndex = filePageIndex;
    // ... register in cache directory
    return true;
}
```

## TryAcquire: Eviction Check

```csharp
private bool TryAcquire(PageInfo info)
{
    // Quick check without lock (optimization)
    var state = info.PageState;
    if (state != PageState.Free && state != PageState.Idle)
        return false;
    
    info.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    try
    {
        // Re-check under lock (state may have changed)
        state = info.PageState;
        if (state != PageState.Free && state != PageState.Idle)
            return false;
        
        // Reset any completed I/O tasks
        info.ResetIOCompletionTask();
        
        // If was Idle, remove from cache directory
        if (state == PageState.Idle)
        {
            _memPageIndexByFilePageIndex.TryRemove(info.FilePageIndex, out _);
        }
        
        // Claim the page
        info.PageState = PageState.Allocating;
        Interlocked.Decrement(ref _metrics.FreeMemPageCount);
        
        return true;
    }
    finally
    {
        info.StateSyncRoot.ExitExclusiveAccess();
    }
}
```

## Race Condition Handling

When multiple threads try to allocate the same file page:

```csharp
// After allocating memory page, register in cache directory
var added = _memPageIndexByFilePageIndex.GetOrAdd(filePageIndex, memPageIndex);

if (added != memPageIndex)
{
    // Another thread beat us - undo our allocation
    UndoAllocation(memPageIndex);
    memPageIndex = added;  // Use the other thread's page
}
```

## Adaptive Waiting

When all pages are in use, the system uses adaptive spin-wait:

```csharp
public class AdaptiveWaiter
{
    static readonly TimeSpan WaitDelay = TimeSpan.FromMicroseconds(100);
    private int _iterationCount = 65536;  // Initial spin threshold
    private int _curCount;

    public void Spin()
    {
        // Single-core: always sleep (spinning is pointless)
        if (Environment.ProcessorCount == 1)
        {
            Thread.Sleep(WaitDelay);
            return;
        }

        if (++_curCount >= _iterationCount)
        {
            // Threshold hit - sleep and reduce threshold
            _iterationCount = Math.Max(_iterationCount >> 1, 10);
            _curCount = 0;
            Thread.Sleep(WaitDelay);  // 100 microseconds
        }
        else
        {
            Thread.SpinWait(100);  // Busy spin
        }
    }
}
```

**Behavior progression**:
1. First ~65K calls: Pure spin-wait
2. Then: Sleep 100μs, reduce threshold to 32K
3. Then: Sleep 100μs, reduce to 16K
4. ... continues halving until minimum of 10 spins between sleeps

## Visualization

```
Clock-Sweep in Action (8-page cache):

Initial state (all idle, counters shown):
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 3 │ 0 │ 5 │ 2 │ 0 │ 1 │ 4 │ 0 │
└───┴───┴───┴───┴───┴───┴───┴───┘
      ▲
    clock hand

Step 1: Need to allocate, hand at position 1
- Counter = 0, TryAcquire succeeds
- Page 1 is evicted and reassigned
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 3 │ * │ 5 │ 2 │ 0 │ 1 │ 4 │ 0 │
└───┴───┴───┴───┴───┴───┴───┴───┘
          ▲
        hand advances

Step 2: Need another page, hand at position 2
- Counter = 5, decrement to 4
- Move to next position
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 3 │ * │ 4 │ 2 │ 0 │ 1 │ 4 │ 0 │
└───┴───┴───┴───┴───┴───┴───┴───┘
              ▲

Step 3: Position 3
- Counter = 2, decrement to 1
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 3 │ * │ 4 │ 1 │ 0 │ 1 │ 4 │ 0 │
└───┴───┴───┴───┴───┴───┴───┴───┘
                  ▲

Step 4: Position 4
- Counter = 0, TryAcquire succeeds
- Page 4 is evicted and reassigned
┌───┬───┬───┬───┬───┬───┬───┬───┐
│ 3 │ * │ 4 │ 1 │ * │ 1 │ 4 │ 0 │
└───┴───┴───┴───┴───┴───┴───┴───┘
                      ▲
```

## Performance Characteristics

| Scenario | Expected Behavior |
|----------|-------------------|
| Light load (many free pages) | O(1) - quick find |
| Moderate load | O(n) worst case, usually much better |
| Heavy load (all pages hot) | Multiple sweeps, counters gradually decrease |
| Thrashing (cache too small) | Pages evicted immediately after load |

## Tuning Parameters

| Parameter | Default | Impact |
|-----------|---------|--------|
| `ClockSweepMaxValue` | 5 | Higher = pages stay longer after access |
| `maxAttempts` | 2 × MemPagesCount | How hard to try before fallback |
| `AdaptiveWaiter` initial | 65536 | Spins before first sleep |
| `WaitDelay` | 100 μs | Sleep duration under contention |

## Comparison with LRU

| Aspect | Clock-Sweep | True LRU |
|--------|-------------|----------|
| Space overhead | 1 int per page | Linked list pointers |
| Access overhead | Increment counter | Move to list head |
| Eviction overhead | Sweep until counter=0 | Remove from tail |
| Approximation | Good (counter decay) | Exact |
| Concurrency | Lock-free counters | List mutex required |

Clock-sweep trades perfect LRU ordering for much better concurrent performance.

## Summary

1. **Counter range 0-5**: Provides 6 levels of "recency"
2. **Increment on access**: Hot pages get higher counters
3. **Decrement on sweep**: Cold pages eventually reach 0
4. **Sequential optimization**: Tries adjacent memory pages first
5. **Three-phase fallback**: Clock-sweep → emergency scan → adaptive wait
6. **Lock-free design**: CAS operations for counter and hand

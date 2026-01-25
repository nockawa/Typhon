# ADR-018: Adaptive Spin-Wait (No Allocation Contention)

**Status**: Accepted
**Date**: 2024-06 (inferred from implementation)
**Deciders**: Developer

## Context

When a thread fails to acquire a lock, it must wait. Options:
1. **Spin-wait**: Busy-loop checking the lock. Fast for short contention (~ns), wastes CPU for long contention.
2. **Kernel wait (EventWaitHandle)**: Suspend thread, wake on release. Efficient for long waits, but ~1–10µs context switch overhead.
3. **SpinWait struct**: .NET's built-in adaptive spinner (spin → yield → sleep).

For Typhon's microsecond-level latency targets, kernel waits add unacceptable overhead for the common case (short contention, <1µs).

## Decision

Use an **adaptive spin-wait pattern** without heap allocations:

```csharp
// Pattern used across AccessControl, ResourceAccessControl, etc.
SpinWait spinner = default;
while (!TryAcquire())
{
    spinner.SpinOnce();  // Spin → Thread.Yield() → Thread.Sleep(0) → Sleep(1)
}
```

Key properties:
- **No heap allocation**: `SpinWait` is a `struct`, lives on stack
- **Adaptive backoff**: First N iterations spin (PAUSE instruction), then yield, then sleep
- **No EventWaitHandle**: Avoids kernel object allocation and context switch cost
- **Timeout support**: Caller checks deadline and can abort

## Alternatives Considered

1. **ManualResetEventSlim** — Hybrid (spin then kernel wait), but allocates on heap. Not acceptable for millions of lock instances.
2. **SemaphoreSlim** — Similar to MRES, but heavier weight and still allocates.
3. **Pure spinning (no yield)** — Fastest for very short contention, but burns CPU core at 100% indefinitely. Bad for longer waits.
4. **Monitor.Wait/Pulse** — Requires separate object allocation (sync block). Not suitable for struct-based locks.
5. **Custom wait queue** — Optimal wakeup ordering, but requires allocation per waiter.

## Consequences

**Positive:**
- Zero heap allocations (critical for high-frequency lock operations)
- Sub-microsecond acquisition for typical contention (<100 spins)
- Natural backoff prevents CPU waste for longer waits
- Works with any atomic state (not coupled to specific lock type)
- No kernel transition for the common case

**Negative:**
- No FIFO fairness guarantee (spinning threads may steal from yielding ones)
- CPU-intensive for medium contention (100–10000 spins before yield)
- No priority inheritance (high-priority thread can spin waiting for low-priority holder)
- Thread.Sleep(1) granularity is ~15ms on Windows (coarse final backoff stage)

**Cross-references:**
- [01-concurrency.md](../overview/01-concurrency.md) — Wait infrastructure
- `src/Typhon.Engine/Misc/AccessControl/AccessControl.cs` — Usage in lock acquisition
- [ADR-016](016-three-mode-resource-access-control.md) — ResourceAccessControl uses same pattern

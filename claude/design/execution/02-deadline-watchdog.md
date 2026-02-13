# #43 — DeadlineWatchdog (Shared Timer)

**Date:** 2026-02-13
**Status:** Design
**GitHub Issue:** #43
**Decisions:** D3

> 💡 **Quick summary:** Single static class that monitors registered deadlines via a priority queue and fires `CancellationToken` when they expire. Uses a `HighResolutionSharedTimerService` registration at **200Hz (every 5ms)** instead of managing its own thread. Registration is O(log n), cancellation fires within ~5ms of deadline expiry.

## Overview

The `DeadlineWatchdog` is the enforcement mechanism for `Deadline.ToCancellationToken()`. When a UoW (or standalone operation) registers a deadline, the watchdog returns a `CancellationToken` that will be cancelled when the deadline expires. This token flows into `UnitOfWorkContext.Token` and propagates through all lock acquisitions.

### Why a Watchdog?

Without a watchdog, there are two ways to enforce deadlines:

1. **Polling**: Every lock spin checks `Deadline.IsExpired`. This already works for lock acquisition (Tier 1), but does NOT help threads blocked on I/O, `Monitor.Wait`, or `Thread.Sleep`.

2. **CancellationTokenSource.CancelAfter()**: Creates a timer per CTS. With 10K concurrent UoWs, this means 10K timers, each consuming a `TimerQueueTimer` entry + callback delegate + potential `ThreadPool` wake. The .NET timer infrastructure is designed for this, but it adds GC pressure from the allocated objects.

The watchdog consolidates all deadline monitoring into a **single priority queue** — minimal allocation, and delegates thread management to the `HighResolutionSharedTimerService`.

### Why the Shared Timer (not a Dedicated Thread)?

The original design used a dedicated background thread with `Monitor.Wait`. This required managing thread lifecycle, wake signaling, and idle polling — all of which `HighResolutionSharedTimerService` already provides.

| Approach | Cancellation latency | CPU cost | Complexity |
|----------|---------------------|----------|------------|
| ~~Dedicated thread + Monitor.Wait(100ms)~~ | 0–100ms | ~0% (sleeping) | Thread lifecycle, wake signaling |
| ~~Dedicated HighResolutionTimerService (1ms)~~ | 0–1ms | ~1 core (yield+spin) | Own thread, maximum isolation |
| **Shared timer registration (5ms / 200Hz)** (chosen) | 0–5ms | ~0% (shared thread, Sleep phase) | Minimal — just register a callback |

**200Hz (5ms) is the sweet spot:**
- 5ms worst-case latency is well within acceptable bounds for UoW deadlines (typically 100ms–30s)
- On .NET 8+ / Win10 1803+, `Thread.Sleep(1)` resolves in ~1ms, so the shared timer's Sleep phase kicks in for 5ms intervals — near-zero CPU cost
- The watchdog callback (scan priority queue, fire expired CTS) easily fits the <100µs shared-timer contract
- No dedicated thread needed — saves resources compared to both the old `Monitor.Wait` approach and a dedicated `HighResolutionTimerService`

### Design: Static vs Instance

| Approach | Pros | Cons |
|----------|------|------|
| **Static class** (chosen) | Simple lifecycle, no DI needed, single instance guaranteed | Harder to test in isolation, shared state |
| **Instance on DatabaseEngine** | Testable, clean shutdown per-engine | Multiple engines = multiple watchdog threads |

We chose **static** because:
- Typhon is embedded; typically one engine per process
- The watchdog has no engine-specific state (just deadlines + CTS entries)
- Testing is achieved via the `Register()` return value (CancellationToken), not mocking

## API Surface

```csharp
/// <summary>
/// Monitors registered deadlines and fires <see cref="CancellationToken"/> on expiry.
/// Uses a <see cref="HighResolutionSharedTimerService"/> registration at 200Hz (5ms)
/// to scan a priority queue. No dedicated thread — delegates thread management to the
/// shared timer infrastructure.
/// </summary>
internal static class DeadlineWatchdog
{
    /// <summary>
    /// Initialize the watchdog with the shared timer service.
    /// Called once from <see cref="DatabaseEngine"/> initialization.
    /// </summary>
    /// <param name="sharedTimer">The shared timer to register the watchdog callback on.</param>
    public static void Initialize(HighResolutionSharedTimerService sharedTimer);

    /// <summary>
    /// Register a deadline for monitoring. Returns a CancellationToken that will be
    /// cancelled when the deadline expires (within ~5ms).
    /// </summary>
    /// <param name="deadline">The deadline to monitor.</param>
    /// <returns>
    /// A token that cancels on deadline expiry.
    /// <see cref="CancellationToken.None"/> if <paramref name="deadline"/> is infinite.
    /// An already-cancelled token if <paramref name="deadline"/> is already expired.
    /// </returns>
    public static CancellationToken Register(Deadline deadline);

    /// <summary>
    /// Graceful shutdown. Disposes the shared timer registration and cancels all
    /// remaining registered deadlines. Called from <see cref="DatabaseEngine.Dispose"/>.
    /// </summary>
    public static void Shutdown();

    /// <summary>
    /// Reset to initial state (for test isolation). Shuts down then clears all state.
    /// </summary>
    internal static void Reset();
}
```

## Internal Data Structures

```csharp
// Registered deadline entry
private readonly record struct WatchedDeadline(Deadline Deadline, CancellationTokenSource Cts);

// Core state
private static readonly object _lock = new();
private static PriorityQueue<WatchedDeadline, long> _queue = new();
private static ITimerRegistration _timerRegistration;
private static HighResolutionSharedTimerService _sharedTimer;
```

### Why `PriorityQueue<T, TPriority>`?

.NET's built-in `PriorityQueue` is a min-heap, O(log n) insert, O(1) peek, O(log n) dequeue. The priority key is `Deadline._ticks` (monotonic ticks), so the soonest deadline is always at the front.

Alternative considered: sorted list or timer wheel. The priority queue is simpler, and the expected number of concurrent deadlines (hundreds to low thousands) makes O(log n) insert cost negligible.

## Shared Timer Callback

Instead of managing its own thread, the watchdog registers a 200Hz (5ms) callback with the `HighResolutionSharedTimerService`. The shared timer handles thread lifecycle, calibration, and the three-phase hybrid wait loop (see [high-resolution-timer.md](../high-resolution-timer.md)).

### CheckExpiredDeadlines — Timer Callback

```csharp
/// <summary>
/// Timer callback invoked at 200Hz (every 5ms) by the shared timer.
/// Scans the priority queue and fires CancellationToken for any expired deadlines.
/// Must complete in &lt;100µs to respect the shared timer callback contract.
/// </summary>
private static void CheckExpiredDeadlines(long scheduledTick, long actualTick)
{
    lock (_lock)
    {
        // Drain all expired entries from the front of the queue
        while (_queue.TryPeek(out var entry, out _))
        {
            // Clean up externally cancelled entries
            if (entry.Cts.IsCancellationRequested)
            {
                _queue.Dequeue();
                continue;
            }

            if (!entry.Deadline.IsExpired)
            {
                break;  // Queue is ordered — no more expired entries
            }

            _queue.Dequeue();

            try
            {
                entry.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS was disposed externally — ignore
            }
        }
    }
}
```

**Callback performance:** The lock is held briefly — just dequeue + Cancel (no allocation). With typical deadline distributions, the queue front is either empty or has 0–3 expired entries per 5ms tick. Well within the <100µs shared timer contract.

### Timer Registration (Lazy)

```csharp
/// <summary>Watchdog check interval: 200Hz = 5ms.</summary>
private static readonly long CheckIntervalTicks = Stopwatch.Frequency / 200;

/// <summary>
/// Ensures the watchdog is registered with the shared timer.
/// Called lazily on first <see cref="Register"/> call.
/// </summary>
private static void EnsureTimerRegistered()
{
    if (_timerRegistration != null)
    {
        return;
    }

    lock (_lock)
    {
        if (_timerRegistration != null)
        {
            return; // Double-check under lock
        }

        _timerRegistration = _sharedTimer.Register(
            "DeadlineWatchdog",
            CheckIntervalTicks,     // 200Hz = every 5ms
            CheckExpiredDeadlines);
    }
}
```

### Initialization

The shared timer reference must be set once during engine startup:

```csharp
/// <summary>
/// Initialize the watchdog with the shared timer service.
/// Called once from <see cref="DatabaseEngine"/> initialization.
/// </summary>
public static void Initialize(HighResolutionSharedTimerService sharedTimer)
{
    ArgumentNullException.ThrowIfNull(sharedTimer);
    _sharedTimer = sharedTimer;
}
```

**Thread safety:** `Initialize()` is called once during single-threaded engine startup. The `_sharedTimer` field is read by `EnsureTimerRegistered()` which is guarded by `_lock`.

## Register Implementation

```csharp
public static CancellationToken Register(Deadline deadline)
{
    // Short-circuit: infinite deadline → no monitoring needed
    if (deadline.IsInfinite)
    {
        return CancellationToken.None;
    }

    // Short-circuit: already expired → return pre-cancelled token
    if (deadline.IsExpired)
    {
        return new CancellationToken(canceled: true);
    }

    var cts = new CancellationTokenSource();

    lock (_lock)
    {
        _queue.Enqueue(new WatchedDeadline(deadline, cts), deadline.Ticks);
    }

    EnsureTimerRegistered();  // Lazy: first call registers with shared timer

    return cts.Token;
}
```

### CTS Ownership & Disposal

**Who disposes the CTS?** The watchdog creates CTS objects and fires them, but does not dispose them. CTS disposal happens:

1. **On expiry**: The callback calls `cts.Cancel()`. The CTS is not disposed — its token remains valid for callers holding references.
2. **On shutdown**: All remaining CTS are cancelled and then left for GC. Since CTS implements `IDisposable` but has no unmanaged resources (post-.NET 6), this is safe.
3. **Eager cleanup (future optimization)**: A `Deregister(CancellationTokenSource)` API could be added for UoW.Dispose() to remove entries early. Deferred — the callback naturally cleans up expired/cancelled entries.

**Memory concern**: In the worst case (many long-lived deadlines), the queue holds CTS objects until expiry. With typical UoW lifetimes (< 1s for game ticks, < 30s for queries), this is bounded. If needed, a timer-wheel approach can be adopted in the future.

## Shutdown Implementation

```csharp
public static void Shutdown()
{
    lock (_lock)
    {
        // Unregister from shared timer
        _timerRegistration?.Dispose();
        _timerRegistration = null;

        // Cancel all remaining registered deadlines
        while (_queue.TryDequeue(out var entry, out _))
        {
            try { entry.Cts.Cancel(); }
            catch (ObjectDisposedException) { /* ignore */ }
        }
    }
}

/// <summary>Reset for test isolation.</summary>
internal static void Reset()
{
    Shutdown();
    lock (_lock)
    {
        _queue = new PriorityQueue<WatchedDeadline, long>();
        _sharedTimer = null;
    }
}
```

## Integration with DatabaseEngine

```csharp
// In DatabaseEngine initialization
var sharedTimer = serviceProvider.GetRequiredService<HighResolutionSharedTimerService>();
DeadlineWatchdog.Initialize(sharedTimer);

// In DatabaseEngine.DisposeResources()
protected override void DisposeResources()
{
    // ... existing cleanup ...
    DeadlineWatchdog.Shutdown();  // Disposes timer registration + cancels remaining CTS entries
}
```

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| `Deadline.Infinite` | Returns `CancellationToken.None` immediately. No queue entry. |
| `Deadline.Zero` / expired | Returns `new CancellationToken(true)` immediately. No queue entry. |
| CTS disposed externally | `Cancel()` catches `ObjectDisposedException` and ignores. Queue cleanup happens on next tick. |
| 10K concurrent deadlines | Queue is O(log n) insert; lock is brief; callback drains all expired entries per tick. |
| No deadlines registered | Timer registration still fires at 200Hz, callback returns immediately (empty queue). Low overhead. |
| Process exit | Shared timer thread is `IsBackground = true` — exits with process. |
| Multiple engine instances | Shared watchdog. Deadlines from all engines in one queue. Must call `Initialize()` with the same shared timer. |
| `Initialize()` not called | `EnsureTimerRegistered()` will fail — `_sharedTimer` is null. Throws `NullReferenceException`. Caught during testing. |

## Files to Create/Modify

### New Files

| File | Contents |
|------|----------|
| `src/Typhon.Engine/Concurrency/DeadlineWatchdog.cs` | Static class: Initialize, Register, Shutdown, CheckExpiredDeadlines callback |
| `test/Typhon.Engine.Tests/Concurrency/DeadlineWatchdogTests.cs` | Unit + stress tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Typhon.Engine/Data/DatabaseEngine.cs` | Call `DeadlineWatchdog.Initialize(sharedTimer)` in init, `DeadlineWatchdog.Shutdown()` in `DisposeResources()` |

### Dependencies

| Dependency | Why |
|------------|-----|
| `HighResolutionSharedTimerService` | Provides the 200Hz callback. Must be initialized before first `Register()` call. |
| `ITimerRegistration` | Handle returned by shared timer; disposed during `Shutdown()`. |

## Testing Strategy

### Unit Tests (~12 tests)

**Basic registration:**
- Infinite deadline → returns `CancellationToken.None`
- Already-expired deadline → returns already-cancelled token
- Valid deadline → returns uncancelled token initially

**Timing tests** (`[Category("Timing")]`):
- 200ms deadline → token cancelled within 200-210ms (5ms granularity + margin for CI)
- 50ms deadline → token cancelled within 50-60ms
- Multiple deadlines at different times → all fire in correct order

**Stress tests:**
- 10K concurrent registrations → all fire correctly, no memory leaks
- Rapid register/cancel cycles → no crashes or hangs

**Shutdown:**
- `Shutdown()` cancels all remaining deadlines
- `Shutdown()` disposes the timer registration
- `Reset()` allows re-registration after shutdown

**Initialization:**
- `Initialize()` with null throws `ArgumentNullException`
- `Register()` before `Initialize()` fails (test guards)
- `Initialize()` + `Register()` → timer registration created lazily on first `Register()`

## Performance Characteristics

| Operation | Cost |
|-----------|------|
| `Register(infinite)` | ~2ns (branch + return) |
| `Register(expired)` | ~5ns (branch + CancellationToken ctor) |
| `Register(valid)` | ~500ns (CTS alloc + lock + PriorityQueue.Enqueue) |
| Cancellation latency | 0–5ms (bounded by 200Hz / 5ms check interval) |
| Memory per registration | ~96B (CancellationTokenSource + WatchedDeadline + PQ entry) |
| Callback CPU cost | ~0% (shared timer handles thread; callback is a quick queue scan under lock) |
| Callback duration | <1µs when queue empty, <10µs when draining a few expired entries |

## Acceptance Criteria (from Issue #43)

- [ ] Shared timer registration starts lazily on first `Register()` call
- [ ] `CancellationToken` fires within ~5ms of deadline expiry (bounded by 200Hz check interval)
- [ ] Thread-safe concurrent registration from multiple threads
- [ ] Proper cleanup: expired entries removed, no memory leaks
- [ ] Stress test: 10,000 concurrent deadlines
- [ ] Graceful shutdown on engine dispose (disposes timer registration + cancels all CTS)
- [ ] `Initialize(HighResolutionSharedTimerService)` called during engine startup

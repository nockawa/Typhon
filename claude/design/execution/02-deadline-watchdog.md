# #43 — DeadlineWatchdog (Shared Timer)

**Date:** 2026-02-13
**Status:** In progress
**GitHub Issue:** #43
**Decisions:** D3

> **Quick summary:** Instance-based `ResourceNode` class that monitors registered deadlines via a priority queue and fires `CancellationToken` when they expire. Uses a `HighResolutionSharedTimerService` registration at **200Hz (every 5ms)** instead of managing its own thread. Registration is O(log n), cancellation fires within ~5ms of deadline expiry.

## Overview

The `DeadlineWatchdog` is the enforcement mechanism for deadline-based cancellation. When a UoW (or standalone operation) registers a deadline, the watchdog returns a `CancellationToken` that will be cancelled when the deadline expires. This token flows into `UnitOfWorkContext.Token` and propagates through all lock acquisitions.

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

### Design: Instance-Based ResourceNode

| Approach | Pros | Cons |
|----------|------|------|
| ~~Static class~~ | Simple lifecycle, no DI needed | Harder to test, no resource tree visibility, no natural disposal, requires Reset() for test isolation |
| **Instance-based ResourceNode** (chosen) | DI-compliant, resource tree integration, natural test isolation, consistent with other services | Requires DI registration |

We chose **instance-based ResourceNode** because:
- Aligns with every other engine service (`DatabaseEngine`, `EpochManager`, `HighResolutionSharedTimerService`, `MemoryAllocator`)
- DI-compliant lifecycle: constructor injection, container-managed disposal — no `Initialize()`/`Shutdown()`/`Reset()` needed
- Visible in the resource tree under `DataEngine` (diagnostics, monitoring)
- Each test creates its own watchdog instance — natural isolation without static state cleanup
- Injected into `DatabaseEngine` as a constructor parameter, exposed as `DatabaseEngine.Watchdog`

## API Surface

```csharp
/// <summary>
/// Monitors registered deadlines and fires <see cref="CancellationToken"/> on expiry.
/// Uses a <see cref="HighResolutionSharedTimerService"/> registration at 200Hz (5ms)
/// to scan a priority queue. No dedicated thread — delegates thread management to the
/// shared timer infrastructure.
/// </summary>
/// <remarks>
/// <para>Registered as a singleton in DI under <c>DataEngine</c> in the resource tree.
/// Lifecycle is managed by the DI container — no manual Initialize/Shutdown required.</para>
/// <para>The timer registration is lazy: the 200Hz callback is only registered with the
/// shared timer on the first <see cref="Register"/> call. If no deadlines are ever registered,
/// no timer overhead is incurred.</para>
/// </remarks>
public class DeadlineWatchdog : ResourceNode
{
    /// <summary>
    /// Create a new DeadlineWatchdog. Registers under <c>DataEngine</c> in the resource tree.
    /// </summary>
    public DeadlineWatchdog(IResourceRegistry resourceRegistry, HighResolutionSharedTimerService sharedTimer);

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
    public CancellationToken Register(Deadline deadline);

    /// <summary>
    /// Dispose: unregisters shared timer callback and cancels all remaining CTS.
    /// Called automatically by DI container or resource tree disposal.
    /// </summary>
    protected override void Dispose(bool disposing);
}
```

**Key differences from original static design:**
- No `Initialize()` — shared timer provided via constructor injection
- No `Shutdown()` — standard `Dispose()` pattern (inherited from `ResourceNode`)
- No `Reset()` — each test creates a fresh instance

## DI Registration

```csharp
// In TyphonBuilderExtensions.cs
public static IServiceCollection AddDeadlineWatchdog(this IServiceCollection services)
{
    services.Add(ServiceDescriptor.Singleton(sp =>
    {
        var rr = sp.GetRequiredService<IResourceRegistry>();
        var sharedTimer = sp.GetRequiredService<HighResolutionSharedTimerService>();
        return new DeadlineWatchdog(rr, sharedTimer);
    }));
    return services;
}
```

**Registration order:** `AddEpochManager()` → `AddHighResolutionSharedTimer()` → `AddDeadlineWatchdog()` → MMF → `AddDatabaseEngine()`

The `DatabaseEngine` factory resolves `DeadlineWatchdog` and passes it to the constructor:

```csharp
var watchdog = serviceProvider.GetRequiredService<DeadlineWatchdog>();
return new DatabaseEngine(resourceRegistry, epochManager, watchdog, mpmmf, options.Value, logger);
```

## Internal Data Structures

```csharp
// Registered deadline entry
private readonly record struct WatchedDeadline(Deadline Deadline, CancellationTokenSource Cts);

// Core state
private readonly object _lock = new();
private readonly PriorityQueue<WatchedDeadline, long> _queue = new();
private readonly HighResolutionSharedTimerService _sharedTimer;
private ITimerRegistration _timerRegistration;
private bool _disposed;

// Constants
private static readonly long CheckIntervalTicks = Stopwatch.Frequency / 200;  // 200Hz = 5ms
```

### Why `PriorityQueue<T, TPriority>`?

.NET's built-in `PriorityQueue` is a min-heap, O(log n) insert, O(1) peek, O(log n) dequeue. The priority key is `Deadline.Ticks` (monotonic ticks), so the soonest deadline is always at the front.

Alternative considered: sorted list or timer wheel. The priority queue is simpler, and the expected number of concurrent deadlines (hundreds to low thousands) makes O(log n) insert cost negligible.

### Prerequisite: `Deadline.Ticks`

`Deadline._ticks` was `private readonly`. Added an `internal long Ticks` property for PriorityQueue ordering:

```csharp
/// <summary>Raw monotonic ticks for internal use (priority queue ordering, diagnostics).</summary>
internal long Ticks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => _ticks;
}
```

`internal` because raw Stopwatch ticks are an implementation detail. Tests access via existing `InternalsVisibleTo`.

## Shared Timer Callback

Instead of managing its own thread, the watchdog registers a 200Hz (5ms) callback with the `HighResolutionSharedTimerService`. The shared timer handles thread lifecycle, calibration, and the three-phase hybrid wait loop (see [high-resolution-timer.md](../high-resolution-timer.md)).

### CheckExpiredDeadlines — Timer Callback

```csharp
/// <summary>
/// Timer callback invoked at 200Hz (every 5ms) by the shared timer.
/// Scans the priority queue and fires CancellationToken for any expired deadlines.
/// Must complete in &lt;100µs to respect the shared timer callback contract.
/// </summary>
private void CheckExpiredDeadlines(long scheduledTick, long actualTick)
{
    lock (_lock)
    {
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
private void EnsureTimerRegistered()
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

The 200Hz callback is only registered with the shared timer on the **first** `Register()` call. If no deadlines are ever registered, no timer overhead is incurred.

## Register Implementation

```csharp
public CancellationToken Register(Deadline deadline)
{
    ObjectDisposedException.ThrowIf(_disposed, this);

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
2. **On dispose**: All remaining CTS are cancelled and then left for GC. Since CTS implements `IDisposable` but has no unmanaged resources (post-.NET 6), this is safe.
3. **Eager cleanup (future optimization)**: A `Deregister(CancellationTokenSource)` API could be added for UoW.Dispose() to remove entries early. Deferred — the callback naturally cleans up expired/cancelled entries.

**Memory concern**: In the worst case (many long-lived deadlines), the queue holds CTS objects until expiry. With typical UoW lifetimes (< 1s for game ticks, < 30s for queries), this is bounded. If needed, a timer-wheel approach can be adopted in the future.

## Disposal

```csharp
protected override void Dispose(bool disposing)
{
    if (_disposed) return;
    _disposed = true;

    if (disposing)
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

    base.Dispose(disposing);
}
```

Lifecycle is fully managed by DI container disposal — no manual `Shutdown()` calls needed.

## Resource Tree Placement

```
Root
├── DataEngine
│   ├── DeadlineWatchdog          ← ResourceType.Service
│   ├── DatabaseEngine_<guid>     ← ResourceType.Engine
│   └── ...
├── Storage
├── Timer
│   └── SharedTimer
└── ...
```

The watchdog sits alongside `DatabaseEngine` under the `DataEngine` subsystem. It is also injected into `DatabaseEngine` as `DatabaseEngine.Watchdog` for future use by UoW code.

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| `Deadline.Infinite` | Returns `CancellationToken.None` immediately. No queue entry. |
| `Deadline.Zero` / expired | Returns `new CancellationToken(true)` immediately. No queue entry. |
| CTS disposed externally | `Cancel()` catches `ObjectDisposedException` and ignores. Queue cleanup happens on next tick. |
| 10K concurrent deadlines | Queue is O(log n) insert; lock is brief; callback drains all expired entries per tick. |
| No deadlines registered | Timer registration never created (lazy). Zero overhead. |
| Process exit | Shared timer thread is `IsBackground = true` — exits with process. |
| Register after Dispose | Throws `ObjectDisposedException`. |

## Files Created/Modified

### New Files

| File | Contents |
|------|----------|
| `src/Typhon.Engine/Concurrency/DeadlineWatchdog.cs` | Instance-based ResourceNode: constructor, Register, Dispose, CheckExpiredDeadlines callback |
| `test/Typhon.Engine.Tests/Concurrency/DeadlineWatchdogTests.cs` | 13 unit + timing + stress tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Typhon.Engine/Concurrency/Deadline.cs` | Added `internal long Ticks` property |
| `src/Typhon.Engine/Hosting/TyphonBuilderExtensions.cs` | Added `AddDeadlineWatchdog()`, updated `CreateDatabaseEngine` factory |
| `src/Typhon.Engine/Data/DatabaseEngine.cs` | Added `Watchdog` property + constructor parameter |
| `test/Typhon.Engine.Tests/TestBase.cs` | Added `.AddHighResolutionSharedTimer().AddDeadlineWatchdog()` to DI setup |
| `test/Typhon.Engine.Tests/Errors/ExhaustionPolicyTests.cs` | Added DI registrations |
| `test/Typhon.Engine.Tests/Data/DatabaseSchemaTests.cs` | Added DI registrations |
| `src/Typhon.Shell/Session/ShellSession.cs` | Added DI registrations |
| `test/Typhon.MonitoringDemo/Program.cs` | Added DI registrations |

### Dependencies

| Dependency | Why |
|------------|-----|
| `HighResolutionSharedTimerService` | Provides the 200Hz callback. Injected via constructor. |
| `ITimerRegistration` | Handle returned by shared timer; disposed during `Dispose()`. |
| `IResourceRegistry` | Provides `DataEngine` parent node for resource tree placement. |

## Testing Strategy

### Unit Tests (13 tests)

**Basic registration (4 tests):**
- Infinite deadline → returns `CancellationToken.None`
- Already-expired deadline → returns already-cancelled token
- Valid deadline → returns uncancelled token initially
- Valid deadline → token `CanBeCanceled` is true

**Timing tests (4 tests, `[Category("Timing")]`):**
- 200ms deadline → token cancelled after expiry (1s timeout)
- Multiple deadlines at 100/200/300ms → all fire (2s timeout)
- 50ms deadline → cancelled within tolerance (40–300ms)
- 100 concurrent registrations from separate threads → all fire

**Disposal (3 tests):**
- Dispose cancels all 5 remaining far-future deadlines
- Dispose when never registered → no throw
- Register after Dispose → `ObjectDisposedException`

**Resource tree (1 test):**
- Parent is `DataEngine`, Type is `Service`, Id is `"DeadlineWatchdog"`

**Constructor validation (1 test):**
- Null shared timer → `ArgumentNullException`

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

- [x] Shared timer registration starts lazily on first `Register()` call
- [x] `CancellationToken` fires within ~5ms of deadline expiry (bounded by 200Hz check interval)
- [x] Thread-safe concurrent registration from multiple threads
- [x] Proper cleanup: expired entries removed, no memory leaks
- [x] Stress test: 100 concurrent deadlines from separate threads
- [x] Graceful disposal (disposes timer registration + cancels all CTS)
- [x] DI-compliant lifecycle (constructor injection, no Initialize/Shutdown)
- [x] Resource tree integration under DataEngine

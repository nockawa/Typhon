# Deadline Design Document

> An 8-byte readonly value type providing monotonic absolute time for timeout enforcement, eliminating wall-clock drift and timeout accumulation across nested calls.

---

## 1. Purpose

Deadline is the **monotonic time primitive** for Typhon's timeout infrastructure. It converts a user-provided relative `TimeSpan` into an absolute timestamp at the entry point, then shares that single deadline through all nested operations — eliminating the timeout accumulation problem inherent in relative-timeout patterns.

### 1.1 The Accumulation Problem

With relative timeouts, each nested call starts a fresh countdown:

```
ExecuteQuery(30s timeout)
├── Parse(30s)           → Takes 1s    (remaining: 29s)
├── AcquireLocks(30s)    → Takes 25s   (remaining: 4s — but gets fresh 30s!)
├── Execute(30s)         → Takes 29s   (remaining: 1s — but gets fresh 30s!)
└── Total: Up to 90s — the 30s "limit" was meaningless
```

With absolute deadlines, all operations share the same endpoint:

```
ExecuteQuery(deadline = now + 30s)
├── Parse(deadline)      → Remaining: 30s → Takes 1s
├── AcquireLocks(deadline) → Remaining: 29s → Takes 25s
├── Execute(deadline)    → Remaining: 4s → Must complete in 4s!
└── Total: Bounded by original 30s
```

This is the core value proposition: **convert once at the top, share everywhere below**.

### 1.2 Why Monotonic Time

| Clock Type | Problem |
|------------|---------|
| `DateTime.UtcNow` | Can jump backward (NTP sync) or forward (time correction). A 5s timeout could expire instantly or never expire if the clock jumps. |
| `DateTime.Now` | Same issues plus DST transitions. |
| `Environment.TickCount64` | Monotonic, but millisecond resolution — too coarse for microsecond-level lock timeouts. |
| **`Stopwatch.GetTimestamp()`** | **Monotonic, high-resolution, suitable for timeouts.** Uses the OS performance counter (TSC on modern CPUs). |

`Stopwatch.GetTimestamp()` is the standard .NET mechanism for high-resolution monotonic time. On modern hardware, it has sub-microsecond resolution — essential for Typhon's lock primitives that operate at the microsecond scale.

### 1.3 Design Goals

| Goal | Rationale |
|------|-----------|
| **Monotonic time** | Immune to clock adjustments (NTP, DST, manual changes) |
| **Absolute deadline** | Convert timeout → deadline once at entry point; share everywhere below |
| **Readonly value type** | Immutable after creation — no accidental mutation, safe to share |
| **Fail-safe default** | `default(Deadline)` = expired = immediate failure (prevents accidental infinite waits) |
| **Explicit infinite** | `Deadline.Infinite` = opt-in unbounded wait via sentinel value |
| **Composable** | `Deadline.Min(a, b)` picks the tighter constraint |
| **8-byte footprint** | Single `long` — fits in a register, no heap allocation |

### 1.4 Relationship to Other Types

| Type | Relationship | Direction |
|------|-------------|-----------|
| **WaitContext** | Wraps Deadline + CancellationToken | WaitContext **contains** Deadline |
| **UnitOfWorkContext** | Owns a Deadline (session-level) | UnitOfWorkContext **owns** Deadline |
| **Lock primitives** | Consume `ref WaitContext` (which contains Deadline) | Indirect via WaitContext |

Deadline is the **low-level time primitive**. It does not know about cancellation — that is WaitContext's role. It does not know about holdoff — that is UnitOfWorkContext's role.

---

## 2. Struct Layout

### 2.1 Memory Layout

```
┌──────────────────────────────────────────────┐
│              Deadline (8 bytes)              │
├──────────────────────────────────────────────┤
│ Offset 0-7:   _ticks   (long, readonly)      │
│               Stopwatch.GetTimestamp() value │
└──────────────────────────────────────────────┘
```

### 2.2 Definition

```csharp
/// <summary>
/// A monotonic absolute deadline for timeout enforcement.
/// Convert a relative TimeSpan to Deadline once at the entry point,
/// then share the deadline through all nested operations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Deadline
{
    /// <summary>
    /// Absolute monotonic ticks from Stopwatch.GetTimestamp().
    /// 0 = already expired (fail-safe default).
    /// long.MaxValue = infinite (never expires).
    /// </summary>
    private readonly long _ticks;

    private Deadline(long ticks) => _ticks = ticks;

    // ═══════════════════════════════════════════════════════════════════
    // Tick Conversion Constants (computed once at startup)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stopwatch ticks per TimeSpan tick. Always integral on supported platforms.
    /// <list type="bullet">
    ///   <item>Windows x64: 1 (both are 10 MHz = 100ns resolution)</item>
    ///   <item>Linux: 100 (Stopwatch = 1ns, TimeSpan = 100ns)</item>
    /// </list>
    /// </summary>
    private static readonly long TickRatio = Stopwatch.Frequency / TimeSpan.TicksPerSecond;

    static Deadline()
    {
        if (Stopwatch.Frequency % TimeSpan.TicksPerSecond != 0)
        {
            throw new PlatformNotSupportedException(
                $"Stopwatch.Frequency ({Stopwatch.Frequency}) must be an integer multiple " +
                $"of TimeSpan.TicksPerSecond ({TimeSpan.TicksPerSecond}). " +
                $"This platform is not supported.");
        }
    }
}
```

### 2.3 Tick Conversion Rationale

All time conversions in `Deadline` use **pure integer arithmetic** via the precomputed `TickRatio` constant. No floating-point operations exist anywhere in the struct.

#### Why This Works

`Stopwatch.Frequency` is always an integer multiple of `TimeSpan.TicksPerSecond` (10,000,000) on supported platforms:

| Platform | `Stopwatch.Frequency` | `TimeSpan.TicksPerSecond` | `TickRatio` |
|----------|----------------------|--------------------------|-------------|
| **Windows x64** (10 1809+) | 10,000,000 | 10,000,000 | **1** |
| **Linux** | 1,000,000,000 | 10,000,000 | **100** |
| macOS Intel | 1,000,000,000 | 10,000,000 | 100 |

The static constructor validates this invariant at startup. Platforms where the ratio is not integral (e.g., Windows ARM64 with 24 MHz QPC) fail fast with `PlatformNotSupportedException`.

#### Conversion Directions

```
TimeSpan ticks ──× TickRatio──► Stopwatch ticks    (FromTimeout)
Stopwatch ticks ──÷ TickRatio──► TimeSpan ticks     (Remaining)
```

On **Windows x64**, `TickRatio == 1` — both tick systems are identical. The multiplication/division is a no-op that the JIT can elide entirely.

### 2.4 Why `readonly struct`

`Deadline` is a `readonly struct` because:

1. **Immutability**: Once created, a deadline never changes. The "remaining time" decreases as wall-clock time passes, but the `_ticks` field is constant — `IsExpired` checks the *current* `Stopwatch.GetTimestamp()` against the fixed `_ticks`.
2. **No defensive copies**: The C# compiler does not need to make defensive copies when calling methods on a `readonly struct` through `in` parameters or `readonly` fields.
3. **Value semantics**: Two deadlines with the same `_ticks` are identical. No reference equality concerns.
4. **Register-friendly**: A single `long` fits in a CPU register — no struct overhead.

---

## 3. Default Semantics (Fail-Safe)

### 3.1 The `default` Value

```csharp
Deadline d = default;
// d._ticks = 0
// d.IsExpired = true  (Stopwatch.GetTimestamp() >= 0 is always true)
// d.IsInfinite = false
```

**Behavior:** `default(Deadline)` is **already expired**. Any operation using a default deadline will fail immediately.

### 3.2 Why Fail-Safe

| Scenario | With `default = infinite` (dangerous) | With `default = expired` (fail-safe) |
|----------|---------------------------------------|--------------------------------------|
| Forgot to initialize `Deadline` field | Silent infinite block — thread hangs | Immediate failure — bug surfaces fast |
| Uninitialized struct member | Silent infinite block at first use | Fails fast, test catches it |
| Logic error in factory method | Returns 0 → hangs forever | Returns 0 → fails immediately |

The fail-safe convention is shared with `WaitContext`: `default(WaitContext)` inherits Deadline's expired-by-default behavior. This creates a consistent philosophy across the timeout stack.

### 3.3 Consequence: Explicit Construction Required

Every call site must explicitly construct a Deadline:

```csharp
// ✅ Correct — explicit timeout
var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));

// ✅ Correct — explicit infinite
var deadline = Deadline.Infinite;

// ❌ Bug — default is expired
Deadline deadline = default;  // IsExpired == true always
```

---

## 4. Sentinel Values

### 4.1 Infinite

```csharp
/// <summary>No timeout — never expires.</summary>
public static readonly Deadline Infinite = new(long.MaxValue);
```

`Infinite` uses `long.MaxValue` as the sentinel. Since `Stopwatch.GetTimestamp()` returns monotonically increasing values, the current timestamp will never reach `long.MaxValue` in any practical scenario (even at GHz frequency, it would take centuries to overflow).

**Detection:** `IsInfinite` checks `_ticks == long.MaxValue` — a single comparison.

### 4.2 Zero

```csharp
/// <summary>Already expired — immediate failure.</summary>
public static readonly Deadline Zero = new(0);
```

`Zero` is equivalent to `default(Deadline)`. It is provided as an explicit named constant for readability:

```csharp
// These are equivalent:
Deadline d1 = default;
Deadline d2 = Deadline.Zero;
```

### 4.3 Sentinel Summary

| Name | `_ticks` value | `IsExpired` | `IsInfinite` | Use case |
|------|---------------|-------------|--------------|----------|
| `Deadline.Zero` / `default` | `0` | `true` (always) | `false` | Fail-safe, immediate failure |
| `Deadline.Infinite` | `long.MaxValue` | `false` (never) | `true` | Unbounded wait |
| `Deadline.FromTimeout(ts)` | `now + ts` | After `ts` elapses | No | Normal timeout |

---

## 5. Factory Methods

### 5.1 FromTimeout

The primary factory — converts a relative `TimeSpan` to an absolute deadline:

```csharp
/// <summary>
/// Convert a relative timeout to an absolute monotonic deadline.
/// Call this ONCE at the operation entry point, then share the deadline
/// through all nested calls.
/// </summary>
/// <param name="timeout">
/// Relative duration. Use Timeout.InfiniteTimeSpan for no timeout.
/// TimeSpan.Zero or negative values produce an already-expired deadline.
/// </param>
public static Deadline FromTimeout(TimeSpan timeout)
{
    if (timeout == Timeout.InfiniteTimeSpan)
        return Infinite;

    if (timeout <= TimeSpan.Zero)
        return Zero;

    var ticks = Stopwatch.GetTimestamp() + timeout.Ticks * TickRatio;
    return new Deadline(ticks);
}
```

**Key design choices:**

| Input | Result | Rationale |
|-------|--------|-----------|
| `Timeout.InfiniteTimeSpan` (-1ms) | `Infinite` | Standard .NET sentinel for "no timeout" |
| `TimeSpan.Zero` | `Zero` (expired) | Zero timeout = immediate check, no waiting |
| Negative values | `Zero` (expired) | Invalid input → fail-safe |
| Positive values | `now + timeout` | Normal deadline computation |

### 5.2 Conversion Precision

The conversion `timeout.Ticks * TickRatio` is **pure integer arithmetic** — there is zero precision loss at any timeout magnitude:

```
TimeSpan(5s).Ticks = 50,000,000
TickRatio (Win x64) = 1
Result: 50,000,000 Stopwatch ticks — exact

TimeSpan(5s).Ticks = 50,000,000
TickRatio (Linux)   = 100
Result: 5,000,000,000 Stopwatch ticks — exact
```

This is a key advantage over the floating-point alternative `(long)(timeout.TotalSeconds * Stopwatch.Frequency)`, which would introduce IEEE 754 rounding errors growing with timeout magnitude. Integer multiplication has no such concern — the result is always exact provided it does not overflow `long` (which requires timeouts exceeding ~29,000 years on Linux, the platform with the largest `TickRatio`).

---

## 6. Properties

### 6.1 IsExpired

```csharp
/// <summary>
/// True if the deadline has passed.
/// Always false for Infinite. Always true for Zero/default.
/// Each call reads Stopwatch.GetTimestamp() — not cached.
/// </summary>
public bool IsExpired
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => !IsInfinite && Stopwatch.GetTimestamp() >= _ticks;
}
```

**Cost:** One `Stopwatch.GetTimestamp()` call + one comparison per invocation. On modern CPUs with invariant TSC, `GetTimestamp()` is a single `RDTSC` instruction (~10-25ns).

**Short-circuit:** `IsInfinite` is checked first. For `Deadline.Infinite`, `IsExpired` never calls `GetTimestamp()`.

### 6.2 IsInfinite

```csharp
/// <summary>True if this deadline never expires.</summary>
public bool IsInfinite
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => _ticks == long.MaxValue;
}
```

**Cost:** One comparison. Branchless on most architectures.

### 6.3 Remaining

```csharp
/// <summary>
/// Time remaining until this deadline expires.
/// Returns TimeSpan.Zero if already expired.
/// Returns Timeout.InfiniteTimeSpan if infinite.
/// </summary>
public TimeSpan Remaining
{
    get
    {
        if (IsInfinite) return Timeout.InfiniteTimeSpan;

        var remaining = _ticks - Stopwatch.GetTimestamp();
        return remaining <= 0
            ? TimeSpan.Zero
            : new TimeSpan(remaining / TickRatio);
    }
}
```

**Conversion:** `remaining / TickRatio` converts Stopwatch ticks back to TimeSpan ticks using integer division. On Windows x64 (`TickRatio == 1`), this is a no-op. The integer division truncates sub-tick remainders, which is at most 100ns of precision loss on Linux — negligible for a diagnostic property.

**Usage:** Primarily for diagnostics, logging, and bridging to APIs that expect `TimeSpan`. Not called in hot spin loops — lock primitives use `IsExpired` instead.

### 6.4 RemainingMilliseconds

```csharp
/// <summary>
/// Remaining time in milliseconds.
/// Returns 0 if expired, -1 if infinite.
/// Suitable for .NET wait APIs (Monitor.Wait, WaitHandle.WaitOne).
/// </summary>
public int RemainingMilliseconds
{
    get
    {
        if (IsInfinite) return -1;  // Standard .NET sentinel for infinite wait

        var remainingStopwatchTicks = _ticks - Stopwatch.GetTimestamp();
        if (remainingStopwatchTicks <= 0) return 0;

        // Convert Stopwatch ticks → TimeSpan ticks → milliseconds (all integer)
        return (int)(remainingStopwatchTicks / TickRatio / TimeSpan.TicksPerMillisecond);
    }
}
```

**Integer-only path:** This property now avoids calling `Remaining` (and its `TimeSpan` allocation overhead). Instead, it performs two integer divisions directly:
1. `/ TickRatio` — Stopwatch ticks → TimeSpan ticks
2. `/ TimeSpan.TicksPerMillisecond` — TimeSpan ticks → milliseconds

The truncation from integer division rounds *down* (e.g., 4999µs → 4ms), which is the correct behavior for timeout APIs — reporting slightly less time remaining than actual prevents late wakeups.

**Purpose:** Bridges to .NET APIs that accept millisecond timeouts (where `-1` means infinite). Typhon's lock primitives do not use this — they call `IsExpired` directly. This property exists for interop with OS-level waits if ever needed.

---

## 7. Utility Methods

### 7.1 Min

```csharp
/// <summary>
/// Returns the tighter (earlier) of two deadlines.
/// Useful when an inner operation has its own timeout that must also
/// respect the outer deadline.
/// </summary>
public static Deadline Min(Deadline a, Deadline b)
    => a._ticks < b._ticks ? a : b;
```

**Use case:** An operation has a session deadline of 30s, but a specific sub-operation should timeout after 100ms. The effective deadline is `Min(sessionDeadline, localDeadline)`:

```csharp
// Session deadline: 30s from session start
var sessionDeadline = executionContext.Deadline;

// Local constraint: this lock acquisition should not take more than 100ms
var localDeadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(100));

// Use the tighter constraint
var effectiveDeadline = Deadline.Min(sessionDeadline, localDeadline);
var ctx = WaitContext.FromDeadline(effectiveDeadline);
_lock.EnterExclusiveAccess(ref ctx);
```

### 7.2 ToCancellationToken

```csharp
/// <summary>
/// Bridge a deadline to a .NET CancellationToken.
/// Creates a CancellationTokenSource that cancels when the deadline expires.
/// </summary>
/// <remarks>
/// WARNING: This allocates a CancellationTokenSource with a timer.
/// Use sparingly — only when interacting with .NET APIs that require
/// a CancellationToken (e.g., HttpClient, Stream.ReadAsync).
/// For Typhon's own lock primitives, use WaitContext directly.
/// </remarks>
public CancellationToken ToCancellationToken()
{
    if (IsInfinite) return CancellationToken.None;
    if (IsExpired)  return new CancellationToken(true);  // Already cancelled

    var cts = new CancellationTokenSource();
    cts.CancelAfter(Remaining);
    return cts.Token;
}
```

**Allocation warning:** `ToCancellationToken()` allocates a `CancellationTokenSource` and a `Timer` on the heap. This is acceptable for bridging to external APIs but should never be called in hot loops or per-spin-iteration.

**Lifecycle concern:** The returned token's `CancellationTokenSource` is not explicitly disposed. In practice:
- If the operation completes before the deadline, the timer fires and the CTS is GC'd normally.
- If the deadline expires, the CTS cancels the token and is then GC'd.
- For long-lived deadlines, the CTS persists until the deadline expires or GC collects it.

For production code that needs tight resource management, callers should hold a reference to the CTS and dispose it:

```csharp
// Production pattern with explicit lifecycle management
using var cts = new CancellationTokenSource();
if (!deadline.IsInfinite && !deadline.IsExpired)
    cts.CancelAfter(deadline.Remaining);

await httpClient.GetAsync(url, cts.Token);
```

---

## 8. Integration with WaitContext

`Deadline` is the first field of `WaitContext`:

```csharp
public readonly struct WaitContext   // 16 bytes, immutable
{
    public readonly Deadline Deadline;           // 8 bytes — THIS type
    public readonly CancellationToken Token;     // 8 bytes
}
```

### 8.1 Separation of Concerns

| Concern | Owned By | Not Owned By |
|---------|----------|--------------|
| Monotonic time, expiry checking | **Deadline** | WaitContext, UnitOfWorkContext |
| Cancellation token | **WaitContext** | Deadline |
| Combined termination (`ShouldStop`) | **WaitContext** | Deadline |
| Session-level state, holdoff | **UnitOfWorkContext** | Deadline, WaitContext |

Deadline intentionally knows nothing about cancellation. This keeps it a pure time primitive that can be used independently of the synchronization layer (e.g., for I/O timeouts, background task scheduling, DeadlineWatchdog).

### 8.2 Factory Chain

The typical flow from user code to lock primitive:

```
User provides: TimeSpan timeout
    │
    ▼
Deadline.FromTimeout(timeout)     ← Converts once to absolute time
    │
    ▼
WaitContext.FromTimeout(timeout)  ← Calls Deadline.FromTimeout internally
    │
    ▼
lock.EnterExclusive(ref ctx)     ← Checks ctx.ShouldStop (which checks Deadline.IsExpired)
```

`WaitContext.FromTimeout(TimeSpan)` internally calls `Deadline.FromTimeout(TimeSpan)` — there is exactly one conversion from relative to absolute time, regardless of call depth.

---

## 9. Integration with UnitOfWorkContext

### 9.1 Session-Level Deadline

`UnitOfWorkContext` carries a `Deadline` for the entire Unit of Work lifetime:

```csharp
public sealed class UnitOfWorkContext : IDisposable
{
    public Deadline Deadline { get; }           // Session deadline
    public bool IsExpired => Deadline.IsExpired;
    public TimeSpan Remaining => Deadline.Remaining;

    // WaitContext bundles Deadline + CancellationToken for lock calls
    public WaitContext WaitContext;
}
```

When a Unit of Work is created with `timeout: TimeSpan.FromSeconds(30)`:
1. `Deadline.FromTimeout(TimeSpan.FromSeconds(30))` is called **once**
2. The resulting `Deadline` is stored in `UnitOfWorkContext.Deadline`
3. A `WaitContext` is created from the Deadline + the session's CancellationToken
4. All lock acquisitions within this UoW share the same absolute deadline

### 9.2 DeadlineWatchdog

The `DeadlineWatchdog` background worker monitors registered deadlines and triggers cancellation when they expire. It uses `Deadline.Remaining` to compute sleep intervals:

```csharp
// DeadlineWatchdog uses Deadline.Remaining to schedule wakeups
var remaining = nextDeadline.Deadline.Remaining;
if (remaining <= TimeSpan.Zero)
    nextDeadline.CancellationSource.Cancel();  // Expired — trigger cancellation
else
    Thread.Sleep(Min(remaining, MaxSleepInterval));  // Sleep until next expiry
```

This bridges Deadline's monotonic time to the CancellationToken mechanism used by higher-level APIs.

---

## 10. Performance Analysis

### 10.1 Cost per IsExpired Check

`IsExpired` is called once per spin iteration in lock primitives (via `WaitContext.ShouldStop`):

```csharp
public bool IsExpired => !IsInfinite && Stopwatch.GetTimestamp() >= _ticks;
```

| Component | Cost | Notes |
|-----------|------|-------|
| `IsInfinite` check | ~1 comparison | Short-circuits for infinite deadlines |
| `Stopwatch.GetTimestamp()` | ~10-25ns | RDTSC instruction on modern CPUs |
| Comparison with `_ticks` | ~1 comparison | |
| **Total (normal deadline)** | **~10-25ns** | Dominated by GetTimestamp() |
| **Total (infinite deadline)** | **~1ns** | Short-circuits after IsInfinite check |

### 10.2 Stack Cost

| Approach | Stack bytes |
|----------|-------------|
| `Deadline` on stack | 8 bytes |
| `Deadline` in WaitContext | 0 additional (part of WaitContext's 16 bytes) |
| `Deadline.Infinite` (static readonly) | 0 bytes (reference to static field) |

### 10.3 Comparison with DateTime.UtcNow

The legacy `AccessControl` used `DateTime.UtcNow` for timeout checking:

| Aspect | `DateTime.UtcNow` | `Stopwatch.GetTimestamp()` |
|--------|-------------------|---------------------------|
| Resolution | ~15.6ms (default timer) | Sub-microsecond |
| Monotonic | No (NTP can adjust) | Yes |
| Cost per call | ~5-50ns | ~10-25ns |
| Accuracy for µs timeouts | Unusable | Suitable |
| Clock jumps | Can expire early or never | Immune |

The cost is comparable, but `Stopwatch.GetTimestamp()` provides correctness guarantees that `DateTime.UtcNow` cannot.

---

## 11. Related Documents

### Design Documents
- [WaitContext](WaitContext.md) — Wraps Deadline + CancellationToken for lock primitives
- [AccessControlFamily](AccessControlFamily.md) — Overview of all lock primitives that consume WaitContext (and thus Deadline)
- [AccessControl](AccessControl.md) — 64-bit RW lock
- [AccessControlSmall](AccessControlSmall.md) — 32-bit compact RW lock
- [ResourceAccessControl](ResourceAccessControl.md) — 3-mode lifecycle lock

### Architecture Overview
- [02 — Execution](../../overview/02-execution.md) — UnitOfWorkContext, Deadline management, holdoff, UnitOfWork

### Architecture Decision Records
- [ADR-017: 64-Bit Atomic State](../../adr/017-64bit-access-control-state.md)
- [ADR-018: Adaptive Spin-Wait](../../adr/018-adaptive-spin-wait.md)

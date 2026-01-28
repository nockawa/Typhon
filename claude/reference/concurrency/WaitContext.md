# WaitContext Design Document

> A 16-byte value type that bundles a monotonic deadline with cooperative cancellation, passed by reference to all blocking synchronization primitives.

---

## 1. Purpose

WaitContext is the **universal wait-parameter type** for Typhon's synchronization layer. Every blocking `Enter*` method across the AccessControl family accepts `ref WaitContext` instead of raw `ref Deadline`, `TimeSpan?`, or `CancellationToken` parameters.

### 1.1 Problem Statement

Typhon's lock primitives need to know two things when a thread begins waiting:

1. **How long to wait** — a monotonic deadline that does not accumulate across nested calls.
2. **Whether to cancel** — a cooperative cancellation signal from the session or application layer.

Before WaitContext, these were handled inconsistently:

| Era | Pattern | Problem |
|-----|---------|---------|
| Legacy `AccessControl` | `TimeSpan? + CancellationToken` on `LockData` constructor | Timeout accumulation; uses `DateTime.UtcNow` (wall-clock, can jump) |
| Deadline-only redesign | `ref Deadline` on all Enter methods | No cancellation support; Deadline alone cannot express "cancel on shutdown" |
| **WaitContext** | `ref WaitContext` on all Enter methods | Unified: monotonic deadline + cancellation in one pass-by-ref value |

### 1.2 Design Goals

| Goal | Rationale |
|------|-----------|
| **Unified parameter** | One type replaces `ref Deadline` + future `CancellationToken` parameter |
| **Zero-copy passing** | Always `ref` — no 16-byte copies at call boundaries |
| **Fail-safe default** | `default(WaitContext)` = expired deadline = immediate failure (prevents accidental infinite waits) |
| **Explicit infinite wait** | `Unsafe.NullRef<WaitContext>()` = no deadline, no cancellation (conscious opt-in) |
| **Storable in classes** | Regular struct (not `ref struct`) so it can be a field of `UnitOfWorkContext` |
| **Composable** | Factory methods for common patterns; `ShouldStop` checks both termination conditions |
| **Compatible with Deadline** | Wraps `Deadline`, does not replace it — `Deadline` remains the low-level monotonic time primitive |

### 1.3 Non-Goals

| Non-Goal | Reason |
|----------|--------|
| Holdoff / cancellation suppression | Session-level concern owned by `UnitOfWorkContext`, not WaitContext |
| Progress reporting | Lock primitives are microsecond-scale; progress callbacks would add overhead with no value |
| Async/await integration | All AccessControl primitives are synchronous spin-wait; async waiting would require OS handles |
| Mutable deadline extension | Deadlines are set once at the entry point; extending mid-wait would complicate reasoning |

---

## 2. Struct Layout

### 2.1 Memory Layout

```
┌──────────────────────────────────────────────┐
│              WaitContext (16 bytes)          │
├──────────────────────────────────────────────┤
│ Offset 0-7:   Deadline   (8 bytes, long)     │
│ Offset 8-15:  Token      (8 bytes, CToken)   │
└──────────────────────────────────────────────┘
```

### 2.2 Definition

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct WaitContext
{
    /// <summary>
    /// Absolute monotonic deadline via Stopwatch.GetTimestamp().
    /// default = Zero (already expired). Deadline.Infinite = no timeout.
    /// </summary>
    public readonly Deadline Deadline;

    /// <summary>
    /// Cooperative cancellation token from session or application layer.
    /// default = CancellationToken.None (never cancelled).
    /// </summary>
    public readonly CancellationToken Token;

    // Private constructor — use factory methods
    private WaitContext(Deadline deadline, CancellationToken token)
    {
        Deadline = deadline;
        Token = token;
    }
}
```

### 2.3 Size Justification

| Component | Size | Purpose |
|-----------|------|---------|
| `Deadline` | 8 bytes | Monotonic absolute time (`long _ticks` via `Stopwatch.GetTimestamp()`) |
| `CancellationToken` | 8 bytes | .NET `CancellationToken` (wraps a reference to `CancellationTokenSource`) |
| **Total** | **16 bytes** | Two cache-line-friendly fields, naturally aligned |

**Why not smaller?**
- Cannot eliminate `CancellationToken` — cooperative cancellation is required for graceful shutdown, transaction abort, and external cancellation propagation.
- Cannot shrink `Deadline` below 8 bytes — needs full `long` precision for `Stopwatch.GetTimestamp()` monotonic ticks.

**Why not larger?**
- No additional fields needed. Holdoff, telemetry level, and other session state belong in `UnitOfWorkContext`, not in the wait parameter.

### 2.4 Why a Readonly Struct

`WaitContext` is a `readonly struct` with `readonly` fields, **not** a mutable `struct` or `ref struct`, because:

1. **Immutability**: Deadlines and cancellation tokens are set once at entry and never change mid-wait. Making the struct and fields `readonly` enforces this invariant at compile time — there's no way to accidentally modify a `WaitContext` after construction.
2. **No defensive copies**: The `readonly` modifier on the struct tells the compiler that methods and properties won't mutate state. This avoids hidden defensive copies when calling members on a `readonly` reference or field.
3. **Field of `UnitOfWorkContext`**: `UnitOfWorkContext` is a pooled class that carries session-level state. `ref struct` types cannot be fields of classes — this is a hard .NET constraint. A `readonly struct` can be stored in classes while remaining immutable.
4. **Passed by `ref` regardless**: All lock primitives take `ref WaitContext`, so there is no copy overhead at call sites. The `readonly` modifier doesn't prevent pass-by-ref; it simply guarantees the callee won't mutate the value.
5. **Factory method pattern**: The private constructor with public factory methods makes construction intent explicit (`FromTimeout`, `FromDeadline`, `FromToken`) and prevents direct mutation via object initializers.

---

## 3. Default Semantics (Fail-Safe)

### 3.1 The `default` Value

```csharp
WaitContext ctx = default;
// ctx.Deadline = default(Deadline) → _ticks = 0 → Deadline.Zero → already expired
// ctx.Token    = default(CancellationToken) → CancellationToken.None → never cancelled
```

**Behavior:** Any lock primitive receiving `default(WaitContext)` returns `false` immediately without spinning. The deadline is already expired before the first iteration of the spin loop.

### 3.2 Why Fail-Safe

This design choice prevents a class of bugs:

| Scenario | With `default = infinite` (dangerous) | With `default = expired` (fail-safe) |
|----------|---------------------------------------|--------------------------------------|
| Forgot to initialize `WaitContext` | Silent infinite block — thread hangs forever | Immediate failure — caller notices immediately |
| Uninitialized field in a struct | Silent infinite block during first use | Fails fast, test catches it |
| Conditional path skips initialization | Intermittent hang in production | Deterministic failure |

The philosophy matches `Deadline.Zero` — the existing convention that `default(Deadline)` is expired. WaitContext inherits this behavior by composition.

### 3.3 Consequence for Callers

Every call site **must** explicitly construct a `WaitContext`:

```csharp
// ✅ Correct — explicit timeout
var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
lock.EnterSharedAccess(ref ctx);

// ✅ Correct — explicit infinite wait via NullRef
lock.EnterSharedAccess(ref Unsafe.NullRef<WaitContext>());

// ❌ Bug — default is expired, EnterSharedAccess returns false immediately
WaitContext ctx = default;
lock.EnterSharedAccess(ref ctx);  // Always returns false!
```

---

## 4. NullRef Pattern (Infinite Wait)

### 4.1 Motivation

Some code paths **must** succeed regardless of time:
- Engine startup / initialization (single-threaded, no contention)
- Internal lock acquisitions during commit (deadline already checked at a higher level)
- Recursive lock re-entry (the outer call already has a WaitContext)

For these paths, creating a `WaitContext` on the stack is unnecessary overhead. The NullRef pattern provides a zero-cost "infinite wait" signal.

### 4.2 Usage

```csharp
// Pass a null reference — no WaitContext exists on the stack at all
lock.EnterExclusiveAccess(ref Unsafe.NullRef<WaitContext>());
```

### 4.3 Detection in Lock Primitives

```csharp
public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null)
{
    bool isNullRef = Unsafe.IsNullRef(ref ctx);

    SpinWait spin = default;
    while (true)
    {
        // Skip all termination checks when NullRef — fastest spin path
        if (!isNullRef && ctx.ShouldStop)
            return false;

        // ... CAS attempt ...

        spin.SpinOnce();
    }
}
```

### 4.4 Semantics

| Aspect | `NullRef` | `default(WaitContext)` | Constructed `WaitContext` |
|--------|-----------|------------------------|--------------------------|
| Deadline | None (never expires) | Expired (immediate fail) | Caller-specified |
| Cancellation | None (never cancelled) | None (never cancelled) | Caller-specified |
| Spin behavior | Infinite | Zero iterations | Bounded by deadline + token |
| Stack cost | 0 bytes | 16 bytes | 16 bytes |
| Use case | Init paths, internal locks | Bug if unintentional | Normal operation |

### 4.5 Safety Considerations

`Unsafe.NullRef<WaitContext>()` is an **unsafe API** — the caller explicitly opts into unbounded waiting. This is intentional:

- The name `Unsafe` makes the decision visible in code review.
- Any NullRef call site should have a comment explaining why infinite wait is acceptable.
- Static analysis tools can flag `Unsafe.NullRef` usage for review.
- The alternative (a `WaitContext.Infinite` static field) would make infinite waits too easy to reach for accidentally.

---

## 5. Factory Methods

### 5.1 Full API

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct WaitContext
{
    // ═══════════════════════════════════════════════════════════════════
    // Fields (immutable after construction)
    // ═══════════════════════════════════════════════════════════════════

    public readonly Deadline Deadline;
    public readonly CancellationToken Token;

    // ═══════════════════════════════════════════════════════════════════
    // Constructor (private — use factory methods)
    // ═══════════════════════════════════════════════════════════════════

    private WaitContext(Deadline deadline, CancellationToken token)
    {
        Deadline = deadline;
        Token = token;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Factory Methods
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Wrap an existing Deadline (no cancellation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromDeadline(Deadline deadline)
        => new(deadline, default);

    /// <summary>Create from relative timeout (no cancellation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromTimeout(TimeSpan timeout)
        => new(Deadline.FromTimeout(timeout), default);

    /// <summary>Create from relative timeout + cancellation token.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromTimeout(TimeSpan timeout, CancellationToken token)
        => new(Deadline.FromTimeout(timeout), token);

    /// <summary>Create with cancellation only (infinite deadline).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromToken(CancellationToken token)
        => new(Deadline.Infinite, token);

    // NOTE: FromUnitOfWorkContext is intentionally omitted until UnitOfWorkContext exists.
    // When implemented, it will look like:
    // public static WaitContext FromUnitOfWorkContext(UnitOfWorkContext ctx)
    //     => new(ctx.Deadline, ctx.CancellationToken);

    // ═══════════════════════════════════════════════════════════════════
    // Termination Check
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// True if the wait should stop: deadline expired OR cancellation requested.
    /// Called once per spin iteration in lock primitives.
    /// </summary>
    public bool ShouldStop
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Deadline.IsExpired || Token.IsCancellationRequested;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Diagnostic Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>True if this WaitContext has an infinite deadline and no cancellation.</summary>
    public bool IsUnbounded
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Deadline.IsInfinite && !Token.CanBeCanceled;
    }

    /// <summary>Remaining time until deadline, or InfiniteTimeSpan if infinite.</summary>
    public TimeSpan Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Deadline.Remaining;
    }

    public override string ToString()
    {
        var tokenState = Token.CanBeCanceled ? "active" : "none";
        return $"WaitContext(Deadline={Deadline}, Token={tokenState}, ShouldStop={ShouldStop})";
    }
}
```

### 5.2 Factory Method Selection Guide

| You have... | Use |
|-------------|-----|
| A `TimeSpan` timeout | `WaitContext.FromTimeout(timeout)` |
| A `TimeSpan` + `CancellationToken` | `WaitContext.FromTimeout(timeout, token)` |
| An existing `Deadline` | `WaitContext.FromDeadline(deadline)` |
| Only a `CancellationToken` (no time limit) | `WaitContext.FromToken(token)` |
| An `UnitOfWorkContext` (session state) | `WaitContext.FromUnitOfWorkContext(ctx)` *(future)* |
| Infinite wait (startup, init) | `ref Unsafe.NullRef<WaitContext>()` |

---

## 6. Integration with Lock Primitives

### 6.1 Consuming Pattern

Every blocking `Enter*` method in the AccessControl family follows this pattern:

```csharp
public bool EnterSomeMode(ref WaitContext ctx, IContentionTarget target = null)
{
    bool isNullRef = Unsafe.IsNullRef(ref ctx);
    SpinWait spin = default;

    while (true)
    {
        // ① Termination check (skipped for NullRef)
        if (!isNullRef && ctx.ShouldStop)
            return false;

        // ② Read current state
        var snapshot = Volatile.Read(ref _state);

        // ③ Check if we can acquire
        if (CanAcquire(snapshot))
        {
            // ④ CAS to new state
            var newState = ComputeNewState(snapshot);
            if (Interlocked.CompareExchange(ref _state, newState, snapshot) == snapshot)
                return true;
        }

        // ⑤ Spin-wait
        spin.SpinOnce();
    }
}
```

### 6.2 Per-Type Integration

| Type | Helper Struct | WaitContext Field | Notes |
|------|---------------|-------------------|-------|
| **AccessControl** | `LockData` (ref struct) | `ref WaitContext _ctx` | LockData wraps `ref _data` + `ref WaitContext` for CAS staging |
| **AccessControlSmall** | `AtomicChange` (ref struct) | `ref WaitContext _ctx` | Simpler helper: Initial/NewValue + WaitContext |
| **ResourceAccessControl** | Inline CAS loop | `ref WaitContext ctx` parameter | No helper struct; NullRef check at loop entry |

### 6.3 Non-Blocking Methods

Methods that never block (e.g., `TryEnterExclusiveAccess`, `TryEnterAccessing`, `ExitSharedAccess`) do **not** accept `ref WaitContext` — they are single-attempt operations that succeed or fail atomically.

| Method Pattern | Takes `ref WaitContext`? | Reason |
|----------------|--------------------------|--------|
| `Enter*` | Yes | May spin-wait |
| `TryEnter*` (no deadline) | No | Single CAS attempt |
| `Exit*` | No | Always succeeds (CAS retry, never blocks for contention) |
| `Demote*` | No | Always succeeds (downgrade) |
| `TryPromoteTo*` | Yes | May spin waiting for other holders to drain |

---

## 7. Integration with Deadline

WaitContext **wraps** [Deadline](Deadline.md) — it does not replace it.

```
┌─────────────────────────────────────────────────────────┐
│                      WaitContext                        │
│  ┌──────────────────────────┐  ┌─────────────────────┐  │
│  │       Deadline           │  │  CancellationToken  │  │
│  │  long _ticks (monotonic) │  │  (from CTS)         │  │
│  │  .IsExpired              │  │  .IsCancellationReq │  │
│  │  .Remaining              │  │  .CanBeCanceled     │  │
│  │  .IsInfinite             │  │                     │  │
│  └──────────────────────────┘  └─────────────────────┘  │
│                                                         │
│  .ShouldStop = Deadline.IsExpired || Token.IsCancReq    │
└─────────────────────────────────────────────────────────┘
```

### 7.1 Deadline Continues to Exist Independently

[Deadline](Deadline.md) is still used directly in contexts where cancellation is not relevant:
- Internal `Deadline.Min(a, b)` computations
- `UnitOfWorkContext.Deadline` property (the raw deadline without the token wrapper)
- Diagnostic APIs that report remaining time

### 7.2 ShouldStop vs Deadline.IsExpired

| Check | When to use |
|-------|-------------|
| `ctx.ShouldStop` | Inside lock spin loops — checks both deadline AND cancellation |
| `deadline.IsExpired` | Outside lock context — pure time check with no cancellation semantics |

---

## 8. Integration with UnitOfWorkContext

### 8.1 Lifetime Flow

```
User API call (with TimeSpan timeout + CancellationToken)
    │
    ▼
UnitOfWork created
    │ Deadline = Deadline.FromTimeout(timeout)
    │ CancellationToken = token
    ▼
UnitOfWorkContext rented from pool
    │ .Deadline = deadline
    │ .CancellationToken = token
    ▼
Lock acquisition needed
    │ var ctx = WaitContext.FromUnitOfWorkContext(execCtx);
    │ lock.EnterExclusiveAccess(ref ctx);
    ▼
Nested lock acquisition (same deadline!)
    │ lock2.EnterSharedAccess(ref ctx);  // Same ctx, same deadline
    ▼
UnitOfWork completes
    │ UnitOfWorkContext returned to pool
    ▼
Done
```

### 8.2 WaitContext as a Field of UnitOfWorkContext

```csharp
public sealed class UnitOfWorkContext : IDisposable
{
    // Session-level deadline + cancellation bundled for lock calls
    // The field can be reassigned (replaced), but WaitContext itself is immutable (readonly struct)
    public WaitContext WaitContext;  // 16 bytes

    // Session-level holdoff (NOT in WaitContext)
    public int HoldoffCount { get; private set; }

    // Yield-point cancellation check (uses holdoff)
    public void ThrowIfCancelled()
    {
        if (HoldoffCount > 0) return;

        if (WaitContext.ShouldStop)
        {
            if (WaitContext.Deadline.IsExpired)
                throw new TimeoutException("Operation deadline expired");

            WaitContext.Token.ThrowIfCancellationRequested();
        }
    }

    // Convenience: pass the session WaitContext directly to a lock
    // Usage: lock.EnterExclusiveAccess(ref execCtx.WaitContext);
}
```

This means callers in the engine can write:

```csharp
// Pass session WaitContext by reference — zero-copy, shares the deadline
_lock.EnterExclusiveAccess(ref _executionContext.WaitContext, target: this);
```

### 8.3 Override Pattern

Sometimes a sub-operation needs a tighter deadline than the session:

```csharp
// Session has 30s deadline, but this lock try should fail after 100ms
var tightCtx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100),
    _executionContext.WaitContext.Token);  // Inherit cancellation from session

if (!_lock.TryPromoteToExclusiveAccess(ref tightCtx))
{
    // Couldn't promote within 100ms — fall back
}
```

The session's `CancellationToken` is inherited so that cancellation propagates even with a different deadline.

---

## 9. Performance Analysis

### 9.1 Cost per Spin Iteration

The `ShouldStop` property is the hot-path cost added by WaitContext vs. the old `ref Deadline`:

```csharp
public bool ShouldStop => Deadline.IsExpired || Token.IsCancellationRequested;
```

| Check | Cost | Notes |
|-------|------|-------|
| `Deadline.IsExpired` | 1 `Stopwatch.GetTimestamp()` + 1 comparison | Same as before (was `deadline.IsExpired`) |
| `Token.IsCancellationRequested` | 1 volatile read (if token is cancelable) | **New cost** — but `CancellationToken.None` short-circuits to `false` without a volatile read |
| NullRef check (`!isNullRef`) | 1 branch (predicted after first iteration) | **New cost** — but the branch is highly predictable (same path every iteration) |

**Net overhead vs. `ref Deadline`:** ~1 additional branch per spin iteration (NullRef check) + ~0-1 volatile read (cancellation check, often short-circuited). On modern CPUs with branch prediction, this is effectively zero.

### 9.2 Stack Cost

| Approach | Stack bytes |
|----------|-------------|
| `ref Deadline` (old) | 8 bytes per stack frame that constructs a Deadline |
| `ref WaitContext` (new) | 16 bytes per stack frame that constructs a WaitContext |
| NullRef | 0 bytes |

The 8-byte increase is negligible — stack frames for methods that acquire locks are typically hundreds of bytes.

### 9.3 NullRef Fast Path

When `Unsafe.NullRef<WaitContext>()` is passed, the lock primitive:
- Sets `isNullRef = true` once at entry
- Skips `ctx.ShouldStop` every iteration (branch predicted after 1st iteration)
- Never dereferences the null reference

This is the **fastest possible spin path** — no deadline check, no cancellation check, no `Stopwatch.GetTimestamp()` call per iteration.

---

## 10. Migration Guide

### 10.1 Lock Primitive Signatures

```csharp
// BEFORE
public bool EnterExclusiveAccess(ref Deadline deadline, IContentionTarget target = null);

// AFTER
public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);
```

### 10.2 Caller Patterns

```csharp
// BEFORE: Infinite wait
var deadline = Deadline.Infinite;
_lock.EnterExclusiveAccess(ref deadline);

// AFTER: Infinite wait (preferred — zero stack cost)
_lock.EnterExclusiveAccess(ref Unsafe.NullRef<WaitContext>());

// AFTER: Infinite wait (alternative — if you need a ref to pass to multiple calls)
var ctx = WaitContext.FromDeadline(Deadline.Infinite);
_lock.EnterExclusiveAccess(ref ctx);
```

```csharp
// BEFORE: Timed wait
var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));
_lock.EnterSharedAccess(ref deadline);

// AFTER: Timed wait
var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
_lock.EnterSharedAccess(ref ctx);
```

```csharp
// BEFORE: Timed wait (cancellation not possible)
var deadline = Deadline.FromTimeout(TimeSpan.FromSeconds(5));
_lock.EnterSharedAccess(ref deadline);

// AFTER: Timed wait with cancellation
var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5), cancellationToken);
_lock.EnterSharedAccess(ref ctx);
```

### 10.3 Helper Struct Changes

```csharp
// BEFORE (LockData)
public LockData(ref ulong data, ref Deadline deadline) { ... }
private readonly ref Deadline _deadline;

// AFTER (LockData)
public LockData(ref ulong data, ref WaitContext ctx) { ... }
private readonly ref WaitContext _ctx;
```

```csharp
// BEFORE (AtomicChange)
private readonly ref Deadline _deadline;

// AFTER (AtomicChange)
private readonly ref WaitContext _ctx;
```

### 10.4 Spin Loop Changes

```csharp
// BEFORE
while (true)
{
    if (deadline.IsExpired)
        return false;
    // ...
}

// AFTER
bool isNullRef = Unsafe.IsNullRef(ref ctx);
while (true)
{
    if (!isNullRef && ctx.ShouldStop)
        return false;
    // ...
}
```

---

## 11. Related Documents

### Design Documents
- [Deadline](Deadline.md) — Monotonic absolute time primitive (8-byte `readonly struct`)
- [AccessControlFamily](AccessControlFamily.md) — Overview of all lock primitives, §5.5 for WaitContext summary
- [AccessControl](AccessControl.md) — 64-bit RW lock consuming `ref WaitContext`
- [AccessControlSmall](AccessControlSmall.md) — 32-bit compact RW lock consuming `ref WaitContext`
- [ResourceAccessControl](ResourceAccessControl.md) — 3-mode lifecycle lock consuming `ref WaitContext`

### Architecture Overview
- [02 — Execution](../../overview/02-execution.md) — `UnitOfWorkContext`, `Deadline` struct, holdoff, `UnitOfWork`

### Research
- [Timeout Taxonomy](../../research/timeout/01-timeout-taxonomy.md) — 7-layer timeout model
- [Timeout Design Patterns](../../research/timeout/02-design-patterns.md) — Absolute deadline pattern, accumulation problem

### Architecture Decision Records
- [ADR-016: Three-Mode ResourceAccessControl](../../adr/016-three-mode-resource-access-control.md)
- [ADR-017: 64-Bit Atomic State](../../adr/017-64bit-access-control-state.md)
- [ADR-018: Adaptive Spin-Wait](../../adr/018-adaptive-spin-wait.md)

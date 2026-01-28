# AccessControl Family — Design Overview

> Entry-point document for Typhon's synchronization primitives based on atomic compare-and-swap.

---

## 1. Overview

The AccessControl family is a set of **struct-based synchronization primitives** that share a common design philosophy:

- **Single atomic word**: All state fits in one `int` (32-bit) or `ulong` (64-bit)
- **CAS-only transitions**: All state changes go through `Interlocked.CompareExchange`
- **No heap allocations**: No wait queues, no OS handles, no managed objects
- **SpinWait for contention**: Spin-then-yield strategy, no thread parking
- **Embeddable**: Value types that can live inline in any struct or class
- **WaitContext-based timeout**: All blocking operations accept `ref WaitContext` (monotonic deadline + cancellation, no accumulation problem)

These primitives are designed for Typhon's microsecond-level performance targets where OS-level synchronization (mutexes, semaphores) would be too expensive.

---

## 2. Type Catalog

| Type | Size | Purpose | Design Doc |
|------|------|---------|------------|
| **AccessControl** | 8 bytes | Full-featured reader-writer lock with built-in diagnostics, waiter fairness, and optional recursion | [AccessControl.md](AccessControl.md) |
| **AccessControlSmall** | 4 bytes | Compact reader-writer lock for space-constrained scenarios | [AccessControlSmall.md](AccessControlSmall.md) |
| **ResourceAccessControl** | 4 bytes | 3-mode lock (Access/Modify/Destroy) for resource lifecycle management | [ResourceAccessControl.md](ResourceAccessControl.md) |

### Removed Types

| Type | Notes |
|------|-------|
| Legacy `AccessControl` (8 bytes, two `volatile int` fields) | Removed - replaced by current `AccessControl` (64-bit atomic state) |
| `SmallAccessControl` | Removed - all usages migrated to `AccessControlSmall` |

---

## 3. Selection Guide

### Decision Flowchart

```
Do you need Shared + Exclusive access?
├── YES
│   ├── Can modifications coexist with readers (append-only/extend-only)?
│   │   └── YES ──► ResourceAccessControl (4 bytes)
│   │
│   ├── Do you need any of: waiter fairness, diagnostics, recursion?
│   │   ├── YES ──► AccessControl (8 bytes)
│   │   └── NO
│   │       └── Is memory budget tight (per-node, per-page, inline in arrays)?
│   │           ├── YES ──► AccessControlSmall (4 bytes)
│   │           └── NO  ──► AccessControl (8 bytes)
│
└── NO (exclusive only)
    └── AccessControlSmall in exclusive-only mode (4 bytes)
```

> **Note:** All three types now support telemetry via `IContentionTarget`. The decision is based on size constraints and behavioral requirements.

### Quick Selection Table

| Scenario | Recommended Type | Why |
|----------|-----------------|-----|
| Page cache latch | **AccessControl** | Needs fairness to prevent writer starvation under heavy reads |
| B+Tree node lock | **AccessControlSmall** | Thousands of nodes, 4-byte budget; short critical sections |
| ComponentTable access | **AccessControl** | Telemetry needed; complex promote/demote patterns |
| Revision chain traversal | **ResourceAccessControl** | Readers don't block appenders; destruction must drain |
| Chained allocator | **ResourceAccessControl** | Enumeration + extend + destroy lifecycle |
| TransactionChain lock | **AccessControl** | Central lock, contention tracking valuable |
| Internal bitmap guard | **AccessControlSmall** | Simple, fast, compact |

---

## 4. Feature Matrix

### State & Capacity

| | **AccessControl** | **AccessControlSmall** | **ResourceAccessControl** |
|---|---|---|---|
| **Size** | 8 bytes (`ulong`) | 4 bytes (`int`) | 4 bytes (`int`) |
| **State encoding** | Explicit 2-bit field | Implicit from ThreadId/counter | Explicit flags + counter |
| **Thread ID bits** | 16 (max 65,535) | 16 (max 65,535) | 16 (max 65,535) |
| **Shared/Accessing counter** | 8 bits (max 255) | 15 bits (max 32,767) | 8 bits (max 255) |
| **Contention flag** | 1 bit (sticky) | 1 bit (sticky) | 1 bit (sticky) |
| **Reserved bits** | 13 | 0 | 5 |

### Access Modes

| Mode | **AccessControl** | **AccessControlSmall** | **ResourceAccessControl** |
|---|---|---|---|
| Shared / Accessing | Multiple concurrent | Multiple concurrent | Multiple concurrent |
| Exclusive / Modify | Single holder | Single holder | Single holder |
| Destroy | — | — | Terminal, drains all holders |

### Mode Compatibility

**AccessControl & AccessControlSmall:**
```
            Shared    Exclusive
Shared        Yes        No
Exclusive      No        No
```

**ResourceAccessControl:**
```
            Accessing   Modify   Destroy
Accessing       Yes       Yes       No
Modify          Yes        No       No
Destroy          No        No       No
```

### API Surface

| Operation | **AccessControl** | **AccessControlSmall** | **ResourceAccessControl** |
|---|:---:|:---:|:---:|
| Enter shared/accessing | `EnterSharedAccess` | `EnterSharedAccess` | `EnterAccessing` |
| Exit shared/accessing | `ExitSharedAccess` | `ExitSharedAccess` | `ExitAccessing` |
| Enter exclusive/modify | `EnterExclusiveAccess` | `EnterExclusiveAccess` | `EnterModify` |
| Exit exclusive/modify | `ExitExclusiveAccess` | `ExitExclusiveAccess` | `ExitModify` |
| Try enter exclusive | `TryEnterExclusiveAccess` | `TryEnterExclusiveAccess` | `TryEnterModify` |
| Try enter shared | — | — | `TryEnterAccessing` |
| Promote | `TryPromoteToExclusiveAccess` | `TryPromoteToExclusiveAccess` | `TryPromoteToModify` |
| Demote | `DemoteFromExclusiveAccess` | `DemoteFromExclusiveAccess` | `DemoteFromModify` |
| Destroy | — | — | `EnterDestroy` |
| Generic enter/exit | — | `Enter(bool)` / `Exit(bool)` | — |
| Scoped guards | — | — | `EnterAccessingScoped` / `EnterModifyScoped` |
| Reset | `Reset` | `Reset` | `Reset` |

### Behavioral Features

| Feature | **AccessControl** | **AccessControlSmall** | **ResourceAccessControl** |
|---|:---:|:---:|:---:|
| WaitContext (`ref WaitContext`) | Yes | Yes | Yes |
| Telemetry (`IContentionTarget`) | Yes | Yes | Yes |
| Contention flag (`WasContended`) | Yes (sticky) | Yes (sticky) | Yes (sticky) |
| Waiter tracking | 3 counters (S/E/P) | — | — |
| Waiter fairness | Promoter > Exclusive > Shared | None | MODIFY_PENDING blocks new accessors |
| Recursion | Opt-in via flag (proposed) | — | — |
| Reentrant detection | Proposed | Throws on re-entry | — |
| Overflow detection | Debug.Assert | Throws | Throws |
| Exit validation | Debug.Assert (wrong thread) | Throws (wrong thread) | Debug.Assert |
| Built-in diagnostics | Always-on (proposed) | — | — |
| DebuggerDisplay | Yes | Yes | Yes |

---

## 5. Shared Concepts

### 5.1 Thread ID Storage

All three types store the owning thread's `Environment.CurrentManagedThreadId` in a **16-bit field** (max 65,535). This provides:

- **Consistency**: All AccessControl types use the same 16-bit Thread ID size
- **Headroom**: Supports servers with 500+ cores and high thread counts
- **Future-proofing**: .NET managed thread IDs are sequentially assigned, but the pool can grow significantly in long-running applications

| Type | Thread ID Bits | Max Value |
|------|---------------|-----------|
| `AccessControl` | 16 | 65,535 |
| `AccessControlSmall` | 16 | 65,535 |
| `ResourceAccessControl` | 16 | 65,535 |

Truncation is performed via bit mask (`& 0xFFFF`), not modulo. Two threads with IDs that collide after truncation would appear to be the same owner — this is a theoretical concern only for applications with >65,535 concurrent threads, which is well beyond typical usage.

### 5.2 IContentionTarget

The callback-based telemetry interface used by all three AccessControl types:

```csharp
public interface IContentionTarget
{
    TelemetryLevel TelemetryLevel { get; }    // None, Light, Deep
    void RecordContention(long waitTimeUs);    // Light: aggregate counter
    void LogLockOperation(LockOperation op, long elapsedUs);  // Deep: full history
}
```

- **None**: No callbacks, zero overhead (null target or `TelemetryLevel.None`)
- **Light**: `RecordContention` called only when a thread had to wait
- **Deep**: `LogLockOperation` called for every Enter/Exit

The target is always the last optional parameter. Resources that want telemetry implement `IContentionTarget` and pass `this` to Enter/Exit methods.

See [Observability: Track 4](../../overview/09-observability.md#track-4-per-resource-telemetry-icontentiontarget) for the broader telemetry architecture.

### 5.3 SpinWait Strategy

All types use `System.Threading.SpinWait` for contention handling:

1. First iterations: busy-spin (`Thread.SpinWait`)
2. After threshold: `Thread.Yield()` (give up time slice)
3. After more iterations: `Thread.Sleep(0)` then `Thread.Sleep(1)`

This adaptive strategy keeps latency minimal for short-lived contention while avoiding CPU waste for longer waits. No type uses OS-level parking (futex, WaitHandle) — all are pure userspace.

### 5.4 CAS Loop Pattern

Every type follows the same atomic update pattern:

```
1. Read current state (snapshot)
2. Validate preconditions on snapshot
3. Compute new state
4. CAS(ref field, newState, snapshot)
5. If CAS failed → re-read and retry from step 1
```

`AccessControl` encapsulates this in `LockData` (ref struct with `_initial` and `_staging` fields). `AccessControlSmall` uses `AtomicChange`. `ResourceAccessControl` uses inline CAS loops.

### 5.5 WaitContext

All blocking operations accept a `ref WaitContext` parameter that bundles timeout and cancellation into a single pass-by-reference value. This replaces the earlier `ref Deadline`-only approach and eliminates the need for separate `TimeSpan?` + `CancellationToken` parameters.

#### 5.5.1 Struct Layout

```csharp
/// <summary>
/// Bundles a monotonic deadline with an optional cancellation token.
/// Passed by ref to all lock primitives for zero-copy sharing across nested calls.
/// </summary>
public readonly struct WaitContext   // 16 bytes, immutable
{
    public readonly Deadline Deadline;              // 8 bytes — monotonic absolute time
    public readonly CancellationToken Token;        // 8 bytes — cooperative cancellation
}
```

**Why a `readonly struct`?**
`WaitContext` is immutable once created — deadlines and cancellation tokens are set at entry and never change mid-wait. The `readonly` modifier enforces this at compile time and prevents defensive copies when accessing members. It must also be storable as a field of `UnitOfWorkContext` (a class), which rules out `ref struct`. Passed by `ref` to lock primitives for zero-copy semantics.

**Why 16 bytes?**
- `Deadline` (8 bytes): Monotonic absolute time via `Stopwatch.GetTimestamp()`. Avoids wall-clock jumps and timeout accumulation across nested calls.
- `CancellationToken` (8 bytes): Standard .NET cooperative cancellation. Lock primitives check `Token.IsCancellationRequested` alongside `Deadline.IsExpired` during spin-wait loops.

#### 5.5.2 Default Semantics (Fail-Safe)

`default(WaitContext)` produces:
- `Deadline` = `default(Deadline)` = `_ticks = 0` = **already expired**
- `Token` = `default(CancellationToken)` = **none** (never cancelled)

**Result:** Any lock primitive receiving `default(WaitContext)` **fails immediately** — it will not spin or block. This is the fail-safe behavior: callers must explicitly construct a `WaitContext` with a real deadline to allow waiting.

#### 5.5.3 NullRef Pattern (Infinite Wait)

For callers that want **infinite wait with no cancellation** (e.g., startup paths, single-threaded initialization):

```csharp
// Explicit opt-in to unbounded wait
lock.EnterExclusiveAccess(ref Unsafe.NullRef<WaitContext>());
```

Lock primitives detect `Unsafe.IsNullRef(ref ctx)` and treat it as:
- **No deadline check** — never expires
- **No cancellation check** — never cancelled
- **Fastest spin path** — skips both `IsExpired` and `IsCancellationRequested` per iteration

This replaces the previous `Deadline.Infinite` pattern with a more explicit opt-in that avoids allocating a `WaitContext` on the stack entirely.

#### 5.5.4 Factory Methods

```csharp
public readonly struct WaitContext
{
    public readonly Deadline Deadline;
    public readonly CancellationToken Token;

    private WaitContext(Deadline deadline, CancellationToken token)
    {
        Deadline = deadline;
        Token = token;
    }

    /// <summary>Create with deadline only (no cancellation).</summary>
    public static WaitContext FromDeadline(Deadline deadline)
        => new(deadline, default);

    /// <summary>Create from relative timeout (no cancellation).</summary>
    public static WaitContext FromTimeout(TimeSpan timeout)
        => new(Deadline.FromTimeout(timeout), default);

    /// <summary>Create from relative timeout + cancellation token.</summary>
    public static WaitContext FromTimeout(TimeSpan timeout, CancellationToken token)
        => new(Deadline.FromTimeout(timeout), token);

    /// <summary>Create with cancellation only (infinite deadline).</summary>
    public static WaitContext FromToken(CancellationToken token)
        => new(Deadline.Infinite, token);

    /// <summary>Check if the wait should stop (deadline expired OR cancellation requested).</summary>
    public bool ShouldStop => Deadline.IsExpired || Token.IsCancellationRequested;
}
```

#### 5.5.5 Usage in Lock Primitives

All `Enter*` methods that can block accept `ref WaitContext ctx`:

```csharp
public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null)
{
    // NullRef = infinite wait, skip all checks
    bool isNullRef = Unsafe.IsNullRef(ref ctx);

    SpinWait spin = default;
    while (true)
    {
        // Check wait termination (unless NullRef)
        if (!isNullRef && ctx.ShouldStop)
            return false;

        // ... CAS attempt ...

        spin.SpinOnce();
    }
}
```

#### 5.5.6 Relationship to Deadline

`WaitContext` **wraps** [`Deadline`](Deadline.md) — it does not replace it. The `Deadline` struct remains the monotonic time primitive:

- `Deadline.Infinite` — No timeout (`long.MaxValue`)
- `Deadline.Zero` — Already expired (immediate fail)
- `Deadline.FromTimeout(TimeSpan)` — Converts relative timeout to absolute deadline
- `deadline.IsExpired` — Check if deadline has passed

`WaitContext` adds `CancellationToken` alongside `Deadline` and provides the `ShouldStop` convenience that checks both.

#### 5.5.7 Why `ref WaitContext` Instead of Separate Parameters

| Concern | `TimeSpan? + CancellationToken` | `ref Deadline` (previous) | `ref WaitContext` |
|---------|--------------------------------|---------------------------|-------------------|
| Accumulation | Each nested call gets a fresh timeout | Single deadline shared | Single context shared |
| Clock source | Wall clock (can jump) | Monotonic | Monotonic (via Deadline) |
| Copy cost | 24+ bytes per call | 0 bytes (by ref) | 0 bytes (by ref) |
| Cancellation | Separate parameter | Not supported | Integrated |
| Default semantics | `null` = infinite (implicit) | `default` = expired (fail-safe) | `default` = expired (fail-safe) |
| Infinite wait | Pass `null` (implicit) | Pass `Deadline.Infinite` | Pass `NullRef` (explicit opt-in) |

#### 5.5.8 Holdoff (Not Part of WaitContext)

Holdoff is a session-level counter in `UnitOfWorkContext` that suppresses `ThrowIfCancelled()` at yield points (e.g., between transaction steps). It is **not** stored in `WaitContext` because:

- Holdoff is mutable session state (incremented/decremented), not wait parameters
- Lock primitives don't use holdoff — they always respect their deadline and cancellation token
- Holdoff suppresses cancellation at **yield points**, which is an `UnitOfWorkContext` concern, not a lock concern

### 5.6 Naming Conventions

| Concept | AccessControl | AccessControlSmall | ResourceAccessControl |
|---------|--------------|-------------------|----------------------|
| Read mode | Shared | Shared | Accessing |
| Write mode | Exclusive | Exclusive | Modify |
| Acquire | `Enter___Access` | `Enter___Access` | `Enter___` |
| Release | `Exit___Access` | `Exit___Access` | `Exit___` |
| Upgrade | `TryPromoteTo___` | `TryPromoteTo___` | `TryPromoteTo___` |
| Downgrade | `DemoteFrom___` | — | `DemoteFrom___` |

---

## 6. Historical Notes

### Legacy AccessControl (REMOVED)

The original 8-byte `AccessControl` struct used two separate `volatile int` fields (`_lockedByThreadId` + `_sharedUsedCounter`) instead of a single atomic word. This made transitions non-atomic and had several limitations:

- No timeout or cancellation support
- No telemetry
- Non-atomic state transitions
- Pure busy-spin with no adaptive yielding

It has been fully replaced by the current `AccessControl` struct (64-bit atomic state with WaitContext support).

### SmallAccessControl (REMOVED)

The `SmallAccessControl` type has been removed. All usages have been migrated to `AccessControlSmall` using `EnterExclusiveAccess()` / `ExitExclusiveAccess()`. This consolidates on a single compact lock primitive that provides:

- Timeout and cancellation support via method parameters
- Thread ownership validation
- Shared mode capability if needed in the future

---

## 7. References

### Design Documents
- [Deadline](Deadline.md) — Monotonic absolute time primitive (8-byte `readonly struct`)
- [WaitContext](WaitContext.md) — Universal wait-parameter type (deadline + cancellation)
- [AccessControl](AccessControl.md) — Full-featured 64-bit RW lock
- [ResourceAccessControl](ResourceAccessControl.md) — 3-mode resource lifecycle lock
- [AccessControlSmall](AccessControlSmall.md) — Compact 32-bit RW lock

### Architecture Overview
- [01 — Concurrency](../../overview/01-concurrency.md) — Locks, latches, thread-safety patterns

### Architecture Decision Records
- [ADR-016: Three-Mode ResourceAccessControl](../../adr/016-three-mode-resource-access-control.md)
- [ADR-017: 64-Bit Atomic State](../../adr/017-64bit-access-control-state.md)
- [ADR-018: Adaptive Spin-Wait](../../adr/018-adaptive-spin-wait.md)

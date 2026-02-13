# #42 — UnitOfWorkContext Struct

**Date:** 2026-02-13
**Status:** Implemented
**GitHub Issue:** #42
**Decisions:** D1, D2, D7, D8

> 💡 **Quick summary:** Create a 24-byte `UnitOfWorkContext` struct that embeds a `WaitContext` (deadline + cancellation) and adds epoch ID and holdoff counter. `HoldoffScope` ref struct provides RAII critical sections. Lock sites can use the embedded `WaitContext` directly — no construction needed.

## Overview

`UnitOfWorkContext` is the **execution context** that flows through all operations inside a Unit of Work (or standalone transaction). It replaces ad-hoc `WaitContext` construction at individual call sites with a single context carrying a unified deadline.

Every major database engine has this pattern:
- SQL Server: `SOS_Task`
- PostgreSQL: `PGPROC`
- MySQL: `THD`

Typhon's implementation is a lightweight value type — no allocation, no pooling, no GC pressure.

### Design Deviation from Overview

The overview doc (§2.4) specifies a **pooled class** with managed references. We chose a **struct** because:

1. All fields are value types (`Deadline` = 8B struct, `CancellationToken` = 8B struct, `ushort` + `int`)
2. The `CancellationTokenSource` that generates the token will be owned externally — by the future `UnitOfWork` class or by `DeadlineWatchdog`
3. Zero allocation on the hot path (commit)
4. `ref` passing is natural for Typhon's synchronous call chains

The overview's pooling rationale (avoiding CTS allocations) still applies but shifts to the `UnitOfWork` class (Tier 3+) which will own the CTS and pass the resulting token into the context.

## Struct Layout

`UnitOfWorkContext` **embeds a `WaitContext`** rather than duplicating its fields. This avoids redundant field definitions and allows lock call sites to pass `ref ctx.WaitContext` directly — zero construction cost.

```csharp
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct UnitOfWorkContext
{
    // ═══════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Embedded wait context carrying deadline + cancellation token.
    /// Lock call sites can use <c>ref ctx.WaitContext</c> directly.
    /// Non-readonly to allow <c>ref</c> passing to lock primitives.
    /// </summary>
    public WaitContext WaitContext;                       // 16 bytes (Deadline 8B + Token 8B)

    /// <summary>UoW epoch for revision stamping (reserved for Tier 3).</summary>
    public readonly ushort EpochId;                      // 2 bytes

    private readonly ushort _padding;                    // 2 bytes (alignment)

    /// <summary>
    /// Holdoff counter. When > 0, <see cref="ThrowIfCancelled"/> is a no-op.
    /// Supports nesting (increment on enter, decrement on exit).
    /// </summary>
    internal int _holdoffCount;                          // 4 bytes

    // Total: 24 bytes (3 × 8-byte qwords, naturally aligned)
}
```

### Why embed WaitContext instead of separate Deadline + Token?

Lock primitives (`AccessControl.EnterSharedAccess`, etc.) take `ref WaitContext`. By embedding the field:

```csharp
// Direct ref passing — zero copy, zero construction:
lock.EnterSharedAccess(ref ctx.WaitContext);

// vs. constructing a new WaitContext each time (previous design):
var wc = ctx.ToWaitContext();
lock.EnterSharedAccess(ref wc);
```

The `WaitContext` field is intentionally **non-readonly** so that `ref ctx.WaitContext` produces a mutable reference compatible with lock primitive signatures. The field itself is never mutated after construction (WaitContext is a readonly struct internally).

For **deadline composition** (UoW deadline + subsystem-specific timeout), a new WaitContext must still be constructed since the deadline differs:

```csharp
var composed = WaitContext.FromDeadline(
    Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));
lock.EnterSharedAccess(ref composed);
```

### Memory Layout Verification

```
Offset  Size  Field
  0      16   WaitContext
  0       8     ├─ Deadline (_ticks : long)
  8       8     └─ Token (CancellationToken struct)
 16       2   EpochId
 18       2   _padding
 20       4   _holdoffCount
────────────
 24 bytes total
```

A size-verification test ensures this doesn't drift:

```csharp
[Test]
public void UnitOfWorkContext_Size_Is24Bytes()
{
    Assert.That(Unsafe.SizeOf<UnitOfWorkContext>(), Is.EqualTo(24));
}
```

## Factory Methods

```csharp
// ═══════════════════════════════════════════════════════════════
// Construction
// ═══════════════════════════════════════════════════════════════

/// <summary>Primary constructor — WaitContext + epoch.</summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public UnitOfWorkContext(WaitContext waitContext, ushort epochId = 0)
{
    WaitContext = waitContext;
    EpochId = epochId;
    _padding = 0;
    _holdoffCount = 0;
}

/// <summary>Primary constructor — deadline + cancellation token.</summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public UnitOfWorkContext(Deadline deadline, CancellationToken token, ushort epochId = 0)
    : this(new WaitContext(deadline, token), epochId)
{
}

/// <summary>Create from a relative timeout (no cancellation, no epoch).</summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static UnitOfWorkContext FromTimeout(TimeSpan timeout)
    => new(WaitContext.FromTimeout(timeout));

/// <summary>Create from a relative timeout + cancellation token.</summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static UnitOfWorkContext FromTimeout(TimeSpan timeout, CancellationToken token)
    => new(WaitContext.FromTimeout(timeout, token));
```

### Default / None Semantics

Following the `Deadline` / `WaitContext` pattern:

```csharp
/// <summary>
/// <c>default(UnitOfWorkContext)</c> is <b>already expired</b> (fail-safe).
/// The embedded WaitContext has an expired deadline and the holdoff count is zero, so
/// <see cref="ThrowIfCancelled"/> will throw immediately.
/// </summary>
/// <remarks>
/// This matches Typhon's fail-safe convention: default → fail, not default → infinite.
/// Use <see cref="None"/> for unbounded operations.
/// </remarks>

/// <summary>
/// Unbounded context: infinite deadline, no cancellation. For internal operations
/// that should not be subject to timeout (e.g., cleanup, rollback).
/// </summary>
public static UnitOfWorkContext None
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => new(Deadline.Infinite, CancellationToken.None);
}
```

## Core Methods

### ThrowIfCancelled — Yield Point Check

```csharp
/// <summary>
/// Cooperative cancellation check. Call at yield points (safe locations where
/// aborting won't leave data structures in an inconsistent state).
/// </summary>
/// <remarks>
/// <para>If <see cref="_holdoffCount"/> > 0 (inside a holdoff region), this is a no-op.
/// Cancellation is deferred until the holdoff exits.</para>
/// <para>Checks deadline first (cheaper than token check on most paths), then cancellation token.
/// Accesses fields through embedded <see cref="WaitContext"/> — the JIT resolves struct
/// field offsets at compile time, producing identical code to direct field access.</para>
/// </remarks>
/// <exception cref="TyphonTimeoutException">Deadline has expired (outside holdoff).</exception>
/// <exception cref="OperationCanceledException">Token was cancelled (outside holdoff).</exception>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void ThrowIfCancelled()
{
    if (_holdoffCount > 0)
    {
        return;
    }

    if (WaitContext.Deadline.IsExpired)
    {
        ThrowDeadlineExpired();
    }

    WaitContext.Token.ThrowIfCancellationRequested();
}

[MethodImpl(MethodImplOptions.NoInlining)]
private void ThrowDeadlineExpired()
{
    // TransactionTimeoutException requires a transaction ID and wait duration.
    // At this layer we don't have the transaction ID — the caller (Transaction.Commit)
    // catches and re-throws with full context. Use the general TyphonTimeoutException here.
    throw new TyphonTimeoutException(
        TyphonErrorCode.TransactionTimeout,
        "Operation deadline expired",
        WaitContext.Deadline.Remaining); // Remaining will be TimeSpan.Zero when expired
}
```

**Design note:** `ThrowDeadlineExpired()` is `[NoInlining]` following `ThrowHelper` convention — the JIT won't inline throw paths into the hot `ThrowIfCancelled()` method, keeping it small and cache-friendly.

**Exception type:** We throw `TyphonTimeoutException` (not `TransactionTimeoutException`) because at the context level we don't know the transaction ID. The `Transaction.Commit()` method wraps this in a `TransactionTimeoutException` with full context (see doc 04).

### Holdoff — Critical Section Protection

```csharp
/// <summary>
/// Enter a holdoff region. While in holdoff, <see cref="ThrowIfCancelled"/> is a no-op.
/// Returns a disposable scope guard. Supports nesting.
/// </summary>
/// <example>
/// <code>
/// using var holdoff = ctx.EnterHoldoff();
/// // Critical section — cancellation deferred
/// SplitBTreeNode(ref ctx);
/// // holdoff.Dispose() decrements counter
/// </code>
/// </example>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public HoldoffScope EnterHoldoff() => new(ref this);

/// <summary>Increment holdoff counter (prefer <see cref="EnterHoldoff"/> for RAII safety).</summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void BeginHoldoff() => _holdoffCount++;

/// <summary>Decrement holdoff counter.</summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void EndHoldoff()
{
    Debug.Assert(_holdoffCount > 0, "EndHoldoff called without matching BeginHoldoff");
    _holdoffCount--;
}
```

### Properties

```csharp
/// <summary>The embedded deadline.</summary>
public Deadline Deadline => WaitContext.Deadline;

/// <summary>The embedded cancellation token.</summary>
public CancellationToken Token => WaitContext.Token;

/// <summary>True if the deadline has expired.</summary>
public bool IsExpired => WaitContext.Deadline.IsExpired;

/// <summary>True if the cancellation token has been triggered.</summary>
public bool IsCancellationRequested => WaitContext.Token.IsCancellationRequested;

/// <summary>True if currently inside a holdoff region.</summary>
public bool IsInHoldoff => _holdoffCount > 0;

/// <summary>Remaining time until deadline expires.</summary>
public TimeSpan Remaining => WaitContext.Deadline.Remaining;
```

## HoldoffScope Ref Struct

Follows the `EpochGuard` pattern exactly (`src/Typhon.Engine/Concurrency/EpochGuard.cs`):

```csharp
/// <summary>
/// RAII scope guard for holdoff regions. Enter via <see cref="UnitOfWorkContext.EnterHoldoff"/>,
/// exit via <see cref="Dispose"/>. Supports nesting — inner holdoffs keep the outer holdoff active.
/// </summary>
/// <remarks>
/// <para>Uses a <c>ref</c> field (C# 11+) to mutate the caller's <see cref="UnitOfWorkContext"/>
/// on the stack. This requires .NET 7+ / C# 11+, which Typhon targets (.NET 10 / C# 13).</para>
/// <para>This is a <c>ref struct</c> to prevent heap allocation and boxing.
/// Always use in a <c>using</c> statement or explicit try/finally.</para>
/// </remarks>
[PublicAPI]
public ref struct HoldoffScope
{
    private ref UnitOfWorkContext _ctx;
    private bool _disposed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal HoldoffScope(ref UnitOfWorkContext ctx)
    {
        _ctx = ref ctx;
        _disposed = false;
        _ctx._holdoffCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Debug.Assert(_ctx._holdoffCount > 0, "HoldoffScope.Dispose: holdoff count underflow");
            _ctx._holdoffCount--;
        }
    }
}
```

**Key patterns from `EpochGuard`:**
- `_disposed` flag prevents double-dispose
- `Debug.Assert` catches misuse in debug builds
- `ref struct` prevents heap allocation (compile-time enforced)
- `[AggressiveInlining]` on both constructor and Dispose

## WaitContext Integration

### Direct ref passing at lock sites

Since `WaitContext` is embedded as a non-readonly field, lock call sites can pass it directly:

```csharp
// SIMPLE CASE: pass embedded WaitContext directly — zero copy
lock.EnterSharedAccess(ref ctx.WaitContext);
```

This is the primary benefit of embedding over separate fields.

### Factory on WaitContext (fill TODO at line 109-112)

**File:** `src/Typhon.Engine/Concurrency/WaitContext.cs`

The TODO at line 109-112 can now be filled. Since the WaitContext is embedded, this factory simply reads it:

```csharp
/// <summary>Create from an existing <see cref="UnitOfWorkContext"/> (reads embedded WaitContext).</summary>
/// <param name="ctx">The execution context containing the embedded WaitContext.</param>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static WaitContext FromUnitOfWorkContext(ref UnitOfWorkContext ctx)
    => ctx.WaitContext;
```

**Note:** This is now a trivial 16-byte copy. Callers that prefer the explicit factory for readability can use it, but `ref ctx.WaitContext` is preferred at lock sites.

### Deadline Composition

When internal operations need a tighter deadline than the UoW context provides, compose with `Deadline.Min`. In this case, a new WaitContext must be constructed (the embedded one has the UoW deadline, not the composed one):

```csharp
// Inside Transaction.Commit — BTree lock should respect both UoW deadline and subsystem limit
var wc = WaitContext.FromDeadline(
    Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout)));
if (!index.Control.EnterExclusiveAccess(ref wc))
{
    ThrowHelper.ThrowLockTimeout("BTree/Index", TimeoutOptions.Current.BTreeLockTimeout);
}
```

This ensures both the overall UoW deadline and subsystem-specific limits are respected.

## Files to Create/Modify

### New Files

| File | Contents |
|------|----------|
| `src/Typhon.Engine/Concurrency/UnitOfWorkContext.cs` | Struct + factory methods + ThrowIfCancelled + holdoff + properties |
| `src/Typhon.Engine/Concurrency/HoldoffScope.cs` | Ref struct scope guard |
| `test/Typhon.Engine.Tests/Concurrency/UnitOfWorkContextTests.cs` | Unit tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Typhon.Engine/Concurrency/WaitContext.cs` | Add `FromUnitOfWorkContext(ref UnitOfWorkContext)` factory at line 109 |

### Decision: Separate files or combined?

`HoldoffScope` could live in the same file as `UnitOfWorkContext`. However, following the `EpochGuard` pattern (separate file from `EpochManager`), we put it in its own file for discoverability. If the team prefers co-location, both types can live in `UnitOfWorkContext.cs`.

## Testing Strategy

### Unit Tests (~15 tests)

**Construction & defaults:**
- `default(UnitOfWorkContext)` is already expired (fail-safe)
- `UnitOfWorkContext.None` has infinite deadline, no cancellation
- `FromTimeout(TimeSpan)` creates correct deadline
- `FromTimeout(TimeSpan, CancellationToken)` carries both

**Size & layout:**
- `Unsafe.SizeOf<UnitOfWorkContext>() == 24`

**ThrowIfCancelled:**
- Expired deadline → throws `TyphonTimeoutException`
- Cancelled token → throws `OperationCanceledException`
- Unexpired + uncancelled → no throw
- In holdoff → no throw even if expired
- In holdoff → no throw even if cancelled
- Exit holdoff with expired deadline → throws on next check

**Holdoff nesting:**
- Enter 1 holdoff → `IsInHoldoff == true`
- Enter 2 holdoffs → `IsInHoldoff == true`, count is 2
- Exit inner holdoff → `IsInHoldoff == true` (count is 1)
- Exit outer holdoff → `IsInHoldoff == false`

**HoldoffScope:**
- Using pattern increments/decrements correctly
- Double-dispose is safe (no-op)
- Nested scopes work correctly

**WaitContext integration:**
- `ctx.WaitContext` returns embedded WaitContext
- `WaitContext.FromUnitOfWorkContext(ref ctx)` returns copy of embedded WaitContext
- `Deadline` and `Token` convenience properties delegate to embedded WaitContext

## Acceptance Criteria (from Issue #42)

- [ ] `UnitOfWorkContext` struct implemented with all fields (24 bytes)
- [ ] `WaitContext.FromUnitOfWorkContext()` factory implemented
- [ ] Holdoff mechanism prevents cancellation during critical sections
- [ ] `ThrowIfCancelled()` throws `TyphonTimeoutException` when expired/cancelled (outside holdoff)
- [ ] `HoldoffScope` ref struct with RAII pattern
- [ ] Unit tests for all construction, cancellation, and holdoff scenarios

## Appendix: Elapsed Time for Timeout Diagnostics

`ThrowDeadlineExpired()` needs to report how long the operation ran. `Deadline` stores the absolute expiry time but not the creation time, so it can't compute elapsed.

**Two options:**

**Option A (recommended): Use the caller's start ticks.** `Transaction.Commit()` already records `startTicks` (line 1596). The catch block can compute elapsed from that. No changes to `UnitOfWorkContext` needed.

```csharp
// In Transaction.Commit — the caller has start time already
var startTicks = Stopwatch.GetTimestamp();
try
{
    // ... commit logic ...
}
catch (TyphonTimeoutException ex)
{
    var elapsed = Stopwatch.GetElapsedTime(startTicks);
    throw new TransactionTimeoutException(TSN, elapsed);
}
```

**Option B: Add `_startTicks` field to UnitOfWorkContext.** This adds 8 bytes (total 32B) but provides accurate elapsed time from any yield point. Deferred unless diagnostics need it.

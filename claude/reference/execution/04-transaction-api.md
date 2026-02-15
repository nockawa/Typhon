# #45 — Transaction API: Add UnitOfWorkContext Parameter

**Date:** 2026-02-13
**Status:** Implemented
**GitHub Issue:** #45
**Decisions:** D4, D5, D6

> 💡 **Quick summary:** Add new `Commit(ref UnitOfWorkContext, ...)` and `Rollback(ref UnitOfWorkContext)` overloads. Existing parameterless overloads become backward-compatible wrappers using a default timeout. Inside commit, replace hardcoded `WaitContext.FromTimeout(...)` with `ctx.ToWaitContext()` for deadline propagation through all lock acquisitions.

## Overview

This is the integration sub-issue that wires `UnitOfWorkContext` (#42) and yield points/holdoff (#44) into the Transaction API. After this, a user can set a deadline on an operation and have it propagate through every lock, page access, and index operation within the commit path.

### Current State

```csharp
// Today: no timeout, no cancellation
public bool Commit(ConcurrencyConflictHandler handler = null)   // Transaction.cs:1576
public bool Rollback()                                          // Transaction.cs:1387
```

Inside `Commit()`, lock acquisitions use per-subsystem timeouts:
```csharp
var wcTail = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wcTail))
{
    ThrowHelper.ThrowLockTimeout("TransactionChain/CommitTailCheck", ...);
}
```

### Target State

```csharp
// New: deadline flows through all lock acquisitions
public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
public bool Rollback(ref UnitOfWorkContext ctx)

// Existing: backward-compatible wrapper with default timeout
public bool Commit(ConcurrencyConflictHandler handler = null)
public bool Rollback()
```

## API Changes

### New Commit Overload

```csharp
/// <summary>
/// Commit the transaction with deadline and cancellation propagation.
/// </summary>
/// <param name="ctx">Execution context carrying deadline and cancellation token.
/// The deadline propagates to all internal lock acquisitions. If the deadline
/// expires before commit starts, <see cref="TyphonTimeoutException"/> is thrown.
/// Once commit begins modifying data, it runs to completion (holdoff).</param>
/// <param name="handler">Optional conflict resolution handler.</param>
/// <returns>True if committed successfully, false if transaction was empty or already processed.</returns>
/// <exception cref="TyphonTimeoutException">Deadline expired before commit started.</exception>
/// <exception cref="OperationCanceledException">Cancellation token was triggered before commit started.</exception>
/// <exception cref="LockTimeoutException">A lock acquisition timed out during commit.</exception>
public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
{
    AssertThreadAffinity();

    if (State is TransactionState.Created) return true;
    if (State is TransactionState.Rollbacked or TransactionState.Committed) return false;

    // ── Yield point: safe to cancel before any modifications ──
    ctx.ThrowIfCancelled();

    using var activity = TyphonActivitySource.StartActivity("Transaction.Commit");
    activity?.SetTag(TyphonSpanAttributes.TransactionTsn, TSN);
    activity?.SetTag(TyphonSpanAttributes.TransactionComponentCount, _componentInfos.Count);

    var startTicks = Stopwatch.GetTimestamp();

    // ── Holdoff: entire commit loop runs to completion ──
    using var holdoff = ctx.EnterHoldoff();

    var conflictSolver = handler != null ? GetConflictSolver() : null;
    var context = new CommitContext { IsRollback = false, Solver = conflictSolver, Ctx = ref ctx };
    var hasConflict = false;

    // Process every Component Type and their components
    foreach (var kvp in _componentInfos)
    {
        // ... existing loop body (lines 1605-1651) ...
        // CommitComponent now receives ctx via CommitContext
    }

    // ... existing conflict check and state update ...

    State = TransactionState.Committed;

    var elapsedUs = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;
    _dbe?.RecordCommitDuration(elapsedUs);

    return true;
}
```

### Backward-Compatible Wrapper

```csharp
/// <summary>
/// Commit the transaction using default timeout from <see cref="TimeoutOptions"/>.
/// </summary>
/// <remarks>
/// This overload exists for backward compatibility. Prefer the overload accepting
/// <see cref="UnitOfWorkContext"/> for deadline propagation.
/// </remarks>
public bool Commit(ConcurrencyConflictHandler handler = null)
{
    var ctx = UnitOfWorkContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
    return Commit(ref ctx, handler);
}
```

### New Rollback Overload

```csharp
/// <summary>
/// Rollback the transaction. Runs entirely in holdoff (cleanup must complete).
/// </summary>
/// <param name="ctx">Execution context. Rollback runs in holdoff regardless of deadline state,
/// ensuring cleanup always completes.</param>
public bool Rollback(ref UnitOfWorkContext ctx)
{
    AssertThreadAffinity();

    if (State is TransactionState.Created) return true;
    if (State is TransactionState.Rollbacked or TransactionState.Committed) return false;

    // No yield point for rollback — cleanup must always complete
    using var holdoff = ctx.EnterHoldoff();

    // ... existing rollback logic (lines 1403-1465) ...

    State = TransactionState.Rollbacked;
    return true;
}

/// <summary>Backward-compatible wrapper.</summary>
public bool Rollback()
{
    var ctx = UnitOfWorkContext.None;  // Infinite deadline for rollback
    return Rollback(ref ctx);
}
```

**Design note:** Rollback uses `UnitOfWorkContext.None` (infinite deadline) in the backward-compatible wrapper. Rollback should never timeout — it's a cleanup operation.

## CommitContext Update

The existing `CommitContext` ref struct carries per-component-iteration state. Add a ref to the UnitOfWorkContext:

```csharp
internal ref struct CommitContext
{
    public long PrimaryKey;
    public ComponentInfoBase Info;
    public ref ComponentInfoBase.CompRevInfo CompRevInfo;
    public ConcurrencyConflictSolver Solver;
    public bool IsRollback;
    public ref UnitOfWorkContext Ctx;  // ← NEW: execution context for lock acquisitions
}
```

**C# 13 ref field in ref struct:** `CommitContext` is already a `ref struct` (it has `ref CompRevInfo`), so adding `ref UnitOfWorkContext` is natural.

## Deadline Composition in Lock Acquisitions

Inside the commit path, lock acquisitions currently use per-subsystem timeouts:

```csharp
// BEFORE (current):
var wcTail = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);

// AFTER — simple case (UoW deadline only, no subsystem limit):
lock.EnterSharedAccess(ref ctx.WaitContext);  // Direct ref passing, zero copy

// AFTER — with deadline composition (UoW deadline + subsystem limit):
var wcTail = WaitContext.FromDeadline(
    Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout)));
```

`Deadline.Min` ensures the **tighter** of the two deadlines wins:
- If UoW deadline is 500ms from now and subsystem limit is 10s → uses 500ms
- If UoW deadline is 30s from now and subsystem limit is 5s → uses 5s

This applies to all lock acquisition sites within the commit path:

| Lock Site | Current Timeout Source | After |
|-----------|----------------------|-------|
| TransactionChain tail check | `TransactionChainLockTimeout` (10s) | `Min(ctx.Deadline, 10s)` |
| Revision chain lock | `RevisionChainLockTimeout` (5s) | `Min(ctx.Deadline, 5s)` |
| B+Tree index lock | `BTreeLockTimeout` (5s) | `Min(ctx.Deadline, 5s)` |
| Segment allocation | `SegmentAllocationLockTimeout` (10s) | `Min(ctx.Deadline, 10s)` |

### Helper Method (Optional)

To reduce boilerplate at lock sites needing deadline composition:

```csharp
/// <summary>
/// Create a WaitContext that respects both the UoW deadline and a subsystem-specific timeout.
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static WaitContext ComposeWaitContext(ref UnitOfWorkContext ctx, TimeSpan subsystemTimeout)
    => WaitContext.FromDeadline(
        Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));
```

Usage:
```csharp
// With deadline composition:
var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wc))
{
    ThrowHelper.ThrowLockTimeout("TransactionChain/CommitTailCheck", ...);
}

// Without composition (UoW deadline only):
if (!lock.EnterSharedAccess(ref ctx.WaitContext))
{
    ThrowHelper.ThrowLockTimeout(...);
}
```

## TimeoutOptions Addition

Add a default commit timeout for backward-compatible `Commit()`:

```csharp
// In TimeoutOptions.cs
/// <summary>
/// Default timeout for <see cref="Transaction.Commit()"/> when called without an explicit
/// <see cref="UnitOfWorkContext"/>. Also used as the default for standalone transactions
/// not associated with a Unit of Work.
/// </summary>
/// <remarks>
/// Defaults to 30 seconds. This is generous because commit may involve multiple lock
/// acquisitions, conflict resolution, and index updates. Individual lock timeouts
/// (5-10s) provide tighter bounds within this overall limit.
/// </remarks>
public TimeSpan DefaultCommitTimeout { get; set; } = TimeSpan.FromSeconds(30);
```

## ThrowHelper Addition

```csharp
// In ThrowHelper.cs
[MethodImpl(MethodImplOptions.NoInlining)]
public static void ThrowTransactionTimeout(long transactionId, TimeSpan waitDuration)
    => throw new TransactionTimeoutException(transactionId, waitDuration);
```

This is used when the caller catches a `TyphonTimeoutException` from a yield point and re-wraps it with transaction context:

```csharp
// In the public Commit wrapper (optional enhancement)
public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
{
    try
    {
        return CommitInternal(ref ctx, handler);
    }
    catch (TyphonTimeoutException ex) when (ex is not TransactionTimeoutException)
    {
        // Re-throw with transaction context
        throw new TransactionTimeoutException(TSN, ex.WaitDuration);
    }
}
```

**This enhancement is optional.** The yield point in `ThrowIfCancelled()` already throws a meaningful `TyphonTimeoutException`. Adding `TransactionTimeoutException` enrichment is a polish step.

## Affected Lock Sites in Commit Path

These are the lock acquisition sites within `Transaction.Commit()` and `CommitComponent()` that need deadline composition:

### In Transaction.cs

| Line | Lock | Current Timeout |
|------|------|-----------------|
| ~1271 | `TransactionChain.Control.EnterSharedAccess` | `TransactionChainLockTimeout` (10s) |

### In CommitComponent (lines 1163-1565)

`CommitComponent` itself doesn't directly acquire named locks — it operates on data via `ChunkAccessor` and `ComponentRevision` helpers. Lock acquisitions happen within:

- **Revision chain operations**: `ComponentRevisionManager.GetRevisionElement()` acquires `RevisionChainLockTimeout`
- **Index operations**: B+Tree insert/remove acquires `BTreeLockTimeout`
- **Segment allocation**: New chunk allocation acquires `SegmentAllocationLockTimeout`

These inner methods will need the context threaded through. Two approaches:

**Option A (minimal):** Pass context only to `Commit()`, keep inner methods using `TimeoutOptions` directly but with `Deadline.Min`:
```csharp
// Inner method creates WaitContext using Min of both deadlines
var wc = WaitContext.FromDeadline(
    Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(TimeoutOptions.Current.BTreeLockTimeout)));
```

**Option B (full threading):** Pass `ref UnitOfWorkContext` to all inner methods, let them use `ctx.ToWaitContext()`.

**Recommended: Option A for Tier 2.** Full context threading (Option B) should wait until the `UnitOfWork` class exists (Tier 3), when the context naturally flows through all operations. For Tier 2, the commit path uses deadline composition at its lock sites.

## Transaction State on Timeout

| Timing | State | Retryable? |
|--------|-------|------------|
| Yield point before commit starts | `InProgress` (unchanged) | Yes — transaction untouched |
| Lock timeout during commit (inside holdoff) | `InProgress` (unchanged) | Maybe — depends on which lock; partial state possible |
| After commit completes | `Committed` | N/A — success |

**Lock timeout during holdoff is the edge case.** The holdoff prevents `ThrowIfCancelled()` from firing, but a lock acquisition can still fail with `LockTimeoutException` if the composed deadline expires. In this case:

- `CommitComponent` was partially executed (some components committed, current one failed)
- Transaction state is still `InProgress` (not marked `Committed`)
- The caller should `Rollback()` to clean up

**This is existing behavior** — a lock timeout during commit already causes this today. The context just makes the timeout come from the UoW deadline rather than (or in addition to) the subsystem timeout.

## Files to Modify

| File | Changes |
|------|---------|
| `src/Typhon.Engine/Data/Transaction/Transaction.cs` | New `Commit(ref UnitOfWorkContext, ...)` + `Rollback(ref UnitOfWorkContext)` overloads; update `CommitContext`; deadline composition at lock sites |
| `src/Typhon.Engine/Data/TimeoutOptions.cs` | Add `DefaultCommitTimeout` (30s) |
| `src/Typhon.Engine/Errors/ThrowHelper.cs` | Add `ThrowTransactionTimeout(long, TimeSpan)` |

### Test Files

| File | Changes |
|------|---------|
| Existing commit tests | Verify backward-compatible `Commit()` still works |
| New test file or section | `Commit(ref UnitOfWorkContext)` with timeout, cancellation, deadline composition |

## Testing Strategy

### Unit Tests (~12 tests)

**Basic commit with context:**
- Infinite deadline → commit succeeds normally
- Unexpired deadline → commit succeeds normally
- Already-expired deadline → throws `TyphonTimeoutException` at yield point (before commit starts)
- Cancelled token → throws `OperationCanceledException` at yield point

**Backward compatibility:**
- `Commit()` without context → uses default 30s timeout → succeeds normally
- `Rollback()` without context → uses infinite deadline → succeeds normally
- All existing tests pass unchanged

**Deadline composition:**
- UoW deadline (500ms) + subsystem timeout (5s) → effective deadline is 500ms
- UoW deadline (30s) + subsystem timeout (5s) → effective deadline is 5s
- UoW deadline infinite + subsystem timeout (5s) → effective deadline is 5s (no change from today)

**Contention / timeout interaction** (`[Category("Timing")]`):
- Create contention on a lock → commit with short deadline → `LockTimeoutException` or `TransactionTimeoutException`
- Verify timeout happens within expected window (generous margins)

**Rollback:**
- Rollback with expired deadline → still succeeds (holdoff protects rollback)
- Rollback with cancelled token → still succeeds (holdoff protects rollback)

## End-to-End Test (from Issue #41 Acceptance Criteria)

```csharp
[Test]
public void EndToEnd_DeadlineExpired_TransactionTimeoutException()
{
    using var dbe = CreateEngine();
    dbe.RegisterComponent<TestComponent>();

    // Create some contention: start a long-running transaction that holds locks
    using var blockingTx = dbe.CreateQuickTransaction();
    var pk = blockingTx.CreateEntity(new TestComponent { Value = 1 });
    // Don't commit yet — this holds the revision chain

    // Try to commit another transaction with a very short deadline
    using var tx = dbe.CreateQuickTransaction();
    tx.UpdateComponent(pk, new TestComponent { Value = 2 });

    var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromMilliseconds(100));

    Assert.Throws<TyphonTimeoutException>(() => tx.Commit(ref ctx));

    // Clean up
    blockingTx.Rollback();
}
```

## Acceptance Criteria (from Issue #45)

- [ ] `Commit()` and `Rollback()` accept `ref UnitOfWorkContext`
- [ ] Deadline propagates to all internal lock acquisitions
- [ ] Timeout during commit throws `TransactionTimeoutException` with context
- [ ] Backward-compatible overload exists (default timeout)
- [ ] End-to-end test: set 100ms deadline, create contention → transaction times out cleanly

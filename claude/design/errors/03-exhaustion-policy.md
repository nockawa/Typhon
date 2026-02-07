# #39 — ExhaustionPolicy Enforcement

**Date:** 2026-02-06
**Status:** Ready for implementation
**GitHub Issue:** #39
**Decisions:** D9, D12

> 💡 **Quick summary:** Enforce resource limits on 3 paths (TransactionPool, PageCache wait, ChunkBasedSegment exception type), wire `ExhaustionPolicy` as metadata to `ResourceNode`. ~6 files modified.

## Overview

The `ExhaustionPolicy` enum exists with 4 values (`FailFast`, `Wait`, `Evict`, `Degrade`) but is referenced by zero production code. Each subsystem already implements the *correct behavior* implicitly, but three paths have gaps:

1. **TransactionPool**: `MaxActiveTransactions` defined but **never enforced**
2. **PageCache wait path**: Uses unbounded `AdaptiveWaiter.Spin()` with a timeout that only exists in `#if DEBUG` — Release builds hang forever
3. **ChunkBasedSegment**: Throws `InvalidOperationException` instead of `ResourceExhaustedException`

Additionally, `ExhaustionPolicy` needs to be anchored to the resource graph as diagnostic metadata (D12).

## Change 1: TransactionPool — FailFast Enforcement

**File:** `src/Typhon.Engine/Data/Transaction/TransactionChain.cs`

**Problem:** `MaxActiveTransactions` (in `ResourceOptions`) is reported in metrics/telemetry but never checked when creating a new transaction. If the pool is full, new transactions are created anyway. Additionally, `TransactionChain` currently has no access to the options — its constructor takes no parameters.

**Fix:**

1. Add a `_maxActiveTransactions` field to `TransactionChain`, passed via constructor:

```csharp
private readonly int _maxActiveTransactions;

public TransactionChain(int maxActiveTransactions)
{
    _maxActiveTransactions = maxActiveTransactions;
}
```

2. Update `DatabaseEngine` to pass the limit when creating the chain:

```csharp
// In DatabaseEngine initialization:
_transactionChain = new TransactionChain(_options.Resources.MaxActiveTransactions);
```

3. Add the enforcement check **inside** the exclusive lock in `CreateTransaction()`:

```csharp
public Transaction CreateTransaction(DatabaseEngine dbe)
{
    _control.EnterExclusiveAccess(ref wc);  // already holds lock
    try
    {
        // Check active transaction count against limit — INSIDE the lock
        // to avoid race between check and increment
        if (_activeCount >= _maxActiveTransactions)
        {
            ThrowHelper.ThrowResourceExhausted(
                "Data/TransactionChain/CreateTransaction",
                ResourceType.Service,
                _activeCount,
                _maxActiveTransactions);
        }

        // ... existing creation logic (Interlocked.Increment on _activeCount, etc.)
    }
    finally
    {
        _control.ExitExclusiveAccess();
    }
}
```

**Why pass `int` instead of full options?** TransactionChain only needs the ceiling value. Passing the whole `DatabaseEngineOptions` would couple it to the options class unnecessarily. A single `int` is the minimal dependency.

**Why check inside the lock?** `CreateTransaction()` already holds `_control.EnterExclusiveAccess()`. The check must be inside this lock to prevent a TOCTOU race between reading `_activeCount` and incrementing it.

**Configuration:** `DatabaseEngineOptions.Resources.MaxActiveTransactions` — existing property, just needs enforcement.

**Behavior:** FailFast — no wait, no retry, immediate exception. Caller can catch `ResourceExhaustedException` and retry later.

## Change 2: PageCache Wait Path — Bounded Wait

**File:** `src/Typhon.Engine/Storage/PagedMMF.cs`

**Problem:** When the page cache is full and all pages are pinned (no evictable pages), `FetchPageToMemory()` falls through to `AdaptiveWaiter.Spin()`. The current code has a 1-second timeout that **only exists in `#if DEBUG`** (lines 596-620). In Release builds, the spin is completely unbounded — any situation where all pages are pinned causes an infinite hang. The DEBUG path also throws `OutOfMemoryException`, which is the wrong exception type.

**Fix:** Replace the entire DEBUG-conditional timeout block + unbounded spin with a `WaitContext`-bounded loop:

```csharp
// BEFORE (actual code, simplified):
if (!found)
{
#if DEBUG
    if (DateTime.UtcNow - start > TimeSpan.FromSeconds(1))
    {
        throw new OutOfMemoryException(...);  // DEBUG only!
    }
#endif
    waiter ??= new AdaptiveWaiter();
    waiter.Spin();      // unbounded in Release
    continue;
}

// AFTER:
if (!found)
{
    if (wc.ShouldStop)
    {
        ThrowHelper.ThrowResourceExhausted(
            "Storage/PagedMMF/FetchPageToMemory",
            ResourceType.Memory,
            _pinnedPageCount,
            _totalPages);
    }
    waiter ??= new AdaptiveWaiter();
    waiter.Spin();
    continue;
}
```

Where `wc` is created at the top of `FetchPageToMemory()`:
```csharp
var wc = WaitContext.FromTimeout(_pageCacheEvictionTimeout);
```

**Timeout source:** `TimeoutOptions.PageCacheLockTimeout` from #38's configuration (same subsystem budget — covers both lock acquisition and eviction wait).

**Key changes from current code:**
1. Timeout protection in **all** build configurations, not just DEBUG
2. Exception type: `OutOfMemoryException` → `ResourceExhaustedException`
3. Deadline-based (WaitContext) instead of wall-clock comparison (`DateTime.UtcNow`)

**Behavior:** Evict → Wait (bounded by deadline) → throw `ResourceExhaustedException`. This is the correct escalation: try to evict, wait briefly for pins to release, then fail if the deadline expires.

## Change 3: ChunkBasedSegment — Exception Type Upgrade

**File:** `src/Typhon.Engine/Storage/Segments/ChunkBasedSegment.cs`

**Problem:** When `AllocateChunk()` or `AllocateChunks()` fails (segment full, auto-grow exhausted), they throw `InvalidOperationException`. This is caught by `catch (Exception)` but not by `catch (TyphonException)`.

**Two throw sites** need updating:

```csharp
// Site 1: AllocateChunk() — line 213
// BEFORE:
throw new InvalidOperationException("ChunkBasedSegment has reached maximum capacity. Cannot allocate more chunks.");

// AFTER:
ThrowHelper.ThrowResourceExhausted(
    "Storage/ChunkBasedSegment/AllocateChunk",
    ResourceType.Memory,
    _allocatedChunks,
    _maxChunks);

// Site 2: AllocateChunks() — line 252
// BEFORE:
throw new InvalidOperationException($"ChunkBasedSegment cannot accommodate {count} chunks. Maximum capacity reached.");

// AFTER:
ThrowHelper.ThrowResourceExhausted(
    "Storage/ChunkBasedSegment/AllocateChunks",
    ResourceType.Memory,
    _allocatedChunks,
    _maxChunks);
```

**Resource path convention:** Use `"Subsystem/Class/Method"` format (e.g., `"Storage/ChunkBasedSegment/AllocateChunk"`). The segment doesn't have a name field, so the class + method path provides sufficient diagnostics for Tier 1. A per-instance name field can be added later if diagnostics demand it.

**Behavior:** Degrade (auto-grow) → FailFast (throw `ResourceExhaustedException`). The auto-grow already works correctly; we're just upgrading the final failure from `InvalidOperationException` to a structured exception.

## Change 4: ExhaustionPolicy as ResourceNode Metadata

**File:** `src/Typhon.Engine/Resources/ResourceNode.cs`

**Add property:**

```csharp
/// <summary>
/// Documents this resource's exhaustion behavior.
/// Diagnostic metadata — not used for runtime dispatch.
/// </summary>
public ExhaustionPolicy ExhaustionPolicy { get; }
```

**Constructor update:** Add `ExhaustionPolicy exhaustionPolicy` parameter to `ResourceNode`'s constructor(s). All existing registration sites that call `new ResourceNode(...)` must be updated to pass the appropriate policy value.

Set at construction/registration time:

| Resource | Policy | Meaning |
|----------|--------|---------|
| PageCache | `Evict` | Self-healing: evicts pages, waits, then fails |
| ChunkBasedSegment | `Degrade` | Self-healing: auto-grows, then fails |
| TransactionPool | `FailFast` | No wait, immediate exception |
| ResourceTelemetryAllocator | `Degrade` | Silent degradation (observability) |
| ConcurrentArray | `FailFast` | Internal structure, immediate failure |

**Diagnostic value:** `ResourceSnapshot` / `FindRootCause` can expose the policy:
- *"PageCache at 95% — policy: Evict (self-healing)"*
- *"TransactionPool at 100% — policy: FailFast (callers see exceptions)"*

## Resources Not Changed (Behavior Already Correct)

| Resource | Current Behavior | Change |
|----------|-----------------|--------|
| ResourceTelemetryAllocator | Silent degradation | None — correct for observability |
| ConcurrentArray | `ThrowCapacityReached()` | None — internal, `InvalidOperationException` is fine |
| `AccessControl` spin | `AdaptiveWaiter.Spin()` bounded by `WaitContext` | Handled by #38 deadline propagation |

## Testing Strategy

- [ ] **TransactionPool limit**: Create transactions up to `MaxActiveTransactions`, verify next creation throws `ResourceExhaustedException`
- [ ] **TransactionPool recovery**: After a transaction is committed/disposed, verify new transactions can be created again
- [ ] **TransactionPool limit inside lock**: Verify the check happens atomically — no race between concurrent `CreateTransaction()` calls
- [ ] **PageCache bounded wait**: Mock/force a full cache with all pages pinned, verify `ResourceExhaustedException` after timeout (in Release configuration)
- [ ] **PageCache — no more DEBUG-only timeout**: Verify the old `#if DEBUG` / `OutOfMemoryException` path is removed
- [ ] **ChunkBasedSegment exception type**: Fill a segment to capacity (disable auto-grow or exhaust max pages), verify `ResourceExhaustedException` instead of `InvalidOperationException`
- [ ] **ChunkBasedSegment both throw sites**: Verify both `AllocateChunk()` and `AllocateChunks()` throw `ResourceExhaustedException`
- [ ] **ResourceNode metadata**: Verify each resource's `ExhaustionPolicy` is set correctly and accessible via `ResourceSnapshot`
- [ ] **Regression**: All existing tests pass — existing behavior is preserved, only the exception types change

## Implementation Notes

- **Dependencies**: Requires #37 (exception hierarchy) for `ResourceExhaustedException` re-parenting and `ThrowHelper.ThrowResourceExhausted`. Requires #38 (deadline propagation) for the PageCache wait path `WaitContext` and `TimeoutOptions` configuration.
- **TransactionChain constructor change**: `TransactionChain()` → `TransactionChain(int maxActiveTransactions)`. Update the creation site in `DatabaseEngine` to pass `_options.Resources.MaxActiveTransactions`.
- **PageCache: remove `#if DEBUG` block**: The old `DateTime.UtcNow`-based timeout and `OutOfMemoryException` in the DEBUG conditional must be fully removed, replaced by the WaitContext-based check that works in all configurations.
- **Test isolation**: The TransactionPool limit test should use a dedicated `DatabaseEngine` instance with a low `MaxActiveTransactions` (e.g., 2-3) to avoid needing many transactions.

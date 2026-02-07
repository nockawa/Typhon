# #38 — Activate Deadline Propagation

**Date:** 2026-02-06
**Status:** Ready for implementation
**GitHub Issue:** #38
**Decisions:** D3, D5

> 💡 **Quick summary:** Replace 31 production `WaitContext.Null` call sites with finite deadlines + bool-check-and-throw pattern. Migrate ~128 contention test sites to `TestWaitContext`. Add `TimeoutOptions` to `DatabaseEngineOptions`. ~15 files modified, 2 new files.

## Overview

Today, every lock acquisition in the engine passes `ref WaitContext.Null` (infinite timeout). This means any contention, deadlock, or resource starvation causes the caller to hang forever. This sub-issue replaces all `WaitContext.Null` with finite, configurable deadlines, converting infinite hangs into `LockTimeoutException` (from #37).

**Key invariant:** Lock primitives (`AccessControl`, `AccessControlSmall`) return `bool` — they do **NOT** throw. The subsystem caller checks the return value and throws via `ThrowHelper.ThrowLockTimeout(...)`. This keeps the concurrency primitives simple and puts error policy in the subsystem layer where it belongs.

## Phase 1: Configuration Infrastructure

### `TimeoutOptions` Class

**Location:** New file — `src/Typhon.Engine/Data/TimeoutOptions.cs`

Following the same pattern as `ResourceOptions` (a sub-object on `DatabaseEngineOptions`):

```csharp
/// <summary>
/// Configurable timeout durations for lock acquisitions across subsystems.
/// Each subsystem uses its specific timeout; DefaultLockTimeout is the fallback
/// for subsystems that don't have a dedicated setting.
/// </summary>
public class TimeoutOptions
{
    /// <summary>Global default lock timeout. Applied when no subsystem-specific timeout is set.</summary>
    public TimeSpan DefaultLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Page cache lock acquisition timeout.</summary>
    public TimeSpan PageCacheLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>B+Tree operation lock timeout.</summary>
    public TimeSpan BTreeLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Transaction chain lock timeout.</summary>
    public TimeSpan TransactionChainLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Revision chain access timeout.</summary>
    public TimeSpan RevisionChainLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Segment allocation lock timeout.</summary>
    public TimeSpan SegmentAllocationLockTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
```

### `DatabaseEngineOptions` Integration

**Location:** Modify existing `DatabaseEngineOptions` in `src/Typhon.Engine/Data/DatabaseEngine.cs`

```csharp
public class DatabaseEngineOptions
{
    public ResourceOptions Resources { get; set; } = new();
    public TimeoutOptions Timeouts { get; set; } = new();   // ← new
}
```

**Default rationale (from D3):**

| Subsystem | Default | Rationale |
|-----------|---------|-----------|
| PageCache | 5s | Short — contention here signals serious problems |
| B+Tree | 5s | Internal structure, should be fast |
| TransactionChain | 10s | May queue behind commit processing |
| RevisionChain | 5s | Internal read, should be fast |
| SegmentAllocation | 10s | May need page I/O |

### Timeout Propagation Pattern

Each subsystem receives its timeout at construction (injected from `DatabaseEngineOptions.Timeouts`) and creates `WaitContext` at each call site:

```csharp
// In a subsystem class (e.g., ComponentTable)
private readonly TimeSpan _lockTimeout;

public ComponentTable(DatabaseEngineOptions options, ...)
{
    _lockTimeout = options.Timeouts.RevisionChainLockTimeout;
}

// At each call site:
var wc = WaitContext.FromTimeout(_lockTimeout);
if (!_accessControl.EnterSharedAccess(ref wc))
{
    ThrowHelper.ThrowLockTimeout("RevisionChain", _lockTimeout);
}
```

**Why construct WaitContext at the call site (not once at init)?** Because `WaitContext` wraps a `Deadline` which is a point-in-time value. Creating it at the call site sets the deadline to "now + timeout", giving each operation a fresh countdown.

## Phase 2: Production Call Site Migration

### Lock Primitive Behavior (No Changes)

**Critical design point:** The lock primitives themselves are unchanged. Their timeout behavior is:

| Primitive | Timeout Behavior | Throws? |
|-----------|-----------------|---------|
| `AccessControl` | `EnterSharedAccess` / `EnterExclusiveAccess` return `false` on timeout | **No** |
| `AccessControlSmall` | `EnterSharedAccess` / `EnterExclusiveAccess` return `false` on timeout | **No** |
| `ResourceAccessControl` | Non-scoped methods (`EnterAccessing`, `EnterModify`) return `false` | **No** |
| `ResourceAccessControl` | Scoped guards (`EnterAccessingScoped`, `EnterModifyScoped`) throw `TimeoutException` | Yes — will become `LockTimeoutException` |

The **subsystem caller** is responsible for checking the `bool` return and throwing via `ThrowHelper`. This separation keeps the concurrency primitives simple and focused.

**Why this is safe:** The throw happens at the lock acquisition point — **before** any structural modification begins. The lock was never acquired, so there is no inconsistent state. For example, in `BTree.Insert`:

```csharp
var wc = WaitContext.FromTimeout(_lockTimeout);
if (!_access.EnterExclusiveAccess(ref wc))
{
    ThrowHelper.ThrowLockTimeout("BTree/Insert", _lockTimeout);
}
// Throw happened ↑ BEFORE the try block — no split in progress, no cleanup needed
try
{
    AddOrUpdateCore(ref args);  // B+Tree split happens here, under lock
    return args.ElementId;
}
finally
{
    _storage.CommitChanges(ref accessor);
    _access.ExitExclusiveAccess();
}
```

**Holdoff regions** (cancellation during mid-operation critical sections like B+Tree splits) are a **Tier 2 concern** tied to the Execution Context. Tier 1 only throws at lock acquisition — a safe cancellation point by definition (see [research doc §7.4](../../research/timeout/07-design-guidelines.md#74-cancellation-point-design)).

### 31 Subsystem Call Sites

The 16 concurrency-internal sites (`AccessControl.cs`, `AccessControlSmall.cs`, `ResourceAccessControl.cs`, `WaitContext.cs`, `AccessControl.LockData.cs`) are **not** migrated — they are the *implementations* of lock acquisition, not call sites. They receive the `WaitContext` from their callers.

The 31 subsystem call sites are the migration targets:

| Subsystem | Count | Files | Timeout Source |
|-----------|-------|-------|----------------|
| PageCache | 10 | `Storage/PagedMMF.cs` (8), `Storage/ManagedPagedMMF.cs` (2) | `PageCacheLockTimeout` |
| SegmentAllocation | 8 | `Memory/Allocators/ChainedBlockAllocatorBase.cs` (4), `Storage/Segments/VariableSizedBufferSegment.cs` (3), `Memory/Allocators/ChainedBlockAllocator.cs` (1) | `SegmentAllocationLockTimeout` |
| BTree | 5 | `Data/Index/BTree.cs` — Insert, Delete, TryGet, DeleteValue, CheckConsistency | `BTreeLockTimeout` |
| TransactionChain | 4 | `Data/Transaction/TransactionChain.cs` (3), `Data/Transaction/Transaction.cs` (1) | `TransactionChainLockTimeout` |
| RevisionChain | 4 | `Data/Revision/ComponentRevisionManager.cs` (3), `Data/Revision/RevisionEnumerator.cs` (1) | `RevisionChainLockTimeout` |

**Migration pattern for each site:**

```csharp
// BEFORE:
_accessControl.EnterExclusiveAccess(ref WaitContext.Null);

// AFTER:
var wc = WaitContext.FromTimeout(_lockTimeout);
if (!_accessControl.EnterExclusiveAccess(ref wc))
{
    ThrowHelper.ThrowLockTimeout("Subsystem/Operation", _lockTimeout);
}
```

The resource name in `ThrowLockTimeout` should identify the subsystem and operation (e.g., `"BTree/Insert"`, `"PageCache/AcquirePage"`, `"TransactionChain/CreateTransaction"`).

### Special Case: ResourceAccessControl Scoped Guards

`ResourceAccessControl`'s scoped guards (`EnterAccessingScoped`, `EnterModifyScoped`) already throw `TimeoutException` on timeout. These should be updated to throw `LockTimeoutException` instead. This is a simple change in `ResourceAccessControl`'s private `ThrowTimeout()` method.

**Note:** The user will review `ResourceAccessControl`'s full behavior separately. For Tier 1, only the exception type change is in scope.

### Special Case: Nested Lock Acquisitions

Some code paths acquire multiple locks. Each lock call gets its own `WaitContext` with a fresh deadline. In Tier 2, these will share an Execution Context deadline (overall transaction budget). For now, each is independent.

### Special Case: AccessControl.LockData.cs

`AccessControl.LockData.cs` has 1 `WaitContext.Null` usage. This is inside the lock implementation itself (used for recursive lock re-entry). It should remain as `WaitContext.Null` — the outer caller provides the deadline.

## Phase 3: Test Infrastructure

### `TestWaitContext` Helper

**Location:** `test/Typhon.Engine.Tests/Helpers/TestWaitContext.cs`

```csharp
/// <summary>
/// Provides bounded WaitContext values for tests.
/// Drop-in replacement for <c>ref WaitContext.Null</c> in contention tests.
/// </summary>
public static class TestWaitContext
{
    /// <summary>Default test timeout — generous but finite.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    [ThreadStatic] private static WaitContext _current;

    /// <summary>
    /// Returns a ref to a fresh 10-second WaitContext.
    /// Thread-safe: uses [ThreadStatic] backing field.
    /// Each access creates a fresh deadline (10s from now).
    /// </summary>
    public static ref WaitContext Default
    {
        get
        {
            _current = WaitContext.FromTimeout(DefaultTimeout);
            return ref _current;
        }
    }

    /// <summary>Custom timeout for specific tests.</summary>
    public static ref WaitContext WithTimeout(TimeSpan timeout)
    {
        _current = WaitContext.FromTimeout(timeout);
        return ref _current;
    }
}
```

**Why `ThreadStatic`?** NUnit runs tests in parallel on different threads. A `ThreadStatic` field has a stable per-thread address, so the `ref` return is safe. Each access creates a fresh deadline, so nested calls within a single test each get a full 10s window.

**Ref aliasing warning:** Within a single test, do **not** capture two refs from `TestWaitContext` simultaneously — the second call overwrites the `ThreadStatic` backing field, aliasing both refs to the same value:

```csharp
// DANGEROUS — ref wc1 and ref wc2 alias the same ThreadStatic field:
ref var wc1 = ref TestWaitContext.Default;
ref var wc2 = ref TestWaitContext.Default;  // wc1 now points to wc2's deadline!

// SAFE — use local copies when multiple WaitContexts are needed:
var wc1 = WaitContext.FromTimeout(TestWaitContext.DefaultTimeout);
var wc2 = WaitContext.FromTimeout(TestWaitContext.DefaultTimeout);
```

This is unlikely in practice (most tests need one `WaitContext` at a time), but worth documenting.

### Test Call Site Migration (~128 contention sites)

Tests fall into two categories:

1. **Contention tests** (~128 occurrences): Multi-threaded tests using `Task.Run`, `Parallel.For`, or `Barrier` where deadlock/hang is possible. These migrate to `TestWaitContext.Default`.
2. **Single-threaded tests** (~74 occurrences): Tests exercising lock API on a single thread. No contention possible, so `WaitContext.Null` is safe and stays unchanged.

| File | Total | Contention (migrate) | Single-threaded (keep) |
|------|------:|--------------------:|----------------------:|
| `AccessControlSmallTests.cs` | 79 | ~52 | ~27 |
| `ResourceAccessControlTests.cs` | 66 | ~50 | ~16 |
| `AccessControlTests.cs` | 34 | ~18 | ~16 |
| `AccessControlTelemetryTests.cs` | 21 | ~8 | ~13 |
| `ManagedPagedMMFTests.cs` | 2 | 2 | 0 |
| **Total** | **202** | **~128** | **~74** |

**Migration pattern (contention tests only):**

```csharp
// BEFORE:
control.EnterExclusiveAccess(ref WaitContext.Null);

// AFTER:
control.EnterExclusiveAccess(ref TestWaitContext.Default);
```

**Identification rule:** A test is "contention" if it uses `Task.Run`, `Thread.Start`, `Parallel.For`, `Barrier`, or any mechanism that causes concurrent access to the same lock. All other tests are "single-threaded."

**Stress test exception:** Tests that intentionally hold locks for extended periods or test timeout behavior should explicitly use `WaitContext.Null` or their own longer `WaitContext`, with a comment explaining why.

## Testing Strategy

- [ ] **Timeout fires correctly**: Test that a lock held by thread A causes thread B to get `LockTimeoutException` after the configured timeout
- [ ] **Bool return checked**: Verify subsystem code correctly checks `EnterExclusiveAccess` return value and throws via `ThrowHelper`
- [ ] **Configurable timeouts**: Verify `DatabaseEngineOptions.Timeouts` values propagate to lock call sites
- [ ] **Default timeouts**: Verify defaults (5s/10s) are applied when no override is set
- [ ] **TestWaitContext.Default**: Verify it creates a fresh 10s deadline on each access
- [ ] **TestWaitContext thread safety**: Verify concurrent access from multiple test threads doesn't corrupt state
- [ ] **Regression**: All ~128 migrated contention test sites pass with `TestWaitContext.Default`
- [ ] **Single-threaded tests unchanged**: The ~74 single-threaded sites still use `WaitContext.Null` and pass
- [ ] **Stress test opt-out**: Verify stress tests can still use `WaitContext.Null` explicitly
- [ ] **ResourceAccessControl scoped guards**: Verify they throw `LockTimeoutException` instead of `TimeoutException`

## File Summary

| Action | File | Description |
|--------|------|-------------|
| **Create** | `src/Typhon.Engine/Data/TimeoutOptions.cs` | Timeout configuration sub-class |
| **Create** | `test/Typhon.Engine.Tests/Helpers/TestWaitContext.cs` | Test helper for bounded WaitContext |
| **Modify** | `src/Typhon.Engine/Data/DatabaseEngine.cs` | Add `Timeouts` property to `DatabaseEngineOptions` |
| **Modify** | `src/Typhon.Engine/Storage/PagedMMF.cs` | 8 sites: WaitContext.Null → finite deadline + bool check |
| **Modify** | `src/Typhon.Engine/Storage/ManagedPagedMMF.cs` | 2 sites: same pattern |
| **Modify** | `src/Typhon.Engine/Data/Index/BTree.cs` | 5 sites: same pattern |
| **Modify** | `src/Typhon.Engine/Data/Transaction/TransactionChain.cs` | 3 sites: same pattern |
| **Modify** | `src/Typhon.Engine/Data/Transaction/Transaction.cs` | 1 site: same pattern |
| **Modify** | `src/Typhon.Engine/Data/Revision/ComponentRevisionManager.cs` | 3 sites: same pattern |
| **Modify** | `src/Typhon.Engine/Data/Revision/RevisionEnumerator.cs` | 1 site: same pattern |
| **Modify** | `src/Typhon.Engine/Memory/Allocators/ChainedBlockAllocatorBase.cs` | 4 sites: same pattern |
| **Modify** | `src/Typhon.Engine/Storage/Segments/VariableSizedBufferSegment.cs` | 3 sites: same pattern |
| **Modify** | `src/Typhon.Engine/Memory/Allocators/ChainedBlockAllocator.cs` | 1 site: same pattern |
| **Modify** | `src/Typhon.Engine/Concurrency/ResourceAccessControl.cs` | Change `ThrowTimeout()` to throw `LockTimeoutException` |
| **Modify** | 5 test files (see table above) | ~128 contention sites → `TestWaitContext.Default` |

## Implementation Notes

- **Phased migration**: Migrate one subsystem at a time (PageCache → BTree → TransactionChain → RevisionChain → SegmentAllocation). Run tests after each subsystem to catch regressions early.
- **Internal lock paths stay unchanged**: The 16 `WaitContext.Null` inside `AccessControl*.cs`, `ResourceAccessControl.cs`, `WaitContext.cs`, and `AccessControl.LockData.cs` are the *implementation* — they receive the caller's `WaitContext` as a parameter. Don't touch these.
- **`WaitContext.Null` detection**: Lock primitives currently detect `Unsafe.IsNullRef(ref wc)` to skip deadline checks. After migration, this optimization path is only used by single-threaded tests, stress tests, and internal lock implementation.
- **Bool return pattern**: Every migrated production site must check the bool return from lock acquisition. The `ThrowHelper.ThrowLockTimeout` call ensures the throw is `[NoInlining]` — zero JIT impact on the normal (lock-acquired) path.
- **ResourceAccessControl scoped guards**: The `ThrowTimeout()` private method currently throws `TimeoutException`. Change it to `ThrowHelper.ThrowLockTimeout(...)` with appropriate resource name.
- **Test migration is targeted**: Only contention tests (those using `Task.Run`, `Parallel.For`, `Barrier`) migrate. Single-threaded tests keep `WaitContext.Null` — no benefit to adding timeouts where contention is impossible.
- **Dependencies**: Requires #37 (exception hierarchy) for `LockTimeoutException` and `ThrowHelper.ThrowLockTimeout`. Exception classes must exist before timeout sites can throw them.

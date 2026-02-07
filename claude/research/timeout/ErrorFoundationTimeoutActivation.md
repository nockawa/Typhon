# Error Foundation & Timeout Activation

**Date:** 2026-02-06
**Status:** Moved to design
**GitHub Issue:** #36
**Outcome:** All 12 design decisions resolved, benchmark validates Result&lt;TValue, TStatus&gt;, audits complete, ready for design phase

## Context

Typhon's concurrency primitives (`Deadline`, `WaitContext`) are implemented and tested, but every lock acquisition in the engine uses `WaitContext.Null` (infinite timeout). This means the engine can **hang silently** instead of failing reliably when contention or resource exhaustion occurs.

Issue #36 is the umbrella for four sub-issues that together build a structured error vocabulary and activate the timeout infrastructure:

| Sub-Issue | Focus |
|-----------|-------|
| #37 | `TyphonException` hierarchy (base class + key subclasses) |
| #38 | Activate deadline propagation — replace `WaitContext.Null` |
| #39 | `ExhaustionPolicy` enforcement |
| #40 | `Result<T>` for hot-path error returns |

### Current State of the Codebase

- **`Deadline`** already exists at `src/Typhon.Engine/Concurrency/Deadline.cs` — monotonic readonly struct with `FromTimeout`, `Infinite`, `IsExpired`, `Min`, etc.
- **`WaitContext`** already exists at `src/Typhon.Engine/Concurrency/WaitContext.cs` — readonly struct pairing a `Deadline` with a `CancellationToken`. Has a `WaitContext.Null` (infinite, no cancellation) sentinel.
- **`ResourceExhaustedException`** exists at `src/Typhon.Engine/Resources/ResourceExhaustedException.cs` — currently inherits directly from `Exception`, not from any `TyphonException` base. Carries `ResourcePath`, `ResourceType`, `CurrentUsage`, `Limit`.
- **`ExhaustionPolicy`** enum exists in the Resource System (`FailFast`, `Wait`, `Evict`, `Degrade`).
- **No `TyphonException` base class** exists yet — the error overview doc (`claude/overview/10-errors.md`) has a proposed hierarchy but status is "⚠️ Scattered".
- **~274 `WaitContext.Null` occurrences** across 28 files in the codebase (47 in production `src/`, ~202 in tests, ~25 in docs). The 47 production occurrences break down as 31 subsystem call sites + 16 internal to the concurrency primitives themselves.

### Existing Research & Design References

- [Timeout research series](claude/research/timeout/) — 7-part deep dive into SQL Server, PostgreSQL, MySQL timeout internals
- [Design guidelines](claude/research/timeout/07-design-guidelines.md) — Deadline model, execution context, cancellation points, holdoff regions
- [Error model overview](claude/overview/10-errors.md) — Proposed exception hierarchy, error codes, recovery strategies
- [Budgets & Exhaustion](claude/overview/08-resources.md#87-budgets--exhaustion) — ExhaustionPolicy enforcement design
- [Concurrency overview](claude/overview/01-concurrency.md) — Deadline, WaitContext, AccessControl primitives

## Decisions

All questions have been discussed and resolved. This section records the final decisions.

### D1. Exception Hierarchy Granularity → Core + Stubs

**Decision:** Implement the full `TyphonException` base class and core infrastructure (error codes, categories), but only create concrete exception subclasses that Tier 1 actually uses. Reserve error code ranges for later tiers.

**Tier 1 concrete classes:**
- `TyphonException` base (with `ErrorCode`, `Category`, `IsTransient` — no `Context` dictionary, see D8)
- `LockTimeoutException` (error code 6003)
- `TransactionTimeoutException` (error code 1002)
- `StorageException` (error code range 2xxx)
- `CorruptionException` (error code 2003)
- `ResourceExhaustedException` re-parented (error code 6001)

**Infrastructure:**
- `TyphonErrorCode` enum with Tier 1 codes + reserved ranges
- `ErrorCategory` enum (Transient, Conflict, Resource, Validation, Corruption, Internal) — Durability and Configuration categories deferred to later tiers when relevant subsystems are implemented

**Rationale:** Foundation is solid and extensible without dead exception classes. Later tiers extend naturally by adding new enum values and exception subclasses.

### D2. ResourceExhaustedException Migration → Re-parent

**Decision:** Change `ResourceExhaustedException` to inherit from `TyphonException` directly — no intermediate `ResourceException` base class in Tier 1. This is a breaking change for anyone catching it by base type (`Exception`), but it unifies the hierarchy cleanly.

**No `ResourceException` intermediate:** The proposed 10-errors.md hierarchy shows `ResourceException` as an intermediate, but with `LockTimeoutException` moved under `TyphonTimeoutException` (D10) and `DeadlockDetectedException` deferred to later tiers, `ResourceException` would have only one child. A single-child intermediate adds nothing. The intermediate can be introduced later when more resource-category exceptions exist.

**Rationale:** Wrapping adds indirection and makes `catch (TyphonException)` miss resource exhaustion errors. Since the engine is pre-1.0, breaking changes are acceptable and the re-parenting is straightforward.

### D3. WaitContext.Null Replacement → Subsystem-Specific Defaults

**Decision:** Different default timeouts per subsystem, all configurable via `DatabaseEngineOptions`.

| Subsystem | Default Timeout | Rationale |
|-----------|----------------|-----------|
| Page cache (AccessControl) | 5s | Short — contention here signals serious problems |
| B+Tree operations | 5s | Internal structure, should be fast |
| Transaction chain | 10s | May queue behind commit processing |
| Revision chain access | 5s | Internal read, should be fast |
| Segment allocation | 10s | May need page I/O |

All defaults overridable via `DatabaseEngineOptions.DefaultLockTimeout` and subsystem-specific overrides. The `WaitContext` is constructed from `Deadline.FromTimeout(timeout)` at each call site.

### D4. Result&lt;TValue, TStatus&gt; → Typed Results With Per-Subsystem Status Enums

**Decision:** Introduce `Result<TValue, TStatus>` — a dual-generic readonly struct where `TValue` is the data and `TStatus` is a per-subsystem byte enum listing only the failure modes that method can produce. Apply to three hot-path categories in Tier 1.

**Design:**
- `Result<TValue, TStatus> where TValue : unmanaged, TStatus : unmanaged, Enum`
- Convention: `Success = 0` in all status enums
- Struct size: `sizeof(TValue) + 1 byte + padding` — same as a single-generic approach
- Actual errors (corruption, I/O failure) remain exceptions — `Result` only handles expected non-error outcomes

**Per-subsystem status enums (Tier 1):**
- `BTreeLookupStatus` — `Success`, `NotFound`
- `RevisionReadStatus` — `Success`, `NotFound`, `SnapshotInvisible`, `Deleted`
- `ChunkAccessStatus` — `Success`, `NotLoaded`

**Why not a single global enum?** A shared `ResultStatus` enum accumulates statuses from all subsystems. The caller sees 15 values and has no way to know which subset applies to the method they called. Per-subsystem enums make the method signature self-documenting: the `TStatus` type parameter tells the caller exactly what can happen.

**Why not `bool + out`?** The classic `bool TryGet(out T)` pattern loses information. In an MVCC engine, "not found" has multiple meanings: never existed, tombstoned, or exists but invisible at this snapshot tick. A status byte costs 1 byte (padded away anyway) but gives the caller actionable information without an exception.

**Rationale:** These are the three core data-access paths where exception overhead is unacceptable. Other patterns (e.g., component schema lookups) can use exceptions since they're not in the hot path.

### D5. Test Compatibility → 10s Test Default

**Decision:** Tests use a generous but finite timeout of **10 seconds**. No unit test should legitimately take longer than this. Stress tests are the exception and may use longer or infinite timeouts explicitly.

**Implementation:**
- Introduce a `TestWaitContext` helper (or constant) with a 10s deadline
- Replace all `WaitContext.Null` in tests with this helper
- Stress tests opt in to longer timeouts explicitly via their own `WaitContext` construction
- If a test hits 10s, it's a genuine hang — not CI slowness

**Rationale:** 10s is long enough to never flake on slow CI runners for legitimate unit tests, but short enough to catch real deadlocks quickly instead of waiting for NUnit's global timeout.

### D6. Error Code Ranges → Adopt from 10-errors.md

**Decision:** Use the proposed numeric ranges from `claude/overview/10-errors.md` as-is:

| Range | Category | Tier 1 Codes |
|-------|----------|--------------|
| 1xxx | Transaction | 1002 (TransactionTimeout) |
| 2xxx | Storage | 2003 (Corruption), 2004 (CapacityExceeded) |
| 3xxx | Component | *(reserved)* |
| 4xxx | Index | *(reserved)* |
| 5xxx | Query | *(reserved)* |
| 6xxx | Resource | 6001 (ResourceExhausted), 6003 (LockTimeout) |
| 7xxx | Durability | *(reserved)* |

**Rationale:** The ranges are well-organized and already documented. Only Tier 1 codes are defined in the enum now; reserved ranges get filled as later tiers are implemented.

### D7. Retry Ownership → Caller, Not Engine

**Decision:** The engine does **not** provide a built-in retry loop (`IRetryPolicy`, `ExecuteWithRetry`). Retry is the caller's responsibility. The engine's role is to throw structured exceptions with `IsTransient` as a hint.

**What the engine provides:**
- `IsTransient` flag on `TyphonException` — tells the caller "this failure is temporary, retrying might work"
- `ErrorCode` and `Category` — enough information for the caller to make a decision
- Micro-retries inside the engine via `ExhaustionPolicy` + `WaitContext` — bounded resource-level spin (e.g., page eviction, lock spin-wait), safe because they operate before mutations

**What the engine does NOT provide:**
- `IRetryPolicy` interface (moved to samples/patterns appendix in `10-errors.md`)
- `ExecuteWithRetry` helper (same — appendix only, not an engine API)
- Any automatic transaction-level retry mechanism

**Rationale:** A generic retry wrapper assumes the retried lambda is pure (no side effects outside the transaction). In practice, callers often have external side effects (network calls, in-memory state, resource allocation) that the wrapper cannot undo. Only the caller knows their full state and can manage retry safely. `IsTransient` gives them the information; they own the decision and mechanism.

**Impact on `10-errors.md`:** Updated §10.3 to reflect this. `IRetryPolicy` and `ExecuteWithRetry` moved to a new "Appendix: Retry Pattern" section, clearly labeled as a sample for application developers, not an engine API.

### D8. TyphonException Metadata → Typed Properties Per Subclass

**Decision:** Each exception subclass defines its own strongly-typed properties. No generic `IReadOnlyDictionary<string, object>` context dictionary on the base class.

**Example:**
- `LockTimeoutException` → `string ResourceName`, `TimeSpan WaitDuration`
- `ResourceExhaustedException` → `string ResourcePath`, `ResourceType ResourceType`, `long CurrentUsage`, `long Limit` (already has these)
- `CorruptionException` → `string ComponentName`, `int PageIndex`

**Rationale:** Typed properties are discoverable, zero-allocation, and IDE-friendly. A generic dictionary boxes value types and forces callers to cast. Since each exception subclass represents a specific failure mode, the metadata is known at design time.

### D9. InvalidOperationException Migration → User-Facing Only

**Decision:** In Tier 1, replace `InvalidOperationException` only at sites where callers need to catch and handle the error (lock timeouts, state violations in public API). Leave internal assertion-like throws (precondition checks deep in the engine) as `InvalidOperationException`.

**Codebase state:** 25 `InvalidOperationException` throws, 23 `ArgumentOutOfRangeException`, 11 `ArgumentNullException`. Most are internal precondition checks that indicate bugs, not recoverable errors.

**Rationale:** A massive migration of all ~93 throw sites would be a large diff with no functional benefit for internal assertions. These should continue throwing standard .NET exceptions — they represent programming errors, not operational failures.

### D10. Timeout Exception Hierarchy → Intermediate TyphonTimeoutException Base

**Decision:** Create `TyphonTimeoutException` as an intermediate base class under `TyphonException`. All timeout-related exceptions inherit from it.

**Hierarchy:**
```
TyphonException
  ├─ TyphonTimeoutException                 catch (TyphonTimeoutException) → all timeouts
  │    ├─ LockTimeoutException              Tier 1 — lock acquisition timeout
  │    └─ TransactionTimeoutException       Tier 1 class — throw sites activated in Tier 2
  └─ ResourceExhaustedException             Tier 1 — re-parented directly (no intermediate)
```

**Rationale:** Callers will have multiple timeout sources (locks in Tier 1, transaction deadlines in Tier 2, query execution in Tier 7). `catch (TyphonTimeoutException)` provides a single catch block for "any timeout, regardless of source" — a common handling pattern. This does NOT inherit from `System.TimeoutException` because that would break `catch (TyphonException)` due to single-inheritance; Typhon callers should learn the Typhon hierarchy.

### D11. ThrowHelper Pattern → Extend Centralized ThrowHelper

**Decision:** Add `TyphonException` throw methods to the existing `ThrowHelper` class using `[MethodImpl(MethodImplOptions.NoInlining)]`. Hot-path throw sites call `ThrowHelper.ThrowLockTimeout(...)` instead of `throw new LockTimeoutException(...)`.

**Example:**
```csharp
internal static class ThrowHelper
{
    // Existing
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgument(string message) => throw new ArgumentException(message);

    // New — Tier 1
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowLockTimeout(string resource, TimeSpan waitDuration)
        => throw new LockTimeoutException(resource, waitDuration);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowCorruption(string component, int pageIndex, string detail)
        => throw new CorruptionException(component, pageIndex, detail);
}
```

**Rationale:** The codebase already uses this pattern. `[NoInlining]` prevents the JIT from inlining the throw path into the calling method, keeping hot-path method bodies small and cache-friendly. Centralizing also makes it easy to add telemetry (error counters) at the throw point later.

### D12. ExhaustionPolicy Role → Metadata on ResourceNode, Not Runtime Dispatch

**Decision:** Keep the `ExhaustionPolicy` enum. Anchor it to the resource graph by adding an `ExhaustionPolicy` property to `ResourceNode`, set at construction/registration time. It is **never** used for runtime dispatch (each subsystem hardcodes its own behavior). Its role is purely diagnostic metadata.

**Current state:** The enum exists with 4 values (FailFast, Wait, Evict, Degrade) but is referenced by zero production code. Each subsystem already implements the correct behavior implicitly (PageCache → Evict+Wait, ChunkBasedSegment → Degrade+FailFast, TransactionPool → FailFast intended but unenforced).

**What changes (in #39 design):**
- `ResourceNode` gains an `ExhaustionPolicy` property (set at construction, immutable)
- 5–6 resource registration sites pass the appropriate policy value
- `ResourceSnapshot` / `FindRootCause` can expose it for diagnostics: *"PageCache at 95% — policy: Evict (self-healing)"* vs *"TransactionPool at 100% — policy: FailFast (callers see exceptions)"*
- No `switch (policy)` dispatch anywhere — the policy is documentation-as-code, not a strategy pattern

**Rationale:** Without this anchor, the enum is dead code — and dead code should be deleted, not kept "for documentation" (that's what markdown is for). Connecting it to the resource graph gives it a concrete, queryable purpose while keeping the maintenance cost near zero.

## Audit Results

### WaitContext.Null — 31 Subsystem Call Sites (47 total in src/)

| Subsystem | Count | Key Files |
|-----------|-------|-----------|
| PageCache | 10 | `PagedMMF.cs` (8), `ManagedPagedMMF.cs` (2) |
| SegmentAllocation | 7 | `ChainedBlockAllocatorBase.cs` (4), `VariableSizedBufferSegment.cs` (3), `ChainedBlockAllocator.cs` (1) |
| BTree | 5 | `BTree.cs` — Insert, Delete, TryGet, DeleteValue, CheckConsistency |
| TransactionChain | 4 | `TransactionChain.cs` (3), `Transaction.cs` (1) |
| RevisionChain | 4 | `ComponentRevisionManager.cs` (3), `RevisionEnumerator.cs` (1) |
| Concurrency internals | 16 | `AccessControl.cs` (4), `AccessControlSmall.cs` (4), `ResourceAccessControl.cs` (6), `AccessControl.LockData.cs` (1), `WaitContext.cs` (1) |
| **Total** | **47** | *31 subsystem call sites + 16 internal to concurrency primitives* |

### Current Exception Landscape

| Metric | Value |
|--------|-------|
| Exception types thrown | 11 types, ~93 `throw` statements |
| `InvalidOperationException` | 25 throws — state violations, assertions |
| `ArgumentOutOfRangeException` | 23 throws — parameter validation |
| Custom exceptions | Only `ResourceExhaustedException` (0 throw sites, 0 catch sites) |
| Catch blocks in engine | 5 total — engine is almost purely a "thrower" |
| `TimeoutException` | 1 throw in `ResourceAccessControl.cs` → will become `LockTimeoutException` |
| Existing `ThrowHelper` | Yes, in `ChunkAccessor.cs` with `[NoInlining]` pattern |
| Existing `Result<T>` | Does not exist |
| Existing error code enums | None |

## Benchmark Results

### Result Pattern Comparison (2026-02-06)

Benchmark: `test/Typhon.Benchmark/ResultPatternBenchmark.cs` — BenchmarkDotNet, Release, .NET 10, 64 lookups over a 64-element array (simulates B+Tree leaf scan).

| Method | Mean | Ratio vs Baseline | Allocated |
|--------|------|-------------------|-----------|
| **bool + out (Found)** | 785 ns | 1.00 (baseline) | 0 B |
| **Result&lt;T&gt; single-generic (Found)** | 884 ns | 1.14 | 0 B |
| **Result&lt;TValue, TStatus&gt; dual-generic (Found)** | 780 ns | 1.01 | 0 B |
| **bool + out (NotFound)** | 1,915 ns | 2.47 | 0 B |
| **Result&lt;T&gt; single-generic (NotFound)** | 1,820 ns | 2.35 | 0 B |
| **Result&lt;TValue, TStatus&gt; dual-generic (NotFound)** | 1,797 ns | 2.32 | 0 B |
| **bool + out (Mixed)** | 2,650 ns | 3.42 | 0 B |
| **Result&lt;T&gt; single-generic (Mixed)** | 2,501 ns | 3.23 | 0 B |
| **Result&lt;TValue, TStatus&gt; dual-generic (Mixed)** | 2,394 ns | 3.09 | 0 B |
| **Result&lt;TValue, TStatus&gt; status switch** | 55 ns | 0.07 | 0 B |

**Conclusion:** `Result<TValue, TStatus>` has **zero measurable overhead** vs `bool + out`. The dual-generic struct fits in registers, the `Unsafe.As<TStatus, byte>` check compiles to a single byte comparison, and the per-subsystem status enum switch compiles to a near-free jump table. Decision D4 is validated.

### ExhaustionPolicy Integration Review

The `ExhaustionPolicy` enum (FailFast, Wait, Evict, Degrade) is defined but **not referenced** by any production code. Each subsystem already implements the correct behavior implicitly:

| Resource | Designed Policy | Current Implementation | Status |
|----------|----------------|----------------------|--------|
| **PageCache** | Evict → Wait (if pinned) | Clock-sweep evict, then `AdaptiveWaiter.Spin()` | Aligned — but wait path uses unbounded spin, not `WaitContext` deadline |
| **ChunkBasedSegment** | Degrade → FailFast | Auto-grow, then `throw InvalidOperationException` | Aligned — but should throw `ResourceExhaustedException` |
| **TransactionPool** | FailFast | `MaxActiveTransactions` reported in metrics only; **no limit enforced** | **Missing** — no check in `TransactionChain.CreateTransaction()` |
| **ResourceTelemetryAllocator** | Degrade | Silent failure on allocation exhaustion, continues without logging | Aligned |
| **ConcurrentArray** | FailFast | `ThrowCapacityReached()` on overflow | Aligned |

**Key Tier 1 actions for ExhaustionPolicy:**
1. **TransactionPool**: Add `MaxActiveTransactions` check in `TransactionChain.CreateTransaction()`, throw `ResourceExhaustedException`
2. **PageCache wait path**: Replace unbounded `AdaptiveWaiter.Spin()` with `WaitContext`-aware wait that respects deadlines
3. **ChunkBasedSegment**: Replace `InvalidOperationException` with `ResourceExhaustedException` in `AllocateChunk()`/`AllocateChunks()`

**Not Tier 1** (behavior already correct):
- ResourceTelemetryAllocator (silent degrade is correct for observability)
- ConcurrentArray (internal data structure, `FailFast` is correct but exception type is fine as-is)

### TestWaitContext Helper Design

**Problem:** 202 `WaitContext.Null` occurrences across 5 test files. Lock APIs take `ref WaitContext`, and `WaitContext.Null` returns `ref Unsafe.NullRef<WaitContext>()` (detected by lock primitives to skip deadline checks). Replacing with a bounded timeout requires providing a real `WaitContext` instance by reference.

**Design — `ThreadStatic` ref-return pattern:**

```csharp
// In test/Typhon.Engine.Tests/Helpers/TestWaitContext.cs
public static class TestWaitContext
{
    /// <summary>Default test timeout — generous but finite.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    [ThreadStatic] private static WaitContext _current;

    /// <summary>
    /// Returns a ref to a fresh 10-second WaitContext.
    /// Drop-in replacement for <c>ref WaitContext.Null</c>.
    /// Thread-safe: uses [ThreadStatic] backing field.
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

**Call site migration:**
```csharp
// Before:
control.EnterExclusiveAccess(ref WaitContext.Null);

// After:
control.EnterExclusiveAccess(ref TestWaitContext.Default);
```

**Why `ThreadStatic`?** NUnit runs tests on different threads in parallel mode. A `ThreadStatic` field has a stable per-thread address, so the `ref` return is safe. Each access creates a fresh deadline (10s from now), so nested calls within a single test each get a full 10s window.

**Stress test opt-out:** Stress tests that intentionally hold locks for extended periods explicitly use `ref WaitContext.Null` or create their own `WaitContext` with longer timeouts. They should document why with a comment.

**Migration scope:** 202 occurrences across 5 files:
- `AccessControlTests.cs` (34)
- `AccessControlSmallTests.cs` (79)
- `AccessControlTelemetryTests.cs` (21)
- `ResourceAccessControlTests.cs` (66)
- `ManagedPagedMMFTests.cs` (2)

## Next Steps

- [x] ~~Verify `Result<TValue, TStatus>` hot-path candidates with benchmark impact analysis~~
- [x] ~~Review `ExhaustionPolicy` integration points in PageCache, ChunkBasedSegment, TransactionPool~~
- [x] ~~Design the `TestWaitContext` helper for test infrastructure~~

## References

- GitHub Issue: #36
- Sub-issues: #37, #38, #39, #40
- Depends on: #34 (Tier 0 — merged and done)
- Blocks: #41 (Tier 2 — Execution Context)
- Existing timeout research: `claude/research/timeout/` (7-part series)
- Error model overview: `claude/overview/10-errors.md`
- Concurrency primitives: `claude/overview/01-concurrency.md`
- Budgets & Exhaustion: `claude/overview/08-resources.md` §8.7

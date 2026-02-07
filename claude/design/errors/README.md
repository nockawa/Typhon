# Error Foundation & Timeout Activation — Design

**Date:** 2026-02-06
**Status:** Ready for implementation
**GitHub Issue:** #36 (umbrella), Sub-issues: #37, #38, #39, #40
**Research:** [`claude/research/timeout/ErrorFoundationTimeoutActivation.md`](../../research/timeout/ErrorFoundationTimeoutActivation.md)

> 💡 **TL;DR — Jump to the sub-issue you're implementing:**
> [#37 Exception Hierarchy](./01-exception-hierarchy.md) · [#38 Deadline Propagation](./02-deadline-propagation.md) · [#39 ExhaustionPolicy](./03-exhaustion-policy.md) · [#40 Result\<T\>](./04-result-type.md)

## Summary

Make the engine **fail reliably instead of hang silently**. Today, every lock acquisition uses `WaitContext.Null` (infinite timeout), meaning a stuck lock hangs the caller forever. This design activates the existing timeout infrastructure (`Deadline`, `WaitContext`) and builds a structured error vocabulary so callers get actionable exceptions with error codes and transience hints.

## Goals

- Structured exception hierarchy rooted at `TyphonException` with error codes and `IsTransient`
- Replace all 47 production `WaitContext.Null` sites (31 subsystem + 16 concurrency-internal) with finite, configurable deadlines
- Enforce `ExhaustionPolicy` on the 3 resource paths that need it (TransactionPool, PageCache wait, ChunkBasedSegment)
- Zero-allocation `Result<TValue, TStatus>` for B+Tree lookups and revision chain reads
- All existing tests pass — no regressions from timeout changes

## Non-Goals

- **Retry policies**: The engine provides `IsTransient` hints; retry logic is the caller's responsibility (D7)
- **Tier 2+ exceptions**: Only exceptions that Tier 1 actually throws are created. No dead code stubs.
- **Full `InvalidOperationException` migration**: Only user-facing sites change; internal assertion throws stay as-is (D9)
- **Runtime ExhaustionPolicy dispatch**: The policy is metadata, not a strategy pattern (D12)

## Design Decisions (from Research)

All 12 decisions were resolved during research. See the [research doc](../../research/timeout/ErrorFoundationTimeoutActivation.md#decisions) for full rationale. Key decisions summarized:

| ID | Decision | Impact |
|----|----------|--------|
| D1 | Core + stubs: only create exception classes Tier 1 uses. No `ErrorCategory` enum — type hierarchy is the classification. | Small initial surface |
| D2 | Re-parent `ResourceExhaustedException` → `TyphonException` (no intermediate) | Breaking change, pre-1.0 acceptable |
| D3 | Subsystem-specific default timeouts (5s/10s), all configurable | `DatabaseEngineOptions` gains timeout settings |
| D4 | `Result<TValue, TStatus>` dual-generic with per-subsystem byte enums | Zero overhead (benchmark-validated) |
| D5 | 10s default test timeout via `TestWaitContext` helper | 202 test call sites migrate |
| D6 | Error code ranges from `10-errors.md` (1xxx–7xxx) | Tier 1 codes only |
| D7 | Retry is caller-owned; engine provides `IsTransient` hint | No `IRetryPolicy` in engine |
| D8 | Typed properties per exception subclass (no `Context` dictionary) | Zero-allocation metadata |
| D9 | Replace `InvalidOperationException` only at user-facing sites | ~5–8 sites, not all ~93 |
| D10 | `TyphonTimeoutException` intermediate base for all timeouts | `catch (TyphonTimeoutException)` catches all |
| D11 | Extend existing `ThrowHelper` with `[NoInlining]` throw methods | Hot-path JIT optimization |
| D12 | `ExhaustionPolicy` as metadata on `ResourceNode` | Diagnostic, not dispatch |

## Implementation Order

The sub-issues have natural dependencies:

```
#37 Exception Hierarchy ──┐
                          ├──► #38 Deadline Propagation
#40 Result<T> ────────────┘         │
                                    ▼
                              #39 ExhaustionPolicy
```

**Recommended order:**

1. **#37 + #40 in parallel**: Exception hierarchy and Result type are independent of each other. Both are pure type definitions with no runtime behavior changes.
2. **#38 after #37**: Deadline propagation needs `LockTimeoutException` to throw at timeout sites.
3. **#39 after #37 + #38**: ExhaustionPolicy enforcement needs both the exceptions (#37) and the `WaitContext` propagation (#38) for the PageCache wait path.

## Document Series

| Part | Sub-Issue | Focus |
|------|-----------|-------|
| [01](./01-exception-hierarchy.md) | **#37** | `TyphonException` base, error codes, subclasses, ThrowHelper |
| [02](./02-deadline-propagation.md) | **#38** | Replace 47 `WaitContext.Null` sites, `DatabaseEngineOptions` timeouts, `TestWaitContext` |
| [03](./03-exhaustion-policy.md) | **#39** | TransactionPool limit, PageCache bounded wait, ChunkBasedSegment exception upgrade |
| [04](./04-result-type.md) | **#40** | `Result<TValue, TStatus>`, per-subsystem status enums, integration points |

## Testing Strategy

Each sub-issue has its own test plan (see individual docs). Cross-cutting concerns:

- **Regression**: All existing tests must pass after each sub-issue is merged
- **Timeout tests**: ~128 contention test sites migrate to `TestWaitContext.Default` (10s bounded); ~74 single-threaded test sites keep `WaitContext.Null`
- **No flaky tests**: 10s timeout is generous enough for CI; stress tests opt in to longer/infinite timeouts explicitly
- **Exception contract tests**: Each new exception class gets tests verifying error code, IsTransient, and typed properties

## Acceptance Criteria (from Issue #36)

- [ ] `TyphonException` base class with `ErrorCode`, `IsTransient`
- [ ] Key subclasses: `LockTimeoutException`, `TransactionTimeoutException`, `StorageException`, `CorruptionException`
- [ ] All 47 `WaitContext.Null` call sites replaced with finite deadlines
- [ ] Lock timeout → `LockTimeoutException` instead of infinite hang
- [ ] `ExhaustionPolicy` enforced: PageCache (Evict→Wait→throw), ChunkBasedSegment (throw on full), TransactionPool (FailFast at max)
- [ ] `Result<T>` struct for B+Tree lookups and revision chain reads (chunk access excluded — bounds violations are hard errors, not expected outcomes)
- [ ] All existing tests pass (no regressions from timeout changes)

## Overview Updates

The following overview documents have been updated to reflect Tier 1 design decisions. After each sub-issue is implemented, verify these stay in sync:

| Overview | What Changed | Design Source |
|----------|-------------|---------------|
| [`10-errors.md`](../../overview/10-errors.md) | Removed `ErrorCategory` enum, `IsTransient` → virtual property, `Result` API uses public fields + constructors, removed `ChunkAccessStatus` | #37, #40 |
| [`01-concurrency.md`](../../overview/01-concurrency.md) | Removed stale "to be migrated to Deadline" note, fixed `ResourceAccessControl` code location | #38 |
| [`03-storage.md`](../../overview/03-storage.md) | Completed auto-growth exhaustion section (throws `ResourceExhaustedException`) | #39 |
| [`08-resources.md`](../../overview/08-resources.md) | Added `TimeoutOptions` mention, noted `ResourceNode.ExhaustionPolicy` property | #38, #39 |

## References

- Research: [`claude/research/timeout/ErrorFoundationTimeoutActivation.md`](../../research/timeout/ErrorFoundationTimeoutActivation.md)
- Timeout research series: [`claude/research/timeout/`](../../research/timeout/) (7-part deep dive)
- Error model overview: [`claude/overview/10-errors.md`](../../overview/10-errors.md)
- Concurrency primitives: [`claude/overview/01-concurrency.md`](../../overview/01-concurrency.md)
- Resource budgets: [`claude/overview/08-resources.md`](../../overview/08-resources.md) §8.7
- Benchmark: `test/Typhon.Benchmark/ResultPatternBenchmark.cs`

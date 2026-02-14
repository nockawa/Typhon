# Execution Context & Deadline Watchdog — Design

**Date:** 2026-02-13
**Status:** In Progress
**Branch:** `feature/41-execution-context`
**GitHub Issue:** #41 (umbrella), Sub-issues: #76, #42, #43, #44, #45
**Research:** [`claude/research/timeout/`](../../research/timeout/) (7-part series)
**Architecture:** [`claude/overview/02-execution.md`](../../overview/02-execution.md) §2.4–2.7

> 💡 **TL;DR — Jump to the sub-issue you're implementing:**
> [#76 High-Resolution Timers](../high-resolution-timer.md) · [#42 UnitOfWorkContext](./01-uow-context.md) · [#43 DeadlineWatchdog](./02-deadline-watchdog.md) · [#44 Yield Points & Holdoff](./03-yield-points-holdoff.md) · [#45 Transaction API](./04-transaction-api.md)

## Summary

Build a **coordinated timeout/cancellation model** so that when a user says "this operation must complete in 500ms", that deadline propagates through transaction commit → lock acquisition → page access → B+Tree traversal. Instead of silent hangs, operations fail with rich diagnostics when a deadline expires.

This is **Tier 2** in the reliability roadmap. [Tier 1 (#36)](../../reference/errors/) established the exception hierarchy, deadline propagation in lock sites, and `WaitContext` infrastructure. Tier 2 builds the execution context that coordinates all of this from a single entry point.

## Goals

- 24-byte `UnitOfWorkContext` struct carrying deadline + cancellation + holdoff through every operation
- `DeadlineWatchdog` that fires CancellationToken when deadlines expire (via `HighResolutionSharedTimerService` at 200Hz)
- Cooperative cancellation at safe yield points throughout the engine
- Holdoff regions protecting critical sections (node splits, page writes, commit atomicity)
- `Transaction.Commit()` and `Rollback()` accept `ref UnitOfWorkContext` with backward compatibility

## Non-Goals

- **Unit of Work class**: The `UnitOfWork` durability boundary is Tier 3+ work. Tier 2 builds only the *context* it will carry.
- **WAL/durability integration**: WAL serialization and epoch stamping are separate concerns.
- **Async integration**: `AsyncLocal<UnitOfWorkContext>` is deferred (overview §2.8).
- **Work scheduler**: Deferred to post-1.0 (overview §2.9).

## Key Design Deviation

The overview doc (§2.4) specifies `UnitOfWorkContext` as a **pooled class**. After discussion, we chose a **struct** instead:

| Factor | Class (overview) | Struct (chosen) |
|--------|-----------------|-----------------|
| Allocation | Pooled via ConcurrentQueue | Zero — stack-allocated |
| Passing | By reference (natural) | By `ref` (explicit) |
| GC pressure | Pool reduces but doesn't eliminate | None |
| CancellationTokenSource | Can be owned by class | Owned externally (by future UoW) |
| Hot path cost | Pool rent/return (~20-50ns) | Zero |

**Rationale:** CancellationToken is a struct (8B), so the context doesn't need managed references. The `UowId` field is included but reserved for future UoW integration. The `_holdoffCount` is the only mutable state, requiring `ref` passing — acceptable for Typhon's synchronous commit path.

## Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| D1 | UnitOfWorkContext as struct (24B) with embedded WaitContext | Zero allocation, embeds existing WaitContext for direct `ref` passing to lock primitives |
| D2 | `ThrowIfCancelled()` naming | Follows .NET `ThrowIf*` convention, matches overview doc |
| D3 | DeadlineWatchdog as static class using `HighResolutionSharedTimerService` at 200Hz | No dedicated thread needed; 5ms check interval gives <5ms cancellation latency with near-zero CPU cost |
| D4 | Commit yield point at start only | Prevents partial commit atomicity violation |
| D5 | Entire commit loop in holdoff | Once commit starts modifying data, it runs to completion |
| D6 | Backward-compatible `Commit()` overload | Existing callers unaffected; wrapper uses default timeout |
| D7 | `HoldoffScope` as `ref struct` with `ref` field | C# 13 supports ref fields; follows `EpochGuard` pattern |
| D8 | `UowId` field included but reserved | Stabilizes struct layout for future UoW integration |

## Implementation Order

```
#76 High-Resolution Timers ───┐
#42 UnitOfWorkContext struct ─┤
                              ├──► #44 Yield points & holdoff ──► #45 Transaction API
#43 DeadlineWatchdog ─────────┘
```

1. **#76 first** — timer infrastructure that #43 depends on (already implemented)
2. **#42 next** — foundation struct everything else builds on
3. **#43 in parallel or after #42** — independent of struct internals, depends on #76
4. **#44 after #42** — yield points need `UnitOfWorkContext` to exist
5. **#45 last** — integrates everything into the Transaction API

## Document Series

| Part | Sub-Issue | Focus |
|------|-----------|-------|
| [Timer](../high-resolution-timer.md) | **#76** | `HighResolutionTimerServiceBase`, `HighResolutionTimerService`, `HighResolutionSharedTimerService`, `ITimerRegistration` |
| [01](./01-uow-context.md) | **#42** | `UnitOfWorkContext` struct layout, factories, `ThrowIfCancelled`, `HoldoffScope`, `WaitContext` bridge |
| [02](./02-deadline-watchdog.md) | **#43** | `DeadlineWatchdog` static class, priority queue, shared timer callback (200Hz), shutdown |
| [03](./03-yield-points-holdoff.md) | **#44** | Yield point catalog, holdoff region catalog, integration patterns |
| [04](./04-transaction-api.md) | **#45** | New `Commit`/`Rollback` overloads, deadline composition, backward compat |

## Prerequisites (Tier 1)

All of these are implemented and tested:

- [`01-exception-hierarchy.md`](../../reference/errors/01-exception-hierarchy.md) — `TyphonException`, `TyphonTimeoutException`, `TransactionTimeoutException`, `LockTimeoutException`
- [`02-deadline-propagation.md`](../../reference/errors/02-deadline-propagation.md) — 31 production lock sites with finite deadlines, `TimeoutOptions`
- [`03-exhaustion-policy.md`](../../reference/errors/03-exhaustion-policy.md) — `ExhaustionPolicy` enforcement
- [`04-result-type.md`](../../reference/errors/04-result-type.md) — `Result<TValue, TStatus>` for hot-path error returns

## Testing Strategy

Each sub-issue has its own test plan (see individual docs). Cross-cutting:

- **Regression**: All existing tests must pass after each sub-issue
- **End-to-end**: Set 500ms deadline → create contention → verify `TransactionTimeoutException` with full diagnostic context
- **Timing tests**: Use generous margins (50ms for 10ms deadline) and `[Category("Timing")]` for CI sensitivity

## References

- Architecture: [`claude/overview/02-execution.md`](../../overview/02-execution.md) §2.4–2.7
- Concurrency: [`claude/overview/01-concurrency.md`](../../overview/01-concurrency.md) §1.2, §1.6
- Research: [`claude/research/timeout/07-design-guidelines.md`](../../research/timeout/07-design-guidelines.md) §7.3–7.4
- Tier 1 reference: [`claude/reference/errors/`](../../reference/errors/)

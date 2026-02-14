# ADR-034: UnitOfWorkContext as 24-Byte Struct with Ref Passing

**Status**: Accepted
**Date**: 2026-02-13
**Deciders**: Developer + Claude Code
**Related**: [ADR-031: Unified Concurrency Patterns](031-unified-concurrency-patterns.md), Issue #41 (Coordinated Timeout/Cancellation), Issues #42–#45

## Context

Typhon's execution model needs a context object that flows through all operations within a Unit of Work or transaction: deadline, cancellation token, UoW ID, and holdoff state for critical section protection. Every major database engine has this pattern (SQL Server's `SOS_Task`, PostgreSQL's `PGPROC`, MySQL's `THD`).

The original design (documented in [02-execution.md §2.4](../overview/02-execution.md#24-executioncontext)) specified a **pooled class** — a heap-allocated object with `CancellationTokenSource`, rented from a `ConcurrentQueue` pool and returned on UoW dispose. This was motivated by the assumption that `CancellationTokenSource` required managed heap allocation.

Several factors challenged this assumption during implementation:

1. **CancellationToken is a value type** — it wraps a reference to `CancellationTokenSource`, but the token itself is 8 bytes and copyable. The watchdog owns the `CancellationTokenSource`; the context only needs the derived `CancellationToken`.
2. **WaitContext already exists as a 16-byte struct** — `Deadline` (8 bytes) + `CancellationToken` (8 bytes), passed by `ref` throughout all synchronization primitives (ADR-031). A context built *on top of* WaitContext naturally inherits the struct pattern.
3. **Hot path cost** — the context is checked at every yield point (`ThrowIfCancelled()`) and accessed at every lock acquisition. Heap indirection adds a cache miss on a path that should be ~10-25ns.
4. **Pool overhead** — `ConcurrentQueue` rent/return, reset logic, and potential false sharing on the pool itself add allocation-free but not overhead-free cost.
5. **Ref struct precedent** — Typhon already passes `ChunkAccessor` (~280 bytes), `EpochGuard`, `AtomicChange`, and `LockData` as ref types. The pattern of stack-allocated + ref-passing is established.

## Decision

Implement `UnitOfWorkContext` as a **24-byte struct** passed by `ref` (not a pooled class):

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct UnitOfWorkContext  // 24 bytes, 3 × 8-byte qwords
{
    public WaitContext WaitContext;   // 16 bytes (Deadline + CancellationToken)
    public ushort UowId;             // 2 bytes
    private ushort _padding;         // 2 bytes (alignment)
    private int _holdoffCount;       // 4 bytes (nesting counter)
}
```

Key design choices:

1. **Embeds WaitContext** (16 bytes) rather than duplicating Deadline + CancellationToken fields. This allows zero-copy extraction via `ref ctx.WaitContext` for lock acquisition sites.
2. **Holdoff counter is mutable** — the struct is passed by `ref`, so `EnterHoldoff()` / `EndHoldoff()` mutate in place. The `[NoCopy]` attribute (Typhon custom) flags accidental by-value copies.
3. **Factory methods** create contexts at operation boundaries:
   - `UnitOfWorkContext.FromTimeout(TimeSpan)` — standalone operations
   - `UnitOfWorkContext.None` — infinite deadline, no cancellation (backward-compatible wrappers)
4. **Transaction API** accepts `ref UnitOfWorkContext`:
   - `Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)` — new overload
   - `Commit(ConcurrencyConflictHandler handler = null)` — backward-compatible, creates `FromTimeout(DefaultCommitTimeout)`

## Alternatives Considered

### A: Pooled Class (Original Design)

```csharp
public sealed class UnitOfWorkContext : IDisposable { ... }
```

**Pros:**
- Natural for holding managed references (CancellationTokenSource)
- No ref-passing ceremony through the call stack
- Pooling avoids repeated allocation

**Cons:**
- Heap allocation adds GC pressure (even pooled, the CTS inside allocates)
- Cache miss on every access (indirection through object reference)
- Pool synchronization overhead
- Cannot embed WaitContext by-ref (class fields are copies, not refs)

**Rejected because:** The CancellationTokenSource is owned by DeadlineWatchdog, not the context. The context only needs the derived CancellationToken (8-byte value type). With that realization, no managed reference needs to be held, eliminating the primary reason for a class.

### B: Ref Struct (Stack-Only)

```csharp
public ref struct UnitOfWorkContext { ... }
```

**Pros:**
- Cannot escape to heap (compile-time safety)
- Can hold `ref` fields (C# 11+)

**Cons:**
- Cannot be stored in async state machines
- Cannot be a field in non-ref structs
- Limits future UoW class design (UoW.Context can't be a ref struct property)

**Rejected because:** While the current usage is synchronous and stack-bound, a ref struct is too restrictive for the eventual UoW class (§2.1) which needs to store the context as a field.

### C: 16-Byte Struct (Just WaitContext + Epoch)

Drop the holdoff counter and track holdoff state externally (e.g., thread-static or call-stack convention).

**Rejected because:** Holdoff nesting is critical for correctness (commit holdoff + nested split holdoff). A counter in the context is the simplest correct implementation, and 24 bytes still fits in 3 cache-line qwords.

## Consequences

### Positive

- **Zero allocation**: No heap object, no pool, no GC pressure — ideal for game server ticks processing thousands of operations
- **Cache-friendly**: 24 bytes fits in a single cache line alongside the caller's stack frame
- **Zero-copy WaitContext extraction**: Lock acquisition sites use `ref ctx.WaitContext` directly — no copy, no conversion
- **Deadline composition**: `Deadline.Min(ctx.WaitContext.Deadline, subsystemTimeout)` at lock sites ensures the tighter deadline wins
- **Backward compatibility**: Existing `Commit()` and `Rollback()` parameterless overloads create a default context internally — all existing tests pass unchanged

### Negative

- **Ref-passing ceremony**: Every method in the call chain that needs the context must accept `ref UnitOfWorkContext`. This is already the pattern for `ref ChunkAccessor` in Typhon, so it's familiar but verbose.
- **Copy risk**: A struct passed by value silently loses holdoff counter mutations. The `[NoCopy]` attribute mitigates this with analyzer warnings, but it's not compiler-enforced.
- **Future UoW integration**: When the `UnitOfWork` class (§2.1) is implemented, it will need to store the context. Since it's a regular struct (not ref struct), this works — but the UoW must pass it by ref to inner operations.

### Neutral

- **24 bytes vs 16 bytes**: The holdoff counter adds 8 bytes (4 bytes + 2 padding + 2 UoW ID). On a hot path that already passes 16-byte WaitContext by ref, the marginal cost of 8 more bytes is negligible.
- **Thread safety**: The struct is single-threaded by design (one UoW or transaction per thread). No synchronization needed on the holdoff counter.

## Implementation

Delivered across Issues #41–#45 (Tier 2: Coordinated Timeout/Cancellation):

- **#76**: High-resolution timer infrastructure (HighResolutionTimerServiceBase, shared/dedicated timers)
- **#42**: UnitOfWorkContext struct (24 bytes, factories, ThrowIfCancelled, EnterHoldoff)
- **#43**: DeadlineWatchdog (200Hz via shared timer, CancellationToken firing)
- **#44**: Yield points at safe locations + holdoff regions around critical sections
- **#45**: Transaction API integration (Commit/Rollback overloads, deadline composition, backward compatibility)

# ADR-033: Epoch-Based Page Eviction

| Field | Value |
|-------|-------|
| **Status** | Accepted |
| **Date** | 2026-02-12 |
| **Supersedes** | Extends [ADR-007: Clock-Sweep Eviction](007-clock-sweep-eviction.md) |
| **Related** | [Design: Epoch Resource Management](../archive/epoch-resource-management.md), Issue #69 |

## Context

The original page cache eviction used per-page reference counting (`ConcurrentSharedCounter`) to determine whether a page was safe to evict. Each page access incremented the counter, each release decremented it. This created **2N obligations** for N page accesses per transaction, with several problems:

1. **Struct-copy safety bugs**: `ChunkAccessor` (~1KB struct) held `PageAccessor` instances with ref-count state. Accidental copies could corrupt counters.
2. **"All slots pinned" failure mode**: If all 16 `ChunkAccessor` slots held pinned pages, the accessor could not load new pages — a hard crash.
3. **Complex exclusive access coordination**: Promoting from `Shared` to `Exclusive` required ref-count transitions through multiple `PageState` values (`Shared → Idle → Exclusive`).
4. **6 page states**: `Free`, `Allocating`, `Idle`, `Shared`, `Exclusive`, `IdleAndDirty` — the `Shared` and `IdleAndDirty` states existed solely for ref-counting bookkeeping.

## Decision

Replace per-page reference counting with **epoch-based protection**:

1. **EpochGuard scope**: Each transaction enters an epoch scope that pins the current global epoch. All pages accessed within the scope are stamped with the epoch value.
2. **Eviction predicate**: A page is evictable when `(PageState == Idle) AND (DirtyCounter == 0) AND (AccessEpoch < MinActiveEpoch)`. This replaces the `ConcurrentSharedCounter == 0` check.
3. **Simplified PageState**: 4 states instead of 6 — `Free`, `Allocating`, `Idle`, `Exclusive`. The `Shared` and `IdleAndDirty` states are eliminated.
4. **Exclusive latching**: Uses `AccessControlSmall PageExclusiveLatch` with re-entrance depth tracking, decoupled from page lifetime.

## What Does NOT Change

- **Clock-sweep algorithm**: Still circular scan with counter-based second chance (ADR-007 unchanged).
- **Clock-sweep counter range**: Still 0-5, increment on access, decrement on scan.
- **Cache size and structure**: Still GCHandle-pinned byte array, configurable page count.
- **Dirty tracking**: `DirtyCounter` still tracks pending writes; pages with `DirtyCounter > 0` cannot be evicted.

## Consequences

### Positive

- **2 obligations per transaction** instead of 2N — one `EpochGuard.Enter()`, one `Dispose()`
- **No "all slots pinned" failure**: `ChunkAccessor` eviction always succeeds (no pinned slots to skip)
- **Simpler state machine**: 4 states vs 6, fewer transition paths to reason about
- **Smaller accessor**: `ChunkAccessor` (~280 bytes SOA) vs old `ChunkAccessor` (~1KB AOS)
- **Copy safety**: `EpochGuard` depth validation detects misuse; ref struct prevents heap capture

### Negative

- **Epoch scan cost**: `MinActiveEpoch` scans 256 slots (~128 cycles / ~40ns). This runs during eviction, which is already a slow path.
- **Long-running scopes delay eviction**: A transaction holding an `EpochGuard` for minutes prevents eviction of pages tagged during that epoch. Same trade-off as long-running MVCC transactions delaying revision cleanup.

### Neutral

- **Memory overhead**: +8 bytes per `PageInfo` (`AccessEpoch` field). With 256 pages = +2KB total.
- **Thread registry capacity**: 256 slots via `[ThreadStatic]`. Dead-thread slots reclaimed via `Thread.IsAlive` checks. If exhausted, throws `ResourceExhaustedException`.

## Implementation

Delivered in Issue #69 across 4 phases:
- **Phase 0**: `EpochManager`, `EpochThreadRegistry`, `EpochGuard` foundation
- **Phase 1**: `RequestPageEpoch`, dual-mode eviction predicate, `ChangeSet.AddByMemPageIndex`
- **Phase 2**: `ChunkAccessor` rewrite (SOA layout, clock-hand eviction, SIMD search)
- **Phase 3**: Migrate all callers (Transaction, B+Tree, CRUD, StringTable, segments)
- **Phase 4**: Delete legacy code (`PageAccessor`, `ChunkAccessor`, `PageState.Shared/IdleAndDirty`, `ConcurrentSharedCounter`), simplify `PageState`, update documentation

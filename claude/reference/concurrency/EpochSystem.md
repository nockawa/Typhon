# Epoch-Based Resource Protection

> **TL;DR:** Threads enter an epoch scope once per transaction. All page accesses within that scope are protected from eviction by a single epoch tag on each page — no ref-counting, no per-page obligations. Eviction checks `page.AccessEpoch < MinActiveEpoch` instead of `refCount == 0`.

**Status:** Implemented
**ADR:** [ADR-033: Epoch-Based Page Eviction](../../adr/033-epoch-based-page-eviction.md)

## Why Epochs

The old ref-counting model imposed **2N obligations** per N page accesses: every `RequestPage()` required a matching `ReleasePage()`. Missing a release leaked a ref-count, blocking eviction. Missing a page entirely and the eviction predicate would reclaim it from under a live reader.

The epoch model reduces this to **2 obligations per transaction**: `EnterScope()` and `ExitScope()`. Everything in between is protected.

## Architecture

```
EpochManager (1 per DatabaseEngine)
 ├── GlobalEpoch: long (monotonically increasing)
 ├── EpochThreadRegistry (SOA, 256 slots)
 │    ├── _pinnedEpochs[256]  (hot — scanned for MinActiveEpoch)
 │    ├── _slotStates[256]    (warm — enter/exit scope)
 │    ├── _depths[256]        (warm — nesting depth)
 │    └── _ownerThreads[256]  (cold — registration, liveness)
 └── MinActiveEpoch → min(_pinnedEpochs where != 0)

PageInfo
 └── AccessEpoch: long  ← stamped on every RequestPageEpoch()

Eviction predicate:
  page.AccessEpoch >= MinActiveEpoch → SKIP (epoch-protected)
  page.AccessEpoch <  MinActiveEpoch → EVICTABLE
```

## Key Types

### EpochManager

One instance per `DatabaseEngine`, registered as a `ResourceNode` under `ResourceType.Synchronization`.

| Member | Description |
|--------|-------------|
| `GlobalEpoch` | Current epoch counter. Starts at 1 (0 = "not pinned"). |
| `MinActiveEpoch` | Minimum pinned epoch across all threads. Pages at or above this value are protected. |
| `EnterScope()` | Pin the current thread to `GlobalEpoch`. Returns nesting depth. |
| `ExitScope(depth)` | Unpin if outermost scope; advances `GlobalEpoch` via `Interlocked.Increment`. Validates LIFO ordering. |
| `ExitScopeUnordered()` | Same as `ExitScope` but without LIFO validation. Used by `Transaction.Dispose()`. |

### EpochGuard

A `ref struct` RAII scope guard. Prevents heap allocation and boxing.

```csharp
using var guard = EpochGuard.Enter(epochManager);
// All page accesses inside this scope are epoch-protected.
// guard.Dispose() calls ExitScope() automatically.
```

**Copy safety:** The guard stores `_expectedDepth` from `EnterScope()`. If accidentally copied, `Dispose()` on the copy will detect a depth mismatch and throw.

### EpochThreadRegistry

Fixed-size (256 slots), SOA layout for cache-friendly scanning.

| Array | Temperature | Purpose |
|-------|-------------|---------|
| `_pinnedEpochs[256]` | Hot | Scanned every `ComputeMinActiveEpoch` call (2KB = 32 cache lines) |
| `_slotStates[256]` | Warm | CAS-based slot claiming/freeing |
| `_depths[256]` | Warm | Nesting depth tracking |
| `_ownerThreads[256]` | Cold | Thread liveness checks, dead-thread reclamation |

**Slot lifecycle:**
1. First `EnterScope()` on a thread → `ClaimSlot()` via CAS on `_slotStates`
2. Subsequent calls → O(1) via `[ThreadStatic] _threadSlotIndex`
3. Thread death → `EpochSlotHandle` (CriticalFinalizerObject) calls `FreeSlot()`
4. `ComputeMinActiveEpoch` also reclaims dead-thread slots opportunistically

### EpochSlotHandle

A `CriticalFinalizerObject` stored in a `[ThreadStatic]` field. When the owning thread dies and GC collects the handle, `FreeSlot()` releases the registry slot. Uses CAS-based idempotent free to handle races with `ComputeMinActiveEpoch`.

## Page Cache Integration

### RequestPageEpoch

`PagedMMF.RequestPageEpoch(filePageIndex, currentEpoch, out memPageIndex)`:

1. Resolve `filePageIndex` → `memPageIndex` (cache hit or allocate)
2. CAS-update `PageInfo.AccessEpoch` to `max(existing, currentEpoch)`
3. If page was in `Allocating` state (cache miss), transition to `Idle` **after** epoch tag
4. Increment clock-sweep counter (MRU hint for eviction)

The epoch tag is set **before** the page becomes evictable (`Idle`), ensuring no race window.

### Eviction (TryAcquire)

The clock-sweep eviction checks:
```
if (page.AccessEpoch >= minActiveEpoch || page.DirtyCounter > 0)
    → skip (protected)
```

This is checked twice: once without lock (fast reject), once under `StateSyncRoot` (confirm before evicting).

### Exclusive Latch (TryLatchPageExclusive)

For write operations, pages are exclusively latched via `PageExclusiveLatch` (an `AccessControlSmall`). Re-entrant within a thread (depth-tracked via `ExclusiveLatchDepth`). On unlatch, `AccessEpoch` is reset to 0 — the exclusive latch already protected the page during writes, so epoch protection is no longer needed after the latch is released.

## ChunkAccessor

The `ChunkAccessor` is the primary consumer of epoch-protected pages. It's a 248-byte struct with:

- **16-slot SOA page cache**: `_pageIndices[16]` (SIMD-searchable) + `_baseAddresses[16]`
- **Three-tier hot path**: MRU check (branch prediction) → SIMD Vector256 search → clock-hand eviction
- **Dirty tracking**: `_dirtyFlags` bitmask, flushed to `ChangeSet` on dispose
- **`[NoCopy]` attribute**: Roslyn analyzer prevents accidental value copies

ChunkAccessor requires an active epoch scope (asserted in constructor). Pages loaded into the cache are epoch-tagged, so they're protected from eviction for the duration of the epoch scope.

## PageState Machine

Four states (simplified from the original 6):

```
Free → Allocating → Idle ⇄ Exclusive
                      ↓
                   (evicted) → Free
```

| State | Meaning | Evictable? |
|-------|---------|------------|
| `Free` | Slot available for allocation | Yes |
| `Allocating` | I/O in progress, not yet usable | No |
| `Idle` | Contains valid data, accessible for reads | Only if `AccessEpoch < MinActiveEpoch` and `DirtyCounter == 0` |
| `Exclusive` | Write-latched by a thread | No |

## Usage Patterns

### Transaction (typical)
```csharp
// Transaction.BeginOperation() enters an epoch scope
using var t = dbe.CreateTransaction();

// All operations inside the transaction are epoch-protected
var entity = t.CreateEntity(ref comp);
t.ReadEntity(entity, out MyComp result);
t.Commit();  // ExitScopeUnordered() on dispose
```

### Direct epoch scope (low-level)
```csharp
using var guard = EpochGuard.Enter(epochManager);
var accessor = segment.CreateChunkAccessor();
// ... use accessor ...
accessor.Dispose();
// guard.Dispose() exits the epoch scope
```

### Manual scope (when ref struct isn't possible)
```csharp
var depth = epochManager.EnterScope();
try
{
    var accessor = segment.CreateChunkAccessor();
    // ... use accessor ...
    accessor.Dispose();
}
finally
{
    epochManager.ExitScope(depth);
}
```

## Performance Characteristics

| Operation | Cost |
|-----------|------|
| `EnterScope()` (registered thread) | ~2-3ns (ThreadStatic read + array write) |
| `ExitScope()` | ~5ns (array write + Interlocked.Increment for GlobalEpoch) |
| `ComputeMinActiveEpoch()` | ~40ns (scan 32 cache lines, slow-path only during eviction) |
| ChunkAccessor MRU hit | ~5ns |
| ChunkAccessor SIMD search hit | ~8ns |

## Thread Safety

- `GlobalEpoch` advanced via `Interlocked.Increment` — monotonically increasing, no ABA risk
- `_pinnedEpochs` written only by owning thread (or `FreeSlot` on dead threads) — no contention
- `PageInfo.AccessEpoch` updated via CAS-max loop — concurrent readers converge to highest epoch
- `_slotStates` claimed/freed via `Interlocked.CompareExchange` — idempotent `FreeSlot`
- No locks in the enter/exit fast path — fully wait-free for registered threads

## Metrics

Exposed via `IMetricSource` on `EpochManager`:

| Metric | Type | Description |
|--------|------|-------------|
| `EpochAdvances` | Throughput | Number of times GlobalEpoch was incremented |
| `ScopeEnters` | Throughput | Number of `EnterScope()` calls |
| `ActiveSlotCount` / `MaxSlots` | Capacity | Thread registry utilization |

Note: `_epochAdvances` and `_scopeEnters` use non-atomic `++` intentionally — accuracy is traded for zero overhead on the hot path (per project coding standards).

## Source Files

| File | Description |
|------|-------------|
| `src/Typhon.Engine/Concurrency/EpochManager.cs` | Epoch lifecycle and scope management |
| `src/Typhon.Engine/Concurrency/EpochThreadRegistry.cs` | SOA thread registry and slot management |
| `src/Typhon.Engine/Concurrency/EpochGuard.cs` | RAII ref struct scope guard |
| `src/Typhon.Engine/Storage/PagedMMF.cs` | `RequestPageEpoch`, `TryAcquire` (eviction predicate) |
| `src/Typhon.Engine/Storage/PagedMMF.PageInfo.cs` | `AccessEpoch` field |
| `src/Typhon.Engine/Storage/Segments/ChunkAccessor.cs` | Primary consumer of epoch-protected pages |
| `src/Typhon.Engine/Misc/NoCopyAttribute.cs` | `[NoCopy]` attribute for copy prevention |
| `test/Typhon.Engine.Tests/Concurrency/EpochManagerTests.cs` | Unit tests |
| `test/Typhon.Benchmark/EpochBenchmarks.cs` | Performance benchmarks |

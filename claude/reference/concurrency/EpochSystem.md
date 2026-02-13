# Epoch-Based Resource Protection

> **TL;DR:** Threads enter an epoch scope once per transaction. All page accesses within that scope are protected from eviction by a single epoch tag on each page — no ref-counting, no per-page obligations. Eviction checks `page.AccessEpoch < MinActiveEpoch` instead of `refCount == 0`.

**Status:** Implemented
**ADR:** [ADR-033: Epoch-Based Page Eviction](../../adr/033-epoch-based-page-eviction.md)

## Why Epochs

The old ref-counting model imposed **2N obligations** per N page accesses: every `RequestPage()` required a matching `ReleasePage()`. Missing a release leaked a ref-count, blocking eviction. Missing a page entirely and the eviction predicate would reclaim it from under a live reader.

The epoch model reduces this to **2 obligations per transaction**: `EnterScope()` and `ExitScope()`. Everything in between is protected.

## How the Algorithm Works

### Core Invariant

A page is safe from eviction if **any active thread might still be using it**. Rather than tracking which thread uses which page (ref-counting), epochs answer a simpler question: *"Is the page's timestamp recent enough that some active scope could still reference it?"*

### Step-by-Step Flow

**1. Entering a scope — pinning an epoch**

When a thread enters an epoch scope (via `EpochGuard.Enter` or `Transaction` creation), the thread's slot in `_pinnedEpochs[slot]` is set to the current `GlobalEpoch` value. This "pins" the epoch — the thread is announcing: *"I may access pages tagged with this epoch or later."*

```
GlobalEpoch = 5

Thread A enters scope → _pinnedEpochs[slotA] = 5     (pinned at 5)
```

Nested scopes do not re-pin; only the outermost scope sets the pinned value. Inner scopes increment a depth counter.

**2. Accessing pages — stamping the epoch tag**

Every page access via `RequestPageEpoch` stamps the page with the caller's epoch using a **CAS-max loop**: the page's `AccessEpoch` is updated to `max(existing, currentEpoch)`. This ensures the tag only moves forward — if two threads at epochs 5 and 7 both access the same page, it ends up tagged at 7.

```
Thread A (epoch 5) accesses page P → P.AccessEpoch = max(0, 5) = 5
Thread B (epoch 7) accesses page P → P.AccessEpoch = max(5, 7) = 7
```

**3. Eviction decision — comparing against MinActiveEpoch**

When the page cache needs to evict a page, it computes `MinActiveEpoch` — the minimum value across all non-zero entries in `_pinnedEpochs[256]`. A page is evictable only if:

```
page.AccessEpoch < MinActiveEpoch  AND  page.DirtyCounter == 0
```

If any active thread is pinned at or before the page's epoch, the page is protected.

**4. Exiting a scope — unpinning and advancing**

When the outermost scope exits, the thread's `_pinnedEpochs[slot]` is reset to 0 (unpinned), and `GlobalEpoch` is atomically incremented via `Interlocked.Increment`. This ensures every scope exit produces a new, unique epoch value for the next scope to pin.

```
Thread A exits scope → _pinnedEpochs[slotA] = 0, GlobalEpoch becomes 6
```

### Concrete Timeline Example

```
Time    GlobalEpoch   Thread A             Thread B            MinActiveEpoch
─────   ───────────   ──────────────────   ─────────────────   ──────────────
t0          5         (idle)               (idle)              5 (no pins → GE)

t1          5         EnterScope()         (idle)              5
                      pins at 5

t2          5         reads page P₁        EnterScope()        5
                      P₁.AccessEpoch=5     pins at 5

t3          5         reads page P₂        reads page P₃       5
                      P₂.AccessEpoch=5     P₃.AccessEpoch=5

t4          5         ExitScope()          reads page P₄       6 → 5
                      unpins, GE→6         P₄.AccessEpoch=5        (B still at 5)

t5          6         (idle)               ExitScope()         6 → 6
                                           unpins, GE→7            (no pins → GE)

─── Eviction check at t5: ────────────────────────────────────────
   P₁.AccessEpoch(5) < MinActiveEpoch(6) → EVICTABLE ✓
   P₂.AccessEpoch(5) < MinActiveEpoch(6) → EVICTABLE ✓
   P₃.AccessEpoch(5) < MinActiveEpoch(6) → EVICTABLE ✓
   P₄.AccessEpoch(5) < MinActiveEpoch(6) → EVICTABLE ✓
```

All pages become evictable once both scopes have exited and `MinActiveEpoch` advances past epoch 5.

### The Over-Retention Problem

Between t4 and t5 in the example above, Thread A has exited but Thread B is still pinned at epoch 5. During this window:

- **P₁ and P₂** (accessed only by Thread A) are **not evictable**, even though Thread A is done with them. Thread B's pin at epoch 5 protects *all* pages tagged at epoch ≥ 5 — regardless of which thread accessed them.
- **P₃ and P₄** (accessed by Thread B) are also protected, which is correct.

This is the fundamental trade-off: **epochs protect by time interval, not by identity**. A page is protected if its epoch overlaps with *any* active scope, not just the scope that accessed it.

### Advantages

| Advantage | Explanation |
|-----------|-------------|
| **Constant obligations** | 2 per transaction (`EnterScope` + `ExitScope`) regardless of how many pages are accessed. Ref-counting required 2N for N page accesses. |
| **No leak risk** | Forgetting to release a page is impossible — there's nothing to release. The scope exit handles everything. |
| **Wait-free fast path** | `EnterScope` is a ThreadStatic read + array write (~2-3ns). No CAS, no contention. `ExitScope` adds one `Interlocked.Increment` for advancing `GlobalEpoch`. |
| **No per-page contention** | Ref-counting required atomic increment/decrement on every page access. Epoch stamping uses a CAS-max loop, but only on cache miss — the ChunkAccessor's 16-slot cache absorbs repeated accesses. |
| **Simpler page state machine** | Eliminated 2 states (`InUse`, `PendingEviction`) — the old ref-count transitions are gone. 4 states instead of 6. |

### Costs and Trade-offs

| Cost | Explanation |
|------|-------------|
| **Over-retention of pages** | Pages stay in cache until `MinActiveEpoch` advances past their tag. A single long-running transaction pins an epoch, preventing eviction of **all** pages tagged at or after that epoch — even pages the long-running transaction never touched. This is analogous to how long-running MVCC transactions delay revision cleanup. |
| **256-slot scan on eviction** | `ComputeMinActiveEpoch` scans all 256 `_pinnedEpochs` slots (~40ns). This is acceptable because eviction is already a slow path, but the cost grows with more threads. |
| **Thread registry limit** | Fixed at 256 slots. Exceeding this (unlikely in practice per ADR-004's single-engine-per-process model) throws an exception. |
| **Epoch overflow** | `GlobalEpoch` is a `long` (9.2×10¹⁸). At 1 billion scope exits per second, overflow would take ~292 years. Not a practical concern. |
| **Delayed eviction under load** | Under heavy concurrent load with many overlapping scopes, `MinActiveEpoch` may lag significantly behind `GlobalEpoch`, keeping more pages in cache than ref-counting would. This is the price of simplicity — the system trades cache pressure for lock-free operation. |

### Mitigations for Over-Retention

1. **Short scopes**: Transactions should be as short as possible. The epoch model makes this more important than with ref-counting, because a long scope affects *all* pages, not just the ones it holds.
2. **Epoch advancement**: Every outermost scope exit increments `GlobalEpoch`, so even a steady stream of short transactions continuously advances the eviction frontier.
3. **Clock-sweep tolerance**: The page cache uses a clock-sweep algorithm with MRU counters. Even when pages can't be evicted (epoch-protected), the sweep still decrements counters, so recently-unused pages are evicted first once their epoch protection expires.

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

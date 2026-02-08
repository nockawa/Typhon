# ChunkAccessor Redesign — From Ref-Counted Cache to Pure Performance Cache

**Parent:** [README.md](./README.md)
**Prerequisite:** [02 — Typhon Epoch System](./02-typhon-epoch-system.md), [03 — Page Cache Evolution](./03-page-cache-evolution.md)

---

## The Transformation

The ChunkAccessor transforms from a **ref-counted resource manager** (where you must carefully pin/unpin/promote/demote) into a **pure performance cache** (where you just ask for data and get a pointer).

This is the largest and most impactful change in the entire epoch migration.

---

## What Gets Eliminated

```
REMOVED from ChunkAccessor:
┌──────────────────────────────────────────────────────────────────┐
│  ✗ PinCounter per slot (16 × short)                              │
│  ✗ PromoteCounter per slot (16 × short)                          │
│  ✗ ChunkHandle ref struct (entire type deleted)                  │
│  ✗ ChunkHandleUnsafe ref struct (entire type deleted)            │
│  ✗ GetChunkHandle() method                                       │
│  ✗ GetChunkHandleUnsafe() method                                 │
│  ✗ UnpinSlot() method                                            │
│  ✗ TryPromoteChunk() method                                      │
│  ✗ DemoteChunk() method                                          │
│  ✗ "All slots pinned" crash path                                 │
│  ✗ Pin/promote balance validation (StateSnapshot)                │
│  ✗ HitCount per slot (16 × int) — replaced by clock-hand          │
│  ✗ Eviction block on pinned/promoted slots                       │
│  ✗ Forced demotion in Dispose()                                  │
└──────────────────────────────────────────────────────────────────┘
```

---

## The Core Insight: Why This Is Safe

With epoch-based protection, the **underlying page memory is guaranteed stable** for the entire scope duration. The ChunkAccessor's 16-slot cache is just a local performance optimization — a way to avoid repeated hash lookups in the page cache.

```
BEFORE (ref-counted):
┌─────────────────────┐    ┌──────────────────────┐
│ ChunkAccessor Slot  │───→│ Page Cache Slot      │
│ PinCounter: 2       │    │ ConcurrentShared: 3  │
│                     │    │                      │
│ If I evict this     │    │ If counter hits 0,   │
│ slot, the pointer   │    │ page can be evicted  │
│ becomes INVALID     │    │ and reused → DANGER  │
│ because it might    │    │                      │
│ decrement the page  │    │                      │
│ counter to 0        │    │                      │
└─────────────────────┘    └──────────────────────┘

AFTER (epoch-based):
┌─────────────────────┐    ┌───────────────────────┐
│ ChunkAccessor Slot  │───→│ Page Cache Slot       │
│ (no pin counter)    │    │ AccessEpoch: 42       │
│                     │    │                       │
│ If I evict this     │    │ Page CANNOT be evicted│
│ slot, the pointer   │    │ because MinActiveEpoch│
│ is STILL VALID      │    │ ≤ 42 (we're in scope) │
│ because the page    │    │                       │
│ memory hasn't moved │    │ Memory address is     │
│                     │    │ STABLE (pinned array) │
└─────────────────────┘    └───────────────────────┘
```

**The page cache's memory is a pinned array.** Page at memory slot 42 is always at address `_cacheArray + 42 * 8192`. As long as the page cache doesn't reuse that slot for a different file page (prevented by epoch), the pointer is valid.

When the ChunkAccessor evicts a slot:
- **Before**: Disposes PageAccessor → decrements counter → page might become evictable → memory reused → pointer invalid! Must pin to prevent this.
- **After**: Discards local slot entry. Page cache still has the page (epoch-protected). Memory stable. Pointer valid. No danger.

**This eliminates the entire reason pinning exists.**

---

## New Memory Layout

### Before (~1024 bytes)

```
ChunkAccessor (before):
  _pageIndices[16]       64 bytes   SIMD-searchable page indices
  _slots[16]:                       Per-slot metadata
    BaseAddress          8 bytes    Pointer to page data
    HitCount             4 bytes    LRU counter
    PinCounter           2 bytes    ← REMOVED
    DirtyFlag            1 byte     Dirty tracking
    PromoteCounter       2 bytes    ← REMOVED
    (padding)            ~1 byte
  (total _slots)         ~256 bytes
  _pageAccessors[16]     ~512 bytes  PageAccessor instances
  _segment               8 bytes    Segment reference
  _changeSet             8 bytes    ChangeSet reference
  _mruSlot               4 bytes    MRU optimization
  _usedSlots             4 bytes    High-water mark
  _stride                4 bytes    Chunk stride
  _rootHeaderOffset      4 bytes    Root page offset
  ──────────────────────────────
  Total: ~864 bytes
```

### After (intermediate — still with PageAccessors)

```
ChunkAccessor (after):
  _pageIndices[16]       64 bytes   SIMD-searchable page indices (UNCHANGED)
  _slots[16]:                       Per-slot metadata (SIMPLIFIED)
    BaseAddress          8 bytes    Pointer to page data
    DirtyFlag            1 byte     Dirty tracking
    (padding)            7 bytes
  (total _slots)         ~256 bytes
  _pageAccessors[16]     ~256 bytes  Simplified PageAccessors (lighter)
  _segment               8 bytes    Segment reference
  _changeSet             8 bytes    ChangeSet reference
  _clockHand             1 byte     Round-robin eviction cursor
  _mruSlot               1 byte     MRU optimization
  _usedSlots             1 byte     High-water mark
  _stride                2 bytes    Chunk stride
  _rootHeaderOffset      2 bytes    Root page offset
  ──────────────────────────────
  Total: ~599 bytes (with lighter PageAccessors)
```

**Memory savings**: ~260 bytes per ChunkAccessor instance. But the real win is **complexity reduction**, not memory savings.

Note: the PageAccessor array might be even lighter since shared PageAccessors don't need to track state for disposal. We may be able to eliminate the PageAccessor entirely for shared access and just store raw page addresses.

### Extreme Simplification: No PageAccessor Storage

Since shared PageAccessors are now no-ops (no dispose needed), the ChunkAccessor could skip storing them entirely:

```
ChunkAccessor (minimal):
  _pageIndices[16]       64 bytes   SIMD-searchable page indices
  _baseAddresses[16]     128 bytes  Direct pointers to page data (x64)
  _dirtyFlags             2 bytes   Dirty tracking (ushort bitfield)
  _segment               8 bytes    Segment reference
  _changeSet             8 bytes    ChangeSet reference
  _clockHand              1 byte    Round-robin eviction cursor
  _usedSlots             1 byte     High-water mark
  _stride                2 bytes    Chunk stride (short, max 8000)
  _rootHeaderOffset      2 bytes    Root page offset (short)
  ──────────────────────────────
  Total: ~216 bytes
```

**Under 4 cache lines!** The entire accessor is always L1-hot.

The tradeoff: without stored PageAccessors, evicting a slot can't track which PageInfo it came from (needed for dirty page flushing via ChangeSet). Solution: store the `memPageIndex` per slot (4 bytes each = 64 bytes) for ChangeSet integration.

**Why `int` (4 bytes) and not `ushort` (2 bytes) for memPageIndex:** A `ushort` limits the page cache to 65,536 pages × 8KB = 512MB. While the default 256-page (2MB) cache is good for dev/test (stresses the cache), production workloads may need 2GB+ caches (262,144+ pages). Using `int` supports up to ~16TB of page cache with no artificial ceiling. The cost is 32 extra bytes (64 vs 32), but the struct stays within the same 6 cache lines. And `_memPageIndices` is only accessed on the cold dirty-flush path (eviction/dispose), never on the hot SIMD search — so the wider type has zero impact on `GetChunkAddress` performance.

**Why `ushort` bitfield (2 bytes) instead of `byte[16]` (16 bytes) for dirtyFlags:** With 16 slots, a single `ushort` stores one dirty bit per slot. This saves 14 bytes, but the real wins are algorithmic:

- **Read-only Dispose fast path**: `if (_dirtyFlags == 0) return;` — a single comparison replaces scanning up to 16 bytes. Read-only operations (the common case) skip the flush loop entirely with zero overhead.
- **Flush iterates only dirty slots**: Using `BitOperations.TrailingZeroCount` to walk set bits avoids scanning clean slots. For the typical write pattern (1-2 dirty pages), this is 1-2 iterations with zero wasted scanning.
- **Hot path cost is unchanged**: Setting dirty (`_dirtyFlags |= (ushort)(1 << slot)`) is a single-cycle read-OR-write on a 2-byte register. No atomicity concern since ChunkAccessor is single-threaded.

**Why clock-hand eviction (1 byte) instead of per-slot HitCount (64 bytes):** The existing LRU approach scans all 16 slots to find the minimum hit count — 48 micro-ops on the cold path, and a hit count increment on *every* hot path access (3 extra micro-ops + 1 extra cache line touched per GetChunkAddress call). With epoch protection, eviction is always safe — a bad eviction choice causes a cache miss (re-fetch from page cache), not a use-after-free. This changes the cost/benefit calculus entirely:

- **Hot path**: Eliminating `_hitCounts[slot]++` removes 3 micro-ops and 1 cache line from every MRU and SIMD hit. The hot path drops from 3 cache lines touched to 2 (for read-only access).
- **Eviction**: A `_clockHand` byte with MRU-skip replaces the 16-slot scan. `_clockHand = (_clockHand + 1) & 0xF; if (_clockHand == _mruSlot) _clockHand = (_clockHand + 1) & 0xF;` — effectively O(1) with 2 increments worst case.
- **Quality**: The MRU check already captures the dominant temporal locality (same B+Tree node accessed repeatedly). Among the remaining 15 slots, the difference between "2nd most used" and "15th most used" is negligible in a 16-slot cache. Round-robin with MRU bypass is near-optimal for this size.
- **Struct size**: Eliminates 64 bytes (int hitCounts) or 32 bytes (ushort hitCounts), replaced by 1 byte clock-hand.

```
ChunkAccessor (final):
  _pageIndices[16]       64 bytes   SIMD-searchable file page indices
  _baseAddresses[16]     128 bytes  Direct pointers to page data
  _memPageIndices[16]    64 bytes   Memory page indices (int, for ChangeSet)
  _dirtyFlags             2 bytes   Dirty tracking (ushort, 1 bit per slot)
  _segment               8 bytes
  _changeSet             8 bytes
  _clockHand              1 byte    Round-robin eviction cursor
  _mruSlot               1 byte
  _usedSlots             1 byte
  _stride                2 bytes
  _rootHeaderOffset      2 bytes
  ──────────────────────────────
  Total: ~281 bytes ≈ 5 cache lines
```

This is a **~3.6x reduction** from the current ~1KB. The SIMD search reads exactly 1 cache line (_pageIndices), and the MRU hot path touches only 2 cache lines (pageIndices + baseAddresses) for read-only access.

---

## Hot Path: GetChunkAddress (The Critical Method)

This method is called millions of times per second. It's the single most important method in the storage layer.

### Before

```
GetChunkAddress(chunkId, dirty):
  (pageIndex, offset) = segment.GetChunkLocation(chunkId)

  // ULTRA FAST PATH: MRU check
  if _pageIndices[_mruSlot] == pageIndex:
    slot = _slots[_mruSlot]
    if dirty: slot.DirtyFlag = 1
    slot.HitCount++
    slot.PinCounter++          ← REMOVED (was needed for safety)
    return slot.BaseAddress + offset * _stride

  // FAST PATH: SIMD search
  ... Vector256 search ...
  if found:
    slot.HitCount++
    slot.PinCounter++          ← REMOVED
    return address

  // SLOW PATH: LRU eviction + load
  return LoadAndGet(pageIndex, offset, dirty)
```

### After

```
GetChunkAddress(chunkId, dirty):
  (pageIndex, offset) = segment.GetChunkLocation(chunkId)

  // ULTRA FAST PATH: MRU check (2 cache lines: pageIndices + baseAddresses)
  if _pageIndices[_mruSlot] == pageIndex:
    if dirty: _dirtyFlags |= (ushort)(1 << _mruSlot)
    return _baseAddresses[_mruSlot] + offset * _stride

  // FAST PATH: SIMD search
  ... Vector256 search (UNCHANGED) ...
  if found:
    _mruSlot = slot
    if dirty: _dirtyFlags |= (ushort)(1 << slot)
    return _baseAddresses[slot] + offset * _stride

  // SLOW PATH: clock-hand eviction + load
  return LoadAndGet(pageIndex, offset, dirty)
```

**Difference**: Removed `PinCounter++` and `HitCount++` from MRU and SIMD paths. That's 4 fewer micro-ops per access on the hot path (no load/increment/store of hit counter, no pin counter). The MRU hit path now touches only 2 cache lines (pageIndices + baseAddresses) for read-only access — down from 3-4 previously. `UnpinSlot()` call is also eliminated from callers.

---

## Slot Eviction: Always Possible

### Before

```
FindLRUSlot():
  for each slot:
    if slot.PinCounter == 0          ← must check
       && slot.PromoteCounter == 0   ← must check
       && slot.HitCount < minHit:
      candidate = slot

  if no candidate found:
    CRASH ("All 16 cache slots are pinned or promoted. Cannot evict.")
```

### After

```
FindEvictionSlot():
  // Fast path: use next empty slot (first 16 loads)
  if _usedSlots < 16:
    return _usedSlots++

  // Steady state: clock-hand with MRU skip
  _clockHand = (_clockHand + 1) & 0xF
  if _clockHand == _mruSlot:
    _clockHand = (_clockHand + 1) & 0xF
  return _clockHand               // ~3-5 micro-ops, always succeeds
```

**The "all slots pinned" crash is eliminated.** Every slot is always evictable because:
1. Evicting a local slot doesn't affect the page cache (epoch protects the page)
2. The returned pointer remains valid (pinned memory array)
3. If the same page is needed again, it'll be re-loaded from the page cache (guaranteed hit during same scope)

**The LRU scan is eliminated.** The previous FindLRUSlot scanned all 16 slots (~48 micro-ops) to find the minimum hit count. The clock-hand with MRU-skip is O(1) — at most 2 increments. The MRU check on the hot path already protects the most important slot (the one being repeatedly accessed in tight loops). Among the remaining 15 slots, round-robin is near-optimal — the eviction cost (a page cache re-fetch) is cheap under epoch protection.

---

## Slot Loading: Simplified

### Before

```
LoadIntoSlot(slot, pageIndex):
  1. segment.GetPageSharedAccessor(pageIndex, out _pageAccessors[slot])
     → This increments ConcurrentSharedCounter in page cache
  2. _pageIndices[slot] = pageIndex
  3. _slots[slot].BaseAddress = _pageAccessors[slot].GetRawDataAddr()
  4. _slots[slot].HitCount = 1
  5. _slots[slot].PinCounter = 0
  6. _slots[slot].PromoteCounter = 0
```

### After

```
LoadIntoSlot(slot, pageIndex):
  1. address = segment.GetPageAddress(pageIndex)
     → This updates page.AccessEpoch = max(current, GlobalEpoch)
     → Returns raw pointer to page data
     → NO ref count, NO PageAccessor creation
  2. _pageIndices[slot] = pageIndex
  3. _baseAddresses[slot] = address
  4. _memPageIndices[slot] = memPageIndex  (for ChangeSet tracking)
```

**Eliminated**: PageAccessor creation, ref-count increment, PinCounter/PromoteCounter initialization, HitCount initialization.

---

## Slot Eviction: Simplified

### Before

```
EvictSlot(slot):
  1. Check dirty flag → flush to ChangeSet if dirty
  2. Check PromoteCounter → demote if promoted (COMPLEX)
  3. _pageAccessors[slot].Dispose()
     → Decrements ConcurrentSharedCounter
     → May transition page to Idle
     → May trigger state lock
  4. Clear slot metadata
```

### After

```
EvictSlot(slot):
  1. Check dirty bit → flush to ChangeSet if dirty:
       if (_dirtyFlags & (1 << slot)) != 0:
         _changeSet.AddByMemPageIndex(_memPageIndices[slot])
         _dirtyFlags &= (ushort)~(1 << slot)
  2. Clear slot metadata (zero out index, address)
  → NO PageAccessor disposal
  → NO counter decrement
  → NO state transition
  → NO promotion handling
```

---

## Dispose: Simplified

### Before

```
Dispose():
  for each used slot:
    if PromoteCounter > 0:
      _pageAccessors[i].DemoteExclusive()    ← must demote
    if DirtyFlag && ChangeSet:
      _changeSet.Add(_pageAccessors[i])      ← flush dirty
    _pageAccessors[i].Dispose()              ← must release ref count
  _usedSlots = 0
  _segment = null
```

### After

```
Dispose():
  if _dirtyFlags != 0 && _changeSet != null:    ← single check for any dirty
    var bits = _dirtyFlags
    while bits != 0:
      slot = BitOperations.TrailingZeroCount(bits)
      _changeSet.AddByMemPageIndex(_memPageIndices[slot])
      bits &= (ushort)(bits - 1)                 ← clear lowest set bit
    _dirtyFlags = 0
  _usedSlots = 0
  _segment = null
  → NO PageAccessor disposal
  → NO demotion handling
  → NO ref count release
```

**Key change**: Dispose is now about dirty tracking only. For **read-only operations** (the common case), `Dispose()` is a single `_dirtyFlags != 0` check — effectively a no-op. For write operations, the `TrailingZeroCount` loop iterates only over actually-dirty slots, skipping clean ones entirely.

---

## CommitChanges: Simplified

### Before

```
CommitChanges():
  for each used slot:
    if DirtyFlag && ChangeSet:
      _changeSet.Add(_pageAccessors[i])
    DirtyFlag = 0
```

### After

```
CommitChanges():
  if _dirtyFlags != 0 && _changeSet != null:
    var bits = _dirtyFlags
    while bits != 0:
      slot = BitOperations.TrailingZeroCount(bits)
      _changeSet.AddByMemPageIndex(_memPageIndices[slot])
      bits &= (ushort)(bits - 1)
    _dirtyFlags = 0
```

Same bitfield iteration pattern as Dispose. Uses `memPageIndex` instead of `PageAccessor`.

---

## API Surface Comparison

### Before: 8 Public/Internal Methods for Chunk Access

```
GetChunkAddress(chunkId, dirty)          → byte*        (unsafe, fast)
GetChunk<T>(chunkId, dirty)              → ref T        (unsafe, fast)
GetChunkReadOnly<T>(chunkId)             → ref readonly T (unsafe, fast)
GetChunkHandle(chunkId, dirty)           → ChunkHandle  (safe, pinned)
GetChunkHandleUnsafe(chunkId, dirty)     → ChunkHandleUnsafe (for arrays)
TryPromoteChunk(chunkId)                 → bool         (exclusive)
DemoteChunk(chunkId)                     → void         (release exclusive)
UnpinSlot(slot)                          → void         (release pin)
```

### After: 3 Public/Internal Methods for Chunk Access

```
GetChunk<T>(chunkId, dirty)              → ref T        (safe! epoch-protected)
GetChunkReadOnly<T>(chunkId)             → ref readonly T (safe! epoch-protected)
GetChunkAddress(chunkId, dirty)          → byte*        (safe! epoch-protected)
```

**5 methods eliminated.** The remaining 3 methods are all **safe by construction** — the returned references/pointers are valid for the entire epoch scope without any caller obligation.

```
CALLER OBLIGATION COMPARISON:

Before:
  ref T data = ref accessor.GetChunk<T>(id, dirty);
  // ⚠ UNSAFE: next GetChunk call may evict this page
  // Must use GetChunkHandle for safety, which requires:
  //   using var handle = accessor.GetChunkHandle(id, dirty);
  //   ref T data = ref handle.AsRef<T>();
  //   // handle.Dispose() unpins the slot
  // Even then: must not hold >16 handles simultaneously

After:
  ref T data = ref accessor.GetChunk<T>(id, dirty);
  // ✓ SAFE: epoch protects underlying page memory
  // No handle needed. No dispose needed. No pin limit.
  // Valid until enclosing EpochGuard exits.
```

---

## Struct Copy Safety

### Before: Copying ChunkAccessor Is Dangerous

```
// DANGEROUS: hidden struct copy
void ProcessComponent(ChunkAccessor accessor) {  // ← COPY of accessor
  ref var data = ref accessor.GetChunk<T>(id);
  // ... use data ...
}  // accessor copy goes out of scope — but it's a COPY
   // Original accessor's PageAccessors not disposed → ref count leaked

// Even worse:
ChunkAccessor copy = accessor;  // explicit copy
copy.Dispose();                  // Disposes COPY's PageAccessors
// Original's PageAccessors point to disposed state → undefined behavior
```

### After: Copying Is Harmless

```
// SAFE: struct copy is just a memcpy of addresses
void ProcessComponent(ChunkAccessor accessor) {  // ← COPY, but harmless
  ref var data = ref accessor.GetChunk<T>(id);
  // data is valid — epoch protects the page
  // Copy has same addresses pointing to same valid pages
}  // copy goes out of scope — nothing to clean up

// Still works:
ChunkAccessor copy = accessor;  // just copies indices + addresses
copy.Dispose();                  // flushes dirty pages (idempotent)
// Original is unaffected — no shared mutable state
```

**Note**: We should still prefer passing by `ref` for performance (avoid copying ~281 bytes). But accidental copies no longer cause correctness bugs — they're just a performance issue.

---

## Dirty Page Tracking with ChangeSet

Without stored PageAccessors, the ChunkAccessor needs another way to report dirty pages to the ChangeSet.

### Solution: Store memPageIndex per Slot

Each slot stores the `memPageIndex` (the index into the page cache's memory array). When flushing dirty pages:

```
FlushDirtySlots():
  var bits = _dirtyFlags
  while bits != 0:
    slot = BitOperations.TrailingZeroCount(bits)
    _changeSet.AddByMemPageIndex(_memPageIndices[slot])
    bits &= (ushort)(bits - 1)
  _dirtyFlags = 0
```

The ChangeSet's `AddByMemPageIndex` method creates a lightweight reference to the page for write-back. This is simpler than passing a full PageAccessor.

**ChangeSet already knows the PagedMMF** (it's created via `mmf.CreateChangeSet()`), so it can look up the page info from the memPageIndex.

---

## Compatibility with Existing StackChunkAccessor Design

The existing `claude/reference/StackChunkAccessor.md` design document describes a scope-based accessor. The epoch-based approach **supersedes** that design:

| StackChunkAccessor Feature | Epoch-Based Equivalent |
|---------------------------|----------------------|
| `EnterScope()` / `ExitScope()` on accessor | `EpochGuard.Enter()` / `Dispose()` globally |
| `_scopeLevels[16]` per slot | Not needed — epoch protects all pages |
| Slot eviction respects scope level | Slot eviction is always allowed |
| 15 max nesting levels | Unlimited (epoch nesting is just a counter) |

The epoch approach is **strictly more general** because protection operates at the page cache level, not the accessor level. Multiple accessors sharing the same epoch scope automatically share protection.

---

## Summary

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Struct size | ~1024 bytes | ~281 bytes (5 cache lines) | ~3.6x smaller |
| Cache lines touched (MRU hit) | 3-4 | 2 (read-only) | 2x better locality |
| Hot path micro-ops | ~7 (MRU hit) | ~4 (MRU hit) | 3 fewer per access |
| Eviction | LRU scan, ~48 µ-ops | Clock-hand, ~4 µ-ops | O(16) → O(1) |
| API methods | 8 | 3 | 63% surface reduction |
| Caller obligations | Pin/unpin/promote/demote/dispose | None (for shared) | Eliminated |
| Crash modes | "All slots pinned" | None | Eliminated |
| Struct copy safety | Dangerous | Harmless | Eliminated class of bugs |
| Exclusive access handling | Per-slot PromoteCounter | None (page-level) | Decoupled |

---

**Next:** [05 — Exclusive Access Model](./05-exclusive-access.md) — How exclusive access works without ChunkAccessor involvement.

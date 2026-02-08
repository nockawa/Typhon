# Performance Analysis — CPU Cycles and Memory Access

**Parent:** [README.md](./README.md)

---

## Methodology

All cycle estimates assume:
- Modern x64 CPU (Zen 4 / Alder Lake class)
- L1 cache hit: ~1ns (~4 cycles at 4GHz)
- L2 cache hit: ~4ns (~16 cycles)
- Interlocked CAS (uncontended): ~5-8ns (~20-32 cycles)
- AccessControlSmall enter/exit: ~10-20ns (~40-80 cycles)
- Memory barrier (fence): ~5ns (~20 cycles)

"CL" = cache line (64 bytes). "Ind" = pointer indirection.

---

## Analysis 1: ChunkAccessor.GetChunk — MRU Hit (Ultra-Hot Path)

This is the single most called code path. Called millions of times per second during B+Tree traversals and CRUD operations.

### Before

```
GetChunkAddress(chunkId, dirty):
  1. segment.GetChunkLocation(chunkId)         ~3 cycles  (inline arithmetic)
  2. Load _mruSlot                             ~1 cycle   (register/L1)
  3. Load _pageIndices[_mruSlot]               ~4 cycles  (L1, same cache line as header)
  4. Compare with pageIndex                    ~1 cycle
  5. Branch (predicted taken for MRU)          ~0 cycles  (predicted)
  6. Load slot.BaseAddress                     ~4 cycles  (L1, SlotData array)
  7. If dirty: store slot.DirtyFlag = 1        ~1 cycle
  8. Increment slot.HitCount                   ~1 cycle
  9. Increment slot.PinCounter                 ~1 cycle   ← EPOCH REMOVES THIS
  10. Compute address + return                 ~2 cycles
  ────────────────────────────────────────────
  Total: ~18 cycles (~4.5ns)
  Cache lines touched: 2 (pageIndices CL + slot CL)
  Memory indirections: 0 (all inline in ref struct)
```

### After

```
GetChunkAddress(chunkId, dirty):
  1. segment.GetChunkLocation(chunkId)         ~3 cycles  (inline arithmetic)
  2. Load _mruSlot                             ~1 cycle
  3. Load _pageIndices[_mruSlot]               ~4 cycles  (L1, CL 0)
  4. Compare with pageIndex                    ~1 cycle
  5. Branch (predicted taken for MRU)          ~0 cycles
  6. Load _baseAddresses[_mruSlot]             ~4 cycles  (L1, CL 1-2)
  7. If dirty: store _dirtyFlags[_mruSlot]     ~1 cycle   (L1, CL 4)
  8. Increment _hitCounts[_mruSlot]            ~1 cycle   (L1, CL 3)
  9. Compute address + return                  ~2 cycles
  ────────────────────────────────────────────
  Total: ~17 cycles (~4.25ns)
  Cache lines touched: 2-3 (pageIndices CL + addresses CL + counters CL)
  Memory indirections: 0
```

### Comparison

| Metric | Before | After | Delta |
|--------|--------|-------|-------|
| Cycles (MRU hit) | ~18 | ~17 | -1 cycle (-6%) |
| Cache lines touched | 2 | 2-3 | +0-1 (SOA layout) |
| Memory writes | 3 (dirty + hit + pin) | 2 (dirty + hit) | -1 write |
| PinCounter overhead | 1 cycle write + UnpinSlot later | 0 | Eliminated |

**Net effect**: Marginally faster (~1 cycle) on MRU hit. The real win is eliminating the **UnpinSlot call** that callers had to make separately (~5-10 cycles saved on the caller side).

**Total per-access savings including caller**: ~6-11 cycles per GetChunk call.

---

## Analysis 2: ChunkAccessor.GetChunk — SIMD Search (Fast Path)

When the MRU check fails but the page is in one of the 16 slots.

### Before and After: IDENTICAL

```
SIMD Search:
  1. Load _pageIndices[0..7] into Vector256     ~4 cycles  (one CL load)
  2. Vector256.Equals + ExtractMSB              ~2 cycles  (1 SIMD op + 1 extract)
  3. If hit: TrailingZeroCount                  ~1 cycle
  4. Load slot data                             ~4 cycles  (L1)
  5. Update counters + return                   ~3 cycles
  ────────────────────────────────────────────
  Total (hit in first 8): ~14 cycles
  Total (hit in slots 8-15): ~20 cycles (second Vector256 load)
```

**No change.** The SIMD search is identical between before and after. The only difference is one fewer counter write (no PinCounter), which is negligible in the SIMD path.

---

## Analysis 3: ChunkAccessor Slot Eviction (Slow Path)

When none of the 16 slots has the needed page — must evict one and load new.

### Before

```
FindLRUSlot + EvictSlot + LoadIntoSlot:

  FindLRUSlot:
  1. Scan 16 slots for min HitCount             ~30 cycles
     (check PinCounter==0, PromoteCounter==0)
  2. If all pinned → CRASH                      N/A

  EvictSlot:
  3. Check DirtyFlag → ChangeSet.Add            ~5 cycles (if dirty)
  4. Check PromoteCounter → DemoteExclusive     ~40 cycles (if promoted, rare)
  5. PageAccessor.Dispose():
     a. Lock StateSyncRoot                      ~20 cycles
     b. Decrement ConcurrentSharedCounter       ~1 cycle
     c. Check if last reader → transition state ~3 cycles
     d. Unlock StateSyncRoot                    ~10 cycles
  6. Clear slot                                 ~3 cycles

  LoadIntoSlot:
  7. segment.GetPageSharedAccessor:
     a. FetchPageToMemory (cache hit)           ~20 cycles (hash lookup + check)
     b. Lock StateSyncRoot                      ~20 cycles
     c. Increment ConcurrentSharedCounter       ~1 cycle
     d. Set state to Shared                     ~1 cycle
     e. Increment ClockSweepCounter             ~8 cycles (CAS)
     f. Unlock StateSyncRoot                    ~10 cycles
  8. Store BaseAddress + metadata               ~5 cycles

  ────────────────────────────────────────────
  Total: ~150-180 cycles (~40-45ns)
  Lock acquisitions: 2 (evict + load)
  State transitions: 2 (Active→Idle + Idle→Active)
```

### After

```
FindLRUSlot + EvictSlot + LoadIntoSlot:

  FindLRUSlot:
  1. Scan 16 slots for min HitCount             ~20 cycles
     (no pin/promote checks needed)
  2. Always succeeds                            ✓

  EvictSlot:
  3. Check DirtyFlag → ChangeSet.Add            ~5 cycles (if dirty)
  4. Clear slot                                 ~3 cycles
  → NO PageAccessor.Dispose
  → NO lock acquisition
  → NO state transition

  LoadIntoSlot:
  5. segment.GetPageAddress:
     a. FetchPageToMemory (cache hit)           ~20 cycles (hash lookup + check)
     b. Update AccessEpoch (Interlocked CAS)    ~8 cycles
     c. Increment ClockSweepCounter (CAS)       ~8 cycles
     → NO lock acquisition
     → NO counter increment
  6. Store BaseAddress + metadata               ~5 cycles

  ────────────────────────────────────────────
  Total: ~70-80 cycles (~18-20ns)
  Lock acquisitions: 0
  State transitions: 0
```

### Comparison

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Cycles | ~150-180 | ~70-80 | **2x faster** |
| Lock acquisitions | 2 | 0 | Eliminated |
| State transitions | 2 | 0 | Eliminated |
| CAS operations | 1 | 2 | +1 (epoch tag) |
| Crash risk | Yes (all pinned) | No | Eliminated |

**The slow path is 2x faster.** The dominant cost elimination is the two lock acquire/release cycles (~60 cycles total) on the page cache's StateSyncRoot.

---

## Analysis 4: Page Cache — Shared Access

Acquiring a page for shared (read) access from the PagedMMF.

### Before (PagedMMF.TransitionPageToAccess, shared path)

```
TransitionPageToAccess(pi, exclusive=false):
  1. Lock pi.StateSyncRoot.EnterExclusive       ~20 cycles (CAS + spin)
  2. Check state (Idle/Shared/etc)               ~2 cycles
  3. If Shared: check thread ID, increment       ~3 cycles
  4. Increment ConcurrentSharedCounter           ~1 cycle
  5. Set LockedByThreadId                        ~1 cycle
  6. Increment ClockSweepCounter                 ~8 cycles (CAS)
  7. Unlock pi.StateSyncRoot.ExitExclusive       ~10 cycles
  ────────────────────────────────────────────
  Total: ~45 cycles (~11ns)

TransitionPageFromAccessToIdle (on Dispose):
  1. Lock pi.StateSyncRoot.EnterExclusive       ~20 cycles
  2. Decrement ConcurrentSharedCounter          ~1 cycle
  3. If zero: set state, clear threadId         ~3 cycles
  4. Unlock pi.StateSyncRoot.ExitExclusive      ~10 cycles
  ────────────────────────────────────────────
  Total: ~34 cycles (~8.5ns)

Combined acquire + release: ~79 cycles (~20ns)
```

### After (Epoch-tagged access)

```
TagPageAccess(pi):
  1. Read GlobalEpoch                            ~1 cycle (L1, shared read)
  2. CAS pi.AccessEpoch = max(current, GE)       ~8 cycles (may need retry)
  3. Increment ClockSweepCounter                  ~8 cycles (CAS)
  ────────────────────────────────────────────
  Total: ~17 cycles (~4ns)

Release: NONE (no operation needed)

Combined: ~17 cycles (~4ns)
```

### Comparison

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Acquire cycles | ~45 | ~17 | **2.6x faster** |
| Release cycles | ~34 | 0 | **Eliminated** |
| Combined | ~79 | ~17 | **4.6x faster** |
| Lock contention | Possible (shared counter) | None (CAS only) | No blocking |

**4.6x faster for page access.** This is a significant win for the slow path (cache miss in ChunkAccessor → page cache access).

---

## Analysis 5: EpochGuard.Enter / Exit

Overhead of the epoch scope management, amortized across an entire operation.

### Enter (once per operation)

```
EpochGuard.Enter():
  1. Read thread-local NestingDepth               ~1 cycle
  2. If depth == 0:
     a. Read GlobalEpoch                          ~1 cycle
     b. Store to Registry[SlotIndex]              ~4 cycles (L2 likely)
  3. Increment NestingDepth                       ~1 cycle
  ────────────────────────────────────────────
  Total (outermost): ~7 cycles (~1.7ns)
  Total (nested): ~2 cycles (~0.5ns)
```

### Exit (once per operation)

```
EpochGuard.Exit():
  1. Decrement NestingDepth                       ~1 cycle
  2. If depth == 0:
     a. Store long.MaxValue to Registry[slot]     ~4 cycles
     b. Interlocked.Increment(GlobalEpoch)        ~8 cycles (CAS)
  ────────────────────────────────────────────
  Total (outermost): ~13 cycles (~3.3ns)
  Total (nested): ~1 cycle (~0.25ns)
```

### Amortized Cost

A typical CRUD operation involves:
- 1 outermost Enter/Exit: ~20 cycles
- 5-20 page accesses: savings of ~62 cycles each (79 → 17)
- Net savings: **5×62 - 20 = 290 cycles** for 5 accesses, **20×62 - 20 = 1220 cycles** for 20 accesses

**The epoch overhead is recouped after the first page access.** Every subsequent access is pure savings.

---

## Analysis 6: MinActiveEpoch Scan

Cost of computing MinActiveEpoch (triggered during cache eviction).

```
MinActiveEpoch scan (256 thread slots):
  1. Read 256 × 8 bytes = 2048 bytes = 32 cache lines
  2. Each cache line: ~4ns (L2/L3 hit for shared data)
  3. Total: ~128ns for plain scalar scan
  4. With SIMD (4 longs per Vector256): ~40ns
  ────────────────────────────────────────────
  Frequency: Once per N eviction attempts (N=8..32)
  Amortized per eviction: ~4-16ns
```

This is negligible — a single page fetch from disk takes ~10,000ns (10μs). The MinActiveEpoch scan is 0.01% of a disk read.

---

## Analysis 7: Exclusive Access

### Before (TryPromoteToExclusive)

```
TryPromoteToExclusive:
  1. Lock StateSyncRoot                          ~20 cycles
  2. Check FilePageIndex match                   ~2 cycles
  3. Check LockedByThreadId == current           ~2 cycles
  4. Increment ConcurrentSharedCounter           ~1 cycle
  5. Increment ClockSweepCounter (CAS)           ~8 cycles
  6. Set state to Exclusive                      ~1 cycle
  7. Unlock StateSyncRoot                        ~10 cycles
  Total: ~44 cycles (~11ns)

DemoteExclusive:
  1. Lock StateSyncRoot                          ~20 cycles
  2. Decrement ConcurrentSharedCounter           ~1 cycle
  3. Set state to previousMode                   ~1 cycle
  4. Unlock StateSyncRoot                        ~10 cycles
  Total: ~32 cycles (~8ns)

Combined: ~76 cycles (~19ns)
```

### After (CAS-based exclusive)

```
AcquireExclusive:
  1. CAS ExclusiveOwnerThreadId (0 → myId)       ~8 cycles (uncontended)
  2. Update AccessEpoch                           ~8 cycles (CAS)
  Total: ~16 cycles (~4ns)

ReleaseExclusive:
  1. Write ExclusiveOwnerThreadId = 0             ~1 cycle
  Total: ~1 cycle (~0.25ns)

Combined: ~17 cycles (~4.25ns)
```

### Comparison

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Acquire | ~44 cycles | ~16 cycles | **2.75x** |
| Release | ~32 cycles | ~1 cycle | **32x** |
| Combined | ~76 cycles | ~17 cycles | **4.5x** |

---

## Summary: End-to-End Operation Cost

### B+Tree Point Lookup (5 nodes deep, best case all MRU/SIMD)

```
Before:
  5 × GetChunk (MRU/SIMD):          5 × 18 = 90 cycles
  5 × UnpinSlot (no pin needed):     0 cycles (unsafe path, no pin)
  0 × Page cache access:             0 cycles (all in accessor cache)
  ──────────────────────────────────────────
  Total: ~90 cycles (~23ns)

After:
  1 × EpochGuard.Enter:              7 cycles
  5 × GetChunk (MRU/SIMD):          5 × 17 = 85 cycles
  1 × EpochGuard.Exit:              13 cycles
  ──────────────────────────────────────────
  Total: ~105 cycles (~26ns)

Delta: +15 cycles (+17%) for guaranteed safety
```

**Note**: The "before" path is using the **unsafe** API (no GetChunkHandle). If using the safe handle-based API, the before cost would be ~5 × 35 = 175 cycles, making the after path **40% faster**.

### B+Tree Insert with Split (3 nodes involved)

```
Before:
  3 × GetChunkHandle:               3 × 35 = 105 cycles
  1 × TryPromoteChunk:              44 cycles
  1 × DemoteChunk:                  32 cycles
  2 × Page cache transitions:       2 × 79 = 158 cycles
  3 × UnpinSlot:                    3 × 10 = 30 cycles
  ──────────────────────────────────────────
  Total: ~369 cycles (~92ns)

After:
  1 × EpochGuard.Enter:             7 cycles (nested: 1 cycle)
  3 × GetChunk:                     3 × 17 = 51 cycles
  1 × AcquireExclusive:             16 cycles
  1 × ReleaseExclusive:             1 cycle
  1 × EpochGuard.Exit:              13 cycles (nested: 1 cycle)
  ──────────────────────────────────────────
  Total: ~88 cycles (~22ns)

Delta: -281 cycles, 4.2x faster
```

---

## Cache Line Analysis

### Before: ChunkAccessor Slot Access

```
GetChunk<T> touches:
  CL 0:  _pageIndices (64B)           ← MRU/SIMD search
  CL 1-4: _slots (256B, AoS layout)   ← SlotData (BaseAddr + HitCount + PinCounter + ...)
  = 2-5 cache lines per access
```

### After: ChunkAccessor Slot Access (SOA)

```
GetChunk<T> touches:
  CL 0:  _pageIndices (64B)           ← MRU/SIMD search
  CL 1-2: _baseAddresses (128B)       ← Only the address we need
  CL 3:  _hitCounts (64B)             ← Only if incrementing (write-combine)
  = 2-3 cache lines per access
```

The SOA layout concentrates the hot data (_pageIndices + _baseAddresses) in 3 cache lines, compared to the AoS layout where slot metadata is spread across more lines.

---

## Contention Analysis

### Before

```
Contention points:
  1. PageInfo.StateSyncRoot    — per-page lock, contended under concurrent access
  2. ConcurrentSharedCounter   — incremented/decremented under lock
  3. ClockSweepCounter         — CAS with retry
  4. Global clock hand         — CAS with retry (eviction)

Hot contention: StateSyncRoot when multiple threads access the same page
```

### After

```
Contention points:
  1. AccessEpoch update        — CAS, but monotonic (max only) → rarely retries
  2. ClockSweepCounter         — CAS with retry (unchanged)
  3. Global clock hand         — CAS with retry (unchanged)
  4. GlobalEpoch increment     — CAS on scope exit (new, but one per operation)
  5. ExclusiveOwnerThreadId    — CAS for exclusive (rare operation)

Hot contention: ClockSweepCounter (unchanged from before)
Eliminated: StateSyncRoot lock contention for shared access
```

**Net improvement**: The highest-contention point (StateSyncRoot for shared access) is eliminated. The new GlobalEpoch CAS is one per operation (amortized), not one per page access.

---

**Next:** [08 — Migration Plan](./08-migration-plan.md) — Phased implementation strategy.

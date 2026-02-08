# Typhon Epoch System — Design

**Parent:** [README.md](./README.md)
**Prerequisite:** [01 — Epoch Fundamentals](./01-epoch-fundamentals.md)

---

## Overview

This document specifies how the epoch-based resource management system is implemented within Typhon. It covers the global epoch counter, thread registration, scope management, and MinActiveEpoch computation.

---

## Component 1: Global Epoch Counter

A single `long` value, shared across all threads. Starts at 1 (epoch 0 is reserved as "never accessed").

```
Location: EpochManager (new singleton, owned by DatabaseEngine)
Type: long (64-bit, naturally atomic on x64)
Initial value: 1
Advancement: Interlocked.Increment on outermost scope exit
```

### Advancement Strategy

The global epoch advances when a thread's scope depth returns to 0 (outermost exit). This ensures:
- **One increment per complete operation** (not per nested scope)
- **Natural batching**: a complex operation with 5 nested scopes only advances once at the end
- **Low contention**: only threads finishing operations compete on the counter

```
Thread A:                           Global Epoch
  EnterScope()  depth: 0→1         42
    EnterScope()  depth: 1→2       42  (no change — nested)
    ExitScope()   depth: 2→1       42  (no change — still nested)
  ExitScope()    depth: 1→0        43  ← Interlocked.Increment
```

### Fallback Timer

If no thread exits a scope for >1ms (unusual but possible during heavy exclusive operations), a background mechanism advances the epoch. This prevents starvation of eviction when all threads are in long scopes.

Implementation: Not a dedicated timer thread. Instead, the eviction code path (clock-sweep) checks `DateTime.UtcNow` against a threshold and advances the epoch if stale. This piggybacks on existing work without adding a thread.

---

## Component 2: Thread Registry

A fixed-size array where each participating thread registers its pinned epoch.

```
Structure: EpochThreadRegistry
┌──────────────────────────────────────────────────────────────┐
│ Slot 0:  PinnedEpoch = 40    (Thread A, active)              │
│ Slot 1:  PinnedEpoch = MAX   (Thread B, not in scope)        │
│ Slot 2:  PinnedEpoch = 42    (Thread C, active)              │
│ Slot 3:  PinnedEpoch = MAX   (unregistered)                  │
│ ...                                                          │
│ Slot N:  PinnedEpoch = MAX   (unregistered)                  │
└──────────────────────────────────────────────────────────────┘
```

### Design Choices

**Array size**: Fixed at creation (e.g., 256 slots). Sufficient for any realistic thread count.

**Slot assignment**: Each thread gets a slot on first use via `[ThreadStatic]` or thread-local storage. The slot index is cached thread-locally to avoid repeated lookups.

**Memory layout**: The array of `PinnedEpoch` values is a contiguous `long[]`, aligned for efficient scanning. For 256 slots, this is 2KB — fits in L1 cache for the scan operation.

**Sentinel value**: `long.MaxValue` means "this slot is not pinned / not participating." This ensures it never pulls down MinActiveEpoch.

### Registration Flow

```
Thread first touches the epoch system:
  1. Atomically claim a slot (CAS on a "claimed" flag or bump an allocation counter)
  2. Store slot index in [ThreadStatic] field
  3. Set PinnedEpoch = long.MaxValue (not in scope)

Thread enters scope:
  4. Read GlobalEpoch
  5. Write PinnedEpoch = GlobalEpoch to claimed slot

Thread exits outermost scope:
  6. Write PinnedEpoch = long.MaxValue to slot
  7. Interlocked.Increment(ref GlobalEpoch)

Thread terminates:
  8. GC collects thread-local EpochSlotHandle → finalizer fires
  9. Finalizer: set PinnedEpoch = long.MaxValue, mark slot Free
  (See "Thread Death Detection" below for the full mechanism)
```

### Thread-Local State

Each thread maintains (via `[ThreadStatic]`):

```
ThreadEpochState:
  int SlotIndex           — Index into the registry array
  int NestingDepth        — Current scope nesting depth (0 = not in scope)
  long PinnedEpoch        — Cached copy of the pinned epoch (avoids re-reading array)
  EpochSlotHandle Handle  — Reference to the GC-disposable handle (prevent GC of it while alive)
```

The nesting depth is critical: only the outermost EnterScope/ExitScope pair reads/writes the registry. Inner scopes just increment/decrement the depth counter — zero overhead.

### Thread Death Detection & Slot Reclamation

When a thread dies (normally, via exception, or thread pool retirement), its registry slot must be freed. .NET does not provide a direct "thread died" event, so we use two complementary mechanisms: a **finalizer** as the primary path, and a **liveness scan** as a safety net.

#### Why This Matters

A dead thread's slot that still holds a stale `PinnedEpoch` pins `MinActiveEpoch` low, preventing eviction of ALL pages accessed since that epoch. This is not data corruption (pages are protected, not falsely evicted), but it is **cache starvation** — the 256-slot page cache fills up with non-evictable pages, and new page requests stall.

```
Thread A:  pinned at epoch 500, then dies without exiting scope
           ↓
MinActiveEpoch stuck at 500
           ↓
All pages with AccessEpoch ≥ 500 are non-evictable
           ↓
Cache fills up → new page requests spin in AdaptiveWaiter
           ↓
Engine stalls until slot is reclaimed
```

#### Mechanism 1 (Primary): CriticalFinalizerObject in [ThreadStatic]

Each thread's registry slot is paired with a small GC-managed class stored in a `[ThreadStatic]` field. When the thread dies, the object loses its root and gets collected — the finalizer frees the slot.

```
EpochSlotHandle : CriticalFinalizerObject

  Fields:
    int SlotIndex                — Which registry slot this thread owns
    EpochThreadRegistry Registry — Reference to the registry (to free the slot)

  Finalizer (~EpochSlotHandle):
    Registry.Slots[SlotIndex].PinnedEpoch = long.MaxValue
    Registry.Slots[SlotIndex].State = SlotState.Free
    Registry.Slots[SlotIndex].OwnerThread = null
```

**Thread-static storage** (`[ThreadStatic]` field) ensures:
- One handle per thread (automatic, no manual creation)
- The handle lives as long as the thread lives
- When the thread dies, the handle's root disappears → GC collects it

**Why `CriticalFinalizerObject`**: This is the .NET base class for safety-critical cleanup (same base class as `SafeHandle`). It provides stronger guarantees than a normal finalizer:
- Runs even during AppDomain unload
- Runs AFTER normal finalizers (all other cleanup completed first)
- CLR makes best effort to run it under resource pressure

**Lifecycle**:

```
Thread starts
  ↓
First epoch access → claim slot → create EpochSlotHandle → store in [ThreadStatic]
  ↓
Thread runs normally (handle stays rooted via [ThreadStatic])
  ↓
Thread dies (any cause: normal exit, exception, abort, pool retirement)
  ↓
[ThreadStatic] fields lose their root
  ↓
... next GC cycle ...
  ↓
EpochSlotHandle found unreachable → moved to f-reachable queue
  ↓
Finalizer thread runs ~EpochSlotHandle()
  ↓
Slot freed: PinnedEpoch = MaxValue, State = Free
```

**The delay**: Between thread death and finalizer execution, there is a gap (typically milliseconds to seconds, until the next GC collects the generation holding the handle). During this gap:
- If the thread died **outside** a scope (NestingDepth == 0): slot already holds `long.MaxValue` — no impact, nothing stale
- If the thread died **inside** a scope (crash mid-operation): slot holds a stale epoch, blocking `MinActiveEpoch` temporarily → delayed eviction until finalizer runs, not corruption

This delay is acceptable — it's bounded by GC frequency and the consequence is temporary cache pressure, not data corruption.

#### Mechanism 2 (Safety Net): Liveness Check During MinActiveEpoch Scan

The finalizer handles the common case. For faster reclamation under cache pressure, the `MinActiveEpoch` scan (which already runs during eviction) piggybacks a liveness check for suspicious slots.

**Per-slot data** (added to registry):

```
RegistrySlot:
  long PinnedEpoch               — The pinned epoch (or MaxValue if not in scope)
  SlotState State                — Free / Claimed
  Thread OwnerThread             — Reference to the owning Thread object
```

Storing a `Thread` reference (8 bytes per slot = 2KB for 256 slots) allows checking `Thread.IsAlive` during the scan.

**Enhanced MinActiveEpoch scan**:

```
MinActiveEpoch():
  min = long.MaxValue
  for each slot:
    if slot.State != Claimed:
      continue

    epoch = slot.PinnedEpoch
    if epoch == long.MaxValue:
      continue                       // not in scope, skip

    // Suspicion check: is this epoch abnormally old?
    if (GlobalEpoch - epoch) > STALE_THRESHOLD:
      // This slot has been pinned for a very long time
      // Check if the owning thread is still alive
      if slot.OwnerThread != null && !slot.OwnerThread.IsAlive:
        // Thread is dead but finalizer hasn't run yet
        // Force-free the slot immediately
        slot.PinnedEpoch = long.MaxValue
        slot.State = Free
        slot.OwnerThread = null
        continue

    if epoch < min:
      min = epoch
  return min
```

**`STALE_THRESHOLD`**: A configurable epoch distance (e.g., 10,000 epochs ≈ 10ms at 1M ops/sec). Below this threshold, the slot is young enough that checking liveness is unnecessary — the thread is probably just mid-operation. Above it, something unusual is happening (very long operation, or dead thread).

**Why not check `Thread.IsAlive` on every slot?**: `Thread.IsAlive` reads thread state from the CLR, which is slightly more expensive than reading a `long` field. By only checking stale slots, we keep the scan cost minimal for the common case (all threads alive and active).

**Why a strong `Thread` reference?**: A `WeakReference<Thread>` would seem ideal (avoid preventing GC of the Thread object), but it has a problem: if the WeakReference target is collected before our finalizer runs, we can't call `Thread.IsAlive` anymore. A strong reference keeps the Thread object alive, which is fine because:
- Thread objects are small (~200 bytes) and there are at most 256
- The finalizer frees the reference when it cleans up the slot
- The Thread object would survive anyway as long as the thread is running

#### How the Two Mechanisms Interact

```
Normal case (thread exits cleanly):
  Thread exits scope before dying → PinnedEpoch already MaxValue
  → Finalizer runs later, just marks slot Free
  → No stale epoch, no impact on MinActiveEpoch
  → Slot available for reuse after finalizer

Abnormal case (thread dies inside scope):
  Thread dies mid-operation → PinnedEpoch still holds stale value
  → MinActiveEpoch pinned low → eviction stalls
  → On next MinActiveEpoch scan:
      If (GlobalEpoch - staleEpoch) > STALE_THRESHOLD:
        Check Thread.IsAlive → false → force-free slot → immediate recovery
  → Meanwhile, finalizer is also queued → would also free it (but scan got there first)
  → Both mechanisms are idempotent (freeing an already-free slot is harmless)

Thread pool thread (never dies during process):
  Slot stays Claimed for process lifetime
  → PinnedEpoch alternates between epoch values and MaxValue (scope in/out)
  → No cleanup needed until process exits
  → On process exit: finalizer runs during AppDomain unload (CriticalFinalizerObject)
```

#### async/await Safety: Compile-Time Prevention

A related but distinct concern: epoch scopes must not survive across `await` points, because after `await` the code may resume on a **different thread** whose `[ThreadStatic]` state references a different slot.

This is prevented at compile time by making `EpochGuard` a `ref struct`:

```
ref struct EpochGuard : IDisposable { ... }

// This is a COMPILER ERROR (CS4012):
async Task DoWork()
{
    using var guard = EpochGuard.Enter();
    await SomethingAsync();          // ← compiler rejects this
}

// The compiler enforces that EpochGuard cannot cross await boundaries,
// which means it cannot cross thread boundaries.
```

This is not thread death detection — it's a design-level prevention of the situation where epoch state could become cross-thread confused. Mentioned here because it's part of the same "thread lifecycle correctness" story.

---

## Component 3: EpochGuard (Scope Wrapper)

Epoch scope management uses a **hybrid design**: the real logic lives as static methods on `EpochManager`, and `EpochGuard` is a thin `ref struct` convenience wrapper with built-in **copy-safety via depth validation**.

### Why Not a Naive IDisposable ref struct?

A `ref struct` prevents heap storage, lambda capture, and `await` crossing — but it does **not** prevent local copies:

```
var g1 = EpochGuard.Enter();  // NestingDepth: 0→1, pins epoch
var g2 = g1;                   // ← silent copy, perfectly legal
g1.Dispose();                  // NestingDepth: 1→0, unpins, advances epoch
g2.Dispose();                  // NestingDepth: 0→-1 !! Corrupted state
```

This is the same class of problem as `ChunkAccessor` copies: double-Dispose on copied value types. For `EpochGuard`, the consequence is premature epoch unpin (pages become evictable while a thread still holds pointers to them) or negative nesting depth that permanently breaks the thread's epoch participation.

### Design: Depth-Validation Guard

The guard stores the **expected nesting depth** at creation. On `Dispose`, it only acts if the current depth still matches — a copy that disposes after the original will see a mismatched depth and become a no-op.

**Core logic (static methods on EpochManager):**

```
EpochManager:

  EnterScope():                              // [ThreadStatic] state
    depth = ThreadEpochState.NestingDepth
    if (depth == 0):
      PinnedEpoch = read GlobalEpoch
      Registry[SlotIndex] = PinnedEpoch
    ThreadEpochState.NestingDepth = depth + 1

  ExitScope():
    ThreadEpochState.NestingDepth--
    if (ThreadEpochState.NestingDepth == 0):
      Registry[SlotIndex] = long.MaxValue
      Interlocked.Increment(ref GlobalEpoch)
```

**Guard wrapper (ref struct with copy defense):**

```
EpochGuard (ref struct, IDisposable):

  Fields:
    int _expectedDepth       — NestingDepth at time of creation

  Enter():
    EpochManager.EnterScope()
    return new EpochGuard { _expectedDepth = ThreadEpochState.NestingDepth }

  Dispose():
    if (ThreadEpochState.NestingDepth != _expectedDepth):
      return                 // copy or already disposed — no-op
    EpochManager.ExitScope()
```

### Copy Safety — How It Works

The `_expectedDepth` acts as a de facto scope token within the LIFO stack:

```
Copy scenario (safe):
  var g1 = EpochGuard.Enter()   // depth=1, _expectedDepth=1
  var g2 = g1                    // _expectedDepth=1 (copy)
  g1.Dispose()                   // depth==1==expected → ExitScope(), depth→0. OK
  g2.Dispose()                   // depth==0≠1 → SKIP. Safe!

Nesting scenario (correct):
  var outer = EpochGuard.Enter() // depth=1, _expectedDepth=1
  var inner = EpochGuard.Enter() // depth=2, _expectedDepth=2
  inner.Dispose()                // depth==2==2 → ExitScope(), depth→1. OK
  outer.Dispose()                // depth==1==1 → ExitScope(), depth→0, unpin. OK
```

Out-of-order disposal (disposing inner before outer in non-`using` code) would silently fail — but that is already a bug regardless. A `Debug.Assert` can catch it during development.

### Usage Pattern

```
// Preferred: using pattern (copy-safe, LIFO-guaranteed)
using var guard = EpochGuard.Enter();    // pin to current epoch
// ... access any number of pages ...
// ... all pointers valid until here ...
// guard.Dispose() auto-exits scope

// Alternative: raw static methods for try/finally scenarios
EpochManager.EnterScope();
try
{
    // ... access pages ...
}
finally
{
    EpochManager.ExitScope();
}
```

### Nesting

```
using var outer = EpochGuard.Enter();    // pin at epoch 42, depth=1, expected=1
  // access pages...
  using var inner = EpochGuard.Enter();  // depth=2, expected=2, no registry write
    // access more pages (still protected at epoch 42)
  // inner.Dispose(): depth==2==expected → depth→1, no registry write
// outer.Dispose(): depth==1==expected → depth→0, registry cleared, epoch→43
```

Inner scopes are essentially free — they only touch a thread-local integer (plus one comparison for the copy check).

---

## Component 4: MinActiveEpoch Computation

The minimum PinnedEpoch across all registered thread slots. This determines what resources can be evicted.

### Computation

```
MinActiveEpoch():
  min = long.MaxValue
  for i in 0..RegistrySize:
    epoch = Registry[i]    // plain read (x64 atomic for long)
    if epoch < min:
      min = epoch
  return min
```

For 256 slots, this reads 2KB of contiguous memory — about 32 cache lines. On a modern CPU, this takes ~100-200ns (dominated by cache line fetches if not already in L1).

### Caching Strategy

Computing MinActiveEpoch on every eviction attempt would be wasteful. Instead:

```
Cached MinActiveEpoch:
  Recomputed when:
    - Clock-sweep needs to evict a page (triggered by cache pressure)
    - At most once per N eviction attempts (e.g., N=16)
    - The cached value is a LOWER BOUND: it may be stale-low, meaning we might
      fail to evict some actually-evictable pages, but we'll never evict a
      page that's still in use. This is safe.

  Storage: single long field on EpochManager, updated atomically
```

**Staleness is safe**: If the cached MinActiveEpoch is 40 but the real value is 45, we'll keep pages tagged 40-44 when they could be evicted. This wastes a few cache slots temporarily but doesn't cause correctness issues. On the next recomputation, we catch up.

**Staleness is bounded**: Since eviction recomputes periodically, the delay is at most a few eviction cycles.

### SIMD-Accelerated Scan (Optional Optimization)

For large registries, the MinActiveEpoch scan can use SIMD:

```
Scan 4 epochs per Vector256<long> iteration:
  Load 4 values → Vector256.Min with running minimum → extract scalar at end

For 256 slots: 64 vector operations → ~50ns on modern CPUs
```

This is a micro-optimization — the plain scalar loop is already fast enough for 256 slots. But it's available if the registry grows or if the scan is called more frequently.

---

## Component 5: Epoch Advancement Rules

### When GlobalEpoch Advances

| Trigger | Frequency | Mechanism |
|---------|-----------|-----------|
| Outermost scope exit | Per-operation (~μs) | `Interlocked.Increment` |
| Eviction fallback | When eviction stalls | `Interlocked.CompareExchange` if stale >1ms |

### What Prevents Advancement?

Nothing prevents the epoch counter from advancing. It always moves forward. What changes is **MinActiveEpoch** — that depends on active scopes.

A long-running scope keeps MinActiveEpoch low, which prevents pages from becoming evictable. This is analogous to a long-running transaction keeping MinTSN low.

### Epoch Wrap-Around

With a 64-bit counter starting at 1 and incrementing once per operation:
- At 1 million operations/second: wrap-around after ~292,000 years
- At 1 billion operations/second: wrap-around after ~292 years

Wrap-around is not a practical concern.

---

## Integration Points

### Where EpochGuard Is Entered

```
1. Transaction creation:
   Transaction constructor enters an epoch scope.
   Scope exits on Transaction.Dispose().
   → All pages accessed during the transaction are protected.

2. Individual CRUD operations (if no transaction):
   Each operation enters/exits its own scope.
   → Short-lived protection window.

3. B+Tree operations:
   Enter scope before tree traversal.
   Exit scope when traversal complete.
   → Nested within transaction scope (free, just depth++).

4. Segment growth/allocation:
   Enter scope before allocating new pages.
   Exit scope when allocation complete.
   → Protects both old and new pages during structural changes.
```

### Where EpochGuard Is NOT Needed

```
- Background I/O (page flush to disk): operates on dirty page data, not cache slots
- Metadata reads (schema, config): pinned in memory permanently, not cache-managed
- Metrics/diagnostics: read-only snapshots, tolerant of stale data
```

---

## Memory Overhead

| Component | Size | Notes |
|-----------|------|-------|
| Global epoch counter | 8 bytes | Single long |
| Thread registry (256 slots) | ~4,608 bytes | PinnedEpoch (8) + State (4) + Thread ref (8) per slot |
| Cached MinActiveEpoch | 8 bytes | Single long |
| Per-thread state | 20 bytes | SlotIndex + NestingDepth + PinnedEpoch (via [ThreadStatic]) |
| Per-thread EpochSlotHandle | ~32 bytes | GC object on heap (CriticalFinalizerObject) |
| **Total fixed** | **~4.6 KB** | Still negligible for a 2MB page cache |
| **Per-thread** | **~52 bytes** | [ThreadStatic] fields + heap handle |

This is still dramatically less than the current per-page `ConcurrentSharedCounter` + per-slot `PinCounter` + per-slot `PromoteCounter` overhead.

---

## Correctness Properties

### Safety: No Use-After-Reuse

```
THEOREM: If a thread is pinned at epoch E and accesses page P,
         page P's cache slot cannot be reused for a different file page
         until the thread exits its scope.

PROOF:
  1. Thread pins at epoch E → Registry[slot] = E
  2. Thread accesses page P → P.AccessEpoch = max(P.AccessEpoch, GlobalEpoch) ≥ E
  3. MinActiveEpoch ≤ E (because at least this thread is pinned at E)
  4. Eviction requires P.AccessEpoch < MinActiveEpoch
  5. Since P.AccessEpoch ≥ E and MinActiveEpoch ≤ E:
     P.AccessEpoch < MinActiveEpoch → (≥E) < (≤E) → impossible
  6. Therefore P cannot be evicted while the thread is pinned. ∎
```

### Liveness: Pages Eventually Become Evictable

```
THEOREM: If all threads that accessed page P exit their scopes,
         P will eventually become evictable.

PROOF:
  1. All threads exit scopes → all Registry entries become MaxValue
  2. MinActiveEpoch = MaxValue (or current GlobalEpoch if recomputed)
  3. P.AccessEpoch is some finite value E
  4. E < MaxValue → eviction check passes
  5. Therefore P is evictable. ∎
```

### Progress: No Deadlock from Epoch System

The epoch system introduces no mutual exclusion between threads. Threads never block on each other's epoch operations. The only contention point is `Interlocked.Increment` on GlobalEpoch, which is wait-free (CAS retry on contention, but no blocking).

---

**Next:** [03 — Page Cache Evolution](./03-page-cache-evolution.md) — How the page cache changes to support epochs.

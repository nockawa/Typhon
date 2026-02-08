# Epoch-Based Resource Management — Fundamentals

**Parent:** [README.md](./README.md)

---

## What Problem Are We Solving?

The current Typhon storage layer uses **reference counting** to decide when a cached resource (page or chunk slot) can be reused. Every time code accesses a page, it increments a counter; when done, it decrements. If you forget to decrement (e.g., struct copy, missing Dispose), the resource leaks. If you decrement twice, you get corruption. If all 16 ChunkAccessor slots are ref-held simultaneously, the engine crashes with no recovery.

We want a model where:
- You **never need to "release"** a shared resource — it becomes reclaimable automatically
- **Copying a struct** doesn't create correctness bugs
- There is **no hard crash** from exhausting pinned slots
- The system remains **microsecond-fast** on the hot path

---

## The Core Insight: Protect by Era, Not by Count

Instead of counting "how many threads hold this resource," we track "during which era was this resource last used, and is any thread still in that era?"

```
Reference Counting:                    Epoch-Based:
┌─────────────────────┐                ┌─────────────────────┐
│ Page P              │                │ Page P              │
│ RefCount: 3         │                │ AccessEpoch: 42     │
│                     │                │                     │
│ Evictable? No       │                │ Evictable?          │
│ (RefCount > 0)      │                │ 42 < MinActiveEpoch │
│                     │                │ → depends on active │
│ Must decrement 3x   │                │   threads           │
│ to make evictable   │                │ No action needed    │
└─────────────────────┘                └─────────────────────┘
```

---

## Epoch-Based Reclamation: The Concept

Epoch-Based Reclamation (EBR) is a well-established technique used in:
- **Linux kernel RCU** (Read-Copy-Update) — protects data structures read by millions of threads
- **crossbeam-epoch** (Rust) — high-performance concurrent data structures
- **Java's EpochBasedMemoryReclamation** — used in concurrent garbage collectors
- Many lock-free data structures and databases

### The Three Components

```
┌─────────────────────────────────────────────────────────────────┐
│                    EPOCH-BASED RECLAMATION                      │
│                                                                 │
│  ┌──────────────┐   ┌──────────────────┐   ┌────────────────┐   │
│  │  1. GLOBAL   │   │  2. THREAD-LOCAL │   │ 3. RESOURCE    │   │
│  │  EPOCH       │   │  EPOCH PINS      │   │ TAGS           │   │
│  │              │   │                  │   │                │   │
│  │  A single    │   │  Each thread     │   │ Each resource  │   │
│  │  counter     │   │  records which   │   │ records when   │   │
│  │  that moves  │   │  epoch it        │   │ it was last    │   │
│  │  forward     │   │  entered         │   │ accessed       │   │
│  │              │   │                  │   │                │   │
│  │  [E=42]      │   │  Thread A: 40    │   │ Page X: E=38   │   │
│  │              │   │  Thread B: 42    │   │ Page Y: E=42   │   │
│  │              │   │  Thread C: ---   │   │ Page Z: E=35   │   │
│  │              │   │  (not active)    │   │                │   │
│  └──────────────┘   └──────────────────┘   └────────────────┘   │
│                                                                 │
│  MinActiveEpoch = min(40, 42) = 40                              │
│                                                                 │
│  Can evict Page X (38 < 40)? YES — no active thread was in      │
│                                      epoch 38 or earlier        │
│  Can evict Page Y (42 < 40)? NO  — thread B is still in 42      │
│  Can evict Page Z (35 < 40)? YES — epoch 35 is long past        │
└─────────────────────────────────────────────────────────────────┘
```

### How a Thread Interacts with Epochs

```
Timeline:
  Global Epoch:  ... 40 ──── 41 ──── 42 ──── 43 ──── 44 ...
                        │              │              │
  Thread A:             │              │              │
    ┌─── EnterScope ────┤              │              │
    │  Pin = 40         │              │              │
    │  Access Page X    │              │              │
    │  Access Page Y    │              │              │
    │  Access Page Z    │              │              │
    └─── ExitScope ─────┤              │              │
    │  Pin = cleared    │              │              │
    │                   │              │              │
    ┌─── EnterScope ────┼──────────────┤              │
    │  Pin = 42         │              │              │
    │  Access Page W    │              │              │
    └─── ExitScope ─────┼──────────────┤              │
         Pin = cleared  │              │              │
```

**Key rules:**
1. When a thread enters a scope, it records the **current global epoch** as its pin.
2. While pinned, ANY resource the thread touches is protected from eviction.
3. When the thread exits its scope, it clears its pin.
4. Once ALL threads that were active during epoch E have exited, resources from epoch E become evictable.

### The Guarantee

```
INVARIANT: A resource tagged with epoch E will not be evicted
           while any thread is pinned to an epoch ≤ E.

This means:
  If you enter a scope (pin at epoch E) and access a resource,
  that resource CANNOT be reused for something else until you
  exit the scope. Your pointers remain valid.

  You don't need to "release" the resource. Just exit the scope.
```

---

## Why This Eliminates Reference Counting

### Reference Counting: Per-Resource Obligation

```
For EVERY resource access:
  acquire()    ← Must call
  use()
  release()    ← Must call (exact match!)

Consequences:
  - Forget release       → resource leak (cache exhaustion)
  - Double release       → corruption / crash
  - Copy struct + release both → double release
  - Copy struct + release neither → leak
  - N resources → N acquire/release pairs
```

### Epoch-Based: Per-Scope Obligation

```
For an ENTIRE operation (touching N resources):
  enterScope()    ← Call once
  use(resource1)  ← No acquire needed
  use(resource2)  ← No acquire needed
  ...
  use(resourceN)  ← No acquire needed
  exitScope()     ← Call once

Consequences:
  - Forget exitScope  → epoch doesn't advance (GC delays, not crash)
  - Copy struct       → harmless (no per-resource state to corrupt)
  - N resources       → still just 1 enter + 1 exit
```

The obligation count drops from **2N** (N acquires + N releases) to **2** (1 enter + 1 exit).

---

## Epoch Advancement

The global epoch must advance for resources to become reclaimable. If the epoch never advances, no page can ever be evicted (because `MinActiveEpoch` stays low).

### When Does the Epoch Advance?

```
Option A: On every scope exit
  Pro: Maximum reclamation speed
  Con: High contention on global counter (Interlocked.Increment)

Option B: Periodic timer (every 100μs)
  Pro: Predictable, low contention
  Con: Adds timer dependency; latency between "done" and "reclaimable"

Option C: When scope depth returns to 0 (outermost exit)
  Pro: Natural batching; one increment per operation
  Con: Long operations delay advancement

Option D: Hybrid — advance on outermost scope exit, OR periodic fallback
  Pro: Best of both; low contention + bounded latency
  Con: Slightly more complex
```

**Recommended for Typhon: Option C with a fallback.** Most operations are microsecond-level, so "advance on outermost scope exit" provides fast advancement with one atomic increment per operation. A periodic fallback (e.g., every 1ms) ensures advancement even during long operations.

---

## The Coarse-Grained Protection Tradeoff

EBR is **coarser** than reference counting. A pinned thread protects not just the pages it accessed, but ALL pages tagged with epoch ≥ its pin (because the eviction check is `AccessEpoch < MinActiveEpoch`).

```
With Ref Counting:                    With Epochs:
┌─────────────────────┐               ┌─────────────────────┐
│ Thread A holds:     │               │ Thread A pinned     │
│   Page 1 (ref=1)    │               │ at epoch 5          │
│   Page 2 (ref=1)    │               │                     │
│                     │               │ ALL pages with      │
│ Page 3 (ref=0)      │               │ AccessEpoch ≥ 5     │
│ → evictable ✓       │               │ are protected       │
│                     │               │                     │
│ Page 4 (ref=0)      │               │ Even Page 3 and 4   │
│ → evictable ✓       │               │ if accessed at E≥5  │
│                     │               │ → NOT evictable ✗   │
└─────────────────────┘               └─────────────────────┘
```

### Why This Is Acceptable for Typhon

1. **Scopes are short**: A CRUD operation is microseconds. The "window of protection" is tiny.
2. **Working set is small**: A single operation touches 5-20 pages. The cache has 256 slots.
3. **Epoch advances fast**: With microsecond operations, the epoch advances thousands of times per second.
4. **Same tradeoff exists in MVCC**: Long-running transactions already block revision GC via `MinTSN`. This is the same pattern at a different layer.

### When It Could Be a Problem

A thread that enters a scope and stays for a **long time** (e.g., a complex query touching 200+ pages) prevents eviction of ALL pages accessed during that epoch. Mitigation: break long operations into multiple scopes (exit and re-enter between batches).

---

## Comparison with Typhon's Existing MVCC Model

The epoch system is structurally identical to the transaction system you already have:

| Concept | MVCC (Existing) | Epoch (Proposed) |
|---------|-----------------|------------------|
| Global counter | `TransactionChain.NextFreeId` (TSN) | `GlobalEpoch` |
| Thread pin | `Transaction.TSN` | Thread-local `PinnedEpoch` |
| Oldest active | `TransactionChain.MinTSN` | `MinActiveEpoch` |
| Resource tag | `Revision.TSN` | `Page.AccessEpoch` |
| Reclaimable when | `RevisionTSN < MinTSN` | `AccessEpoch < MinActiveEpoch` |
| Blocks reclamation | Long-running transaction | Long-running scope |

This is the exact same pattern applied at the storage layer instead of the data layer.

---

## Visual Summary: Complete Lifecycle

```
┌──────────── EPOCH LIFECYCLE ──────────────────────────────────────────┐
│                                                                       │
│  1. THREAD ENTERS SCOPE                                               │
│     ┌──────────────────────────────────────┐                          │
│     │ threadSlot.PinnedEpoch = GlobalEpoch │  (one atomic read)       │
│     │ nestingDepth++                       │                          │
│     └──────────────────────────────────────┘                          │
│                          │                                            │
│  2. THREAD ACCESSES PAGES (any number)                                │
│     ┌──────────────────────────────────────┐                          │
│     │ page.AccessEpoch = max(current, GE)  │  (one atomic update)     │
│     │ return page.Address                  │  (just a pointer)        │
│     │                                      │                          │
│     │ → No ref-count increment             │                          │
│     │ → No PageAccessor.Dispose needed     │                          │
│     │ → Pointer valid until scope exit     │                          │
│     └──────────────────────────────────────┘                          │
│                          │                                            │
│  3. THREAD EXITS SCOPE                                                │
│     ┌──────────────────────────────────────┐                          │
│     │ nestingDepth--                       │                          │
│     │ if (nestingDepth == 0):              │                          │
│     │   threadSlot.PinnedEpoch = MAX_VALUE │  (one atomic write)      │
│     │   tryAdvanceGlobalEpoch()            │  (one CAS, may fail ok)  │
│     └──────────────────────────────────────┘                          │
│                          │                                            │
│  4. EVICTION (later, when cache needs space)                          │
│     ┌──────────────────────────────────────┐                          │
│     │ minEpoch = scan all thread slots     │  (periodic, cached)      │
│     │ for each candidate page:             │                          │
│     │   if page.AccessEpoch < minEpoch     │                          │
│     │      && !page.IsExclusiveLocked      │                          │
│     │      && page.DirtyCounter == 0:      │                          │
│     │     → EVICT (reuse slot)             │                          │
│     └──────────────────────────────────────┘                          │
│                                                                       │
└───────────────────────────────────────────────────────────────────────┘
```

---

## Key Terminology

| Term | Definition |
|------|-----------|
| **Global Epoch** | A monotonically increasing counter shared by all threads |
| **Pinned Epoch** | The epoch a thread recorded when it entered its current scope |
| **MinActiveEpoch** | The minimum pinned epoch across all currently active threads |
| **AccessEpoch** | The global epoch at the time a page was last accessed |
| **Scope** | A bounded region of code where a thread needs resource stability |
| **Quiescent Point** | A moment when a specific thread has no active scope |
| **Grace Period** | The interval between a resource being tagged and becoming reclaimable |

---

**Next:** [02 — Typhon Epoch System](./02-typhon-epoch-system.md) — How these concepts map to Typhon's specific implementation.

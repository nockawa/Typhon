# Epoch-Based Resource Management — Research

**Date:** 2026-02-08
**Status:** Research (analysis & recommendation)
**Motivation:** Eliminate reference counting complexity in PageCache and ChunkAccessor
**Preceding research:** [`claude/research/ChunkAccess.md`](../ChunkAccess.md) (identified the problems this proposes to solve)
**Next step:** If accepted → create design doc in `claude/design/` with concrete implementation specs

> **TL;DR** — Replace per-resource reference counting (acquire/release on every page access) with epoch-based protection (enter scope once, access freely, exit scope once). This **eliminates** PinCounter, PromoteCounter, ChunkHandle, ChunkHandleUnsafe, "all slots pinned" crash, and struct-copy bugs in ChunkAccessor. Page cache changes are minimal. Start with [01 — Epoch Fundamentals](./01-epoch-fundamentals.md) to understand the technique, then [04 — ChunkAccessor Redesign](./04-chunk-accessor-redesign.md) for the main impact.

---

## The Problem

The current storage layer uses **reference counting** at two levels to manage resource lifetimes:

1. **Page Cache** (`PagedMMF`): `ConcurrentSharedCounter` tracks how many threads hold a page. Pages can only be evicted when counter reaches 0. Requires lock acquisition on every access and release.

2. **ChunkAccessor**: Layers **three additional ref-counting mechanisms** on top:
   - `PinCounter` — prevents local slot eviction (manual pin/unpin ceremony)
   - `PromoteCounter` — ref-counts exclusive access promotions (must match exactly)
   - `ChunkHandle` / `ChunkHandleUnsafe` — disposable wrappers for safe pinning

This creates:
- **Mandatory acquire/release ceremony** on every resource access (2N obligations for N accesses)
- **Struct copy bugs** — copying ChunkAccessor creates correctness issues (leaked refs, double disposal)
- **Hard crash** — all 16 ChunkAccessor slots pinned simultaneously → `InvalidOperationException`, no recovery
- **Complex exclusive access** — promotion/demotion ref-counting must match exactly

## The Solution: Epoch-Based Resource Management

Replace per-resource ref-counting with **epoch scoping**:

```
BEFORE: For each of N page accesses    AFTER: For the entire operation
  acquire(page1)                          enterScope()
  use(page1)                              use(page1)
  release(page1)                          use(page2)
  acquire(page2)                          use(page3)
  use(page2)                              ...
  release(page2)                          use(pageN)
  ...                                     exitScope()
  acquire(pageN)
  use(pageN)
  release(pageN)

  Obligations: 2N                         Obligations: 2
  Failure mode: crash on leak             Failure mode: delayed eviction (self-correcting)
```

---

## Document Map

| # | Document | Focus | Key Takeaway |
|---|----------|-------|--------------|
| 01 | [Epoch Fundamentals](./01-epoch-fundamentals.md) | What is EBR? How does it work? Visual diagrams. | "Protect by era, not by count" — obligations drop from 2N to 2 |
| 02 | [Typhon Epoch System](./02-typhon-epoch-system.md) | GlobalEpoch, thread registry, EpochGuard, MinActiveEpoch | One EpochGuard per operation; nested scopes are free (~1 cycle) |
| 03 | [Page Cache Evolution](./03-page-cache-evolution.md) | PagedMMF changes (minimal) | Replace ConcurrentSharedCounter with AccessEpoch; shared access becomes 4.6x faster |
| 04 | [ChunkAccessor Redesign](./04-chunk-accessor-redesign.md) | Complete transformation | 1KB → 326 bytes; 8 methods → 3; all crash modes eliminated |
| 05 | [Exclusive Access Model](./05-exclusive-access.md) | How writes work without ref-counting | Decoupled: epochs for lifetime, CAS latch for exclusivity |
| 06 | [Operations Walkthrough](./06-operations-walkthrough.md) | Before/after for every operation type | B+Tree split: from "very high" to "low" complexity |
| 07 | [Performance Analysis](./07-performance-analysis.md) | CPU cycle projections, cache line analysis | Slot eviction 2x faster; page access 4.6x faster; exclusive 4.5x faster |
| 08 | [Migration Plan](./08-migration-plan.md) | Phased implementation strategy | 5 phases, each independently testable and reversible |

### Reading Order

```
For understanding the technique:
  01 (fundamentals) → 02 (Typhon specifics)

For understanding the impact:
  04 (ChunkAccessor — the big win) → 05 (exclusive access)

For understanding how operations change:
  06 (walkthrough — before/after for each operation)

For implementation planning:
  07 (performance) → 08 (migration phases)

For page cache specifics (minor changes):
  03 (page cache evolution)
```

---

## Key Design Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Epoch-scoped (not per-resource tick deadlines) | Callers don't need to predict "how long" they need a resource. Just enter scope, use freely, exit scope. |
| D2 | Global epoch advances on outermost scope exit | One atomic increment per operation — natural batching, low contention |
| D3 | Thread registry is fixed-size (256 slots) | Sufficient for any realistic thread count. Simple, no allocation. |
| D4 | Page cache changes are minimal | The page cache's current complexity is well-encapsulated. Only the eviction predicate and access tagging change. |
| D5 | ChunkAccessor is completely redesigned | The ChunkAccessor's triple ref-counting is the source of most complexity. Full redesign to pure performance cache. |
| D6 | Exclusive access decoupled from lifetime | Epochs handle "how long does it live in cache." CAS latch handles "can I write exclusively." Orthogonal concerns. |
| D7 | No shared reader latch (unlatched reads) | MVCC provides read isolation. B+Tree reads see snapshots. Only structural modifications need exclusive access. |
| D8 | ChunkHandle / ChunkHandleUnsafe eliminated | Epoch protection makes all GetChunk references safe by construction. No scoped wrapper needed. |
| D9 | Phased migration with dual-mode support | Old and new code paths coexist during migration. Each phase is independently testable. |
| D10 | Supersedes StackChunkAccessor design | Epoch protection at the page-cache level is strictly more general than scope tracking at the accessor level. |

---

## Impact Summary

### What Simplifies

| Component | Current | After | Reduction |
|-----------|---------|-------|-----------|
| ChunkAccessor size | ~1024 bytes | ~326 bytes | 3x smaller |
| ChunkAccessor API methods | 8 | 3 | 63% fewer |
| Caller obligations per access | 2 (acquire + release) | 0 | Eliminated |
| Crash modes | "All slots pinned" | None | Eliminated |
| Struct copy safety | Dangerous | Harmless | Bug class eliminated |
| ChunkHandle types | 2 (Handle + HandleUnsafe) | 0 | Deleted |
| Page cache shared access | Lock + counter + unlock (~20ns) | Epoch tag (~4ns) | 5x faster |

### What Gets More Complex

| Component | Current | After | Notes |
|-----------|---------|-------|-------|
| New EpochManager | N/A | New singleton (~2KB) | Small, well-defined |
| Global epoch CAS | N/A | One CAS per operation | Negligible overhead |
| MinActiveEpoch scan | N/A | ~128ns per scan | Amortized, cached |
| Coarser eviction granularity | Precise (per-ref) | Approximate (per-epoch) | Acceptable for short scopes |

### What Stays the Same

- Clock-sweep eviction algorithm (same structure)
- Dirty page tracking (DirtyCounter, ChangeSet)
- Async I/O (disk reads/writes)
- Page size (8KB), cache sizing
- SIMD search in ChunkAccessor
- MRU optimization
- B+Tree algorithms (same logic, simpler resource management)
- MVCC semantics (unchanged)

---

## Recommendation: Should This Replace the Current Design?

**Verdict: Yes.** The epoch-based approach should replace the current reference-counting model. The rest of this section explains why, acknowledges the risks honestly, and identifies conditions that would invalidate this recommendation.

### The Case FOR Adoption

**1. It eliminates an entire class of bugs, not just one bug.**

The current ref-counting model creates a *systematic* correctness burden: every code path that touches pages or chunks must correctly balance acquire/release, pin/unpin, promote/demote. This is not one bug to fix — it is a permanent tax on every feature, every refactor, every new contributor. The cost compounds as the codebase grows.

Evidence from the current codebase:
- `ChunkAccessor` already needed a `StateSnapshot` + `CheckInternalState` testing mechanism specifically to catch pin/promote imbalances
- The `DisposeAccessors()` pattern in `Transaction` exists solely to work around struct-copy-induced ref leaks
- The `ChunkHandleUnsafe` type exists solely because `ChunkHandle` (a `ref struct` with a `ref` field) cannot be stored in arrays — a workaround for a workaround
- The "all slots pinned" crash is a known, documented failure mode with no graceful recovery

With epochs, none of these problems exist. Pointers obtained inside a scope are valid until the scope ends. Period. No balancing, no ceremony, no workarounds.

**2. It aligns with a proven, battle-tested pattern.**

Epoch-Based Reclamation is not experimental. It powers:
- Linux kernel RCU (in production on billions of devices)
- crossbeam-epoch (standard Rust concurrency primitive)
- Multiple production databases (Silo, ERMIA, etc.)

Typhon's workload (short, microsecond-level operations with clear scope boundaries) is the *ideal* use case for EBR.

**3. It makes future development cheaper.**

Every future feature (Query engine, WAL, backup, new index types) needs to interact with pages and chunks. Under ref-counting, each feature must carefully manage acquire/release pairs. Under epochs, each feature just enters a scope and works freely. The "API gravity" changes from "complex and error-prone" to "simple and safe by default."

**4. It is faster, not just simpler.**

This is not a tradeoff of "simplicity for speed." The epoch model is *both* simpler and faster:
- Shared page access: 4.6x faster (no lock acquire/release)
- Slot eviction: 2x faster (no PageAccessor disposal)
- Exclusive access: 4.5x faster (CAS vs lock+counter+state)

Simpler code often IS faster code — fewer instructions, fewer memory barriers, fewer cache lines touched.

**5. The migration is safe and incremental.**

The phased migration plan (doc 08) ensures:
- Phase 0-1 add new code without changing existing behavior
- Phase 2 builds the new ChunkAccessor alongside the old one
- Phase 3 migrates callers one at a time, with full test suite after each
- Phase 4 removes dead code only after everything is verified

At no point is the system in a "half-migrated, broken" state. Each phase produces a working system.

### The Case AGAINST Adoption (Honest Risks)

**Risk 1: Coarser eviction granularity**

With ref-counting, a page becomes evictable the instant the last reader releases it. With epochs, a page remains non-evictable until `MinActiveEpoch` advances past its `AccessEpoch`. This means pages stay in cache slightly longer than necessary.

*Why this is acceptable:*
- Typhon operations are microsecond-level. The "extra" time a page stays non-evictable is measured in microseconds.
- The page cache (256 slots = 2MB) is sized for working sets much larger than a single operation's footprint.
- The same tradeoff already exists in MVCC (long-running transactions delay revision GC via `MinTSN`). This is the same pattern at a different layer — and it's been acceptable there.

*When it would NOT be acceptable:*
- If operations routinely run for >100ms while touching hundreds of pages. This could exhaust the 256-slot cache. Mitigation: break long operations into multiple epoch scopes.

**Risk 2: Long-running scope starvation**

A thread that enters a scope and runs for a long time pins `MinActiveEpoch` low, preventing eviction of ALL pages accessed since that epoch — even by other threads.

*Why this is acceptable:*
- This is identical to the "long-running transaction blocks GC" problem that Typhon already handles in MVCC.
- Operations are designed to be short. Long operations (bulk imports, full scans) can be structured to exit and re-enter scopes periodically.
- The impact is bounded: worst case is cache pressure (adaptive waiter spin), not crash or data corruption.

*When it would NOT be acceptable:*
- If Typhon needs to support unbounded-duration operations that touch large working sets within a single scope. This seems unlikely given the microsecond-level performance targets.

**Risk 3: New global contention point (GlobalEpoch CAS)**

Every operation's outermost scope exit does `Interlocked.Increment(ref GlobalEpoch)`. Under extreme concurrency (1M+ ops/sec across many cores), this CAS becomes a contention hotspot due to cache-line bouncing.

*Why this is acceptable:*
- At 1M ops/sec, each CAS is ~8ns. Even with 50% retry rate under contention, this adds ~12ns per operation — negligible compared to the 290-1220 cycles saved per operation.
- If contention becomes measurable, mitigation exists: batch advancement (advance every N outermost exits, or per-thread local epoch with periodic sync). The design accommodates this without API changes.

*When it would NOT be acceptable:*
- If Typhon targets >10M ops/sec across >64 cores with very small operations (sub-microsecond). At that scale, even nanosecond-level contention matters. But at that scale, the current ref-counting lock contention would also be a problem.

**Risk 4: Unlatched reads depend on MVCC correctness**

The design proposes no shared reader latch — readers access pages without any synchronization. This is safe because MVCC ensures readers and writers operate on different data. If MVCC has a bug (e.g., a writer modifies data that a reader is concurrently reading), the epoch model wouldn't catch it.

*Why this is acceptable:*
- MVCC snapshot isolation is a core, well-tested guarantee of Typhon. The design doesn't weaken it — it depends on it.
- The optional seqlock pattern (doc 05) provides an additional safety layer for B+Tree structural modifications, if needed.
- The current system also depends on MVCC for correctness — readers already don't lock the data they read, only the page cache entry.

**Risk 5: Implementation effort**

The migration touches the most critical layer of the engine (page cache + chunk access). Bugs here can cause data corruption.

*Why this is acceptable:*
- The phased plan ensures incremental, testable changes. Phase 2 builds the new accessor alongside the old one — full A/B comparison is possible.
- The existing test suite (1000+ lines of ChunkAccessor tests, stress tests, chaos tests) provides a strong safety net.
- The new code is *simpler* than the old code. Simpler code is easier to verify.

### Alternatives Considered

| Alternative | Why Not |
|------------|---------|
| **Keep ref-counting, fix bugs** | Doesn't address systemic complexity. Every new feature re-encounters the same pin/promote/dispose ceremony. Treats symptoms, not cause. |
| **Per-resource tick deadlines** (original proposal) | Requires callers to predict "how long" they need a resource. Overestimates waste cache; underestimates cause use-after-free. Epoch scoping avoids this problem entirely. |
| **StackChunkAccessor scopes** (existing reference doc) | Addresses ChunkAccessor complexity but not page cache overhead. Epoch-based is strictly more general — protection at the page cache level means all consumers benefit, not just one accessor type. |
| **Hazard pointers** | Per-resource tracking (like ref-counting but lock-free). More complex to implement than EBR, higher per-access overhead. Better suited for unbounded-lifetime resources, not scoped operations. |
| **RCU (full Read-Copy-Update)** | Requires copy-on-write for all mutations. Typhon already uses COW for MVCC data, but B+Tree structural modifications are in-place. Full RCU would require B+Tree redesign — too invasive. |
| **Do nothing** | The current complexity is manageable for the existing codebase but will compound as Query, WAL, Backup, and other systems are built. Each new subsystem that touches pages/chunks re-encounters the same pitfalls. |

### Conditions That Would Invalidate This Recommendation

This design should **NOT** be adopted if any of the following are true:

1. **Typhon needs to support very long-running operations (>1 second) that touch >100 pages within a single scope** — epoch starvation would exhaust the cache. (Mitigation: scope splitting, but if every operation is inherently long, epochs are a poor fit.)

2. **The page cache size cannot be increased beyond 256 slots** and the working set per operation is close to 256 pages — coarser eviction granularity would cause frequent cache exhaustion. (Currently, operations touch 5-20 pages. This is not a concern.)

3. **MVCC snapshot isolation cannot be relied upon for read safety** — the unlatched-read model depends on MVCC. If there are known MVCC bugs that allow readers to see partially-written data, the latch removal would expose them. (MVCC is Typhon's most tested subsystem. This is not a concern.)

4. **The existing ref-counting complexity is considered acceptable long-term** — if the current model is "good enough" and the development cost of migration is not justified by future features. (Given the stated frustration with ChunkAccessor complexity and the number of planned features, this seems unlikely.)

---

## Relationship to Existing Work

| Document | Relationship |
|----------|-------------|
| `claude/research/ChunkAccess.md` | **Preceded by**: That research identified the problems (pin complexity, crash modes). This design proposes a solution. |
| `claude/reference/StackChunkAccessor.md` | **Superseded by**: The scope-based accessor design is replaced by epoch-based protection at the page-cache level, which is more general. |
| `claude/overview/03-storage.md` | **Will be updated**: Page cache section needs epoch-based eviction description. |
| `claude/adr/007-clock-sweep-eviction.md` | **Extends**: Clock-sweep stays, but eviction predicate changes. May warrant a new ADR. |
| `claude/design/errors/` | **Interacts with**: Epoch operations should respect deadline infrastructure from #36. |

---

## Open Questions

1. **Epoch advancement frequency tuning**: One CAS per operation may be too frequent under extreme load (>1M ops/sec). May need batching (advance every N operations).

2. **Long-running query scopes**: A query scanning thousands of entities within one epoch scope holds all touched pages. Need guidance on scope boundaries for query operations.

3. **ChangeSet adaptation**: Current ChangeSet takes `PageAccessor` instances. Need lightweight `AddByMemPageIndex` alternative.

4. **Interaction with future WAL**: The WAL design may have its own page access patterns. Ensure epoch model works for WAL operations.

5. **Benchmark validation**: Performance projections in doc 07 are theoretical. Need real benchmarks to validate (Phase 2 of migration plan).

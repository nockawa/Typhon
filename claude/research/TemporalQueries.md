# Temporal Queries: Exposing MVCC Revision History

**Date:** February 2025
**Status:** In progress
**Outcome:** TBD

---

> 💡 **TL;DR:** Typhon already stores multi-version revision chains for every component — but exposes none of that history to consumers. This document analyzes how to surface temporal queries at near-zero marginal cost. Jump to [Section 4 (Proposed APIs)](#4-proposed-apis) for the concrete API surface, or [Section 8 (Implementation Roadmap)](#8-implementation-roadmap) for phased delivery.

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Internal Machinery Audit](#2-internal-machinery-audit)
3. [Three Temporal Axes](#3-three-temporal-axes)
4. [Proposed APIs](#4-proposed-apis)
5. [Retention Policy Design](#5-retention-policy-design)
6. [Competitive Analysis](#6-competitive-analysis)
7. [Domain Impact Matrix](#7-domain-impact-matrix)
8. [Implementation Roadmap](#8-implementation-roadmap)
9. [Secondary Index Implications](#9-secondary-index-implications)
10. [Performance Considerations](#10-performance-considerations)
11. [Risks and Open Questions](#11-risks-and-open-questions)
12. [Glossary](#12-glossary)
13. [Cross-References](#13-cross-references)

---

## 1. Problem Statement

### The Paradox: Pay the Cost, Expose None of the Value

Typhon's MVCC engine creates a new `CompRevStorageElement` on every component create, update, or delete. These revision chains exist to guarantee snapshot isolation — concurrent readers see a frozen view of the database without blocking writers. This is a core architectural choice (see [ADR-003](../adr/003-mvcc-snapshot-isolation.md)).

The cost is already paid:

- **Storage**: Every mutation allocates a revision element (10 bytes) and a component data chunk.
- **Write path**: Every commit traverses revision chains for conflict detection.
- **GC path**: `CleanUpUnusedEntries` walks and compacts chains when the oldest transaction completes.

Yet today, revision chains are treated as **pure overhead** — internal bookkeeping for concurrency, eagerly garbage-collected as soon as the oldest active transaction no longer needs them. No API exists to:

- Read a component at a past TSN ("what was this entity's state 500 transactions ago?")
- Enumerate the history of mutations on an entity
- Detect which entities changed between two points in time

### Why This Matters

Most databases that offer temporal features **bolt them on after the fact**: PostgreSQL added temporal tables in PG16 (SQL:2011); CockroachDB's `AS OF SYSTEM TIME` reads from a separate MVCC GC window. These systems pay additional storage and complexity costs because their primary storage wasn't designed for retained history.

Typhon's MVCC revision chains already **are** the history. By deferring GC and parameterizing the existing read path, Typhon can offer temporal queries at **near-zero marginal implementation cost** for the basic feature set — the storage format, traversal infrastructure, and concurrency model are already in place.

### Goal

Design a temporal query surface that:

1. Reuses existing revision chain infrastructure (no new storage format)
2. Offers three temporal axes: TSN (primary), wall-clock DateTime (sparse), UoW Epoch (future)
3. Integrates with retention policies to control storage growth
4. Targets high-value domains: gaming (replay), finance (audit trails), digital twins (state history), IoT (sensor history)

---

## 2. Internal Machinery Audit

This section maps the existing code paths that temporal queries would parameterize, extend, or hook into.

### 2.1 CompRevStorageElement — The Revision Record

**Location:** `src/Typhon.Engine/Data/ComponentTable.cs:72-116`

Each revision element is currently **10 bytes**, packed as:

```
┌──────────────────────────────────────────────────────────┐
│  ComponentChunkId   │  _packedTickHigh  │ _packedTickLow │
│      int (4B)       │    uint (4B)      │  ushort (2B)   │
└──────────────────────────────────────────────────────────┘
         4 bytes            4 bytes           2 bytes = 10 bytes total
```

The 6 bytes of `_packedTickHigh` + `_packedTickLow` encode:

- **TSN**: 47 bits (high 32 from `_packedTickHigh`, low 15 from `_packedTickLow`)
- **IsolationFlag**: 1 bit (bit 0 of `_packedTickLow`)

Key properties exposed:

| Property | Type | Description |
|----------|------|-------------|
| `ComponentChunkId` | `int` | Points to the component data chunk (0 = deleted entity) |
| `TSN` | `long` | Transaction Sequence Number that created this revision |
| `IsolationFlag` | `bool` | True while the owning transaction has not yet committed |
| `IsVoid` | `bool` | True when all three fields are zero (rollback marker) |

> **⚠ Key Discrepancy:** `overview/04-data.md` §4.6 and `ADR-023` both document `CompRevStorageElement` as **12 bytes** with a `_uowEpoch: ushort` field. The actual code at `ComponentTable.cs:72-116` is **10 bytes** with no `_uowEpoch` field. This gap is analyzed in [Section 3.3](#33-uow-epoch--0-implemented-struct-size-implications).

### 2.2 CompRevStorageHeader — The Chain Header

**Location:** `src/Typhon.Engine/Data/ComponentTable.cs:30-64`

Each entity's revision chain starts with a **20-byte** header:

```
┌──────────────────────────────────────────────────────────────────────┐
│  NextChunkId   │  Control         │  FirstItemRevision               │
│   int (4B)     │  ACSm (4B)       │     int (4B)                     │
├──────────────────────────────────────────────────────────────────────┤
│  FirstItemIndex │  ItemCount  │  ChainLength  │  LastCommitRevIdx    │
│   short (2B)    │  short (2B) │   short (2B)  │    short (2B)        │
└──────────────────────────────────────────────────────────────────────┘
                                                         Total: 20 bytes
```

Where `Control` is an `AccessControlSmall` (single `int`, 4 bytes) providing reader-writer locking at the chain level.

The header's `FirstItemIndex` and `ItemCount` implement a **circular buffer** — new revisions wrap around, and GC advances `FirstItemIndex`. `ChainLength` tracks how many 64-byte chunks the chain spans (1 = root only, 2+ = root + overflow).

### 2.3 Chunk Capacity

With a 64-byte chunk size:

| Chunk Type | Calculation | Capacity |
|------------|-------------|----------|
| **Root** (header + elements) | `(64 - 20) / 10` = 4 | **4 elements** (4B wasted) |
| **Overflow** (nextPtr + elements) | `(64 - 4) / 10` = 6 | **6 elements** (0B wasted) |

A single-chunk entity can hold 4 revisions. A two-chunk entity holds 10. This is the baseline for retention cost analysis (see [Section 5.4](#54-storage-impact-analysis)).

### 2.4 Traversal Infrastructure

Two traversal primitives already exist:

**RevisionEnumerator** (`src/Typhon.Engine/Data/Revision/RevisionEnumerator.cs`, 147 lines)
A `ref struct` implementing forward iteration through the circular buffer:

- Acquires `AccessControlSmall` lock on the chain header (shared or exclusive)
- Tracks cross-chunk navigation via `StepToChunk()` with circular looping support
- Exposes `Current` (ref to `CompRevStorageElement`), `RevisionIndex`, `IndexInChunk`, `HasLopped`
- Used by both the read path and the GC path

**RevisionWalker** (`src/Typhon.Engine/Data/Revision/RevisionWalker.cs`, 67 lines)
A simpler `ref struct` for chunk-level traversal (no per-element iteration):

- Provides chunk-level `Step()` with loop support
- Exposes `Elements` span for direct element access
- Used for bulk operations where element-by-element enumeration is unnecessary

Both are `IDisposable` and manage `ChunkHandle` lifetimes. Temporal queries can reuse `RevisionEnumerator` directly — it already iterates in TSN order within a chain.

### 2.5 Read Path — The Method to Parameterize

**Location:** `src/Typhon.Engine/Data/Transaction/Transaction.cs:968-1035`

`GetCompRevInfoFromIndex` is the core read path for single-component entities:

```csharp
private bool GetCompRevInfoFromIndex(long pk, ComponentInfoSingle info,
    long tick, out ComponentInfoBase.CompRevInfo compRevInfo)
```

The method:

1. Looks up the entity's revision chain first chunk via the PK index
2. Creates a `RevisionEnumerator` over the chain
3. Walks forward, tracking `curCompRevisionIndex` and `prevCompRevisionIndex`
4. **Stops when `element.TSN > TSN`** — the transaction's own snapshot TSN
5. Skips void entries and isolated (uncommitted) entries

**For temporal queries, the key change is step 4**: instead of comparing against `this.TSN` (the transaction's creation timestamp), compare against a **caller-supplied target TSN**. This is the smallest possible delta for point-in-time reads.

A parallel method exists for multi-value components at `Transaction.cs:1037-1097`.

### 2.6 GC Path — The Retention Hook Point

**Location:** `src/Typhon.Engine/Data/Revision/ComponentRevisionManager.cs:181-316`

`CleanUpUnusedEntries` is the static method that garbage-collects old revisions:

```csharp
internal static bool CleanUpUnusedEntries(
    Transaction.ComponentInfoBase info,
    ref Transaction.ComponentInfoBase.CompRevInfo compRevInfo,
    ref ChunkAccessor compRevTableAccessor,
    long nextMinTSN)          // ← The retention threshold
```

The method:

1. Enumerates all revisions in the chain
2. **Skips (deletes) entries where `element.TSN < nextMinTSN`** — these are invisible to all active transactions
3. Frees the component data chunks of deleted revisions
4. Compacts remaining revisions into a defragmented chain (starting from the first chunk)
5. Returns `true` if the entity is fully deleted (single void entry remaining)

**For retention policies, the hook is the threshold computation at step 2**: instead of using `nextMinTSN` from the `TransactionChain` (which is purely "oldest active transaction"), the threshold can be modified to respect retention windows: keep-all, keep-N, or keep-duration.

### 2.7 TSN Generation

**Location:** `src/Typhon.Engine/Data/Transaction/TransactionChain.cs:122`

```csharp
t.Init(dbe, Interlocked.Increment(ref _nextFreeId));
```

TSNs are monotonically increasing `long` values generated via `Interlocked.Increment`. The field starts at 1 and is checked at `1L << 46` for overflow warning (47-bit address space). At 1 billion TSN/sec, this provides ~1.5 years of headroom before the warning threshold — and ~2.8 years before actual 47-bit overflow.

TSNs are the **native temporal axis** — every revision already stores one, so TSN-based temporal queries add zero storage overhead.

---

## 3. Three Temporal Axes

Temporal queries can be indexed along different "time" dimensions. Typhon's architecture naturally supports three, at different readiness levels.

### 3.1 TSN — Native, Zero-Cost, Already Stored

**Readiness: 100%** — Every `CompRevStorageElement` already stores a 47-bit TSN.

TSNs are:

- **Monotonically increasing**: Total ordering across all transactions
- **Unique per transaction**: Each transaction gets exactly one TSN at creation
- **Globally consistent**: The same TSN identifies the same logical snapshot everywhere
- **Lightweight**: No additional storage, no clock synchronization issues

TSNs are the ideal temporal axis for Typhon because they map directly to the MVCC model. "Show me entity X at TSN 500" means "show me entity X as it was visible to a transaction created at TSN 500" — this is exactly what `GetCompRevInfoFromIndex` already computes (just hardcoded to `this.TSN`).

**Limitations:**

- TSNs are opaque to users — "TSN 14827" has no human-readable meaning
- TSNs are engine-local — they don't survive database recreation or replication
- TSN gaps are possible after rollbacks (the counter increments, but no revision is committed)

### 3.2 Wall-Clock DateTime — Sparse Mapping Table

**Readiness: 0%** — Not stored anywhere today.

Users often want to query "what was the state at 2pm yesterday?" — this requires mapping wall-clock time to TSNs. However, calling `DateTime.UtcNow` on every transaction creation would add ~15-40ns of overhead (rdtsc + calibration on Windows) and storing a full 8-byte DateTime per revision would increase `CompRevStorageElement` to 18 bytes, cutting root chunk capacity from 4 to 2.

**Proposed: Sparse Mapping Table**

Instead of per-revision wall-clock timestamps, maintain a separate **TSN-to-DateTime mapping** sampled at intervals:

```
┌──────────────────────────────────────────────────┐
│              TSN ↔ DateTime Mapping              │
├──────────────────────────────────────────────────┤
│ TSN: 1000    → 2025-02-05T10:00:00.000Z          │
│ TSN: 2000    → 2025-02-05T10:00:00.312Z          │
│ TSN: 3000    → 2025-02-05T10:00:00.587Z          │
│ ...                                              │
└──────────────────────────────────────────────────┘
```

**Sampling strategies:**

| Strategy | Overhead | Accuracy | Storage |
|----------|----------|----------|---------|
| Every N transactions | ~0ns amortized | ±N TSNs | 12B per sample |
| Every T milliseconds | Timer interrupt | ±T ms | 12B per sample |
| On UoW boundaries | ~15ns per UoW | ±UoW duration | 12B per UoW |

**Recommended:** Sample on UoW creation (future) or every N=1000 transactions. At 1M tx/sec this produces ~1000 samples/sec = ~12KB/sec of mapping data. The mapping table can be stored as a dedicated `ChunkBasedSegment` or a simple in-memory sorted array persisted periodically.

**Bidirectional lookup:**

- `DateTime → TSN`: Binary search in the mapping table, returning the closest TSN ≤ target time
- `TSN → DateTime`: Binary search, then linear interpolation between the two surrounding samples

Interpolation accuracy depends on sampling density. At 1000-TSN intervals with ~300µs between samples (1M tx/sec), interpolation error is bounded by the sampling interval.

### 3.3 UoW Epoch — 0% Implemented, Struct Size Implications

**Readiness: 0%** — The `_uowEpoch` field described in `overview/04-data.md` §4.6 does not exist in code.

The UoW Epoch would enable grouping revisions by their Unit of Work — answering questions like "show me all changes from UoW #7" or "roll back to the state before UoW #5 started."

**The documentation/code discrepancy:**

| Source | Element Size | Fields |
|--------|-------------|--------|
| `overview/04-data.md` §4.6 | 12 bytes | `ComponentChunkId(4) + packed TSN+IF(6) + _uowEpoch(2)` |
| `ADR-023` | 12 bytes | Same as above |
| Actual code (`ComponentTable.cs:72-116`) | **10 bytes** | `ComponentChunkId(4) + packed TSN+IF(6)` — no `_uowEpoch` |

Adding `_uowEpoch: ushort` (2 bytes) would increase `CompRevStorageElement` from 10 to 12 bytes:

| Metric | Current (10B) | With Epoch (12B) | Change |
|--------|--------------|-------------------|--------|
| Root chunk capacity | 4 elements | 3 elements | -25% |
| Overflow chunk capacity | 6 elements | 5 elements | -17% |
| Root wasted bytes | 4 | 8 | +4B |
| Overflow wasted bytes | 0 | 0 | 0B |
| Single-chunk max revisions | 4 | 3 | -25% |
| Two-chunk max revisions | 10 | 8 | -20% |

The -25% density reduction in root chunks is significant: entities with exactly 4 active revisions today (common under moderate contention) would spill to a second chunk, doubling their storage footprint. This should not be taken lightly.

**Recommendation:** Defer `_uowEpoch` until UoW is fully implemented and the epoch stamping design (see `overview/04-data.md` §4.5) is finalized. TSN-based temporal queries require zero struct changes.

### 3.4 Axis Relationships and Cross-Axis Queries

The three axes form a hierarchy:

```
UoW Epoch (coarsest)
  └── Contains multiple TSNs (one per transaction in the UoW)
        └── Each TSN maps to a wall-clock DateTime (approximately)
```

Cross-axis query patterns:

| Query Pattern | Axes Involved | Implementation |
|--------------|---------------|----------------|
| "State at TSN X" | TSN only | Parameterize existing read path |
| "State at 2pm yesterday" | DateTime → TSN | Mapping table lookup, then TSN query |
| "All changes in UoW #5" | Epoch → TSNs | Epoch range scan (requires epoch storage) |
| "State before last UoW" | Epoch → TSN → read | Two-level indirection |
| "Diff between 2pm and 3pm" | DateTime → TSN × 2, then delta | Two mapping lookups + change enumeration |

---

## 4. Proposed APIs

All APIs are transaction-scoped (called on a `Transaction` instance) to respect snapshot isolation semantics. Temporal queries are fundamentally reads — they never modify state.

### 4.1 ReadEntityAtVersion — Point-in-Time Read

```csharp
// Read entity state as of a specific TSN
public bool ReadEntityAtVersion<T>(long entityId, long targetTSN, out T component)
    where T : unmanaged;

// Read entity state as of a specific DateTime (requires mapping table)
public bool ReadEntityAtTime<T>(long entityId, DateTime targetTime, out T component)
    where T : unmanaged;
```

**Implementation:** Parameterize `GetCompRevInfoFromIndex` to accept a target TSN instead of using `this.TSN`:

```csharp
// Current (Transaction.cs:968):
private bool GetCompRevInfoFromIndex(long pk, ComponentInfoSingle info,
    long tick, out ComponentInfoBase.CompRevInfo compRevInfo)
{
    // ...
    if (element.TSN > TSN)   // ← hardcoded to transaction's TSN
        break;
}

// Proposed:
private bool GetCompRevInfoFromIndex(long pk, ComponentInfoSingle info,
    long targetTSN, out ComponentInfoBase.CompRevInfo compRevInfo)
{
    // ...
    if (element.TSN > targetTSN)   // ← parameterized
        break;
}
```

The existing `tick` parameter is already declared but unused in the current signature — it shadows `this.TSN`. The change is to actually use it.

**Constraints:**

- `targetTSN` must be ≤ `this.TSN` (cannot see revisions created after the current transaction)
- `targetTSN` must be ≥ the oldest retained revision's TSN (otherwise returns false)
- The returned component is a **copy** (not a mutable reference), since it represents historical state

**Complexity:** O(R) where R is the number of retained revisions in the chain (typically 1-10 for moderate contention).

### 4.2 GetRevisionHistory — Enumeration of All Retained Revisions

```csharp
// Enumerate all retained revisions for an entity
public RevisionHistoryEnumerator<T> GetRevisionHistory<T>(long entityId)
    where T : unmanaged;
```

Yielding:

```csharp
public readonly ref struct ComponentRevision<T> where T : unmanaged
{
    public long TSN { get; }
    public T Component { get; }         // Copy of the component at this revision
    public bool IsDeleted { get; }      // ComponentChunkId == 0
}
```

**Implementation:** Wrap `RevisionEnumerator` with component data resolution:

1. Look up the revision chain first chunk via PK index
2. Create a `RevisionEnumerator` (shared access, forward iteration)
3. For each non-void, committed element:
   a. Read `element.TSN` and `element.ComponentChunkId`
   b. If `element.TSN <= this.TSN` and `!element.IsolationFlag`: yield the revision
   c. Resolve `ComponentChunkId` → component data via `CompContentAccessor`

**Design choice — ref struct enumerator vs. materialized list:**

| Approach | Allocation | Memory | Use Case |
|----------|-----------|--------|----------|
| `ref struct` enumerator | Zero | O(1) | Streaming, most common |
| `List<ComponentRevision<T>>` | Heap alloc | O(R) | LINQ, persistence |
| `Span<ComponentRevision<T>>` | Stack (if bounded) | O(R) | Small chains only |

**Recommended:** Provide the `ref struct` enumerator as the primary API (zero-allocation, Typhon's idiom) and a convenience `ToList()` extension for cases where materialization is needed.

**Complexity:** O(R) time, O(1) memory for the enumerator path.

### 4.3 GetChangedEntities — Change Detection Between TSNs

```csharp
// Find all entities of type T that changed between two TSNs
public ChangedEntityEnumerator<T> GetChangedEntities<T>(long fromTSN, long toTSN)
    where T : unmanaged;
```

This is the most complex API because it requires scanning across entities, not within a single entity's chain.

**Three implementation strategies:**

#### Strategy A: Full PK Index Scan

Walk the entire PK index, and for each entity, check if any revision's TSN falls in `[fromTSN, toTSN]`.

| Metric | Value |
|--------|-------|
| Complexity | O(N × R) where N = entity count, R = avg chain length |
| Allocation | O(1) with streaming |
| Accuracy | Exact |
| Prerequisites | None |

Acceptable for small component tables (< 10K entities) or infrequent queries. Prohibitive at scale.

#### Strategy B: TSN-Indexed Change Log

Maintain a secondary B+Tree index mapping `TSN → List<EntityID>` updated on each commit.

| Metric | Value |
|--------|-------|
| Complexity | O(K × log N) where K = changes in range |
| Allocation | Change log storage overhead |
| Accuracy | Exact |
| Prerequisites | New segment, write-path hook |
| Write overhead | One B+Tree insert per commit (amortized across touched entities) |

The change log is a `VariableSizedBufferSegment` keyed by TSN, storing a packed array of entity IDs. This is the recommended long-term approach for production temporal workloads.

#### Strategy C: Revision Chain Bloom Filters

Per-entity bloom filter of TSNs present in the chain, stored in the `CompRevStorageHeader` (currently 4 bytes of padding exist in the root chunk).

| Metric | Value |
|--------|-------|
| Complexity | O(N) scan, but O(1) rejection per entity |
| Allocation | 0 additional (uses existing padding) |
| Accuracy | Probabilistic (false positives) |
| Prerequisites | Header modification |

A 32-bit bloom filter with 3 hash functions provides ~3% false positive rate at 4 elements. Useful as a pre-filter for Strategy A but insufficient alone.

**Recommendation:** Phase 1 delivers Strategy A (simple, correct). Phase 5 adds Strategy B (scalable). Strategy C is optional optimization if Strategy A proves too slow before Strategy B is ready.

### 4.4 TSN-to-DateTime Mapping

```csharp
// Map between temporal axes
public DateTime TSNToDateTime(long tsn);
public long DateTimeToTSN(DateTime targetTime);
```

**Implementation:**

The mapping table is a sorted array of `(long TSN, DateTime Time)` pairs stored in a dedicated segment. Queries use binary search with linear interpolation:

```
Given: samples [(TSN_a, T_a), (TSN_b, T_b)] where TSN_a ≤ target ≤ TSN_b
Interpolated time = T_a + (T_b - T_a) × (target - TSN_a) / (TSN_b - TSN_a)
```

The reverse mapping (DateTime → TSN) uses binary search on the DateTime column and returns the closest TSN ≤ target time. Since TSNs and DateTimes are both monotonically increasing, the mapping is order-preserving and binary search is valid.

**Edge cases:**

- TSN before first sample: return `DateTime.MinValue` or the first sample's time
- TSN after last sample: return the last sample's time (cannot extrapolate into the future)
- TSN gap (rollback): interpolation still valid because TSNs are monotonic even with gaps

---

## 5. Retention Policy Design

Without retention policies, temporal queries require keeping all revisions forever — an untenable storage proposition. Retention policies control how aggressively `CleanUpUnusedEntries` prunes old revisions.

### 5.1 Three Retention Modes

| Mode | Semantics | Storage Impact | Use Case |
|------|-----------|---------------|----------|
| **KeepAll** | Never GC revisions (append-only) | Unbounded growth | Audit, compliance |
| **KeepDuration** | Keep revisions younger than a time window | Bounded by write rate × window | Replay, debugging |
| **KeepCount** | Keep the N most recent revisions per entity | Bounded: N × element size × entity count | Change tracking |

These modes compose: `KeepDuration(1 hour) AND KeepCount(100)` means "keep at least the last 100 revisions, but always keep revisions less than 1 hour old."

### 5.2 Configuration API

**Attribute-based (per component type):**

```csharp
[Component]
[RetentionPolicy(Mode = RetentionMode.KeepDuration, Duration = "01:00:00")]
public struct PlayerPosition
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

[Component]
[RetentionPolicy(Mode = RetentionMode.KeepAll)]  // Audit trail
public struct TradeRecord
{
    [Field] public long TradeId;
    [Field] public int Amount;
}
```

**Programmatic (runtime configuration):**

```csharp
dbe.ConfigureRetention<PlayerPosition>(new RetentionPolicy
{
    Mode = RetentionMode.KeepCount,
    MaxRevisions = 50
});
```

Runtime configuration overrides attribute-based defaults. This allows different deployments (dev vs. production) to use different retention windows without recompiling.

### 5.3 Hook into CleanUpUnusedEntries

The retention hook modifies the threshold passed to `CleanUpUnusedEntries` at `ComponentRevisionManager.cs:181-316`.

Currently, the threshold is `nextMinTSN` — the TSN of the oldest active transaction:

```csharp
// Current: skip entries older than the oldest active transaction
if ((--maxSkipCount > 0) && (enumerator.Current.TSN < nextMinTSN))
```

With retention policies, the threshold becomes the **minimum of** (oldest active transaction TSN) and (retention boundary TSN):

```csharp
// Proposed: retention-aware threshold
var retentionTSN = ComputeRetentionThreshold(policy, currentTSN);
var effectiveMinTSN = Math.Min(nextMinTSN, retentionTSN);

if ((--maxSkipCount > 0) && (enumerator.Current.TSN < effectiveMinTSN))
```

Where `ComputeRetentionThreshold`:

| Mode | Computation |
|------|------------|
| KeepAll | Returns `0` (never GC anything) |
| KeepDuration | Map `DateTime.UtcNow - Duration` to TSN via mapping table |
| KeepCount | Walk chain backward, find TSN of the Nth-from-newest revision |

**Important interaction:** The deferred cleanup design (`design/CompRevDeferredCleanup.md`) already addresses the problem of long-running transactions preventing GC of unrelated entities. Retention policies add an orthogonal constraint: even when no long-running transactions block GC, the retention window may require keeping old revisions.

### 5.4 Storage Impact Analysis

**Per-entity storage cost of retention:**

| Retention | Root Chunks | Overflow Chunks | Total Bytes | vs. No Retention |
|-----------|-------------|-----------------|-------------|------------------|
| 1 revision (default) | 1 | 0 | 64 | Baseline |
| 4 revisions | 1 | 0 | 64 | +0% |
| 10 revisions | 1 | 1 | 128 | +100% |
| 22 revisions | 1 | 3 | 256 | +300% |
| 100 revisions | 1 | 16 | 1,088 | +1,600% |
| KeepAll (unbounded) | 1 | N/6 | 64 + ⌈N/6⌉×64 | Unbounded |

**At scale:**

| Scenario | Entities | Retention | Storage Overhead |
|----------|---------|-----------|-----------------|
| Game server: position updates | 10K | 50 revisions | ~5.4 MB |
| Finance: trade records | 100K | KeepAll, 1K avg | ~660 MB |
| IoT: sensor readings | 1M | 1 hour @ 1/sec | ~6.8 GB |

The IoT scenario shows why KeepDuration must be bounded. At high write rates, even moderate duration windows produce significant storage.

### 5.5 Interaction with Deferred Cleanup

The deferred cleanup design (`design/CompRevDeferredCleanup.md`) introduces a cleanup queue to handle entities that accumulate revisions due to long-running transactions. Retention policies interact as follows:

| Scenario | Deferred Cleanup | Retention Policy | Result |
|----------|-----------------|-----------------|--------|
| Long-running TX blocks GC | Queues entity for later cleanup | N/A | Cleaned when TX completes |
| Long-running TX + retention | Queues entity | Keeps N/duration revisions | Retention overrides, less is GC'd |
| No long-running TX + retention | Normal commit-time GC | Keeps N/duration revisions | Retention is the binding constraint |
| KeepAll + long-running TX | Queues entity | Never GC | Queue grows indefinitely (warning needed) |

**Key insight:** Retention policies should be evaluated **at GC time**, not at write time. The `CleanUpUnusedEntries` method already runs at the right moment; it just needs the additional retention check.

---

## 6. Competitive Analysis

### 6.1 Datomic — Immutable History as First-Class Citizen

**Architecture:** Datomic stores all data as immutable facts (datoms) in an append-only log. Every assertion and retraction is permanently recorded. The database value at any point in time is the accumulation of all facts up to that point.

**Temporal APIs:**

| API | Description | Analog in Typhon |
|-----|-------------|-----------------|
| `db.asOf(t)` | Returns a database value as of time t | `ReadEntityAtVersion(pk, tsn)` |
| `db.since(t)` | Returns only facts added after time t | `GetChangedEntities(tsn, currentTSN)` |
| `db.history()` | Returns all facts ever asserted/retracted | `GetRevisionHistory(pk)` |
| `Datalog` queries on history | Full query language over temporal data | No query engine equivalent yet |

**Key differences from Typhon:**

- Datomic's immutability is **unconditional** — there is no GC. Typhon's MVCC chains are designed for GC.
- Datomic uses transaction time (wall-clock) as the primary axis. Typhon uses TSN (logical).
- Datomic's `since()` is efficient because the transaction log is a sequential structure. Typhon's equivalent requires either a PK scan or a change log index.
- Datomic stores facts at the attribute (field) level. Typhon stores revisions at the component (struct) level — coarser granularity.

**Lesson for Typhon:** Datomic proves that temporal queries over immutable history are a compelling feature. Its `asOf` API maps directly to Typhon's `ReadEntityAtVersion`. The `since` API highlights the need for a change log index (Strategy B in Section 4.3).

### 6.2 CockroachDB — AS OF SYSTEM TIME

**Architecture:** CockroachDB uses MVCC internally for distributed transaction isolation. The `AS OF SYSTEM TIME` clause exposes historical reads by parameterizing the MVCC read timestamp — exactly what we propose for Typhon.

**Temporal APIs:**

```sql
-- Point-in-time read
SELECT * FROM accounts AS OF SYSTEM TIME '2024-01-15 10:30:00';
SELECT * FROM accounts AS OF SYSTEM TIME '-1h';    -- Relative time
SELECT * FROM accounts AS OF SYSTEM TIME follower_read_timestamp();

-- GC window
ALTER TABLE accounts CONFIGURE ZONE USING gc.ttlseconds = 86400; -- 24h retention
```

**Key differences from Typhon:**

- CockroachDB uses wall-clock timestamps (HLC — Hybrid Logical Clock) as the primary axis. Typhon uses logical TSNs.
- CockroachDB's GC is table-level (`gc.ttlseconds`). Typhon's proposed retention is per-component-type.
- CockroachDB cannot enumerate history — it only supports point-in-time reads. Typhon proposes full revision enumeration.
- CockroachDB's MVCC GC runs as a background job. Typhon's runs synchronously at commit time (with the deferred cleanup extension).

**Lesson for Typhon:** CockroachDB validates the "parameterize the MVCC read path" approach. Its `gc.ttlseconds` maps to Typhon's `KeepDuration` retention mode. CockroachDB's lack of history enumeration is a gap Typhon can fill.

### 6.3 EventStoreDB — Event Sourcing and Stream History

**Architecture:** EventStoreDB is purpose-built for event sourcing. Every state change is stored as an immutable event in a stream. The current state is derived by replaying events.

**Temporal APIs:**

| API | Description | Analog in Typhon |
|-----|-------------|-----------------|
| `ReadStreamAsync(stream, start, count)` | Read events from a position | `GetRevisionHistory(pk)` with offset |
| `ReadStreamAsync(stream, End, backward)` | Read events backward from head | Reverse enumerator (not proposed) |
| `SubscribeToStream(stream, start)` | Live subscription from a position | Not proposed (push model) |
| `$all` stream | Global ordered stream of all events | `GetChangedEntities` across all types |

**Key differences from Typhon:**

- EventStoreDB stores **events** (what happened). Typhon stores **snapshots** (what the state is). Events enable richer temporal reasoning ("why did this change?"), snapshots enable faster reads ("what is the current state?").
- EventStoreDB's stream position is analogous to TSN. Both are monotonic, opaque integers.
- EventStoreDB natively supports subscriptions (push). Typhon's proposed API is pull-only.
- EventStoreDB's retention is per-stream with `$maxAge` and `$maxCount` — similar to Typhon's per-component retention.

**Lesson for Typhon:** EventStoreDB's `$maxAge` and `$maxCount` retention modes map directly to `KeepDuration` and `KeepCount`. The concept of stream position as a temporal coordinate validates TSN-based queries. EventStoreDB shows that snapshot-based systems (like Typhon) can still offer rich temporal features even without storing full event details.

### 6.4 PostgreSQL — SQL:2011 Temporal Tables

**Architecture:** PostgreSQL (v16+) supports SQL:2011 temporal tables with `SYSTEM_TIME` versioning. The database maintains a separate history table alongside the main table, automatically copying rows on UPDATE/DELETE.

**Temporal APIs:**

```sql
-- Create temporal table
CREATE TABLE accounts (
    id INT PRIMARY KEY,
    balance DECIMAL,
    sys_period TSTZRANGE NOT NULL DEFAULT tstzrange(now(), NULL)
);
CREATE TABLE accounts_history (LIKE accounts);

-- Temporal query
SELECT * FROM accounts FOR SYSTEM_TIME AS OF '2024-01-15 10:30:00';
SELECT * FROM accounts FOR SYSTEM_TIME FROM '2024-01-15' TO '2024-01-16';
SELECT * FROM accounts FOR SYSTEM_TIME BETWEEN '2024-01-15' AND '2024-01-16';
```

**Key differences from Typhon:**

- PostgreSQL uses **separate history tables** — INSERT cost on every UPDATE/DELETE to copy to history. Typhon's revision chains are inline.
- PostgreSQL's temporal support is bolt-on — it requires explicit schema setup. Typhon's would be built-in.
- PostgreSQL uses wall-clock ranges (`TSTZRANGE`). Typhon uses discrete TSNs.
- PostgreSQL supports temporal joins and temporal aggregations via SQL. Typhon has no query language.
- PostgreSQL's history tables can be indexed independently. Typhon's revision chains share the PK index.

**Lesson for Typhon:** PostgreSQL shows the cost of bolt-on temporal support: separate tables, triggers, increased write amplification. Typhon's inline revision chains avoid all of this. However, PostgreSQL's temporal SQL syntax (`FOR SYSTEM_TIME AS OF/FROM/BETWEEN`) provides a useful API design reference.

### 6.5 Comparative Summary

| Feature | Datomic | CockroachDB | EventStoreDB | PostgreSQL | **Typhon (Proposed)** |
|---------|---------|-------------|--------------|------------|----------------------|
| **History model** | Immutable facts | MVCC GC window | Immutable events | Separate tables | MVCC chains + retention |
| **Primary axis** | Transaction time | HLC wall-clock | Stream position | Wall-clock range | TSN (logical) |
| **Point-in-time read** | `asOf(t)` | `AS OF SYSTEM TIME` | `ReadStream(pos)` | `FOR SYSTEM_TIME AS OF` | `ReadEntityAtVersion` |
| **History enumeration** | `history()` | No | `ReadStream(start, count)` | Range query on history | `GetRevisionHistory` |
| **Change detection** | `since(t)` | No | `SubscribeToStream` | Range query | `GetChangedEntities` |
| **Retention** | None (immutable) | `gc.ttlseconds` | `$maxAge/$maxCount` | Manual purge | KeepAll/Duration/Count |
| **Write overhead** | Append-only | MVCC + GC | Append-only | Copy to history table | **Near-zero** (already paid) |
| **Storage overhead** | Unbounded | GC window × write rate | Per-stream retention | History table size | Retention × chain size |
| **Granularity** | Attribute (field) | Row | Event (custom) | Row | Component (struct) |
| **Built-in vs bolt-on** | Built-in | Built-in | Built-in | Bolt-on | **Built-in** |

**Typhon's competitive edge:** Near-zero marginal write-path cost (revisions already exist), built-in rather than bolt-on, and flexible per-component retention. The main gap is the lack of a query language for complex temporal reasoning — but for the embedded engine use case, programmatic APIs are the right abstraction.

---

## 7. Domain Impact Matrix

### API × Domain Matrix

| API | Gaming | Finance | Digital Twins | Robotics/IoT |
|-----|--------|---------|---------------|--------------|
| `ReadEntityAtVersion` | Replay frames, spectator rewind | Audit: "balance at close-of-day" | State history visualization | Sensor state at alarm time |
| `GetRevisionHistory` | Kill-cam: entity path over time | Trade journal, compliance export | Maintenance history per device | Full trajectory reconstruction |
| `GetChangedEntities` | "What moved since last render?" | Anomaly: "which accounts changed?" | Delta sync to visualization | "Which sensors reported in last 5s?" |
| `TSN-to-DateTime` | Map game ticks to wall time | Regulatory timestamps | Correlate sim-time to real-time | Sensor reading timestamps |

### Domain Deep Dives

**Gaming (primary target):**
- **Server-side replay**: Record TSN ranges per match; replay by stepping `ReadEntityAtVersion` through retained TSNs. Zero-cost recording (revisions already stored), cheap playback.
- **Spectator mode**: Second client reads at a delayed TSN — `ReadEntityAtVersion(pk, currentTSN - delay)`. Works out of the box.
- **Anti-cheat forensics**: `GetRevisionHistory` on flagged player entities to reconstruct movement patterns. `GetChangedEntities` to detect which entities were suspiciously modified.
- **Rollback on disconnect**: If a player disconnects, roll back their entity to the last known-good TSN.

**Finance (high-value):**
- **Audit trails**: `KeepAll` retention on financial components. `GetRevisionHistory` produces a complete audit log. Regulators can query "account balance at 3:47pm" via `ReadEntityAtTime`.
- **Regulatory compliance**: MiFID II, SOX require point-in-time state reconstruction. `ReadEntityAtTime` + TSN-to-DateTime mapping satisfies this directly.
- **Reconciliation**: `GetChangedEntities(fromTSN, toTSN)` per trading day boundaries provides daily change sets for reconciliation.

**Digital Twins:**
- **State history**: `GetRevisionHistory` on device components shows how a simulated system evolved.
- **Delta synchronization**: `GetChangedEntities` provides efficient delta updates to visualization clients.
- **What-if analysis**: Fork at a historical TSN, apply different inputs, compare outcomes.

**Robotics/IoT:**
- **Sensor history**: High-frequency sensor components with `KeepDuration(1 hour)` retention. `GetRevisionHistory` for trend analysis.
- **Alarm forensics**: When an alarm triggers, `ReadEntityAtVersion` at the alarm TSN shows the exact sensor state.
- **Trajectory reconstruction**: `GetRevisionHistory` on position components produces full motion paths.

---

## 8. Implementation Roadmap

### Phase 1: TSN-Based Point-in-Time Read — Size S

**Goal:** `ReadEntityAtVersion<T>(pk, targetTSN)`

**Changes:**
1. Modify `GetCompRevInfoFromIndex` to use the `targetTSN` parameter instead of `this.TSN`
2. Add public `ReadEntityAtVersion<T>(long entityId, long targetTSN, out T component)` method on `Transaction`
3. Add validation: `targetTSN <= this.TSN`

**Files touched:**
- `src/Typhon.Engine/Data/Transaction/Transaction.cs` — Parameterize read path, add public API
- `test/Typhon.Engine.Tests/` — New test file for temporal read scenarios

**Complexity:** Minimal. The core logic already exists; this phase just exposes it.

**Dependencies:** None

### Phase 2: Retention Policy Infrastructure — Size M

**Goal:** Configurable per-component retention that modifies GC behavior.

**Changes:**
1. Define `RetentionPolicy` class and `RetentionMode` enum
2. Add `[RetentionPolicy]` attribute
3. Modify `CleanUpUnusedEntries` threshold computation
4. Add `ConfigureRetention<T>()` API on `DatabaseEngine`
5. Store retention configuration in system schema

**Files touched:**
- `src/Typhon.Engine/Data/Revision/ComponentRevisionManager.cs` — GC threshold modification
- `src/Typhon.Engine/Database Engine/ComponentTable.cs` — Store retention policy
- `src/Typhon.Engine/Database Engine/DatabaseEngine.cs` — Configuration API
- `src/Typhon.Engine/Database Engine/Schema/` — New attribute, policy classes
- `test/Typhon.Engine.Tests/` — Retention behavior tests

**Complexity:** Moderate. Requires careful interaction with deferred cleanup.

**Dependencies:** None (can be done in parallel with Phase 1)

### Phase 3: Revision History Enumeration — Size S

**Goal:** `GetRevisionHistory<T>(pk)` returning a ref struct enumerator.

**Changes:**
1. Create `RevisionHistoryEnumerator<T>` ref struct wrapping `RevisionEnumerator` with component data resolution
2. Create `ComponentRevision<T>` readonly ref struct
3. Add public API on `Transaction`

**Files touched:**
- `src/Typhon.Engine/Data/Revision/` — New enumerator type
- `src/Typhon.Engine/Data/Transaction/Transaction.cs` — Public API
- `test/Typhon.Engine.Tests/` — History enumeration tests

**Complexity:** Low. Composing existing `RevisionEnumerator` with `CompContentAccessor`.

**Dependencies:** Phase 1 (reuses parameterized read logic)

### Phase 4: TSN-to-DateTime Mapping — Size M

**Goal:** Bidirectional TSN ↔ DateTime mapping with configurable sampling.

**Changes:**
1. Implement `TSNDateTimeMapping` class with sorted array storage
2. Add sampling hook in `TransactionChain.CreateTransaction()`
3. Implement `TSNToDateTime()` and `DateTimeToTSN()` with binary search + interpolation
4. Add `ReadEntityAtTime<T>()` API that composes mapping + point-in-time read
5. Persist mapping table in a dedicated segment

**Files touched:**
- `src/Typhon.Engine/Data/Transaction/TransactionChain.cs` — Sampling hook
- `src/Typhon.Engine/Data/` — New mapping class
- `src/Typhon.Engine/Data/Transaction/Transaction.cs` — Time-based API
- `src/Typhon.Engine/Persistence Layer/` — Mapping segment (if persisted)
- `test/Typhon.Engine.Tests/` — Mapping accuracy tests

**Complexity:** Moderate. Sampling strategy and persistence require design decisions.

**Dependencies:** Phase 1 (uses `ReadEntityAtVersion` internally)

### Phase 5: Change Set / Delta Queries — Size L

**Goal:** `GetChangedEntities<T>(fromTSN, toTSN)` with efficient change log index.

**Changes:**
1. Phase 5a: Implement Strategy A (PK scan) as the simple baseline
2. Phase 5b: Implement Strategy B (TSN-indexed change log)
   - New `VariableSizedBufferSegment` for the change log
   - Write-path hook in commit to record changed entity IDs
   - B+Tree keyed by TSN for efficient range lookups
3. Create `ChangedEntityEnumerator<T>` ref struct

**Files touched:**
- `src/Typhon.Engine/Data/Transaction/Transaction.cs` — Commit hook, public API
- `src/Typhon.Engine/Database Engine/ComponentTable.cs` — Change log segment
- `src/Typhon.Engine/Data/` — New enumerator type
- `test/Typhon.Engine.Tests/` — Change detection tests at scale

**Complexity:** High. Change log introduces a new write-path cost and a new segment type.

**Dependencies:** Phase 2 (retention determines what's in the change log)

### Phase 6: UoW Epoch Integration — Size XL, Future

**Goal:** Add `_uowEpoch` to `CompRevStorageElement` and enable epoch-based queries.

**Changes:**
1. Add `_uowEpoch: ushort` field to `CompRevStorageElement` (10 → 12 bytes)
2. Update all chunk capacity calculations
3. Add epoch-stamping in the commit path
4. Add epoch-based query APIs
5. Handle migration from 10-byte to 12-byte format (existing databases)

**Files touched:** Many — struct size change ripples through the entire revision subsystem.

**Complexity:** Very high. Breaking struct size change affects storage density, existing data migration, and all revision-related code.

**Dependencies:** UoW implementation (not yet started), Phases 1-5

**Recommendation:** Defer until UoW is fully designed and implemented. TSN-based temporal queries cover the majority of use cases without any struct changes.

### Summary Timeline

```
Phase 1 (S) ──────────┐
                      ├──► Phase 3 (S) ──► Phase 5a (M)
Phase 2 (M) ──────────┘                        │
                                               ▼
Phase 4 (M) ────────────────────────────► Phase 5b (L)
                                               │
                                               ▼
                                          Phase 6 (XL, future)
```

Phases 1 and 2 can proceed in parallel. Phase 3 depends on Phase 1. Phase 5 depends on Phases 2 and 3. Phase 6 is independent and deferred.

---

## 9. Secondary Index Implications

### Current State: Indexes Are "Current State Only"

Typhon's B+Tree indexes (PK and secondary) reflect only the **latest committed state** of each entity. When a component's indexed field changes (e.g., a player's `Score` increases), the old index entry is removed and the new one is inserted at commit time.

This means:

- Indexes cannot answer "which entities had Score > 100 at TSN 500?" — only "which entities have Score > 100 now?"
- Temporal queries on indexed fields require a full revision chain walk, not an index lookup
- The PK index maps `EntityID → RevisionChainFirstChunkId`, which is stable across revisions (the chain grows, but the first chunk ID is permanent)

### Temporal Index Strategies

The `research/IndexingAndMVCC.md` document analyzes this in depth. Key strategies:

| Strategy | Description | Cost | Benefit |
|----------|-------------|------|---------|
| **No temporal index** | PK temporal queries only | Zero | Simple, correct |
| **TSN-range index** | B+Tree: `(fieldValue, TSN) → EntityID` | O(log N) per write | Range queries on historical field values |
| **Versioned index** | Separate B+Tree per "epoch" | O(full index) per epoch | Point-in-time index lookups |
| **Change log index** | TSN → changed entities (Strategy B from §4.3) | O(1) per write | Efficient delta queries |

### V1 Recommendation: PK-Only Temporal Queries

For the initial temporal query feature set (Phases 1-5), restrict temporal queries to **primary-key lookups only**:

- `ReadEntityAtVersion(entityId, tsn)` — uses PK index + chain walk (already O(R))
- `GetRevisionHistory(entityId)` — uses PK index + chain walk
- `GetChangedEntities(fromTSN, toTSN)` — uses PK scan or change log, not secondary indexes

This avoids the complexity and storage cost of temporal secondary indexes. Users who need "which entities had field X = Y at time T" can use `GetChangedEntities` to find candidates and then filter with `ReadEntityAtVersion`.

Temporal secondary indexes can be explored as a future enhancement if workload analysis shows that PK-only queries are insufficient.

---

## 10. Performance Considerations

### 10.1 Revision Chain Walk Cost

The fundamental cost of any temporal query is walking the revision chain from the oldest retained revision to the target TSN. This is sequential within a chain, but crosses chunk boundaries.

| Chain Length (revisions) | Chunks Traversed | Cache Lines Touched | Estimated Latency |
|--------------------------|-----------------|--------------------|--------------------|
| 1-4 | 1 (root only) | 1 | ~50ns |
| 5-10 | 2 | 2 | ~100-150ns |
| 11-22 | 2-4 | 2-4 | ~150-300ns |
| 100+ | 1 + ⌈(N-4)/6⌉ | Same | ~500ns-2µs |

Each chunk boundary crossing involves a `ChunkAccessor.GetChunkHandle()` call, which checks the page cache (hot path: ~20ns if cached, cold path: ~5µs if page fault).

**Optimization opportunity:** For `ReadEntityAtVersion` with a recent target TSN, walk the chain **backward** from the newest revision. This requires knowing the chain tail position (available in `CompRevStorageHeader.ItemCount + FirstItemIndex`). However, the current `RevisionEnumerator` only supports forward iteration. Backward iteration would require a new enumerator variant.

### 10.2 Memory Impact of Retention

Retention increases the steady-state memory pressure:

| Retention Mode | Steady-State Memory per Entity | Notes |
|----------------|-------------------------------|-------|
| Default (no retention) | 64 bytes (1 root chunk) | Current behavior |
| KeepCount(10) | 128 bytes (root + 1 overflow) | 100% increase |
| KeepCount(100) | 1,088 bytes (root + 16 overflow) | 1,600% increase |
| KeepAll @ 1K revisions | ~10.7 KB | Audit/compliance use cases |

The page cache impact is the real concern: retained revisions occupy page cache pages. With 8KB pages and 128 revision chunks per page, a 10K-entity database with KeepCount(100) requires ~85 pages of revision data — about 680KB of page cache.

### 10.3 Write-Path Overhead per Feature

| Feature | Additional Write-Path Cost | When Incurred |
|---------|---------------------------|---------------|
| `ReadEntityAtVersion` | **None** — read-only API | Never |
| Retention policy | ~10ns threshold check in GC | Every GC invocation |
| `GetRevisionHistory` | **None** — read-only API | Never |
| TSN-to-DateTime mapping | ~15-40ns `DateTime.UtcNow` per sample | Every Nth transaction |
| Change log (Strategy B) | ~200-500ns B+Tree insert per commit | Every commit |

The change log (Phase 5b) is the only feature with meaningful write-path cost. All other features are either read-only or amortized to near-zero.

### 10.4 Concurrency Implications

Temporal reads acquire **shared** access on the `AccessControlSmall` in `CompRevStorageHeader` — same as normal reads. They do not block writers or other readers.

The retention policy modifies GC behavior, which already runs under **exclusive** access. No new contention points are introduced.

The change log (Strategy B) introduces a new write during commit. This can be serialized via the existing commit path (which is already single-threaded per transaction) but adds contention on the change log segment's page-level locks. At very high commit rates (> 100K tx/sec), this may become a bottleneck. Mitigation: batch change log writes per commit (one entry per commit, not per entity).

---

## 11. Risks and Open Questions

### 11.1 Risks

**R1: Retention + Long-Running Transactions = Unbounded Growth**

If a component has `KeepAll` retention and a long-running transaction prevents GC, revision chains grow without bound. The deferred cleanup design helps (cleanup runs when the long transaction completes), but `KeepAll` means cleanup never removes anything.

**Mitigation:** Monitor revision chain length. Emit a warning when any chain exceeds a configurable threshold (e.g., 1000 revisions). Consider a hard cap even for `KeepAll` mode.

**R2: CompRevStorageElement Size Increase for UoW Epoch**

Adding `_uowEpoch` (Phase 6) changes the struct from 10 to 12 bytes, reducing root chunk density by 25%. This affects all entities, not just those using temporal queries. Existing databases would require migration.

**Mitigation:** Defer Phase 6 until UoW is fully implemented. Consider a format version flag in the file header to support gradual migration.

**R3: Clock Skew Affecting DateTime Mapping**

TSN-to-DateTime mapping assumes monotonic wall-clock time. Clock adjustments (NTP step, DST transitions, manual changes) break the monotonicity assumption, producing incorrect interpolation.

**Mitigation:** Use `DateTime.UtcNow` (no DST). Detect backward time jumps (new sample time < previous sample time) and insert a discontinuity marker. Document that UTC is required.

**R4: Change Log Scalability**

The change log (Strategy B) grows linearly with commit count. At 1M commits/sec with an average of 5 entities per commit, the log produces ~60 MB/sec of change log data. Without compaction, this is unsustainable.

**Mitigation:** Compact the change log using the same retention window as revision chains. Change log entries older than the retention boundary can be safely deleted, since the revisions they reference are also gone.

**R5: API Surface Explosion**

Each temporal axis (TSN, DateTime, UoW Epoch) multiplied by each query type (point-read, history, changes) produces a combinatorial API surface. Over-engineering this risks complexity without proportional value.

**Mitigation:** V1 delivers TSN-only APIs (3 methods). DateTime mapping adds 2 convenience methods. UoW Epoch adds 2 more. Keep the API minimal and extend based on real usage.

### 11.2 Open Design Questions

**Q1:** Should `ReadEntityAtVersion` return a nullable component (indicating "entity didn't exist at this TSN") or throw? Current `ReadEntity` returns `bool` — follow the same pattern?

**Recommendation:** Follow existing pattern: `bool ReadEntityAtVersion<T>(long pk, long tsn, out T component)` returns `false` if the entity didn't exist at that TSN or is outside the retention window.

**Q2:** Should `GetRevisionHistory` yield revisions in newest-first or oldest-first order?

**Recommendation:** Oldest-first (matches `RevisionEnumerator`'s natural iteration order). Provide a `reverse: bool` parameter for the opposite. Note that reverse iteration requires allocating or stack-allocating a buffer to reverse the chain.

**Q3:** How should temporal queries interact with the transaction cache? If the current transaction has uncommitted changes to an entity, does `ReadEntityAtVersion` see them?

**Recommendation:** No — temporal reads should bypass the transaction cache and read directly from the committed revision chain. The caller is explicitly asking for historical state, not in-flight state. `ReadEntity` (current-state read) continues to use the cache.

**Q4:** Should the TSN-to-DateTime mapping be per-database or global (across multiple DatabaseEngine instances)?

**Recommendation:** Per-database. Each `DatabaseEngine` has its own `TransactionChain` with independent TSN generation. Mapping tables must be co-located with the engine they describe.

**Q5:** What happens when a temporal query targets a TSN that was rolled back (no committed revision at that TSN)?

**Recommendation:** Treat rollback gaps transparently. The chain walk finds the most recent committed revision with `TSN ≤ targetTSN`. Since rolled-back revisions have `TSN = 0` or `IsVoid = true`, they are naturally skipped by the existing traversal logic. The result is the same as if the rolled-back transaction never existed.

---

## 12. Glossary

| Term | Definition |
|------|-----------|
| **TSN** | Transaction Sequence Number — a monotonically increasing `long` assigned to each transaction at creation. The native temporal axis. |
| **CompRevStorageElement** | The 10-byte struct storing one revision in a chain: `ComponentChunkId + packed(TSN, IsolationFlag)`. |
| **CompRevStorageHeader** | The 20-byte header of a revision chain chunk: manages circular buffer state and chain linking. |
| **Revision chain** | A linked list of 64-byte chunks storing `CompRevStorageElement` entries as a circular buffer. One chain per entity-component pair. |
| **Retention policy** | Configuration that controls how long old revisions are preserved before GC can reclaim them. |
| **Change log** | (Proposed) A TSN-indexed structure recording which entities were modified at each TSN. Enables efficient `GetChangedEntities` queries. |
| **Snapshot isolation** | MVCC guarantee: each transaction sees the database as of its creation TSN. Temporal queries generalize this to arbitrary historical TSNs. |
| **UoW Epoch** | (Future) A 16-bit identifier stamped on revisions to group them by Unit of Work. |
| **Sparse mapping** | A sampled (not per-revision) table mapping TSNs to wall-clock DateTimes, enabling time-based queries with bounded storage cost. |

---

## 13. Cross-References

| Document | Relevance |
|----------|-----------|
| [ADR-003: MVCC Snapshot Isolation](../adr/003-mvcc-snapshot-isolation.md) | Foundational MVCC design that temporal queries extend |
| [ADR-023: Circular Buffer Revision Chains](../adr/023-circular-buffer-revision-chains.md) | Revision chain storage format (documents 12-byte layout not yet in code) |
| [Overview 04: Data Engine §4.5-4.6](../overview/04-data.md) | ComponentTable structure and revision chain architecture |
| [Design: CompRevDeferredCleanup](../design/CompRevDeferredCleanup.md) | Deferred GC strategy — retention policies interact with this |
| [Research: IndexingAndMVCC](../research/IndexingAndMVCC.md) | Index + MVCC interaction, temporal index strategies |
| [ADR-027: Even-Sized Structs](../adr/027-even-sized-hot-path-structs.md) | Rationale for keeping hot-path structs at even byte boundaries |

# Versioned Secondary Indexes

**Date:** February 2025
**Status:** Draft
**Dependencies:** [ADR-003: MVCC Snapshot Isolation](../adr/003-mvcc-snapshot-isolation.md), [CompRevDeferredCleanup](CompRevDeferredCleanup.md)
**Downstream:** [Temporal Queries](../research/TemporalQueries.md)
**Prior Research:** [Indexing and MVCC Analysis](../research/IndexingAndMVCC.md)
**Related ADRs:** [ADR-021: Specialized B+Tree Variants](../adr/021-specialized-btree-variants.md), [ADR-022: 64-Byte Cache-Aligned Nodes](../adr/022-64byte-cache-aligned-nodes.md)

---

> 💡 **TL;DR:** Secondary indexes are destructively updated today, breaking MVCC snapshot isolation. This design replaces plain `int chainId` VSBS entries with a two-section buffer layout (HEAD for current state, TAIL for versioned history) while keeping B+Tree keys unchanged. Jump to [§5 (VSBS Design)](#5-versioned-vsbs-design) for the buffer layout, [§7 (Write Path)](#7-write-path-changes) for the new `UpdateIndices` logic, or [§18 (Phases)](#18-implementation-phases) for the rollout plan.

---

## Table of Contents

1. [Header + Metadata](#versioned-secondary-indexes)
2. [TL;DR](#tldr)
3. [Problem Statement](#3-problem-statement)
4. [Current Architecture Audit](#4-current-architecture-audit)
5. [Versioned VSBS Design](#5-versioned-vsbs-design)
6. [VSBS Entry Format](#6-vsbs-entry-format)
7. [Write Path Changes](#7-write-path-changes)
8. [Read Path / Query Algorithm](#8-read-path--query-algorithm)
9. [Delete Handling](#9-delete-handling)
10. [GC / Retention Integration](#10-gc--retention-integration)
11. [VSBS Changes](#11-vsbs-changes)
12. [IBTree / Interface Changes](#12-ibtree--interface-changes)
13. [ComponentTable Changes](#13-componenttable-changes)
14. [Concurrency Analysis](#14-concurrency-analysis)
15. [Performance Analysis](#15-performance-analysis)
16. [Bug Resolution Summary](#16-bug-resolution-summary)
17. [Testing Strategy](#17-testing-strategy)
18. [Implementation Phases](#18-implementation-phases)
19. [Open Questions](#19-open-questions)
20. [Cross-References](#20-cross-references)

---

## 3. Problem Statement

### The Snapshot Isolation Violation

Secondary indexes are **destructively updated** during `Transaction.Commit()`. When a component's indexed field changes from value A to value B, the commit path executes:

```
Remove(oldKey=A, chainId)  →  Add(newKey=B, chainId)
```

This permanently erases the fact that the entity was ever associated with value A. Any concurrent or future transaction whose snapshot predates the commit **cannot find the entity** by querying the old field value through the secondary index.

This violates [ADR-003 (MVCC Snapshot Isolation)](../adr/003-mvcc-snapshot-isolation.md), which guarantees that each transaction sees a consistent snapshot of the database as of its creation timestamp. The revision chains correctly preserve old component data, but the secondary index — the only efficient way to *find* entities by field value — points exclusively to the latest committed state.

### Worked Example

```
Timeline:
═════════════════════════════════════════════════════════════════

T=100: Transaction T1 creates Entity E1 with RegionId=5
       ┌─ Secondary Index (RegionId): 5 → [ChainId(E1)]
       └─ Revision Chain (E1):  [Rev1: T=100, RegionId=5, Committed]

T=150: Transaction T2 starts (snapshot at T=150)
       T2 should see E1.RegionId=5 if it queries by RegionId

T=200: Transaction T3 updates E1.RegionId = 10, commits
       ┌─ Secondary Index (RegionId):
       │    5 → []  (REMOVED!)
       │   10 → [ChainId(E1)]
       └─ Revision Chain (E1):  [Rev1: T=100, RegionId=5]
                                [Rev2: T=200, RegionId=10]

T=250: Transaction T2 queries: "Find entities where RegionId=5"
       ┌─ Expected: E1 (at T=150, RegionId WAS 5)
       └─ Actual:   EMPTY (index entry for RegionId=5 was destroyed)
```

The revision chain still has the correct data (Rev1 at T=100 with RegionId=5), but **there is no index path to discover E1** from the field value `5`. The transaction must fall back to a full component scan — defeating the purpose of the index entirely.

### Impact on Query Engine

The planned Query Engine ([TemporalQueries.md](../research/TemporalQueries.md)) depends on secondary indexes for efficient field-based filtering. Without versioned indexes:

1. **`WHERE RegionId = 5`** at a past TSN returns incorrect results (missing entities)
2. **`GetChangedEntities(fromTSN, toTSN)`** cannot use indexes to find what changed for a given field value
3. **Range queries** (`WHERE Health BETWEEN 50 AND 100`) miss entities whose values have since moved out of range

This is not a theoretical concern — it is a **correctness bug** that affects any concurrent workload where secondary index queries overlap with field mutations.

### The Three Existing Bugs

The [IndexingAndMVCC research](../research/IndexingAndMVCC.md) identified three bugs in the current index update logic, all of which this redesign addresses:

| Bug | Location | Severity | Description |
|-----|----------|----------|-------------|
| **Bug 1** | `ComponentRevisionManager.cs:275` | Critical | Wrong segment used in `CleanUpUnusedEntries` (already fixed) |
| **Bug 2** | `Transaction.cs` (TOFIX section) | High | Index cleanup on entity delete is disabled — orphaned entries accumulate |
| **Bug 3** | `Transaction.cs:1293-1302` | Medium | Race window between `Remove` and `Add` during index update |

---

## 4. Current Architecture Audit

### The `UpdateIndices` Method

**Location:** `Transaction.cs:1271-1338`

This is the central method that maintains secondary indexes during commit. It handles two cases:

**Case 1: Update (prevCompChunkId ≠ 0)** — Lines 1275-1305

```csharp
// For each indexed field, compare old vs new values
if (prevSpan.Slice(ifi.OffsetToField, ifi.Size)
    .SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)) == false)
{
    if (ifi.Index.AllowMultiple)
    {
        // DESTRUCTIVE: Remove old value, add new value
        ifi.Index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId],
                              startChunkId, ref accessor);
        *(int*)&cur[ifi.OffsetToIndexElementId] =
            ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
    }
    else
    {
        ifi.Index.Remove(&prev[ifi.OffsetToField], out var val, ref accessor);
        ifi.Index.Add(&cur[ifi.OffsetToField], val, ref accessor);
    }
}
```

**Case 2: Create (no previous revision)** — Lines 1310-1337

```csharp
// Add PK index entry
info.PrimaryKeyIndex.Add(pk, startChunkId, ref accessor);

// Add secondary index entries for each indexed field
if (ifi.Index.AllowMultiple)
{
    *(int*)&cur[ifi.OffsetToIndexElementId] =
        ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
}
```

**Key observation:** The `AllowMultiple` path stores the VSBS element ID back into the component data at `OffsetToIndexElementId`. This is used by `RemoveValue` to locate the correct entry in the VSBS buffer for deletion. The versioned design must preserve this bookkeeping.

### Commit Path: Index Update Ordering

**Location:** `Transaction.cs:1206-1218`

```csharp
// Update indices BEFORE clearing isolation flag
if (compRevInfo.CurCompContentChunkId != 0)
{
    UpdateIndices(pk, info, compRevInfo, readCompChunkId);
}

// Clear isolation flag — revision becomes visible
elementHandle.Commit(TSN);

// Update Last Commit Revision Index
compRev.SetLastCommitRevisionIndex(...);
```

The isolation flag is cleared **after** index updates. This means the index change becomes visible before the revision becomes visible — creating a brief window where the index points to a revision that other transactions cannot yet read. The versioned design must maintain this ordering constraint or eliminate the window.

### AllowMultiple / VSBS Integration Pattern

**Location:** `L32BTree.cs:703-731`

The `L32MultipleBTree` uses `VariableSizedBufferSegment<int>` as the value store:

```csharp
public class L32MultipleNodeStorage : L32NodeStorage
{
    private VariableSizedBufferSegment<int> _valueStore;

    internal override void Initialize(BTree<TKey> owner, ChunkBasedSegment segment)
    {
        base.Initialize(owner, segment);
        _valueStore = new VariableSizedBufferSegment<int>(segment);
    }

    public override int Append(int bufferId, int value, ref ChunkAccessor accessor)
        => _valueStore.AddElement(bufferId, value, ref accessor);

    public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor accessor)
        => _valueStore.DeleteElement(bufferId, elementId, value, ref accessor);
}
```

Each B+Tree key maps to a VSBS buffer ID. The buffer contains plain `int` values (revision chain head chunk IDs). `AllowMultiple` is used when multiple entities can share the same field value (e.g., many entities with `RegionId=5`).

### VSBS Internals

**Location:** `VariableSizedBufferSegment.cs`

The VSBS is a linked-list of fixed-size chunks storing a variable number of uniform-typed elements:

```
VariableSizedBufferSegment<int> layout:
┌──────────────────────────────────────────────────────┐
│ Root Chunk (ChunkId = bufferId)                      │
│  ┌─VariableSizedBufferRootHeader─────────────────┐   │
│  │ Header.NextChunkId  │ Header.ElementCount     │   │
│  │ Lock (AccessControl) │ FirstFreeChunkId       │   │
│  │ FirstStoredChunkId   │ TotalCount             │   │
│  │ TotalFreeChunk       │ RefCounter             │   │
│  └───────────────────────────────────────────────┘   │
│  [Element 0] [Element 1] ... [Element N]             │
├──────────────────────────────────────────────────────┤
│ Overflow Chunk (linked via NextChunkId)              │
│  ┌─VariableSizedBufferChunkHeader────────────────┐   │
│  │ NextChunkId          │ ElementCount           │   │
│  └───────────────────────────────────────────────┘   │
│  [Element N+1] [Element N+2] ...                     │
└──────────────────────────────────────────────────────┘
```

**Critical details for the versioned design:**

- Element type is generic (`T : unmanaged`). Currently `int` for index values.
- `DeleteElement` performs a linear search then swap-with-last compaction — O(K) where K = elements in chunk.
- `AddElement` appends to the first chunk with space — O(1) amortized.
- `RefCounter` enables shared ownership (used by component collections, not currently by indexes).
- The `AccessControl` lock protects the entire buffer during writes; reads use shared access.
- Root chunk header is 32 bytes (`sizeof(VariableSizedBufferRootHeader)`). Overflow chunk header is 8 bytes.

### Bug Audit Detail

**Bug 2 — Disabled delete cleanup:** When an entity is deleted and becomes the oldest revision, the PK index entry is removed (for single components) but **secondary index entries are not cleaned up**. The TOFIX-commented code in the commit path was intended to handle this but was disabled. The current code at `Transaction.cs:1237-1256` shows that for `ComponentInfoMultiple`, even PK cleanup is skipped.

**Bug 3 — Race window:** Between `RemoveValue` and `Add` (lines 1295-1296), another thread reading the index via shared access could observe a state where the entity's old value is gone but the new value hasn't been added yet. This is mitigated by ChangeSets (uncommitted changes are buffered), but the window exists at the storage level.

---

## 5. Versioned VSBS Design

### Core Concept: Two-Section Buffer Layout

The key insight is that **current-state queries and temporal queries have fundamentally different access patterns**. Current-state queries need fast enumeration of active entities; temporal queries need to reconstruct state at a past TSN by scanning versioned history.

Rather than forcing all queries through a single versioned entry format (which would penalize current-state performance), we split the VSBS buffer into two logical sections:

```
VSBS Buffer for FieldValue="Active" (AllowMultiple):
┌─────────────────────────────────────────────────────────────┐
│ BUFFER HEADER                                               │
│   HeadCount: 3          ← number of current-state entries   │
│   TailStartOffset: 3    ← index where TAIL begins           │
├─────────────────────────────────────────────────────────────┤
│ HEAD (Current State) — compact, for current-TSN queries     │
│   [0] ChainId=10                                            │
│   [1] ChainId=20                                            │
│   [2] ChainId=30                                            │
├─────────────────────────────────────────────────────────────┤
│ TAIL (History) — versioned entries for temporal queries     │
│   [0] (ChainId=10, TSN=100, Active)    ← E1 gained "Active" │
│   [1] (ChainId=20, TSN=100, Active)    ← E2 gained "Active" │
│   [2] (ChainId=30, TSN=200, Active)    ← E3 gained "Active" │
│   [3] (ChainId=10, TSN=300, Tombstone) ← E1 lost "Active"   │
│   [4] (ChainId=40, TSN=350, Active)    ← E4 gained "Active" │
└─────────────────────────────────────────────────────────────┘
```

### Why HEAD + TAIL, Not Just Versioned Entries

| Approach | Current-State Cost | Temporal Cost | Storage |
|----------|-------------------|---------------|---------|
| **Versioned-only** (scan all entries, filter by TSN) | O(H) — must scan full history | O(H) | 12 bytes/entry |
| **HEAD + TAIL** (two sections) | O(K) — scan HEAD only | O(H) — scan TAIL | 4 bytes/HEAD + 12 bytes/TAIL |
| **Dual B+Tree** (separate current/historical trees) | O(K) | O(H) | 2× tree overhead |

The HEAD+TAIL approach matches today's current-state performance (O(K) with 4-byte entries, identical to existing `VariableSizedBufferSegment<int>`) while adding temporal capability at the cost of TAIL storage. The dual B+Tree approach was rejected because it doubles tree management complexity without meaningful performance gain.

### HEAD Section

- Stores **plain `int` chain IDs** — exactly what the VSBS stores today.
- Represents the **current active set** of entities with this field value.
- Updated synchronously on every commit (same as today's `RemoveValue` + `Add`).
- No TSN, no flags — pure chain IDs for maximum density.
- **Query path:** Enumerate HEAD entries, each is a valid chain ID to look up in the revision chain.

### TAIL Section

- Stores **versioned entries**: `(ChainId, TSN, Active/Tombstone)`.
- Append-only during normal operations (entries are never modified, only appended).
- Entries record the **moment** an entity gained or lost a particular field value.
- **Query path:** For a target TSN, scan TAIL entries and track per-entity last-seen state at or before the target TSN.

### Physical Layout Strategy

Rather than embedding HEAD and TAIL in a single `VariableSizedBufferSegment<T>`, use **two separate VSBS instances** per indexed field:

```
Per indexed field in ComponentTable:
┌─────────────────────────────────────────────┐
│ B+Tree (FieldValue → bufferId)              │
│   Key: FieldValue                           │
│   Value: HeadBufferId (int)                 │
│                                             │
│   HeadBufferId → VSBS<int> (HEAD section)   │
│     Same as today: plain chain IDs          │
│                                             │
│ Parallel VSBS<VersionedIndexEntry> (TAIL)   │
│   Keyed by same bufferId mapping            │
│     Versioned history entries               │
└─────────────────────────────────────────────┘
```

**Rationale for two VSBS instances:**

1. **Element type mismatch:** HEAD stores `int` (4 bytes), TAIL stores `VersionedIndexEntry` (12 bytes). A single `VariableSizedBufferSegment<T>` requires uniform element size.
2. **Access pattern isolation:** HEAD uses `DeleteElement` (swap-compact) for removals; TAIL is append-only. Mixing these in one buffer would require complex offset management.
3. **Existing code reuse:** The HEAD VSBS remains `VariableSizedBufferSegment<int>` — zero changes to existing `AllowMultiple` read/write paths for current-state queries.
4. **Independent GC:** TAIL can be compacted/pruned independently without touching HEAD.

The linkage between HEAD and TAIL is maintained through a **parallel buffer ID mapping**: when a B+Tree key is created with `AllowMultiple`, both a HEAD buffer and a TAIL buffer are allocated. The TAIL buffer ID is stored in a companion lookup (see [§11 VSBS Changes](#11-vsbs-changes)).

---

## 6. VSBS Entry Format

### HEAD Entry Format (Unchanged)

```
HEAD entry: int (4 bytes)
┌──────────────────────┐
│ ChainId (int, 4B)    │  ← revision chain head chunk ID
└──────────────────────┘
```

Identical to today's `VariableSizedBufferSegment<int>` element. No changes required.

### TAIL Entry Format: `VersionedIndexEntry`

Two encoding options were evaluated:

#### Option A: Dedicated Struct (Recommended)

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct VersionedIndexEntry
{
    /// <summary>
    /// Revision chain head chunk ID.
    /// Sign encodes state: positive = Active, negative = Tombstone.
    /// Use Math.Abs(SignedChainId) to get the actual ChainId.
    /// </summary>
    public int SignedChainId;

    /// <summary>
    /// Transaction Sequence Number when this entry was created.
    /// </summary>
    public long TSN;
}
```

**Size:** 12 bytes (4 + 8). Even-sized, satisfying [ADR-027](../adr/027-even-sized-hot-path-structs.md).

**Encoding:**
- `SignedChainId > 0` → entity **gained** this field value at this TSN (Active)
- `SignedChainId < 0` → entity **lost** this field value at this TSN (Tombstone)
- `|SignedChainId|` → actual revision chain head chunk ID
- `SignedChainId == 0` → invalid/sentinel (never used as a real chunk ID)

**Advantages:**
- Natural struct, works directly with `VariableSizedBufferSegment<VersionedIndexEntry>`
- Type-safe — cannot accidentally mix HEAD and TAIL entries
- TSN stored as `long` — full precision, no bit-packing errors
- SIMD-friendly for bulk scans (12-byte stride, chainIds at offset 0)

#### Option B: Packed `int` Triplets

```
Each TAIL entry = 3 consecutive ints in VariableSizedBufferSegment<int>:
  int[0] = signed ChainId (positive=active, negative=tombstone)
  int[1] = (int)(TSN >> 32)    // high 32 bits
  int[2] = (int)(TSN & 0xFFFFFFFF)  // low 32 bits
```

**Size:** 12 bytes (3 × 4).

**Disadvantages:**
- Shared `VariableSizedBufferSegment<int>` instance — risk of mixing HEAD and TAIL ops
- `TotalCount` reflects triplet count, not logical entry count (divide by 3)
- `DeleteElement` operates on single ints, would need custom 3-element deletion
- No type safety — easy to accidentally process TAIL entries as HEAD entries

**Decision: Option A.** The dedicated struct is cleaner, type-safe, and requires no hacks to the existing VSBS generic implementation. The storage overhead is identical (12 bytes either way).

### Chunk Capacity Analysis

HEAD uses the standard 64-byte chunk stride (`sizeof(Index64Chunk)`), unchanged from today.

TAIL uses a dedicated **512-byte chunk stride**. Unlike B+Tree nodes (where 64-byte = 1 cache line guarantees exactly 1 miss per random-access node lookup), TAIL scanning is a **sequential linked-list enumeration** — every chunk is visited. The bottleneck is the pointer chase between chunks (~100ns each, unpredictable), not within-chunk access (sequential, prefetchable). Larger chunks drastically reduce the number of pointer chases. At 64 bytes, a 20K-entry TAIL scan would require 5,000 pointer chases (~500µs in miss latency alone — unacceptable). At 512 bytes, the same scan needs only 476 chases (~48µs), with the intra-chunk sequential reads handled efficiently by the CPU prefetcher.

```
HEAD (VariableSizedBufferSegment<int>, 64-byte stride):
  Root chunk:     (64 - 32) / 4 =  8 entries per root chunk
  Overflow chunk: (64 -  8) / 4 = 14 entries per overflow chunk

TAIL (VariableSizedBufferSegment<VersionedIndexEntry>, 512-byte stride):
  Root chunk:     (512 - 32) / 12 = 40 entries per root chunk
  Overflow chunk: (512 -  8) / 12 = 42 entries per overflow chunk
```

**Volumetry at scale (1M components, low-cardinality field with 100 unique values):**

| Chunk stride | Entries/overflow | Chunks for 100K TAIL entries | Pointer chases | Chase latency |
|-------------|-----------------|------------------------------|----------------|---------------|
| 64 B | 4 | 25,000 | 25,000 | ~2.5 ms |
| 128 B | 10 | 10,000 | 10,000 | ~1.0 ms |
| 256 B | 20 | 5,000 | 5,000 | ~500 µs |
| **512 B** | **42** | **2,380** | **2,380** | **~238 µs** |
| 1024 B | 84 | 1,190 | 1,190 | ~119 µs |

512 bytes (8 cache lines) is the chosen trade-off: pointer chases drop to ~10-20% of total scan time for typical workloads, while space waste for sparse buffers (~480 bytes per single-entry root chunk) is negligible at scale (~4.8 MB for 10K unique field values — trivial compared to component data).

**Page utilization:** 8,000 bytes `PageRawData` / 512 bytes = 15 chunks per page. Acceptable density.

---

## 7. Write Path Changes

### Overview

The `UpdateIndices` method is rewritten to maintain both HEAD and TAIL sections. Three cases are handled: **Create**, **Update**, and **Delete**.

### Case 1: Create (First Revision for Entity)

When a new entity is created, its indexed field values must be added to the indexes.

```
Pseudocode — UpdateIndices (Create path):
═══════════════════════════════════════════

For each indexed field:
  1. HEAD: Add(fieldValue, chainId)             ← same as today
  2. TAIL: Append(fieldValue, VersionedIndexEntry {
       SignedChainId = +chainId,                ← Active
       TSN = transaction.TSN
     })
  3. Store HEAD elementId back in component data ← same as today
```

**Cost:** O(log N) B+Tree lookup + O(1) HEAD append + O(1) TAIL append. Same asymptotic cost as today plus one TAIL append.

### Case 2: Update (Field Value Changed)

When an indexed field changes from `oldValue` to `newValue`:

```
Pseudocode — UpdateIndices (Update path):
══════════════════════════════════════════

For each indexed field where old ≠ new:
  // HEAD updates (same as today)
  1. HEAD: RemoveValue(oldValue, elementId, chainId)  ← remove from old key
  2. HEAD: Add(newValue, chainId)                      ← add to new key
  3. Store new HEAD elementId back in component data

  // TAIL appends (NEW)
  4. TAIL: Append(oldValue, VersionedIndexEntry {
       SignedChainId = -chainId,                       ← Tombstone
       TSN = transaction.TSN
     })
  5. TAIL: Append(newValue, VersionedIndexEntry {
       SignedChainId = +chainId,                       ← Active
       TSN = transaction.TSN
     })
```

**Semantics:** The tombstone on `oldValue` records that the entity **stopped** having this field value at this TSN. The active entry on `newValue` records that the entity **started** having this field value at this TSN.

**Cost:** O(log N) for two B+Tree lookups + O(K) for HEAD removal (linear scan in chunk) + O(1) for HEAD add + O(1) for two TAIL appends. Net overhead vs today: two TAIL appends (negligible).

### Case 3: Delete (Entity Deleted)

When an entity is deleted, its current field values must be tombstoned.

```
Pseudocode — UpdateIndices (Delete path):
══════════════════════════════════════════

For each indexed field:
  1. HEAD: RemoveValue(fieldValue, elementId, chainId)  ← remove from current set
  2. TAIL: Append(fieldValue, VersionedIndexEntry {
       SignedChainId = -chainId,                         ← Tombstone
       TSN = transaction.TSN
     })
```

This **resolves Bug 2** (disabled delete cleanup). Instead of the commented-out TOFIX code that tried to clean up indexes in the GC path, the delete is now recorded explicitly as a tombstone in the TAIL at commit time. The HEAD removal ensures current-state queries no longer find the entity.

### Revised `UpdateIndices` Pseudocode

```csharp
private void UpdateIndices(long pk, ComponentInfoBase info,
    ComponentInfoBase.CompRevInfo compRevInfo, int prevCompChunkId)
{
    var startChunkId = compRevInfo.CompRevTableFirstChunkId;
    var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;

    if (prevCompChunkId != 0)
    {
        // === CASE 2: UPDATE ===
        using var prevHandle = info.CompContentAccessor.GetChunkHandle(prevCompChunkId);
        using var curHandle = info.CompContentAccessor.GetChunkHandle(compRevInfo.CurCompContentChunkId);
        var prev = prevHandle.Address;
        var cur = curHandle.Address;
        var prevSpan = new Span<byte>(prev, info.ComponentTable.ComponentTotalSize);
        var curSpan = new Span<byte>(cur, info.ComponentTable.ComponentTotalSize);

        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            if (prevSpan.Slice(ifi.OffsetToField, ifi.Size)
                .SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)))
                continue;   // Field unchanged

            var accessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);

            // HEAD: Remove old, Add new (same as today for AllowMultiple)
            ifi.Index.RemoveValue(&prev[ifi.OffsetToField],
                *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor);
            *(int*)&cur[ifi.OffsetToIndexElementId] =
                ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);

            // TAIL: Tombstone on old value, Active on new value
            var tailAccessor = ifi.TailVSBS.Segment.CreateChunkAccessor(_changeSet);
            var oldTailBufferId = ifi.Index.GetTailBufferId(&prev[ifi.OffsetToField], ref accessor);
            var newTailBufferId = ifi.Index.GetTailBufferId(&cur[ifi.OffsetToField], ref accessor);

            ifi.TailVSBS.AddElement(oldTailBufferId,
                new VersionedIndexEntry(-startChunkId, TSN), ref tailAccessor);
            ifi.TailVSBS.AddElement(newTailBufferId,
                new VersionedIndexEntry(+startChunkId, TSN), ref tailAccessor);

            tailAccessor.Dispose();
            accessor.Dispose();
        }
    }
    else if ((compRevInfo.Operations & ComponentInfoBase.OperationType.Created)
              == ComponentInfoBase.OperationType.Created)
    {
        // === CASE 1: CREATE ===
        var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId);

        // PK index (unchanged)
        {
            var accessor = info.PrimaryKeyIndex.Segment.CreateChunkAccessor(_changeSet);
            info.PrimaryKeyIndex.Add(pk, startChunkId, ref accessor);
            accessor.Dispose();
        }

        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            var accessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);

            // HEAD: Add (same as today)
            *(int*)&cur[ifi.OffsetToIndexElementId] =
                ifi.Index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);

            // TAIL: Active entry
            var tailAccessor = ifi.TailVSBS.Segment.CreateChunkAccessor(_changeSet);
            var tailBufferId = ifi.Index.GetTailBufferId(&cur[ifi.OffsetToField], ref accessor);
            ifi.TailVSBS.AddElement(tailBufferId,
                new VersionedIndexEntry(+startChunkId, TSN), ref tailAccessor);

            tailAccessor.Dispose();
            accessor.Dispose();
        }
    }
}
```

### Delete Path Integration

Delete handling is currently scattered across the commit path. The versioned design centralizes it:

```csharp
// In the commit path, when CurCompContentChunkId == 0 (delete):
// Instead of skipping UpdateIndices entirely, call a new method:
private void UpdateIndicesForDelete(long pk, ComponentInfoBase info,
    ComponentInfoBase.CompRevInfo compRevInfo)
{
    // Read the LAST COMMITTED component data to get current field values
    // (needed to know which index entries to tombstone)
    var lastCommittedChunkId = /* from revision chain */;
    var data = info.CompContentAccessor.GetChunkAddress(lastCommittedChunkId);

    var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
    for (int i = 0; i < indexedFieldInfos.Length; i++)
    {
        ref var ifi = ref indexedFieldInfos[i];
        var accessor = ifi.Index.Segment.CreateChunkAccessor(_changeSet);

        // HEAD: Remove
        ifi.Index.RemoveValue(&data[ifi.OffsetToField],
            *(int*)&data[ifi.OffsetToIndexElementId],
            compRevInfo.CompRevTableFirstChunkId, ref accessor);

        // TAIL: Tombstone
        var tailAccessor = ifi.TailVSBS.Segment.CreateChunkAccessor(_changeSet);
        var tailBufferId = ifi.Index.GetTailBufferId(&data[ifi.OffsetToField], ref accessor);
        ifi.TailVSBS.AddElement(tailBufferId,
            new VersionedIndexEntry(-compRevInfo.CompRevTableFirstChunkId, TSN),
            ref tailAccessor);

        tailAccessor.Dispose();
        accessor.Dispose();
    }
}
```

---

## 8. Read Path / Query Algorithm

### Current-State Query (TSN == latest)

**Path:** Read HEAD section only.

```
Algorithm: CurrentStateQuery(fieldValue)
════════════════════════════════════════
1. B+Tree lookup: fieldValue → headBufferId
2. If not found → return empty
3. Enumerate VSBS<int> buffer at headBufferId
4. For each chainId in buffer:
     yield chainId  (caller resolves via revision chain)
```

**Complexity:** O(log N) B+Tree lookup + O(K) enumeration where K = current matches.

This is **identical to today's read path**. No performance regression for current-state queries.

### Temporal Query (TSN < latest)

**Path:** Scan TAIL section, reconstruct active set at target TSN.

```
Algorithm: TemporalQuery(fieldValue, targetTSN)
════════════════════════════════════════════════
1. B+Tree lookup: fieldValue → tailBufferId
2. If not found → return empty
3. Create dictionary: perEntity = {}  (ChainId → last known state)
4. Enumerate VSBS<VersionedIndexEntry> buffer at tailBufferId:
     For each entry where entry.TSN ≤ targetTSN:
       chainId = |entry.SignedChainId|
       if entry.SignedChainId > 0:
         perEntity[chainId] = Active
       else:
         perEntity[chainId] = Tombstone
5. Return [chainId for (chainId, state) in perEntity where state == Active]
```

**Complexity:** O(log N) B+Tree lookup + O(H) TAIL scan where H = total historical entries for this field value.

**Optimization for common case:** If `targetTSN` is very recent (close to current), the HEAD section result is likely identical. A fast path can check whether any TAIL entries exist with `TSN > targetTSN`; if not, HEAD is already the correct answer.

### Query Algorithm: Correctness Argument

The TAIL section records every state transition for every entity. For a given field value and target TSN:

1. **Completeness:** Every entity that ever had this field value has at least one Active entry in TAIL (from its creation or update).
2. **Correctness:** If an entity lost this value (update or delete), a Tombstone entry exists at the TSN of that change.
3. **Temporal consistency:** By processing entries in TSN order (TAIL is append-ordered by commit sequence) and tracking the last-seen state per entity, we reconstruct the exact active set at any historical TSN.

**Edge case — entity re-gains same value:** If entity E1 has `Status=Active` at T=100, changes to `Status=Inactive` at T=200, then back to `Status=Active` at T=300:

```
TAIL for Status="Active":
  (E1, TSN=100, Active)     ← created with Active
  (E1, TSN=200, Tombstone)  ← changed to Inactive
  (E1, TSN=300, Active)     ← changed back to Active

Query at TSN=150: perEntity[E1] = Active   ✓ (only entry at TSN≤150 is Active)
Query at TSN=250: perEntity[E1] = Tombstone ✓ (Tombstone at T=200 overrides)
Query at TSN=350: perEntity[E1] = Active   ✓ (Active at T=300 overrides)
```

---

## 9. Delete Handling

### Current Problem

Delete handling for secondary indexes is currently **disabled** (Bug 2). The TOFIX section in the commit path was commented out, meaning:

1. Deleted entities retain their secondary index entries indefinitely
2. Index queries return chain IDs that point to deleted entities
3. The caller must detect and skip these — wasting work and risking crashes if the chain was freed

### Versioned Delete Design

The versioned design handles deletes naturally:

```
Entity E1 created with Status="Active" at T=100, deleted at T=300:

HEAD for Status="Active" after create:  [ChainId=10]
HEAD for Status="Active" after delete:  []  (removed)

TAIL for Status="Active":
  (ChainId=10, TSN=100, Active)      ← created
  (ChainId=10, TSN=300, Tombstone)   ← deleted
```

**Current-state query:** HEAD is empty → E1 not returned. Correct.

**Temporal query at T=200:** TAIL scan → last-seen state for ChainId=10 at T≤200 is Active. Returned. Correct.

**Temporal query at T=350:** TAIL scan → last-seen state for ChainId=10 at T≤350 is Tombstone. Not returned. Correct.

### Re-Creation After Delete

If an entity is deleted and then re-created with the same field value (or a new entity gets the same chain ID via pooling):

```
E1 created Status="Active" at T=100
E1 deleted at T=200
E5 (reusing ChainId=10) created Status="Active" at T=300

TAIL for Status="Active":
  (ChainId=10, TSN=100, Active)      ← E1 created
  (ChainId=10, TSN=200, Tombstone)   ← E1 deleted
  (ChainId=10, TSN=300, Active)      ← E5 created (same ChainId)
```

This is correct: the temporal query algorithm tracks state transitions per ChainId, and the re-creation at T=300 creates a new Active entry that correctly represents the new entity.

**Important caveat:** If chain IDs are reused across logically different entities, the TAIL entries for the old and new entity are interleaved under the same ChainId. This is correct for temporal queries (the tombstone at T=200 separates them), but callers performing entity-level history should be aware that chain ID reuse creates apparent "gaps" in an entity's timeline.

---

## 10. GC / Retention Integration

### HEAD: No GC Needed

The HEAD section always reflects the current state. Entries are added and removed as part of normal commit operations. There is nothing to garbage-collect.

### TAIL: Prunable by Retention Policy

TAIL entries are append-only and grow without bound. They must be pruned based on a retention policy (coordinated with the [Temporal Queries retention design](../research/TemporalQueries.md#5-retention-policy-design)):

```
GC Algorithm: PruneTail(fieldValue, retentionTSN)
═════════════════════════════════════════════════
1. Lookup tailBufferId for fieldValue
2. Scan TAIL buffer:
     For each entry where entry.TSN < retentionTSN:
       Track per-ChainId: last seen entry at TSN < retentionTSN
3. For entries with TSN < retentionTSN:
     Keep the LAST entry per ChainId (boundary sentinel)
     Discard all earlier entries for that ChainId
4. Compact buffer (rewrite without discarded entries)
```

### Boundary Entry Retention

When pruning TAIL entries older than `retentionTSN`, we must keep **one boundary entry per ChainId** — the most recent entry with `TSN < retentionTSN`. This boundary entry tells temporal queries whether the entity was Active or Tombstoned at the retention boundary.

```
Before GC (retentionTSN = 250):
  TAIL: (E1, T=100, Active) (E1, T=150, Tombstone) (E1, T=200, Active) (E1, T=300, Tombstone)

After GC:
  TAIL: (E1, T=200, Active)  ← boundary sentinel (last entry before T=250)
        (E1, T=300, Tombstone)

Query at T=220: Active  ✓ (boundary entry covers)
Query at T=180: UNKNOWN — data pruned, query returns "retention limit exceeded" error
```

### GC Trigger Points

TAIL GC can be triggered by:

1. **Transaction chain advancement:** When `MinTSN` advances (oldest transaction completes), entries older than the new `MinTSN` are candidates for pruning (unless retention policy extends beyond `MinTSN`).
2. **Explicit retention policy:** A background task prunes entries older than `retentionDuration` or `retentionCount`.
3. **Deferred cleanup integration:** The [CompRevDeferredCleanup](CompRevDeferredCleanup.md) manager can include TAIL GC entries in its deferred queue, processing them in batches during low-contention periods.

### Storage Growth Estimate

For a workload with 10,000 entities, each with one indexed field updated 5 times on average:

```
HEAD: 10,000 entities × 4 bytes = 40 KB (stable, no growth)
TAIL: 10,000 entities × 5 updates × 12 bytes = 600 KB (before GC)
TAIL after GC (keep last 2 versions): 10,000 × 2 × 12 = 240 KB
```

Compared to today's index storage (10,000 × 4 bytes = 40 KB), the TAIL adds ~6× overhead before GC and ~2.5× after GC. This is the cost of temporal correctness.

---

## 11. VSBS Changes

### New Type: `VariableSizedBufferSegment<VersionedIndexEntry>`

No changes to `VariableSizedBufferSegment<T>` itself — it is already generic over `T : unmanaged`. The new `VersionedIndexEntry` struct is unmanaged (contains only `int` and `long`), so it works directly.

```csharp
// In L32MultipleNodeStorage or equivalent:
private VariableSizedBufferSegment<int> _headStore;                    // existing
private VariableSizedBufferSegment<VersionedIndexEntry> _tailStore;    // new
```

### TAIL Buffer ID Mapping

Each B+Tree key with `AllowMultiple` needs both a HEAD buffer ID (stored in the B+Tree value) and a TAIL buffer ID. Two options:

**Option A: Parallel mapping via companion B+Tree**

A second, simpler B+Tree maps `FieldValue → TailBufferId`. This duplicates the key space but keeps HEAD and TAIL completely independent.

**Option B: Packed buffer ID pair (Recommended)**

Store both HEAD and TAIL buffer IDs in the B+Tree value:

```csharp
// Current: B+Tree value = headBufferId (int)
// New: B+Tree value = encoded pair

// The B+Tree value is currently a single int.
// For AllowMultiple versioned indexes, we pack two IDs:
//   bits [0..30]  = headBufferId (positive, max 2^31)
//   bit  [31]     = always 0 (sentinel, head IDs are always positive)
//
// The tailBufferId is stored at a fixed offset in the HEAD buffer's root header
// (extending VariableSizedBufferRootHeader with a TailBufferId field).
```

Actually, the simplest approach: **store `TailBufferId` in the HEAD buffer's root header.** The `VariableSizedBufferRootHeader` has a `RefCounter` field (currently unused by indexes — always 1). We can add a `TailBufferId` field to the root header, or store it at a reserved position in the first chunk's element area.

**Recommended approach:** Add a `TailBufferId` field to a new `VersionedBufferRootHeader` that extends the existing header:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct VersionedBufferRootHeader
{
    public VariableSizedBufferRootHeader Base;  // existing 32 bytes
    public int TailBufferId;                    // 4 bytes — links to TAIL VSBS buffer
}
```

This reduces the root chunk's element capacity by 1 entry (from 8 to 7 for `int` HEAD entries) but avoids any B+Tree value format changes.

### Buffer Lifecycle

```
On B+Tree key creation (first entity with this field value):
  1. Allocate HEAD buffer: headId = _headStore.AllocateBuffer(...)
  2. Allocate TAIL buffer: tailId = _tailStore.AllocateBuffer(...)
  3. Store tailId in HEAD buffer's root header
  4. Store headId as B+Tree value (unchanged from today)

On B+Tree key deletion (last entity removed, optional):
  1. Read tailId from HEAD buffer's root header
  2. Delete TAIL buffer: _tailStore.DeleteBuffer(tailId, ...)
  3. Delete HEAD buffer: _headStore.DeleteBuffer(headId, ...)
  4. Remove B+Tree key (unchanged from today)
```

### VSBS `AddElement` for TAIL

TAIL appends use the standard `AddElement` method on `VariableSizedBufferSegment<VersionedIndexEntry>`. Since TAIL is append-only (no deletions during normal operation), the VSBS's linked-list structure naturally provides efficient sequential storage:

- New entries go to the first chunk with space (LIFO order within chunks)
- Chunks are allocated as needed (O(1) amortized)
- No deletions, so no fragmentation during normal operation

### VSBS GC for TAIL

During GC, TAIL entries are pruned. This requires a new operation not currently supported by VSBS: **selective bulk deletion** (remove entries matching a predicate). The recommended approach:

```csharp
// New method on VariableSizedBufferSegment<T>:
public int CompactBuffer(int bufferId, Func<T, bool> keepPredicate, ref ChunkAccessor accessor)
{
    // 1. Enumerate all entries
    // 2. Copy entries matching predicate to a new buffer
    // 3. Swap new buffer into old buffer's root chunk
    // 4. Free old overflow chunks
    // Returns: new TotalCount
}
```

Alternatively, GC can use `CloneBuffer` with filtering — allocate a new buffer with only kept entries, then swap IDs.

---

## 12. IBTree / Interface Changes

### Current `IBTree` Interface

**Location:** `BTree.cs:90-101`

```csharp
public interface IBTree
{
    ChunkBasedSegment Segment { get; }
    bool AllowMultiple { get; }
    unsafe int Add(void* keyAddr, int value, ref ChunkAccessor accessor);
    unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor accessor);
    unsafe bool TryGet(void* keyAddr, out int value, ref ChunkAccessor accessor);
    unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor accessor);
    unsafe VariableSizedBufferAccessor<int> TryGetMultiple(void* keyAddr, ref ChunkAccessor accessor);
    void CheckConsistency(ref ChunkAccessor accessor);
}
```

### New Methods for Versioned Access

```csharp
public interface IBTree
{
    // ... existing methods unchanged ...

    /// <summary>
    /// Gets the TAIL buffer ID associated with a B+Tree key's HEAD buffer.
    /// Only valid for AllowMultiple indexes with versioning enabled.
    /// </summary>
    unsafe int GetTailBufferId(void* keyAddr, ref ChunkAccessor accessor);

    /// <summary>
    /// Returns true if this index supports versioned (temporal) queries.
    /// </summary>
    bool SupportsVersioning { get; }
}
```

**Note:** The core `Add`, `Remove`, `TryGet`, `RemoveValue` methods remain unchanged — they operate on the HEAD section. TAIL operations are performed directly on the `VariableSizedBufferSegment<VersionedIndexEntry>` instance, bypassing the B+Tree interface. This keeps the B+Tree API clean and avoids forcing non-versioned indexes to carry versioned baggage.

### `IndexedFieldInfo` Extension

```csharp
internal struct IndexedFieldInfo
{
    public int OffsetToField;
    public int Size;
    public int OffsetToIndexElementId;
    public IBTree Index;

    // NEW: TAIL VSBS instance for versioned entries
    public VariableSizedBufferSegment<VersionedIndexEntry> TailVSBS;
}
```

The `TailVSBS` field is `null` for non-versioned indexes (e.g., PK index, indexes on immutable fields where versioning is unnecessary).

---

## 13. ComponentTable Changes

### Segment Allocation

**Location:** `ComponentTable.cs:318-349`

The `Create` method currently allocates four segments:

```csharp
ComponentSegment    = mmf.AllocateChunkBasedSegment(..., ComponentTotalSize);
CompRevTableSegment = mmf.AllocateChunkBasedSegment(..., CompRevChunkSize);
DefaultIndexSegment = mmf.AllocateChunkBasedSegment(..., sizeof(Index64Chunk));
String64IndexSegment = mmf.AllocateChunkBasedSegment(..., sizeof(IndexString64Chunk));
```

For versioned indexes, a new segment is needed for TAIL VSBS storage:

```csharp
// New segment for versioned index TAIL entries
// Element size: sizeof(VersionedIndexEntry) = 12 bytes
// Uses 512-byte stride: optimized for sequential TAIL scanning
// (unlike B+Tree nodes which use 64-byte for random-access cache alignment)
const int TailChunkStride = 512;
VersionedIndexTailSegment = mmf.AllocateChunkBasedSegment(
    PageBlockType.None, MainIndexSegmentStartingSize, TailChunkStride);
```

The TAIL VSBS uses a **512-byte chunk stride** — deliberately larger than the 64-byte B+Tree node stride. TAIL scanning is a sequential linked-list walk where the bottleneck is pointer chases between chunks, not within-chunk access. Larger chunks reduce pointer chases at the cost of higher per-chunk space, a trade-off that favors temporal query performance at scale (see [§6 Chunk Capacity Analysis](#chunk-capacity-analysis)).

### `BuildIndexedFieldInfo` Changes

**Location:** `ComponentTable.cs:351-376`

```csharp
private void BuildIndexedFieldInfo()
{
    var l = new List<IndexedFieldInfo>();
    var ro = ComponentOverhead;

    for (int i = 0, j = 0; i < Definition.MaxFieldId; i++)
    {
        var f = Definition[i];
        if (f == null || !f.HasIndex) continue;

        var fi = new IndexedFieldInfo
        {
            OffsetToField = ro + f.OffsetInComponentStorage,
            Size = f.SizeInComponentStorage,
            Index = CreateIndexForField(f),
        };
        fi.OffsetToIndexElementId = fi.Index.AllowMultiple ? (j++ * sizeof(int)) : 0;

        // NEW: Create TAIL VSBS for versioned AllowMultiple indexes
        if (fi.Index.AllowMultiple)
        {
            fi.TailVSBS = new VariableSizedBufferSegment<VersionedIndexEntry>(
                VersionedIndexTailSegment);
        }

        l.Add(fi);
    }

    IndexedFieldInfos = l.ToArray();
}
```

### `CreateIndexForField` — No Changes

The B+Tree creation (`CreateIndexForField`, lines 394-413) remains unchanged. The B+Tree key type and node storage are unaffected — only the VSBS value layer is extended with TAIL.

---

## 14. Concurrency Analysis

### HEAD Section: Same Locking as Today

HEAD operations (`Add`, `RemoveValue`, `DeleteElement`) use the VSBS buffer's built-in `AccessControl` lock:

- **Write operations** (commit path): Acquire exclusive lock on HEAD buffer → modify → release.
- **Read operations** (query path): Acquire shared lock on HEAD buffer → enumerate → release.
- **Contention model:** Same as today. The commit path is serialized per-transaction, so HEAD write contention is bounded by commit throughput.

### TAIL Section: Append-Only Semantics

TAIL writes are append-only during normal operation, which has favorable concurrency properties:

- **Append:** Acquires exclusive lock on TAIL buffer → append to first chunk with space → release. Lock hold time is O(1) — just writing 12 bytes.
- **Read (temporal query):** Acquires shared lock on TAIL buffer → enumerate all entries → release. Lock hold time is O(H).
- **GC (compaction):** Acquires exclusive lock on TAIL buffer → rewrite buffer → release. Must not run concurrently with temporal reads. This is the only operation that requires blocking readers.

### Ordering Constraints

The commit path must maintain this order:

```
1. Update HEAD (remove from old key, add to new key)
2. Append to TAIL (tombstone on old key, active on new key)
3. Clear isolation flag on revision
```

Steps 1 and 2 operate on different VSBS buffers (HEAD and TAIL), potentially on different field values. They must both complete before step 3 (isolation flag clearing) to ensure that:

- A concurrent reader sees either the old state (if flag not yet cleared) or the new state (flag cleared + HEAD/TAIL updated) — never a partial update.
- TAIL entries are always present before the revision becomes visible.

### Lock Ordering to Prevent Deadlocks

When updating an indexed field from value A to value B:

```
Acquire: HEAD(A).Lock (exclusive) → remove
Acquire: HEAD(B).Lock (exclusive) → add
Acquire: TAIL(A).Lock (exclusive) → append tombstone
Acquire: TAIL(B).Lock (exclusive) → append active
```

If two transactions concurrently change the same field (one from A→B, another from B→A), they could deadlock on HEAD locks. However, **transactions commit sequentially** (the commit path is single-threaded per transaction, and conflict detection serializes concurrent commits to the same entity). Therefore, this cross-lock scenario cannot occur in practice.

For safety, the recommended lock acquisition order is: **sort buffer IDs numerically, acquire in ascending order**. This provides a total ordering that prevents deadlocks even if the commit path is ever parallelized.

---

## 15. Performance Analysis

### Current-State Query Performance

| Operation | Current Design | Versioned Design | Difference |
|-----------|---------------|-----------------|------------|
| B+Tree lookup | O(log N) | O(log N) | None |
| Value enumeration | O(K) scan `int[]` | O(K) scan `int[]` (HEAD) | None |
| Per-entry cost | 4 bytes/entry | 4 bytes/entry (HEAD) | None |
| Lock acquisition | 1 shared lock | 1 shared lock (HEAD only) | None |

**Current-state queries have zero performance regression.** The HEAD section is identical in format and access pattern to today's VSBS.

### Temporal Query Performance

| Metric | Value | Notes |
|--------|-------|-------|
| B+Tree lookup | O(log N) | Same as current-state |
| TAIL scan | O(H) | H = total historical entries for this field value |
| Per-entry processing | Compare TSN + update hashmap | ~5ns per entry |
| Memory for result tracking | O(U) | U = unique ChainIds seen |

### Write Path Performance

| Operation | Current Design | Versioned Design | Overhead |
|-----------|---------------|-----------------|----------|
| HEAD remove | O(K) linear scan | O(K) linear scan | None |
| HEAD add | O(1) amortized | O(1) amortized | None |
| TAIL append (old key) | N/A | O(1) amortized | +1 append |
| TAIL append (new key) | N/A | O(1) amortized | +1 append |
| B+Tree lookups | 2 (remove + add) | 2 (same) + 2 TAIL buffer lookups | +2 lookups |

**Net write overhead:** 2 TAIL appends (12 bytes each) + 2 TAIL buffer ID lookups. At ~1µs per append (dominated by chunk access), this adds ~2-4µs to the commit of a single indexed field change.

### Worked Performance Examples

#### Example 1: Current-State Query, 10K Entities

```
Scenario: 10,000 entities with RegionId indexed, 100 unique values
          Average 100 entities per value

Current design:    B+Tree(RegionId=5) → VSBS buffer with 100 ints
                   100 × 4B = 400 bytes → single cache line fetch
                   Latency: ~1µs (B+Tree) + ~0.5µs (scan) = ~1.5µs

Versioned design:  Identical HEAD path
                   Latency: ~1.5µs (unchanged)
```

#### Example 2: Temporal Query, 10K Entities × 5 Updates

```
Scenario: Same 10K entities, each updated 5 times on average
          Query at historical TSN for RegionId=5

TAIL entries per field value: ~500 entries (100 entities × 5 transitions)
TAIL size: 500 × 12B = 6,000 bytes
Chunks (512B stride): 1 root (40 entries) + 11 overflow (42 each) = 12 chunks
Pointer chases: 12 × ~100ns = ~1.2µs
Data processing: 500 × ~5ns = ~2.5µs
Latency: ~1µs (B+Tree) + ~1.2µs (chases) + ~2.5µs (processing) = ~4.7µs
```

#### Example 2b: Temporal Query at Scale, 1M Components

```
Scenario: 1M components, low-cardinality field (100 unique values)
          200K entities per value, 5 updates each, post-GC (keep last 2)
          Query at historical TSN for Status="Active"

TAIL entries per field value: ~400,000 entries (200K entities × 2 retained)
TAIL size: 400K × 12B = 4.8 MB
Chunks (512B stride): 1 root (40) + 9,524 overflow (42 each) = 9,525 chunks
Pointer chases: 9,525 × ~100ns = ~953µs
Data processing: 400K × ~5ns = ~2ms
Latency: ~1µs (B+Tree) + ~953µs (chases) + ~2ms (processing) = ~3ms

Note: With 64B chunks this would be ~47,600 chases = ~4.8ms in chases alone.
      512B chunks save ~4ms of pure pointer-chase latency at this scale.
```

#### Example 3: Write Path, Single Field Change

```
Scenario: Update one entity's RegionId from 5 to 10

Current design:
  HEAD(5).Remove: ~2µs (linear scan in chunk)
  HEAD(10).Add:   ~1µs (append)
  Total:          ~3µs

Versioned design:
  HEAD(5).Remove: ~2µs (unchanged)
  HEAD(10).Add:   ~1µs (unchanged)
  TAIL(5).Append: ~1µs (tombstone)
  TAIL(10).Append: ~1µs (active)
  Total:          ~5µs (+2µs overhead)
```

### Storage Overhead

| Component | Current | Versioned | Growth |
|-----------|---------|-----------|--------|
| B+Tree nodes | Same | Same | 0% |
| HEAD VSBS (per entity) | 4 bytes | 4 bytes | 0% |
| TAIL VSBS (per state change) | 0 | 12 bytes | N/A |
| TAIL overhead (10K entities, 5 updates) | 0 | 600 KB | N/A |
| TAIL after GC (keep last 2) | 0 | 240 KB | N/A |

---

## 16. Bug Resolution Summary

### Bug 1: Wrong Segment in `CleanUpUnusedEntries`

**Status:** Already fixed in prior commit.

**Original problem:** Line 1243 allocated from `CompContentSegment` instead of `CompRevTableSegment` during revision chain compaction, corrupting the chain.

**Resolution:** Direct code fix (not related to versioned indexes, but noted as a prerequisite).

### Bug 2: Disabled Delete Cleanup

**Status:** Resolved by this design.

**Original problem:** The TOFIX-commented code in the commit path meant deleted entities retained orphaned secondary index entries indefinitely.

**Resolution:** The versioned design adds explicit delete handling in `UpdateIndicesForDelete` (see [§7](#7-write-path-changes)):
- HEAD: `RemoveValue` removes the chain ID from the current set → queries no longer find the entity.
- TAIL: Tombstone entry records the deletion for temporal queries.
- The previously disabled TOFIX code path is no longer needed — delete cleanup happens at commit time, not during GC.

### Bug 3: Race Window During Index Update

**Status:** Partially mitigated by this design.

**Original problem:** Between `RemoveValue(oldKey)` and `Add(newKey)`, the index is briefly inconsistent — the entity appears in neither old nor new key's result set.

**Resolution:**
- **HEAD:** The race window still exists for HEAD operations (Remove then Add are still separate steps). However, this only affects current-state queries during the brief commit window.
- **TAIL:** No race window. TAIL appends are independent of HEAD operations. A temporal query at any TSN will see consistent results because TAIL entries are append-only and protected by their own lock.
- **Mitigation:** The ChangeSet mechanism already buffers uncommitted changes, making the HEAD race window invisible to other transactions in practice. For full elimination, a future enhancement could use an atomic HEAD snapshot swap (allocate new HEAD buffer with the updated set, then atomically swap buffer IDs).

---

## 17. Testing Strategy

### Unit Tests

#### VSBS Entry Format Tests

```
- VersionedIndexEntry sign encoding: positive = Active, negative = Tombstone
- VersionedIndexEntry round-trip: write → read preserves ChainId and TSN
- VersionedIndexEntry with ChainId = 0 → rejected (sentinel value)
- VersionedIndexEntry size is exactly 12 bytes
- VersionedIndexEntry layout matches StructLayout.Sequential expectations
```

#### HEAD Operation Tests (Regression)

```
- HEAD Add/Remove/Enumerate: identical behavior to current VSBS<int>
- HEAD with AllowMultiple: multiple entities per field value
- HEAD after field update: old value removed, new value added
- HEAD after delete: entity removed from current set
```

#### TAIL Operation Tests

```
- TAIL append Active entry on create
- TAIL append Tombstone + Active entries on field update
- TAIL append Tombstone entry on delete
- TAIL enumeration returns entries in append order
- TAIL entries preserve TSN correctly (full 64-bit range)
- TAIL with re-creation: tombstone followed by new active entry
```

### Integration Tests

#### Snapshot Isolation Verification

```
Test: ConcurrentQueryDuringFieldUpdate
  1. T1 creates E1 with RegionId=5, commits (T=100)
  2. T2 starts (snapshot at T=150)
  3. T3 updates E1.RegionId=10, commits (T=200)
  4. T2 queries "RegionId=5" using temporal query at T=150
  5. Assert: E1 is found ← THIS CURRENTLY FAILS, should pass with versioned indexes

Test: TemporalQueryAcrossMultipleUpdates
  1. Create E1 with Status="Active" (T=100)
  2. Update E1 Status="Inactive" (T=200)
  3. Update E1 Status="Active" (T=300)
  4. Temporal query at T=150: E1 found under "Active" ✓
  5. Temporal query at T=250: E1 found under "Inactive" ✓
  6. Temporal query at T=350: E1 found under "Active" ✓
  7. Current-state query: E1 found under "Active" ✓
```

#### Delete Correctness

```
Test: DeleteRemovesFromHeadAndTombstonesTail
  1. Create E1 with RegionId=5 (T=100)
  2. Delete E1 (T=200)
  3. Current-state query "RegionId=5": empty ✓
  4. Temporal query at T=150 "RegionId=5": E1 found ✓
  5. Temporal query at T=250 "RegionId=5": empty ✓
```

#### GC / Retention Tests

```
Test: TailGCPreservesBoundaryEntries
  1. Create/update entities across T=100..T=500
  2. Run GC with retentionTSN=300
  3. Temporal queries at T≥300 return correct results
  4. Temporal queries at T<300 return "retention limit exceeded"
  5. HEAD is unaffected by GC
```

### Noise / Stress Tests

Using the existing noise generation pattern (`CreateNoiseCompA`, `UpdateNoiseCompA`):

```
Test: VersionedIndexUnderConcurrentLoad
  Parameters: BuildNoiseCasesL2 (multiple thread counts and operation mixes)
  1. N threads performing random create/update/delete operations
  2. M threads performing concurrent current-state and temporal queries
  3. Assert: No crashes, no assertion failures
  4. Assert: Current-state queries match HEAD content
  5. Assert: Temporal queries at any valid TSN return consistent results

Test: TailGCUnderLoad
  1. Concurrent writes generating TAIL entries
  2. Background GC thread pruning old entries
  3. Concurrent temporal queries reading TAIL
  4. Assert: No use-after-free, no corruption
```

---

## 18. Implementation Phases

### Phase 1: `VersionedIndexEntry` Struct + TAIL VSBS Infrastructure

**Scope:** Define the entry type, create TAIL VSBS instance in `ComponentTable`, add `TailBufferId` linkage.

**Files modified:**
- New: `VersionedIndexEntry.cs` (struct definition)
- Modified: `ComponentTable.cs` (segment allocation, `BuildIndexedFieldInfo`)
- Modified: `IndexedFieldInfo` (add `TailVSBS` field)
- Modified: `L32MultipleNodeStorage` and variants (add `_tailStore` field, `GetTailBufferId` method)

**Deliverable:** TAIL VSBS is allocated alongside HEAD but not yet used. No behavior change.

**Tests:** Unit tests for `VersionedIndexEntry` encoding.

### Phase 2: Write Path — TAIL Appends in `UpdateIndices`

**Scope:** Modify `UpdateIndices` to append TAIL entries on create, update, and delete.

**Files modified:**
- `Transaction.cs` (`UpdateIndices`, new `UpdateIndicesForDelete`)

**Deliverable:** Every indexed field change generates TAIL entries. HEAD behavior unchanged. Existing tests still pass.

**Tests:** Integration tests verifying TAIL entries are created with correct ChainId, TSN, and sign.

### Phase 3: Read Path — Temporal Query Algorithm

**Scope:** Implement `TemporalQuery(fieldValue, targetTSN)` method and integrate with the query engine.

**Files modified:**
- `IBTree` interface (new `GetTailBufferId` method, `SupportsVersioning` property)
- New query helper class for TAIL scanning
- Transaction read path (add temporal query overloads)

**Deliverable:** Temporal queries work correctly. Snapshot isolation is restored for secondary index queries.

**Tests:** Full integration tests from [§17](#17-testing-strategy). Snapshot isolation verification tests pass.

### Phase 4: Delete Handling + Bug 2 Fix

**Scope:** Implement `UpdateIndicesForDelete`, remove the disabled TOFIX code, handle secondary index cleanup at commit time.

**Files modified:**
- `Transaction.cs` (delete path in commit, remove TOFIX code)

**Deliverable:** Deleted entities are properly cleaned up from HEAD and tombstoned in TAIL.

**Tests:** Delete correctness tests from [§17](#17-testing-strategy).

### Phase 5: TAIL GC + Retention Integration

**Scope:** Implement TAIL pruning, integrate with retention policies from [CompRevDeferredCleanup](CompRevDeferredCleanup.md).

**Files modified:**
- `VariableSizedBufferSegment<T>` (new `CompactBuffer` method or equivalent)
- GC scheduling integration (deferred cleanup manager)

**Deliverable:** TAIL entries are pruned based on retention policy. Storage growth is bounded.

**Tests:** GC tests and stress tests from [§17](#17-testing-strategy).

---

## 19. Open Questions

### Q1: TAIL Chunk Size — RESOLVED

**Decision: 512-byte stride.** Analysis showed that TAIL scanning is a sequential linked-list walk where pointer chases (~100ns each) dominate latency. At 64 bytes (4 entries/chunk), a 20K-entry scan requires 5,000 chases (~500µs). At 512 bytes (42 entries/chunk), the same scan needs 476 chases (~48µs). Space waste for sparse buffers is negligible at scale (~4.8 MB for 10K single-entry root chunks vs. multi-GB component data). See [§6 Chunk Capacity Analysis](#chunk-capacity-analysis) for full numbers.

If profiling shows temporal queries on high-volume low-cardinality fields are still chase-dominated, the move to 1024-byte stride is a single constant change (`TailChunkStride`).

### Q2: HEAD Compaction Trigger

When should empty HEAD buffers (all entities removed) trigger B+Tree key deletion? Currently, keys with empty VSBS buffers remain in the tree.

**Options:**
- **Eager:** Delete the B+Tree key when HEAD becomes empty. Requires also deleting the TAIL buffer (losing history for that field value).
- **Lazy:** Keep the key until GC prunes the TAIL. The HEAD buffer has `TotalCount=0` but remains allocated.
- **Deferred:** Queue empty keys for later cleanup, similar to `CompRevDeferredCleanup`.

**Recommendation:** Lazy approach initially. Empty HEAD buffers cost 64 bytes each (one HEAD chunk) and empty TAIL buffers cost 512 bytes (one TAIL chunk). Both are harmless at scale. GC can clean them up when TAIL is pruned.

### Q3: GC Scheduling

How should TAIL GC be scheduled relative to revision chain GC?

**Options:**
- **Piggyback:** Run TAIL GC inside `CleanUpUnusedEntries` when processing each entity.
- **Batch:** Run TAIL GC as a separate pass after `MinTSN` advances.
- **Background:** Dedicated GC thread/task, independent of transaction processing.

**Recommendation:** Batch approach, triggered when `MinTSN` advances by a configurable threshold. This amortizes GC cost and avoids extending the commit path.

### Q4: Non-AllowMultiple Indexes

Currently, single-value indexes (`AllowMultiple == false`) store one `int` value per key directly in the B+Tree node — no VSBS. Should versioned indexes be supported for single-value indexes?

**Analysis:** Single-value indexes guarantee field uniqueness (one entity per field value). Temporal queries for unique fields can directly use the entity's revision chain (look up the entity by PK at the target TSN, read the field value). A dedicated TAIL is only needed if we want to answer "which entity had field value X at TSN T?" efficiently — which requires versioning.

**Recommendation:** Defer single-value versioned indexes. Most unique-field queries are PK-based (know the entity, want the value), not value-based (know the value, want the entity). If needed, single-value versioning can be added by switching to `AllowMultiple` with TAIL.

---

## 20. Cross-References

| Document | Relationship |
|----------|-------------|
| [Temporal Queries](../research/TemporalQueries.md) | Downstream consumer — temporal query APIs depend on versioned secondary indexes for efficient field-based temporal lookups |
| [Indexing and MVCC Analysis](../research/IndexingAndMVCC.md) | Prior research — identifies the snapshot isolation violation and evaluates Option A (temporal keys) vs Option B (versioned values) |
| [CompRevDeferredCleanup](CompRevDeferredCleanup.md) | Related design — GC scheduling and deferred cleanup mechanisms that TAIL GC will integrate with |
| [ADR-003: MVCC Snapshot Isolation](../adr/003-mvcc-snapshot-isolation.md) | The invariant this design restores — snapshot isolation must extend to secondary index queries |
| [ADR-021: Specialized B+Tree Variants](../adr/021-specialized-btree-variants.md) | B+Tree variant pattern — versioned indexes extend the existing `L32MultipleBTree` family |
| [ADR-022: 64-Byte Cache-Aligned Nodes](../adr/022-64byte-cache-aligned-nodes.md) | B+Tree node sizing — TAIL deliberately uses 512-byte stride instead, optimized for sequential scan rather than random-access (see [§6 Chunk Capacity Analysis](#chunk-capacity-analysis)) |
| [ADR-027: Even-Sized Structs](../adr/027-even-sized-hot-path-structs.md) | `VersionedIndexEntry` at 12 bytes satisfies the even-size requirement |
| [ADR-008: ChunkBasedSegment](../adr/008-chunk-based-segments.md) | Underlying storage — both HEAD and TAIL VSBS use ChunkBasedSegment for chunk allocation |

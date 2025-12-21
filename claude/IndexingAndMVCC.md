# Typhon Indexing System and MVCC Integration Analysis

## Table of Contents

1. [Overview](#overview)
2. [Index Architecture](#index-architecture)
3. [Primary Key Index](#primary-key-index)
4. [Secondary Indexes](#secondary-indexes)
5. [MVCC Integration](#mvcc-integration)
6. [Index Updates During Commit](#index-updates-during-commit)
7. [Mutable Field Indexing: The Key Problem](#mutable-field-indexing-the-key-problem)
8. [Potential Bugs Identified](#potential-bugs-identified)
9. [Design Implications](#design-implications)
10. [Recommendations](#recommendations)

---

## Overview

Typhon uses B+Tree indexes for both entity identification (Primary Key) and field-based lookups (Secondary Indexes). This document analyzes how these indexes interact with the MVCC (Multi-Version Concurrency Control) system, particularly focusing on mutable indexed fields.

### Key Files Involved

| File | Purpose |
|------|---------|
| `ComponentTable.cs` | Index creation and management |
| `Transaction.cs` | Index updates during commit, MVCC read logic |
| `BPTree/*.cs` | B+Tree implementations |
| `DBComponentDefinition.cs` | Schema with index definitions |

---

## Index Architecture

### High-Level Structure

```
                     ComponentTable
                          |
    +---------------------+---------------------+
    |                     |                     |
ComponentSegment   CompRevTableSegment    Index Segments
(Component Data)   (Revision Chains)      (B+Tree Nodes)
    |                     |                     |
+---+---+           +-----+-----+         +-----+-----+
| Chunk |           | RevChain  |         | PK Index  |
| Chunk |           | RevChain  |         | Field Idx |
| ...   |           | ...       |         | ...       |
+-------+           +-----------+         +-----------+
```

### Index Types

```csharp
// Primary Key Index - always exists
public BTree<long> PrimaryKeyIndex { get; }

// Secondary Indexes - one per indexed field
internal IndexedFieldInfo[] IndexedFieldInfos;

struct IndexedFieldInfo {
    int OffsetToField;           // Offset in component storage
    int Size;                    // Field size in bytes
    int OffsetToIndexElementId;  // For multi-value indexes
    IBTree Index;                // The B+Tree index
}
```

---

## Primary Key Index

### Structure

```
Primary Key Index (BTree<long>)
=====================================

Key:   Entity ID (long)
Value: First ChunkId of CompRevTable (revision chain head)

Example:
  Entity 1 -> ChunkId 5   (points to revision chain)
  Entity 2 -> ChunkId 8
  Entity 3 -> ChunkId 12
```

### Characteristics

- **Created**: When entity is first committed (`UpdateIndices`, line 1054-1055)
- **Updated**: NEVER changes for an entity's lifetime
- **Deleted**: When entity is deleted (currently has a TOFIX - see bugs section)

### MVCC Compatibility: FULLY COMPATIBLE

The PK index points to the revision chain HEAD, not to a specific revision. When reading:

```
Transaction Read Flow:

  1. Query PK Index    2. Get RevChain Head    3. Walk Chain for Timestamp
        |                      |                         |
        v                      v                         v
   +---------+            +----------+            +-------------+
   | PK: 123 | ---------> | ChunkId  | ---------> | Rev T=100   |
   +---------+            |   = 5    |            | Rev T=200   | <- Find this
        |                 +----------+            | Rev T=300   |    for T=250
        v                                         +-------------+
   Found: 5
```

The transaction walks the revision chain and finds the revision valid at its snapshot timestamp.

---

## Secondary Indexes

### Structure

```
Secondary Index (BTree<FieldType>)
=====================================

Key:   Field Value (int, float, string, etc.)
Value: First ChunkId of CompRevTable (revision chain head)

Example (Health field index):
  Health=100 -> ChunkId 5
  Health=100 -> ChunkId 8   (if AllowMultiple=true)
  Health=75  -> ChunkId 12
```

### Index Creation

```csharp
// ComponentTable.cs:226-246
private IBTree CreateIndexForField(DBComponentDefinition.Field field)
{
    var s = field.Type == FieldType.String64 ? String64IndexSegment : DefaultIndexSegment;
    var a = ChunkRandomAccessor.GetFromPool(s, 8);

    switch (field.Type)
    {
        case FieldType.Int:
            return field.IndexAllowMultiple
                ? new IntMultipleBTree(s, a)
                : new IntSingleBTree(s, a);
        // ... other types
    }
}
```

### Supported Field Types for Indexing

| Type | B+Tree Class | AllowMultiple |
|------|--------------|---------------|
| byte/ubyte | L16BTree | Yes/No |
| short/ushort | L16BTree | Yes/No |
| int/uint | L32BTree | Yes/No |
| long/ulong | L64BTree | Yes/No |
| float | L32BTree | Yes/No |
| double | L64BTree | Yes/No |
| char | L16BTree | Yes/No |
| String64 | String64BTree | Yes/No |

---

## MVCC Integration

### Revision Chain Structure

```
CompRevStorageHeader (First Chunk)
=====================================
+-------------------+
| NextChunkId       | -> Points to next chunk in chain (or 0)
| Control           | -> Thread-safety lock
| FirstItemRevision | -> Revision number of oldest item
| FirstItemIndex    | -> Start index in circular buffer
| ItemCount         | -> Total items in chain
| ChainLength       | -> Number of chunks
| LastCommitRevIdx  | -> Used for conflict detection
+-------------------+
| CompRevElement[0] | -> { ComponentChunkId, DateTime, IsolationFlag }
| CompRevElement[1] |
| CompRevElement[2] |
| ...               |
+-------------------+

Circular Buffer Structure:
  Revisions are stored oldest-to-newest
  IsolationFlag=true means uncommitted (invisible to other transactions)
```

### Reading with MVCC

```csharp
// Transaction.cs:767-825 (GetCompRevInfoFromIndex)
private bool GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick, out CompRevInfo compRevInfo)
{
    // 1. Get revision chain head from PK index
    if (!info.PrimaryKeyIndex.TryGet(pk, out var compRevFirstChunkId, accessor))
        return false;  // Entity doesn't exist

    // 2. Walk revision chain
    using var enumerator = new RevisionEnumerator(...);
    while (enumerator.MoveNext())
    {
        ref var element = ref enumerator.Current;

        // Stop if we passed our timestamp
        if (element.DateTime.Ticks > tick)
            break;

        // Only consider committed, non-isolated revisions
        if ((element.DateTime.Ticks > 0) && !element.IsolationFlag)
        {
            // This revision is valid for our snapshot
            curCompRevisionIndex = ...;
            curCompChunkId = element.ComponentChunkId;
        }
    }

    return curCompRevisionIndex != -1;
}
```

---

## Index Updates During Commit

### The UpdateIndices Method

This is the critical method that manages index updates during transaction commit:

```csharp
// Transaction.cs:1010-1074
private void UpdateIndices(long pk, ComponentInfo info, CompRevInfo compRevInfo, int prevCompChunkId)
{
    var startChunkId = compRevInfo.CompRevTableFirstChunkId;

    // CASE 1: Update with previous revision
    if (prevCompChunkId != 0)
    {
        var prev = GetChunkAddress(prevCompChunkId);  // Previous component data
        var cur = GetChunkAddress(compRevInfo.CurCompContentChunkId);  // New data

        // For each indexed field
        foreach (var ifi in IndexedFieldInfos)
        {
            // Compare old and new field values
            if (!prevSpan.Slice(ifi.OffsetToField, ifi.Size)
                .SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)))
            {
                // FIELD VALUE CHANGED!

                if (ifi.Index.AllowMultiple)
                {
                    // Multi-value index: Remove old, add new
                    ifi.Index.RemoveValue(&prev[...], elementId, startChunkId, accessor);
                    *(int*)&cur[...] = ifi.Index.Add(&cur[...], startChunkId, accessor);
                }
                else
                {
                    // Unique index: Remove old key, add new key with same value
                    ifi.Index.Remove(&prev[...], out var val, accessor);
                    ifi.Index.Add(&cur[...], val, accessor);
                }
            }
        }
    }

    // CASE 2: New entity (no previous revision)
    else
    {
        // Add to PK index
        info.PrimaryKeyIndex.Add(pk, startChunkId, accessor);

        // Add to all secondary indexes
        foreach (var ifi in IndexedFieldInfos)
        {
            if (ifi.Index.AllowMultiple)
                *(int*)&cur[...] = ifi.Index.Add(&cur[...], startChunkId, accessor);
            else
                ifi.Index.Add(&cur[...], startChunkId, accessor);
        }
    }
}
```

### Visual Flow: Index Update on Field Change

```
BEFORE UPDATE:
==============
Entity E1: Health = 100

Secondary Index (Health):
  100 -> RevChainHead(E1)

Revision Chain (E1):
  [Rev1: T=100, Health=100, Committed]


TRANSACTION: Update E1.Health = 75
=================================

Step 1: Add new revision (isolated)
  Revision Chain (E1):
    [Rev1: T=100, Health=100, Committed]
    [Rev2: T=200, Health=75,  ISOLATED]  <- New, not visible yet

Step 2: Index unchanged (still isolated)
  Secondary Index (Health):
    100 -> RevChainHead(E1)  <- Still points to old value!


ON COMMIT:
==========

Step 3: Update index (UpdateIndices called)
  - Compare prev (Health=100) vs cur (Health=75)
  - Values differ!
  - REMOVE: 100 -> RevChainHead(E1)
  - ADD:    75  -> RevChainHead(E1)

Step 4: Clear isolation flag
  Revision Chain (E1):
    [Rev1: T=100, Health=100, Committed]
    [Rev2: T=200, Health=75,  Committed]  <- Now visible

AFTER COMMIT:
=============
Secondary Index (Health):
  75 -> RevChainHead(E1)     <- OLD VALUE GONE!

Note: Index entry for Health=100 no longer exists!
```

---

## Mutable Field Indexing: The Key Problem

### The Core Issue

**Secondary indexes only reflect the LATEST committed value, not historical values.**

This means:
1. Old field values are REMOVED from the index when updated
2. Point-in-time queries using secondary indexes DON'T WORK
3. Only the PK index supports true MVCC queries

### Example Scenario

```
Timeline:
=========
T=100: Create E1 with RegionId=5
T=200: Update E1 with RegionId=10
T=300: Query by RegionId=5 from transaction at T=150

Expected: Find E1 (at T=150, RegionId was 5)
Actual:   NOT FOUND (RegionId=5 entry removed from index)
```

### Detailed Trace

```
Step 1: T=100 - Create Entity
================================
Transaction T1 (tick=100):
  - CreateEntity(E1, {RegionId: 5})
  - Commit

State after:
  PK Index:       E1 -> ChunkId=10
  Region Index:   5  -> ChunkId=10

  RevChain(E1):
    [Rev1: T=100, ChunkId=20, RegionId=5]


Step 2: T=200 - Update Entity
================================
Transaction T2 (tick=200):
  - UpdateEntity(E1, {RegionId: 10})
  - Commit (calls UpdateIndices)
    - prev = {RegionId: 5}
    - cur  = {RegionId: 10}
    - Index.Remove(5, out val)     <- REMOVES 5!
    - Index.Add(10, val)

State after:
  PK Index:       E1 -> ChunkId=10     (unchanged)
  Region Index:   10 -> ChunkId=10     (5 REMOVED, 10 ADDED)

  RevChain(E1):
    [Rev1: T=100, ChunkId=20, RegionId=5]
    [Rev2: T=200, ChunkId=25, RegionId=10]


Step 3: T=300 - Historical Query
================================
Transaction T3 (tick=150):

  Query: "Find entities where RegionId=5"

  Method 1: Use Index
    - RegionIndex.TryGet(5) -> NOT FOUND! (entry removed)
    - Result: Empty

  Method 2: Use PK + Full Scan (if we knew PK)
    - PKIndex.TryGet(E1) -> ChunkId=10
    - Walk RevChain for tick=150
    - Find Rev1 (T=100 <= 150)
    - Read Component -> RegionId=5
    - Result: Found!

PROBLEM: Index-based query fails for historical data!
```

---

## Potential Bugs Identified

### Bug 1: Incorrect Segment in CleanUpUnusedEntries

**Location**: `Transaction.cs:1243`

```csharp
// CleanUpUnusedEntries method, line 1243
var newChunkId = info.CompContentSegment.AllocateChunk(false);  // BUG!
```

**Problem**: When growing the revision chain during cleanup, the code allocates from `CompContentSegment` instead of `CompRevTableSegment`. This would corrupt the revision chain.

**Expected**:
```csharp
var newChunkId = info.CompRevTableSegment.AllocateChunk(false);
```

### Bug 2: Index Cleanup on Delete is Disabled (TOFIX)

**Location**: `Transaction.cs:971-997`

```csharp
// TOFIX
/*
if ((itemLeftCount < 0) ||
    ((itemLeftCount == 0) && ...deleted...) ||
    ((itemLeftCount == 0) && ...created rollback...))
{
    // Remove the index
    info.PrimaryKeyIndex.Remove(pk, out _, accessor);

    // Free the Component Revision chain chunks
    ...
}
*/
```

**Problem**: When entities are deleted, their index entries are NOT being cleaned up. This leads to:
1. Orphaned index entries pointing to freed chunks
2. Memory/storage leaks
3. Potential crashes when index lookup returns invalid chunk IDs

### Bug 3: Race Window During Index Update

**Location**: `Transaction.cs:1029-1041`

```csharp
if (ifi.Index.AllowMultiple)
{
    ifi.Index.RemoveValue(&prev[...], ...);  // Step 1: Remove
    // <-- WINDOW: Index inconsistent here
    *(int*)&cur[...] = ifi.Index.Add(&cur[...], ...);  // Step 2: Add
}
else
{
    ifi.Index.Remove(&prev[...], out var val, ...);  // Step 1: Remove
    // <-- WINDOW: Index missing entry
    ifi.Index.Add(&cur[...], val, ...);  // Step 2: Add
}
```

**Problem**: Although commits are serialized, there's a window between Remove and Add where the index is inconsistent. If the system crashes here, index consistency is lost.

**Note**: This is mitigated by ChangeSets and likely not an issue in practice, but worth noting for recovery scenarios.

### Bug 4: No Verification of Index Integrity on Load

**Observation**: When loading an existing database, there's no verification that index contents match the actual committed component data. If Bug 2 or 3 caused corruption, it wouldn't be detected.

---

## Design Implications

### What Works

| Feature | Status | Notes |
|---------|--------|-------|
| PK-based reads at any timestamp | Works | Uses revision chain walking |
| Secondary index reads for current data | Works | Points to latest values |
| Create/Update/Delete operations | Works | With noted bugs |
| Conflict detection | Works | Uses LastCommitRevisionIndex |
| Concurrent reads | Works | Lock-free shared access |

### What Doesn't Work

| Feature | Status | Notes |
|---------|--------|-------|
| Historical queries via secondary index | BROKEN | Old values removed from index |
| Index cleanup on delete | BROKEN | Code commented out (TOFIX) |
| Index integrity verification | MISSING | No validation on load |

### Design Decision: "Current State" Indexes

The current design makes a deliberate trade-off:

**Advantages**:
- Simpler implementation (no historical index entries)
- Less storage (single index entry per field value)
- Faster updates (one remove + one add per field change)

**Disadvantages**:
- No support for point-in-time secondary index queries
- Index represents "now" not "as of timestamp"

---

## Recommendations

### Short-Term Fixes

1. **Fix CleanUpUnusedEntries segment bug** (Bug 1)
   ```csharp
   // Change line 1243 from:
   var newChunkId = info.CompContentSegment.AllocateChunk(false);
   // To:
   var newChunkId = info.CompRevTableSegment.AllocateChunk(false);
   ```

2. **Enable index cleanup on delete** (Bug 2)
   - Uncomment and fix the TOFIX section
   - Ensure both PK index and secondary indexes are cleaned up

3. **Add index integrity verification on load**
   - Verify index entries point to valid revision chains
   - Verify field values in index match committed data

### Long-Term Considerations

If historical secondary index queries are needed, consider:

**Option A: Temporal Index Entries**
```
Each index entry includes timestamp range:
  {FieldValue, StartTick, EndTick} -> RevChainHead

Query: Find RegionId=5 at tick=150
  Scan index for entries where:
    FieldValue=5 AND StartTick<=150 AND (EndTick>150 OR EndTick=MAX)
```

**Option B: Versioned Index Values**
```
Index stores list of (RevChainHead, ValidFromTick) pairs:
  FieldValue -> [(ChunkId1, T100), (ChunkId2, T200), ...]

Query: Binary search for tick
```

**Option C: Accept Current Design**
- Document that secondary indexes are "current state only"
- For historical queries, use PK index + component scan
- Implement the planned Query system with this limitation in mind

---

## Summary

### Current Behavior

```
                        Index Type Comparison
                        =====================

Primary Key Index          Secondary Index
-----------------          ---------------
Maps: EntityId -> RevHead  Maps: FieldValue -> RevHead
Updates: Never             Updates: On every field change
MVCC: Full support         MVCC: Current state only
Historical queries: Yes    Historical queries: No
```

### Key Insight

**Secondary indexes in Typhon do NOT support MVCC for queries.**

When a field value changes:
1. OLD value is REMOVED from the index
2. NEW value is ADDED to the index
3. Historical queries for the OLD value will fail

This is not necessarily a bug - it's a design choice that trades historical query support for simpler index maintenance. However, it should be:
1. Documented clearly
2. Considered when implementing the Query system
3. Acknowledged as a limitation for time-travel queries

### Bugs to Fix

1. **Critical**: Wrong segment used in `CleanUpUnusedEntries` (line 1243) - **FIXED**
2. **High**: Index cleanup disabled on entity delete (TOFIX section)
3. **Medium**: No index integrity verification on database load
4. **Low**: Index inconsistency window during update (mitigated by ChangeSets)

---

## Appendix: Historical Secondary Index Design Options

This section elaborates on Options A and B from the recommendations for supporting historical secondary index queries.

---

### Option A: Temporal Index Entries

#### Design Overview

In this approach, each index entry becomes a temporal entry that includes validity timestamps. Instead of storing just `FieldValue -> RevChainHead`, we store `{FieldValue, StartTick, EndTick} -> RevChainHead`.

#### Data Structures

```
Temporal Index Structure
========================

BTree Key (Composite):
+----------------+------------+------------+
| FieldValue     | StartTick  | EndTick    |
| (4-8 bytes)    | (8 bytes)  | (8 bytes)  |
+----------------+------------+------------+

BTree Value:
+----------------+
| RevChainHead   |
| (4 bytes)      |
+----------------+

Example entries for Entity E1 (RegionId changes from 5 to 10 at T=200):
  {RegionId=5,  StartTick=100, EndTick=200}   -> ChunkId=10
  {RegionId=10, StartTick=200, EndTick=MAX}   -> ChunkId=10
```

#### Index Entry Lifecycle

```
CREATION (Entity Created)
=========================
When E1 is created at T=100 with RegionId=5:

  Create entry: {5, T=100, T=MAX} -> RevChainHead

  Index state:
    {5, 100, MAX} -> ChunkId=10


UPDATE (Field Value Changes)
============================
When E1.RegionId changes from 5 to 10 at T=200:

  Step 1: Close old entry (set EndTick)
    Find: {5, 100, MAX}
    Update to: {5, 100, 200}   <- EndTick = current tick

  Step 2: Create new entry
    Insert: {10, 200, MAX}

  Index state:
    {5,  100, 200} -> ChunkId=10  (historical)
    {10, 200, MAX} -> ChunkId=10  (current)


DELETE (Entity Deleted)
=======================
When E1 is deleted at T=300:

  Close current entry:
    Find: {10, 200, MAX}
    Update to: {10, 200, 300}

  Index state:
    {5,  100, 200} -> ChunkId=10  (historical)
    {10, 200, 300} -> ChunkId=10  (historical)
```

#### Query Algorithms

**Point-in-Time Query: Find entities where RegionId=5 at tick=150**

```csharp
// Pseudocode
IEnumerable<int> QueryAtTimestamp(int fieldValue, long tick)
{
    // Strategy 1: Range scan (if StartTick is second in sort order)
    // Scan all entries where:
    //   FieldValue = fieldValue
    //   StartTick <= tick
    //   EndTick > tick

    var results = new List<int>();

    // Start from {fieldValue, 0, 0}
    // End at {fieldValue, tick, MAX}
    using var enumerator = index.GetRangeEnumerator(
        startKey: (fieldValue, 0, 0),
        endKey: (fieldValue, tick, long.MaxValue)
    );

    while (enumerator.MoveNext())
    {
        var entry = enumerator.Current;
        // Check EndTick condition
        if (entry.EndTick > tick)
        {
            results.Add(entry.RevChainHead);
        }
    }

    return results;
}
```

**Current State Query: Find entities where RegionId=5 (at current tick)**

```csharp
IEnumerable<int> QueryCurrent(int fieldValue)
{
    // Optimize: scan for EndTick=MAX entries only
    // This is efficient if we use a secondary sort order

    // Or: Query at current transaction tick
    return QueryAtTimestamp(fieldValue, CurrentTransaction.Tick);
}
```

#### Storage Layout Options

**Option A1: Composite Key B+Tree**

```
Key Structure: {FieldValue, StartTick, EndTick}
Sort Order: FieldValue ASC, StartTick ASC, EndTick ASC

Pros:
  - All entries for same FieldValue are contiguous
  - Range scans are efficient for point-in-time queries

Cons:
  - Closing an entry requires key modification (delete + insert)
  - EndTick condition requires post-filtering
```

**Option A2: Separate Temporal Table**

```
Primary Index: FieldValue -> List<TemporalEntryId>
Temporal Table: TemporalEntryId -> {StartTick, EndTick, RevChainHead}

Pros:
  - Closing entry only updates Temporal Table
  - Can add secondary index on EndTick for current-state queries

Cons:
  - Two lookups required
  - More complex implementation
```

**Option A3: Interval Tree**

```
Use an interval tree structure instead of B+Tree:
  - Each node stores {FieldValue, [StartTick, EndTick], RevChainHead}
  - Optimized for "point in interval" queries

Pros:
  - O(log n + k) query time for k results
  - Designed for temporal queries

Cons:
  - More complex implementation
  - May not fit existing B+Tree infrastructure
```

#### Performance Analysis

| Operation | Current Design | Temporal Index (A1) |
|-----------|---------------|---------------------|
| **Insert** | O(log n) | O(log n) |
| **Update** | O(log n) remove + O(log n) add | O(log n) modify + O(log n) add |
| **Delete** | O(log n) remove | O(log n) modify |
| **Current Query** | O(log n) | O(log n + m) where m = historical entries |
| **Historical Query** | O(n) full scan | O(log n + m) |

**Space Complexity**:
- Current: 1 entry per unique (entity, field) pair
- Temporal: u entries per (entity, field) pair, where u = number of updates

```
Space Overhead Calculation
==========================
Current entry size: ~16 bytes (key + value)
Temporal entry size: ~32 bytes (key + StartTick + EndTick + value)

If average entity is updated k times during lifetime:
  Current: 16 bytes per entity
  Temporal: 32k bytes per entity

For k=10 average updates: 20x storage increase for indexes
```

#### Implementation Complexity

**Required Changes**:

1. **New Index Structure** (High complexity)
   - Create `TemporalBTree<TKey>` with composite key support
   - Or modify existing B+Tree to support multi-part keys

2. **UpdateIndices Changes** (Medium complexity)
   ```csharp
   // Instead of:
   ifi.Index.Remove(&prev[...], out var val, accessor);
   ifi.Index.Add(&cur[...], val, accessor);

   // Do:
   ifi.Index.CloseEntry(&prev[...], currentTick, accessor);
   ifi.Index.AddEntry(&cur[...], currentTick, long.MaxValue, startChunkId, accessor);
   ```

3. **Query System Integration** (Medium complexity)
   - Modify query executors to use temporal queries
   - Add tick parameter to all secondary index lookups

4. **Garbage Collection** (High complexity)
   - Old temporal entries need cleanup based on MinTick
   - Cannot delete entries while active transactions might query them

---

### Option B: Versioned Index Values

#### Design Overview

In this approach, the index key remains simple (`FieldValue`), but the value becomes a versioned list of `(RevChainHead, ValidFromTick)` pairs. Each entry tracks when that entity started having this field value.

#### Data Structures

```
Versioned Index Structure
=========================

BTree Key: FieldValue (simple, like current design)

BTree Value: VersionedList
+----------------------------------------+
| VersionedEntry[0]: {ChunkId, Tick}     |
| VersionedEntry[1]: {ChunkId, Tick}     |
| ...                                    |
+----------------------------------------+

Each VersionedEntry:
+----------------+------------+
| RevChainHead   | ValidFrom  |
| (4 bytes)      | (8 bytes)  |
+----------------+------------+

Example for RegionId=5:
  Key: 5
  Value: [
    {ChunkId=10, ValidFrom=100},  <- E1 from T=100-199
    {ChunkId=20, ValidFrom=150},  <- E2 from T=150 onwards
  ]
```

#### Index Entry Lifecycle

```
CREATION (Entity Created at T=100 with RegionId=5)
==================================================

  Index[5] = []  (initially empty or doesn't exist)

  Append: {ChunkId=10, ValidFrom=100}

  Index[5] = [{ChunkId=10, ValidFrom=100}]


UPDATE (E1.RegionId changes 5->10 at T=200)
==========================================

  Step 1: Remove E1 from Index[5]
    Index[5] = [{ChunkId=10, ValidFrom=100}]
    After remove: Index[5] = []

  Step 2: Add E1 to Index[10]
    Index[10] = []
    Append: {ChunkId=10, ValidFrom=200}
    Index[10] = [{ChunkId=10, ValidFrom=200}]

  BUT WAIT - This loses historical information!

CORRECTED: Keep historical entries with tombstones
==================================================

VersionedEntry extended:
+----------------+------------+------------+
| RevChainHead   | ValidFrom  | ValidUntil |
| (4 bytes)      | (8 bytes)  | (8 bytes)  |
+----------------+------------+------------+

Update process:
  Step 1: Mark old entry with ValidUntil
    Index[5] = [{ChunkId=10, ValidFrom=100, ValidUntil=MAX}]
    Update to: [{ChunkId=10, ValidFrom=100, ValidUntil=200}]

  Step 2: Add new entry
    Index[10] = [{ChunkId=10, ValidFrom=200, ValidUntil=MAX}]
```

#### Storage Implementation

**Using Existing VariableSizedBuffer Segment**

```csharp
// Leverage existing AllowMultiple index infrastructure
struct VersionedIndexEntry
{
    public int RevChainHead;    // 4 bytes
    public long ValidFromTick;  // 8 bytes
    public long ValidUntilTick; // 8 bytes (MAX if current)
}
// Total: 20 bytes per entry

// Storage in VariableSizedBufferSegment:
// +------------------+------------------+------------------+
// | EntryCount (4B)  | Entry[0] (20B)   | Entry[1] (20B)   | ...
// +------------------+------------------+------------------+
```

**Alternative: Embedded List in B+Tree Value**

```csharp
// For small lists, store directly in B+Tree leaf
// For large lists, overflow to VariableSizedBuffer

const int MAX_EMBEDDED_ENTRIES = 4;

struct IndexValue
{
    public byte EntryCount;
    public byte IsOverflowed;  // 1 if using VariableSizedBuffer

    // If not overflowed:
    public fixed byte EmbeddedEntries[80]; // 4 entries * 20 bytes

    // If overflowed:
    // public int OverflowBufferId;  // overlaps EmbeddedEntries
}
```

#### Query Algorithms

**Point-in-Time Query: Find entities where RegionId=5 at tick=150**

```csharp
IEnumerable<int> QueryAtTimestamp(int fieldValue, long tick)
{
    // Get versioned list for this field value
    if (!index.TryGet(fieldValue, out var versionedList))
        return Enumerable.Empty<int>();

    var results = new List<int>();

    // Linear scan through versioned entries
    foreach (var entry in versionedList)
    {
        if (entry.ValidFromTick <= tick && entry.ValidUntilTick > tick)
        {
            results.Add(entry.RevChainHead);
        }
    }

    return results;
}
```

**Optimized Query with Binary Search**

```csharp
IEnumerable<int> QueryAtTimestampOptimized(int fieldValue, long tick)
{
    if (!index.TryGet(fieldValue, out var versionedList))
        return Enumerable.Empty<int>();

    // Sort entries by ValidFromTick (maintained during updates)
    // Binary search to find entries where ValidFromTick <= tick
    var startIdx = BinarySearchLowerBound(versionedList, tick);

    var results = new List<int>();

    // Check entries from startIdx backwards
    for (int i = startIdx; i >= 0; i--)
    {
        var entry = versionedList[i];
        if (entry.ValidUntilTick > tick)
        {
            results.Add(entry.RevChainHead);
        }
    }

    return results;
}
```

**Current State Query (Optimized)**

```csharp
IEnumerable<int> QueryCurrent(int fieldValue)
{
    if (!index.TryGet(fieldValue, out var versionedList))
        return Enumerable.Empty<int>();

    // Only return entries with ValidUntilTick = MAX
    return versionedList
        .Where(e => e.ValidUntilTick == long.MaxValue)
        .Select(e => e.RevChainHead);
}

// Alternative: Maintain separate "current" list
// Index stores both:
//   - CurrentList: entries with ValidUntilTick = MAX
//   - HistoricalList: entries with ValidUntilTick < MAX
```

#### Performance Analysis

| Operation | Current Design | Versioned Index (B) |
|-----------|---------------|---------------------|
| **Insert** | O(log n) | O(log n) + O(1) append |
| **Update** | O(log n) + O(log n) | O(log n) + O(m) scan + O(log n) |
| **Delete** | O(log n) | O(log n) + O(m) scan |
| **Current Query** | O(log n) | O(log n) + O(m) filter |
| **Historical Query** | O(N) full scan | O(log n) + O(m) search |

Where:
- n = total unique field values
- m = average versioned entries per field value
- N = total entities

**Space Complexity**:

```
Space Analysis
==============

Current design (AllowMultiple=false):
  Per unique field value: ~16 bytes

Current design (AllowMultiple=true):
  Per unique field value: ~16 bytes + 4 bytes per entity

Versioned Index:
  Per unique field value: ~16 bytes + 20 bytes per (entity × updates)

Example: 1000 entities with RegionId field
  - 10 unique regions
  - Average 5 region changes per entity lifetime

  Current: 10 × 16 + 1000 × 4 = 4,160 bytes
  Versioned: 10 × 16 + 1000 × 5 × 20 = 100,160 bytes

  Overhead: ~24x
```

#### Implementation Complexity

**Required Changes**:

1. **Modify BTree Value Type** (Medium complexity)
   - Change from single value to versioned list
   - Can reuse VariableSizedBufferSegment infrastructure

2. **Update Logic in UpdateIndices** (Medium complexity)
   ```csharp
   // Instead of:
   ifi.Index.Remove(&prev[...], out var val, accessor);
   ifi.Index.Add(&cur[...], val, accessor);

   // Do:
   // Close entry in old key's versioned list
   ifi.Index.CloseVersionedEntry(&prev[...], startChunkId, currentTick, accessor);

   // Add entry to new key's versioned list
   ifi.Index.AddVersionedEntry(&cur[...], startChunkId, currentTick, accessor);
   ```

3. **Query Integration** (Low-Medium complexity)
   - Add tick parameter to index lookups
   - Filter versioned lists by timestamp

4. **Garbage Collection** (Medium complexity)
   - Prune old entries when MinTick advances
   - More localized than Option A (per-key cleanup)

---

### Comparison: Option A vs Option B

| Aspect | Option A (Temporal Keys) | Option B (Versioned Values) |
|--------|--------------------------|----------------------------|
| **Index Key Size** | Large (field + 2 timestamps) | Small (field only) |
| **Key Modifications** | On every update (delete + insert) | Never (key stable) |
| **Value Modifications** | Never | On every update |
| **B+Tree Depth** | May increase (larger keys) | Unchanged |
| **Current Query** | Requires filtering | Can optimize with separate list |
| **Historical Query** | Range scan | Binary search in list |
| **Storage Locality** | Entries scattered by timestamp | Entries grouped by field value |
| **GC Complexity** | Global scan | Per-key cleanup |
| **Implementation Fit** | New B+Tree type needed | Extends existing infrastructure |

#### When to Choose Option A

- **High cardinality fields**: Many unique values, few entities per value
- **Infrequent updates**: Updates are rare, so temporal entries don't accumulate
- **Complex temporal queries**: Need range queries on timestamps
- **New codebase**: Can design B+Tree from scratch

#### When to Choose Option B

- **Low cardinality fields**: Few unique values, many entities per value (e.g., status flags)
- **Frequent updates**: Updates are common, want localized storage
- **Existing infrastructure**: Can leverage VariableSizedBuffer
- **Current-state queries dominate**: Most queries are for current state, not historical

---

### Recommended Approach for Typhon

Given Typhon's existing architecture and goals, **Option B (Versioned Index Values)** is recommended:

1. **Fits existing infrastructure**: Can leverage `VariableSizedBufferSegment` already used for `AllowMultiple` indexes

2. **Minimizes B+Tree changes**: Keys remain simple, only values change

3. **Aligns with MVCC philosophy**: Versioned lists mirror revision chains conceptually

4. **Supports hybrid approach**: Can maintain separate "current" and "historical" lists for optimal current-state query performance

5. **Localized cleanup**: GC is per-key, simpler than global temporal entry cleanup

#### Suggested Implementation Path

```
Phase 1: Versioned Value Structure
==================================
1. Define VersionedIndexEntry struct
2. Create VersionedIndexList storage in VariableSizedBufferSegment
3. Modify IBTree interface to support versioned operations:
   - AddVersioned(key, revChainHead, validFromTick)
   - CloseVersioned(key, revChainHead, validUntilTick)
   - QueryAtTick(key, tick) -> IEnumerable<revChainHead>

Phase 2: Update Transaction Logic
=================================
1. Modify UpdateIndices to use versioned operations
2. Ensure proper tick propagation through commit path
3. Add tests for historical index queries

Phase 3: Query System Integration
=================================
1. Add tick parameter to ComponentTable.QueryByIndex
2. Implement efficient current-state shortcut
3. Integrate with planned Query system

Phase 4: Garbage Collection
===========================
1. Track MinTick across all transactions
2. Periodically prune versioned entries where ValidUntilTick < MinTick
3. Implement compaction for versioned lists
```

# Typhon Database Engine - Architecture Documentation

**Version:** 1.0
**Last Updated:** November 2025
**Target Audience:** Contributors, advanced users, and architects

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Architectural Overview](#architectural-overview)
3. [Layer-by-Layer Architecture](#layer-by-layer-architecture)
4. [MVCC Implementation Deep Dive](#mvcc-implementation-deep-dive)
5. [Concurrency Model & Thread Safety](#concurrency-model--thread-safety)
6. [Performance Optimizations](#performance-optimizations)
7. [Key Algorithms](#key-algorithms)
8. [Data Structures](#data-structures)
9. [Transaction Lifecycle](#transaction-lifecycle)
10. [Code Examples](#code-examples)
11. [Advanced Topics](#advanced-topics)
12. [References](#references)

---

## Executive Summary

Typhon is a **real-time, low-latency ACID database engine** designed for microsecond-level performance in game and simulation workloads. It combines:

- **Entity-Component-System (ECS) architecture** for flexible data modeling
- **Multi-Version Concurrency Control (MVCC)** for snapshot isolation
- **Memory-mapped file persistence** with intelligent page caching
- **B+Tree indexes** for efficient lookups
- **Lock-free reads** and **optimistic writes** for high concurrency

**Key Performance Characteristics:**
- Microsecond-level operations (single-digit microseconds for in-cache operations)
- Zero-copy data access via unsafe pointers
- Minimal allocations through object pooling and value types
- Lock-free reads for maximum scalability
- Adaptive contention management (spin-then-sleep)

**ACID Guarantees:**
- **Atomicity:** Two-phase commit ensures all-or-nothing semantics
- **Consistency:** Schema validation and referential integrity via indexes
- **Isolation:** MVCC snapshot isolation prevents dirty reads and non-repeatable reads
- **Durability:** Optional per-component persistence via ChangeSet tracking

---

## Architectural Overview

### High-Level Design Philosophy

Typhon's architecture is built on **layered abstractions** that separate concerns while maintaining performance:

```
┌─────────────────────────────────────────────────────────────┐
│                    Application Layer                        │
│           (User Code, Components, Queries)                  │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                   DatabaseEngine (API Layer)                │
│  - Transaction orchestration                                │
│  - Component registration                                   │
│  - Primary key generation                                   │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│              Transaction System (MVCC Layer)                │
│  TransactionChain ←→ Transaction ←→ ComponentInfo           │
│  - Snapshot isolation                                       │
│  - Conflict detection                                       │
│  - Read/Write caching                                       │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│             ComponentTable (Data Storage Layer)             │
│  - Component data segments                                  │
│  - Revision chains (MVCC metadata)                          │
│  - Primary + secondary indexes                              │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│            Persistence Layer (Storage Engine)               │
│  ManagedPagedMMF → PagedMMF → Memory-Mapped Files           │
│  - Page caching (clock-sweep eviction)                      │
│  - Segment management                                       │
│  - Chunk allocation                                         │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                Operating System (Disk I/O)                  │
│  - Memory-mapped files (MemoryMappedFile API)               │
│  - Async I/O (RandomAccess.ReadAsync/WriteAsync)            │
└─────────────────────────────────────────────────────────────┘
```

### Core Design Principles

1. **Performance First:** Unsafe code, pointers, and zero-copy operations are used extensively
2. **MVCC for Concurrency:** Multiple transaction versions coexist; no locks during reads
3. **Optimistic Concurrency:** Detect conflicts at commit time (not during execution)
4. **Cache Everything:** Multi-level caching (transactions, pages, chunks) minimizes I/O
5. **ECS Data Model:** Components are independent, enabling flexible entity composition

---

## Layer-by-Layer Architecture

### 1. DatabaseEngine (Orchestration Layer)

**File:** `src/Typhon.Engine/Database Engine/DatabaseEngine.cs`

The DatabaseEngine is the **top-level orchestrator** and primary API entry point.

**Responsibilities:**
- Create and manage transactions via `TransactionChain`
- Maintain a registry of `ComponentTable` instances (one per component type)
- Generate unique primary keys using atomic increment (`Interlocked.Increment`)
- Manage the `ManagedPagedMMF` persistence layer
- Register component schemas via reflection

**Key Properties:**
```csharp
public class DatabaseEngine
{
    public DatabaseDefinitions DBD { get; private set; }
    public ManagedPagedMMF MMF { get; private set; }

    private TransactionChain _transactionChain;
    private ConcurrentDictionary<Type, ComponentTable> _componentTables;
    private long _curPrimaryKey;  // Atomic counter for entity IDs
}
```

**Primary Key Generation:**
```csharp
public long GetNewPrimaryKey()
{
    return Interlocked.Increment(ref _curPrimaryKey);
}
```

**Component Registration:**
```csharp
public void RegisterComponent<T>() where T : unmanaged
{
    var definition = DBD.GetOrCreateComponentDefinition<T>();
    var table = new ComponentTable();
    table.Create(this, definition);
    _componentTables.TryAdd(typeof(T), table);
}
```

**Transaction Creation:**
```csharp
public Transaction CreateTransaction()
{
    return _transactionChain.CreateTransaction();
}
```

**See:** [Transaction Lifecycle](#transaction-lifecycle)

---

### 2. Transaction System (MVCC Layer)

#### 2.1 Transaction Class

**File:** `src/Typhon.Engine/Database Engine/Transaction.cs` (1,452 lines)

Each `Transaction` represents a **snapshot in time** with MVCC snapshot isolation.

**Key Properties:**
```csharp
public class Transaction : IDisposable
{
    public long TransactionTick { get; private set; }  // DateTime.UtcNow.Ticks
    public DateTime TransactionDateTime => new DateTime(TransactionTick);
    public TransactionState State { get; private set; }

    private Dictionary<Type, ComponentInfo> _componentInfos;  // Per-component caches
    private ChangeSet _changeSet;  // Dirty pages for persistence

    public Transaction Previous { get; internal set; }  // Doubly-linked list
    public Transaction Next { get; internal set; }
}
```

**Transaction State Machine:**
```csharp
public enum TransactionState
{
    Invalid = 0,
    Created,       // New, no operations yet
    InProgress,    // At least one operation
    Rollbacked,    // Aborted (user or auto)
    Committed      // Successfully committed
}
```

**ComponentInfo Structure:**

Each transaction maintains a `ComponentInfo` cache per component type:

```csharp
internal class ComponentInfo
{
    public ComponentTable ComponentTable;
    public ChunkBasedSegment CompContentSegment;        // Component data
    public ChunkBasedSegment CompRevTableSegment;       // Revision metadata
    public LongSingleBTree PrimaryKeyIndex;             // Entity ID → Revision chain
    public ChunkRandomAccessor CompContentAccessor;     // Cached chunk access
    public ChunkRandomAccessor CompRevTableAccessor;    // Cached revision access
    public Dictionary<long, CompRevInfo> CompRevInfoCache;  // Transaction's view
}
```

**CompRevInfo Structure:**

Tracks the transaction's view of each entity's component:

```csharp
public struct CompRevInfo
{
    public OperationType Operations;            // Created | Read | Updated | Deleted
    public int CompRevTableFirstChunkId;        // Entry point to revision chain
    public short PrevRevisionIndex;             // Revision before transaction (-1 if none)
    public short CurRevisionIndex;              // Current revision in use
    public int PrevCompContentChunkId;          // Previous component data chunk
    public int CurCompContentChunkId;           // Current component data chunk
}
```

**OperationType Flags:**
```csharp
[Flags]
public enum OperationType
{
    Undefined = 0,
    Created   = 1,  // New entity created
    Read      = 2,  // Only read, no mutation
    Updated   = 4,  // Modified existing entity
    Deleted   = 8   // Removed entity
}
```

#### 2.2 TransactionChain (Transaction Pool & Registry)

**File:** `src/Typhon.Engine/Database Engine/TransactionChain.cs`

The `TransactionChain` manages all active transactions as a **doubly-linked list**.

**Purpose:**
- Track oldest transaction (tail) for garbage collection
- Pool transaction objects (up to 16 reused instances)
- Provide `MinTick` for revision cleanup

**Key Methods:**
```csharp
public class TransactionChain
{
    public Transaction Head { get; private set; }  // Most recent
    public Transaction Tail { get; private set; }  // Oldest
    public long MinTick => Tail?.TransactionTick ?? DateTime.UtcNow.Ticks;

    private Stack<Transaction> _transactionPool;  // Max 16 pooled
    private AccessControl _control;               // Thread-safety

    public Transaction CreateTransaction()
    {
        _control.EnterExclusiveAccess();
        try
        {
            var t = _transactionPool.Count > 0
                ? _transactionPool.Pop()
                : new Transaction();

            t.Initialize(_dbe);
            t.TransactionTick = DateTime.UtcNow.Ticks;
            t.State = TransactionState.Created;

            // Insert at head (most recent)
            t.Next = Head;
            if (Head != null) Head.Previous = t;
            Head = t;
            if (Tail == null) Tail = t;

            return t;
        }
        finally
        {
            _control.ExitExclusiveAccess();
        }
    }

    public void Remove(Transaction t)
    {
        _control.EnterExclusiveAccess();
        try
        {
            // Unlink from list
            if (t.Previous != null) t.Previous.Next = t.Next;
            if (t.Next != null) t.Next.Previous = t.Previous;
            if (Head == t) Head = t.Next;
            if (Tail == t) Tail = t.Previous;

            // Return to pool (max 16)
            if (_transactionPool.Count < 16)
            {
                t.Reset();
                _transactionPool.Push(t);
            }
        }
        finally
        {
            _control.ExitExclusiveAccess();
        }
    }
}
```

**Garbage Collection Integration:**

The `MinTick` property is critical for MVCC garbage collection. When the oldest transaction (tail) commits, its revisions older than `nextMinTick` (the new tail's tick) can be safely removed.

**See:** [Revision Cleanup](#revision-cleanup-garbage-collection)

---

### 3. ComponentTable (Data Storage Layer)

**File:** `src/Typhon.Engine/Database Engine/ComponentTable.cs`

Each `ComponentTable` stores **all instances of a single component type** with MVCC support.

**Segment Organization:**
```csharp
public class ComponentTable
{
    public ChunkBasedSegment ComponentSegment { get; private set; }      // Component data
    public ChunkBasedSegment CompRevTableSegment { get; private set; }   // Revision chains
    public ChunkBasedSegment DefaultIndexSegment { get; private set; }   // Index nodes (64 bytes)
    public ChunkBasedSegment String64IndexSegment { get; private set; }  // String index nodes
    public LongSingleBTree PrimaryKeyIndex { get; private set; }         // Entity ID → RevChain

    public DBComponentDefinition Definition { get; private set; }
    internal IndexedFieldInfo[] IndexedFieldInfos { get; private set; }
}
```

**Segment Layout Diagram:**
```
ComponentSegment (variable chunk size):
┌──────────────────────────────────────────┐
│ Chunk 0: Reserved (null reference)      │
├──────────────────────────────────────────┤
│ Chunk 1: Component instance 1            │
│   - IndexElementId[] (overhead)          │
│   - Field 1, Field 2, ..., Field N       │
├──────────────────────────────────────────┤
│ Chunk 2: Component instance 2            │
│   ...                                    │
└──────────────────────────────────────────┘

CompRevTableSegment (64-byte chunks):
┌──────────────────────────────────────────┐
│ Chunk 0: Reserved                        │
├──────────────────────────────────────────┤
│ Chunk 1: Revision chain for entity E1    │
│   - CompRevStorageHeader (12 bytes)      │
│   - CompRevStorageElement[0..5] (60 bytes)│
├──────────────────────────────────────────┤
│ Chunk 2: Overflow for entity E1 (if needed)│
│   - NextChunkId (4 bytes)                │
│   - CompRevStorageElement[0..5] (60 bytes)│
└──────────────────────────────────────────┘

PrimaryKeyIndex (B+Tree):
┌─────────────────────────────────────┐
│ Root Node                           │
│   Key: 1001 → Value: 1 (ChunkId)    │
│   Key: 1002 → Value: 3 (ChunkId)    │
│   ...                               │
└─────────────────────────────────────┘
```

**Component Storage Layout:**

Each component chunk contains:

```
┌───────────────────────────────────────────────────────────┐
│ ComponentOverhead (MultipleIndicesCount × 4 bytes)        │
│   - Index element IDs for multi-value indexes             │
├───────────────────────────────────────────────────────────┤
│ Component Data (ComponentStorageSize bytes)               │
│   - Actual component field values                         │
└───────────────────────────────────────────────────────────┘
```

**Example:**
```csharp
[Component]
public struct PlayerStats
{
    [Field] [Index] public int PlayerId;      // 4 bytes
    [Field] public float Health;              // 4 bytes
    [Field] public float Stamina;             // 4 bytes
}
// ComponentStorageSize = 12 bytes
// ComponentOverhead = 0 (no multi-value indexes)
// ComponentTotalSize = 12 bytes
```

**IndexedFieldInfo:**

Tracks metadata for indexed fields:

```csharp
internal struct IndexedFieldInfo
{
    public int OffsetToField;            // Byte offset within component
    public int Size;                     // Field size in bytes
    public int OffsetToIndexElementId;   // Offset to element ID (multi-value)
    public IBTree Index;                 // Type-specific B+Tree instance
}
```

**Index Types:**
- **L16BTree:** 16-bit keys (byte, short, ushort, etc.)
- **L32BTree:** 32-bit keys (int, uint, float, etc.)
- **L64BTree:** 64-bit keys (long, ulong, double, DateTime, etc.)
- **String64BTree:** 64-byte fixed-size strings

**Single vs. Multiple Value Indexes:**
- **SingleBTree:** Unique constraint (one value per key)
- **MultipleBTree:** Multiple values per key (via `VariableSizedBufferSegment`)

**See:** [B+Tree Index Implementation](#btree-index-implementation)

---

### 4. Persistence Layer (Storage Engine)

#### 4.1 PagedMMF (Base Memory-Mapped File Management)

**File:** `src/Typhon.Engine/Persistence Layer/PagedMMF.cs` (700+ lines)

`PagedMMF` provides **low-level page caching and I/O** for memory-mapped files.

**Constants:**
```csharp
internal const int PageSize         = 8192;   // 8KB pages
internal const int PageSizePow2     = 13;     // 2^13 = 8192
internal const int PageHeaderSize   = 192;    // Base + Metadata
internal const int PageRawDataSize  = 8000;   // 8192 - 192
```

**Page Structure:**
```
Page (8192 bytes):
├─ PageBaseHeader (64 bytes)
│  ├─ PageBlockFlags (1 byte)      ← First field for fast access
│  ├─ PageBlockType (1 byte)
│  ├─ FormatRevision (2 bytes)
│  ├─ PageIndex (4 bytes)
│  ├─ ChangeRevision (4 bytes)
│  └─ Reserved (52 bytes)
├─ PageMetadata (128 bytes)
│  └─ Occupancy bitmaps for chunks
└─ PageRawData (8000 bytes)
   └─ Actual segment data
```

**Page State Machine:**
```csharp
public enum PageState : ushort
{
    Free         = 0,  // Not allocated, available for reuse
    Allocating   = 1,  // Currently being acquired
    Idle         = 2,  // Allocated but not accessed
    Shared       = 3,  // Multiple readers accessing
    Exclusive    = 4,  // Single exclusive writer
    IdleAndDirty = 5   // Needs disk write
}
```

**State Transitions:**
```
Free → Allocating → Idle
Idle → Shared (multiple readers)
Idle → Exclusive (single writer)
Shared → Idle (all readers exit)
Exclusive → IdleAndDirty (dirty write)
Exclusive → Idle (clean write)
IdleAndDirty → Idle (after flush)
Idle → Free (eviction)
```

**Page Cache:**

Default cache: **256 pages × 8KB = 2MB**

```csharp
public class PagedMMF
{
    private byte[] _memPages;                      // Pinned memory (GCHandle)
    private PageInfo[] _memPagesInfo;              // Metadata per cached page
    private ConcurrentDictionary<int, int>
        _memPageIndexByFilePageIndex;              // Fast lookup: file→memory

    private int _clockSweepHand;                   // Clock-sweep eviction pointer
}
```

**PageInfo Structure:**
```csharp
internal class PageInfo
{
    public int FilePageIndex;               // -1 if free
    public PageState PageState;
    public int ClockSweepCounter;           // 0-5, for LRU approximation
    public int LockedByThreadId;            // Exclusive lock holder
    public Task IOReadTask;                 // Async read operation
    public object StateSyncRoot;            // Lock for state transitions
}
```

**Page Allocation Flow:**

```csharp
private int AllocateMemoryPage(int filePageIndex, bool exclusive)
{
    // 1. Try sequential allocation (prefetch adjacent pages)
    if (filePageIndex > 0 &&
        _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemIdx))
    {
        var nextMemIdx = (prevMemIdx + 1) % _memPagesCount;
        if (TryAcquire(_memPagesInfo[nextMemIdx], filePageIndex, exclusive))
        {
            return nextMemIdx;  // Fast path: adjacent allocation
        }
    }

    // 2. Clock-sweep first pass (find ClockSweepCounter == 0)
    int start = _clockSweepHand;
    int maxIter = _memPagesCount * 2;
    for (int i = 0; i < maxIter; i++)
    {
        var memIdx = (_clockSweepHand + i) % _memPagesCount;
        var pageInfo = _memPagesInfo[memIdx];

        if (pageInfo.ClockSweepCounter == 0)
        {
            if (TryAcquire(pageInfo, filePageIndex, exclusive))
            {
                _clockSweepHand = (memIdx + 1) % _memPagesCount;
                return memIdx;
            }
        }
        else
        {
            // Decrement counter (LRU approximation)
            Interlocked.Decrement(ref pageInfo.ClockSweepCounter);
        }
    }

    // 3. Clock-sweep second pass (ignore counter, take any free)
    // ... (fallback if first pass exhausted)
}
```

**ClockSweepCounter Management:**
- **Max value:** 5 (capped by `IncrementClockSweepCounter`)
- **Incremented:** On page access (via `TransitionPageToAccess`)
- **Decremented:** During clock-sweep eviction pass
- **Reset:** On page allocation

**Async I/O:**

Pages are loaded asynchronously to avoid blocking:

```csharp
private void FetchPageToMemory(int filePageIndex)
{
    var memIdx = AllocateMemoryPage(filePageIndex, exclusive: false);
    var pageInfo = _memPagesInfo[memIdx];

    // Start async read
    var offset = (long)filePageIndex * PageSize;
    var buffer = new Memory<byte>(_memPages, memIdx * PageSize, PageSize);
    pageInfo.IOReadTask = RandomAccess.ReadAsync(_fileHandle, buffer, offset);
}

public void EnsureDataReady(PageInfo pageInfo)
{
    if (pageInfo.IOReadTask != null && !pageInfo.IOReadTask.IsCompleted)
    {
        pageInfo.IOReadTask.Wait();  // Block until loaded
    }
}
```

**Batch Writes:**

Sequential pages are batched into a single write operation:

```csharp
private void FlushDirtyPages()
{
    var dirtyPages = CollectDirtyPages();  // Sorted by FilePageIndex

    foreach (var batch in GroupContiguous(dirtyPages))
    {
        var offset = (long)batch[0].FilePageIndex * PageSize;
        var length = batch.Count * PageSize;
        var buffer = GetContiguousBuffer(batch);

        RandomAccess.Write(_fileHandle, buffer, offset);  // Single syscall
    }
}
```

**See:** [Clock-Sweep Page Eviction Algorithm](#clock-sweep-page-eviction-algorithm)

#### 4.2 ManagedPagedMMF (Segment Management)

**File:** `src/Typhon.Engine/Persistence Layer/ManagedPagedMMF.cs`

`ManagedPagedMMF` extends `PagedMMF` with **segment allocation and occupancy tracking**.

**File Layout:**
```
File Page Layout:
┌─────────────────────────────────────────┐
│ Page 0: RootFileHeader (database metadata)│
├─────────────────────────────────────────┤
│ Page 1: Occupancy segment root          │
├─────────────────────────────────────────┤
│ Page 2-3: Reserved for occupancy growth │
├─────────────────────────────────────────┤
│ Page 4+: User segments (components, indexes, etc.)│
└─────────────────────────────────────────┘
```

**RootFileHeader:**
```csharp
internal struct RootFileHeader
{
    public byte[] HeaderSignature;         // "TyphonDatabase" (16 bytes)
    public int DatabaseFormatRevision;     // Version (4 bytes)
    public ulong DatabaseFilesChunkSize;   // Page size (8 bytes)
    public byte[] DatabaseName;            // UTF-8 name (64 bytes)
    public int OccupancyMapSPI;            // Segment page index
    public int SystemSchemaRevision;
    public int ComponentTableSPI;          // Component definitions segment
    public int FieldTableSPI;              // Field definitions segment
}
```

**Occupancy Bitmap (BitmapL3):**

Tracks which pages are allocated:

- **L0:** 64 bits per entry (64 pages)
- **L1:** 64×8 bits per entry (512 pages)
- **L2:** 64×8×8 bits per entry (4096 pages = 32MB)

Each occupancy page tracks **8000 bytes × 8 bits = 64,000 pages = 512MB**.

**Segment Allocation:**
```csharp
public ChunkBasedSegment AllocateChunkBasedSegment(
    PageBlockType type,
    int startingPages,
    int chunkSize)
{
    var segment = new ChunkBasedSegment();
    segment.Create(this, type, startingPages, chunkSize);
    return segment;
}
```

**Change Tracking:**

`ChangeSet` tracks dirty pages for persistence:

```csharp
public class ChangeSet
{
    private HashSet<int> _changedPages;  // File page indices

    public void RecordChange(int filePageIndex)
    {
        _changedPages.Add(filePageIndex);
    }

    public void Flush()
    {
        foreach (var pageIdx in _changedPages)
        {
            FlushPage(pageIdx);
        }
        _changedPages.Clear();
    }
}
```

**See:** [Segment Architecture](#segment-architecture)

---

## MVCC Implementation Deep Dive

### Revision Chain Structure

MVCC is the **core concurrency mechanism** in Typhon, enabling snapshot isolation.

**CompRevStorageHeader (64 bytes):**

The first chunk of every revision chain contains this header:

```csharp
internal struct CompRevStorageHeader
{
    public int NextChunkId;                  // Next chunk in chain (MUST BE FIRST)
    public AccessControlSmall Control;       // Thread-safe access (4 bytes)
    public int FirstItemRevision;            // Base revision number
    public short FirstItemIndex;             // Circular buffer head index
    public short ItemCount;                  // Total revisions in chain
    public short ChainLength;                // Number of chunks in chain
    public short LastCommitRevisionIndex;    // For conflict detection
}
```

**CompRevStorageElement (10 bytes):**

Each revision element stores metadata about a component version:

```csharp
internal struct CompRevStorageElement
{
    public int ComponentChunkId;        // Points to component data chunk
    private uint _packedTickHigh;       // Upper 32 bits of timestamp
    private ushort _packedTickLow;      // Lower 16 bits (15 bits tick + 1 bit flag)

    public bool IsolationFlag { get; set; }        // Bit 0: uncommitted flag
    public PackedDateTime48 DateTime { get; set; } // 48-bit timestamp (15 lower + 32 high bits)
}
```

**Bit Packing:**
- **IsolationFlag (1 bit):** Set when revision is uncommitted (invisible to other transactions)
- **DateTime (48 bits):** Compressed timestamp (trades precision for space)

**Chunk Layout:**

```
Root Chunk (64 bytes):
├─ CompRevStorageHeader (12 bytes)
│  ├─ NextChunkId (4 bytes)
│  ├─ Control (4 bytes)
│  ├─ FirstItemRevision (4 bytes)
│  ├─ FirstItemIndex (2 bytes)
│  ├─ ItemCount (2 bytes)
│  ├─ ChainLength (2 bytes)
│  └─ LastCommitRevisionIndex (2 bytes)
└─ CompRevStorageElement[0..4] (5 × 10 = 50 bytes)

Overflow Chunk (64 bytes):
├─ NextChunkId (4 bytes)
└─ CompRevStorageElement[0..5] (6 × 10 = 60 bytes)
```

**Circular Buffer:**

Revision chains use a **circular buffer** to efficiently manage old revisions:

```
Example: FirstItemIndex = 3, ItemCount = 8, ChainLength = 2

Chunk 0: [X][X][X][Rev3][Rev4]
             ↑ FirstItemIndex = 3

Chunk 1: [Rev5][Rev6][Rev7][Rev8][Rev9][Rev10]
                                         ↑ Newest

Total capacity: 5 (chunk 0) + 6 (chunk 1) = 11
Used: 8 items (Rev3 through Rev10)
```

**Growth & Wrap:**
- When full: allocate new chunk, increment `ChainLength`
- When oldest transaction commits: shift `FirstItemIndex` forward
- Defragmentation: `CleanUpUnusedEntries()` compacts the buffer

**See:** [Revision Cleanup (Garbage Collection)](#revision-cleanup-garbage-collection)

---

### Snapshot Isolation Mechanism

**How Transactions See Data:**

Each transaction has a `TransactionTick` (creation timestamp). When reading a component:

1. Retrieve the revision chain from `PrimaryKeyIndex`
2. Walk the chain from newest to oldest
3. Find the **first committed revision** with `DateTime ≤ TransactionTick`
4. Skip revisions with `IsolationFlag = true` (uncommitted)

**Code (simplified from Transaction.cs:713-771):**
```csharp
private CompRevInfo GetCompRevInfoFromIndex(long primaryKey, ComponentInfo ci)
{
    // Get revision chain entry point
    if (!ci.PrimaryKeyIndex.TryGet(primaryKey, out int firstChunkId))
        return default;  // Entity doesn't exist

    int curChunkId = 0;
    int prevChunkId = 0;
    int curRevisionIndex = -1;
    int prevRevisionIndex = -1;

    // Walk revision chain backwards (newest to oldest)
    using var enumerator = new RevisionEnumerator(
        ci.CompRevTableAccessor, firstChunkId, goToFirstItem: true);

    while (enumerator.MoveNext())
    {
        ref var element = ref enumerator.Current;

        // Stop at first revision after our snapshot point
        if (element.DateTime.Ticks > TransactionTick)
            break;

        // Skip isolated (uncommitted) revisions
        if (!element.IsolationFlag && element.DateTime.Ticks > 0)
        {
            prevRevisionIndex = curRevisionIndex;
            curRevisionIndex = enumerator.AbsoluteIndex;
            prevChunkId = curChunkId;
            curChunkId = element.ComponentChunkId;
        }
    }

    if (curRevisionIndex < 0)
        return default;  // No visible revision

    return new CompRevInfo
    {
        Operations = OperationType.Read,
        CompRevTableFirstChunkId = firstChunkId,
        PrevRevisionIndex = (short)prevRevisionIndex,
        CurRevisionIndex = (short)curRevisionIndex,
        PrevCompContentChunkId = prevChunkId,
        CurCompContentChunkId = curChunkId
    };
}
```

**Read-Your-Own-Writes:**

Transactions cache their own changes in `CompRevInfoCache`. When reading:

1. Check cache first (transaction's view)
2. If found: return cached revision (even if uncommitted)
3. If not found: query index and walk revision chain

This ensures transactions see their own uncommitted writes.

---

### Conflict Detection & Resolution

**Optimistic Concurrency Control:**

Typhon uses **optimistic locking**: no locks during transaction execution, conflicts detected at commit time.

**Conflict Detection (Transaction.cs:788-954):**

When committing a component update:

```csharp
private bool CommitComponent(
    long primaryKey,
    ref CompRevInfo compRevInfo,
    ComponentInfo ci,
    bool buildPhase)
{
    // Get revision chain header (with exclusive lock)
    ref var firstChunkHeader = ref GetRevChainHeader(compRevInfo.CompRevTableFirstChunkId);

    // CONFLICT DETECTION:
    // Check if another transaction committed a newer revision
    var hasConflict = firstChunkHeader.LastCommitRevisionIndex >= compRevInfo.CurRevisionIndex;

    if (hasConflict)
    {
        // CONFLICT RESOLUTION:

        if (buildPhase && _conflictSolver != null)
        {
            // Phase 1: Record conflict details
            _conflictSolver.RecordConflict(
                primaryKey,
                readVersion: compRevInfo.CurRevisionIndex,
                committedVersion: firstChunkHeader.LastCommitRevisionIndex,
                committingVersion: newRevisionIndex);
        }
        else
        {
            // Default: "last write wins"
            // Create new revision and copy data forward
            AddCompRev(ref compRevInfo, ci, ref firstChunkHeader);
            CopyComponentData(
                from: compRevInfo.PrevCompContentChunkId,
                to: compRevInfo.CurCompContentChunkId,
                ci);
        }
    }

    // Update metadata
    firstChunkHeader.LastCommitRevisionIndex = newRevisionIndex;
    SetRevisionDateTime(compRevInfo.CurRevisionIndex, _commitTime);
    ClearIsolationFlag(compRevInfo.CurRevisionIndex);  // Make visible

    // Update indexes
    UpdateIndices(primaryKey, compRevInfo, ci);

    return true;
}
```

**Conflict Resolution Strategies:**

1. **Last Write Wins (default):**
   - Create new revision
   - Copy data from previous version
   - Apply current transaction's changes on top
   - Automatically resolves conflicts

2. **Custom ConcurrencyConflictHandler:**
   - Two-phase commit: build phase records conflicts, apply phase resolves
   - Handler receives: read version, committed version, committing version
   - Can decide: accept, reject, or merge changes

**Example Custom Handler:**
```csharp
public class MergeConflictHandler : ConcurrencyConflictHandler
{
    public override void OnConflict(
        long primaryKey,
        short readVersion,
        short committedVersion,
        short committingVersion,
        ref ComponentData readData,
        ref ComponentData committedData,
        ref ComponentData committingData)
    {
        // Example: Merge numeric fields additively
        var read = (PlayerStats*)readData.Data;
        var committed = (PlayerStats*)committedData.Data;
        var committing = (PlayerStats*)committingData.Data;

        // Delta from read to committing
        var healthDelta = committing->Health - read->Health;

        // Apply delta to committed version
        committing->Health = committed->Health + healthDelta;

        // Keep other fields from committed version
        committing->Stamina = committed->Stamina;
    }
}
```

**See:** [Code Examples - Conflict Resolution](#conflict-resolution)

---

### Revision Cleanup (Garbage Collection)

**When to Clean Up:**

Old revisions are removed when:
1. The **oldest transaction** (tail of `TransactionChain`) commits or rolls back
2. The revision chain has **more items than root chunk capacity** (5 elements)

**CleanUpUnusedEntries() Algorithm (Transaction.cs:1095-1186):**

```csharp
private void CleanUpUnusedEntries(ComponentInfo ci)
{
    var nextMinTick = _transactionChain.GetNextMinTick(this);  // Next oldest transaction

    foreach (var kvp in ci.CompRevInfoCache)
    {
        var primaryKey = kvp.Key;
        var compRevInfo = kvp.Value;

        ref var header = ref GetRevChainHeader(compRevInfo.CompRevTableFirstChunkId);

        // Only clean up if chain is full
        if (header.ItemCount <= ComponentTable.CompRevCountInRoot)
            continue;

        int keepCount = 0;
        int firstKeptRevision = -1;

        // Walk chain, skip revisions older than nextMinTick
        using var enumerator = new RevisionEnumerator(...);
        while (enumerator.MoveNext())
        {
            ref var element = ref enumerator.Current;

            if (element.DateTime.Ticks >= nextMinTick)
            {
                if (firstKeptRevision < 0)
                    firstKeptRevision = enumerator.AbsoluteIndex;
                keepCount++;
            }
            else
            {
                // Free old component data chunk
                ci.CompContentSegment.FreeChunk(element.ComponentChunkId);
            }
        }

        // Defragment: compact kept revisions to start of chain
        if (firstKeptRevision > 0)
        {
            CompactRevisionChain(ref header, firstKeptRevision, keepCount, ci);
        }
    }
}
```

**Defragmentation:**

After removing old revisions, the circular buffer is compacted:

```
Before:
Chunk 0: [Rev1][Rev2][Rev3][Rev4][Rev5]  ← Rev1, Rev2 are old
         FirstItemIndex = 0, ItemCount = 7

Chunk 1: [Rev6][Rev7][X][X][X][X]

After cleanup (nextMinTick allows Rev3+):
Chunk 0: [Rev3][Rev4][Rev5][Rev6][Rev7]
         FirstItemIndex = 0, ItemCount = 5

Chunk 1: [X][X][X][X][X][X] (freed)
```

**Lazy Cleanup:**

Cleanup only happens when the **oldest transaction** commits, preventing:
- Premature deletion of revisions still visible to active transactions
- Excessive cleanup overhead on every commit

**Trade-Off:**
- **Pro:** Minimal overhead (only when tail transaction completes)
- **Con:** Long-running transactions can accumulate many old revisions

**See:** [Advanced Topics - Long-Running Transactions](#long-running-transactions)

---

## Concurrency Model & Thread Safety

### Overview

Typhon achieves **high concurrency** through:

1. **Lock-Free Reads:** Multiple transactions read simultaneously without locks
2. **Optimistic Writes:** No locks during execution; conflicts detected at commit
3. **Fine-Grained Locks:** Per-page and per-revision-chain locks (not global)
4. **Adaptive Waiting:** Spin-then-sleep for contended locks

### Concurrency Primitives

#### AccessControl (8 bytes)

**File:** `src/Typhon.Engine/Misc/AccessControl.cs`

A **reader-writer lock** using `SpinWait` for low-latency synchronization.

**Structure:**
```csharp
public struct AccessControl
{
    private volatile int _lockedByThreadId;      // Thread ID if exclusively locked
    private volatile int _sharedUsedCounter;     // Number of concurrent readers
}
```

**Reader Lock (Shared Access):**
```csharp
public void EnterSharedAccess()
{
    // Wait for exclusive lock to release
    if (_lockedByThreadId != 0)
    {
        var sw = new SpinWait();
        while (_lockedByThreadId != 0)
            sw.SpinOnce();
    }

    // Increment shared counter
    Interlocked.Increment(ref _sharedUsedCounter);

    // Double-check exclusive lock (prevent race)
    while (_lockedByThreadId != 0)
    {
        Interlocked.Decrement(ref _sharedUsedCounter);  // Rollback
        var sw = new SpinWait();
        while (_lockedByThreadId != 0)
            sw.SpinOnce();
        Interlocked.Increment(ref _sharedUsedCounter);  // Retry
    }
}

public void ExitSharedAccess()
{
    Interlocked.Decrement(ref _sharedUsedCounter);
}
```

**Writer Lock (Exclusive Access):**
```csharp
public void EnterExclusiveAccess()
{
    var threadId = Environment.CurrentManagedThreadId;

    // Try to acquire exclusive lock via CAS
    if (Interlocked.CompareExchange(ref _lockedByThreadId, threadId, 0) == 0)
    {
        // Success! Now wait for readers to finish
        if (_sharedUsedCounter == 0)
            return;  // Fast path: no readers

        var sw = new SpinWait();
        while (_sharedUsedCounter != 0)
            sw.SpinOnce();

        return;
    }

    // Slow path: wait for lock to be available
    var sw2 = new SpinWait();
    while (Interlocked.CompareExchange(ref _lockedByThreadId, threadId, 0) != 0)
        sw2.SpinOnce();

    // Wait for readers
    while (_sharedUsedCounter != 0)
        sw2.SpinOnce();
}

public void ExitExclusiveAccess()
{
    _lockedByThreadId = 0;
}
```

**Promotion (Shared → Exclusive):**
```csharp
public bool TryPromoteToExclusiveAccess()
{
    var threadId = Environment.CurrentManagedThreadId;

    // Only promote if we're the ONLY shared user
    if (_sharedUsedCounter != 1)
        return false;

    // Try exclusive lock
    if (Interlocked.CompareExchange(ref _lockedByThreadId, threadId, 0) != 0)
        return false;

    // Double-check we're still the only user
    if (_sharedUsedCounter != 1)
    {
        _lockedByThreadId = 0;  // Rollback
        return false;
    }

    return true;
}
```

**Use Cases:**
- `PageInfo.StateSyncRoot`: Protects page state transitions
- `TransactionChain._control`: Protects transaction list modifications
- `RevisionEnumerator`: Shared read, exclusive modify

#### AccessControlSmall (4 bytes)

**File:** `src/Typhon.Engine/Misc/AccessControlSmall.cs`

A **space-optimized reader-writer lock** packing data into a single `int`:

**Bit Layout:**
```
31                                12 11                               0
┌────────────────────────────────┬─────────────────────────────────┐
│     Thread ID (20 bits)        │  Shared Counter (12 bits)       │
└────────────────────────────────┴─────────────────────────────────┘
```

- **Bits 0-11:** Shared reader counter (max 4095 readers)
- **Bits 12-31:** Thread ID (max ~1 million threads)

**Structure:**
```csharp
public struct AccessControlSmall
{
    private volatile int _packedValue;

    private const int SharedCounterMask = 0x00000FFF;   // Bits 0-11
    private const int ThreadIdShift = 12;
    private const int ThreadIdMask = 0xFFFFF000;        // Bits 12-31
}
```

**Operations:**
```csharp
public void EnterSharedAccess()
{
    // Increment bits 0-11
    int oldValue, newValue;
    do
    {
        oldValue = _packedValue;
        if ((oldValue & ThreadIdMask) != 0)
        {
            // Exclusive lock held, spin-wait
            var sw = new SpinWait();
            while ((_packedValue & ThreadIdMask) != 0)
                sw.SpinOnce();
            continue;
        }

        newValue = oldValue + 1;  // Increment counter
    }
    while (Interlocked.CompareExchange(ref _packedValue, newValue, oldValue) != oldValue);
}

public void EnterExclusiveAccess()
{
    var threadId = Environment.CurrentManagedThreadId << ThreadIdShift;

    // Try to set bits 12-31 to threadId
    int oldValue, newValue;
    do
    {
        oldValue = _packedValue;
        if ((oldValue & ThreadIdMask) != 0)
        {
            // Already locked, spin-wait
            var sw = new SpinWait();
            while ((_packedValue & ThreadIdMask) != 0)
                sw.SpinOnce();
            continue;
        }

        newValue = (oldValue & SharedCounterMask) | threadId;
    }
    while (Interlocked.CompareExchange(ref _packedValue, newValue, oldValue) != oldValue);

    // Wait for shared readers to finish
    while ((_packedValue & SharedCounterMask) != 0)
    {
        var sw = new SpinWait();
        sw.SpinOnce();
    }
}
```

**Use Cases:**
- `CompRevStorageHeader.Control`: Protects revision chain header (tight memory constraints)

#### AdaptiveWaiter

**File:** `src/Typhon.Engine/Misc/AdaptiveWaiter.cs`

An **adaptive spin-then-sleep** waiter for lock contention.

**Strategy:**
- Start with **spin-wait** (65,536 iterations)
- If still contended: **sleep** (yield to OS scheduler)
- On each sleep: **halve** spin iterations (65536 → 32768 → 16384 → ...)
- Minimum: **10 iterations**

**Code:**
```csharp
public class AdaptiveWaiter
{
    private int _iterationCount = 1 << 16;  // 65536
    private int _curCount;

    public void Wait()
    {
        if (Environment.ProcessorCount == 1)
        {
            // Single CPU: always sleep
            Thread.Sleep(0);
            return;
        }

        if (_curCount++ < _iterationCount)
        {
            // Spin-wait
            Thread.SpinWait(100);
        }
        else
        {
            // Sleep and reduce future spin count
            Thread.Sleep(0);
            _curCount = 0;
            _iterationCount = Math.Max(10, _iterationCount / 2);
        }
    }
}
```

**Rationale:**
- **Low contention:** Spin-wait avoids context switches (faster)
- **High contention:** Sleep yields CPU to other threads (fairer)
- **Adaptive:** Adjusts based on observed contention

**Use Cases:**
- Lock acquisition retry loops
- Page state transition waits

### Lock-Free Patterns

#### 1. ConcurrentDictionary for Page Lookup

```csharp
private ConcurrentDictionary<int, int> _memPageIndexByFilePageIndex;
```

Fast, lock-free lookup from file page index to memory page index.

#### 2. Double-Check Locking in Page Acquisition

```csharp
private bool TryAcquire(PageInfo pageInfo, int filePageIndex, bool exclusive)
{
    // Fast path: unlocked check
    if (pageInfo.PageState != PageState.Free &&
        pageInfo.PageState != PageState.Idle)
        return false;

    // Slow path: acquire lock and double-check
    lock (pageInfo.StateSyncRoot)
    {
        if (pageInfo.PageState != PageState.Free &&
            pageInfo.PageState != PageState.Idle)
            return false;

        // Acquire page
        pageInfo.FilePageIndex = filePageIndex;
        pageInfo.PageState = PageState.Allocating;
        return true;
    }
}
```

#### 3. Atomic Primary Key Generation

```csharp
public long GetNewPrimaryKey()
{
    return Interlocked.Increment(ref _curPrimaryKey);
}
```

Lock-free, thread-safe entity ID generation.

#### 4. Transaction Pooling with Stack

```csharp
private Stack<Transaction> _transactionPool;  // Not thread-safe

// Protected by TransactionChain._control
_control.EnterExclusiveAccess();
try
{
    var t = _transactionPool.Count > 0
        ? _transactionPool.Pop()
        : new Transaction();
    // ...
}
finally
{
    _control.ExitExclusiveAccess();
}
```

Stack operations protected by exclusive lock (not lock-free, but efficient).

### Thread-Safety Guarantees

#### Read Operations

**Guarantee:** Multiple transactions can read concurrently without blocking.

**Mechanism:**
- Readers acquire **shared page access** (AccessControl)
- MVCC ensures each transaction sees a consistent snapshot
- IsolationFlag hides uncommitted writes from other transactions

**Example:**
```csharp
// Transaction T1 (tick: 100)
using var t1 = dbe.CreateTransaction();
t1.ReadEntity(1001, out PlayerStats stats);  // Sees version at tick ≤ 100

// Transaction T2 (tick: 110)
using var t2 = dbe.CreateTransaction();
t2.UpdateEntity(1001, ref newStats);  // Creates new revision at tick 110 (isolated)

// Transaction T3 (tick: 120)
using var t3 = dbe.CreateTransaction();
t3.ReadEntity(1001, out PlayerStats stats);  // Still sees version at tick ≤ 100 (T2 uncommitted)

t2.Commit();  // Clears IsolationFlag

// Transaction T4 (tick: 130)
using var t4 = dbe.CreateTransaction();
t4.ReadEntity(1001, out PlayerStats stats);  // Sees T2's committed version at tick 110
```

#### Write Operations

**Guarantee:** Writes are buffered until commit; conflicts detected optimistically.

**Mechanism:**
- Writes allocate new component chunks and revision elements
- IsolationFlag prevents visibility to other transactions
- At commit: acquire **exclusive revision chain lock**, detect conflicts, clear IsolationFlag

**Example:**
```csharp
// Transaction T1
using var t1 = dbe.CreateTransaction();
t1.UpdateEntity(1001, ref stats1);  // Buffered in transaction cache

// Transaction T2 (concurrent)
using var t2 = dbe.CreateTransaction();
t2.UpdateEntity(1001, ref stats2);  // Also buffered (no conflict yet)

// Commit T1 (succeeds)
t1.Commit();  // Updates LastCommitRevisionIndex

// Commit T2 (conflict detected)
t2.Commit();  // Detects conflict, creates new revision, applies "last write wins"
```

#### Index Operations

**Guarantee:** Indexes are updated atomically during commit.

**Mechanism:**
- Index B+Trees have internal locks (AccessControl per node)
- Updates happen during commit phase (after conflict resolution)
- Primary key index: only updated on entity creation (never modified)
- Secondary indexes: updated on field changes

### Deadlock Prevention

**Strategies:**

1. **No Nested Locks:** Transactions don't acquire multiple locks simultaneously
2. **Lock Ordering:** Locks acquired in consistent order (page locks before revision chain locks)
3. **Timeout-Free:** SpinWait doesn't timeout (eventual progress guaranteed)
4. **Optimistic Execution:** No locks during transaction execution (only at commit)

**Potential Deadlock Scenario (Avoided):**

```
Thread 1: Lock(Page A) → Lock(Page B)
Thread 2: Lock(Page B) → Lock(Page A)  ← Deadlock!
```

**Typhon's Solution:**
- **Read phase:** Only shared locks (no exclusivity)
- **Commit phase:** Locks acquired in deterministic order (by primary key, then page index)

---

## Performance Optimizations

### 1. Zero-Copy Data Access

**Technique:** Use pointers to access component data directly in memory-mapped pages.

**Example:**
```csharp
public bool ReadEntity<T>(long primaryKey, out T component) where T : unmanaged
{
    var ci = GetComponentInfo<T>();
    var compRevInfo = GetCompRevInfo(primaryKey, ci);

    if (compRevInfo.CurCompContentChunkId == 0)
    {
        component = default;
        return false;
    }

    // Zero-copy: get pointer to chunk, cast to T*
    using var handle = ci.CompContentAccessor.GetChunkHandle(
        compRevInfo.CurCompContentChunkId, dirty: false);

    var componentPtr = (T*)handle.Address;  // Direct pointer access
    component = *componentPtr;  // Single memcpy

    return true;
}
```

**Benefits:**
- No intermediate buffers
- Single memory copy (from page to output)
- Cache-friendly (spatial locality)

### 2. Object Pooling

**Transaction Pooling:**
```csharp
// TransactionChain.cs
private Stack<Transaction> _transactionPool;  // Max 16 instances

public void Remove(Transaction t)
{
    // ...
    if (_transactionPool.Count < 16)
    {
        t.Reset();  // Clear state
        _transactionPool.Push(t);  // Reuse later
    }
}
```

**Benefits:**
- Reduces GC pressure (fewer allocations)
- Reduces initialization overhead (reuse initialized objects)
- Predictable memory usage

### 3. Page Caching (Clock-Sweep)

**Algorithm:** Approximate LRU with O(1) eviction.

**Key Metrics:**
- **ClockSweepCounter (0-5):** Access recency indicator
- **ClockSweepHand:** Circular pointer to next eviction candidate

**Behavior:**
- On page access: increment `ClockSweepCounter` (max 5)
- On eviction sweep: decrement counters, evict when 0
- Sequential allocation: prefetch adjacent pages

**Example:**
```
Cache: [Page A (counter=3)] [Page B (counter=1)] [Page C (counter=5)] [Page D (counter=0)]
        ↑ ClockSweepHand

Eviction request:
1. Check A: counter=3 → decrement to 2, advance
2. Check B: counter=1 → decrement to 0, advance
3. Check C: counter=5 → decrement to 4, advance
4. Check D: counter=0 → EVICT! Return D
```

**Benefits:**
- Fast eviction (no sorting, no heap)
- Good approximation of LRU (recently used pages have higher counters)
- Batching: sequential pages allocated adjacently for batch writes

**See:** [Clock-Sweep Page Eviction Algorithm](#clock-sweep-page-eviction-algorithm)

### 4. Sequential Page Allocation

**Optimization:** Allocate adjacent memory pages for contiguous file pages.

**Code (PagedMMF.cs:445):**
```csharp
private int AllocateMemoryPage(int filePageIndex, bool exclusive)
{
    // Prefetch: if filePageIndex-1 is in memory page N, try to use N+1
    if (filePageIndex > 0 &&
        _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemIdx))
    {
        var nextMemIdx = (prevMemIdx + 1) % _memPagesCount;
        if (TryAcquire(_memPagesInfo[nextMemIdx], filePageIndex, exclusive))
        {
            return nextMemIdx;  // Sequential allocation success
        }
    }

    // Otherwise, use clock-sweep...
}
```

**Benefits:**
- Enables **batch writes**: contiguous memory → single `RandomAccess.Write()` syscall
- Reduces disk seeks (file pages written sequentially)
- Improves OS page cache efficiency

**Example:**
```
File pages: [100][101][102][103]
Memory pages: [M10][M11][M12][M13]

Write batch: M10-M13 → single Write() at offset 100*8192, length 4*8192
```

### 5. Adaptive Waiting

**Strategy:** Spin-wait under low contention, sleep under high contention.

**Code (AdaptiveWaiter.cs):**
```csharp
public void Wait()
{
    if (_curCount++ < _iterationCount)
    {
        Thread.SpinWait(100);  // Burn CPU cycles
    }
    else
    {
        Thread.Sleep(0);  // Yield to OS scheduler
        _iterationCount = Math.Max(10, _iterationCount / 2);  // Adapt
    }
}
```

**Benefits:**
- Low contention: avoids context switch overhead (microseconds faster)
- High contention: yields CPU to other threads (better throughput)
- Self-tuning: adapts to workload characteristics

### 6. Chunk-Based Allocation

**Fixed-Size Chunks:**
- Components: variable size per type (e.g., 64 bytes for PlayerStats)
- Revisions: fixed 64 bytes
- Index nodes: fixed 64 bytes

**Occupancy Bitmap (BitmapL3):**
- **3-level hierarchy:** L0 (64 bits) → L1 (512 bits) → L2 (4096 bits)
- **Allocation:** Search L2 → L1 → L0 for first set bit (O(1) amortized)
- **Deallocation:** Clear bit in L0, propagate to L1/L2

**Benefits:**
- Fast allocation (no free list traversal)
- Low fragmentation (fixed sizes)
- Compact metadata (3-level bitmap vs. linked lists)

**See:** [Occupancy Bitmap Hierarchy](#occupancy-bitmap-hierarchy)

### 7. Pinned Memory (GCHandle)

**Technique:** Pin page cache memory to prevent GC relocations.

**Code (PagedMMF.cs):**
```csharp
private GCHandle _memPagesHandle;
private byte[] _memPages;

public void Initialize()
{
    _memPages = new byte[_memPagesCount * PageSize];
    _memPagesHandle = GCHandle.Alloc(_memPages, GCHandleType.Pinned);
}

public void Dispose()
{
    _memPagesHandle.Free();
}
```

**Benefits:**
- Stable pointers (no GC moves invalidate addresses)
- Reduces GC overhead (pinned memory not moved during compaction)
- Enables unsafe pointer access

### 8. Blittable Component Structs

**Requirement:** Components must be **unmanaged** (no managed references).

**Example:**
```csharp
[Component]
public struct ValidComponent  // ✓ Blittable
{
    [Field] public int PlayerId;
    [Field] public float Health;
    [Field] public String64 Name;  // Fixed-size string (64 bytes)
}

[Component]
public struct InvalidComponent  // ✗ Not blittable
{
    [Field] public int PlayerId;
    [Field] public string Name;  // Managed reference (not allowed)
}
```

**Benefits:**
- **Zero-copy:** Direct memcpy between pages and structs
- **Predictable layout:** `StructLayout.Sequential` ensures consistent offsets
- **Cache-friendly:** Compact, contiguous memory

### 9. Lazy I/O (Async Reads)

**Technique:** Start async reads immediately, block only when data is accessed.

**Code (PagedMMF.cs):**
```csharp
private void FetchPageToMemory(int filePageIndex)
{
    var memIdx = AllocateMemoryPage(filePageIndex, exclusive: false);
    var pageInfo = _memPagesInfo[memIdx];

    // Start async read (doesn't block)
    var offset = (long)filePageIndex * PageSize;
    var buffer = new Memory<byte>(_memPages, memIdx * PageSize, PageSize);
    pageInfo.IOReadTask = RandomAccess.ReadAsync(_fileHandle, buffer, offset);
}

public void EnsureDataReady(PageInfo pageInfo)
{
    if (pageInfo.IOReadTask != null && !pageInfo.IOReadTask.IsCompleted)
    {
        pageInfo.IOReadTask.Wait();  // Block only if not ready
    }
}
```

**Benefits:**
- **Overlapping I/O:** Multiple reads in flight simultaneously
- **Reduced latency:** Data often ready before first access
- **Better throughput:** Disk I/O parallelized with CPU work

### 10. ChangeSet Batching

**Technique:** Track dirty pages, flush in batches.

**Code (ManagedPagedMMF.cs):**
```csharp
public class ChangeSet
{
    private HashSet<int> _changedPages;

    public void RecordChange(int filePageIndex)
    {
        _changedPages.Add(filePageIndex);
    }

    public void Flush()
    {
        var sortedPages = _changedPages.OrderBy(p => p).ToList();

        foreach (var batch in GroupContiguous(sortedPages))
        {
            WriteBatch(batch);  // Single syscall for contiguous pages
        }

        _changedPages.Clear();
    }
}
```

**Benefits:**
- **Reduced syscalls:** Batch writes instead of individual page writes
- **Sequential writes:** Sorted pages → better disk performance
- **Deferred writes:** Write at transaction commit (not on every update)

---

## Key Algorithms

### Clock-Sweep Page Eviction Algorithm

**Purpose:** Evict least-recently-used pages from cache when full.

**Data Structures:**
```csharp
private PageInfo[] _memPagesInfo;  // Metadata per cached page
private int _clockSweepHand;       // Circular pointer (0 to _memPagesCount-1)

class PageInfo
{
    public int ClockSweepCounter;  // 0-5, access recency
    // ...
}
```

**Algorithm (simplified from PagedMMF.cs:421-576):**

```csharp
private int AllocateMemoryPage(int filePageIndex, bool exclusive)
{
    // Phase 1: Try sequential allocation (prefetch)
    if (TrySequentialAllocation(filePageIndex, exclusive, out var memIdx))
        return memIdx;

    // Phase 2: Clock-sweep first pass (find counter == 0)
    int start = _clockSweepHand;
    int maxIter = _memPagesCount * 2;

    for (int i = 0; i < maxIter; i++)
    {
        memIdx = (_clockSweepHand + i) % _memPagesCount;
        var pageInfo = _memPagesInfo[memIdx];

        if (pageInfo.ClockSweepCounter == 0)
        {
            if (TryAcquire(pageInfo, filePageIndex, exclusive))
            {
                _clockSweepHand = (memIdx + 1) % _memPagesCount;
                return memIdx;
            }
        }
        else
        {
            // Decrement counter (age the page)
            Interlocked.Decrement(ref pageInfo.ClockSweepCounter);
        }
    }

    // Phase 3: Clock-sweep second pass (ignore counter, take first free)
    for (int i = 0; i < _memPagesCount; i++)
    {
        memIdx = (_clockSweepHand + i) % _memPagesCount;
        var pageInfo = _memPagesInfo[memIdx];

        if (TryAcquire(pageInfo, filePageIndex, exclusive))
        {
            _clockSweepHand = (memIdx + 1) % _memPagesCount;
            return memIdx;
        }
    }

    // Phase 4: No free pages (should never happen)
    throw new InvalidOperationException("Page cache exhausted");
}
```

**Visualization:**
```
Initial state:
┌─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┐
│ P0  │ P1  │ P2  │ P3  │ P4  │ P5  │ P6  │ P7  │
│ C=2 │ C=1 │ C=0 │ C=3 │ C=5 │ C=0 │ C=4 │ C=1 │
└─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┘
  ↑ ClockSweepHand

Eviction request:
1. Check P0 (C=2): decrement to C=1, advance
2. Check P1 (C=1): decrement to C=0, advance
3. Check P2 (C=0): EVICT! Return P2

New state:
┌─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┐
│ P0  │ P1  │ NEW │ P3  │ P4  │ P5  │ P6  │ P7  │
│ C=1 │ C=0 │ C=0 │ C=3 │ C=5 │ C=0 │ C=4 │ C=1 │
└─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┘
              ↑ ClockSweepHand
```

**Complexity:**
- **Best case:** O(1) (first page has counter == 0)
- **Worst case:** O(N) (all counters > 0, first pass decrements all)
- **Amortized:** O(1) (counters bounded to 0-5)

**Trade-Offs:**
- **Accuracy:** Approximate LRU (not perfect)
- **Overhead:** Very low (single integer increment/decrement)
- **Fairness:** Frequently accessed pages survive longer (higher counters)

**References:**
- [LRU-K and Clock Algorithms (Wikipedia)](https://en.wikipedia.org/wiki/Page_replacement_algorithm#Clock)
- [Buffer Management in Database Systems (Gray & Reuter)](https://www.microsoft.com/en-us/research/publication/transaction-processing-concepts-and-techniques/)

---

### Occupancy Bitmap Hierarchy

**Purpose:** Fast chunk allocation/deallocation with O(1) amortized complexity.

**Data Structure (BitmapL3):**

```
L2 Array (64 entries):
┌───────┬───────┬───────┬─────┬───────┐
│ L2[0] │ L2[1] │ L2[2] │ ... │ L2[63]│  Each: 1 bit
└───┬───┴───────┴───────┴─────┴───────┘
    │
    ├─→ L1 Array (8 entries):
    │   ┌───────┬───────┬─────┬───────┐
    │   │ L1[0] │ L1[1] │ ... │ L1[7] │  Each: 1 bit
    │   └───┬───┴───────┴─────┴───────┘
    │       │
    │       ├─→ L0 Array (8 entries):
    │       │   ┌──────────┬──────────┬─────┬──────────┐
    │       │   │  L0[0]   │  L0[1]   │ ... │  L0[7]   │  Each: 64 bits
    │       │   └──────────┴──────────┴─────┴──────────┘
    │       │        ↓
    │       │   Chunk occupancy: bit 1 = free, bit 0 = used
```

**Capacity:**
- L0: 64 bits → 64 chunks
- L1: 8 × 64 bits = 512 chunks
- L2: 64 × 8 × 64 bits = **32,768 chunks**

**Allocation Algorithm:**

```csharp
public int AllocateChunk()
{
    // Search L2 for first set bit
    for (int i2 = 0; i2 < 64; i2++)
    {
        if ((_l2Bitmap & (1UL << i2)) == 0)
            continue;  // L2[i2] is full

        // Search L1 for first set bit
        for (int i1 = 0; i1 < 8; i1++)
        {
            if ((_l1Bitmap[i2] & (1UL << i1)) == 0)
                continue;  // L1[i2][i1] is full

            // Search L0 for first set bit
            var l0Index = i2 * 8 + i1;
            var freeBit = FindFirstSetBit(_l0Bitmap[l0Index]);

            if (freeBit < 0)
            {
                // L0 is full, update L1
                _l1Bitmap[i2] &= ~(1UL << i1);
                if (_l1Bitmap[i2] == 0)
                {
                    // L1 is full, update L2
                    _l2Bitmap &= ~(1UL << i2);
                }
                continue;
            }

            // Allocate chunk
            var chunkId = l0Index * 64 + freeBit;
            _l0Bitmap[l0Index] &= ~(1UL << freeBit);  // Clear bit (mark used)

            // Update L1/L2 if L0 is now full
            if (_l0Bitmap[l0Index] == 0)
            {
                _l1Bitmap[i2] &= ~(1UL << i1);
                if (_l1Bitmap[i2] == 0)
                    _l2Bitmap &= ~(1UL << i2);
            }

            return chunkId;
        }
    }

    throw new OutOfMemoryException("No free chunks");
}
```

**Deallocation Algorithm:**

```csharp
public void FreeChunk(int chunkId)
{
    var l0Index = chunkId / 64;
    var bitIndex = chunkId % 64;
    var i2 = l0Index / 8;
    var i1 = l0Index % 8;

    // Set bit in L0 (mark free)
    _l0Bitmap[l0Index] |= (1UL << bitIndex);

    // Propagate to L1
    _l1Bitmap[i2] |= (1UL << i1);

    // Propagate to L2
    _l2Bitmap |= (1UL << i2);
}
```

**Complexity:**
- **Allocation:** O(1) amortized (bounded by hierarchy depth: 3 levels)
- **Deallocation:** O(1) exact (constant-time bit operations)

**Example:**

```
Initial state (some chunks allocated):
L2: 0xFFFFFFFFFFFFFFFF (all L1 arrays have free chunks)
L1[0]: 0xFF (all L0 arrays have free chunks)
L0[0]: 0b1111111111111110 (chunk 0 used, others free)

AllocateChunk():
1. Check L2[0]: bit set → proceed to L1
2. Check L1[0][0]: bit set → proceed to L0
3. L0[0]: FindFirstSetBit(0b1111111111111110) = 1
4. Clear bit 1: L0[0] = 0b1111111111111100
5. Return chunkId = 0*64 + 1 = 1

FreeChunk(0):
1. Set bit 0 in L0[0]: L0[0] = 0b1111111111111101
2. Set bit 0 in L1[0]: L1[0] = 0xFF (already set)
3. Set bit 0 in L2: L2 = 0xFFFFFFFFFFFFFFFF (already set)
```

**References:**
- [Bitmap Index (Database Internals, Alex Petrov)](https://www.databass.dev/)

---

### B+Tree Index Implementation

**Purpose:** Efficient range queries and point lookups with O(log N) complexity.

**File:** `src/Typhon.Engine/Database Engine/BPTree/BTree.cs`

**Type Hierarchy:**
```
IBTree (interface)
  ├─ L16BTree (16-bit keys: byte, short, ushort, etc.)
  ├─ L32BTree (32-bit keys: int, uint, float, etc.)
  ├─ L64BTree (64-bit keys: long, ulong, double, DateTime, etc.)
  └─ String64BTree (64-byte fixed strings)

Each has two variants:
  - SingleBTree: unique keys (one value per key)
  - MultipleBTree: non-unique keys (multiple values per key)
```

**B+Tree Structure:**

```
Root Node (internal):
┌─────────────────────────────────────────┐
│ Key[0]=100 | Key[1]=200 | Key[2]=300   │
│ Child[0]   | Child[1]   | Child[2]     │
└─────────────────────────────────────────┘
     ↓              ↓              ↓
  ┌──────┐       ┌──────┐       ┌──────┐
  │ Leaf │       │ Leaf │       │ Leaf │
  │ 1-99 │       │100-199│      │200-299│
  └──────┘       └──────┘       └──────┘
```

**Node Storage:**

Nodes are stored as fixed-size chunks in `ChunkBasedSegment`:

```csharp
public class L64BTree
{
    private ChunkBasedSegment _nodeSegment;  // 64-byte chunks
    private int _rootChunkId;

    internal struct InternalNode
    {
        public int ChildCount;
        public long[] Keys;       // Variable length (fit in 64 bytes)
        public int[] ChildIds;    // Chunk IDs of children
    }

    internal struct LeafNode
    {
        public int Count;
        public long[] Keys;       // Variable length
        public int[] Values;      // For SingleBTree: single value
                                  // For MultipleBTree: ElementId in VariableSizedBufferSegment
        public int NextLeafId;    // Linked list for range queries
    }
}
```

**Insert Algorithm:**

```csharp
public void Add(long key, int value)
{
    if (_rootChunkId == 0)
    {
        // Create root leaf
        _rootChunkId = CreateLeafNode();
        InsertIntoLeaf(_rootChunkId, key, value);
        return;
    }

    // Navigate to leaf
    var path = new Stack<(int nodeId, int index)>();
    var leafId = FindLeaf(key, path);

    // Insert into leaf
    using var handle = _nodeSegment.GetChunkHandle(leafId, dirty: true);
    ref var leaf = ref *(LeafNode*)handle.Address;

    if (leaf.Count < MaxLeafEntries)
    {
        // Simple insert (no split)
        InsertIntoLeaf(ref leaf, key, value);
    }
    else
    {
        // Split leaf
        var (newLeafId, middleKey) = SplitLeaf(ref leaf, key, value);

        // Propagate split up the tree
        PropagateInsert(path, middleKey, newLeafId);
    }
}
```

**Lookup Algorithm:**

```csharp
public bool TryGet(long key, out int value)
{
    if (_rootChunkId == 0)
    {
        value = 0;
        return false;
    }

    // Navigate to leaf
    var leafId = FindLeaf(key, path: null);

    using var handle = _nodeSegment.GetChunkHandle(leafId, dirty: false);
    ref var leaf = ref *(LeafNode*)handle.Address;

    // Binary search in leaf
    var index = BinarySearch(leaf.Keys, leaf.Count, key);

    if (index >= 0)
    {
        value = leaf.Values[index];
        return true;
    }

    value = 0;
    return false;
}
```

**Multiple Value Indexes:**

For non-unique indexes (e.g., `PlayerId` where multiple entities have same ID):

```csharp
public class L32MultipleBTree
{
    private VariableSizedBufferSegment _valueSegment;  // Stores int[] arrays

    public void Add(int key, int value)
    {
        if (TryGet(key, out int elementId))
        {
            // Key exists, append to value array
            AppendToElement(elementId, value);
        }
        else
        {
            // New key, create value array
            var elementId = _valueSegment.AllocateElement(new[] { value });
            _btree.Add(key, elementId);
        }
    }

    public int[] GetAll(int key)
    {
        if (_btree.TryGet(key, out int elementId))
        {
            return _valueSegment.ReadElement(elementId);
        }
        return Array.Empty<int>();
    }
}
```

**Thread Safety:**

Each node has an `AccessControl` lock:

```csharp
internal struct NodeHeader
{
    public AccessControl Lock;
    public bool IsLeaf;
    public int Count;
}

// During insert
node.Lock.EnterExclusiveAccess();
try
{
    // Modify node...
}
finally
{
    node.Lock.ExitExclusiveAccess();
}
```

**Complexity:**
- **Insert:** O(log N) (tree height)
- **Lookup:** O(log N)
- **Range Query:** O(log N + K) (K = result size)
- **Delete:** O(log N)

**References:**
- [B+Tree (Wikipedia)](https://en.wikipedia.org/wiki/B%2B_tree)
- [Modern B-Tree Techniques (Goetz Graefe)](https://w6113.github.io/files/papers/btreesurvey-graefe.pdf)

---

## Data Structures

### Segment Architecture

**Hierarchy:**

```
LogicalSegment (abstract multi-page abstraction)
  ├─ ChunkBasedSegment (fixed-size chunks)
  ├─ VariableSizedBufferSegment (variable-size elements)
  └─ StringTableSegment (string storage)
```

#### LogicalSegment

**File:** `src/Typhon.Engine/Persistence Layer/LogicalSegment.cs`

**Purpose:** Abstract a contiguous logical address space across multiple physical pages.

**Structure:**

```
Root Page (Page 0):
┌───────────────────────────────────────────────────┐
│ LogicalSegmentHeader (8 bytes)                    │
│   - NextMapPBID (page index of next directory)    │
│   - NextRawDataPBID (page index of next data)     │
├───────────────────────────────────────────────────┤
│ PageDirectory (500 × 4 bytes = 2000 bytes)        │
│   - Page indices for directory/data pages         │
├───────────────────────────────────────────────────┤
│ RawData (6000 bytes)                              │
│   - Actual segment data                           │
└───────────────────────────────────────────────────┘

Directory Pages (overflow):
┌───────────────────────────────────────────────────┐
│ PageDirectory (2000 × 4 bytes = 8000 bytes)       │
└───────────────────────────────────────────────────┘

Data Pages (overflow):
┌───────────────────────────────────────────────────┐
│ RawData (8000 bytes)                              │
└───────────────────────────────────────────────────┘
```

**Capacity:**
- **Root page:** 6000 bytes data + 500 page indices
- **Data pages:** 8000 bytes each
- **Directory pages:** 2000 page indices each

**Address Translation:**

```csharp
public void GetPageAndOffset(long logicalAddress, out int pageIndex, out int offset)
{
    if (logicalAddress < RootPageRawDataSize)
    {
        // In root page
        pageIndex = _rootPageIndex;
        offset = LogicalSegmentHeaderSize + PageDirectoryRootSize + (int)logicalAddress;
        return;
    }

    // In overflow page
    var adjustedAddr = logicalAddress - RootPageRawDataSize;
    var pageNum = (int)(adjustedAddr / PageRawDataSize);
    offset = (int)(adjustedAddr % PageRawDataSize) + PageHeaderSize;

    // Lookup page index in directory
    pageIndex = GetPageIndexFromDirectory(pageNum);
}
```

#### ChunkBasedSegment

**File:** `src/Typhon.Engine/Persistence Layer/ChunkBasedSegment.cs`

**Purpose:** Fixed-size chunk allocation (components, revisions, index nodes).

**Properties:**
```csharp
public class ChunkBasedSegment : LogicalSegment
{
    public int Stride { get; private set; }              // Chunk size in bytes
    public int ChunkCountRootPage { get; private set; }  // Chunks in first page
    public int ChunkCountPerPage { get; private set; }   // Chunks per overflow page

    private BitmapL3 _map;  // Occupancy tracking
}
```

**Chunk ID Layout:**
- Chunk 0: Reserved (null reference)
- Chunk 1+: Usable chunks

**Allocation:**
```csharp
public int AllocateChunk(bool clearContent)
{
    var chunkId = _map.AllocateBit();  // Find free chunk via bitmap

    if (clearContent)
    {
        // Zero-initialize chunk
        var addr = GetChunkAddress(chunkId);
        NativeMemory.Clear(addr, (uint)Stride);
    }

    return chunkId;
}
```

**Deallocation:**
```csharp
public void FreeChunk(int chunkId)
{
    _map.FreeBit(chunkId);  // Mark chunk as free
}
```

**Example Usage:**
```csharp
// ComponentSegment: variable chunk size per component type
var componentSegment = mmf.AllocateChunkBasedSegment(
    PageBlockType.None,
    startingPages: 4,
    chunkSize: 64);  // 64-byte components

// RevisionSegment: fixed 64-byte chunks
var revisionSegment = mmf.AllocateChunkBasedSegment(
    PageBlockType.None,
    startingPages: 4,
    chunkSize: 64);
```

---

### ChunkRandomAccessor

**File:** `src/Typhon.Engine/Persistence Layer/ChunkRandomAccessor.cs`

**Purpose:** Cache recently accessed pages for efficient chunk access.

**Cache Strategy:**

```csharp
public class ChunkRandomAccessor
{
    private const int MaxCachedPages = 8;  // Default cache size

    private struct CachedEntry
    {
        public int HitCount;                 // Access frequency (LRU)
        public short PinCounter;             // Prevents eviction
        public PagedMMF.PageState CurrentPageState;
        public short IsDirty;                // Needs flush
        public short PromoteCounter;         // Exclusive lock depth
        public byte* BaseAddress;            // Cached page pointer
    }

    private CachedEntry[] _cachedEntries;
    private int[] _cachedPageIndices;  // File page index per cache slot
}
```

**Access Pattern:**

```csharp
public ChunkHandle GetChunkHandle(int chunkId, bool dirty)
{
    var (pageIndex, offset) = GetChunkLocation(chunkId);
    var cacheIndex = FindOrLoadPage(pageIndex);

    ref var entry = ref _cachedEntries[cacheIndex];
    entry.HitCount++;  // Update LRU
    entry.PinCounter++;  // Prevent eviction

    if (dirty)
        entry.IsDirty = 1;

    return new ChunkHandle
    {
        Address = entry.BaseAddress + offset,
        CacheIndex = cacheIndex,
        Accessor = this
    };
}
```

**Eviction (LRU):**

```csharp
private int FindOrLoadPage(int pageIndex)
{
    // Check if already cached
    for (int i = 0; i < MaxCachedPages; i++)
    {
        if (_cachedPageIndices[i] == pageIndex)
            return i;  // Cache hit
    }

    // Cache miss: find victim (lowest HitCount)
    int victimIndex = -1;
    int lowestHitCount = int.MaxValue;

    for (int i = 0; i < MaxCachedPages; i++)
    {
        if (_cachedEntries[i].PinCounter > 0)
            continue;  // Skip pinned entries

        if (_cachedEntries[i].HitCount < lowestHitCount)
        {
            lowestHitCount = _cachedEntries[i].HitCount;
            victimIndex = i;
        }
    }

    if (victimIndex < 0)
        throw new InvalidOperationException("All pages pinned");

    // Evict victim and load new page
    EvictPage(victimIndex);
    LoadPage(victimIndex, pageIndex);

    return victimIndex;
}
```

**Pin/Unpin:**

```csharp
public struct ChunkHandle : IDisposable
{
    public byte* Address;
    private int CacheIndex;
    private ChunkRandomAccessor Accessor;

    public void Dispose()
    {
        ref var entry = ref Accessor._cachedEntries[CacheIndex];
        entry.PinCounter--;  // Unpin
    }
}
```

**Promotion (Shared → Exclusive):**

```csharp
public void PromoteChunk(int chunkId)
{
    var (pageIndex, _) = GetChunkLocation(chunkId);
    var cacheIndex = FindCachedPage(pageIndex);

    ref var entry = ref _cachedEntries[cacheIndex];

    if (entry.PromoteCounter == 0)
    {
        // Request exclusive access from PagedMMF
        _pagedMMF.PromotePageToExclusive(pageIndex);
        entry.CurrentPageState = PageState.Exclusive;
    }

    entry.PromoteCounter++;
}

public void DemoteChunk(int chunkId)
{
    var (pageIndex, _) = GetChunkLocation(chunkId);
    var cacheIndex = FindCachedPage(pageIndex);

    ref var entry = ref _cachedEntries[cacheIndex];
    entry.PromoteCounter--;

    if (entry.PromoteCounter == 0)
    {
        // Release exclusive access
        _pagedMMF.DemotePageToShared(pageIndex);
        entry.CurrentPageState = PageState.Shared;
    }
}
```

**Object Pooling:**

```csharp
private static ConcurrentBag<ChunkRandomAccessor> _pool = new();

public static ChunkRandomAccessor Get(ChunkBasedSegment segment)
{
    if (_pool.TryTake(out var accessor))
    {
        accessor.Initialize(segment);
        return accessor;
    }
    return new ChunkRandomAccessor(segment);
}

public void ReturnToPool()
{
    Reset();
    _pool.Add(this);
}
```

---

## Transaction Lifecycle

### Complete Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│ 1. CreateTransaction()                                      │
│   - TransactionChain.CreateTransaction()                    │
│   - Pop from pool or allocate new                           │
│   - Set TransactionTick = DateTime.UtcNow.Ticks             │
│   - State = Created                                         │
│   - Insert at head of doubly-linked list                    │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│ 2. CreateEntity(ref component1, ref component2, ...)        │
│   For each component:                                       │
│   - GetComponentInfo(type) → create/fetch cache             │
│   - AllocateChunk() → get component storage chunk           │
│   - AllocCompRevStorage() → create revision header chunk    │
│   - Add to CompRevInfoCache with Operation=Created          │
│   - Copy component data into chunk                          │
│   Returns: new primary key (via GetNewPrimaryKey)           │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│ 3. ReadEntity(pk, out component)                            │
│   - Check CompRevInfoCache (transaction's view)             │
│   - If not cached:                                          │
│     • PrimaryKeyIndex.TryGet(pk) → firstChunkId             │
│     • Walk RevisionEnumerator (newest → oldest)             │
│     • Find revision with DateTime ≤ TransactionTick         │
│     • Skip IsolationFlag=true (uncommitted)                 │
│     • Cache result in CompRevInfoCache                      │
│   - Copy component data to output                           │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│ 4. UpdateEntity(pk, ref newComponent)                       │
│   - GetComponentInfo(type)                                  │
│   - Get/create CompRevInfo from cache                       │
│   - If first mutation:                                      │
│     • AddCompRev() → create new revision element            │
│     • Set IsolationFlag=true (uncommitted)                  │
│   - Copy new data to component chunk                        │
│   - Update CompRevInfoCache with Operation|=Updated         │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│ 5. Commit()                                                 │
│   Phase 1: Build (if ConflictSolver):                       │
│     - Iterate CompRevInfoCache                              │
│     - Detect conflicts (LastCommitRevisionIndex)            │
│     - Record conflicts in solver                            │
│                                                             │
│   Phase 2: Apply:                                           │
│     For each component type:                                │
│       For each modified entity:                             │
│         - CommitComponent():                                │
│           • Acquire exclusive lock on revision chain        │
│           • Detect conflicts                                │
│           • Resolve conflicts (default: last write wins)    │
│           • Update indexes (primary + secondary)            │
│           • Set DateTime = CommitTime                       │
│           • Clear IsolationFlag (make visible)              │
│           • Update LastCommitRevisionIndex                  │
│         - If oldest transaction:                            │
│           • CleanUpUnusedEntries() (garbage collect)        │
│                                                             │
│   - TransactionChain.Remove(this)                           │
│   - State = Committed                                       │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│ 6. Dispose()                                                │
│   - If State != Committed && State != Rollbacked:           │
│     • Rollback() (auto-rollback)                            │
│   - Release resources                                       │
│   - Return to pool (if pool.Count < 16)                     │
└─────────────────────────────────────────────────────────────┘
```

### Step-by-Step Example

```csharp
// Component definition
[Component]
public struct PlayerStats
{
    [Field] [Index] public int PlayerId;
    [Field] public float Health;
    [Field] public float Stamina;
}

// Setup
using var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponent<PlayerStats>();

// ===== STEP 1: Create Transaction =====
using var t1 = dbe.CreateTransaction();
// t1.TransactionTick = 638400000000000 (example)
// t1.State = Created

// ===== STEP 2: Create Entity =====
var stats = new PlayerStats
{
    PlayerId = 1001,
    Health = 100f,
    Stamina = 80f
};
long entityId = t1.CreateEntity(ref stats);
// entityId = 1 (first entity)
// t1.State = InProgress
// CompRevInfoCache[1] = {
//   Operations = Created,
//   CompRevTableFirstChunkId = 1,
//   CurRevisionIndex = 0,
//   CurCompContentChunkId = 1
// }

// ===== STEP 3: Read Entity (same transaction) =====
bool found = t1.ReadEntity(entityId, out PlayerStats readStats);
// found = true
// readStats = { PlayerId=1001, Health=100, Stamina=80 }
// (Read from cache, no index lookup)

// ===== STEP 4: Update Entity =====
stats.Health = 90f;
t1.UpdateEntity(entityId, ref stats);
// CompRevInfoCache[1].Operations |= Updated
// (Data copied to component chunk, IsolationFlag=true)

// ===== STEP 5: Commit =====
bool committed = t1.Commit();
// committed = true
// - Revision chain updated: LastCommitRevisionIndex = 0
// - DateTime set to commit time
// - IsolationFlag cleared (visible to other transactions)
// - PrimaryKeyIndex updated: 1 → ChunkId 1
// - Secondary index (PlayerId) updated: 1001 → ChunkId 1
// t1.State = Committed

// ===== STEP 6: Dispose (automatic) =====
// using block ends, t1.Dispose() called
// - Transaction removed from chain
// - Returned to pool
```

### Concurrent Transaction Example

```csharp
// Transaction T1 (tick: 100)
using var t1 = dbe.CreateTransaction();
var stats1 = new PlayerStats { PlayerId = 1001, Health = 100f };
var e1 = t1.CreateEntity(ref stats1);
t1.Commit();  // Revision 0 committed at tick 100

// Transaction T2 (tick: 110)
using var t2 = dbe.CreateTransaction();
t2.ReadEntity(e1, out var stats2);  // Sees revision 0
stats2.Health = 90f;
t2.UpdateEntity(e1, ref stats2);  // Creates revision 1 (isolated)

// Transaction T3 (tick: 120, concurrent with T2)
using var t3 = dbe.CreateTransaction();
t3.ReadEntity(e1, out var stats3);  // Still sees revision 0 (T2 uncommitted)
stats3.Health = 85f;
t3.UpdateEntity(e1, ref stats3);  // Creates revision 2 (isolated)

// Commit T2 first
t2.Commit();  // Succeeds, revision 1 committed at tick 110

// Commit T3 (conflict!)
t3.Commit();
// - Detects conflict: LastCommitRevisionIndex (1) >= CurRevisionIndex (0)
// - Creates new revision 3 (not 2, since 2 was isolated)
// - Copies data from revision 0 (read version)
// - Applies T3's changes (Health=85)
// - Commits revision 3
// Result: T3's changes overwrite T2's changes ("last write wins")

// Transaction T4 (tick: 130)
using var t4 = dbe.CreateTransaction();
t4.ReadEntity(e1, out var stats4);  // Sees revision 3 (Health=85)
```

---

## Code Examples

### Basic CRUD Operations

```csharp
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

// Component definition
[Component]
public struct Player
{
    [Field] [Index] public int PlayerId;
    [Field] public String64 Name;
    [Field] public float Health;
    [Field] public float X;
    [Field] public float Y;
}

// Setup
var services = new ServiceCollection();
services.AddTyphonEngine(options =>
{
    options.DatabaseFilePath = "game.db";
    options.PageCacheSize = 512;  // 4MB cache
});

var provider = services.BuildServiceProvider();
var dbe = provider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponent<Player>();

// Create
using (var t = dbe.CreateTransaction())
{
    var player = new Player
    {
        PlayerId = 1001,
        Name = "Alice",
        Health = 100f,
        X = 10.5f,
        Y = 20.3f
    };

    var entityId = t.CreateEntity(ref player);
    Console.WriteLine($"Created entity {entityId}");

    t.Commit();
}

// Read
using (var t = dbe.CreateTransaction())
{
    long entityId = 1;
    if (t.ReadEntity(entityId, out Player player))
    {
        Console.WriteLine($"Player: {player.Name}, Health: {player.Health}");
    }
}

// Update
using (var t = dbe.CreateTransaction())
{
    long entityId = 1;
    if (t.ReadEntity(entityId, out Player player))
    {
        player.Health -= 10f;
        t.UpdateEntity(entityId, ref player);
        t.Commit();
    }
}

// Delete
using (var t = dbe.CreateTransaction())
{
    long entityId = 1;
    t.DeleteEntity<Player>(entityId);
    t.Commit();
}
```

### Multi-Component Entities

```csharp
[Component]
public struct Position
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

[Component]
public struct Velocity
{
    [Field] public float VX;
    [Field] public float VY;
    [Field] public float VZ;
}

[Component]
public struct Health
{
    [Field] public float Current;
    [Field] public float Max;
}

// Register all components
dbe.RegisterComponent<Position>();
dbe.RegisterComponent<Velocity>();
dbe.RegisterComponent<Health>();

// Create entity with multiple components
using (var t = dbe.CreateTransaction())
{
    var pos = new Position { X = 10, Y = 20, Z = 0 };
    var vel = new Velocity { VX = 1, VY = 0, VZ = 0 };
    var hp = new Health { Current = 100, Max = 100 };

    var entityId = t.CreateEntity(ref pos, ref vel, ref hp);

    t.Commit();
}

// Read partial components
using (var t = dbe.CreateTransaction())
{
    long entityId = 1;

    if (t.ReadEntity(entityId, out Position pos, out Velocity vel))
    {
        // Update position based on velocity
        pos.X += vel.VX;
        pos.Y += vel.VY;
        pos.Z += vel.VZ;

        t.UpdateEntity(entityId, ref pos);
        t.Commit();
    }
}
```

### Index Queries

```csharp
[Component]
public struct Enemy
{
    [Field] [Index] public int Level;  // Non-unique index (multiple enemies per level)
    [Field] public String64 Name;
    [Field] public float Health;
}

dbe.RegisterComponent<Enemy>();

// Create enemies
using (var t = dbe.CreateTransaction())
{
    var e1 = new Enemy { Level = 5, Name = "Goblin", Health = 50 };
    var e2 = new Enemy { Level = 5, Name = "Orc", Health = 80 };
    var e3 = new Enemy { Level = 10, Name = "Troll", Health = 200 };

    t.CreateEntity(ref e1);
    t.CreateEntity(ref e2);
    t.CreateEntity(ref e3);

    t.Commit();
}

// Query by index
using (var t = dbe.CreateTransaction())
{
    var table = dbe.GetComponentTable<Enemy>();
    var levelIndex = table.GetIndex<int>("Level");  // Get index for Level field

    var level5Enemies = levelIndex.GetAll(5);  // Returns array of chunk IDs

    Console.WriteLine($"Found {level5Enemies.Length} level 5 enemies:");
    foreach (var chunkId in level5Enemies)
    {
        // Resolve chunk ID to primary key (need to implement reverse lookup)
        // ... (omitted for brevity)
    }
}
```

### Conflict Resolution

```csharp
// Custom conflict handler
public class PlayerConflictHandler : ConcurrencyConflictHandler
{
    public override void OnConflict(
        long primaryKey,
        short readVersion,
        short committedVersion,
        short committingVersion,
        ref ComponentData readData,
        ref ComponentData committedData,
        ref ComponentData committingData)
    {
        var read = (Player*)readData.Data;
        var committed = (Player*)committedData.Data;
        var committing = (Player*)committingData.Data;

        // Merge strategy: keep highest health
        if (committed->Health > committing->Health)
        {
            committing->Health = committed->Health;
        }

        // Keep position from committed version (prefer server state)
        committing->X = committed->X;
        committing->Y = committed->Y;
    }
}

// Use custom handler
using (var t = dbe.CreateTransaction())
{
    t.SetConflictHandler(new PlayerConflictHandler());

    // ... perform updates ...

    t.Commit();  // Custom handler invoked on conflicts
}
```

### Batch Operations

```csharp
// Batch create 10,000 entities
using (var t = dbe.CreateTransaction())
{
    for (int i = 0; i < 10000; i++)
    {
        var enemy = new Enemy
        {
            Level = (i % 10) + 1,
            Name = $"Enemy_{i}",
            Health = 50f + (i % 10) * 10
        };

        t.CreateEntity(ref enemy);
    }

    t.Commit();  // Single commit for all 10,000 entities
}

// Batch update with index lookup
using (var t = dbe.CreateTransaction())
{
    var table = dbe.GetComponentTable<Enemy>();
    var levelIndex = table.GetIndex<int>("Level");

    var level5Enemies = levelIndex.GetAll(5);

    foreach (var entityId in level5Enemies)
    {
        if (t.ReadEntity(entityId, out Enemy enemy))
        {
            enemy.Health *= 1.1f;  // 10% health buff
            t.UpdateEntity(entityId, ref enemy);
        }
    }

    t.Commit();
}
```

### Long-Running Read Transaction

```csharp
// Snapshot for analytics (doesn't block writes)
Task.Run(() =>
{
    using var t = dbe.CreateTransaction();

    // This transaction sees a consistent snapshot
    // even if other transactions commit changes

    var totalHealth = 0f;
    var count = 0;

    // Iterate all entities (slow operation)
    for (long entityId = 1; entityId <= 1000000; entityId++)
    {
        if (t.ReadEntity(entityId, out Player player))
        {
            totalHealth += player.Health;
            count++;
        }
    }

    Console.WriteLine($"Average health: {totalHealth / count}");

    // No commit needed (read-only transaction)
});

// Meanwhile, writes proceed normally
using (var t = dbe.CreateTransaction())
{
    // This will commit successfully even though
    // the long-running read transaction is still active
    var player = new Player { /* ... */ };
    t.CreateEntity(ref player);
    t.Commit();
}
```

---

## Advanced Topics

### Long-Running Transactions

**Problem:** Long-running transactions prevent garbage collection of old revisions.

**Example:**
```csharp
// Transaction T1 starts
using var t1 = dbe.CreateTransaction();  // Tick: 100

// Many updates happen
for (int i = 0; i < 1000; i++)
{
    using var t = dbe.CreateTransaction();  // Tick: 100 + i
    t.UpdateEntity(1, ref data);
    t.Commit();
}

// T1 is still the tail (oldest transaction)
// All 1000 revisions are retained (can't be garbage collected)

// T1 finally reads
t1.ReadEntity(1, out var data);  // Sees version at tick 100
```

**Mitigation Strategies:**

1. **Short Transactions:** Keep transactions short-lived (milliseconds, not minutes)
2. **Read-Only Snapshots:** Use separate read-only transactions for analytics
3. **Periodic Commits:** Break long operations into multiple transactions
4. **Monitoring:** Track `TransactionChain.Tail.TransactionTick` to detect long-running transactions

**Example (Periodic Commits):**
```csharp
const int batchSize = 1000;

for (int batch = 0; batch < totalCount; batch += batchSize)
{
    using var t = dbe.CreateTransaction();

    for (int i = 0; i < batchSize; i++)
    {
        // Process entity...
        t.UpdateEntity(batch + i, ref data);
    }

    t.Commit();  // Commit every 1000 entities
}
// Allows garbage collection after each commit
```

### Custom Indexing Strategies

**Composite Indexes (Manual Implementation):**

```csharp
// Index by (Level, Health) composite key
public class LevelHealthIndex
{
    private Dictionary<(int Level, int HealthBucket), List<long>> _index = new();

    public void Add(long entityId, int level, float health)
    {
        var bucket = (int)(health / 10);  // 10 HP buckets
        var key = (level, bucket);

        if (!_index.ContainsKey(key))
            _index[key] = new List<long>();

        _index[key].Add(entityId);
    }

    public IEnumerable<long> Query(int level, float minHealth, float maxHealth)
    {
        var minBucket = (int)(minHealth / 10);
        var maxBucket = (int)(maxHealth / 10);

        for (int bucket = minBucket; bucket <= maxBucket; bucket++)
        {
            var key = (level, bucket);
            if (_index.TryGetValue(key, out var entities))
            {
                foreach (var entityId in entities)
                    yield return entityId;
            }
        }
    }
}
```

**Spatial Indexing (Grid-Based):**

```csharp
public class SpatialIndex
{
    private const int CellSize = 100;
    private Dictionary<(int X, int Y), List<long>> _grid = new();

    public void Add(long entityId, float x, float y)
    {
        var cell = ((int)(x / CellSize), (int)(y / CellSize));

        if (!_grid.ContainsKey(cell))
            _grid[cell] = new List<long>();

        _grid[cell].Add(entityId);
    }

    public IEnumerable<long> QueryRadius(float centerX, float centerY, float radius)
    {
        var cellRadius = (int)(radius / CellSize) + 1;
        var centerCell = ((int)(centerX / CellSize), (int)(centerY / CellSize));

        for (int dx = -cellRadius; dx <= cellRadius; dx++)
        {
            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                var cell = (centerCell.Item1 + dx, centerCell.Item2 + dy);
                if (_grid.TryGetValue(cell, out var entities))
                {
                    foreach (var entityId in entities)
                        yield return entityId;
                }
            }
        }
    }
}
```

### Performance Tuning

**Page Cache Sizing:**

```csharp
// Rule of thumb: Cache size = Working set size × 2
services.AddTyphonEngine(options =>
{
    // Default: 256 pages × 8KB = 2MB
    options.PageCacheSize = 1024;  // 8MB cache

    // Large datasets: 10,000 pages × 8KB = 80MB
    options.PageCacheSize = 10000;
});
```

**Monitoring Cache Efficiency:**

```csharp
var metrics = dbe.MMF.GetMetrics();

Console.WriteLine($"Cache hit rate: {metrics.CacheHitRate:P}");
Console.WriteLine($"Avg page load time: {metrics.AvgPageLoadTime}ms");
Console.WriteLine($"Dirty pages: {metrics.DirtyPageCount}");
Console.WriteLine($"Free pages: {metrics.FreePageCount}");

// Tune cache size based on hit rate:
// - Hit rate < 80%: Increase cache size
// - Hit rate > 95%: Cache may be oversized
```

**Batch Writes:**

```csharp
// INEFFICIENT: Many small transactions
for (int i = 0; i < 10000; i++)
{
    using var t = dbe.CreateTransaction();
    t.CreateEntity(ref data);
    t.Commit();  // 10,000 commits (slow!)
}

// EFFICIENT: Single large transaction
using (var t = dbe.CreateTransaction())
{
    for (int i = 0; i < 10000; i++)
    {
        t.CreateEntity(ref data);
    }
    t.Commit();  // 1 commit (fast!)
}
```

**Parallel Reads:**

```csharp
// Multiple read transactions can run concurrently
Parallel.ForEach(entityIds, entityId =>
{
    using var t = dbe.CreateTransaction();
    if (t.ReadEntity(entityId, out Player player))
    {
        // Process player...
    }
});
// No locks, full parallelism
```

### Debugging & Diagnostics

**Enable Verbose Logging:**

```bash
dotnet build -c VerboseLogging
```

**Log Configuration (appsettings.json):**

```json
{
  "Serilog": {
    "MinimumLevel": "Debug",
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "Seq",
        "Args": { "serverUrl": "http://localhost:5341" }
      }
    ]
  }
}
```

**Transaction Inspection:**

```csharp
using var t = dbe.CreateTransaction();

Console.WriteLine($"Transaction ID: {t.Id}");
Console.WriteLine($"Tick: {t.TransactionTick}");
Console.WriteLine($"DateTime: {t.TransactionDateTime}");
Console.WriteLine($"State: {t.State}");

// After operations
Console.WriteLine($"Operations: {t.CommittedOperationCount}");
Console.WriteLine($"Components touched: {t.ComponentInfoCount}");
```

**Page Access Patterns:**

```csharp
var pageAccessLog = new List<int>();

// Hook into page access (requires custom PagedMMF extension)
dbe.MMF.OnPageAccess += (pageIndex) =>
{
    pageAccessLog.Add(pageIndex);
};

// Analyze access patterns
var hotPages = pageAccessLog
    .GroupBy(p => p)
    .OrderByDescending(g => g.Count())
    .Take(10);

Console.WriteLine("Top 10 hot pages:");
foreach (var page in hotPages)
{
    Console.WriteLine($"Page {page.Key}: {page.Count()} accesses");
}
```

---

## References

### Academic Papers

1. **Transaction Processing: Concepts and Techniques**
   Jim Gray, Andreas Reuter (1992)
   [Microsoft Research](https://www.microsoft.com/en-us/research/publication/transaction-processing-concepts-and-techniques/)
   Covers ACID, MVCC, and concurrency control fundamentals.

2. **Modern B-Tree Techniques**
   Goetz Graefe (2011)
   [Foundations and Trends in Databases](https://w6113.github.io/files/papers/btreesurvey-graefe.pdf)
   Comprehensive survey of B+Tree optimizations.

3. **Snapshot Isolation in SQL Server**
   Hal Berenson, Philip Bernstein, et al. (1995)
   [Microsoft Research](https://www.microsoft.com/en-us/research/publication/a-critique-of-ansi-sql-isolation-levels/)
   Defines snapshot isolation semantics.

### Books

1. **Database Internals**
   Alex Petrov (2019)
   [O'Reilly](https://www.databass.dev/)
   Modern database storage engine design.

2. **Designing Data-Intensive Applications**
   Martin Kleppmann (2017)
   [O'Reilly](https://dataintensive.net/)
   Distributed systems, consistency, and transactions.

### Online Resources

1. **Memory-Mapped Files (.NET Documentation)**
   [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files)
   Official documentation for MemoryMappedFile API.

2. **B+Tree Visualization**
   [University of San Francisco](https://www.cs.usfca.edu/~galles/visualization/BPlusTree.html)
   Interactive B+Tree operations.

3. **MVCC Explained**
   [PostgreSQL Documentation](https://www.postgresql.org/docs/current/mvcc.html)
   PostgreSQL's MVCC implementation.

4. **Clock-Sweep Algorithm**
   [Wikipedia: Page Replacement Algorithms](https://en.wikipedia.org/wiki/Page_replacement_algorithm#Clock)
   Overview of clock-based eviction.

### Typhon Documentation

1. **Official Documentation**
   [https://nockawa.github.io/Typhon/](https://nockawa.github.io/Typhon/)
   API reference, tutorials, and guides.

2. **GitHub Repository**
   [https://github.com/nockawa/Typhon](https://github.com/nockawa/Typhon)
   Source code, issues, and discussions.

### Related Projects

1. **FASTER (Microsoft Research)**
   [GitHub](https://github.com/microsoft/FASTER)
   High-performance key-value store with similar MVCC approach.

2. **LMDB (Lightning Memory-Mapped Database)**
   [Symas](https://www.symas.com/lmdb)
   Memory-mapped database with B+Tree indexes.

3. **RocksDB (Meta)**
   [GitHub](https://github.com/facebook/rocksdb)
   Embedded key-value store with LSM trees.

---

## Appendix

### Glossary

- **ACID:** Atomicity, Consistency, Isolation, Durability
- **MVCC:** Multi-Version Concurrency Control
- **B+Tree:** Balanced tree structure optimized for disk-based storage
- **ECS:** Entity-Component-System (data-oriented design pattern)
- **Blittable:** Memory layout compatible with unsafe pointers (no managed references)
- **Chunk:** Fixed-size memory block allocated from a segment
- **Segment:** Multi-page logical address space abstraction
- **Page:** 8KB unit of disk I/O and caching
- **Revision Chain:** Linked list of component versions (MVCC metadata)
- **Isolation Flag:** Bit indicating uncommitted revision (invisible to other transactions)
- **Clock-Sweep:** Approximate LRU page eviction algorithm
- **Occupancy Bitmap:** Hierarchical bitmap tracking free/used chunks

### File Structure Summary

```
src/Typhon.Engine/
├── Database Engine/
│   ├── DatabaseEngine.cs                 # Top-level API and orchestration
│   ├── Transaction.cs                    # MVCC snapshot isolation
│   ├── TransactionChain.cs               # Transaction pool and registry
│   ├── ComponentTable.cs                 # Per-component-type storage
│   ├── Schema/
│   │   ├── DBComponentDefinition.cs      # Component metadata
│   │   └── DatabaseDefinitions.cs        # Schema registry
│   └── BPTree/
│       ├── BTree.cs                      # Generic B+Tree interface
│       ├── L16BTree.cs                   # 16-bit key B+Tree
│       ├── L32BTree.cs                   # 32-bit key B+Tree
│       ├── L64BTree.cs                   # 64-bit key B+Tree
│       └── String64BTree.cs              # 64-byte string B+Tree
├── Persistence Layer/
│   ├── PagedMMF.cs                       # Page caching and I/O
│   ├── ManagedPagedMMF.cs                # Segment management
│   ├── LogicalSegment.cs                 # Multi-page abstraction
│   ├── ChunkBasedSegment.cs              # Fixed-size chunk allocation
│   ├── ChunkRandomAccessor.cs            # Chunk-level caching
│   ├── PageAccessor.cs                   # Page access interface
│   ├── PageBaseHeader.cs                 # Page structure definitions
│   ├── VariableSizedBufferSegment.cs     # Variable-size elements
│   └── StringTableSegment.cs             # String storage
├── Collections/
│   ├── BitmapL3.cs                       # 3-level occupancy bitmap
│   └── ConcurrentDictionary.cs           # Thread-safe dictionary
└── Misc/
    ├── AccessControl.cs                  # 8-byte reader-writer lock
    ├── AccessControlSmall.cs             # 4-byte reader-writer lock
    ├── AdaptiveWaiter.cs                 # Spin-then-sleep waiter
    ├── String64.cs                       # 64-byte fixed string
    ├── PackedDateTime48.cs               # 48-bit compressed timestamp
    └── Variant.cs                        # Discriminated union
```

---

**End of Architecture Documentation**

For the latest updates and API reference, visit: [https://nockawa.github.io/Typhon/](https://nockawa.github.io/Typhon/)

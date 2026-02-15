# WAL (Write-Ahead Log) — Detailed Design

> The durability backbone: a sequential, append-only, crash-recoverable log ensuring every committed transaction can survive power failures and torn pages.

---

## Document Context

| Item | Value |
|------|-------|
| **Status** | Design (ready for implementation planning) |
| **Parent** | [06-durability.md](../overview/06-durability.md) |
| **Research** | [IO-Pipeline-Crash-Semantics.md](../research/IO-Pipeline-Crash-Semantics.md) |
| **Prerequisite** | CRC32C page checksums (Phase 1 of durability implementation) |

### Design Parameters (from user)

| Parameter | Value |
|-----------|-------|
| Components per transaction | 4–10 |
| Peak throughput target | Microsecond-level, scales linearly with CPU cores |
| Workload balance | 70% queries / 30% transactions |
| WAL file size constraint | None; recommended on separate SSD |
| Threading model | Whatever is fastest |

---

## Table of Contents

1. [Prior Art & Lessons Learned](#1-prior-art--lessons-learned)
2. [WAL Record Format](#2-wal-record-format)
3. [WAL File Layout](#3-wal-file-layout)
4. [Ring Buffer Architecture](#4-ring-buffer-architecture)
5. [WAL Writer Thread](#5-wal-writer-thread)
6. [Integration with Transaction.Commit](#6-integration-with-transactioncommit)
7. [Durability Modes](#7-durability-modes)
8. [Full-Page Images (FPI)](#8-full-page-images-fpi)
9. [Checkpoint Pipeline](#9-checkpoint-pipeline)
10. [Segment Lifecycle & File Management](#10-segment-lifecycle--file-management)
11. [Crash Recovery](#11-crash-recovery)
12. [CRC32C Implementation](#12-crc32c-implementation)
13. [Platform Abstraction](#13-platform-abstraction)
14. [Performance Budget](#14-performance-budget)
15. [Implementation Phases](#15-implementation-phases)

---

## 1. Prior Art & Lessons Learned

### 1.1 PostgreSQL WAL (pg_wal)

**Architecture:**
- Variable-size records with `XLogRecord` header (24 bytes)
- Resource Manager system: each subsystem (heap, btree, etc.) registers its own WAL record types
- Full-Page Images (FPI): writes the entire 8KB page on first modification since last checkpoint
- Segments: 16MB files named by LSN position, pre-allocated
- `wal_compression`: LZ4/zstd on FPI payloads (saves 50-80% I/O on FPI-heavy workloads)

**What Typhon borrows:**
- FPI on first-modification-since-checkpoint (proven torn page repair mechanism)
- The concept of checkpoint LSN in the file header (recovery start point)
- Pre-allocated segment files to avoid filesystem metadata writes

**What Typhon does differently:**
- No resource manager abstraction (overkill for ECS model — one WAL record type per operation)
- Variable-size log buffer instead of fixed-slot ring (Typhon records range 40B–8200B)
- No shared_buffers equivalent (Typhon already has PagedMMF page cache with clock-sweep)

### 1.2 RocksDB WAL

**Architecture:**
- 32KB block-aligned format
- Record types for spanning: `kFullType`, `kFirstType`, `kMiddleType`, `kLastType`
- WriteBatch: atomic group of operations (sequence number + count + operation list)
- Group commit: leader-follower model via `JoinBatchGroup` — one thread writes for many
- Pipelined write: WAL write and memtable insertion happen in parallel

**What Typhon borrows:**
- **Group commit pattern** for GroupCommit durability mode (leader-follower)
- **WriteBatch concept** → WAL Transaction Record containing multiple component operations
- Block-aligned writes for O_DIRECT compatibility
- The insight that sequential WAL writes are easily parallelizable with in-memory updates

**What Typhon does differently:**
- No block spanning with types — Typhon uses variable-length records with explicit length field (simpler, no fragmentation tracking)
- Direct FUA per write in Immediate mode (RocksDB always batches)
- No separate memtable concept (Typhon writes in-place to page cache)

### 1.3 SQLite WAL

**Architecture:**
- Frame = 24-byte header + full-page data (page_size bytes, typically 4KB)
- Rolling checksum: each frame's checksum includes previous frame's checksum (chain validation)
- WAL-index: shared memory file (`.shm`) mapping frame → page for readers
- Checkpoint: copies WAL frames back to database file, resets WAL
- Passive vs Active checkpoint modes

**What Typhon borrows:**
- **Rolling checksum chain** concept — each record's CRC includes previous record's CRC for sequence validation
- The simplicity of full-page WAL entries (for FPI records only)

**What Typhon does differently:**
- Not page-level WAL — Typhon logs logical operations (component create/update/delete) for non-FPI records
- No WAL-index needed (Typhon's MVCC visibility is through epoch registry, not WAL)
- No single-writer limitation (Typhon supports concurrent transactions)

### 1.4 Aeron Log Buffer

**Architecture:**
- Linear memory buffer with atomic tail position (`LOCK XADD` for allocation)
- Padding records fill remaining space at end of term when record doesn't fit
- 3 term buffers: active → dirty → clean, rotated in cycle
- `BufferClaim`: zero-copy API — producer gets buffer pointer, writes directly, then commits
- Contiguous allocation: each message occupies exactly `aligned(header + length)` bytes
- Header: 32 bytes with length, type, term-id, term-offset

**What Typhon borrows:**
- **Atomic tail increment (`LOCK XADD`)** for lock-free contiguous allocation in ring buffer
- **Padding record** at end of term/segment when record doesn't fit (avoid wrapping mid-record)
- **BufferClaim (zero-copy)** pattern: transaction serializes directly into WAL buffer
- **Contiguous allocation guarantee**: simplifies recovery (scan forward, no fragmentation)

**What Typhon does differently:**
- Single log buffer (not 3 term buffers) — WAL is file-backed, not memory-backed
- Writes go to memory-mapped or direct I/O file, not retained in memory ring
- Producer is transaction thread; consumer is dedicated WAL writer (not Aeron's receiver)

---

## 2. WAL Record Format

### 2.1 Design Principles

1. **Self-describing**: each record contains enough metadata for standalone replay
2. **CRC-protected**: hardware-accelerated CRC32C covers header + payload
3. **Aligned**: all records start on 8-byte boundaries (natural alignment for 64-bit fields)
4. **Variable-length**: payload ranges from 0 bytes (delete) to ~8000 bytes (FPI)
5. **Chain-validated**: CRC includes previous record's CRC for sequence integrity (SQLite pattern)

### 2.2 Record Header Structure

```csharp
/// <summary>
/// WAL record header — 48 bytes, 8-byte aligned.
/// Represents a single component operation within a transaction.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WalRecordHeader
{
    // ─── Identification (24 bytes) ───
    public long   LSN;               // 8B — Monotonically increasing Log Sequence Number
    public long   TransactionTSN;    // 8B — Transaction timestamp (links to epoch)
    public uint   TotalRecordLength; // 4B — Header + payload length (for skip-ahead)
    public ushort UowEpoch;          // 2B — Epoch stamp (links to UoW Registry)
    public ushort ComponentTypeId;   // 2B — Which component table (from schema registry)

    // ─── Operation (8 bytes) ───
    public long   EntityId;          // 8B — Primary key (entity ID) - changed from int to long

    // ─── Metadata (8 bytes) ───
    public ushort PayloadLength;     // 2B — Bytes of component data after header
    public byte   OperationType;     // 1B — Create=1, Update=2, Delete=3
    public byte   Flags;             // 1B — See WalRecordFlags below
    public uint   PrevCRC;           // 4B — Previous record's CRC (chain validation)

    // ─── Integrity (8 bytes) ───
    public uint   CRC;               // 4B — CRC32C of entire record (header with CRC=0 + payload)
    public uint   _reserved;         // 4B — Future use (alignment padding)
}
// Total: 48 bytes (6 cache lines at 8-byte alignment)

[Flags]
public enum WalRecordFlags : byte
{
    None             = 0x00,
    FullPageImage    = 0x01,  // Payload is full 8KB page, not component data
    GroupCommitEnd   = 0x02,  // Last record in a group commit batch
    TransactionEnd   = 0x04,  // Last record for this transaction
    TransactionBegin = 0x08,  // First record for this transaction
    Checkpoint       = 0x10,  // Checkpoint record (no payload, marks safe point)
    Padding          = 0x20,  // Padding record (skip to next segment boundary)
}
```

### 2.3 Record Types

| Type | OperationType | Payload | Size Range |
|------|---------------|---------|------------|
| **ComponentCreate** | 1 | Component struct bytes | 48 + sizeof(T) |
| **ComponentUpdate** | 2 | Component struct bytes | 48 + sizeof(T) |
| **ComponentDelete** | 3 | None | 48 |
| **FullPageImage** | 0 | 8000 bytes (page raw data) | 48 + 8000 = 8048 |
| **Checkpoint** | 0 | None | 48 |
| **Padding** | 0 | Zero-fill to boundary | Variable |

### 2.4 Transaction Envelope

A multi-component transaction produces N records (one per component operation, 4–10 typical):

```
┌─────────────────────┬─────────────────────┬─────┬─────────────────────┐
│ Record 1            │ Record 2            │ ... │ Record N            │
│ TransactionBegin    │ (none)              │     │ TransactionEnd      │
│ CompTypeA, Create   │ CompTypeB, Update   │     │ CompTypeC, Update   │
│ + payload           │ + payload           │     │ + payload           │
└─────────────────────┴─────────────────────┴─────┴─────────────────────┘
 ← All share same TransactionTSN and UowEpoch →
```

**Atomicity guarantee**: A transaction is considered complete only when a record with `TransactionEnd` flag is found with matching TSN. Incomplete transactions are rolled back during recovery.

### 2.5 Size Calculations

For typical Typhon components (blittable structs, 32–256 bytes):

| Scenario | Records | Payload/Record | Total WAL Bytes |
|----------|---------|----------------|-----------------|
| Minimal transaction (1 component, 64B) | 1 | 64 | 112 |
| Typical transaction (6 components, 128B avg) | 6 | 128 | 1056 |
| Large transaction (10 components, 256B) | 10 | 256 | 3040 |
| Transaction with 1 FPI | 7 | mix | ~9100 |

---

## 3. WAL File Layout

### 3.1 Segment Architecture

WAL is composed of fixed-size **segments** (files):

```
WAL File Structure:
┌──────────────────────────────────────────────────────────────┐
│ Segment Header (4096 bytes — one OS page, O_DIRECT aligned) │
├──────────────────────────────────────────────────────────────┤
│ Record 1 (48B header + payload, 8-byte aligned)             │
├──────────────────────────────────────────────────────────────┤
│ Record 2 ...                                                │
├──────────────────────────────────────────────────────────────┤
│ ...                                                         │
├──────────────────────────────────────────────────────────────┤
│ Padding record (fills to segment boundary)                  │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 Segment Header

```csharp
/// <summary>
/// WAL segment file header — 4096 bytes (one aligned page).
/// Written once when segment is created; never modified after.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct WalSegmentHeader
{
    public uint   Magic;             // 4B — 0x54594657 ("TYFW" — TYphon File Wal)
    public uint   Version;           // 4B — Format version (1)
    public long   SegmentId;         // 8B — Monotonic segment number
    public long   FirstLSN;          // 8B — LSN of first record in this segment
    public long   PrevSegmentLSN;    // 8B — Last LSN of previous segment (chain link)
    public uint   SegmentSize;       // 4B — Total file size in bytes
    public uint   HeaderCRC;         // 4B — CRC32C of header (with CRC=0)
    public fixed byte Reserved[4056]; // Padding to 4096 bytes
}
```

### 3.3 Segment Sizing

**Default segment size: 64 MB**

Rationale:
- At 1000 bytes average per transaction, 6 records per tx: ~64,000 transactions per segment
- At 100K tx/sec peak: rotates every ~0.64 seconds → manageable file count
- Large enough to amortize file creation overhead
- Small enough that recovery never needs to scan more than 64MB of stale data
- Aligns with NVMe erase block sizes (typically 2–4MB), minimizing write amplification

| Throughput | Segment Lifetime | Segments/Hour |
|------------|-----------------|---------------|
| 10K tx/sec | ~6.4 sec | ~560 |
| 100K tx/sec | ~0.64 sec | ~5,600 |
| 1M tx/sec | ~0.064 sec | ~56,000 |

**Pre-allocation**: Keep 4 segments pre-allocated ahead of the write position. The WAL writer creates new segments asynchronously when the current segment reaches 75% capacity.

### 3.4 File Naming Convention

```
wal/
├── 0000000000000001.wal    (segment 1)
├── 0000000000000002.wal    (segment 2)  ← checkpoint LSN is here
├── 0000000000000003.wal    (segment 3)  ← current write segment
├── 0000000000000004.wal    (segment 4)  ← pre-allocated (empty header)
├── 0000000000000005.wal    (segment 5)  ← pre-allocated (empty header)
└── wal.meta                 (current state metadata)
```

### 3.5 O_DIRECT Alignment

All writes use `FILE_FLAG_NO_BUFFERING` (Windows) for direct I/O:
- Write offset: always 4096-byte aligned (sector boundary)
- Write size: always multiple of 4096 bytes
- Buffer address: allocated via `NativeMemory.AlignedAlloc(size, 4096)`

This means WAL writes are accumulated in a 4096-byte-aligned write buffer, and flushed when either:
1. The buffer is full (4096 bytes of records accumulated)
2. A durability guarantee requires it (GroupCommit timer or Immediate FUA)

---

## 4. Ring Buffer Architecture

### 4.1 Design Choice: Aeron-Style Linear Buffer

Instead of a traditional circular ring buffer, Typhon uses an **Aeron-inspired linear allocation buffer** with the following properties:

```
WAL Commit Buffer (2MB, 4096-aligned):
┌─────────────────────────────────────────────────────────────────────┐
│                                                                     │
│  [Committed][Committed][Committed][  In-Flight  ][   Free Space   ] │
│                                                                     │
│  ^                                ^              ^                  │
│  FlushPos                         ClaimPos       TailPos            │
│  (writer has flushed to disk)     (claimed but   (next allocation)  │
│                                    not yet ready)                   │
└─────────────────────────────────────────────────────────────────────┘
```

### 4.2 Atomic Allocation Protocol

**Producer (transaction thread) claims space:**

```csharp
/// <summary>
/// Claims a contiguous region in the WAL buffer for writing a transaction's records.
/// Returns a WalClaim that the producer fills, then commits.
/// Lock-free via atomic tail increment (LOCK XADD / Interlocked.Add).
/// </summary>
public struct WalClaim
{
    public long   StartOffset;     // Buffer offset where records begin
    public int    ClaimedLength;   // Total bytes claimed
    public int    RecordCount;     // Number of records in this claim
    public long   FirstLSN;        // LSN assigned to first record
    internal long _commitFlag;     // Volatile: 0 = writing, 1 = ready for flush
}

public WalClaim TryClaim(int totalBytes)
{
    // Atomically advance tail — LOCK XADD on x64
    var offset = Interlocked.Add(ref _tailPosition, aligned) - aligned;

    // Check if we'd overflow the buffer
    if (offset + aligned > _bufferCapacity)
    {
        // Signal buffer full — writer must flush before we can proceed
        // Write a Padding record at current position, reset tail
        return WalClaim.Failed;  // Caller waits on _bufferAvailable event
    }

    return new WalClaim
    {
        StartOffset = offset,
        ClaimedLength = aligned,
        FirstLSN = Interlocked.Add(ref _nextLSN, recordCount) - recordCount,
        // ...
    };
}
```

**Key insight from Aeron**: The `Interlocked.Add` provides *exactly once* contiguous allocation. Multiple producers calling `TryClaim` concurrently each get a unique, non-overlapping buffer region. No CAS retry loop needed (unlike Disruptor's multi-producer mode).

### 4.3 Commit Protocol (Two-Phase)

```
Phase 1: Claim (atomic, nanosecond)
  Transaction thread calls TryClaim(totalBytes)
  Gets exclusive buffer region [offset..offset+totalBytes]

Phase 2: Write (no contention, ~100ns per record)
  Transaction serializes records directly into buffer (zero-copy)
  Sets CRC, LSN, flags for each record

Phase 3: Publish (atomic, nanosecond)
  Volatile.Write(ref claim._commitFlag, 1)
  WAL writer now knows this region is ready
```

### 4.4 Back-Pressure Strategy

When the ring buffer is full (TailPos would exceed capacity):

1. **Spin-wait (< 1µs)**: Check if writer has advanced FlushPos
2. **Yield**: If still full after 64 spins, `Thread.Yield()`
3. **Block**: If still full after 1ms, block on `ManualResetEventSlim`

This should rarely trigger because:
- 2MB buffer holds ~2000 typical transactions
- WAL writer flushes every 4096 bytes or on GroupCommit timer (5–10ms)
- At 100K tx/sec, buffer turns over every ~20ms → 4x safety margin

### 4.5 Buffer Recycling

When the WAL writer has flushed all records up to position P:
- All claims with `StartOffset < P` are complete
- Buffer space `[0..P)` is recyclable
- Uses a simple swap: double-buffer with ping-pong

```
Buffer A: [Flushing to disk...]     Buffer B: [Accepting new claims...]
         ↕ swap when A is fully flushed
Buffer A: [Accepting new claims...] Buffer B: [Flushing to disk...]
```

This avoids the complexity of wrap-around ring buffer logic while providing zero-allocation steady-state operation.

---

## 5. WAL Writer Thread

### 5.1 Architecture

A single dedicated thread (pinned to a core if possible) handles all WAL I/O:

```
┌─────────────────────────────────────────────────────────────────┐
│ WAL Writer Thread                                               │
│                                                                 │
│  Loop:                                                          │
│    1. Scan committed claims (FlushPos → TailPos)                │
│    2. Batch contiguous ready claims into write buffer            │
│    3. Write to WAL segment file (O_DIRECT + FUA for Immediate)  │
│    4. Advance FlushPos                                          │
│    5. Signal waiting producers (GroupCommit/Immediate waiters)   │
│    6. Check segment rotation threshold                          │
│                                                                 │
│  Wake conditions:                                               │
│    - New claim committed (SpinWait → EventWaitHandle)           │
│    - GroupCommit timer fired (5ms default)                      │
│    - Immediate mode claim published                             │
└─────────────────────────────────────────────────────────────────┘
```

### 5.2 Batching Strategy

The writer scans forward from `FlushPos`, collecting contiguous committed claims:

```csharp
// Pseudo-code for writer scan
int batchStart = _flushPos;
int batchEnd = batchStart;

while (batchEnd < _tailPos)
{
    ref var claim = ref GetClaimAt(batchEnd);

    // Stop if we hit an uncommitted claim (still being written by producer)
    if (Volatile.Read(ref claim._commitFlag) == 0)
        break;

    batchEnd += claim.ClaimedLength;
}

if (batchEnd > batchStart)
{
    // Write [batchStart..batchEnd) to WAL file in one I/O
    WriteToSegment(buffer + batchStart, batchEnd - batchStart);
}
```

### 5.3 Flush Semantics

| Mode | Write Behavior | Latency | Throughput |
|------|---------------|---------|------------|
| **Deferred** | Batch writes, no explicit flush | ~0 (fire-and-forget) | Maximum |
| **GroupCommit** | Write + `FlushFileBuffers` every 5–10ms | ≤10ms | High |
| **Immediate** | Write + `FILE_FLAG_WRITE_THROUGH` per claim | ~15–85µs | Moderate |

### 5.4 Thread Affinity

The WAL writer thread should be pinned to a dedicated core:

```csharp
// On startup
var writerThread = new Thread(WalWriterLoop)
{
    IsBackground = true,
    Priority = ThreadPriority.AboveNormal,
    Name = "Typhon-WAL-Writer"
};

// Pin to last core (least likely to be used by app threads)
if (OperatingSystem.IsWindows())
{
    var mask = 1UL << (Environment.ProcessorCount - 1);
    SetThreadAffinityMask(GetCurrentThread(), (IntPtr)mask);
}
```

### 5.5 Leader-Follower Group Commit (RocksDB Pattern)

For GroupCommit mode, when multiple transactions complete within the same timer window:

```
Timeline:
  T1 commits → claims buffer, publishes, waits on _groupCommitEvent
  T2 commits → claims buffer, publishes, waits on _groupCommitEvent
  T3 commits → claims buffer, publishes, waits on _groupCommitEvent

  Timer fires (5ms) → Writer:
    1. Collects all 3 claims
    2. Single write: T1+T2+T3 records (one I/O operation)
    3. Single FlushFileBuffers
    4. Signals _groupCommitEvent → all 3 transactions unblock
```

This amortizes the ~15–85µs flush cost across N transactions, achieving ~1–5µs per-transaction durable commit.

---

## 6. Integration with Transaction.Commit

### 6.1 Current Commit Flow (Reference)

Based on the codebase analysis of `Transaction.cs`:

```
Current: Transaction.Commit()
├── foreach componentType in _componentInfos:
│   ├── CommitComponent(ref context)     ← makes revisions visible
│   │   ├── Conflict detection
│   │   ├── UpdateIndices()
│   │   └── element.Commit(TSN)          ← IsolationFlag = false
│   └── CleanUpUnusedEntries() (if tail)
├── Transaction.Dispose()
│   ├── ChunkAccessor.CommitChanges()    ← flushes dirty flags
│   └── ChangeSet.SaveChanges()          ← writes all dirty pages
└── (no durability guarantee currently)
```

### 6.2 New Commit Flow (with WAL)

```
New: Transaction.Commit()
├── Phase 1: Prepare WAL Records (in-memory, no I/O)
│   ├── Calculate total WAL bytes needed
│   ├── TryClaim(totalBytes) → get buffer region
│   ├── Serialize all component operations into buffer
│   │   ├── For each create: header + component bytes
│   │   ├── For each update: header + component bytes
│   │   ├── For each delete: header only
│   │   └── Set TransactionBegin on first, TransactionEnd on last
│   ├── Compute CRC32C for each record (rolling chain)
│   └── Publish claim (Volatile.Write commitFlag = 1)
│
├── Phase 2: Wait for Durability (mode-dependent)
│   ├── Deferred: no wait, return immediately
│   ├── GroupCommit: wait on _groupCommitEvent (≤10ms)
│   └── Immediate: wait on per-claim completion event
│
├── Phase 3: Make Visible (same as current)
│   ├── CommitComponent() for each operation
│   │   ├── UpdateIndices()
│   │   └── element.Commit(TSN) → IsolationFlag = false
│   └── Epoch registry: mark UoW as WalDurable/Committed
│
├── Phase 4: Background Page Flush (decoupled)
│   ├── ChangeSet.SaveChanges() → queued, not blocking
│   └── Checkpoint pipeline handles actual data page I/O
│
└── Return success to caller
```

### 6.3 Critical Ordering Invariant

```
WAL record durably written  →  revisions made visible  →  data pages eventually flushed
         (Phase 2)                  (Phase 3)                    (Phase 4)
```

This is the fundamental WAL guarantee: if a crash occurs after Phase 2 but before Phase 4, recovery replays the WAL to reconstruct the in-memory state and re-flush data pages.

### 6.4 Integration Points in Existing Code

| File | Change | Purpose |
|------|--------|---------|
| `Transaction.cs` | Add WAL serialization before `CommitComponent` | Record operations before visibility |
| `Transaction.cs` | Add durability wait between serialize and commit | Mode-dependent blocking |
| `ChangeSet.cs` | Decouple `SaveChanges()` from transaction thread | Async data page flush |
| `DatabaseEngine.cs` | Create `WalManager` instance | Lifecycle management |
| `PagedMMF.cs` | Add page checksum write/verify | Torn page detection |
| `ManagedPagedMMF.cs` | Track checkpoint LSN in file header | Recovery start point |

### 6.5 WAL Serialization — Zero-Copy Pattern

The transaction serializes directly into the claimed buffer region (no intermediate allocation):

```csharp
public void SerializeToWal(Span<byte> buffer, ref int offset)
{
    bool first = true;
    bool last = false;
    int opIndex = 0;
    int totalOps = CountOperations();

    foreach (var (type, info) in _componentInfos)
    {
        foreach (var (pk, revInfo) in info.GetOperations())
        {
            opIndex++;
            last = (opIndex == totalOps);

            ref var header = ref MemoryMarshal.AsRef<WalRecordHeader>(
                buffer.Slice(offset, sizeof(WalRecordHeader)));

            header.LSN = _claim.FirstLSN + opIndex - 1;
            header.TransactionTSN = _tsn;
            header.UowEpoch = _currentEpoch;
            header.ComponentTypeId = type.ComponentTypeId;
            header.EntityId = pk;
            header.OperationType = (byte)revInfo.Operations;
            header.Flags = (byte)(
                (first ? WalRecordFlags.TransactionBegin : 0) |
                (last  ? WalRecordFlags.TransactionEnd   : 0));

            offset += sizeof(WalRecordHeader);

            if (revInfo.Operations != OperationType.Deleted)
            {
                // Zero-copy: read component data from chunk directly into WAL buffer
                var componentAddr = info.CompContentAccessor
                    .GetChunkAddress(revInfo.CurCompContentChunkId, dirty: false);
                var componentSize = info.ComponentSize;

                new Span<byte>(componentAddr, componentSize)
                    .CopyTo(buffer.Slice(offset, componentSize));

                header.PayloadLength = (ushort)componentSize;
                offset += AlignUp(componentSize, 8);
            }

            header.TotalRecordLength = (uint)(offset - recordStart);
            header.PrevCRC = _prevCRC;
            header.CRC = ComputeCRC32C(buffer.Slice(recordStart, (int)header.TotalRecordLength));
            _prevCRC = header.CRC;

            first = false;
        }
    }
}
```

---

## 7. Durability Modes

### 7.1 Mode Definitions

```csharp
public enum DurabilityMode
{
    /// <summary>
    /// WAL records buffered, flushed at system's convenience.
    /// Commit latency: ~1-2µs. May lose last N transactions on crash.
    /// Use for: game state, telemetry, non-critical data.
    /// </summary>
    Deferred = 0,

    /// <summary>
    /// WAL records flushed every GroupCommitInterval (default 5ms).
    /// Commit latency: ~1-2µs (async) + ≤10ms visibility-to-durable gap.
    /// Use for: most OLTP workloads, 30-200 tx/batch amortization.
    /// </summary>
    GroupCommit = 1,

    /// <summary>
    /// WAL records flushed with FUA immediately on commit.
    /// Commit latency: ~15-85µs. Zero data loss on crash.
    /// Use for: financial, audit logs, critical state transitions.
    /// </summary>
    Immediate = 2
}
```

### 7.2 Configuration Hierarchy

```csharp
// Database-level default
var options = new DatabaseOptions
{
    DefaultDurabilityMode = DurabilityMode.GroupCommit,
    GroupCommitIntervalMs = 5,
    WalDirectory = @"D:\wal",   // Separate SSD recommended
};

// Per-transaction override
using var tx = dbe.CreateTransaction(new TransactionOptions
{
    DurabilityOverride = DurabilityMode.Immediate  // This tx is critical
});
```

### 7.3 Deferred → GroupCommit Upgrade

A transaction can start as Deferred and upgrade to GroupCommit/Immediate before commit:

```csharp
using var tx = dbe.CreateQuickTransaction();  // Inherits Deferred
// ... do work ...

// Realize this is important — upgrade before commit
tx.DurabilityMode = DurabilityMode.Immediate;
tx.Commit();  // Now waits for FUA
```

---

## 8. Full-Page Images (FPI)

### 8.1 Purpose

When a data page is modified for the first time since the last checkpoint, the *entire original page content* (8000 bytes of raw data) is written to the WAL *before* the modification. This provides:

1. **Torn page repair**: If a crash occurs mid-write to a data page, recovery can restore the page from the FPI, then replay subsequent WAL records.
2. **No need for double-write buffer**: Unlike InnoDB's doublewrite buffer, FPI in WAL serves the same purpose without a separate file.

### 8.2 Tracking Bitmap

Each `ChunkBasedSegment` maintains a bitmap of pages modified since last checkpoint:

```csharp
/// <summary>
/// Tracks which pages have had their FPI written since last checkpoint.
/// Bit set = FPI already in WAL, no need to write again.
/// Reset to all-zeros at each checkpoint completion.
/// </summary>
private ConcurrentBitmapL3All _fpiWrittenBitmap;
```

**Protocol when dirtying a page:**

```csharp
public void MarkPageDirty(int pageIndex)
{
    // Atomically test-and-set the FPI bit
    if (!_fpiWrittenBitmap.TrySet(pageIndex))
    {
        return;  // FPI already written for this checkpoint cycle
    }

    // First modification since checkpoint — must write FPI to WAL
    var pageData = GetPageRawData(pageIndex);  // 8000 bytes
    _walManager.WriteFPI(pageIndex, pageData, _currentSegmentId);
}
```

### 8.3 FPI Record Format

```
┌──────────────────────────────────────────┐
│ WalRecordHeader (48 bytes)               │
│   Flags = FullPageImage                  │
│   PayloadLength = 8000                   │
│   OperationType = 0 (N/A)               │
│   EntityId = 0 (N/A)                     │
│   ComponentTypeId = segment type ID      │
├──────────────────────────────────────────┤
│ FPI Metadata (16 bytes)                  │
│   int PageIndex                          │
│   int SegmentId                          │
│   long OriginalChangeRevision            │
├──────────────────────────────────────────┤
│ Page Raw Data (8000 bytes)               │
└──────────────────────────────────────────┘
Total: 8064 bytes per FPI record
```

### 8.4 FPI Frequency Estimation

At 256-page cache (2MB), with 10-second checkpoint interval:
- Steady-state: most pages get FPI'd once per 10s, then are hot for remaining 10s
- Peak FPI rate: 256 pages × 8064 bytes = ~2MB per checkpoint cycle
- Amortized: ~200KB/sec of WAL bandwidth for FPI (negligible vs transaction data)

### 8.5 Optimization: FPI Compression (Phase 3)

Following PostgreSQL's `wal_compression`:
- LZ4 compress FPI payloads before writing to WAL
- Typical 8KB page compresses to 2–4KB (50–75% savings for sparse pages)
- Decompression is fast enough (~2GB/sec) to not slow recovery
- Flag in header: `FpiCompressed = 0x40`

---

## 9. Checkpoint Pipeline

### 9.1 Purpose

Checkpoints write all dirty data pages to their final locations on disk, then advance the WAL recycling point. After a checkpoint, WAL segments before the checkpoint LSN can be deleted.

### 9.2 Checkpoint Flow

```
Checkpoint Pipeline (background thread, every CheckpointInterval):
│
├── 1. Begin Checkpoint
│   ├── Record checkpoint-start LSN (current WAL tail)
│   ├── Capture snapshot of all dirty page indices
│   └── Write Checkpoint WAL record
│
├── 2. Flush Data Pages
│   ├── Sort dirty pages by file offset (sequential I/O)
│   ├── Write all dirty pages to data file
│   │   └── Batch contiguous pages for large writes
│   └── fsync data file (all pages durable on disk)
│
├── 3. Update File Header
│   ├── Write new checkpoint LSN to file header (atomic 8-byte write)
│   └── fsync file header
│
├── 4. Reset FPI Bitmaps
│   └── Clear all _fpiWrittenBitmap (new checkpoint cycle starts)
│
├── 5. Advance WAL Recycle Point
│   ├── All WAL segments with LastLSN < checkpoint LSN are safe to delete
│   └── Update wal.meta with new recycle LSN
│
└── 6. Delete/Recycle Old Segments
    ├── Delete segment files before recycle point
    └── OR: rename to pre-allocated pool for reuse
```

### 9.3 Fuzzy Checkpointing

Typhon uses **fuzzy checkpoints** (same as PostgreSQL):
- New transactions continue during checkpoint
- Pages modified after checkpoint-start get their own FPI in the next cycle
- No stop-the-world pause required

### 9.4 Checkpoint Interval

| Workload | Interval | Rationale |
|----------|----------|-----------|
| Low-latency gaming | 10–30s | Minimize I/O interference with queries |
| Standard OLTP | 5–10s | Balance recovery time vs I/O load |
| Audit/Financial | 1–5s | Minimize WAL accumulation |

Default: **10 seconds** (configurable via `DatabaseOptions.CheckpointIntervalMs`).

---

## 10. Segment Lifecycle & File Management

### 10.1 Segment States

```
[PreAllocated] → [Active] → [Sealed] → [Reclaimable] → [Deleted/Recycled]
```

| State | Description |
|-------|-------------|
| **PreAllocated** | File created, header written, no records yet |
| **Active** | Currently receiving WAL writes |
| **Sealed** | Full (padding record written), no more writes |
| **Reclaimable** | All records checkpointed, safe to delete |
| **Deleted** | File removed from disk (or renamed for recycling) |

### 10.2 Pre-Allocation Strategy

```csharp
// Maintain pool of 4 pre-allocated segments
private const int PreAllocPool = 4;

// When active segment reaches 75% capacity:
//   1. Begin writing to next pre-allocated segment
//   2. Seal current segment (write Padding record)
//   3. Create new pre-allocated segment asynchronously
```

### 10.3 WAL Metadata File

`wal.meta` contains current WAL state (updated atomically via rename):

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct WalMetadata
{
    public long ActiveSegmentId;       // Currently accepting writes
    public long LastFlushedLSN;        // Durable up to this point
    public long CheckpointLSN;         // Recovery can start here
    public long RecycleBeforeLSN;      // Segments with all LSNs < this are deletable
    public long TotalBytesWritten;     // Lifetime WAL throughput (for metrics)
    public uint MetaCRC;               // CRC32C of this struct
}
```

### 10.4 Separate SSD Deployment

When configured for a separate WAL SSD:
```csharp
var options = new DatabaseOptions
{
    DataDirectory = @"C:\typhon\data",     // Random I/O: data pages
    WalDirectory  = @"D:\typhon\wal",      // Sequential I/O: WAL only
};
```

Benefits:
- WAL sequential writes don't compete with data page random reads (70% workload)
- No head-of-line blocking: WAL flush doesn't wait for data page reads to complete
- Separate wear leveling: WAL SSD sees only sequential writes (optimal for TLC/QLC NAND)

---

## 11. Crash Recovery

### 11.1 Recovery Overview

Recovery restores the database to the last consistent state after an unexpected crash:

```
Recovery Flow:
├── Phase 1: Read Checkpoint LSN from data file header
├── Phase 2: Open WAL segments starting at Checkpoint LSN
├── Phase 3: Scan WAL records forward
│   ├── For each complete transaction (has TransactionEnd):
│   │   ├── Replay all operations
│   │   └── Mark in epoch registry as Committed
│   ├── For each FPI record:
│   │   ├── Compare page checksum on disk
│   │   └── If torn: restore from FPI
│   └── For incomplete transactions (no TransactionEnd):
│       └── Discard (transaction was in-flight at crash)
├── Phase 4: Verify all data page checksums
│   └── Any torn pages without FPI → report corruption
└── Phase 5: Resume normal operation
```

### 11.2 CRC Chain Validation

During WAL scan, each record's `PrevCRC` must match the previous record's `CRC`. A mismatch indicates:
- Torn WAL write (crash during WAL I/O) → truncate WAL at this point
- Media corruption → report and attempt repair from prior checkpoint

```csharp
uint expectedPrevCRC = 0;

foreach (var record in ScanSegmentForward(segment))
{
    // Verify chain
    if (record.Header.PrevCRC != expectedPrevCRC)
    {
        // WAL is torn here — everything before is good
        return RecoverUpTo(record.Header.LSN - 1);
    }

    // Verify self-integrity
    if (!VerifyCRC(record))
    {
        return RecoverUpTo(record.Header.LSN - 1);
    }

    expectedPrevCRC = record.Header.CRC;
}
```

### 11.3 Transaction Completeness Check

A transaction is replayable only if ALL of these conditions hold:
1. First record has `TransactionBegin` flag
2. Last record has `TransactionEnd` flag with matching TSN
3. All records in between have matching `TransactionTSN`
4. CRC chain is unbroken across all records

Incomplete transactions (missing `TransactionEnd`) are rolled back — their WAL records are simply ignored during replay.

### 11.4 FPI Replay

When an FPI record is encountered during recovery:

```csharp
void ReplayFPI(WalRecordHeader header, ReadOnlySpan<byte> payload)
{
    var meta = MemoryMarshal.Read<FpiMetadata>(payload);
    var pageData = payload.Slice(sizeof(FpiMetadata));

    // Read current page from data file
    var currentPage = ReadPageFromDisk(meta.SegmentId, meta.PageIndex);

    // Check if page is torn (CRC mismatch)
    if (!VerifyPageChecksum(currentPage))
    {
        // Page is corrupted — restore from FPI
        WritePageToDisk(meta.SegmentId, meta.PageIndex, pageData);
        _logger.Info("Repaired torn page {Segment}/{Page} from FPI at LSN {LSN}",
            meta.SegmentId, meta.PageIndex, header.LSN);
    }
    // If page checksum is valid, FPI is informational only (page already correct)
}
```

### 11.5 Recovery Time Estimation

| Scenario | WAL to Scan | Records | Time |
|----------|------------|---------|------|
| 5s checkpoint, 100K tx/s, 6 records/tx | ~32 MB | ~600K records | ~200ms |
| 10s checkpoint, 100K tx/s, 6 records/tx | ~64 MB | ~1.2M records | ~400ms |
| 10s checkpoint, 10K tx/s, 6 records/tx | ~6.4 MB | ~120K records | ~40ms |

Sequential SSD reads at 3 GB/sec + CRC validation overhead → sub-second recovery for all scenarios.

---

## 12. CRC32C Implementation

### 12.1 Hardware Acceleration in .NET

> ⚠️ **Important**: `System.IO.Hashing.Crc32` computes IEEE 802.3 CRC-32 (polynomial `0x04C11DB7`), which is **NOT** CRC32C.
> The Castagnoli polynomial (`0x1EDC6F41`) required for database checksums is only available via the SSE4.2/ARM hardware intrinsics directly.
> There is no `Crc32C` class in .NET's standard library — we implement it using `System.Runtime.Intrinsics`.

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

/// <summary>
/// Hardware-accelerated CRC32C (Castagnoli polynomial 0x1EDC6F41) computation.
/// Uses SSE4.2 CRC32 instruction on x86/x64, ARM CRC32C instructions on ARM64.
/// Falls back to software lookup table on unsupported platforms.
///
/// Performance: ~1.3µs per 8KB page (sequential), ~0.5µs with 3-way interleaving.
/// The SSE4.2 CRC32 instruction has 3-cycle latency but 1-cycle throughput.
/// </summary>
public static class WalCrc
{
    /// <summary>
    /// Compute CRC32C over a data span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        return ComputePartial(0xFFFFFFFF, data) ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Compute CRC32C over a page, skipping the checksum field itself.
    /// The skip region is treated as zeros for CRC purposes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeSkipping(ReadOnlySpan<byte> data, int skipOffset, int skipLength)
    {
        uint crc = 0xFFFFFFFF;
        if (skipOffset > 0)
            crc = ComputePartial(crc, data[..skipOffset]);
        int afterSkip = skipOffset + skipLength;
        if (afterSkip < data.Length)
            crc = ComputePartial(crc, data[afterSkip..]);
        return crc ^ 0xFFFFFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputePartial(uint crc, ReadOnlySpan<byte> data)
    {
        if (Sse42.X64.IsSupported)
            return ComputeSse42X64(crc, data);
        if (Sse42.IsSupported)
            return ComputeSse42X32(crc, data);
        if (ArmCrc32.Arm64.IsSupported)
            return ComputeArm64(crc, data);
        return ComputeSoftware(crc, data);
    }

    /// <summary>
    /// SSE4.2 x64: Process 8 bytes per iteration via CRC32 r64, r/m64.
    /// For 8KB page: 1024 iterations of main loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeSse42X64(uint crc, ReadOnlySpan<byte> data)
    {
        ulong crc64 = crc;
        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int offset = 0;
        int aligned = data.Length & ~7;

        while (offset < aligned)
        {
            crc64 = Sse42.X64.Crc32(crc64,
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, offset)));
            offset += 8;
        }

        uint crc32 = (uint)crc64;
        while (offset < data.Length)
        {
            crc32 = Sse42.Crc32(crc32, Unsafe.Add(ref ptr, offset));
            offset++;
        }
        return crc32;
    }

    /// <summary>
    /// SSE4.2 x86 (32-bit): Process 4 bytes per iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeSse42X32(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int offset = 0;
        int aligned = data.Length & ~3;

        while (offset < aligned)
        {
            crc = Sse42.Crc32(crc,
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, offset)));
            offset += 4;
        }
        while (offset < data.Length)
        {
            crc = Sse42.Crc32(crc, Unsafe.Add(ref ptr, offset));
            offset++;
        }
        return crc;
    }

    /// <summary>
    /// ARM64: Process 8 bytes per iteration via CRC32CX instruction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeArm64(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int offset = 0;
        int aligned = data.Length & ~7;

        while (offset < aligned)
        {
            crc = ArmCrc32.Arm64.ComputeCrc32C(crc,
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, offset)));
            offset += 8;
        }
        while (offset < data.Length)
        {
            crc = ArmCrc32.ComputeCrc32C(crc, Unsafe.Add(ref ptr, offset));
            offset++;
        }
        return crc;
    }

    /// <summary>
    /// Software fallback: byte-at-a-time with precomputed table.
    /// Castagnoli polynomial (bit-reversed): 0x82F63B78.
    /// </summary>
    private static uint ComputeSoftware(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = (crc >> 8) ^ s_table[(byte)(crc ^ b)];
        return crc;
    }

    private static readonly uint[] s_table = GenerateTable(0x82F63B78u);

    private static uint[] GenerateTable(uint polynomial)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
                entry = (entry & 1) != 0 ? (entry >> 1) ^ polynomial : entry >> 1;
            table[i] = entry;
        }
        return table;
    }
}
```

### 12.2 Performance Characteristics

SSE4.2 CRC32 instruction: 3-cycle latency, 1-cycle throughput. At ~4 GHz, sequential processing yields ~8 bytes/3 cycles ≈ 10.7 GB/s theoretical. Real-world with loop overhead: ~6 GB/s.

| Operation | Size | Hardware CRC (SSE4.2 x64) | Software CRC |
|-----------|------|--------------------------|-------------|
| WAL record header | 48 bytes | ~8ns | ~50ns |
| Typical component | 128 bytes | ~20ns | ~130ns |
| Full page (FPI) | 8000 bytes | ~1.3µs | ~8µs |
| WAL record (header + 128B payload) | 176 bytes | ~28ns | ~180ns |

For a typical 6-record transaction (1056 bytes total): **~170ns total CRC time** — negligible vs I/O cost.

**Future optimization (Phase 4):** 3-way interleaved CRC32C exploits the instruction pipeline (3-cycle latency but 1-cycle throughput) to achieve ~3x speedup on large buffers (≥192 bytes). Uses PCLMULQDQ (`Pclmulqdq.CarrylessMultiply`) to combine partial CRCs. This would reduce FPI checksums from ~1.3µs to ~0.4µs per page.

### 12.3 Page Checksum Placement

Each 8KB page gets a CRC32C stored in `PageBaseHeader`:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct PageBaseHeader  // 64 bytes
{
    public int   PageIndex;           // 4B
    public long  ChangeRevision;      // 8B
    // ... existing fields ...
    public uint  PageCRC32C;          // 4B — CRC32C of remaining 8128 bytes
}
```

**Verification protocol:**
- On page read from disk: verify CRC, report corruption if mismatch
- On page write to disk: compute CRC, store in header, then write
- During recovery: CRC mismatch + no FPI = unrecoverable corruption (log + skip)

---

## 13. Platform Abstraction

### 13.1 I/O Operations Needed

Based on [IO-Pipeline-Crash-Semantics.md](../research/IO-Pipeline-Crash-Semantics.md) research:

```csharp
/// <summary>
/// Platform I/O abstraction for WAL operations.
/// Encapsulates O_DIRECT + FUA + pre-allocation semantics.
/// </summary>
public interface IWalPlatformIO : IDisposable
{
    /// <summary>
    /// Open WAL segment file with O_DIRECT + optional FUA.
    /// Windows: FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH (if FUA)
    /// Linux: O_DIRECT | O_DSYNC (if FUA)
    /// </summary>
    SafeFileHandle OpenSegment(string path, bool useFUA);

    /// <summary>
    /// Write aligned buffer to file at given offset.
    /// Buffer MUST be 4096-byte aligned. Size MUST be multiple of 4096.
    /// </summary>
    void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Flush file buffers (for GroupCommit mode when FUA is not used).
    /// Windows: FlushFileBuffers. Linux: fdatasync.
    /// </summary>
    void FlushBuffers(SafeFileHandle handle);

    /// <summary>
    /// Pre-allocate file to given size (avoids metadata updates on extend).
    /// Windows: SetEndOfFile. Linux: fallocate.
    /// </summary>
    void PreAllocate(SafeFileHandle handle, long size);

    /// <summary>
    /// Allocate aligned memory for O_DIRECT buffers.
    /// </summary>
    IntPtr AllocAligned(int size, int alignment = 4096);

    /// <summary>
    /// Free aligned memory.
    /// </summary>
    void FreeAligned(IntPtr ptr);
}
```

### 13.2 Windows Implementation Notes

```csharp
// CreateFileW flags for WAL segment:
//   FILE_FLAG_NO_BUFFERING  → bypass OS page cache (O_DIRECT equivalent)
//   FILE_FLAG_WRITE_THROUGH → FUA (data hits platters before return)
//   FILE_FLAG_SEQUENTIAL_SCAN → hint for read-ahead during recovery

// For Immediate mode: both flags enabled
// For GroupCommit: NO_BUFFERING only, explicit FlushFileBuffers on timer
// For Deferred: NO_BUFFERING only, no explicit flush

// Pre-allocation: SetFilePointerEx + SetEndOfFile
// Aligned allocation: NativeMemory.AlignedAlloc(size, 4096)
```

### 13.3 Why O_DIRECT for WAL?

Counter-intuitive but critical:
1. **Avoids double-buffering**: OS page cache is useless for append-only sequential writes
2. **Predictable latency**: No interference from page cache pressure or writeback
3. **Enables FUA**: On Windows, `WRITE_THROUGH` only works correctly with `NO_BUFFERING`
4. **Prevents false durability**: Without O_DIRECT, `fsync` might return before data hits media

---

## 14. Performance Budget

### 14.1 Commit Path Breakdown (Typical 6-Record Transaction)

| Phase | Time | Notes |
|-------|------|-------|
| Calculate WAL size | ~20ns | Count operations, compute aligned total |
| TryClaim (atomic tail) | ~10ns | Single `LOCK XADD` instruction |
| Serialize records | ~200ns | 6 × memcpy of 128B + header fill |
| CRC32C computation | ~50ns | Hardware-accelerated over 1056B |
| Publish claim | ~5ns | Volatile write |
| **Total producer-side** | **~285ns** | Before any I/O |

### 14.2 Durability Wait Overhead

| Mode | Additional Latency | Notes |
|------|-------------------|-------|
| Deferred | 0 | Return immediately after publish |
| GroupCommit | ≤10ms amortized | Wait for timer (5ms avg) |
| Immediate | 15–85µs | Wait for FUA completion |

### 14.3 WAL Writer Throughput

At typical 1056 bytes per transaction:
- **Sequential write bandwidth**: 2–3 GB/sec on NVMe
- **Max throughput**: 2M+ transactions/sec (I/O limited, not CPU limited)
- **FUA overhead**: Reduces to ~50K–200K FUA'd transactions/sec
- **GroupCommit at 5ms**: Batches 500–5000 transactions per flush → effective 1M+ durable tx/sec

### 14.4 Comparison: WAL + Epoch vs Epoch-Only

| Metric | Epoch-Only (old design) | WAL + Epoch (new design) |
|--------|------------------------|--------------------------|
| Commit latency (Deferred) | ~1µs | ~1.3µs (+285ns WAL serialize) |
| Commit latency (GroupCommit) | N/A | ~1.3µs + ≤10ms wait |
| Commit latency (Immediate) | 2× fsync ~150µs | 1× FUA ~50µs |
| Recovery time | O(1) scan | O(WAL_size) scan + O(1) |
| Torn page survival | ❌ Silent corruption | ✅ FPI repair |
| Data loss window (Deferred) | Last epoch | Same (last epoch) |
| Data loss window (GroupCommit) | N/A | ≤10ms |
| Data loss window (Immediate) | 0 (but 3× slower) | 0 (and faster) |

---

## 15. Implementation Phases

### Phase 1: Foundation (CRC32C + Page Checksums)

**Files to create/modify:**
- `src/Typhon.Engine/Misc/WalCrc.cs` — CRC32C via SSE4.2/ARM intrinsics (no external packages)
- `src/Typhon.Engine/Persistence Layer/PageBaseHeader.cs` — Add CRC field
- `src/Typhon.Engine/Persistence Layer/PagedMMF.cs` — Verify on read, compute on write
- `test/Typhon.Engine.Tests/Misc/WalCrcTests.cs` — Verify against known CRC32C test vectors

**Deliverable:** All pages get checksums. Torn pages detected on read. No WAL yet.

### Phase 2: WAL Core (Record Format + Ring Buffer + Writer)

**Files to create:**
- `src/Typhon.Engine/WAL/WalRecordHeader.cs` — Record struct + flags
- `src/Typhon.Engine/WAL/WalSegmentHeader.cs` — Segment file header
- `src/Typhon.Engine/WAL/WalCommitBuffer.cs` — Ring buffer with atomic allocation
- `src/Typhon.Engine/WAL/WalWriter.cs` — Dedicated writer thread
- `src/Typhon.Engine/WAL/WalSegmentManager.cs` — File lifecycle + pre-allocation
- `src/Typhon.Engine/WAL/IWalPlatformIO.cs` — Platform abstraction interface
- `src/Typhon.Engine/WAL/WindowsWalPlatformIO.cs` — Windows implementation

**Deliverable:** WAL writes happen on commit. No recovery yet. Deferred mode only.

### Phase 3: Durability Modes + FPI + Recovery

**Files to create/modify:**
- `src/Typhon.Engine/WAL/WalManager.cs` — Top-level API coordinating all WAL subsystems
- `src/Typhon.Engine/WAL/WalRecovery.cs` — Crash recovery logic
- `src/Typhon.Engine/WAL/FpiTracker.cs` — Bitmap tracking first-modification-since-checkpoint
- `src/Typhon.Engine/Database Engine/Transaction.cs` — Integrate WAL serialization
- `src/Typhon.Engine/Database Engine/DatabaseEngine.cs` — DurabilityMode configuration
- `src/Typhon.Engine/Persistence Layer/ManagedPagedMMF.cs` — Checkpoint LSN in header

**Deliverable:** All three durability modes functional. FPI on first modification. Crash recovery works.

### Phase 4: Polish & Optimization

- FPI compression (LZ4)
- Metrics / telemetry for WAL throughput, flush latency, buffer utilization
- WAL segment archiving (for point-in-time recovery — future extension)
- Benchmark suite for all durability modes
- Stress test: crash injection during WAL writes

---

## Appendix A: Glossary

| Term | Definition |
|------|-----------|
| **LSN** | Log Sequence Number — monotonically increasing identifier for each WAL record |
| **FPI** | Full-Page Image — complete page snapshot written to WAL for torn page repair |
| **FUA** | Force Unit Access — hardware guarantee that data is on persistent media |
| **Epoch** | Logical timestamp grouping transactions within a Unit of Work |
| **Checkpoint** | Process of flushing all dirty pages and advancing WAL recycle point |
| **O_DIRECT** | Bypass OS page cache for predictable I/O latency |
| **Segment** | Single WAL file (64MB default) containing sequential records |
| **Claim** | Buffer region atomically allocated to a transaction for WAL serialization |
| **CRC32C** | Castagnoli CRC variant with hardware acceleration (SSE4.2/ARM) |

## Appendix B: Configuration Reference

```csharp
public class WalOptions
{
    /// <summary>WAL directory path (recommend separate SSD)</summary>
    public string WalDirectory { get; set; } = "wal";

    /// <summary>Default durability mode for transactions</summary>
    public DurabilityMode DefaultDurabilityMode { get; set; } = DurabilityMode.GroupCommit;

    /// <summary>GroupCommit flush interval in milliseconds</summary>
    public int GroupCommitIntervalMs { get; set; } = 5;

    /// <summary>WAL segment file size in bytes</summary>
    public long SegmentSize { get; set; } = 64 * 1024 * 1024;  // 64 MB

    /// <summary>Number of pre-allocated segments ahead of write position</summary>
    public int PreAllocateSegments { get; set; } = 4;

    /// <summary>WAL commit buffer size in bytes (should hold ~2000 transactions)</summary>
    public int CommitBufferSize { get; set; } = 2 * 1024 * 1024;  // 2 MB

    /// <summary>Checkpoint interval in milliseconds</summary>
    public int CheckpointIntervalMs { get; set; } = 10_000;  // 10 seconds

    /// <summary>Enable FPI compression (LZ4). Phase 4 feature.</summary>
    public bool EnableFpiCompression { get; set; } = false;

    /// <summary>Pin WAL writer thread to specific CPU core (-1 = no pinning)</summary>
    public int WriterThreadCoreAffinity { get; set; } = -1;
}
```

## Appendix C: Key Invariants

These invariants must hold at all times for correctness:

1. **WAL-before-visibility**: A WAL record must be durably written (per durability mode) before the corresponding revision becomes visible to other transactions.

2. **FPI-before-modification**: The first time a page is modified after a checkpoint, its pre-modification image must be in the WAL before the in-memory page is mutated.

3. **CRC chain integrity**: Each record's `PrevCRC` equals the preceding record's `CRC`. A break in the chain indicates the end of valid WAL.

4. **Transaction atomicity**: A transaction's WAL records are all-or-nothing. Only transactions with a `TransactionEnd` record are replayed during recovery.

5. **Checkpoint ordering**: `CheckpointLSN` in the file header is always ≤ the oldest WAL segment's `FirstLSN` that contains un-checkpointed data.

6. **Segment monotonicity**: Segment IDs are strictly increasing. LSNs within a segment are strictly increasing. A lower segment ID always contains lower LSNs.

7. **Buffer claim ordering**: LSN assignment order equals buffer position order. The WAL writer flushes records in LSN order, never out-of-order.

---

## Appendix D: Design Decisions & Rationale

| Decision | Alternatives Considered | Rationale |
|----------|------------------------|-----------|
| Variable-length records | Fixed 8KB WAL pages (SQLite) | 95% of records are 48–300B; fixed-page wastes 96% of WAL bandwidth |
| Aeron-style linear buffer | Disruptor ring buffer | Variable-size records don't fit fixed slots; linear buffer is simpler |
| Double-buffer ping-pong | Circular wrap-around | Avoids complex wrap logic; simpler memory management |
| CRC32C via intrinsics | System.IO.Hashing.Crc32, xxHash | S.I.H.Crc32 is IEEE 802.3 (wrong polynomial); SSE4.2 CRC32 instruction is natively Castagnoli; standard in storage (iSCSI, ext4, RocksDB) |
| 64MB segments | 16MB (PostgreSQL), 4GB | 64MB balances pre-alloc cost, recovery scan time, and file count |
| Logical WAL records | Physical (page-level) WAL | Logical is 10–50× smaller for typical components; faster write |
| FPI for torn pages | Double-write buffer (InnoDB) | Simpler (no extra file); amortized into existing WAL stream |
| Single writer thread | Multiple writers, lock-striped | Sequential write is I/O optimal; single thread avoids ordering issues |
| Leader-follower group commit | Token-passing, epoch-based batch | Proven pattern (RocksDB); minimal latency for followers |

---

*End of WAL Design Document*

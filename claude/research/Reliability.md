# Typhon Reliability Architecture

**Date:** November 2024
**Status:** In progress
**Outcome:** —

---

This document outlines the critical reliability features missing from Typhon and provides detailed implementation guidance for achieving ACID durability guarantees.

## Executive Summary

Typhon currently provides:
- **Atomicity**: Via transaction isolation and commit/rollback
- **Consistency**: Via schema validation and constraint checking
- **Isolation**: Via MVCC snapshot isolation

Typhon is **missing**:
- **Durability**: No guarantee that committed transactions survive crashes

The three pillars of database durability are:
1. **Write-Ahead Logging (WAL)** - Ensures committed transactions can be recovered
2. **Crash Recovery** - Mechanism to restore consistent state after failure
3. **Page Checksums** - Detects storage corruption

---

## Part 1: Write-Ahead Logging (WAL)

### 1.1 Why WAL is Critical

Without WAL, Typhon has these failure modes:

| Scenario | Current Behavior | With WAL |
|----------|-----------------|----------|
| Crash during page write | Partial/torn page, corruption | Recover from log |
| Crash after commit returns | Data may be lost | Guaranteed durable |
| Power failure | Undefined state | Consistent recovery |
| OS crash | Cache not flushed | Log is fsync'd |

**The fundamental problem**: When `Commit()` returns success, the user expects data to be durable. Currently, data sits in:
1. Page cache (memory) - Lost on crash
2. OS file cache - Lost on power failure
3. Disk write cache - Lost on sudden power loss

WAL solves this by:
1. Writing changes to a sequential log file BEFORE modifying data pages
2. Calling `fsync()` on the log BEFORE returning from commit
3. Using the log to reconstruct state after crashes

### 1.2 Design Choices

#### Option A: Classic ARIES-Style WAL

**Description**: Industry-standard approach used by PostgreSQL, MySQL InnoDB, SQL Server.

**Architecture**:
```
┌─────────────────────────────────────────────────────────────────┐
│                        Transaction                               │
│  1. Begin transaction                                           │
│  2. Make changes (in-memory)                                    │
│  3. Write WAL records for each change                           │
│  4. Commit: Write COMMIT record → fsync WAL → return success    │
│  5. Background: Flush dirty pages to data file                  │
└─────────────────────────────────────────────────────────────────┘

WAL Record Structure:
┌────────┬─────────┬──────────┬────────┬──────────┬─────────────┐
│ LSN    │ TxnID   │ Type     │ PageID │ Offset   │ Before/After│
│ (8B)   │ (8B)    │ (1B)     │ (4B)   │ (2B)     │ (variable)  │
└────────┴─────────┴──────────┴────────┴──────────┴─────────────┘
```

**WAL Record Types**:
- `BEGIN`: Transaction start
- `UPDATE`: Page modification (before + after image)
- `INSERT`: New data (after image only)
- `DELETE`: Removed data (before image only)
- `COMMIT`: Transaction committed
- `ABORT`: Transaction rolled back
- `CHECKPOINT`: Snapshot of dirty page state

**Pros**:
- Battle-tested, well-understood algorithm
- Supports point-in-time recovery
- Enables replication via log shipping
- Fine-grained recovery (can undo/redo individual operations)

**Cons**:
- Complex implementation (~3000-5000 lines of code)
- Requires understanding ARIES protocol deeply
- Before-images needed for undo complicate the log
- Log truncation requires careful checkpoint management

**Effort**: High (4-6 weeks for experienced developer)

---

#### Option B: Shadow Paging (Copy-on-Write)

**Description**: Instead of logging changes, create new copies of modified pages.

**Architecture**:
```
Current Page Table          Shadow Page Table (during txn)
┌────────────────┐          ┌────────────────┐
│ Page 0 → 0x100 │          │ Page 0 → 0x100 │  (unchanged)
│ Page 1 → 0x200 │          │ Page 1 → 0x500 │  ← NEW COPY
│ Page 2 → 0x300 │          │ Page 2 → 0x300 │  (unchanged)
└────────────────┘          └────────────────┘

Commit: Atomically swap page table pointer
Crash: Old page table still valid
```

**Pros**:
- Conceptually simpler than WAL
- No undo needed (old pages preserved)
- Instant crash recovery (just use old page table)
- Natural fit for copy-on-write file systems (ZFS, Btrfs)

**Cons**:
- High space overhead (full page copies vs. delta logs)
- Poor locality (pages scattered across file)
- Difficult to implement efficiently for fine-grained updates
- Doesn't scale well for large transactions
- Makes sequential scans slower (page fragmentation)

**Effort**: Medium-High (3-4 weeks)

---

#### Option C: Log-Structured Storage (LSM-Style)

**Description**: All writes go to an append-only log. Periodically compact into sorted runs.

**Architecture**:
```
Writes → MemTable (sorted in-memory) → Flush to SSTable files
                                              ↓
                                       Background compaction
                                              ↓
                                       Merged SSTables

Read path: Check MemTable → Check SSTables (newest first)
```

**Pros**:
- Excellent write performance (sequential I/O only)
- Natural durability (log IS the data)
- Simple crash recovery
- Good for write-heavy workloads

**Cons**:
- **Requires complete rewrite of storage layer**
- Read amplification (must check multiple levels)
- Space amplification during compaction
- Compaction can cause latency spikes
- Poor fit for Typhon's fixed-size component model

**Effort**: Very High (3-6 months, essentially a new storage engine)

---

#### Option D: Simplified WAL (Typhon-Specific)

**Description**: A streamlined WAL designed specifically for Typhon's component model.

**Architecture**:
```
WAL optimized for Typhon's fixed-size components:

┌──────────────────────────────────────────────────────────────┐
│ WAL Header (per file)                                        │
│ - Magic number, version                                      │
│ - Last checkpoint LSN                                        │
│ - File sequence number                                       │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ WAL Record (fixed header + variable payload)                 │
│ - LSN (8 bytes): Log sequence number                         │
│ - TSN (8 bytes): Transaction sequence number                 │
│ - Type (1 byte): BEGIN/COMPONENT_WRITE/INDEX_WRITE/COMMIT    │
│ - Length (2 bytes): Payload length                           │
│ - Checksum (4 bytes): CRC32 of record                        │
│ - Payload: Type-specific data                                │
└──────────────────────────────────────────────────────────────┘

Component Write Payload:
┌──────────────────────────────────────────────────────────────┐
│ - ComponentTypeId (4 bytes)                                  │
│ - EntityId (8 bytes)                                         │
│ - ChunkId (4 bytes)                                          │
│ - SlotIndex (2 bytes)                                        │
│ - ComponentData (ComponentSize bytes) ← after-image only     │
└──────────────────────────────────────────────────────────────┘
```

**Key Simplifications**:
1. **No before-images**: Typhon's MVCC already preserves old versions in revision chains. Undo = read previous revision.
2. **Component-level granularity**: Log entire components, not byte-level changes.
3. **No nested transactions**: Simplifies commit protocol.
4. **Redo-only recovery**: MVCC handles "undo" naturally.

**Pros**:
- Tailored to Typhon's architecture
- Simpler than full ARIES (no undo logic)
- Leverages existing MVCC for rollback scenarios
- Moderate implementation effort

**Cons**:
- Not industry-standard (harder to find references)
- May log more data than necessary (full components vs. deltas)
- Still requires careful implementation

**Effort**: Medium (2-4 weeks)

---

### 1.3 Recommendation: Option D (Simplified WAL)

**Rationale**:
1. Typhon's MVCC already provides "before images" via revision chains
2. Component-level logging matches Typhon's data model
3. Fixed-size components simplify log record format
4. Avoids the complexity of full ARIES undo logic
5. Can be enhanced to full ARIES later if needed

### 1.4 Detailed Implementation Plan

#### Phase 1: WAL Infrastructure

**New Files**:
```
src/Typhon.Engine/
  WAL/
    WalFile.cs           - WAL file I/O operations
    WalRecord.cs         - Record structures and serialization
    WalWriter.cs         - Append records, manage fsync
    WalReader.cs         - Read records for recovery
    LogSequenceNumber.cs - LSN tracking
```

**WalRecord Types**:
```csharp
public enum WalRecordType : byte
{
    Begin = 1,
    ComponentCreate = 2,
    ComponentUpdate = 3,
    ComponentDelete = 4,
    IndexInsert = 5,
    IndexDelete = 6,
    Commit = 7,
    Abort = 8,
    Checkpoint = 9
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WalRecordHeader
{
    public long LSN;
    public long TSN;
    public WalRecordType Type;
    public ushort PayloadLength;
    public uint Checksum;
}
```

**WalWriter Core Logic**:
```csharp
public class WalWriter : IDisposable
{
    private FileStream _logFile;
    private long _currentLSN;
    private readonly object _writeLock = new();

    public long AppendRecord(WalRecordType type, long tsn, ReadOnlySpan<byte> payload)
    {
        lock (_writeLock)
        {
            var lsn = Interlocked.Increment(ref _currentLSN);

            var header = new WalRecordHeader
            {
                LSN = lsn,
                TSN = tsn,
                Type = type,
                PayloadLength = (ushort)payload.Length,
                Checksum = 0 // Computed below
            };

            // Compute checksum over header (minus checksum field) + payload
            header.Checksum = ComputeCRC32(header, payload);

            // Write header + payload
            WriteToLog(header, payload);

            return lsn;
        }
    }

    public void Flush()
    {
        _logFile.Flush(flushToDisk: true); // fsync
    }
}
```

#### Phase 2: Transaction Integration

**Modifications to Transaction.cs**:

```csharp
public class Transaction
{
    private List<WalRecord> _pendingWalRecords;
    private long _beginLSN;

    public void Init(DatabaseEngine dbe, int id)
    {
        // ... existing code ...

        // Write BEGIN record
        _beginLSN = dbe.WalWriter.AppendRecord(
            WalRecordType.Begin,
            TransactionTick,
            ReadOnlySpan<byte>.Empty);
    }

    // Called when creating/updating a component
    private void LogComponentWrite(ComponentTable table, long entityId,
        int chunkId, int slotIndex, ReadOnlySpan<byte> componentData)
    {
        var payload = BuildComponentPayload(table.Definition.ComponentTypeId,
            entityId, chunkId, slotIndex, componentData);

        var record = new WalRecord(WalRecordType.ComponentUpdate, payload);
        _pendingWalRecords.Add(record);
    }

    public bool Commit()
    {
        // ... existing conflict detection ...

        // Phase 1: Write all WAL records
        foreach (var record in _pendingWalRecords)
        {
            DBE.WalWriter.AppendRecord(record.Type, TransactionTick, record.Payload);
        }

        // Phase 2: Write COMMIT record and fsync
        DBE.WalWriter.AppendRecord(WalRecordType.Commit, TransactionTick,
            ReadOnlySpan<byte>.Empty);
        DBE.WalWriter.Flush(); // Critical: fsync before returning

        // Phase 3: Apply changes to data pages (existing code)
        // These can be async/lazy since WAL guarantees recovery

        return true;
    }
}
```

#### Phase 3: Checkpoint Implementation

Checkpoints reduce recovery time by recording which pages are dirty:

```csharp
public class CheckpointManager
{
    public void CreateCheckpoint()
    {
        // 1. Pause new transactions briefly
        _transactionChain.Control.EnterExclusiveAccess();

        try
        {
            // 2. Get list of all dirty pages
            var dirtyPages = _pageCache.GetDirtyPageList();

            // 3. Write checkpoint record with dirty page info
            var payload = SerializeDirtyPageList(dirtyPages);
            var checkpointLSN = _walWriter.AppendRecord(
                WalRecordType.Checkpoint, 0, payload);

            // 4. Flush dirty pages to data file
            foreach (var pageId in dirtyPages)
            {
                _pageCache.FlushPage(pageId);
            }

            // 5. Update checkpoint LSN in file header
            UpdateCheckpointLSN(checkpointLSN);

            // 6. Truncate WAL before checkpoint (optional)
            TruncateWalBefore(checkpointLSN);
        }
        finally
        {
            _transactionChain.Control.ExitExclusiveAccess();
        }
    }
}
```

---

## Part 2: Crash Recovery

### 2.1 Why Crash Recovery is Critical

After a crash, the database may be in an inconsistent state:
- Committed transactions partially written to disk
- Uncommitted transactions partially written (dirty pages flushed)
- Index structures inconsistent with component data
- Revision chains incomplete

### 2.2 Design Choices

#### Option A: Full ARIES Recovery

**Description**: Three-phase recovery: Analysis, Redo, Undo.

**Process**:
```
1. ANALYSIS: Scan WAL from last checkpoint
   - Identify transactions in-progress at crash
   - Build dirty page table
   - Find end of log

2. REDO: Replay all logged changes
   - Start from checkpoint
   - Apply all changes (committed and uncommitted)
   - Brings database to crash-time state

3. UNDO: Rollback uncommitted transactions
   - Scan backward through log
   - Undo changes from transactions without COMMIT
   - Write CLR (Compensation Log Records) for each undo
```

**Pros**:
- Complete recovery semantics
- Handles all crash scenarios
- Industry standard

**Cons**:
- Complex implementation
- Requires before-images for undo
- CLR records complicate the log

**Effort**: High (integrated with WAL implementation)

---

#### Option B: Redo-Only Recovery (Typhon-Specific)

**Description**: Leverage MVCC for undo, only redo committed transactions.

**Process**:
```
1. ANALYSIS: Scan WAL from last checkpoint
   - Build set of committed TSNs (saw COMMIT record)
   - Build set of uncommitted TSNs

2. REDO: Replay only committed transactions
   - For each record with TSN in committed set:
     - Apply the change to data pages
   - Skip records from uncommitted transactions

3. CLEANUP: Mark uncommitted revisions as rolled back
   - Scan revision chains
   - Any revision with TSN not in committed set → mark as rolled back
   - This is natural in MVCC: uncommitted = invisible
```

**Why This Works for Typhon**:
- MVCC already maintains previous versions
- Uncommitted changes have IsolationFlag set
- No need to physically undo - just don't commit them
- Revision chain naturally shows "valid" vs "invalid" versions

**Pros**:
- Simpler than full ARIES
- No before-images needed
- Leverages existing MVCC infrastructure
- Faster recovery (no undo phase)

**Cons**:
- Requires careful handling of isolation flags
- Must ensure revision chains are consistent
- Index cleanup may be needed

**Effort**: Medium (2-3 weeks, after WAL is implemented)

---

### 2.3 Recommendation: Option B (Redo-Only Recovery)

**Rationale**: Typhon's MVCC provides natural undo semantics. We only need to ensure committed transactions are durable.

### 2.4 Implementation Plan

**New File**: `src/Typhon.Engine/WAL/RecoveryManager.cs`

```csharp
public class RecoveryManager
{
    public void Recover()
    {
        Console.WriteLine("Starting crash recovery...");

        // Phase 1: Analysis
        var (committedTSNs, lastLSN) = AnalyzeWal();
        Console.WriteLine($"Found {committedTSNs.Count} committed transactions");

        // Phase 2: Redo committed transactions
        RedoCommittedTransactions(committedTSNs);

        // Phase 3: Clean up uncommitted revisions
        CleanupUncommittedRevisions(committedTSNs);

        // Phase 4: Rebuild indexes if necessary
        VerifyAndRepairIndexes();

        Console.WriteLine("Recovery complete");
    }

    private (HashSet<long> committed, long lastLSN) AnalyzeWal()
    {
        var committed = new HashSet<long>();
        var inProgress = new HashSet<long>();
        long lastLSN = 0;

        using var reader = new WalReader(_walPath);
        while (reader.TryReadNext(out var record))
        {
            lastLSN = record.LSN;

            switch (record.Type)
            {
                case WalRecordType.Begin:
                    inProgress.Add(record.TSN);
                    break;

                case WalRecordType.Commit:
                    inProgress.Remove(record.TSN);
                    committed.Add(record.TSN);
                    break;

                case WalRecordType.Abort:
                    inProgress.Remove(record.TSN);
                    break;
            }
        }

        // Transactions still in inProgress were uncommitted at crash
        Console.WriteLine($"Uncommitted transactions at crash: {inProgress.Count}");

        return (committed, lastLSN);
    }

    private void RedoCommittedTransactions(HashSet<long> committedTSNs)
    {
        using var reader = new WalReader(_walPath);
        while (reader.TryReadNext(out var record))
        {
            if (!committedTSNs.Contains(record.TSN))
                continue; // Skip uncommitted

            switch (record.Type)
            {
                case WalRecordType.ComponentCreate:
                case WalRecordType.ComponentUpdate:
                    RedoComponentWrite(record);
                    break;

                case WalRecordType.IndexInsert:
                    RedoIndexInsert(record);
                    break;

                case WalRecordType.IndexDelete:
                    RedoIndexDelete(record);
                    break;
            }
        }
    }
}
```

---

## Part 3: Page Checksums

### 3.1 Why Page Checksums are Critical

Silent data corruption can occur from:
- Disk firmware bugs
- Controller errors
- Bit rot (magnetic decay)
- Cosmic rays (single-bit flips)
- Partial writes (torn pages)

Without checksums, corrupted data is silently returned to users.

### 3.2 Design Choices

#### Option A: CRC32 per Page

**Description**: Store 4-byte CRC32 checksum in page header.

```
Page Layout with Checksum:
┌────────────────────────────────────────────┐
│ PageBaseHeader                             │
│   - PageIndex (4 bytes)                    │
│   - ChangeRevision (8 bytes)               │
│   - Checksum (4 bytes) ← NEW               │
│   - ... other fields ...                   │
├────────────────────────────────────────────┤
│ PageMetadata (128 bytes)                   │
├────────────────────────────────────────────┤
│ PageRawData (remaining bytes)              │
└────────────────────────────────────────────┘
```

**Verification Points**:
- After reading page from disk
- Before writing page to disk (compute new checksum)
- Optionally: background verification thread

**Pros**:
- Simple to implement
- Low overhead (CRC32 is fast, especially with hardware acceleration)
- Industry standard approach

**Cons**:
- 4 bytes per page overhead (0.05% for 8KB pages)
- CRC32 not cryptographically secure (but fine for corruption detection)

**Effort**: Low (1-2 days)

---

#### Option B: xxHash per Page

**Description**: Use xxHash (faster than CRC32 on modern CPUs).

**Pros**:
- Faster than CRC32 on large data
- Good distribution

**Cons**:
- Less standard than CRC32
- No hardware acceleration
- Slightly larger (8 bytes for xxHash64)

**Effort**: Low (1-2 days)

---

#### Option C: Full-Page Checksums with Torn Page Detection

**Description**: Enhanced checksums that detect torn pages (partial writes).

```
Page Layout:
┌────────────────────────────────────────────┐
│ Header Checksum (4 bytes) - covers header  │
│ Page Header                                │
├────────────────────────────────────────────┤
│ Data Checksum (4 bytes) - covers data      │
│ Page Data                                  │
├────────────────────────────────────────────┤
│ Footer Checksum (4 bytes) - copy of data   │
│ Page Sequence Number (8 bytes)             │
└────────────────────────────────────────────┘

Torn Page Detection:
- If footer checksum != data checksum → torn page
- Page sequence must match header sequence
```

**Pros**:
- Detects torn pages
- Higher integrity assurance

**Cons**:
- More complex
- Higher overhead
- May be overkill with WAL (which handles torn pages)

**Effort**: Medium (1 week)

---

### 3.3 Recommendation: Option A (CRC32 per Page)

**Rationale**:
1. CRC32 is sufficient for corruption detection
2. Hardware-accelerated on modern CPUs (SSE4.2, ARM CRC)
3. Industry standard (PostgreSQL, MySQL, SQLite all use CRC32)
4. WAL handles torn page recovery, so we only need corruption detection

### 3.4 Implementation Plan

**Modifications to PagedMMF.cs**:

```csharp
// Add to PageBaseHeader
[StructLayout(LayoutKind.Sequential)]
public struct PageBaseHeader
{
    public int PageIndex;
    public long ChangeRevision;
    public uint Checksum;        // NEW: CRC32 of page (excluding checksum field)
    public PageState State;
    // ... rest of header ...
}

// Add checksum computation
public static class PageChecksum
{
    // Use hardware-accelerated CRC32 if available
    public static uint Compute(ReadOnlySpan<byte> pageData)
    {
        // Skip the checksum field itself (offset 12, length 4)
        uint crc = 0;
        crc = Crc32.ComputeHash(crc, pageData.Slice(0, 12));
        crc = Crc32.ComputeHash(crc, pageData.Slice(16)); // Skip checksum field
        return crc;
    }

    public static bool Verify(ReadOnlySpan<byte> pageData, uint expectedChecksum)
    {
        return Compute(pageData) == expectedChecksum;
    }
}

// Modify page read
private void ReadPageFromDisk(int pageIndex, Span<byte> buffer)
{
    // ... existing read code ...

    ref var header = ref MemoryMarshal.AsRef<PageBaseHeader>(buffer);

    if (!PageChecksum.Verify(buffer, header.Checksum))
    {
        throw new PageCorruptionException(pageIndex,
            $"Page {pageIndex} failed checksum verification");
    }
}

// Modify page write
private void WritePageToDisk(int pageIndex, ReadOnlySpan<byte> buffer)
{
    // Compute checksum before writing
    ref var header = ref MemoryMarshal.AsRef<PageBaseHeader>(buffer);
    header.Checksum = PageChecksum.Compute(buffer);

    // ... existing write code ...
}
```

**New Exception**:
```csharp
public class PageCorruptionException : Exception
{
    public int PageIndex { get; }

    public PageCorruptionException(int pageIndex, string message)
        : base(message)
    {
        PageIndex = pageIndex;
    }
}
```

---

## Part 4: Implementation Order and Effort Summary

### Recommended Implementation Order

```
Phase 1: Page Checksums (Foundation)
    ↓
Phase 2: WAL Infrastructure
    ↓
Phase 3: Transaction WAL Integration
    ↓
Phase 4: Crash Recovery
    ↓
Phase 5: Checkpoint Implementation
    ↓
Phase 6: Testing and Hardening
```

### Effort and Complexity Summary

| Component | Effort | Complexity | Existing Code Impact |
|-----------|--------|------------|---------------------|
| **Page Checksums** | 2-3 days | Low | Minor: Add checksum field to PageBaseHeader, modify read/write paths |
| **WAL Infrastructure** | 1-2 weeks | Medium | New code: WalFile, WalWriter, WalReader, WalRecord |
| **Transaction Integration** | 1 week | Medium-High | Moderate: Modify Transaction.cs commit path, add logging calls |
| **Crash Recovery** | 1-2 weeks | High | New code: RecoveryManager, startup integration |
| **Checkpoint Manager** | 1 week | Medium | New code: CheckpointManager, integration with page cache |
| **Testing** | 2 weeks | Medium | New tests: crash simulation, corruption tests, recovery verification |

**Total Estimated Effort**: 6-10 weeks

### Existing Code Modifications Required

#### PagedMMF.cs (Medium Impact)
- Add checksum computation in read/write paths
- Add fsync support for WAL
- Expose dirty page list for checkpoints

```csharp
// Changes needed:
// 1. Add checksum verification in RequestPage
// 2. Add checksum computation in FlushPage
// 3. Add FlushToDisk flag support
// 4. Add GetDirtyPageList() method
```

#### Transaction.cs (High Impact)
- Add WAL record generation for each operation
- Modify Commit() to write WAL records and fsync
- Add recovery-related fields (TSN tracking)

```csharp
// Changes needed:
// 1. Add _pendingWalRecords list
// 2. Log ComponentCreate/Update/Delete operations
// 3. Log IndexInsert/Delete operations
// 4. Modify Commit() for WAL-first protocol
```

#### DatabaseEngine.cs (Medium Impact)
- Add WalWriter instance
- Add RecoveryManager integration
- Startup recovery logic

```csharp
// Changes needed:
// 1. Create WalWriter in constructor
// 2. Call RecoveryManager.Recover() on startup
// 3. Create CheckpointManager
// 4. Periodic checkpoint scheduling
```

#### ComponentTable.cs (Low Impact)
- May need to expose chunk allocation details for WAL logging
- No fundamental changes required

### Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Performance regression from fsync | High | Medium | Group commits, async WAL writes |
| Complex crash recovery bugs | Medium | High | Extensive testing, fuzzing |
| WAL file growth | Medium | Low | Checkpoint and truncation |
| Compatibility with existing databases | Low | High | Version header, migration path |

---

## Part 5: Performance Considerations

### WAL Performance Optimization

1. **Group Commit**: Batch multiple transaction commits into single fsync
   ```csharp
   // Instead of fsync per transaction:
   // Wait up to 10ms or 100 transactions, then fsync once
   ```

2. **Async WAL Writes**: Write WAL records asynchronously, sync only at commit
   ```csharp
   // Write records to buffer, background thread flushes
   // Only sync when Commit() is called
   ```

3. **WAL Compression**: Compress WAL records (especially for large components)
   ```csharp
   // LZ4 compression for records > 256 bytes
   ```

4. **Separate WAL Disk**: Put WAL on different physical disk than data
   ```
   // WAL: Sequential writes only → SSD or fast HDD
   // Data: Random access → NVMe recommended
   ```

### Checkpoint Frequency Tuning

- **Too frequent**: Excessive I/O, slower overall performance
- **Too infrequent**: Long recovery time after crash

Recommended: Checkpoint every 5 minutes or every 10,000 transactions, whichever comes first.

---

## Part 6: Testing Strategy

### Unit Tests

```csharp
[Test]
public void WAL_WriteAndRead_RoundTrip()
{
    // Write records, read back, verify contents
}

[Test]
public void Checksum_CorruptedPage_ThrowsException()
{
    // Corrupt a byte, verify checksum fails
}

[Test]
public void Recovery_CrashedDuringCommit_TransactionRolledBack()
{
    // Write BEGIN + changes, no COMMIT
    // Run recovery
    // Verify changes not visible
}

[Test]
public void Recovery_CrashedAfterCommit_TransactionDurable()
{
    // Write BEGIN + changes + COMMIT
    // Run recovery
    // Verify changes visible
}
```

### Crash Simulation Tests

```csharp
[Test]
public void CrashDuringPageWrite_RecoverySucceeds()
{
    // Use mock file system to simulate crash mid-write
    // Run recovery
    // Verify consistent state
}

[Test]
public void PowerFailure_WALSynced_DataRecovered()
{
    // Write data, commit (with fsync)
    // Simulate power failure (discard unflushed pages)
    // Run recovery from WAL
    // Verify data present
}
```

### Fuzzing Tests

```csharp
[Test]
public void Fuzz_RandomCrashPoints_AlwaysRecoverable()
{
    // Run random transactions
    // Crash at random points
    // Always verify recovery produces consistent state
}
```

---

## Appendix A: Industry Comparisons

| Feature | PostgreSQL | MySQL InnoDB | SQLite | Typhon (Current) | Typhon (Proposed) |
|---------|------------|--------------|--------|------------------|-------------------|
| WAL | Yes (pg_wal) | Yes (redo log) | Yes (WAL mode) | No | Yes |
| Checksum | Yes (data_checksums) | Yes (innodb_checksum) | Yes | No | Yes |
| Recovery | ARIES-based | ARIES-based | Rollback journal | None | Redo-only |
| Durability | fsync configurable | fsync configurable | fsync configurable | No fsync | fsync on commit |

---

## Appendix B: Configuration Options (Future)

```csharp
public class DurabilityOptions
{
    // WAL sync mode
    public WalSyncMode SyncMode { get; set; } = WalSyncMode.FsyncOnCommit;

    // Checkpoint interval
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(5);
    public int CheckpointTransactionThreshold { get; set; } = 10000;

    // Checksum verification
    public bool VerifyChecksumsOnRead { get; set; } = true;
    public bool BackgroundChecksumVerification { get; set; } = false;

    // Group commit settings
    public bool EnableGroupCommit { get; set; } = true;
    public TimeSpan GroupCommitDelay { get; set; } = TimeSpan.FromMilliseconds(10);
    public int GroupCommitMaxTransactions { get; set; } = 100;
}

public enum WalSyncMode
{
    NoSync,           // Fastest, no durability guarantee
    FsyncOnCommit,    // Default, sync on each commit
    FsyncOnCheckpoint // Sync only at checkpoints
}
```

---

## Summary

Implementing these reliability features will transform Typhon from a fast but crash-vulnerable engine into a production-ready database with full ACID guarantees. The recommended approach:

1. **Start with checksums** (low risk, high value)
2. **Build simplified WAL** (leverages MVCC for undo)
3. **Implement redo-only recovery** (simpler than full ARIES)
4. **Add checkpoints** (reduces recovery time)

This pragmatic approach provides robust durability while avoiding the complexity of a full ARIES implementation.

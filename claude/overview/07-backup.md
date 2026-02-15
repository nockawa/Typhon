# Component 7: Backup & Restore

> Page-level forward-incremental backups with dirty bitmap tracking, scoped Copy-on-Write, and compaction-based lifecycle.

---

## Overview

The Backup component creates consistent incremental snapshots of the database at the page level. Only **changed pages** are captured at each backup point, stored as compressed `.pack` files in an external backup directory. A **persistent dirty bitmap** (maintained by the checkpoint pipeline) provides O(changed pages) change detection, and a **scoped CoW shadow buffer** ensures snapshot consistency during the backup window.

<a href="../assets/typhon-pitbackup-architecture.svg"><img src="../assets/typhon-pitbackup-architecture.svg" width="1200" alt="PIT Backup — Architecture Overview"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

**Key design choices:**
- **Forward incrementals** — backup I/O proportional to changes, not database size
- **No WAL dependency** — backups are self-contained page snapshots (aligns with ADR-014)
- **Persistent dirty bitmap** — O(changed pages) detection across checkpoint intervals
- **Scoped CoW** — shadow buffer limited to dirty pages only (~10% of DB at typical churn)
- **Periodic compaction** — bounds chain depth; amortizes O(DB size) cost over many incrementals

**Design document:** For byte-level format specs, I/O simulations, code samples, and in-depth analysis, see the [PIT Backup Design Series](../design/pit-backup/README.md) (6-part deep design doc).

---

## Status: 🆕 New

This component does not yet exist in the codebase. This document captures the design.

---

## Sub-Components

| # | Name | Purpose | Status |
|---|------|---------|--------|
| **7.1** | [Dirty Bitmap & Change Detection](#71-dirty-bitmap--change-detection) | Track pages modified since last backup | 🆕 New |
| **7.2** | [CoW Shadow Buffer](#72-cow-shadow-buffer) | Capture pre-modification pages during active backup | 🆕 New |
| **7.3** | [Seqlock Page Protection](#73-seqlock-page-protection) | Zero-cost CRC consistency for checkpoint copies | 🆕 New |
| **7.4** | [Backup Manager](#74-backup-manager) | Create incremental backups, manage catalog | 🆕 New |
| **7.5** | [Backup File Format](#75-backup-file-format) | `.pack` file layout, catalog, page index | 🆕 New |
| **7.6** | [Reconstruction & Restore](#76-reconstruction--restore) | Rebuild database from backup chain | 🆕 New |
| **7.7** | [Compaction & Pruning](#77-compaction--pruning) | Consolidate chain, manage retention | 🆕 New |

---

## 7.1 Dirty Bitmap & Change Detection

### Purpose

Track which pages have been modified since the last backup, enabling O(changed pages) change detection instead of scanning all page revisions.

### How It Works

The checkpoint pipeline already iterates over dirty pages during each flush cycle. The backup system adds a single bit-OR operation per flushed page:

```csharp
// Inside checkpoint flush loop, per dirty page (cost: ~1-2 ns):
_backupDirtyBitmap.Set(pageInfo.FilePageIndex);
```

This accumulates changes across all checkpoint cycles between backups. With checkpoints every ~10 seconds and backups every ~4 hours, the bitmap accumulates ~1,440 checkpoint cycles of changes.

### Data Structure

| Property | Value |
|----------|-------|
| Type | `ConcurrentBitmapL3All` |
| Size | 1 bit per page — `ceil(TotalAllocatedPages / 8)` bytes |
| 1 GB DB (128K pages) | 16 KB |
| 10 GB DB (1.28M pages) | 160 KB |
| 100 GB DB (12.8M pages) | 1.6 MB |

### Lifecycle

1. **Normal operation:** Checkpoint pipeline OR's each flushed page's bit
2. **Backup starts:** Backup manager reads a snapshot of the bitmap, then clears it
3. **During backup:** New changes accumulate in the cleared bitmap (captured by next backup)
4. **Crash safety:** If backup fails, the bitmap remains intact (it was read-then-cleared atomically). The bitmap is **persisted at each checkpoint** alongside checkpoint metadata (see [Part 2 — Backup Creation](../design/pit-backup/02-backup-creation.md) §Persistence). On crash recovery, the persisted bitmap (covering T_backup → T_last_checkpoint) is loaded, then WAL replay re-dirties pages for the delta (T_last_checkpoint → T_crash), and the next checkpoint OR's those bits back — giving full coverage from T_backup to T_crash. If the persisted bitmap is corrupted (torn write), recovery falls back to forcing a full backup on the next attempt.

> **Detailed spec:** See [Part 2 — Backup Creation](../design/pit-backup/02-backup-creation.md) §Dirty Bitmap Lifecycle

---

## 7.2 CoW Shadow Buffer

### Purpose

Intercept page mutations during an active backup to preserve the consistent snapshot-start state. The mechanism ensures transactions continue at full speed while the backup captures a frozen view of changed pages.

### Scoped Interception

Unlike a full-database CoW, this design scopes the shadow buffer to **only pages in the dirty bitmap** — typically ~10% of the database at 10% churn. This reduces shadow pool traffic by 10x.

```csharp
// Inside TryLatchPageExclusive, just before: pi.PageState = PageState.Exclusive
if (_backupActive != 0)                              // 1 volatile read (~1 cycle)
{
    if (_backupDirtyBitmap.IsSet(pi.FilePageIndex))  // 1 bitmap test — scoped!
    {
        TryCoWForBackup(pi);                         // only for changed pages
    }
}
```

### TryCoWForBackup — The CoW Logic

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]  // keep hot path icache-friendly
private void TryCoWForBackup(PageInfo pi)
{
    int filePageIndex = pi.FilePageIndex;

    // Atomic test-and-set: only first thread for this page wins
    if (!_cowBitmap.TrySet(filePageIndex))
        return;  // already CoW'd — skip

    // Allocate shadow slot (lock-free bump allocator)
    int slot = Interlocked.Increment(ref _shadowPoolHead) - 1;
    if (slot >= _shadowPoolSize)
        slot = WaitForFreeSlot();  // backpressure if pool exhausted

    // Copy 8KB from page cache to shadow pool (both pinned)
    byte* src = _memPagesAddr + (pi.MemPageIndex * PageSize);
    byte* dst = _shadowPoolAddr + (slot * PageSize);
    Buffer.MemoryCopy(src, dst, PageSize, PageSize);

    // Enqueue for backup writer
    _backupQueue.Enqueue(new ShadowEntry
    {
        FilePageIndex = filePageIndex,
        ChangeRevision = ((PageBaseHeader*)src)->ChangeRevision,
        ShadowSlotIndex = slot
    });
}
```

### Data Structures

| Structure | Purpose | Size |
|-----------|---------|------|
| `_backupActive` | Volatile int flag (0/1) | 4 bytes |
| `_cowBitmap` | ConcurrentBitmapL3All — 1 bit per page | ~1.6 MB for 100 GB DB |
| `_backupDirtyBitmap` | Snapshot of dirty bitmap at backup start | Same |
| `_shadowPool` | Pre-allocated pinned byte[] ring buffer | Configurable (default 4 MB = 512 pages) |
| `_backupQueue` | ConcurrentQueue\<ShadowEntry\> | Lock-free MPSC |

### Performance Characteristics

| Scenario | Cost |
|----------|------|
| No backup active (99.9%+ of the time) | 1 volatile read (~1 CPU cycle) |
| Backup active, page NOT in dirty set (90%) | Volatile read + bitmap test (~3 ns) |
| Backup active, page in dirty set, already CoW'd | + second bitmap test (~4-5 ns) |
| Backup active, first write to dirty page | Bitmap set + 8KB memcpy + enqueue (~200-500 ns) |

The CoW storm is self-limiting: at 10% churn on a 100 GB database, CoW'ing all 1.28M dirty pages takes ~0.45 seconds, after which all subsequent writes take the cheap "already CoW'd" path.

> **Detailed spec:** See [Part 2 — Backup Creation](../design/pit-backup/02-backup-creation.md) §Scoped CoW Shadow Buffer

---

## 7.3 Seqlock Page Protection

### Purpose

Guarantee consistent page copies during checkpoint I/O using `ChangeRevision` as a seqlock counter, with **zero overhead** on the transaction write path.

### How It Works

Each page's `PageBaseHeader` already contains a `ChangeRevision` field that increments on every modification. The checkpoint thread uses this as a seqlock:

```csharp
// Checkpoint thread, per dirty page:
retry:
    var v1 = header->ChangeRevision;                          // read before
    Buffer.MemoryCopy(src, dst, PageSize, PageSize);          // ~0.3µs copy
    var v2 = header->ChangeRevision;                          // read after
    if (v1 != v2) goto retry;                                 // torn read — retry
    uint crc = Crc32C.Compute(dst, PageSize);                 // CRC on frozen copy
```

**Retry probability:** With a ~0.3µs copy window and transaction latency of ~50µs, the probability of a torn read is ~0.6%. When it occurs, the retry adds 0.3µs — negligible.

> **Detailed spec:** See [Part 2 — Backup Creation](../design/pit-backup/02-backup-creation.md) §Seqlock for Checkpoint CRC

---

## 7.4 Backup Manager

### Purpose

Coordinate the full backup lifecycle: force checkpoint, snapshot dirty bitmap, enable CoW, read changed pages, compress and write the `.pack` file, update the catalog.

<a href="../assets/typhon-pitbackup-creation-flow.svg"><img src="../assets/typhon-pitbackup-creation-flow.svg" width="1200" alt="PIT Backup — Creation Flow"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

### Backup Flow (12 Steps)

```
 1. Force Checkpoint       → flush all dirty pages to data file
 2. Snapshot Dirty Bitmap  → atomic read-then-clear; gives O(changed) page list
 3. Allocate Catalog Entry → assign BackupId, record DateTime/Epoch
 4. Allocate Shadow Pool   → pre-pin 4 MB ring buffer for CoW copies
 5. Set _backupActive = 1  → enables CoW interception in TryLatchPageExclusive
 6. Open .pack Writer      → create backup-NNNN.pack in backup directory
 7. Write Header           → 128 bytes: magic, version, BackupId, epoch, flags
 8. Read & Compress Pages  → for each dirty-bitmap bit:
                              if CoW'd → read from shadow; else → read from data file
                              compress with LZ4, write [size][data] frame
 9. Write Allocation Bitmap → optional; marks which pages exist in the database
10. Write Page Index       → N × 16B entries sorted by PageId (enables binary search)
11. Write Footer           → PageIndexOffset, PageCount, FooterMagic, FileCRC
12. Set _backupActive = 0  → disable CoW, drain queue, free shadow pool
```

### Concurrency During Backup

| Concern | Resolution |
|---------|------------|
| Two threads CoW same page | `_cowBitmap.TrySet` — atomic, only first wins |
| Checkpoint fires during backup | CoW captures pre-modification state before any write |
| Transaction commits during backup | CoW preserves snapshot-start version |
| Page loaded directly to Exclusive | CoW captures it — content matches on-disk |

### Error Handling

Failed backups leave the dirty bitmap intact (changes will be captured by the next backup attempt). The partially-written `.pack` file is deleted, and the catalog entry is not committed.

> **Detailed spec:** See [Part 2 — Backup Creation](../design/pit-backup/02-backup-creation.md)

---

## 7.5 Backup File Format

### .pack File Layout

<a href="../assets/typhon-pitbackup-file-format.svg"><img src="../assets/typhon-pitbackup-file-format.svg" width="600" alt="PIT Backup — .pack File Format"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

```
┌─────────────────────────────────────────────────────┐
│  Header (128 bytes)                                 │
│    Magic: "TYPHBACK" | Version | Flags              │
│    BackupId | CheckpointEpoch | DateTime            │
│    TotalAllocatedPages | PageCount | Compression    │
│    HeaderCRC: CRC32C                                │
├─────────────────────────────────────────────────────┤
│  Page Data (variable)                               │
│    [4B size][LZ4(page_0)] [4B size][LZ4(page_1)].. │
│    Each page compressed independently               │
│    Original: 8KB per page                           │
├─────────────────────────────────────────────────────┤
│  Allocation Bitmap (optional)                       │
│    1 bit per page — which pages exist in DB         │
│    Present in compacted/full backups                │
├─────────────────────────────────────────────────────┤
│  Page Index (N × 16 bytes)                          │
│    PackPageEntry per page, sorted by PageId:        │
│      PageId (4B) | ChangeRevision (4B)              │
│      DataOffset (4B) | CompressedSize (4B)          │
├─────────────────────────────────────────────────────┤
│  Footer (32 bytes)                                  │
│    PageIndexOffset | PageCount | AllocBitmapOffset  │
│    FooterMagic: "PACK" | FileCRC: CRC32C            │
└─────────────────────────────────────────────────────┘
```

### Catalog (catalog.dat)

The catalog is an append-only file that tracks all backup points in a chain:

```
Header (64B):  Magic "TYPHCATL" | FormatVersion | EntryCount | LastBackupId | HeaderCRC
Entries (128B each):  BackupId | Epoch | DateTime | TotalAllocatedPages | PageCount
                      FileSize | Flags | PreviousBackupId | ChainBaseId | EntryCRC
                      FileName (80B UTF-8, null-terminated)
```

**Crash safety:** If a crash occurs during append, the entry may be written but the header's EntryCount is stale. On recovery, the reader checks for a valid entry beyond EntryCount (via CRC verification) and repairs the header.

### Page Index

Each `PackPageEntry` (16 bytes) enables O(log N) binary search for any page within a `.pack` file:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct PackPageEntry
{
    public int PageId;           // File page index
    public int ChangeRevision;   // ChangeRevision at capture time
    public int DataOffset;       // Offset into page data section
    public int CompressedSize;   // 0 = stored uncompressed
}
```

### CRC Coverage

| CRC Field | Location | Covers |
|-----------|----------|--------|
| HeaderCRC | .pack header | Header bytes (quick rejection of corrupt files) |
| EntryCRC | Catalog entry | Per-entry integrity |
| FileCRC | .pack footer | Entire file (strongest check) |
| Page CRC | In PageBaseHeader | Individual page content |

> **Detailed spec:** See [Part 3 — File Format](../design/pit-backup/03-file-format.md)

---

## 7.6 Reconstruction & Restore

### Purpose

Rebuild a complete database from the backup chain by walking backward through `.pack` files, collecting the newest version of each page.

<a href="../assets/typhon-pitbackup-reconstruction.svg"><img src="../assets/typhon-pitbackup-reconstruction.svg" width="1200" alt="PIT Backup — Reconstruction Chain Walk"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

### Algorithm: First Hit Wins

The reconstruction engine walks the backup chain **backward** (newest first). For each `.pack` file, it loads the page index and inserts entries into a `page_map`. The first occurrence of each PageId wins (it's the newest version):

```
1. Resolve chain: catalog.dat → list of .pack files from target → base
2. Walk BACKWARD (newest to oldest):
   For each .pack file:
     Load page index
     For each PageId not already in page_map:
       page_map[PageId] = { SourceFile, DataOffset, CompressedSize }
3. Validate completeness: all allocated pages must be in page_map
4. Read + decompress pages, grouped by source file (sequential I/O)
5. Write pages to target database file at their PageId offsets
6. Verify CRC32C on each restored page
```

### Restore Time (100 GB DB, 3 GB/s NVMe)

| Chain Depth | Read (compressed) | Write (full DB) | Total |
|-------------|-------------------|-----------------|-------|
| 1 (compacted) | ~50 GB | 100 GB | ~50s |
| 6 (1 day) | ~65 GB | 100 GB | ~55s |
| 42 (1 week) | ~86 GB | 100 GB | ~62s |

The read cost grows sub-linearly because later backups increasingly overlap with earlier ones — by depth 42 with 10% churn, 98.76% of pages have been modified at least once, so the base contributes almost nothing.

### CLI Interface

```
typhon-backup restore  --source <backup-dir> --target <db-path> [--point <id|datetime>]
typhon-backup verify   --source <backup-dir> [--point <id|datetime>] [--strict]
```

> **Detailed spec:** See [Part 4 — Reconstruction & Restore](../design/pit-backup/04-reconstruction.md)

---

## 7.7 Compaction & Pruning

### Purpose

Consolidate the backup chain into a single file, bound chain depth, and manage storage via retention policies.

<a href="../assets/typhon-pitbackup-compaction.svg"><img src="../assets/typhon-pitbackup-compaction.svg" width="1200" alt="PIT Backup — Compaction Lifecycle"></a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

### Compaction

Compaction merges the full chain (base + all incrementals) into a single `.pack` file containing every page at its latest version. The result is equivalent to a fresh full backup:

```
Before: base-001 → incr-002 → incr-003 → ... → incr-042  (chain depth: 42)
After:  compact-042  (chain depth: 1)
```

**I/O cost:** Read all `.pack` files in chain (~sum of file sizes), write one compacted file (~DB size × compression ratio). For a 100 GB database: ~150 GB total I/O, completing in ~50 seconds at 3 GB/s.

**Frequency:** Weekly (every ~42 backups at 4-hour intervals). Runs in the background with no impact on the live database.

### Pruning

After compaction, all pre-compaction `.pack` files can be safely deleted. For more granular pruning (deleting individual incrementals mid-chain), the pruning tool handles **orphaned page promotion** — pages that only existed in the deleted file must be copied forward to the next file in the chain.

### Retention Policy

```csharp
public class BackupRetentionPolicy
{
    public int MaxCount { get; set; }          // Max backups to keep (0 = unlimited)
    public TimeSpan MaxAge { get; set; }       // Delete older than this (Zero = unlimited)
    public long MaxTotalSize { get; set; }     // Total backup dir size cap (0 = unlimited)
    public int CompactThreshold { get; set; }  // Compact after this many incrementals (default: 42)
    public int MinKeep { get; set; } = 3;      // Never delete below this count
}
```

### Configuration Examples

**Game server — 4-hour backups, 7 days history:**
```csharp
new BackupRetentionPolicy { MaxAge = TimeSpan.FromDays(7), CompactThreshold = 42, MinKeep = 5 }
```

**Simulation — daily backups, keep last 30:**
```csharp
new BackupRetentionPolicy { MaxCount = 30, CompactThreshold = 7 }
```

**Embedded — minimal storage:**
```csharp
new BackupRetentionPolicy { MaxCount = 5, MaxTotalSize = 500L * 1024 * 1024, MinKeep = 2 }
```

### CLI Interface

```
typhon-backup compact  --source <backup-dir> --target <backup-id>
typhon-backup prune    --source <backup-dir> --before <id|datetime>
typhon-backup list     --source <backup-dir> [--verbose]
```

> **Detailed spec:** See [Part 5 — Compaction & Pruning](../design/pit-backup/05-compaction-pruning.md)

---

## Compression

| Algorithm | Ratio | Speed | Use Case |
|-----------|-------|-------|----------|
| **LZ4** | ~2:1 | ~2 GB/s compress, ~4 GB/s decompress | Default — fast backup with acceptable size |
| **Zstd** | ~3:1 | ~500 MB/s compress, ~1.5 GB/s decompress | Archival — better ratio when speed is secondary |
| **None** | 1:1 | Max | Debugging, benchmarking |

Each page is compressed independently, enabling random-access decompression without cross-page dependencies. Incompressible pages (compressed output >= PageSize) are stored raw.

---

## I/O Cost Summary

### Per-Backup I/O (100 GB DB, 10% churn)

| Component | Size |
|-----------|------|
| Read changed pages from data file | 10 GB |
| Write compressed pages to .pack | 5 GB |
| Page index + catalog | Negligible |
| **Total per incremental backup** | **~15 GB** |

### Weekly I/O (42 backups + compaction)

| Operation | I/O |
|-----------|-----|
| 41 incremental backups | 41 × 15 GB = 615 GB |
| 1 initial full backup | 150 GB |
| 1 weekly compaction | ~150 GB |
| **Total** | **~915 GB** |

Compare with the reverse-delta approach: 42 × 205 GB = **~8,610 GB** — a **9.4x I/O reduction**.

---

## Code Locations (Planned)

| Component | Planned Location |
|-----------|------------------|
| Dirty Bitmap | `src/Typhon.Engine/Backup/BackupDirtyBitmap.cs` |
| CoW Shadow Buffer | `src/Typhon.Engine/Backup/CowShadowBuffer.cs` |
| Backup Manager | `src/Typhon.Engine/Backup/BackupManager.cs` |
| Pack Writer | `src/Typhon.Engine/Backup/PackWriter.cs` |
| Pack Reader | `src/Typhon.Engine/Backup/PackReader.cs` |
| Backup Catalog | `src/Typhon.Engine/Backup/BackupCatalog.cs` |
| Restore Manager | `src/Typhon.Engine/Backup/RestoreManager.cs` |
| Compaction Engine | `src/Typhon.Engine/Backup/CompactionEngine.cs` |
| Retention Manager | `src/Typhon.Engine/Backup/RetentionManager.cs` |
| Backup Store (interface) | `src/Typhon.Engine/Backup/IBackupStore.cs` |
| Local Backup Store | `src/Typhon.Engine/Backup/LocalBackupStore.cs` |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| **Delta direction** | Forward incremental | Backup I/O = O(changed pages); reverse deltas require O(DB size) every time |
| **Changed page detection** | Persistent dirty bitmap | O(changed pages) vs O(total pages) revision scan; ~1-2 ns cost per checkpoint flush |
| **Snapshot consistency** | Scoped CoW shadow buffer | Handles checkpoint race; scoped to dirty set only (~10% of DB) |
| **Chain management** | Periodic compaction | Bounds chain depth; simple "merge + delete old" |
| **WAL dependency** | None | Self-contained page snapshots; checkpoint forced before backup (ADR-014) |
| **CoW trigger** | TryLatchPageExclusive | Single convergence point for all write paths |
| **Shadow pool** | Pre-allocated pinned ring buffer | Lock-free allocation, no GC pressure |
| **Backup unit** | Full page (8KB) | Matches storage model; no sub-page XOR diffs needed |
| **Compression** | LZ4 per page (default) | Fast (~2 GB/s), ~50% ratio, random-access decompression |
| **File format** | Append-only .pack + catalog | Sequential writes, page index at end for O(log N) lookup |
| **Concurrent backups** | One at a time | Simplicity; at most 2 page versions (live + shadow) |
| **Pruning** | CLI tool | Acceptable complexity; runs offline/scheduled |
| **Retention** | Count + age + volume + compact threshold | Simple, predictable, frequency-independent |

---

## Open Questions

1. **Shadow pool sizing heuristic** — Fixed 4 MB (512 pages) default. Should it auto-scale based on write rate during backup?

2. **Backup scheduling** — Should the engine provide a built-in periodic scheduler, or leave this to the application layer?

3. **Encryption** — Should .pack files support encryption at rest? If so, which algorithm (AES-256-GCM)?

4. **Remote storage** — The `IBackupStore` abstraction from the original design is deferred. Local filesystem first; remote backends as external packages later.

---

## Related Documents

- **[PIT Backup Design Series](../design/pit-backup/README.md)** — 6-part deep design document with byte-level specs, I/O simulations, and implementation details
- [PitBackup.md](../research/PitBackup.md) — Research document that led to this design
- [06-durability.md](06-durability.md) — Checkpoint pipeline, WAL, page checksums, ChangeRevision
- [03-storage.md](03-storage.md) — PagedMMF, page cache, page layout
- [01-concurrency.md](01-concurrency.md) — AccessControlSmall, ConcurrentBitmapL3All
- [ADR-014](../adr/014-no-point-in-time-recovery.md) — No PITR, WAL recycled after checkpoint
- [ADR-028](../adr/028-cow-snapshot-backup.md) — Original CoW snapshot backup decision

# Part 5: Compaction & Pruning

> Managing backup chain growth through periodic consolidation and selective removal.

**Status:** Draft
**Date:** 2026-02-15
**Series:** [PIT Backup Design](./README.md) | Part 5 of 6
**Depends on:** [03 - File Format](./03-file-format.md), [04 - Reconstruction & Restore](./04-reconstruction.md)

<a href="../../assets/typhon-pitbackup-compaction.svg">
  <img src="../../assets/typhon-pitbackup-compaction.svg" width="1200"
       alt="PIT Backup — Compaction Lifecycle">
</a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

---

## Overview

Forward incremental backups accumulate `.pack` files over time. Without management, the chain grows indefinitely, increasing restore time, management complexity, and storage consumption. This document describes two mechanisms for controlling chain growth:

- **Compaction** merges an entire chain into a single self-contained `.pack` file, resetting the chain depth to 1.
- **Pruning** removes individual backup points from the chain, either by promoting orphaned pages to adjacent backups or by discarding history entirely.

Both operations run offline (or in a separate process) and have zero impact on the running database engine.

---

## Why Compaction

Forward incremental chains have a fundamental trade-off: each incremental backup is cheap to create (only changed pages), but restore cost grows with chain depth because the reconstruction engine must read page indices from every file in the chain and merge them.

Compaction resolves this by periodically collapsing the entire chain into a single file:

| Without Compaction | With Weekly Compaction |
|-------------------|----------------------|
| Chain grows by 1 file every 4 hours | Chain resets to depth 1 every week |
| After 1 month: 180+ files | After 1 month: max ~42 files between compactions |
| Restore reads indices from 180 files | Restore reads indices from at most 42 files |
| Storage: all historical pages retained | Storage: only current state + recent incrementals |

Compaction is conceptually equivalent to "reconstruct the database from the backup chain, then immediately create a new full backup from the result." The key difference is that compaction reads from backup files only — it never touches the live database, so there is zero impact on running transactions.

---

## Compaction Algorithm

```
Input:  target_backup_id   — the backup point to compact up to
        catalog            — catalog.dat contents
        backup_directory   — path containing .pack files

Output: single compacted .pack file replacing the chain

Algorithm:

1. BUILD PAGE MAP
   Use the same page map building algorithm from Part 4 (Reconstruction):
     - Resolve chain: [base, incr_1, ..., target]
     - Load page indices from each .pack file
     - Walk backward: page_map[PageId] = (file, offset, compressed_size)
     - Validate page completeness against allocation bitmap

2. CREATE COMPACTED PACK FILE
   Open a new file: backup-{target_backup_id}-compacted.pack

   a. Write Header (128 bytes):
      - Magic: "TYPHPACK"
      - Version: 1
      - BackupId: target_backup_id
      - Flags: 0x01 (Compacted flag set)
      - BackupEpoch: epoch from the target backup
      - Timestamp: current UTC time
      - TotalPages: total allocated pages
      - PreviousBackupId: 0 (compacted file has no predecessor)
      - Compression: LZ4

   b. Write ALL pages (not just changed pages):
      - Group page_map entries by source file (same I/O optimization as restore)
      - For each source file, read pages in DataOffset order
      - For each page:
        * Read compressed data from source .pack file
        * Decompress -> verify page CRC
        * Re-compress with LZ4 (or copy compressed if same algorithm)
        * Write to compacted file
        * Record (PageId, DataOffset, CompressedSize) for the page index

   c. Write allocation bitmap (as a special section before the page index)

   d. Write page index:
      - Sort all entries by PageId
      - Write N x 16 bytes (same format as incremental .pack files)

   e. Write Footer (32 bytes):
      - PageIndexOffset, PageCount, FileCRC

3. VERIFY COMPACTED FILE
   Re-read the compacted file:
     - Verify FileCRC
     - Spot-check page CRCs (all pages, or a random sample for speed)
     - Verify page index is sorted and complete

4. UPDATE CATALOG
   In catalog.dat:
     - Add entry for the compacted backup
     - Mark all old entries in the chain as superseded
     - Set chain_base = target_backup_id

5. DELETE OLD PACK FILES
   For each .pack file in the old chain (base through target):
     - Verify it is marked as superseded in the catalog
     - Delete the file
     - Remove the catalog entry (or mark as deleted)

6. FINALIZE CATALOG
   Write the updated catalog.dat with the new chain base.
   The compacted file is now indistinguishable from a "first backup" —
   subsequent incremental backups chain off it normally.
```

### Atomicity Guarantees

Compaction must be crash-safe. If the process is interrupted at any point, the backup chain must remain valid:

- The compacted file is written to a temporary name (`*.pack.tmp`) and renamed atomically after verification.
- Old `.pack` files are deleted only after the compacted file is verified and the catalog is updated.
- If the process crashes after creating the compacted file but before deleting old files, the next compaction (or a cleanup pass) will detect the duplicate and clean up.
- The catalog uses a write-ahead pattern: the new entry is written before old entries are removed.

### Re-Compression Optimization

When the source `.pack` files use the same compression algorithm as the compacted file (typically both LZ4), a shortcut is possible:

```
If source compression == target compression:
  Copy compressed bytes directly (no decompress/recompress)
  Still verify CRC by decompressing a copy, but write the original bytes
  Saves ~40% CPU time during compaction
```

This optimization is safe because LZ4 is deterministic for the same input, and we verify the page CRC regardless.

---

## Compaction I/O Cost

Compaction reads from backup files (not the live database) and writes a new backup file. The I/O cost depends on the total data in the chain and the final database size.

| DB Size | Chain Depth | Compaction Read | Compaction Write (LZ4) | Duration (3 GB/s) |
|---------|-------------|----------------|----------------------|-------------------|
| 1 GB | 42 | ~1.5 GB (chain) | ~0.5 GB | ~0.7s |
| 10 GB | 42 | ~13 GB (chain) | ~5 GB | ~6s |
| 100 GB | 42 | ~130 GB (chain) | ~50 GB | ~60s |
| 1 TB | 42 | ~1.3 TB (chain) | ~500 GB | ~10 min |

**Key properties:**
- Zero impact on the running database. Compaction reads and writes backup files only.
- Read cost is proportional to the sum of all chain files (including redundant page versions). With re-compression optimization, only the needed pages are decompressed.
- Write cost is proportional to the full database size (the compacted file contains all pages).
- For very large databases (1TB+), compaction can run during off-peak hours.

### Scheduling Compaction During Off-Peak

For databases where compaction takes minutes, schedule it during low-activity windows:

```csharp
// Example: compact weekly at 3 AM Sunday
var schedule = new CompactionSchedule
{
    Trigger = CompactionTrigger.Scheduled,
    DayOfWeek = DayOfWeek.Sunday,
    TimeOfDay = new TimeSpan(3, 0, 0),  // 3:00 AM
    MaxDuration = TimeSpan.FromMinutes(30)
};
```

---

## Pruning Individual Incrementals

Pruning removes a specific intermediate backup point from the chain without compacting the entire chain. This is useful for:
- Removing a backup taken during a known-bad state
- Reducing chain depth without the full I/O cost of compaction
- Freeing space from a single large incremental

### The Orphaned Page Problem

When removing an incremental backup, some pages in that file may be the ONLY version in the chain. These are "orphaned" pages that must be preserved somewhere:

```
Chain: [base-0001, incr-0002, incr-0003, incr-0004, incr-0005]

Prune incr-0003:

Page P was modified in incr-0003 but NOT in incr-0004 or incr-0005.
If we delete incr-0003, we lose the only record of P's state at that epoch.
Any restore to point 0004 or 0005 would be missing page P.

Solution: promote page P into the next incremental (incr-0004).
```

### Pruning Algorithm

```
Input:  prune_backup_id    — the backup point to remove
        catalog            — catalog.dat contents
        backup_directory   — path containing .pack files

Algorithm:

1. IDENTIFY CHAIN POSITION
   Locate prune_backup_id in the chain:
     chain = [base, ..., prev, PRUNE_TARGET, next, ..., latest]

   If prune_backup_id is the chain base:
     ERROR: cannot prune the chain base. Compact first to create a new base.

   If prune_backup_id is the latest:
     Simple delete — no orphaned pages possible (nothing depends on it).
     Delete the .pack file, remove from catalog, done.

2. LOAD PAGE INDICES
   Load page index of the prune target.
   Load page indices of ALL newer backups: [next, ..., latest]

3. IDENTIFY ORPHANED PAGES
   orphaned_pages = []

   For each page P in prune_target.page_index:
     found_newer = false
     for backup in [next, ..., latest]:
       if P.PageId in backup.page_index:
         found_newer = true
         break

     if NOT found_newer:
       orphaned_pages.Add(P)

4. PROMOTE ORPHANED PAGES
   If orphaned_pages is not empty:
     Append orphaned pages to the NEXT backup file (next):

     a. Open next.pack for append (or create a replacement file)
     b. For each orphaned page:
        - Read compressed data from prune_target.pack
        - Append to next.pack
        - Add entry to next's page index
     c. Rewrite next.pack's page index (must remain sorted by PageId)
     d. Rewrite next.pack's footer with updated CRC and page count

     Alternative (safer): create a new replacement file for next, copy all
     existing data plus orphaned pages, then atomically replace.

5. DELETE PRUNE TARGET
   Delete prune_target.pack
   Remove entry from catalog

6. UPDATE CATALOG
   Update the chain: prev's successor is now next.
   Write updated catalog.dat.
```

### Orphaned Page Count Analysis

The number of orphaned pages when pruning depends on the backup's position in the chain and the churn rate:

**Pruning the NEWEST incremental (latest in the chain):**
- Every page in this file is orphaned (no newer version exists anywhere).
- Orphaned pages = all pages in this backup = ~10% of total pages at 10% churn.
- But since this is the latest, there is no "next" to promote to. Simple delete suffices.
- Any subsequent restore can only reach the previous point.

**Pruning a MIDDLE incremental:**
- Most pages have been modified again in later backups, so they have newer versions.
- For 10% churn per interval and a middle position with N subsequent backups:
  - Probability a page has a newer version: 1 - (1 - 0.10)^N
  - For N=5: 1 - 0.9^5 = 41% of pages have newer versions
  - So 59% of pages are orphaned and need promotion
  - For N=20: 1 - 0.9^20 = 88% have newer versions, only 12% orphaned

**Pruning the OLDEST incremental (first after base):**
- Many pages may only exist here (especially with low churn after the base).
- Worst case: nearly all pages are orphaned and must be promoted.
- This is expensive and approaches the cost of compaction. Best practice: compact first.

### Pruning Cost Estimate

| Prune Position | DB Size (100GB) | Pages in Target | Orphaned Pages | Promotion I/O |
|----------------|-----------------|----------------|----------------|---------------|
| Latest | 100 GB | 1.28M | 0 (simple delete) | 0 |
| Middle (10 later) | 100 GB | 1.28M | ~450K (35%) | ~3.4 GB read + write |
| Middle (5 later) | 100 GB | 1.28M | ~750K (59%) | ~5.7 GB read + write |
| Oldest (40 later) | 100 GB | 1.28M | ~15K (1.2%) | ~0.1 GB read + write |
| Oldest (5 later) | 100 GB | 1.28M | ~750K (59%) | ~5.7 GB read + write |

---

## Retention Policy

A retention policy automates the decision of when to compact and which backup points to prune. The policy is evaluated after each backup completes.

### Configuration

```csharp
public class BackupRetentionPolicy
{
    /// <summary>
    /// Keep at most this many backup points. 0 = unlimited.
    /// When exceeded, oldest backups beyond the chain base are pruned.
    /// </summary>
    public int MaxCount { get; set; }

    /// <summary>
    /// Delete backups older than this duration. TimeSpan.Zero = unlimited.
    /// Age is measured from the backup's creation timestamp.
    /// </summary>
    public TimeSpan MaxAge { get; set; }

    /// <summary>
    /// Maximum total size of all backup files in bytes. 0 = unlimited.
    /// When exceeded, oldest backups are pruned until under budget.
    /// </summary>
    public long MaxTotalSize { get; set; }

    /// <summary>
    /// Auto-compact when chain depth exceeds this threshold. 0 = never auto-compact.
    /// Default is 42 (~1 week at 4-hour backup intervals).
    /// Compaction targets the midpoint of the chain, keeping the recent half
    /// as individual incrementals for granular restore.
    /// </summary>
    public int CompactThreshold { get; set; } = 42;

    /// <summary>
    /// Always keep at least this many backup points, regardless of other limits.
    /// Prevents a large backup from triggering deletion of all history.
    /// </summary>
    public int MinKeep { get; set; } = 1;
}
```

### Retention Enforcement Algorithm

```
After each backup completes:

1. CHECK CHAIN DEPTH
   if chain_depth >= CompactThreshold:
     midpoint = chain_base + (chain_depth / 2)
     Compact the chain up to midpoint.
     This produces:
       [compacted_base, incr_mid+1, incr_mid+2, ..., incr_latest]
     Chain depth is now halved.

     Why midpoint? Compacting to the latest would destroy granular restore
     points for recent backups. The midpoint balances chain depth reduction
     with preserving recent history.

2. COUNT AND AGE CHECK
   backups = list all backup points, sorted oldest-first

   for each backup (oldest first):
     skip if this is the chain base (never auto-delete the base)
     skip if remaining count <= MinKeep

     should_delete = false

     if MaxCount > 0 AND total_count > MaxCount:
       should_delete = true

     if MaxAge > TimeSpan.Zero AND backup.Timestamp + MaxAge < now:
       should_delete = true

     if should_delete:
       if a newer compacted base exists:
         Delete this backup's .pack file (safe — it's before the base)
       else:
         Cannot delete without compacting first.
         Trigger compaction to create a new base, then delete.

3. SIZE CHECK
   if MaxTotalSize > 0:
     total_size = sum of all .pack file sizes

     while total_size > MaxTotalSize AND backup_count > MinKeep:
       oldest = oldest backup after chain base
       if oldest exists AND a newer base exists after it:
         Delete oldest.pack
         total_size -= oldest.size
       else:
         break  // cannot free more space without compaction

4. NEVER DELETE THE CHAIN BASE WITHOUT A REPLACEMENT
   The chain base is the foundation. Deleting it without a newer base
   would make the entire chain unrestorable. The algorithm enforces:
     - Compact first, creating a new base
     - Then delete files before the new base
```

### Safety Invariants

The retention algorithm maintains these invariants at all times:

1. **Chain continuity:** There is always a valid chain from base to latest.
2. **Base exists:** The chain base `.pack` file always exists.
3. **MinKeep honored:** At least MinKeep backup points are always preserved.
4. **No orphaned pages:** Pages are never lost — pruning promotes them, compaction includes them.

---

## Scheduling Recommendations

Different deployment scenarios call for different backup and compaction frequencies:

| Scenario | DB Size | Incremental Interval | Compact Interval | Retain | Rationale |
|----------|---------|---------------------|-------------------|--------|-----------|
| Game server | 1-10 GB | Every 1 hour | Daily | 7 days | Frequent saves, fast compaction, bounded storage |
| Simulation engine | 10-100 GB | Every 4 hours | Weekly | 30 days | Balance between backup cost and restore granularity |
| Large simulation | 100 GB+ | Every 4 hours | Weekly (off-peak) | 14 days | Compaction during maintenance windows |
| Embedded / edge | < 1 GB | Every 15 minutes | Every 6 hours | 48 hours | Small data, frequent points, tight storage |
| Development / test | Any | Manual | Manual | 3 latest | No schedule needed, manual control |

### Example Configurations

**Game server (5 GB database):**
```csharp
var policy = new BackupRetentionPolicy
{
    MaxCount = 0,               // no count limit
    MaxAge = TimeSpan.FromDays(7),
    MaxTotalSize = 20L * 1024 * 1024 * 1024,  // 20 GB cap
    CompactThreshold = 24,      // compact daily (24 hourly backups)
    MinKeep = 5
};
// ~168 backup points per week, compacted daily
// Storage: ~5 GB base + ~24 x 0.25 GB incrementals = ~11 GB peak
```

**Simulation engine (50 GB database):**
```csharp
var policy = new BackupRetentionPolicy
{
    MaxCount = 0,
    MaxAge = TimeSpan.FromDays(30),
    MaxTotalSize = 200L * 1024 * 1024 * 1024,  // 200 GB cap
    CompactThreshold = 42,      // compact weekly (42 four-hour backups)
    MinKeep = 6                 // always keep 1 day of history
};
// ~180 backup points per month
// Storage: ~25 GB base + ~42 x 2.5 GB incrementals = ~130 GB peak
```

**Large simulation (200 GB database, off-peak compaction):**
```csharp
var policy = new BackupRetentionPolicy
{
    MaxCount = 0,
    MaxAge = TimeSpan.FromDays(14),
    MaxTotalSize = 500L * 1024 * 1024 * 1024,  // 500 GB cap
    CompactThreshold = 42,
    MinKeep = 6
};
// Compaction takes ~2 minutes, scheduled for 3 AM Sunday
// Storage: ~100 GB base + ~42 x 10 GB incrementals = ~520 GB peak
// MaxTotalSize triggers early pruning to stay under 500 GB
```

---

## CLI Interface

### Compact Command

```
typhon-backup compact --source /backup/typhon
```

Compacts the entire chain into a single base file. The target defaults to the latest backup point.

```
typhon-backup compact --source /backup/typhon --target 30
```

Compacts the chain up to backup point 30, preserving incrementals 31+ as a separate chain on top of the new base.

### Prune Command

```
typhon-backup prune --source /backup/typhon --before 10
```

Removes all backup points before ID 10. Requires that a compacted base exists at or after ID 10.

```
typhon-backup prune --source /backup/typhon --before "2024-01-10T00:00:00Z"
```

Removes all backup points with timestamps before the specified datetime.

```
typhon-backup prune --source /backup/typhon --keep-last 10
```

Removes all but the 10 most recent backup points. If the chain has more than 10 points, the oldest are removed (compaction is triggered if needed to maintain chain integrity).

### List Command

```
typhon-backup list --source /backup/typhon
```

Lists all backup points with summary information.

```
typhon-backup list --source /backup/typhon --verbose
```

Lists all backup points with detailed information including per-file page counts and sizes.

### List Output Example

```
Backup chain at /backup/typhon

ID    Type         Epoch  DateTime              Pages      Size       Chain
----  -----------  -----  --------------------  ---------  ---------  -----
0001  Compacted    10     2024-01-08 00:00:00   12,800,000  48.2 GB   base
0002  Incremental  60     2024-01-08 04:00:00   1,280,000   4.8 GB   +1
0003  Incremental  110    2024-01-08 08:00:00   1,150,000   4.3 GB   +2
0004  Incremental  160    2024-01-08 12:00:00   1,310,000   4.9 GB   +3
0005  Incremental  210    2024-01-08 16:00:00   1,290,000   4.8 GB   +4
0006  Incremental  260    2024-01-08 20:00:00   1,260,000   4.7 GB   +5
0007  Incremental  310    2024-01-09 00:00:00   1,180,000   4.4 GB   +6
0008  Incremental  360    2024-01-09 04:00:00   1,350,000   5.1 GB   +7
0009  Incremental  410    2024-01-09 08:00:00   1,220,000   4.6 GB   +8
0010  Incremental  460    2024-01-09 12:00:00   1,300,000   4.9 GB   +9
0011  Incremental  510    2024-01-09 16:00:00   1,270,000   4.8 GB   +10
0012  Incremental  560    2024-01-09 20:00:00   1,310,000   4.9 GB   +11

Total: 12 backups, chain depth 12, total size 100.5 GB
Database size: 100.0 GB (12,800,000 pages x 8 KB)
Oldest: 2024-01-08 00:00:00 (1 day ago)
Latest: 2024-01-09 20:00:00 (4 hours ago)
```

### Verbose List Output

With `--verbose`, each entry includes additional detail:

```
ID 0002 - Incremental
  File:       backup-0002.pack
  Epoch:      60
  Timestamp:  2024-01-08 04:00:00 UTC
  Pages:      1,280,000 (10.0% of database)
  Size:       4.8 GB (compressed), 10.0 GB (uncompressed)
  Ratio:      0.48 (LZ4)
  Chain:      +1 from base 0001
  File CRC:   0xA3B7C1D2 (valid)
```

---

## Implementation Notes

### Compaction vs. Full Backup

A compacted `.pack` file is structurally identical to a first-ever full backup. The only difference is the `Compacted` flag (0x01) in the header, which indicates this file was produced by merging a chain rather than scanning the live database. This flag is informational only — reconstruction and subsequent incremental backups treat compacted files the same as initial full backups.

### Concurrent Access During Compaction

Compaction reads from existing `.pack` files and writes a new one. It does not touch the live database or any files that the backup manager writes to during a concurrent incremental backup. However, two constraints apply:

1. **Do not compact while a backup is in progress.** The compacted file replaces the chain base, and a concurrent backup might be referencing old chain files. Use a file-based lock (`backup.lock`) to serialize.

2. **Do not delete old files while a restore is in progress.** If a restore operation is reading from a `.pack` file that compaction wants to delete, the delete must wait. On Linux/macOS, open file handles prevent data loss (unlink removes the directory entry but the file persists until closed). On Windows, the delete will fail if the file is open, which is the safe behavior.

### Code Location

| Component | Planned Path |
|-----------|-------------|
| Compaction Engine | `src/Typhon.Engine/Backup/CompactionEngine.cs` |
| Pruning Engine | `src/Typhon.Engine/Backup/PruningEngine.cs` |
| Retention Policy | `src/Typhon.Engine/Backup/BackupRetentionPolicy.cs` |
| Retention Manager | `src/Typhon.Engine/Backup/RetentionManager.cs` |
| CLI Compact Command | `tools/Typhon.BackupCli/CompactCommand.cs` |
| CLI Prune Command | `tools/Typhon.BackupCli/PruneCommand.cs` |
| CLI List Command | `tools/Typhon.BackupCli/ListCommand.cs` |

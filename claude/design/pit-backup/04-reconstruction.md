# Part 4: Reconstruction & Restore

> Rebuilding a complete database from a forward-incremental backup chain.

**Status:** Draft
**Date:** 2026-02-15
**Series:** [PIT Backup Design](./README.md) | Part 4 of 6
**Depends on:** [03 - File Format](./03-file-format.md)

<a href="../../assets/typhon-pitbackup-reconstruction.svg">
  <img src="../../assets/typhon-pitbackup-reconstruction.svg" width="1200"
       alt="PIT Backup — Reconstruction Chain Walk">
</a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

---

## Overview

Reconstruction is the process of rebuilding a complete database file from a chain of `.pack` files. The algorithm walks the chain backward from the target backup point, building a page map that identifies where each page's most recent version lives, then reads and decompresses those pages into the target database file.

The key insight is that backward walking naturally deduplicates: the first occurrence of each PageId (encountered from newest to oldest) is the most recent version. Once a PageId is seen, all older versions are ignored.

---

## Page Map Building Algorithm

The page map building algorithm is the core of reconstruction. It determines, for every page in the database, which `.pack` file contains the authoritative version of that page at the target backup point.

### Algorithm

```
Input:  target_backup_id   — the backup point to restore to
        catalog            — catalog.dat contents (all backup metadata)
        backup_directory   — path to the directory containing .pack files

Output: reconstructed database file at the target path

Algorithm:

1. RESOLVE CHAIN
   Read catalog.dat -> find the chain from chain_base to target_backup_id.
   The chain is an ordered list of backup points:

     chain = [base, incr_1, incr_2, ..., target]

   The base is always a compacted or initial full backup.
   Each subsequent entry is an incremental backup.
   If target_backup_id IS the chain base, the chain has depth 1.

2. LOAD PAGE INDICES
   For each .pack file in the chain:
     a. Read the Footer (last 32 bytes of the file)
     b. Extract PageIndexOffset and PageCount from the footer
     c. Seek to PageIndexOffset
     d. Read PageCount x 16 bytes -> page index entries
        Each entry: { PageId: uint32, DataOffset: uint48, CompressedSize: uint16 }

   This reads ONLY the index sections — not the compressed page data.
   Total index data is small: 16 bytes x changed_pages per file.

3. BUILD PAGE MAP (backward walk)
   page_map = {}  // PageId -> (backup_file, data_offset, compressed_size)

   for backup in reverse(chain):      // newest first
     for entry in backup.page_index:
       if entry.PageId NOT in page_map:
         page_map[entry.PageId] = (
           backup.file_path,
           entry.DataOffset,
           entry.CompressedSize
         )

   // First hit wins = most recent version of each page.
   // Once a PageId is in the map, older versions are skipped.

4. VALIDATE PAGE COMPLETENESS
   Read the allocation bitmap from the TARGET backup
   (stored as a special entry in the .pack file, or from the catalog).

   For every page marked as allocated in the bitmap:
     if PageId NOT in page_map:
       ERROR: missing page — the backup chain is incomplete or corrupt.

   For every PageId in page_map:
     if PageId NOT marked as allocated:
       WARNING: orphaned page in backup chain (page was deallocated).
       Remove from page_map — do not write deallocated pages.

5. READ AND WRITE PAGES
   Group page_map entries by source backup file (minimize file opens/seeks).

   For each source file:
     a. Open the .pack file for reading
     b. Sort entries by DataOffset (sequential read order)
     c. For each page mapped to this file:
        - Seek to DataOffset
        - Read CompressedSize bytes
        - LZ4 decompress -> 8KB page buffer
        - Verify CRC32C from PageBaseHeader matches computed CRC
        - Write 8KB to target database file at offset = PageId x 8192

6. FINAL VERIFICATION
   Read each page from the reconstructed database file.
   Verify CRC32C in every PageBaseHeader matches computed CRC.
   Compare total page count with TotalAllocatedPages from the catalog.
   Report any mismatches.
```

### Why Backward Walking Works Efficiently

The backward walk exploits a key property of the page indices: they are sorted by PageId within each `.pack` file. This means the merge across multiple indices resembles a merge-sort pass:

- Walk the newest backup's index. Every PageId encountered goes directly into the map (the map is empty, so there are no conflicts).
- Walk the next-newest backup's index. For each entry, a single hash lookup determines whether this PageId was already seen. If yes, skip. If no, insert.
- Continue until the base is reached.

Because page indices are sorted, the per-file scan is sequential in memory. The hash map provides O(1) lookups. Total time complexity is O(sum of all page index entries across the chain), which is much smaller than the total page count for typical chain depths.

For a 100GB database (12.8M pages) with 10% churn per backup and a chain of 42 incrementals:
- Total page index entries across all files: 42 x 1.28M = ~53.8M entries
- Each lookup/insert is O(1) in the hash map
- Total: ~53.8M hash operations, completing in tens of milliseconds

---

## Chain Depth Impact on Restore Time

Restore time depends on two factors: how many unique pages must be read (determined by chain depth and churn overlap), and the I/O throughput of the storage device.

### Analysis for a 100GB Database, 10% Churn per Backup (every 4h)

**Chain depth 1 (compacted base only):**
- All pages are in a single file, already compressed
- Read: 100GB x 0.5 (LZ4 ratio) = ~50GB of compressed data
- Write: 100GB uncompressed to target file
- At 3 GB/s sequential read: ~17s for reads, ~33s for writes
- Total: ~50s (I/O bound on write side)

**Chain depth 6 (1 day of incrementals):**
- Base file: 50GB compressed (all pages)
- 5 incremental files: 5 x 1.28M pages x 8KB x 0.5 = ~5 x 5GB = 25GB compressed
- But many pages overlap between incrementals. A page modified in backup 6 was likely also in backup 5.
- Unique pages from incrementals: roughly 40% of pages modified at least once in 5 intervals = 1 - 0.9^5 = 41% = 5.2M pages
- These 5.2M pages are read from incrementals instead of the base
- Net compressed reads: ~50GB (from base for the 59% untouched pages) + ~20GB (from incrementals) = ~70GB
- But we skip reading the base copies of those 5.2M pages, so actual reads are less
- Effective: ~65GB compressed reads + 100GB write = ~22s read + ~33s write = ~55s

**Chain depth 42 (1 week of incrementals):**
- Probability a page was never modified in 42 intervals: 0.9^42 = 0.0124 = 1.24%
- So 98.76% of pages have a version in some incremental
- Only 1.24% of pages (159K pages, ~1.2GB compressed) come from the base
- Remaining pages come from the most recent incremental that touched them
- The page_map building reads all 42 index files: 42 x 1.28M x 16B = 860MB (fast)
- Compressed page data reads: spread across 42 files, ~85GB total (with overlap savings)
- Write: 100GB uncompressed
- Total: ~29s read + ~33s write = ~62s

### Summary Table

| Chain Depth | Scenario | Compressed Reads | Index Reads | Write | Est. Total (3 GB/s) |
|-------------|----------|-----------------|-------------|-------|---------------------|
| 1 | Compacted base | ~50 GB | ~200 MB | 100 GB | ~50s |
| 6 | 1 day | ~65 GB | 120 MB | 100 GB | ~55s |
| 12 | 2 days | ~75 GB | 240 MB | 100 GB | ~58s |
| 42 | 1 week | ~85 GB | 860 MB | 100 GB | ~62s |

The sub-linear growth occurs because pages modified multiple times only need their latest version. At depth 42, ~99% of pages have been modified at least once, so incremental reads add diminishing amounts of new data.

**Index reads are negligible.** Even at chain depth 42 with 1.28M entries per file, total index data is ~860MB. At 3 GB/s, this takes ~290ms. The index read phase is always a tiny fraction of total restore time.

---

## Grouping I/O by Source File

After building the page_map, the naive approach would read pages in PageId order (sorted by target offset). This causes random seeks across multiple `.pack` files. A better strategy groups reads by source file:

### Optimized Read Strategy

```
1. After building page_map, create a dictionary:
     file_groups = {}  // file_path -> List<(PageId, DataOffset, CompressedSize)>

   for (pageId, (file, offset, size)) in page_map:
     file_groups[file].Add((pageId, offset, size))

2. For each file in file_groups:
     a. Sort entries by DataOffset (ascending)
        — this converts random reads into sequential reads within each file
     b. Open the .pack file
     c. Read entries in DataOffset order:
        - Seek to offset (often sequential or near-sequential)
        - Read compressed data
        - Decompress to 8KB page buffer
        - Write to target file at PageId x 8192

3. Close each .pack file after processing all its entries.
```

### Why This Matters

On spinning disks, random seeks cost ~5-10ms each. Reading 12.8M pages randomly from 42 files would take hours. Grouping by file and sorting by offset turns the pattern into 42 sequential passes, each reading data in order.

On NVMe drives, seek penalties are negligible (~0.01ms), so the optimization is less critical. However, sequential access still benefits from:
- OS read-ahead prefetching (the kernel detects sequential patterns and prefetches)
- Filesystem block allocation locality (pages written sequentially are stored sequentially)
- Reduced syscall overhead (larger sequential reads can be batched)

### Write-Side Optimization

The write side (target database file) receives pages in arbitrary order because different source files contribute different PageId ranges. Two approaches:

1. **Random writes (simple):** Write each decompressed page to `PageId x 8192` immediately. On NVMe, random 8KB writes are fast. On spinning disks, this causes seeks.

2. **Buffered sequential write (optimized):** Accumulate pages in memory (or a temp file), sort by PageId, then write sequentially. Requires extra memory or disk space but produces optimal write patterns.

For the default implementation, random writes are acceptable on modern storage. The buffered approach is a future optimization for spinning disk targets.

---

## Verification

Backup integrity verification operates at two levels, with distinct error handling for each.

### Level 1: Pack File Integrity

Before trusting any data from a `.pack` file, verify the file-level CRC:

```
1. Read the Footer (last 32 bytes)
2. Extract FileCRC (CRC32C of the entire file excluding the CRC field itself)
3. Compute CRC32C over the file content
4. Compare: if mismatch, the file is corrupt
   -> Abort if this is the only source for any required page
   -> Warn and continue if all pages from this file have alternatives in other files
```

File-level CRC catches:
- Truncated files (partial write during backup)
- Bit-rot on storage media
- Accidental file modification

### Level 2: Page-Level Integrity

After decompressing each page, verify the page's own CRC:

```
1. Decompress the compressed data -> 8KB page buffer
2. Read CRC32C from PageBaseHeader (first bytes of the page)
3. Compute CRC32C over the page content (excluding the CRC field)
4. Compare: if mismatch, the page is corrupt
```

Page-level CRC catches:
- Compression/decompression errors
- Memory corruption during backup creation
- Partial page writes in the original database

### Error Handling During Restore

When a page fails CRC verification during restore:

```
Severity levels:
  WARN:  Log the error, mark the page as potentially corrupt, continue restoring.
         The restored database will contain the corrupt page data.
         A post-restore report lists all corrupt pages.

  ERROR: If --strict mode is enabled, abort the restore immediately.
         No partial database is written (or the partial file is deleted).

Default behavior: WARN (best-effort restore).
Rationale: A database with 1 corrupt page out of 12.8M is more useful than no database.
```

### Post-Restore Full Verification

After the restore completes, an optional (default: enabled) full verification pass reads every page in the reconstructed database:

```
1. Open the reconstructed database file
2. For each page (0 to TotalAllocatedPages - 1):
   a. Read 8KB at offset PageId x 8192
   b. Read CRC32C from PageBaseHeader
   c. Compute CRC32C over the page content
   d. Compare: if mismatch, add to corrupt_pages list
3. Report:
   - Total pages verified: N
   - Corrupt pages: M (list of PageIds)
   - Verification time: Xs
   - Result: PASS / FAIL
```

### Standalone Verify Command

The `verify` command validates backup chain integrity without performing a restore:

```
1. Read catalog.dat -> validate structure and checksums
2. For each .pack file in the chain:
   a. Verify FileCRC
   b. Read page index -> verify it parses correctly
   c. For each page entry: read, decompress, verify page CRC
3. Cross-validate:
   a. Build page_map -> verify all allocated pages are covered
   b. Verify chain continuity (no gaps in backup IDs)
   c. Verify epoch monotonicity (each backup epoch >= previous)
4. Report summary
```

---

## CLI Interface

### Restore Command

```
typhon-backup restore --source /backup/typhon --target /data/typhon-restored.db
```

Restores the latest backup point to the target path.

```
typhon-backup restore --source /backup/typhon --target /data/typhon-restored.db --point 42
```

Restores to a specific backup ID. The backup ID is the integer identifier assigned sequentially to each backup point (visible in catalog and `list` output).

```
typhon-backup restore --source /backup/typhon --target /data/typhon-restored.db --point "2024-01-15T14:00:00Z"
```

Restores to a specific point in time. The tool finds the latest backup whose timestamp is less than or equal to the specified datetime. If no backup exists at or before that time, an error is reported.

### Restore Options

| Flag | Default | Description |
|------|---------|-------------|
| `--source` | (required) | Path to the backup directory containing `.pack` files and `catalog.dat` |
| `--target` | (required) | Path for the reconstructed database file |
| `--point` | latest | Backup ID (integer) or ISO 8601 datetime string |
| `--verify` | `true` | Run post-restore CRC verification pass |
| `--force` | `false` | Overwrite the target file if it already exists |
| `--strict` | `false` | Abort on any CRC failure instead of best-effort restore |
| `--buffer-size` | `64MB` | Read buffer size for decompression (larger = fewer syscalls) |
| `--parallel` | `1` | Number of parallel decompression threads (future optimization) |

### Verify Command

```
typhon-backup verify --source /backup/typhon
```

Verifies the entire backup chain (all `.pack` files and catalog integrity).

```
typhon-backup verify --source /backup/typhon --point 42
```

Verifies only the chain required to restore backup point 42 (the base plus all incrementals up to and including 42).

### Verify Output Example

```
Verifying backup chain at /backup/typhon ...

  catalog.dat:          OK (12 entries, CRC valid)
  backup-0001.pack:     OK (12,800,000 pages, 48.2 GB, CRC valid)
  backup-0002.pack:     OK (1,280,000 pages, 4.8 GB, CRC valid)
  backup-0003.pack:     OK (1,150,000 pages, 4.3 GB, CRC valid)
  ...
  backup-0012.pack:     OK (1,310,000 pages, 4.9 GB, CRC valid)

Chain validation:
  Chain base:           backup-0001 (compacted)
  Chain depth:          12
  Page coverage:        12,800,000 / 12,800,000 (100%)
  Epoch continuity:     OK (10 -> 670, monotonic)

Page-level verification:
  Total pages checked:  18,420,000 (across 12 files)
  CRC failures:         0

Result: PASS
Duration: 14.2s
```

### Restore Output Example

```
Restoring from /backup/typhon to /data/typhon-restored.db ...

  Target backup:        #12 (2024-01-08 12:00:00 UTC)
  Chain depth:          12
  Database size:        100.0 GB (12,800,000 pages)

  Phase 1: Building page map ...
    Loaded 12 page indices (18.4M entries) in 0.29s
    Unique pages: 12,800,000

  Phase 2: Reading and writing pages ...
    backup-0001.pack:   159,000 pages (1.2%)    [==                    ]
    backup-0002.pack:   1,180,000 pages (9.2%)  [====                  ]
    ...
    backup-0012.pack:   1,310,000 pages (10.2%) [====================  ]
    Total I/O: 85.3 GB read, 100.0 GB written

  Phase 3: Verification ...
    12,800,000 pages verified, 0 CRC failures

Restore complete in 62.4s
```

---

## Error Scenarios and Recovery

### Missing Pack File

If a `.pack` file in the chain is missing (deleted or corrupted beyond recognition):

```
Error: backup-0007.pack not found.
  This file is required for restoring backup point #12.
  The chain requires: [0001, 0002, ..., 0007, ..., 0012]

Options:
  1. Restore to backup point #6 (last point before the missing file)
  2. If backup-0007.pack can be recovered, place it in the backup directory and retry
```

### Corrupt Pack File (File CRC Failure)

```
Warning: backup-0007.pack failed file CRC verification.
  Expected: 0xA3B7C1D2  Computed: 0xF1E2D3C4

  This file contains 1,280,000 pages.
  Of these, 1,150,000 pages have newer versions in later backups.
  130,000 pages are ONLY available in this file.

  With --strict: abort (130,000 pages at risk)
  Without --strict: restore with potentially corrupt pages (best-effort)
```

### Incomplete Backup (Backup Was Interrupted)

A `.pack` file without a valid footer indicates an interrupted backup:

```
Warning: backup-0012.pack has no valid footer (likely interrupted during creation).
  Ignoring this file. Latest restorable point: #11
```

The catalog tracks backup completion status. An entry without the `Completed` flag set is treated as non-existent during reconstruction.

---

## Implementation Notes

### Memory Requirements

The page map is the primary memory consumer during reconstruction:

| Database Size | Total Pages | Page Map Size (12 bytes/entry) | Index Data (all files) |
|---------------|-------------|-------------------------------|----------------------|
| 1 GB | 128,000 | 1.5 MB | ~2 MB (chain of 42) |
| 10 GB | 1,280,000 | 14.6 MB | ~20 MB |
| 100 GB | 12,800,000 | 146 MB | ~200 MB |
| 1 TB | 128,000,000 | 1.46 GB | ~2 GB |

Each page_map entry stores: file index (2 bytes) + data offset (6 bytes) + compressed size (4 bytes) = 12 bytes per page.

For databases up to 100GB, the page map fits comfortably in memory. For 1TB+ databases, a memory-mapped temporary file could be used, though 1.5GB of RAM is not unreasonable for a restore operation.

### Streaming Decompression

LZ4 decompression is performed per-page (each page is independently compressed). This allows:
- No dependency between pages during decompression
- Future parallelism: multiple threads can decompress different pages simultaneously
- Bounded memory: only one 8KB decompression buffer needed per thread

### Code Location

| Component | Planned Path |
|-----------|-------------|
| Reconstruction Engine | `src/Typhon.Engine/Backup/ReconstructionEngine.cs` |
| Page Map Builder | `src/Typhon.Engine/Backup/PageMapBuilder.cs` |
| Pack File Reader | `src/Typhon.Engine/Backup/PackFileReader.cs` |
| CLI Restore Command | `tools/Typhon.BackupCli/RestoreCommand.cs` |
| CLI Verify Command | `tools/Typhon.BackupCli/VerifyCommand.cs` |

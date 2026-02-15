# Part 6: Analysis & Simulations

> Quantitative modeling of I/O costs, memory overhead, hot-path impact, and comparison with the reverse-delta design.

**Status:** Draft
**Date:** 2026-02-15
**Series:** [PIT Backup Design](./README.md) | Part 6 of 6
**Depends on:** All previous parts

---

## Overview

This document provides the quantitative foundation for the forward-incremental backup design. It models I/O costs at three database sizes (1 GB, 10 GB, 100 GB), analyzes memory overhead during backup and restore operations, measures hot-path impact on transaction latency, and presents a detailed comparison with the reverse-delta approach described in [07-backup.md](../../overview/07-backup.md).

All simulations use these baseline parameters unless stated otherwise:
- Page size: 8 KB (8,192 bytes)
- Churn rate: 10% of pages changed per backup interval
- Backup interval: 4 hours (6 backups/day, 42 backups/week)
- LZ4 compression ratio: 0.5 (compressed size = 50% of uncompressed)
- Storage throughput: 3 GB/s sequential read/write (NVMe class)

---

## I/O Cost Model

### Formulas

```
Constants:
  PAGE_SIZE           = 8192 bytes (8 KB)
  COMPRESSION_RATIO   = 0.5 (LZ4 typical for database pages)

Derived:
  DB_SIZE             = total_pages x PAGE_SIZE
  CHURN_RATE          = fraction of pages changed per backup interval (e.g., 0.10)
  CHANGED_PAGES       = total_pages x CHURN_RATE
```

**Incremental Backup I/O:**
```
  Read from data file:     CHANGED_PAGES x PAGE_SIZE
  Write to .pack file:     CHANGED_PAGES x PAGE_SIZE x COMPRESSION_RATIO
  Page index write:        CHANGED_PAGES x 16 bytes (negligible)

  Total I/O per backup:    CHANGED_PAGES x PAGE_SIZE x (1 + COMPRESSION_RATIO)
```

**Compaction I/O:**
```
  Read from chain:         Sum of all .pack file sizes in the chain
                           (includes redundant page versions across incrementals)
  Write compacted file:    total_pages x PAGE_SIZE x COMPRESSION_RATIO

  With re-compression optimization (same codec):
    Read cost reduced by ~40% (skip decompress/recompress, just copy bytes)
    Still must decompress for CRC verification

  Total I/O:               ~DB_SIZE x (1 + chain_overhead) + DB_SIZE x COMPRESSION_RATIO
  Where chain_overhead accounts for redundant reads from overlapping incrementals.
```

**Restoration I/O:**
```
  Read from .pack files:   Sum of unique pages across chain x PAGE_SIZE x COMPRESSION_RATIO
                           + page indices (16 bytes x total_entries, small)
  Write to database:       DB_SIZE (uncompressed, all pages)

  Total I/O:               ~DB_SIZE + DB_SIZE x COMPRESSION_RATIO x chain_read_factor

  Where chain_read_factor accounts for reading some pages from multiple candidates
  (the page map selects the newest, but we still read all indices to build the map).
```

**First Backup (full) I/O:**
```
  Read from data file:     DB_SIZE (all pages)
  Write to .pack file:     DB_SIZE x COMPRESSION_RATIO

  Total I/O:               DB_SIZE x (1 + COMPRESSION_RATIO)
```

---

## Simulation: 1 GB Database

**Parameters:**
- Database size: 1 GB = 128,000 pages
- Churn rate: 10% per 4-hour interval = 12,800 pages changed per backup
- LZ4 compression ratio: 0.5

### Week-Long Simulation (42 backups + 1 compaction)

| Operation | Count | Read | Write | Total I/O |
|-----------|-------|------|-------|-----------|
| Backup 1 (initial full) | 1 | 1.0 GB | 0.5 GB | 1.5 GB |
| Backups 2-42 (incremental) | 41 | 100 MB each | 50 MB each | 150 MB each |
| Weekly compaction | 1 | ~7 GB (chain) | 0.5 GB | ~7.5 GB |
| **Total weekly** | | | | **~15.15 GB** |

Breakdown:
- Initial full: 1.5 GB
- 41 incrementals: 41 x 0.15 GB = 6.15 GB
- Compaction: ~7.5 GB (reads the full chain, writes a new base)
- **Grand total: ~15.15 GB per week**

### Comparison with Reverse Delta (07-backup.md)

Under the reverse-delta design, each backup:
1. Reads the entire database: 1.0 GB
2. Writes a new full snapshot: 0.5 GB (compressed)
3. Reads the previous full snapshot: 0.5 GB
4. Computes and writes the reverse delta: ~0.05 GB (10% of pages, XOR compressed)
5. Total per backup: ~2.05 GB

Weekly reverse-delta total: 42 x 2.05 GB = **~86.1 GB**

**Forward incremental saves 5.7x I/O** (15.15 GB vs 86.1 GB).

### Storage Footprint

| Time | Forward Incremental | Reverse Delta |
|------|-------------------|---------------|
| After backup 1 | 0.5 GB (base) | 0.5 GB (full) |
| After backup 7 (day 1) | 0.5 + 6 x 0.05 = 0.8 GB | 0.5 + 6 x 0.05 = 0.8 GB |
| After backup 42 (week 1) | 0.5 + 41 x 0.05 = 2.55 GB | 0.5 + 41 x 0.05 = 2.55 GB |
| After compaction | 0.5 GB (reset) | N/A (no compaction) |

Storage footprint is similar between the two designs. The I/O difference is what matters.

---

## Simulation: 10 GB Database

**Parameters:**
- Database size: 10 GB = 1,280,000 pages
- Churn rate: 10% per 4-hour interval = 128,000 pages changed per backup
- LZ4 compression ratio: 0.5

### Week-Long Simulation

| Operation | Count | Read | Write | Total I/O |
|-----------|-------|------|-------|-----------|
| Backup 1 (initial full) | 1 | 10.0 GB | 5.0 GB | 15.0 GB |
| Backups 2-42 (incremental) | 41 | 1.0 GB each | 0.5 GB each | 1.5 GB each |
| Weekly compaction | 1 | ~70 GB (chain) | 5.0 GB | ~75.0 GB |
| **Total weekly** | | | | **~151.5 GB** |

Breakdown:
- Initial full: 15.0 GB
- 41 incrementals: 41 x 1.5 GB = 61.5 GB
- Compaction: ~75.0 GB
- **Grand total: ~151.5 GB per week**

Note: the compaction read of ~70 GB is the sum of all `.pack` files. With 10% churn and 42 intervals, many pages appear in multiple incrementals. The total chain data is approximately: 5 GB (base) + 41 x 0.5 GB (incrementals) = ~25.5 GB on disk, but reading and processing all pages from these files costs ~70 GB of effective I/O when accounting for decompression and re-compression.

### Comparison with Reverse Delta

Each reverse-delta backup:
1. Read entire database: 10.0 GB
2. Write new full snapshot: 5.0 GB
3. Read previous full snapshot: 5.0 GB
4. Write reverse delta: ~0.5 GB
5. Total per backup: ~20.5 GB

Weekly reverse-delta total: 42 x 20.5 GB = **~861 GB**

**Forward incremental saves 5.7x I/O** (151.5 GB vs 861 GB).

### Restore Time at Various Chain Depths

| Chain Depth | Compressed Reads | Index Reads | Write (uncompressed) | Total Time (3 GB/s) |
|-------------|-----------------|-------------|---------------------|---------------------|
| 1 (compacted) | 5.0 GB | 20 MB | 10 GB | ~5.0s |
| 6 (1 day) | ~6.5 GB | 12 MB | 10 GB | ~5.5s |
| 12 (2 days) | ~7.5 GB | 24 MB | 10 GB | ~5.8s |
| 42 (1 week) | ~8.5 GB | 86 MB | 10 GB | ~6.2s |

For a 10 GB database, restore time differences across chain depths are small (seconds). The 42-deep chain adds only ~1.2 seconds compared to a compacted base.

---

## Simulation: 100 GB Database

**Parameters:**
- Database size: 100 GB = 12,800,000 pages
- Churn rate: 10% per 4-hour interval = 1,280,000 pages changed per backup
- LZ4 compression ratio: 0.5

### Week-Long Simulation

| Operation | Count | Read | Write | Total I/O |
|-----------|-------|------|-------|-----------|
| Backup 1 (initial full) | 1 | 100 GB | 50 GB | 150 GB |
| Backups 2-42 (incremental) | 41 | 10 GB each | 5 GB each | 15 GB each |
| Weekly compaction | 1 | ~700 GB (chain) | 50 GB | ~750 GB |
| **Total weekly** | | | | **~1,515 GB (~1.48 TB)** |

Breakdown:
- Initial full: 150 GB
- 41 incrementals: 41 x 15 GB = 615 GB
- Compaction: ~750 GB
- **Grand total: ~1,515 GB (~1.48 TB) per week**

### Comparison with Reverse Delta

Each reverse-delta backup:
1. Read entire database: 100 GB
2. Write new full snapshot: 50 GB
3. Read previous full snapshot: 50 GB
4. Write reverse delta: ~5 GB
5. Total per backup: ~205 GB

Weekly reverse-delta total: 42 x 205 GB = **~8,610 GB (~8.4 TB)**

**Forward incremental saves 5.7x I/O** (1.48 TB vs 8.4 TB).

### Restore Time at Various Chain Depths

For a 100 GB database, the relationship between chain depth and restore time is more interesting because the absolute numbers are larger:

| Chain Depth | Unique Page Reads (compressed) | Index Reads | Total Read | Write | Total Time (3 GB/s) |
|-------------|-------------------------------|-------------|------------|-------|---------------------|
| 1 (compacted) | 50 GB | 200 MB | ~50 GB | 100 GB | ~50s |
| 6 (1 day) | ~65 GB | 120 MB | ~65 GB | 100 GB | ~55s |
| 12 (2 days) | ~75 GB | 240 MB | ~75 GB | 100 GB | ~58s |
| 42 (1 week) | ~85 GB | 860 MB | ~86 GB | 100 GB | ~62s |

**Why does the read cost increase sub-linearly?**

Many pages are modified multiple times across the 42 backup intervals. The page_map deduplicates: only the most recent version of each page is read. At chain depth N with churn rate C:

- Probability a page was NEVER modified: (1 - C)^N
- At depth 42, 10% churn: (0.9)^42 = 0.0124 = 1.24%
- So 98.76% of pages have at least one version in an incremental

The base file contributes only the 1.24% of pages that were never touched. All other pages come from incrementals, and the reconstruction engine reads each unique page exactly once (from the newest incremental containing it).

The incremental reads add diminishing amounts of new data because later backups increasingly overlap with earlier ones. By depth 42, nearly every page has been modified, so each new incremental mostly overwrites entries already in the page_map rather than adding new ones.

---

## Chain Depth vs. Restore Time (Detailed)

This section provides a more detailed model of how restore time scales with chain depth.

### Mathematical Model

For a database with `P` total pages, churn rate `C`, and chain depth `D`:

```
Pages from base = P x (1 - C)^D
Pages from incrementals = P x (1 - (1 - C)^D)

Compressed read volume:
  From base:         P x (1 - C)^D x PAGE_SIZE x COMPRESSION_RATIO
  From incrementals: P x (1 - (1 - C)^D) x PAGE_SIZE x COMPRESSION_RATIO
  Index data:        D x P x C x 16 bytes

Total compressed reads = P x PAGE_SIZE x COMPRESSION_RATIO + D x P x C x 16
(Note: total unique pages is always P, so total compressed reads for pages
 is constant at P x PAGE_SIZE x COMPRESSION_RATIO regardless of chain depth.
 The overhead comes from reading indices and seeking across multiple files.)

Write volume: P x PAGE_SIZE (always the full database, uncompressed)
```

The additional cost at higher chain depths comes from:
1. **Index overhead:** Reading page indices from D files instead of 1
2. **File opening overhead:** Opening and seeking within D files
3. **Fragmented reads:** Pages scattered across multiple files (though grouped by file for sequential access)

### Empirical Estimates (100 GB database)

| Chain Depth | Index Read Time | Page Read Time | Write Time | File Open Overhead | Total |
|-------------|----------------|----------------|------------|-------------------|-------|
| 1 | 0.07s | 16.7s | 33.3s | 0.01s | ~50s |
| 6 | 0.04s | 21.7s | 33.3s | 0.03s | ~55s |
| 12 | 0.08s | 25.0s | 33.3s | 0.05s | ~58s |
| 42 | 0.29s | 28.3s | 33.3s | 0.10s | ~62s |

The write side is constant (always 100 GB). The read side grows because pages must be read from different files across the chain, reducing the benefit of sequential read patterns within any single file.

---

## Memory Overhead Analysis

### Persistent Overhead (Always Present)

| Component | Formula | 1 GB DB | 10 GB DB | 100 GB DB | 1 TB DB |
|-----------|---------|---------|----------|-----------|---------|
| Dirty bitmap (in-memory) | ceil(total_pages / 8) | 16 KB | 160 KB | 1.6 MB | 16 MB |
| Dirty bitmap (persisted) | Same (written at checkpoint) | 16 KB | 160 KB | 1.6 MB | 16 MB |

The dirty bitmap is the only persistent memory cost. At 1 bit per page, it is negligible even for terabyte databases.

### Backup Window Overhead (During Active Backup Only)

| Component | Formula | 1 GB DB | 10 GB DB | 100 GB DB | 1 TB DB |
|-----------|---------|---------|----------|-----------|---------|
| CoW bitmap (_cowBitmap) | ceil(total_pages / 8) | 16 KB | 160 KB | 1.6 MB | 16 MB |
| Shadow pool | Configurable, default 4 MB | 4 MB | 4 MB | 4-16 MB | 16 MB |
| Page index (in-memory) | 16 bytes x changed_pages | 200 KB | 2 MB | 20 MB | 200 MB |
| Compression buffer | PAGE_SIZE (reused) | 8 KB | 8 KB | 8 KB | 8 KB |
| **Total during backup** | | **~4.2 MB** | **~6.3 MB** | **~25-40 MB** | **~232 MB** |

### Restore Overhead (During Restore Only)

| Component | Formula | 1 GB DB | 10 GB DB | 100 GB DB | 1 TB DB |
|-----------|---------|---------|----------|-----------|---------|
| Page map | 12 bytes x total_pages | 1.5 MB | 14.6 MB | 146 MB | 1.46 GB |
| Page indices (loaded) | 16 bytes x sum(changed_pages per file) | ~0.8 MB | ~8 MB | ~80 MB | ~800 MB |
| Decompression buffer | PAGE_SIZE per thread | 8 KB | 8 KB | 8 KB | 8 KB |
| **Total during restore** | | **~2.3 MB** | **~22.6 MB** | **~226 MB** | **~2.26 GB** |

### Key Observations

- **During normal operation (no backup active):** The dirty bitmap is the only overhead. At 1.6 MB for a 100 GB database, this is negligible.
- **During backup:** The shadow pool dominates at 4-16 MB. The CoW bitmap and page index add minimal overhead.
- **During restore:** The page map dominates. For databases up to 100 GB, it fits easily in memory. For 1 TB+, ~2.3 GB of RAM is required, which is reasonable for a restore operation.
- **The shadow pool is configurable.** The default 4 MB (512 pages) handles typical write rates. Under extreme write pressure during backup, the pool can be increased to 16 MB or more.

---

## Hot Path Impact Analysis

The critical question: how much does the backup system slow down normal transaction processing?

### No Backup Active (99.9%+ of the time)

**Zero overhead.** No conditional checks, no bitmap tests, no function calls.

The dirty bitmap OR operation happens in the checkpoint pipeline, not the transaction hot path. Checkpoint flushes already iterate over dirty pages and write them to disk. The added `OR bit into backup_dirty_bitmap` costs ~1-2 nanoseconds per page flush and runs on the checkpoint thread, not the transaction thread.

Transactions see zero additional cost. The `_backupActive` volatile read in `TryLatchPageExclusive` is the only addition to the hot path, and it is a single load instruction that hits L1 cache (the field is rarely modified and stays cached).

### During Backup (~3-10 Seconds Every ~4 Hours)

When a backup is active, every `TryLatchPageExclusive` call performs additional checks:

```
Path 1: Page NOT in dirty set (90% of pages at 10% churn)
  Cost: 1 volatile read (_backupActive)
      + 1 bitmap test (_backupDirtyBitmap.IsSet)
      = ~5-10 CPU cycles
      = ~2-3 nanoseconds at 3 GHz

Path 2: Page IN dirty set, already CoW'd (grows during backup window)
  Cost: 1 volatile read
      + 1 bitmap test (dirty → true)
      + 1 bitmap test (_cowBitmap → already set)
      = ~10-15 CPU cycles
      = ~3-5 nanoseconds

Path 3: Page IN dirty set, first write (needs CoW copy)
  Cost: 1 volatile read
      + 1 bitmap test (dirty → true)
      + 1 atomic test-and-set (_cowBitmap.TrySet)
      + 8 KB memcpy (page → shadow slot)
      + 1 queue enqueue
      = ~200-500 nanoseconds
```

### Workload Analysis

Consider a workload performing 1,000,000 page acquisitions per second during a backup window:

```
Distribution (10% churn rate):
  Path 1 (not in dirty set):              900,000 ops  x  ~3 ns  =  2.7 ms
  Path 2 (dirty, already CoW'd):           90,000 ops  x  ~4 ns  =  0.36 ms
  Path 3 (dirty, first CoW):               10,000 ops  x  350 ns =  3.5 ms
  Total added latency per second:                                    6.56 ms

Average per operation: 6.56 ms / 1,000,000 = ~6.6 nanoseconds
```

At 6.6 nanoseconds average overhead per page acquisition during the backup window, this is well within the noise of normal operation. For comparison:
- A single L3 cache miss: ~10-40 ns
- A single mutex acquisition (uncontended): ~20-50 ns
- A single page latch acquisition: ~50-200 ns

The backup overhead is smaller than a single cache miss.

### Worst-Case Scenario

The worst case is a workload that exclusively writes to pages in the dirty set during the backup window, triggering maximum CoW copies:

```
100% of operations hit Path 3 (first CoW):
  1,000,000 ops x 350 ns = 350 ms of added latency per second

This represents a 35% throughput reduction.
```

However, this scenario is unrealistic because:
1. Once a page is CoW'd, subsequent writes to it follow Path 2 (cheap)
2. At 10% churn (1.28M dirty pages for 100 GB), CoW'ing all of them takes: 1.28M x 350 ns = 0.45 seconds
3. After 0.45 seconds, all dirty pages are CoW'd, and the remaining backup window (~3-9 seconds) runs at Path 2 speeds

The CoW storm is self-limiting: it lasts at most a fraction of a second.

---

## Comparison with 07-backup.md (Reverse Deltas)

### Full Comparison Table

| Metric | Forward Incremental (this design) | Reverse Delta (07-backup) |
|--------|----------------------------------|--------------------------|
| **Backup I/O (100 GB, 10% churn)** | ~15 GB per backup | ~205 GB per backup |
| **Weekly I/O (42 backups + compact)** | ~1.48 TB | ~8.4 TB |
| **I/O ratio** | **1x** | **5.7x** |
| **Restore latest** | ~50-86 GB (chain depth dependent) | ~50 GB (single file read) |
| **Restore older point** | Same algorithm, subset of chain | Apply reverse deltas backward |
| **Restore latest speed** | ~50-62s (100 GB, 3 GB/s) | ~50s (100 GB, 3 GB/s) |
| **Pruning (delete oldest)** | Compact first, then delete chain | Delete delta file directly |
| **Pruning complexity** | Medium (orphan promotion needed) | Simple (tail removal) |
| **Hot path cost (no backup)** | Zero | 1 volatile read |
| **Hot path cost (during backup)** | ~6.6 ns avg (scoped CoW) | ~similar (full CoW) |
| **Changed page detection** | O(changed pages) via dirty bitmap | O(total pages) via revision scan |
| **Detection time (100 GB)** | ~1 ms (scan 1.6 MB bitmap) | ~100 ms (scan 12.8M revisions) |
| **Shadow pool scope** | Dirty pages only (~10% of DB) | All pages (100% of DB) |
| **Shadow pool pressure** | Low | Higher |
| **Backup file count** | Multiple (chain) | 1 full + N deltas |
| **Self-contained** | Yes | Yes |
| **WAL dependency** | None | None |
| **Implementation complexity** | Medium (chain management) | Medium-High (reverse delta computation) |
| **Compaction needed** | Yes (periodic) | No |
| **Maximum backup duration** | O(changed pages) | O(total pages) |
| **Backup duration (100 GB, 10% churn)** | ~5s | ~68s |

### I/O Breakdown Per Backup

**Forward Incremental:**
```
Read changed pages from data file:   10 GB  (10% of 100 GB)
Write compressed pages to .pack:      5 GB  (10 GB x 0.5 compression)
                                     -----
Total:                                15 GB
```

**Reverse Delta:**
```
Read ALL pages from data file:       100 GB  (full scan)
Write new full snapshot:              50 GB  (100 GB x 0.5 compression)
Read previous full snapshot:          50 GB  (to compute XOR deltas)
Write reverse delta:                   5 GB  (10% of pages, XOR + compress)
                                     -----
Total:                               205 GB
```

The 14x I/O difference per backup stems from the fundamental design choice: forward incrementals read only changed pages, while reverse deltas must read the entire database every time to produce a new full snapshot.

---

## When Reverse Deltas Would Be Better

The forward-incremental design is not universally superior. Reverse deltas win in specific scenarios:

### 1. Very Small Databases (< 1 GB)

When the database is small, backup I/O is trivial regardless of approach. A 1 GB database takes ~0.3 seconds for a full read at 3 GB/s. The 5.7x I/O savings of forward incrementals translates to saving ~1.7 seconds per backup — hardly worth the added chain management complexity.

**Threshold:** Below ~1 GB, the simpler reverse-delta model is preferable unless backup frequency is very high (every few minutes).

### 2. Extreme "Restore Latest" Speed Requirements

Forward incrementals require reading from multiple `.pack` files (chain depth dependent) to restore the latest state. At chain depth 42, the 100 GB restore takes ~62 seconds vs ~50 seconds for reverse deltas.

If those 12 seconds matter (e.g., a service with strict RTO requirements), the single-file read of the reverse-delta full snapshot is faster. However, compacting the forward chain to depth 1 achieves the same ~50 second restore, so this advantage only applies between compactions.

**Mitigation:** More frequent compaction (e.g., daily instead of weekly) keeps chain depth low and restore times close to the reverse-delta baseline.

### 3. No Maintenance Windows for Compaction

Forward incrementals require periodic compaction. For fully autonomous embedded systems with no maintenance windows and no scheduler infrastructure, the inability to compact means the chain grows indefinitely.

Reverse deltas have no compaction concept — each backup is self-maintaining. The "latest is always full" property means no chain management is ever needed.

**Mitigation:** In practice, compaction can run in the background with minimal resource impact. A simple timer-based scheduler (compact every N backups) requires no external infrastructure.

### 4. High-Churn Databases (> 50% Pages Change Per Interval)

When more than 50% of pages change between backups, the incremental advantage shrinks:

| Churn Rate | Forward Incr. I/O | Reverse Delta I/O | Ratio |
|------------|-------------------|-------------------|-------|
| 10% | 15 GB | 205 GB | 13.7x |
| 25% | 37.5 GB | 205 GB | 5.5x |
| 50% | 75 GB | 205 GB | 2.7x |
| 75% | 112.5 GB | 205 GB | 1.8x |
| 100% | 150 GB | 205 GB | 1.4x |

At 75%+ churn, the forward-incremental advantage narrows to less than 2x, and the added chain management complexity may not be worth the modest I/O savings.

**Observation:** Databases with > 50% churn per 4-hour interval are unusual. This would mean the majority of the database is rewritten every few hours, which suggests the backup interval should be lengthened or the data model reconsidered.

### 5. Simplicity as the Primary Constraint

The reverse-delta design is conceptually simpler:
- No chain management
- No compaction scheduling
- No orphan promotion during pruning
- Single file for "latest" state

For teams that prioritize operational simplicity over I/O efficiency, reverse deltas are the safer choice. The forward-incremental design is more efficient but has more moving parts.

---

## Summary of Recommendations

| Database Size | Recommended Approach | Rationale |
|---------------|---------------------|-----------|
| < 1 GB | Either (reverse delta simpler) | I/O savings negligible |
| 1-10 GB | Forward incremental | Clear I/O advantage, fast compaction |
| 10-100 GB | Forward incremental (strongly) | 5.7x I/O savings, compaction completes in minutes |
| 100 GB+ | Forward incremental (essential) | Reverse delta I/O is prohibitive at scale |
| > 50% churn | Evaluate both | Forward advantage narrows |
| No scheduler | Reverse delta | Avoids compaction dependency |

The forward-incremental design is the clear choice for Typhon's target use cases (real-time simulation databases in the 1-100 GB range with moderate churn). The 5.7x I/O reduction per week compounds into significant storage bandwidth savings over the lifetime of a deployment.

---

## Related Documents

- [01 - Architecture](./01-architecture.md) — System integration and data flow
- [02 - Backup Creation](./02-backup-creation.md) — Step-by-step backup flow
- [03 - File Format](./03-file-format.md) — `.pack` file layout specification
- [04 - Reconstruction & Restore](./04-reconstruction.md) — Page map building and restore algorithm
- [05 - Compaction & Pruning](./05-compaction-pruning.md) — Chain management lifecycle
- [07-backup.md](../../overview/07-backup.md) — Previous reverse-delta design (superseded by this series)
- [06-durability.md](../../overview/06-durability.md) — Checkpoint pipeline, WAL, page checksums
- [ADR-014](../../adr/014-no-point-in-time-recovery.md) — No WAL-based PITR

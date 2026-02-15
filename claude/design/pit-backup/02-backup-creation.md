# Part 2: Backup Creation

> Detailed step-by-step backup creation flow, covering the dirty bitmap lifecycle, scoped CoW shadow buffer, seqlock-based checkpoint CRC, and complete backup procedure.

**Parent:** [README.md](./README.md) | **Previous:** [01 - Architecture](./01-architecture.md) | **Next:** [03 - File Format](./03-file-format.md)

<a href="../../assets/typhon-pitbackup-creation-flow.svg">
  <img src="../../assets/typhon-pitbackup-creation-flow.svg" width="1200"
       alt="PIT Backup — Creation Flow">
</a>
<sub>🔍 Open `claude/assets/viewer.html` for interactive pan-zoom</sub>

---

## Table of Contents

1. [Dirty Bitmap Lifecycle](#dirty-bitmap-lifecycle)
2. [Scoped CoW Shadow Buffer](#scoped-cow-shadow-buffer)
3. [Seqlock for Checkpoint CRC](#seqlock-for-checkpoint-crc)
4. [Complete Backup Flow](#complete-backup-flow)
5. [Error Handling](#error-handling)
6. [Performance Characteristics](#performance-characteristics)

---

## Dirty Bitmap Lifecycle

The dirty bitmap is the mechanism by which the backup system knows **which pages changed** since the last backup. It is maintained by the checkpoint pipeline, not by the backup system directly, and accumulates changes across checkpoint cycles until the next backup consumes them.

### Ownership and Maintenance

The dirty bitmap is **owned and maintained by the checkpoint pipeline**. The checkpoint thread is the only writer (via bit-OR operations). The backup manager is a reader that also performs the post-backup clear.

- One bit per page, indexed by **file page index**
- Checkpoint: after successfully flushing a page to the data file, OR the corresponding bit into the bitmap
- The bitmap accumulates across checkpoint cycles (checkpoints run every ~10s; backups run every ~4h)
- Between two backup points, the bitmap accumulates changes from ~1,440 checkpoint cycles

### Persistence

The dirty bitmap is persisted at each checkpoint alongside checkpoint metadata. This is inexpensive because the bitmap is tiny relative to the data it represents:

| Database Size | Total Pages | Bitmap Size | % of DB |
|---------------|-------------|-------------|---------|
| 1 GB          | 131,072     | 16 KB       | 0.002%  |
| 10 GB         | 1,310,720   | 160 KB      | 0.002%  |
| 100 GB        | 13,107,200  | 1.6 MB      | 0.002%  |
| 1 TB          | 134,217,728 | 16 MB       | 0.002%  |

**Size formula:** `ceil(totalPages / 8)` bytes.

The bitmap is written atomically as part of the checkpoint metadata record. Because it is small (even at 1 TB scale), it adds negligible overhead to the checkpoint I/O.

### Crash Safety

The dirty bitmap is crash-safe by design, without requiring any special recovery logic:

1. **On crash, the bitmap may be stale** (last persisted at the most recent completed checkpoint)
2. **WAL replay re-dirties pages** during crash recovery -- any page modified by replayed WAL records is marked dirty in the page cache
3. **The next checkpoint after recovery** flushes those re-dirtied pages and OR's their bits back into the bitmap
4. **No pages are missed** -- the WAL guarantees that all committed modifications are replayed, and the checkpoint pipeline guarantees that all flushed pages are reflected in the bitmap

This means the bitmap can lose information on crash (bits set between last checkpoint and crash are lost), but those same pages will be re-dirtied by WAL replay and re-set by the next checkpoint. The invariant is: **a page that changed since the last backup will always have its bit set by the time the next backup reads the bitmap.**

### Torn Bitmap Recovery

For large databases, the persisted bitmap spans multiple pages (e.g., 1.6 MB = ~200 pages for a 100 GB DB). If a crash interrupts the checkpoint, the bitmap persistence itself may be torn — some pages written, others stale.

**Detection:** The bitmap is protected by a CRC32C checksum computed over the entire bitmap content, stored in the checkpoint metadata. On recovery, if the CRC doesn't match, the bitmap is considered corrupt.

**Recovery strategy:**
1. **Primary:** Load the bitmap from the *previous* successful checkpoint (which is consistent). WAL replay from that earlier checkpoint covers the delta. Total coverage: T_backup → T_earlier_checkpoint (persisted bitmap) + T_earlier_checkpoint → T_crash (WAL replay + checkpoint).
2. **Fallback:** If no valid persisted bitmap exists (e.g., first backup cycle, or multiple consecutive torn checkpoints), the backup manager forces a **full backup** on the next attempt (treat all allocated pages as dirty). This is safe — full backups are always correct — and the cost is amortized by the rarity of the event (torn checkpoints are already rare; consecutive torn checkpoints are extremely rare).

### Backup-Time Interaction

At backup time, the bitmap is consumed in two steps:

1. **Read:** The backup manager reads the bitmap to determine the changed page set. This is a snapshot copy (or pointer swap) so that new checkpoint-driven OR operations do not interfere with the in-progress backup scan.

2. **Clear:** After the backup completes successfully, the bitmap is cleared (all bits set to 0). This resets the accumulator for the next backup cycle. The clear happens **only on successful backup completion** -- if the backup fails, the bitmap retains its bits and the next backup attempt will retry the same (plus any newly changed) pages.

### Bitmap Growth

As the database grows (new pages allocated), the bitmap is extended. The bitmap size is always `ceil(totalAllocatedPages / 8)` bytes. Extension is cheap: append zero bytes (new pages have not been modified since they were allocated, so their bits start at 0). If a newly allocated page is subsequently modified and checkpointed, the checkpoint pipeline sets its bit normally.

---

## Scoped CoW Shadow Buffer

The Copy-on-Write (CoW) shadow buffer provides snapshot consistency during the backup window. Its key property is **scoping**: only pages in the dirty bitmap's changed set are eligible for CoW. This dramatically reduces the cost of the mechanism compared to a full-database CoW.

### Activation Window

The CoW shadow buffer is **only active during the backup scan window** -- typically a few seconds every ~4 hours. Outside this window, the entire CoW mechanism is bypassed with a single volatile read of `_backupActive` (value 0).

### Interception Point

All write paths converge at `TryLatchPageExclusive` in `PagedMMF.cs`, where a page transitions to exclusive (write) mode. This is the single CoW interception point:

```csharp
// Inside TryLatchPageExclusive, before page becomes Exclusive
if (_backupActive != 0)                              // 1 volatile read
{
    if (_backupDirtyBitmap.IsSet(pi.FilePageIndex))  // 1 bitmap test (scoped!)
    {
        TryCoWForBackup(pi);                         // only for changed pages
    }
}
```

**Why this works:** The two-check design ensures minimal hot-path impact:

1. **`_backupActive != 0`** -- A single volatile read. When no backup is running (99.9% of the time), this is the only cost: ~1 CPU cycle. Branch prediction strongly favors the false path.

2. **`_backupDirtyBitmap.IsSet(pi.FilePageIndex)`** -- Only reached during backup. This bitmap test scopes the CoW to only those pages that changed since the last backup. For a typical 10% churn rate, 90% of pages skip the CoW entirely. This is the key optimization over a full-database CoW approach.

### CoW Logic

The `TryCoWForBackup` method is marked `NoInlining` to keep the hot path icache-friendly. The vast majority of calls to `TryLatchPageExclusive` never reach this method, so inlining it would waste instruction cache space on the common path.

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
private void TryCoWForBackup(PageInfo pi)
{
    int filePageIndex = pi.FilePageIndex;

    // Atomic test-and-set: only first thread for this page wins
    if (!_cowBitmap.TrySet(filePageIndex))
        return;  // already CoW'd

    // Allocate shadow slot (lock-free bump allocator)
    int slot = Interlocked.Increment(ref _shadowPoolHead) - 1;
    if (slot >= _shadowPoolSize)
        slot = WaitForFreeSlot();  // backpressure

    // Copy 8KB from page cache to shadow pool
    byte* src = _memPagesAddr + (pi.MemPageIndex * PageSize);
    byte* dst = _shadowPoolAddr + (slot * PageSize);
    Buffer.MemoryCopy(src, dst, PageSize, PageSize);

    // Enqueue for backup reader
    _backupQueue.Enqueue(new ShadowEntry
    {
        FilePageIndex = filePageIndex,
        ChangeRevision = ((PageBaseHeader*)src)->ChangeRevision,
        ShadowSlotIndex = slot
    });
}
```

### Data Structures

| Structure | Type | Purpose | Size |
|-----------|------|---------|------|
| `_backupActive` | `volatile int` | Flag (0/1) enabling CoW interception | 4 bytes |
| `_backupDirtyBitmap` | Bitmap reference | Scoping: which pages are in the changed set | ~16 KB per 1GB DB |
| `_cowBitmap` | `ConcurrentBitmapL3All` | Ensures each page is CoW'd exactly once (atomic `TrySet`) | ~16 KB per 1GB DB |
| `_shadowPool` | Pinned byte array (ring buffer) | Pre-allocated buffer for CoW page copies | Default 512 pages = 4 MB |
| `_shadowPoolHead` | `int` (Interlocked) | Lock-free bump allocator index into shadow pool | 4 bytes |
| `_backupQueue` | `ConcurrentQueue<ShadowEntry>` | Lock-free MPSC queue for shadow entries | Variable |

### Shadow Pool Design

The shadow pool is a **pre-allocated, pinned ring buffer** sized to hold a configurable number of page copies. Default: 512 pages = 4 MB.

**Why pre-allocated and pinned:**
- No GC pressure during the backup window (no allocations on the hot path)
- `Buffer.MemoryCopy` requires stable source and destination addresses
- Pinning prevents GC from moving the buffer during concurrent page copies

**Lock-free slot allocation:** Each writer thread atomically increments `_shadowPoolHead` via `Interlocked.Increment` to claim a slot. This is contention-free in practice because:
- Only pages in the dirty set trigger CoW (10% of writes)
- Each page is CoW'd at most once (`_cowBitmap.TrySet` deduplicates)
- The typical backup window is a few seconds (limited time for conflicts)

### Backpressure

If the shadow pool is exhausted (all slots in use, backup reader has not consumed entries fast enough), `WaitForFreeSlot()` blocks the calling writer thread until the backup reader frees a slot by consuming the entry and releasing it.

This provides **natural throttling**: under extreme write pressure during backup, transaction latency increases gracefully. The backup reader is the bottleneck, and writers slow down to match its consumption rate. In practice, this is rare because:
- The backup reader processes pages sequentially (LZ4 compress + write) at multi-GB/s rates
- The shadow pool default of 512 pages provides ample headroom for burst writes
- CoW is scoped to only changed pages, so only a fraction of writes trigger it

### Concurrency Safety

| Concern | Resolution |
|---------|------------|
| Two threads CoW the same page simultaneously | `_cowBitmap.TrySet` is atomic -- only the first thread wins; the second returns immediately |
| Writer modifies page after CoW copy starts | Irrelevant: the `Buffer.MemoryCopy` captures the pre-modification state. The writer is still blocked at `TryLatchPageExclusive` until CoW completes |
| Checkpoint fires during backup window | CoW fires before checkpoint flush -- the shadow copy preserves the epoch-E state. Checkpoint writes epoch E+1 to data file, but backup uses the shadow copy |
| Page loaded directly from disk to Exclusive | CoW captures it -- content matches on-disk state (harmless duplicate in shadow pool) |

### Performance Summary

| Scenario | Cost |
|----------|------|
| No backup active | 1 volatile read (~1 CPU cycle) |
| Backup active, page NOT in dirty set | Volatile read + bitmap test (~5-10 cycles) |
| Backup active, page in dirty set, already CoW'd | Volatile read + bitmap test + `TrySet` fail (~10-15 cycles) |
| Backup active, page in dirty set, first write | Bitmap test + `TrySet` + 8KB memcpy + enqueue (~200-500 ns) |

---

## Seqlock for Checkpoint CRC

The `ChangeRevision` field in `PageBaseHeader` serves dual duty: it is the MVCC revision counter for the page AND the seqlock counter for checkpoint CRC computation. No additional fields or mechanisms are needed.

### How ChangeRevision Works

Every page modification increments `ChangeRevision` via `Interlocked.Increment`. This is already part of the transaction write path (zero added cost for the backup system). The checkpoint thread uses this counter as a seqlock to detect concurrent modifications during its page copy.

### Checkpoint CRC Computation

The checkpoint thread copies a page from the page cache to a write buffer, then computes a CRC32C checksum on the copy. The seqlock pattern ensures the copy is consistent:

```
Checkpoint thread:
  1. Read ChangeRevision → v1
  2. memcpy page to write buffer (~0.3us for 8KB)
  3. Read ChangeRevision → v2
  4. If v1 == v2 → copy is consistent, compute CRC32C, store in copy's header
  5. If v1 != v2 → retry (extremely rare: writer touched page during 0.3us window)
```

**Why this is nearly free:**

- The `Interlocked.Increment` on the writer side already exists (it is part of the page modification logic, not added for backup)
- The checkpoint thread performs two plain reads of `ChangeRevision` -- no atomic operations, no memory barriers on x86
- Retry is extremely rare: a writer must modify the exact same page during the ~0.3us memcpy window
- No lock contention: readers (checkpoint) and writers (transactions) never block each other

### Memory Ordering Considerations

**x86/x64 (primary target):**
- Stores are release-ordered (Total Store Order). A store to page data followed by `Interlocked.Increment` of `ChangeRevision` guarantees that the checkpoint thread sees all data stores before seeing the incremented revision.
- Loads are acquire-ordered. The checkpoint thread's plain reads of `ChangeRevision` see a consistent value.
- Plain reads are sufficient on the checkpoint side -- no `Volatile.Read` needed.

**ARM (future consideration):**
- ARM has a weaker memory model. The `Interlocked.Increment` on the writer side provides a full barrier, which is sufficient.
- On the checkpoint side, explicit acquire fences would be needed around the `ChangeRevision` reads to prevent reordering with the memcpy.
- This is a future concern; Typhon currently targets x86/x64 only.

### CRC32C Computation

Once the seqlock confirms a consistent copy:
- CRC32C is computed over the page content (excluding the CRC field itself in `PageBaseHeader`)
- The computed CRC is stored in the copy's header before writing to the data file
- Hardware CRC32C instruction (SSE 4.2) provides ~8 GB/s throughput -- an 8KB page takes ~1us
- The CRC is also used during backup: when the backup reads a page, it can verify the embedded CRC against the page content

---

## Complete Backup Flow

The backup creation process follows a strict 12-step sequence. Each step has clear preconditions and postconditions, ensuring consistency and enabling clean error recovery.

### Step 1: Force Checkpoint

**Action:** Trigger a checkpoint cycle and wait for it to complete.

**Purpose:** Ensure the data file is consistent at a known epoch E and LSN L. All committed transactions up to this point have their page modifications flushed to the data file.

**Postcondition:** Data file reflects all committed state up to epoch E. The dirty bitmap contains all pages modified since the previous backup (accumulated across all checkpoint cycles since then).

### Step 2: Snapshot the Dirty Bitmap

**Action:** Atomically copy (or swap) the dirty bitmap to create an immutable snapshot for the backup scan.

**Purpose:** The checkpoint pipeline continues running during the backup and will OR new bits into the active bitmap. The backup needs a stable view of what changed.

**Implementation options:**
- **Copy:** `Buffer.MemoryCopy` the bitmap to a separate buffer. Simple, O(bitmap size) -- at most 16 MB even for 1 TB databases.
- **Swap:** Allocate a fresh zeroed bitmap, atomically swap the pointer. The backup reads from the old bitmap; new checkpoint operations write to the new one. More efficient for very large databases.

**Postcondition:** `_backupDirtyBitmap` holds an immutable snapshot of changed pages. The live bitmap continues accumulating for the next backup cycle.

### Step 3: Allocate and Reset Shadow Pool and CoW Bitmap

**Action:**
- Reset `_shadowPoolHead` to 0
- Clear `_cowBitmap` (all bits to 0)
- Ensure shadow pool memory is pinned and ready

**Purpose:** Prepare the CoW infrastructure for the backup window. The CoW bitmap tracks which pages have already been CoW'd (preventing duplicate copies). The shadow pool head is the bump allocator index.

**Postcondition:** Shadow pool and CoW bitmap are in a clean initial state.

### Step 4: Enable CoW Interception

**Action:** Set `_backupActive = 1` (volatile write).

**Purpose:** This single write enables the CoW interception path in `TryLatchPageExclusive`. From this point forward, any transaction that writes to a page in the dirty set will trigger a CoW copy before the write proceeds.

**Ordering:** This must happen AFTER step 3 (shadow pool ready) and AFTER step 2 (dirty bitmap installed). A stale shadow pool or missing bitmap would cause incorrect behavior.

**Postcondition:** CoW is active. Transactions writing to changed pages will produce shadow copies.

### Step 5: Open New .pack File

**Action:** Create a new `.pack` file at the backup destination. Write the 128-byte header (see [Part 3: File Format](./03-file-format.md)).

**Naming convention:** `backup-NNNN.pack` where NNNN is the zero-padded monotonic backup ID.

**Postcondition:** .pack file is open for sequential writing, header written.

### Step 6: Scan and Write Changed Pages

**Action:** For each page in the dirty bitmap's changed set:

1. **Check if page was CoW'd:** Poll `_backupQueue` for a shadow entry matching this page's file page index.
   - If a shadow entry is available: read the page from the shadow buffer at the entry's slot index. This is the pre-modification (epoch E) version.
   - If no shadow entry: the page has not been modified since CoW was enabled. Read it from the data file at the page's file offset. This is the epoch E version (written by the checkpoint in step 1).

2. **LZ4 compress** the 8KB page. If the compressed output is >= 8192 bytes (page did not compress well), store it uncompressed and set `CompressedSize = 0` in the index entry.

3. **Write** the compressed (or raw) data sequentially to the .pack file.

4. **Record** a `PackPageEntry` in an in-memory list: `{PageId, ChangeRevision, DataOffset, CompressedSize}`.

5. **Release** the shadow slot (if applicable) so the pool can be reused.

**Ordering within the scan:** Pages can be processed in any order. The natural order is sequential scan of the dirty bitmap (bit index 0 to N), which corresponds to file page index order. This gives good I/O locality for data file reads but is not strictly required.

**Postcondition:** All changed pages are written to the .pack file. Shadow pool slots are freed as they are consumed.

### Step 7: Disable CoW

**Action:** Set `_backupActive = 0` (volatile write).

**Purpose:** Disable the CoW interception path. Transactions no longer pay any CoW cost. New writes proceed without checking the backup bitmap.

**Postcondition:** CoW is inactive. The backup window is closed.

### Step 8: Drain Remaining Shadow Queue

**Action:** Process any remaining entries in `_backupQueue` that were enqueued between the last scan iteration and the moment CoW was disabled.

**Purpose:** A transaction may have CoW'd a page just before `_backupActive` was set to 0. Those shadow entries need to be consumed to ensure no pages are missed.

**Postcondition:** `_backupQueue` is empty. All shadow copies have been processed.

### Step 9: Write Page Index and Footer

**Action:**
1. Sort the in-memory `PackPageEntry` list by `PageId` (for binary search during reconstruction).
2. Write the sorted page index to the .pack file (N x 16 bytes).
3. Optionally write the allocation bitmap (if this is the first backup or a compacted backup).
4. Write the 32-byte footer containing the page index offset, page count, and file CRC.

**Postcondition:** The .pack file is complete and self-describing.

### Step 10: Append Entry to Catalog

**Action:** Append a 128-byte catalog entry to `catalog.dat` (see [Part 3: File Format](./03-file-format.md)).

**Purpose:** The catalog is the index of all backup points. It enables listing, restoration, and chain-walking without parsing every .pack file.

**Postcondition:** The new backup point is registered in the catalog.

### Step 11: Clear the Dirty Bitmap

**Action:** Clear the backup dirty bitmap (set all bits to 0).

**Purpose:** The bitmap has been consumed. Clearing it resets the accumulator so that new checkpoint operations will only set bits for pages that change AFTER this backup.

**Ordering:** This must happen AFTER the .pack file and catalog entry are successfully written. If the backup fails, the bitmap must NOT be cleared (so the next backup retries the same pages).

**Postcondition:** The dirty bitmap is empty, ready to accumulate changes for the next backup cycle.

### Step 12: Free Shadow Pool Resources

**Action:** Release shadow pool resources:
- Reset `_shadowPoolHead` to 0
- Clear `_cowBitmap`
- Optionally unpin/deallocate the shadow pool if not kept resident between backups

**Postcondition:** All backup-related resources are released. The system is back to normal operation with zero backup overhead.

### Flow Summary Diagram

```
 Force Checkpoint
        |
        v
 Snapshot Dirty Bitmap ───────────────────────────────┐
        |                                             |
        v                                             |
 Allocate Shadow Pool + Reset CoW Bitmap              |
        |                                             |
        v                                             |
 Set _backupActive = 1  ──> CoW now active            |
        |                                             |
        v                                             |
 Open .pack file, write header                        |
        |                                             |
        v                                             |
 For each page in dirty set: ───────────────────────  |
 |  CoW'd? ──yes──> read from shadow buffer           |
 |    |no                                             |
 |    └──────────> read from data file                |
 |  LZ4 compress                                      |
 |  Write to .pack file                               |
 |  Record PackPageEntry                              |
 └────────────────────────────────────────────────────┘
        |
        v
 Set _backupActive = 0  ──> CoW disabled
        |
        v
 Drain remaining shadow queue
        |
        v
 Write page index + footer to .pack
        |
        v
 Append catalog entry
        |
        v
 Clear dirty bitmap
        |
        v
 Free shadow pool resources
        |
        v
 Backup complete
```

---

## Error Handling

Each failure mode has a defined recovery strategy. The guiding principle is: **a failed backup must leave the system in a state where the next backup attempt will succeed and capture all necessary pages.**

### Backup Failure Mid-Write

**Scenario:** The backup process crashes or encounters an I/O error while writing compressed pages to the .pack file.

**Recovery:**
- Delete the incomplete .pack file (partial files are not valid -- the footer and file CRC are missing)
- The dirty bitmap is NOT cleared (step 11 is skipped on failure)
- The next backup attempt will see the same changed pages (plus any newly changed pages) and produce a complete .pack file
- No data loss, no inconsistency

### Shadow Pool Exhaustion

**Scenario:** The shadow pool's 512 slots are all in use. A writer thread attempts to CoW a page but no free slot is available.

**Recovery:**
- `WaitForFreeSlot()` blocks the writer thread until the backup reader consumes an entry and frees a slot
- Transaction latency increases during the blocked period (backpressure)
- The backup reader continues processing at its normal rate; the blockage resolves as slots are freed
- This is a performance degradation, not a correctness issue
- **Mitigation:** Increase shadow pool size for databases with high write rates during backup windows. Monitor shadow pool utilization via diagnostics.

### Data File Read Error

**Scenario:** A read from the data file fails for a specific page during the backup scan (e.g., I/O error, bad sector).

**Recovery:**
- Log the error with the affected file page index
- Skip the page and continue scanning remaining pages
- Mark the backup as **partial** in the catalog entry (set a flag)
- A partial backup can be used for restoration but the affected page(s) will be missing -- the restore process will report which pages could not be recovered
- **Recommendation:** Run `typhon-backup verify` after a partial backup to assess the damage

### Catalog Write Failure

**Scenario:** The .pack file is written successfully, but the append to `catalog.dat` fails (e.g., disk full, permissions error).

**Recovery:**
- The .pack file exists on disk but is not cataloged -- it is an "orphaned" file
- The dirty bitmap is NOT cleared (the backup is not considered complete without a catalog entry)
- The CLI tool (`typhon-backup verify` or `typhon-backup recatalog`) can detect orphaned .pack files by scanning the backup directory and matching them against the catalog
- The recatalog operation reads each .pack file's header and re-creates the missing catalog entry

### Checkpoint During Backup Window

**Scenario:** A checkpoint fires while the backup is scanning pages (between steps 4 and 7).

**Recovery:** This is a normal, expected condition -- not an error.
- The CoW mechanism captures the epoch-E state of any page before it is overwritten by the checkpoint
- The checkpoint writes epoch E+1 data to the data file, but the backup uses the shadow copy (epoch E)
- Bits set in the live dirty bitmap by this checkpoint are for the NEXT backup cycle (the backup reads from its snapshot copy, not the live bitmap)
- No special handling required

### System Crash During Backup

**Scenario:** The system crashes while a backup is in progress.

**Recovery:**
- Any incomplete .pack file is detected on next startup (no valid footer/CRC) and deleted
- The dirty bitmap is recovered via the normal crash recovery path (WAL replay re-dirties pages, next checkpoint re-sets bits)
- The shadow pool is in-memory only and is lost on crash (this is fine -- it is transient)
- The next backup attempt starts fresh with a correct dirty bitmap

---

## Performance Characteristics

### Cost at Different Scales

The table below models a backup with 10% page churn (typical for a 4-hour backup interval with moderate write activity). Bitmap scan time assumes sequential memory access at ~3 GB/s effective throughput.

| DB Size | Total Pages | Changed Pages (10%) | Bitmap Size | Bitmap Scan Time | CoW Shadow Pool | Pack File Size (est.) |
|---------|-------------|---------------------|-------------|------------------|-----------------|----------------------|
| 1 GB    | 131,072     | 12,800              | 16 KB       | ~5 us            | 4 MB (plenty)   | ~50 MB               |
| 10 GB   | 1,310,720   | 128,000             | 160 KB      | ~53 us           | 4 MB (plenty)   | ~500 MB              |
| 100 GB  | 13,107,200  | 1,280,000           | 1.6 MB      | ~533 us          | 4 MB (may cycle) | ~5 GB               |
| 1 TB    | 134,217,728 | 12,800,000          | 16 MB       | ~5.3 ms          | 8-16 MB         | ~50 GB               |

### Backup Window Duration

The backup window (time between `_backupActive = 1` and `_backupActive = 0`) is dominated by the page scan and write phase. Estimated durations:

| DB Size | Changed Pages | Read + Compress + Write (est.) | Total Backup Window |
|---------|---------------|-------------------------------|---------------------|
| 1 GB    | 12,800        | ~0.5s                         | < 1s                |
| 10 GB   | 128,000       | ~3s                           | ~3-5s               |
| 100 GB  | 1,280,000     | ~30s                          | ~30-40s             |
| 1 TB    | 12,800,000    | ~5 min                        | ~5-6 min            |

Estimates assume LZ4 compression at ~2 GB/s and sequential write at ~1 GB/s to the backup destination.

### Hot-Path Impact

| Metric | No Backup Active | Backup Active (page NOT in dirty set) | Backup Active (page in dirty set, first write) |
|--------|-----------------|--------------------------------------|-----------------------------------------------|
| Added latency | ~1 ns (1 volatile read) | ~5 ns (volatile read + bitmap test) | ~300-500 ns (bitmap + CoW + memcpy) |
| Cache lines touched | 1 (`_backupActive`) | 2 (`_backupActive` + bitmap word) | 4+ (bitmap + CoW bitmap + shadow pool) |
| Allocations | 0 | 0 | 0 (shadow pool is pre-allocated) |

### CoW Frequency During Backup

For a database with 10% churn and a 5-second backup window:
- Total pages written by transactions in 5s: depends on workload, but typically a small fraction of the changed set
- Pages requiring CoW: only those in the dirty set AND written during the backup window
- Typical CoW rate: < 1% of the dirty set (most changed pages are not actively being written during the brief backup scan)
- Shadow pool utilization: typically < 10% of capacity

### I/O Breakdown

| Phase | I/O Type | Volume (100 GB DB, 10% churn) | Duration |
|-------|----------|-------------------------------|----------|
| Force checkpoint | Data file write (existing) | Variable (current dirty set) | ~1-5s |
| Bitmap snapshot | Memory copy | 1.6 MB | < 1 us |
| Page reads (data file) | Sequential read | ~10 GB (8KB x 1.28M pages) | ~3s at 3 GB/s |
| Page reads (shadow) | Memory read | Negligible (< 1% of pages) | < 1 ms |
| LZ4 compression | CPU | ~10 GB input | ~5s at 2 GB/s |
| Pack file write | Sequential write | ~5 GB (50% compression) | ~5s at 1 GB/s |
| Page index write | Sequential write | ~20 MB (1.28M x 16B) | < 1 ms |
| Catalog append | Write | 128 B | < 1 ms |

---

## Related Documents

- [01 - Architecture](./01-architecture.md) -- Core principles and system integration
- [03 - File Format](./03-file-format.md) -- .pack file layout and catalog format
- [06-durability.md](../../overview/06-durability.md) -- Checkpoint pipeline, ChangeRevision
- [03-storage.md](../../overview/03-storage.md) -- PagedMMF, TryLatchPageExclusive, page cache
- [01-concurrency.md](../../overview/01-concurrency.md) -- ConcurrentBitmapL3All, AccessControl

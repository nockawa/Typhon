# Point-in-Time Backup: CRC, Page Protection, and Incremental Snapshots

**Date:** 2026-02-15
**Status:** In progress
**Outcome:** TBD — analysis reveals tensions with existing backup design (07-backup.md, ADR-014, ADR-028)

## Context

This research emerged from a practical question: *when should we compute the CRC of a page, and how do we prevent modifications during async saves?* The investigation expanded into incremental point-in-time backup architecture and uncovered design tensions with the existing backup specification.

### Questions to Answer

1. When exactly should page CRC32C be computed?
2. How to prevent page modifications during async I/O without killing transaction latency?
3. Can we build incremental point-in-time backup on top of the checkpoint pipeline?
4. How to efficiently track which pages changed between backups (hours apart)?
5. How does this interact with the existing backup design (07-backup.md)?

### Related Documents

| Document | Relevance |
|----------|-----------|
| [06-durability.md](../overview/06-durability.md) | Checkpoint pipeline, WAL, page checksums (§6.2, §6.6) |
| [07-backup.md](../overview/07-backup.md) | Existing backup design: CoW shadow buffer, reverse deltas |
| [ADR-014](../adr/014-no-point-in-time-recovery.md) | Decision: no PITR, WAL recycled after checkpoint |
| [ADR-028](../adr/028-cow-snapshot-backup.md) | Decision: CoW snapshot backup, no WAL dependency |
| [IO-Pipeline-Crash-Semantics.md](IO-Pipeline-Crash-Semantics.md) | Full I/O pipeline analysis, torn write risks |
| [03-storage.md](../overview/03-storage.md) | PagedMMF, page cache, DirtyCounter, AccessEpoch |
| [01-concurrency.md](../overview/01-concurrency.md) | AccessControlSmall, EpochManager |

---

## Part 1: CRC Timing — When to Compute Page Checksums

### The Problem

A page's CRC32C must represent the **exact bytes written to disk**. Computing at the wrong time produces a checksum that doesn't match the on-disk content, defeating torn write detection.

### Analysis

| Timing | Correct? | Why |
|--------|----------|-----|
| At commit time (dirty-marking) | No | Page may be modified by later transactions before checkpoint |
| At "register for save" time | No | Same window — write may be delayed |
| **Immediately before I/O write** | **Yes** | Checksum matches exact bytes being written |
| After I/O write | No | Can't detect torn writes — checksum wasn't in the page header during write |

### Conclusion

Compute CRC32C **immediately before the I/O write, on a frozen copy of the page**. This aligns with `06-durability.md` §6.6 step 2: *"Write all dirty data pages to OS cache (compute + store page checksums)"*.

Cost: ~0.4µs per 8KB page with SSE4.2 hardware CRC32C — negligible vs. I/O latency (~15-50µs NVMe).

### Verification Points

1. **On cold page read** (page loaded from disk): verify CRC32C matches. Mismatch = corruption.
2. **During crash recovery**: registry scan verifies all pages. Mismatches repaired from WAL FPI.

---

## Part 2: Page Protection During Async I/O — The Seqlock Approach

### The Problem

The checkpoint pipeline copies dirty pages to disk. If a transaction modifies a page *during* the copy or I/O, the written content may be inconsistent (half old, half new), and the CRC will be wrong.

### Why Not Per-Page Latching on Every Write?

The naive approach — acquire an `AccessControlSmall` exclusive latch on every page modification — would devastate transaction performance. Page writes happen millions of times per second; adding even 10ns of latch overhead per write is unacceptable at Typhon's microsecond latency targets.

### The Seqlock Solution: Zero Writer Overhead

The key insight: **transactions are the hot path, checkpoints are the cold path.** The coordination cost should be borne entirely by the checkpoint thread.

`ChangeRevision` already exists in `PageBaseHeader` and increments on every page modification. This is a natural seqlock counter:

**Transaction write path (hot — zero added cost):**
```
Modify page content
Interlocked.Increment(ref header.ChangeRevision)  ← already happens, provides release fence
```

**Checkpoint thread per dirty page (cold — bears all cost):**
```
1. Read ChangeRevision → v1          (acquire read, naturally ordered on x86)
2. memcpy page to write buffer       (~0.3µs for 8KB)
3. Read ChangeRevision → v2
4. If v1 == v2 → copy is consistent
   - Compute CRC32C on the copy
   - Store CRC in copy's PageBaseHeader.PageChecksum
   - Issue async write from the copy
5. If v1 != v2 → retry (writer touched page during our ~0.3µs copy window — extremely rare)
```

### Properties

| Property | Value |
|----------|-------|
| Writer overhead | Zero — `ChangeRevision` increment already exists |
| Checkpoint overhead | ~0.7µs per page (memcpy + CRC), plus rare retries |
| Retry probability | Extremely low — requires a transaction to modify *this exact page* during a 0.3µs window |
| Memory requirement | Write buffer pool: N × 8KB for N concurrent page writes |
| Correctness | Seqlock guarantees the copy is consistent if v1 == v2 |

### Comparison with Full Exclusive Latching

| Approach | Writer cost | Checkpoint cost | Writer blocked during I/O? |
|----------|-------------|----------------|---------------------------|
| Exclusive latch held for full I/O | Latch acquire/release on every write | Latch hold for ~15-50µs (I/O duration) | **Yes** — blocks for full I/O |
| Exclusive latch for copy only | Latch acquire/release on every write | Latch hold for ~0.7µs (copy) | No — released after copy |
| **Seqlock (proposed)** | **None** | ~0.7µs + rare retries | **No** |

### Memory Fence Considerations (x86)

On x86, stores have release semantics and loads have acquire semantics by default. The `Interlocked.Increment` on the writer side provides a full barrier, ensuring all page modifications are visible before `ChangeRevision` updates. The checkpoint thread's plain reads of `ChangeRevision` are sufficient on x86 (loads are not reordered past other loads). On ARM/other architectures, explicit acquire fences would be needed on the checkpoint reader.

---

## Part 3: Incremental Point-in-Time Backup

### Design Discussion Summary

The following backup architecture was explored in conversation:

- **Backup frequency:** Every ~4 hours (much less frequent than checkpoints)
- **Consistency boundary:** Checkpoint epoch (not wall-clock time)
- **Incremental unit:** Full pages (not sub-page diffs)
- **Storage:** Separate directory/disk for durability isolation
- **PITR:** Checkpoint-granularity + WAL segments for fine-grained restore
- **Compression:** LZ4 per page within backup files

### Proposed Architecture

#### Consistency

A backup point is always anchored to a **completed checkpoint** — the point where all committed transactions up to a known LSN are fully materialized in the data file.

```
Backup trigger (every ~4h or manual):
  1. Trigger a checkpoint (or wait for current one to complete)
  2. Checkpoint completes → data file consistent at epoch E, LSN L
  3. Read changed pages from the DATA FILE (not page cache)
     ← data file has checkpointed state; page cache may already be ahead
  4. Compress + write pages to backup location
  5. Copy WAL segments since last backup (for fine-grained PITR)
  6. Write backup metadata (epoch, LSN, datetime, page count)
```

Reading from the data file post-checkpoint is deliberate: it's guaranteed consistent and fully decoupled from the transaction hot path.

#### Changed Page Tracking: Persistent Bitmap

Since backups are hours apart but checkpoints are frequent, we need to accumulate dirty pages across many checkpoints:

```
Checkpoint pipeline (already runs frequently):
  1. Collect dirty pages → flush to data file (existing)
  2. OR each flushed page's bit into the backup_dirty_bitmap  ← NEW

Backup time:
  1. Read bitmap → exact set of changed pages (O(changed pages))
  2. Copy those pages from data file + compress
  3. Clear bitmap

Size: 1M pages (8GB database) = 128 KB. Negligible.
```

**Crash safety:** Persist the bitmap as part of each checkpoint (it's tiny). On crash recovery, WAL replay re-dirties pages modified after the last persisted bitmap, so the next checkpoint will set those bits again.

**Why not WAL-derived?** Scanning 4 hours of WAL to extract page IDs is O(WAL volume). The bitmap is O(1) per checkpoint and O(changed pages) at backup time.

#### Backup Storage Format

```
/backup-location/
├── manifest.dat              # Ordered list of backup points
│
├── base-00001/               # Full backup
│   ├── meta.dat              # Epoch, LSN, datetime, total_page_count
│   ├── alloc.bmp             # Page allocation bitmap snapshot
│   ├── data.pack             # LZ4-compressed pages: [page_id|compressed_len|lz4(page)]
│   └── data.idx              # Index: page_id → offset in data.pack
│
├── incr-00002/               # Incremental (same structure, fewer pages)
│   ├── meta.dat
│   ├── alloc.bmp
│   ├── data.pack
│   └── data.idx
│
└── wal/                      # WAL segments for fine-grained PITR
    ├── segment-000042.wal
    └── ...
```

Multi-directory chosen for simplicity: pruning = delete a directory. No fragmentation.

#### Pruning: Merge-Forward

When removing the oldest backup, promote its still-relevant page versions into the next backup (making it a new full backup):

```
Before:  [base-001] → [incr-002] → [incr-003]
Prune:   merge base-001 unreferenced pages into incr-002
After:   [base-002] → [incr-003]
```

Cannot delete a full backup without first promoting its successor.

#### PITR Restore Flow

```
User: "Restore to 2024-01-15 14:32:07"

1. Find latest backup point with datetime ≤ target
   → incr-003 (checkpoint epoch 58, LSN 4200)

2. Build page set: base-001 + incr-002 + incr-003
   For each page: use LATEST version across the chain

3. Write restored pages to new database file

4. Replay WAL segments from LSN 4200 to target datetime
   → Exact state at 14:32:07

5. Verify page checksums post-restore
```

---

## Part 4: Tensions with Existing Design

This is the most important section. The proposed approach differs from the existing backup specification in several significant ways.

### Tension 1: ADR-014 — No Point-in-Time Recovery

**ADR-014 states:** WAL segments are recycled after checkpoint. No WAL archive, no PITR.

**This research proposes:** Keep WAL segments until the next backup (not just until checkpoint) to enable fine-grained PITR.

**Impact:** This would require revising ADR-014. WAL segments would need to be retained for up to ~4 hours (between backups) instead of being recycled immediately after checkpoint. This increases disk usage from the current budget of 4 × 64MB = 256MB to potentially several GB depending on write volume.

**Resolution needed:** Is fine-grained PITR worth the WAL retention cost? Or is checkpoint-granularity restore (every ~4h) sufficient? If the latter, WAL segments are not needed in the backup, and ADR-014 remains unchanged.

### Tension 2: Reverse Deltas vs. Forward Incrementals

**07-backup.md uses:** Reverse deltas — latest backup is always a full snapshot. Older backups store backward XOR diffs.

| Operation | Reverse Delta (existing) | Forward Incremental (proposed) |
|-----------|-------------------------|-------------------------------|
| Restore latest | Read full directly (instant) | Chain: full + all increments (slower) |
| Restore older point | Apply reverse deltas backward | Read full + increments up to target |
| Delete oldest | Just delete the delta file | Must merge-forward (promote) |
| New snapshot cost | Convert previous full → delta + write new full | Write only changed pages |

**Analysis:** The reverse delta approach optimizes for "restore latest" which is by far the most common operation. The forward incremental approach is simpler to reason about but slower to restore the latest state.

**Resolution needed:** Are these actually incompatible? The backup storage format could support both: forward incrementals for the append-to-backup workflow, with periodic compaction to a full snapshot.

### Tension 3: CoW Shadow Buffer vs. Read-from-Data-File

**07-backup.md uses:** CoW shadow buffer — intercepts `TryLatchPageExclusive`, copies pre-modification pages to shadow pool during backup. Backup reads from shadow pool for modified pages, data file for unmodified pages.

**This research proposes:** Read all changed pages from the data file after checkpoint completes. No CoW needed because the checkpoint already ensures data file consistency.

| Approach | Hot path cost | Backup correctness | Coupling |
|----------|--------------|-------------------|----------|
| CoW shadow buffer | 1 volatile read (no backup) / ~200-500ns (first write during backup) | Pre-modification state captured | Coupled to page cache write path |
| **Post-checkpoint read** | **Zero** (no backup) / **Zero** (during backup) | Checkpoint-consistent state | **Decoupled** — reads from data file |

**Key difference:** The CoW approach captures the state at backup-start time (snapshot semantics during the scan). The post-checkpoint approach captures the state at the most recent checkpoint. For backups happening every ~4 hours, this difference is negligible — the backup is always checkpoint-aligned anyway.

**Resolution needed:** Is the CoW shadow buffer's complexity justified when we can simply read from the data file post-checkpoint? The CoW approach was designed for "backup while transactions continue" — but a forced checkpoint followed by a data file read achieves the same consistency without modifying the hot path.

### Tension 4: Changed Page Tracking

**07-backup.md uses:** Per-page `ChangeRevision` comparison against `BackupPageEntry.ChangeRevision` in the backup index.

**This research proposes:** Persistent dirty bitmap maintained by the checkpoint pipeline.

| Approach | Cost to detect changes | Storage | Crash-safe? |
|----------|----------------------|---------|-------------|
| Revision comparison | O(total pages) — must scan all pages | 22 bytes per page in backup index | Yes (on-disk revisions) |
| **Persistent bitmap** | **O(changed pages)** — read bitmap directly | 1 bit per page (~128KB for 1M pages) | Yes (persisted at checkpoint) |

**Analysis:** For large databases (millions of pages), the bitmap approach is significantly cheaper at backup time. The revision comparison requires reading every page's header. However, the existing design's approach has the advantage of not requiring any modification to the checkpoint pipeline.

**Resolution needed:** Is the checkpoint pipeline modification (OR bits into bitmap per flushed page) acceptable for the performance gain at backup time?

### Tension 5: WAL Dependency

**ADR-028 explicitly states:** "No WAL dependency — backups are self-contained page snapshots."

**This research proposes:** Optional WAL segment inclusion for fine-grained PITR.

These are not necessarily incompatible: the base backup mechanism (page snapshots at checkpoint boundaries) is self-contained. WAL inclusion would be an *optional enhancement* for sub-checkpoint-interval restore precision. The backup is always valid without WAL — the WAL segments just add finer granularity.

---

## Part 5: Integration with Checkpoint Pipeline

### What Changes to the Hot Path

The seqlock approach for CRC requires **zero changes** to the transaction write path. `ChangeRevision` is already incremented.

The persistent dirty bitmap requires **one bit-OR per flushed page** in the checkpoint pipeline. This is ~1-2ns per page — negligible.

### Checkpoint Pipeline Flow (with CRC + backup bitmap)

```
Checkpoint Manager:
  1. Collect dirty page set
  2. FOR EACH dirty page:
     a. Read ChangeRevision → v1
     b. memcpy page to write buffer (~0.3µs)
     c. Read ChangeRevision → v2
     d. If v1 != v2: retry from (a)
     e. Compute CRC32C on copy → store in copy's PageBaseHeader
     f. OR page bit into backup_dirty_bitmap                    ← NEW
  3. Batch-write copies to data file
  4. fsync data file
  5. Persist backup_dirty_bitmap (append to checkpoint metadata) ← NEW
  6. Update WAL checkpoint marker
  7. Recycle eligible WAL segments
  8. Release write buffers (decrement DirtyCounter)
```

### Write Buffer Pool

The seqlock + CoW-for-checkpoint approach requires a pool of 8KB write buffers:

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Buffer count | max dirty pages per checkpoint | One buffer per concurrent page write |
| Buffer size | 8KB (page size) | Exact page copy |
| Allocation | Pre-allocated, pinned | No GC pressure, no allocation on hot path |
| Reuse | Free after I/O completes | Pool recycled per checkpoint |

This is similar to the shadow pool in 07-backup.md §7.1 but serves the checkpoint pipeline rather than the backup process.

---

## Summary: What's New vs. What Conflicts

### Net-New (not in existing docs)

| Finding | Section |
|---------|---------|
| Seqlock for zero-cost page protection during checkpoint I/O | Part 2 |
| Persistent dirty bitmap for changed-page tracking across checkpoints | Part 3 |
| Write buffer pool for checkpoint CoW copies | Part 5 |
| CRC timing analysis confirming "compute on frozen copy" | Part 1 |

### Aligns with Existing Design

| Aspect | Existing Doc |
|--------|-------------|
| CRC compute at checkpoint write time | 06-durability.md §6.2 |
| Checkpoint as consistency boundary for backup | ADR-028, 07-backup.md §7.2 |
| LZ4 compression for backup pages | 07-backup.md §Compression |
| Separate backup storage location | 07-backup.md §7.7 (IBackupStore) |
| Forced checkpoint before backup | ADR-028, 07-backup.md §7.2 lifecycle step 1 |

### Conflicts Requiring Resolution

| Tension | This Research | Existing Design | Resolution Needed |
|---------|--------------|-----------------|-------------------|
| PITR via WAL | Keep WAL segments until next backup | ADR-014: recycle after checkpoint | Revise ADR-014 or accept checkpoint-granularity only |
| Delta direction | Forward incrementals | Reverse deltas (07-backup.md §7.3) | Evaluate which fits the 4h backup frequency better |
| Backup mechanism | Read from data file post-checkpoint | CoW shadow buffer (07-backup.md §7.1) | Can we simplify to post-checkpoint reads? |
| Changed page tracking | Persistent bitmap (O(changed)) | Revision comparison (O(total)) | Is checkpoint pipeline modification acceptable? |
| WAL dependency | Optional WAL inclusion for PITR | ADR-028: no WAL dependency | Additive (optional) — not necessarily conflicting |

## Open Questions

1. **Is fine-grained PITR (via WAL) needed?** Or is checkpoint-granularity restore (every ~4h) sufficient for the game/simulation use case? This is the key question that determines whether ADR-014 needs revision.

2. **Forward vs. reverse deltas at 4h backup frequency?** With only ~6 backup points per day, the chain is short. Reverse deltas optimize restore-latest but add complexity (convert previous full → delta on each backup). Forward incrementals are simpler but slower to restore.

3. **Can the CoW shadow buffer be replaced by post-checkpoint data file reads?** The shadow buffer was designed for low-latency backup during active transactions. But if we force a checkpoint first and read from the data file, the same consistency is achieved without hot-path modifications. The trade-off is that the backup reads from disk (though likely OS-cache-warm) rather than from memory.

4. **Seqlock correctness on ARM.** The seqlock relies on x86 memory ordering. ARM/other architectures need explicit acquire fences on the checkpoint reader side. Should we add platform-conditional fences now or defer until ARM support matters?

5. **Write buffer pool sizing.** The checkpoint needs one 8KB buffer per dirty page it processes concurrently. Should this be a fixed-size pool with backpressure, or dynamically sized?

## Next Steps

- [ ] Resolve PITR question (ADR-014 revision or accept checkpoint-granularity)
- [ ] Resolve forward vs. reverse delta direction
- [ ] Resolve CoW shadow buffer vs. post-checkpoint read approach
- [ ] If design proceeds: promote to design doc (update or replace 07-backup.md)
- [ ] Cross-check with 06-durability.md checkpoint pipeline for integration feasibility

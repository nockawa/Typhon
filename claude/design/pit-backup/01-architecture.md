# Part 1: Architecture

## Core Principles

1. **Backup I/O proportional to change rate, not database size.** A 100GB database with 1% changes should cost ~1GB of backup I/O, not 100GB.

2. **Zero hot-path overhead.** Transaction writes must not pay any cost for the backup system's existence. The only overhead is during active backup windows (~seconds every ~4 hours).

3. **Self-contained backups.** Each backup chain is restorable without WAL, without the running engine, without any external state. This aligns with ADR-014 (WAL recycled after checkpoint).

4. **Compaction amortizes full-backup cost.** Rather than writing all pages every backup (reverse delta tax), write all pages once per compaction cycle (e.g., weekly). Between compactions, only changed pages are persisted.

5. **Consistency via checkpoint + CoW.** Backups are anchored to checkpoint epochs. The CoW shadow buffer handles concurrent modifications during the backup scan window.

## System Integration

### Where Backup Fits in the Persistence Architecture

<!-- Thumbnail links to SVG file -->
<a href="../../assets/typhon-pitbackup-architecture.svg">
  <img src="../../assets/typhon-pitbackup-architecture.svg" width="1200"
       alt="PIT Backup Architecture — integration with checkpoint and WAL pipelines">
</a>
<sub>D2 source: <code>assets/src/typhon-pitbackup-architecture.d2</code></sub>

Typhon has two persistence pipelines (from 06-durability.md):

```
WAL Pipeline:          Ring buffer → WAL file (FUA)     → Durable
Data Page Pipeline:    Page cache  → Data file (fsync)  → Checkpointed
```

The backup system adds a **third, decoupled pipeline**:

```
Backup Pipeline:       Data file   → Backup .pack files → Backed up
```

Key interactions:

| Component | Interaction with Backup |
|-----------|------------------------|
| **Checkpoint Pipeline** | Maintains the dirty bitmap (OR bits per flushed page). Backup reads from data file post-checkpoint. |
| **WAL Pipeline** | No interaction. Backup is WAL-independent (ADR-014). |
| **Page Cache** | CoW shadow buffer intercepts `TryLatchPageExclusive` during active backup. |
| **Transaction Path** | Zero overhead. `ChangeRevision` increment (already exists) serves as seqlock counter. |

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Typhon Engine                                │
│                                                                     │
│  ┌───────────────┐    ┌──────────────────┐    ┌──────────────────┐  │
│  │ Transaction   │    │ Checkpoint       │    │ WAL Writer       │  │
│  │ (hot path)    │    │ Pipeline         │    │                  │  │
│  │               │    │                  │    │                  │  │
│  │ Modify page   │───>│ Collect dirty    │    │ Ring buffer →    │  │
│  │ Incr ChangeRev│    │ Seqlock copy     │    │ WAL file (FUA)   │  │
│  │               │    │ CRC compute      │    │                  │  │
│  └───────────────┘    │ Write data file  │    └──────────────────┘  │
│         │             │ OR dirty bitmap ◄─── NEW                    │
│         │             └────────┬─────────┘                          │
│         │                      │                                    │
│         │             ┌────────▼─────────┐                          │
│         │             │ Dirty Bitmap     │  128KB for 1M pages      │
│         │             │ (persistent)     │  Persisted at checkpoint │
│         │             └────────┬─────────┘                          │
│         │                      │                                    │
│  ┌──────▼──────────────────────▼──────────────────────────────────┐ │
│  │                    Backup Manager                              │ │
│  │                                                                │ │
│  │  1. Force checkpoint                                           │ │
│  │  2. Read dirty bitmap → changed page set                       │ │
│  │  3. Enable scoped CoW (only changed pages)                     │ │
│  │  4. Read pages from data file (or shadow if CoW'd)             │ │
│  │  5. LZ4 compress → write .pack file                            │ │
│  │  6. Disable CoW, clear bitmap                                  │ │
│  │  7. Update catalog                                             │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────┐
│  Backup Storage (separate location)  │
│                                      │
│  catalog.dat                         │
│  backup-0001.pack (full / compacted) │
│  backup-0002.pack (incremental)      │
│  backup-0003.pack (incremental)      │
│  ...                                 │
└──────────────────────────────────────┘
```

### Three-Layer Responsibility Split

| Layer | Responsibility | Runs When | Hot Path Impact |
|-------|---------------|-----------|-----------------|
| **Checkpoint pipeline** | Maintains dirty bitmap (1 bit-OR per flushed page) | Every ~10s | ~1-2ns per page (negligible) |
| **Backup Manager** | Reads bitmap, CoW scope, writes .pack files | Every ~4h | Zero when inactive; CoW for ~seconds during backup |
| **CLI Tool** | Restore, compact, prune, verify, list | On demand | None (offline or separate process) |

## Data Flow: Normal Operation vs. Backup Window

### Normal Operation (99.9% of the time)

```
Transaction writes page P:
  1. Modify page content in page cache
  2. Interlocked.Increment(ChangeRevision)          ← already exists, zero added cost
  3. Mark page dirty (existing DirtyCounter logic)

Checkpoint flushes page P:
  1. Seqlock copy to write buffer
  2. Compute CRC32C on copy
  3. Write to data file
  4. OR bit into backup_dirty_bitmap                 ← NEW: ~1-2ns
  5. Decrement DirtyCounter
```

No backup code executes. The bitmap OR is the only added operation.

### During Backup Window (~3-10 seconds every ~4 hours)

```
Backup Manager starts:
  1. Force checkpoint → data file consistent at epoch E
  2. Read dirty bitmap → {P1, P2, P3, ..., Pn} changed pages
  3. Set _backupActive = 1
  4. Install _backupDirtyBitmap reference for CoW scoping

Transaction writes page Px (where Px is in dirty set):
  1. TryLatchPageExclusive checks _backupActive        ← 1 volatile read
  2. _backupDirtyBitmap.IsSet(Px.FilePageIndex)?        ← 1 bitmap test
  3. If yes and not already CoW'd:
     a. _cowBitmap.TrySet(Px.FilePageIndex)             ← atomic test-and-set
     b. memcpy 8KB to shadow slot                       ← ~300ns
     c. Enqueue for backup reader
  4. Proceed with normal write

Transaction writes page Py (where Py is NOT in dirty set):
  1. TryLatchPageExclusive checks _backupActive         ← 1 volatile read
  2. _backupDirtyBitmap.IsSet(Py.FilePageIndex)?         ← 1 bitmap test → false
  3. Proceed with normal write (no CoW)                  ← fast path, most pages
```

### Page State During Backup

Each page in the changed set exists in one of three states during the backup window:

| State | Where Backup Reads From | How It Gets Here |
|-------|------------------------|------------------|
| **Unmodified since checkpoint** | Data file | No transaction touched this page during backup scan |
| **CoW'd** | Shadow buffer | Transaction modified page; CoW captured pre-modification state |
| **Already read by backup** | Already in .pack file | Backup reader already consumed this page |

## Consistency Guarantee

The backup captures a snapshot-consistent state at checkpoint epoch E:

1. **Force checkpoint** ensures all committed transactions up to LSN L are on the data file
2. **Pages not modified during backup scan** → read from data file → epoch E state
3. **Pages modified during backup scan** → CoW captured epoch E state before modification
4. **Pages modified AND another checkpoint fires** → CoW already captured epoch E state; checkpoint writes epoch E+1 to data file, but backup uses the shadow copy

The CoW bitmap (`_cowBitmap`) ensures each page is copied **exactly once** — the first writer wins. The backup always sees a consistent epoch-E snapshot regardless of concurrent transaction activity or subsequent checkpoints.

## Code Locations (Planned)

| Component | Planned Location |
|-----------|------------------|
| Backup Manager | `src/Typhon.Engine/Backup/BackupManager.cs` |
| Dirty Bitmap | `src/Typhon.Engine/Backup/BackupDirtyBitmap.cs` |
| CoW Shadow Buffer | `src/Typhon.Engine/Backup/CowShadowBuffer.cs` |
| Pack File Writer | `src/Typhon.Engine/Backup/PackFileWriter.cs` |
| Pack File Reader | `src/Typhon.Engine/Backup/PackFileReader.cs` |
| Catalog | `src/Typhon.Engine/Backup/BackupCatalog.cs` |
| Reconstruction Engine | `src/Typhon.Engine/Backup/ReconstructionEngine.cs` |
| Compaction Engine | `src/Typhon.Engine/Backup/CompactionEngine.cs` |
| CLI Tool | `tools/Typhon.BackupCli/` |

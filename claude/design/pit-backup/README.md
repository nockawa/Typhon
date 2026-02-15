# Point-in-Time Incremental Backup — Design Document

> Fast, incremental, forward-chained page backup with compaction-based lifecycle management.

## Summary

This design specifies an incremental backup system for Typhon that captures only **changed pages** at each backup point, stores them as self-contained compressed pack files, and supports full database reconstruction from any backup point via backward chain-walking. A periodic **compaction** operation consolidates the chain into a fresh base, bounding chain depth and enabling simple pruning.

The design integrates three key mechanisms from prior research:
- **Persistent dirty bitmap** — O(changed pages) change detection, maintained by the checkpoint pipeline
- **Scoped CoW shadow buffer** — snapshot consistency during backup scan, limited to changed pages only
- **Seqlock (ChangeRevision)** — zero-cost page protection for checkpoint CRC computation

## Status

**Status:** Draft
**Date:** 2026-02-15
**Branch:** `feature/47-unit-of-work` (design phase)
**Preceded by:** [PitBackup.md research](../../research/PitBackup.md)
**Supersedes:** [07-backup.md](../../overview/07-backup.md) (updated — reverse delta design replaced)
**Related ADRs:** [ADR-014](../../adr/014-no-point-in-time-recovery.md), [ADR-028](../../adr/028-cow-snapshot-backup.md)

## Goals

- Backup I/O proportional to **changes**, not database size
- **Zero** hot-path overhead when no backup is active
- **Near-zero** overhead on checkpoint pipeline (1 bit-OR per flushed page)
- Full database reconstruction from any backup point
- Bounded chain depth via periodic compaction
- Self-contained backups (no WAL dependency) — aligns with ADR-014
- Separate backup location for durability isolation

## Non-Goals

- Fine-grained PITR via WAL replay (ADR-014 stands — WAL is recycled after checkpoint)
- Sub-page delta compression (XOR diffs) — full pages only, simpler and sufficient
- Real-time streaming/replication — backup is a batch operation
- Remote storage abstraction — local filesystem first (IBackupStore from 07-backup can be added later)

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-architecture.md) | **Architecture** | Core principles, integration with checkpoint/WAL, component diagram |
| [02](./02-backup-creation.md) | **Backup Creation** | Step-by-step flow, dirty bitmap, scoped CoW, seqlock |
| [03](./03-file-format.md) | **File Format** | `.pack` layout, catalog, page index — byte-level specification |
| [04](./04-reconstruction.md) | **Reconstruction & Restore** | Page map building, chain walk, verification, CLI interface |
| [05](./05-compaction-pruning.md) | **Compaction & Pruning** | Compaction algorithm, retention policy, scheduling |
| [06](./06-analysis.md) | **Analysis & Simulations** | I/O modeling at 1/10/100 GB, comparison with reverse deltas |

## Key Design Choices

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Delta direction | **Forward incremental** | Backup I/O = O(changed pages), not O(database size) |
| Changed page detection | **Persistent dirty bitmap** | O(changed pages) vs O(total pages) revision scan |
| Snapshot consistency | **Scoped CoW shadow buffer** | Handles checkpoint race; scoped to dirty set only |
| Chain management | **Periodic compaction** | Bounds chain depth, simple "create new base + delete old" |
| Full backup frequency | **Weekly compaction** (not per-backup) | Amortizes O(DB size) cost over ~42 incrementals |
| Backup unit | **Full page (8KB)** | Matches storage model, no semantic knowledge needed |
| Compression | **LZ4 per page** | Fast (~2 GB/s), ~50% ratio, random-access decompression |
| WAL dependency | **None** | Self-contained; aligns with ADR-014 |
| Pruning mechanism | **CLI tool** | Acceptable complexity; runs offline/scheduled |

## Key Insights

### 1. Forward Incrementals Beat Reverse Deltas at Scale

For a 100GB database with 10% churn per backup interval:
- Forward incremental: **~15 GB** I/O per backup
- Reverse delta (07-backup): **~205 GB** I/O per backup

The "restore-latest is instant" advantage of reverse deltas costs 14x more I/O on every backup — a tax paid continuously for a benefit used rarely.

### 2. Compaction = Amortized Full Backup

Instead of writing a full snapshot every backup (reverse delta requirement), write one during weekly compaction. Between compactions, only changed pages are written. This reduces weekly I/O from ~8,600 GB to ~370 GB for a 100GB database.

### 3. CoW Scope Reduction

Forward incrementals only read changed pages during backup. The CoW shadow buffer only needs to protect those same pages — typically 10% of the database. This means:
- 10x less shadow pool traffic
- 10x less backpressure risk
- The hot-path CoW check (`_backupDirtyBitmap.IsSet(pi.FilePageIndex)`) skips 90% of pages

### 4. The Dirty Bitmap Bridges Checkpoint and Backup Frequencies

Checkpoints run every ~10 seconds. Backups run every ~4 hours. The persistent dirty bitmap accumulates changes across ~1,440 checkpoints, giving the backup process an instant O(1) answer to "what changed?"

## Quick Reference

### Backup File Layout
```
.pack = [Header 128B] [Compressed Pages...] [Page Index N×16B] [Footer 32B]
```

### CLI Operations
```
typhon-backup create   --db <path> --dest <backup-dir>
typhon-backup restore  --source <backup-dir> --target <db-path> [--point <id|datetime>]
typhon-backup compact  --source <backup-dir> --target <backup-id>
typhon-backup list     --source <backup-dir>
typhon-backup verify   --source <backup-dir> [--point <id|datetime>]
typhon-backup prune    --source <backup-dir> --before <id|datetime>
```

### I/O Cost Summary (100GB DB, 10% churn)

| Operation | I/O | Frequency |
|-----------|-----|-----------|
| Incremental backup | ~15 GB | Every 4h |
| Restore (chain of 6) | ~130 GB | Rare |
| Compact | ~100 GB | Weekly |
| Prune (after compact) | ~0 (file deletes) | Weekly |

## Related Documents

- [PitBackup.md](../../research/PitBackup.md) — Research that led to this design
- [07-backup.md](../../overview/07-backup.md) — Previous backup design (reverse deltas, CoW shadow buffer)
- [06-durability.md](../../overview/06-durability.md) — Checkpoint pipeline, WAL, page checksums
- [03-storage.md](../../overview/03-storage.md) — PagedMMF, page cache, DirtyCounter
- [01-concurrency.md](../../overview/01-concurrency.md) — AccessControlSmall, EpochManager
- [ADR-014](../../adr/014-no-point-in-time-recovery.md) — No PITR, WAL recycled after checkpoint
- [ADR-028](../../adr/028-cow-snapshot-backup.md) — Original CoW snapshot backup decision
- [IO-Pipeline-Crash-Semantics.md](../../research/IO-Pipeline-Crash-Semantics.md) — I/O pipeline analysis

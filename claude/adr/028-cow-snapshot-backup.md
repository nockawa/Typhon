# ADR-028: Copy-on-Write Snapshot Backup (No WAL Dependency)

**Status**: Accepted (enhanced — see update below)
**Date**: 2025-01 (design session)
**Updated**: 2026-02-15 — Scoped CoW with dirty bitmap
**Deciders**: Developer + Claude (design session)

## Context

Typhon needs a backup mechanism that:
1. Creates consistent point-in-time snapshots without stopping the database
2. Does not depend on WAL for backup integrity (WAL is recycled after checkpoint — see [ADR-014](014-no-point-in-time-recovery.md))
3. Imposes near-zero overhead when no backup is active
4. Works within the embedded, single-process architecture ([ADR-004](004-embedded-engine-no-server.md))

The primary challenge: taking a consistent snapshot while transactions continue modifying pages. If we read pages sequentially while writes happen, we get a "torn" snapshot mixing pre- and post-modification states.

## Decision

Use a **Copy-on-Write (CoW) shadow buffer** triggered at the page cache level:

1. **Force checkpoint** before starting backup (ensures on-disk consistency)
2. **Set `_backupActive` flag** (volatile int, atomic)
3. **Intercept exclusive page acquisition**: in `TryLatchPageExclusive`, before a page becomes `Exclusive`, copy its current content to a shadow buffer
4. **Track CoW'd pages**: `ConcurrentBitmapL3All` — one bit per `FilePageIndex`, atomic test-and-set
5. **Background writer**: dequeues shadow pages, compresses, writes sequentially to backup file
6. **Clear flag** when backup completes

### 2026-02 Update: Scoped CoW with Dirty Bitmap

The original design CoW'd **every page** written during backup. The enhanced design adds a **persistent dirty bitmap** and scopes the CoW to **only pages that changed since the last backup** — typically ~10% of the database. This reduces shadow pool traffic by ~10x.

The interception now has a two-check fast path:
```
if (_backupActive != 0)                             // 1 volatile read
    if (_backupDirtyBitmap.IsSet(pi.FilePageIndex)) // 1 bitmap test — scoped!
        TryCoWForBackup(pi);                        // only for dirty pages
```

The dirty bitmap is maintained by the checkpoint pipeline at near-zero cost (~1-2 ns per flushed page, on the checkpoint thread, not the transaction thread).

### Hot-Path Cost

```
No backup active:                          1 volatile read (~1 cycle)
Backup active, page NOT in dirty set (90%): + 1 bitmap test (~3 ns)
Backup active, dirty page, already CoW'd:  + 2 bitmap tests (~4-5 ns)
First write to dirty page:                 + 8KB memcpy + enqueue (~200-500 ns)
```

### Shadow Pool

Pre-allocated, pinned ring buffer (reuses [ADR-009](009-pinned-memory-unsafe-code.md) pattern):
- Default: 512 pages = 4MB
- Lock-free slot allocation (`Interlocked.Increment`)
- Backpressure: if pool exhausted, writing thread blocks until background writer frees slots

### Consistency Guarantee

The combination of "force checkpoint" + "CoW on first write to dirty page" ensures the backup captures the database state at the checkpoint boundary:
- Pages not in dirty bitmap → not read during backup (unchanged since last backup)
- Dirty pages not modified during backup → read from data file (already consistent)
- Dirty pages modified during backup → shadow buffer holds the pre-modification version

## Alternatives Considered

1. **WAL-based backup** (stream WAL records, replay to build snapshot) — Requires keeping WAL segments alive during backup (contradicts [ADR-014](014-no-point-in-time-recovery.md)). Complex replay logic. Tight coupling between backup and WAL format.

2. **Quiesce database during backup** (stop all transactions, copy pages, resume) — Simple but unacceptable for real-time workloads. Backup of a 1GB database at NVMe speed (~3GB/s) still takes 300ms of total downtime.

3. **OS-level filesystem snapshot** (ZFS/Btrfs/LVM snapshot) — Platform-dependent. Not all deployment targets support CoW filesystems. Exposes internal page format to external tools.

4. **Double-buffering** (maintain two copies of all pages, swap on backup) — 2× memory usage always, not just during backup. Wasteful for the 99.9% of time when no backup is active.

5. **Logical backup** (iterate all entities, serialize to application format) — Requires semantic understanding of data. Slow (must read all revision chains). Cannot do byte-level incremental deltas.

## Consequences

**Positive:**
- Near-zero cost when no backup is active (1 volatile read per page acquisition)
- Non-blocking: transactions continue at full speed during backup
- Self-contained: backup file has no WAL dependency
- Consistent: captures exact checkpoint-boundary state
- Leverages existing infrastructure: pinned memory, ConcurrentBitmapL3All, page cache
- Compatible with [ADR-025](025-checkpoint-manager-sole-fsync-owner.md): checkpoint forced first, then CoW handles subsequent modifications

**Negative:**
- Memory pressure during backup: shadow pool + dirty bitmap + CoW bitmap (~4-40 MB depending on DB size)
- Backpressure risk: if write rate exceeds backup writer throughput, transaction latency increases (mitigated by scoped CoW — only ~10% of pages need CoW)
- Complexity in `TryLatchPageExclusive` hot path (conditional branch, bitmap access)
- One concurrent backup at a time (simplicity trade-off; avoids multi-version page management)
- Checkpoint latency spike at backup start (forced flush of all dirty pages)

**Cross-references:**
- [ADR-009](009-pinned-memory-unsafe-code.md) — Pinned memory pattern for shadow pool
- [ADR-014](014-no-point-in-time-recovery.md) — WAL recycled, backup is primary recovery mechanism
- [ADR-025](025-checkpoint-manager-sole-fsync-owner.md) — Checkpoint forced before backup start
- [ADR-029](029-reverse-delta-incremental-snapshots.md) — Superseded by forward incrementals
- [07-backup.md](../overview/07-backup.md) §7.2 — CoW Shadow Buffer design details
- [PIT Backup Design](../design/pit-backup/02-backup-creation.md) — Scoped CoW and dirty bitmap details

# ADR-028: Copy-on-Write Snapshot Backup (No WAL Dependency)

**Status**: Accepted
**Date**: 2025-01 (design session)
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
3. **Intercept exclusive page acquisition**: in `TransitionPageToAccess`, before a page becomes `Exclusive`, copy its current content to a shadow buffer
4. **Track CoW'd pages**: `ConcurrentBitmapL3All` — one bit per `FilePageIndex`, atomic test-and-set
5. **Background writer**: dequeues shadow pages, compresses, writes sequentially to backup file
6. **Clear flag** when backup completes

### Hot-Path Cost

```
No backup active:    1 volatile read (~1 cycle)
Page already CoW'd:  + 1 bitmap test (~5-10 cycles)
First write to page: + 8KB memcpy + enqueue (~200-500ns)
```

### Shadow Pool

Pre-allocated, pinned ring buffer (reuses [ADR-009](009-pinned-memory-unsafe-code.md) pattern):
- Default: 512 pages = 4MB
- Lock-free slot allocation (`Interlocked.Increment`)
- Backpressure: if pool exhausted, writing thread blocks until background writer frees slots

### Consistency Guarantee

The combination of "force checkpoint" + "CoW on first write" ensures the backup captures the database state at the checkpoint boundary:
- Pages not modified during backup → read from data file (already consistent)
- Pages modified during backup → shadow buffer holds the pre-modification version

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
- Memory pressure during backup: up to `modified_pages × 8KB` in shadow pool
- Backpressure risk: if write rate exceeds backup writer throughput, transaction latency increases
- Complexity in `TransitionPageToAccess` hot path (conditional branch, bitmap access)
- One concurrent backup at a time (simplicity trade-off; avoids multi-version page management)
- Checkpoint latency spike at backup start (forced flush of all dirty pages)

**Cross-references:**
- [ADR-009](009-pinned-memory-unsafe-code.md) — Pinned memory pattern for shadow pool
- [ADR-014](014-no-point-in-time-recovery.md) — WAL recycled, backup is primary recovery mechanism
- [ADR-025](025-checkpoint-manager-sole-fsync-owner.md) — Checkpoint forced before backup start
- [07-backup.md](../overview/07-backup.md) §7.1 — CoW Shadow Buffer design details
- [07-backup.md](../overview/07-backup.md) §7.2 — Snapshot Manager lifecycle

# ADR-029: Reverse-Delta Incremental Snapshots

**Status**: Accepted
**Date**: 2025-01 (design session)
**Deciders**: Developer + Claude (design session)

## Context

A backup system needs incremental snapshots to avoid copying the entire database on every backup. Two main strategies exist:

**Forward deltas** (traditional): First backup is full, subsequent ones store only what changed since the previous backup.
```
Full(T0) → Δ(T0→T1) → Δ(T1→T2) → Δ(T2→T3)
Restore T3 = Full + Δ1 + Δ2 + Δ3 (apply all forward)
Delete Full = must rebuild Δ1 into new Full (cascade)
```

**Reverse deltas**: Most recent backup is always full, older ones store how to go backward.
```
Δ(T2→T1) ← Δ(T3→T2) ← Full(T3)
Restore T3 = read Full directly (instant)
Delete T1 = just remove Δ1 file (no cascade)
```

Typhon's use case: game/simulation servers where "restore to latest" is the overwhelmingly common operation (disaster recovery), while "restore to old point" is rare (debugging, post-mortem).

## Decision

Use **reverse deltas** for incremental snapshots:

1. The **most recent snapshot is always a full backup** (all pages, compressed)
2. When creating a new snapshot:
   - The previous full backup is converted to a reverse-delta file (stores XOR-compressed diffs to go backward)
   - The new snapshot becomes the full backup
3. Older snapshots are just `.typhon-rdelta` files that can be applied in reverse to reach earlier states

### Delta Algorithm

For 8KB pages:
1. **XOR** current page with previous page → diff buffer
2. **Compress** the XOR result (LZ4 default, Zstd for archival)

XOR is ideal because:
- Unchanged bytes → zeros → compress to near-nothing
- Typical B+Tree leaf update changes ~32/8192 bytes → XOR result is 99.6% zeros → compresses to ~50 bytes
- Fully reorganized pages (splits) → XOR is ~full page size → acceptable (splits are rare)

### Change Detection

Pages are identified as changed via `ChangeRevision` (4-byte counter in `PageBaseHeader`, incremented on every flush). Combined with a 2-byte `BackupEpoch` per page in the backup index:

```
Page needs backup when: page.ChangeRevision != lastBackup[page].ChangeRevision
```

## Alternatives Considered

1. **Forward deltas** (PostgreSQL pg_basebackup, SQL Server) — Optimizes for "create new backup" (just store diff). But "restore latest" requires applying entire chain (slow), and "delete oldest" cascades (must rebuild).

2. **Copy-on-Write filesystem snapshots** (ZFS, Btrfs) — Efficient but platform-dependent. Ties backup strategy to deployment infrastructure. No cross-platform portability.

3. **Block-level deduplication** (like Borg, Restic) — Content-addressed chunks, excellent dedup ratio. But adds complexity (chunk index, garbage collection) and doesn't leverage page-level knowledge.

4. **WAL-based incremental** (stream WAL since last backup) — Compact, but requires WAL retention (contradicts [ADR-014](014-no-point-in-time-recovery.md)). Restore requires full WAL replay.

5. **Byte-level diff algorithms** (xdelta3, bsdiff) — Better compression ratio than XOR for complex changes. But significantly slower (10-100× per page) and overkill for 8KB blocks where XOR+LZ4 already handles the common case.

## Consequences

**Positive:**
- "Restore latest" is instant: just read the full snapshot file
- "Delete old snapshot" is free: remove the reverse-delta file, no chain rebuild
- Simple to validate: full snapshot is self-contained, can verify independently
- XOR+compress is extremely fast (~2GB/s for XOR, ~2GB/s for LZ4)
- Typical delta size: near-zero for unchanged pages, ~50 bytes for single-field updates
- Compatible with streaming: can write backup sequentially, page index at end

**Negative:**
- "Create new snapshot" is more expensive: must read previous full + write new full + convert old to delta
- "Restore old point" is slower: must read latest full + apply reverse deltas backward
- Storage: latest snapshot is always full database size (no savings on the most recent copy)
- New snapshot creation requires reading the previous full backup (I/O amplification)
- XOR doesn't handle page-number changes well (if a page is freed and reallocated, XOR gives full-page diff)

**Mitigations:**
- New snapshot cost: sequential reads at NVMe speed (~3GB/s) make even 10GB databases take ~3s to read
- Old-point restore rarity: game servers almost never need to go back more than one snapshot
- Full snapshot size: LZ4 compression typically reduces by 50%+

**Cross-references:**
- [ADR-028](028-cow-snapshot-backup.md) — CoW mechanism that captures consistent page state
- [ADR-014](014-no-point-in-time-recovery.md) — No WAL retention, backup is primary recovery
- [ADR-006](006-8kb-page-size.md) — 8KB pages: the unit of backup and delta
- [07-backup.md](../overview/07-backup.md) §7.3 — Reverse Delta Engine design
- [07-backup.md](../overview/07-backup.md) §7.5 — File format (.typhon-snap, .typhon-rdelta)

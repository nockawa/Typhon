# ADR-030: Dual-Limit Retention Policy for Backup Snapshots

**Status**: Accepted (updated for forward incrementals)
**Date**: 2025-01 (design session)
**Updated**: 2026-02-15 — Adapted for forward-incremental backup chain
**Deciders**: Developer + Claude (design session)

## Context

Typhon's backup system creates forward-incremental backup files (see [07-backup.md](../overview/07-backup.md) and the [PIT Backup Design](../design/pit-backup/README.md)). Over time, `.pack` files accumulate in the backup chain and must be cleaned up. The retention policy must:

- Support wildly different backup frequencies (every 10 minutes to once a day)
- Give users direct control over storage consumption
- Be simple to configure and reason about
- Integrate with the compaction lifecycle (forward incrementals require periodic compaction to bound chain depth)

## Decision

Use a **dual-limit** retention strategy with configurable thresholds — **any** exceeded limit triggers cleanup (most restrictive wins):

```
MaxCount:         int       — max backups to keep (0 = unlimited)
MaxAge:           TimeSpan  — delete backups older than this (Zero = unlimited)
MaxTotalSize:     long      — total backup directory size in bytes (0 = unlimited)
CompactThreshold: int       — compact after this many incrementals (default: 42)
MinKeep:          int       — always keep at least N backups (default: 3)
```

**Cleanup runs after each new backup**, iterating oldest-to-newest:
1. If `backupCount > MaxCount` → compact + prune oldest
2. If `backup.Timestamp + MaxAge < now` → compact + prune oldest
3. If `TotalDirectorySize > MaxTotalSize AND backupCount > MinKeep` → compact + prune oldest
4. If chain depth exceeds `CompactThreshold` → trigger compaction
5. Stop when all limits are satisfied

**Invariants:**
- The chain base (compacted or initial full `.pack`) is **never** deleted without first compacting
- `MinKeep` prevents volume spikes from purging all history
- At least one of MaxCount/MaxAge/MaxTotalSize must be non-zero
- Pruning mid-chain requires orphan promotion (pages only in the deleted file must be copied forward)

## Alternatives Considered

1. **Tiered thinning (Time Machine style)** — Keep all for 24h, thin to 1/day for 30d, thin to 1/week for older. Rich history but complex configuration (6+ parameters), thinning logic must pick "best representative" for each period, and doesn't directly control disk usage. Overkill for an embedded database engine.

2. **Volume-only with minimum count** — Primary control is `MaxTotalSize`, with `MinKeep` guarantee. Directly controls disk but provides unpredictable history depth — depends on delta sizes which vary with workload. Large databases may never fit their full snapshot within budget. Users can't answer "how many days of history do I have?" without knowing delta sizes.

3. **Simple count-only** — Just `MaxCount=30`. Trivial to implement but penalizes high-frequency backers (30 hourly snapshots = 30h, not 30 days). No storage control.

4. **Age-only** — Just `MaxAge=7d`. Simple but no volume control — 168 hourly deltas for a 50GB database could be 30+ GB of deltas.

5. **Application-layer responsibility** — Don't provide retention at all; let the caller manage files. Breaks the "batteries included" principle and risks users forgetting cleanup, filling disks.

## Consequences

**Positive:**
- Only 3-4 knobs to configure (most users set MaxAge + CompactThreshold, optionally MaxCount/MaxTotalSize)
- Predictable: "I'll always have at least 7 days" or "at most 30 backups"
- Frequency-independent: same config works for hourly and daily backups
- `MinKeep` provides safety net against volume spikes
- `CompactThreshold` automatically bounds chain depth and restore time
- Works identically regardless of database size or delta sizes

**Negative:**
- No "thin old history" capability — either a backup exists or it doesn't
- Users who back up every 10 minutes and want 30-day history need MaxCount=4320 (many files)
- Count and Age can feel redundant for users with regular schedules (both achieve same thing)
- Volume cap is a blunt instrument — large backup spikes can cause older history to be trimmed unexpectedly (mitigated by MinKeep)
- Pruning mid-chain is more complex than the original reverse-delta design (requires orphan page promotion); compaction-then-prune is the recommended pattern

**Cross-references:**
- [ADR-028](028-cow-snapshot-backup.md) — CoW snapshot mechanism (enhanced with scoped CoW)
- [07-backup.md](../overview/07-backup.md) §7.7 — Compaction & Pruning design
- [PIT Backup Design — Part 5](../design/pit-backup/05-compaction-pruning.md) — Detailed compaction and retention spec

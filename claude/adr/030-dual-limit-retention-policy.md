# ADR-030: Dual-Limit Retention Policy for Backup Snapshots

**Status**: Accepted
**Date**: 2025-01 (design session)
**Deciders**: Developer + Claude (design session)

## Context

Typhon's backup system creates reverse-delta incremental snapshots (see [ADR-029](029-reverse-delta-incremental-snapshots.md)). Over time, reverse-delta files accumulate and must be cleaned up. The retention policy must:

- Support wildly different backup frequencies (every 10 minutes to once a day)
- Give users direct control over storage consumption
- Be simple to configure and reason about
- Leverage reverse deltas' property: oldest deltas can always be deleted freely (no cascade)

## Decision

Use a **dual-limit** retention strategy with three configurable thresholds — **any** exceeded limit triggers cleanup (most restrictive wins):

```
MaxCount:     int       — max reverse deltas to keep (0 = unlimited)
MaxAge:       TimeSpan  — delete deltas older than this (Zero = unlimited)
MaxTotalSize: long      — total backup directory size in bytes (0 = unlimited)
MinKeep:      int       — always keep at least N deltas (default: 1)
```

**Cleanup runs after each new snapshot**, iterating oldest-to-newest:
1. If `deltaCount > MaxCount` → delete oldest
2. If `delta.Timestamp + MaxAge < now` → delete oldest
3. If `TotalDirectorySize > MaxTotalSize AND deltaCount > MinKeep` → delete oldest
4. Stop when all limits are satisfied

**Invariants:**
- The full snapshot (`.typhon-snap`) is **never** deleted by retention
- `MinKeep` prevents volume spikes from purging all history
- At least one of MaxCount/MaxAge/MaxTotalSize must be non-zero

## Alternatives Considered

1. **Tiered thinning (Time Machine style)** — Keep all for 24h, thin to 1/day for 30d, thin to 1/week for older. Rich history but complex configuration (6+ parameters), thinning logic must pick "best representative" for each period, and doesn't directly control disk usage. Overkill for an embedded database engine.

2. **Volume-only with minimum count** — Primary control is `MaxTotalSize`, with `MinKeep` guarantee. Directly controls disk but provides unpredictable history depth — depends on delta sizes which vary with workload. Large databases may never fit their full snapshot within budget. Users can't answer "how many days of history do I have?" without knowing delta sizes.

3. **Simple count-only** — Just `MaxCount=30`. Trivial to implement but penalizes high-frequency backers (30 hourly snapshots = 30h, not 30 days). No storage control.

4. **Age-only** — Just `MaxAge=7d`. Simple but no volume control — 168 hourly deltas for a 50GB database could be 30+ GB of deltas.

5. **Application-layer responsibility** — Don't provide retention at all; let the caller manage files. Breaks the "batteries included" principle and risks users forgetting cleanup, filling disks.

## Consequences

**Positive:**
- Only 2-3 knobs to configure (most users set MaxCount + MaxAge, optionally MaxTotalSize)
- Predictable: "I'll always have at least 7 days" or "at most 30 snapshots"
- Frequency-independent: same config works for hourly and daily backups
- `MinKeep` provides safety net against volume spikes
- `PreviewRetention` API lets users verify before tightening policy
- Deletion is always from oldest — trivial implementation, no chain rebuild
- Works identically regardless of database size or delta sizes

**Negative:**
- No "thin old history" capability — either a delta exists or it doesn't
- Users who back up every 10 minutes and want 30-day history need MaxCount=4320 (many files)
- Count and Age can feel redundant for users with regular schedules (both achieve same thing)
- Volume cap is a blunt instrument — large delta spikes can cause older history to be trimmed unexpectedly (mitigated by MinKeep)

**Cross-references:**
- [ADR-029](029-reverse-delta-incremental-snapshots.md) — Reverse deltas: deletion is free (no cascade)
- [ADR-028](028-cow-snapshot-backup.md) — CoW snapshot mechanism
- [07-backup.md](../overview/07-backup.md) §7.6 — Retention Policy design

# ADR-014: No Point-in-Time Recovery (WAL Recycled After Checkpoint)

**Status**: Accepted
**Date**: 2025-01 (inferred from conversation history)
**Deciders**: Developer + Claude (design session)

## Context

Point-in-Time Recovery (PITR) allows restoring a database to any arbitrary timestamp by maintaining a continuous WAL archive. This requires:

1. Never deleting WAL segments (or archiving them to separate storage)
2. Continuous WAL streaming infrastructure
3. Large storage for WAL archives (grows indefinitely)

Typhon is an embedded game/simulation database. Its primary use case is:
- Game servers that restart cleanly or recover from the last checkpoint
- Simulation workloads that can re-run from a known state
- Environments where application-level snapshots (save files) provide recovery points

## Decision

**WAL segments are recycled (deleted/reused) after each successful checkpoint.** There is no WAL archive and no PITR capability.

Recovery restores to the most recent checkpoint-consistent state:
```
Checkpoint (T=0) → ... → WAL records → Crash
                                          ↓
Recovery: Restore checkpoint + replay WAL since checkpoint
          (WAL before checkpoint already recycled)
```

Backup-based restore is checkpoint-consistent (no WAL replay needed for backups).

## Alternatives Considered

1. **Full PITR with WAL archiving** — Powerful but adds: WAL archive storage (grows indefinitely), streaming infrastructure, complex restore tooling. Overkill for embedded game databases.
2. **Retain last N WAL segments** — Limited PITR window. Adds complexity for unclear benefit in game workloads.
3. **WAL streaming to replica** — Useful for read replicas, but Typhon is embedded (single process). Deferred to future if needed.

## Consequences

**Positive:**
- Simple WAL lifecycle: segments recycled after checkpoint, bounded disk usage
- No WAL archive infrastructure needed
- Faster checkpoints (no need to wait for archive confirmation)
- Predictable disk usage: max WAL size = segments × segment_size (default 4 × 64MB = 256MB)

**Negative:**
- Cannot restore to arbitrary point in time
- Data loss window = time since last backup (not since last WAL record)
- No streaming replication capability (future enhancement possible)
- Backup is the only disaster recovery mechanism

**Future option:** WAL archiving can be added later without changing the core WAL format — the architecture supports streaming (records are self-contained with LSNs).

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.9 — Design decisions
- [07-backup.md](../overview/07-backup.md) §7.3 — Checkpoint-consistent restore
- [09-resources.md](../overview/09-resources.md) — WAL budget (4 × 64MB segments)

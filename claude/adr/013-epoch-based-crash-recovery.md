# ADR-013: Epoch-Based Crash Recovery

**Status**: Accepted
**Date**: 2025-01 (inferred from conversation history)
**Deciders**: Developer + Claude (design session)

## Context

On crash, Typhon must determine which in-memory changes were committed (and should be preserved) vs which were uncommitted (and should be rolled back). Traditional approaches:

- **WAL-only recovery**: Replay all WAL records since last checkpoint. Correct but slow for large WAL.
- **Undo logging**: Log before-images, undo uncommitted on recovery. Complex and doubles I/O.

Typhon's UoW model provides a natural grouping: each UoW has a set of transactions that either all flushed or didn't.

## Decision

Use **epoch-based crash recovery** with a persistent UoW Registry:

1. Each UoW is assigned a unique `ushort Epoch` (2 bytes) from a registry
2. Every revision element is stamped with its owning UoW's epoch at write time
3. The registry segment tracks epoch lifecycle: `Active → Flushed → Recycled`
4. On crash recovery:
   - Scan registry: identify epochs that were `Active` (never flushed) at crash time
   - Mark those epochs as `Voided`
   - During normal reads, revisions with voided epochs are invisible (as if rolled back)
   - WAL replay restores any committed-but-not-yet-checkpointed data

**Epoch stamping happens in ComponentTable** at write time — the CT is UoW-aware and receives the epoch from the transaction.

## Alternatives Considered

1. **Full WAL replay only** — Correct but recovery time proportional to WAL size. No instant initialization.
2. **Undo logging** — Write before-images to undo uncommitted changes. Doubles write I/O during normal operation.
3. **Transaction-level tracking** — Stamp each revision with TxId, maintain committed-tx bitmap. More granular but larger metadata (4 bytes per revision vs 2).
4. **Checkpoint-only (no WAL)** — Lose all work since last checkpoint on crash. Unacceptable for Immediate mode.

## Consequences

**Positive:**
- O(1) recovery initialization (just scan registry, mark voided epochs)
- Minimal per-revision overhead (2 bytes for epoch vs 4+ for transaction ID)
- No undo I/O during normal operation
- Natural integration with UoW lifecycle
- 65536 concurrent epochs sufficient (recycled after GC)

**Negative:**
- Recovery granularity is UoW-level (cannot partially recover a UoW)
- Epoch space limited to 65536 concurrent UoWs (sufficient for embedded use)
- Registry segment requires its own fsync lifecycle
- Voided epochs must be cleaned up during subsequent operations (lazy scan)

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.7 — Recovery algorithm
- [02-execution.md](../overview/02-execution.md) §2.1 — UoW epoch allocation
- [04-data.md](../overview/04-data.md) §4.5 — Epoch in revision element (12 bytes)
- [ideas/uow-crash-recovery/](../ideas/uow-crash-recovery/) — Original exploration

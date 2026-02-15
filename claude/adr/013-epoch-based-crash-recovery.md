# ADR-013: UoW ID-Based Crash Recovery

**Status**: Accepted (updated 2026-02 — terminology and state machine aligned with current design)
**Date**: 2025-01 (inferred from conversation history)
**Updated**: 2026-02-15 — Renamed "epoch" → "UoW ID", updated state machine, aligned with WAL-based registry
**Deciders**: Developer + Claude (design session)

> **Terminology note:** This ADR was originally titled "Epoch-Based Crash Recovery" and used "epoch" throughout. The term was renamed to **UoW ID** to avoid ambiguity with `EpochManager.GlobalEpoch` (a separate 64-bit EBRM counter for page cache eviction safety). See the [UoW design doc](../design/unit-of-work.md) terminology note.

## Context

On crash, Typhon must determine which in-memory changes were committed (and should be preserved) vs which were uncommitted (and should be rolled back). Traditional approaches:

- **WAL-only recovery**: Replay all WAL records since last checkpoint. Correct but slow for large WAL.
- **Undo logging**: Log before-images, undo uncommitted on recovery. Complex and doubles I/O.

Typhon's UoW model provides a natural grouping: each UoW has a set of transactions that either all flushed or didn't.

## Decision

Use **UoW ID-based crash recovery** with a WAL-backed UoW Registry:

1. Each UoW is assigned a unique **15-bit UoW ID** (max 32,767) from a registry. Bit 15 of the 2-byte `_packedUowId` field is reserved for the IsolationFlag.
2. Every revision element is stamped with its owning UoW's ID at commit time
3. The registry tracks UoW lifecycle: `Free → Pending → WalDurable → Committed → Free` (normal), or `Pending → Void → Free` (crash recovery)
4. On crash recovery:
   - Load registry checkpoint from ManagedMMF, replay WAL delta since last checkpoint
   - Identify UoW IDs that were `Pending` at crash time → mark `Void`
   - Set `CommittedBeforeTSN = 0` → activates committed bitmap as visibility filter
   - WAL replay restores any WalDurable-but-not-yet-checkpointed data
   - Ghost revisions (from voided UoWs physically on disk) are invisible via the committed bitmap

**UoW ID stamping happens in `CompRevStorageElement._packedUowId`** at commit time — the transaction stamps each pending revision with `OwningUnitOfWork.UowId`.

## Alternatives Considered

1. **Full WAL replay only** — Correct but recovery time proportional to WAL size. No instant initialization.
2. **Undo logging** — Write before-images to undo uncommitted changes. Doubles write I/O during normal operation.
3. **Transaction-level tracking** — Stamp each revision with TxId, maintain committed-tx bitmap. More granular but larger metadata (4 bytes per revision vs 2).
4. **Checkpoint-only (no WAL)** — Lose all work since last checkpoint on crash. Unacceptable for Immediate mode.

## Consequences

**Positive:**
- O(registry checkpoint + WAL delta) recovery initialization — registry scan + replay, typically 15-60ms
- Minimal per-revision overhead (2 bytes for UoW ID + IsolationFlag)
- No undo I/O during normal operation
- Natural integration with UoW lifecycle
- 32,767 concurrent UoW IDs sufficient (recycled after GC)
- Two-tier visibility: `CommittedBeforeTSN` fast path during normal operation (zero bitmap overhead), bitmap fallback only post-crash

**Negative:**
- Recovery granularity is UoW-level (cannot partially recover a UoW)
- UoW ID space limited to 32,767 concurrent UoWs (sufficient for embedded use)
- Voided UoW ghost revisions must be cleaned up by `DeferredCleanupManager` as `MinTSN` advances
- Post-crash visibility uses committed bitmap (~5-10 cycles per read) until void entries are GC'd

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.7 — Recovery algorithm
- [06-durability.md](../overview/06-durability.md) §6.4 — UoW Registry (WAL-based, checkpoint cache)
- [06-durability.md](../overview/06-durability.md) §6.8 — Visibility & Isolation (CommittedBeforeTSN + bitmap)
- [02-execution.md](../overview/02-execution.md) §2.1 — UoW ID allocation and lifecycle
- [design/unit-of-work.md](../design/unit-of-work.md) §5 — UoW ID stamping in CompRevStorageElement
- [design/unit-of-work.md](../design/unit-of-work.md) §6 — UoW Registry detailed design
- [ideas/uow-crash-recovery/](../ideas/uow-crash-recovery/) — Original exploration

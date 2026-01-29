# ADR-025: Checkpoint Manager as Sole Data Page fsync Owner

**Status**: Accepted
**Date**: 2025-01 (inferred from conversation history)
**Deciders**: Developer + Claude (design session)

## Context

Dirty pages in the buffer pool must eventually be written to their final on-disk locations. Multiple threads could potentially flush pages:
- Transaction commit could flush its own dirty pages
- Background thread could flush pages on a schedule
- Page eviction could write back dirty pages

If multiple code paths call fsync on data files, reasoning about durability becomes complex: "which pages are guaranteed on disk at any point?" The answer depends on race conditions between flushers.

## Decision

The **Checkpoint Manager** is the sole owner of data page fsync. No other component calls fsync on data files:

```
WAL Writer:         Owns WAL file fsync (FUA writes)
Checkpoint Manager: Owns data file fsync (periodic dirty page persistence)
Page Eviction:      May write back dirty pages (write, but NOT fsync)
Transaction Commit: Never touches data files directly
```

Checkpoint sequence:
1. Mark dirty pages for this checkpoint
2. Write all marked pages to disk (writeback, no fsync yet)
3. fsync data file(s)
4. Record checkpoint LSN
5. Recycle WAL segments before checkpoint LSN

## Alternatives Considered

1. **Per-transaction data page flush** — Each commit fsyncs its dirty pages. Extremely expensive (fsync per transaction), and ordering issues with concurrent transactions.
2. **Page eviction with fsync** — Eviction writes back and fsyncs. Unpredictable fsync timing tied to cache pressure, not durability requirements.
3. **Multiple checkpoint workers** — Parallel fsync of different file regions. Complex coordination; diminishing returns on NVMe (already parallel internally).
4. **No explicit fsync (rely on OS writeback)** — Unpredictable durability window. On crash, may lose much more than expected.

## Consequences

**Positive:**
- Single point of truth for "what's durable on disk"
- Predictable durability: everything before checkpoint LSN is on disk
- WAL recycling is safe: only recycle segments < checkpoint LSN
- Simple to reason about: checkpoint = consistent point
- Recovery only needs to replay WAL since last checkpoint

**Negative:**
- Dirty pages may stay dirty longer than strictly necessary (until next checkpoint)
- Checkpoint can be expensive if many pages are dirty (mitigated by configurable interval)
- Single fsync point = potential latency spike during checkpoint (mitigated by async writeback before fsync)
- If checkpoint fails, WAL continues growing (bounded by max segment count → forced checkpoint)

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.6 — Checkpoint pipeline
- [02-execution.md](../overview/02-execution.md) §2.7 — Checkpoint Manager as background worker
- [ADR-014](014-no-point-in-time-recovery.md) — WAL recycled after checkpoint

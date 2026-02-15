# ADR-012: Lock-Free MPSC Ring Buffer for WAL Serialization

**Status**: Accepted
**Date**: 2025-01 (inferred from WAL design and Aeron research)
**Deciders**: Developer + Claude (design session)

## Context

Multiple transaction threads need to serialize WAL records to a buffer that a single writer thread drains to disk. Requirements:

1. Zero allocations in steady state (hot path)
2. Minimal contention between producers (game servers have many threads)
3. Contiguous allocation (each producer gets a single region to write into)
4. No CAS retry loops under contention (predictable latency)

## Decision

Use an **Aeron-inspired lock-free MPSC (Multi-Producer, Single-Consumer) linear buffer** with atomic tail increment:

```
Producer (tx.Commit):
  1. size = CalculateRecordSize()
  2. offset = Interlocked.Add(ref _tail, size) - size  // Atomic XADD
  3. WriteRecord(buffer + offset)                       // Write to claimed region
  4. MarkComplete(offset)                               // Publish

Consumer (WAL Writer Thread):
  1. Scan from _head for contiguous completed records
  2. Write contiguous batch to disk (FUA)
  3. Advance _head
```

**Key property:** `Interlocked.Add` (LOCK XADD on x86) gives each producer a unique, non-overlapping buffer region in a single atomic operation — no CAS retry loop needed.

**Double-buffering:** When the active buffer fills, swap to a second buffer (ping-pong). Writer drains the full buffer while producers write to the fresh one.

## Alternatives Considered

1. **LMAX Disruptor ring buffer** — Fixed-slot, not variable-size. Would waste space for small records and can't handle large FPIs.
2. **Lock-based queue** — Simple but creates contention point. Under 16 threads, lock acquisition becomes bottleneck.
3. **Per-thread buffers with merge** — No contention, but complex ordering and variable merge latency.
4. **CAS-based linked list** — Allows variable sizes, but pointer chasing is cache-unfriendly and CAS retries unpredictable.
5. **Traditional circular ring buffer** — Wrap-around logic complex with variable-size records; potential split across boundary.

## Consequences

**Positive:**
- Zero contention between producers (each gets unique region atomically)
- Zero allocations (buffer pre-allocated, reused via double-buffering)
- Predictable latency (~100–500ns for claim + write)
- Sequential memory writes (cache-friendly for producer)
- Sequential memory reads (cache-friendly for consumer)

**Negative:**
- Fixed buffer size (4MB default); must handle "buffer full" back-pressure
- Double-buffering adds complexity (swap coordination)
- Late publishers can hold up the consumer (must wait for all records in region to be marked complete)
- Memory overhead: 2 × 4MB buffers always allocated

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.1 — Ring buffer in commit path (authoritative WAL record format)
- [ADR-020](020-dedicated-wal-writer-thread.md) — Consumer thread design

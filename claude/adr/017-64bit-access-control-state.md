# ADR-017: 64-Bit Atomic State for AccessControl

**Status**: Accepted
**Date**: 2024-09 (inferred from implementation)
**Deciders**: Developer

## Context

The general-purpose AccessControl (reader-writer lock for pages, indexes, etc.) needs to track:
- Shared reader count
- Shared waiters count
- Exclusive waiters count
- Promoter waiters count (shared→exclusive upgrade)
- Current state (Idle/Shared/Exclusive)
- Telemetry: which operation block holds the lock

All state changes must be atomic (no torn reads/writes). Options:
- Multiple fields with separate locks (complex, multiple atomics)
- Single wide atomic word encoding all state (one CAS per operation)

## Decision

Encode all lock state in a **single 64-bit atomic value**:

```
Bits 0–7:   Shared usage counter (0–255 concurrent readers)
Bits 8–15:  Shared waiters counter
Bits 16–23: Exclusive waiters counter
Bits 24–31: Promoter waiters counter
Bits 32–51: Operation block ID (20 bits, telemetry)
Bits 52–61: Exclusive thread ID (10 bits)
Bits 62–63: State (Idle=0, Shared=1, Exclusive=2)
```

All transitions via `Interlocked.CompareExchange(ref long)`. Shared acquisition is an `Interlocked.Add` (no CAS retry for the common read path).

## Alternatives Considered

1. **32-bit state** — Not enough bits for all counters + telemetry. Would need separate telemetry tracking.
2. **Separate fields with SpinLock** — Simpler per-field access, but SpinLock on every read acquisition is a bottleneck.
3. **128-bit (two 64-bit words)** — More room, but no atomic 128-bit CAS on x86 without `CMPXCHG16B` (not universally available, and even when available, expensive).
4. **CLR Monitor (lock statement)** — Allocates a sync block per object; not acceptable for millions of page-level locks.

## Consequences

**Positive:**
- Single atomic operation for all state changes (predictable latency)
- Shared path uses `Interlocked.Add` (no CAS retry loop for readers)
- 8 bytes total per lock instance (excellent memory density for per-page locks)
- Telemetry embedded in lock word (no separate allocation for contention tracking)
- No heap allocation for lock object (can be inline struct field)

**Negative:**
- Maximum 255 concurrent readers (sufficient for embedded engine)
- 10-bit thread ID truncation (relies on low thread count in embedded scenario)
- Complex bit manipulation for every operation
- 20-bit operation block ID limits to ~1M distinct telemetry blocks

**Cross-references:**
- [01-concurrency.md](../overview/01-concurrency.md) — AccessControl overview
- `src/Typhon.Engine/Misc/AccessControl/AccessControl.cs` — Implementation
- [ADR-019](019-runtime-telemetry-toggle.md) — Telemetry integration

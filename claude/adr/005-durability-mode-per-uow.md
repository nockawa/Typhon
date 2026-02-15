# ADR-005: Durability Mode Per Unit of Work

**Status**: Accepted
**Date**: 2025-01 (inferred from conversation history)
**Deciders**: Developer + Claude (design session)

## Context

Different workloads within the same game server have vastly different durability requirements:

- **Game tick (physics, AI)**: Can tolerate losing last tick on crash (~16ms of work). Needs minimal commit latency.
- **Player trades / purchases**: Must be durable immediately. Players will dispute lost transactions.
- **General server operations**: Can tolerate ~5–10ms window of potential loss for throughput gains.

A single global durability setting forces all workloads to the most conservative (slow) option.

## Decision

Durability mode is specified **per Unit of Work**, with optional **per-transaction override** (escalation only):

```csharp
public enum DurabilityMode
{
    Deferred,     // Flush only on explicit Flush()/FlushAsync()
    GroupCommit,  // WAL writer auto-flushes every N ms (default 5ms)
    Immediate     // FUA on every tx.Commit()
}

public enum DurabilityOverride
{
    Default,    // Use UoW's DurabilityMode
    Immediate   // Force FUA for this specific commit (escalation only)
}
```

- **Deferred**: ~1µs commit, explicit flush at UoW boundary. May lose unflushed work on crash.
- **GroupCommit**: ~1µs commit + ≤5ms implicit flush (default interval). Amortizes FUA across N transactions.
- **Immediate**: ~15–85µs commit. Zero data loss window.
- **DurabilityOverride**: Allows a single critical transaction within a Deferred UoW to escalate to Immediate.

Override can only **escalate** (never downgrade). This prevents accidental data loss.

## Alternatives Considered

1. **Per-database durability** — Too coarse. Game ticks and trades on same DB need different modes.
2. **Per-transaction durability** — Can't batch (GroupCommit inherently multi-transaction). UoW is the natural boundary.
3. **Two modes only (Sync/Async)** — Misses the GroupCommit sweet spot (batched amortization).
4. **Caller-managed flush** — Error-prone. Developers forget to flush. GroupCommit automates the common case.

## Consequences

**Positive:**
- Optimal latency for each workload type
- GroupCommit achieves ~1M+ effective tx/sec with durability
- Critical operations can escalate without changing the overall UoW mode
- Clear crash semantics: unflushed Deferred work is explicitly volatile

**Negative:**
- More complex mental model than "everything is durable"
- Developers must understand crash implications of each mode
- GroupCommit interval is a tuning parameter (too short = many flushes, too long = larger loss window)

**Cross-references:**
- [02-execution.md](../overview/02-execution.md) §2.3 — Durability modes detailed
- [06-durability.md](../overview/06-durability.md) §6.5 — Durability modes implementation details
- [ADR-001](001-three-tier-api-hierarchy.md) — UoW as durability boundary

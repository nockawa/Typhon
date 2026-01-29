# ADR-003: MVCC Snapshot Isolation with Optimistic Concurrency

**Status**: Accepted
**Date**: 2024-01 (project inception)
**Deciders**: Developer

## Context

Typhon requires concurrent access from multiple threads (game server processing physics, AI, player actions simultaneously). The concurrency control mechanism must:

1. Never block readers (queries dominate workload, ~70%)
2. Support multiple concurrent writers without excessive locking
3. Detect conflicts at commit time (not during execution)
4. Provide repeatable reads within a transaction

## Decision

Implement Multi-Version Concurrency Control (MVCC) with snapshot isolation and optimistic conflict detection:

- **Snapshot isolation**: Each transaction sees the database as of its creation timestamp (tick)
- **Optimistic locking**: No locks during execution; conflict detection only at commit
- **Revision chains**: Each component update creates a new immutable revision
- **IsolationFlag**: New revisions invisible to other transactions until committed
- **Read-your-own-writes**: Transactions see their uncommitted changes via local cache
- **Conflict resolution**: Default "last write wins" with optional custom `ConcurrencyConflictHandler`

## Alternatives Considered

1. **Pessimistic locking (2PL)** — Blocks readers on writes; unacceptable for real-time workloads where 16ms tick budgets are common.
2. **Serializable isolation** — Stronger guarantee but more aborts; snapshot isolation sufficient for game workloads.
3. **Lock-free CRDT** — No conflicts by design, but limited operation types; doesn't support arbitrary component mutations.
4. **Single-threaded (event loop)** — Simplest, but wastes multi-core hardware.

## Consequences

**Positive:**
- Readers never block (critical for real-time)
- High throughput under contention (conflicts rare in practice for game workloads)
- Simple mental model: "you see a frozen snapshot"
- Enables efficient GC (revisions older than oldest active transaction are safe to collect)

**Negative:**
- Write-write conflicts possible (must retry or resolve)
- Memory overhead from multiple revisions per component
- Requires garbage collection of old revisions (see CompRevDeferredCleanup)
- Phantom reads possible (snapshot isolation weaker than serializable)

**Cross-references:**
- [04-data.md](../overview/04-data.md) §4.5 — MVCC implementation
- [design/CompRevDeferredCleanup.md](../design/CompRevDeferredCleanup.md) — Revision GC strategy

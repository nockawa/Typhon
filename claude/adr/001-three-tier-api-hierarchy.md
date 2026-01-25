# ADR-001: Three-Tier API Hierarchy (DatabaseEngine → UoW → Transaction)

**Status**: Accepted
**Date**: 2025-01 (inferred from conversation history)
**Deciders**: Developer + Claude (design session)

## Context

Typhon initially exposed `DatabaseEngine.CreateTransaction()` as a direct API, allowing transactions without explicit durability context. This made it impossible to:

1. Control durability at a granular level (per-workload, not per-database)
2. Batch multiple transactions into a single durable flush
3. Know which transactions belong to the same logical operation

The durability model (Deferred/GroupCommit/Immediate) requires a boundary that groups transactions and owns their flush lifecycle.

## Decision

Adopt a strict three-tier API hierarchy:

```
DatabaseEngine (setup & schema)
    └── UnitOfWork (durability boundary, lifetime scope)
            └── Transaction (MVCC operations, conflict resolution)
```

- **DatabaseEngine**: `RegisterComponent<T>()`, `CreateUnitOfWork(...)`, `GeneratePrimaryKey()`
- **UnitOfWork**: Owns durability mode, epoch, deadline; creates transactions; controls flush
- **Transaction**: `CreateEntity()`, `ReadEntity()`, `UpdateEntity()`, `DeleteEntity()`, `Commit()`

`DatabaseEngine.CreateTransaction()` is **removed entirely**. All work flows through a UoW.

## Alternatives Considered

1. **Keep direct `CreateTransaction()`** with default durability — Simpler API, but hides durability semantics. Users wouldn't understand crash behavior.
2. **Transaction-level durability mode** — Each tx specifies its own mode. Rejected because batching (Deferred/GroupCommit) is inherently multi-transaction.
3. **Two-tier (DatabaseEngine → Transaction)** with durability on DB config — Too coarse. Different workloads on the same database need different modes.

## Consequences

**Positive:**
- Forces developers to make explicit durability decisions
- Enables GroupCommit (batch N transactions, one FUA)
- Epoch-based crash recovery naturally falls out (epoch = UoW boundary)
- Clean separation: schema setup vs runtime operations

**Negative:**
- More verbose API for simple use cases (must create UoW even for single transaction)
- Breaking change from initial API design
- Slightly higher learning curve for new users

**Cross-references:**
- [02-execution.md](../overview/02-execution.md) §2.1 — UoW lifecycle
- [04-data.md](../overview/04-data.md) §4.1 — API hierarchy
- [06-durability.md](../overview/06-durability.md) §6.3 — Durability modes

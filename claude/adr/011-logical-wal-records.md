# ADR-011: Logical WAL Records (Not Physical Pages)

**Status**: Accepted
**Date**: 2025-01 (inferred from WAL design)
**Deciders**: Developer + Claude (design session)

## Context

Write-Ahead Logs can record changes at different granularities:

- **Physical (page-level)**: Log entire modified pages or page diffs. Simple recovery (just apply pages) but large records.
- **Logical (operation-level)**: Log the semantic operation (create/update/delete component). Smaller records but more complex replay.

Typhon's ECS model means most transactions modify one or a few small components (32–256 bytes each). Logging entire 8KB pages for a 64-byte component update wastes 99% of WAL bandwidth.

## Decision

Use **logical WAL records** that capture component-level operations:

```
WAL Record (32B header + variable payload):
  - Header: LSN (8B), TransactionTSN (8B), UowId (2B), ComponentTypeId (2B),
            EntityId (4B), PayloadLength (2B), OperationType (1B), Flags (1B), Checksum (4B)
  - Payload: Component data (32–256 bytes typical)
```

Typical record sizes:
- Component Create: 32B header + 32–256B component = **64–288B total**
- Component Update: 32B header + 32–256B component = **64–288B total**
- Component Delete: 32B header + 4B entity ID = **36B total**

Compare with physical page logging: **8224B per modified page** (header + 8192B page image).

## Alternatives Considered

1. **Physical page-level WAL** (PostgreSQL model) — Simpler recovery (apply pages), but 10–50× more I/O for typical ECS workloads.
2. **Physiological WAL** (page + offset + operation) — Middle ground used by some databases, but complex and still page-sized for torn page repair.
3. **Operation log + full-page images** — This is what we actually do: logical records + FPI for torn page repair. Best of both worlds.

## Consequences

**Positive:**
- 10–50× smaller WAL records for typical component operations
- Higher effective throughput (more transactions per WAL segment)
- Smaller WAL segments = faster checkpoint rotation
- Natural fit for ECS model (operations are component-level)

**Negative:**
- Recovery is more complex (must replay logical operations, not just copy pages)
- Cannot handle torn pages with logical records alone (requires Full-Page Images, see ADR-024)
- Logical replay must handle schema changes carefully
- Recovery time proportional to WAL length × replay cost (not just copy cost)

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.1 — WAL record format (authoritative 32B header struct)
- [ADR-024](024-fpi-over-double-write-buffer.md) — Torn page repair via FPI

# ADR-002: ECS Data Model with Blittable Components

**Status**: Accepted
**Date**: 2024-01 (project inception)
**Deciders**: Developer

## Context

Typhon targets game/simulation workloads where entities have diverse, evolving sets of properties. Traditional relational schemas (fixed tables with columns) are a poor fit because:

1. Entities gain/lose capabilities at runtime (e.g., a player gains an inventory)
2. Different entity types share some components but not others
3. Game loops process components in bulk (physics, AI, rendering) — favoring data-oriented access patterns

## Decision

Adopt an Entity-Component-System (ECS) data model:

- **Entity**: A unique identifier (`long` primary key), no inherent structure
- **Component**: An unmanaged blittable struct marked with `[Component]` attribute
- **ComponentTable**: One table per component type, stores all instances across all entities
- **Multi-component entities**: Same entity ID can have components in multiple ComponentTables

Components must be:
- `unmanaged` (no managed references — no `string`, `object`, arrays)
- Fixed memory layout (blittable — enables zero-copy operations)
- Decorated with `[Component]` and `[Field]` attributes for schema metadata

## Alternatives Considered

1. **Relational model** (fixed schema, ALTER TABLE for changes) — Poor fit for dynamic entity composition; schema migrations expensive at runtime.
2. **Document model** (JSON/BSON per entity) — Variable-size documents break fixed-chunk allocation; poor cache locality for bulk processing.
3. **Column-family** (Cassandra-style wide rows) — Good for some ECS patterns but complex for ACID transactions.
4. **Traditional ECS (in-memory only)** — No persistence, no transactions, no durability guarantees.

## Consequences

**Positive:**
- Excellent data locality for bulk operations (all PlayerComponents contiguous in memory)
- Zero-copy reads (component is already in correct memory layout)
- Schema flexibility (add components to entities without migration)
- Natural fit for game server workloads
- Fixed-size chunks enable efficient allocation and caching

**Negative:**
- No variable-length fields inline (must use String64 or external storage)
- Joins across component types require multiple lookups
- Less intuitive for developers expecting relational semantics
- Components limited to ~8000 bytes (must fit in page raw data)

**Cross-references:**
- [04-data.md](../overview/04-data.md) §4.4 — Schema system
- [CLAUDE.md](../../CLAUDE.md) — Component definition examples

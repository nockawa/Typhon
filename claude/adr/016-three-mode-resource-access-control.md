# ADR-016: Three-Mode ResourceAccessControl (ACCESSING/MODIFY/DESTROY)

**Status**: Accepted
**Date**: 2025-01 (inferred from design document)
**Deciders**: Developer + Claude (design session)

## Context

Typhon's data structures (ChainedBlockAllocator chains, segment pages) need concurrent access control with semantics that don't fit traditional reader-writer locks:

- **Multiple threads traversing** a chain simultaneously (readers)
- **One thread appending** new blocks to the chain (writer)
- **One thread destroying** the entire structure (lifecycle end)

In a standard RWLock, the writer blocks all readers. But in Typhon, **appending to a chain doesn't invalidate active traversals** — iterators hold references to existing blocks that remain valid after append.

## Decision

Create a **three-mode synchronization primitive** with compatibility matrix:

| | ACCESSING | MODIFY | DESTROY |
|---|:-:|:-:|:-:|
| **ACCESSING** | ✅ Compatible | ✅ Compatible | ❌ Exclusive |
| **MODIFY** | ✅ Compatible | ❌ Exclusive | ❌ Exclusive |
| **DESTROY** | ❌ Exclusive | ❌ Exclusive | ❌ Exclusive |

- **ACCESSING** (many): Prevents destruction. Multiple holders allowed. Compatible with MODIFY.
- **MODIFY** (one): Single holder. Appends/extends structure. Compatible with active ACCESSING.
- **DESTROY** (one, terminal): Fully exclusive. Waits for all ACCESSING and MODIFY to drain. Never released.

## Alternatives Considered

1. **Standard ReaderWriterLock** — Writers block readers. Would block traversals during append, causing unnecessary latency.
2. **ReaderWriterLockSlim** — Same issue: writers are exclusive with respect to readers.
3. **Lock-free append (no synchronization)** — Append is safe for existing iterators, but destruction would race with active traversals.
4. **Reference counting + disposal** — Complex lifetime management; doesn't prevent new acquisitions during teardown.

## Consequences

**Positive:**
- Traversals never blocked by appends (critical for read-heavy workloads)
- DESTROY cleanly drains all activity before teardown (no use-after-free)
- 32-bit state encoding: minimal memory footprint
- MODIFY_PENDING flag prevents ACCESSING starvation of modifiers

**Negative:**
- Non-standard primitive: developers must understand three-mode semantics
- DESTROY is terminal: primitive cannot be reused (requires new allocation)
- SpinWait contention possible if many threads compete for MODIFY
- Limited to 1023 concurrent ACCESSING holders (10-bit counter)

**Cross-references:**
- [01-concurrency.md](../overview/01-concurrency.md) — Synchronization primitives
- [design/ResourceAccessControl-Design.md](../design/ResourceAccessControl-Design.md) — Full specification
- `src/Typhon.Engine/Misc/AccessControl/` — Implementation

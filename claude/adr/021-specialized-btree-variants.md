# ADR-021: Specialized B+Tree Variants (No Runtime Generics)

**Status**: Accepted
**Date**: 2024-06 (inferred from implementation)
**Deciders**: Developer

## Context

Typhon's B+Tree indexes support multiple key types (16-bit, 32-bit, 64-bit integers, and String64). A generic `BTree<TKey>` would be natural in C#, but:

1. Generics over value types in .NET are JIT-specialized per type — but the JIT may not inline as aggressively as hand-specialized code
2. Node layout must be exactly 64 bytes for cache-line alignment
3. Different key sizes pack different numbers of keys per node
4. The hot path (lookup, insert) benefits from compile-time-known sizes

## Decision

Implement **separate, specialized B+Tree classes** for each key size:

- `L16BTree` — 16-bit keys
- `L32BTree` — 32-bit keys
- `L64BTree` — 64-bit keys
- `String64BTree` — String64 keys (8 bytes)

Each class is tuned for:
- Exact key count per 64-byte node
- Optimal comparison operations for that key type
- Cache-line-aligned node structure

All support `AllowMultiple` flag (unique vs non-unique indexes) and use AccessControl for thread safety.

## Alternatives Considered

1. **Generic `BTree<TKey>`** — Less code duplication, but JIT may not optimize node layout as tightly. Generic constraints can't express "exactly 64 bytes per node."
2. **Single B+Tree with variable-size keys** — Flexible, but variable node sizes break cache-line alignment and complicate SIMD operations.
3. **External index library** — Existing libraries don't integrate with Typhon's ChunkBasedSegment storage or AccessControl synchronization.
4. **Hash indexes** — O(1) point lookup, but no range queries or ordered iteration.

## Consequences

**Positive:**
- Each variant is exactly 64-byte node aligned (one cache line per node)
- JIT can fully inline key comparisons (no virtual dispatch)
- Optimal key packing per node (more keys = shallower tree = fewer cache misses)
- No generic type metadata overhead

**Negative:**
- Code duplication across 4 classes (common logic repeated)
- Adding new key types requires new class (cannot just add type parameter)
- Maintenance burden: bug fixes must be applied to all variants
- No support for arbitrary key types without new specialization

**Cross-references:**
- [05-query.md](../overview/05-query.md) — Index usage in queries
- [04-data.md](../overview/04-data.md) §4.4 — Schema system (Index attribute)
- `src/Typhon.Engine/Database Engine/BPTree/` — Implementation

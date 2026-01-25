# ADR-010: SOA Layout with SIMD for ChunkAccessor

**Status**: Accepted
**Date**: 2024-09 (inferred from implementation)
**Deciders**: Developer

## Context

The ChunkAccessor is the most frequently called component in Typhon — every component read/write goes through it. It caches recently accessed chunk addresses to avoid repeated page lookups. Requirements:

1. Stack-allocated (no heap pressure)
2. Fast lookup: given a page index, find the cached chunk address
3. Minimal memory footprint (~1KB)
4. Called millions of times per game tick

A naive array-of-structs (AoS) layout would require checking each slot sequentially.

## Decision

Use a **hybrid SOA+AoS layout** with SIMD-accelerated search:

```csharp
// SOA: page indices packed for SIMD parallel comparison
private fixed int _pageIndices[16];     // 64 bytes — Vector256<int> searchable

// AoS: per-slot metadata co-located after SIMD identifies the slot
private SlotDataBuffer _slots;          // 256 bytes — 16 × 16-byte structs
```

**Three-tier lookup strategy:**
1. **MRU cache** (1 comparison): Most-recently-used slot, nearly instant for repeat access
2. **SIMD search** (Vector256): Compare target page index against all 16 cached indices simultaneously
3. **LRU eviction** (cache miss): Evict least-used slot, load new page

## Alternatives Considered

1. **Pure AoS (struct[] with linear scan)** — Simple but O(n) per lookup; 16 comparisons worst case.
2. **Hash table** — O(1) amortized but heap-allocated, poor cache behavior for 16 entries.
3. **Pure SOA (separate arrays)** — Good for SIMD, but after finding the slot, metadata access requires separate cache line load.
4. **Binary search on sorted indices** — O(log n) but requires maintaining sort order on every insert.

## Consequences

**Positive:**
- SIMD search checks 8 page indices in one CPU instruction (Vector256<int>)
- MRU fast-path handles the common case (same chunk accessed repeatedly) in ~1ns
- Stack-allocated: zero heap pressure, destroyed with enclosing scope
- Hybrid layout: SIMD for search phase, AoS for data phase (both cache-friendly for their use case)
- 16 slots × 16-byte metadata = 256 bytes (fits in 4 cache lines)

**Negative:**
- Complex implementation (unsafe, fixed buffers, SIMD intrinsics)
- Fixed 16-slot capacity (cannot grow for workloads with wide access patterns)
- Requires hardware SIMD support (fallback scalar path needed for older CPUs)
- Stack size concern: ~1KB per accessor (limits nesting depth)

**Cross-references:**
- [CLAUDE.md](../../CLAUDE.md) — SOA optimization and SIMD requirements
- `src/Typhon.Engine/Persistence Layer/ChunkBasedSegment.cs` — ChunkAccessor usage
- [research/ChunkAccess.md](../research/ChunkAccess.md) — Design exploration
- [reference/StackChunkAccessor.md](../reference/StackChunkAccessor.md) — Implementation reference

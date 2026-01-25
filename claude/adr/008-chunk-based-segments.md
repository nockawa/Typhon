# ADR-008: ChunkBasedSegment with Three-Level Bitmap

**Status**: Accepted
**Date**: 2024-06 (inferred from implementation)
**Deciders**: Developer

## Context

Components, B+Tree nodes, and revision chains all need fixed-size allocation within pages. The allocation scheme must:

1. Find free slots in O(1) or near-O(1) time
2. Support concurrent allocation (multiple threads)
3. Track occupancy without per-slot metadata overhead
4. Scale to millions of chunks across thousands of pages

Linear scanning of a flat bitmap is O(n) where n = total chunks. For 100K pages × 125 chunks/page = 12.5M bits to scan.

## Decision

Use a **three-level hierarchical bitmap** (ConcurrentBitmapL3All) for occupancy tracking:

```
L2All (top summary)     — 1 bit per 64 L1 entries
L1All (mid summary)     — 1 bit per 64 L0 entries (all full?)
L1Any (mid summary)     — 1 bit per 64 L0 entries (any free?)
L0All (ground truth)    — 1 bit per chunk slot
```

**Key properties:**
- L0 is the ground truth (atomic CAS updates)
- L1/L2 are best-effort acceleration hints (temporal incoherence acceptable)
- Finding a free chunk: scan L2 → L1Any → L0 (typically 10–50× faster than linear)
- Fast division optimization: magic multiplier for chunks-per-page (~3–4 cycles vs ~20–80 for division)

## Alternatives Considered

1. **Flat bitmap + linear scan** — Simple but O(n) for finding free slots; unacceptable at scale.
2. **Free list (linked list of free chunks)** — O(1) allocation, but pointer chasing across pages (cache-unfriendly) and complex concurrent updates.
3. **Two-level bitmap** — Better than flat, but still O(√n) worst case for large datasets.
4. **Buddy allocator** — Good for power-of-2 sizes, but chunks are fixed-size (no fragmentation concern).

## Consequences

**Positive:**
- Near-O(1) allocation: L2 → L1 → L0 skip fully-occupied regions
- Lock-free L0 operations (Interlocked CAS on ground truth)
- L1/L2 incoherence is safe: worst case is a redundant scan (never misses a truly free slot)
- No heap allocations during allocation (everything is inline bitmaps)
- SIMD-friendly: L1Any/L1All can be checked with Vector256 operations

**Negative:**
- Complexity of three-level maintenance (resize, recount)
- L1/L2 can become stale (requires periodic recount for accurate stats)
- Memory overhead: ~3 bits per chunk slot (vs 1 bit for flat bitmap)
- CAS contention on hot L0 words under extreme allocation pressure

**Cross-references:**
- [03-storage.md](../overview/03-storage.md) §3.3 — Segment architecture
- `src/Typhon.Engine/Collections/ConcurrentBitmapL3All.cs` — Implementation
- `src/Typhon.Engine/Persistence Layer/ChunkBasedSegment.cs` — Consumer

# ADR-007: Clock-Sweep Page Cache Eviction

**Status**: Accepted
**Date**: 2024-06 (inferred from implementation)
**Deciders**: Developer

## Context

The page cache (default 256 pages = 2MB) needs an eviction policy when full. Requirements:
1. O(1) or near-O(1) eviction decision (hot path for every cache miss)
2. Approximates LRU behavior without per-access metadata updates
3. No per-page linked-list manipulation (expensive pointer chasing)
4. Handles "scan resistance" (bulk sequential scans shouldn't evict hot pages)

## Decision

Implement **Clock-Sweep** (also called "second-chance") eviction:

- Each page has a small counter (capped at 5)
- Counter incremented on access (capped to prevent single-page dominance)
- Eviction sweeps pages in circular order:
  - If counter > 0: decrement, skip (give second chance)
  - If counter == 0: evict this page
- Dirty pages require writeback before eviction

## Alternatives Considered

1. **LRU (doubly-linked list)** — Exact LRU, but requires list manipulation on every access (cache-unfriendly pointer updates in hot path).
2. **LFU (frequency-based)** — Good for stable workloads, but poor adaptation to changing access patterns.
3. **ARC (Adaptive Replacement Cache)** — Better than LRU/LFU, but complex (ghost lists, parameter tuning).
4. **Random eviction** — Simplest O(1), but no locality benefit; hot pages evicted randomly.

## Consequences

**Positive:**
- Near-O(1) eviction (one sweep, rarely more than one full rotation)
- No per-access linked-list manipulation (just atomic counter increment)
- Counter cap (5) prevents scan-pollution: bulk scans increment once, not enough to survive multiple sweeps
- Simple implementation (single `int` per page slot)

**Negative:**
- Only approximates LRU (not optimal for all access patterns)
- Single sweep pointer means sequential scan can still temporarily pollute if pages accessed many times
- Counter cap is a tuning parameter (too high = slow eviction, too low = no second chance)

**Cross-references:**
- [03-storage.md](../overview/03-storage.md) §3.2 — Buffer pool cache
- `src/Typhon.Engine/Persistence Layer/PagedMMF.cs` — Implementation

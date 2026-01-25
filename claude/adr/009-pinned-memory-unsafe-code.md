# ADR-009: Pinned Memory and Unsafe Code for Hot Paths

**Status**: Accepted
**Date**: 2024-01 (project inception)
**Deciders**: Developer

## Context

Typhon targets microsecond-level operation latency. The .NET garbage collector can:
1. Move objects in memory during compaction (invalidating pointers)
2. Pause all threads during collection (unpredictable latency spikes)
3. Add bounds-checking overhead on array access

For a page cache holding database pages, any GC-induced movement would corrupt pointer arithmetic used for direct component access.

## Decision

Use **pinned memory** (GCHandle) for the page cache and **unsafe code** (raw pointers) for hot-path operations:

- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` project-wide
- Page cache pinned via `GCHandle.Alloc(array, GCHandleType.Pinned)`
- Components accessed via pointer arithmetic (zero bounds-checking)
- `stackalloc` for temporary buffers (no heap allocation)
- Blittable component requirement ensures direct `memcpy` semantics

## Alternatives Considered

1. **Managed arrays with bounds checking** — Safe, but ~2–5ns overhead per access (significant at 1M+ accesses/tick).
2. **NativeMemory.Alloc (unmanaged heap)** — No GC interaction, but manual lifetime management; harder to debug.
3. **Memory-mapped files only (no cache)** — OS manages paging, but unpredictable page faults; no control over eviction.
4. **Span<T> everywhere** — Safe alternative to pointers with elided bounds checks, but doesn't solve the pinning problem for long-lived buffers.

## Consequences

**Positive:**
- Zero-copy component reads (pointer directly to mapped page data)
- No GC pauses on page cache (pinned, never moved)
- No bounds-checking overhead on hot paths
- Direct SIMD operations on pinned memory regions
- Predictable, consistent latency

**Negative:**
- Memory safety is developer's responsibility (buffer overflows, use-after-free possible)
- Harder to debug (no managed exceptions on bad access, just access violations)
- Pinned objects fragment the GC heap (can't be compacted)
- Code complexity: pointer arithmetic instead of idiomatic C#
- Requires careful `fixed` statement scoping or persistent pins

**Cross-references:**
- [CLAUDE.md](../../CLAUDE.md) — Performance considerations section
- [03-storage.md](../overview/03-storage.md) §3.2 — Buffer pool pinning
- [ADR-010](010-soa-simd-chunk-accessor.md) — SIMD operations on pinned memory

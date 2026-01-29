# ADR-027: Even-Sized Structs for Hot-Path Data

**Status**: Accepted
**Date**: 2025-01 (inferred from design session)
**Deciders**: Developer + Claude (design session)

## Context

Typhon stores millions of small fixed-size records (revision elements, B+Tree nodes, index entries) packed into 8KB pages via ChunkBasedSegment. These structs are accessed in tight loops with pointer arithmetic:

```csharp
byte* element = baseAddr + (index * stride);
```

An **odd-sized struct** (e.g., 13 bytes) causes problems:

1. **Alignment waste**: With `Pack=2` or `Pack=4`, the compiler pads to the next even/aligned boundary anyway — so you pay for bytes you don't use.
2. **Cache line splitting**: A 13-byte element at offset 51 straddles bytes 51–63 and 0–1 of the next cache line. The CPU must load two cache lines for one element access.
3. **SIMD unfriendliness**: Vector operations work on powers-of-2 lanes (16B, 32B, 64B). Odd strides prevent vectorized scanning.
4. **Integer division cost**: Computing `chunkId = offset / stride` is expensive for non-power-of-2 strides. Even-but-not-power-of-2 sizes can still use magic multiplier optimization, but odd sizes interact poorly with 2-byte-aligned addressing.
5. **Packing density**: An odd-sized struct in a 64-byte chunk wastes more tail bytes than an even one (64/13 = 4 elements + 12 bytes wasted, vs 64/12 = 5 elements + 4 bytes wasted).

## Decision

All structs stored in ChunkBasedSegment (hot-path repeated data) must have an **even total size**. Prefer sizes that are:

1. **Divisible by 4** (ideal — 4-byte aligned, no padding on any platform)
2. **Divisible by 2** (acceptable — 2-byte aligned with `Pack=2`)
3. **Power of 2** (best for SIMD — 8, 16, 32, 64 bytes)

When adding fields to an existing struct:
- If the new field would make the struct odd-sized, **pack it with an adjacent field** using bit manipulation (see CompRevStorageElement's IsolationFlag packed into TSN).
- If packing isn't possible, add a 1-byte padding field explicitly (`private byte _reserved;`) rather than leaving the struct at an odd size.

### Current Struct Sizes

| Struct | Size | Divisible by | Status |
|--------|------|-------------|--------|
| CompRevStorageElement | 12 bytes | 4 ✓ | Compliant |
| B+Tree L32 Node | 64 bytes | 64 ✓ | Compliant (1 cache line) |
| B+Tree L64 Node | 64 bytes | 64 ✓ | Compliant |
| PageBaseHeader | 8 bytes | 8 ✓ | Compliant |
| BackupPageEntry | 22 bytes | 2 ✓ | Compliant |

## Alternatives Considered

1. **No size constraint** — Let the compiler handle alignment via `Pack`. But this doesn't solve packing density or SIMD issues.
2. **Power-of-2 only** — Too restrictive. Many useful sizes (12, 22, 28) waste too much if rounded up to 16/32.
3. **Cache-line-aligned only (64 bytes)** — Only practical for B+Tree nodes. Most data elements are much smaller.
4. **Runtime padding by ChunkBasedSegment** — The segment could round up stride internally. But this hides the waste and complicates chunk ID calculations.

## Consequences

**Positive:**
- Predictable alignment: no surprise padding on any platform
- Better packing density in 64-byte and 8000-byte page payloads
- SIMD-friendly scanning (even strides work with shuffle/gather)
- Faster integer division (magic multiplier for even strides)
- Explicit bit-packing documents the trade-off (e.g., TSN loses 1 bit for IsolationFlag)

**Negative:**
- Developers must think about sizing when adding/removing fields
- Bit-packing adds complexity (mask/shift operations in accessors)
- Padding bytes waste a small amount of space (but less than odd-size alignment would)
- Changing a struct size requires recalculating chunk capacity per page

**Cross-references:**
- [ADR-022](022-64byte-cache-aligned-nodes.md) — B+Tree nodes: the extreme case (exactly 1 cache line)
- [ADR-008](008-chunk-based-segments.md) — ChunkBasedSegment chunk size determines packing
- [04-data.md](../overview/04-data.md) §4.6 — CompRevStorageElement physical layout
- [07-backup.md](../overview/07-backup.md) §7.5 — BackupPageEntry (22 bytes)

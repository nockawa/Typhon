# ADR-022: 64-Byte Cache-Aligned B+Tree Nodes

**Status**: Accepted
**Date**: 2024-06 (inferred from implementation)
**Deciders**: Developer

## Context

B+Tree performance is dominated by memory access patterns. Each node access potentially causes a cache miss (~100ns penalty on modern CPUs). For a tree of depth 4, a lookup touches 4 nodes. If each node spans multiple cache lines, the effective cost multiplies.

Modern CPUs have 64-byte cache lines. If a B+Tree node fits in exactly one cache line, a single cache miss loads the entire node.

## Decision

Size all B+Tree node structs to exactly **64 bytes** (one cache line):

```
Node (64 bytes):
  - Keys: [key_size × max_keys]
  - Children/Values: [pointer_size × (max_keys + 1)]
  - Metadata: count, flags

  L32BTree internal node:    [8 × 4B keys] + [9 × 4B children] + metadata = 64B
  L64BTree internal node:    [4 × 8B keys] + [5 × 4B children] + metadata = 64B
```

Nodes are stored in ChunkBasedSegment with 64-byte chunk size, ensuring natural alignment.

## Applicability

This decision applies to **B+Tree node storage** — data structures accessed via random pointer-chase traversal (parent → child), where each node is visited independently and the number of nodes touched per operation is O(depth).

This decision does **NOT** apply to sequential-scan data structures such as VariableSizedBufferSegment (VSBS) buffers, where all chunks in a linked list are visited in order. For sequential scans:

- The bottleneck is the **pointer chase between chunks** (~100ns each, unpredictable), not within-chunk cache efficiency.
- Larger chunks (multiples of 64 bytes) reduce the number of pointer chases, and the CPU prefetcher handles sequential intra-chunk cache line reads at near-zero cost.
- The 64-byte / 1-cache-line rule actively harms sequential workloads by maximizing the number of linked-list hops.

**Guideline for chunk stride selection:**

| Access Pattern | Optimal Stride | Rationale |
|---------------|---------------|-----------|
| Random access (B+Tree nodes) | 64 B (1 cache line) | Minimize per-access miss cost |
| Sequential scan (VSBS buffers) | 256–512 B (4–8 cache lines) | Minimize pointer chases; prefetcher handles intra-chunk reads |
| Mixed (rare) | Profile both patterns | No universal answer |

See [VersionedSecondaryIndexes](../design/VersionedSecondaryIndexes.md#chunk-capacity-analysis) for the volumetry analysis that motivated this clarification.

## Alternatives Considered

1. **Larger nodes (256B, 512B, page-sized)** — More keys per node = shallower tree, but each node access loads multiple cache lines. For B+Tree random-access traversal, the extra cache lines per node are wasted when only one node per level is visited. However, for sequential-scan structures (VSBS linked lists), larger chunks are beneficial because all data is visited and the prefetcher amortizes sequential cache line loads. This alternative was rejected for B+Tree nodes but is the correct choice for VSBS TAIL buffers (see Applicability).
2. **Variable-size nodes** — Pack more keys in leaf nodes, fewer in internal. Complicates allocation (can't use fixed-chunk segment).
3. **Cache-oblivious layouts** (van Emde Boas) — Optimal for any cache size, but complex implementation and poor fit for persistent storage.
4. **128-byte nodes (2 cache lines)** — More keys per node, but 50% chance of 2 cache misses per node access (depending on alignment).

## Consequences

**Positive:**
- Exactly one cache miss per node access (guaranteed by alignment)
- Predictable lookup cost: depth × ~100ns (cache miss) + depth × ~10ns (comparison)
- Natural fit for ChunkBasedSegment's fixed-size allocation
- No wasted cache line space (node fills entire line)
- SIMD-friendly: entire node in one Vector512 or two Vector256

**Negative:**
- Limited fan-out: L64BTree has only 4 keys per node (tree is deeper)
- Smaller fan-out means more levels for large datasets
- 64-byte constraint limits metadata per node
- Different key sizes have different max fan-outs (4 for 8B keys, 8 for 4B keys)
- Risk of over-application: the 64-byte rule is optimal for random-access patterns but harmful for sequential-scan patterns. New chunk-based data structures must evaluate their access pattern before defaulting to 64-byte stride

**Cross-references:**
- [ADR-021](021-specialized-btree-variants.md) — Per-type specialization enables exact sizing
- [ADR-008](008-chunk-based-segments.md) — 64-byte chunks in ChunkBasedSegment
- [CLAUDE.md](../../CLAUDE.md) — Cache line optimization requirements
- [VersionedSecondaryIndexes](../design/VersionedSecondaryIndexes.md) — Uses 512-byte TAIL stride, demonstrating when the 64-byte rule does not apply

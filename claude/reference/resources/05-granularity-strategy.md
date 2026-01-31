# 05 — Granularity Strategy

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [01-registry.md](01-registry.md), [04-metric-kinds.md](04-metric-kinds.md)

---

## Overview

Typhon has thousands of latches, millions of chunks, and potentially hundreds of index instances. Making each one a graph node would be insane — both in memory and in snapshot cost. This document defines **what gets a node** and what gets aggregated.

> 💡 **Key principle:** Track at the level where decisions are made, not at the level where data lives.

---

## Table of Contents

1. [The Granularity Problem](#1-the-granularity-problem)
2. [Decision Rules](#2-decision-rules)
3. [Level Definitions](#3-level-definitions)
4. [The "Owner Aggregates" Pattern](#4-the-owner-aggregates-pattern)
5. [Cost Model](#5-cost-model)
6. [When to Add Depth](#6-when-to-add-depth)
7. [Anti-Patterns](#7-anti-patterns)
8. [Resource Inventory Reference](#8-resource-inventory-reference)

---

## 1. The Granularity Problem

### The Scale Challenge

| Type | Typical Count | Make Each a Node? |
|------|---------------|-------------------|
| Database Engine | 1 | ✅ Yes |
| Component Tables | 10–50 | ✅ Yes |
| B+Tree Indexes | 50–200 | ✅ Yes (per-index metrics are valuable) |
| Segments | 100–400 | ❌ No (Owner Aggregates) |
| Chunks | 10,000–1,000,000 | ❌ No |
| Latches | 1,000–10,000 | ❌ No |
| Revision chains | 100,000–10,000,000 | ❌ No |
| Entities | 1,000,000+ | ❌ No |

### Why Not Track Everything?

If we made every chunk a graph node:
- **Memory**: 1M chunks × 200 bytes/node = 200 MB overhead
- **Snapshot time**: 1M nodes × 50 ns/node = 50 ms per snapshot (way too slow)
- **Signal-to-noise**: Finding the important information becomes impossible

### The Key Insight

**Resources should be tracked at the level where:**
1. Decisions can be made (evict, throttle, alert)
2. Troubleshooting benefits from visibility
3. The overhead is acceptable

Individual chunks don't make decisions — the ComponentTable does.

---

## 2. Decision Rules

### The Decision Matrix

| Question | Yes → | No → |
|----------|-------|------|
| Does this resource have a **distinct lifecycle**? | Consider node | Aggregate into parent |
| Can operators take **action** based on this resource's metrics? | Consider node | Aggregate into parent |
| Is the count of these resources **bounded** (< 500)? | Consider node | Must aggregate |
| Does tracking this **aid debugging**? | Consider node | Probably skip |
| Is this a **struct** or **ref struct**? | Cannot be node | — |

### Quick Decision Flowchart

```
                    ┌─────────────────────────────┐
                    │ Is it a struct/ref struct?  │
                    └──────────────┬──────────────┘
                                   │
                    ┌──────────────┴──────────────┐
                    │                             │
                   Yes                            No
                    │                             │
                    ▼                             ▼
            ┌───────────────┐    ┌────────────────────────────┐
            │ CANNOT be a   │    │ Does it manage memory,     │
            │ graph node    │    │ files, or child objects?   │
            └───────────────┘    └──────────────┬─────────────┘
                                               │
                                 ┌─────────────┴─────────────┐
                                 │                           │
                                No                          Yes
                                 │                           │
                                 ▼                           ▼
                    ┌───────────────────────┐   ┌────────────────────────────┐
                    │ Skip — not a resource │   │ Count bounded under 500?   │
                    └───────────────────────┘   └──────────────┬─────────────┘
                                                              │
                                              ┌───────────────┴───────────────┐
                                              │                               │
                                             Yes                              No
                                              │                               │
                                              ▼                               ▼
                                ┌─────────────────────────┐    ┌──────────────────────┐
                                │ Consider: would tracking │    │ MUST aggregate into  │
                                │ this aid debugging?      │    │ parent node          │
                                └───────────┬─────────────┘    └──────────────────────┘
                                            │
                              ┌─────────────┴─────────────┐
                              │                           │
                             Yes                          No
                              │                           │
                              ▼                           ▼
                 ┌───────────────────────┐    ┌───────────────────────┐
                 │ MAKE IT A NODE        │    │ Low priority —        │
                 └───────────────────────┘    │ maybe skip            │
                                              └───────────────────────┘
```

---

## 3. Level Definitions

### The 4 Granularity Levels

| Level | Definition | Gets a Node? | Examples |
|-------|------------|:------------:|----------|
| **System** | One per engine | ✅ | Root |
| **Subsystem** | One per architectural component | ✅ | Storage, DataEngine, Durability |
| **Component** | One per significant instance | ✅ | PageCache, WALRingBuffer, TransactionPool |
| **Per-type Instance** | One per registered type | ✅ | ComponentTable\<Player\>, PrimaryKeyIndex\<Player\> |
| **Per-entity** | Individual entities, chains, pages | ❌ | One player's revision chain |
| **Per-primitive** | Individual latches, chunks, bits | ❌ | One page latch, one chunk slot |

### Target Depth: 3 Levels

For most subsystems, the target is **3 levels** of depth:

```
Root                           (Level 1: System)
├── Storage                    (Level 2: Subsystem)
│   ├── PageCache              (Level 3: Component)
│   └── ManagedPagedMMF        (Level 3: Component - file management)
├── DataEngine                 (Level 2: Subsystem)
│   ├── TransactionPool        (Level 3: Component)
│   └── ComponentTable<T>      (Level 3: Per-type Instance)
│       ├── PrimaryKeyIndex    (Level 4: only if distinct metrics)
│       └── (segments)         (AGGREGATED - not nodes)
```

### When Level 4 Is Justified

Add a 4th level only when:
1. The sub-resource has **distinct metrics** from its parent
2. The sub-resource is **debuggable separately** (e.g., "which index is slow?")
3. The sub-resource has **its own resource pool** to track

---

## 4. The "Owner Aggregates" Pattern

For fine-grained primitives that don't get nodes, the **owning component** aggregates their metrics.

### The Pattern

```
ComponentTable<Player> ─── [graph node with aggregated metrics]
    │
    ├── _tableLatch ────────── [no node — contention flows to ComponentTable]
    ├── _indexLatch ────────── [no node — contention flows to ComponentTable]
    ├── _revisionSegment ───── [no node — capacity tracked by ComponentTable]
    └── _componentSegment ──── [no node — capacity tracked by ComponentTable]
```

### Implementation with IContentionTarget

The `IContentionTarget` callback pattern inverts the typical relationship:

```csharp
public class ComponentTable<T> : IResource, IMetricSource, IContentionTarget
{
    private AccessControl _tableLatch;
    private AccessControl _indexLatch;

    // Aggregate contention counters
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;

    // When acquiring any latch, pass 'this' as the target
    public void DoSomething()
    {
        _tableLatch.EnterExclusiveAccess(target: this);  // Callbacks go to us
        try { /* ... */ }
        finally { _tableLatch.ExitExclusiveAccess(); }
    }

    // IContentionTarget — called by locks when threads wait
    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalUs, waitUs);

        // Plain check-and-write is acceptable for diagnostic high-water marks
        // (occasional missed update is fine for monitoring purposes)
        if (waitUs > _contentionMaxUs)
            _contentionMaxUs = waitUs;
    }

    // IMetricSource — report the aggregate
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                              _contentionMaxUs, 0);
    }
}
```

### What Gets Aggregated

| Primitive Type | Aggregated Into | Metric Kind |
|---------------|-----------------|-------------|
| AccessControl latches | Owning component | Contention |
| Chunk allocations | ComponentTable | Capacity |
| Page accesses | PageCache | Throughput |
| Bitmap operations | ComponentTable | Capacity |
| **Segments** | **ComponentTable** | **Capacity (via IDebugPropertiesProvider for drill-down)** |

> **Segments follow the Owner Aggregates pattern:** ComponentTable owns its segments (ComponentSegment, CompRevTableSegment, DefaultIndexSegment, String64IndexSegment) and reports aggregated capacity via `IMetricSource`. Per-segment breakdown is available via `IDebugPropertiesProvider` for diagnostic drill-down, but segments are NOT graph nodes.

---

## 5. Cost Model

### Per-Node Overhead

Each graph node costs:

| Cost Type | Amount | Notes |
|-----------|--------|-------|
| **Memory** | ~200 bytes | `ResourceNode` fields + `ConcurrentDictionary` overhead |
| **Snapshot read** | ~50 ns | Read 4–8 fields + write to buffer |
| **Registration** | Once | `RegisterChild()` at startup |

### Total Cost Examples

| Configuration | Node Count | Memory | Snapshot Time |
|---------------|------------|--------|---------------|
| Small (5 component types) | ~50 nodes | ~10 KB | ~2.5 μs |
| Medium (20 component types) | ~100 nodes | ~20 KB | ~5 μs |
| Large (100 component types) | ~300 nodes | ~60 KB | ~15 μs |

All configurations are well within acceptable bounds (target: < 100 μs for snapshot).

### When Cost Becomes a Concern

Cost becomes a concern if:
- Node count exceeds ~1,000 (50+ μs snapshot)
- Memory overhead exceeds ~500 KB
- Hot-path registration/deregistration (don't do this)

If you're hitting these limits, you're tracking too fine-grained.

---

## 6. When to Add Depth

### Good Reasons to Add a 4th Level

| Reason | Example |
|--------|---------|
| **Distinct resource pool** | PrimaryKeyIndex has its own chunk allocator |
| **Meaningfully different metrics** | RevisionSegment (capacity) vs IndexSegment (lookups) |
| **Debugging isolation** | "Which specific index is the bottleneck?" |
| **Separate configuration** | Index might have different eviction policy |

### Bad Reasons to Add Depth

| Anti-Pattern | Why It's Bad |
|--------------|--------------|
| "Structural symmetry" | Extra nodes with no value |
| "Completeness" | Tracking everything means tracking nothing useful |
| "Every allocation should be visible" | Use aggregation instead |
| "Future-proofing" | Add nodes when needed, not before |

---

## 7. Anti-Patterns

### Anti-Pattern 1: Node Per Entity

```
❌ BAD
ComponentTable<Player>
├── Entity_1001
├── Entity_1002
├── Entity_1003
└── ... (millions more)
```

**Why it's bad:** Unbounded growth, massive overhead, no actionable insight.

**Instead:** Track entity count as a Capacity metric on ComponentTable.

### Anti-Pattern 2: Node Per Latch

```
❌ BAD
PageCache
├── PageLatch_0
├── PageLatch_1
├── PageLatch_2
└── ... (thousands more)
```

**Why it's bad:** Thousands of nodes; can't take action on individual latches anyway.

**Instead:** Aggregate contention into PageCache using `IContentionTarget`.

### Anti-Pattern 3: Node Per Chunk

```
❌ BAD
ComponentTable<Player>
├── Chunk_0
├── Chunk_1
└── ... (tens of thousands more)
```

**Why it's bad:** Chunks are data, not resources with lifecycle.

**Instead:** Track chunk utilization as Capacity metric on ComponentTable (aggregated from its segments).

### Anti-Pattern 4: Dynamic Node Creation on Hot Path

```csharp
❌ BAD
public void ProcessQuery(Query query)
{
    var queryNode = new ResourceNode($"Query_{query.Id}", this);
    // ... process
    queryNode.Dispose();
}
```

**Why it's bad:** Allocation on hot path, tree churn, registration overhead.

**Instead:** Track query throughput/duration as metrics on a single QueryProcessor node.

---

## 8. Resource Inventory Reference

### Complete Tree with Granularity Annotations

```
Root ──────────────────────────────────────────────── [Level 1: System]
│
├── Storage ───────────────────────────────────────── [Level 2: Subsystem - grouping only]
│   ├── PageCache ────────────────────────────────── [Level 3: Component with metrics]
│   │   └── (latches, pages) ─────────────────────── [AGGREGATED - not nodes]
│   │
│   └── ManagedPagedMMF ──────────────────────────── [Level 3: Component - file management]
│       └── (page allocations) ───────────────────── [AGGREGATED - not nodes]
│
├── DataEngine ────────────────────────────────────── [Level 2: Subsystem - grouping only]
│   ├── TransactionPool ──────────────────────────── [Level 3: Component with metrics]
│   │   └── (individual transactions) ────────────── [AGGREGATED - not nodes]
│   │
│   └── ComponentTable<T₁> ───────────────────────── [Level 3: Per-type Instance]
│       ├── PrimaryKeyIndex ──────────────────────── [Level 4: if lookup metrics distinct]
│       ├── SecondaryIndex<Field> ────────────────── [Level 4: if distinct]
│       └── (segments, latches, revisions) ───────── [AGGREGATED via IDebugPropertiesProvider]
│           ├── ComponentSegment ─────────────────── [capacity via DebugProperties]
│           ├── CompRevTableSegment ──────────────── [capacity via DebugProperties]
│           ├── DefaultIndexSegment ──────────────── [capacity via DebugProperties]
│           └── String64IndexSegment ─────────────── [capacity via DebugProperties]
│
├── Durability ────────────────────────────────────── [Level 2: Subsystem - grouping only]
│   ├── WALRingBuffer ────────────────────────────── [Level 3: Component with metrics]
│   ├── WALSegments ──────────────────────────────── [Level 3: Component]
│   ├── Checkpoint ───────────────────────────────── [Level 3: Component]
│   └── UoWEpochRegistry ─────────────────────────── [Level 3: Component]
│
├── Backup ────────────────────────────────────────── [Level 2: Subsystem - grouping only]
│   ├── ShadowBuffer ─────────────────────────────── [Level 3: Component]
│   └── SnapshotStore ────────────────────────────── [Level 3: Component]
│
└── Allocation ────────────────────────────────────── [Level 2: Subsystem - grouping only]
    ├── MemoryAllocator ──────────────────────────── [Level 3: Component]
    │   └── PinnedMemoryBlock[] ──────────────────── [Level 4: each block is a node]
    │
    └── OccupancyBitmaps ─────────────────────────── [Level 3: grouping]
        └── (individual bitmaps) ─────────────────── [AGGREGATED - not nodes]
```

### Node Count Estimate by Component Count

| Component Types | Estimated Node Count |
|-----------------|---------------------|
| 5 | 40–50 |
| 20 | 100–120 |
| 50 | 200–250 |
| 100 | 350–400 |

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [04-metric-kinds.md](04-metric-kinds.md) | What metrics nodes report |
| [01-registry.md](01-registry.md) §4 | Type inventory (which types are resources) |
| [overview/08-resources.md](../../overview/08-resources.md) §8.4 | High-level granularity rules |
| [overview/08-resources.md](../../overview/08-resources.md) §8.5 | Resource inventory reference |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Target depth | 3 levels (allow 4 when justified) | Balance visibility vs overhead |
| Individual latches | No nodes — aggregate via IContentionTarget | Thousands of latches → thousands of useless nodes |
| Individual chunks | No nodes | Chunks are data, not resources |
| Per-entity tracking | No | Unbounded, no actionable insight |
| Memory blocks | Yes, each is a node | Bounded count, distinct lifecycle, valuable for leak detection |
| Per-component-type tables | Yes | Different tables have different characteristics |
| Per-index nodes | Yes if distinct metrics | Separating index vs data metrics aids debugging |
| **Segments** | **No nodes — Owner Aggregates** | ComponentTable aggregates via IMetricSource; per-segment drill-down via IDebugPropertiesProvider. Easiest to implement/maintain, centralizes metric logic. |
| High-water marks | Plain check-and-write | InterlockedMax not needed for diagnostic metrics; occasional missed update acceptable |

---

*Document Version: 2.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

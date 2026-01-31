# 07 — Budgets & Exhaustion Policies

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [04-metric-kinds.md](04-metric-kinds.md), [06-snapshot-api.md](06-snapshot-api.md)

---

## Overview

What happens when a bounded resource reaches its limit? This document defines the **budget hierarchy**, **exhaustion policies**, and **back-pressure patterns** that keep Typhon stable under pressure.

> 💡 **Key principle:** Shed load at the edge, not in the core. Transactions should fail fast when resources are exhausted — internal components should self-heal.

---

## Table of Contents

0. [Architecture Overview](#0-architecture-overview)
1. [Budget Hierarchy](#1-budget-hierarchy)
2. [ResourceOptions Configuration](#2-resourceoptions-configuration)
3. [Exhaustion Policies](#3-exhaustion-policies)
4. [Policy Selection Matrix](#4-policy-selection-matrix)
5. [Back-Pressure Patterns](#5-back-pressure-patterns)
6. [Cascade Detection](#6-cascade-detection)
7. [Budget Enforcement Modes](#7-budget-enforcement-modes)
8. [Open Questions](#8-open-questions)

---

## 0. Architecture Overview

### Where Budget Configuration Lives

Budget limits are part of `DatabaseEngineOptions`, passed to `DatabaseEngine` at construction:

```csharp
var options = new DatabaseEngineOptions
{
    // ... other options (file paths, durability mode, etc.)

    Resources = new ResourceOptions
    {
        PageCachePages = 262144,           // 2 GB
        MaxActiveTransactions = 1000,
        WalRingBufferSizeBytes = 4 << 20,  // 4 MB
        // ...
    }
};

var engine = new DatabaseEngine(options);
```

### Startup Flow

```
DatabaseEngine Constructor
│
├── 1. Validate ResourceOptions
│      └── Check fixed allocations fit in TotalMemoryBudget
│      └── Throw InvalidOperationException if invalid
│
├── 2. Create components with their limits
│      ├── PageCache(options.Resources.PageCachePages)
│      ├── TransactionPool(options.Resources.MaxActiveTransactions)
│      ├── WALRingBuffer(options.Resources.WalRingBufferSizeBytes)
│      └── ShadowBuffer(options.Resources.ShadowBufferPages)
│
└── 3. Register components with IResourceRegistry
       └── Each component becomes a node in the resource tree
```

### How Limits Flow to Components

Each component receives **only its specific limits** via constructor injection:

```csharp
public class PageCache : IResource, IMetricSource
{
    private readonly int _totalSlots;

    public PageCache(int totalSlots, IResource parent)
    {
        _totalSlots = totalSlots;  // Receives limit, doesn't know about other budgets
        // ...
    }
}
```

Components don't have access to `ResourceOptions` — they only know their own limits. This keeps them focused and testable.

### Policy Assignment — Hardcoded, Not Configurable

Each component has a **hardcoded exhaustion policy** based on its semantics:

| Component | Policy | Why Hardcoded |
|-----------|--------|---------------|
| TransactionPool (creation) | FailFast | Queueing makes backlog worse; clients can retry |
| PageCache | Evict, then Wait | That's what makes it a cache; timeout prevents deadlock |
| WALRingBuffer | Wait | Losing durability is unacceptable |
| ShadowBuffer | Wait | Backup must complete; timeout prevents deadlock |

Policies are **not configurable** because they're inherent to what the component IS:
- A cache must be able to evict
- A durability buffer must wait
- A client-facing limit must fail fast

### Page Cache as Memory Bottleneck

The **page cache is the ultimate memory bottleneck** for page-based resources:

```
┌─────────────────────────────────────────────────────────┐
│                    Page Cache (2 GB)                    │
│                                                         │
│   All page-based allocations compete for slots:         │
│   ├── ComponentTable data pages                         │
│   ├── Index B+Tree node pages                           │
│   ├── Revision chain pages                              │
│   └── ...                                               │
│                                                         │
│   When full: Evict LRU → if all pinned → Wait           │
└─────────────────────────────────────────────────────────┘
```

This means:
- **Indexes don't have their own memory budget** — they grow by allocating pages
- **When PageCache is full**, index growth triggers eviction (or wait if all pinned)
- **The "1 GB for indexes"** in the budget hierarchy is informational (expected usage), not enforced

### Interaction with IMetricSource

Components report their utilization via `IMetricSource.ReadMetrics()`:

```csharp
public void ReadMetrics(IMetricWriter writer)
{
    // Report current vs max — enables monitoring and alerting
    writer.WriteCapacity(_usedSlots, _totalSlots);
}
```

This enables:
- **Dashboards:** See utilization across all bounded resources
- **Alerts:** Trigger when utilization > 80%
- **Cascade detection:** Find the root cause when multiple resources are pressured

### What Gets Validated at Startup

`ResourceOptions.Validate()` checks **fixed allocations** (what's allocated immediately):

| Validated | Not Validated |
|-----------|---------------|
| PageCache pages × 8KB | Active transactions (grows at runtime) |
| WAL ring buffer | Index nodes (bounded by page cache) |
| WAL segments × max size | Query buffers (bounded at runtime) |
| Shadow buffer pages × 8KB | |

Growable resources have **runtime caps** (`MaxActiveTransactions`, etc.) that prevent unbounded growth.

---

## 1. Budget Hierarchy

### Total Memory Budget

The total memory budget is subdivided across subsystems:

```
Total Budget (configurable, default 4 GB)
│
├── Storage/PageCache ─────────────── 2 GB ─── Fixed at startup
│       └── (pages × 8 KB)
│
├── Durability/WAL ────────────────── 260 MB ─── Ring + segments
│       ├── WALRingBuffer ─────────── 4 MB
│       └── WALSegments ───────────── 256 MB (4 × 64 MB)
│
├── DataEngine/Transactions ──────── 256 MB ─── Active state
│       └── (active count × avg state size)
│
├── DataEngine/Indexes ───────────── 1 GB ─── B+Tree nodes
│       └── (all index segments)
│
└── Working Memory ───────────────── 508 MB ─── Query, misc
        └── (query buffers, compression, temp)
```

### Budget Ownership

Each subsystem **owns** its budget — it can allocate freely within its limit without checking with others:

| Subsystem | Owns | Can Grow? |
|-----------|------|-----------|
| PageCache | Page slots | No — fixed at startup |
| WALRingBuffer | Ring bytes | No — fixed at startup |
| WALSegments | Segment files | Yes — up to MaxSegments |
| TransactionPool | Active transactions | Yes — up to MaxActive |
| Indexes | B+Tree nodes | Yes — bounded by page cache |

---

## 2. ResourceOptions Configuration

```csharp
/// <summary>
/// Configuration options for resource budgets and limits.
/// Set at startup, immutable thereafter.
/// </summary>
public class ResourceOptions
{
    // ═══════════════════════════════════════════════════════════════
    // PAGE CACHE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Number of pages in the page cache.
    /// Each page is 8 KB, so 256 pages = 2 MB cache.
    /// </summary>
    public int PageCachePages { get; set; } = 256;

    /// <summary>
    /// Maximum pages the cache can grow to.
    /// Used if dynamic cache sizing is enabled (future).
    /// </summary>
    public int MaxPageCachePages { get; set; } = 16384;  // 128 MB

    // ═══════════════════════════════════════════════════════════════
    // TRANSACTIONS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum concurrent active transactions.
    /// Beyond this, CreateTransaction throws ResourceExhaustedException.
    /// </summary>
    public int MaxActiveTransactions { get; set; } = 1000;

    /// <summary>
    /// Number of Transaction objects kept in pool for reuse.
    /// </summary>
    public int TransactionPoolSize { get; set; } = 16;

    // ═══════════════════════════════════════════════════════════════
    // WAL & DURABILITY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Size of the WAL ring buffer in bytes.
    /// When full, commit threads block until WAL writer drains it.
    /// </summary>
    public int WalRingBufferSizeBytes { get; set; } = 4 * 1024 * 1024;  // 4 MB

    /// <summary>
    /// Back-pressure threshold as fraction of ring buffer capacity.
    /// At this level, commits start blocking.
    /// </summary>
    public double WalBackPressureThreshold { get; set; } = 0.8;  // 80%

    /// <summary>
    /// Maximum size of a single WAL segment file.
    /// </summary>
    public long WalMaxSegmentSizeBytes { get; set; } = 64L << 20;  // 64 MB

    /// <summary>
    /// Maximum number of WAL segment files.
    /// When all are full, checkpoint is forced.
    /// </summary>
    public int WalMaxSegments { get; set; } = 4;

    // ═══════════════════════════════════════════════════════════════
    // CHECKPOINT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum dirty pages before forcing a checkpoint.
    /// </summary>
    public int CheckpointMaxDirtyPages { get; set; } = 10000;

    /// <summary>
    /// Checkpoint interval when idle (milliseconds).
    /// </summary>
    public int CheckpointIntervalMs { get; set; } = 30000;  // 30 seconds

    // ═══════════════════════════════════════════════════════════════
    // BACKUP
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Size of the CoW shadow buffer in pages.
    /// Writers block when all slots are occupied.
    /// </summary>
    public int ShadowBufferPages { get; set; } = 512;  // 4 MB

    // ═══════════════════════════════════════════════════════════════
    // OVERALL
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Total memory budget for the engine.
    /// Used for validation at startup (all allocations must fit).
    /// </summary>
    public long TotalMemoryBudgetBytes { get; set; } = 4L << 30;  // 4 GB
}
```

### Validation at Startup

```csharp
public void Validate()
{
    // Only validate FIXED allocations (allocated immediately at startup).
    // Growable resources (transactions, indexes, query buffers) have runtime
    // caps that prevent unbounded growth — they don't need upfront validation.

    var pageCacheBytes = (long)PageCachePages * 8192;
    var walBytes = WalRingBufferSizeBytes + (WalMaxSegments * WalMaxSegmentSizeBytes);
    var shadowBytes = ShadowBufferPages * 8192;

    var totalRequired = pageCacheBytes + walBytes + shadowBytes;

    if (totalRequired > TotalMemoryBudgetBytes)
    {
        throw new InvalidOperationException(
            $"Configuration requires {totalRequired / 1_000_000} MB " +
            $"but budget is only {TotalMemoryBudgetBytes / 1_000_000} MB");
    }
}
```

---

## 3. Exhaustion Policies

When a bounded resource reaches its limit, one of four policies applies:

### Policy: FailFast

**Behavior:** Throw `ResourceExhaustedException` immediately.

```csharp
public Transaction CreateTransaction()
{
    if (_activeCount >= _options.MaxActiveTransactions)
    {
        throw new ResourceExhaustedException(
            "Maximum active transactions exceeded",
            ResourceType.TransactionPool,
            _activeCount,
            _options.MaxActiveTransactions);
    }
    // ...
}
```

**Use when:**
- The caller can handle failure gracefully
- Queueing would make the problem worse
- The resource represents edge capacity (client-facing)

**Examples:**
- Transaction creation beyond max
- Query memory allocation beyond limit

### Policy: Wait

**Behavior:** Block caller until resource becomes available. Respects `Deadline`.

```csharp
public void AcquirePageSlot(Deadline deadline)
{
    while (_usedSlots >= _totalSlots)
    {
        if (deadline.IsExpired)
        {
            throw new TimeoutException("Timed out waiting for page slot");
        }
        _slotAvailable.Wait(deadline.Remaining);
    }
    // ...
}
```

**Use when:**
- The resource will become available soon
- Blocking is acceptable (not on UI thread, has timeout)
- The alternative is worse (losing durability)

**Examples:**
- Page latch acquisition
- WAL ring buffer space

### Policy: Evict

**Behavior:** Remove least-recently-used entry, retry.

```csharp
private PageCacheEntry AllocateSlot()
{
    if (_usedSlots >= _totalSlots)
    {
        var victim = _clockSweep.FindVictim();
        if (victim.IsDirty)
        {
            FlushPage(victim);
        }
        _usedSlots--;
    }
    return AllocateNewSlot();
}
```

**Use when:**
- Entries can be recreated (cache semantics)
- Eviction has bounded cost
- The resource is self-healing

**Examples:**
- Page cache (evict clean pages first)
- Chunk accessor MRU cache

### Policy: Degrade

**Behavior:** Continue with reduced performance or functionality.

```csharp
public Transaction CreateTransaction()
{
    if (!_pool.TryRent(out var txn))
    {
        // Pool empty — allocate new (slower, but works)
        txn = new Transaction();
        _metrics.PoolMisses++;
    }
    // ...
}
```

**Use when:**
- A fallback exists (slower but correct)
- Failure is worse than degradation
- The situation is temporary

**Examples:**
- Transaction pool empty → allocate new
- Compression buffer unavailable → skip compression

---

## 4. Policy Selection Matrix

| Resource | FailFast | Wait | Evict | Degrade | Default |
|----------|:--------:|:----:|:-----:|:-------:|---------|
| **TransactionPool (creation)** | ✅ | | | | FailFast |
| **TransactionPool (pooled object)** | | | | ✅ | Degrade |
| **PageCache** | | ✅ | ✅ | | Evict, then Wait |
| **WALRingBuffer** | | ✅ | | | Wait |
| **ShadowBuffer** | | ✅ | | | Wait |
| **ChunkAccessorCache** | | | ✅ | | Evict |
| **QueryMemory** | ✅ | | | | FailFast |
| **IndexNodes** | | | | | (grows via page cache) |

### Combined Policies

Some resources use multiple policies in sequence:

```csharp
// PageCache: try evict first, then wait if all pages are pinned
private PageCacheEntry GetSlot(Deadline deadline)
{
    // Step 1: Try eviction
    if (TryEvictUnpinned(out var slot))
        return slot;

    // Step 2: All pinned — wait for unpin
    while (!TryEvictUnpinned(out slot))
    {
        if (deadline.IsExpired)
            throw new TimeoutException("All pages pinned");
        _unpinEvent.Wait(deadline.Remaining);
    }
    return slot;
}
```

---

## 5. Back-Pressure Patterns

### Pattern 1: WAL Ring Buffer

**Trigger:** > 80% fill level

**Effect:** Commit threads block until WAL writer drains.

```
Producer (Commit) ───────────────────────────────────────────→
                                                              │
    [Insert WAL record into ring]                             │
         │                                                    │
         └── if (fillLevel > 80%) ──→ WAIT ──────────────────→│
                                       ↑                      │
                                       │                      │
Consumer (WAL Writer) ─────────────────┴── [Drain & fsync] ──→
```

**Resolution:** WAL writer fsyncs and advances the tail pointer.

**Metrics:**
- `Capacity.Utilization` shows fill level
- `Contention.WaitCount` shows blocked commits

### Pattern 2: CoW Shadow Buffer

**Trigger:** All slots occupied

**Effect:** Page writers block until backup reader consumes.

```
Page Writer ──→ [Copy to shadow] ──→ if (full) ──→ WAIT
                                                    ↑
Backup Reader ──→ [Read shadow + write to backup] ──┘
```

**Resolution:** Backup writer reads pages and frees slots.

### Pattern 3: Checkpoint Dirty Pages

**Trigger:** Dirty pages > threshold (e.g., 90% of max)

**Effect:** Checkpoint fires sooner, not blocked.

```
Normal: checkpoint every 30 seconds
Under pressure: checkpoint immediately
```

**Resolution:** Checkpoint flushes dirty pages to disk.

### Back-Pressure Hierarchy

When multiple resources are under pressure, they cascade:

```
Transaction blocked on Commit
    └── because WAL ring is full
        └── because WAL writer is slow
            └── because disk is overloaded
                └── because checkpoint is flushing
```

The graph can trace this cascade — see [Cascade Detection](#6-cascade-detection).

---

## 6. Cascade Detection

### Known Wait Dependencies

Components have **architectural wait relationships** that differ from the resource tree structure.
These are hardcoded based on how Typhon's subsystems interact:

```csharp
/// <summary>
/// Known wait dependencies based on architectural knowledge.
/// Key = component that waits, Value = components it may block on.
/// </summary>
private static readonly Dictionary<string, string[]> WaitDependencies = new()
{
    // Commits wait for WAL ring buffer space
    ["DataEngine/TransactionPool"] = ["Durability/WALRingBuffer"],

    // WAL ring drains to WAL segments (disk I/O)
    ["Durability/WALRingBuffer"] = ["Durability/WALSegments"],

    // Page cache eviction waits for dirty page flush
    ["Storage/PageCache"] = ["Storage/ManagedPagedMMF"],

    // Shadow buffer waits for backup writer to consume
    ["Backup/ShadowBuffer"] = ["Backup/SnapshotStore"],
};
```

### Graph-Based Root Cause Analysis

The `FindRootCause` method traces the wait chain from a symptomatic node:

```csharp
/// <summary>
/// Traces back from a symptomatic node to find the root cause.
/// Uses hardcoded wait dependencies to follow the causal chain.
/// </summary>
/// <remarks>
/// This is a heuristic based on architectural knowledge, not runtime tracking.
/// It may not identify the true root cause in all scenarios.
/// </remarks>
public NodeSnapshot FindRootCause(ResourceSnapshot snapshot, string symptomPath)
{
    var visited = new HashSet<string>();
    var current = symptomPath;

    while (current != null && !visited.Contains(current))
    {
        visited.Add(current);

        if (!snapshot.Nodes.TryGetValue(current, out var node))
            break;

        // If this node is highly utilized, check what it waits on
        if (node.Capacity?.Utilization > 0.8)
        {
            if (WaitDependencies.TryGetValue(current, out var dependencies))
            {
                // Find the most utilized dependency
                var nextCause = dependencies
                    .Where(d => snapshot.Nodes.ContainsKey(d))
                    .Where(d => snapshot.Nodes[d].Capacity?.Utilization > 0.8)
                    .OrderByDescending(d => snapshot.Nodes[d].Capacity?.Utilization ?? 0)
                    .FirstOrDefault();

                if (nextCause != null)
                {
                    current = nextCause;
                    continue;
                }
            }
        }

        // No further dependencies — this is the root cause
        return node;
    }

    // Fallback: return the symptom node itself
    return snapshot.Nodes.GetValueOrDefault(symptomPath);
}
```

### Example Cascade

1. **Symptom:** TransactionPool shows high utilization (0.95)
2. **Query:** `FindRootCause(snapshot, "DataEngine/TransactionPool")`
3. **Analysis:**
   - TransactionPool (0.95) waits on → WALRingBuffer
   - WALRingBuffer (0.92) waits on → WALSegments
   - WALSegments (0.60) — not highly utilized
4. **Root cause returned:** WALRingBuffer (end of the high-utilization chain)

### Limitations

This approach has known limitations:

| Limitation | Impact |
|------------|--------|
| **Static dependencies** | Can't detect runtime-specific wait patterns |
| **No contention tracking** | Doesn't know which thread is actually blocked |
| **Threshold-based** | 80% threshold may miss gradual degradation |

For production debugging, combine with:
- Contention metrics (`Contention.WaitCount`, `TotalWaitUs`)
- Duration metrics (commit latency, flush latency)
- External profiling tools

### Priority Table (Fallback)

If cascade analysis isn't conclusive, use this priority ordering for load shedding:

```
Disk I/O > WAL > Page Cache > Transactions > Indexes > Query
```

Shed load from right to left (lower priority first).

---

## 7. Budget Enforcement Modes

### Mode: Strict

Check budget before every allocation:

```csharp
public void AllocateChunk()
{
    if (_totalAllocated + ChunkSize > _budget)
    {
        throw new ResourceExhaustedException(...);
    }
    // ...
}
```

**Overhead:** ~5 ns per allocation
**Use when:** Multi-tenant, production, need guarantees

### Mode: Sampled

Check every N allocations or at page boundaries:

```csharp
private int _allocationsSinceCheck;

public void AllocateChunk()
{
    if (++_allocationsSinceCheck >= 64)
    {
        _allocationsSinceCheck = 0;
        if (_totalAllocated > _budget)
        {
            // Already over — stop future allocations
            throw new ResourceExhaustedException(...);
        }
    }
    // ...
}
```

**Overhead:** Near-zero (amortized)
**Use when:** Single-tenant, low-latency, page-level catches overruns

### Default: Sampled

Typhon defaults to Sampled mode:
- Check when a new page is allocated
- Check every 64 chunk allocations
- Page-level allocation catches overruns anyway

---

## 8. Open Questions

### Hot-Resize Budgets

**Question:** Should budgets be adjustable at runtime?

**Pros:**
- Long-running servers benefit from adapting to workload
- Could shrink page cache to grow indexes

**Cons:**
- Complexity in coordination
- Memory fragmentation concerns
- Current design says restart-required

**Status:** Not planned for v1. Reconsider if demand arises.

### Pressure Callbacks

**Question:** Should we expose `OnPressure(node, level)` for adaptive behavior?

```csharp
// Example: query engine reduces sort buffer when page cache is pressured
resourceGraph.OnPressure += (node, level) =>
{
    if (node.Path == "Storage/PageCache" && level > 0.8)
        _queryEngine.ReduceSortBuffer();
};
```

**Status:** Interesting but adds complexity. Not planned for v1.

### Per-Component-Type Budgets

**Question:** Should `ComponentTable<Player>` have a different budget than `ComponentTable<Inventory>`?

**Pros:**
- Useful for multi-tenant scenarios
- Prevents one hot table from starving others

**Cons:**
- Configuration complexity
- Hard to predict needs upfront

**Status:** Not planned. Use aggregate budgets for now.

### GC Integration

**Question:** Should the resource graph react to `GC.RegisterForFullGCNotification`?

**Pros:**
- Could pre-emptively evict cache before Gen2 pause
- Reduces GC pause impact

**Cons:**
- GC notifications are unreliable in practice
- Adds complexity

**Status:** Not planned for v1.

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [04-metric-kinds.md](04-metric-kinds.md) | Capacity metrics for utilization |
| [06-snapshot-api.md](06-snapshot-api.md) | Querying utilization |
| [08-observability-bridge.md](08-observability-bridge.md) | Health check thresholds |
| [overview/08-resources.md](../../overview/08-resources.md) §8.7 | High-level overview |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Default policy | FailFast at edge, Evict/Wait for internal | Shed load early; internals self-heal |
| Back-pressure threshold | 80% | Leave headroom for burst |
| Enforcement mode | Sampled by default | Page-level catches overruns anyway |
| Budget timing | Set at startup, immutable | Avoid runtime complexity |
| Cascade detection | Hardcoded wait dependencies | Architectural knowledge; runtime tracking too complex for v1 |
| Pinned memory | Allocate at startup, keep forever | Avoid GC fragmentation |
| Transaction limit behavior | FailFast | Clients can retry; queueing makes it worse |
| Page cache full | Evict, then Wait | Self-healing; timeout prevents deadlock |
| **ResourceOptions location** | **Part of DatabaseEngineOptions** | Single configuration object; components receive only their limits |
| **Policy assignment** | **Hardcoded per component** | Policies are inherent to component semantics; configuration would allow bad choices |
| **Memory bottleneck** | **Page cache is the limit** | Growable resources (indexes) compete for pages; no per-subsystem quotas |
| **Validation scope** | **Fixed allocations only** | Growable resources have runtime caps; worst-case estimation unreliable |

---

*Document Version: 2.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

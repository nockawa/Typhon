# Component 8: Resource Management

> A live resource graph providing snapshot-based diagnostics, hierarchical aggregation, and exhaustion detection across all Typhon subsystems.

---

## Overview

Typhon maintains a **runtime resource graph** — a tree of nodes representing every significant resource in the engine. Each node declares the metrics it exposes (memory, capacity, contention, I/O, throughput, duration). At any instant, the graph can produce a **snapshot**: a consistent-enough reading of the entire tree for diagnostics, exhaustion detection, and export to [Observability](09-observability.md).

The resource graph is:
- **Hierarchical** — query "how much memory does Storage use?" by summing children
- **Pull-based** — counters live in the components; the graph assembles them on demand
- **Low-overhead** — components maintain plain fields (no atomics on hot path); the snapshot reads them
- **Already partially implemented** — `IResource`, `ResourceNode`, `ResourceRegistry`, `MemoryAllocator`, and `PagedMMF.Metrics` exist in the codebase

<a href="../assets/typhon-resource-graph-overview.svg">
  <img src="../assets/typhon-resource-graph-overview.svg"
       alt="Resource Graph — Component overview showing hierarchical tree with metric kinds per node">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-resource-graph-overview.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

---

## Status: ✅ Implemented

The Resource System is fully implemented. The tree structure (`IResource`, `ResourceNode`, `ResourceRegistry`), memory tracking (`IMemoryResource`, `MemoryAllocator`, `PinnedMemoryBlock`), metric interfaces (`IMetricSource`, `IMetricWriter`), snapshot mechanism (`IResourceGraph`, `ResourceSnapshot`), budget configuration (`ResourceOptions`), and observability bridge (`OTelMetricsExporter`) are all complete.

---

## Sub-Components

| # | Name | Purpose | Status |
|---|------|---------|--------|
| **8.1** | [Motivation & Problem Statement](#81-motivation--problem-statement) | Why we need this, what it enables | ✅ Done |
| **8.2** | [Resource Graph Model](#82-resource-graph-model) | Runtime tree, node types, evolution of IResource | ✅ Implemented |
| **8.3** | [Metric Kinds](#83-metric-kinds) | The 6 measurement types a node can declare | ✅ Implemented |
| **8.4** | [Granularity Strategy](#84-granularity-strategy) | What gets a node vs what gets aggregated | ✅ Implemented |
| **8.5** | [Resource Inventory](#85-resource-inventory) | Complete tree of all engine resources | ✅ Implemented |
| **8.6** | [Resource Lifecycle](#86-resource-lifecycle) | Creation, consumption, release patterns | ✅ Implemented |
| **8.7** | [Budgets & Exhaustion](#87-budgets--exhaustion) | Limits, policies, back-pressure, cascade detection | ✅ Implemented |
| **8.8** | [Snapshot & Query API](#88-snapshot--query-api) | Pull-based reads, aggregation, bottleneck analysis | ✅ Implemented |
| **8.9** | [Bridge to Observability](#89-bridge-to-observability) | How graph feeds metrics, health checks, traces | ✅ Implemented |

---

## 8.1 Motivation & Problem Statement

### The Questions We Want to Answer

At any point during Typhon's operation, an operator (or the engine itself) should be able to answer:

| Question | Without Graph | With Graph |
|----------|--------------|------------|
| "Why is the engine slow right now?" | Check each flat counter manually | Walk the tree, find the node with highest contention/utilization |
| "What's consuming all the memory?" | Sum individual allocators manually | `GetSubtreeMemory(Root)` — instant hierarchical breakdown |
| "Is this a local issue or cascading?" | Hard-coded priority table | Trace back-pressure edges in the graph |
| "How many resources does ComponentTable<Player> own?" | Grep the code | `node.Children.Sum(c => c.Metrics.Memory.AllocatedBytes)` |
| "What's the root cause of transaction stalls?" | Correlate multiple dashboards | `FindBottleneck()` — traverse from symptom to source |
| "Is it safe to increase page cache size?" | Estimate manually | `GetSubtreeMemory(Root) + requestedIncrease < TotalBudget` |

### Problems This Solves

1. **Resource Attribution** — When the engine uses 2 GB of memory, *which subsystem* owns it? Flat counters can't answer this. The tree can: sum any subtree.

2. **Cascade Root-Cause Analysis** — When transactions stall, is it because the WAL ring is full (which blocks commits), or because the page cache is full (which blocks page loads)? The graph traces dependency chains.

3. **Adaptive Exhaustion Handling** — Instead of hard-coded priority tables (`WAL > PageCache > Transactions`), the graph can *mechanically* determine which resource to shed first based on its position in the dependency chain.

4. **Granular Monitoring with Bounded Cost** — You can't monitor every `AccessControl` latch individually (thousands exist), but you *can* monitor the `ComponentTable<Player>` node which aggregates its children's contention stats.

5. **Reliability Under Pressure** — Enterprise-grade database engines must handle never-occurring scenarios (disk full, memory exhaustion, deadlock, CPU starvation) gracefully. The resource graph provides the situational awareness to detect these before they cascade.

### Design Philosophy

- **Snapshot, not stream** — We don't need 50 updates per second. We need a consistent-enough picture when we request one.
- **Approximate is acceptable** — ±1 on a counter is fine. The alternative (Interlocked everywhere) costs cache-line bounces on every operation.
- **Depth has cost** — Every graph node is memory, every metric is a read during snapshot. Target 3 levels for most subsystems; allow deeper only when justified.
- **Components own their counters** — The graph doesn't *write* metrics; it *reads* what components already maintain.
- **The graph structure is the documentation** — The tree IS the map of what the engine owns and how resources relate.

---

## 8.2 Resource Graph Model

### Existing Infrastructure

The codebase already has the tree structure:

```csharp
// Tree node — already implemented
public interface IResource : IDisposable
{
    string Id { get; }
    ResourceType Type { get; }          // Node, Service, Memory, File, Synchronization, Bitmap
    IResource Parent { get; }
    IEnumerable<IResource> Children { get; }
    DateTime CreatedAt { get; }
    IResourceRegistry Owner { get; }
    bool RegisterChild(IResource child);
    bool RemoveChild(IResource resource);
}

// Tree root — already implemented
public interface IResourceRegistry : IDisposable
{
    IResource Root { get; }
    IResource Services { get; }
    IResource Orphans { get; }           // Nodes registered without explicit parent
    IResource RegisterService<T>(T service) where T : IResource;
}

// Memory-specific node — already implemented
public interface IMemoryResource : IResource
{
    int Size { get; }                    // Bytes owned by this allocation
}
```

**Current usage**: `MemoryAllocator` registers as a `ServiceBase`. `PinnedMemoryBlock` and `MemoryBlockArray` register as `IMemoryResource` children of their parent nodes. `ConcurrentBitmapL3All` implements `IResource`. The `ResourceRegistry` is DI-registered (singleton, scoped, or transient).

### Evolution: Adding Metric Declarations

The existing `IResource` provides structure (parent/children) but not measurement. We extend it with a **metric provider** interface — each node that has measurable state implements it:

```csharp
/// <summary>
/// A resource node that can report its current metric values.
/// Not all IResource nodes need to implement this — only those with measurable state.
/// </summary>
public interface IMetricSource
{
    /// <summary>
    /// Reads current metric values into the provided snapshot builder.
    /// Called during GetSnapshot() — should be fast (read fields, no allocation).
    /// </summary>
    void ReadMetrics(IMetricWriter writer);
}

/// <summary>
/// Writer interface for reporting metrics during snapshot collection.
/// Implementations buffer values; callers just write.
/// </summary>
public interface IMetricWriter
{
    void WriteMemory(long allocatedBytes, long peakBytes);
    void WriteCapacity(long current, long maximum);
    void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes);
    void WriteContention(long waitCount, long totalWaitUs, long maxWaitUs, long timeoutCount);
    void WriteThroughput(string name, long count);
    void WriteDuration(string name, long lastUs, long avgUs, long maxUs);
}
```

**Key design**: `IMetricSource` is *optional* — structural nodes (pure grouping, like "Storage") don't implement it. Only leaves and instances with real counters do. The writer pattern avoids allocating metric objects per-read.

### Node Types in Practice

| Node Role | Implements | Example |
|-----------|-----------|---------|
| **Root** | `IResource` only | `Root` — pure grouping |
| **Subsystem** | `IResource` only | `Storage`, `DataEngine` — aggregate children |
| **Component** | `IResource` + `IMetricSource` | `PageCache` — has real counters |
| **Instance** | `IResource` + `IMetricSource` | `ComponentTable<Player>` — per-table metrics |
| **Memory leaf** | `IMemoryResource` + `IMetricSource` | `PinnedMemoryBlock` — exact byte tracking |

### Tree Topology

The graph structure is defined at startup (components register themselves and their resources). The tree does not change shape at runtime — only the metric values on nodes change.

<a href="../assets/typhon-resource-tree-topology.svg">
  <img src="../assets/typhon-resource-tree-topology.svg"
       alt="Resource Tree — Full topology showing Root with 6 subsystems and their component nodes with metric kinds">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-resource-tree-topology.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

---

## 8.3 Metric Kinds

Each resource node declares which **metric kinds** it exposes. There are 6 kinds, each with specific sub-measurements:

### Kind 1: Memory

Tracks byte allocations owned by this node (and transitively, its children).

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `AllocatedBytes` | Gauge | Current live allocation |
| `PeakBytes` | High-water | Maximum ever observed (resettable) |

**Implementation pattern**: Components with pinned/managed arrays already know their size. For collections, an estimation heuristic is acceptable (e.g., `Dictionary<K,V>` capacity × entry size).

```csharp
// Already exists in IMemoryResource:
public interface IMemoryResource : IResource
{
    int Size { get; }  // ← This IS the Memory.AllocatedBytes for leaves
}

// For aggregate nodes, Memory.AllocatedBytes = sum of children's Memory.AllocatedBytes
```

**Collection estimation** (for nodes tracking dictionaries, lists, etc.):

| Collection | Estimation Formula | Accuracy |
|-----------|-------------------|----------|
| `T[]` | `Length × sizeof(T)` | Exact |
| `List<T>` | `Capacity × sizeof(T)` | Exact (managed allocation) |
| `Dictionary<K,V>` | `Capacity × (sizeof(K) + sizeof(V) + 12)` | ~90% (hash table overhead) |
| `ConcurrentDictionary<K,V>` | `Count × (sizeof(K) + sizeof(V) + 48)` | ~80% (node + lock overhead) |
| `HashSet<T>` | `Capacity × (sizeof(T) + 12)` | ~90% |

### Kind 2: Capacity

Tracks utilization of a bounded slot-based structure.

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `Current` | Gauge | Slots/entries currently used |
| `Maximum` | Config | Total slots available |
| `Utilization` | Derived | `Current / Maximum` (0.0–1.0) |

**Examples**: Page cache pages (used/total), transaction pool (active/max), WAL ring buffer (used bytes/capacity), bitmap bits (set/total).

```csharp
// Already exists in codebase:
// BlockAllocatorBase: Capacity, AllocatedCount
// ConcurrentBitmapL3All: Capacity, TotalBitSet, IsFull
// PagedMMF.Metrics: FreeMemPageCount, TotalMemPageAllocatedCount
```

### Kind 3: DiskIO

Tracks read/write operations to persistent storage.

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `ReadOps` | Counter | Number of read operations |
| `WriteOps` | Counter | Number of write operations |
| `ReadBytes` | Counter | Total bytes read |
| `WriteBytes` | Counter | Total bytes written |

**Implementation**: Plain `int` fields incremented on each I/O call. Already partially exists in `PagedMMF.Metrics` (`ReadFromDiskCount`, `PageWrittenToDiskCount`, `WrittenOperationCount`).

### Kind 4: Contention

Tracks lock/latch wait behavior. This is the key metric for detecting CPU starvation and concurrency bottlenecks.

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `WaitCount` | Counter | Times a thread had to wait (not immediate acquisition) |
| `TotalWaitUs` | Counter | Cumulative microseconds spent waiting |
| `MaxWaitUs` | High-water | Longest single wait observed |
| `TimeoutCount` | Counter | Waits that exceeded Deadline |

**Granularity decision**: Individual `AccessControl` latches do NOT get their own graph nodes. Instead, the *owning component* (e.g., `PageCache`, `ComponentTable<T>`) aggregates contention across all its latches via the **callback pattern**.

#### The IContentionTarget Callback Pattern

Resources that want contention telemetry implement `IContentionTarget`. When acquiring locks, they pass themselves to the lock methods, and the lock calls back on contention events:

```csharp
/// <summary>
/// Interface for resources that want to receive contention telemetry from locks.
/// </summary>
public interface IContentionTarget
{
    /// <summary>Current telemetry level. Use volatile field for thread-safe reads.</summary>
    TelemetryLevel TelemetryLevel { get; }

    /// <summary>Optional link to owning IResource for graph integration.</summary>
    IResource OwningResource { get; }

    /// <summary>Light mode: Record that contention occurred (thread had to wait).</summary>
    void RecordContention(long waitUs);

    /// <summary>Deep mode: Log a detailed lock operation.</summary>
    void LogLockOperation(LockOperation operation, long durationUs);
}

/// <summary>Per-resource telemetry granularity.</summary>
public enum TelemetryLevel
{
    None = 0,   // Zero overhead — null check only
    Light = 1,  // Aggregate counters (WaitCount, TotalWaitUs)
    Deep = 2    // Full operation history with timestamps
}
```

**Usage in lock methods** — `IContentionTarget` is always the last parameter, `null` means no telemetry:

```csharp
// Lock methods accept optional IContentionTarget
public bool EnterExclusiveAccess(TimeSpan? timeOut = null,
    CancellationToken token = default, IContentionTarget target = null)

// Resource passes itself when acquiring locks
_latch.EnterExclusiveAccess(target: this);  // 'this' implements IContentionTarget
```

**Example implementation** — a resource aggregating its latches' contention:

```csharp
public class PageCacheResource : IResource, IMetricSource, IContentionTarget
{
    // Telemetry level can be changed at runtime (volatile for thread-safety)
    private volatile TelemetryLevel _telemetryLevel = TelemetryLevel.Light;
    public TelemetryLevel TelemetryLevel => _telemetryLevel;
    public IResource OwningResource => this;

    // Aggregate counters updated by lock callbacks
    internal long ContentionWaitCount;
    internal long ContentionTotalUs;
    internal long ContentionMaxUs;

    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref ContentionWaitCount);
        Interlocked.Add(ref ContentionTotalUs, waitUs);
        // Update max atomically (compare-exchange loop)
    }

    public void LogLockOperation(LockOperation operation, long durationUs)
    {
        // Deep mode: append to operation log via ResourceTelemetryAllocator
    }

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteContention(ContentionWaitCount, ContentionTotalUs,
                              ContentionMaxUs, 0);
    }
}
```

**CPU cost measurement**: Since we already use `Stopwatch` for Deadline timeout tracking, reusing the elapsed time for contention duration is nearly free — the Stopwatch is already started when we enter a wait loop.

**Zero overhead when disabled**: When `target` is `null` or `TelemetryLevel` is `None`, the lock code skips all telemetry paths with simple null/enum checks that the JIT can optimize.

### Kind 5: Throughput

Tracks monotonically increasing operation counters.

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `Count` | Counter | Total operations since startup |

Named per-operation: a node can declare multiple throughput metrics (e.g., `Lookups`, `Inserts`, `Splits` on a B+Tree index).

**Implementation**: Plain `int` or `long` field, incremented on each operation. The rate (ops/sec) is derived by the consumer ([Observability](09-observability.md)) by differencing two snapshots.

```csharp
// Already exists in PagedMMF.Metrics:
public int MemPageCacheHit;   // Throughput: cache hits
public int MemPageCacheMiss;  // Throughput: cache misses
```

### Kind 6: Duration

Tracks time cost of discrete operations (start/stop pairs).

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `LastUs` | Gauge | Duration of the most recent operation |
| `AvgUs` | Rolling | Exponential moving average |
| `MaxUs` | High-water | Longest operation observed (resettable) |

**Implementation**: Uses `Stopwatch` (already needed for Deadline). After an operation completes, update the node's duration fields:

```csharp
internal long CheckpointLastUs;
internal long CheckpointMaxUs;
internal long CheckpointAvgNumerator;  // Sum for computing average
internal long CheckpointCount;         // Division denominator
```

**When to use Duration vs Contention**:
- **Contention** = time spent *waiting for a resource* (involuntary)
- **Duration** = time spent *doing useful work* (voluntary)

### Metric Kind Summary

| Kind | What It Answers | Hot-Path Cost | Update Pattern |
|------|----------------|---------------|----------------|
| Memory | "How many bytes does this own?" | 0 (read existing Size) | At allocation/deallocation |
| Capacity | "How full is this?" | 0 (read existing counters) | At allocate/free slot |
| DiskIO | "How much I/O is this doing?" | ~1 ns (field increment) | Per I/O operation |
| Contention | "Is this a bottleneck?" | ~5 ns (Stopwatch reuse) | On lock wait (not acquisition) |
| Throughput | "How many ops/sec?" | ~1 ns (field increment) | Per operation |
| Duration | "How long do operations take?" | ~5 ns (Stopwatch stop) | Per operation completion |

---

## 8.4 Granularity Strategy

### The Granularity Problem

Typhon has thousands of latches, millions of chunks, and potentially hundreds of index instances. Making each one a graph node would be insane — both in memory and in snapshot cost. The granularity strategy defines what gets a node.

### Rules

| Level | Gets a Node? | Rule | Examples |
|-------|:---:|------|----------|
| **System** | ✅ | One per engine | `Root` |
| **Subsystem** | ✅ | One per architectural component | `Storage`, `DataEngine`, `Durability` |
| **Component** | ✅ | One per significant instance | `PageCache`, `WALRingBuffer`, `TransactionPool` |
| **Per-type Instance** | ✅ | One per registered component type | `ComponentTable<Player>`, `PrimaryKeyIndex<Player>` |
| **Per-entity** | ❌ | Individual entities, revision chains, pages | One player's revision chain |
| **Per-primitive** | ❌ | Individual latches, bitmap words, chunks | One page latch, one chunk slot |

### The "Owner Aggregates" Pattern

For fine-grained primitives that don't get their own nodes, the **owning component** maintains aggregate counters. The mechanism is the `IContentionTarget` callback interface (see [Kind 4: Contention](#kind-4-contention)):

1. The resource (e.g., `ComponentTable<T>`) implements `IContentionTarget`
2. When acquiring any of its latches, it passes `this` as the `target` parameter
3. The latch calls back to `RecordContention()` when threads have to wait
4. All latches' contention metrics aggregate into the single resource node

This inverts the traditional pattern where latches push metrics — instead, the resource *pulls* by passing itself to lock methods, and the lock *pushes back* via callbacks.

<a href="../assets/typhon-owner-aggregates-pattern.svg">
  <img src="../assets/typhon-owner-aggregates-pattern.svg" width="1200"
       alt="Owner Aggregates Pattern — ComponentTable graph node with aggregated metrics, non-node latches, and child PrimaryKeyIndex node">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-owner-aggregates-pattern.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

### When to Add Depth

The 3-level target (System → Component → Instance) is a guideline. Add a 4th level when:

1. **The instance has its own distinct resource pool** — e.g., a ComponentTable's PrimaryKeyIndex has its own chunk allocator separate from the revision segment
2. **The instance has meaningfully different metrics** — e.g., distinguishing the PrimaryKeyIndex (lookups) from the RevisionSegment (capacity) under the same ComponentTable
3. **Debugging requires it** — when you need to isolate "which specific index is the bottleneck?"

Do NOT add depth for:
- Structural symmetry ("every layer should have children")
- Completeness ("every allocation should be visible")
- Individual entities or operations

### Cost Model

Each graph node costs:

| Cost | Per Node | Rationale |
|------|----------|-----------|
| Memory | ~200 bytes | `ResourceNode` fields + `ConcurrentDictionary` overhead (empty = ~100B) |
| Snapshot read | ~50 ns | Read 4–8 fields + write to snapshot buffer |
| Registration | Once | `RegisterChild()` at startup, not hot path |

For a typical Typhon instance with ~50–100 graph nodes, the total overhead is:
- Memory: ~10–20 KB (negligible)
- Snapshot cost: ~5–10 μs for a full tree walk (acceptable for a diagnostic operation)

---

## 8.5 Resource Inventory

The 35 resources from the previous design, reorganized as a tree with metric declarations per node:

### Tree Structure with Metrics

```
Root ─────────────────────────────────────────────────────── [Memory(aggregate)]
│
├── Storage ──────────────────────────────────────────────── [Memory(aggregate)]
│   ├── PageCache ────────────────────────── [Memory, Capacity, DiskIO, Contention]
│   │   • 256–16384 pages (2 MB – 128 MB), clock-sweep eviction
│   │   • Config: PagedMMFOptions.PageCacheSize
│   │   • Exhaustion: Evict → Wait (if all pinned)
│   │
│   ├── SegmentManager ──────────────────────────────── [Capacity, Throughput]
│   │   ├── Segment: ComponentSegment<T> ────────────── [Memory, Capacity]
│   │   ├── Segment: RevisionSegment<T> ─────────────── [Memory, Capacity]
│   │   ├── Segment: IndexSegment<T> ────────────────── [Memory, Capacity]
│   │   └── Segment: StringTableSegment ─────────────── [Memory, Capacity]
│   │
│   └── ChunkAccessorCache ──────────────────────────── [Capacity, Throughput]
│       • 16 SIMD-optimized slots per accessor
│       • Exhaustion: LRU eviction (self-healing)
│
├── DataEngine ──────────────────────────────────────── [Memory(aggregate)]
│   ├── TransactionPool ────────────────── [Capacity, Throughput, Duration]
│   │   • 16 pooled instances, active count ≤ 1000
│   │   • Config: ResourceOptions.MaxActiveTransactions
│   │   • Exhaustion: FailFast beyond max
│   │
│   ├── ComponentTable<T₁> ─────────── [Memory, Contention, Throughput]
│   │   ├── PrimaryKeyIndex ────────── [Capacity, Throughput, Duration]
│   │   └── SecondaryIndex<Field> ──── [Capacity, Throughput]
│   │
│   ├── ComponentTable<T₂> ─────────── ...
│   └── ...
│
├── Durability ───────────────────────────────────────────── [Memory(aggregate)]
│   ├── WALRingBuffer ────────────────── [Memory, Capacity, Contention, Throughput]
│   │   • 4 MB MPSC ring, back-pressure at 80%
│   │   • Config: ResourceOptions.WalRingBufferSizeBytes
│   │   • Exhaustion: Wait (back-pressure)
│   │
│   ├── WALSegments ─────────────────────────── [DiskIO, Capacity]
│   │   • 4 × 64 MB pre-allocated files
│   │   • Exhaustion: Force checkpoint → recycle
│   │
│   ├── Checkpoint ──────────────────────────── [Throughput, Duration, DiskIO]
│   │   • Tracks dirty page count, flush duration, pages flushed
│   │
│   └── UoWEpochRegistry ────────────────────── [Capacity, Throughput]
│       • 253 entries per page, overflow on demand
│
├── Backup ───────────────────────────────────────────────── [Memory(aggregate)]
│   ├── ShadowBuffer ─────────────────── [Memory, Capacity, Contention]
│   │   • 512 pages (4 MB) pinned ring
│   │   • Exhaustion: Wait (back-pressure on writers)
│   │
│   └── SnapshotStore ────────────────────────── [DiskIO, Capacity]
│       • Bounded by retention policy (MaxCount/MaxAge/MaxTotalSize)
│
├── Execution ────────────────────────────────────────────── (grouping only)
│   ├── Watchdog ─────────────────────────────── [Throughput]
│   ├── GroupCommitTimer ─────────────────────── [Throughput, Duration]
│   └── CheckpointTimer ─────────────────────── [Throughput]
│
└── Allocation ───────────────────────────────────────────── [Memory(aggregate)]
    ├── MemoryAllocator ─────────────────────── [Memory, Throughput]
    │   ├── PinnedMemoryBlock: PageCacheBuffer ── [Memory]
    │   ├── PinnedMemoryBlock: WALBuffer ──────── [Memory]
    │   └── ... (all tracked allocations)
    │
    ├── BlockAllocators ─────────────────────── [Capacity, Throughput]
    │   └── ChainedBlockAllocator<AccessOps> ── [Capacity] (#if TELEMETRY only)
    │
    └── OccupancyBitmaps ────────────────────── [Capacity]
        • TotalBitSet / BankBitCountCapacity per bitmap
```

### Resource Count by Metric Kind

| Metric Kind | Nodes Declaring It | Examples |
|------------|-------------------|----------|
| Memory | ~15 | PageCache, ShadowBuffer, each PinnedMemoryBlock, aggregate subsystems |
| Capacity | ~20 | PageCache, TransactionPool, WALRing, Bitmaps, Segments, Indexes |
| DiskIO | ~5 | PageCache, WALSegments, Checkpoint, SnapshotStore |
| Contention | ~5 | PageCache, WALRingBuffer, ShadowBuffer, ComponentTable<T> |
| Throughput | ~15 | TransactionPool, Indexes, Checkpoint, CacheHit/Miss, WAL |
| Duration | ~5 | TransactionPool (tx lifetime), Checkpoint, GroupCommit |

**Total estimated nodes**: 50–100 depending on registered component types and indexes.

---

## 8.6 Resource Lifecycle

Resources follow one of four lifecycle patterns:

### Pattern 1: Static Allocation (Create Once, Use Forever)

```
Engine Start → Allocate → Use ─────────────────→ Engine Stop → Free
```

**Resources**: PageCache buffer, WAL ring buffer, occupancy bitmaps, watchdog thread.

Size is fixed at creation. Graph node is registered once at startup, never changes topology.

### Pattern 2: Pooled Recycling (Create Pool, Borrow/Return)

```
         ┌─────── Return ───────┐
         │                      │
Pool ──→ Borrow ──→ Use ──→ Release
         │                      │
         └─── Create if empty ──┘
```

**Resources**: Transaction pool, UnitOfWorkContext pool, UoW epoch slots.

Capacity metric tracks `borrowed / poolSize`. Throughput tracks borrow/return rate.

### Pattern 3: Growable Chain (Allocate on Demand, Reclaim Later)

```
Allocate Chunk₁ → Fill → Allocate Chunk₂ → Fill → ... → GC oldest
```

**Resources**: Revision chains, B+Tree nodes, variable buffers, segment pages, block allocators.

Capacity metric tracks allocated chunks. These are bounded *transitively* through the page cache — growable segments consume pages from the fixed-size cache.

### Pattern 4: Ring Buffer (Producer/Consumer with Back-Pressure)

```
         ┌──── Back-pressure when full ────┐
         ↓                                  │
Produce ──→ [Buffer] ──→ Consume ──→ Reclaim
```

**Resources**: WAL ring buffer, CoW shadow buffer.

Capacity metric tracks fill level. Contention metric tracks back-pressure wait events.

### Cross-Component Resource Flow

A single `Transaction.Commit()` touches resources across multiple subtrees:

```
Transaction.Commit()
    │
    ├─→ DataEngine/TransactionPool ──→ [Capacity check]
    ├─→ DataEngine/ComponentTable<T>/RevisionSegment ──→ [Capacity: allocate chunks]
    │       └─→ Storage/SegmentManager ──→ [Capacity: allocate pages if needed]
    │             └─→ Storage/PageCache ──→ [Capacity: evict if full]
    │                   └─→ [Contention: page latch wait]
    ├─→ DataEngine/ComponentTable<T>/PrimaryKeyIndex ──→ [Throughput: insert]
    ├─→ Durability/WALRingBuffer ──→ [Capacity: write record, back-pressure if full]
    │       └─→ [Contention: CAS spin on ring head]
    ├─→ Durability/Checkpoint ──→ [Throughput: dirty page count++]
    └─→ DataEngine/TransactionPool ──→ [Throughput: return to pool]
```

This flow shows why the resource graph must be *hierarchical* — a single operation cascades through multiple subtrees, and flat counters can't show the dependency chain.

---

## 8.7 Budgets & Exhaustion

### Budget Hierarchy

The total memory budget is subdivided. Each subsystem manages its own allocation:

```
Total Budget (configurable, default 4 GB)
├── Storage/PageCache ─────────────── (2 GB) ── Fixed at startup
├── Durability/WAL ────────────────── (260 MB) ── Ring + segments
├── DataEngine/Transactions ───────── (256 MB) ── Active state + revision chains
├── DataEngine/Indexes ────────────── (1 GB) ── B+Tree nodes + variable buffers
└── Working Memory ────────────────── (508 MB) ── Query, compression, misc
```

### Configuration Classes

Resource limits and timeout budgets are configured via two sub-objects on `DatabaseEngineOptions`:

- **`ResourceOptions`** — Memory budgets, pool sizes, capacity limits
- **`TimeoutOptions`** — Per-subsystem lock acquisition timeouts (see [01-concurrency.md](01-concurrency.md), [design/errors/02-deadline-propagation.md](../design/errors/02-deadline-propagation.md))

### ResourceOptions Configuration

```csharp
public class ResourceOptions
{
    // ─── Page Cache ───────────────────────────────────────────
    public int PageCachePages { get; set; } = 256;           // 2 MB default
    public int MaxPageCachePages { get; set; } = 16384;      // 128 MB max

    // ─── Transactions ─────────────────────────────────────────
    public int MaxActiveTransactions { get; set; } = 1000;
    public int TransactionPoolSize { get; set; } = 16;

    // ─── WAL & Durability ─────────────────────────────────────
    public int WalRingBufferSizeBytes { get; set; } = 4 * 1024 * 1024;  // 4 MB
    public long WalMaxSegmentSizeBytes { get; set; } = 64L << 20;       // 64 MB
    public int WalMaxSegments { get; set; } = 4;

    // ─── Backup ───────────────────────────────────────────────
    public int ShadowBufferPages { get; set; } = 512;        // 4 MB

    // ─── Overall ──────────────────────────────────────────────
    public long TotalMemoryBudgetBytes { get; set; } = 4L << 30;  // 4 GB
}
```

### Exhaustion Policies

When a bounded resource reaches its limit:

| Policy | Behavior | When Used |
|--------|----------|-----------|
| **FailFast** | Throw `ResourceExhaustedException` immediately | Active transactions at max |
| **Wait** | Block caller until freed (respects Deadline) | Page latches, WAL ring |
| **Evict** | Remove least-used entry, retry | Page cache, chunk accessor cache |
| **Degrade** | Continue with reduced performance | Transaction pool empty (alloc new) |

Each `ResourceNode` in the graph carries an `ExhaustionPolicy` property documenting which policy applies. This is diagnostic metadata — not used for runtime dispatch (D12). Set at construction/registration time. See [design/errors/03-exhaustion-policy.md](../design/errors/03-exhaustion-policy.md) for the enforcement design.

### Back-Pressure Patterns

Three resources use producer-blocks-when-consumer-is-behind:

| Resource | Trigger | Effect | Resolution |
|----------|---------|--------|------------|
| WAL Ring Buffer | > 80% fill | Commit threads block | WAL Writer drains + fsync |
| CoW Shadow Buffer | All slots occupied | Page writers block | Backup writer reads + frees |
| Checkpoint Dirty Pages | > threshold | Checkpoint fires sooner | Flush dirty pages to disk |

### Graph-Based Cascade Detection

Instead of a hard-coded priority table, the resource graph can mechanically trace cascades by following dependency edges:

```csharp
/// <summary>
/// Traces back from a symptomatic node to find the root cause.
/// Follows Capacity.Utilization upstream until finding the node
/// that is full AND has no upstream pressure.
/// </summary>
ResourceNode FindRootCause(ResourceNode symptom)
{
    // Walk parent/dependency edges looking for the highest-utilization ancestor
    // that isn't itself blocked by something upstream
}
```

**Example cascade**:
1. `TransactionPool` shows `Capacity.Utilization = 0.95` (symptom: transactions stalling)
2. Trace upstream: transactions are blocked in `Commit()`, which is waiting on...
3. `WALRingBuffer` shows `Capacity.Utilization = 0.92` (root cause: WAL can't drain fast enough)
4. The WAL Writer is doing disk I/O, check `WALSegments.DiskIO.WriteOps` for abnormal latency

The graph makes this traversal mechanical rather than requiring operator intuition.

### Budget Enforcement Model

| Level | Mechanism | Overhead | When |
|-------|-----------|----------|------|
| **Strict** | Counter check before every allocation | ~5 ns | Multi-tenant, production |
| **Sampled** | Check every N allocations (N=64) | Near-zero | Single-tenant, low-latency |

Default is **Sampled** — check when a new page is allocated or every 64 chunk allocations (whichever comes first).

---

## 8.8 Snapshot & Query API

### Snapshot Semantics

A snapshot is a **consistent-enough** reading of all metric values across the tree. "Consistent-enough" means:

- Each node's metrics are read atomically (a single call to `ReadMetrics()` reads all fields of that node together)
- Different nodes may be read at slightly different instants (μs apart)
- Total snapshot time: ~5–10 μs for 50–100 nodes (acceptable for a diagnostic operation, not hot-path)

### Core API

```csharp
public interface IResourceGraph
{
    /// <summary>
    /// Walk the entire tree, read all IMetricSource nodes, return immutable snapshot.
    /// Cost: ~50ns per node × number of nodes. No allocations on hot path (pooled buffers).
    /// </summary>
    ResourceSnapshot GetSnapshot();

    /// <summary>
    /// Read metrics for a single subtree only.
    /// Useful when you know which subsystem to inspect.
    /// </summary>
    ResourceSnapshot GetSnapshot(IResource subtreeRoot);

    /// <summary>
    /// The tree root. Useful for enumeration/discovery.
    /// </summary>
    IResource Root { get; }
}
```

### ResourceSnapshot Structure

```csharp
public class ResourceSnapshot
{
    public DateTime Timestamp { get; init; }

    /// <summary>All nodes with their metric values, keyed by node path (e.g., "Storage/PageCache")</summary>
    public IReadOnlyDictionary<string, NodeSnapshot> Nodes { get; init; }

    // ─── Aggregation Queries ────────────────────────────────────

    /// <summary>Sum Memory.AllocatedBytes across all descendants of the given node.</summary>
    public long GetSubtreeMemory(string nodePath);

    /// <summary>Find the node with highest Capacity.Utilization in the tree.</summary>
    public NodeSnapshot FindMostUtilized();

    /// <summary>Find nodes where Contention.WaitCount > 0, sorted by TotalWaitUs descending.</summary>
    public IEnumerable<NodeSnapshot> FindContentionHotspots();

    /// <summary>Compute rate (ops/sec) by differencing this snapshot with a previous one.</summary>
    public ThroughputRates ComputeRates(ResourceSnapshot previousSnapshot);
}

public class NodeSnapshot
{
    public string Path { get; init; }         // "Storage/PageCache"
    public string Id { get; init; }           // "PageCache"
    public ResourceType Type { get; init; }

    // Each metric kind is nullable — only present if the node declares it
    public MemoryMetrics? Memory { get; init; }
    public CapacityMetrics? Capacity { get; init; }
    public DiskIOMetrics? DiskIO { get; init; }
    public ContentionMetrics? Contention { get; init; }
    public IReadOnlyList<ThroughputMetric>? Throughput { get; init; }
    public IReadOnlyList<DurationMetric>? Duration { get; init; }
}

// Metric value structs (immutable, captured at snapshot time)
public readonly record struct MemoryMetrics(long AllocatedBytes, long PeakBytes);
public readonly record struct CapacityMetrics(long Current, long Maximum, double Utilization);
public readonly record struct DiskIOMetrics(long ReadOps, long WriteOps, long ReadBytes, long WriteBytes);
public readonly record struct ContentionMetrics(long WaitCount, long TotalWaitUs, long MaxWaitUs, long TimeoutCount);
public readonly record struct ThroughputMetric(string Name, long Count);
public readonly record struct DurationMetric(string Name, long LastUs, long AvgUs, long MaxUs);
```

### Usage Examples

```csharp
// Diagnostic: "Why is the engine slow?"
var snapshot = _resourceGraph.GetSnapshot();
var bottleneck = snapshot.FindMostUtilized();
// → NodeSnapshot { Path: "Durability/WALRingBuffer", Capacity: { Utilization: 0.93 } }

// Attribution: "How much memory does DataEngine use?"
var dataEngineMemory = snapshot.GetSubtreeMemory("DataEngine");
// → 847_000_000 (847 MB across all component tables, indexes, revision chains)

// Contention analysis: "Where are threads waiting?"
var hotspots = snapshot.FindContentionHotspots().Take(3);
// → [PageCache: 120μs total, WALRingBuffer: 45μs total, ComponentTable<Player>: 12μs total]

// Rate computation: "What's the throughput?"
var prev = _lastSnapshot;
var rates = snapshot.ComputeRates(prev);
// → TransactionPool.Committed: 15,000 ops/sec
// → PageCache.Hits: 890,000 ops/sec
```

### Snapshot Frequency

The snapshot is **on-demand, not periodic**. Consumers decide when to call `GetSnapshot()`:

| Consumer | Frequency | Reason |
|----------|-----------|--------|
| [Observability](09-observability.md) OTel metrics export | Every 1–5 seconds | Feed gauges to Prometheus/Mimir |
| [Observability](09-observability.md) Health checks | Every 1 second | Detect degradation |
| Exhaustion policy | On threshold cross | Decide whether to back-pressure |
| Developer debugging | On demand | Manual inspection via API |
| Automated root-cause | On alert trigger | Walk tree to find bottleneck |

---

## 8.9 Bridge to Observability

The resource graph is the **single source of truth** for the observability system. [Observability](09-observability.md) is a consumer, not an owner of resource state.

### Data Flow

<a href="../assets/typhon-resource-snapshot-flow.svg">
  <img src="../assets/typhon-resource-snapshot-flow.svg" width="1000"
       alt="Resource Snapshot — Data flow from hot-path counters through graph snapshot to observability consumers">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-resource-snapshot-flow.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

### Mapping: Graph Nodes → OTel Metrics

Each `NodeSnapshot` in the resource graph maps to OTel metrics following the naming convention `typhon.resource.{path}.{metric}`:

```
NodeSnapshot: "Storage/PageCache"
  Memory.AllocatedBytes    → typhon.resource.storage.page_cache.memory_bytes (Gauge)
  Capacity.Current         → typhon.resource.storage.page_cache.used (Gauge)
  Capacity.Maximum         → typhon.resource.storage.page_cache.max (Gauge)
  Capacity.Utilization     → typhon.resource.storage.page_cache.utilization (Gauge)
  DiskIO.ReadOps           → typhon.resource.storage.page_cache.read_ops (Counter)
  DiskIO.WriteOps          → typhon.resource.storage.page_cache.write_ops (Counter)
  Contention.WaitCount     → typhon.resource.storage.page_cache.contention_waits (Counter)
  Contention.TotalWaitUs   → typhon.resource.storage.page_cache.contention_us (Counter)
  Throughput["CacheHits"]  → typhon.resource.storage.page_cache.cache_hits (Counter)
  Throughput["CacheMisses"]→ typhon.resource.storage.page_cache.cache_misses (Counter)
```

### Mapping: Graph → Health Checks

Health checks in [Observability](09-observability.md) are derived from `Capacity.Utilization` readings:

| Graph Node | Healthy | Degraded | Unhealthy |
|-----------|---------|----------|-----------|
| Storage/PageCache | < 80% | 80–95% | > 95% |
| Durability/WALRingBuffer | < 60% | 60–80% | > 80% |
| DataEngine/TransactionPool | < 60% | 60–80% | > 80% |
| Durability/Checkpoint (dirty lag) | < 50% | 50–90% | > 90% |

Composite health = worst of all checks. Root cause = `FindMostUtilized()` on the graph snapshot.

### Mapping: Graph → Root-Cause Alerts

When health transitions to Unhealthy, [Observability](09-observability.md) uses the graph to generate an alert with attribution:

```
ALERT: Typhon Health Unhealthy
  Root Cause: Durability/WALRingBuffer (Capacity.Utilization = 0.93)
  Cascading: DataEngine/TransactionPool (Contention.WaitCount += 47 in last 5s)
  Impact: Transactions blocked on WAL back-pressure
  Resolution: WAL Writer I/O latency elevated (Duration.AvgUs = 850μs, normal: 200μs)
```

### What Observability Does NOT Own

The observability layer does NOT:
- Maintain counters (components do)
- Define resource structure (the graph does)
- Decide exhaustion policy ([Budgets & Exhaustion](#87-budgets--exhaustion) does)
- Track resource lifecycle (components do)

[Observability](09-observability.md) is purely a **read and export** layer. It consumes snapshots and converts them to OTel signals.

---

## Code Locations

| Component | Location | Status |
|-----------|----------|--------|
| IResource | `src/Typhon.Engine/Resources/IResource.cs` | ✅ Exists |
| ResourceNode | `src/Typhon.Engine/Resources/ResourceNode.cs` | ✅ Exists |
| ResourceRegistry | `src/Typhon.Engine/Resources/ResourceRegistry.cs` | ✅ Exists |
| IMemoryResource | `src/Typhon.Engine/Memory/MemoryBlockBase.cs` | ✅ Exists |
| MemoryAllocator | `src/Typhon.Engine/Memory/MemoryAllocator.cs` | ✅ Exists |
| PinnedMemoryBlock | `src/Typhon.Engine/Memory/PinnedMemoryBlock.cs` | ✅ Exists |
| PagedMMF.Metrics | `src/Typhon.Engine/Storage/PagedMMF.Metrics.cs` | ✅ Exists |
| ConcurrentBitmapL3All : IResource | `src/Typhon.Engine/Collections/ConcurrentBitmapL3All.cs` | ✅ Exists |
| BlockAllocatorBase (Capacity/Count) | `src/Typhon.Engine/Memory/Allocators/BlockAllocatorBase.cs` | ✅ Exists |
| TyphonBuilderExtensions (DI setup) | `src/Typhon.Engine/Hosting/TyphonBuilderExtensions.cs` | ✅ Exists |
| **IContentionTarget** | `src/Typhon.Engine/Observability/IContentionTarget.cs` | ✅ Exists |
| **TelemetryLevel** | `src/Typhon.Engine/Observability/TelemetryLevel.cs` | ✅ Exists |
| **LockOperation** | `src/Typhon.Engine/Observability/LockOperation.cs` | ✅ Exists |
| **ResourceOperationEntry** | `src/Typhon.Engine/Observability/ResourceOperationEntry.cs` | ✅ Exists |
| **ResourceTelemetryAllocator** | `src/Typhon.Engine/Observability/ResourceTelemetryAllocator.cs` | ✅ Exists |
| IMetricSource | `src/Typhon.Engine/Resources/IMetricSource.cs` | ✅ Exists |
| IResourceGraph | `src/Typhon.Engine/Resources/IResourceGraph.cs` | ✅ Exists |
| ResourceSnapshot | `src/Typhon.Engine/Resources/ResourceSnapshot.cs` | ✅ Exists |
| ResourceOptions | `src/Typhon.Engine/Resources/ResourceOptions.cs` | ✅ Exists |
| ExhaustionPolicy | `src/Typhon.Engine/Resources/ExhaustionPolicy.cs` | ✅ Exists |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| **Graph model** | Extend existing `IResource` tree with `IMetricSource` | Don't replace working infrastructure; additive evolution |
| **Metric ownership** | Components maintain plain fields; graph reads them | Zero hot-path overhead; snapshot is the only cost |
| **Snapshot trigger** | Pull-based (on demand) | No background threads, no timers, consumer decides frequency |
| **Consistency** | Per-node atomic, cross-node approximate | Full tree lock would be too expensive; ±1 is acceptable |
| **Granularity** | 3-level guideline (not enforced) | System → Component → Instance covers 95% of cases; allow deeper when justified |
| **Individual latches** | Not graph nodes; owner aggregates contention | Thousands of latches → thousands of nodes is insane; aggregate at component level |
| **Collection memory estimation** | Heuristic formulas (Capacity × entry size) | Exact tracking would require wrapping every collection; 80-90% accuracy is sufficient |
| **Budget enforcement** | Sampled (every 64 allocs) by default | Amortize check cost; page-level catches overruns anyway |
| **Cascade detection** | Graph traversal (mechanical) | Replaces hard-coded priority tables; adapts to actual topology |
| **Configuration timing** | Set at startup, immutable | Avoid runtime complexity; budget changes require restart |
| **Snapshot cost** | ~5–10 μs for full tree | Acceptable for 1–5s polling; not hot-path |
| **Metric value types** | readonly record structs | Immutable snapshot values; zero-alloc on stack in common case |
| **Exhaustion default** | FailFast for transactions, Evict for caches, Wait for WAL | Shed load at edge; caches self-heal; WAL needs durability guarantee |
| **Back-pressure threshold** | 80% utilization | Leave headroom for burst before hard block |
| **Pinned memory** | Allocate at startup, keep forever | Avoid GC heap fragmentation from dynamic pin/unpin |

---

## Open Questions

1. **Hot-resize budgets** — Should budgets be adjustable at runtime (e.g., shrink page cache to grow indexes)? Current design says restart-required, but long-running servers may benefit from hot-resize.

2. **Pressure callbacks** — Should we expose `OnPressure(node, level)` for adaptive behavior? E.g., query engine reduces sort buffer when page cache is under pressure.

3. **Per-component-type budgets** — Should `ComponentTable<Player>` have a different memory budget than `ComponentTable<Inventory>`? Useful for multi-tenant but adds configuration complexity.

4. **GC integration** — Should the resource graph react to `GC.RegisterForFullGCNotification`? Could pre-emptively evict cache entries before Gen2 pause.

5. **Dependency edges** — Should the graph explicitly model *dependency* edges (not just parent/child)? E.g., "WALRingBuffer depends on WALWriter throughput" is a dependency, not a parent-child. Would enable more precise cascade detection.

6. **Snapshot pooling** — Should `ResourceSnapshot` objects be pooled to avoid GC pressure when polling at 1Hz? Or is the allocation trivial enough (~few KB) to ignore?

7. **Historical snapshots** — Should the graph keep the last N snapshots in a ring buffer for trend analysis (rate computation, spike detection)? Or is that the consumer's responsibility?

# 03 — Metric Source Interfaces

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [01-registry.md](01-registry.md)

---

## Overview

The `IMetricSource` and `IMetricWriter` interfaces form the bridge between resource nodes and the metrics system. Not every `IResource` needs to report metrics — only those with measurable state implement `IMetricSource`.

> 💡 **Key insight:** The resource graph *structure* is defined by `IResource`. The *measurements* are provided by `IMetricSource`. These are deliberately separate concerns.

---

## Table of Contents

1. [Design Philosophy](#1-design-philosophy)
2. [IMetricSource Interface](#2-imeticsource-interface)
3. [IMetricWriter Interface](#3-imetricwriter-interface)
4. [Relationship to IResource](#4-relationship-to-iresource)
5. [Relationship to IContentionTarget](#5-relationship-to-icontentiontarget)
6. [Metric Source Discovery](#6-metric-source-discovery)
7. [Implementation Patterns](#7-implementation-patterns)
8. [Thread Safety](#8-thread-safety)
9. [Call Frequency Guidelines](#9-call-frequency-guidelines)
10. [Zero-Allocation Design](#10-zero-allocation-design)
11. [Code Examples](#11-code-examples)

---

## 1. Design Philosophy

### Separation of Concerns

```
IResource        → Tree structure (parent/children)
IMetricSource    → Measurement reporting (optional)
```

Not all resources have metrics. A pure grouping node like "Storage" exists only to organize the tree — it has no counters of its own. Its metrics are the *aggregate* of its children, computed by the snapshot system.

### Pull vs Push

Typhon uses a **pull model**:
- Components maintain plain fields (counters, gauges)
- The graph *reads* these fields during snapshot collection
- No continuous push, no event streams

This keeps the hot path simple — components just increment fields. The cost of reading happens only when someone requests a snapshot.

### Writer Pattern for Zero Allocation

Instead of returning metric objects from `ReadMetrics()`, we pass a **writer** that the source writes to:

```csharp
// BAD - allocates on every read
public MetricSet ReadMetrics() => new MetricSet { Memory = ..., Capacity = ... };

// GOOD - writer buffers internally, no allocation per-source
public void ReadMetrics(IMetricWriter writer) {
    writer.WriteMemory(allocatedBytes, peakBytes);
    writer.WriteCapacity(current, maximum);
}
```

---

## 2. IMetricSource Interface

```csharp
/// <summary>
/// A resource node that can report its current metric values.
/// Not all IResource nodes implement this — only those with measurable state.
/// </summary>
public interface IMetricSource
{
    /// <summary>
    /// Reads current metric values into the provided snapshot builder.
    /// Called during GetSnapshot() — should be fast (read fields, no allocation).
    /// </summary>
    /// <param name="writer">
    /// The writer to report metrics to. Call one or more Write* methods.
    /// </param>
    /// <remarks>
    /// <para>
    /// Implementation requirements:
    /// - Read your fields, write to writer, return quickly
    /// - Don't allocate (no new objects, no LINQ, no string formatting)
    /// - Don't acquire locks (read fields directly, accept slight inconsistency)
    /// - Don't call other IMetricSource.ReadMetrics() — the graph handles recursion
    /// </para>
    /// <para>
    /// The caller (ResourceGraph) ensures single-threaded access to each node's
    /// ReadMetrics during a snapshot, but your fields may be updated concurrently
    /// by other threads.
    /// </para>
    /// </remarks>
    void ReadMetrics(IMetricWriter writer);

    /// <summary>
    /// Resets all high-water mark metrics (PeakBytes, MaxWaitUs, MaxUs, etc.) to current values.
    /// Called by ResourceGraph.ResetPeaks() to enable windowed peak measurements.
    /// </summary>
    /// <remarks>
    /// If this node has no high-water marks, implement as an empty method.
    /// Use Volatile.Write for visibility; full atomicity is not required.
    /// </remarks>
    void ResetPeaks();
}
```

### Key Design Points

1. **Void return** — No allocation, writer buffers internally
2. **Single method** — All metric kinds go through the same writer
3. **No exceptions** — If a metric can't be read, skip it
4. **Fast** — Target < 100ns per node

---

## 3. IMetricWriter Interface

```csharp
/// <summary>
/// Writer interface for reporting metrics during snapshot collection.
/// Implementations buffer values; callers just write.
/// </summary>
public interface IMetricWriter
{
    /// <summary>
    /// Reports memory allocation metrics for this node.
    /// </summary>
    /// <param name="allocatedBytes">Current live allocation in bytes.</param>
    /// <param name="peakBytes">Maximum allocation ever observed (high-water mark).</param>
    void WriteMemory(long allocatedBytes, long peakBytes);

    /// <summary>
    /// Reports capacity utilization metrics for this node.
    /// </summary>
    /// <param name="current">Slots/entries currently used.</param>
    /// <param name="maximum">Total slots available (capacity).</param>
    /// <remarks>
    /// Utilization (current / maximum) is computed by the snapshot builder,
    /// not by the writer. This keeps ReadMetrics fast (no division).
    /// </remarks>
    void WriteCapacity(long current, long maximum);

    /// <summary>
    /// Reports disk I/O metrics for this node.
    /// </summary>
    /// <param name="readOps">Number of read operations (counter).</param>
    /// <param name="writeOps">Number of write operations (counter).</param>
    /// <param name="readBytes">Total bytes read (counter).</param>
    /// <param name="writeBytes">Total bytes written (counter).</param>
    void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes);

    /// <summary>
    /// Reports lock contention metrics for this node.
    /// </summary>
    /// <param name="waitCount">Times a thread had to wait (not immediate acquisition).</param>
    /// <param name="totalWaitUs">Cumulative microseconds spent waiting.</param>
    /// <param name="maxWaitUs">Longest single wait observed (high-water mark).</param>
    /// <param name="timeoutCount">Waits that exceeded Deadline.</param>
    void WriteContention(long waitCount, long totalWaitUs, long maxWaitUs, long timeoutCount);

    /// <summary>
    /// Reports a named throughput counter.
    /// Call multiple times for multiple counters (e.g., "Lookups", "Inserts").
    /// </summary>
    /// <param name="name">Counter name (e.g., "CacheHits", "Commits").</param>
    /// <param name="count">Total operations since startup (monotonically increasing).</param>
    void WriteThroughput(string name, long count);

    /// <summary>
    /// Reports a named duration metric.
    /// Call multiple times for multiple duration types.
    /// </summary>
    /// <param name="name">Operation name (e.g., "Checkpoint", "GroupCommit").</param>
    /// <param name="lastUs">Duration of the most recent operation in microseconds.</param>
    /// <param name="avgUs">Exponential moving average in microseconds.</param>
    /// <param name="maxUs">Longest operation observed (high-water mark).</param>
    void WriteDuration(string name, long lastUs, long avgUs, long maxUs);
}
```

### Writer Implementation Notes

The `ResourceGraph` provides a concrete `IMetricWriter` implementation that:
- Buffers values in pre-allocated structures
- Tracks which metric kinds were written for this node
- Handles multiple `WriteThroughput`/`WriteDuration` calls (appends to lists)
- **Last wins** for fixed methods: if `WriteMemory()` or `WriteCapacity()` is called twice, the second call overwrites the first (no validation overhead)

---

## 4. Relationship to IResource

### Not All Resources Are Metric Sources

```
IResource                     IMetricSource
    │                              │
    ├── Root                       │ (no metrics - pure grouping)
    │   ├── Storage                │ (no metrics - aggregate from children)
    │   │   ├── PageCache ─────────┼── ✅ implements IMetricSource
    │   │   └── ManagedPagedMMF ───┼── ✅ implements IMetricSource
    │   └── DataEngine             │ (no metrics)
    │       └── ComponentTable ────┼── ✅ implements IMetricSource
    │           └── (segments) ────│── (AGGREGATED - Owner Aggregates pattern)
```

> **Note:** Segments do NOT implement IResource or IMetricSource. ComponentTable aggregates segment metrics via the Owner Aggregates pattern — see [05-granularity-strategy.md](05-granularity-strategy.md).

### Implementation Pattern

Resources that have measurable state implement *both* interfaces:

```csharp
public class PageCache : IResource, IMetricSource, IDisposable
{
    // IResource members
    public string Id => "PageCache";
    public ResourceType Type => ResourceType.Cache;
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => Array.Empty<IResource>();
    // ... etc

    // IMetricSource implementation
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteMemory(_bufferSizeBytes, _peakBufferSizeBytes);
        writer.WriteCapacity(_usedSlots, _totalSlots);
        writer.WriteDiskIO(_readOps, _writeOps, _readBytes, _writeBytes);
        writer.WriteContention(_contentionCount, _contentionTotalUs, _contentionMaxUs, _timeoutCount);
        writer.WriteThroughput("CacheHits", _cacheHits);
        writer.WriteThroughput("CacheMisses", _cacheMisses);
    }

    // Metric fields (plain longs, updated on hot path)
    private long _usedSlots;
    private long _totalSlots;
    private long _readOps;
    // ... etc
}
```

### Structural vs Metric Nodes

| Node Type | Implements `IResource` | Implements `IMetricSource` | Example |
|-----------|:----------------------:|:--------------------------:|---------|
| Root/grouping | ✅ | ❌ | "Root", "Storage" |
| Subsystem aggregate | ✅ | ❌ | "DataEngine" |
| Component with state | ✅ | ✅ | PageCache, TransactionPool |
| Instance with state | ✅ | ✅ | ComponentTable<Player> |
| Memory leaf | ✅ (`IMemoryResource`) | ✅ (via base) | PinnedMemoryBlock |

---

## 5. Relationship to IContentionTarget

`IMetricSource` and `IContentionTarget` serve complementary purposes for resources that track lock contention:

```
IContentionTarget                    IMetricSource
      │                                    │
      │  ┌─────────────────────────────┐   │
      │  │     Resource Fields         │   │
      └─►│  _contentionWaitCount       │◄──┘
         │  _contentionTotalUs         │
         │  _contentionMaxUs           │
         └─────────────────────────────┘
              ▲                    │
              │                    ▼
         Locks call            Graph calls
       RecordContention()     ReadMetrics()
         (hot path)           (snapshot)
```

### Interface Purposes

| Interface | Direction | When Called | Purpose |
|-----------|-----------|-------------|---------|
| `IContentionTarget` | **Write** | On lock contention (hot path) | Accumulate contention events |
| `IMetricSource` | **Read** | During snapshot (cold path) | Report accumulated values |

### Implementation Pattern

Resources with locks implement **both** interfaces. The contention counters are:
- **Written** by `IContentionTarget.RecordContention()` when locks call back on contention
- **Read** by `IMetricSource.ReadMetrics()` when the graph takes a snapshot

```csharp
public class PageCache : IResource, IMetricSource, IContentionTarget
{
    // Shared fields — written by IContentionTarget, read by IMetricSource
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;

    // IContentionTarget — called by locks on contention
    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalUs, waitUs);
        if (waitUs > _contentionMaxUs)
            _contentionMaxUs = waitUs;  // Plain write OK for diagnostics
    }

    // IMetricSource — called by graph during snapshot
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                               _contentionMaxUs, 0);
    }

    public void ResetPeaks()
    {
        Volatile.Write(ref _contentionMaxUs, 0);
    }
}
```

### When to Implement Both

| Scenario | IMetricSource | IContentionTarget |
|----------|:-------------:|:-----------------:|
| Resource has metrics but no locks | ✅ | ❌ |
| Resource has locks but no metrics | ❌ | ✅ |
| Resource has locks AND wants contention metrics | ✅ | ✅ |

See [09-observability.md](../../overview/09-observability.md) §9.1.4 for full `IContentionTarget` specification.

---

## 6. Metric Source Discovery

### Runtime Check During Tree Walk

The graph discovers `IMetricSource` implementations during each snapshot's tree walk using a simple `is` check:

```csharp
private void WalkTree(IResource resource, string parentPath,
    Dictionary<string, NodeSnapshot> nodes, MetricWriter writer)
{
    var path = string.IsNullOrEmpty(parentPath)
        ? resource.Id
        : $"{parentPath}/{resource.Id}";

    writer.Reset();

    // Runtime type check — acceptable at snapshot frequency
    if (resource is IMetricSource source)
    {
        source.ReadMetrics(writer);
    }

    nodes[path] = writer.ToNodeSnapshot(path, resource.Id, resource.Type);

    foreach (var child in resource.Children)
    {
        WalkTree(child, path, nodes, writer);
    }
}
```

### Why Runtime Check Is Acceptable

| Approach | Type Check Cost | Complexity |
|----------|-----------------|------------|
| Cached list at registration | ~10-20ns × nodes once | More complex (maintain parallel list) |
| **Runtime check (chosen)** | ~10-20ns × nodes per snapshot | Simpler code, tree walk needed anyway |

**Rationale:**
- Snapshots are taken every 1-5 seconds, not on hot path
- Tree walk is already required for path building and hierarchy
- For 100 nodes: 100 × 15ns = ~1.5μs per snapshot — negligible
- Simpler code is easier to maintain and debug
- Can optimize later if profiling shows it matters

---

## 7. Implementation Patterns

### Pattern 1: Leaf Component with All Metrics

Full implementation for a component that tracks all metric kinds:

```csharp
public class WALRingBuffer : IResource, IMetricSource
{
    // Metric fields (plain longs, no locks needed for reads)
    private long _allocatedBytes;
    private long _peakBytes;
    private long _usedBytes;
    private long _capacityBytes;
    private long _writeOps;
    private long _writeBytes;
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;
    private long _contentionTimeouts;
    private long _recordsWritten;
    private long _flushes;
    private long _lastFlushUs;
    private long _avgFlushUs;
    private long _maxFlushUs;

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteMemory(_allocatedBytes, _peakBytes);
        writer.WriteCapacity(_usedBytes, _capacityBytes);
        writer.WriteDiskIO(0, _writeOps, 0, _writeBytes);
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                              _contentionMaxUs, _contentionTimeouts);
        writer.WriteThroughput("RecordsWritten", _recordsWritten);
        writer.WriteThroughput("Flushes", _flushes);
        writer.WriteDuration("Flush", _lastFlushUs, _avgFlushUs, _maxFlushUs);
    }
}
```

### Pattern 2: Partial Metrics (Not All Kinds)

Components only report what's relevant:

```csharp
public class TransactionPool : IResource, IMetricSource
{
    private long _activeCount;
    private long _maxCount;
    private long _totalCreated;
    private long _totalCommitted;
    private long _totalRolledBack;

    public void ReadMetrics(IMetricWriter writer)
    {
        // Only Capacity and Throughput are relevant
        writer.WriteCapacity(_activeCount, _maxCount);
        writer.WriteThroughput("Created", _totalCreated);
        writer.WriteThroughput("Committed", _totalCommitted);
        writer.WriteThroughput("RolledBack", _totalRolledBack);
        // No Memory, DiskIO, Contention, or Duration for this component
    }
}
```

### Pattern 3: Memory-Only Leaf

For simple memory allocations:

```csharp
public class PinnedMemoryBlock : MemoryBlockBase, IResource, IMetricSource
{
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteMemory(Size, Size); // Size is constant, peak = current
    }
}
```

### Pattern 4: Component with IContentionTarget Integration

When using the `IContentionTarget` callback pattern for lock telemetry:

```csharp
public class ComponentTable : IResource, IMetricSource, IContentionTarget
{
    // Contention counters updated by lock callbacks
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;
    private long _peakMemoryBytes;

    // IContentionTarget implementation
    public TelemetryLevel TelemetryLevel => _telemetryLevel;
    public IResource OwningResource => this;

    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalUs, waitUs);
        if (waitUs > _contentionMaxUs)
            _contentionMaxUs = waitUs;  // Plain write — see Thread Safety
    }

    // IMetricSource implementation
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                              _contentionMaxUs, 0);
        writer.WriteThroughput("Lookups", _lookupCount);
        writer.WriteThroughput("Inserts", _insertCount);
    }

    public void ResetPeaks()
    {
        Volatile.Write(ref _contentionMaxUs, 0);
        Volatile.Write(ref _peakMemoryBytes, _currentMemoryBytes);
    }
}
```

### Pattern 5: No Peaks to Reset

Components without high-water marks implement an empty `ResetPeaks()`:

```csharp
public class TransactionPool : IResource, IMetricSource
{
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteCapacity(_activeCount, _maxCount);
        writer.WriteThroughput("Committed", _totalCommitted);
    }

    public void ResetPeaks() { }  // No high-water marks — empty implementation
}
```

---

## 8. Thread Safety

### Reading Metrics

`ReadMetrics()` is called from the snapshot thread while other threads may be updating the same fields. This is acceptable because:

1. **Reads are atomic for primitives** — Reading a `long` on x64 is atomic
2. **Approximate is OK** — ±1 on a counter is acceptable
3. **No locks in hot path** — Avoids cache-line bouncing

### Updating Metrics

Hot-path code updates counters without locks:

```csharp
// In hot path: simple increment (not Interlocked for throughput counters)
_cacheHits++;

// For contention counters (from IContentionTarget callback, medium-hot):
Interlocked.Increment(ref _contentionWaitCount);

// For high-water marks: plain check-and-write is acceptable
if (waitUs > _maxWaitUs)
    _maxWaitUs = waitUs;  // Occasionally loses a max, acceptable for diagnostics
```

### When to Use Interlocked

| Field Type | Update Pattern | Use Interlocked? |
|------------|----------------|------------------|
| Throughput counter | Single writer | No |
| Contention counter | Multiple writers (locks) | Yes (Increment, Add) |
| High-water mark | Check-and-write | **No** — occasional lost max is acceptable |
| Capacity gauge | Single updater | No |

**Why plain writes for high-water marks?**

High-water marks are diagnostic — they answer "what's the worst we've seen?" If two threads race to update the max, occasionally losing a higher value to a lower one is acceptable:
- Under sustained load, similar high values recur quickly
- The ~5-10ns cost of `Interlocked.CompareExchange` loops adds up
- Snapshots every 1-5 seconds will capture representative peaks

---

## 9. Call Frequency Guidelines

### Recommended Intervals

`ReadMetrics()` is designed for periodic polling, not continuous streaming:

| Use Case | Recommended Interval |
|----------|---------------------|
| Production monitoring | 1–5 seconds |
| Health checks | 1 second |
| Debugging/profiling | As needed |

### No Built-in Throttling

There is no built-in throttling — each `GetSnapshot()` call reads fresh values. Consumers control call frequency.

### Cost Budget

For a typical 50-100 node tree:
- **Snapshot cost:** ~5-10μs (50-100 nodes × ~100ns per node)
- **At 1Hz:** ~5-10μs per second — negligible
- **At 100Hz:** ~0.5-1ms per second — still acceptable for debugging

Avoid calling `GetSnapshot()` in tight loops or on every request. For per-request metrics, accumulate in counters and let periodic snapshots read them.

---

## 10. Zero-Allocation Design

### Why It Matters

Snapshot collection runs every 1-5 seconds in production. If every node allocated objects, that's ~50-100 allocations per snapshot — acceptable but wasteful.

### How We Achieve It

1. **Writer pattern** — `IMetricWriter` buffers internally
2. **Pre-allocated snapshot structure** — `ResourceSnapshot` reuses buffers
3. **No string formatting** — `WriteThroughput("CacheHits", count)` passes static strings
4. **No LINQ in ReadMetrics** — No `Select`, `Where`, `ToList`

### String Name Safety

`WriteThroughput()` and `WriteDuration()` accept string names. Callers must use **static strings** to maintain zero-allocation:

```csharp
// BAD - allocates string on every call
writer.WriteThroughput($"Index_{_indexName}_Lookups", _lookups);

// GOOD - use static strings
writer.WriteThroughput("Lookups", _lookups);

// BAD - allocates array
writer.WriteThroughput("AllCounters", _counters.Sum());

// GOOD - report each counter individually
writer.WriteThroughput("Hits", _hits);
writer.WriteThroughput("Misses", _misses);
```

This is enforced by documentation and code review, not runtime validation.

---

## 11. Code Examples

### Complete Example: PageCache

```csharp
public partial class PageCache : IResource, IMetricSource, IContentionTarget, IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // METRIC FIELDS
    // ═══════════════════════════════════════════════════════════════

    // Memory
    private readonly long _bufferSizeBytes;
    private long _peakBufferSizeBytes;

    // Capacity
    private long _usedSlots;
    private readonly long _totalSlots;

    // DiskIO
    private long _readOps;
    private long _writeOps;
    private long _readBytes;
    private long _writeBytes;

    // Contention (updated via IContentionTarget)
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;
    private long _contentionTimeouts;

    // Throughput
    private long _cacheHits;
    private long _cacheMisses;
    private long _evictions;

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource IMPLEMENTATION
    // ═══════════════════════════════════════════════════════════════

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteMemory(_bufferSizeBytes, _peakBufferSizeBytes);
        writer.WriteCapacity(_usedSlots, _totalSlots);
        writer.WriteDiskIO(_readOps, _writeOps, _readBytes, _writeBytes);
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                              _contentionMaxUs, _contentionTimeouts);
        writer.WriteThroughput("CacheHits", _cacheHits);
        writer.WriteThroughput("CacheMisses", _cacheMisses);
        writer.WriteThroughput("Evictions", _evictions);
    }

    public void ResetPeaks()
    {
        Volatile.Write(ref _peakBufferSizeBytes, _bufferSizeBytes);
        Volatile.Write(ref _contentionMaxUs, 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // IContentionTarget IMPLEMENTATION
    // ═══════════════════════════════════════════════════════════════

    private volatile TelemetryLevel _telemetryLevel = TelemetryLevel.Light;
    public TelemetryLevel TelemetryLevel => _telemetryLevel;
    public IResource OwningResource => this;

    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalUs, waitUs);

        // Plain check-and-write for high-water mark — occasional lost max is acceptable
        if (waitUs > _contentionMaxUs)
            _contentionMaxUs = waitUs;
    }

    public void LogLockOperation(LockOperation operation, long durationUs)
    {
        // Deep mode: log to ResourceTelemetryAllocator
    }

    // ═══════════════════════════════════════════════════════════════
    // HOT PATH — Update counters
    // ═══════════════════════════════════════════════════════════════

    private PageCacheEntry GetPage(long pageIndex)
    {
        if (TryGetCached(pageIndex, out var entry))
        {
            _cacheHits++;  // No Interlocked — single writer
            return entry;
        }

        _cacheMisses++;
        return LoadFromDisk(pageIndex);
    }

    private PageCacheEntry LoadFromDisk(long pageIndex)
    {
        _readOps++;
        _readBytes += PageSize;
        // ... actual I/O
    }
}
```

### Example: Registering a New Metric Source

When adding `IMetricSource` to an existing `IResource`:

```csharp
// Before: Only IResource
public class MySegment : IResource, IDisposable
{
    // ... IResource implementation
}

// After: Add IMetricSource
public class MySegment : IResource, IMetricSource, IDisposable
{
    // Metric fields
    private long _allocatedChunks;
    private long _totalChunks;
    private long _peakAllocatedChunks;

    // IMetricSource implementation
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteCapacity(_allocatedChunks, _totalChunks);
        writer.WriteMemory(_allocatedChunks * ChunkSize, _peakAllocatedChunks * ChunkSize);
    }

    public void ResetPeaks()
    {
        Volatile.Write(ref _peakAllocatedChunks, _allocatedChunks);
    }
}
```

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [04-metric-kinds.md](04-metric-kinds.md) | Detailed spec for each of the 6 metric types |
| [05-granularity-strategy.md](05-granularity-strategy.md) | Which resources should implement `IMetricSource` |
| [06-snapshot-api.md](06-snapshot-api.md) | How the graph calls `ReadMetrics()` |
| [overview/08-resources.md](../../overview/08-resources.md) §8.2 | High-level overview |
| [overview/09-observability.md](../../overview/09-observability.md) §9.1.4 | `IContentionTarget` interface specification |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Return type | `void` with writer | Zero allocation per node |
| `IMetricSource` separate from `IResource` | Yes | Not all resources have metrics; separation of concerns |
| Writer has one method per metric kind | Yes | Type-safe; compiler catches misuse |
| Named throughput/duration | `(string name, long value)` | One node can have multiple counters |
| String name enforcement | Trust callers | Documentation + code review; runtime checks add overhead |
| Derived metrics (Utilization) | Snapshot computes | Keeps ReadMetrics fast (no division) |
| Metric source discovery | Runtime `is` check during tree walk | Tree walk needed anyway for paths; ~1.5μs per snapshot is negligible; simpler code |
| Multiple Write* calls | Last wins | Simple overwrite, no validation overhead |
| High-water mark reset | `ResetPeaks()` on IMetricSource | Enables windowed peak measurements |
| Reset implementation | Empty method if no peaks | Simpler than separate interface |
| High-water mark atomicity | Plain check-and-write | Occasional lost max acceptable for diagnostics |
| Zero-allocation enforcement | Documentation only | Analyzer possible later; code review for now |
| Thread safety | Approximate reads OK | Exact would require locks everywhere |
| Lock on ReadMetrics | No | Cost would be too high; approximate is fine |
| Call frequency contract | Advisory guideline | Recommend 1-5 seconds; no built-in throttle |

---

*Document Version: 2.1*
*Last Updated: January 2026*
*Part of the Resource System Design series*

**Change History:**
- v2.1: Removed SegmentManager reference; aligned with Owner Aggregates pattern
- v2.0: Added runtime `is` check for IMetricSource discovery (simpler than caching)

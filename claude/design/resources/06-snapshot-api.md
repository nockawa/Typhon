# 06 — Snapshot & Query API

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [03-metric-source.md](03-metric-source.md), [04-metric-kinds.md](04-metric-kinds.md)

---

## Overview

A **snapshot** is a consistent-enough reading of all metric values across the resource tree. The `IResourceGraph` interface provides the API for taking snapshots and querying them.

> 💡 **Key insight:** The graph doesn't push metrics continuously. Consumers pull snapshots on demand — typically every 1-5 seconds for monitoring, or ad-hoc for debugging.

---

## Table of Contents

1. [Snapshot Semantics](#1-snapshot-semantics)
2. [IResourceGraph Interface](#2-iresourcegraph-interface)
3. [ResourceSnapshot Class](#3-resourcesnapshot-class)
4. [NodeSnapshot Class](#4-nodesnapshot-class)
5. [Query Methods](#5-query-methods)
6. [Aggregation Patterns](#6-aggregation-patterns)
7. [Implementation Details](#7-implementation-details)
8. [Usage Examples](#8-usage-examples)
9. [Snapshot Frequency Guidance](#9-snapshot-frequency-guidance)

---

## 1. Snapshot Semantics

### What "Consistent-Enough" Means

| Guarantee | Description |
|-----------|-------------|
| **Per-node atomic** | Each node's `ReadMetrics()` call reads all its fields together |
| **Cross-node approximate** | Different nodes may be read microseconds apart |
| **No global lock** | Tree traversal doesn't block other threads |
| **Stable structure** | Tree topology doesn't change during snapshot |

### Why Not Full Consistency?

Full consistency would require:
- Locking the entire tree (blocks all operations)
- Or: Coordinated snapshots across all nodes (complex, slow)

Neither is acceptable. ±1 on a counter is fine for monitoring. The alternative costs cache-line bounces on every operation.

### What Can Change During Snapshot

| Can Change | Cannot Change |
|------------|---------------|
| Counter values (slightly stale is OK) | Tree structure (nodes don't appear/disappear) |
| Gauge values | Node identity |
| High-water marks | |

---

## 2. IResourceGraph Interface

```csharp
/// <summary>
/// Entry point for the resource graph. Provides tree traversal and snapshot collection.
/// </summary>
public interface IResourceGraph
{
    /// <summary>
    /// The root of the resource tree.
    /// </summary>
    IResource Root { get; }

    /// <summary>
    /// Walk the entire tree, read all IMetricSource nodes, return immutable snapshot.
    /// Throughput rates are auto-computed from the previous snapshot.
    /// </summary>
    /// <returns>Snapshot containing all metric values at approximately this instant.</returns>
    /// <remarks>
    /// Cost: ~50ns per node × number of nodes. Typically 50-100 nodes = 2.5-5 μs.
    /// No allocations on hot path (pooled buffers).
    /// The graph internally tracks the previous snapshot for rate computation.
    /// First snapshot has Rates = null.
    /// </remarks>
    ResourceSnapshot GetSnapshot();

    /// <summary>
    /// Read metrics for a single subtree only.
    /// </summary>
    /// <param name="subtreeRoot">The root of the subtree to snapshot.</param>
    /// <returns>Snapshot containing only nodes under subtreeRoot.</returns>
    /// <remarks>
    /// Use when you know which subsystem to inspect. Faster than full snapshot.
    /// Note: Rates are computed only for nodes in the subtree.
    /// </remarks>
    ResourceSnapshot GetSnapshot(IResource subtreeRoot);

    /// <summary>
    /// Find a resource by path.
    /// </summary>
    /// <param name="path">Slash-separated path (e.g., "Storage/PageCache").</param>
    /// <returns>The resource, or null if not found.</returns>
    IResource FindByPath(string path);

    /// <summary>
    /// Find all resources of a specific type.
    /// </summary>
    IEnumerable<IResource> FindByType(ResourceType type);

    /// <summary>
    /// Reset all peak/high-water mark values across all IMetricSource nodes.
    /// </summary>
    /// <remarks>
    /// Walks the tree and calls ResetPeaks() on each IMetricSource.
    /// Use after alert acknowledgment, periodic reset, or on operator request.
    /// This is a separate operation from snapshot collection (snapshots are read-only).
    /// </remarks>
    void ResetAllPeaks();
}
```

### Key Design Points

1. **Single method for full snapshot** — `GetSnapshot()` does it all
2. **Rates auto-computed** — IResourceGraph tracks previous snapshot internally, rates included
3. **Subtree snapshot** — When you know what you need, skip the rest
4. **Path-based lookup** — For quick access to known nodes
5. **Type-based enumeration** — Find all indexes, all component tables, etc.
6. **ResetPeaks separate** — Explicit operation, not tied to snapshot collection

---

## 3. ResourceSnapshot Class

```csharp
/// <summary>
/// Immutable snapshot of all resource metrics at a point in time.
/// </summary>
public class ResourceSnapshot
{
    /// <summary>
    /// When this snapshot was taken.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// All nodes with their metric values, keyed by node path.
    /// </summary>
    /// <example>
    /// snapshot.Nodes["Storage/PageCache"].Capacity.Utilization
    /// </example>
    public IReadOnlyDictionary<string, NodeSnapshot> Nodes { get; init; }

    /// <summary>
    /// Throughput rates (ops/sec) computed from the previous snapshot.
    /// Null for the first snapshot (no previous to compare against).
    /// </summary>
    /// <example>
    /// var hitRate = snapshot.Rates?["Storage/PageCache"]["CacheHits"];
    /// </example>
    public ThroughputRates? Rates { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // AGGREGATION QUERIES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sum Memory.AllocatedBytes across all descendants of the given node.
    /// </summary>
    /// <param name="nodePath">Path to the subtree root (e.g., "DataEngine").</param>
    /// <returns>Total bytes allocated by all nodes under the path.</returns>
    public long GetSubtreeMemory(string nodePath);

    /// <summary>
    /// Find the node with highest Capacity.Utilization in the tree.
    /// </summary>
    /// <returns>The most utilized node, or null if no nodes have Capacity metrics.</returns>
    public NodeSnapshot FindMostUtilized();

    /// <summary>
    /// Find nodes where Contention.WaitCount > 0, sorted by TotalWaitUs descending.
    /// </summary>
    /// <returns>Nodes with contention, hottest first.</returns>
    public IEnumerable<NodeSnapshot> FindContentionHotspots();
}
```

### Path Convention

Paths use `/` as separator:
- `"Root"` — The root node
- `"Storage"` — Top-level subsystem
- `"Storage/PageCache"` — Component under Storage
- `"DataEngine/ComponentTable<Player>"` — Per-type instance

---

## 4. NodeSnapshot Class

```csharp
/// <summary>
/// Snapshot of a single resource node's metrics.
/// </summary>
public class NodeSnapshot
{
    /// <summary>Full path in the tree (e.g., "Storage/PageCache").</summary>
    public string Path { get; init; }

    /// <summary>Node identifier (e.g., "PageCache").</summary>
    public string Id { get; init; }

    /// <summary>Node type from ResourceType enum.</summary>
    public ResourceType Type { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // METRIC VALUES — nullable, only present if node declares this kind
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Memory allocation metrics, if declared.</summary>
    public MemoryMetrics? Memory { get; init; }

    /// <summary>Capacity utilization metrics, if declared.</summary>
    public CapacityMetrics? Capacity { get; init; }

    /// <summary>Disk I/O metrics, if declared.</summary>
    public DiskIOMetrics? DiskIO { get; init; }

    /// <summary>Lock contention metrics, if declared.</summary>
    public ContentionMetrics? Contention { get; init; }

    /// <summary>Named throughput counters, if declared.</summary>
    public IReadOnlyList<ThroughputMetric> Throughput { get; init; }

    /// <summary>Named duration metrics, if declared.</summary>
    public IReadOnlyList<DurationMetric> Duration { get; init; }
}
```

### ThroughputRates Class

```csharp
/// <summary>
/// Throughput rates (ops/sec) computed from two consecutive snapshots.
/// </summary>
public class ThroughputRates
{
    private readonly Dictionary<string, Dictionary<string, double>> _rates;

    internal ThroughputRates(Dictionary<string, Dictionary<string, double>> rates)
    {
        _rates = rates;
    }

    /// <summary>
    /// Get rates for a specific node path.
    /// </summary>
    /// <param name="nodePath">Path to the node (e.g., "Storage/PageCache").</param>
    /// <returns>Dictionary of metric name → rate (ops/sec).</returns>
    public IReadOnlyDictionary<string, double> this[string nodePath]
        => _rates.TryGetValue(nodePath, out var nodeRates)
            ? nodeRates
            : new Dictionary<string, double>();

    /// <summary>
    /// All node paths with rate data.
    /// </summary>
    public IEnumerable<string> NodePaths => _rates.Keys;

    /// <summary>
    /// Get a specific rate value.
    /// </summary>
    /// <returns>Rate in ops/sec, or 0 if not found.</returns>
    public double GetRate(string nodePath, string metricName)
        => _rates.TryGetValue(nodePath, out var nodeRates)
            && nodeRates.TryGetValue(metricName, out var rate)
            ? rate
            : 0.0;
}
```

### Nullable Metrics

Not all nodes declare all metric kinds. A pure grouping node has no metrics at all:

```csharp
var storageNode = snapshot.Nodes["Storage"];
// storageNode.Memory is null (grouping only)
// storageNode.Capacity is null
// etc.

var pageCacheNode = snapshot.Nodes["Storage/PageCache"];
// pageCacheNode.Memory has value
// pageCacheNode.Capacity has value
// pageCacheNode.Contention has value
// etc.
```

---

## 5. Query Methods

### GetSubtreeMemory

Sum all Memory.AllocatedBytes under a subtree:

```csharp
public long GetSubtreeMemory(string nodePath)
{
    return Nodes.Values
        .Where(n => n.Path.StartsWith(nodePath))
        .Where(n => n.Memory.HasValue)
        .Sum(n => n.Memory.Value.AllocatedBytes);
}
```

**Use cases:**
- "How much memory does DataEngine use?"
- "What's the total allocation under Storage?"

### FindMostUtilized

Find the node closest to capacity:

```csharp
public NodeSnapshot FindMostUtilized()
{
    return Nodes.Values
        .Where(n => n.Capacity.HasValue)
        .OrderByDescending(n => n.Capacity.Value.Utilization)
        .FirstOrDefault();
}
```

**Use cases:**
- "Which resource is the bottleneck?"
- "What's about to run out?"

### FindContentionHotspots

Find nodes with lock contention:

```csharp
public IEnumerable<NodeSnapshot> FindContentionHotspots()
{
    return Nodes.Values
        .Where(n => n.Contention.HasValue)
        .Where(n => n.Contention.Value.WaitCount > 0)
        .OrderByDescending(n => n.Contention.Value.TotalWaitUs);
}
```

**Use cases:**
- "Where are threads waiting?"
- "Which component has the most contention?"

---

## 6. Aggregation Patterns

### Pattern 1: Sum Children

For aggregate nodes that don't have their own metrics:

```csharp
// "Storage" node has no Memory metric of its own
// Its memory = sum of children (PageCache, ManagedPagedMMF, etc.)
var storageMemory = snapshot.GetSubtreeMemory("Storage");
```

### Pattern 2: Worst-Of for Utilization

Composite utilization takes the worst:

```csharp
var mostUtilized = snapshot.FindMostUtilized();
var overallUtilization = mostUtilized?.Capacity?.Utilization ?? 0;
```

### Pattern 3: Sum for Contention

Total contention across a subsystem:

```csharp
var dataEngineContention = snapshot.Nodes.Values
    .Where(n => n.Path.StartsWith("DataEngine"))
    .Where(n => n.Contention.HasValue)
    .Sum(n => n.Contention.Value.TotalWaitUs);
```

---

## 7. Implementation Details

### Tree Walk Algorithm

```csharp
// IResourceGraph tracks previous snapshot internally
private ResourceSnapshot _previousSnapshot;

internal ResourceSnapshot CollectSnapshot(IResource root)
{
    var timestamp = DateTime.UtcNow;
    var nodes = new Dictionary<string, NodeSnapshot>();
    var writer = _writerPool.Rent();  // Reuse buffer

    try
    {
        WalkTree(root, "", nodes, writer);

        // Compute rates from previous snapshot (null for first snapshot)
        var rates = _previousSnapshot != null
            ? ComputeRates(nodes, _previousSnapshot, timestamp)
            : null;

        var snapshot = new ResourceSnapshot
        {
            Timestamp = timestamp,
            Nodes = nodes,
            Rates = rates
        };

        _previousSnapshot = snapshot;  // Track for next call
        return snapshot;
    }
    finally
    {
        _writerPool.Return(writer);
    }
}

private ThroughputRates ComputeRates(
    Dictionary<string, NodeSnapshot> currentNodes,
    ResourceSnapshot previous,
    DateTime currentTimestamp)
{
    var elapsed = (currentTimestamp - previous.Timestamp).TotalSeconds;
    var rates = new Dictionary<string, Dictionary<string, double>>();

    foreach (var (path, node) in currentNodes)
    {
        if (node.Throughput == null || node.Throughput.Count == 0)
            continue;

        if (!previous.Nodes.TryGetValue(path, out var prevNode))
            continue;

        var nodeRates = new Dictionary<string, double>();
        foreach (var metric in node.Throughput)
        {
            var prevMetric = prevNode.Throughput?.FirstOrDefault(m => m.Name == metric.Name);
            if (prevMetric != null)
            {
                var delta = metric.Count - prevMetric.Value.Count;
                nodeRates[metric.Name] = delta / elapsed;
            }
        }
        if (nodeRates.Count > 0)
            rates[path] = nodeRates;
    }

    return new ThroughputRates(rates);
}

private void WalkTree(IResource resource, string parentPath,
    Dictionary<string, NodeSnapshot> nodes, MetricWriter writer)
{
    var path = string.IsNullOrEmpty(parentPath)
        ? resource.Id
        : $"{parentPath}/{resource.Id}";

    writer.Reset();  // Clear for this node

    // Read metrics if this node is a metric source
    if (resource is IMetricSource source)
    {
        source.ReadMetrics(writer);
    }

    nodes[path] = writer.ToNodeSnapshot(path, resource.Id, resource.Type);

    // Recurse to children
    foreach (var child in resource.Children)
    {
        WalkTree(child, path, nodes, writer);
    }
}
```

### Buffer Pooling

The `MetricWriter` is pooled to avoid allocations:

```csharp
internal class MetricWriter : IMetricWriter
{
    private MemoryMetrics? _memory;
    private CapacityMetrics? _capacity;
    private DiskIOMetrics? _diskIO;
    private ContentionMetrics? _contention;
    private readonly List<ThroughputMetric> _throughput = new(8);
    private readonly List<DurationMetric> _duration = new(4);

    public void Reset()
    {
        _memory = null;
        _capacity = null;
        _diskIO = null;
        _contention = null;
        _throughput.Clear();
        _duration.Clear();
    }

    public void WriteMemory(long allocatedBytes, long peakBytes)
    {
        _memory = new MemoryMetrics(allocatedBytes, peakBytes);
    }

    // ... other Write methods

    public NodeSnapshot ToNodeSnapshot(string path, string id, ResourceType type)
    {
        return new NodeSnapshot
        {
            Path = path,
            Id = id,
            Type = type,
            Memory = _memory,
            Capacity = _capacity,
            DiskIO = _diskIO,
            Contention = _contention,
            Throughput = _throughput.Count > 0
                ? _throughput.ToArray()
                : Array.Empty<ThroughputMetric>(),
            Duration = _duration.Count > 0
                ? _duration.ToArray()
                : Array.Empty<DurationMetric>()
        };
    }
}
```

### Thread Safety During Snapshot

The snapshot collection:
1. Iterates `Children` (which returns a snapshot-safe enumerable)
2. Calls `ReadMetrics()` on each node sequentially
3. Doesn't lock — accepts slightly stale values

Concurrent updates to metric fields are fine — reads are atomic for primitives.

---

## 8. Usage Examples

### Example 1: Diagnostic "Why Is It Slow?"

```csharp
var snapshot = _resourceGraph.GetSnapshot();

// Find the bottleneck
var bottleneck = snapshot.FindMostUtilized();
Console.WriteLine($"Bottleneck: {bottleneck.Path} at {bottleneck.Capacity.Value.Utilization:P0}");
// → "Bottleneck: Durability/WALRingBuffer at 93%"

// Find contention hotspots
var hotspots = snapshot.FindContentionHotspots().Take(3);
foreach (var node in hotspots)
{
    Console.WriteLine($"  {node.Path}: {node.Contention.Value.TotalWaitUs} μs total wait");
}
// → "  Storage/PageCache: 120 μs total wait"
// → "  Durability/WALRingBuffer: 45 μs total wait"
```

### Example 2: Memory Attribution

```csharp
var snapshot = _resourceGraph.GetSnapshot();

// How much does each subsystem use?
var dataEngineMemory = snapshot.GetSubtreeMemory("DataEngine");
var storageMemory = snapshot.GetSubtreeMemory("Storage");
var durabilityMemory = snapshot.GetSubtreeMemory("Durability");

Console.WriteLine($"DataEngine: {dataEngineMemory / 1_000_000} MB");
Console.WriteLine($"Storage: {storageMemory / 1_000_000} MB");
Console.WriteLine($"Durability: {durabilityMemory / 1_000_000} MB");
```

### Example 3: Rate Computation

```csharp
// Rates are auto-computed by IResourceGraph (tracks previous internally)
var snapshot = _resourceGraph.GetSnapshot();

// First snapshot has Rates = null
if (snapshot.Rates != null)
{
    var pageCacheRates = snapshot.Rates["Storage/PageCache"];
    var hitRate = pageCacheRates["CacheHits"] /
        (pageCacheRates["CacheHits"] + pageCacheRates["CacheMisses"]);

    Console.WriteLine($"Cache hit rate: {hitRate:P1}");
    // → "Cache hit rate: 98.5%"
}
```

### Example 4: Subtree Snapshot

```csharp
// Only interested in DataEngine metrics
var dataEngine = _resourceGraph.FindByPath("DataEngine");
var snapshot = _resourceGraph.GetSnapshot(dataEngine);

// Snapshot only contains DataEngine and its descendants
foreach (var (path, node) in snapshot.Nodes)
{
    Console.WriteLine(path);
}
// → "DataEngine"
// → "DataEngine/TransactionPool"
// → "DataEngine/ComponentTable<Player>"
// → etc.
```

---

## 9. Snapshot Frequency Guidance

| Consumer | Frequency | Rationale |
|----------|-----------|-----------|
| **OTel metrics export** | 1–5 seconds | Feed gauges to Prometheus |
| **Health checks** | 1 second | Detect degradation quickly |
| **Exhaustion policy** | On threshold cross | Only when needed |
| **Developer debugging** | On demand | Manual inspection |
| **Automated root-cause** | On alert trigger | Walk tree to find bottleneck |
| **Dashboards** | 5–15 seconds | Sufficient for visualization |

### Don't Do This

- **Don't snapshot on every operation** — That defeats the point
- **Don't snapshot faster than 100ms** — Diminishing returns, adds overhead
- **Don't keep all historical snapshots** — Keep last N (e.g., 60 for 1-minute history at 1Hz)

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [03-metric-source.md](03-metric-source.md) | How nodes report metrics |
| [04-metric-kinds.md](04-metric-kinds.md) | What metrics snapshots contain |
| [08-observability-bridge.md](08-observability-bridge.md) | How snapshots feed OTel |
| [overview/08-resources.md](../../overview/08-resources.md) §8.8 | High-level overview |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Consistency model | Per-node atomic, cross-node approximate | Full consistency too expensive |
| Snapshot return type | Immutable class with dictionary | Simple to use, safe to pass around |
| Path separator | `/` | Unix-style, familiar |
| Pooling | Pool MetricWriter, not snapshots | Writers reused frequently; snapshots may be retained |
| Query methods | On ResourceSnapshot, not IResourceGraph | Queries work on frozen data |
| Subtree snapshot | Separate overload | Optimization for targeted inspection |
| **Rate computation** | **Auto-computed, included in snapshot** | Simpler consumer code; IResourceGraph tracks previous internally |
| **ResetPeaks** | **Separate method on IResourceGraph** | Snapshot collection is read-only (no side effects) |
| **IMetricSource discovery** | **Runtime `is` check during tree walk** | Snapshot frequency (1-5 sec) makes caching unnecessary; simpler code |
| **Timestamp precision** | **DateTime.UtcNow (~15ms)** | Sufficient for monitoring; rate computation tolerates ±1.5% error |
| **ToArray() for throughput/duration** | **Accept small allocations** | Same-sized arrays per node are GC-efficient; simplicity over complexity |

---

*Document Version: 2.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

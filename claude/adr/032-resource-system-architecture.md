# ADR-032: Resource System Architecture (Pull-Based Metrics, Owner Aggregates, Snapshot API)

**Status**: Accepted
**Date**: 2026-01-31 (amended 2026-02-11)
**Deciders**: Developer + Claude Code

## Context

Issue #20 implemented a comprehensive Resource System for tracking memory, capacity, contention, disk I/O, throughput, and duration metrics across all Typhon components. The system needed to:

1. Provide hierarchical resource attribution ("How much memory does DataEngine use?")
2. Enable cascade root-cause analysis ("Why are transactions stalling?")
3. Support per-resource telemetry granularity
4. Have minimal hot-path overhead
5. Integrate with OpenTelemetry for export

## Decisions

### 1. Pull-Based Metric Collection via `IMetricSource`

**Metrics are owned by components, read on-demand by the graph.**

```csharp
public interface IMetricSource
{
    void ReadMetrics(IMetricWriter writer);
}

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

**Rationale**:
- Components maintain plain fields (no `Interlocked` in hot paths)
- Snapshot is the only point of coordination
- Zero overhead when no one is observing
- Components already track their state internally
- Writer pattern avoids allocation per metric read

### 2. Six Metric Kinds

**Standardized metric types cover all measurement needs:**

| Kind | Sub-metrics | Use Case |
|------|-------------|----------|
| **Memory** | AllocatedBytes, PeakBytes | Byte allocation tracking |
| **Capacity** | Current, Maximum, Utilization | Bounded slot structures |
| **DiskIO** | ReadOps, WriteOps, ReadBytes, WriteBytes | I/O operations |
| **Contention** | WaitCount, TotalWaitUs, MaxWaitUs, TimeoutCount | Lock/latch waits |
| **Throughput** | Named counters | Operation rates |
| **Duration** | LastUs, AvgUs, MaxUs | Operation timing |

**Rationale**:
- Covers all resource types in Typhon
- Each kind has clear semantics
- Derived values (Utilization, rates) computed at read time
- `readonly record struct` ensures immutability

### 3. Owner Aggregates Pattern (No Nodes for Fine-Grained Primitives)

**Individual latches, chunks, and pages do NOT get graph nodes. Owning components aggregate.**

```
ComponentTable<T> (graph node with contention metrics)
├── ComponentSegment (NOT a node — metrics aggregated into parent)
├── RevisionSegment (NOT a node — metrics aggregated into parent)
├── PrimaryKeyIndex (graph node — has distinct metrics)
└── AccessControl latches (NOT nodes — contention via IContentionTarget)
```

**Rationale**:
- Thousands of latches → thousands of nodes would be insane
- Memory cost: ~200 bytes per node
- Snapshot cost: ~50ns per node
- 3-level guideline (System → Component → Instance) covers 95% of cases
- `IContentionTarget` callback pattern aggregates latch contention into owning resource

### 4. Snapshot API with Approximate Consistency

**`ResourceSnapshot` provides consistent-enough readings of the entire tree.**

```csharp
public interface IResourceGraph
{
    ResourceSnapshot GetSnapshot();
    ResourceSnapshot GetSnapshot(IResource subtreeRoot);
    IResource Root { get; }
}

public class ResourceSnapshot
{
    public DateTime Timestamp { get; }
    public IReadOnlyDictionary<string, NodeSnapshot> Nodes { get; }

    // Query methods
    public long GetSubtreeMemory(string nodePath);
    public NodeSnapshot FindMostUtilized();
    public IEnumerable<NodeSnapshot> FindContentionHotspots();
    public ThroughputRates ComputeRates(ResourceSnapshot previous);
}
```

**Consistency model**:
- Per-node atomic (single `ReadMetrics()` call reads all fields together)
- Cross-node approximate (different nodes read microseconds apart)
- Total snapshot time: ~5-10μs for 50-100 nodes

**Rationale**:
- Full tree lock would be too expensive
- ±1 on a counter is acceptable for diagnostics
- Consumers (OTel export, health checks) call `GetSnapshot()` on their schedule

### 5. Hardcoded Wait Dependencies for Cascade Detection

**Root-cause tracing uses a static dependency map, not runtime discovery.**

```csharp
private static readonly Dictionary<string, string[]> WaitDependencies = new()
{
    ["DataEngine/TransactionPool"] = ["Durability/WALRingBuffer"],
    ["Durability/WALRingBuffer"] = ["Durability/WALSegments"],
    ["Storage/PageCache"] = ["Storage/ManagedPagedMMF"],
    ["Backup/ShadowBuffer"] = ["Backup/SnapshotStore"],
};

public ResourceNode FindRootCause(ResourceNode symptom)
{
    // Walk dependency edges looking for highest-utilization upstream node
}
```

**Rationale**:
- Runtime dependency discovery would require instrumenting all blocking calls
- Known dependencies are static (WAL blocks commits, PageCache blocks page loads)
- Hardcoded map is ~10 entries, trivial to maintain
- Can trace from symptom to root cause mechanically

### 6. OTel Bridge with Path-Based Naming

**Graph nodes map to OTel metrics using dot-separated paths.**

```
NodeSnapshot: "Storage/PageCache"
  Memory.AllocatedBytes    → typhon.resource.storage.page_cache.memory_bytes
  Capacity.Utilization     → typhon.resource.storage.page_cache.utilization
  Contention.WaitCount     → typhon.resource.storage.page_cache.contention_waits
```

**Rationale**:
- OTel naming convention uses dots for hierarchy
- Path transforms: `/` → `.`, PascalCase → snake_case
- Single source of truth (graph) feeds all external systems
- Consumers can subscribe to subtrees

### 7. ResourceNode Self-Registration and Root Lockdown (Amendment, 2026-02-11)

**ResourceNode's public constructor self-registers with its parent. Root creation is locked down to a factory method.**

```csharp
// Public constructor — the ONLY way to create a non-root node
public ResourceNode(string id, ResourceType type, IResource parent, ...)
{
    Parent = parent;
    Owner = parent.Owner;
    Parent.RegisterChild(this);  // self-registers
}

// Root creation — private constructor, factory access only
internal static ResourceNode CreateRoot(ResourceRegistry registry) => new(registry);
```

**Rationale**:
- Eliminates "forgot to call RegisterChild" bugs — the old `internal` constructor allowed parentless nodes that had to manually register, leading to split-brain (node's `Parent` was null despite being in the tree)
- Makes illegal states unrepresentable: every non-root node always has a parent and is always registered
- DatabaseEngine and MemoryAllocator now pass the correct subsystem node (`resourceRegistry.DataEngine`, `resourceRegistry.Allocation`) as parent instead of the registry itself

### 8. Dispose(bool) Chain Contract for ResourceNode Subclasses (Amendment, 2026-02-11)

**All ResourceNode subclasses must follow a strict Dispose pattern:**

```csharp
protected override void Dispose(bool disposing)
{
    if (IsDisposed) return;           // 1. Guard against double-dispose
    if (disposing)
    {
        // 2. Managed cleanup here only
    }
    base.Dispose(disposing);          // 3. ALWAYS outside if(disposing)
    IsDisposed = true;                // 4. Flag after base call
}
```

**Rules**:
- `base.Dispose(disposing)` must ALWAYS be called, and ALWAYS outside the `if (disposing)` block
- Pass `disposing` to base (never hardcode `true`)
- Managed cleanup (collections, child resources) goes inside `if (disposing)`
- Don't manually dispose children that are registered in the ResourceNode tree — `base.Dispose` handles cascade
- `IsDisposed` guard prevents re-entry from both explicit Dispose and finalizer paths

**Rationale**:
- Discovered bugs: ComponentTable was hiding Dispose (CS0108), DatabaseEngine never called base, PagedMMF hardcoded `base.Dispose(true)`, TransactionChain ran managed ops on finalizer path
- The chain pattern ensures ResourceNode's child cascade always executes
- Explicit `if (disposing)` guard prevents managed-object access during finalization

### 9. IMemoryResource — Subtree Memory Aggregation (Amendment, 2026-02-11)

**`IMemoryResource.EstimatedMemorySize` reports only the node's own memory, not children.**

```csharp
public interface IMemoryResource : IResource
{
    /// <remarks>
    /// Children resources must NOT be considered for the computation.
    /// </remarks>
    int EstimatedMemorySize { get; }
}
```

**Implementors**: PinnedMemoryBlock (native allocation size), MemoryBlockArray (array length), BlockAllocatorBase (managed overhead ~100B), PagedMMF (PageInfo array).

**Aggregation**: The shell's `SumMemory` walks the tree recursively, summing `EstimatedMemorySize` at each `IMemoryResource` node. No double-counting because each node reports only its own contribution.

**Rationale**:
- MemoryAllocator should NOT implement IMemoryResource — it's a cross-cutting tracker/factory, not a memory owner. The blocks it creates are parented to their actual owners
- BlockAllocatorBase SHOULD — it extends ResourceNode and its pages are children, making it a natural subtree root for memory queries
- Separating `EstimatedMemorySize` (for tree aggregation) from `MemoryBlockSize` (for allocator tracking) avoids semantic confusion

### 10. Owner Aggregates Refinement — Exclusions (Amendment, 2026-02-11)

**Refined which types should NOT implement IResource:**

| Type | Reason for Exclusion |
|------|---------------------|
| Transaction | Pooled, microsecond lifetime, hot-path overhead not justified |
| ChangeSet | Short-lived commit artifact, same reasoning as Transaction |
| DatabaseDefinitions | Static metadata, no lifecycle — exposed via DatabaseEngine.GetDebugProperties() |
| DBComponentDefinition | Static metadata within DatabaseDefinitions |

**Rationale**:
- Transaction objects are pooled (16 pre-allocated) and recycled every microsecond — IResource registration/deregistration per transaction would add measurable overhead
- Static metadata types have no lifecycle to track — their data is better exposed as debug properties on their owning node

## Alternatives Considered

### For Metric Collection
1. **Push-based (emit on every operation)** — Rejected: hot-path overhead
2. **Event-based (ETW/EventSource)** — Rejected: complex, platform-specific
3. **Shared counters with Interlocked** — Rejected: cache-line bouncing

### For Granularity
1. **Every allocation gets a node** — Rejected: memory explosion, snapshot cost
2. **Flat list (no hierarchy)** — Rejected: can't aggregate subtrees
3. **Dynamic depth based on load** — Rejected: complexity, unpredictable costs

### For Consistency
1. **Global lock during snapshot** — Rejected: would block all operations
2. **MVCC-style snapshot** — Rejected: overkill for diagnostic reads
3. **Eventual consistency with version vectors** — Rejected: unnecessary complexity

### For Cascade Detection
1. **Runtime dependency graph** — Rejected: requires instrumenting all blocking points
2. **ML-based correlation** — Rejected: Typhon is embedded, not cloud-scale
3. **Manual operator analysis** — Current fallback if hardcoded map insufficient

## Consequences

**Positive:**
- Zero hot-path overhead (components use plain field increments)
- Hierarchical aggregation answers "how much memory does X use?"
- Cascade root-cause analysis is mechanical, not intuition-based
- Single snapshot feeds all consumers (OTel, health checks, debugging)
- Owner Aggregates keeps node count bounded (~50-100 typical)

**Negative:**
- Snapshot consistency is approximate (acceptable for diagnostics)
- Hardcoded dependency map needs manual maintenance
- 6 metric kinds may not cover all future needs (extensible via Throughput/Duration)
- `IMetricSource` interface must be implemented by each measurable component

## Cross-References

- [08-resources.md](../overview/08-resources.md) — Resource Management overview
- [09-observability.md](../overview/09-observability.md) — Observability integration
- [ADR-031](031-unified-concurrency-patterns.md) — IContentionTarget pattern
- Reference docs: `claude/reference/resources/` (design docs moved post-implementation)

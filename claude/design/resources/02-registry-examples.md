# 02 — Resource Registry Examples

> **Part of the [Resource System Design](README.md) series**
> **Companion to:** [01-registry.md](01-registry.md)

**Date:** January 2026

Usage patterns and code examples for integrating the Resource Registry system.

---

## Setup: DI Registration

```csharp
// In your DI configuration (e.g., Program.cs or Startup.cs)
public static IServiceCollection AddTyphon(this IServiceCollection services)
{
    // One registry per process — no static accessor
    services.AddSingleton<IResourceRegistry>(sp =>
        new ResourceRegistry(new ResourceRegistryOptions { Name = "Typhon" }));

    // Components register under appropriate subsystems via DI
    services.AddSingleton<IMemoryAllocator>(sp =>
    {
        var registry = sp.GetRequiredService<IResourceRegistry>();
        return new MemoryAllocator("MemoryAllocator", registry.Allocation);
    });

    services.AddSingleton<DatabaseEngine>(sp =>
    {
        var registry = sp.GetRequiredService<IResourceRegistry>();
        return new DatabaseEngine(registry);
    });

    return services;
}

// Also configure OpenTelemetry for metrics export
services.AddOpenTelemetry()
    .WithMetrics(builder => builder
        .AddMeter("Typhon.*")
        .AddPrometheusExporter()
    );
```

---

## Example 1: ComponentTable as IResource

ComponentTable implements `IResource` and registers under the `DataEngine` subsystem:

```csharp
public class ComponentTable<T> : IResource, IMetricSource, IDebugPropertiesProvider, IDisposable
    where T : unmanaged
{
    private readonly ConcurrentDictionary<string, IResource> _children = new();

    // Metric fields (updated on hot path)
    private long _entityCount;
    private long _maxEntities = 100_000;
    private long _lookupCount;
    private long _insertCount;

    public ComponentTable(IResource parent)
    {
        // Name-only ID — type comes from ResourceType property
        Id = typeof(T).Name;  // "Player", not "ComponentTable:Player"

        // Fail fast — explicit parent required
        Parent = parent ?? throw new ArgumentNullException(nameof(parent),
            "Resources must have an explicit parent.");
        Owner = Parent.Owner;
        CreatedAt = DateTime.UtcNow;

        // Self-register with parent
        Parent.RegisterChild(this);

        // Create child resources (indexes register themselves with us)
        _primaryKeyIndex = new BTreeIndex("PrimaryKey", this);
    }

    // ═══════════════════════════════════════════════════════════════
    // IResource Implementation
    // ═══════════════════════════════════════════════════════════════

    public string Id { get; }
    public ResourceType Type => ResourceType.ComponentTable;
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => _children.Values;
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }

    public bool RegisterChild(IResource child) => _children.TryAdd(child.Id, child);
    public bool RemoveChild(IResource resource) => _children.TryRemove(resource.Id, out _);

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource Implementation (for dashboards, alerts)
    // ═══════════════════════════════════════════════════════════════

    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteCapacity(_entityCount, _maxEntities);
        writer.WriteThroughput("Lookups", _lookupCount);
        writer.WriteThroughput("Inserts", _insertCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // IDebugPropertiesProvider Implementation (for debugging, logs)
    // ═══════════════════════════════════════════════════════════════

    public Dictionary<string, Func<object>> DebugProperties => new()
    {
        ["ComponentType"] = () => typeof(T).FullName,
        ["EntityCount"] = () => _entityCount,
        ["IndexNames"] = () => string.Join(", ", _children.Keys),
        ["Utilization"] = () => $"{_entityCount:N0} / {_maxEntities:N0}"
    };

    // ═══════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        foreach (var child in _children.Values.ToList())
        {
            try { child.Dispose(); }
            catch (Exception ex) { /* log and continue */ }
        }
        _children.Clear();
        Parent?.RemoveChild(this);
    }
}
```

**Resulting path:** `DataEngine/Player/PrimaryKey`

---

## Example 2: PageCache Under Storage Subsystem

```csharp
public class PageCache : IResource, IMetricSource, IContentionTarget, IDisposable
{
    // Metric fields
    private long _usedSlots;
    private readonly long _totalSlots;
    private long _cacheHits;
    private long _cacheMisses;
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;

    public PageCache(string id, IResource parent, int cacheSlots)
    {
        Id = id;  // e.g., "PageCache"
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Owner = Parent.Owner;
        CreatedAt = DateTime.UtcNow;
        _totalSlots = cacheSlots;

        Parent.RegisterChild(this);
    }

    // IResource
    public string Id { get; }
    public ResourceType Type => ResourceType.Cache;
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => Array.Empty<IResource>();
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;  // Leaf node
    public bool RemoveChild(IResource resource) => false;

    // IMetricSource
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteCapacity(_usedSlots, _totalSlots);
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                              _contentionMaxUs, 0);
        writer.WriteThroughput("CacheHits", _cacheHits);
        writer.WriteThroughput("CacheMisses", _cacheMisses);
    }

    // IContentionTarget — receives callbacks from locks
    public TelemetryLevel TelemetryLevel => TelemetryLevel.Light;
    public IResource OwningResource => this;

    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalUs, waitUs);
    }

    public void Dispose() => Parent?.RemoveChild(this);
}
```

**Resulting path:** `Storage/PageCache`

---

## Example 3: WAL Resources Under Durability Subsystem

```csharp
public class WALRingBuffer : IResource, IMetricSource, IDisposable
{
    private long _usedBytes;
    private readonly long _capacityBytes;
    private long _recordsWritten;
    private long _flushes;

    public WALRingBuffer(IResource parent, long capacityBytes)
    {
        Id = "RingBuffer";  // Name only
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Owner = Parent.Owner;
        CreatedAt = DateTime.UtcNow;
        _capacityBytes = capacityBytes;

        Parent.RegisterChild(this);
    }

    // IResource
    public string Id { get; }
    public ResourceType Type => ResourceType.WAL;
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => Array.Empty<IResource>();
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;
    public bool RemoveChild(IResource resource) => false;

    // IMetricSource
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteCapacity(_usedBytes, _capacityBytes);
        writer.WriteThroughput("RecordsWritten", _recordsWritten);
        writer.WriteThroughput("Flushes", _flushes);
    }

    public void Dispose() => Parent?.RemoveChild(this);
}
```

**Resulting path:** `Durability/WAL/RingBuffer`

---

## Example 4: Query & Debug at Runtime

```csharp
public class ResourceInspector
{
    private readonly IResourceRegistry _registry;

    public ResourceInspector(IResourceRegistry registry) => _registry = registry;

    /// <summary>Print the entire resource tree.</summary>
    public void DumpResourceTree()
    {
        PrintResource(_registry.Root, 0);
    }

    private void PrintResource(IResource resource, int depth)
    {
        var indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}{resource.Id} ({resource.Type})");

        foreach (var child in resource.Children)
            PrintResource(child, depth + 1);
    }

    /// <summary>Find resource by clean path.</summary>
    public void ShowComponentTable()
    {
        // Clean paths — no type prefixes
        var player = FindByPath("DataEngine/Player");
        var primaryKey = FindByPath("DataEngine/Player/PrimaryKey");
        var pageCache = FindByPath("Storage/PageCache");
        var walRing = FindByPath("Durability/WAL/RingBuffer");

        Console.WriteLine($"Player table: {player?.Id} ({player?.Type})");
        Console.WriteLine($"Primary key index: {primaryKey?.Id}");
    }

    /// <summary>Collect debug snapshot from IDebugPropertiesProvider.</summary>
    public void DumpDebugInfo()
    {
        var snapshot = CollectDebugSnapshot(_registry.Root);

        foreach (var (path, properties) in snapshot)
        {
            Console.WriteLine($"\n{path}:");
            foreach (var (key, value) in properties)
                Console.WriteLine($"  {key}: {value}");
        }
    }

    /// <summary>Query all component tables under DataEngine.</summary>
    public void ListComponentTables()
    {
        var tables = _registry.DataEngine.Children
            .Where(c => c.Type == ResourceType.ComponentTable);

        foreach (var table in tables)
        {
            Console.WriteLine($"Component: {table.Id}");

            if (table is IDebugPropertiesProvider provider)
            {
                var props = provider.DebugProperties;
                Console.WriteLine($"  Type: {props["ComponentType"]()}");
                Console.WriteLine($"  Entities: {props["EntityCount"]()}");
            }
        }
    }

    private IResource FindByPath(string path)
    {
        var parts = path.Split('/');
        IResource current = _registry.Root;

        foreach (var part in parts)
        {
            current = current.Children.FirstOrDefault(c => c.Id == part);
            if (current == null) return null;
        }
        return current;
    }

    private Dictionary<string, Dictionary<string, object>> CollectDebugSnapshot(IResource root)
    {
        var result = new Dictionary<string, Dictionary<string, object>>();
        CollectFromNode(root, "", result);
        return result;
    }

    private void CollectFromNode(IResource node, string parentPath,
        Dictionary<string, Dictionary<string, object>> result)
    {
        var path = string.IsNullOrEmpty(parentPath) ? node.Id : $"{parentPath}/{node.Id}";

        var props = new Dictionary<string, object>
        {
            ["Type"] = node.Type.ToString(),
            ["CreatedAt"] = node.CreatedAt
        };

        if (node is IDebugPropertiesProvider provider)
        {
            foreach (var (key, getter) in provider.DebugProperties)
            {
                try { props[key] = getter(); }
                catch { props[key] = "<error>"; }
            }
        }

        result[path] = props;

        foreach (var child in node.Children)
            CollectFromNode(child, path, result);
    }
}
```

---

## Example 5: Integration in Tests

```csharp
[TestFixture]
public class ResourceRegistryTests : TestBase
{
    [Test]
    public void ComponentTable_RegistersUnderDataEngine()
    {
        var registry = ServiceProvider.GetRequiredService<IResourceRegistry>();
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponent<Player>();
        dbe.RegisterComponent<Position>();

        // Verify registration under DataEngine subsystem
        var tables = registry.DataEngine.Children
            .Where(c => c.Type == ResourceType.ComponentTable)
            .ToList();

        Assert.That(tables, Has.Count.EqualTo(2));
        Assert.That(tables.Select(t => t.Id), Contains.Item("Player"));

        // Verify clean paths work
        var player = FindByPath(registry, "DataEngine/Player");
        Assert.That(player, Is.Not.Null);
        Assert.That(player.Type, Is.EqualTo(ResourceType.ComponentTable));

        // Verify indexes are children of component table
        var primaryKey = FindByPath(registry, "DataEngine/Player/PrimaryKey");
        Assert.That(primaryKey, Is.Not.Null);
        Assert.That(primaryKey.Type, Is.EqualTo(ResourceType.Index));
    }

    [Test]
    public void NullParent_ThrowsArgumentNullException()
    {
        // No Orphans fallback — fail fast
        Assert.Throws<ArgumentNullException>(() =>
            new ComponentTable<Player>(null));
    }

    [Test]
    public void Dispose_CascadesToChildren()
    {
        var registry = ServiceProvider.GetRequiredService<IResourceRegistry>();
        var parent = new ResourceNode("TestParent", ResourceType.Node, registry.DataEngine);
        var child = new ResourceNode("TestChild", ResourceType.Node, parent);

        Assert.That(parent.Children.Count(), Is.EqualTo(1));

        parent.Dispose();

        // Child should be disposed and removed
        Assert.That(parent.Children.Count(), Is.EqualTo(0));
        // Parent should be removed from DataEngine
        Assert.That(registry.DataEngine.Children.Any(c => c.Id == "TestParent"), Is.False);
    }

    [Test]
    public void MetricSource_ReportsCorrectValues()
    {
        var registry = ServiceProvider.GetRequiredService<IResourceRegistry>();
        var table = new ComponentTable<Player>(registry.DataEngine);

        // Simulate some activity
        table.SimulateInserts(100);
        table.SimulateLookups(500);

        // Take a snapshot
        var snapshot = new ResourceSnapshot(registry);
        var nodeSnapshot = snapshot.Nodes["DataEngine/Player"];

        Assert.That(nodeSnapshot.Capacity.Value.Current, Is.EqualTo(100));
        Assert.That(nodeSnapshot.Throughput.First(t => t.Name == "Inserts").Count, Is.EqualTo(100));
        Assert.That(nodeSnapshot.Throughput.First(t => t.Name == "Lookups").Count, Is.EqualTo(500));
    }

    private IResource FindByPath(IResourceRegistry registry, string path)
    {
        var parts = path.Split('/');
        IResource current = registry.Root;
        foreach (var part in parts)
        {
            current = current.Children.FirstOrDefault(c => c.Id == part);
            if (current == null) return null;
        }
        return current;
    }
}
```

---

## Example 6: Grafana Dashboard Queries

With the resource graph feeding OTel metrics (via [08-observability-bridge.md](08-observability-bridge.md)):

```promql
# Page cache utilization
typhon_resource_storage_page_cache_utilization

# Cache hit rate (derived from throughput counters)
rate(typhon_resource_storage_page_cache_cache_hits[5m]) /
(rate(typhon_resource_storage_page_cache_cache_hits[5m]) +
 rate(typhon_resource_storage_page_cache_cache_misses[5m]))

# Entity count per component table
typhon_resource_dataengine_player_capacity_current
typhon_resource_dataengine_position_capacity_current

# WAL ring buffer utilization
typhon_resource_durability_wal_ringbuffer_utilization

# Top contention hotspots
topk(5, typhon_resource_contention_total_wait_us)

# Memory usage by subsystem
sum by (subsystem) (typhon_resource_memory_bytes)
```

---

## Key Design Points

### Subsystem Organization
```
Root
├── Storage      → PageCache, Segments
├── DataEngine   → Transactions, ComponentTables
├── Durability   → WAL, Checkpoint
└── Allocation   → MemoryAllocator, Bitmaps
```

### Name-Only IDs
- `"Player"` not `"ComponentTable:Player"`
- Type information from `ResourceType` property
- Clean paths: `DataEngine/Player/PrimaryKey`

### No Orphans
- Resources must have explicit parent
- Null parent → `ArgumentNullException`
- Fail fast surfaces bugs at call site

### Dual Interface Pattern
- `IMetricSource` → Structured numeric metrics for dashboards
- `IDebugPropertiesProvider` → Ad-hoc debug info for humans

---

## Key Benefits Summary

### For Development
- **Fail fast**: Null parent throws immediately — bugs found at source
- **Clean paths**: `DataEngine/Player` is intuitive to navigate
- **Debug snapshot**: `CollectDebugSnapshot()` shows full system state

### For Production
- **Structured metrics**: `IMetricSource` feeds OTel directly
- **Subsystem isolation**: Query by subtree (`Storage/*`, `Durability/*`)
- **Health derivation**: Capacity utilization → health status

### For Testing
- **DI-friendly**: No static accessors to mock
- **Explicit ownership**: Tree structure matches code ownership
- **Disposal verification**: Cascade disposal is testable

---

*Document Version: 2.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

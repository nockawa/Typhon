# Resource Registry Integration Examples

## Setup: Register as Singleton

```csharp
// In your DI configuration (e.g., Program.cs or Startup.cs)
services.AddSingleton<IResourceRegistry, ResourceRegistry>();

// Also configure OpenTelemetry
services.AddOpenTelemetry()
    .WithMetrics(builder => builder
        .AddMeter("Typhon.*") // Capture all Typhon meters
        .AddPrometheusExporter() // Or Grafana, OTLP, etc.
    );
```

## Example 1: DatabaseEngine Registration

```csharp
public class DatabaseEngine : IDisposable
{
    private readonly IResourceRegistry _resourceRegistry;
    private readonly Meter _meter;
    private readonly ObservableGauge<int> _activeTransactionsGauge;
    private readonly Counter<long> _totalTransactionsCounter;

    public DatabaseEngine(IResourceRegistry resourceRegistry, /* other deps */)
    {
        _resourceRegistry = resourceRegistry;

        // Create OTel meter for this component
        _meter = new Meter("Typhon.DatabaseEngine", "1.0.0");

        // Create instruments
        _activeTransactionsGauge = _meter.CreateObservableGauge(
            "active_transactions",
            () => _transactionChain.ActiveCount,
            description: "Number of currently active transactions");

        _totalTransactionsCounter = _meter.CreateCounter<long>(
            "transactions_total",
            description: "Total transactions created");

        // Register in resource tree
        _resourceRegistry.Register("DatabaseEngine", new ResourceMetadata
        {
            Name = "DatabaseEngine",
            FullPath = "DatabaseEngine",
            ResourceType = "Engine",
            Meter = _meter,
            Tags = new()
            {
                ["version"] = "1.0.0",
                ["mvcc_enabled"] = true
            },
            DebugProperties = new()
            {
                ["ActiveTransactions"] = () => _transactionChain.ActiveCount,
                ["MinTick"] = () => _transactionChain.MinTick,
                ["ComponentTableCount"] = () => _componentTables.Count
            }
        });
    }

    public Transaction CreateTransaction()
    {
        _totalTransactionsCounter.Add(1); // Record metric
        // ... create transaction
    }
}
```

## Example 2: ComponentTable Registration

```csharp
public class ComponentTable<T> where T : unmanaged
{
    private readonly IResourceRegistry _resourceRegistry;
    private readonly Meter _meter;
    private readonly string _componentName;

    public ComponentTable(IResourceRegistry resourceRegistry)
    {
        _resourceRegistry = resourceRegistry;
        _componentName = typeof(T).Name;

        // Create dedicated meter for this component type
        _meter = new Meter($"Typhon.ComponentTable.{_componentName}", "1.0.0");

        // Register hierarchically under DatabaseEngine
        _resourceRegistry.Register(
            "DatabaseEngine/ComponentTables",
            _componentName,
            new ResourceMetadata
            {
                Name = _componentName,
                FullPath = $"DatabaseEngine/ComponentTables/{_componentName}",
                ResourceType = "ComponentTable",
                Meter = _meter,
                Tags = new()
                {
                    ["component_type"] = typeof(T).FullName,
                    ["chunk_size"] = Marshal.SizeOf<T>(),
                    ["has_primary_index"] = true
                },
                DebugProperties = new()
                {
                    ["EntityCount"] = () => GetEntityCount(),
                    ["RevisionCount"] = () => _compRevTable.TotalRevisions,
                    ["IndexCount"] = () => _secondaryIndexes.Count,
                    ["SegmentPageCount"] = () => _componentSegment.PageCount
                }
            });

        // Create OTel instruments
        _meter.CreateObservableGauge(
            "entity_count",
            () => GetEntityCount(),
            description: "Number of entities in this component table");

        _meter.CreateObservableGauge(
            "revision_count",
            () => _compRevTable.TotalRevisions,
            description: "Total MVCC revisions stored");
    }

    private long GetEntityCount() => _primaryIndex.Count;
}
```

## Example 3: PagedMMF Cache Registration

```csharp
public class PagedMMF : IDisposable
{
    private readonly IResourceRegistry? _resourceRegistry;
    private readonly Meter _meter;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;
    private readonly Histogram<double> _pageLoadTime;

    public PagedMMF(string filePath, IResourceRegistry? resourceRegistry = null)
    {
        _resourceRegistry = resourceRegistry;

        if (_resourceRegistry != null)
        {
            _meter = new Meter("Typhon.PagedMMF", "1.0.0");

            // Register in hierarchy
            var fileName = Path.GetFileName(filePath);
            _resourceRegistry.Register(
                "DatabaseEngine/Persistence/PageCache",
                fileName,
                new ResourceMetadata
                {
                    Name = fileName,
                    FullPath = $"DatabaseEngine/Persistence/PageCache/{fileName}",
                    ResourceType = "PageCache",
                    Meter = _meter,
                    Tags = new()
                    {
                        ["file_path"] = filePath,
                        ["page_size"] = 8192,
                        ["cache_capacity"] = _cacheCapacity
                    },
                    DebugProperties = new()
                    {
                        ["CachedPages"] = () => _pageCache.Count(p => p.State != PageState.Free),
                        ["DirtyPages"] = () => _pageCache.Count(p => p.State == PageState.IdleAndDirty),
                        ["HitRate"] = () => CalculateHitRate(),
                        ["TotalPages"] = () => _header.PageCount
                    }
                });

            // Create instruments
            _cacheHits = _meter.CreateCounter<long>("cache_hits");
            _cacheMisses = _meter.CreateCounter<long>("cache_misses");
            _pageLoadTime = _meter.CreateHistogram<double>("page_load_ms");
        }
    }

    protected PageCacheEntry GetPageCacheEntry(long pageIndex)
    {
        // ... existing cache logic

        if (cacheHit)
            _cacheHits?.Add(1);
        else
        {
            _cacheMisses?.Add(1);
            var sw = Stopwatch.StartNew();
            // ... load page
            _pageLoadTime?.Record(sw.Elapsed.TotalMilliseconds);
        }

        return entry;
    }
}
```

## Example 4: Query & Debug at Runtime

```csharp
// In a test or admin endpoint
public class ResourceInspector
{
    private readonly IResourceRegistry _registry;

    public void DumpResourceTree()
    {
        foreach (var resource in _registry.GetAllResources())
        {
            Console.WriteLine($"{resource.FullPath} ({resource.ResourceType})");
            foreach (var (key, value) in resource.Tags)
            {
                Console.WriteLine($"  Tag: {key} = {value}");
            }
        }
    }

    public void DumpDebugInfo()
    {
        var snapshot = _registry.CollectDebugSnapshot();

        foreach (var (path, properties) in snapshot)
        {
            Console.WriteLine($"\n{path}:");
            foreach (var (key, value) in properties)
            {
                Console.WriteLine($"  {key}: {value}");
            }
        }
    }

    public void QueryComponentTables()
    {
        var tables = _registry.GetDescendants("DatabaseEngine/ComponentTables");

        foreach (var table in tables)
        {
            Console.WriteLine($"Component: {table.Name}");
            Console.WriteLine($"  Type: {table.Tags["component_type"]}");
            // DebugProperties are evaluated on-demand via CollectDebugSnapshot()
        }
    }
}
```

## Example 5: Integration in Tests

```csharp
[Test]
public void Should_Register_All_ComponentTables()
{
    var registry = ServiceProvider.GetRequiredService<IResourceRegistry>();
    var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

    dbe.RegisterComponent<CompA>();
    dbe.RegisterComponent<CompB>();

    // Verify registration
    var tables = registry.GetDescendants("DatabaseEngine/ComponentTables").ToList();
    Assert.That(tables, Has.Count.EqualTo(2));
    Assert.That(tables.Select(t => t.Name), Contains.Item("CompA"));

    // Check debug properties
    var snapshot = registry.CollectDebugSnapshot();
    var compAInfo = snapshot["DatabaseEngine/ComponentTables/CompA"];
    Assert.That(compAInfo["EntityCount"], Is.EqualTo(0));
}

[Test]
public void Should_Track_Metrics_Via_OpenTelemetry()
{
    // Arrange: setup OTel listener
    var exportedItems = new List<Metric>();
    using var meterProvider = Sdk.CreateMeterProviderBuilder()
        .AddMeter("Typhon.*")
        .AddInMemoryExporter(exportedItems)
        .Build();

    var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
    dbe.RegisterComponent<CompA>();

    using var t = dbe.CreateTransaction();
    var comp = new CompA { Field1 = 42 };
    t.CreateEntity(ref comp);
    t.Commit();

    // Force collection
    meterProvider.ForceFlush();

    // Assert: verify metrics were captured
    var transactionMetric = exportedItems
        .FirstOrDefault(m => m.Name == "transactions_total");
    Assert.That(transactionMetric, Is.Not.Null);
}
```

## Example 6: Grafana Dashboard Query

With Prometheus exporter configured, you can query in Grafana:

```promql
# Total transactions across all database engines
sum(typhon_databaseengine_transactions_total)

# Entity count per component table
typhon_componenttable_entity_count{component_type="CompA"}

# Page cache hit rate
rate(typhon_pagedmmf_cache_hits[5m]) /
  (rate(typhon_pagedmmf_cache_hits[5m]) + rate(typhon_pagedmmf_cache_misses[5m]))

# P95 page load time
histogram_quantile(0.95, typhon_pagedmmf_page_load_ms_bucket)
```

## Key Benefits Summary

### For Development
- **Console debugging**: `registry.CollectDebugSnapshot()` shows live state
- **Test assertions**: Verify resource creation/cleanup
- **Hierarchy visualization**: Understand component relationships

### For Production
- **Standard metrics**: Auto-export to Grafana/Prometheus/etc.
- **Zero overhead when not observed**: OTel instruments are lazy
- **Rich context**: Tags provide filtering dimensions (component type, file path, etc.)

### Best of Both Worlds
- **Registry**: Runtime inspection, hierarchical organization, debug properties
- **OpenTelemetry**: Industry-standard telemetry export, rich aggregation, visualization tools

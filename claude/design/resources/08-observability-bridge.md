# 08 — Observability Bridge

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [06-snapshot-api.md](06-snapshot-api.md), [overview/09-observability.md](../../overview/09-observability.md)

---

## Overview

The resource graph is the **single source of truth** for system state. The observability layer is a **consumer** — it reads snapshots and exports them to OpenTelemetry metrics, health checks, and alerts.

> 💡 **Key principle:** The observability layer does NOT maintain counters. It reads what the resource graph provides and translates it for external consumption.

---

## Table of Contents

1. [Data Flow](#1-data-flow)
2. [Graph → OTel Metrics Mapping](#2-graph--otel-metrics-mapping)
3. [Graph → Health Checks Mapping](#3-graph--health-checks-mapping)
4. [Graph → Alerts Mapping](#4-graph--alerts-mapping)
5. [What Observability Does NOT Own](#5-what-observability-does-not-own)
6. [Configuration Options](#6-configuration-options)
7. [Integration Examples](#7-integration-examples)

---

## 1. Data Flow

```
┌──────────────────────────────────────────────────────────────────────┐
│                        HOT PATH (components)                         │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐  ┌────────────┐     │
│  │ PageCache  │  │ WALRing    │  │ TxnPool    │  │ CompTable  │     │
│  │ _cacheHits │  │ _usedBytes │  │ _active    │  │ _lookups   │     │
│  │ _usedSlots │  │ _writeOps  │  │ _committed │  │ _inserts   │     │
│  └─────┬──────┘  └─────┬──────┘  └─────┬──────┘  └─────┬──────┘     │
│        │               │               │               │             │
│        └───────────────┴───────────────┴───────────────┘             │
│                        │                                             │
│                        ▼                                             │
│              ┌─────────────────────┐                                 │
│              │   Resource Graph    │                                 │
│              │   (IResourceGraph)  │                                 │
│              └──────────┬──────────┘                                 │
└─────────────────────────┼────────────────────────────────────────────┘
                          │
                          │ GetSnapshot() every 1-5 seconds
                          ▼
              ┌─────────────────────┐
              │  ResourceSnapshot   │
              │  (immutable)        │
              └──────────┬──────────┘
                         │
        ┌────────────────┼────────────────┐
        │                │                │
        ▼                ▼                ▼
┌───────────────┐ ┌───────────────┐ ┌───────────────┐
│ OTel Metrics  │ │ Health Checks │ │ Alerts        │
│ Export        │ │ Provider      │ │ Generator     │
└───────┬───────┘ └───────┬───────┘ └───────┬───────┘
        │                 │                 │
        ▼                 ▼                 ▼
┌───────────────┐ ┌───────────────┐ ┌───────────────┐
│ Prometheus    │ │ /health       │ │ AlertManager  │
│ Grafana       │ │ Kubernetes    │ │ PagerDuty     │
│ Mimir         │ │ Load Balancer │ │ Slack         │
└───────────────┘ └───────────────┘ └───────────────┘
```

### Key Data Flow Properties

1. **Pull-based:** Observability pulls snapshots on a timer (not push from components)
2. **Decoupled:** Components don't know about OTel, health checks, or alerts
3. **Single source:** All external visibility comes from the same snapshot
4. **Immutable snapshots:** Safe to process without locks

---

## 2. Graph → OTel Metrics Mapping

### Naming Convention

Resource graph paths map to OTel metric names following this pattern:

```
typhon.resource.{path.with.dots}.{metric_kind}.{sub_metric}
```

> **Note:** OTel uses dots as hierarchy separators. When exported to Prometheus, dots are automatically converted to underscores (e.g., `typhon.resource.storage.page_cache.utilization` becomes `typhon_resource_storage_page_cache_utilization`).

### Examples

| Graph Node | Metric Kind | OTel Metric Name | OTel Type |
|------------|-------------|------------------|-----------|
| Storage/PageCache | Memory.AllocatedBytes | `typhon.resource.storage.page_cache.memory.bytes` | Gauge |
| Storage/PageCache | Capacity.Current | `typhon.resource.storage.page_cache.capacity.current` | Gauge |
| Storage/PageCache | Capacity.Maximum | `typhon.resource.storage.page_cache.capacity.max` | Gauge |
| Storage/PageCache | Capacity.Utilization | `typhon.resource.storage.page_cache.capacity.utilization` | Gauge |
| Storage/PageCache | DiskIO.ReadOps | `typhon.resource.storage.page_cache.disk_io.read_ops` | Counter |
| Storage/PageCache | Throughput["CacheHits"] | `typhon.resource.storage.page_cache.throughput.cache_hits` | Counter |
| Durability/WALRingBuffer | Contention.WaitCount | `typhon.resource.durability.wal_ring_buffer.contention.wait_count` | Counter |

> **Prometheus equivalent:** `typhon_resource_storage_page_cache_capacity_utilization` (dots → underscores)

### Mapping Implementation

```csharp
public class ResourceMetricsExporter
{
    private readonly IResourceGraph _graph;
    private readonly Meter _meter;

    // Cached snapshot for OTel observable gauges to read during Prometheus scrape.
    // Updated periodically by the background service.
    private ResourceSnapshot _lastSnapshot;

    public ResourceMetricsExporter(IResourceGraph graph)
    {
        _graph = graph;
        _meter = new Meter("Typhon.Resources", "1.0.0");

        // Register observable gauges that read from cached snapshot
        _meter.CreateObservableGauge(
            "typhon.resource.storage.page_cache.capacity.utilization",
            () => GetCapacityUtilization("Storage/PageCache"),
            unit: "ratio",
            description: "Page cache utilization (0.0-1.0)");

        // ... register other metrics
    }

    /// <summary>
    /// Called periodically (every 1-5 seconds) to refresh the snapshot.
    /// Rates are auto-computed by IResourceGraph from the previous snapshot.
    /// </summary>
    public void UpdateSnapshot()
    {
        _lastSnapshot = _graph.GetSnapshot();
        // _lastSnapshot.Rates contains pre-computed ops/sec for all throughput counters
    }

    private double GetCapacityUtilization(string path)
    {
        if (_lastSnapshot?.Nodes.TryGetValue(path, out var node) == true)
        {
            return node.Capacity?.Utilization ?? 0;
        }
        return 0;
    }
}
```

### Rate Export Options

Throughput rates can be obtained two ways:

| Approach | When to Use |
|----------|-------------|
| **Pre-computed (snapshot.Rates)** | Health checks, alert messages, non-Prometheus consumers |
| **Derived in Prometheus (`rate()`)** | Standard Prometheus dashboards |

**Pre-computed rates** (from snapshot):
```csharp
// Rates are auto-computed by IResourceGraph
var snapshot = _graph.GetSnapshot();
var cacheHitRate = snapshot.Rates?["Storage/PageCache"]["CacheHits"] ?? 0;
// → 523.5 (ops/sec)
```

**Derived rates** (Prometheus query):
```promql
rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m])
```

Both approaches are valid. Pre-computed rates are useful for alert messages ("523 ops/sec dropped to 12 ops/sec") while Prometheus derivation is standard practice for dashboards.

### OTel Metric Types

| Graph Metric Kind | OTel Type | Notes |
|-------------------|-----------|-------|
| Memory.AllocatedBytes | Gauge | Current value |
| Memory.PeakBytes | Gauge | High-water mark |
| Capacity.Current | Gauge | Current value |
| Capacity.Maximum | Gauge | Config value |
| Capacity.Utilization | Gauge | Derived ratio |
| DiskIO.* | Counter | Monotonically increasing |
| Contention.WaitCount | Counter | Monotonically increasing |
| Contention.TotalWaitUs | Counter | Monotonically increasing |
| Contention.MaxWaitUs | Gauge | High-water mark |
| Throughput.Count | Counter | Monotonically increasing |
| Duration.LastUs | Gauge | Most recent |
| Duration.AvgUs | Gauge | Rolling average |
| Duration.MaxUs | Gauge | High-water mark |

### Labels/Tags

Each metric includes labels for filtering:

```csharp
new TagList
{
    { "node_path", "Storage/PageCache" },
    { "node_type", "Cache" },
    { "subsystem", "Storage" }
}
```

---

## 3. Graph → Health Checks Mapping

### Health Status Derivation

Health checks derive from `Capacity.Utilization`:

```csharp
public class ResourceHealthCheck : IHealthCheck
{
    private readonly IResourceGraph _graph;
    private readonly ResourceHealthOptions _options;

    public HealthCheckResult CheckHealth(HealthCheckContext context)
    {
        var snapshot = _graph.GetSnapshot();

        var issues = new List<string>();
        var status = HealthStatus.Healthy;

        foreach (var (path, thresholds) in _options.Thresholds)
        {
            if (!snapshot.Nodes.TryGetValue(path, out var node))
                continue;

            if (node.Capacity == null)
                continue;

            var util = node.Capacity.Value.Utilization;

            if (util > thresholds.Unhealthy)
            {
                status = HealthStatus.Unhealthy;
                issues.Add($"{path}: {util:P0} (unhealthy threshold: {thresholds.Unhealthy:P0})");
            }
            else if (util > thresholds.Degraded)
            {
                if (status != HealthStatus.Unhealthy)
                    status = HealthStatus.Degraded;
                issues.Add($"{path}: {util:P0} (degraded threshold: {thresholds.Degraded:P0})");
            }
        }

        return new HealthCheckResult(status, string.Join("; ", issues));
    }
}
```

### Default Thresholds

| Graph Node | Healthy | Degraded | Unhealthy |
|-----------|---------|----------|-----------|
| Storage/PageCache | < 80% | 80–95% | > 95% |
| Durability/WALRingBuffer | < 60% | 60–80% | > 80% |
| DataEngine/TransactionPool | < 60% | 60–80% | > 80% |
| Durability/Checkpoint (dirty lag) | < 50% | 50–90% | > 90% |

### Composite Health

Overall engine health is the **worst** of all individual checks:

```csharp
public HealthStatus ComputeOverallHealth(ResourceSnapshot snapshot)
{
    var worst = HealthStatus.Healthy;

    foreach (var node in snapshot.Nodes.Values)
    {
        if (node.Capacity == null) continue;

        var nodeStatus = GetNodeHealthStatus(node);
        if (nodeStatus > worst)
            worst = nodeStatus;
    }

    return worst;
}
```

### Root Cause Attribution

When health is degraded or unhealthy, quickly identify the pressure point:

```csharp
public (HealthStatus Status, string PressurePoint) CheckHealthWithPressurePoint()
{
    var snapshot = _graph.GetSnapshot();
    var status = ComputeOverallHealth(snapshot);

    if (status == HealthStatus.Healthy)
        return (status, null);

    // FindMostUtilized is fast — suitable for health checks (every 1 second)
    // For detailed causal analysis, alerts use FindRootCause instead
    var pressurePoint = snapshot.FindMostUtilized();
    return (status, pressurePoint?.Path);
}
```

> **Health vs Alerts:** Health checks use `FindMostUtilized()` for quick identification (runs every 1 second). Alert generation uses `FindRootCause()` for detailed causal tracing (runs only on health transitions).

---

## 4. Graph → Alerts Mapping

### Alert Generation

When health transitions to Unhealthy, generate a detailed alert using `FindRootCause()` for accurate causal analysis:

```csharp
public class ResourceAlertGenerator
{
    private readonly IResourceGraph _graph;

    public ResourceAlertGenerator(IResourceGraph graph) => _graph = graph;

    public ResourceAlert GenerateAlert(ResourceSnapshot snapshot, string symptomPath)
    {
        // Use FindRootCause for causal chain tracing (see 07-budgets-exhaustion.md)
        var rootCause = _graph.FindRootCause(snapshot, symptomPath);
        var hotspots = snapshot.FindContentionHotspots().Take(3).ToList();

        return new ResourceAlert
        {
            Severity = AlertSeverity.Critical,
            Title = "Typhon Health Unhealthy",
            Symptom = symptomPath,
            RootCause = $"{rootCause.Path} " +
                        $"(Utilization: {rootCause.Capacity?.Utilization:P0})",
            CascadingEffects = hotspots.Select(h =>
                $"{h.Path}: Contention +{h.Contention.Value.WaitCount} waits"),
            Timestamp = snapshot.Timestamp
        };
    }
}
```

> **Note:** `FindRootCause()` traces the causal chain using hardcoded wait dependencies (see [07-budgets-exhaustion.md](07-budgets-exhaustion.md) §6). This identifies the actual bottleneck, not just the most utilized node.

### Example Alert

```
ALERT: Typhon Health Unhealthy
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Root Cause:
  Durability/WALRingBuffer (Utilization: 93%)

Cascading Effects:
  - DataEngine/TransactionPool: Contention +47 waits in last 5s
  - Storage/PageCache: Contention +12 waits in last 5s

Impact:
  Transactions blocked on WAL back-pressure

Possible Resolution:
  - WAL Writer I/O latency elevated (Duration.AvgUs = 850μs, normal: 200μs)
  - Check disk I/O queue depth
  - Consider increasing WAL ring buffer size

Timestamp: 2026-01-29T14:30:00Z
```

### AlertManager Integration

```yaml
# Prometheus alerting rule
groups:
  - name: typhon
    rules:
      - alert: TyphonResourceExhausted
        expr: typhon_resource_storage_page_cache_utilization > 0.95
        for: 30s
        labels:
          severity: critical
        annotations:
          summary: "Page cache exhausted"
          description: "Page cache at {{ $value | humanizePercentage }}"
```

---

## 5. What Observability Does NOT Own

The observability layer is a **read-only consumer**. It does NOT:

| Responsibility | Owner | NOT Observability |
|---------------|-------|-------------------|
| Maintain counters | Components (hot path) | ✗ |
| Define tree structure | Resource Graph | ✗ |
| Decide exhaustion policy | [07-budgets-exhaustion.md](07-budgets-exhaustion.md) | ✗ |
| Track resource lifecycle | Components | ✗ |
| Store historical data | External (Prometheus, Grafana) | ✗ |
| Process alerts | External (AlertManager) | ✗ |

Observability **only**:
- Reads snapshots periodically
- Translates to OTel format
- Computes health status
- Generates alert payloads

---

## 6. Configuration Options

### ObservabilityBridgeOptions

```csharp
public class ObservabilityBridgeOptions
{
    // ═══════════════════════════════════════════════════════════════
    // SNAPSHOT TIMING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// How often to take snapshots for OTel metrics export.
    /// </summary>
    public TimeSpan MetricsSnapshotInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to run health checks.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    // ═══════════════════════════════════════════════════════════════
    // HEALTH THRESHOLDS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-node health thresholds. If not specified, uses defaults.
    /// </summary>
    public Dictionary<string, HealthThresholds> Thresholds { get; set; } = new()
    {
        ["Storage/PageCache"] = new(0.80, 0.95),
        ["Durability/WALRingBuffer"] = new(0.60, 0.80),
        ["DataEngine/TransactionPool"] = new(0.60, 0.80),
    };

    // ═══════════════════════════════════════════════════════════════
    // OTEL EXPORT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Prefix for all OTel metric names.
    /// </summary>
    public string MetricNamePrefix { get; set; } = "typhon.resource";

    /// <summary>
    /// Whether to export Memory metrics.
    /// </summary>
    public bool ExportMemoryMetrics { get; set; } = true;

    /// <summary>
    /// Whether to export Capacity metrics.
    /// </summary>
    public bool ExportCapacityMetrics { get; set; } = true;

    /// <summary>
    /// Whether to export DiskIO metrics.
    /// </summary>
    public bool ExportDiskIOMetrics { get; set; } = true;

    /// <summary>
    /// Whether to export Contention metrics.
    /// </summary>
    public bool ExportContentionMetrics { get; set; } = true;

    /// <summary>
    /// Whether to export Throughput metrics.
    /// </summary>
    public bool ExportThroughputMetrics { get; set; } = true;

    /// <summary>
    /// Whether to export Duration metrics.
    /// </summary>
    public bool ExportDurationMetrics { get; set; } = true;
}

public record HealthThresholds(double Degraded, double Unhealthy);
```

### DI Registration

```csharp
services.Configure<ObservabilityBridgeOptions>(options =>
{
    options.MetricsSnapshotInterval = TimeSpan.FromSeconds(1);
    options.Thresholds["Storage/PageCache"] = new(0.70, 0.90);  // Custom
});

services.AddSingleton<IHealthCheck, ResourceHealthCheck>();
services.AddHostedService<ResourceMetricsExporterService>();
```

---

## 7. Integration Examples

### Example: Grafana Dashboard Query

```promql
# Page cache utilization over time
typhon_resource_storage_page_cache_capacity_utilization

# Cache hit rate (derived from throughput counters)
rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) /
(rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) +
 rate(typhon_resource_storage_page_cache_throughput_cache_misses[5m]))

# Top 5 contention hotspots by total wait time
topk(5, typhon_resource_contention_total_wait_us)

# Memory usage by subsystem
sum by (subsystem) (typhon_resource_memory_bytes)
```

> **Note:** Prometheus metric names use underscores (converted from OTel dot notation).

### Example: Kubernetes Health Probe

```yaml
apiVersion: v1
kind: Pod
spec:
  containers:
    - name: typhon
      livenessProbe:
        httpGet:
          path: /health/live
          port: 8080
        initialDelaySeconds: 10
        periodSeconds: 5
      readinessProbe:
        httpGet:
          path: /health/ready  # Uses ResourceHealthCheck
          port: 8080
        initialDelaySeconds: 5
        periodSeconds: 1
```

### Example: ASP.NET Health Check Registration

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<ResourceHealthCheck>("resources", tags: new[] { "ready" });

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

### Example: Custom Alerting Webhook

```csharp
public class AlertWebhookSender : IHostedService
{
    private readonly IResourceGraph _graph;
    private readonly IHttpClientFactory _httpFactory;
    private HealthStatus _lastStatus = HealthStatus.Healthy;

    public async Task CheckAndAlertAsync()
    {
        var snapshot = _graph.GetSnapshot();
        var currentStatus = ComputeOverallHealth(snapshot);

        // Only alert on transitions
        if (currentStatus != _lastStatus && currentStatus == HealthStatus.Unhealthy)
        {
            var alert = new ResourceAlertGenerator().GenerateAlert(snapshot);
            await SendWebhook(alert);
        }

        _lastStatus = currentStatus;
    }
}
```

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [06-snapshot-api.md](06-snapshot-api.md) | How snapshots are taken |
| [07-budgets-exhaustion.md](07-budgets-exhaustion.md) | Thresholds that drive health |
| [overview/09-observability.md](../../overview/09-observability.md) | Telemetry tracks, OTel catalog |
| [01-registry.md](01-registry.md) §7 (removed) | Former location of this content |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Data ownership | Components own counters, observability only reads | Separation of concerns |
| Export frequency | 1-5 seconds | Sufficient for monitoring, low overhead |
| Health computation | Worst-of composite | Simple, conservative |
| Alert generation | On health transition only | Avoid alert storms |
| Metric naming | `typhon.resource.{path}.{metric}` with dots | OTel standard; Prometheus auto-converts to underscores |
| Labels | path, type, subsystem | Standard Prometheus practice |
| **Health root cause** | **FindMostUtilized()** | Quick identification for frequent health checks |
| **Alert root cause** | **FindRootCause()** | Detailed causal tracing for operator alerts |
| **Rate export** | **Both pre-computed and derived** | Pre-computed for alerts/health; Prometheus derives for dashboards |
| Snapshot caching | Exporter caches snapshot | OTel observable pattern requires stable data during scrape |

---

*Document Version: 2.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

# Typhon Observability Guide

> 💡 **TL;DR — Quick start:** Jump to [Tier 1: Console Exporters](#tier-1-console-exporters-5-minutes) to verify everything works. For production, skip to [Tier 4: Full Observability Stack](#tier-4-full-observability-stack-prometheus--loki--jaeger--grafana).

This guide walks you through observing Typhon's internals across all three pillars: **metrics**, **logs**, and **traces**. Each tier builds on the previous, adding capabilities while increasing complexity.

---

## Table of Contents

- [Overview: The Three Pillars](#overview-the-three-pillars)
- [Prerequisites](#prerequisites)
- [Tier 1: Console Exporters](#tier-1-console-exporters-5-minutes) — Verify it works
- [Tier 2: Aspire Dashboard](#tier-2-aspire-dashboard-15-minutes) — Visual exploration (all three pillars)
- [Tier 3: Prometheus + Grafana](#tier-3-prometheus--grafana-30-minutes) — Persistent metrics
- [Tier 4: Full Observability Stack](#tier-4-full-observability-stack-prometheus--loki--jaeger--grafana) — Metrics + Logs + Traces
- [Tier 5: Enterprise Options](#tier-5-enterprise-options) — Managed services
- [Instrumenting Typhon with Spans](#instrumenting-typhon-with-spans)
- [Troubleshooting](#troubleshooting)
- [Reference: Typhon Metrics](#reference-typhon-metrics)
- [Reference: Structured Logging](#reference-structured-logging)

---

## Overview: The Three Pillars

Complete observability requires three complementary data types:

| Pillar | What It Shows | Tool | Query Language |
|--------|---------------|------|----------------|
| **Metrics** | Aggregated numbers over time (rates, gauges, histograms) | Prometheus | PromQL |
| **Logs** | Structured event records with context | Loki | LogQL |
| **Traces** | Request flow with timing (spans) | Jaeger | Trace ID lookup + filters |

### Why All Three?

| Question | Best Answered By |
|----------|------------------|
| "Is the system healthy?" | **Metrics** (dashboards, alerts) |
| "What happened at 3:42 AM?" | **Logs** (search by time, filter by level) |
| "Why was this request slow?" | **Traces** (flame graph, span breakdown) |
| "Which component caused the error?" | **Traces** → **Logs** (correlated by trace ID) |
| "Is this a pattern or one-off?" | **Logs** → **Metrics** (count errors over time) |

### Architecture Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Your Application                                  │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐              │
│  │    Metrics      │  │      Logs       │  │     Traces      │              │
│  │  (OTel Meter)   │  │   (Serilog)     │  │   (Activity)    │              │
│  └────────┬────────┘  └────────┬────────┘  └────────┬────────┘              │
└───────────┼─────────────────────┼─────────────────────┼─────────────────────┘
            │ /metrics            │ HTTP POST           │ OTLP gRPC
            ▼                     ▼                     ▼
     ┌────────────┐        ┌────────────┐        ┌────────────┐
     │ Prometheus │        │    Loki    │        │   Jaeger   │
     │  (metrics) │        │   (logs)   │        │  (traces)  │
     └─────┬──────┘        └─────┬──────┘        └─────┬──────┘
           │                     │                     │
           └─────────────────────┼─────────────────────┘
                                 ▼
                          ┌────────────┐
                          │  Grafana   │
                          │ (unified   │
                          │    UI)     │
                          └────────────┘
```

---

## Prerequisites

### Required NuGet Packages

```xml
<!-- Core OpenTelemetry -->
<PackageReference Include="OpenTelemetry" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.9.*" />

<!-- Metrics Exporters -->
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.9.*-*" />

<!-- Tracing Exporters -->
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.9.*" />

<!-- Logging (Serilog) -->
<PackageReference Include="Serilog" Version="4.*" />
<PackageReference Include="Serilog.Extensions.Logging" Version="8.*" />
<PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
<PackageReference Include="Serilog.Sinks.GrafanaLoki" Version="8.*" />
```

### Enable Typhon Telemetry

Create `typhon.telemetry.json`:

```json
{
  "Typhon": {
    "Telemetry": {
      "Enabled": true,
      "AccessControl": { "Enabled": true, "TrackContention": true },
      "PagedMMF": { "Enabled": true, "TrackPageAllocations": true, "TrackCacheHits": true },
      "BTree": { "Enabled": true, "TrackNodeSplits": true },
      "Transaction": { "Enabled": true, "TrackCommitRollback": true, "TrackConflicts": true }
    }
  }
}
```

---

## Tier 1: Console Exporters (5 minutes)

**Goal:** Verify all three pillars are working before adding infrastructure.

### Setup

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using System.Diagnostics;

// Define trace source
var activitySource = new ActivitySource("Typhon.Engine");

// Configure Serilog (logs)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Configure OpenTelemetry (metrics + traces)
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddConsoleExporter())
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddConsoleExporter());

var host = builder.Build();
await host.RunAsync();
```

### Expected Output

**Metrics:**
```
Export Metrics
  typhon.resource.storage.page_cache.capacity.utilization = 0.25
  typhon.resource.storage.page_cache.throughput.cache_hits = 1523
```

**Logs:**
```
[14:23:45 INF] Transaction committed {"tsn": 42851, "duration_ms": 12}
[14:23:45 WRN] Page cache utilization high {"utilization": 0.85}
```

**Traces:**
```
Activity.TraceId:    abc123def456
Activity.SpanId:     span001
Activity.Name:       Transaction.Commit
Activity.Duration:   00:00:00.0120000
```

### Verification Checklist

- [ ] Metrics appear with `typhon.resource.*` prefix
- [ ] Logs show structured JSON properties
- [ ] Traces show Activity name and duration

---

## Tier 2: Aspire Dashboard (15 minutes)

**Goal:** Visual exploration of all three pillars with zero infrastructure.

The .NET Aspire Dashboard provides a unified view of metrics, logs, and traces in a single UI - perfect for local development.

### Start Aspire Dashboard

```bash
podman run --rm -it \
    -p 18888:18888 \
    -p 4317:18889 \
    --name aspire-dashboard \
    mcr.microsoft.com/dotnet/aspire-dashboard:9.0
```

Access at: `http://localhost:18888`

### Application Configuration

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Serilog with OpenTelemetry sink
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.OpenTelemetry(options =>
    {
        options.Endpoint = "http://localhost:4317";
        options.Protocol = OtlpProtocol.Grpc;
    })
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// OpenTelemetry for metrics and traces
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));

var host = builder.Build();
await host.RunAsync();
```

### Using the Dashboard

| Tab | What You See |
|-----|--------------|
| **Metrics** | Time-series graphs, search by name |
| **Traces** | Waterfall view, span details |
| **Structured Logs** | Filterable log entries with properties |

**Limitations:** In-memory only (no persistence), no alerting, not for production.

---

## Tier 3: Prometheus + Grafana (30 minutes)

**Goal:** Persistent metrics with dashboards and basic alerting.

This tier focuses on **metrics only**. For logs and traces, see [Tier 4](#tier-4-full-observability-stack-prometheus--loki--jaeger--grafana).

### Directory Structure

```
monitoring/
├── compose.yaml
├── prometheus/
│   └── prometheus.yml
└── grafana/
    └── provisioning/
        ├── dashboards/
        │   ├── dashboard.yml
        │   └── typhon-overview.json
        └── datasources/
            └── datasource.yml
```

### compose.yaml

```yaml
version: "3.8"

services:
  prometheus:
    image: docker.io/prom/prometheus:v2.50.0
    container_name: typhon-prometheus
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus-data:/prometheus
    command:
      - '--config.file=/etc/prometheus/prometheus.yml'
      - '--storage.tsdb.path=/prometheus'
      - '--storage.tsdb.retention.time=15d'
    restart: unless-stopped

  grafana:
    image: docker.io/grafana/grafana:10.4.0
    container_name: typhon-grafana
    ports:
      - "3000:3000"
    volumes:
      - ./grafana/provisioning:/etc/grafana/provisioning:ro
      - grafana-data:/var/lib/grafana
    environment:
      - GF_SECURITY_ADMIN_USER=admin
      - GF_SECURITY_ADMIN_PASSWORD=typhon
    depends_on:
      - prometheus
    restart: unless-stopped

volumes:
  prometheus-data:
  grafana-data:
```

### prometheus.yml

```yaml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: 'typhon'
    static_configs:
      - targets: ['host.containers.internal:5000']
    metrics_path: '/metrics'
```

### Application Configuration

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddPrometheusExporter());

var app = builder.Build();
app.MapPrometheusScrapingEndpoint();  // Exposes /metrics
app.Run();
```

### Start and Verify

```bash
cd monitoring
podman-compose up -d
```

| Service | URL | Credentials |
|---------|-----|-------------|
| Prometheus | http://localhost:9090 | None |
| Grafana | http://localhost:3000 | admin / typhon |

---

## Tier 4: Full Observability Stack

**Goal:** Complete observability with metrics, structured logs, and traces (with flame graph visualization).

Two pre-built stacks are available in `claude/ops/stack/`. Both accept OTLP on port 4317 — only run one at a time.

| Stack | Path | RAM | UIs |
|-------|------|-----|-----|
| **PLJG** | `claude/ops/stack/pljg/` | ~1 GB | Grafana :3000, Jaeger :16686, Prometheus :9090 |
| **SigNoz** | `claude/ops/stack/signoz/` | ~4 GB | SigNoz :8080 |

Use `claude/ops/stack/select-stack.ps1` to interactively pick a stack, or `cd` into either directory and run `.\start.ps1`.

### The PLJG Stack

| Component | Pillar | Purpose | UI |
|-----------|--------|---------|-----|
| **P**rometheus | Metrics | Time-series storage | Grafana |
| **L**oki | Logs | Log aggregation (label-indexed) | Grafana |
| **J**aeger | Traces | Distributed tracing | Jaeger UI + Grafana |
| **G**rafana | All | Unified dashboards, correlation | Grafana |

### Why Jaeger (not Tempo)?

Jaeger provides a **built-in flame graph view** ("Trace Graph" tab) that Tempo lacks. You can still query Jaeger from Grafana for waterfall views, then click through to Jaeger UI for flame graphs.

### Directory Structure

```
monitoring/
├── compose.yaml
├── prometheus/
│   ├── prometheus.yml
│   └── rules/
│       └── typhon-alerts.yml
├── loki/
│   └── loki-config.yml
├── alertmanager/
│   └── alertmanager.yml
└── grafana/
    └── provisioning/
        ├── dashboards/
        │   ├── dashboard.yml
        │   └── typhon-overview.json
        └── datasources/
            └── datasources.yml
```

### compose.yaml (Full Stack)

```yaml
version: "3.8"

services:
  # ============ METRICS ============
  prometheus:
    image: docker.io/prom/prometheus:v2.50.0
    container_name: typhon-prometheus
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - ./prometheus/rules:/etc/prometheus/rules:ro
      - prometheus-data:/prometheus
    command:
      - '--config.file=/etc/prometheus/prometheus.yml'
      - '--storage.tsdb.path=/prometheus'
      - '--storage.tsdb.retention.time=30d'
      - '--web.enable-lifecycle'
    restart: unless-stopped
    networks:
      - typhon-observability

  alertmanager:
    image: docker.io/prom/alertmanager:v0.27.0
    container_name: typhon-alertmanager
    ports:
      - "9093:9093"
    volumes:
      - ./alertmanager/alertmanager.yml:/etc/alertmanager/alertmanager.yml:ro
      - alertmanager-data:/alertmanager
    command:
      - '--config.file=/etc/alertmanager/alertmanager.yml'
      - '--storage.path=/alertmanager'
    restart: unless-stopped
    networks:
      - typhon-observability

  # ============ LOGS ============
  loki:
    image: docker.io/grafana/loki:2.9.0
    container_name: typhon-loki
    ports:
      - "3100:3100"
    volumes:
      - ./loki/loki-config.yml:/etc/loki/local-config.yaml:ro
      - loki-data:/loki
    command: -config.file=/etc/loki/local-config.yaml
    restart: unless-stopped
    networks:
      - typhon-observability

  # ============ TRACES ============
  jaeger:
    image: docker.io/jaegertracing/all-in-one:1.54
    container_name: typhon-jaeger
    ports:
      - "16686:16686"   # Jaeger UI (with flame graph!)
      - "4317:4317"     # OTLP gRPC receiver
      - "4318:4318"     # OTLP HTTP receiver
    environment:
      - COLLECTOR_OTLP_ENABLED=true
    restart: unless-stopped
    networks:
      - typhon-observability

  # ============ VISUALIZATION ============
  grafana:
    image: docker.io/grafana/grafana:10.4.0
    container_name: typhon-grafana
    ports:
      - "3000:3000"
    volumes:
      - ./grafana/provisioning:/etc/grafana/provisioning:ro
      - grafana-data:/var/lib/grafana
    environment:
      - GF_SECURITY_ADMIN_USER=admin
      - GF_SECURITY_ADMIN_PASSWORD=typhon
      - GF_USERS_ALLOW_SIGN_UP=false
    depends_on:
      - prometheus
      - loki
      - jaeger
    restart: unless-stopped
    networks:
      - typhon-observability

volumes:
  prometheus-data:
  alertmanager-data:
  loki-data:
  grafana-data:

networks:
  typhon-observability:
    driver: bridge
```

### loki-config.yml

```yaml
# monitoring/loki/loki-config.yml
auth_enabled: false

server:
  http_listen_port: 3100
  grpc_listen_port: 9096

common:
  instance_addr: 127.0.0.1
  path_prefix: /loki
  storage:
    filesystem:
      chunks_directory: /loki/chunks
      rules_directory: /loki/rules
  replication_factor: 1
  ring:
    kvstore:
      store: inmemory

query_range:
  results_cache:
    cache:
      embedded_cache:
        enabled: true
        max_size_mb: 100

schema_config:
  configs:
    - from: 2020-10-24
      store: tsdb
      object_store: filesystem
      schema: v13
      index:
        prefix: index_
        period: 24h

ruler:
  alertmanager_url: http://alertmanager:9093

limits_config:
  retention_period: 744h  # 31 days
```

### prometheus.yml (with Alertmanager)

```yaml
# monitoring/prometheus/prometheus.yml
global:
  scrape_interval: 15s
  evaluation_interval: 15s

alerting:
  alertmanagers:
    - static_configs:
        - targets: ['alertmanager:9093']

rule_files:
  - /etc/prometheus/rules/*.yml

scrape_configs:
  - job_name: 'typhon'
    static_configs:
      - targets: ['host.containers.internal:5000']
    metrics_path: '/metrics'

  - job_name: 'prometheus'
    static_configs:
      - targets: ['localhost:9090']

  - job_name: 'loki'
    static_configs:
      - targets: ['loki:3100']
```

### Grafana Datasources (All Three)

```yaml
# monitoring/grafana/provisioning/datasources/datasources.yml
apiVersion: 1

datasources:
  # Metrics
  - name: Prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
    editable: false

  # Logs
  - name: Loki
    type: loki
    access: proxy
    url: http://loki:3100
    editable: false
    jsonData:
      derivedFields:
        - name: TraceID
          matcherRegex: '"trace_id":"([a-f0-9]+)"'
          url: '$${__value.raw}'
          datasourceUid: jaeger
          urlDisplayLabel: View Trace

  # Traces
  - name: Jaeger
    type: jaeger
    access: proxy
    url: http://jaeger:16686
    uid: jaeger
    editable: false
```

### alertmanager.yml

```yaml
# monitoring/alertmanager/alertmanager.yml
global:
  resolve_timeout: 5m

route:
  group_by: ['alertname', 'severity']
  group_wait: 10s
  group_interval: 10s
  repeat_interval: 1h
  receiver: 'default-receiver'

receivers:
  - name: 'default-receiver'
    # Configure your notification channels:
    # slack_configs:
    #   - api_url: 'https://hooks.slack.com/services/YOUR/WEBHOOK'
    #     channel: '#typhon-alerts'
```

### typhon-alerts.yml

```yaml
# monitoring/prometheus/rules/typhon-alerts.yml
groups:
  - name: typhon-alerts
    interval: 30s
    rules:
      - alert: TyphonPageCacheHigh
        expr: typhon_resource_storage_page_cache_capacity_utilization > 0.85
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "Page cache utilization high ({{ $value | humanizePercentage }})"

      - alert: TyphonPageCacheCritical
        expr: typhon_resource_storage_page_cache_capacity_utilization > 0.95
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: "Page cache near exhaustion ({{ $value | humanizePercentage }})"

      - alert: TyphonHighErrorRate
        expr: |
          sum(rate({app="typhon"} |= "error" [5m])) > 10
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "High error rate in logs"

      - alert: TyphonSlowCommits
        expr: typhon_resource_database_transactions_duration_commit_avg_us > 10000
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "Transaction commits averaging {{ $value }}µs"
```

### Application Configuration (Full Stack)

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// ============ LOGGING (Loki) ============
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", "typhon")
    .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
    // Add trace context to logs for correlation
    .Enrich.With<TraceIdEnricher>()
    .WriteTo.Console()
    .WriteTo.GrafanaLoki(
        "http://localhost:3100",
        labels: new List<LokiLabel>
        {
            new() { Key = "app", Value = "typhon" },
            new() { Key = "environment", Value = builder.Environment.EnvironmentName }
        })
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// ============ METRICS (Prometheus) + TRACES (Jaeger) ============
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://localhost:4317");
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        }));

// Register Typhon services
builder.Services.AddTyphonEngine();
builder.Services.AddTyphonObservabilityBridge();

var app = builder.Build();

// Expose /metrics for Prometheus
app.MapPrometheusScrapingEndpoint();

app.Run();

// ============ HELPER: Add trace_id to logs ============
public class TraceIdEnricher : Serilog.Core.ILogEventEnricher
{
    public void Enrich(Serilog.Events.LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory factory)
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("trace_id", activity.TraceId.ToString()));
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("span_id", activity.SpanId.ToString()));
        }
    }
}
```

### Start the Stack

```bash
cd monitoring
podman-compose up -d
```

### Access Points

| Service | URL | Purpose |
|---------|-----|---------|
| Grafana | http://localhost:3000 | Unified dashboards (admin/typhon) |
| Prometheus | http://localhost:9090 | Direct metric queries |
| Jaeger UI | http://localhost:16686 | **Trace flame graph!** |
| Alertmanager | http://localhost:9093 | Alert management |

### Using the Stack

#### Metrics in Grafana

1. Go to **Explore** → Select **Prometheus**
2. Query: `typhon_resource_storage_page_cache_capacity_utilization`

#### Logs in Grafana

1. Go to **Explore** → Select **Loki**
2. Query: `{app="typhon"} |= "error"`
3. Click a log line with `trace_id` → Click **"View Trace"** → Opens in Jaeger

#### Traces in Jaeger (with Flame Graph!)

1. Go to **http://localhost:16686**
2. Select Service: `Typhon.Engine`
3. Click **Find Traces**
4. Click a trace → See waterfall view
5. **Click "Trace Graph" tab** → **Flame graph visualization!**

### Correlation Workflow

```
1. Alert fires: "TyphonPageCacheCritical"
                    │
                    ▼
2. Grafana → Prometheus → See spike in utilization metric
                    │
                    ▼
3. Grafana → Loki → Query: {app="typhon"} | ts >= alert_time
   See: "Page eviction failed" with trace_id=abc123
                    │
                    ▼
4. Click trace_id → Jaeger → See full request breakdown
   → "Trace Graph" tab → Flame graph shows Commit took 80% of time
```

---

## Tier 5: Enterprise Options

For managed infrastructure:

| Provider | Metrics | Logs | Traces | Notes |
|----------|---------|------|--------|-------|
| **Grafana Cloud** | Mimir | Loki | Tempo | Free tier, fully managed |
| **Datadog** | ✅ | ✅ | ✅ | Excellent UX, expensive at scale |
| **Azure Monitor** | ✅ | ✅ | ✅ | Native Azure integration |
| **AWS CloudWatch** | ✅ | ✅ | X-Ray | Native AWS integration |

### Grafana Cloud Setup

```csharp
// All-in-one OTLP endpoint
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Typhon.Resources")
        .AddOtlpExporter(o => {
            o.Endpoint = new Uri("https://otlp-gateway-prod-XX.grafana.net/otlp");
            o.Headers = "Authorization=Basic <base64>";
        }))
    .WithTracing(t => t
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(o => {
            o.Endpoint = new Uri("https://otlp-gateway-prod-XX.grafana.net/otlp");
            o.Headers = "Authorization=Basic <base64>";
        }));

// Loki
Log.Logger = new LoggerConfiguration()
    .WriteTo.GrafanaLoki(
        "https://logs-prod-XX.grafana.net",
        credentials: new LokiCredentials 
        { 
            Login = "<user-id>", 
            Password = "<api-token>" 
        })
    .CreateLogger();
```

---

## Instrumenting Typhon with Spans

### What is a Span?

A **Span** represents a timed unit of work with:
- Name (operation)
- Start time + duration
- Parent span (call hierarchy)
- Tags/attributes (metadata)

### Creating Spans with Activity API

```csharp
using System.Diagnostics;

// Define once per class/assembly
private static readonly ActivitySource TyphonTracing = new("Typhon.Engine");

// Use in methods
public bool Commit()
{
    using var span = TyphonTracing.StartActivity("Transaction.Commit");
    span?.SetTag("tsn", TSN);
    span?.SetTag("component_count", _componentInfos.Count);

    // Child spans for sub-operations
    using (TyphonTracing.StartActivity("ValidateConflicts"))
    {
        ValidateConflicts();
    }

    using (TyphonTracing.StartActivity("UpdateIndices"))
    {
        UpdateIndices();
    }

    using (TyphonTracing.StartActivity("FlushRevisions"))
    {
        FlushRevisions();
    }

    span?.SetStatus(ActivityStatusCode.Ok);
    return true;
}
```

### Viewing as Flame Graph

1. Spans export to Jaeger via OTLP
2. Open Jaeger UI → Find trace
3. Click **"Trace Graph"** tab
4. See hierarchical flame graph of your instrumented operations

### What to Instrument

| Operation | Instrument? | Why |
|-----------|-------------|-----|
| `Transaction.Commit` | ✅ Yes | Critical path, want timing |
| `ReadEntity` / `UpdateEntity` | ✅ Yes | User-facing operations |
| `GetChunkHandle` (internal) | ⚠️ Maybe | Only if debugging internals |
| Small helper methods | ❌ No | Too granular, adds overhead |

**Rule of thumb:** Instrument operations you'd want to see in a flame graph. Not every function - just the meaningful ones.

---

## Troubleshooting

### Logs Not Appearing in Loki

1. Check Loki is running: `curl http://localhost:3100/ready`
2. Verify labels match your query: `{app="typhon"}`
3. Check Serilog configuration has correct Loki URL

### Traces Not Appearing in Jaeger

1. Check Jaeger is running: `curl http://localhost:16686`
2. Verify OTLP endpoint: `http://localhost:4317`
3. Ensure `AddSource("Typhon.Engine")` matches your ActivitySource name
4. Check spans are being created (add console exporter temporarily)

### Metrics Not in Prometheus

1. Check target status: http://localhost:9090/targets
2. Verify `/metrics` endpoint works: `curl http://localhost:5000/metrics | grep typhon`
3. Check firewall/network connectivity from container to host

### Log-Trace Correlation Not Working

1. Ensure `TraceIdEnricher` is added to Serilog
2. Verify logs contain `trace_id` field
3. Check Grafana Loki datasource has `derivedFields` configured

---

## Reference: Typhon Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `typhon_resource_*_memory_allocated_bytes` | Gauge | Current memory usage |
| `typhon_resource_*_capacity_utilization` | Gauge | Usage ratio (0-1) |
| `typhon_resource_*_throughput_cache_hits` | Counter | Cache hit count |
| `typhon_resource_*_throughput_cache_misses` | Counter | Cache miss count |
| `typhon_resource_*_throughput_commits` | Counter | Transaction commits |
| `typhon_resource_*_throughput_conflicts` | Counter | MVCC conflicts |
| `typhon_resource_*_duration_commit_avg_us` | Gauge | Average commit time |
| `typhon_resource_*_contention_wait_count` | Counter | Lock waits |
| `typhon_resource_*_contention_timeout_count` | Counter | Lock timeouts |

### Useful PromQL Queries

```promql
# Cache hit rate
rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) /
(rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) +
 rate(typhon_resource_storage_page_cache_throughput_cache_misses[5m]))

# Transaction throughput
rate(typhon_resource_database_transactions_throughput_commits[5m])

# Conflict rate
rate(typhon_resource_database_transactions_throughput_conflicts[5m]) /
rate(typhon_resource_database_transactions_throughput_commits[5m])
```

---

## Reference: Structured Logging

### Log Levels

| Level | When to Use |
|-------|-------------|
| `Debug` | Detailed diagnostic info (high volume) |
| `Information` | Normal operations (transaction committed, entity created) |
| `Warning` | Unusual but handled (cache pressure, conflict resolved) |
| `Error` | Failures requiring attention (timeout, corruption detected) |
| `Fatal` | Unrecoverable errors (database cannot start) |

### Useful LogQL Queries

```logql
# All errors from Typhon
{app="typhon"} |= "error"

# Parse JSON and filter
{app="typhon"} | json | level="error"

# Slow operations
{app="typhon"} | json | duration_ms > 100

# Specific transaction
{app="typhon"} | json | tsn=42851

# Errors with stack traces
{app="typhon"} | json | level="error" | line_format "{{.message}}\n{{.exception}}"
```

### Recommended Log Fields

```csharp
Log.Information("Transaction committed {@Transaction}", new
{
    TSN = transaction.TSN,
    ComponentCount = componentCount,
    DurationMs = stopwatch.ElapsedMilliseconds,
    ConflictDetected = hadConflict
});
```

---

## Summary: Stack Comparison

| Tier | Metrics | Logs | Traces | Flame Graph | Setup Time |
|------|---------|------|--------|-------------|------------|
| 1 - Console | ✅ | ✅ | ✅ | ❌ | 5 min |
| 2 - Aspire | ✅ | ✅ | ✅ (waterfall) | ❌ | 15 min |
| 3 - Prom+Grafana | ✅ | ❌ | ❌ | ❌ | 30 min |
| 4 - Full Stack | ✅ | ✅ | ✅ | ✅ (Jaeger) | 45 min |
| 5 - Enterprise | ✅ | ✅ | ✅ | Varies | Varies |

### SigNoz Alternative

SigNoz provides unified logs, metrics, and traces in a **single UI** backed by ClickHouse.
It requires more RAM (~4 GB) but offers a simpler developer experience with built-in log aggregation.

```powershell
# Quick start
cd claude\ops\stack\signoz
.\start.ps1
# Open http://localhost:8080
```

See the [SigNoz Evaluation](../design/observability/04-signoz-stack-evaluation.md) for a detailed comparison.

**Recommendation:** Start with Tier 2 (Aspire) for development, deploy Tier 4 (Full Stack) for production.

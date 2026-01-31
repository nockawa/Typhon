# Typhon Monitoring Guide

> 💡 **TL;DR — Quick start:** Jump to [Tier 1: Console Exporter](#tier-1-console-exporter-5-minutes) to verify metrics work immediately. For production, skip to [Tier 4: Production Stack](#tier-4-production-stack-grafana--prometheus--alertmanager).

This guide walks you through monitoring Typhon's internals, from simple console output to a full production-grade observability stack. Each tier builds on the previous, adding capabilities while increasing complexity.

---

## Table of Contents

- [Overview](#overview)
- [Prerequisites](#prerequisites)
- [Tier 1: Console Exporter](#tier-1-console-exporter-5-minutes) — Verify it works
- [Tier 2: OTLP + Aspire Dashboard](#tier-2-otlp--aspire-dashboard-15-minutes) — Visual exploration
- [Tier 3: Prometheus + Grafana](#tier-3-prometheus--grafana-30-minutes) — Real dashboards
- [Tier 4: Production Stack](#tier-4-production-stack-grafana--prometheus--alertmanager) — Full observability
- [Tier 5: Enterprise Options](#tier-5-enterprise-options) — Managed services
- [Automation Recommendations](#automation-recommendations)
- [Troubleshooting](#troubleshooting)
- [Reference: Typhon Metrics](#reference-typhon-metrics)

---

## Overview

### What Typhon Exposes

Typhon's observability is built on **OpenTelemetry (OTel)** with these metric categories:

| Category | Purpose | Example Metrics |
|----------|---------|-----------------|
| **Memory** | Track allocations | `allocated_bytes`, `peak_bytes` |
| **Capacity** | Bounded resource usage | `current`, `maximum`, `utilization` |
| **DiskIO** | Storage operations | `read_ops`, `write_ops`, `read_bytes`, `write_bytes` |
| **Contention** | Lock wait behavior | `wait_count`, `total_wait_us`, `max_wait_us`, `timeout_count` |
| **Throughput** | Operation counts | `cache_hits`, `cache_misses`, `commits`, `rollbacks` |
| **Duration** | Timing metrics | `last_us`, `avg_us`, `max_us` |

### Metric Naming Convention

```
typhon.resource.{path}.{metric_kind}.{sub_metric}

Examples:
- typhon.resource.storage.page_cache.memory.allocated_bytes
- typhon.resource.storage.page_cache.capacity.utilization
- typhon.resource.database.transactions.throughput.commits
```

### Architecture Summary

```
┌─────────────────────────────────────────────────────┐
│         Components (hot path)                        │
│  Increment counters via IMetricSource               │
└────────────┬────────────────────────────────────────┘
             │
             ▼
┌─────────────────────────────────────────────────────┐
│      Resource Graph (tree structure)                 │
│  Aggregates all component metrics                    │
└────────────┬────────────────────────────────────────┘
             │ GetSnapshot() every 1-5 seconds
             ▼
┌─────────────────────────────────────────────────────┐
│    ResourceMetricsExporter (OTel Meter)              │
│  Exposes observable gauges and counters              │
└──┬──────────────────┬──────────────────┬────────────┘
   │                  │                  │
   ▼                  ▼                  ▼
 Console          OTLP Endpoint      Prometheus
 Exporter         (Aspire, Jaeger)   Scrape Target
```

---

## Prerequisites

### Required NuGet Packages

Add these to your application project:

```xml
<!-- Core OTel -->
<PackageReference Include="OpenTelemetry" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.9.*" />

<!-- Exporters (add based on tier) -->
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.9.*-*" />
```

### Enable Typhon Telemetry

Create `typhon.telemetry.json` in your application directory:

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

Or via environment variables:
```bash
export TYPHON__TELEMETRY__ENABLED=true
export TYPHON__TELEMETRY__PAGEDMMF__ENABLED=true
```

---

## Tier 1: Console Exporter (5 minutes)

**Goal:** Verify metrics are being collected and exported correctly.

**Pros:**
- Zero infrastructure required
- Immediate feedback
- Great for debugging

**Cons:**
- No persistence
- No visualization
- Console spam in production

### Setup

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Typhon.Engine.Observability;

var builder = Host.CreateApplicationBuilder(args);

// Ensure telemetry is initialized early
TelemetryConfig.EnsureInitialized();

// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Typhon.Resources")  // Typhon's meter name
            .AddConsoleExporter(options =>
            {
                options.Targets = ConsoleExporterOutputTargets.Console;
            });
    });

// Register Typhon services
builder.Services.AddTyphonEngine();
builder.Services.AddTyphonObservabilityBridge();

var host = builder.Build();
await host.RunAsync();
```

### Expected Output

```
Export Metrics (1 items)
  Typhon.Resources
    typhon.resource.storage.page_cache.memory.allocated_bytes = 2097152
    typhon.resource.storage.page_cache.capacity.current = 256
    typhon.resource.storage.page_cache.capacity.maximum = 1024
    typhon.resource.storage.page_cache.capacity.utilization = 0.25
    typhon.resource.storage.page_cache.throughput.cache_hits = 1523
    typhon.resource.storage.page_cache.throughput.cache_misses = 47
```

### Verification Checklist

- [ ] Metrics appear in console every few seconds
- [ ] `typhon.resource.*` prefix is present
- [ ] Memory, capacity, and throughput metrics show non-zero values
- [ ] No exceptions in startup logs

---

## Tier 2: OTLP + Aspire Dashboard (15 minutes)

**Goal:** Visual exploration of metrics with minimal setup.

**What's New vs Tier 1:**
- Web-based dashboard
- Metric history (in-memory)
- Search and filtering
- No external dependencies (single binary)

**Pros:**
- Beautiful UI out of the box
- Single container/binary
- .NET-native experience
- Supports traces and logs too

**Cons:**
- In-memory only (no persistence)
- Limited querying capabilities
- Not designed for production alerting

### Option A: Standalone Aspire Dashboard (Recommended for beginners)

```bash
# Using Podman
podman run --rm -it \
  -p 18888:18888 \
  -p 4317:18889 \
  --name aspire-dashboard \
  mcr.microsoft.com/dotnet/aspire-dashboard:9.0
```

Access the dashboard at: `http://localhost:18888`

### Option B: .NET Tool

```bash
dotnet tool install -g dotnet-aspire-dashboard
aspire-dashboard
```

### Application Configuration

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Typhon.Resources")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:4317");
                options.Protocol = OtlpExportProtocol.Grpc;
            });
    });
```

### Exploring the Dashboard

1. Open `http://localhost:18888`
2. Navigate to **Metrics** tab
3. Search for `typhon.resource`
4. Click on metrics to see time-series graphs
5. Use filters to focus on specific resources

### Verification Checklist

- [ ] Dashboard accessible at port 18888
- [ ] Typhon metrics appear under "Metrics" tab
- [ ] Graphs show data points over time
- [ ] Can filter by metric name

---

## Tier 3: Prometheus + Grafana (30 minutes)

**Goal:** Persistent metrics with rich visualization and basic alerting.

**What's New vs Tier 2:**
- Persistent storage (survives restarts)
- Powerful query language (PromQL)
- Custom dashboards
- Basic alerting rules
- Industry-standard tooling

**Pros:**
- Battle-tested in production
- Extensive ecosystem
- Rich visualization options
- Query-based alerting

**Cons:**
- More moving parts
- Requires container orchestration
- Learning curve for PromQL

### Directory Structure

Create these files in your project:

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

### Container Stack (compose.yaml)

```yaml
# monitoring/compose.yaml
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
      - '--web.enable-lifecycle'
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
      - GF_USERS_ALLOW_SIGN_UP=false
    depends_on:
      - prometheus
    restart: unless-stopped

volumes:
  prometheus-data:
  grafana-data:
```

### Prometheus Configuration

```yaml
# monitoring/prometheus/prometheus.yml
global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: 'typhon'
    # Adjust to your application's address
    # Use host.containers.internal for Podman on Windows/Mac
    static_configs:
      - targets: ['host.containers.internal:5000']
    metrics_path: '/metrics'
    scheme: http
```

### Grafana Datasource

```yaml
# monitoring/grafana/provisioning/datasources/datasource.yml
apiVersion: 1

datasources:
  - name: Prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
    editable: false
```

### Grafana Dashboard Provisioning

```yaml
# monitoring/grafana/provisioning/dashboards/dashboard.yml
apiVersion: 1

providers:
  - name: 'Typhon Dashboards'
    orgId: 1
    folder: 'Typhon'
    type: file
    disableDeletion: false
    updateIntervalSeconds: 30
    options:
      path: /etc/grafana/provisioning/dashboards
```

### Application Configuration (Prometheus Exporter)

```csharp
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

TelemetryConfig.EnsureInitialized();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Typhon.Resources")
            .AddPrometheusExporter();  // Exposes /metrics endpoint
    });

builder.Services.AddTyphonEngine();
builder.Services.AddTyphonObservabilityBridge();

var app = builder.Build();

// Map the /metrics endpoint for Prometheus scraping
app.MapPrometheusScrapingEndpoint();

app.Run();
```

### Starting the Stack

```bash
cd monitoring
podman-compose up -d

# Or with Podman directly
podman compose up -d
```

### Access Points

| Service | URL | Credentials |
|---------|-----|-------------|
| Prometheus | http://localhost:9090 | None |
| Grafana | http://localhost:3000 | admin / typhon |

### Useful PromQL Queries

```promql
# Page cache utilization
typhon_resource_storage_page_cache_capacity_utilization

# Cache hit rate (5-minute window)
rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) /
(rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) +
 rate(typhon_resource_storage_page_cache_throughput_cache_misses[5m]))

# Transaction commit rate
rate(typhon_resource_database_transactions_throughput_commits[5m])

# Top 5 contention hotspots by total wait time
topk(5, typhon_resource_contention_total_wait_us)

# Memory usage by resource path
sum by (resource_path) (typhon_resource_memory_allocated_bytes)

# Average commit duration over time
typhon_resource_database_transactions_duration_commit_avg_us
```

### Verification Checklist

- [ ] Prometheus UI shows Typhon target as "UP" (Status > Targets)
- [ ] Can query `typhon_resource_*` metrics in Prometheus
- [ ] Grafana datasource test succeeds
- [ ] Basic graphs render in Grafana

---

## Tier 4: Production Stack (Grafana + Prometheus + Alertmanager)

**Goal:** Full production-grade observability with alerting.

**What's New vs Tier 3:**
- Alertmanager for notifications
- Pre-configured alert rules
- Comprehensive dashboards
- Notification routing (Slack, email, PagerDuty)

**Pros:**
- Production-ready
- Actionable alerts
- Team collaboration features
- Proven at scale

**Cons:**
- Most complex setup
- Requires alert tuning
- Notification service configuration

### Enhanced Directory Structure

```
monitoring/
├── compose.yaml
├── prometheus/
│   ├── prometheus.yml
│   └── rules/
│       └── typhon-alerts.yml
├── alertmanager/
│   └── alertmanager.yml
└── grafana/
    └── provisioning/
        ├── dashboards/
        │   ├── dashboard.yml
        │   └── typhon-overview.json
        └── datasources/
            └── datasource.yml
```

### Production Compose File

```yaml
# monitoring/compose.yaml
version: "3.8"

services:
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
      - '--web.enable-admin-api'
    restart: unless-stopped
    networks:
      - typhon-monitoring

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
      - typhon-monitoring

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
      - GF_ALERTING_ENABLED=true
      - GF_UNIFIED_ALERTING_ENABLED=true
    depends_on:
      - prometheus
    restart: unless-stopped
    networks:
      - typhon-monitoring

volumes:
  prometheus-data:
  alertmanager-data:
  grafana-data:

networks:
  typhon-monitoring:
    driver: bridge
```

### Prometheus with Alerting Rules

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
```

### Typhon Alert Rules

```yaml
# monitoring/prometheus/rules/typhon-alerts.yml
groups:
  - name: typhon-resource-alerts
    interval: 30s
    rules:
      # Page Cache Alerts
      - alert: TyphonPageCacheHighUtilization
        expr: typhon_resource_storage_page_cache_capacity_utilization > 0.80
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "Page cache utilization high"
          description: "Page cache is at {{ $value | humanizePercentage }} utilization for more than 2 minutes."
          runbook_url: "https://your-docs/runbooks/page-cache"

      - alert: TyphonPageCacheCritical
        expr: typhon_resource_storage_page_cache_capacity_utilization > 0.95
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: "Page cache near exhaustion"
          description: "Page cache is at {{ $value | humanizePercentage }} - immediate attention required."

      # Cache Performance
      - alert: TyphonLowCacheHitRate
        expr: |
          (
            rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) /
            (rate(typhon_resource_storage_page_cache_throughput_cache_hits[5m]) +
             rate(typhon_resource_storage_page_cache_throughput_cache_misses[5m]))
          ) < 0.70
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "Cache hit rate degraded"
          description: "Cache hit rate is {{ $value | humanizePercentage }}, below 70% threshold."

      # Contention Alerts
      - alert: TyphonHighContention
        expr: rate(typhon_resource_contention_wait_count[5m]) > 100
        for: 3m
        labels:
          severity: warning
        annotations:
          summary: "High lock contention detected"
          description: "{{ $labels.resource_path }} experiencing {{ $value | humanize }} waits/sec."

      - alert: TyphonContentionTimeouts
        expr: increase(typhon_resource_contention_timeout_count[5m]) > 0
        labels:
          severity: critical
        annotations:
          summary: "Lock acquisition timeouts occurring"
          description: "{{ $labels.resource_path }} had {{ $value }} timeouts in the last 5 minutes."

      # Transaction Alerts
      - alert: TyphonHighConflictRate
        expr: |
          rate(typhon_resource_database_transactions_throughput_conflicts[5m]) /
          rate(typhon_resource_database_transactions_throughput_commits[5m]) > 0.05
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "High transaction conflict rate"
          description: "{{ $value | humanizePercentage }} of transactions experiencing conflicts."

      - alert: TyphonSlowCommits
        expr: typhon_resource_database_transactions_duration_commit_avg_us > 10000
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "Slow transaction commits"
          description: "Average commit duration is {{ $value | humanize }}µs (threshold: 10ms)."

      # Memory Alerts
      - alert: TyphonHighMemoryUsage
        expr: |
          sum(typhon_resource_memory_allocated_bytes) > 1073741824
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "High memory allocation"
          description: "Typhon using {{ $value | humanize1024 }}B of memory."

  - name: typhon-availability
    rules:
      - alert: TyphonMetricsDown
        expr: up{job="typhon"} == 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: "Typhon metrics endpoint down"
          description: "Cannot scrape metrics from Typhon - application may be down."
```

### Alertmanager Configuration

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
  routes:
    - match:
        severity: critical
      receiver: 'critical-receiver'
      continue: true

receivers:
  - name: 'default-receiver'
    # Configure your notification channels here
    # Example: Slack webhook
    # slack_configs:
    #   - api_url: 'https://hooks.slack.com/services/YOUR/SLACK/WEBHOOK'
    #     channel: '#typhon-alerts'
    #     send_resolved: true

  - name: 'critical-receiver'
    # For critical alerts, you might want PagerDuty or multiple channels
    # pagerduty_configs:
    #   - service_key: 'YOUR_PAGERDUTY_KEY'

inhibit_rules:
  - source_match:
      severity: 'critical'
    target_match:
      severity: 'warning'
    equal: ['alertname']
```

### Verification Checklist

- [ ] All containers running: `podman ps`
- [ ] Prometheus targets healthy: http://localhost:9090/targets
- [ ] Alert rules loaded: http://localhost:9090/rules
- [ ] Alertmanager reachable: http://localhost:9093
- [ ] Grafana dashboards show data

---

## Tier 5: Enterprise Options

For organizations with existing observability infrastructure or managed service preferences:

### Grafana Cloud

**Pros:** Fully managed, includes Prometheus, Loki, Tempo
**Setup:** Use remote_write in Prometheus or OTLP exporter

```yaml
# prometheus.yml addition for Grafana Cloud
remote_write:
  - url: https://prometheus-prod-XX-XX.grafana.net/api/prom/push
    basic_auth:
      username: YOUR_INSTANCE_ID
      password: YOUR_API_KEY
```

### Azure Monitor / Application Insights

**Pros:** Native Azure integration, powerful AI-driven insights
**Setup:** Use Azure Monitor exporter

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Typhon.Resources")
            .AddAzureMonitorMetricExporter(options =>
            {
                options.ConnectionString = "InstrumentationKey=...";
            });
    });
```

### AWS CloudWatch

**Pros:** Native AWS integration, auto-scaling integration
**Setup:** Use CloudWatch EMF exporter or OTLP to AWS Distro

### Datadog

**Pros:** Excellent UX, APM integration, anomaly detection
**Setup:** Use Datadog exporter or OTLP endpoint

---

## Automation Recommendations

### 🎯 **Recommended Automation Point: Tier 3**

Tier 3 (Prometheus + Grafana) is the sweet spot for automation because:
- Covers 90% of monitoring needs
- Well-documented, stable APIs
- Container-based (easy to ship)
- No complex alert routing to configure

### Automation Approaches

#### 1. Docker/Podman Compose (Simplest)

Ship the `monitoring/` directory with your application:

```bash
# monitoring/start.sh
#!/bin/bash
podman-compose up -d
echo "Grafana: http://localhost:3000 (admin/typhon)"
echo "Prometheus: http://localhost:9090"
```

#### 2. Helm Chart (Kubernetes)

For Kubernetes deployments, create a Helm chart:

```yaml
# Chart.yaml
apiVersion: v2
name: typhon-monitoring
description: Monitoring stack for Typhon database engine
version: 1.0.0
dependencies:
  - name: prometheus
    version: "25.*"
    repository: https://prometheus-community.github.io/helm-charts
  - name: grafana
    version: "7.*"
    repository: https://grafana.github.io/helm-charts
```

#### 3. Terraform Module (Infrastructure as Code)

For cloud deployments:

```hcl
module "typhon_monitoring" {
  source = "./modules/typhon-monitoring"

  prometheus_retention_days = 30
  grafana_admin_password    = var.grafana_password
  alertmanager_slack_url    = var.slack_webhook
}
```

#### 4. One-Click Script

Create an interactive setup script:

```bash
#!/bin/bash
# setup-monitoring.sh

echo "Typhon Monitoring Setup"
echo "======================"
echo ""
echo "Select monitoring tier:"
echo "1) Console only (development)"
echo "2) Aspire Dashboard (local exploration)"
echo "3) Prometheus + Grafana (recommended)"
echo "4) Full production stack (with alerting)"
read -p "Choice [3]: " tier
tier=${tier:-3}

case $tier in
  1) echo "Configure console exporter in your app..." ;;
  2) podman run --rm -d -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:9.0 ;;
  3) cd monitoring && podman-compose up -d ;;
  4) cd monitoring-prod && podman-compose up -d ;;
esac
```

### Recommended Next Steps for Automation

1. **Create `monitoring/` directory** in Typhon repo root with Tier 3 configs
2. **Add `setup-monitoring.sh`** script for interactive setup
3. **Publish pre-built Grafana dashboard** as JSON in repo
4. **Document in main README** with quick-start command
5. **Consider NuGet package** `Typhon.Monitoring` that auto-configures exporters

---

## Troubleshooting

### Metrics Not Appearing

1. **Check telemetry is enabled:**
   ```bash
   # Verify typhon.telemetry.json exists and is valid
   cat typhon.telemetry.json | jq .
   ```

2. **Verify OTel meter registration:**
   ```csharp
   // Ensure meter name matches exactly
   metrics.AddMeter("Typhon.Resources")  // Case-sensitive!
   ```

3. **Check Prometheus scrape target:**
   - Visit http://localhost:9090/targets
   - Look for "typhon" job status

4. **Verify /metrics endpoint:**
   ```bash
   curl http://localhost:5000/metrics | grep typhon
   ```

### Common Issues

| Symptom | Cause | Solution |
|---------|-------|----------|
| No metrics in Prometheus | Scrape target unreachable | Check network, firewall, container networking |
| Metrics exist but no data | ObservabilityBridge not started | Call `services.AddTyphonObservabilityBridge()` |
| Stale metrics | Snapshot not updating | Verify `ResourceMetricsService` is running |
| Wrong metric names | Prometheus naming convention | OTel auto-converts dots to underscores |

### Container Networking (Podman)

When your Typhon app runs on the host and monitoring in containers:

```yaml
# For Podman on Windows/Mac
static_configs:
  - targets: ['host.containers.internal:5000']

# For Podman on Linux
static_configs:
  - targets: ['host.docker.internal:5000']
# Or use host network mode:
# network_mode: host
```

---

## Reference: Typhon Metrics

### Complete Metric List

| Metric Name | Type | Description |
|-------------|------|-------------|
| `typhon_resource_*_memory_allocated_bytes` | Gauge | Current memory allocation |
| `typhon_resource_*_memory_peak_bytes` | Gauge | Peak memory allocation |
| `typhon_resource_*_capacity_current` | Gauge | Current slot/entry usage |
| `typhon_resource_*_capacity_maximum` | Gauge | Maximum capacity |
| `typhon_resource_*_capacity_utilization` | Gauge | Utilization ratio (0-1) |
| `typhon_resource_*_disk_io_read_ops` | Counter | Read operation count |
| `typhon_resource_*_disk_io_write_ops` | Counter | Write operation count |
| `typhon_resource_*_disk_io_read_bytes` | Counter | Bytes read |
| `typhon_resource_*_disk_io_write_bytes` | Counter | Bytes written |
| `typhon_resource_*_contention_wait_count` | Counter | Lock wait count |
| `typhon_resource_*_contention_total_wait_us` | Counter | Total wait time (µs) |
| `typhon_resource_*_contention_max_wait_us` | Gauge | Max wait time (µs) |
| `typhon_resource_*_contention_timeout_count` | Counter | Lock timeout count |
| `typhon_resource_*_throughput_*` | Counter | Named operation counters |
| `typhon_resource_*_duration_*_last_us` | Gauge | Last operation duration |
| `typhon_resource_*_duration_*_avg_us` | Gauge | Average duration |
| `typhon_resource_*_duration_*_max_us` | Gauge | Maximum duration |

### Key Resources to Monitor

| Resource Path | Critical Metrics | Why It Matters |
|---------------|------------------|----------------|
| `storage.page_cache` | utilization, cache_hits/misses | Primary performance driver |
| `database.transactions` | commits, conflicts, commit_avg_us | Application throughput |
| `storage.*.contention` | wait_count, timeout_count | Concurrency bottlenecks |
| `database.component_tables.*` | capacity utilization | Data growth tracking |

---

## Summary: Tier Comparison

| Tier | Setup Time | Persistence | Visualization | Alerting | Best For |
|------|------------|-------------|---------------|----------|----------|
| 1 - Console | 5 min | ❌ | ❌ | ❌ | Quick verification |
| 2 - Aspire | 15 min | ❌ | ✅ Basic | ❌ | Local development |
| 3 - Prom+Grafana | 30 min | ✅ | ✅ Rich | ⚠️ Basic | Dev/staging |
| 4 - Full Stack | 1 hour | ✅ | ✅ Rich | ✅ Full | Production |
| 5 - Enterprise | Varies | ✅ | ✅ Rich | ✅ Full | Large orgs |

**Recommendation:** Start with Tier 1 to verify integration, move to Tier 2 for development, and deploy Tier 3 or 4 for anything beyond local testing.

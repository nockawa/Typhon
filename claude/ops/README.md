# Typhon Operations

> 💡 **TL;DR — Quick start:** See the [Observability Guide](monitoring-guide.md) for step-by-step setup from console output to full production stack.

This directory contains operational documentation for deploying, monitoring, and maintaining Typhon in production environments.

## Contents

| Document | Purpose |
|----------|---------|
| [monitoring-guide.md](monitoring-guide.md) | Complete observability setup: metrics, logs, and traces with 4-tier progression |
| [grafana-typhon-overview.json](grafana-typhon-overview.json) | Pre-built Grafana dashboard for metrics (import directly) |

## The Three Pillars

Typhon's observability covers all three pillars:

| Pillar | Tool | What It Answers |
|--------|------|-----------------|
| **Metrics** | Prometheus + Grafana | "Is the system healthy? What are the trends?" |
| **Logs** | Loki + Grafana | "What happened? Why did it fail?" |
| **Traces** | Jaeger + Grafana | "Where did time go? What's the call flow?" |

## Quick Start

### 1. Verify Telemetry Works (5 minutes)

Add console exporters to your app:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddConsoleExporter())
    .WithTracing(tracing => tracing
        .AddSource("Typhon")
        .AddConsoleExporter());
```

### 2. Visual Exploration (15 minutes)

Run the Aspire Dashboard for metrics and traces:

```bash
podman run --rm -it -p 18888:18888 -p 4317:18889 \
  mcr.microsoft.com/dotnet/aspire-dashboard:9.0
```

### 3. Production Stack (30 minutes)

See [Tier 3: Prometheus + Grafana](monitoring-guide.md#tier-3-prometheus--grafana-30-minutes) in the observability guide.

### 4. Full Observability (1 hour)

See [Tier 4: Full Stack (PLJG)](monitoring-guide.md#tier-4-full-stack-pljg-1-hour) for Prometheus + Loki + Jaeger + Grafana.

## Grafana Dashboard

Import `grafana-typhon-overview.json` directly into Grafana:

1. Go to Dashboards → Import
2. Upload JSON file or paste contents
3. Select your Prometheus data source
4. Click Import

The dashboard includes:
- Page cache utilization and hit rates
- Transaction throughput and latency
- Contention hotspots
- Disk I/O metrics

## Stack Components

| Component | Port | Purpose |
|-----------|------|---------|
| Prometheus | 9090 | Metrics collection and storage |
| Loki | 3100 | Log aggregation (structured logs via Serilog) |
| Jaeger | 16686 | Trace collection with flame graph visualization |
| Grafana | 3000 | Unified dashboard for all three pillars |

## Future Additions

Planned content for this directory:

- [ ] `deployment-guide.md` - Container and Kubernetes deployment patterns
- [ ] `runbooks/` - Operational runbooks for common alerts
- [ ] `backup-restore.md` - Backup and recovery procedures
- [ ] `performance-tuning.md` - Configuration tuning guide
- [ ] `grafana-typhon-traces.json` - Pre-built dashboard for trace analysis

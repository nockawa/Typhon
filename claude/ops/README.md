# Typhon Operations

> 💡 **TL;DR — Quick start:** See the [Monitoring Guide](monitoring-guide.md) for step-by-step setup instructions.

This directory contains operational documentation for deploying, monitoring, and maintaining Typhon in production environments.

## Contents

| Document | Purpose |
|----------|---------|
| [monitoring-guide.md](monitoring-guide.md) | Complete monitoring setup guide with 5 tiers from console to production |
| [grafana-typhon-overview.json](grafana-typhon-overview.json) | Pre-built Grafana dashboard (import directly) |

## Quick Start

### 1. Verify Metrics Work (5 minutes)

Add console exporter to your app:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddConsoleExporter());
```

### 2. Visual Exploration (15 minutes)

Run the Aspire Dashboard:

```bash
podman run --rm -it -p 18888:18888 -p 4317:18889 \
  mcr.microsoft.com/dotnet/aspire-dashboard:9.0
```

### 3. Production Stack (30 minutes)

See [Tier 3: Prometheus + Grafana](monitoring-guide.md#tier-3-prometheus--grafana-30-minutes) in the monitoring guide.

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

## Future Additions

Planned content for this directory:

- [ ] `deployment-guide.md` - Container and Kubernetes deployment patterns
- [ ] `runbooks/` - Operational runbooks for common alerts
- [ ] `backup-restore.md` - Backup and recovery procedures
- [ ] `performance-tuning.md` - Configuration tuning guide

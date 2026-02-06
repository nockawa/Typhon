# Typhon SigNoz Observability Stack

> **Quick start:** Run `.\start.ps1` and open http://localhost:8080 (SigNoz UI).

This directory contains the SigNoz-based observability stack for Typhon development.
SigNoz provides unified logs, metrics, and traces in a single UI, backed by ClickHouse.

## Architecture

```
┌─────────────────┐     OTLP      ┌─────────────────┐
│   .NET App      │──────────────>│  OTel Collector │
│ (MonitoringDemo)│   :4317       │  (SigNoz)       │
└─────────────────┘               └────────┬────────┘
                                           │
                                    writes to ClickHouse
                                           │
                                           ▼
                                  ┌────────────────┐
                                  │  ClickHouse    │
                                  │  (analytics)   │
                                  └────────┬───────┘
                                           │
                                           ▼
                                  ┌────────────────┐
                                  │   SigNoz UI    │
                                  │    :8080       │
                                  │                │
                                  │  Traces        │
                                  │  Metrics       │
                                  │  Logs          │
                                  │  Service Maps  │
                                  │  Alerting      │
                                  └────────────────┘
```

## Stack Components

| Service | Container | Purpose | Exposed Ports |
|---------|-----------|---------|---------------|
| **OTel Collector** | typhon-signoz-otel-collector | OTLP receiver, writes to ClickHouse | 4317 (gRPC), 4318 (HTTP) |
| **SigNoz** | typhon-signoz | Query service + UI + Alertmanager | 8080 (UI) |
| **ClickHouse** | typhon-signoz-clickhouse | Columnar analytics database | (internal) |
| **ZooKeeper** | typhon-signoz-zookeeper-1 | ClickHouse coordination | (internal) |
| **Schema Migrator (sync)** | typhon-signoz-schema-migrator-sync | Initializes sync tables | (init, exits) |
| **Schema Migrator (async)** | typhon-signoz-schema-migrator-async | Initializes async tables | (init, exits) |
| **Init ClickHouse** | typhon-signoz-init-clickhouse | Downloads histogramQuantile UDF | (init, exits) |

## Quick Reference

```powershell
# Start the stack
.\start.ps1

# Stop the stack
.\stop.ps1

# Stop and remove all data
.\stop.ps1 -RemoveVolumes

# View container status
podman compose ps

# View logs
podman compose logs -f

# View specific service logs
podman compose logs -f otel-collector
podman compose logs -f signoz
podman compose logs -f clickhouse
```

## Access Points

| Service | URL | Credentials |
|---------|-----|-------------|
| SigNoz UI | http://localhost:8080 | Create account on first visit |
| OTLP gRPC | localhost:4317 | (none) |
| OTLP HTTP | localhost:4318 | (none) |

## Configuring Your Application

Send telemetry to the OTLP endpoint at `localhost:4317` (gRPC) or `localhost:4318` (HTTP).
The SigNoz OTel Collector writes traces, metrics, and logs to ClickHouse.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));
```

This is the same configuration as the PLJG stack — both use OTLP port 4317.

## Port Conflicts

If default ports conflict with other services, edit `.env`:

```env
SIGNOZ_UI_PORT=8081
OTEL_GRPC_PORT=4319
OTEL_HTTP_PORT=4320
```

## Resource Requirements

SigNoz requires more resources than the PLJG stack due to ClickHouse:

| Resource | Minimum | Recommended |
|----------|---------|-------------|
| RAM | 4 GB | 6 GB |
| CPU | 2 cores | 4 cores |
| Disk | 2 GB | 5 GB |

Ensure your Podman machine has enough resources:

```powershell
podman machine init --cpus 4 --memory 6144 --disk-size 50
```

## Troubleshooting

### SigNoz UI not loading

The first start takes longer due to schema migration. Wait 30-60s and try again.

```powershell
# Check if schema migration is still running
podman compose logs schema-migrator-sync
podman compose logs schema-migrator-async
```

### ClickHouse out of memory

The dev config uses reduced memory settings. If ClickHouse crashes, increase
your Podman machine's memory:

```powershell
podman machine stop
podman machine set --memory 8192
podman machine start
```

### No traces appearing

1. Verify the stack is running: `podman compose ps`
2. Verify your app sends OTLP to `localhost:4317`
3. Check OTel Collector logs: `podman compose logs otel-collector`
4. In SigNoz UI, go to **Services** and check if your service appears

### Containers fail to start

```powershell
# Check for port conflicts
netstat -an | findstr "4317 4318 8080"

# View container logs
podman compose logs
```

## Files

| File | Purpose |
|------|---------|
| `compose.yaml` | Docker Compose for SigNoz stack |
| `otel-collector-config.yaml` | OTel Collector pipeline (writes to ClickHouse) |
| `otel-collector-opamp-config.yaml` | OpAMP manager config |
| `prometheus.yml` | Required by SigNoz service (remote read from ClickHouse) |
| `.env` | Version pins and port overrides |
| `start.ps1` | Windows startup script |
| `stop.ps1` | Windows shutdown script |
| `clickhouse/` | ClickHouse configuration (dev-tuned) |

## Related Documentation

- [Stack Selector](../README.md) - Choose between PLJG and SigNoz
- [PLJG Stack](../pljg/README.md) - Alternative lightweight stack
- [Monitoring Guide](../../monitoring-guide.md) - Detailed setup and usage
- [SigNoz Evaluation](../../../design/observability/04-signoz-stack-evaluation.md) - Evaluation report
- [SigNoz Features](../../../design/observability/05-signoz-features-deep-dive.md) - Feature reference

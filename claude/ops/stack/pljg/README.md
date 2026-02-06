# Typhon Observability Stack

> **Quick start:** Run `.\start.ps1` and open http://localhost:3000 (Grafana) or http://localhost:16686 (Jaeger).

This directory contains the containerized observability stack for Typhon development.

## Architecture

```
┌─────────────────┐     OTLP      ┌─────────────────┐
│   .NET App      │──────────────>│  OTel Collector │
│ (MonitoringDemo)│   :4317       │                 │
└─────────────────┘               └────────┬────────┘
                                           │
                          ┌────────────────┼────────────────┐
                          │ traces         │ metrics        │
                          ▼                ▼                │
                   ┌──────────┐     ┌────────────┐          │
                   │  Jaeger  │     │ Prometheus │          │
                   │  :16686  │     │   :9090    │          │
                   └──────────┘     └────────────┘          │
                          │                │                │
                          └────────┬───────┘                │
                                   ▼                        │
                            ┌──────────┐                    │
                            │ Grafana  │<───────────────────┘
                            │  :3000   │    Prometheus scrape :8889
                            └──────────┘
```

## Stack Components

| Service | Port | Purpose |
|---------|------|---------|
| **OTel Collector** | 4317 (gRPC), 4318 (HTTP), 8889 | Telemetry receiver and router |
| **Jaeger** | 16686 | Trace storage and flame graph visualization |
| **Prometheus** | 9090 | Metrics storage and querying |
| **Grafana** | 3000 | Dashboards and visualization |

## Quick Reference

```powershell
# Start the stack
.\start.ps1

# Stop the stack
.\stop.ps1

# View container status
podman compose ps

# View logs
podman compose logs -f

# View specific service logs
podman compose logs -f otel-collector
podman compose logs -f prometheus
```

## Access Points

| Service | URL | Credentials |
|---------|-----|-------------|
| Grafana | http://localhost:3000 | admin / typhon |
| Jaeger UI | http://localhost:16686 | (none) |
| Prometheus | http://localhost:9090 | (none) |

## Configuring Your Application

Send telemetry to the OTLP endpoint at `localhost:4317` (gRPC) or `localhost:4318` (HTTP).
The OTel Collector routes traces to Jaeger and metrics to Prometheus.

### Example: MonitoringDemo Configuration

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));
```

## Port Conflicts

If default ports conflict with other services, edit `.env`:

```env
GRAFANA_PORT=3001
JAEGER_UI_PORT=16687
PROMETHEUS_PORT=9091
OTEL_GRPC_PORT=4319
```

## Troubleshooting

### Podman machine not running

```powershell
podman machine start
```

### Containers fail to start

```powershell
# Check for port conflicts
netstat -an | findstr "3000 4317 9090 16686"

# View container logs
podman compose logs
```

### No traces appearing in Jaeger

1. Verify the stack is running: `podman compose ps`
2. Verify your app is configured for OTLP export to `localhost:4317`
3. Check OTel Collector logs: `podman compose logs otel-collector`

### No metrics in Grafana dashboards

1. Verify Prometheus is scraping: http://localhost:9090/targets
2. Check OTel Collector metrics endpoint: http://localhost:8889/metrics
3. Ensure your app is exporting metrics via OTLP

## Files

| File | Purpose |
|------|---------|
| `compose.yaml` | Docker Compose for full stack |
| `otel-collector-config.yaml` | OTel Collector pipeline configuration |
| `prometheus/prometheus.yml` | Prometheus scrape configuration |
| `.env` | Port configuration overrides |
| `start.ps1` | Windows startup script |
| `stop.ps1` | Windows shutdown script |
| `grafana/provisioning/` | Auto-provisioning configs |

## Related Documentation

- [Stack Selector](../README.md) - Choose between PLJG and SigNoz
- [SigNoz Stack](../signoz/README.md) - Alternative SigNoz stack
- [Monitoring Guide](../../monitoring-guide.md) - Detailed setup and usage
- [Observability Overview](../../../overview/09-observability.md) - Architecture
- [Design Document](../../../design/observability/01-monitoring-stack-setup.md) - Implementation plan

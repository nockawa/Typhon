# Typhon Observability Stacks

> **Quick start:** Run `.\select-stack.ps1` to interactively pick and launch a stack, or go directly to a stack's directory and run its `.\start.ps1`.

This directory hosts two alternative observability stacks for Typhon development. Both accept OTLP telemetry on port **4317** — your application code doesn't change regardless of which stack you use.

## Stack Comparison

| | PLJG | SigNoz |
|---|---|---|
| **Components** | Prometheus + Jaeger + Grafana + OTel Collector | ClickHouse + ZooKeeper + SigNoz + OTel Collector |
| **Containers** | 4 | 6 (+ 3 init/migration) |
| **RAM** | ~1 GB | ~4 GB |
| **UIs** | Grafana (:3000), Jaeger (:16686), Prometheus (:9090) | SigNoz (:8080) |
| **Traces** | Jaeger (flame graphs) | Built-in (flame charts) |
| **Metrics** | Prometheus + Grafana dashboards | ClickHouse + built-in dashboards |
| **Logs** | Not integrated (add Loki) | Built-in |
| **Best for** | Lightweight, familiar tools | Unified UI, all-in-one |

## Quick Start

### Option A: Interactive selector

```powershell
.\select-stack.ps1
```

### Option B: Direct launch

```powershell
# PLJG (Prometheus + Jaeger + Grafana)
cd pljg
.\start.ps1

# SigNoz (ClickHouse-backed)
cd signoz
.\start.ps1
```

## Important: Only One Stack at a Time

Both stacks bind to OTLP port **4317**. Only run one stack at a time. Use `select-stack.ps1` to safely switch — it will stop the running stack before starting another.

## Application Configuration

Both stacks use the same OTLP endpoint. No code changes needed:

```csharp
options.Endpoint = new Uri("http://localhost:4317");
```

## Stack READMEs

- [PLJG Stack](pljg/README.md) — Prometheus, Jaeger, Grafana
- [SigNoz Stack](signoz/README.md) — SigNoz with ClickHouse

## Related Documentation

- [Monitoring Guide](../monitoring-guide.md) — Detailed setup and usage
- [Observability Overview](../../overview/09-observability.md) — Architecture
- [SigNoz Evaluation](../../design/observability/04-signoz-stack-evaluation.md) — Evaluation report

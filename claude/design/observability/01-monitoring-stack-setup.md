# Part 1: Monitoring Stack Setup

> **TL;DR — Quick start:** This plan covers setting up the full PLJG observability stack (Prometheus, Loki, Jaeger, Grafana) using Podman on Windows. Jump to [Implementation Tasks](#6-implementation-tasks) for the work breakdown.

**Date:** February 2026
**Status:** Design
**Prerequisites:** Podman Desktop installed on Windows
**Related:** [monitoring-guide.md](../../ops/monitoring-guide.md), [09-observability.md](../../overview/09-observability.md)

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [Directory Structure](#3-directory-structure)
4. [Stack Components](#4-stack-components)
5. [Windows/Podman Considerations](#5-windowspodman-considerations)
6. [Implementation Tasks](#6-implementation-tasks)
7. [Verification Checklist](#7-verification-checklist)
8. [Open Questions](#8-open-questions)

---

## 1. Overview

### Goal

Create a one-command observability stack that provides:
- **Metrics** (Prometheus) — Time-series data, dashboards, alerting
- **Logs** (Loki) — Structured log aggregation with LogQL queries
- **Traces** (Jaeger) — Distributed tracing with flame graph visualization
- **Visualization** (Grafana) — Unified UI for all three pillars

### Why This Stack?

| Component | Why Chosen |
|-----------|------------|
| **Prometheus** | Industry standard, excellent PromQL, native OTel support |
| **Loki** | Label-indexed logs (not full-text), low resource usage, LogQL |
| **Jaeger** | Built-in flame graph ("Trace Graph" tab) — Tempo lacks this |
| **Grafana** | Unified dashboard, correlates all three pillars |

### Target Environment

- **OS:** Windows (11/10)
- **Container Runtime:** Podman Desktop
- **Use Case:** Development debugging, stress test analysis
- **Retention:** Hours to days (not months)

---

## 2. Architecture

### Unified OTLP Pipeline (Recommended)

With .NET 8+ and OpenTelemetry, all three pillars flow through a **single OTLP endpoint**:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Typhon Application (Host)                            │
│                                                                             │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐              │
│  │    Metrics      │  │      Logs       │  │     Traces      │              │
│  │  (OTel Meter)   │  │   (ILogger)     │  │   (Activity)    │              │
│  └────────┬────────┘  └────────┬────────┘  └────────┬────────┘              │
│           │                    │                    │                       │
│           └────────────────────┴────────────────────┘                       │
│                                │                                            │
│                    OpenTelemetry SDK                                        │
│                         OTLP gRPC                                           │
│                          :4317                                              │
└────────────────────────────────┬────────────────────────────────────────────┘
                                 │
                                 ▼
┌───────────────────────────────────────────────────────────────────────────────┐
│                        Podman Pod: typhon-observability                       │
│                                                                               │
│  ┌──────────────────────────────────────────────────────────────────────┐     │
│  │                         Jaeger (all-in-one)                          │     │
│  │                                                                      │     │
│  │   :4317 (OTLP gRPC) ─────► Receives metrics, logs, AND traces        │     │
│  │   :16686 (UI)       ─────► Query and visualize                       │     │
│  └──────────────────────────────────────────────────────────────────────┘     │
│                                    │                                          │
│  ┌────────────────┐                │                                          │
│  │  Prometheus    │ ◄──────────────┤  (optional: scrape Jaeger metrics)       │
│  │   :9090        │                │                                          │
│  └───────┬────────┘                │                                          │
│          │                         │                                          │
│          └─────────────────────────┘                                          │
│                         │                                                     │
│                         ▼                                                     │
│                ┌────────────────┐                                             │
│                │    Grafana     │                                             │
│                │     :3000      │                                             │
│                └────────────────┘                                             │
└───────────────────────────────────────────────────────────────────────────────┘
```

### Data Flow (Simplified)

| Pillar | Source | Transport | Destination | Query |
|--------|--------|-----------|-------------|-------|
| **Metrics** | `Typhon.Resources` Meter | OTLP gRPC to :4317 | Jaeger | Jaeger UI or Grafana |
| **Logs** | `ILogger` | OTLP gRPC to :4317 | Jaeger | Jaeger UI or Grafana |
| **Traces** | `ActivitySource` | OTLP gRPC to :4317 | Jaeger | Jaeger UI or Grafana |

### Why This Simplification?

- **Single export path** — All telemetry goes to one endpoint
- **Native .NET** — Uses `ILogger` instead of Serilog dependency
- **Fewer containers** — Jaeger handles all three pillars for development
- **Still extensible** — Add Prometheus/Loki for production scale if needed

### Optional: Full Stack for Production

For production with longer retention and scale, add Prometheus and Loki:

```
Development:   App ──OTLP──► Jaeger ◄── Grafana
Production:    App ──OTLP──► OTel Collector ──► Prometheus (metrics)
                                           ──► Loki (logs)
                                           ──► Jaeger (traces)
```

This plan focuses on the **development stack** (Jaeger-centric). Production scaling is a future enhancement.

---

## 3. Directory Structure

```
claude/ops/
├── README.md                         # Overview (exists)
├── monitoring-guide.md               # Setup guide (to be updated)
├── grafana-typhon-overview.json      # Dashboard (exists)
│
└── stack/                            # NEW - Compose stack
    ├── compose.yaml                  # Main compose file (Jaeger + Grafana)
    ├── compose.full.yaml             # Full stack (+ Prometheus + Loki)
    ├── start.ps1                     # Windows startup script
    ├── stop.ps1                      # Windows shutdown script
    ├── .env                          # Environment variables
    │
    ├── grafana/
    │   └── provisioning/
    │       ├── datasources/
    │       │   └── datasources.yml   # Auto-configure Jaeger
    │       └── dashboards/
    │           ├── dashboards.yml    # Dashboard provisioning
    │           └── typhon-overview.json  # Copy of existing dashboard
    │
    └── full/                         # Optional: Full stack configs
        ├── prometheus/
        │   ├── prometheus.yml
        │   └── rules/
        │       └── typhon-alerts.yml
        ├── loki/
        │   └── loki-config.yml
        └── alertmanager/
            └── alertmanager.yml
```

**Note:** The minimal stack (`compose.yaml`) contains only Jaeger + Grafana. The full stack (`compose.full.yaml`) adds Prometheus, Loki, and Alertmanager for production use.

---

## 4. Stack Components

### 4.1 Jaeger (Primary — All Three Pillars)

**Purpose:** Receives and stores metrics, logs, AND traces via OTLP

**Configuration highlights:**
- All-in-one deployment (collector + query + UI)
- OTLP receiver enabled on :4317 (gRPC) and :4318 (HTTP)
- Receives all three telemetry types from OpenTelemetry SDK
- In-memory storage (sufficient for development debugging)
- UI on :16686 with **Trace Graph** (flame graph) tab

**Why Jaeger as the hub:**
- Single endpoint for all telemetry — simpler configuration
- Built-in "Trace Graph" tab provides flame graph visualization
- Supports OTLP natively in recent versions
- Sufficient for development and debugging workflows

### 4.2 Grafana

**Purpose:** Unified visualization and dashboards

**Configuration highlights:**
- Auto-provisioned Jaeger datasource
- Auto-provisioned Typhon dashboard
- Admin credentials: admin/typhon (development only)
- Can query Jaeger for metrics, logs, and traces

**Key files:**
- `datasources.yml` — Auto-configure Jaeger datasource
- `dashboards.yml` — Auto-load Typhon dashboard

### 4.3 Prometheus (Optional — For Metrics Retention)

**Purpose:** Long-term metrics storage with PromQL

**When to add:**
- Need metrics retention beyond Jaeger's in-memory storage
- Want to use PromQL for complex queries
- Setting up alerting rules

**Configuration highlights:**
- Can scrape Jaeger's metrics endpoint
- Or receive via OTLP with remote-write adapter
- Retention: 15 days (configurable)

**Key files:**
- `prometheus.yml` — Scrape configuration
- `typhon-alerts.yml` — Alert rules for common issues

### 4.4 Loki (Optional — For Log Retention)

**Purpose:** Long-term log storage with LogQL

**When to add:**
- Need log retention beyond Jaeger's capacity
- Want powerful log querying with LogQL
- Production environments

**Configuration highlights:**
- Receives logs via OTLP or Promtail
- Label-based indexing (efficient)
- Schema v13 with TSDB store

### 4.5 Alertmanager (Optional)

**Purpose:** Alert routing and notification

**When to add:**
- Want alerts sent to Slack/email/PagerDuty
- Production monitoring

**Configuration highlights:**
- Receives alerts from Prometheus
- Configurable routing and receivers

### Stack Tiers

| Tier | Components | Use Case |
|------|------------|----------|
| **Minimal** | Jaeger + Grafana | Development debugging (this plan) |
| **Standard** | + Prometheus | Add metrics retention and alerting |
| **Full** | + Loki + Alertmanager | Production observability |

This plan implements the **Minimal** tier. Other components can be added incrementally.

---

## 5. Windows/Podman Considerations

### 5.1 Podman Machine Setup

```powershell
# Initialize podman machine (if not already done)
podman machine init --cpus 4 --memory 4096 --disk-size 50

# Start the machine
podman machine start

# Verify
podman info
```

### 5.2 Host Networking

Accessing the host machine from containers on Podman/Windows:

| Method | Address | Notes |
|--------|---------|-------|
| `host.containers.internal` | Resolves to host | Recommended for Podman |
| `host.docker.internal` | Resolves to host | Works on some setups |
| Host IP directly | e.g., `192.168.1.x` | Requires knowing IP |

**Prometheus scrape target:** `host.containers.internal:5000`

### 5.3 Volume Mounts

Podman on Windows uses WSL2 backend. Path translation:
- Windows: `C:\Dev\github\Typhon\claude\ops\stack\prometheus`
- In compose: `./prometheus:/etc/prometheus:ro`

The compose file uses relative paths which Podman handles correctly.

### 5.4 Port Conflicts

Default ports used by the stack:

| Port | Service | Stack | Conflict Check |
|------|---------|-------|----------------|
| 3000 | Grafana | Minimal | Common (dev servers) |
| 4317 | Jaeger OTLP gRPC | Minimal | Uncommon |
| 4318 | Jaeger OTLP HTTP | Minimal | Uncommon |
| 16686 | Jaeger UI | Minimal | Uncommon |
| 3100 | Loki | Full | Uncommon |
| 9090 | Prometheus | Full | Uncommon |
| 9093 | Alertmanager | Full | Uncommon |

If port 3000 conflicts, use `.env` to override:
```env
GRAFANA_PORT=3001
```

### 5.5 Startup Script (start.ps1)

```powershell
#!/usr/bin/env pwsh
# claude/ops/stack/start.ps1

param(
    [switch]$Full  # Use -Full for full stack (Prometheus + Loki + Alertmanager)
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$ComposeFile = if ($Full) { "compose.full.yaml" } else { "compose.yaml" }
$StackName = if ($Full) { "Full (PLJG)" } else { "Minimal (Jaeger + Grafana)" }

Write-Host "Starting Typhon Observability Stack [$StackName]..." -ForegroundColor Cyan

# Check podman is available
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Error "Podman not found. Please install Podman Desktop."
    exit 1
}

# Check podman machine is running
$machineStatus = podman machine info 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Starting Podman machine..." -ForegroundColor Yellow
    podman machine start
}

# Start the stack
Push-Location $ScriptDir
try {
    podman compose -f $ComposeFile up -d

    Write-Host ""
    Write-Host "Stack started successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Access points:" -ForegroundColor Cyan
    Write-Host "  Grafana:     http://localhost:3000  (admin/typhon)"
    Write-Host "  Jaeger UI:   http://localhost:16686"
    Write-Host "  OTLP gRPC:   localhost:4317  (for app telemetry)"

    if ($Full) {
        Write-Host "  Prometheus:  http://localhost:9090"
        Write-Host "  Loki:        http://localhost:3100"
    }

    Write-Host ""
    Write-Host "To stop: .\stop.ps1" -ForegroundColor Gray
}
finally {
    Pop-Location
}
```

---

## 6. Application Configuration (Native .NET)

### Required NuGet Packages

```xml
<!-- OpenTelemetry Core -->
<PackageReference Include="OpenTelemetry" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.9.*" />

<!-- Optional: Console exporter for debugging -->
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.9.*" />
```

### Minimal Configuration

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;

var builder = Host.CreateApplicationBuilder(args);

// Configure OpenTelemetry — all three pillars via OTLP
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));

// Logs also go via OTLP
builder.Logging.AddOpenTelemetry(options =>
{
    options.AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"));
});

// Register Typhon services
builder.Services.AddTyphonEngine();
builder.Services.AddTyphonObservabilityBridge();

var host = builder.Build();
await host.RunAsync();
```

### Key Points

- **No Serilog** — Uses native `ILogger` with OpenTelemetry exporter
- **Single endpoint** — All telemetry goes to `:4317` (Jaeger OTLP)
- **Unified SDK** — OpenTelemetry handles metrics, traces, AND logs

---

## 7. Implementation Tasks

### Phase 1: Core Stack Files (Minimal)

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 1.1 | Create directory structure | `claude/ops/stack/` with subdirectories | S |
| 1.2 | Write `compose.yaml` | Jaeger + Grafana (minimal stack) | S |
| 1.3 | Write `datasources.yml` | Auto-provision Jaeger datasource in Grafana | S |
| 1.4 | Write `dashboards.yml` | Auto-provision Typhon dashboard | S |
| 1.5 | Copy dashboard JSON | Copy existing `grafana-typhon-overview.json` to provisioning | S |

### Phase 2: Windows Integration

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 2.1 | Write `start.ps1` | One-click startup script with validation | S |
| 2.2 | Write `stop.ps1` | Clean shutdown script | S |
| 2.3 | Write `.env` | Environment variables for port overrides | S |
| 2.4 | Test on Podman Desktop | Verify stack starts and telemetry flows | M |

### Phase 3: Full Stack (Optional)

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 3.1 | Write `compose.full.yaml` | Add Prometheus + Loki + Alertmanager | M |
| 3.2 | Write `prometheus.yml` | Scrape config for full stack | S |
| 3.3 | Write `loki-config.yml` | Log retention config | S |
| 3.4 | Write `typhon-alerts.yml` | Alert rules | M |
| 3.5 | Write `alertmanager.yml` | Alert routing | S |

### Phase 4: Documentation

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 4.1 | Update `monitoring-guide.md` | Reflect native .NET approach, new stack location | M |
| 4.2 | Write `stack/README.md` | Quick reference for the stack directory | S |

---

## 8. Verification Checklist

After implementation, verify:

### Stack Health (Minimal)
- [ ] `podman compose ps` shows Jaeger + Grafana running
- [ ] Grafana accessible at http://localhost:3000
- [ ] Jaeger UI accessible at http://localhost:16686
- [ ] Jaeger health: http://localhost:16686 loads

### Data Flow
- [ ] Run Typhon app with OTLP export to :4317
- [ ] Jaeger UI → Search → Service "Typhon.Engine" shows traces
- [ ] Jaeger UI → Monitor tab shows metrics (if enabled)
- [ ] Grafana → Explore → Jaeger shows traces
- [ ] Typhon dashboard loads (when implemented)

### Telemetry Verification
- [ ] Traces: Transaction operations appear as spans
- [ ] Metrics: `typhon.resource.*` metrics visible
- [ ] Logs: ILogger output appears in Jaeger
- [ ] Flame graph: "Trace Graph" tab renders correctly

### Full Stack Health (Optional)
- [ ] Prometheus accessible at http://localhost:9090
- [ ] Prometheus targets show Jaeger as "UP"
- [ ] Loki ready at http://localhost:3100/ready
- [ ] Grafana → Explore → Loki shows logs

---

## 9. Open Questions

1. **Persistence across restarts:** Should volumes persist data or start fresh each time? (Current: persist with named volumes)

2. **Resource limits:** Should we set CPU/memory limits on containers? (Suggested: 1GB for minimal stack, 2GB for full)

3. **HTTPS/TLS:** Is HTTPS needed for local development? (Current: HTTP only)

4. **Jaeger storage backend:** Use in-memory (default) or Badger for persistence? (Current: in-memory, sufficient for dev)

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [monitoring-guide.md](../../ops/monitoring-guide.md) | Detailed setup instructions (to be updated) |
| [09-observability.md](../../overview/09-observability.md) | Observability architecture overview |
| [08-observability-bridge.md](../../reference/resources/08-observability-bridge.md) | OTel metrics export design |
| [02-span-instrumentation.md](02-span-instrumentation.md) | Tracing instrumentation plan |
| [03-deep-diagnostics.md](03-deep-diagnostics.md) | Deep diagnostics design |

---

*Document Version: 1.0*
*Last Updated: February 2026*
*Part of the Observability Implementation series*

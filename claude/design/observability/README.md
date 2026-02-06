# Observability Implementation

> Implementation plans for Typhon's development observability stack: monitoring infrastructure, distributed tracing, and deep diagnostics for lock forensics.

**Date:** February 2026
**Status:** Design complete, ready for implementation
**GitHub Issue:** (to be created)

---

## Quick Start

> 💡 **Want to set up the monitoring stack?** Start with [01-monitoring-stack-setup.md](01-monitoring-stack-setup.md).
>
> **Need to add tracing to Typhon code?** See [02-span-instrumentation.md](02-span-instrumentation.md).
>
> **Debugging lock contention?** Jump to [03-deep-diagnostics.md](03-deep-diagnostics.md).

---

## Document Series

| # | Document | Purpose | Status |
|---|----------|---------|--------|
| 01 | [Monitoring Stack Setup](01-monitoring-stack-setup.md) | Jaeger + Grafana stack with Podman, native .NET OTel | ✅ Core Done (Phase 3 optional) |
| 02 | [Span Instrumentation](02-span-instrumentation.md) | Transaction, Index, Storage tracing with `Activity` API | ✅ Done (read-path spans deferred by design) |
| 03 | [Deep Diagnostics](03-deep-diagnostics.md) | Lock contention forensics via Jaeger spans | 📐 Designed |
| 04 | [SigNoz Stack Evaluation](04-signoz-stack-evaluation.md) | Evaluation of SigNoz as PLJG replacement | ✅ Evaluated + Implemented |
| 05 | [SigNoz Features Deep Dive](05-signoz-features-deep-dive.md) | Comprehensive SigNoz feature reference | 📖 Reference |

---

## Prerequisites

Before reading this series, familiarize yourself with:

| Document | What It Covers |
|----------|----------------|
| [overview/09-observability.md](../../overview/09-observability.md) | Telemetry tracks, Track 1-4 architecture, metrics catalog |
| [ops/monitoring-guide.md](../../ops/monitoring-guide.md) | Existing observability setup guide (to be updated) |
| [reference/resources/08-observability-bridge.md](../../reference/resources/08-observability-bridge.md) | OTel metrics export design |

---

## Document Dependencies

```
README.md ◄─── Entry point (you are here)
    │
    ├── 01-monitoring-stack-setup.md ─── Infrastructure
    │       │
    │       │   Jaeger + Grafana via Podman
    │       │   Native .NET logging (no Serilog)
    │       │   Unified OTLP pipeline
    │       │
    │       ▼
    ├── 02-span-instrumentation.md ──── Distributed Tracing
    │       │
    │       │   ActivitySource for Typhon.Engine
    │       │   Transaction, Index, Storage spans
    │       │   Flame graph visualization in Jaeger
    │       │
    │       ▼
    ├── 03-deep-diagnostics.md ──────── Lock Forensics
    │       │
    │       │   Contention-only capture
    │       │   Stack traces at wait points
    │       │   Natural parent-child with transactions
    │       │   30-60 minute ring buffer retention
    │
    ├── 04-signoz-stack-evaluation.md ─ Alternative Stack Evaluation
    │       │
    │       │   SigNoz vs PLJG comparison
    │       │   Licensing analysis (100% free)
    │       │   Gap analysis & recommendations
    │       │
    │       ▼
    └── 05-signoz-features-deep-dive.md ─ SigNoz Feature Reference
            │
            │   Panel types & visualizations
            │   Query builder & aggregations
            │   Service dependency map
            │   APM, tracing, logs, alerting
```

**Reading order:**
- Sequential (01 → 03) for full understanding
- Start with 01 to set up the stack first
- Jump to 03 if specifically debugging lock contention

---

## Reading Guide by Goal

| If You Want To... | Read These Documents |
|-------------------|---------------------|
| **Set up the monitoring stack** | 01-monitoring-stack-setup.md |
| **Add tracing to Typhon operations** | 02-span-instrumentation.md |
| **Debug lock contention** | 03-deep-diagnostics.md |
| **Understand flame graphs** | 02-span-instrumentation.md §4 |
| **Configure the stack for Windows/Podman** | 01-monitoring-stack-setup.md §5 |
| **Evaluate SigNoz as alternative** | 04-signoz-stack-evaluation.md |
| **Explore SigNoz features in depth** | 05-signoz-features-deep-dive.md |
| **Build custom SigNoz dashboards** | 05-signoz-features-deep-dive.md §2-4 |
| **Run the demo stress test** | (Demo project TBD) |

---

## Architecture Overview

### Unified OTLP Pipeline

All three pillars (metrics, logs, traces) flow through a single endpoint:

```
┌──────────────────────────────────────────────────────────────────┐
│                    Typhon Application                            │
│                                                                  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐            │
│  │   Metrics    │  │    Logs      │  │   Traces     │            │
│  │ (OTel Meter) │  │  (ILogger)   │  │ (Activity)   │            │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘            │
│         │                 │                 │                    │
│         └─────────────────┴─────────────────┘                    │
│                           │                                      │
│                   OpenTelemetry SDK                              │
│                     OTLP gRPC :4317                              │
└───────────────────────────┬──────────────────────────────────────┘
                            │
                            ▼
              ┌─────────────────────────┐
              │     Jaeger (all-in-one) │
              │                         │
              │  :4317 ─► OTLP receiver │
              │  :16686 ─► UI + Flame   │
              │            Graphs       │
              └─────────────────────────┘
                            │
                            ▼
              ┌─────────────────────────┐
              │        Grafana          │
              │     (unified UI)        │
              └─────────────────────────┘
```

### Deep Diagnostics Flow

Lock contention events naturally nest inside transaction traces:

```
Transaction.Commit (12ms) ─────────────────────────────────────
├── Commit.ValidateConflicts (0.5ms) ─────
├── Lock.Contention (2.3ms) ───────────────  ◄── Deep diagnostics span
│   └── [event] stack_trace: "at UpdateEntity()..."
└── Commit.FlushRevisions (3ms) ───────────
```

---

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Container runtime** | Podman on Windows | User's environment |
| **Logging** | Native `ILogger` + OTel | No Serilog dependency, unified pipeline |
| **Primary backend** | Jaeger (all three pillars) | Flame graphs, simpler stack |
| **Contention capture** | Contention-only | Reduces volume 100-10,000x |
| **Holder thread ID** | Use existing `LockedByThreadId` | Already in `AccessControl` atomic state |
| **Stack frame depth** | 15 frames (configurable) | Includes test code context |
| **Trace correlation** | Natural parent-child via `Activity.Current` | Zero extra plumbing |

---

## Implementation Phases

| Phase | Focus | Documents |
|-------|-------|-----------|
| **1** | Stack Setup | 01-monitoring-stack-setup.md §7 Phase 1-2 |
| **2** | Basic Tracing | 02-span-instrumentation.md §7 Phase 1-2 |
| **3** | Demo Project | Stress test with writer-contention scenario |
| **4** | Deep Diagnostics | 03-deep-diagnostics.md §9 |

---

## Related Documentation

| Document | Relationship |
|----------|--------------|
| [overview/09-observability.md](../../overview/09-observability.md) | Observability architecture (Track 1-4) |
| [ops/monitoring-guide.md](../../ops/monitoring-guide.md) | Setup guide (to be updated) |
| [reference/resources/](../../reference/resources/) | Resource metrics design |
| [adr/019-runtime-telemetry-toggle.md](../../adr/019-runtime-telemetry-toggle.md) | Static readonly JIT optimization |

---

## Artifacts

| Artifact | Location | Purpose |
|----------|----------|---------|
| PLJG compose stack | `claude/ops/stack/pljg/` | Prometheus + Jaeger + Grafana containers |
| SigNoz compose stack | `claude/ops/stack/signoz/` | SigNoz + ClickHouse containers |
| Stack selector | `claude/ops/stack/select-stack.ps1` | Interactive stack launcher |
| Demo project | `test/Typhon.MonitoringDemo/` | Stress test with OTel wiring |
| `TyphonActivitySource` | `src/Typhon.Engine/Observability/` | ActivitySource for tracing |
| `TyphonSpanAttributes` | `src/Typhon.Engine/Observability/` | Span attribute name constants |
| `TraceIdEnricher` | `src/Typhon.Engine/Observability/` | Serilog enricher for log-trace correlation |
| `TelemetryConfig` | `src/Typhon.Engine/Observability/` | Static readonly gating for JIT dead-code elimination |
| `ContentionRingBuffer` | `src/Typhon.Engine/Observability/` | Deep diagnostics storage (not yet implemented) |

---

*Last updated: February 2026*

# Resource System Design

> Comprehensive documentation for Typhon's resource management, metrics, and observability subsystems.

**Date:** January 2026
**Status:** Design complete, implementation in progress

---

## Quick Start

> 💡 **Want to understand the resource tree?** Start with [01-registry.md](01-registry.md).
>
> **Need to add metrics to a component?** See [03-metric-source.md](03-metric-source.md) and [04-metric-kinds.md](04-metric-kinds.md).
>
> **Debugging resource exhaustion?** Jump to [07-budgets-exhaustion.md](07-budgets-exhaustion.md).

---

## Document Series

| # | Document | Purpose | Status |
|---|----------|---------|--------|
| 01 | [Registry & Tree Structure](01-registry.md) | `IResource` interface, tree topology, lifecycle | ✅ Ready |
| 02 | [Registry Examples](02-registry-examples.md) | Usage patterns, code samples, DI setup | ✅ Ready |
| 03 | [Metric Source](03-metric-source.md) | `IMetricSource`, `IMetricWriter` interfaces | 📐 Designed |
| 04 | [Metric Kinds](04-metric-kinds.md) | 6 metric types: Memory, Capacity, DiskIO, Contention, Throughput, Duration | 📐 Designed |
| 05 | [Granularity Strategy](05-granularity-strategy.md) | What gets a node, cost model, aggregation | 📐 Designed |
| 06 | [Snapshot API](06-snapshot-api.md) | `IResourceGraph`, `ResourceSnapshot`, queries | 📐 Designed |
| 07 | [Budgets & Exhaustion](07-budgets-exhaustion.md) | `ResourceOptions`, policies, back-pressure | 📐 Designed |
| 08 | [Observability Bridge](08-observability-bridge.md) | OTel mapping, health checks, alerts | 📐 Designed |

---

## Prerequisites

Before reading this series, familiarize yourself with:

| Document | What It Covers |
|----------|----------------|
| [overview/08-resources.md](../../overview/08-resources.md) | High-level resource graph architecture, metric kind definitions |
| [overview/09-observability.md](../../overview/09-observability.md) | Telemetry tracks, OTel integration, health checks |

---

## Document Dependencies

```
README.md ◄─── Entry point (you are here)
    │
    ├── 01-registry.md ─────────────────── Tree structure, IResource
    │       │
    │       └── 02-registry-examples.md ── Usage patterns
    │
    ├── 03-metric-source.md ────────────── Interfaces for reporting metrics
    │       │
    │       ▼
    ├── 04-metric-kinds.md ─────────────── The 6 measurement types
    │       │
    │       ▼
    ├── 05-granularity-strategy.md ─────── What gets a node
    │       │
    │       ▼
    ├── 06-snapshot-api.md ─────────────── Pulling metrics from the tree
    │       │
    │       ▼
    ├── 07-budgets-exhaustion.md ───────── Limits and back-pressure
    │       │
    │       ▼
    └── 08-observability-bridge.md ─────── Export to OTel, health, alerts
```

**Reading order:**
- Sequential (01 → 08) for full understanding
- Jump to 07 if debugging exhaustion issues
- Jump to 08 if integrating with monitoring

---

## Reading Guide by Goal

| If You Want To... | Read These Documents |
|-------------------|---------------------|
| **Understand the resource tree** | 01-registry.md, 02-registry-examples.md |
| **Add metrics to a component** | 03-metric-source.md, 04-metric-kinds.md |
| **Decide what needs tracking** | 05-granularity-strategy.md |
| **Query resource state at runtime** | 06-snapshot-api.md |
| **Configure memory budgets** | 07-budgets-exhaustion.md |
| **Debug exhaustion/back-pressure** | 07-budgets-exhaustion.md |
| **Set up Grafana dashboards** | 08-observability-bridge.md |
| **Configure health checks** | 08-observability-bridge.md |

---

## Implementation Status

| Component | Code Location | Status |
|-----------|---------------|--------|
| `IResource` | `src/Typhon.Engine/Resources/IResource.cs` | ✅ Implemented |
| `ResourceNode` | `src/Typhon.Engine/Resources/ResourceNode.cs` | ✅ Implemented |
| `ResourceRegistry` | `src/Typhon.Engine/Resources/ResourceRegistry.cs` | ✅ Implemented |
| `IMemoryResource` | `src/Typhon.Engine/Memory/MemoryBlockBase.cs` | ✅ Implemented |
| `MemoryAllocator` | `src/Typhon.Engine/Memory/MemoryAllocator.cs` | ✅ Implemented |
| `IContentionTarget` | `src/Typhon.Engine/Observability/IContentionTarget.cs` | ✅ Implemented |
| `IMetricSource` | `src/Typhon.Engine/Resources/IMetricSource.cs` | 🆕 Planned |
| `IResourceGraph` | `src/Typhon.Engine/Resources/IResourceGraph.cs` | 🆕 Planned |
| `ResourceSnapshot` | `src/Typhon.Engine/Resources/ResourceSnapshot.cs` | 🆕 Planned |
| `ResourceOptions` | `src/Typhon.Engine/Resources/ResourceOptions.cs` | 🆕 Planned |

---

## What's NOT Covered Here

| Topic | Where to Find It |
|-------|------------------|
| Telemetry configuration (`TelemetryConfig`) | [overview/09-observability.md](../../overview/09-observability.md) §9.1 |
| Lock-centric deep diagnostics | [overview/09-observability.md](../../overview/09-observability.md) §9.1.3 |
| OpenTelemetry metrics catalog | [overview/09-observability.md](../../overview/09-observability.md) §9.2 |
| Distributed tracing spans | [overview/09-observability.md](../../overview/09-observability.md) §9.3 |
| Serilog structured logging | [overview/09-observability.md](../../overview/09-observability.md) §9.4 |

---

## Key Concepts at a Glance

### The Resource Graph

A **runtime tree** of every significant resource in the engine. Each node:
- Has a parent (except Root)
- May have children
- May implement `IMetricSource` to report measurements

### Metric Kinds

Six measurement types that resource nodes can declare:

| Kind | What It Measures | Example |
|------|------------------|---------|
| **Memory** | Byte allocations | PageCache buffer size |
| **Capacity** | Slot utilization | Transaction pool fill level |
| **DiskIO** | Read/write operations | Pages loaded from disk |
| **Contention** | Lock wait events | Latch contention in PageCache |
| **Throughput** | Operation counts | Cache hits per second |
| **Duration** | Time cost | Checkpoint duration |

### Snapshots

A **consistent-enough** reading of all metrics at a point in time. The graph doesn't push metrics continuously — consumers pull snapshots on demand.

### Exhaustion Policies

What happens when a resource reaches its limit:

| Policy | Behavior |
|--------|----------|
| **FailFast** | Throw immediately |
| **Wait** | Block caller (respects Deadline) |
| **Evict** | Remove least-used entry |
| **Degrade** | Continue with reduced performance |

---

*Last updated: January 2026*

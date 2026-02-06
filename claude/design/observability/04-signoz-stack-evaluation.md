# SigNoz Stack Evaluation

> **Status:** Evaluation
> **Author:** Claude
> **Date:** 2026-02-05
> **Related:** [01-monitoring-stack-setup.md](./01-monitoring-stack-setup.md)

## Executive Summary

This document evaluates [SigNoz](https://signoz.io/) as a potential replacement for the current PLJG (Prometheus, Loki, Jaeger, Grafana) observability stack. SigNoz is an open-source, OpenTelemetry-native observability platform that combines logs, metrics, and traces in a single application.

**Recommendation:** SigNoz is a strong candidate for replacing the current stack. It offers unified observability (logs, metrics, traces) in a single UI, is 100% free for self-hosted use, and runs in containers via Podman just like the current stack.

---

## Current Stack Overview

The current observability stack (`claude/ops/stack/pljg/`) consists of:

| Component | Purpose | Port |
|-----------|---------|------|
| OTel Collector | OTLP receiver, routes telemetry | 4317, 4318, 8889 |
| Jaeger | Distributed tracing | 16686 |
| Prometheus | Metrics storage & querying | 9090 |
| Grafana | Visualization & dashboards | 3000 |

**Architecture:**
```
.NET App (OTLP) → OTel Collector → Jaeger (traces)
                                 → Prometheus (metrics) → Grafana
```

**Pros of current stack:**
- Windows/Podman compatible
- Each component is battle-tested and widely used
- Flexible - can swap individual components
- Grafana has extensive plugin ecosystem

**Cons of current stack:**
- 4 separate containers to manage
- Multiple UIs (Grafana, Jaeger, Prometheus)
- Configuration spread across multiple files
- No native log aggregation (Loki not yet integrated)

---

## SigNoz Overview

SigNoz is an open-source observability platform built natively on OpenTelemetry.

### Key Features

| Feature | Description |
|---------|-------------|
| **Unified Platform** | Logs, metrics, and traces in one UI |
| **OpenTelemetry Native** | Built on OTel from the ground up |
| **ClickHouse Backend** | Columnar database optimized for analytics |
| **APM Features** | Service maps, error tracking, latency analysis |
| **Alerting** | Built-in alerting on metrics, traces, and logs |
| **Dashboard Builder** | Create custom dashboards (similar to Grafana) |

### Architecture

```
.NET App (OTLP) → SigNoz OTel Collector → ClickHouse
                                        → Query Service → SigNoz UI

Components:
- signoz-otel-collector (OTLP receiver)
- clickhouse (data storage)
- zookeeper (ClickHouse coordination)
- query-service (API backend)
- frontend (React UI)
```

---

## Licensing Analysis

### Is SigNoz 100% Free for Self-Hosted Use?

**Yes, with minor limitations.**

| Edition | License | Cost | Limitations |
|---------|---------|------|-------------|
| **Community (Self-Hosted)** | MIT (core) + Enterprise License (ee/) | **Free** | Limited dashboard templates, no SSO/SAML |
| **Teams (Cloud)** | Proprietary | $49/month base | Full features, managed service |
| **Enterprise** | Proprietary | Custom | SSO, SAML, dedicated support |

**License Details:**
- Core code: MIT Expat License
- Enterprise features (`ee/` directory): Separate enterprise license
- No usage limits, no data caps, no time restrictions

**What's NOT in Community Edition:**
- SSO & SAML authentication
- Some premium dashboard templates
- Professional support

**What IS in Community Edition:**
- Full logs, metrics, traces collection
- Alerting
- Custom dashboards (unlimited)
- Service maps
- APM features
- All OpenTelemetry integrations

**Sources:**
- [SigNoz Pricing](https://signoz.io/pricing/)
- [GitHub License](https://github.com/SigNoz/signoz/blob/main/LICENSE)

---

## Gap Analysis: Current Stack vs SigNoz

### Feature Comparison

| Feature | Current Stack | SigNoz | Winner |
|---------|---------------|--------|--------|
| **Traces** | Jaeger (excellent) | Built-in (excellent) | Tie |
| **Metrics** | Prometheus (excellent) | ClickHouse-based | Prometheus (more mature) |
| **Logs** | Not integrated | Built-in | SigNoz |
| **Unified UI** | No (3 UIs) | Yes (1 UI) | SigNoz |
| **Service Maps** | Jaeger (basic) | Built-in (good) | SigNoz |
| **Alerting** | Grafana | Built-in | Tie |
| **Dashboard Ecosystem** | Grafana (extensive) | Growing | Grafana |
| **PromQL Support** | Native | Via adapter | Prometheus |
| **Resource Usage** | Lower (~1GB) | Higher (~4GB ClickHouse) | Current |
| **Container Support** | Podman ✅ | Podman ✅ | Tie |
| **Setup Complexity** | 4 containers | 5+ containers | Tie |
| **OpenTelemetry Native** | Via Collector | Native | SigNoz |

### Detailed Gap Analysis

#### Advantages of SigNoz

1. **Unified Observability**
   - Single UI for logs, metrics, and traces
   - Correlation between signals is seamless
   - No context-switching between tools

2. **OpenTelemetry Native**
   - Built from ground up for OTel
   - No translation layers or adapters
   - Future-proof as OTel becomes standard

3. **Integrated Logs**
   - Current stack lacks log aggregation
   - SigNoz includes logs out of the box
   - Log-to-trace correlation built-in

4. **APM Features**
   - Service dependency maps
   - Error rate tracking
   - P99 latency dashboards
   - Database call analysis

5. **Simpler Mental Model**
   - One tool to learn instead of four
   - Single documentation source
   - Unified query language

#### Disadvantages of SigNoz

1. **Resource Requirements**
   - ClickHouse is memory-hungry (minimum 4GB recommended)
   - Zookeeper adds overhead
   - Current stack is lighter weight (~1GB total)

2. **Maturity**
   - Prometheus: 12+ years, industry standard
   - Grafana: Massive plugin ecosystem
   - SigNoz: Younger project (started 2021, but actively developed)

3. **PromQL Compatibility**
   - Existing PromQL queries need migration
   - SigNoz uses ClickHouse SQL for queries
   - Some advanced PromQL features may not translate directly

4. **Dashboard Migration**
   - Current Grafana dashboards won't transfer automatically
   - Must rebuild dashboards in SigNoz
   - Fewer community dashboard templates (but growing)

---

## Container Compatibility

Both stacks run as Linux containers, making them equally compatible with Windows via Podman.

### Current Stack
```powershell
cd claude/ops/stack
podman compose up -d
```

### SigNoz
```powershell
# Clone SigNoz and run with Podman
git clone https://github.com/SigNoz/signoz.git
cd signoz/deploy/docker/clickhouse-setup
podman compose up -d
```

**Note:** SigNoz's documentation mentions "Windows not officially supported" - this refers to running SigNoz *natively* on Windows (without containers), not running it in containers. Since we use Podman to run Linux containers, this limitation does not apply.

Both stacks:
- ✅ Run as Linux containers
- ✅ Work with Podman on Windows
- ✅ Use standard OTLP ports (4317, 4318)

---

## Resource Comparison

### Current Stack (Approximate)

| Container | Memory | CPU |
|-----------|--------|-----|
| OTel Collector | 100MB | 0.1 |
| Jaeger | 200MB | 0.2 |
| Prometheus | 500MB | 0.2 |
| Grafana | 200MB | 0.1 |
| **Total** | **~1GB** | **~0.6** |

### SigNoz (Approximate)

| Container | Memory | CPU |
|-----------|--------|-----|
| OTel Collector | 100MB | 0.1 |
| ClickHouse | 2-4GB | 0.5 |
| Zookeeper | 500MB | 0.1 |
| Query Service | 500MB | 0.2 |
| Frontend | 100MB | 0.1 |
| **Total** | **~4-5GB** | **~1.0** |

**Note:** ClickHouse is optimized for analytics and requires significant memory for good performance.

---

## Migration Path

If proceeding with SigNoz, here's the migration approach:

### Phase 1: Parallel Deployment
1. Keep current stack running
2. Deploy SigNoz alongside (different ports)
3. Configure MonitoringDemo to send to both
4. Validate data appears correctly in SigNoz

### Phase 2: Dashboard Recreation
1. Recreate Typhon Overview dashboard in SigNoz
2. Build service maps for Typhon components
3. Set up alerting rules
4. Test all visualization features

### Phase 3: Cutover
1. Update MonitoringDemo to use SigNoz only
2. Update documentation
3. Remove old stack
4. Archive Grafana dashboard JSON for reference

### Configuration Changes

**Current OTLP endpoint:**
```csharp
options.Endpoint = new Uri("http://localhost:4317");
```

**SigNoz OTLP endpoint:**
```csharp
options.Endpoint = new Uri("http://localhost:4317"); // Same!
```

SigNoz uses the same OTLP ports, so application code requires **no changes**.

---

## Recommendation

### For Typhon Development: **Consider Migrating to SigNoz**

**Rationale:**

1. **Unified Observability**
   - Single UI for logs, metrics, and traces
   - Built-in correlation between signals
   - Eliminates context-switching between Grafana, Jaeger, Prometheus

2. **OpenTelemetry Native**
   - Built from ground up for OTel
   - Better long-term alignment with industry direction
   - Same OTLP endpoint (4317) - no app code changes needed

3. **Includes Logs**
   - Current stack lacks log aggregation
   - SigNoz includes logs out of the box
   - Log-to-trace correlation built-in

4. **100% Free for Self-Hosted**
   - No usage limits or data caps
   - Full feature set (except SSO/SAML)
   - MIT licensed core

### Trade-offs to Accept:

- [ ] Higher memory usage (~4GB vs ~1GB)
- [ ] Must rebuild Grafana dashboards in SigNoz
- [ ] Younger project (but actively developed)
- [ ] PromQL queries need migration to ClickHouse SQL

### Alternative: Keep Current Stack + Add Loki

If resource usage is a concern, keep the current stack and add Loki for logs:

```
.NET App → OTel Collector → Jaeger (traces)
                          → Prometheus (metrics)
                          → Loki (logs)
                                    ↓
                               Grafana (unified visualization)
```

This provides full observability with lower resource overhead, but requires managing 5 separate components.

---

## Decision Matrix

| Criterion | Weight | Current Stack | SigNoz |
|-----------|--------|---------------|--------|
| Unified UI | 25% | 5 | 10 |
| Feature completeness | 25% | 7 | 9 |
| Resource efficiency | 20% | 9 | 5 |
| Maturity/stability | 15% | 10 | 7 |
| Setup simplicity | 15% | 7 | 7 |
| **Weighted Score** | 100% | **7.25** | **7.80** |

SigNoz scores slightly higher due to unified UI and better feature completeness (built-in logs). Current stack wins on resource efficiency and maturity.

---

## Conclusion

SigNoz is a compelling platform that offers genuine advantages in unified observability and OpenTelemetry-native design. The Community Edition is **100% free** for self-hosted use with no significant limitations for development purposes.

Both stacks run as Linux containers via Podman, so there are no compatibility concerns. The main trade-offs are:

| Go with SigNoz if... | Stay with Current Stack if... |
|----------------------|-------------------------------|
| Unified UI is important | Memory is constrained (<4GB) |
| You want built-in logs | You prefer mature, battle-tested tools |
| OpenTelemetry-native matters | Existing Grafana dashboards are valuable |
| Simpler mental model appeals | PromQL expertise exists |

**Recommended Action:**
1. **Try SigNoz:** Deploy alongside current stack to evaluate hands-on
2. **Compare UX:** Run MonitoringDemo and compare debugging workflows
3. **Decide based on experience:** After 1-2 weeks of parallel usage

> **Note:** The SigNoz stack is now implemented alongside PLJG at `claude/ops/stack/signoz/`.
> Both stacks accept OTLP on port 4317 — use `claude/ops/stack/select-stack.ps1` to switch between them.

---

## References

- [SigNoz Official Site](https://signoz.io/)
- [SigNoz GitHub](https://github.com/SigNoz/signoz)
- [SigNoz Pricing](https://signoz.io/pricing/)
- [SigNoz Docker Installation](https://signoz.io/docs/install/docker/)
- [SigNoz vs Datadog Alternatives](https://signoz.io/comparisons/open-source-datadog-alternatives/)
- [Install SigNoz on Windows](https://www.restack.io/docs/signoz-knowledge-install-signoz-windows)

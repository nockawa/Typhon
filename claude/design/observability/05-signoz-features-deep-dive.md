# SigNoz Features Deep Dive

> **Status:** Reference
> **Author:** Claude
> **Date:** 2026-02-05
> **Related:** [04-signoz-stack-evaluation.md](./04-signoz-stack-evaluation.md)

## Purpose

This document provides an in-depth reference of all SigNoz features, capabilities, and UI components. Use this to understand what's available for building custom dashboards and views for Typhon observability.

---

## Table of Contents

1. [OpenTelemetry Integration](#1-opentelemetry-integration)
2. [Dashboard System](#2-dashboard-system)
3. [Panel Types & Visualizations](#3-panel-types--visualizations)
4. [Query Builder](#4-query-builder)
5. [APM Features](#5-apm-features)
6. [Service Dependency Map](#6-service-dependency-map)
7. [Trace Explorer](#7-trace-explorer)
8. [Logs Explorer](#8-logs-explorer)
9. [Metrics Explorer](#9-metrics-explorer)
10. [Alerting System](#10-alerting-system)
11. [Infrastructure Monitoring](#11-infrastructure-monitoring)
12. [LLM Observability](#12-llm-observability)
13. [Dashboard Templates](#13-dashboard-templates)

---

## 1. OpenTelemetry Integration

![OpenTelemetry Overview](https://signoz.io/img/docs/services-overview.webp)
*SigNoz receives traces, metrics, and logs via OpenTelemetry OTLP protocol*

SigNoz is **built natively on OpenTelemetry** (OTel), making it the ideal backend for OTel-instrumented applications like Typhon.

### Protocol Support

| Protocol | Port | Description |
|----------|------|-------------|
| OTLP/gRPC | 4317 | Primary ingestion (recommended) |
| OTLP/HTTP | 4318 | Alternative HTTP-based ingestion |

### Three Pillars Support

| Signal | Description | Correlation |
|--------|-------------|-------------|
| **Traces** | Distributed request flows with span hierarchy | TraceID links to logs |
| **Metrics** | Aggregated measurements over time | Resource attributes match |
| **Logs** | Structured log events | TraceID/SpanID correlation |

### Automatic Correlation

SigNoz automatically correlates signals using:
- **TraceID**: Links logs and traces from the same request
- **SpanID**: Associates logs with specific spans
- **Resource Attributes**: Matches metrics to services (e.g., `service.name`)

**Example Correlation Flow:**
```
Alert triggers → Click metric → See related traces → View correlated logs
```

### .NET Integration

```csharp
// Same configuration as current stack - no changes needed!
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Typhon.Resources")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));
```

**Sources:**
- [SigNoz OpenTelemetry](https://signoz.io/opentelemetry/)
- [What is OTLP](https://signoz.io/blog/what-is-otlp/)

---

## 2. Dashboard System

![Dashboard Overview](https://signoz.io/img/unified-observability/unified-observability-infrastructure-monitoring.webp)
*SigNoz dashboard with multiple panels, sections, and time range controls*

### Dashboard Structure

```
Dashboard
├── Sections (collapsible groups)
│   ├── Panel 1 (visualization)
│   ├── Panel 2
│   └── Panel 3
├── Variables (dynamic filters)
└── Time Range Selector
```

### Dashboard Features

| Feature | Description |
|---------|-------------|
| **Sections** | Collapsible panel groups for organization |
| **Drag & Drop** | Reorder panels by dragging |
| **Full Screen** | Expand dashboard for presentations |
| **Lock Mode** | Prevent accidental modifications |
| **Auto Refresh** | Configurable refresh intervals |
| **Time Range** | 5 min to 1 week, plus custom ranges |
| **JSON Export/Import** | Share dashboards as JSON files |
| **Terraform Provider** | Infrastructure-as-code dashboard management |

### Dashboard Variables

![Dashboard Variables](https://signoz.io/img/docs/dashboards/variables/dynamic-variable.png)
*Dashboard variable configuration for dynamic filtering*

Four variable types enable dynamic, interactive dashboards:

#### 1. Dynamic Variables (Recommended)
Automatically fetch values from OTel attributes without queries.

```
Type: Dynamic
Attribute: service.name
Options: Related values | All values
```

**Features:**
- Auto-complete from actual data
- "Related values" filtered by other variable selections
- "All values" for complete unfiltered list
- No SQL knowledge required

#### 2. Query Variables
ClickHouse SQL queries for custom value lists.

```sql
SELECT DISTINCT JSONExtractString(labels, 'host.name') AS host_name
FROM signoz_metrics.distributed_time_series_v4_1day
WHERE metric_name = 'system.cpu.time'
```

#### 3. Custom Variables
Comma-separated static values.

```
Values: production, staging, development
```

#### 4. Textbox Variables
Free-form text input with optional default.

```
Default: trace-id-here
```

### Using Variables in Queries

| Context | Syntax | Example |
|---------|--------|---------|
| Query Builder (single) | `$var` | `service.name = $service` |
| Query Builder (multi) | `IN $var` | `k8s.pod.name IN $pods` |
| ClickHouse | `$var` | `WHERE host = $hostname` |
| PromQL | `$var` | `rate(http_total{job="$job"}[5m])` |

### Variable Chaining

Create dependent dropdowns where selecting a service filters available operations:

```
Variable 1: $service (Dynamic: service.name)
Variable 2: $operation (Query filtered by $service)
```

**Sources:**
- [Manage Dashboards](https://signoz.io/docs/userguide/manage-dashboards/)
- [Manage Variables](https://signoz.io/docs/userguide/manage-variables/)
- [Terraform Provider](https://signoz.io/docs/dashboards/terraform-provider-signoz/)

---

## 3. Panel Types & Visualizations

SigNoz provides **7 panel types** for dashboard visualization:

### 3.1 Time Series

**Best for:** Metrics over time, trends, patterns

![Time Series Panel](https://signoz.io/img/docs/dashboards/panel-types/line-chart.png)
*Example time series chart showing metrics over time*

```
┌────────────────────────────────────────┐
│  100 ─┤    ╭─────╮                     │
│   80 ─┤   ╭╯     ╰╮    ╭──╮           │
│   60 ─┤  ╭╯       ╰────╯  ╰╮          │
│   40 ─┤──╯                  ╰──────   │
│    0 ─┼──────────────────────────────  │
│       00:00    06:00    12:00   18:00  │
└────────────────────────────────────────┘
```

**Configuration Options:**

| Option | Description |
|--------|-------------|
| **Fill Gaps** | Replace missing data with zeros |
| **Y-axis Unit** | None, bytes, percent, ops/sec, µs, etc. |
| **Soft Min/Max** | Prevent small values from being magnified |
| **Thresholds** | Horizontal lines at specific values with colors |

**Data Sources:** Logs, Traces, Metrics

### 3.2 Bar Chart

**Best for:** Categorical comparisons, distributions

![Bar Chart Panel](https://signoz.io/img/docs/dashboards/panel-types/bar-chart.png)
*Bar chart showing categorical data comparison*

```
┌────────────────────────────────────────┐
│                                        │
│  Service A  ████████████████  150      │
│  Service B  ██████████        100      │
│  Service C  ██████             60      │
│  Service D  ████               40      │
│                                        │
└────────────────────────────────────────┘
```

**Data Sources:** Logs, Traces, Metrics

### 3.3 Histogram

**Best for:** Distribution analysis, latency buckets

![Histogram Panel](https://signoz.io/img/docs/dashboards/panel-types/histogram.png)
*Histogram showing distribution of values across buckets*

```
┌────────────────────────────────────────┐
│      │                                 │
│  500 │        ████                     │
│  400 │      ████████                   │
│  200 │    ████████████                 │
│  100 │  ████████████████               │
│      └──0-10──10-50──50-100──100+──    │
│              Latency (ms)              │
└────────────────────────────────────────┘
```

**Use case:** Visualize P50/P90/P99 latency distributions

### 3.4 Pie Chart

**Best for:** Composition, proportions, market share

![Pie Chart Panel](https://signoz.io/img/docs/dashboards/panel-types/pie-chart.png)
*Pie chart showing proportional breakdown*

```
┌────────────────────────────────────────┐
│                                        │
│         ╭───────────╮                  │
│       ╭─┤  HTTP 45% ├─╮                │
│      │  ╰───────────╯  │               │
│      │   gRPC 30%      │               │
│       ╰──── DB 25% ───╯                │
│                                        │
└────────────────────────────────────────┘
```

**Data Sources:** Logs, Traces, Metrics

### 3.5 Table

**Best for:** Detailed data, sortable lists, top-N views

![Table Panel](https://signoz.io/img/docs/dashboards/panel-types/table.png)
*Table panel with sortable columns and clickable rows*

```
┌────────────────────────────────────────┐
│ Service    │ P99 (ms) │ Errors │ RPS  │
├────────────┼──────────┼────────┼──────┤
│ api-gw     │    45    │  0.1%  │ 1200 │
│ user-svc   │    23    │  0.0%  │  800 │
│ order-svc  │   120    │  2.1%  │  450 │
└────────────────────────────────────────┘
```

**Features:**
- Sortable columns
- Clickable rows for drill-down
- Filter directly from cell values

### 3.6 List Chart

**Best for:** Simple value lists, top items, recent events

```
┌────────────────────────────────────────┐
│ Top Error Messages                     │
├────────────────────────────────────────┤
│ • Connection timeout (redis)     (142) │
│ • Invalid token                   (87) │
│ • Rate limit exceeded             (45) │
│ • Database unavailable            (12) │
└────────────────────────────────────────┘
```

### 3.7 Value (Single Stat)

**Best for:** KPIs, current values, gauges

![Value Panel](https://signoz.io/img/docs/dashboards/panel-types/value.png)
*Single stat value panel showing key metrics*

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│   P99 Latency│  │  Error Rate  │  │     RPS      │
│              │  │              │  │              │
│    45 ms     │  │    0.12%     │  │    12.4K     │
│      ▲ 5%    │  │      ▼ 2%    │  │      ▲ 15%   │
└──────────────┘  └──────────────┘  └──────────────┘
```

**Features:**
- Color thresholds (green/yellow/red)
- Sparkline mini-graph
- Trend indicator

**Sources:**
- [Panel Types Overview](https://signoz.io/docs/dashboards/panel-types/)
- [Time Series Panel](https://signoz.io/docs/dashboards/panel-types/timeseries/)

---

## 4. Query Builder

The Query Builder provides a visual interface for constructing queries without writing raw SQL.

![Query Builder Filter](https://signoz.io/img/docs/product-features/query-builder/query-builder-filtering.gif)
*Query Builder with filtering capabilities*

### Supported Data Sources

| Source | Description |
|--------|-------------|
| **Metrics** | OTel metrics with aggregation |
| **Traces** | Span data with filtering |
| **Logs** | Log entries with search |

### Aggregation Functions

#### Basic Aggregations

| Function | Description | Example |
|----------|-------------|---------|
| `Count` | Number of events | Total requests |
| `Count Distinct` | Unique values | Unique users |
| `Sum` | Total of values | Total bytes |
| `Avg` | Mean value | Average latency |
| `Min` | Minimum value | Fastest response |
| `Max` | Maximum value | Slowest response |

#### Percentiles

| Function | Description |
|----------|-------------|
| `P05` | 5th percentile |
| `P10` | 10th percentile |
| `P50` | Median (50th percentile) |
| `P90` | 90th percentile |
| `P95` | 95th percentile |
| `P99` | 99th percentile |

#### Rate Functions

| Function | Description |
|----------|-------------|
| `Rate` | Events per second |
| `Rate Sum` | Rate of sum changes |
| `Rate Avg` | Rate of average changes |
| `Rate Min/Max` | Rate of extreme changes |

### Filtering Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `=` | Equals | `service.name = "api"` |
| `!=` | Not equals | `status != "OK"` |
| `IN` | In list | `env IN ["prod", "stage"]` |
| `NOT_IN` | Not in list | `host NOT_IN ["test1"]` |
| `CONTAINS` | String contains | `message CONTAINS "error"` |
| `REGEX` | Regex match | `path REGEX "/api/v[0-9]+"` |
| `>`, `<`, `>=`, `<=` | Numeric comparisons | `duration > 1000` |

### Mathematical Functions (20 total)

| Category | Functions |
|----------|-----------|
| **Exponential** | `exp`, `exp2`, `exp10` |
| **Logarithmic** | `log`, `ln`, `log2`, `log10` |
| **Roots** | `sqrt`, `cbrt` |
| **Trigonometric** | `sin`, `cos`, `tan`, `asin`, `acos`, `atan` |
| **Special** | `erf`, `erfc`, `lgamma`, `tgamma` |
| **Conversion** | `degrees`, `radians` |
| **Time** | `now` |

### Metrics-Specific Functions

| Category | Functions | Description |
|----------|-----------|-------------|
| **Exclusion** | `Cut Off Min`, `Cut Off Max` | Exclude values outside range |
| **Arithmetic** | `Absolute`, `Log`, `Log10` | Transform values |
| **Smoothing** | `EWMA 3/5/7` | Exponential moving averages |
| **Time Shift** | `Time Shift` | Compare to previous periods |

### Result Manipulation

![Query Builder Group By](https://signoz.io/img/docs/product-features/query-builder/group_by.gif)
*Grouping and aggregation in the Query Builder*

| Option | Description |
|--------|-------------|
| `Order By` | Sort results |
| `Aggregate Every` | Time bucket size |
| `Limit` | Max results returned |
| `Having` | Post-aggregation filter |
| `Legend Format` | Custom legend labels |

### Multiple Queries

Run multiple independent queries in one panel:

```
Query A: rate(http_requests_total{status="200"}[5m])
Query B: rate(http_requests_total{status="500"}[5m])
Formula: A / (A + B) * 100  → Success Rate %
```

**Sources:**
- [Query Builder](https://signoz.io/docs/userguide/query-builder/)
- [Create Custom Query](https://signoz.io/docs/userguide/create-a-custom-query/)
- [Metrics ClickHouse Query](https://signoz.io/docs/userguide/write-a-metrics-clickhouse-query/)

---

## 5. APM Features

### Services Overview

![Services Overview](https://signoz.io/img/docs/services-overview.webp)
*Services list showing RED metrics (Rate, Errors, Duration) for all instrumented applications*

The Services page displays all instrumented applications with RED metrics:

| Metric | Description |
|--------|-------------|
| **Rate** | Requests per second |
| **Errors** | Error percentage |
| **Duration** | P99 latency |
| **Apdex** | User satisfaction score (0-1) |

### Service Detail View

![Application Details](https://signoz.io/img/docs/application-details-page.webp)
*Service detail view with latency percentiles, throughput, and error rates*

#### Application Metrics Pane

Five core visualizations per service:

1. **Application Latency** - P99, P95, P50 over time
2. **Operations per Second** - Request throughput
3. **Error Percentage** - Failure rate trend
4. **Key Operations** - Sortable table of endpoints
5. **Apdex Score** - Performance satisfaction

#### Database Calls Monitoring

Tracks all database operations:

| Metric | Description |
|--------|-------------|
| DB calls/sec | Operation throughput |
| Avg duration | Mean query time |
| Slowest calls | Table of slow queries with `db.statement` |

**Required span attributes:**
- `db.system` (e.g., "postgresql", "redis")
- `span.kind != SERVER`

#### External Calls Monitoring

Tracks dependencies on external services:

| Metric | Description |
|--------|-------------|
| Error % | Failed external calls |
| Avg duration | Mean response time |
| Calls by address | Volume breakdown |
| Duration by address | Latency breakdown |

**Detects:**
- HTTP calls to external hosts
- gRPC service calls
- AWS Lambda invocations
- Any network call outside the process

### Key Operations Table

| Column | Description |
|--------|-------------|
| Operation | Endpoint/method name |
| P50/P90/P99 | Latency percentiles |
| Error Rate | Failure percentage |
| RPS | Requests per second |

**Features:**
- Click to drill into operation details
- Sort by any column
- Filter by time range

**Sources:**
- [View Services](https://signoz.io/docs/userguide/metrics/)
- [APM Metrics Guide](https://signoz.io/guides/apm-metrics/)

---

## 6. Service Dependency Map

The Service Map provides a **visual topology** of your distributed system.

![Service Map](https://signoz.io/img/docs/service-map.webp)
*Service dependency map showing service relationships, call volumes, and error rates*

### Visualization

```
                    ┌─────────┐
                    │ Gateway │ (500 RPS)
                    └────┬────┘
                         │
           ┌─────────────┼─────────────┐
           │             │             │
      ┌────▼────┐  ┌─────▼─────┐  ┌────▼────┐
      │ User    │  │  Order    │  │ Product │
      │ Service │  │  Service  │  │ Service │
      └────┬────┘  └─────┬─────┘  └────┬────┘
           │             │             │
      ┌────▼────┐  ┌─────▼─────┐  ┌────▼────┐
      │ Postgres│  │   Redis   │  │  Mongo  │
      └─────────┘  └───────────┘  └─────────┘
```

### Node Representation

| Visual Element | Meaning |
|----------------|---------|
| **Circle size** | Proportional to traffic volume |
| **Circle color** | Health status (green/yellow/red) |
| **Edge thickness** | Call volume between services |
| **Edge color** | Error rate (red = high errors) |

### Edge Metrics (Hover)

| Metric | Description |
|--------|-------------|
| **P99 Latency** | 99th percentile response time |
| **Error Rate** | Percentage of failed calls |
| **RPS** | Requests per second |

### How It's Generated

The map is automatically constructed from **trace data**:

1. Each span with `span.kind = CLIENT` represents an outgoing call
2. The `peer.service` or destination attributes identify the target
3. SigNoz aggregates all spans to build the topology

### Use Cases for Typhon

| Scenario | What You'd See |
|----------|----------------|
| **Transaction flow** | DatabaseEngine → ComponentTable → PageCache |
| **Index operations** | BTree → ChunkBasedSegment → ManagedPagedMMF |
| **Slow dependencies** | Red edges to slow storage operations |

**Sources:**
- [Service Map](https://signoz.io/docs/userguide/service-map/)
- [Application Dependency Mapping](https://signoz.io/guides/application-dependency-map/)

---

## 7. Trace Explorer

![Trace Filter](https://signoz.io/img/docs/product-features/trace-explorer/trace-explorer-quick-filters.webp)
*Trace filter panel for searching and filtering traces*

### Trace List View

Filter and search traces by:

| Filter | Description |
|--------|-------------|
| Service | Filter by service name |
| Operation | Filter by span name |
| Tags | Filter by span attributes |
| Duration | Min/max duration range |
| Status | OK, ERROR, UNSET |
| Time Range | Absolute or relative |

### Trace Detail Views

![Trace Details](https://signoz.io/img/docs/apm-and-distributed-tracing/tarce-details-overview.webp)
*Trace detail view with flame graph and waterfall timeline*

#### Flame Graph

Hierarchical visualization showing span nesting:

```
Transaction.Commit (12ms) ████████████████████████████████████████
├── Validate (1ms) ██
├── ComponentTable.Write (8ms) ████████████████████████████
│   ├── BTree.Insert (3ms) ██████████
│   │   └── PageCache.Request (1ms) ███
│   └── Revision.Create (4ms) ████████████
└── Flush (3ms) ██████████
```

**Features:**
- Click to zoom into span
- Color-coded by service
- Width proportional to duration

#### Waterfall / Gantt Chart

Timeline view showing sequence and overlap:

```
Time →  0ms    5ms    10ms   15ms
        │      │      │      │
Commit  ████████████████████████
  ├─Validate  ██
  ├─Write        ████████████████
  │  ├─Insert       ██████
  │  └─Create          ████████
  └─Flush                    ██████
```

**Features:**
- Synchronized with flame graph
- Scroll horizontally for long traces
- Shows parallel execution

### Span Details

![Span Percentiles](https://signoz.io/img/docs/apm-and-distributed-tracing/span-percentile.webp)
*Span detail view showing latency percentiles compared to similar spans*

Click any span to see:

| Section | Contents |
|---------|----------|
| **Context** | TraceID, SpanID, ParentSpanID |
| **Attributes** | All span tags/attributes |
| **Events** | Timestamped log-like entries |
| **Links** | References to related traces |
| **Percentiles** | P50/P90/P95/P99 compared to similar spans |

### Span Classification

| Type | Description |
|------|-------------|
| `CLIENT` | Outgoing call to another service |
| `SERVER` | Incoming request handler |
| `PRODUCER` | Message queue producer |
| `CONSUMER` | Message queue consumer |
| `INTERNAL` | Internal operation |

### Performance Features

- **1M+ span support**: Handles massive traces without UI degradation
- **Virtual rendering**: Only renders visible spans
- **Progressive loading**: Loads spans as you scroll
- **Error filtering**: `has_error = true` to find problems

**Sources:**
- [Trace Details](https://signoz.io/docs/userguide/span-details/)
- [View Traces](https://signoz.io/docs/userguide/traces/)
- [Flamegraphs Guide](https://signoz.io/blog/flamegraphs/)

---

## 8. Logs Explorer

![Logs Explorer Views](https://signoz.io/img/docs/product-features/logs-explorer/logs-explorer-views.gif)
*Logs Explorer with multiple view modes: Raw, Default, and Column*

### Log Views

| View | Description |
|------|-------------|
| **Raw** | Original log format |
| **Default** | Formatted with key attributes |
| **Column** | Tabular view with columns |

### Search Capabilities

![Logs Filter Search](https://signoz.io/img/docs/product-features/logs-explorer/logs-explorer-search.webp)
*Filter and search functionality in Logs Explorer*

| Feature | Description |
|---------|-------------|
| **Full-text search** | Search across all log content |
| **Attribute filters** | Filter by specific fields |
| **Regex support** | Go RE2 syntax patterns |
| **Auto-complete** | Suggests values from actual data |

### Filter Operators

| Operator | Example |
|----------|---------|
| `=` | `level = "error"` |
| `!=` | `service != "debug"` |
| `IN` | `host IN ["prod1", "prod2"]` |
| `CONTAINS` | `message CONTAINS "timeout"` |
| `REGEX` | `path REGEX "/api/v[0-9]+"` |
| `LIKE` | `message LIKE "%connection%"` |

### Live Tail

![Live Tail](https://signoz.io/img/docs/product-features/logs-explorer/logs-explorer-live-view.gif)
*Live tail streaming logs in real-time*

Real-time log streaming:
- **Pause/Resume**: Control the stream
- **Filter while streaming**: Apply filters to live data
- **Auto-scroll**: Keep up with new logs

### Log Attributes

| Feature | Description |
|---------|-------------|
| **Pin attributes** | Keep important fields at top |
| **Filter from value** | Click to add filter |
| **Exclude value** | Click to exclude |
| **Copy value** | Quick copy to clipboard |

### Log-to-Trace Correlation

Click the trace icon on any log entry to:
1. Jump to the associated trace
2. See the exact span that generated the log
3. View full request context

**Sources:**
- [Logs Explorer](https://signoz.io/docs/product-features/logs-explorer/)
- [Logs Query Builder](https://signoz.io/docs/userguide/logs_query_builder/)
- [Full-Text Search](https://signoz.io/docs/userguide/full-text-search/)

---

## 9. Metrics Explorer

![Metrics Explorer](https://signoz.io/img/docs/product-features/query-builder/temporal-spatial-aggregations.webp)
*Metrics explorer showing available metrics with temporal and spatial aggregations*

### Metrics Catalog

Auto-discover all metrics in your system:

| Column | Description |
|--------|-------------|
| **Metric Name** | Full metric identifier |
| **Type** | Counter, Gauge, Histogram, Summary |
| **Sample Count** | Number of data points |
| **Time Series** | Unique label combinations |
| **Sources** | Services sending this metric |

### Use Cases

| Scenario | How Metrics Explorer Helps |
|----------|---------------------------|
| **Onboarding** | Verify new integrations are sending data |
| **Troubleshooting** | Find alert-related metrics quickly |
| **Discovery** | Explore available metrics without dashboards |
| **Validation** | Confirm metric types and labels |

### Quick Actions

From any metric, you can:
- **View in graph**: Instant visualization
- **Add to dashboard**: Create panel from metric
- **Create alert**: Set up threshold alert
- **See related**: Find metrics with similar labels

**Sources:**
- [Metrics Explorer](https://signoz.io/docs/metrics-management/metrics-explorer/)
- [Metrics Explorer Blog](https://signoz.io/blog/metrics-explorer/)

---

## 10. Alerting System

![Alert Rules](https://signoz.io/img/docs/product-features/alerts/alerts-alert-rules-tab.gif)
*Alert rules management showing configured alerts with status and conditions*

### Alert Types

| Type | Trigger |
|------|---------|
| **Metrics-based** | Threshold or rate conditions on metrics |
| **Logs-based** | Log patterns, error codes, keywords |
| **Traces-based** | Latency, error rate, trace patterns |
| **Anomaly-based** | Deviation from historical patterns |

### Threshold Alerts

```yaml
Alert: High P99 Latency
Condition: p99(duration) > 500ms
For: 5 minutes
Severity: Warning
```

**Configuration:**

| Option | Description |
|--------|-------------|
| Evaluation window | 5min to 1 day |
| Trigger threshold | Value to exceed |
| Occurrence | At least once / Every time |
| Missing data | Treat as OK / Alerting |

### Anomaly Detection

Automatically detect unusual patterns without manual thresholds.

**Algorithm:** Statistical analysis with seasonality support

| Seasonality | Use Case |
|-------------|----------|
| **Hourly** | Metrics with hourly cycles |
| **Daily** | Business-hour patterns |
| **Weekly** | Weekend vs weekday differences |

**How It Works:**

```
Predicted = Avg(Past) + Avg(Current Season) - Mean(Past 3 Seasons)
Anomaly Score = |Actual - Predicted| / Std Dev
Alert if: Anomaly Score > Z-score threshold (default: 3)
```

**Bounds:**
```
Upper = MovingAvg(Predicted) + (Z-score × StdDev)
Lower = MovingAvg(Predicted) - (Z-score × StdDev)
```

### Notification Channels

| Channel | Description |
|---------|-------------|
| **Email** | Direct email notifications |
| **Slack** | Slack channel messages |
| **PagerDuty** | Incident management integration |
| **Webhook** | Custom HTTP endpoints |
| **OpsGenie** | Alert management platform |

### Alert Routing

Group and route alerts by:
- Deployment environment
- Customer/tenant
- Service name
- Any custom attribute

**Sources:**
- [Alerts Management](https://signoz.io/docs/userguide/alerts-management/)
- [Anomaly-Based Alerts](https://signoz.io/docs/alerts-management/anomaly-based-alerts/)
- [Alert Evaluation Patterns](https://signoz.io/docs/alerts-management/user-guides/understanding-alert-evaluation-patterns/)

---

## 11. Infrastructure Monitoring

![Hosts List](https://signoz.io/img/docs/infrastructure-monitoring/hosts-list.webp)
*Infrastructure monitoring showing host metrics and resource utilization*

### Host Metrics

Collected via OpenTelemetry hostmetrics receiver:

| Category | Metrics |
|----------|---------|
| **CPU** | Usage, idle, system, user, iowait |
| **Memory** | Used, free, cached, buffers |
| **Disk** | Read/write ops, bytes, latency |
| **Network** | Bytes in/out, packets, errors |
| **Filesystem** | Usage, inodes |
| **Load** | 1m, 5m, 15m averages |
| **Process** | Count, CPU, memory per process |

### Host List View

| Column | Description |
|--------|-------------|
| Hostname | Machine identifier |
| CPU % | Current utilization |
| Memory % | Usage percentage |
| Disk I/O | Read/write activity |
| Network | Traffic volume |

### Host Detail View

Three tabs per host:
1. **Metrics**: All system metrics with graphs
2. **Traces**: Traces from this host
3. **Logs**: Logs from this host

### Kubernetes Monitoring

Pre-built dashboards for:
- Node metrics (CPU, memory, disk across nodes)
- Pod metrics (per-pod resource usage)
- Container metrics (Docker/containerd)
- Cluster overview (aggregate health)

**Sources:**
- [Infrastructure Monitoring](https://signoz.io/docs/infrastructure-monitoring/overview/)
- [Host Metrics](https://signoz.io/docs/infrastructure-monitoring/hostmetrics/)
- [K8s Monitoring](https://signoz.io/docs/opentelemetry-collection-agents/k8s/k8s-infra/overview/)

---

## 12. LLM Observability

### Token Usage Tracking

| Metric | Description |
|--------|-------------|
| Input tokens | Tokens in prompts |
| Output tokens | Tokens in responses |
| Total tokens | Combined usage |
| Cost estimation | Based on model pricing |

### Model Performance

| Metric | Description |
|--------|-------------|
| P95 latency | 95th percentile response time |
| Error rate | Failed LLM calls |
| Throughput | Requests per second |
| Model distribution | Usage by model type |

### Pre-Built Dashboards

| Dashboard | Monitors |
|-----------|----------|
| OpenAI | GPT-3.5, GPT-4, embeddings |
| Vercel AI SDK | AI-powered applications |
| LangChain | Agent workflows |
| Temporal AI | Agentic AI systems |

**Sources:**
- [LLM Observability](https://signoz.io/docs/llm-observability/)
- [LangChain Observability](https://signoz.io/blog/langchain-observability-with-opentelemetry/)

---

## 13. Dashboard Templates

### Available Templates

| Category | Templates |
|----------|-----------|
| **APM** | Key Operations, DB Calls, External Services |
| **Infrastructure** | Host Metrics, Docker, Kubernetes |
| **Databases** | PostgreSQL, MySQL, MongoDB, Redis |
| **Message Queues** | Kafka, RabbitMQ |
| **AI/ML** | OpenAI, LangChain, Vercel AI |
| **CI/CD** | Build pipelines, deployment metrics |

### Template Variables

Common variables across templates:

| Variable | Type | Description |
|----------|------|-------------|
| `$service` | Dynamic | Filter by service name |
| `$environment` | Dynamic | prod/staging/dev |
| `$host` | Dynamic | Hostname filter |
| `$namespace` | Dynamic | K8s namespace |

### Importing Templates

```bash
# From SigNoz dashboard repo
git clone https://github.com/SigNoz/dashboards
# Import JSON via UI or API
```

**Sources:**
- [Dashboard Templates](https://signoz.io/docs/dashboards/dashboard-templates/overview/)
- [SigNoz Dashboards Repo](https://github.com/SigNoz/dashboards)

---

## 14. Interactive Features Summary

### Drill-Down Capabilities

| Feature | Description |
|---------|-------------|
| **View Logs** | Click metric → See related logs |
| **View Traces** | Click metric → See related traces |
| **Breakout By** | Regroup by additional dimension |
| **Context Links** | Navigate to external tools |
| **Cross Filtering** | Click to set dashboard variables |
| **Filter in Tables** | Click cell to add filter |

### Context Preservation

All drill-downs preserve:
- Time range
- Active filters
- Variable selections
- Grouping context

---

## Typhon-Specific Recommendations

### Recommended Dashboards

| Dashboard | Purpose |
|-----------|---------|
| **Typhon Overview** | RED metrics for DatabaseEngine |
| **Storage Deep Dive** | PageCache, ManagedPagedMMF metrics |
| **Transaction Analysis** | Commit duration, conflict rates |
| **Contention Monitor** | Lock wait times, timeout counts |

### Recommended Panel Types

| Data | Panel Type |
|------|------------|
| Cache hit rate | Value (single stat) |
| Latency over time | Time Series |
| Operation breakdown | Pie Chart |
| Top slow queries | Table |
| Error distribution | Histogram |

### Service Map for Typhon

```
DatabaseEngine
├── TransactionPool
├── ComponentTable (per type)
│   ├── PrimaryKeyIndex
│   ├── SecondaryIndexes
│   └── RevisionChain
└── ManagedPagedMMF
    └── PageCache
```

---

## References

### Official Documentation
- [SigNoz Docs](https://signoz.io/docs/)
- [Panel Types](https://signoz.io/docs/dashboards/panel-types/)
- [Query Builder](https://signoz.io/docs/userguide/query-builder/)
- [Service Map](https://signoz.io/docs/userguide/service-map/)
- [Alerts](https://signoz.io/docs/alerts/)

### GitHub Resources
- [SigNoz Repository](https://github.com/SigNoz/signoz)
- [Dashboard Templates](https://github.com/SigNoz/dashboards)

### Community
- [SigNoz Blog](https://signoz.io/blog/)
- [SigNoz Community](https://community-chat.signoz.io/)

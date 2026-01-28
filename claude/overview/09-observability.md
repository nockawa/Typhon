# Component 9: Observability

> Zero-overhead telemetry, structured metrics, and diagnostics for monitoring Typhon's internals.

---

## Overview

The Observability component provides four tracks of visibility into Typhon's operations, each with different performance/detail trade-offs:

| Track | Mechanism | Overhead | Use Case |
|-------|-----------|----------|----------|
| **Track 1: Hot-path telemetry** | `static readonly` fields, JIT-eliminated when false | Zero when disabled | Production monitoring |
| **Track 2: DI-injectable options** | `IOptions<TelemetryOptions>` pattern | Low (DI resolution) | Cold-path configuration |
| **Track 3: Deep diagnostics** | `static readonly bool` (same JIT trick as Track 1) | Zero when disabled | Lock post-mortem, development |
| **Track 4: Per-resource telemetry** | `IContentionTarget` callbacks, per-resource `TelemetryLevel` | Negligible (null check) | Targeted resource diagnostics |

<a href="../assets/typhon-observability-overview.svg">
  <img src="../assets/typhon-observability-overview.svg" width="1200"
       alt="Observability — Component overview showing resource graph source, telemetry tracks, consumers, and sinks">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-observability-overview.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

---

## Status: 🔧 In Progress

Track 1 (static readonly) and Track 3 (static readonly deep diagnostics) are implemented. Track 2 (DI options) has infrastructure but no consumers yet. **Track 4** (per-resource telemetry via `IContentionTarget`) is implemented in `AccessControl`. Metrics emission and sink integration are designed but not yet wired.

> **Migration in progress**: Track 3 is transitioning from `#if TELEMETRY` preprocessor directives to `static readonly bool` fields for simpler build configuration.

---

## Sub-Components

| # | Name | Purpose | Status |
|---|------|---------|--------|
| **9.1** | [Telemetry Architecture](#91-telemetry-architecture) | Zero-overhead multi-track system | ✅ Implemented |
| **9.1.1** | [Track 1: Static Readonly](#track-1-static-readonly-configuration-telemetryconfig) | JIT-eliminated hot-path guards | ✅ Implemented |
| **9.1.2** | [Track 2: DI Options](#track-2-di-injectable-options-telemetryoptions) | Cold-path configuration | ✅ Implemented |
| **9.1.3** | [Track 3: Deep Diagnostics](#track-3-deep-diagnostics-static-readonly-bool) | Lock-centric operation history | ✅ Implemented |
| **9.1.4** | [Track 4: Per-Resource](#track-4-per-resource-telemetry-icontentiontarget) | Resource-centric callbacks | ✅ Implemented |
| **9.2** | [Metrics](#92-metrics) | Counters, histograms, gauges | 🆕 Designed |
| **9.3** | [Traces](#93-traces) | Distributed tracing spans | 🆕 Designed |
| **9.4** | [Structured Logging](#94-structured-logging) | Serilog-based logs | ✅ Exists |
| **9.5** | [Health Checks](#95-health-checks) | Resource-aware system health | 🆕 Designed |
| **9.6** | [Telemetry Sinks](#96-telemetry-sinks) | Dev and production export | 🆕 Designed |

---

## 9.1 Telemetry Architecture

### Purpose

Provide telemetry infrastructure that has **provably zero overhead** when disabled, while supporting per-component granularity. The architecture exploits .NET JIT behavior: `static readonly` fields evaluated at Tier 1 compilation allow dead-code elimination of disabled paths.

### The Zero-Overhead Guarantee

When `TelemetryConfig.AccessControlActive` is `false` (a `static readonly` field), the JIT treats:

```csharp
if (TelemetryConfig.AccessControlActive)
{
    RecordContention(waitDuration);
}
```

...as dead code, eliminating it entirely — no branch, no NOP, nothing. This was validated by benchmark:

| Pattern | Mean (ns) | Overhead |
|---------|-----------|----------|
| No check (baseline) | 0.221 | — |
| `static readonly false` | 0.221 | **0%** |
| `static readonly true` | 0.446 | +102% (expected) |
| `static` (mutable) | 0.695 | +214% |
| Interface dispatch | 1.341 | +507% |
| Delegate | 0.924 | +318% |

> Source: `TelemetryToggleBenchmark.cs` — confirms `static readonly false` = baseline.

<a href="../assets/typhon-telemetry-tracks.svg">
  <img src="../assets/typhon-telemetry-tracks.svg" width="1200"
       alt="Telemetry Tracks — Zero-overhead architecture showing Track 1/2/3 with JIT elimination">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-telemetry-tracks.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

### Track 1: Static Readonly Configuration (`TelemetryConfig`)

Initialized once via static constructor, immutable thereafter. The JIT sees final values before compiling hot paths (ensured by `EnsureInitialized()`).

**Configuration hierarchy** (4 components × 3–5 sub-flags each):

```
TelemetryConfig
├── Enabled (master switch)
│
├── AccessControl
│   ├── AccessControlEnabled
│   ├── AccessControlTrackContention
│   ├── AccessControlTrackContentionDuration
│   ├── AccessControlTrackAccessPatterns
│   └── AccessControlActive = Enabled && AccessControlEnabled ← use this in hot paths
│
├── PagedMMF
│   ├── PagedMMFEnabled
│   ├── PagedMMFTrackPageAllocations
│   ├── PagedMMFTrackPageEvictions
│   ├── PagedMMFTrackIOOperations
│   ├── PagedMMFTrackCacheHitRatio
│   └── PagedMMFActive = Enabled && PagedMMFEnabled
│
├── BTree
│   ├── BTreeEnabled
│   ├── BTreeTrackNodeSplits
│   ├── BTreeTrackNodeMerges
│   ├── BTreeTrackSearchDepth
│   ├── BTreeTrackKeyComparisons (default: false — high overhead)
│   └── BTreeActive = Enabled && BTreeEnabled
│
└── Transaction
    ├── TransactionEnabled
    ├── TransactionTrackCommitRollback
    ├── TransactionTrackConflicts
    ├── TransactionTrackDuration
    └── TransactionActive = Enabled && TransactionEnabled
```

**Pre-combined `Active` flags** eliminate two-field checks in hot paths. Instead of `if (Enabled && AccessControlEnabled)`, callers use the single `if (AccessControlActive)`.

**Configuration precedence** (highest wins):

1. Environment variables: `TYPHON__TELEMETRY__ENABLED=true` (uses `__` separator)
2. `typhon.telemetry.json` in current directory
3. `typhon.telemetry.json` next to the assembly
4. Built-in defaults (all disabled)

**Startup protocol:**

```csharp
// MUST be called before hot paths are JIT-compiled (Tier 0 → Tier 1 promotion)
TelemetryConfig.EnsureInitialized();

// Diagnostic output at startup
_logger.LogInformation(TelemetryConfig.GetConfigurationSummary());
// → "Telemetry: Enabled [AccessControl, PagedMMF]"
```

### Track 2: DI-Injectable Options (`TelemetryOptions`)

For cold-path consumers that need configuration via dependency injection:

```csharp
// Registration (three overloads)
services.AddTyphonTelemetry(configuration);           // From IConfiguration
services.AddTyphonTelemetry();                        // Defaults only
services.AddTyphonTelemetry(opts => {                 // Programmatic
    opts.Enabled = true;
    opts.AccessControl.Enabled = true;
});
```

**Important:** Track 2 options are independent from Track 1 static fields. Both read the same configuration sources but serve different roles:
- Track 1 (`TelemetryConfig`): JIT-eliminable hot-path guards
- Track 2 (`TelemetryOptions`): DI-injectable for services, reporting, configuration UI

### Track 3: Deep Diagnostics (`static readonly bool`)

Runtime-switchable deep instrumentation for development and post-mortem analysis. Uses the same `static readonly` JIT dead-code elimination as Track 1 — zero overhead when the flag is `false`.

> **Note**: This track is transitioning from `#if TELEMETRY` preprocessor directives to `static readonly bool` fields. The new approach provides the same zero-overhead guarantee while avoiding separate build configurations.

**AccessControl lock history** — Records every lock operation in a `ChainedBlockAllocator`:

```csharp
// AccessOperations: InlineArray of 6 AccessOperation structs = 108 bytes
// Fits in 128-byte blocks (2 cache lines) minus chain header
[InlineArray(6)]
internal struct AccessOperations { ... }

// AccessOperation: 18-byte packed struct
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AccessOperation
{
    internal ulong LockData;       //  0: Lock state snapshot (8 bytes)
    internal long Tick;            //  8: DateTime.UtcNow.Ticks (8 bytes)
    internal OperationType Type;   // 16: Operation enum (1 byte)
    internal byte ThreadId;        // 17: Thread ID (1 byte)
}
```

**Operation types tracked:**
- `EnterSharedAccess` / `ExitSharedAccess`
- `EnterExclusiveAccess` / `ExitExclusiveAccess`
- `SharedStartWait` / `ExclusiveStartWait`
- `PromoteToExclusive` / `PromoteStartWait` / `DemoteFromExclusive`
- `TimedOutOrCanceled`

**Post-mortem output** (`ToDebugString`):

```
Lock #42:
[2025-01-15T10:30:00.123] | Thread: 3  | Op: Exclusive Start | Data: [State: Idle   Shared: 0  ...]
[2025-01-15T10:30:00.124] | Thread: 7  | Op: Wait (S)  Start | Data: [State: Exclus Shared: 0  ...]
[2025-01-15T10:30:00.130] | Thread: 3  | Op: Exclusive Exit  | Data: [State: Exclus Shared: 0  ...]
[2025-01-15T10:30:00.131] | Thread: 7  | Op: Shared    Start | Data: [State: Shared Shared: 1  ...]
```

### Track 4: Per-Resource Telemetry (`IContentionTarget`)

A **callback-based** telemetry system where resources opt-in to receive contention events from their locks. Unlike Tracks 1-3 which are global on/off switches, Track 4 provides **per-resource granularity** with three levels.

#### TelemetryLevel Enum

```csharp
public enum TelemetryLevel
{
    None = 0,   // Zero overhead — simple null/enum check
    Light = 1,  // Aggregate contention counters (WaitCount, TotalWaitUs)
    Deep = 2    // Full operation history with timestamps and thread IDs
}
```

#### IContentionTarget Interface

Resources implement this interface to receive telemetry callbacks from locks:

```csharp
public interface IContentionTarget
{
    /// <summary>Current telemetry level (use volatile field).</summary>
    TelemetryLevel TelemetryLevel { get; }

    /// <summary>Optional link to IResource for graph integration.</summary>
    IResource OwningResource { get; }

    /// <summary>Light mode: called when a thread had to wait for a lock.</summary>
    void RecordContention(long waitUs);

    /// <summary>Deep mode: called for every lock operation.</summary>
    void LogLockOperation(LockOperation operation, long durationUs);
}
```

#### LockOperation Enum

```csharp
public enum LockOperation : byte
{
    None = 0,
    SharedAcquired, SharedReleased, SharedWaitStart,
    ExclusiveAcquired, ExclusiveReleased, ExclusiveWaitStart,
    PromoteToExclusiveStart, PromoteToExclusiveAcquired, DemoteToShared,
    TimedOut, Canceled
}
```

#### Usage Pattern

Lock methods accept `IContentionTarget` as the **last parameter** (`null` = no telemetry):

```csharp
// Lock API
public bool EnterExclusiveAccess(TimeSpan? timeOut = null,
    CancellationToken token = default, IContentionTarget target = null)

// Resource passes itself when acquiring its locks
_tableLatch.EnterExclusiveAccess(target: this);  // 'this' implements IContentionTarget
```

#### Deep Mode Storage

When `TelemetryLevel.Deep` is active, operations are logged via `ResourceTelemetryAllocator`:

```csharp
// 16-byte packed entry stored in ChainedBlockAllocator
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ResourceOperationEntry
{
    public long Timestamp;           // 8 bytes - Stopwatch.GetTimestamp()
    public uint DurationUs;          // 4 bytes - wait/operation duration
    public ushort ThreadId;          // 2 bytes
    public LockOperation Operation;  // 1 byte
    public byte Flags;               // 1 byte - reserved
}

// 6 entries per block (96 bytes) fits in 128-byte allocation
[InlineArray(6)]
internal struct ResourceOperationBlock { ... }
```

#### Tracks Comparison

| Track | Scope | Granularity | Overhead When Disabled | Use Case |
|-------|-------|-------------|------------------------|----------|
| **Track 1** | Global | Per-component (all AccessControl, all PagedMMF) | Zero (JIT eliminated) | Production monitoring |
| **Track 2** | Global | Per-component | Low (DI resolution) | Cold-path config |
| **Track 3** | Global | Lock-centric (per-lock history) | Zero (JIT eliminated) | Post-mortem debugging |
| **Track 4** | Per-resource | Resource-centric (aggregate + history) | Negligible (null check) | Per-resource diagnostics |

Track 4 complements the other tracks: it enables **targeted telemetry** on specific resources (e.g., enable Deep mode only on `ComponentTable<Player>`) without global overhead.

---

## 9.2 Metrics

### Purpose

Collect quantitative measurements for real-time monitoring and alerting. Metrics integrate with the [Resource Taxonomy](08-resources.md) — each tracked resource exposes utilization gauges and operation counters.

### Metrics Catalog

#### Transaction Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.tx.count` | Counter | Total transactions created | — |
| `typhon.tx.committed` | Counter | Successful commits | — |
| `typhon.tx.rolled_back` | Counter | Rollbacks | — |
| `typhon.tx.duration_us` | Histogram | Transaction lifetime (μs) | — |
| `typhon.tx.conflicts` | Counter | Optimistic concurrency conflicts | — |
| `typhon.tx.active` | Gauge | Currently active transactions | F.1 Transaction Pool |
| `typhon.tx.cache_entries` | Gauge | Cached component entries across active txns | F.2 Transaction Caches |

#### Page Cache Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.cache.hits` | Counter | Page found in cache | — |
| `typhon.cache.misses` | Counter | Page required disk load | — |
| `typhon.cache.hit_ratio` | Gauge | Hits / (Hits + Misses) | — |
| `typhon.cache.evictions` | Counter | Pages evicted (clock-sweep) | A.1 Page Cache |
| `typhon.cache.dirty_pages` | Gauge | Pages modified but not yet flushed | A.1 Page Cache |
| `typhon.cache.utilization` | Gauge | Used / Total slots | A.1 Page Cache |

#### I/O Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.io.page_reads` | Counter | 8KB page reads from disk | B.1 Data File |
| `typhon.io.page_writes` | Counter | 8KB page writes to disk | B.1 Data File |
| `typhon.io.read_bytes` | Counter | Total bytes read | — |
| `typhon.io.write_bytes` | Counter | Total bytes written | — |
| `typhon.io.read_duration_us` | Histogram | Read latency (μs) | — |
| `typhon.io.write_duration_us` | Histogram | Write latency (μs) | — |

#### Concurrency / AccessControl Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.lock.acquisitions` | Counter | Total lock acquisitions (shared + exclusive) | C.1–C.4 Latches |
| `typhon.lock.contentions` | Counter | Threads that had to wait | C.1–C.4 Latches |
| `typhon.lock.contention_duration_us` | Histogram | Wait time before acquisition (μs) | — |
| `typhon.lock.exclusive_ratio` | Gauge | Exclusive / Total acquisitions | — |
| `typhon.lock.timeouts` | Counter | Lock attempts that timed out | — |
| `typhon.lock.promotions` | Counter | Shared → Exclusive upgrades | — |

#### B+Tree Index Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.index.lookups` | Counter | Point lookups | — |
| `typhon.index.range_scans` | Counter | Range scan operations | — |
| `typhon.index.inserts` | Counter | Key insertions | — |
| `typhon.index.deletes` | Counter | Key deletions | — |
| `typhon.index.node_splits` | Counter | Node overflow splits | B.4 B+Tree Nodes |
| `typhon.index.node_merges` | Counter | Node underflow merges | B.4 B+Tree Nodes |
| `typhon.index.depth` | Gauge | Current tree depth | — |
| `typhon.index.key_comparisons` | Counter | Comparisons per operation (high overhead) | — |

#### WAL / Durability Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.wal.records_written` | Counter | WAL records flushed | D.1 WAL Ring Buffer |
| `typhon.wal.flush_duration_us` | Histogram | FUA write latency (μs) | — |
| `typhon.wal.ring_fill` | Gauge | Ring buffer utilization (0.0–1.0) | D.1 WAL Ring Buffer |
| `typhon.wal.flushes` | Counter | Total FUA operations | — |
| `typhon.wal.bytes_written` | Counter | Bytes written to WAL | — |
| `typhon.checkpoint.duration_ms` | Histogram | Checkpoint duration | — |
| `typhon.checkpoint.pages_flushed` | Counter | Pages synced per checkpoint | — |
| `typhon.checkpoint.dirty_pages` | Gauge | Pages awaiting checkpoint | D.3 Checkpoint Dirty Set |
| `typhon.checkpoint.count` | Counter | Total checkpoints completed | — |

#### Allocation Infrastructure Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.alloc.segment_pages` | Gauge | Total pages across all segments | B.2 Segment Pages |
| `typhon.alloc.chunk_utilization` | Gauge | Used / Total chunks per segment | — |
| `typhon.alloc.overflow_pages` | Gauge | Overflow pages allocated | — |
| `typhon.alloc.bitmap_scans` | Counter | L0/L1/L2 bitmap scans for free slot | G.1–G.3 Bitmaps |

#### Backup Metrics

| Metric | Type | Description | Resource |
|--------|------|-------------|---------------|
| `typhon.backup.snapshot_duration_ms` | Histogram | Full snapshot creation time | — |
| `typhon.backup.delta_size_bytes` | Histogram | Reverse-delta file sizes | E.3 Delta Files |
| `typhon.backup.pages_changed` | Counter | Pages included in delta | — |
| `typhon.backup.store_io_bytes` | Counter | Bytes written to backup store | — |

### OpenTelemetry Integration

```csharp
// Meter registration
var meter = new Meter("Typhon.Engine", "1.0.0");

// Counters (monotonically increasing)
var txCounter = meter.CreateCounter<long>("typhon.tx.count",
    unit: "{transactions}", description: "Total transactions created");

// Histograms (distribution tracking)
var txDuration = meter.CreateHistogram<double>("typhon.tx.duration_us",
    unit: "us", description: "Transaction lifetime");

// Observable gauges (pull-based, sampled on collection)
var dirtyPages = meter.CreateObservableGauge("typhon.cache.dirty_pages",
    () => _pageCache.DirtyPageCount,
    unit: "{pages}", description: "Modified pages pending flush");
```

### Metrics and Resource Tracking Integration

Metrics expose resource utilization defined in [Resources](08-resources.md). The mapping is:

| Resource | Metric | Alert Threshold |
|-------------|--------|-----------------|
| A.1 Page Cache | `typhon.cache.utilization` | > 95% |
| D.1 WAL Ring Buffer | `typhon.wal.ring_fill` | > 80% |
| D.3 Checkpoint Dirty Set | `typhon.checkpoint.dirty_pages` | > MaxDirtyPages × 0.9 |
| F.1 Transaction Pool | `typhon.tx.active` | > MaxConcurrentTransactions × 0.8 |

---

## 9.3 Traces

### Purpose

Track operation flow through the engine for debugging and performance analysis. Traces connect to the [Resource Lifecycle](08-resources.md#86-resource-lifecycle) — each lifecycle transition can be a span boundary.

### Trace Spans

| Span | Attributes | Description |
|------|------------|-------------|
| `typhon.transaction` | `tsn`, `duration_us`, `status` | Transaction lifecycle (create → commit/rollback) |
| `typhon.read` | `entity_id`, `component`, `revision` | Component read within transaction |
| `typhon.write` | `entity_id`, `component`, `revision` | Component write within transaction |
| `typhon.commit` | `changes_count`, `conflicts` | Two-phase commit execution |
| `typhon.index.lookup` | `index_name`, `key`, `result_count` | B+Tree point lookup |
| `typhon.index.range_scan` | `index_name`, `from`, `to`, `results` | Range scan operation |
| `typhon.page.load` | `page_id`, `segment`, `source` | Page load from disk into cache |
| `typhon.page.evict` | `page_id`, `was_dirty` | Page eviction from clock-sweep |
| `typhon.wal.flush` | `lsn_range`, `record_count`, `bytes` | WAL FUA write |
| `typhon.checkpoint` | `pages_count`, `duration_ms`, `epoch` | Checkpoint operation |
| `typhon.uow` | `epoch`, `mode`, `txn_count` | UnitOfWork lifecycle |
| `typhon.backup.snapshot` | `pages_total`, `pages_changed`, `bytes` | Snapshot creation |

### Example Trace: Transaction with Conflict

```
Transaction (TSN: 12345, 2.3ms, status: committed)
├── Read PlayerComponent (entity: 1001, revision: 42, 0.1ms)
├── Read InventoryComponent (entity: 1001, revision: 18, 0.15ms)
│   └── Page Load (page: 42, segment: ComponentSegment, source: disk, 0.08ms)
├── Write PlayerComponent (entity: 1001, revision: 43, 0.05ms)
│   └── Index Update (AccountId, key: "player123", 0.02ms)
├── Commit (changes: 1, conflicts: 0, 0.15ms)
│   ├── Conflict Check (0.02ms)
│   └── WAL Flush (lsn: 1000..1001, records: 1, 128B, 0.1ms)
└── [Event: page_eviction, page: 37, was_dirty: true]
```

### Example Trace: Checkpoint

```
Checkpoint (epoch: 5, pages: 128, 45ms)
├── Acquire Exclusive Latches (12 pages, 0.5ms)
├── Sort Dirty Pages (128 pages → 12 contiguous runs, 0.1ms)
├── Flush Run #1 (pages: 10..18, 8ms)
│   └── [Event: io_write, bytes: 73728]
├── Flush Run #2 (pages: 42..55, 12ms)
│   └── [Event: io_write, bytes: 114688]
├── ... (10 more runs)
├── Fsync (2ms)
└── Release Latches + Update WAL Trim Point (0.2ms)
```

### Sampling Strategy

For high-throughput workloads, trace sampling prevents overwhelming the sink:

| Span Type | Default Sample Rate | Rationale |
|-----------|--------------------:|-----------|
| `typhon.transaction` | 1% | High volume |
| `typhon.commit` | 10% | Medium volume |
| `typhon.page.load` | 0.1% | Very high volume |
| `typhon.checkpoint` | 100% | Low volume, always interesting |
| `typhon.backup.*` | 100% | Rare, always interesting |
| `typhon.wal.flush` | 10% | Medium volume |

---

## 9.4 Structured Logging

### Purpose

Emit structured log events with semantic context for debugging and auditing.

### Current Implementation

Typhon uses Serilog with Microsoft.Extensions.Logging abstractions:

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .Enrich.With<CurrentFrameEnricher>()
    .WriteTo.Seq("http://localhost:5341")
    .CreateLogger();
```

### Log Levels

| Level | Use Case | Example |
|-------|----------|---------|
| **Verbose** | Internal details, hot-path diagnostics | Page access, lock acquisition, bitmap scan |
| **Debug** | Development debugging | Transaction state transitions, cache decisions |
| **Information** | Significant lifecycle events | Transaction committed, checkpoint completed, backup created |
| **Warning** | Degraded operation, recoverable issues | Conflicts, retries, cache thrashing, near-budget limits |
| **Error** | Failures requiring attention | I/O errors, lock timeouts, resource exhaustion |
| **Fatal** | Unrecoverable state, engine must stop | Data corruption detected, WAL integrity failure |

### Enrichers

| Enricher | Property | Description |
|----------|----------|-------------|
| `CurrentFrameEnricher` | `Frame` | Current simulation/game frame number |
| `LogContext` | (varies) | Push-based contextual properties |

### Correlation

Structured log events carry correlation IDs for cross-component tracing:

```csharp
using (LogContext.PushProperty("TransactionId", transaction.TSN))
using (LogContext.PushProperty("EntityId", entityId))
{
    _logger.LogDebug("Component read: {ComponentType} revision {Revision}",
        typeof(T).Name, revision);
}
```

---

## 9.5 Health Checks

### Purpose

Expose system health for container orchestration and monitoring dashboards. Health checks integrate with the [Exhaustion & Back-Pressure policies](08-resources.md#87-budgets--exhaustion) — each critical resource contributes to health status.

### Health Check Types

| Check | Source | Healthy | Degraded | Unhealthy |
|-------|--------|---------|----------|-----------|
| **Page Cache** | `typhon.cache.utilization` | < 80% | 80–95% | > 95% |
| **WAL Ring** | `typhon.wal.ring_fill` | < 60% | 60–80% | > 80% (back-pressure active) |
| **Transaction Load** | `typhon.tx.active` | < 60% of pool | 60–80% | > 80% |
| **I/O Latency** | `typhon.io.write_duration_us` | p99 < 1ms | 1–10ms | > 10ms |
| **Disk Space** | OS query | > 20% free | 5–20% | < 5% |
| **Lock Contention** | `typhon.lock.contentions` / acquisitions | < 5% | 5–20% | > 20% |
| **Checkpoint Lag** | Pages dirty since last checkpoint | < MaxDirty × 0.5 | 50–90% | > 90% |

### Composite Health

Overall engine health is the **worst** of all individual checks:

```csharp
public interface ITyphonHealthCheck
{
    string Name { get; }
    HealthStatus Check();
    HealthCheckDetail GetDetail();
}

public enum HealthStatus
{
    Healthy,
    Degraded,    // Operational but approaching limits
    Unhealthy    // Back-pressure active or resource exhausted
}

public record HealthCheckDetail
{
    public HealthStatus Status { get; init; }
    public string Description { get; init; }
    public double CurrentValue { get; init; }
    public double ThresholdDegraded { get; init; }
    public double ThresholdUnhealthy { get; init; }
    public IReadOnlyDictionary<string, object> Data { get; init; }
}
```

### Cascade Failure Detection

When multiple resources degrade simultaneously, the health check system reports the **root cause** following the priority ordering from [Budgets & Exhaustion](08-resources.md#87-budgets--exhaustion):

```
Priority: WAL Ring > Page Cache > Transactions > Indexes
```

If WAL Ring is Unhealthy, downstream degradation in Transactions (which are waiting for WAL space) is attributed to WAL, not reported independently.

---

## 9.6 Telemetry Sinks

<a href="../assets/typhon-telemetry-sinks.svg">
  <img src="../assets/typhon-telemetry-sinks.svg" width="1200"
       alt="Telemetry Sinks — Dev (Seq + Aspire) and Production (Grafana LGTM) stacks with data source connections">
</a>
<sub>🔍 Click to open full size — D2 source: <code>assets/src/typhon-telemetry-sinks.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

### Configuration Example

```json
{
  "Typhon": {
    "Telemetry": {
      "Enabled": true,
      "AccessControl": {
        "Enabled": true,
        "TrackContention": true,
        "TrackContentionDuration": true,
        "TrackAccessPatterns": false
      },
      "PagedMMF": {
        "Enabled": true,
        "TrackPageAllocations": true,
        "TrackPageEvictions": true,
        "TrackIOOperations": true,
        "TrackCacheHitRatio": true
      },
      "BTree": {
        "Enabled": false
      },
      "Transaction": {
        "Enabled": true,
        "TrackCommitRollback": true,
        "TrackConflicts": true,
        "TrackDuration": true
      }
    }
  }
}
```

Environment variable overrides:

```bash
TYPHON__TELEMETRY__ENABLED=true
TYPHON__TELEMETRY__ACCESSCONTROL__ENABLED=true
TYPHON__TELEMETRY__BTREE__TRACKKEYCOMPARISONS=true
```

---

## Code Locations

### Track 1-3: Global Telemetry

| Component | Location | Status |
|-----------|----------|--------|
| TelemetryConfig | `src/Typhon.Engine/Observability/TelemetryConfig.cs` | ✅ Implemented |
| TelemetryOptions | `src/Typhon.Engine/Observability/TelemetryOptions.cs` | ✅ Implemented |
| TelemetryServiceExtensions | `src/Typhon.Engine/Observability/TelemetryServiceExtensions.cs` | ✅ Implemented |
| AccessControl Telemetry | `src/Typhon.Engine/Concurrency/AccessControl.Telemetry.cs` | ✅ Implemented |
| AccessOperation | `src/Typhon.Engine/Concurrency/AccessOperation.cs` | ✅ Implemented |
| OperationType enum | `src/Typhon.Engine/Concurrency/AccessOperation.cs` | ✅ Implemented |
| CurrentFrameEnricher | `src/Typhon.Engine/Observability/CurrentFrameEnricher.cs` | ✅ Exists |
| Telemetry Toggle Benchmark | `test/Typhon.Benchmark/TelemetryToggleBenchmark.cs` | ✅ Exists |
| ThreadLocal Dict Benchmark | `test/Typhon.Benchmark/ThreadLocalDictOverheadBenchmark.cs` | ✅ Exists |

### Track 4: Per-Resource Telemetry (IContentionTarget)

| Component | Location | Status |
|-----------|----------|--------|
| **IContentionTarget** | `src/Typhon.Engine/Observability/IContentionTarget.cs` | ✅ Implemented |
| **TelemetryLevel** | `src/Typhon.Engine/Observability/TelemetryLevel.cs` | ✅ Implemented |
| **LockOperation** | `src/Typhon.Engine/Observability/LockOperation.cs` | ✅ Implemented |
| **ResourceOperationEntry** | `src/Typhon.Engine/Observability/ResourceOperationEntry.cs` | ✅ Implemented |
| **ResourceTelemetryAllocator** | `src/Typhon.Engine/Observability/ResourceTelemetryAllocator.cs` | ✅ Implemented |
| AccessControl (with IContentionTarget) | `src/Typhon.Engine/Concurrency/AccessControl.cs` | ✅ Implemented |
| AccessControlTelemetryTests | `test/Typhon.Engine.Tests/Concurrency/AccessControlTelemetryTests.cs` | ✅ Implemented |

---

## Design Decisions

### Tracks 1-3: Global Telemetry

| Question | Decision | Rationale |
|----------|----------|-----------|
| **Hot-path guards** | `static readonly` fields | JIT eliminates dead branches at Tier 1 — zero overhead when disabled (benchmark-proven) |
| **Configuration immutability** | Set once in static constructor | Avoids volatile/memory barriers in hot path; restart to reconfigure |
| **Pre-combined Active flags** | `ComponentActive = Enabled && ComponentEnabled` | Single field check in hot path, no multi-field branch |
| **Deep diagnostics** | `static readonly bool` (same JIT trick) | Lock history needs ChainedBlockAllocator — zero overhead when disabled, no separate build config needed |
| **Preprocessor → static readonly** | Replace `#if TELEMETRY` with `static readonly bool` | Same JIT elimination, but avoids separate build configurations |
| **Lock operation recording** | 18-byte packed struct in InlineArray(6) | Fits 6 operations in 108 bytes → 128-byte block (2 cache lines) |
| **DI track** | Separate `TelemetryOptions` class | Cold-path consumers need IOptions pattern; don't contaminate static hot-path design |
| **Config file format** | `typhon.telemetry.json` | Separate from appsettings.json — telemetry config travels with the binary |
| **Env var separator** | `__` (double underscore) | Cross-platform IConfiguration standard (`:` doesn't work on Linux) |
| **Default state** | All disabled | Zero-overhead by default; opt-in per component |

### Track 4: Per-Resource Telemetry (IContentionTarget)

| Question | Decision | Rationale |
|----------|----------|-----------|
| **Per-resource levels** | `TelemetryLevel` enum (None/Light/Deep) | Different resources need different telemetry granularity; hot resources may need Deep, others just Light |
| **Callback pattern** | Resources implement `IContentionTarget`, pass `this` to locks | Inverts dependency: locks don't need to know about resources; resources opt-in |
| **IContentionTarget as last param** | `EnterExclusiveAccess(..., IContentionTarget target = null)` | Backward compatible — existing code passes no target (null = no telemetry) |
| **Volatile TelemetryLevel** | Simple volatile field, no locking | Level changes are rare; volatile is sufficient for visibility |
| **OwningResource property** | `IResource OwningResource { get; }` | Links telemetry back to resource graph without inheritance (`IContentionTarget` doesn't extend `IResource`) |
| **Deep mode storage** | 16-byte `ResourceOperationEntry` in ChainedBlockAllocator | Compact format fits 6 entries per 128-byte block; chains grow unbounded until resource frees |
| **No chain cap** | Deep mode chains grow unbounded | Resources are responsible for freeing chains when disabling Deep mode |

### General

| Question | Decision | Rationale |
|----------|----------|-----------|
| **Metrics framework** | OpenTelemetry `System.Diagnostics.Metrics` | .NET native, vendor-neutral, low overhead |
| **Production sink** | Grafana LGTM | Full-stack solution (logs + metrics + traces), good visualization |
| **Development sink** | Seq + Aspire | Rich structured log querying + real-time OTel dashboard |
| **Sampling** | Per-span-type configurable rates | High-volume spans (page loads) need aggressive sampling; rare spans (checkpoints) always captured |
| **Health model** | Worst-of composite | Simple, conservative — any degraded component degrades the whole engine |
| **Key comparisons** | Default disabled | Marked as high overhead in BTreeTelemetryOptions — only for targeted investigation |

---

## Cross-References

| Section | Relationship |
|---------|-------------|
| [Resources](08-resources.md) | Defines what to measure — metrics expose resource utilization |
| [Budgets & Exhaustion](08-resources.md#87-budgets--exhaustion) | Budget thresholds drive alert rules |
| [Exhaustion policies](08-resources.md#87-budgets--exhaustion) | Exhaustion policies map to health check states |
| [Concurrency](01-concurrency.md) | AccessControl telemetry tracks latch contention |
| [Storage](03-storage.md) | PagedMMF telemetry tracks page cache and I/O |
| [Data Engine](04-data-engine.md) | B+Tree and Transaction telemetry |
| [Durability](06-durability.md) | WAL and Checkpoint metrics |
| [Backup](07-backup.md) | Backup metrics (snapshot duration, delta sizes) |

---

## Open Questions

1. **Grafana dashboards** — Should Typhon ship pre-built dashboard JSON files? (Pro: immediate value. Con: maintenance burden, version coupling.)

2. **Alerting rules** — Should default Prometheus/Alertmanager rules be provided? What thresholds are universally applicable vs workload-dependent?

3. **dotnet-counters integration** — Should we expose `EventCounters` / `Meters` for `dotnet-counters monitor` without requiring OTel collector? (Useful for quick triage.)

4. **Trace context propagation** — How should Typhon traces correlate with the application's distributed trace? Should `Transaction.Create()` accept a parent `ActivityContext`?

5. **Metric cardinality** — Per-index metrics (`typhon.index.{name}.lookups`) could explode cardinality. Use labels/tags or separate meters?

6. **Log sampling in production** — Should Verbose/Debug logs be sampled (e.g., 1%) rather than completely suppressed in production? Helps catch rare issues.

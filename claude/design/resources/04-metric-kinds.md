# 04 — Metric Kinds Specification

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [03-metric-source.md](03-metric-source.md)

---

## Overview

Each resource node can declare which **metric kinds** it exposes. There are 6 kinds, each with specific sub-measurements and semantic meaning. This document is the authoritative specification for each kind.

> 💡 **Quick reference:** Jump to the [Summary Table](#9-summary-table) for a compact view of all metric kinds.

---

## Table of Contents

1. [Design Philosophy](#1-design-philosophy)
2. [Kind 1: Memory](#2-kind-1-memory)
3. [Kind 2: Capacity](#3-kind-2-capacity)
4. [Kind 3: DiskIO](#4-kind-3-diskio)
5. [Kind 4: Contention](#5-kind-4-contention)
6. [Kind 5: Throughput](#6-kind-5-throughput)
7. [Kind 6: Duration](#7-kind-6-duration)
8. [Value Type Definitions](#8-value-type-definitions)
9. [Summary Table](#9-summary-table)

---

## 1. Design Philosophy

### Why Exactly 6 Kinds?

These 6 kinds cover the fundamental questions operators ask:

| Question | Answered By |
|----------|-------------|
| "How much memory is this using?" | **Memory** |
| "How full is this?" | **Capacity** |
| "How much I/O is it doing?" | **DiskIO** |
| "Is this a bottleneck?" | **Contention** |
| "How many operations per second?" | **Throughput** |
| "How long do operations take?" | **Duration** |

### Counters vs Gauges

| Type | Behavior | Example |
|------|----------|---------|
| **Counter** | Monotonically increasing | ReadOps, WriteOps, CacheHits |
| **Gauge** | Can go up or down | UsedSlots, DirtyPages |
| **High-water mark** | Maximum ever observed | PeakBytes, MaxWaitUs |
| **Derived** | Computed from others | Utilization = Current / Maximum |

### When to Report a Metric

A resource should report a metric kind if:
1. The information is **meaningful** for that resource
2. The cost of tracking it is **acceptable** (see cost column below)
3. It helps answer **diagnostic questions**

Don't report a metric kind just because you can — empty metrics add noise.

---

## 2. Kind 1: Memory

Tracks byte allocations owned by this node (and transitively, its children).

### Sub-Metrics

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `AllocatedBytes` | Gauge | Current live allocation in bytes |
| `PeakBytes` | High-water | Maximum ever observed (resettable on demand) |

### Writer Method

```csharp
void WriteMemory(long allocatedBytes, long peakBytes);
```

### Value Type

```csharp
public readonly record struct MemoryMetrics(long AllocatedBytes, long PeakBytes);
```

### Implementation Guidance

**For fixed allocations (pinned buffers):**
```csharp
// Size never changes — peak equals current
writer.WriteMemory(_bufferSize, _bufferSize);
```

**For growable allocations:**
```csharp
// Track peak during allocation
if (_currentSize > _peakSize)
    _peakSize = _currentSize;

writer.WriteMemory(_currentSize, _peakSize);
```

### Collection Estimation Formulas

When tracking managed collections (dictionaries, lists), use these estimation formulas:

| Collection | Estimation Formula | Accuracy |
|-----------|-------------------|----------|
| `T[]` | `Length × sizeof(T)` | Exact |
| `List<T>` | `Capacity × sizeof(T)` | Exact |
| `Dictionary<K,V>` | `Capacity × (sizeof(K) + sizeof(V) + 12)` | ~90% |
| `ConcurrentDictionary<K,V>` | `Count × (sizeof(K) + sizeof(V) + 48)` | ~80% |
| `HashSet<T>` | `Capacity × (sizeof(T) + 12)` | ~90% |

The `+12` accounts for hash table entry overhead; `+48` for concurrent node + lock overhead.

### Ownership and Aggregation

Each node reports the memory **it directly owns** — not its children's memory:

```csharp
public class PageCache : IMetricSource
{
    private readonly PinnedMemoryBlock _buffer;  // Child node, reports itself
    private readonly ClockSweepState _state;     // Internal, not a node

    public void ReadMetrics(IMetricWriter writer)
    {
        // Report only what PageCache directly owns (internal state)
        // The _buffer child reports its own memory separately
        long ownMemory = EstimateStateMemory(_state);
        writer.WriteMemory(ownMemory, _peakOwnMemory);
    }
}
```

**Subtree aggregation** is computed by the snapshot API:

```csharp
// Get total memory for a subtree (node + all descendants)
long totalMemory = snapshot.GetSubtreeMemory("Storage/PageCache");
```

This separation keeps each node simple — it reports what it owns, and the graph handles rollup.

### Hot-Path Cost

**Zero** — just read existing size fields.

---

## 3. Kind 2: Capacity

Tracks utilization of a bounded slot-based structure.

### Sub-Metrics

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `Current` | Gauge | Slots/entries currently used |
| `Maximum` | Config | Total slots available |
| `Utilization` | Derived | `Current / Maximum` (0.0–1.0) |

### Writer Method

```csharp
void WriteCapacity(long current, long maximum);
```

Note: `Utilization` is derived by the consumer, not written explicitly.

### Value Type

```csharp
public readonly record struct CapacityMetrics(long Current, long Maximum)
{
    public double Utilization => Maximum > 0 ? (double)Current / Maximum : 0.0;
}
```

### Examples

| Resource | Current | Maximum | What It Represents |
|----------|---------|---------|-------------------|
| PageCache | 240 | 256 | Pages in cache / cache size |
| TransactionPool | 47 | 1000 | Active transactions / max |
| WALRingBuffer | 3_200_000 | 4_194_304 | Used bytes / 4 MB capacity |
| Bitmap | 847_231 | 1_000_000 | Bits set / total bits |
| ChunkSegment | 15_842 | 20_000 | Allocated chunks / max chunks |

### Alerting Thresholds

Standard thresholds for health checks (see [08-observability-bridge.md](08-observability-bridge.md)):

| Healthy | Degraded | Unhealthy |
|---------|----------|-----------|
| < 60-80% | 60-95% | > 95% |

Exact thresholds vary by resource — WAL should alert at 80%, page cache at 95%.

### Hot-Path Cost

**Zero** — read existing count fields.

---

## 4. Kind 3: DiskIO

Tracks read/write operations to persistent storage.

### Sub-Metrics

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `ReadOps` | Counter | Number of read operations |
| `WriteOps` | Counter | Number of write operations |
| `ReadBytes` | Counter | Total bytes read |
| `WriteBytes` | Counter | Total bytes written |

### Writer Method

```csharp
void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes);
```

### Value Type

```csharp
public readonly record struct DiskIOMetrics(
    long ReadOps,
    long WriteOps,
    long ReadBytes,
    long WriteBytes);
```

### Rate Derivation

I/O rates (ops/sec, MB/sec) are derived by consumers by differencing two snapshots:

```csharp
var opsPerSec = (current.DiskIO.ReadOps - previous.DiskIO.ReadOps) / elapsedSeconds;
var mbPerSec = (current.DiskIO.ReadBytes - previous.DiskIO.ReadBytes) / elapsedSeconds / 1_000_000;
```

### Which Resources Report DiskIO?

| Resource | Typical Values |
|----------|---------------|
| PageCache | High read/write ops during cache miss/flush |
| WALSegments | High write ops, sequential writes |
| Checkpoint | Burst write during checkpoint |
| SnapshotStore | Read during restore, write during backup |

### Hot-Path Cost

**~1 ns** — increment a field per I/O operation.

```csharp
// In page read path
_readOps++;
_readBytes += PageSize;
```

---

## 5. Kind 4: Contention

Tracks lock/latch wait behavior. This is the key metric for detecting concurrency bottlenecks.

### Sub-Metrics

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `WaitCount` | Counter | Times a thread had to wait (not immediate acquisition) |
| `TotalWaitUs` | Counter | Cumulative microseconds spent waiting |
| `MaxWaitUs` | High-water | Longest single wait observed (resettable) |
| `TimeoutCount` | Counter | Waits that exceeded Deadline (not cancellation) |

**Note:** `TimeoutCount` only increments when the lock's own `Deadline` timeout is exceeded. External cancellation (via `CancellationToken`) does not increment this counter — cancellation is not a contention metric.

### Writer Method

```csharp
void WriteContention(long waitCount, long totalWaitUs, long maxWaitUs, long timeoutCount);
```

### Value Type

```csharp
public readonly record struct ContentionMetrics(
    long WaitCount,
    long TotalWaitUs,
    long MaxWaitUs,
    long TimeoutCount)
{
    public double AvgWaitUs => WaitCount > 0 ? (double)TotalWaitUs / WaitCount : 0.0;
}
```

### The IContentionTarget Pattern

Individual latches do NOT get their own graph nodes — there could be thousands. Instead, the **owning component** aggregates contention via the `IContentionTarget` callback:

```csharp
public class PageCache : IResource, IMetricSource, IContentionTarget
{
    // Aggregate counters
    private long _contentionWaitCount;
    private long _contentionTotalUs;
    private long _contentionMaxUs;
    private long _contentionTimeouts;

    // IContentionTarget callback — called by locks when threads wait
    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalUs, waitUs);

        // Plain check-and-write for high-water mark — occasional lost max is acceptable
        if (waitUs > _contentionMaxUs)
            _contentionMaxUs = waitUs;
    }

    // IMetricSource — report aggregated contention
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteContention(_contentionWaitCount, _contentionTotalUs,
                              _contentionMaxUs, _contentionTimeouts);
    }

    public void ResetPeaks()
    {
        Volatile.Write(ref _contentionMaxUs, 0);
    }
}
```

### CPU Cost Measurement

Since Typhon already uses `Stopwatch` for Deadline timeout tracking, reusing the elapsed time for contention duration is nearly free — the stopwatch is already started when entering a wait loop.

### Zero Overhead When Disabled

When `target` is `null` or `TelemetryLevel` is `None`, lock code skips all telemetry paths with simple null/enum checks that the JIT can optimize.

### Distinguishing Contention from Duration

| Metric | What It Measures | Voluntary? |
|--------|-----------------|------------|
| **Contention** | Time spent *waiting for a resource* | No — involuntary |
| **Duration** | Time spent *doing useful work* | Yes — voluntary |

### Hot-Path Cost

**~5 ns** — Stopwatch is already running for Deadline; just reuse the elapsed value.

---

## 6. Kind 5: Throughput

Tracks monotonically increasing operation counters. Rates are derived by consumers.

### Sub-Metrics

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `Count` | Counter | Total operations since startup |

### Writer Method

```csharp
void WriteThroughput(string name, long count);
```

A node can call `WriteThroughput` multiple times with different names:

```csharp
writer.WriteThroughput(MetricNames.CacheHits, _cacheHits);
writer.WriteThroughput(MetricNames.CacheMisses, _cacheMisses);
writer.WriteThroughput(MetricNames.Evictions, _evictions);
```

### Value Type

```csharp
public readonly record struct ThroughputMetric(string Name, long Count);
```

A node's snapshot contains a list: `IReadOnlyList<ThroughputMetric>`.

### MetricNames Class

Use constants from `MetricNames` for consistent naming across components:

```csharp
/// <summary>
/// Standard metric names for consistent taxonomy across components.
/// </summary>
public static class MetricNames
{
    // Cache metrics
    public const string CacheHits = "CacheHits";
    public const string CacheMisses = "CacheMisses";
    public const string Evictions = "Evictions";

    // Transaction metrics
    public const string Created = "Created";
    public const string Committed = "Committed";
    public const string RolledBack = "RolledBack";
    public const string Conflicts = "Conflicts";

    // Index metrics
    public const string Lookups = "Lookups";
    public const string RangeScans = "RangeScans";
    public const string Inserts = "Inserts";
    public const string Deletes = "Deletes";
    public const string Splits = "Splits";
    public const string Merges = "Merges";

    // WAL/Durability metrics
    public const string RecordsWritten = "RecordsWritten";
    public const string Flushes = "Flushes";
    public const string CheckpointsCompleted = "CheckpointsCompleted";

    // General
    public const string HeartbeatsChecked = "HeartbeatsChecked";
}
```

Using centralized names ensures:
- Consistent Grafana dashboard queries (`sum(typhon_cache_hits)`)
- IntelliSense discoverability
- No typos across components

### Common Counter Names

| Component | Counter Names |
|-----------|---------------|
| PageCache | CacheHits, CacheMisses, Evictions |
| TransactionPool | Created, Committed, RolledBack, Conflicts |
| BTree Index | Lookups, RangeScans, Inserts, Deletes, Splits, Merges |
| WALRingBuffer | RecordsWritten, Flushes |
| Checkpoint | CheckpointsCompleted |
| Watchdog | HeartbeatsChecked |

### Rate Computation

```csharp
var prev = previousSnapshot.Nodes["Storage/PageCache"].Throughput["CacheHits"];
var curr = currentSnapshot.Nodes["Storage/PageCache"].Throughput["CacheHits"];
var hitsPerSecond = (curr - prev) / elapsedSeconds;
```

### Hot-Path Cost

**~1 ns** — increment a field.

```csharp
// Not even Interlocked — single writer assumed
_cacheHits++;
```

---

## 7. Kind 6: Duration

Tracks time cost of discrete operations (start/stop pairs).

### Sub-Metrics

| Sub-metric | Type | Description |
|-----------|------|-------------|
| `LastUs` | Gauge | Duration of the most recent operation (microseconds) |
| `AvgUs` | Simple average | Sum / Count since last reset (microseconds) |
| `MaxUs` | High-water | Longest operation observed (resettable) |

### Writer Method

```csharp
void WriteDuration(string name, long lastUs, long avgUs, long maxUs);
```

A node can call `WriteDuration` multiple times for different operation types.

### Value Type

```csharp
public readonly record struct DurationMetric(string Name, long LastUs, long AvgUs, long MaxUs);
```

### MetricNames for Duration

Use constants from `MetricNames` (defined in Throughput section):

```csharp
// Add to MetricNames class:
public const string TransactionLifetime = "TransactionLifetime";
public const string CheckpointFlush = "CheckpointFlush";
public const string Flush = "Flush";
public const string Commit = "Commit";
public const string SnapshotCreation = "SnapshotCreation";
```

### Common Duration Names

| Component | Duration Name |
|-----------|---------------|
| TransactionPool | TransactionLifetime |
| Checkpoint | CheckpointFlush |
| WALRingBuffer | Flush |
| GroupCommit | Commit |
| Backup | SnapshotCreation |

### Tracking Implementation

Uses **simple average (sum / count)** — reset via `ResetPeaks()` for windowed averages:

```csharp
// Fields for tracking duration
private long _checkpointLastUs;
private long _checkpointMaxUs;
private long _checkpointSumUs;   // For computing average
private long _checkpointCount;   // Division denominator

private void RecordCheckpointDuration(long durationUs)
{
    _checkpointLastUs = durationUs;
    if (durationUs > _checkpointMaxUs)
        _checkpointMaxUs = durationUs;
    _checkpointSumUs += durationUs;
    _checkpointCount++;
}

public void ReadMetrics(IMetricWriter writer)
{
    var avgUs = _checkpointCount > 0 ? _checkpointSumUs / _checkpointCount : 0;
    writer.WriteDuration(MetricNames.CheckpointFlush, _checkpointLastUs, avgUs, _checkpointMaxUs);
}

public void ResetPeaks()
{
    // Reset for windowed average measurement
    Volatile.Write(ref _checkpointMaxUs, 0);
    Volatile.Write(ref _checkpointSumUs, 0);
    Volatile.Write(ref _checkpointCount, 0);
}
```

**Why simple average over EMA?**
- Simpler to understand and implement
- No alpha tuning required
- `ResetPeaks()` enables windowed averages when needed
- Overflow is not a concern (would take centuries at μs granularity)

### Hot-Path Cost

**~5 ns** — Stopwatch stop and field update.

---

## 8. Value Type Definitions

All metric value types are **readonly record structs** for:
- Immutability (snapshot values don't change)
- Zero allocation when passed by value
- Value equality semantics

### Complete Definitions

```csharp
namespace Typhon.Engine.Resources;

/// <summary>Tracks byte allocations.</summary>
public readonly record struct MemoryMetrics(long AllocatedBytes, long PeakBytes);

/// <summary>Tracks utilization of bounded structures.</summary>
public readonly record struct CapacityMetrics(long Current, long Maximum)
{
    /// <summary>Current / Maximum (0.0–1.0), or 0 if Maximum is 0.</summary>
    public double Utilization => Maximum > 0 ? (double)Current / Maximum : 0.0;
}

/// <summary>Tracks read/write operations to storage.</summary>
public readonly record struct DiskIOMetrics(
    long ReadOps,
    long WriteOps,
    long ReadBytes,
    long WriteBytes);

/// <summary>Tracks lock wait behavior.</summary>
public readonly record struct ContentionMetrics(
    long WaitCount,
    long TotalWaitUs,
    long MaxWaitUs,
    long TimeoutCount)
{
    /// <summary>Average wait time in microseconds, or 0 if no waits.</summary>
    public double AvgWaitUs => WaitCount > 0 ? (double)TotalWaitUs / WaitCount : 0.0;
}

/// <summary>Named operation counter.</summary>
public readonly record struct ThroughputMetric(string Name, long Count);

/// <summary>Named operation duration.</summary>
public readonly record struct DurationMetric(string Name, long LastUs, long AvgUs, long MaxUs);
```

---

## 9. Summary Table

| Kind | What It Answers | Sub-Metrics | Hot-Path Cost | Update Pattern |
|------|----------------|-------------|---------------|----------------|
| **Memory** | "How many bytes does this own?" | AllocatedBytes, PeakBytes | 0 | At alloc/dealloc |
| **Capacity** | "How full is this?" | Current, Maximum, Utilization | 0 | At slot alloc/free |
| **DiskIO** | "How much I/O is it doing?" | ReadOps, WriteOps, ReadBytes, WriteBytes | ~1 ns | Per I/O operation |
| **Contention** | "Is this a bottleneck?" | WaitCount, TotalWaitUs, MaxWaitUs, TimeoutCount | ~5 ns | On lock wait |
| **Throughput** | "How many ops/sec?" | (named) Count | ~1 ns | Per operation |
| **Duration** | "How long do operations take?" | (named) LastUs, AvgUs, MaxUs | ~5 ns | Per operation end |

### Metric Kind Selection Guide

| If Your Resource... | Report These Kinds |
|---------------------|-------------------|
| Allocates memory | Memory |
| Has bounded slots | Capacity |
| Does disk I/O | DiskIO |
| Has locks that threads wait for | Contention |
| Counts operations | Throughput |
| Has timed operations | Duration |

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [03-metric-source.md](03-metric-source.md) | How to implement `IMetricSource` |
| [06-snapshot-api.md](06-snapshot-api.md) | How snapshots read these values |
| [08-observability-bridge.md](08-observability-bridge.md) | How metrics map to OTel |
| [overview/08-resources.md](../../overview/08-resources.md) §8.3 | High-level overview |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Duration averaging | Simple average (sum/count) | Simpler than EMA; reset via `ResetPeaks()` for windowed averages |
| Counter/Duration names | Centralized `MetricNames` class | Enforces taxonomy; consistent Grafana queries |
| TimeoutCount semantics | Deadline only | Cancellation is external, not a contention metric |
| Memory aggregation | Each node reports its own | Graph computes subtree totals via `GetSubtreeMemory()` |
| High-water mark atomicity | Plain check-and-write | Occasional lost max acceptable for diagnostics |

---

*Document Version: 2.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

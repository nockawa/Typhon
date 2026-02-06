# Part 2: Span Instrumentation

> **TL;DR — Quick start:** This plan covers adding distributed tracing spans to Typhon using `System.Diagnostics.Activity`. Jump to [Implementation Tasks](#7-implementation-tasks) for the work breakdown.

**Date:** February 2026
**Status:** ✅ Done (read-path spans deferred by design; segment allocation deferred for hot-path safety)
**Prerequisites:** [01-monitoring-stack-setup.md](01-monitoring-stack-setup.md) (Jaeger running)
**Related:** [monitoring-guide.md](../../ops/monitoring-guide.md) §Instrumenting Typhon with Spans

---

## Table of Contents

1. [Overview](#1-overview)
2. [What is a Span?](#2-what-is-a-span)
3. [Instrumentation Strategy](#3-instrumentation-strategy)
4. [Span Catalog](#4-span-catalog)
5. [Implementation Patterns](#5-implementation-patterns)
6. [Performance Considerations](#6-performance-considerations)
7. [Implementation Tasks](#7-implementation-tasks)
8. [Verification Checklist](#8-verification-checklist)

---

## 1. Overview

### Goal

Add distributed tracing spans to Typhon so that:
- Transaction lifecycle is visible as a trace with nested spans
- Flame graphs in Jaeger show where time is spent
- Slow operations can be identified and diagnosed
- Traces correlate with logs via `trace_id`

### Scope

Comprehensive instrumentation across all subsystems:
- **Transaction Lifecycle** — Create, Read, Write, Commit, Rollback
- **Storage/Cache Path** — Page cache, disk I/O, segment allocation
- **Index Operations** — B+Tree lookups, inserts, range scans, splits

### Non-Goals

- Instrumenting every internal method (too granular, adds overhead)
- Real-time streaming of spans (batch export is fine)
- Custom span storage (Jaeger handles this)

---

## 2. What is a Span?

A **Span** represents a timed unit of work:

```
┌─────────────────────────────────────────────────────────────────┐
│ Span: Transaction.Commit                                        │
│ ├── TraceId: abc123...                                          │
│ ├── SpanId: span001                                             │
│ ├── ParentSpanId: (root)                                        │
│ ├── StartTime: 2026-02-04T14:32:05.123Z                         │
│ ├── Duration: 12.5ms                                            │
│ ├── Status: OK                                                  │
│ ├── Attributes:                                                 │
│ │   ├── tsn: 42851                                              │
│ │   ├── component_count: 3                                      │
│ │   └── conflict_detected: false                                │
│ └── Events:                                                     │
│     └── [14:32:05.130] "Revision chain updated"                 │
└─────────────────────────────────────────────────────────────────┘
```

### Span Hierarchy (Parent-Child)

Spans form a tree representing the call hierarchy:

```
Transaction.Commit (root span, 12.5ms)
├── ValidateConflicts (child, 0.5ms)
├── UpdateIndices (child, 8ms)
│   ├── BTree.Insert (grandchild, 3ms)
│   └── BTree.Insert (grandchild, 4.5ms)
└── FlushRevisions (child, 3ms)
    └── PageCache.WritePage (grandchild, 2ms)
```

### Flame Graph View

In Jaeger's "Trace Graph" tab, this renders as:

```
|████████████████████████████████████████████████| Transaction.Commit (12.5ms)
|██|                                              | ValidateConflicts (0.5ms)
   |████████████████████████████████|             | UpdateIndices (8ms)
   |████████████|                   |             | BTree.Insert (3ms)
                |██████████████████|              | BTree.Insert (4.5ms)
                                    |████████████| FlushRevisions (3ms)
                                    |████████|    | PageCache.WritePage (2ms)
```

---

## 3. Instrumentation Strategy

### 3.1 ActivitySource Definition

✅ **Implemented** as `TyphonActivitySource` (src/Typhon.Engine/Observability/TyphonActivitySource.cs):

```csharp
// src/Typhon.Engine/Observability/TyphonActivitySource.cs
namespace Typhon.Engine;

public static class TyphonActivitySource
{
    public const string Name = "Typhon.Engine";
    public const string Version = "1.0.0";

    public static ActivitySource Instance { get; } = new(Name, Version);

    public static Activity StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
        => Instance.StartActivity(operationName, kind);
}
```

### 3.2 What to Instrument

**Rule of thumb:** Instrument operations you'd want to see in a flame graph.

| Category | Instrument? | Rationale |
|----------|-------------|-----------|
| User-facing operations | ✅ Yes | Transaction lifecycle, entity CRUD |
| I/O operations | ✅ Yes | Page loads, writes, flushes |
| Index operations | ✅ Yes | Lookups, inserts, splits |
| Lock acquisitions | ⚠️ Selective | Only when contention occurs (Part 3) |
| Small helper methods | ❌ No | Too granular, noise |
| Hot-path micro-operations | ❌ No | Overhead too high |

### 3.3 Attribute Naming Convention

Follow OpenTelemetry semantic conventions where applicable:

| Attribute | Type | Example |
|-----------|------|---------|
| `typhon.transaction.tsn` | long | `42851` |
| `typhon.transaction.status` | string | `"committed"`, `"rolledback"` |
| `typhon.entity.id` | long | `1001` |
| `typhon.component.type` | string | `"PlayerComponent"` |
| `typhon.component.revision` | int | `42` |
| `typhon.index.name` | string | `"IX_Player_AccountId"` |
| `typhon.index.operation` | string | `"lookup"`, `"insert"`, `"range_scan"` |
| `typhon.page.id` | int | `128` |
| `typhon.page.source` | string | `"cache"`, `"disk"` |
| `typhon.cache.hit` | bool | `true` |

---

## 4. Span Catalog

### 4.1 Transaction Lifecycle

| Span Name | Parent | Attributes | Events |
|-----------|--------|------------|--------|
| `Transaction.Create` | (root) | `tsn` | — |
| `Transaction.Commit` | (root) | `tsn`, `component_count`, `conflict_detected` | "Validation complete", "Indices updated" |
| `Transaction.Rollback` | (root) | `tsn`, `reason` | — |

### 4.2 Entity Operations

| Span Name | Parent | Attributes | Events |
|-----------|--------|------------|--------|
| `Entity.Create` | Transaction.* | `entity_id`, `component_type` | — |
| `Entity.Read` | Transaction.* | `entity_id`, `component_type`, `revision` | — |
| `Entity.Update` | Transaction.* | `entity_id`, `component_type`, `old_revision`, `new_revision` | — |
| `Entity.Delete` | Transaction.* | `entity_id`, `component_type` | — |

### 4.3 Index Operations

| Span Name | Parent | Attributes | Events |
|-----------|--------|------------|--------|
| `Index.Lookup` | Entity.* or Transaction.Commit | `index_name`, `key`, `found` | — |
| `Index.RangeScan` | Transaction.* | `index_name`, `from_key`, `to_key`, `result_count` | — |
| `Index.Insert` | Transaction.Commit | `index_name`, `key` | "Node split" (if occurred) |
| `Index.Delete` | Transaction.Commit | `index_name`, `key` | "Node merge" (if occurred) |

### 4.4 Storage Operations

| Span Name | Parent | Attributes | Events |
|-----------|--------|------------|--------|
| `PageCache.GetPage` | * | `page_id`, `cache_hit` | — |
| `PageCache.LoadPage` | PageCache.GetPage | `page_id`, `segment` | — |
| `PageCache.WritePage` | Transaction.Commit | `page_id`, `was_dirty` | — |
| `PageCache.Evict` | PageCache.LoadPage | `page_id`, `was_dirty` | — |
| `Segment.AllocateChunk` | Entity.Create | `segment_type`, `chunk_id` | — |

### 4.5 Commit Path (Detailed)

The commit path is critical for performance analysis:

```
Transaction.Commit
├── Commit.ValidateConflicts
│   └── (per component) Commit.CheckRevision
├── Commit.UpdateIndices
│   └── (per index) Index.Insert or Index.Delete
├── Commit.FlushRevisions
│   └── (per component) RevisionChain.Append
└── Commit.UpdatePrimaryKeys
```

---

## 5. Implementation Patterns

### 5.1 Basic Span Creation

```csharp
using static Typhon.Engine.Observability.TyphonTracing;

public bool Commit()
{
    using var span = Source.StartActivity("Transaction.Commit");
    span?.SetTag("typhon.transaction.tsn", TSN);
    span?.SetTag("typhon.transaction.component_count", _componentInfos.Count);

    try
    {
        // ... commit logic ...

        span?.SetTag("typhon.transaction.conflict_detected", hadConflict);
        span?.SetStatus(ActivityStatusCode.Ok);
        return true;
    }
    catch (Exception ex)
    {
        span?.SetStatus(ActivityStatusCode.Error, ex.Message);
        span?.RecordException(ex);
        throw;
    }
}
```

### 5.2 Nested Spans (Child Operations)

```csharp
public bool Commit()
{
    using var commitSpan = Source.StartActivity("Transaction.Commit");
    commitSpan?.SetTag("typhon.transaction.tsn", TSN);

    // Child span for validation
    using (Source.StartActivity("Commit.ValidateConflicts"))
    {
        ValidateConflicts();
    }

    // Child span for index updates
    using (var indexSpan = Source.StartActivity("Commit.UpdateIndices"))
    {
        int indexCount = UpdateIndices();
        indexSpan?.SetTag("typhon.index.update_count", indexCount);
    }

    // Child span for revision flush
    using (Source.StartActivity("Commit.FlushRevisions"))
    {
        FlushRevisions();
    }

    commitSpan?.SetStatus(ActivityStatusCode.Ok);
    return true;
}
```

### 5.3 Conditional Span Creation (Performance)

For operations that may be too frequent:

```csharp
// Only create span if someone is listening
public void GetPage(int pageId, out PageAccessor accessor)
{
    // StartActivity returns null if no listener is registered
    using var span = Source.StartActivity("PageCache.GetPage");

    bool cacheHit = TryGetFromCache(pageId, out accessor);

    // Only set tags if span was created
    span?.SetTag("typhon.page.id", pageId);
    span?.SetTag("typhon.cache.hit", cacheHit);

    if (!cacheHit)
    {
        using (Source.StartActivity("PageCache.LoadPage"))
        {
            LoadFromDisk(pageId, out accessor);
        }
    }
}
```

### 5.4 Adding Events (Milestones within a Span)

```csharp
using var span = Source.StartActivity("Transaction.Commit");

// ... validation ...
span?.AddEvent(new ActivityEvent("Validation complete"));

// ... index updates ...
span?.AddEvent(new ActivityEvent("Indices updated",
    tags: new ActivityTagsCollection { { "index_count", 3 } }));

// ... flush ...
span?.AddEvent(new ActivityEvent("Revisions flushed"));
```

### 5.5 Exception Recording

```csharp
try
{
    // ... operation ...
}
catch (Exception ex)
{
    span?.SetStatus(ActivityStatusCode.Error, ex.Message);
    span?.RecordException(ex);  // Adds exception details as event
    throw;
}
```

---

## 6. Performance Considerations

### 6.1 Zero Overhead When Disabled

`ActivitySource.StartActivity()` returns `null` when no listener is registered:

```csharp
// This is essentially free when tracing is disabled
using var span = Source.StartActivity("Operation");
span?.SetTag("key", value);  // null-conditional, no allocation
```

### 6.2 Sampling for High-Volume Operations

For very frequent operations, use activity sampling:

```csharp
// In OTel configuration
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Typhon.Engine")
        .SetSampler(new TraceIdRatioBasedSampler(0.1))  // 10% sampling
    );
```

Or implement custom sampling:

```csharp
public static class TyphonSampler
{
    private static long _counter;

    public static bool ShouldSample(int rate = 100)
    {
        return Interlocked.Increment(ref _counter) % rate == 0;
    }
}

// Usage
if (TyphonSampler.ShouldSample(100))  // 1 in 100
{
    using var span = Source.StartActivity("HighFrequencyOp");
    // ...
}
```

### 6.3 Attribute Cost

- **Primitive types:** Near-zero cost
- **Strings:** Allocation cost; prefer constants or interned strings
- **Objects:** Avoid; serialize to string if needed

```csharp
// Good: primitive
span?.SetTag("typhon.page.id", pageId);

// Good: constant string
span?.SetTag("typhon.page.source", "cache");

// Avoid: string interpolation in hot path
span?.SetTag("message", $"Loaded page {pageId}");  // Allocates!
```

### 6.4 Span Depth Limits

Don't create spans for every nested call. Rule of thumb:
- **2-4 levels deep** for a typical trace
- Deeper nesting adds overhead and visual clutter

---

## 7. Implementation Tasks

### Phase 1: Infrastructure

| # | Task | Description | Effort | Status |
|---|------|-------------|--------|--------|
| 1.1 | Create `TyphonActivitySource` class | Static `ActivitySource` definition | S | ✅ Done |
| 1.2 | Add OTel packages to Typhon.Engine | `OpenTelemetry.Api` reference | S | ✅ Done |
| 1.3 | Create attribute constants | `TyphonSpanAttributes` static class | S | ✅ Done |

### Phase 2: Transaction Spans

| # | Task | Description | Effort | Status |
|---|------|-------------|--------|--------|
| 2.1 | Instrument `Transaction.Commit()` | Root span with component count, conflict detection | M | ✅ Done |
| 2.2 | Instrument `Transaction.Rollback()` | Root span with reason | S | ✅ Done |
| 2.3 | Add commit sub-spans | CommitComponent span provides per-component breakdown; BTree spans nest as children | M | ✅ Done |
| 2.4 | Instrument `CreateEntity()` | Child span under transaction | S | ✅ Done |
| 2.5 | Instrument `ReadEntity()` | Child span with revision info | S | ✅ Done |
| 2.6 | Instrument `UpdateEntity()` | Child span with old/new revision | S | ✅ Done |

### Phase 3: Index Spans

| # | Task | Description | Effort | Status |
|---|------|-------------|--------|--------|
| 3.1 | Instrument B+Tree `Lookup()` | Span with key, found status | M | ⏸️ Deferred |
| 3.2 | Instrument B+Tree `Insert()` | Span with split event | M | ✅ Done |
| 3.3 | Instrument B+Tree `Delete()` | Span with merge event | M | ✅ Done |
| 3.4 | Instrument `RangeScan()` | Span with range and result count | M | ⏸️ Deferred |

> **Note:** Tasks 3.1 and 3.4 are deferred by design — only mutation operations (Insert/Delete) are traced. Read operations (Lookup/RangeScan) are not instrumented to avoid overhead on the read hot path.
>
> **Note:** Task 4.4 is deferred — `ChunkBasedSegment.AllocateChunk` is an extremely hot path (millions/sec, lock-free design). No telemetry gating flag exists. Instrumenting this would violate the zero-overhead principle.

### Phase 4: Storage Spans

| # | Task | Description | Effort | Status |
|---|------|-------------|--------|--------|
| 4.1 | Instrument `PageCache` get | Span with cache hit/miss | M | ✅ Done |
| 4.2 | Instrument page load from disk | Child span for I/O | M | ✅ Done |
| 4.3 | Instrument page eviction | ActivityEvent on AllocatePage span with evicted page ID | S | ✅ Done |
| 4.4 | Instrument segment allocation | Span for chunk allocation | S | ⏸️ Deferred |

### Phase 5: Integration

| # | Task | Description | Effort | Status |
|---|------|-------------|--------|--------|
| 5.1 | Add `TraceIdEnricher` to Serilog | Correlate logs with traces | S | ✅ Done |
| 5.2 | Configure OTel in demo project | Wire up OTLP exporter (PLJG or SigNoz) | M | ✅ Done |
| 5.3 | Verify flame graphs | End-to-end test (verified with SigNoz) | M | ✅ Done |

---

## 8. Verification Checklist

### Span Creation
- [x] `TyphonActivitySource.Instance` is initialized at startup
- [x] Transaction operations create spans (Commit, Rollback, CommitComponent)
- [x] Index operations create child spans (BTree.Insert, BTree.Delete, NodeSplit, NodeMerge)
- [x] Page cache operations create child spans (RequestPage, Fetch, DiskRead, AllocatePage, Flush, DiskWrite)
- [x] Page eviction recorded as ActivityEvent on AllocatePage span

### Flame Graph Quality
- [x] Trace backend shows nested span hierarchy (verified with SigNoz)
- [x] Commit path shows CommitComponent → BTree.Insert/Delete nesting
- [x] Slow operations are visually identifiable

### Log Correlation
- [x] TraceIdEnricher adds `TraceId`/`SpanId` to log events via `.Enrich.WithTraceId()`
- [ ] Grafana Loki shows clickable trace links (requires PLJG stack + Loki config)

### Performance
- [x] Tracing disabled: zero overhead via `TelemetryConfig` static readonly gating
- [x] Hot paths excluded: Lookup/RangeScan not instrumented, segment allocation deferred
- [ ] Tracing enabled: <5% overhead on typical workload (benchmark pending)

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [01-monitoring-stack-setup.md](01-monitoring-stack-setup.md) | Stack must be running for traces |
| [03-deep-diagnostics.md](03-deep-diagnostics.md) | Lock contention spans |
| [monitoring-guide.md](../../ops/monitoring-guide.md) | §Instrumenting Typhon with Spans |
| [09-observability.md](../../overview/09-observability.md) | Observability architecture |

---

*Document Version: 1.0*
*Last Updated: February 2026*
*Part of the Observability Implementation series*

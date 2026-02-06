# Part 3: Deep Diagnostics

> **TL;DR — Quick start:** This design covers microsecond-level lock forensics for post-mortem analysis and stress test debugging. Lock contention events are emitted as spans to Jaeger, with full call stacks. Jump to [Implementation Tasks](#9-implementation-tasks) for the work breakdown.

**Date:** February 2026
**Status:** Design
**Prerequisites:** [01-monitoring-stack-setup.md](01-monitoring-stack-setup.md), [02-span-instrumentation.md](02-span-instrumentation.md)
**Related:** [09-observability.md](../../overview/09-observability.md) §Track 3 & 4

---

## Table of Contents

1. [Overview](#1-overview)
2. [Problem Statement](#2-problem-statement)
3. [Design Principles](#3-design-principles)
4. [Architecture](#4-architecture)
5. [Contention Span Design](#5-contention-span-design)
6. [Ring Buffer Design](#6-ring-buffer-design)
7. [Call Stack Capture](#7-call-stack-capture)
8. [Consumption via Jaeger](#8-consumption-via-jaeger)
9. [Implementation Tasks](#9-implementation-tasks)
10. [Verification Checklist](#10-verification-checklist)
11. [Open Questions](#11-open-questions)

---

## 1. Overview

### Goal

Provide **microsecond-level forensics** for lock contention analysis:
- Capture every contention event (when a thread actually has to **wait**)
- Include full call stacks at the wait point
- Retain 30-60 minutes of history in memory
- Export to Jaeger for querying, filtering, and visualization
- Enable post-mortem analysis: "Why did thread 7 wait 2.3ms for thread 3?"

### What This Solves

Production observability tools (Prometheus, Grafana) show:
- "47 contention events averaging 340μs"

Deep diagnostics shows:
- "Thread 7 waited 2.3ms for Thread 3 at ComponentTable[Player]._tableLatch"
- "Thread 3 was holding exclusive lock while executing UpdateIndices()"
- "This happened 12 times in the last minute, always Thread 7 waiting for Thread 3"

### Scope

- **In scope:** Lock contention (AccessControl, AccessControlSmall, ResourceAccessControl)
- **Out of scope:** General tracing (covered in Part 2), memory allocation tracking, I/O tracing

---

## 2. Problem Statement

### The Challenge of Debugging Lock Contention

When developing Typhon, you encounter scenarios like:
- Stress test completes but with unexpectedly high latency
- Occasional transaction timeouts under load
- One thread seems to be blocking others

**What you need to know:**
1. Which lock(s) had contention?
2. Which threads were waiting?
3. Which thread was holding the lock?
4. What code path was the holder executing?
5. How long did waits last?
6. Is there a pattern (same threads, same locks)?

**What traditional tools show:**
- Metrics: Aggregate counts and averages (lose individual events)
- Logs: High-level events (miss the microsecond detail)
- Debugger: Point-in-time snapshot (miss the history)

### Volume Considerations

**Why contention-only capture:**

| Capture Strategy | Events/sec (estimate) | 30 min data |
|------------------|----------------------|-------------|
| Every lock acquire/release | 1,000,000+ | 1.8 billion events |
| Contention-only | 100-10,000 | 180K - 18M events |

Contention-only reduces volume by **100-10,000x** while capturing exactly what matters for debugging.

---

## 3. Design Principles

### 3.1 Zero Overhead When Not Contending

The fast path (immediate lock acquisition) must have **zero additional overhead**:

```csharp
// Fast path: TryEnter succeeds immediately
if (TryEnterExclusive())
{
    return;  // No contention tracking, no allocation, nothing
}

// Slow path: Must wait → capture diagnostics
CaptureContentionStart();
SpinWait();
CaptureContentionEnd();
```

### 3.2 Capture at the Right Moment

Capture happens when contention **starts** (we know we'll have to wait):
- Thread ID of waiter
- Lock identity
- Call stack of waiter
- Timestamp (high resolution)

And when contention **ends** (we acquired the lock):
- Duration of wait
- Thread ID that was holding (if available)

### 3.3 Export as Spans (Not Custom Format)

Emit contention events as **OpenTelemetry spans** to Jaeger:
- Reuse existing infrastructure (no new tools)
- Query by time range, filter by attributes
- Visualize in Jaeger UI
- Correlate with transaction traces

### 3.4 Ring Buffer for History

Keep recent contention events in memory:
- Configurable retention (default: 30-60 minutes)
- Export to Jaeger on demand or continuously
- Dump to file for offline analysis

---

## 4. Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Hot Path (Lock Operations)                        │
│                                                                             │
│  AccessControl.EnterExclusiveAccess()                                       │
│         │                                                                   │
│         ▼                                                                   │
│  ┌─────────────────┐     ┌─────────────────┐                                │
│  │ TryEnter (fast) │────▶│ Success: return │  Zero overhead                 │
│  └────────┬────────┘     └─────────────────┘                                │
│           │ Contention!                                                     │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │ ContentionCapture.Start()               │                                │
│  │  - Capture timestamp                    │                                │
│  │  - Capture thread ID                    │                                │
│  │  - Capture call stack                   │                                │
│  │  - Create ContentionEvent               │                                │
│  └────────────────────┬────────────────────┘                                │
│                       │                                                     │
│                       ▼                                                     │
│  ┌─────────────────────────────────────────┐                                │
│  │ SpinWait / Sleep (waiting for lock)     │                                │
│  └────────────────────┬────────────────────┘                                │
│                       │                                                     │
│                       ▼                                                     │
│  ┌─────────────────────────────────────────┐                                │
│  │ ContentionCapture.End()                 │                                │
│  │  - Capture end timestamp                │                                │
│  │  - Calculate duration                   │                                │
│  │  - Enqueue to ring buffer               │                                │
│  └────────────────────┬────────────────────┘                                │
└───────────────────────┼─────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Contention Ring Buffer                               │
│                                                                             │
│  ┌─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┐              │
│  │  E  │  E  │  E  │  E  │  E  │  E  │  E  │  E  │  E  │  E  │ ...          │
│  └─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┘              │
│  │◀─────────────────── 30-60 minutes ──────────────────────▶│               │
│                                                                             │
│  ContentionEvent:                                                           │
│  - Timestamp (long, Stopwatch ticks)                                        │
│  - Duration (long, microseconds)                                            │
│  - WaiterThreadId (ushort)                                                  │
│  - HolderThreadId (ushort, if known)                                        │
│  - LockId (int, identifies the lock instance)                               │
│  - LockName (string, e.g., "ComponentTable[Player]._tableLatch")            │
│  - StackTrace (string, captured at wait start)                              │
└────────────────────────────────────┬────────────────────────────────────────┘
                                     │
                                     │ Export (continuous or on-demand)
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Jaeger (via OTLP)                                 │
│                                                                             │
│  Span: Lock.Contention                                                      │
│  ├── typhon.lock.name: "ComponentTable[Player]._tableLatch"                 │
│  ├── typhon.lock.waiter_thread: 7                                           │
│  ├── typhon.lock.holder_thread: 3                                           │
│  ├── typhon.lock.wait_us: 2300                                              │
│  └── Events:                                                                │
│      └── "stack_trace": "at Typhon...UpdateEntity()\n  at..."               │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 5. Contention Span Design

### 5.1 Span Structure

Each contention event becomes a span in Jaeger:

| Field | Value | Purpose |
|-------|-------|---------|
| **Name** | `Lock.Contention` | Identifies span type |
| **Start Time** | When wait began | Time filtering |
| **Duration** | Wait duration | How long thread was blocked |
| **Service** | `Typhon.Diagnostics` | Separate from main traces |

### 5.2 Attributes

| Attribute | Type | Example | Purpose |
|-----------|------|---------|---------|
| `typhon.lock.name` | string | `"ComponentTable[Player]._tableLatch"` | Identify which lock |
| `typhon.lock.id` | int | `42` | Unique lock instance ID |
| `typhon.lock.type` | string | `"AccessControl"` | Lock implementation |
| `typhon.lock.mode` | string | `"exclusive"`, `"shared"` | What access was requested |
| `typhon.thread.waiter` | int | `7` | Thread that waited |
| `typhon.thread.holder` | int | `3` | Thread that held (if known) |
| `typhon.wait.duration_us` | long | `2300` | Microseconds waited |
| `typhon.resource.path` | string | `"DataEngine/ComponentTable[Player]"` | Resource graph path |

### 5.3 Events (Call Stack)

The call stack is attached as a span event:

```csharp
span.AddEvent(new ActivityEvent("wait_start", tags: new ActivityTagsCollection
{
    { "stack_trace", capturedStack }
}));
```

### 5.4 Example Span in Jaeger

```
Span: Lock.Contention
Service: Typhon.Diagnostics
Duration: 2.3ms
Start: 2026-02-04 14:32:05.123456

Attributes:
  typhon.lock.name = ComponentTable[Player]._tableLatch
  typhon.lock.type = AccessControl
  typhon.lock.mode = exclusive
  typhon.thread.waiter = 7
  typhon.thread.holder = 3
  typhon.wait.duration_us = 2300

Events:
  [14:32:05.123456] wait_start
    stack_trace =
      at Typhon.Engine.DatabaseEngine.Transaction.UpdateEntity[T](...)
      at Typhon.Engine.Tests.StressTest.WorkerThread(...)
      at System.Threading.ThreadHelper.ThreadStart_Context(...)
```

---

## 6. Ring Buffer Design

### 6.1 Requirements

- **Capacity:** 30-60 minutes of contention events
- **Thread-safe:** Multiple threads enqueue concurrently
- **Lock-free enqueue:** Must not add contention while tracking contention!
- **Ordered:** Events maintain chronological order for analysis
- **Bounded:** Old events evicted when buffer is full

### 6.2 Size Estimation

| Scenario | Events/min | 60 min total | Event size | Memory |
|----------|------------|--------------|------------|--------|
| Light contention | 100 | 6,000 | ~500 bytes | 3 MB |
| Moderate | 1,000 | 60,000 | ~500 bytes | 30 MB |
| Heavy | 10,000 | 600,000 | ~500 bytes | 300 MB |

**Default configuration:** 1 million events max (~500 MB cap)

### 6.3 Data Structure

```csharp
public class ContentionRingBuffer
{
    private readonly ContentionEvent[] _buffer;
    private long _writeIndex;  // Atomically incremented
    private long _readIndex;   // For export

    public ContentionRingBuffer(int capacity = 1_000_000)
    {
        _buffer = new ContentionEvent[capacity];
    }

    public void Enqueue(ContentionEvent evt)
    {
        // Lock-free enqueue using Interlocked
        var index = Interlocked.Increment(ref _writeIndex) - 1;
        _buffer[index % _buffer.Length] = evt;
    }

    public IEnumerable<ContentionEvent> GetEvents(DateTime from, DateTime to)
    {
        // Return events in time range
    }

    public void ExportToJaeger(ISpanExporter exporter)
    {
        // Convert events to spans and export
    }
}
```

### 6.4 ContentionEvent Structure

```csharp
public readonly struct ContentionEvent
{
    // Core timing (16 bytes)
    public readonly long TimestampTicks;     // Stopwatch.GetTimestamp()
    public readonly long DurationUs;         // Microseconds waited

    // Thread info (4 bytes)
    public readonly ushort WaiterThreadId;
    public readonly ushort HolderThreadId;   // 0 if unknown

    // Lock identity (8 bytes)
    public readonly int LockId;              // Unique instance ID
    public readonly int LockNameIndex;       // Index into string table

    // Mode (1 byte)
    public readonly LockMode Mode;           // Shared, Exclusive

    // Stack trace (reference, interned)
    public readonly string StackTrace;       // Captured at wait start

    // Resource path (reference, interned)
    public readonly string ResourcePath;     // From IResource.Path
}
```

---

## 7. Call Stack Capture

### 7.1 When to Capture

Capture stack trace **at the moment contention is detected** (before waiting):

```csharp
if (!TryEnterExclusive())
{
    // Contention detected - capture stack NOW
    var stack = CaptureStackTrace();
    var startTime = Stopwatch.GetTimestamp();

    // Now wait...
    SpinWaitForLock();

    // Wait complete - calculate duration
    var endTime = Stopwatch.GetTimestamp();
    var durationUs = (endTime - startTime) * 1_000_000 / Stopwatch.Frequency;

    // Record event
    _ringBuffer.Enqueue(new ContentionEvent(startTime, durationUs, stack, ...));
}
```

### 7.2 Stack Capture Method

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
private static string CaptureStackTrace()
{
    // Skip frames: CaptureStackTrace, EnterExclusiveAccess, actual caller
    return new StackTrace(skipFrames: 2, fNeedFileInfo: false).ToString();
}
```

### 7.3 Stack Trace Interning

To reduce memory, intern frequently-seen stack traces:

```csharp
public static class StackTraceInterner
{
    private static readonly ConcurrentDictionary<int, string> _cache = new();

    public static string Intern(string stackTrace)
    {
        var hash = stackTrace.GetHashCode();
        return _cache.GetOrAdd(hash, _ => stackTrace);
    }
}
```

### 7.4 Performance Impact

Stack trace capture is expensive (~1-5μs). But we only capture when:
1. Contention occurs (thread must wait anyway)
2. Wait will be at least 10-100μs typically

So the capture overhead is negligible compared to the wait time.

---

## 8. Consumption via Jaeger

### 8.1 Export Strategy

**Option A: Continuous export** (recommended for development)
- Export events to Jaeger as they occur
- Jaeger handles storage and querying
- Real-time visibility in UI

**Option B: On-demand export**
- Keep events in ring buffer
- Export when requested (e.g., after test completes)
- Lower overhead during test

**Recommendation:** Start with continuous export for simplicity.

### 8.2 Jaeger Query Examples

**Find all contention in last 5 minutes:**
```
Service: Typhon.Diagnostics
Operation: Lock.Contention
Time: Last 5 minutes
```

**Find long waits (>1ms):**
```
Service: Typhon.Diagnostics
Tags: typhon.wait.duration_us > 1000
```

**Find contention on specific lock:**
```
Service: Typhon.Diagnostics
Tags: typhon.lock.name = "ComponentTable[Player]._tableLatch"
```

**Find waits by specific thread:**
```
Service: Typhon.Diagnostics
Tags: typhon.thread.waiter = 7
```

### 8.3 Analysis Workflow

```
1. Run stress test
   │
   ▼
2. Grafana shows "contention spike at 14:32"
   │
   ▼
3. Jaeger: Query Lock.Contention spans around 14:32
   │
   ▼
4. See: 47 contention events, sort by duration
   │
   ▼
5. Click longest wait (2.3ms)
   │
   ▼
6. See attributes:
   - Lock: ComponentTable[Player]._tableLatch
   - Waiter: Thread 7
   - Holder: Thread 3
   │
   ▼
7. Expand "wait_start" event → see stack trace
   │
   ▼
8. Understand: Thread 7 was in UpdateEntity(), waiting for Thread 3
   │
   ▼
9. Query: "Show all waits where holder = Thread 3"
   │
   ▼
10. Pattern emerges: Thread 3 holds lock while doing expensive index updates
```

---

## 9. Implementation Tasks

### Phase 1: Core Infrastructure

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 1.1 | Create `ContentionEvent` struct | Immutable struct with all fields | S |
| 1.2 | Create `ContentionRingBuffer` | Lock-free ring buffer implementation | M |
| 1.3 | Create `StackTraceInterner` | Reduce memory for repeated stacks | S |
| 1.4 | Create `ContentionCapture` static class | Entry point for capture logic | M |
| 1.5 | Create `LockRegistry` | Track lock names and IDs | M |

### Phase 2: Lock Integration

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 2.1 | Modify `AccessControl` | Add contention capture points | M |
| 2.2 | Modify `AccessControlSmall` | Add contention capture points | M |
| 2.3 | Modify `ResourceAccessControl` | Add contention capture points | M |
| 2.4 | Add lock registration | Register locks with names on creation | S |
| 2.5 | Wire up `IContentionTarget` | Connect to resource graph | M |

### Phase 3: Jaeger Export

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 3.1 | Create `ContentionSpanExporter` | Convert events to OTel spans | M |
| 3.2 | Create `Typhon.Diagnostics` ActivitySource | Separate source for diagnostics | S |
| 3.3 | Implement continuous export | Background thread exports to Jaeger | M |
| 3.4 | Add export configuration | Enable/disable, batch size, etc. | S |

### Phase 4: Configuration & Control

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 4.1 | Add `DeepDiagnosticsOptions` | Buffer size, retention, export settings | S |
| 4.2 | Add DI registration | `AddTyphonDeepDiagnostics()` | S |
| 4.3 | Add enable/disable API | Runtime control of capture | S |
| 4.4 | Add manual export trigger | `TyphonDiagnostics.ExportNow()` | S |

### Phase 5: Demo Integration

| # | Task | Description | Effort |
|---|------|-------------|--------|
| 5.1 | Add to MonitoringDemo | Enable deep diagnostics in demo | M |
| 5.2 | Create writer-contention scenario | Stress test that generates contention | M |
| 5.3 | Document analysis workflow | How to use Jaeger for debugging | M |

---

## 10. Verification Checklist

### Capture
- [ ] Contention events captured when threads wait
- [ ] Stack traces captured at wait start
- [ ] Fast path (no contention) has zero overhead
- [ ] All lock types instrumented (AccessControl, AccessControlSmall, ResourceAccessControl)

### Ring Buffer
- [ ] Lock-free enqueue works under concurrent load
- [ ] Old events evicted when buffer fills
- [ ] Time-range queries return correct events
- [ ] Memory usage stays within bounds

### Jaeger Integration
- [ ] Spans appear in Jaeger with correct attributes
- [ ] Stack traces visible in span events
- [ ] Query by lock name works
- [ ] Query by thread ID works
- [ ] Query by duration works

### End-to-End
- [ ] Run stress test → See contention in Jaeger
- [ ] Identify which lock had most contention
- [ ] Identify which threads were involved
- [ ] Stack traces show meaningful call sites

---

## 11. Design Decisions (Resolved)

### 11.1 Holder Thread Identification

**Decision:** Use existing `LockedByThreadId` property — **already implemented**.

Both `AccessControl` and `AccessControlSmall` store the holder thread ID as part of their atomic 64-bit state:

```csharp
// AccessControl.cs line 605
internal int LockedByThreadId => (int)((_data & ThreadIdMask) >> ThreadIdShift);

// AccessControlSmall.cs line 49
public int LockedByThreadId => _data >> ThreadIdShift;
```

**For exclusive contention:** Read `LockedByThreadId` at moment of contention — 100% accurate, zero overhead.

**For shared contention:** `LockedByThreadId` is 0 (multiple holders). Record as "waiting for shared readers to release."

#### Future Enhancement: Shared Holder Tracking

For debugging shared-to-exclusive waits, a future "deep monitoring mode" could track all shared holders:

- **Activation:** `static readonly bool` (JIT-eliminated when false)
- **Storage:** Ephemeral allocation external to the lock struct (doesn't bloat `AccessControl`)
- **Lifetime:** Created when shared acquired, destroyed when lock becomes idle
- **Status:** Deferred — adds complexity; implement if shared contention debugging becomes a priority

### 11.2 Call Stack Depth

**Decision:** 15 frames by default, configurable.

```csharp
public class DeepDiagnosticsOptions
{
    /// <summary>Number of stack frames to capture at contention points.</summary>
    public int StackFrameCount { get; set; } = 15;
}
```

**Rationale:** 15 frames typically includes:
- The lock acquisition call site
- The Typhon API call (ReadEntity, Commit, etc.)
- The application/test code calling Typhon
- Minimal framework noise

### 11.3 Export Batching

**Decision:** Batch with 100ms or 100 events (whichever comes first).

**Rationale:** Balances real-time visibility with OTLP efficiency. During stress tests, batching reduces exporter overhead.

### 11.4 Trace Correlation

**Decision:** Natural parent-child via `Activity.Current`, same ActivitySource.

**How it works:** `Activity.Current` is automatically propagated via AsyncLocal. Creating a contention span with `StartActivity()` automatically makes it a child of whatever Activity is active.

| Scenario | Behavior |
|----------|----------|
| Contention during transaction | Contention span is **child** → visible in transaction flame graph |
| Contention outside transaction | Contention span is **root** → independent trace |

**Implementation:**
```csharp
// Same ActivitySource as transaction spans
using var span = TyphonTracing.Source.StartActivity("Lock.Contention");

// Add TSN for searchability when inside a transaction
if (Transaction.Current != null)
{
    span?.SetTag("typhon.transaction.tsn", Transaction.Current.TSN);
}
```

**Benefits:**
- **Simple** — no manual parent propagation
- **Unified** — contention appears in flame graph when relevant
- **Queryable** — filter by `typhon.transaction.tsn` or operation name

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [01-monitoring-stack-setup.md](01-monitoring-stack-setup.md) | Jaeger must be running |
| [02-span-instrumentation.md](02-span-instrumentation.md) | General span patterns |
| [09-observability.md](../../overview/09-observability.md) | Track 3 & 4 architecture |
| [01-concurrency.md](../../overview/01-concurrency.md) | AccessControl implementation |
| [IContentionTarget.cs](../../../src/Typhon.Engine/Observability/IContentionTarget.cs) | Existing interface |

---

*Document Version: 1.0*
*Last Updated: February 2026*
*Part of the Observability Implementation series*

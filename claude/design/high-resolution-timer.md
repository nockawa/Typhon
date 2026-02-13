# High-Resolution Timer — Design

**Date:** 2026-02-13
**Status:** Implemented
**GitHub Issue:** #76
**Branch:** `feature/41-execution-context`
**Related:** [DeadlineWatchdog](./execution/02-deadline-watchdog.md), [Concurrency Overview](../overview/01-concurrency.md)

> **TL;DR — A three-class hierarchy providing sub-millisecond periodic callbacks via dedicated threads using a three-phase hybrid wait strategy (Sleep → Yield → Spin).** `HighResolutionTimerServiceBase` (abstract, extends `ResourceNode`, implements `IMetricSource`) owns the calibrated wait loop and timing metrics. `HighResolutionTimerService` wraps a single handler at a fixed interval. `HighResolutionSharedTimerService` multiplexes multiple callbacks, dynamically computing the nearest next tick. All timer instances register in the resource tree under a new `Timer` root node. Self-calibrating across Windows/Linux/macOS.

---

## Table of Contents

1. [Motivation](#1-motivation)
2. [The Platform Problem](#2-the-platform-problem)
3. [Three-Phase Hybrid Wait Strategy](#3-three-phase-hybrid-wait-strategy)
4. [Class Hierarchy](#4-class-hierarchy)
5. [HighResolutionTimerServiceBase (Abstract)](#5-highresolutiontimerservicebase-abstract)
6. [HighResolutionTimerService (Single Handler)](#6-highresolutiontimerservice-single-handler)
7. [HighResolutionSharedTimerService (Multi-Callback)](#7-highresolutionsharedtimerservice-multi-callback)
8. [Resource Tree Integration](#8-resource-tree-integration)
9. [Observability](#9-observability)
10. [Integration Points](#10-integration-points)
11. [Performance Characteristics](#11-performance-characteristics)
12. [Testing Strategy](#12-testing-strategy)
13. [Design Decisions](#13-design-decisions)
14. [Files to Create/Modify](#14-files-to-createmodify)
15. [Future Extensions](#15-future-extensions)

---

## 1. Motivation

Several Typhon subsystems need periodic execution with better-than-15ms resolution:

| Consumer | Desired Frequency | Why |
|----------|------------------|-----|
| `DeadlineWatchdog` | 200Hz (5ms) | Deadline expiry within 5ms; shared timer avoids dedicated thread |
| Telemetry flush | 1–10Hz | Periodic metric aggregation without timer proliferation |
| Epoch advancement | 10–100Hz | Epoch-based resource cleanup |
| Shell status refresh | 2–5Hz | Interactive display updates |
| Future: checkpoint trigger | 1Hz | Periodic checkpoint scheduling |

Without a high-resolution timer foundation, each subsystem would either:

1. **Use `Thread.Sleep(1)`** — which on Windows (pre-.NET 8 or pre-Win10 1803) sleeps for ~15.6ms, far exceeding the target.
2. **Spin-wait** — which burns a full CPU core.
3. **Create its own dedicated thread** — proliferating threads and duplicating the calibration/wait logic.

### Two Usage Patterns

Different consumers have different needs:

| Pattern | Example | Service |
|---------|---------|---------|
| **Dedicated timer**: one handler, maximum isolation, own thread | Heartbeat monitor (safety-critical, 1ms) | `HighResolutionTimerService` |
| **Shared timer**: multiple handlers on one thread, CPU-efficient | DeadlineWatchdog + Telemetry flush + Shell refresh + epoch advance | `HighResolutionSharedTimerService` |

The abstract base class (`HighResolutionTimerServiceBase`) provides the calibrated three-phase wait loop and timing metrics, while the two derived classes specialize for each pattern.

---

## 2. The Platform Problem

### `Thread.Sleep(1)` Actual Resolution

| Platform | Runtime | Actual Sleep(1) | Mechanism |
|----------|---------|-----------------|-----------|
| **Windows (pre-.NET 8)** | .NET 6/7 | ~15.6ms | Default system timer tick (`timeGetDevCaps`) |
| **Windows (.NET 8+, Win10 1803+)** | .NET 8+ | ~0.5–1ms | `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` flag |
| **Windows (old, any .NET)** | Any | ~15.6ms | Only `timeBeginPeriod(1)` helps (global side effect) |
| **Linux (HZ=1000)** | Any | ~1ms | Kernel `timerfd` with 1ms granularity |
| **Linux (HZ=250)** | Any | ~4ms | Older kernels or server configs |
| **macOS** | Any | ~1ms | Mach kernel absolute time |

**Key insight**: The actual sleep resolution varies by 30x across platforms. Any design targeting sub-ms accuracy must **self-calibrate** rather than assume a fixed resolution.

### `Stopwatch` Resolution

`Stopwatch.Frequency` provides the high-resolution counter:

| Platform | Typical Frequency | Resolution |
|----------|------------------|------------|
| Windows | 10 MHz | 100ns |
| Linux | 1 GHz (TSC) | 1ns |
| macOS | 24 MHz (Apple Silicon) | ~42ns |

All platforms provide sub-microsecond measurement resolution via `Stopwatch.GetTimestamp()`. This is the timing foundation for the spin phase.

---

## 3. Three-Phase Hybrid Wait Strategy

The core technique divides the wait period into three phases, each trading off CPU usage against timing precision:

```
Time remaining:  ████████████████░░░░░░░░▓▓▓▓  0
                 │   Phase 1    │ Phase 2 │ P3│
                 │   Sleep(1)   │  Yield  │Spin
                 │   ~0% CPU    │  ~low   │brief
                 │   ±1-15ms    │  ±0-1ms │±µs
```

### Phase 1 — Sleep (CPU-friendly, coarse)

`Thread.Sleep(1)` deschedules the thread entirely. The OS may take 1ms to 15ms to re-schedule it. Used only when `remaining > calibrated_sleep_threshold`.

**CPU cost**: Near zero. The thread is not runnable.
**Precision**: Poor (±15ms worst case on old Windows).
**When used**: Only when far enough from the target time that even a worst-case oversleep won't overshoot.

### Phase 2 — Yield (medium CPU, medium precision)

`Thread.Yield()` gives up the current timeslice but the thread stays in the runnable queue. On a lightly loaded system, the thread is re-scheduled almost immediately. On a loaded system, it may take hundreds of microseconds.

**CPU cost**: Low-to-high depending on system load. On a lightly loaded system, `Thread.Yield()` returns almost immediately (via `SwitchToThread()` finding nothing to switch to), making the yield loop effectively a polling loop at ~95-100% of one core. On loaded systems, actual yields happen and CPU drops.
**Precision**: ±0–500µs depending on system load.
**When used**: When remaining time is below the sleep threshold but above the spin threshold.

> **CPU cost note for 1ms intervals**: At 1ms base period, Phase 1 (Sleep) is skipped entirely because `Sleep(1)` takes ≥0.5ms — there's no room. The timer operates in Yield+Spin mode, consuming approximately **one logical core**. This is the standard trade-off for sub-ms timing in latency-sensitive systems (game engines, HFT, audio). For longer intervals (≥5ms on .NET 8+), the Sleep phase kicks in and CPU drops to near zero.

### Phase 3 — Spin (brief CPU burst, high precision)

`Thread.SpinWait(1)` emits the `PAUSE` instruction (x86) or `YIELD` (ARM), signaling the CPU to:
- Save power during the spin (reduced pipeline speculation)
- Avoid memory-order-violation pipeline flushes
- Hint the hyper-threading sibling to use more resources

**CPU cost**: One core at ~100% for ~50–100µs. Negligible in practice.
**Precision**: Sub-microsecond (bounded by `Stopwatch` resolution).
**When used**: Final 50–100µs before the target time.

### Why This Order Matters

Phase thresholds are calibrated at startup by measuring actual `Thread.Sleep(1)` cost. This makes the timer self-tuning:

| Platform | Calibrated sleep_threshold | Effective behavior for 1ms period |
|----------|--------------------------|-----------------------------------|
| .NET 8 + Win10 1803+ | ~1.5ms | Skip Sleep → Yield ~950µs → Spin ~50µs |
| Old Windows | ~23ms | Skip Sleep → Yield ~950µs → Spin ~50µs |
| Linux (HZ=1000) | ~1.5ms | Skip Sleep → Yield ~950µs → Spin ~50µs |
| macOS | ~1.5ms | Skip Sleep → Yield ~950µs → Spin ~50µs |

For longer periods (10ms+), Phase 1 kicks in and CPU usage drops to near zero during most of the wait.

### Drift Prevention

The timer advances from the **scheduled** tick time, not the actual completion time:

```csharp
nextTick += _intervalTicks;  // Metronome: fixed reference
// NOT: nextTick = Stopwatch.GetTimestamp() + _intervalTicks;  // Drift: error accumulates
```

This prevents jitter from compounding across ticks. If tick N fires 20µs late, tick N+1 is still scheduled from the ideal time. This is the same technique used by audio engines, GPS clocks, and game loops.

---

## 4. Class Hierarchy

```
                 ┌───────────────────┐
                 │   ResourceNode    │  (from Typhon.Engine.Resources)
                 └────────┬──────────┘
                          │ extends
                    ┌─────┴─────────────────────────────────┐
                    │  HighResolutionTimerServiceBase       │
                    │  (abstract, IMetricSource)            │
                    ├───────────────────────────────────────┤
                    │  Three-phase wait loop                │
                    │  Startup calibration                  │
                    │  Thread lifecycle (lazy start/stop)   │
                    │  Timing metrics (jitter, missed)      │
                    ├───────────────────────────────────────┤
                    │  # GetNextTick() → long               │
                    │  # ExecuteCallbacks(scheduled, actual)│
                    └──────────┬───────────┬────────────────┘
                               │           │
              ┌────────────────┘           └────────────────┐
              │                                             │
┌─────────────┴──────────────────┐     ┌────────────────────┴─────────────────┐
│  HighResolutionTimerService    │     │  HighResolutionSharedTimerService    │
│  (single handler)              │     │  (multi-callback)                    │
├────────────────────────────────┤     ├──────────────────────────────────────┤
│  Fixed interval (ticks)        │     │  Per-registration next-fire tracking │
│  Single Action callback        │     │  Dynamic next-tick computation       │
│  GetNextTick = last + interval │     │  GetNextTick = min(all next-fires)   │
│  ExecuteCallbacks = call it    │     │  ExecuteCallbacks = fire all due     │
│  Own dedicated thread          │     │  Shared thread for all callbacks     │
│  Parent: Timer/Dedicated       │     │  Parent: Timer                       │
└────────────────────────────────┘     └──────────────────────────────────────┘
```

### Resource Tree Position

```
Root
├── Storage
├── DataEngine
├── Durability
├── Allocation
├── Synchronization
└── Timer                                    ← new root node
    ├── SharedTimer (HighResolutionSharedTimerService, ResourceType.Service)
    │   Registrations: DeadlineWatchdog (200Hz), TelemetryFlush (10Hz), ...
    └── Dedicated (ResourceNode, ResourceType.Node)
        └── HeartbeatMonitor (HighResolutionTimerService, ResourceType.Service)
```

### When to Use Which

| Use Case | Class | Why |
|----------|-------|-----|
| Safety-critical periodic check requiring <1ms isolation (heartbeat) | `HighResolutionTimerService` | Own thread, no interference from other handlers |
| Deadline watchdog (5ms latency acceptable) | `HighResolutionSharedTimerService` | 200Hz check interval is sufficient; avoids dedicating a full thread |
| Multiple lightweight periodic tasks | `HighResolutionSharedTimerService` | One thread serves many callbacks, saves resources |
| Low-frequency housekeeping (metrics, cleanup) | `HighResolutionSharedTimerService` | Not worth a dedicated thread |

---

## 5. HighResolutionTimerServiceBase (Abstract)

### 5.1 API Surface

```csharp
/// <summary>
/// Abstract base class for high-resolution periodic timers using a three-phase hybrid
/// wait strategy (Sleep → Yield → Spin) on a dedicated background thread.
/// Self-calibrates across Windows, Linux, and macOS.
/// </summary>
/// <remarks>
/// Extends <see cref="ResourceNode"/> to participate in the resource tree (under the
/// <c>Timer</c> root node). Implements <see cref="IMetricSource"/> to expose timing
/// metrics (tick count, timing error, missed ticks) to the resource graph.
/// <para/>
/// Derived classes provide the scheduling policy via two abstract methods:
/// <see cref="GetNextTick"/> determines when to wake, and
/// <see cref="ExecuteCallbacks"/> determines what to run.
/// The base class owns the wait loop, thread lifecycle, calibration, and timing metrics.
/// </remarks>
public abstract class HighResolutionTimerServiceBase : ResourceNode, IMetricSource
{
    // ═══════════════════════════════════════════════════════════════
    // Abstract methods — scheduling policy
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the <see cref="Stopwatch"/> timestamp of the next tick to wait for.
    /// Called by the timer loop after each callback execution to determine the
    /// next wake-up time.
    /// </summary>
    /// <remarks>
    /// Returning a timestamp in the past causes the wait loop to exit immediately
    /// and call <see cref="ExecuteCallbacks"/> without delay (catch-up behavior).
    /// Derived classes should use metronome-style advancement (nextTick += interval)
    /// rather than computing from current time, to prevent drift accumulation.
    /// </remarks>
    /// <returns>
    /// The <see cref="Stopwatch.GetTimestamp"/> value at which the next tick should fire.
    /// Return <see cref="long.MaxValue"/> to idle indefinitely (e.g., no registrations).
    /// </returns>
    protected abstract long GetNextTick();

    /// <summary>
    /// Execute the callback(s) due at this tick. Called by the timer loop immediately
    /// after the three-phase wait completes.
    /// </summary>
    /// <param name="scheduledTick">
    /// The <see cref="Stopwatch"/> timestamp that was targeted (as returned by <see cref="GetNextTick"/>).
    /// </param>
    /// <param name="actualTick">
    /// The <see cref="Stopwatch"/> timestamp when the wait actually completed.
    /// The difference <c>abs(actualTick - scheduledTick)</c> is the timing error for this tick.
    /// </param>
    protected abstract void ExecuteCallbacks(long scheduledTick, long actualTick);

    // ═══════════════════════════════════════════════════════════════
    // Timing metrics
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Total number of ticks executed since the timer started.</summary>
    public long TickCount { get; }

    /// <summary>
    /// Number of ticks where the actual wake-up time exceeded the scheduled time
    /// by more than one expected interval (indicating the timer fell behind and
    /// had to skip ticks).
    /// </summary>
    public long MissedTicks { get; }

    /// <summary>
    /// Exponential moving average of <c>abs(actualTick - scheduledTick)</c>,
    /// expressed in microseconds. Represents how precisely the timer is hitting
    /// its target wake-up times.
    /// </summary>
    /// <remarks>
    /// Uses an EMA with alpha = 0.01 (roughly a 100-sample window), providing
    /// a responsive yet stable metric. Typical values: 10–50µs on dedicated cores,
    /// 50–200µs on shared cores under moderate load.
    /// </remarks>
    public double MeanTimingErrorUs { get; }

    /// <summary>
    /// Maximum observed timing error (abs(actualTick - scheduledTick)) in microseconds
    /// since the timer started. Useful for worst-case analysis.
    /// </summary>
    public double MaxTimingErrorUs { get; }

    /// <summary>Calibrated <c>Thread.Sleep(1)</c> worst-case duration, measured at startup.</summary>
    public TimeSpan CalibratedSleepResolution { get; }

    /// <summary>Whether the timer thread is currently running.</summary>
    public bool IsRunning { get; }

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the base class and registers in the resource tree.
    /// </summary>
    /// <param name="id">Resource node ID (e.g., "SharedTimer", "HeartbeatMonitor").</param>
    /// <param name="parent">
    /// Parent resource node. For shared timer: <c>registry.Timer</c>.
    /// For dedicated timers: <c>registry.TimerDedicated</c>.
    /// </param>
    protected HighResolutionTimerServiceBase(string id, IResource parent)
        : base(id, ResourceType.Service, parent)
    {
        // Calibrate Sleep(1) once during construction
        _sleepThreshold = CalibrateSleepResolution();
        _spinThreshold = (long)(Stopwatch.Frequency * 0.000_050); // 50µs
    }

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource — exposes timing metrics to the resource graph
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes timing metrics for resource graph snapshots.
    /// </summary>
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("TickCount", _tickCount);
        writer.WriteThroughput("MissedTicks", _missedTicks);
        writer.WriteLatency("MeanTimingErrorUs", _meanTimingErrorUs);
        writer.WriteLatency("MaxTimingErrorUs", _maxTimingErrorUs);
    }

    public void ResetPeaks() => _maxTimingErrorUs = _meanTimingErrorUs;

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Start the timer thread. Idempotent — does nothing if already running.
    /// Can be called by derived classes when their first callback is registered.
    /// </summary>
    protected void Start();

    /// <summary>
    /// Stops the timer thread and disposes managed resources.
    /// Follows the standard <see cref="ResourceNode"/> disposal pattern:
    /// own cleanup first, then <c>base.Dispose(disposing)</c> which
    /// recursively disposes children and clears the children collection.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            _shutdown = true;
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        base.Dispose(disposing);  // ResourceNode: disposes children, clears collection
        IsDisposed = true;
    }
}
```

### 5.2 Startup Calibration

```csharp
/// <summary>
/// Measures actual Thread.Sleep(1) duration to calibrate phase thresholds.
/// Called once during construction. Records worst-case observed duration.
/// </summary>
private static long CalibrateSleepResolution()
{
    const int warmupRounds = 3;
    const int measureRounds = 10;
    long worst = 0;

    // Warm up — let the OS scheduler settle
    for (var i = 0; i < warmupRounds; i++)
    {
        Thread.Sleep(1);
    }

    for (var i = 0; i < measureRounds; i++)
    {
        var before = Stopwatch.GetTimestamp();
        Thread.Sleep(1);
        var elapsed = Stopwatch.GetTimestamp() - before;

        if (elapsed > worst)
        {
            worst = elapsed;
        }
    }

    // Threshold = 1.5x worst observed Sleep(1) duration.
    // Ensures we never Sleep() when it would overshoot the next tick.
    return (long)(worst * 1.5);
}
```

**Why 1.5x**: A safety margin that accounts for occasional OS scheduling jitter. If `Sleep(1)` is observed to take 1.1ms at worst, the threshold becomes ~1.65ms. We only enter the Sleep phase when remaining time exceeds this, guaranteeing we never oversleep past the target.

### 5.3 Timer Loop (Core)

```csharp
private void TimerLoop()
{
    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
    long tickCount = 0;

    while (!_shutdown)
    {
        // ═══════════════════════════════════════════════════════════
        // Ask derived class: when is the next tick?
        // ═══════════════════════════════════════════════════════════
        var nextTick = GetNextTick();

        // long.MaxValue = idle signal (no registrations)
        if (nextTick == long.MaxValue)
        {
            Thread.Sleep(100);
            continue;
        }

        // ═══════════════════════════════════════════════════════════
        // Three-phase wait until next tick
        // ═══════════════════════════════════════════════════════════

        // ── Phase 1: Sleep (CPU-friendly, coarse) ──────────────
        // Only when remaining time exceeds calibrated Sleep(1) duration.
        while (true)
        {
            var remaining = nextTick - Stopwatch.GetTimestamp();
            if (remaining <= _sleepThreshold)
            {
                break;
            }

            Thread.Sleep(1);
        }

        // ── Phase 2: Yield (low CPU, medium precision) ─────────
        // Gives up timeslice but thread stays runnable.
        while (true)
        {
            var remaining = nextTick - Stopwatch.GetTimestamp();
            if (remaining <= _spinThreshold)
            {
                break;
            }

            Thread.Yield();
        }

        // ── Phase 3: Spin (precise, burns CPU briefly) ─────────
        // Final ~50µs. SpinWait(1) emits PAUSE (x86) / YIELD (ARM).
        while (Stopwatch.GetTimestamp() < nextTick)
        {
            Thread.SpinWait(1);
        }

        // ═══════════════════════════════════════════════════════════
        // Record timing error
        // ═══════════════════════════════════════════════════════════
        var actualTick = Stopwatch.GetTimestamp();
        tickCount++;

        var errorTicks = Math.Abs(actualTick - nextTick);
        var errorUs = (double)errorTicks / Stopwatch.Frequency * 1_000_000.0;

        // EMA with alpha = 0.01 (~100-sample smoothing window)
        _meanTimingErrorUs = _meanTimingErrorUs * 0.99 + errorUs * 0.01;

        if (errorUs > _maxTimingErrorUs)
        {
            _maxTimingErrorUs = errorUs;
        }

        _tickCount = tickCount;

        // ═══════════════════════════════════════════════════════════
        // Execute callbacks (derived class policy)
        // ═══════════════════════════════════════════════════════════
        ExecuteCallbacks(nextTick, actualTick);
    }
}
```

### 5.4 Thread Lifecycle

```csharp
private Thread _thread;
private volatile bool _shutdown;

/// <summary>Thread name set by derived classes for diagnostics.</summary>
protected abstract string ThreadName { get; }

protected void Start()
{
    if (_thread != null && _thread.IsAlive)
    {
        return;
    }

    lock (_lifecycleLock)
    {
        if (_thread != null && _thread.IsAlive)
        {
            return;
        }

        _shutdown = false;
        _thread = new Thread(TimerLoop)
        {
            IsBackground = true,  // Won't prevent process exit
            Name = ThreadName
        };
        _thread.Start();
    }
}

// Dispose(bool) is shown in §5.1 — follows ResourceNode pattern:
//   1. Guard against double-dispose via IsDisposed
//   2. Stop thread (managed cleanup, only when disposing=true)
//   3. base.Dispose(disposing) — ResourceNode recursively disposes children
//   4. Set IsDisposed = true
```

### 5.5 Timing Error Metric — Details

The **Mean Timing Error** (`MeanTimingErrorUs`) is defined as:

```
MeanTimingError = EMA( abs(actualTick - scheduledTick) )
```

Where `actualTick` is the `Stopwatch.GetTimestamp()` reading immediately after the spin phase exits, and `scheduledTick` is the target returned by `GetNextTick()`.

| Property | Formula | Purpose |
|----------|---------|---------|
| `MeanTimingErrorUs` | EMA (α=0.01) of `abs(actual - scheduled)` in µs | Steady-state precision indicator |
| `MaxTimingErrorUs` | All-time max of `abs(actual - scheduled)` in µs | Worst-case jitter since startup |
| `MissedTicks` | Counter incremented by derived classes | How often the timer fell behind |

**Why EMA over simple average?** At 1kHz, a simple mean over millions of ticks would be dominated by ancient history. The EMA with α=0.01 (~100-tick window) reflects **recent** behavior, making it useful for real-time monitoring and alerting. If the system becomes loaded and jitter spikes, the metric responds within ~100 ticks (~100ms at 1kHz).

---

## 6. HighResolutionTimerService (Single Handler)

A dedicated timer for a **single callback at a fixed interval**. Each instance runs its own thread. Use this when the handler needs guaranteed isolation (e.g., heartbeat monitor).

### 6.1 API Surface

```csharp
/// <summary>
/// High-resolution timer for a single periodic callback. Runs on its own dedicated
/// thread. Use this for safety-critical or latency-sensitive handlers that must not
/// be affected by other callbacks.
/// </summary>
/// <example>
/// <code>
/// var heartbeat = new HighResolutionTimerService(
///     "HeartbeatMonitor",
///     intervalTicks: Stopwatch.Frequency / 1000,  // 1ms
///     callback: (scheduled, actual) => HeartbeatMonitor.CheckAlive());
/// heartbeat.Start();
/// // ...
/// heartbeat.Dispose();
/// </code>
/// </example>
public sealed class HighResolutionTimerService : HighResolutionTimerServiceBase
{
    /// <summary>
    /// Creates a dedicated high-resolution timer for a single handler.
    /// The thread does not start until <see cref="HighResolutionTimerServiceBase.Start"/> is called.
    /// </summary>
    /// <param name="name">
    /// Human-readable name for diagnostics. Used as the thread name
    /// (prefixed with "Typhon.HRT.") and in telemetry.
    /// </param>
    /// <param name="intervalTicks">
    /// Period between invocations, expressed in <see cref="Stopwatch"/> ticks.
    /// Use <c>Stopwatch.Frequency / 1000</c> for 1ms, <c>Stopwatch.Frequency / 100</c>
    /// for 10ms, etc.
    /// </param>
    /// <param name="callback">
    /// The action to invoke each tick. Receives the scheduled and actual
    /// <see cref="Stopwatch"/> timestamps. Must be fast (target: &lt;100µs).
    /// </param>
    /// <param name="parent">
    /// Parent resource node. Should be <c>registry.TimerDedicated</c> (the "Dedicated"
    /// sub-node under Timer). The timer self-registers via <see cref="ResourceNode"/> constructor.
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public HighResolutionTimerService(
        string name,
        long intervalTicks,
        Action<long, long> callback,
        IResource parent,
        ILogger logger = null);

    /// <summary>Human-readable name for this timer.</summary>
    public string Name { get; }

    /// <summary>Configured interval in <see cref="Stopwatch"/> ticks.</summary>
    public long IntervalTicks { get; }

    /// <summary>Configured interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Number of times the callback has been invoked.</summary>
    public long InvocationCount { get; }

    /// <summary>Duration of the last callback invocation.</summary>
    public TimeSpan LastCallbackDuration { get; }

    /// <summary>Maximum callback invocation duration observed.</summary>
    public TimeSpan MaxCallbackDuration { get; }
}
```

### 6.2 Implementation

```csharp
public sealed class HighResolutionTimerService : HighResolutionTimerServiceBase
{
    private readonly string _name;
    private readonly long _intervalTicks;
    private readonly Action<long, long> _callback;
    private readonly ILogger _logger;

    private long _nextTick;
    private long _invocationCount;
    private long _lastCallbackDurationTicks;
    private long _maxCallbackDurationTicks;

    public HighResolutionTimerService(
        string name,
        long intervalTicks,
        Action<long, long> callback,
        IResource parent,
        ILogger logger = null)
        : base(name, parent)  // ResourceNode self-registers with parent
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalTicks, 0);

        _name = name;
        _intervalTicks = intervalTicks;
        _callback = callback;
        _logger = logger;

        // First tick is one interval from now, set when Start() is called
        _nextTick = long.MaxValue;
    }

    protected override string ThreadName => $"Typhon.HRT.{_name}";

    protected override long GetNextTick()
    {
        // On first call, initialize the metronome anchor
        if (_nextTick == long.MaxValue)
        {
            _nextTick = Stopwatch.GetTimestamp() + _intervalTicks;
        }

        return _nextTick;
    }

    protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
    {
        // Advance metronome (drift-free)
        _nextTick += _intervalTicks;

        // Skip forward if we fell behind
        if (actualTick > _nextTick + _intervalTicks)
        {
            var missed = (actualTick - _nextTick) / _intervalTicks;
            _nextTick += missed * _intervalTicks;
            Interlocked.Add(ref _missedTicks, missed);
        }

        // Execute the single handler
        var callbackStart = Stopwatch.GetTimestamp();
        try
        {
            _callback(scheduledTick, actualTick);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HighResolutionTimer '{Name}' callback threw", _name);
        }

        var elapsed = Stopwatch.GetTimestamp() - callbackStart;
        _lastCallbackDurationTicks = elapsed;

        if (elapsed > _maxCallbackDurationTicks)
        {
            _maxCallbackDurationTicks = elapsed;
        }

        Interlocked.Increment(ref _invocationCount);
    }
}
```

### 6.3 Usage

```csharp
// Dedicated 1ms timer for heartbeat monitoring
// Parent is the "Dedicated" sub-node under Timer in the resource tree
var heartbeatTimer = new HighResolutionTimerService(
    name: "HeartbeatMonitor",
    intervalTicks: Stopwatch.Frequency / 1000,  // 1ms in Stopwatch ticks
    callback: (scheduled, actual) =>
    {
        HeartbeatMonitor.CheckAlive();
    },
    parent: resourceRegistry.TimerDedicated,
    logger: loggerFactory.CreateLogger<HighResolutionTimerService>());

// Thread starts here — timer is already visible in the resource tree
heartbeatTimer.Start();

// Inspect metrics (also available via resource graph)
Console.WriteLine($"Timing error: {heartbeatTimer.MeanTimingErrorUs:F1}µs");
Console.WriteLine($"Invocations: {heartbeatTimer.InvocationCount}");

// Cleanup — removes from resource tree
heartbeatTimer.Dispose();
```

---

## 7. HighResolutionSharedTimerService (Multi-Callback)

Multiplexes **multiple callbacks** on a single thread. Each registration has its own period. The service dynamically computes the nearest next tick across all registrations, waking up only when the soonest callback is due.

### 7.1 API Surface

```csharp
/// <summary>
/// High-resolution shared timer that multiplexes multiple periodic callbacks on a
/// single background thread. Each callback has its own period (in <see cref="Stopwatch"/>
/// ticks). The timer thread wakes up at the nearest next callback, avoiding wasted ticks.
/// </summary>
/// <remarks>
/// Use this for non-critical periodic tasks (telemetry, metrics, cleanup).
/// For safety-critical handlers that need guaranteed isolation, use
/// <see cref="HighResolutionTimerService"/> instead.
/// <para/>
/// Callbacks run sequentially on the timer thread. A slow callback delays
/// all subsequent callbacks in that cycle. Follow the callback contract:
/// target &lt;100µs execution, no blocking calls.
/// </remarks>
public sealed class HighResolutionSharedTimerService : HighResolutionTimerServiceBase
{
    /// <summary>
    /// Creates a shared timer service. The thread starts lazily on first registration.
    /// Self-registers under the <c>Timer</c> root node in the resource tree.
    /// </summary>
    /// <param name="parent">
    /// Parent resource node. Should be <c>registry.Timer</c> (the Timer root node).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public HighResolutionSharedTimerService(IResource parent, ILogger<HighResolutionSharedTimerService> logger = null);

    /// <summary>
    /// Register a periodic callback.
    /// </summary>
    /// <param name="name">Human-readable name for diagnostics and telemetry.</param>
    /// <param name="intervalTicks">
    /// Period between invocations, in <see cref="Stopwatch"/> ticks.
    /// Use <c>Stopwatch.Frequency / 1000</c> for 1ms, etc.
    /// </param>
    /// <param name="callback">
    /// Action to invoke. Receives the scheduled and actual <see cref="Stopwatch"/>
    /// timestamps. Must be fast (target: &lt;100µs).
    /// </param>
    /// <returns>A registration handle. Dispose it to unregister.</returns>
    public ITimerRegistration Register(string name, long intervalTicks, Action<long, long> callback);

    /// <summary>Number of currently active registrations.</summary>
    public int ActiveRegistrations { get; }

    /// <summary>
    /// Read-only snapshot of all active registrations for diagnostics.
    /// </summary>
    public IReadOnlyList<ITimerRegistration> Registrations { get; }
}
```

### 7.2 Registration Handle

```csharp
/// <summary>
/// Handle returned by <see cref="HighResolutionSharedTimerService.Register"/>.
/// Dispose to unregister the callback from the shared timer.
/// </summary>
public interface ITimerRegistration : IDisposable
{
    /// <summary>Human-readable name for this registration.</summary>
    string Name { get; }

    /// <summary>Configured interval in <see cref="Stopwatch"/> ticks.</summary>
    long IntervalTicks { get; }

    /// <summary>Configured interval as a <see cref="TimeSpan"/>.</summary>
    TimeSpan Interval { get; }

    /// <summary>Number of times this callback has been invoked.</summary>
    long InvocationCount { get; }

    /// <summary>Duration of the last callback invocation.</summary>
    TimeSpan LastCallbackDuration { get; }

    /// <summary>Maximum invocation duration observed.</summary>
    TimeSpan MaxCallbackDuration { get; }

    /// <summary>Number of invocations that exceeded 100µs.</summary>
    long SlowInvocationCount { get; }

    /// <summary>Whether this registration is still active (not disposed).</summary>
    bool IsActive { get; }
}
```

### 7.3 Internal Data Structure

```csharp
private sealed class TimerRegistration : ITimerRegistration
{
    public string Name { get; init; }
    public long IntervalTicks { get; init; }
    public Action<long, long> Callback { get; init; }

    // Scheduling state — only accessed from the timer thread
    public long NextFireTimestamp;

    // Per-callback metrics
    public long InvocationCount;
    public long LastCallbackDurationTicks;
    public long MaxCallbackDurationTicks;
    public long SlowInvocationCount;
    public volatile bool IsActive;

    TimeSpan ITimerRegistration.Interval =>
        TimeSpan.FromSeconds((double)IntervalTicks / Stopwatch.Frequency);
    TimeSpan ITimerRegistration.LastCallbackDuration =>
        TimeSpan.FromSeconds((double)LastCallbackDurationTicks / Stopwatch.Frequency);
    TimeSpan ITimerRegistration.MaxCallbackDuration =>
        TimeSpan.FromSeconds((double)MaxCallbackDurationTicks / Stopwatch.Frequency);
    long ITimerRegistration.InvocationCount => InvocationCount;
    long ITimerRegistration.SlowInvocationCount => SlowInvocationCount;

    public void Dispose() => IsActive = false;
}

// Copy-on-write array for lock-free iteration on the timer thread.
// Mutations (Register/Dispose) take _registrationLock and swap the array.
private TimerRegistration[] _registrations = [];
private readonly object _registrationLock = new();
```

**Why copy-on-write array?** The timer thread iterates registrations on every tick. A contiguous array gives optimal cache locality. Registration/unregistration is rare (startup/shutdown), so taking a lock to build a new array is acceptable. The timer thread reads the array reference via plain field access — on x64, reference reads are naturally atomic and the writer's `lock` provides the necessary memory fence.

### 7.4 GetNextTick — Dynamic Nearest-Callback Computation

```csharp
protected override long GetNextTick()
{
    var registrations = _registrations;

    if (registrations.Length == 0)
    {
        return long.MaxValue;  // Idle signal → base class sleeps 100ms
    }

    var nearest = long.MaxValue;
    var hasInactive = false;

    for (var i = 0; i < registrations.Length; i++)
    {
        var reg = registrations[i];

        if (!reg.IsActive)
        {
            hasInactive = true;
            continue;
        }

        if (reg.NextFireTimestamp < nearest)
        {
            nearest = reg.NextFireTimestamp;
        }
    }

    // Lazy cleanup of disposed registrations
    if (hasInactive)
    {
        CleanupInactiveRegistrations();
    }

    return nearest;
}
```

### 7.5 ExecuteCallbacks — Fire All Due Callbacks

```csharp
private static readonly long SlowThresholdTicks =
    (long)(Stopwatch.Frequency * 0.000_100);  // 100µs

protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
{
    var registrations = _registrations;

    for (var i = 0; i < registrations.Length; i++)
    {
        var reg = registrations[i];

        if (!reg.IsActive)
        {
            continue;
        }

        // Fire if this registration is due (its next-fire <= the scheduled tick)
        if (reg.NextFireTimestamp > scheduledTick)
        {
            continue;
        }

        // Advance metronome for this registration (drift-free)
        reg.NextFireTimestamp += reg.IntervalTicks;

        // If we fell behind, skip forward rather than burst-firing
        if (actualTick > reg.NextFireTimestamp + reg.IntervalTicks)
        {
            var missed = (actualTick - reg.NextFireTimestamp) / reg.IntervalTicks;
            reg.NextFireTimestamp += missed * reg.IntervalTicks;
            Interlocked.Add(ref _missedTicks, missed);
        }

        // Execute the callback
        var callbackStart = Stopwatch.GetTimestamp();
        try
        {
            reg.Callback(scheduledTick, actualTick);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "SharedTimer callback '{Name}' threw", reg.Name);
        }

        var elapsed = Stopwatch.GetTimestamp() - callbackStart;
        reg.LastCallbackDurationTicks = elapsed;

        if (elapsed > reg.MaxCallbackDurationTicks)
        {
            reg.MaxCallbackDurationTicks = elapsed;
        }

        Interlocked.Increment(ref reg.InvocationCount);

        if (elapsed > SlowThresholdTicks)
        {
            Interlocked.Increment(ref reg.SlowInvocationCount);
        }
    }
}
```

### 7.6 Registration / Unregistration

```csharp
public ITimerRegistration Register(string name, long intervalTicks, Action<long, long> callback)
{
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(callback);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalTicks, 0L);

    var now = Stopwatch.GetTimestamp();
    var reg = new TimerRegistration
    {
        Name = name,
        IntervalTicks = intervalTicks,
        Callback = callback,
        NextFireTimestamp = now + intervalTicks,  // First fire one interval from now
        IsActive = true
    };

    lock (_registrationLock)
    {
        var current = _registrations;
        var next = new TimerRegistration[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[current.Length] = reg;
        _registrations = next;
    }

    Start();  // Lazy thread start (idempotent)

    _logger?.LogDebug(
        "Registered shared timer callback '{Name}' every {Interval}ms",
        name, (double)intervalTicks / Stopwatch.Frequency * 1000.0);

    return reg;
}

private void CleanupInactiveRegistrations()
{
    lock (_registrationLock)
    {
        var current = _registrations;
        var active = current.Where(r => r.IsActive).ToArray();  // Allocation OK: rare path

        if (active.Length != current.Length)
        {
            _registrations = active;
        }
    }
}

protected override string ThreadName => "Typhon.HRT.Shared";
```

### 7.7 Callback Contract

Callbacks registered with the shared timer **must** follow these rules:

| Rule | Rationale |
|------|-----------|
| Target < 100µs execution | Longer callbacks delay the next tick and affect other registrations |
| No blocking calls | `Thread.Sleep`, `Monitor.Wait`, I/O will stall the timer thread |
| No exceptions (best effort) | Exceptions are caught and logged, but indicate a bug |
| Thread-safe | Callback runs on the timer thread, not the registering thread |

If a subsystem needs to do > 100µs of work periodically, it should use the timer callback to **signal** a separate worker (e.g., set a flag, pulse a `ManualResetEventSlim`) rather than doing the work inline.

### 7.8 Usage Examples

```csharp
// --- DI registration (with resource tree parent) ---
services.AddSingleton(sp =>
    new HighResolutionSharedTimerService(
        parent: sp.GetRequiredService<IResourceRegistry>().Timer,
        logger: sp.GetService<ILogger<HighResolutionSharedTimerService>>()));

// --- Registering callbacks ---
var shared = serviceProvider.GetRequiredService<HighResolutionSharedTimerService>();

// DeadlineWatchdog: 200Hz (5ms) — scans priority queue for expired deadlines
DeadlineWatchdog.Initialize(shared);
// (lazy registration on first Register() call)

// Telemetry flush: every 100ms
var telemetryReg = shared.Register(
    "TelemetryFlush",
    intervalTicks: Stopwatch.Frequency / 10,  // 100ms
    callback: (scheduled, actual) =>
    {
        TelemetryManager.Flush();
    });

// Shell status: every 200ms
var shellReg = shared.Register(
    "ShellStatus",
    intervalTicks: Stopwatch.Frequency / 5,   // 200ms
    callback: (scheduled, actual) =>
    {
        ShellStatusLine.Refresh();
    });

// Epoch advancement: every 10ms
var epochReg = shared.Register(
    "EpochAdvance",
    intervalTicks: Stopwatch.Frequency / 100,  // 10ms
    callback: (scheduled, actual) =>
    {
        EpochManager.TryAdvance();
    });

// --- Metrics ---
Console.WriteLine($"Mean timing error: {shared.MeanTimingErrorUs:F1}µs");
Console.WriteLine($"Active: {shared.ActiveRegistrations}");

// --- Unregistering ---
telemetryReg.Dispose();  // Removed from the shared timer loop
```

### 7.9 Dynamic Next-Tick Behavior

Unlike a fixed-tick model where the thread wakes every N ms regardless, the shared timer computes the **minimum across all registration next-fire times**. This means the effective tick rate adapts to the registered callbacks:

```
Registrations:
  DeadlineWatchdog: every   5ms, next fire at T+5
  EpochAdvance:    every  10ms, next fire at T+10
  TelemetryFlush:  every 100ms, next fire at T+100
  ShellStatus:     every 200ms, next fire at T+200

Timeline:
  T+5    → wake, fire DeadlineWatchdog,  next: min(T+10, T+10, T+100, T+200) = T+10
  T+10   → wake, fire DeadlineWatchdog + EpochAdvance, next: T+15
  T+15   → wake, fire DeadlineWatchdog,  next: T+20
  T+20   → wake, fire DeadlineWatchdog + EpochAdvance, next: T+25
  ...
  T+100  → wake, fire DeadlineWatchdog + EpochAdvance + TelemetryFlush, next: T+105
  ...
  T+200  → wake, fire all four, next: T+205
```

With the DeadlineWatchdog at 200Hz, the effective base tick rate is 5ms. The 5ms interval is long enough for the shared timer's Sleep phase to engage on .NET 8+ (where `Thread.Sleep(1)` resolves in ~1ms), keeping CPU usage near zero.

When all callbacks are removed, `GetNextTick()` returns `long.MaxValue` and the base class enters its idle sleep (100ms polling).

---

## 8. Resource Tree Integration

All timer instances participate in the Typhon resource tree via `ResourceNode` inheritance. This enables centralized monitoring, resource graph snapshots, and Shell diagnostics.

### 8.1 New Root Node: Timer

The `ResourceRegistry` is extended with a new `Timer` root node and a `Dedicated` sub-node:

```csharp
// Additions to ResourceSubsystem enum
public enum ResourceSubsystem
{
    Storage,
    DataEngine,
    Durability,
    Allocation,
    Synchronization,
    Timer              // ← new
}

// Additions to IResourceRegistry
public interface IResourceRegistry : IDisposable
{
    // ...existing properties...
    IResource Timer { get; }           // ← new: Timer root node
    IResource TimerDedicated { get; }  // ← new: Timer/Dedicated sub-node
}
```

```csharp
// In ResourceRegistry constructor
public ResourceRegistry(ResourceRegistryOptions options)
{
    Root = ResourceNode.CreateRoot(this);

    // ...existing subsystem nodes...
    Storage = new ResourceNode("Storage", ResourceType.Node, Root);
    DataEngine = new ResourceNode("DataEngine", ResourceType.Node, Root);
    Durability = new ResourceNode("Durability", ResourceType.Node, Root);
    Allocation = new ResourceNode("Allocation", ResourceType.Node, Root);
    Synchronization = new ResourceNode("Synchronization", ResourceType.Node, Root);

    // Timer subsystem
    Timer = new ResourceNode("Timer", ResourceType.Node, Root);
    TimerDedicated = new ResourceNode("Dedicated", ResourceType.Node, Timer);
}
```

### 8.2 Tree Structure at Runtime

After `DatabaseEngine` initializes its timers:

```
Root
├── Storage
│   └── ...
├── DataEngine
│   └── ...
├── Durability
├── Allocation
│   └── ...
├── Synchronization
│   └── ...
└── Timer
    ├── SharedTimer (Service, IMetricSource)         ← HighResolutionSharedTimerService
    │   [TickCount: 52,600 | MeanError: 18.7µs | MaxError: 142.3µs | Missed: 0]
    │   Registrations:
    │     DeadlineWatchdog   5ms (200Hz)  InvocationCount: 52,600
    │     TelemetryFlush     100ms        InvocationCount: 2,630
    │     EpochAdvance       10ms         InvocationCount: 26,300
    └── Dedicated (Node)
        └── HeartbeatMonitor (Service, IMetricSource) ← HighResolutionTimerService
            [TickCount: 263,102 | MeanError: 12.3µs | MaxError: 87.1µs | Missed: 0]
```

### 8.3 Self-Registration Pattern

Following the established Typhon pattern, `ResourceNode` constructors auto-register with their parent:

```csharp
// HighResolutionSharedTimerService — registers under Timer
var shared = new HighResolutionSharedTimerService(
    parent: registry.Timer,    // → appears as Timer/SharedTimer
    logger: logger);

// DeadlineWatchdog — registers as a shared timer callback (not a ResourceNode itself)
DeadlineWatchdog.Initialize(shared);
// On first Register() call, creates a 200Hz registration named "DeadlineWatchdog"

// HighResolutionTimerService — registers under Timer/Dedicated
var heartbeat = new HighResolutionTimerService(
    name: "HeartbeatMonitor",
    intervalTicks: Stopwatch.Frequency / 1000,
    callback: (s, a) => HeartbeatMonitor.CheckAlive(),
    parent: registry.TimerDedicated,  // → appears as Timer/Dedicated/HeartbeatMonitor
    logger: logger);
```

On `Dispose()`, the timer calls `base.Dispose()` which removes it from the parent's children — the resource tree stays clean.

### 8.4 IMetricSource Integration

The base class implements `IMetricSource`, so all timers automatically participate in resource graph snapshots:

```csharp
// In HighResolutionTimerServiceBase
public void ReadMetrics(IMetricWriter writer)
{
    writer.WriteThroughput("TickCount", _tickCount);
    writer.WriteThroughput("MissedTicks", _missedTicks);
    writer.WriteLatency("MeanTimingErrorUs", _meanTimingErrorUs);
    writer.WriteLatency("MaxTimingErrorUs", _maxTimingErrorUs);
}

public void ResetPeaks() => _maxTimingErrorUs = _meanTimingErrorUs;
```

The resource graph snapshot captures these metrics alongside all other Typhon subsystems — storage, memory, synchronization — in a single tree traversal. No special plumbing needed.

### 8.5 FindByPath Access

Existing `ResourceExtensions.FindByPath` works naturally:

```csharp
// Navigate to a specific timer
var heartbeat = registry.FindByPath("Timer/Dedicated/HeartbeatMonitor");
var shared = registry.FindByPath("Timer/SharedTimer");

// Enumerate all dedicated timers
var dedicated = registry.TimerDedicated.Children;
```

---

## 9. Observability

### 9.1 Base Class Metrics (all timers)

| Metric | Type | Source | Description |
|--------|------|--------|-------------|
| `typhon.hrt.tick_count` | Counter | Base | Total ticks executed |
| `typhon.hrt.missed_ticks` | Counter | Base | Ticks skipped due to overrun |
| `typhon.hrt.mean_timing_error_us` | Gauge | Base | EMA of abs(actual - scheduled) in µs |
| `typhon.hrt.max_timing_error_us` | Gauge | Base | Worst-case timing error since start |
| `typhon.hrt.calibrated_sleep_ms` | Gauge | Base | Measured Sleep(1) worst-case duration |

### 9.2 Shared Timer Metrics (additional)

| Metric | Type | Source | Description |
|--------|------|--------|-------------|
| `typhon.hrt.shared.active_registrations` | Gauge | Shared | Number of active callbacks |
| `typhon.hrt.shared.callback_duration_us` | Histogram | Shared | Per-callback invocation duration |
| `typhon.hrt.shared.slow_invocations` | Counter | Shared | Callbacks exceeding 100µs |

### 9.3 Shell Integration

```
tsh> timer status
High-Resolution Timers
══════════════════════════════════════════════════════════════════════

Dedicated Timers:
  Name                Thread               Interval  Ticks     Error(avg)  Error(max)
  ──────────────────  ───────────────────  ────────  ────────  ──────────  ──────────
  HeartbeatMonitor    Typhon.HRT.Heartbt   1.0ms     263,102   12.3µs      87.1µs

Shared Timer:  (Typhon.HRT.Shared)
  Timing: 52,600 ticks | Mean error: 18.7µs | Max error: 142.3µs | Missed: 0
  Calibrated Sleep(1): 1.12ms

  Name                 Interval  Invocations  Last      Max       Slow
  ───────────────────  ────────  ───────────  ────────  ────────  ────
  DeadlineWatchdog     5ms       52,600       0.3µs     8.7µs     0
  EpochAdvance         10ms      26,300       0.8µs     14.2µs    0
  TelemetryFlush       100ms     2,630        42.1µs    187.3µs   3
  ShellStatus          200ms     1,315        1.2µs     3.1µs     0
```

---

## 10. Integration Points

### 10.1 DeadlineWatchdog — Shared Timer Registration

The `DeadlineWatchdog` registers a 200Hz (5ms) callback on the `HighResolutionSharedTimerService`. This provides <5ms cancellation latency with near-zero CPU cost, without requiring a dedicated thread.

```csharp
// In DatabaseEngine initialization
_sharedTimer = serviceProvider.GetRequiredService<HighResolutionSharedTimerService>();
DeadlineWatchdog.Initialize(_sharedTimer);
// Timer registration happens lazily on first Register() call at 200Hz (5ms)
```

**Why shared, not dedicated?** The watchdog callback (scan a priority queue for expired deadlines, call `CTS.Cancel()`) typically completes in <10µs — well within the <100µs shared timer contract. A 5ms check interval provides sufficient cancellation latency for UoW deadlines (typically 100ms–30s). Using the shared timer saves a dedicated thread compared to `HighResolutionTimerService`.

**Impact vs. original design**: The original `DeadlineWatchdog` design used `Monitor.Wait` with a `MaxWakeInterval` of 100ms. With the shared timer at 200Hz, cancellation latency drops from **0–100ms** to **0–5ms**. The watchdog no longer needs any thread management — the shared timer handles it.

### 10.2 Shared Timer Registrations

The shared timer serves all periodic callbacks — both the deadline watchdog and housekeeping tasks:

```csharp
// In DatabaseEngine initialization
_sharedTimer = serviceProvider.GetRequiredService<HighResolutionSharedTimerService>();

// DeadlineWatchdog: 200Hz (5ms) — priority queue scan
DeadlineWatchdog.Initialize(_sharedTimer);
// (lazy registration on first Register() call)

// Housekeeping tasks
_epochReg = _sharedTimer.Register("EpochAdvance",
    Stopwatch.Frequency / 100,   // 10ms
    (s, a) => _epochManager.TryAdvance());

_telemetryReg = _sharedTimer.Register("TelemetryFlush",
    Stopwatch.Frequency / 10,    // 100ms
    (s, a) => _telemetry.Flush());
```

The shared timer dynamically computes the nearest next-fire time. With the DeadlineWatchdog at 200Hz, the effective tick rate is 200Hz (5ms) when deadlines are active. When no deadlines are registered, the watchdog callback still runs but returns immediately (empty queue scan).

### 10.3 DI Registration

```csharp
public static IServiceCollection AddTyphonEngine(this IServiceCollection services)
{
    // Shared timer — singleton, one thread for all housekeeping callbacks
    // Self-registers under the Timer root node in the resource tree
    services.AddSingleton(sp =>
        new HighResolutionSharedTimerService(
            parent: sp.GetRequiredService<IResourceRegistry>().Timer,
            logger: sp.GetService<ILogger<HighResolutionSharedTimerService>>()));

    // Dedicated timers are not registered via DI — they're created and owned
    // by the subsystem that needs them (e.g., DatabaseEngine creates the
    // watchdog timer). They self-register under Timer/Dedicated via the
    // ResourceNode constructor.

    return services;
}
```

### 10.4 DatabaseEngine Lifecycle

```csharp
// In DatabaseEngine constructor or initialization
_sharedTimer = serviceProvider.GetRequiredService<HighResolutionSharedTimerService>();
DeadlineWatchdog.Initialize(_sharedTimer);  // Lazy 200Hz registration on first use

// In DatabaseEngine.DisposeResources()
DeadlineWatchdog.Shutdown();  // Disposes timer registration + cancels remaining CTS
_sharedTimer.Dispose();       // Stops thread + removes from Timer
```

---

## 11. Performance Characteristics

### CPU Cost

| Scenario | Effective tick rate | CPU usage | Notes |
|----------|-------------------|-----------|-------|
| **Dedicated timer, 1ms** | 1kHz | ~1 logical core | Yield+Spin only; Sleep phase skipped |
| **Dedicated timer, 5ms** | 200Hz | ~0.5% of one core | Sleep phase kicks in (.NET 8+); same rate as DeadlineWatchdog on shared timer |
| **Dedicated timer, 10ms** | 100Hz | ~0.05% of one core | Mostly sleeping |
| **Shared, fastest = 10ms** | 100Hz | ~0.05% of one core | Wakes only for nearest callback |
| **Shared, fastest = 100ms** | 10Hz | ~0.005% of one core | Very low |
| **Any timer, no registrations** | — | ~0% | 100ms idle sleep |

### Timing Accuracy

| Effective interval | Expected timing error | Notes |
|-------------------|----------------------|-------|
| 1ms | 10–50µs | Yield+Spin; depends on system load |
| 5ms | 10–50µs | Sleep absorbs bulk; spin provides precision |
| 10ms+ | 10–50µs | Dominated by spin phase precision |

Timing error is dominated by the Phase 2→3 boundary (OS re-scheduling delay after `Thread.Yield()`) and is largely independent of the interval length.

### Memory

| Item | Cost |
|------|------|
| Base class instance | ~100B (metrics fields, thresholds, lock) |
| Per `HighResolutionTimerService` | ~200B (base + name + interval + callback) |
| `HighResolutionSharedTimerService` | ~250B (base + array ref + lock) |
| Per shared registration | ~120B (TimerRegistration object + delegate) |
| Thread stack (per timer instance) | 1MB (default .NET thread stack) |

---

## 12. Testing Strategy

### Unit Tests (Base + Single)

| Test | What it verifies |
|------|------------------|
| `Start_CreatesBackgroundThread` | Thread is `IsBackground`, `AboveNormal` priority |
| `Dispose_JoinsThread` | Thread stops within 2s |
| `IntervalTicks_Zero_Throws` | Argument validation |
| `Callback_Null_Throws` | Argument validation |
| `IsRunning_ReflectsState` | False before Start, true during, false after Dispose |

### Unit Tests (Shared)

| Test | What it verifies |
|------|------------------|
| `Register_ReturnsActiveHandle` | Registration creates active handle |
| `Dispose_DeactivatesHandle` | Disposing handle sets `IsActive = false` |
| `NoRegistrations_ThreadIdles` | GetNextTick returns `long.MaxValue` |
| `Register_StartsThread` | First registration lazily starts the thread |
| `AllDisposed_ThreadIdles` | Removing all registrations → idle polling |

### Timing Tests (`[Category("Timing")]`)

| Test | Setup | Assertion |
|------|-------|-----------|
| `Single_FiresAtExpectedRate` | 1ms interval, run 100ms | InvocationCount is 80–120 (±20%) |
| `Shared_MultipleRates` | 10ms + 100ms registrations, run 500ms | Count ratio ~10:1 |
| `MeanTimingError_WithinBounds` | 1ms interval, 1000 ticks | `MeanTimingErrorUs < 200` |
| `MaxTimingError_Bounded` | 1ms interval, 1000 ticks | `MaxTimingErrorUs < 5000` (5ms) |
| `Calibration_Reasonable` | Read CalibratedSleepResolution | Between 0.5ms and 20ms |
| `Shared_DynamicNextTick` | 10ms + 100ms registrations | Timer wakes at 10ms intervals |

### Stress Tests

| Test | Setup | Assertion |
|------|-------|-----------|
| `Shared_ConcurrentRegistration` | 10 threads registering/disposing | No crashes, no leaks |
| `Shared_RapidRegisterDispose` | 1000 register + dispose cycles | Array size stays bounded |
| `Single_LongRunning` | 10s at 1ms interval | No memory growth, missed ticks bounded |
| `Shared_LongRunning` | 10s with 5 registrations | No memory growth |

---

## 13. Design Decisions

| ID | Decision | Alternatives Considered | Rationale |
|----|----------|------------------------|-----------|
| D1 | Abstract base + two derived classes | Single class with optional multi-callback | Separation of concerns: base owns timing, derived own scheduling policy. Single-handler avoids shared-state complexity for critical paths. |
| D2 | `GetNextTick()` / `ExecuteCallbacks()` as the two abstract methods | `OnTick()` single method, `ITimerPolicy` strategy | Two methods cleanly separate "when to wake" from "what to do". Base class can record timing error between the two calls. |
| D3 | Intervals in `Stopwatch` ticks (not `TimeSpan`) | `TimeSpan`, milliseconds | Maximum precision. Avoids floating-point conversion on every tick. `Stopwatch.Frequency / 1000` is idiomatic for "1ms". |
| D4 | `HighResolutionTimerService` = dedicated thread per instance | Share thread with shared service | Critical handlers needing <1ms latency (heartbeat) need isolation. Watchdog uses shared timer at 200Hz since 5ms latency is acceptable. |
| D5 | `HighResolutionSharedTimerService` computes nearest next-fire dynamically | Fixed base tick with modulo divisors | No wasted ticks: if fastest callback is 100ms, thread sleeps 100ms, not 1ms. More CPU-efficient for heterogeneous periods. |
| D6 | Copy-on-write array for shared registrations | `ConcurrentDictionary`, linked list | Optimal cache locality for iteration. Mutations are rare (startup/shutdown). |
| D7 | Mean Timing Error as EMA (α=0.01) | Simple average, sliding window | EMA responds to recent changes within ~100 ticks. Simple average over millions of ticks is dominated by history. No buffer allocation needed (unlike sliding window). |
| D8 | Calibrate `Sleep(1)` at startup | Hardcoded thresholds per platform | Self-tuning across .NET versions, OS versions, VM/container configs |
| D9 | `ThreadPriority.AboveNormal` | Normal, Highest | Timely ticks without starving application threads |
| D10 | Log + continue on callback exception | Propagate / remove registration | One bad callback must not kill the timer |
| D11 | Dedicated timers not in DI; shared timer as DI singleton | All timers in DI | Dedicated timers are owned by their subsystem (explicit lifetime). Shared timer is infrastructure (DI singleton). |
| D12 | Extend `ResourceNode` + implement `IMetricSource` | Separate resource registration | Self-registering constructor follows established Typhon pattern (`MemoryAllocator`, `PagedMMF`). `IMetricSource` exposes timing metrics to resource graph snapshots without extra plumbing. |
| D13 | New `Timer` root node with `Dedicated` sub-node | Flat under existing subsystem | Timers are cross-cutting (not Storage, not DataEngine). Dedicated sub-node groups isolated timers separately from the shared timer. |

---

## 14. Files to Create/Modify

### New Files

| File | Contents |
|------|----------|
| `src/Typhon.Engine/Concurrency/Timer/HighResolutionTimerServiceBase.cs` | Abstract base: three-phase wait loop, calibration, metrics, thread lifecycle |
| `src/Typhon.Engine/Concurrency/Timer/HighResolutionTimerService.cs` | Single-handler derived class |
| `src/Typhon.Engine/Concurrency/Timer/HighResolutionSharedTimerService.cs` | Multi-callback derived class |
| `src/Typhon.Engine/Concurrency/Timer/ITimerRegistration.cs` | Registration handle interface (shared timer) |
| `test/Typhon.Engine.Tests/Concurrency/HighResolutionTimerServiceBaseTests.cs` | Base class tests (via single-handler concrete) |
| `test/Typhon.Engine.Tests/Concurrency/HighResolutionTimerServiceTests.cs` | Single-handler tests |
| `test/Typhon.Engine.Tests/Concurrency/HighResolutionSharedTimerServiceTests.cs` | Multi-callback tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Typhon.Engine/Resources/ResourceSubsystem.cs` | Add `Timer` enum value |
| `src/Typhon.Engine/Resources/IResourceRegistry.cs` | Add `Timer` and `TimerDedicated` properties |
| `src/Typhon.Engine/Resources/ResourceRegistry.cs` | Create `Timer` and `Timer/Dedicated` nodes in constructor |
| `src/Typhon.Engine/Hosting/TyphonServiceCollectionExtensions.cs` | Register `HighResolutionSharedTimerService` as singleton (with `IResourceRegistry.Timer` parent) |
| `src/Typhon.Engine/Data/DatabaseEngine.cs` | Initialize `DeadlineWatchdog` with shared timer; dispose via `DeadlineWatchdog.Shutdown()` + `_sharedTimer.Dispose()` |

### Future Modifications (not in initial PR)

| File | Change |
|------|--------|
| `src/Typhon.Engine/Concurrency/DeadlineWatchdog.cs` | Uses `HighResolutionSharedTimerService` registration at 200Hz (see [02-deadline-watchdog.md](./execution/02-deadline-watchdog.md)) |

---

## 15. Future Extensions

These are explicitly **out of scope** for the initial implementation but noted for future consideration:

| Extension | Description |
|-----------|-------------|
| **Priority lanes (shared)** | Separate high/low priority callback arrays within the shared timer |
| **Async callbacks** | `Func<long, long, ValueTask>` overload for callbacks needing async I/O |
| **Timing error alerting** | Callback or log when `MeanTimingErrorUs` exceeds a threshold |
| **Per-callback timing error** | Track timing error per registration (not just per timer) |
| **OTel histogram integration** | Emit `MeanTimingErrorUs` and callback durations as OTel histograms |
| **Shell `timer` command** | `tsh> timer status` displaying the table from §8.3 |
| **Callback timeout** | Auto-disable callbacks that exceed a duration threshold N times |

---

## References

- [DeadlineWatchdog Design](./execution/02-deadline-watchdog.md) — Uses `HighResolutionSharedTimerService` at 200Hz
- [Resources Overview](../overview/08-resources.md) — Resource tree, `IMetricSource`, resource graph snapshots
- [Concurrency Overview](../overview/01-concurrency.md) — `AccessControl`, `AdaptiveWaiter`, thread-safety patterns
- [ADR-018: Adaptive Spin-Wait](../adr/018-adaptive-spin-wait.md) — Related spin-wait calibration pattern
- [.NET PeriodicTimer source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/PeriodicTimer.cs) — Platform timer internals
- [CREATE_WAITABLE_TIMER_HIGH_RESOLUTION](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) — Windows high-res timer flag used by .NET 8+

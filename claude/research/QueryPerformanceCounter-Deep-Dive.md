# QueryPerformanceCounter: A Deep Dive

A comprehensive reference for understanding high-resolution timing in .NET across Windows, Linux, and macOS.

---

## Table of Contents

1. [Understanding Time: Concepts and Terminology](#understanding-time-concepts-and-terminology)
2. [What is QueryPerformanceCounter?](#what-is-queryperformancecounter)
3. [Hardware Timer Sources](#hardware-timer-sources)
4. [How Windows Selects a Timer Source](#how-windows-selects-a-timer-source)
5. [CPU Clock Speed Variations](#cpu-clock-speed-variations)
6. [Cross-Platform Behavior](#cross-platform-behavior)
7. [Virtualization Challenges](#virtualization-challenges)
8. [.NET Implementation Details](#net-implementation-details)
9. [What NOT to Use QPC For](#what-not-to-use-qpc-for)
10. [Common Pitfalls and Gotchas](#common-pitfalls-and-gotchas)
11. [Best Practices](#best-practices)
12. [Code Examples](#code-examples)

---

## Understanding Time: Concepts and Terminology

Before diving into QPC specifics, it's essential to understand the different "kinds" of time available in computing and when to use each.

### The Different Types of Time

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Types of Time in Computing                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                        WALL-CLOCK TIME                              │    │
│  │  "What time is it right now?"                                       │    │
│  │  • Human-readable calendar time                                     │    │
│  │  • Affected by time zones, DST, leap seconds                        │    │
│  │  • Can jump forward/backward (NTP sync, manual changes)             │    │
│  │  • .NET: DateTime, DateTimeOffset                                   │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                       MONOTONIC TIME                                │    │
│  │  "How much time has passed since X?"                                │    │
│  │  • Always moves forward, never jumps backward                       │    │
│  │  • Not tied to calendar time                                        │    │
│  │  • Unaffected by NTP adjustments or manual clock changes            │    │
│  │  • .NET: Stopwatch, QueryPerformanceCounter                         │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                         CPU TIME                                    │    │
│  │  "How much CPU did my process actually use?"                        │    │
│  │  • Measures computation, not elapsed wall time                      │    │
│  │  • Excludes time spent waiting (I/O, sleep, preempted)              │    │
│  │  • .NET: Process.TotalProcessorTime, Thread time APIs               │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                        CYCLE COUNT                                  │    │
│  │  "How many CPU cycles did this take?"                               │    │
│  │  • Raw hardware counter                                             │    │
│  │  • Varies with CPU frequency (turbo, power saving)                  │    │
│  │  • Platform-specific: RDTSC, performance counters                   │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### .NET Time APIs Comparison

| API | Type of Time | Resolution | Use Case |
|-----|--------------|------------|----------|
| `DateTime.Now` | Wall-clock (local) | ~15.6 ms | Display to users, local scheduling |
| `DateTime.UtcNow` | Wall-clock (UTC) | ~15.6 ms | Logging, storage, cross-timezone |
| `DateTimeOffset.Now` | Wall-clock + offset | ~15.6 ms | Preserve timezone context |
| `DateTimeOffset.UtcNow` | Wall-clock (UTC) | ~15.6 ms | Timestamps for APIs, databases |
| `Stopwatch` | Monotonic | ~100 ns | Measuring elapsed time |
| `Stopwatch.GetTimestamp()` | Monotonic | ~100 ns | Low-overhead timing |
| `Environment.TickCount` | Monotonic (32-bit) | ~15.6 ms | Coarse timing, wraps at ~25 days |
| `Environment.TickCount64` | Monotonic (64-bit) | ~15.6 ms | Coarse timing, no wrap |
| `Process.TotalProcessorTime` | CPU time | ~100 ns | Profiling CPU usage |
| `TimeProvider.GetTimestamp()` | Monotonic | ~100 ns | Abstraction for testing |

### Wall-Clock Time Deep Dive

**Wall-clock time** answers "what time is it?" — the same time you'd see on a wall clock or your phone.

```csharp
// Wall-clock time in .NET
DateTime localNow = DateTime.Now;           // Local timezone
DateTime utcNow = DateTime.UtcNow;          // UTC (no timezone)
DateTimeOffset offsetNow = DateTimeOffset.Now;  // Local + offset info

// Key characteristics:
// ✓ Human-meaningful (2025-01-19 14:30:00)
// ✓ Comparable across machines (when using UTC)
// ✓ Persistable to databases
// ✗ Can jump backward (NTP sync)
// ✗ Can jump forward (NTP sync, DST)
// ✗ Low resolution (~15.6ms on Windows)
```

**When to use wall-clock time:**
- Logging events with human-readable timestamps
- Scheduling tasks at specific times (e.g., "run at 3 PM")
- Storing creation/modification dates
- Expiration times (tokens, caches)
- Cross-system event correlation

**Wall-clock gotchas:**

```
┌─────────────────────────────────────────────────────────────────┐
│              Wall-Clock Time Can Jump!                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Scenario: NTP synchronization adjusts system clock             │
│                                                                 │
│  Before NTP sync:  14:30:00.000                                 │
│  Your code:        start = DateTime.UtcNow                      │
│                    DoWork() // takes 100ms                      │
│  NTP syncs:        Clock jumps back 5 seconds!                  │
│  Your code:        end = DateTime.UtcNow                        │
│                                                                 │
│  Result:           end - start = -4.9 seconds (NEGATIVE!)       │
│                                                                 │
│  This is why you NEVER use DateTime for measuring durations!    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Monotonic Time Deep Dive

**Monotonic time** answers "how much time has elapsed?" — it only moves forward.

```csharp
// Monotonic time in .NET
long timestamp = Stopwatch.GetTimestamp();
// ... do work ...
TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);

// Key characteristics:
// ✓ Never goes backward
// ✓ High resolution (sub-microsecond)
// ✓ Unaffected by clock adjustments
// ✗ Not human-meaningful (just a number)
// ✗ Not comparable across machines
// ✗ Resets on reboot
```

**When to use monotonic time:**
- Measuring elapsed time / durations
- Performance profiling
- Rate limiting
- Timeouts and deadlines
- Animation and game loops

### CPU Time Deep Dive

**CPU time** measures actual processor usage, excluding wait time.

```csharp
// CPU time in .NET
var process = Process.GetCurrentProcess();
TimeSpan cpuTime = process.TotalProcessorTime;

// Breakdown by type:
TimeSpan userTime = process.UserProcessorTime;   // Your code
TimeSpan kernelTime = process.PrivilegedProcessorTime; // System calls
```

```
┌─────────────────────────────────────────────────────────────────┐
│           Wall Time vs CPU Time                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Thread execution over 1 second of wall time:                   │
│                                                                 │
│  Wall Time: |████████████████████████████████████████| 1000ms   │
│                                                                 │
│  CPU Time:  |████|    |██████|        |████████|     | 500ms    │
│              ↑         ↑               ↑                        │
│              │         │               └─ Actual computation    │
│              │         └─ Waiting for I/O (not counted)         │
│              └─ Running on CPU                                  │
│                                                                 │
│  Wall time = 1000ms                                             │
│  CPU time  = 500ms (only when actually executing)               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Cycle Count Deep Dive

**Cycle counts** measure raw CPU cycles, varying with frequency.

```csharp
// .NET doesn't expose raw cycle counts directly
// You need platform-specific APIs or tools like BenchmarkDotNet

// What cycles tell you:
// - Instructions executed (roughly)
// - Cache misses impact (more cycles)
// - Branch misprediction impact (more cycles)

// What cycles DON'T tell you:
// - Actual wall time (frequency varies!)
// - Cross-platform comparison
```

### Choosing the Right Time Source

```
┌─────────────────────────────────────────────────────────────────┐
│                  Decision Tree: Which Time?                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Need to know "what time is it"?                                │
│  │                                                              │
│  ├── Yes ──► Need timezone info? ──┬── Yes ──► DateTimeOffset   │
│  │                                 └── No ───► DateTime.UtcNow  │
│  │                                                              │
│  └── No ──► Measuring duration?                                 │
│             │                                                   │
│             ├── Yes ──► High precision needed?                  │
│             │           │                                       │
│             │           ├── Yes ──► Stopwatch.GetTimestamp()    │
│             │           └── No ───► Environment.TickCount64     │
│             │                                                   │
│             └── No ──► Measuring CPU work?                      │
│                        │                                        │
│                        ├── Yes ──► Process.TotalProcessorTime   │
│                        └── No ───► (what are you measuring?)    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Common Mistakes

```csharp
// ❌ WRONG: Using DateTime to measure elapsed time
var start = DateTime.UtcNow;
DoWork();
var elapsed = DateTime.UtcNow - start; // Can be negative if clock adjusts!

// ✅ CORRECT: Using Stopwatch for elapsed time
var start = Stopwatch.GetTimestamp();
DoWork();
var elapsed = Stopwatch.GetElapsedTime(start);

// ❌ WRONG: Using Stopwatch for wall-clock timestamps
long timestamp = Stopwatch.GetTimestamp();
SaveToDatabase(timestamp); // Meaningless after reboot!

// ✅ CORRECT: Using DateTimeOffset for persistent timestamps
var timestamp = DateTimeOffset.UtcNow;
SaveToDatabase(timestamp);

// ❌ WRONG: Comparing timestamps across machines
var machineA_time = Stopwatch.GetTimestamp(); // On machine A
var machineB_time = Stopwatch.GetTimestamp(); // On machine B
// These are completely unrelated values!

// ✅ CORRECT: Use synchronized wall-clock time for cross-machine
var machineA_time = DateTimeOffset.UtcNow; // NTP-synced
var machineB_time = DateTimeOffset.UtcNow; // NTP-synced
// Comparable within NTP accuracy (~ms)
```

### Resolution vs Precision vs Accuracy

These terms are often confused:

```
┌─────────────────────────────────────────────────────────────────┐
│         Resolution vs Precision vs Accuracy                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  RESOLUTION: Smallest unit the timer can distinguish            │
│  ────────────────────────────────────────────────────           │
│  DateTime.UtcNow resolution ≈ 15.6ms (Windows timer tick)       │
│  Stopwatch resolution ≈ 100ns (depends on hardware)             │
│                                                                 │
│  Example: Resolution = 1ms means you can't tell apart           │
│           events that are 0.5ms apart                           │
│                                                                 │
│  PRECISION: Consistency of repeated measurements                │
│  ────────────────────────────────────────────────────           │
│  High precision = same input gives same output                  │
│  Low precision = measurements vary randomly                     │
│                                                                 │
│  Example: Measuring 100ms repeatedly might give                 │
│           99.8, 100.1, 100.0, 99.9 (high precision)             │
│           vs 95, 108, 97, 103 (low precision)                   │
│                                                                 │
│  ACCURACY: How close to the "true" value                        │
│  ────────────────────────────────────────────────────           │
│  Your 100ms measurement might consistently read 102ms           │
│  (high precision, low accuracy - systematic error)              │
│                                                                 │
│  Example: A clock that's always 5 minutes fast is               │
│           precise but not accurate                              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### .NET 8+ TimeProvider Abstraction

.NET 8 introduced `TimeProvider` for testable time-dependent code:

```csharp
// Production code using abstraction
public class CacheService
{
    private readonly TimeProvider _timeProvider;
    
    public CacheService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }
    
    public bool IsExpired(DateTimeOffset createdAt, TimeSpan ttl)
    {
        return _timeProvider.GetUtcNow() > createdAt + ttl;
    }
    
    public TimeSpan MeasureOperation(Action action)
    {
        long start = _timeProvider.GetTimestamp();
        action();
        return _timeProvider.GetElapsedTime(start);
    }
}

// In tests - you can control time!
var fakeTime = new FakeTimeProvider(startDateTime);
var cache = new CacheService(fakeTime);

fakeTime.Advance(TimeSpan.FromMinutes(5)); // "Fast forward" time
Assert.True(cache.IsExpired(startDateTime, TimeSpan.FromMinutes(1)));
```

---

## What is QueryPerformanceCounter?

**QueryPerformanceCounter (QPC)** is Windows' primary API for acquiring high-resolution timestamps. It provides:

- **Monotonically increasing** values (never goes backward)
- **High resolution** (sub-microsecond on modern systems)
- **Low latency** access (nanoseconds when using TSC)
- **Consistent frequency** reported by `QueryPerformanceFrequency`

```
┌─────────────────────────────────────────────────────────────────┐
│                    QPC Conceptual Model                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   QueryPerformanceCounter()                                     │
│            │                                                    │
│            ▼                                                    │
│   ┌─────────────────┐                                           │
│   │  Windows HAL    │ ← Abstraction layer                       │
│   └────────┬────────┘                                           │
│            │                                                    │
│            ▼                                                    │
│   ┌─────────────────────────────────────────────────────┐       │
│   │          Hardware Timer Selection                    │       │
│   │  ┌─────────┐  ┌──────┐  ┌─────────┐  ┌───────────┐  │       │
│   │  │   TSC   │  │ HPET │  │ PM Timer│  │ ARM Timer │  │       │
│   │  │(fastest)│  │      │  │(slowest)│  │           │  │       │
│   │  └─────────┘  └──────┘  └─────────┘  └───────────┘  │       │
│   └─────────────────────────────────────────────────────┘       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Key Guarantees

| Property | Guarantee |
|----------|-----------|
| Monotonic | Yes - consecutive reads never decrease |
| Frequency | Constant at runtime (calibrated at boot) |
| Cross-core consistent | Yes (on modern systems) |
| Resolution | Typically < 1 µs |
| Affected by system time changes | No |

---

## Hardware Timer Sources

Windows can use several hardware timers as the basis for QPC. Understanding these helps explain performance characteristics and edge cases.

### Time Stamp Counter (TSC)

The **TSC** is a 64-bit register present on all x86/x64 processors since the Pentium. It counts CPU "ticks" since reset.

```
┌────────────────────────────────────────────────────────────┐
│                    TSC Evolution                           │
├────────────────────────────────────────────────────────────┤
│                                                            │
│  Legacy TSC (pre-2007)                                     │
│  ├── Tied to CPU clock frequency                           │
│  ├── Changed with P-states (power management)              │
│  ├── Stopped in deep C-states                              │
│  └── Not synchronized across cores                         │
│                                                            │
│  Constant TSC                                              │
│  ├── Fixed rate regardless of P-states                     │
│  └── May still stop in deep C-states                       │
│                                                            │
│  Invariant TSC (modern, ~2008+)                            │
│  ├── Fixed rate in ALL P-states, C-states, T-states        │
│  ├── Never stops (except thermal emergency)                │
│  ├── Synchronized across cores on same die                 │
│  └── CPUID flag: 80000007H:EDX[8]                          │
│                                                            │
│  Nonstop TSC                                               │
│  └── Continues even in deep sleep states                   │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

**Checking for Invariant TSC:**

```csharp
// You can check via Windows - if Stopwatch.Frequency is in GHz range,
// you're likely using TSC. If it's ~3.58 MHz, it's PM Timer.
// If it's ~10-25 MHz, it might be HPET or scaled TSC.

Console.WriteLine($"Frequency: {Stopwatch.Frequency:N0} Hz");
Console.WriteLine($"IsHighResolution: {Stopwatch.IsHighResolution}");

// Modern Windows reports 10 MHz (scaled from TSC)
// This is intentional - see "Why 10 MHz?" section
```

**TSC Characteristics:**

| Aspect | Value |
|--------|-------|
| Typical rate | CPU base frequency (e.g., 3.5 GHz) |
| Access latency | ~10-25 cycles (~5-10 ns) |
| Instruction | `RDTSC` or `RDTSCP` |
| Kernel transition | No (user-space accessible) |

### High Precision Event Timer (HPET)

The **HPET** is a hardware timer specified by Intel, designed to replace older timer hardware.

```
┌────────────────────────────────────────────────────────────┐
│                    HPET Architecture                       │
├────────────────────────────────────────────────────────────┤
│                                                            │
│   ┌─────────────────────────────────────────┐              │
│   │           Main Counter (64-bit)          │              │
│   │         Minimum 10 MHz frequency         │              │
│   └─────────────────────────────────────────┘              │
│                      │                                     │
│        ┌─────────────┼─────────────┐                       │
│        ▼             ▼             ▼                       │
│   ┌─────────┐   ┌─────────┐   ┌─────────┐                  │
│   │ Timer 0 │   │ Timer 1 │   │ Timer 2 │  (3-256 timers)  │
│   │ Compare │   │ Compare │   │ Compare │                  │
│   └─────────┘   └─────────┘   └─────────┘                  │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

**HPET Characteristics:**

| Aspect | Value |
|--------|-------|
| Typical rate | 14.318 MHz or 25 MHz |
| Access latency | ~500-2000 ns |
| Access method | Memory-mapped I/O |
| Kernel transition | Yes (system call required) |

**Why HPET is rarely used today:**

Linux kernel developers blacklisted HPET on some Intel CPUs (Coffee Lake) due to instability. The access latency is 20-100x slower than TSC, making it impractical for high-frequency timing.

### ACPI Power Management Timer (PM Timer)

The **PM Timer** is part of the ACPI specification, providing a simple, reliable clock.

**PM Timer Characteristics:**

| Aspect | Value |
|--------|-------|
| Frequency | 3.579545 MHz (fixed) |
| Access latency | ~1000-3000 ns |
| Counter width | 24 or 32 bits |
| Resolution | ~279 ns |

The PM Timer is the "fallback of last resort" - very reliable but slow and low resolution.

### ARM Generic Timer

On ARM processors (including Apple Silicon), there's no TSC. Instead, ARM provides the **Generic Timer**.

```
┌────────────────────────────────────────────────────────────┐
│                  ARM Generic Timer                         │
├────────────────────────────────────────────────────────────┤
│                                                            │
│   System Counter (CNTVCT_EL0)                              │
│   ├── Platform-wide, synchronized                          │
│   ├── Fixed frequency (1 GHz on ARMv8.6+)                  │
│   ├── Starts at zero on boot                               │
│   └── Independent of CPU frequency                         │
│                                                            │
│   Performance Monitors Cycle Counter (PMCCNTR_EL0)         │
│   ├── Per-CPU, counts actual cycles                        │
│   ├── Non-invariant (varies with frequency)                │
│   └── NOT suitable for wall-clock time                     │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

---

## How Windows Selects a Timer Source

Windows determines the QPC source at boot time based on hardware capabilities.

```
┌─────────────────────────────────────────────────────────────────┐
│              Windows Timer Selection Algorithm                  │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   Boot Time                                                     │
│       │                                                         │
│       ▼                                                         │
│   ┌───────────────────────────────────────┐                     │
│   │ Check CPUID for Invariant TSC flag    │                     │
│   │ (CPUID.80000007H:EDX[8])              │                     │
│   └───────────────────┬───────────────────┘                     │
│                       │                                         │
│           ┌───────────┴───────────┐                             │
│           │                       │                             │
│     TSC Available           TSC Not Available                   │
│           │                       │                             │
│           ▼                       ▼                             │
│   ┌───────────────┐       ┌───────────────┐                     │
│   │ Verify TSC    │       │ Check for     │                     │
│   │ synchronizes  │       │ HPET          │                     │
│   │ across cores  │       └───────┬───────┘                     │
│   └───────┬───────┘               │                             │
│           │               ┌───────┴───────┐                     │
│           ▼               │               │                     │
│   ┌───────────────┐   HPET OK        No HPET                    │
│   │ Use TSC       │       │               │                     │
│   │ (scaled to    │       ▼               ▼                     │
│   │  10 MHz)      │   Use HPET      Use PM Timer                │
│   └───────────────┘                                             │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Why Windows Reports 10 MHz

Modern Windows scales the raw TSC to report a **10 MHz frequency** through `QueryPerformanceFrequency`. This is deliberate:

1. **Consistency** - Applications get predictable frequency across different hardware
2. **Overflow prevention** - Raw TSC at 3+ GHz would overflow 64-bit counters faster
3. **Sufficient precision** - 100 ns resolution is adequate for most applications

```csharp
// What actually happens inside QPC on modern Windows:
// 
// 1. Read raw TSC via RDTSCP instruction
// 2. Add offset from shared memory page (KUSER_SHARED_DATA at 0x7FFE0000)
// 3. Scale by dividing by ~100-400 (depending on CPU frequency)
// 4. Return scaled value
//
// The scaling formula is calibrated at boot time
```

---

## CPU Clock Speed Variations

A critical question: **Does QPC stay accurate when CPU frequency changes?**

### The Answer: Yes (on modern systems)

```
┌─────────────────────────────────────────────────────────────────┐
│           CPU Frequency vs Timer Behavior                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   CPU State Changes:                                            │
│                                                                 │
│   ┌─────────────────┐    Invariant TSC                          │
│   │ Base Frequency  │ ──────────────────► Ticks at constant     │
│   │    (3.0 GHz)    │                     rate (e.g., 3 GHz)    │
│   └─────────────────┘                                           │
│           │                                                     │
│     Turbo Boost                                                 │
│           │                                                     │
│           ▼                                                     │
│   ┌─────────────────┐    Invariant TSC                          │
│   │ Turbo Frequency │ ──────────────────► SAME rate (3 GHz)     │
│   │    (4.5 GHz)    │                     (unchanged!)          │
│   └─────────────────┘                                           │
│           │                                                     │
│     Power Saving                                                │
│           │                                                     │
│           ▼                                                     │
│   ┌─────────────────┐    Invariant TSC                          │
│   │ Reduced Freq    │ ──────────────────► SAME rate (3 GHz)     │
│   │    (800 MHz)    │                     (unchanged!)          │
│   └─────────────────┘                                           │
│                                                                 │
│   Key Insight: Invariant TSC is decoupled from CPU frequency    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Implications

| Scenario | QPC Behavior | Notes |
|----------|--------------|-------|
| Turbo Boost activates | Unaffected | TSC rate constant |
| Power saving reduces frequency | Unaffected | TSC rate constant |
| Deep C-state (sleep) | Unaffected | Nonstop TSC continues |
| Thermal throttling | Unaffected | TSC rate constant |
| Laptop on battery | Unaffected | TSC rate constant |

### What About Measuring CPU Cycles?

If you need to measure **actual CPU cycles** (affected by frequency), QPC is the **wrong tool**. Use:

- `RDPMC` instruction with performance counters
- CPU-specific profiling tools
- Hardware performance monitoring

```csharp
// QPC measures WALL-CLOCK TIME, not CPU cycles!
// 
// If your CPU runs at 4.5 GHz for 1 second, then 800 MHz for 1 second:
// - QPC will report ~2 seconds elapsed
// - Actual cycles executed will vary wildly
//
// For cycle counting, use BenchmarkDotNet's hardware counters
// or platform-specific performance monitoring APIs
```

---

## Cross-Platform Behavior

.NET's `Stopwatch.GetTimestamp()` abstracts platform differences, but understanding them matters.

### Platform Comparison

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    Cross-Platform Timer Mapping                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│   .NET Stopwatch.GetTimestamp()                                          │
│              │                                                           │
│   ┌──────────┼──────────┬──────────────────┐                             │
│   │          │          │                  │                             │
│   ▼          ▼          ▼                  ▼                             │
│                                                                          │
│ Windows    Linux       macOS             macOS                           │
│ (x64)     (x64)       (Intel)           (Apple Silicon)                  │
│   │          │          │                  │                             │
│   ▼          ▼          ▼                  ▼                             │
│ QPC      clock_gettime  mach_continuous   mach_continuous                │
│          (MONOTONIC)    _time()           _time()                        │
│   │          │          │                  │                             │
│   ▼          ▼          ▼                  ▼                             │
│ RDTSCP    RDTSC via    RDTSC via         ARM Generic                     │
│ (scaled)  vDSO         commpage          Timer (CNTVCT)                  │
│                                                                          │
│ Freq:     Freq:        Freq:             Freq:                           │
│ 10 MHz    1 GHz (ns)   1 GHz (ns)*       ~24 MHz**                       │
│                                                                          │
│ * Intel Macs: numer/denom = 1/1                                          │
│ ** Apple Silicon: requires mach_timebase_info conversion                 │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### Windows

```csharp
// Windows implementation
// - Calls QueryPerformanceCounter via P/Invoke or JIT intrinsic
// - Returns scaled TSC value (10 MHz frequency)
// - Includes suspend/sleep time in the count

long ticks = Stopwatch.GetTimestamp();
// ticks / Stopwatch.Frequency = seconds elapsed since boot
```

### Linux

```csharp
// Linux implementation
// - Calls clock_gettime(CLOCK_MONOTONIC) via vDSO (no syscall!)
// - Returns nanoseconds since boot
// - Does NOT include suspend/sleep time (CLOCK_MONOTONIC behavior)

// Note: Stopwatch.Frequency on Linux = 1,000,000,000 (1 GHz = nanoseconds)
```

**Linux Clock Options:**

| Clock | Description | Suspend Time |
|-------|-------------|--------------|
| `CLOCK_MONOTONIC` | Adjusted by NTP | Excluded |
| `CLOCK_MONOTONIC_RAW` | Not adjusted | Excluded |
| `CLOCK_BOOTTIME` | Like MONOTONIC but includes suspend | Included |

### macOS

```csharp
// macOS implementation
// - Calls mach_continuous_time() or clock_gettime_nsec_np(CLOCK_UPTIME_RAW)
// - Intel: ticks are in nanoseconds (timebase 1/1)
// - Apple Silicon: ticks need conversion via mach_timebase_info

// CRITICAL for Apple Silicon:
// mach_absolute_time units are NOT nanoseconds!
// Must multiply by timebase.numer / timebase.denom
```

**Apple Silicon Timing Gotcha:**

```csharp
// On Intel Macs: 1 tick = 1 nanosecond
// On Apple Silicon: 1 tick ≠ 1 nanosecond
//
// .NET handles this for you via Stopwatch.Frequency
// but if you're doing native interop, beware!
```

### Critical Cross-Platform Difference: Suspend Time

```
┌─────────────────────────────────────────────────────────────────┐
│           Suspend/Sleep Time Handling                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   Scenario: System suspends for 1 hour, then resumes            │
│                                                                 │
│   Windows (QPC):                                                │
│   ┌──────────────────────────────────────────────────────┐      │
│   │ T=0        T=5s      [SUSPEND 1hr]      T=3605s      │      │
│   │  │──────────│                             │          │      │
│   │  Start     Before                        After       │      │
│   │            suspend                       resume      │      │
│   │                                                      │      │
│   │  GetTimestamp() difference = 3605 seconds ✓         │      │
│   └──────────────────────────────────────────────────────┘      │
│                                                                 │
│   Linux (CLOCK_MONOTONIC):                                      │
│   ┌──────────────────────────────────────────────────────┐      │
│   │ T=0        T=5s      [SUSPEND 1hr]      T=5s         │      │
│   │  │──────────│                             │          │      │
│   │  Start     Before                        After       │      │
│   │            suspend                       resume      │      │
│   │                                                      │      │
│   │  GetTimestamp() difference = 5 seconds ✗            │      │
│   │  (suspend time not counted!)                        │      │
│   └──────────────────────────────────────────────────────┘      │
│                                                                 │
│   macOS (CLOCK_UPTIME_RAW):                                     │
│   └── Same as Linux - suspend time excluded                     │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**This is a known .NET issue** - `Stopwatch.GetTimestamp()` behaves differently across platforms regarding suspend time.

---

## Virtualization Challenges

Running in a VM introduces significant timing complications.

### The Problem

```
┌─────────────────────────────────────────────────────────────────┐
│              Virtual Machine Timing Challenges                  │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   Physical Machine:                                             │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │ Time  ─────────────────────────────────────────────►    │   │
│   │ CPU   ████████████████████████████████████████████      │   │
│   │       (continuous execution)                            │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│   Virtual Machine:                                              │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │ Time  ─────────────────────────────────────────────►    │   │
│   │ vCPU  ███░░░███░░███░░░░███░░░██████░░░███░░███        │   │
│   │           ▲       ▲           ▲                         │   │
│   │           │       │           │                         │   │
│   │        Scheduler preemption, other VMs running          │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│   Problems:                                                     │
│   1. VM paused → time appears to jump forward                   │
│   2. vMotion/migration → TSC may have different rate            │
│   3. Snapshot/restore → time discontinuity                      │
│   4. Host overcommit → scheduling delays                        │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Hypervisor TSC Virtualization Strategies

| Strategy | Description | Trade-offs |
|----------|-------------|------------|
| **Passthrough** | Guest reads host TSC directly | Fast, but issues on migration |
| **Offset** | Host TSC + offset | Handles migration, still fast |
| **Scale + Offset** | Host TSC × scale + offset | Handles different CPU speeds |
| **Trap & Emulate** | RDTSC causes VM exit | Very slow, but most compatible |
| **Paravirtualized** | Special guest API (kvmclock, Hyper-V ref time) | Fast, requires guest support |

### Hypervisor-Specific Behavior

#### VMware

- Virtualizes TSC in "apparent time" (stays in sync with other virtual timers)
- Virtual TSC advances even when vCPU isn't running
- Provides pseudo-performance counters for real-time access
- VM migration may cause TSC rate to change

#### Hyper-V

- Provides Reference Counter (strictly monotonic, constant rate)
- Reference Time Enlightenment for invariant TSC guests
- TSC is rate-matched to host but offset adjusted
- Windows guests use enlightened path automatically

#### KVM/QEMU

- Can trap RDTSC or use offset virtualization
- kvmclock provides paravirtualized monotonic time
- Guest can choose clock source (`/sys/devices/system/clocksource/`)

#### VirtualBox

- Synchronizes all guest time sources to host monotonic time
- Can tie TSC to execution time (special mode)
- Guest Additions help synchronize clock

### VM Timing Best Practices

```csharp
// In a VM, your timing code should:
// 1. Never assume TSC frequency matches host
// 2. Be tolerant of time jumps
// 3. Use NTP/chrony for wall-clock synchronization
// 4. Avoid measuring intervals > minutes with high precision expectations

// Example: Detecting VM execution gaps
long start = Stopwatch.GetTimestamp();
DoWork();
long end = Stopwatch.GetTimestamp();

TimeSpan elapsed = Stopwatch.GetElapsedTime(start, end);

// In a VM, this could be much larger than actual work time
// due to vCPU scheduling delays
```

### VM Pause/Resume Behavior

```
┌─────────────────────────────────────────────────────────────────┐
│              VM Pause/Resume Scenarios                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   Scenario: VM paused for 10 minutes, then resumed              │
│                                                                 │
│   Typical Hypervisor Behavior:                                  │
│                                                                 │
│   Before Pause:                                                 │
│     QPC = 1,000,000,000 (100 seconds at 10 MHz)                 │
│                                                                 │
│   After Resume:                                                 │
│     QPC = 7,000,000,000 (700 seconds at 10 MHz)                 │
│              └──────────┬──────────┘                            │
│                         │                                       │
│              Time "jumped" 600 seconds                          │
│                                                                 │
│   Your Code Sees:                                               │
│   - Start measurement at QPC = 1,000,000,000                    │
│   - Do quick operation                                          │
│   - VM pauses (you don't know!)                                 │
│   - VM resumes                                                  │
│   - End measurement at QPC = 7,000,000,005                      │
│   - Elapsed = 600.0000005 seconds (!) for a 5µs operation       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## .NET Implementation Details

### How Stopwatch.GetTimestamp() Works

```
┌─────────────────────────────────────────────────────────────────┐
│          .NET Stopwatch.GetTimestamp() Flow                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   Stopwatch.GetTimestamp()                                      │
│            │                                                    │
│            ▼                                                    │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │ [MethodImpl(MethodImplOptions.AggressiveInlining)]      │   │
│   │ public static long GetTimestamp()                       │   │
│   │ {                                                       │   │
│   │     // JIT intrinsic on modern .NET                     │   │
│   │     return QueryPerformanceCounter();                   │   │
│   │ }                                                       │   │
│   └─────────────────────────────────────────────────────────┘   │
│            │                                                    │
│            ▼                                                    │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │              JIT Compilation                            │   │
│   │                                                         │   │
│   │   Windows (x64):         Linux (x64):                   │   │
│   │   ┌─────────────────┐   ┌─────────────────┐            │   │
│   │   │ mov rcx, [...]  │   │ call vDSO       │            │   │
│   │   │ rdtscp          │   │ clock_gettime   │            │   │
│   │   │ shl rdx, 32     │   │ (MONOTONIC)     │            │   │
│   │   │ or rax, rdx     │   └─────────────────┘            │   │
│   │   │ add rax, [...]  │                                   │   │
│   │   │ shr rax, 10     │   macOS:                          │   │
│   │   └─────────────────┘   ┌─────────────────┐            │   │
│   │   (reads shared mem,    │ call            │            │   │
│   │    scales TSC)          │ mach_continuous │            │   │
│   │                         │ _time           │            │   │
│   │                         └─────────────────┘            │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Frequency Differences Across Platforms

```csharp
// Run this on different platforms to see the difference:

Console.WriteLine($"Platform: {Environment.OSVersion}");
Console.WriteLine($"Stopwatch.Frequency: {Stopwatch.Frequency:N0}");
Console.WriteLine($"Stopwatch.IsHighResolution: {Stopwatch.IsHighResolution}");

// Typical outputs:
//
// Windows:
//   Frequency: 10,000,000 (10 MHz)
//   IsHighResolution: True
//
// Linux:
//   Frequency: 1,000,000,000 (1 GHz = nanoseconds)
//   IsHighResolution: True
//
// macOS (Intel):
//   Frequency: 1,000,000,000 (nanoseconds)
//   IsHighResolution: True
//
// macOS (Apple Silicon):
//   Frequency: 24,000,000 (24 MHz, varies by chip)
//   IsHighResolution: True
```

### Raw Timestamp Values Are NOT Portable

```csharp
// WRONG - raw values differ across platforms
long timestamp = Stopwatch.GetTimestamp();
SaveToDatabase(timestamp); // ✗ Don't do this!

// CORRECT - convert to portable TimeSpan
long start = Stopwatch.GetTimestamp();
// ... work ...
TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
SaveToDatabase(elapsed.TotalMilliseconds); // ✓ Portable

// Or use the two-parameter overload:
long end = Stopwatch.GetTimestamp();
TimeSpan elapsed2 = Stopwatch.GetElapsedTime(start, end);
```

---

## What NOT to Use QPC For

### ❌ Wall-Clock Time

```csharp
// WRONG - QPC doesn't give you the current time of day
long qpc = Stopwatch.GetTimestamp();
DateTime wrong = new DateTime(qpc); // ✗ Completely wrong!

// CORRECT - use DateTime or DateTimeOffset for wall-clock time
DateTime now = DateTime.UtcNow;
DateTimeOffset nowOffset = DateTimeOffset.UtcNow;

// For high-precision wall-clock: (Windows 8+)
// GetSystemTimePreciseAsFileTime() - but no direct .NET wrapper
```

### ❌ Timestamps Across Machines

```csharp
// WRONG - QPC values are not comparable across machines
machine1.Send(Stopwatch.GetTimestamp()); // ✗
machine2.Receive(timestamp); // Meaningless!

// CORRECT - use NTP-synchronized wall clock or logical clocks
machine1.Send(DateTimeOffset.UtcNow);
// Or use vector clocks / Lamport timestamps for ordering
```

### ❌ Timestamps Across Reboots

```csharp
// WRONG - QPC resets on reboot
long before = Stopwatch.GetTimestamp();
// ... system reboots ...
long after = Stopwatch.GetTimestamp();
// 'after' could be less than 'before'!

// CORRECT - use wall-clock time for persistence
var before = DateTimeOffset.UtcNow;
SaveToFile(before);
// ... reboot ...
var loaded = LoadFromFile();
var after = DateTimeOffset.UtcNow;
```

### ❌ Very Long Duration Measurements (Days/Weeks)

```csharp
// PROBLEMATIC - drift accumulates over long periods
long start = Stopwatch.GetTimestamp();
await Task.Delay(TimeSpan.FromDays(7));
TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
// Drift could be several seconds over a week!

// BETTER - use wall clock for long durations
var start = DateTimeOffset.UtcNow;
await Task.Delay(TimeSpan.FromDays(7));
var elapsed = DateTimeOffset.UtcNow - start;

// Or periodically resync with NTP
```

### ❌ Sub-Nanosecond Precision

```csharp
// QPC resolution is typically 100ns (Windows) or 1ns (Linux)
// but LATENCY is 10-50ns

// Measuring something faster than the measurement overhead:
for (int i = 0; i < 1000; i++)
{
    long start = Stopwatch.GetTimestamp(); // ~10ns
    // operation that takes 2ns
    long end = Stopwatch.GetTimestamp();   // ~10ns
    // Measured time is dominated by measurement overhead!
}

// CORRECT - batch operations and amortize
long start = Stopwatch.GetTimestamp();
for (int i = 0; i < 1_000_000; i++)
{
    // operation
}
long end = Stopwatch.GetTimestamp();
double perOp = Stopwatch.GetElapsedTime(start, end).TotalNanoseconds / 1_000_000;
```

### ❌ Measuring Actual CPU Cycles

```csharp
// WRONG - QPC measures wall time, not cycles
long start = Stopwatch.GetTimestamp();
CpuIntensiveWork();
long end = Stopwatch.GetTimestamp();
// If CPU was throttled, you get wall time, not cycle count

// CORRECT - use hardware performance counters
// (via BenchmarkDotNet, Intel VTune, or platform APIs)
```

### ❌ Distributed Ordering Without Synchronization

```csharp
// WRONG - QPC has no global ordering
// Server A at QPC 1000, Server B at QPC 2000
// Does NOT mean A's event happened before B's!

// CORRECT - use:
// - NTP-synchronized timestamps (with ~ms precision)
// - Logical clocks (Lamport, Vector)
// - Hybrid logical clocks (HLC)
// - TrueTime (if you're Google)
```

---

## Common Pitfalls and Gotchas

### Pitfall 1: Assuming Frequency is Constant Across Platforms

```csharp
// WRONG
const long TicksPerSecond = 10_000_000; // Only true on Windows!

// CORRECT
long ticksPerSecond = Stopwatch.Frequency;
```

### Pitfall 2: Integer Overflow in Duration Calculations

```csharp
// WRONG - can overflow with large tick values
long ticks = end - start;
long microseconds = ticks * 1_000_000 / Stopwatch.Frequency; // Overflow risk!

// CORRECT - use the built-in method
TimeSpan elapsed = Stopwatch.GetElapsedTime(start, end);
double microseconds = elapsed.TotalMicroseconds;
```

### Pitfall 3: Forgetting About Stopwatch Instance Overhead

```csharp
// ALLOCATES - creates object on heap
var sw = new Stopwatch();
sw.Start();
// ... work ...
sw.Stop();
var elapsed = sw.Elapsed;

// NO ALLOCATION - uses static methods
long start = Stopwatch.GetTimestamp();
// ... work ...
TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
```

### Pitfall 4: Mixing DateTime and Stopwatch

```csharp
// WRONG - mixing time sources
var dateStart = DateTime.UtcNow;
// ... work ...
long tickEnd = Stopwatch.GetTimestamp();
// Can't meaningfully combine these!

// CORRECT - use one time source consistently
long start = Stopwatch.GetTimestamp();
// ... work ...
long end = Stopwatch.GetTimestamp();
```

### Pitfall 5: Trusting Microbenchmark Results Without BenchmarkDotNet

```csharp
// WRONG - many issues: JIT warmup, GC, no statistics
long start = Stopwatch.GetTimestamp();
MyMethod();
Console.WriteLine(Stopwatch.GetElapsedTime(start));

// CORRECT - use BenchmarkDotNet
[Benchmark]
public void MyMethod() { /* ... */ }
```

### Pitfall 6: Assuming ±1 Tick Ordering

```csharp
// QPC values that differ by ±1 tick have AMBIGUOUS ordering
// This is documented by Microsoft!

long a = Stopwatch.GetTimestamp();
long b = Stopwatch.GetTimestamp();

if (b - a == 1)
{
    // We cannot say for certain that 'a' happened before 'b'
    // They might have been essentially simultaneous
}
```

### Pitfall 7: Cross-Platform Sleep/Suspend Behavior

```csharp
// Windows: QPC includes suspend time
// Linux/macOS: Stopwatch does NOT include suspend time

// If your laptop sleeps for an hour:
// - Windows: GetTimestamp difference = 1 hour + work time
// - Linux: GetTimestamp difference = work time only

// This can cause cross-platform bugs!
```

---

## Best Practices

### Use Static Methods for Low-Overhead Timing

```csharp
// ✓ Best practice - no allocations
public void ProcessWithTiming()
{
    long start = Stopwatch.GetTimestamp();
    
    Process();
    
    TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
    _metrics.RecordDuration(elapsed);
}
```

### Always Use Stopwatch.Frequency for Conversions

```csharp
// ✓ Portable conversion
public static double TicksToMilliseconds(long ticks)
{
    return (double)ticks / Stopwatch.Frequency * 1000.0;
}

// ✓ Or just use the built-in
TimeSpan elapsed = Stopwatch.GetElapsedTime(startTicks, endTicks);
```

### Batch Measurements for Very Fast Operations

```csharp
// ✓ Amortize measurement overhead
public double MeasureAverageNanoseconds(Action action, int iterations = 1_000_000)
{
    // Warmup
    for (int i = 0; i < 100; i++) action();
    
    // Measure
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < iterations; i++)
    {
        action();
    }
    TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
    
    return elapsed.TotalNanoseconds / iterations;
}
```

### Handle VM Time Jumps Gracefully

```csharp
// ✓ Detect and handle time jumps
public class RobustTimer
{
    private long _lastTimestamp;
    private readonly TimeSpan _maxExpectedInterval;
    
    public RobustTimer(TimeSpan maxExpectedInterval)
    {
        _maxExpectedInterval = maxExpectedInterval;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }
    
    public TimeSpan GetElapsedAndReset()
    {
        long current = Stopwatch.GetTimestamp();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, current);
        _lastTimestamp = current;
        
        // Detect VM pause or system anomaly
        if (elapsed > _maxExpectedInterval)
        {
            // Log warning, use fallback, or return capped value
            return _maxExpectedInterval;
        }
        
        return elapsed;
    }
}
```

### Use BenchmarkDotNet for Serious Benchmarking

```csharp
// ✓ Proper benchmarking
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int MethodA() => ComputeA();
    
    [Benchmark]
    public int MethodB() => ComputeB();
}
```

---

## Code Examples

### Example 1: Basic Timing

```csharp
using System.Diagnostics;

// Simple elapsed time measurement
long start = Stopwatch.GetTimestamp();

DoWork();

TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
Console.WriteLine($"Work took: {elapsed.TotalMilliseconds:F3} ms");
```

### Example 2: High-Precision Interval

```csharp
using System.Diagnostics;

public readonly struct PrecisionTimer
{
    private readonly long _start;
    
    private PrecisionTimer(long start) => _start = start;
    
    public static PrecisionTimer Start() => new(Stopwatch.GetTimestamp());
    
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);
    
    public double ElapsedMicroseconds => Elapsed.TotalMicroseconds;
    
    public double ElapsedNanoseconds => Elapsed.TotalNanoseconds;
}

// Usage
var timer = PrecisionTimer.Start();
DoFastOperation();
Console.WriteLine($"Operation took: {timer.ElapsedNanoseconds:F0} ns");
```

### Example 3: Timing with Statistics

```csharp
using System.Diagnostics;

public class TimingStats
{
    private readonly List<double> _samples = new();
    
    public void Record(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        _samples.Add(elapsed.TotalNanoseconds);
    }
    
    public void PrintStats()
    {
        if (_samples.Count == 0) return;
        
        _samples.Sort();
        
        Console.WriteLine($"Samples: {_samples.Count}");
        Console.WriteLine($"Min:     {_samples[0]:F1} ns");
        Console.WriteLine($"Max:     {_samples[^1]:F1} ns");
        Console.WriteLine($"Median:  {_samples[_samples.Count / 2]:F1} ns");
        Console.WriteLine($"Mean:    {_samples.Average():F1} ns");
        Console.WriteLine($"P95:     {_samples[(int)(_samples.Count * 0.95)]:F1} ns");
        Console.WriteLine($"P99:     {_samples[(int)(_samples.Count * 0.99)]:F1} ns");
    }
}
```

### Example 4: Cross-Platform Diagnostics

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class TimerDiagnostics
{
    public static void PrintDiagnostics()
    {
        Console.WriteLine("=== Timer Diagnostics ===");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();
        Console.WriteLine($"Stopwatch.Frequency: {Stopwatch.Frequency:N0} Hz");
        Console.WriteLine($"Stopwatch.IsHighResolution: {Stopwatch.IsHighResolution}");
        Console.WriteLine($"Tick duration: {1_000_000_000.0 / Stopwatch.Frequency:F2} ns");
        Console.WriteLine();
        
        // Measure actual latency
        const int iterations = 1_000_000;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            _ = Stopwatch.GetTimestamp();
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        
        Console.WriteLine($"GetTimestamp() latency: {elapsed.TotalNanoseconds / iterations:F1} ns");
    }
}
```

### Example 5: Rate Limiter with QPC

```csharp
using System.Diagnostics;

public class PrecisionRateLimiter
{
    private readonly long _intervalTicks;
    private long _nextAllowedTicks;
    
    public PrecisionRateLimiter(TimeSpan interval)
    {
        _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        _nextAllowedTicks = Stopwatch.GetTimestamp();
    }
    
    public bool TryAcquire()
    {
        long now = Stopwatch.GetTimestamp();
        
        if (now >= _nextAllowedTicks)
        {
            _nextAllowedTicks = now + _intervalTicks;
            return true;
        }
        
        return false;
    }
    
    public async ValueTask WaitAsync(CancellationToken ct = default)
    {
        long now = Stopwatch.GetTimestamp();
        
        if (now < _nextAllowedTicks)
        {
            TimeSpan delay = Stopwatch.GetElapsedTime(now, _nextAllowedTicks);
            // Note: For very short delays, SpinWait might be better
            await Task.Delay(delay, ct);
        }
        
        _nextAllowedTicks = Stopwatch.GetTimestamp() + _intervalTicks;
    }
}
```

---

## Summary

### Quick Reference Card

| Need | Use |
|------|-----|
| Elapsed time (short duration) | `Stopwatch.GetTimestamp()` + `GetElapsedTime()` |
| Wall-clock time | `DateTime.UtcNow` or `DateTimeOffset.UtcNow` |
| High-precision wall-clock | `GetSystemTimePreciseAsFileTime` (Windows) |
| CPU cycles | Hardware performance counters |
| Cross-machine ordering | NTP-synced time or logical clocks |
| Serious benchmarking | BenchmarkDotNet |

### Key Takeaways

1. **QPC/Stopwatch measures elapsed time, not wall-clock time**
2. **Frequency varies by platform** - always use `Stopwatch.Frequency`
3. **Invariant TSC** makes modern timing reliable regardless of CPU frequency changes
4. **VMs complicate everything** - expect time jumps and drift
5. **Suspend/sleep behavior differs** between Windows and Unix
6. **Use static methods** (`GetTimestamp()`) for zero-allocation timing
7. **Batch measurements** for operations faster than ~100ns
8. **BenchmarkDotNet** is essential for reliable benchmarks

---

*Document generated for .NET 10 targeting modern Windows/Linux/macOS systems.*

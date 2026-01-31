# Implementing IMetricSource — Quick Reference

> **TL;DR:** Use this checklist when adding observability to a new `IResource`. Jump to [Implementation Checklist](#implementation-checklist) for the step-by-step guide.

**Date:** January 2026
**Design Reference:** [09-resource-observability-implementations.md](../../design/resources/09-resource-observability-implementations.md)

---

## Implementation Checklist

### 1. Determine Required Interfaces

| Interface | When to Implement |
|-----------|-------------------|
| `IMetricSource` | **Always** — All significant resources should expose metrics |
| `IContentionTarget` | Only if the type uses **blocking synchronization** (locks, `AccessControl`) |
| `IDebugPropertiesProvider` | When detailed breakdown is useful for debugging |

**Key insight:** Lock-free types (`Interlocked`, `ConcurrentDictionary`, plain `++`) do **not** need `IContentionTarget`.

### 2. Choose Counter Update Strategy

| Scenario | Pattern | Example |
|----------|---------|---------|
| **Hot path, single logical writer** | Plain `++` | Bitmap `SetL0()` counter |
| **Hot path, multiple writers** | Plain `++` (accept ~0.1% loss) | Concurrent operation counters |
| **Cold path, multiple writers** | `Interlocked.Increment()` | Allocation/deallocation counters |
| **Aggregated from children** | Read directly in `ReadMetrics()` | Capacity from segments |

**Rule of thumb:** If the counter is in a method marked `[MethodImpl(AggressiveInlining)]`, use plain `++`.

### 3. High-Water Mark Pattern

```csharp
// Non-atomic check-and-write is acceptable for peaks
var newValue = Interlocked.Add(ref _counter, delta);
if (newValue > _peak)
{
    _peak = newValue;  // Plain write — occasional lost peak is OK
}
```

**Why this works:** Peaks are diagnostic, not transactional. Missing an occasional maximum doesn't affect correctness.

### 4. ResetPeaks() Implementation

```csharp
public void ResetPeaks()
{
    // Reset high-water marks to current values
    _peakBytes = _currentBytes;
    _maxWaitUs = 0;  // Or current value if tracking ongoing max

    // Do NOT reset cumulative counters (allocations, operations, etc.)
}
```

---

## Common Patterns

### Pattern A: Memory Allocator Style (Cold Path)

```csharp
// Fields
private long _totalBytes;
private long _peakBytes;
private long _cumulativeAllocations;

// In allocation method (cold path — OK to use Interlocked)
var newTotal = Interlocked.Add(ref _totalBytes, size);
if (newTotal > _peakBytes)
{
    _peakBytes = newTotal;
}
Interlocked.Increment(ref _cumulativeAllocations);

// In ReadMetrics
writer.WriteMemory(_totalBytes, _peakBytes);
writer.WriteThroughput("Allocations", _cumulativeAllocations);
```

### Pattern B: Bitmap Style (Hot Path)

```csharp
// Fields
private long _operationCount;  // Accept occasional misses

// In hot path method (plain ++ for zero overhead)
Interlocked.Increment(ref bank.TotalBitSet);  // State change — must be atomic
_operationCount++;  // Telemetry — plain ++ is fine

// In ReadMetrics
writer.WriteCapacity(TotalBitSet, Capacity);
writer.WriteThroughput("Operations", _operationCount);
```

### Pattern C: Contention Tracking (With AccessControl)

```csharp
// Replace lock() with AccessControl
private AccessControl _access;

// In synchronized method
_access.EnterExclusiveAccess(ref WaitContext.Null, target: this);
try
{
    // Critical section
}
finally
{
    _access.ExitExclusiveAccess();
}

// IContentionTarget implementation
public void RecordContention(long waitUs)
{
    Interlocked.Increment(ref _contentionWaitCount);
    Interlocked.Add(ref _contentionTotalWaitUs, waitUs);

    if (waitUs > _contentionMaxWaitUs)
        _contentionMaxWaitUs = waitUs;
}
```

---

## GetDebugProperties() Guidelines

### Naming Convention

Use dot-notation for hierarchical properties:

```csharp
return new Dictionary<string, object>
{
    // Segment breakdown
    ["ComponentSegment.AllocatedChunks"] = ...,
    ["ComponentSegment.Capacity"] = ...,

    // Contention details
    ["Contention.WaitCount"] = ...,
    ["Contention.MaxWaitUs"] = ...,

    // Per-instance breakdown (when reasonable)
    ["Bank[0].TotalBitSet"] = ...,
};
```

### Performance Considerations

- **Allocation is OK** — `GetDebugProperties()` is called infrequently for debugging
- **Snapshot first** — Take a snapshot of volatile collections before iterating
- **Limit breakdowns** — For large collections, only include first N items

```csharp
var banks = _banks;  // Snapshot reference
if (banks != null && banks.Length <= 8)
{
    for (int i = 0; i < banks.Length; i++)
    {
        props[$"Bank[{i}].TotalBitSet"] = banks[i].TotalBitSet;
    }
}
```

---

## Lessons Learned

### 1. LINQ Removal Safety

When removing `using System.Linq;`, verify that methods like `ToArray()` aren't LINQ extensions:
- `List<T>.ToArray()` — **Built-in**, safe to remove LINQ
- `IEnumerable<T>.ToArray()` — **LINQ extension**, keep the using

### 2. Conditional Logic Refactoring

When adding instrumentation to conditional logic (e.g., CAS operations), preserve the original semantics:

```csharp
// Before: check for failure
if (Interlocked.CompareExchange(ref _banks, newBanks, banks) != banks)
{
    // Handle failure
}

// After: check for success first, then failure
if (Interlocked.CompareExchange(ref _banks, newBanks, banks) == banks)
{
    _successCount++;  // Track success
}
else
{
    // Handle failure (unchanged)
}
```

### 3. Interface Segregation Validation

Before implementing `IContentionTarget`, verify the type actually uses blocking:

| Synchronization | Needs IContentionTarget? |
|-----------------|--------------------------|
| `lock` statement | Yes (replace with `AccessControl`) |
| `AccessControl` / `AccessControlSmall` | Yes |
| `Interlocked.*` | No — lock-free |
| `ConcurrentDictionary` | No — lock-free |
| Plain `++` | No — not synchronized |

### 4. Dispose Safety (Pre-existing Issue)

If `ReadMetrics()` accesses fields that are nulled in `Dispose()`, the class has a pre-existing design issue. Document but don't fix during observability work — that's a separate concern.

---

## Testing Verification

After implementing:

1. **Build:** `dotnet build Typhon.slnx` — 0 errors
2. **Tests:** `dotnet test` — No regressions
3. **Manual verification:** Check that new counters are non-zero after operations

---

## See Also

- [09-resource-observability-implementations.md](../../design/resources/09-resource-observability-implementations.md) — Full design specification
- [IMetricSource.cs](../../../src/Typhon.Engine/Resources/IMetricSource.cs) — Interface definition with examples
- [IContentionTarget.cs](../../../src/Typhon.Engine/Observability/IContentionTarget.cs) — Contention tracking interface

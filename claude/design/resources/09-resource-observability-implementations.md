# 09 — Resource Observability Implementations

> **Part of the [Resource System Design](README.md) series**

**Date:** January 2026
**Status:** Design complete
**Prerequisites:** [03-metric-source.md](03-metric-source.md), [04-metric-kinds.md](04-metric-kinds.md), [05-granularity-strategy.md](05-granularity-strategy.md)

---

## Overview

Currently, only `ComponentTable` implements the full observability interface set (`IMetricSource`, `IContentionTarget`, `IDebugPropertiesProvider`). This document specifies how to extend observability to the remaining `IResource` implementations:

- **ManagedPagedMMF** — Page cache and file I/O metrics
- **DatabaseEngine** — Transaction lifecycle metrics
- **MemoryAllocator** — Allocation tracking
- **ConcurrentBitmapL3All** — Bitmap utilization

> **Key goal:** Consistent observability across all significant resources enables unified dashboards, alerts, and capacity planning.

---

## Table of Contents

1. [Current State](#1-current-state)
2. [ManagedPagedMMF](#2-managedpagedmmf)
3. [DatabaseEngine](#3-databaseengine)
4. [MemoryAllocator](#4-memoryallocator)
5. [ConcurrentBitmapL3All](#5-concurrentbitmapl3all)
6. [Implementation Priority & Phasing](#6-implementation-priority--phasing)
7. [Common Patterns](#7-common-patterns)
8. [Testing Strategy](#8-testing-strategy)
9. [Migration Notes](#9-migration-notes)

---

## 1. Current State

| Type | `IResource` | `IMetricSource` | `IContentionTarget` | `IDebugPropertiesProvider` |
|------|:-----------:|:---------------:|:-------------------:|:--------------------------:|
| **ComponentTable** | ✅ | ✅ | ✅ | ✅ |
| **ManagedPagedMMF** | ✅ | ❌ | ❌ | ❌ |
| **DatabaseEngine** | ✅ | ❌ | ❌ | ❌ |
| **MemoryAllocator** | ✅ | ❌ | ❌ | ❌ |
| **ConcurrentBitmapL3All** | ✅ | ❌ | ❌ | ❌ |

### Target State (This Design)

| Type | `IResource` | `IMetricSource` | `IContentionTarget` | `IDebugPropertiesProvider` |
|------|:-----------:|:---------------:|:-------------------:|:--------------------------:|
| **ComponentTable** | ✅ | ✅ | ✅ | ✅ |
| **ManagedPagedMMF** | ✅ | ✅ | ✅ | ✅ |
| **DatabaseEngine** | ✅ | ✅ | ❌ ¹ | ✅ |
| **MemoryAllocator** | ✅ | ✅ | ❌ ² | ✅ |
| **ConcurrentBitmapL3All** | ✅ | ✅ | ❌ ² | ✅ |

¹ Uses `ConcurrentDictionary`/`Interlocked` (lock-free); `TransactionChain`'s contention is encapsulated.
² Lock-free data structures; no thread waits to track.

### Existing Infrastructure

Several types already track metrics internally but don't expose them via the observability interfaces:

| Type | Internal Tracking | Exposure Gap |
|------|-------------------|--------------|
| **PagedMMF** | `_metrics` object with cache hits/misses, I/O counts | Not exposed via `IMetricSource` |
| **ManagedPagedMMF** | Inherits `_metrics`; has `lock (_occupancyMap)` synchronization | Replace with `AccessControl` for contention tracking |
| **DatabaseEngine** | `TransactionChain` has active transaction count; uses `ConcurrentDictionary` (lock-free) | No metrics interface |
| **ConcurrentBitmapL3All** | `TotalBitSet` per bank | No aggregated metrics |

---

## 2. ManagedPagedMMF

**Priority:** High — Page cache is a critical bottleneck for capacity planning.

### 2.1 Infrastructure to Add

```csharp
public partial class ManagedPagedMMF : PagedMMF, IResource, IMetricSource, IContentionTarget, IDebugPropertiesProvider
{
    // ═══════════════════════════════════════════════════════════════
    // SYNCHRONIZATION (replaces lock (_occupancyMap))
    // ═══════════════════════════════════════════════════════════════

    // Dedicated AccessControl for AllocatePages/FreePages synchronization
    // AccessControl natively supports IContentionTarget callbacks
    private AccessControl _occupancyMapAccess;

    // ═══════════════════════════════════════════════════════════════
    // CONTENTION TRACKING (populated by AccessControl callbacks)
    // ═══════════════════════════════════════════════════════════════

    private long _contentionWaitCount;
    private long _contentionTotalWaitUs;
    private long _contentionMaxWaitUs;

    // ═══════════════════════════════════════════════════════════════
    // THROUGHPUT COUNTERS (supplement inherited _metrics)
    // ═══════════════════════════════════════════════════════════════

    private long _evictionCount;
    private long _segmentAllocations;
    private long _segmentDeletions;
}
```

**Constructor initialization:**

```csharp
// In ManagedPagedMMF constructor, after base initialization:
_occupancyMapAccess = new AccessControl();
```

### 2.2 IMetricSource Implementation

| Metric Kind | Sub-metrics | Source |
|-------------|-------------|--------|
| **Memory** | AllocatedBytes, PeakBytes | `_memPages.Length` (inherited from PagedMMF) |
| **Capacity** | Current, Maximum, Utilization | Page state counts from `_memPagesInfo[]` |
| **DiskIO** | ReadOps, WriteOps, ReadBytes, WriteBytes | Inherited `_metrics` object |
| **Contention** | WaitCount, TotalWaitUs, MaxWaitUs | New tracking fields |
| **Throughput** | CacheHits, CacheMisses, Evictions | `_metrics` + new `_evictionCount` |

```csharp
public void ReadMetrics(IMetricWriter writer)
{
    var metrics = GetMetrics();

    // Memory: page cache buffer size
    long allocatedBytes = _memPages?.Length ?? 0;
    writer.WriteMemory(allocatedBytes, allocatedBytes);  // Fixed size, peak = current

    // Capacity: free vs total memory pages
    long freePages = metrics.FreeMemPageCount;
    long totalPages = _memPagesCount;
    writer.WriteCapacity(totalPages - freePages, totalPages);

    // DiskIO: read/write operations
    writer.WriteDiskIO(
        metrics.ReadFromDiskCount,
        metrics.PageWrittenToDiskCount,
        (long)metrics.ReadFromDiskCount * PageSize,
        (long)metrics.PageWrittenToDiskCount * PageSize);

    // Contention: occupancy map lock
    writer.WriteContention(
        _contentionWaitCount,
        _contentionTotalWaitUs,
        _contentionMaxWaitUs,
        0);  // No timeout tracking yet

    // Throughput: cache performance
    writer.WriteThroughput(MetricNames.CacheHits, metrics.MemPageCacheHit);
    writer.WriteThroughput(MetricNames.CacheMisses, metrics.MemPageCacheMiss);
    writer.WriteThroughput(MetricNames.Evictions, _evictionCount);
}

public void ResetPeaks()
{
    Volatile.Write(ref _contentionMaxWaitUs, 0);
}
```

### 2.3 IContentionTarget Implementation

Replace `lock (_occupancyMap)` with `AccessControl` in `AllocatePages()` and `FreePages()`. The `AccessControl` class natively supports `IContentionTarget` callbacks, making contention tracking automatic.

```csharp
// IContentionTarget implementation
public TelemetryLevel TelemetryLevel => TelemetryLevel.Light;
public IResource OwningResource => this;

public void RecordContention(long waitUs)
{
    // Called by AccessControl when a thread has to wait for the lock
    Interlocked.Increment(ref _contentionWaitCount);
    Interlocked.Add(ref _contentionTotalWaitUs, waitUs);

    // Plain check-and-write for high-water mark
    if (waitUs > _contentionMaxWaitUs)
        _contentionMaxWaitUs = waitUs;
}

public void LogLockOperation(LockOperation operation, long durationUs)
{
    // Light mode only - no operation logging
}
```

**Migration from `lock` to `AccessControl`:**

Replace the existing `lock (_occupancyMap)` pattern:

```csharp
// BEFORE: lock pattern (no contention visibility)
public void AllocatePages(ref Span<int> pageIds, int startFrom = 0, ChangeSet changeSet = null)
{
    lock (_occupancyMap)
    {
        // ... allocation logic ...
    }
}

// AFTER: AccessControl with IContentionTarget (contention tracked automatically)
public void AllocatePages(ref Span<int> pageIds, int startFrom = 0, ChangeSet changeSet = null)
{
    _occupancyMapAccess.EnterExclusiveAccess(target: this);  // 'this' is IContentionTarget
    try
    {
        // ... existing allocation logic (unchanged) ...
        while (_occupancyMap.Allocate(ref pageIds, startFrom, changeSet) == false)
        {
            GrowOccupancySegment(changeSet);
            _occupancyNextReservedPageIndex = AllocatePage(changeSet);
        }
    }
    finally
    {
        _occupancyMapAccess.ExitExclusiveAccess();
    }
}

public bool FreePages(ReadOnlySpan<int> pages, int startFrom = 0, ChangeSet changeSet = null)
{
    _occupancyMapAccess.EnterExclusiveAccess(target: this);
    try
    {
        _occupancyMap.Free(pages, startFrom, changeSet);
    }
    finally
    {
        _occupancyMapAccess.ExitExclusiveAccess();
    }
    return false;
}
```

**Why AccessControl over `lock`:**

| Aspect | `lock` | `AccessControl` |
|--------|--------|-----------------|
| Contention tracking | Manual instrumentation required | Built-in via `IContentionTarget` |
| Wait time measurement | Manual `Stopwatch` wrapping | Automatic when target provided |
| Timeout support | None | Via `Deadline` parameter |
| Reader/writer semantics | Exclusive only | Shared + Exclusive modes |
| Consistency with codebase | Inconsistent | Matches `PageInfo.StateSyncRoot`, `ComponentTable` latches |

### 2.4 IDebugPropertiesProvider Implementation

```csharp
public IReadOnlyDictionary<string, object> GetDebugProperties()
{
    var metrics = GetMetrics();
    metrics.GetMemPageExtraInfo(out var extraInfo);

    return new Dictionary<string, object>
    {
        // Page cache state histogram
        ["PageCache.FreeCount"] = extraInfo.FreeMemPageCount,
        ["PageCache.AllocatingCount"] = extraInfo.AllocatingMemPageCount,
        ["PageCache.IdleCount"] = extraInfo.IdleMemPageCount,
        ["PageCache.SharedCount"] = extraInfo.SharedMemPageCount,
        ["PageCache.ExclusiveCount"] = extraInfo.ExclusiveMemPageCount,
        ["PageCache.IdleAndDirtyCount"] = extraInfo.IdleAndDirtyMemPageCount,

        // I/O state
        ["PageCache.PendingIOReadCount"] = extraInfo.PendingIOReadCount,
        ["PageCache.LockedByThreadCount"] = extraInfo.LockedByThreadCount,

        // Clock-sweep state
        ["ClockSweep.MinCounter"] = extraInfo.MinClockSweepCounter,
        ["ClockSweep.MaxCounter"] = extraInfo.MaxClockSweepCounter,

        // Segment inventory
        ["Segments.Count"] = _segments?.Count ?? 0,

        // Occupancy map state
        ["OccupancyMap.TotalBitSet"] = _occupancyMap?.TotalBitSet ?? 0,

        // Contention details
        ["Contention.WaitCount"] = _contentionWaitCount,
        ["Contention.TotalWaitUs"] = _contentionTotalWaitUs,
        ["Contention.MaxWaitUs"] = _contentionMaxWaitUs,
    };
}
```

---

## 3. DatabaseEngine

**Priority:** High — Transaction metrics are essential for performance tuning.

### 3.1 Infrastructure to Add

```csharp
public class DatabaseEngine : IDisposable, IResource, IMetricSource, IDebugPropertiesProvider
{
    // ═══════════════════════════════════════════════════════════════
    // TRANSACTION COUNTERS
    // ═══════════════════════════════════════════════════════════════

    private long _transactionsCreated;
    private long _transactionsCommitted;
    private long _transactionsRolledBack;
    private long _transactionConflicts;

    // ═══════════════════════════════════════════════════════════════
    // DURATION TRACKING
    // ═══════════════════════════════════════════════════════════════

    private long _commitLastUs;
    private long _commitSumUs;
    private long _commitCount;
    private long _commitMaxUs;
}
```

> **Note:** `DatabaseEngine` does NOT implement `IContentionTarget`. All its internal data structures use lock-free synchronization (`ConcurrentDictionary`, `Interlocked`). The `TransactionChain` has its own `AccessControl`, but that contention is internal to transaction management and doesn't warrant exposing at the engine level.

### 3.2 IMetricSource Implementation

| Metric Kind | Sub-metrics | Source |
|-------------|-------------|--------|
| **Capacity** | Current (active txns), Maximum (pool size) | `TransactionChain` |
| **Throughput** | Created, Committed, RolledBack, Conflicts | New counters |
| **Duration** | Commit (LastUs, AvgUs, MaxUs) | New timing fields |

```csharp
public void ReadMetrics(IMetricWriter writer)
{
    // Capacity: active transactions vs max (if pool has a limit)
    long activeCount = TransactionChain.ActiveCount;
    long maxCount = _options?.Resources?.MaxActiveTransactions ?? 1000;
    writer.WriteCapacity(activeCount, maxCount);

    // Throughput: transaction lifecycle counts
    writer.WriteThroughput(MetricNames.Created, _transactionsCreated);
    writer.WriteThroughput(MetricNames.Committed, _transactionsCommitted);
    writer.WriteThroughput(MetricNames.RolledBack, _transactionsRolledBack);
    writer.WriteThroughput(MetricNames.Conflicts, _transactionConflicts);

    // Duration: commit timing
    var avgUs = _commitCount > 0 ? _commitSumUs / _commitCount : 0;
    writer.WriteDuration(MetricNames.Commit, _commitLastUs, avgUs, _commitMaxUs);
}

public void ResetPeaks()
{
    Volatile.Write(ref _commitMaxUs, 0);
    Volatile.Write(ref _commitSumUs, 0);
    Volatile.Write(ref _commitCount, 0);
}
```

### 3.3 IDebugPropertiesProvider Implementation

```csharp
public IReadOnlyDictionary<string, object> GetDebugProperties()
{
    return new Dictionary<string, object>
    {
        // Transaction chain state
        ["TransactionChain.ActiveCount"] = TransactionChain.ActiveCount,
        ["TransactionChain.MinTSN"] = TransactionChain.MinTSN,
        ["TransactionChain.CurrentTSN"] = TransactionChain.CurrentTSN,

        // Registered component types
        ["ComponentTables.Count"] = _componentTableByType?.Count ?? 0,

        // Primary key generation
        ["PrimaryKey.Current"] = _curPrimaryKey,

        // Transaction lifecycle counters
        ["Transactions.Created"] = _transactionsCreated,
        ["Transactions.Committed"] = _transactionsCommitted,
        ["Transactions.RolledBack"] = _transactionsRolledBack,
        ["Transactions.Conflicts"] = _transactionConflicts,

        // Commit duration stats
        ["Commit.LastUs"] = _commitLastUs,
        ["Commit.MaxUs"] = _commitMaxUs,
        ["Commit.Count"] = _commitCount,
    };
}
```

### 3.4 Instrumentation Points

**Transaction Creation:**
```csharp
public Transaction CreateTransaction()
{
    Interlocked.Increment(ref _transactionsCreated);
    return TransactionChain.CreateTransaction(this);
}
```

**Transaction Commit (in Transaction class, callback to engine):**
```csharp
internal void RecordCommitDuration(long durationUs)
{
    _commitLastUs = durationUs;
    if (durationUs > _commitMaxUs)
        _commitMaxUs = durationUs;
    Interlocked.Add(ref _commitSumUs, durationUs);
    Interlocked.Increment(ref _commitCount);
    Interlocked.Increment(ref _transactionsCommitted);
}

internal void RecordRollback()
{
    Interlocked.Increment(ref _transactionsRolledBack);
}

internal void RecordConflict()
{
    Interlocked.Increment(ref _transactionConflicts);
}
```

---

## 4. MemoryAllocator

**Priority:** Medium — Allocation tracking aids memory leak detection.

### 4.1 Infrastructure to Add

```csharp
public class MemoryAllocator : ServiceBase, IMemoryAllocator, IMetricSource, IDebugPropertiesProvider
{
    // ═══════════════════════════════════════════════════════════════
    // ALLOCATION TRACKING
    // ═══════════════════════════════════════════════════════════════

    private long _totalAllocatedBytes;
    private long _peakAllocatedBytes;
    private long _cumulativeAllocations;
    private long _cumulativeDeallocations;
}
```

### 4.2 IMetricSource Implementation

| Metric Kind | Sub-metrics | Source |
|-------------|-------------|--------|
| **Memory** | AllocatedBytes, PeakBytes | New tracking fields |
| **Capacity** | Current (blocks), Maximum | `_blocks.Count`, configurable max |
| **Throughput** | Allocations, Deallocations | New counters |

```csharp
public void ReadMetrics(IMetricWriter writer)
{
    // Memory: total bytes across all blocks
    writer.WriteMemory(_totalAllocatedBytes, _peakAllocatedBytes);

    // Capacity: active block count
    long blockCount = _blocks.Count;
    long maxBlocks = long.MaxValue;  // No hard limit currently
    writer.WriteCapacity(blockCount, maxBlocks);

    // Throughput: allocation lifecycle
    writer.WriteThroughput("Allocations", _cumulativeAllocations);
    writer.WriteThroughput("Deallocations", _cumulativeDeallocations);
}

public void ResetPeaks()
{
    Volatile.Write(ref _peakAllocatedBytes, _totalAllocatedBytes);
}
```

### 4.3 IDebugPropertiesProvider Implementation

```csharp
public IReadOnlyDictionary<string, object> GetDebugProperties()
{
    var blocks = _blocks.ToArray();  // Snapshot for consistency

    long arrayBlocks = 0, pinnedBlocks = 0;
    long arrayBytes = 0, pinnedBytes = 0;

    foreach (var block in blocks)
    {
        if (block is MemoryBlockArray mba)
        {
            arrayBlocks++;
            arrayBytes += mba.Size;
        }
        else if (block is PinnedMemoryBlock pmb)
        {
            pinnedBlocks++;
            pinnedBytes += pmb.Size;
        }
    }

    return new Dictionary<string, object>
    {
        // Overall stats
        ["Blocks.Total"] = blocks.Length,
        ["Bytes.Total"] = _totalAllocatedBytes,
        ["Bytes.Peak"] = _peakAllocatedBytes,

        // By type breakdown
        ["ArrayBlocks.Count"] = arrayBlocks,
        ["ArrayBlocks.Bytes"] = arrayBytes,
        ["PinnedBlocks.Count"] = pinnedBlocks,
        ["PinnedBlocks.Bytes"] = pinnedBytes,

        // Lifecycle counters
        ["Cumulative.Allocations"] = _cumulativeAllocations,
        ["Cumulative.Deallocations"] = _cumulativeDeallocations,
    };
}
```

### 4.4 Instrumentation Points

**Allocation Methods:**
```csharp
public MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false)
{
    // ... existing allocation logic ...

    Interlocked.Add(ref _totalAllocatedBytes, size);
    var peak = _totalAllocatedBytes;
    while (peak > _peakAllocatedBytes)
    {
        var current = Interlocked.CompareExchange(ref _peakAllocatedBytes, peak, _peakAllocatedBytes);
        if (current >= peak) break;
    }
    Interlocked.Increment(ref _cumulativeAllocations);

    _blocks.Add(mb);
    return mb;
}

internal void Remove(MemoryBlockBase block)
{
    Interlocked.Add(ref _totalAllocatedBytes, -block.Size);
    Interlocked.Increment(ref _cumulativeDeallocations);
    _blocks.Remove(block);
}
```

---

## 5. ConcurrentBitmapL3All

**Priority:** Medium — Bitmap utilization is useful for fragmentation analysis.

### 5.1 Infrastructure to Add

```csharp
public unsafe class ConcurrentBitmapL3All : IResource, IMetricSource, IDebugPropertiesProvider
{
    // ═══════════════════════════════════════════════════════════════
    // OPERATION COUNTERS
    // ═══════════════════════════════════════════════════════════════

    private long _setL0Count;
    private long _clearL0Count;
    private long _setL1Count;
    private long _growCount;
}
```

### 5.2 IMetricSource Implementation

| Metric Kind | Sub-metrics | Source |
|-------------|-------------|--------|
| **Capacity** | Current (bits set), Maximum, Utilization | Existing `TotalBitSet`, `Capacity` |
| **Throughput** | SetOperations, ClearOperations, BulkSets, Grows | New counters |

```csharp
public void ReadMetrics(IMetricWriter writer)
{
    // Capacity: bits set vs total capacity
    writer.WriteCapacity(TotalBitSet, Capacity);

    // Throughput: bitmap operations
    writer.WriteThroughput("SetL0", _setL0Count);
    writer.WriteThroughput("ClearL0", _clearL0Count);
    writer.WriteThroughput("SetL1", _setL1Count);  // Bulk sets (64 bits at once)
    writer.WriteThroughput("Grows", _growCount);
}

public void ResetPeaks()
{
    // No high-water marks to reset
}
```

### 5.3 IDebugPropertiesProvider Implementation

```csharp
public IReadOnlyDictionary<string, object> GetDebugProperties()
{
    var banks = _banks;
    var props = new Dictionary<string, object>
    {
        // Overall stats
        ["Banks.Count"] = banks?.Length ?? 0,
        ["Capacity.Total"] = Capacity,
        ["Capacity.Used"] = TotalBitSet,
        ["Capacity.Utilization"] = Capacity > 0 ? (double)TotalBitSet / Capacity : 0.0,
        ["IsFull"] = IsFull,

        // Per-bank capacity
        ["BankBitCountCapacity"] = BankBitCountCapacity,

        // Operation counters
        ["Operations.SetL0"] = _setL0Count,
        ["Operations.ClearL0"] = _clearL0Count,
        ["Operations.SetL1"] = _setL1Count,
        ["Operations.Grows"] = _growCount,
    };

    // Per-bank breakdown (if not too many banks)
    if (banks != null && banks.Length <= 8)
    {
        for (int i = 0; i < banks.Length; i++)
        {
            props[$"Bank[{i}].TotalBitSet"] = banks[i].TotalBitSet;
            props[$"Bank[{i}].IsFull"] = banks[i].IsFull;
        }
    }

    return props;
}
```

### 5.4 Instrumentation Points

> **Performance note:** Use plain `++` instead of `Interlocked.Increment` for these counters. These are hot-path operations called millions of times, and the counters are purely diagnostic — occasional missed increments from concurrent updates are acceptable.

**SetL0:**
```csharp
public bool SetL0(int bitIndex)
{
    // ... existing CAS logic ...

    if (/* successfully set the bit */)
    {
        _setL0Count++;  // Plain add — accept occasional misses
        // ... rest of existing code ...
        return true;
    }
    return false;
}
```

**SetL1:**
```csharp
public bool SetL1(int bitIndex)
{
    // ... existing CAS logic ...

    if (/* successfully claimed all 64 bits */)
    {
        _setL1Count++;  // Plain add — accept occasional misses
        // ... rest of existing code ...
        return true;
    }
    return false;
}
```

**ClearL0:**
```csharp
public bool ClearL0(int bitIndex)
{
    // ... existing CAS logic ...

    if (/* bit was set, now cleared */)
    {
        _clearL0Count++;  // Plain add — accept occasional misses
        // ... rest of existing code ...
    }
    return true;
}
```

**Grow:**
```csharp
public void Grow()
{
    // ... existing grow logic ...

    if (/* CAS succeeded */)
    {
        _growCount++;  // Plain add — accept occasional misses
    }
}
```

---

## 6. Implementation Priority & Phasing

### Phase 1: Critical Path (Transaction + Storage)

| Priority | Type | Justification |
|----------|------|---------------|
| **1** | ManagedPagedMMF | Page cache metrics are essential for capacity planning |
| **2** | DatabaseEngine | Transaction metrics are essential for performance tuning |

**Phase 1 deliverables:**
- Full `IMetricSource`, `IDebugPropertiesProvider` for both types
- `IContentionTarget` for ManagedPagedMMF only (DatabaseEngine uses lock-free structures)
- Integration tests verifying metrics appear in snapshots
- OTel export verification

### Phase 2: Complete Coverage

| Priority | Type | Justification |
|----------|------|---------------|
| **3** | MemoryAllocator | Allocation tracking aids leak detection |
| **4** | ConcurrentBitmapL3All | Bitmap utilization aids fragmentation analysis |

**Phase 2 deliverables:**
- `IMetricSource` and `IDebugPropertiesProvider` for both types
- No `IContentionTarget` needed (lock-free data structures)

---

## 7. Common Patterns

### 7.1 Contention Tracking with AccessControl

For resources that need `IContentionTarget`, use `AccessControl` for synchronization instead of `lock`. This provides automatic contention tracking:

```csharp
public class MyResource : IResource, IMetricSource, IContentionTarget
{
    // ═══════════════════════════════════════════════════════════════
    // SYNCHRONIZATION
    // ═══════════════════════════════════════════════════════════════

    private AccessControl _syncAccess;  // Replaces lock object

    // ═══════════════════════════════════════════════════════════════
    // CONTENTION COUNTERS (populated by AccessControl callbacks)
    // ═══════════════════════════════════════════════════════════════

    private long _contentionWaitCount;
    private long _contentionTotalWaitUs;
    private long _contentionMaxWaitUs;

    // ═══════════════════════════════════════════════════════════════
    // IContentionTarget IMPLEMENTATION
    // ═══════════════════════════════════════════════════════════════

    public TelemetryLevel TelemetryLevel => TelemetryLevel.Light;
    public IResource OwningResource => this;

    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalWaitUs, waitUs);
        if (waitUs > _contentionMaxWaitUs)
            _contentionMaxWaitUs = waitUs;
    }

    public void LogLockOperation(LockOperation operation, long durationUs) { }

    // ═══════════════════════════════════════════════════════════════
    // USAGE: Pass 'this' as target for automatic contention tracking
    // ═══════════════════════════════════════════════════════════════

    public void DoWork()
    {
        _syncAccess.EnterExclusiveAccess(target: this);  // 'this' is IContentionTarget
        try
        {
            // ... critical section ...
        }
        finally
        {
            _syncAccess.ExitExclusiveAccess();
        }
    }
}
```

**Why AccessControl over `lock`:**

| Aspect | `lock` | `AccessControl` |
|--------|--------|-----------------|
| Contention tracking | Manual instrumentation | Built-in via `IContentionTarget` |
| Wait time measurement | Requires `Stopwatch` wrapper | Automatic when target provided |
| Codebase consistency | Inconsistent | Matches existing Typhon patterns |
| Reader/writer modes | Exclusive only | Shared + Exclusive |

### 7.2 IMetricWriter Delegation Pattern

Extract metrics reading into a helper for testability:

```csharp
public void ReadMetrics(IMetricWriter writer)
{
    // Collect all values first (consistent snapshot)
    var snapshot = CaptureMetricSnapshot();

    // Then write (can't throw between reads)
    writer.WriteCapacity(snapshot.Current, snapshot.Maximum);
    writer.WriteThroughput(MetricNames.CacheHits, snapshot.CacheHits);
    // ...
}

private MetricSnapshot CaptureMetricSnapshot()
{
    return new MetricSnapshot
    {
        Current = ...,
        Maximum = ...,
        CacheHits = ...,
    };
}
```

### 7.3 Thread-Safe Counter Updates

| Counter Type | Update Pattern | Notes |
|--------------|----------------|-------|
| Throughput (single writer) | `_counter++` | No contention |
| Throughput (multiple writers, cold path) | `Interlocked.Increment(ref _counter)` | E.g., contention callbacks |
| Throughput (multiple writers, hot path) | `_counter++` | Accept occasional misses for performance |
| High-water mark | Plain check-and-write | Occasional lost max acceptable |
| Cumulative total (cold path) | `Interlocked.Add(ref _total, value)` | E.g., allocation tracking |

**Hot path guidance:** For diagnostic counters on hot paths (like `ConcurrentBitmapL3All` operations), prefer plain `++` over `Interlocked`. The ~5-10ns cost of atomic operations adds up at millions of calls/second, and ±1% accuracy is acceptable for monitoring.

### 7.4 Peak Update Pattern

For high-water marks that may be updated concurrently:

```csharp
// Simple (acceptable loss)
if (value > _peak)
    _peak = value;

// Atomic (if precision required)
while (true)
{
    var current = _peak;
    if (value <= current) break;
    if (Interlocked.CompareExchange(ref _peak, value, current) == current) break;
}
```

---

## 8. Testing Strategy

### 8.1 Unit Tests (Per-Type)

For each type implementing observability:

```csharp
[TestFixture]
public class ManagedPagedMMFMetricsTests
{
    [Test]
    public void ReadMetrics_ReportsCapacity()
    {
        using var mmf = CreateManagedPagedMMF();
        var writer = new TestMetricWriter();

        mmf.ReadMetrics(writer);

        Assert.That(writer.Capacity.HasValue, Is.True);
        Assert.That(writer.Capacity.Value.Maximum, Is.GreaterThan(0));
    }

    [Test]
    public void ReadMetrics_ReportsDiskIO()
    {
        using var mmf = CreateManagedPagedMMF();

        // Trigger some I/O
        mmf.RequestPage(0, true, out var pa);
        pa.Dispose();

        var writer = new TestMetricWriter();
        mmf.ReadMetrics(writer);

        Assert.That(writer.DiskIO.HasValue, Is.True);
    }

    [Test]
    public void RecordContention_UpdatesCounters()
    {
        using var mmf = CreateManagedPagedMMF();

        mmf.RecordContention(100);
        mmf.RecordContention(200);

        var props = mmf.GetDebugProperties();
        Assert.That(props["Contention.WaitCount"], Is.EqualTo(2));
        Assert.That(props["Contention.TotalWaitUs"], Is.EqualTo(300));
        Assert.That(props["Contention.MaxWaitUs"], Is.EqualTo(200));
    }
}
```

### 8.2 Integration Test

Verify all resource metrics appear in a unified snapshot:

```csharp
[Test]
public void ResourceSnapshot_CapturesAllMetrics()
{
    using var dbe = CreateDatabaseEngine();
    dbe.RegisterComponent<TestComponent>();

    var snapshot = ResourceGraph.GetSnapshot();

    // Verify ManagedPagedMMF metrics
    var mmfNode = snapshot.Nodes["Storage/ManagedPagedMMF_TestDB"];
    Assert.That(mmfNode.Capacity, Is.Not.Null);
    Assert.That(mmfNode.DiskIO, Is.Not.Null);

    // Verify DatabaseEngine metrics
    var dbeNode = snapshot.Nodes["DataEngine/DatabaseEngine_TestDB"];
    Assert.That(dbeNode.Capacity, Is.Not.Null);
    Assert.That(dbeNode.Throughput, Is.Not.Empty);
}
```

### 8.3 OTel Export Verification

```csharp
[Test]
public void OTelExport_IncludesNewMetrics()
{
    using var meterListener = new MeterListener();
    var observedMetrics = new List<string>();

    meterListener.InstrumentPublished = (instrument, listener) =>
    {
        observedMetrics.Add(instrument.Name);
        listener.EnableMeasurementEvents(instrument);
    };
    meterListener.Start();

    // Trigger metrics collection
    var exporter = new ResourceMetricsExporter(ResourceGraph);
    exporter.UpdateSnapshot();

    Assert.That(observedMetrics, Does.Contain("typhon.resource.storage.managed_paged_mmf.capacity.utilization"));
    Assert.That(observedMetrics, Does.Contain("typhon.resource.data_engine.database_engine.throughput.committed"));
}
```

---

## 9. Migration Notes

### 9.1 Non-Breaking Changes

Adding observability interfaces to existing types is **non-breaking**:
- `IMetricSource`, `IContentionTarget`, `IDebugPropertiesProvider` are optional interfaces
- Existing code that only uses `IResource` continues to work
- Snapshot collection handles missing metrics gracefully (null metric kinds)

### 9.2 Incremental Implementation

Interfaces can be added one at a time per type:

1. Add `IMetricSource` first (most valuable)
2. Add `IContentionTarget` if the type has locks
3. Add `IDebugPropertiesProvider` for drill-down diagnostics

### 9.3 Backwards Compatibility

```csharp
// ResourceSnapshot already handles missing metrics
var node = snapshot.Nodes["Storage/ManagedPagedMMF_TestDB"];

// Safe access patterns
var utilization = node.Capacity?.Utilization ?? 0.0;
var cacheHits = node.Throughput?.GetValueOrDefault("CacheHits") ?? 0;
```

---

## Related Documents

| Document | Relationship |
|----------|--------------|
| [03-metric-source.md](03-metric-source.md) | Defines `IMetricSource` interface |
| [04-metric-kinds.md](04-metric-kinds.md) | Defines the 6 metric kinds |
| [05-granularity-strategy.md](05-granularity-strategy.md) | Confirms these types should have metrics |
| [08-observability-bridge.md](08-observability-bridge.md) | Consumer of these metrics |
| [overview/09-observability.md](../../overview/09-observability.md) | `IContentionTarget` specification |

---

## Design Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| Implementation priority | ManagedPagedMMF, DatabaseEngine first | Page cache and transaction metrics are most operationally valuable |
| DatabaseEngine contention | No `IContentionTarget` | Uses `ConcurrentDictionary` and `Interlocked` (lock-free); `TransactionChain`'s internal `AccessControl` is encapsulated |
| ConcurrentBitmapL3All contention | No `IContentionTarget` | Lock-free design; Interlocked operations don't cause thread waits |
| MemoryAllocator contention | No `IContentionTarget` | Uses `ConcurrentCollection`, minimal contention |
| Lock instrumentation | Replace `lock` with `AccessControl` | Native `IContentionTarget` support; consistent with existing Typhon patterns |
| High-water mark updates | Plain check-and-write | Occasional lost max acceptable for diagnostics |
| Phase approach | Critical path first | Delivers value incrementally; validates patterns before full rollout |

---

*Document Version: 1.0*
*Last Updated: January 2026*
*Part of the Resource System Design series*

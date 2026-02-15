# DotCover Coverage Analysis — Typhon.Engine Assembly

**Date:** 2026-02-15
**Source:** `claude/scratch/DotCover.json` (DotCover 2025.3.2)
**Assembly coverage:** 73% (9,880 / 13,620 statements)

## Summary

| Metric | Value |
|--------|-------|
| Total methods | 1,547 |
| Methods at 0% | 415 (26%) |
| Methods at 1–99% | 234 (15%) |
| Methods at 100% | 898 (58%) |
| Total uncovered statements | 3,740 |
| Top 100 uncovered statements | 1,580 (42% of all uncovered) |

## Top 100 Uncovered Methods by Category

All 100 methods below are at **0% coverage**, sorted by category impact.

### Category Overview

| Category | Methods | Uncovered Stmts | Risk Level |
|----------|---------|-----------------|------------|
| **Telemetry / Observability** | 24 | 382 | Low — mostly boilerplate |
| **B+Tree Operations** | 10 | 313 | **HIGH — data integrity** |
| **Bitmap Data Structures** | 13 | 208 | **HIGH — core tracking** |
| **Transactions / MVCC** | 10 | 135 | **HIGH — ACID correctness** |
| **Storage / Persistence** | 7 | 122 | Medium — durability paths |
| **Concurrent Collections** | 8 | 95 | **HIGH — thread safety** |
| **Utility Methods** | 5 | 65 | Low — formatting helpers |
| **Memory Allocation** | 5 | 62 | Medium — resource management |
| **Concurrency Primitives** | 4 | 56 | **HIGH — lock correctness** |
| **Allocators / Buffers** | 5 | 55 | Medium — data access |
| **DI / Service Registration** | 6 | 54 | Low — wiring only |
| **Component / Schema** | 2 | 19 | Medium |
| **Database Engine Core** | 1 | 14 | Medium |

---

### Telemetry / Observability (24 methods, 382 uncovered stmts) — Low Risk

| Uncov | Type | Method |
|-------|------|--------|
| 44 | `ObservabilityBridgeExtensions` | `AddTyphonObservabilityBridge(IServiceCollection,Action<ObservabilityBridgeOptions>)` |
| 31 | `TelemetryServiceExtensions` | `AddTyphonTelemetry(IServiceCollection,IConfiguration)` |
| 24 | `ResourceTelemetryAllocator` | `AppendOperation(ref int,in ResourceOperationEntry)` |
| 21 | `ResourceTelemetryAllocator` | `GetChainEntries(int)` |
| 16 | `ResourceMetricsExporter` | `EnumerateDurationAvgUs()` |
| 16 | `ResourceMetricsExporter` | `EnumerateDurationLastUs()` |
| 16 | `ResourceMetricsExporter` | `EnumerateDurationMaxUs()` |
| 16 | `ResourceMetricsExporter` | `EnumerateThroughputCounts()` |
| 14 | `TelemetryConfig` | `GetActiveComponentsSummary()` |
| 13 | `ResourceMetricsExporter` | `EnumerateCapacityCurrent()` |
| 13 | `ResourceMetricsExporter` | `EnumerateCapacityMaximum()` |
| 13 | `ResourceMetricsExporter` | `EnumerateCapacityUtilization()` |
| 13 | `ResourceMetricsExporter` | `EnumerateContentionMaxWaitUs()` |
| 13 | `ResourceMetricsExporter` | `EnumerateContentionTimeoutCount()` |
| 13 | `ResourceMetricsExporter` | `EnumerateContentionTotalWaitUs()` |
| 13 | `ResourceMetricsExporter` | `EnumerateContentionWaitCount()` |
| 13 | `ResourceMetricsExporter` | `EnumerateDiskIOReadBytes()` |
| 13 | `ResourceMetricsExporter` | `EnumerateDiskIOReadOps()` |
| 13 | `ResourceMetricsExporter` | `EnumerateDiskIOWriteBytes()` |
| 13 | `ResourceMetricsExporter` | `EnumerateDiskIOWriteOps()` |
| 13 | `ResourceMetricsExporter` | `EnumerateMemoryAllocatedBytes()` |
| 13 | `ResourceMetricsExporter` | `EnumerateMemoryPeakBytes()` |
| 8 | `TraceIdEnricher` | `Enrich(LogEvent,ILogEventPropertyFactory)` |
| 7 | `TelemetryServiceExtensions` | `AddTyphonTelemetry(IServiceCollection)` |

**Notes:** 16 nearly-identical `Enumerate*()` methods on `ResourceMetricsExporter`. A single integration test wiring up the metrics pipeline would cover most of these. Easiest coverage number boost.

---

### B+Tree Operations (10 methods, 313 uncovered stmts) — HIGH Risk

| Uncov | Type | Method |
|-------|------|--------|
| 85 | `L16BTree<TKey>.L16NodeStorage` | `MergeLeft(...)` |
| 85 | `String64BTree.String64NodeStorage` | `MergeLeft(...)` |
| 39 | `L16BTree<TKey>.L16NodeStorage` | `LeftShift(ref Index16Chunk,int,int)` |
| 34 | `L16BTree<TKey>.L16NodeStorage` | `SplitRight(ref Index16Chunk,NodeStates,ref ChunkAccessor)` |
| 18 | `L16BTree<TKey>.L16NodeStorage` | `RemoveAt(...)` |
| 18 | `String64BTree.String64NodeStorage` | `RemoveAt(...)` |
| 9 | `L16BTree<TKey>.L16NodeStorage` | `DecrementStart(ref Index16Chunk)` |
| 9 | `L64BTree<TKey>.L64NodeStorage` | `DecrementStart(ref Index64Chunk)` |
| 8 | `L16BTree<TKey>.L16NodeStorage` | `PushFirst(...)` |
| 8 | `L64BTree<TKey>.L64NodeStorage` | `PushFirst(...)` |

**Notes:** These are the B+Tree **deletion and rebalancing** operations — `MergeLeft`, `SplitRight`, `RemoveAt`, `LeftShift`, `DecrementStart`, `PushFirst`. Insertion paths are well-covered but node underflow/overflow during deletion is completely dark. Untested tree rebalancing is how silent index corruption happens.

---

### Bitmap Data Structures (13 methods, 208 uncovered stmts) — HIGH Risk

| Uncov | Type | Method |
|-------|------|--------|
| 61 | `BitmapL3Any.Enumerator` | `MoveNext()` |
| 21 | `BitmapL3Any` | `Clear(int)` |
| 21 | `BitmapL3Any` | `Set(int)` |
| 19 | `ConcurrentBitmapL3Any` | `Clear(int)` |
| 15 | `ConcurrentBitmapL3All` | `GetDebugProperties()` |
| 13 | `ConcurrentBitmapL3All` | `Dispose()` |
| 11 | `BitmapL3Any` | `BitmapL3Any(int)` (constructor) |
| 9 | `ChunkBasedSegment.BitmapL3` | `Free(ReadOnlySpan<uint>)` |
| 9 | `ChunkBasedSegment.BitmapL3` | `IsSet(int)` |
| 8 | `BitmapL3Any` | `ForEach(Action<int>)` |
| 8 | `ConcurrentBitmapL3Any` | `ForEach(Action<int>)` |
| 7 | `ConcurrentBitmapL3All` | `ReadMetrics(IMetricWriter)` |
| 6 | `BitmapL3Any.Enumerator` | `Enumerator(BitmapL3Any)` (constructor) |

**Notes:** Bitmaps are used for entity presence tracking and versioning. The `BitmapL3Any` class (non-concurrent variant) is almost entirely untested — `Set`, `Clear`, `MoveNext` enumerator. `ConcurrentBitmapL3Any.Clear` is also dark.

---

### Transactions / MVCC (10 methods, 135 uncovered stmts) — HIGH Risk

| Uncov | Type | Method |
|-------|------|--------|
| 22 | `RevisionWalker` | `Step(int,bool,out bool)` |
| 19 | `DeferredCleanupManager` | `RemoveFromList(long,ComponentTable,long)` |
| 18 | `UowRegistry` | `WaitForSlotFreed(ref WaitContext)` |
| 15 | `UowRegistry` | `EnsureCapacity(int)` |
| 12 | `TransactionChain` | `WalkHeadToTail(Func<Transaction,bool>)` |
| 10 | `Transaction.ConcurrencyConflictSolver.Entry` | Constructor |
| 10 | `Transaction.ConcurrencyConflictSolver` | `Reset()` |
| 10 | `Transaction` | `GetConflictSolver()` |
| 10 | `Transaction` | `UpdateEntity<TC1,TC2,TC3>(long,ref TC1,ref TC2,ref TC3)` |
| 9 | `RevisionWalker` | Constructor |

**Notes:** Write-write conflict resolution (`ConcurrencyConflictSolver`), MVCC garbage collection (`DeferredCleanupManager.RemoveFromList`, `RevisionWalker.Step`), and UoW capacity management are all untested. These are the paths that fire under contention and during long-running workloads.

---

### Storage / Persistence (7 methods, 122 uncovered stmts) — Medium Risk

| Uncov | Type | Method |
|-------|------|--------|
| 47 | `PagedMMF` | `GetMemPageExtraInfo(out Metrics.MemPageExtraInfo)` |
| 18 | `LogicalSegment` | `WalkIndicesMap(PageMapWalkPredicate,long)` |
| 15 | `LogicalSegment` | `Fill(byte)` |
| 14 | `LogicalSegment` | `Clear()` |
| 13 | `ManagedPagedMMF` | `ReadMetrics(IMetricWriter)` |
| 8 | `ManagedPagedMMF` | `RecordContention(long)` |
| 7 | `PagedMMF` | `DeleteDatabaseFile()` |

---

### Concurrent Collections (8 methods, 95 uncovered stmts) — HIGH Risk

| Uncov | Type | Method |
|-------|------|--------|
| 28 | `ConcurrentArray<T>` | `Remove(int,TimeSpan)` |
| 16 | `ConcurrentArray<T>` | `Add(T)` |
| 11 | `ConcurrentArray<T>` | `PutBack(int,T)` |
| 10 | `ConcurrentCollection<T>.Enumerator` | `MoveNext()` |
| 9 | `ConcurrentArray<T>` | `Pick(int,out T)` |
| 7 | `ConcurrentArray<T>` | Constructor |
| 7 | `ConcurrentArray<T>` | Property getter |
| 7 | `ConcurrentArray<T>` | `Release(int)` |

**Notes:** `ConcurrentArray<T>` is **entirely untested** — every single operation is at 0%. Lock-free/concurrent data structures are notoriously tricky. High-value testing target.

---

### Utility Methods (5 methods, 65 uncovered stmts) — Low Risk

| Uncov | Type | Method |
|-------|------|--------|
| 14 | `MathExtensions` | `FriendlyAmount(int)` |
| 14 | `MathExtensions` | `FriendlyAmount(double)` |
| 14 | `MathExtensions` | `FriendlySize(long)` |
| 14 | `MathExtensions` | `FriendlySize(double)` |
| 9 | `MathExtensions` | `NextPowerOf2(int)` |

---

### Memory Allocation (5 methods, 62 uncovered stmts) — Medium Risk

| Uncov | Type | Method |
|-------|------|--------|
| 24 | `MemoryAllocator` | `GetDebugProperties()` |
| 15 | `MemoryAllocator` | `AllocateArray(string,IResource,int,bool)` |
| 8 | `MemoryBlockArray` | `Pin(int)` |
| 8 | `MemoryBlockArray` | `Unpin()` |
| 7 | `MemoryAllocator` | `ReadMetrics(IMetricWriter)` |

---

### Concurrency Primitives (4 methods, 56 uncovered stmts) — HIGH Risk

| Uncov | Type | Method |
|-------|------|--------|
| 20 | `AccessControl.LockData` | `WaitForSharedCanStart()` |
| 17 | `AccessControlSmall` | `DemoteFromExclusiveAccess(IContentionTarget)` |
| 12 | `EpochThreadRegistry` | `TryReclaimDeadSlot(int,Thread)` |
| 7 | `AdaptiveWaiter` | `Wait(ref WaitContext)` |

**Notes:** These are the **contention paths** of the locking system. The uncontended happy paths are covered, but the paths that fire when threads actually compete are not.

---

### Allocators / Buffers (5 methods, 55 uncovered stmts) — Medium Risk

| Uncov | Type | Method |
|-------|------|--------|
| 19 | `ChainedBlockAllocatorBase` | `SafeAppend(int,out int)` |
| 16 | `BufferEnumerator<T>` | `MoveNext()` |
| 8 | `ChainedBlockAllocator<T>.Enumerator` | `MoveNext()` |
| 6 | `BufferEnumerator<T>` | Constructor |
| 6 | `ChainedBlockAllocator<T>.Enumerator` | Constructor |

---

### DI / Service Registration (6 methods, 54 uncovered stmts) — Low Risk

| Uncov | Type | Method |
|-------|------|--------|
| 10 | `ServiceCollectionExtensions` | `AddScopedMemoryAllocator(...)` |
| 10 | `ServiceCollectionExtensions` | `AddTransientMemoryAllocator(...)` |
| 9 | `ServiceCollectionExtensions` | `AddScopedResourceRegistry(...)` |
| 9 | `ServiceCollectionExtensions` | `AddTransientResourceRegistry(...)` |
| 8 | `ServiceCollectionExtensions` | `AddScopedEpochManager(...)` |
| 8 | `ServiceCollectionExtensions` | `AddTransientEpochManager(...)` |

---

### Component / Schema (2 methods, 19 uncovered stmts) — Medium Risk

| Uncov | Type | Method |
|-------|------|--------|
| 13 | `ComponentCollectionAccessor<T>` | `GetAllElements()` |
| 6 | `DBComponentDefinition` | `GetFieldId(string)` |

---

### Database Engine Core (1 method, 14 uncovered stmts) — Medium Risk

| Uncov | Type | Method |
|-------|------|--------|
| 14 | `DatabaseEngine` | `ReadMetrics(IMetricWriter)` |

---

## Recommended Testing Priority

1. **B+Tree deletion/rebalancing** — highest data corruption risk (313 stmts)
2. **ConcurrentArray<T>** — entirely untested concurrent data structure (95 stmts)
3. **Transaction conflict resolution** — ACID correctness under contention (135 stmts)
4. **Concurrency primitive contention paths** — lock correctness edge cases (56 stmts)
5. **Bitmap operations** — core entity tracking (208 stmts)
6. **Storage/Persistence paths** — durability correctness (122 stmts)
7. **Telemetry / DI** — easy coverage boost, low risk (436 stmts combined)

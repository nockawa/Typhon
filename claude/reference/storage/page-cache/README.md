# Page Cache System Reference

> **TL;DR** — Jump to [Quick Start](#quick-start) for common usage patterns, or see [Architecture Overview](#architecture-overview) for the big picture.

This reference documents Typhon's page caching system—a sophisticated multi-layered storage architecture providing microsecond-level access to persistent data through memory-mapped files, clock-sweep eviction, and hierarchical bitmap allocation.

## Document Index

| Document | Description |
|----------|-------------|
| [README.md](README.md) | This file - overview and quick start |
| [01-page-layout.md](01-page-layout.md) | Page structure, headers, and memory layout |
| [02-page-states.md](02-page-states.md) | Page state machine and lifecycle |
| [03-clock-sweep.md](03-clock-sweep.md) | Clock-sweep eviction algorithm |
| [04-segments.md](04-segments.md) | LogicalSegment and ChunkBasedSegment |
| [05-bitmap-l3.md](05-bitmap-l3.md) | Hierarchical bitmap allocation |
| [06-changesets.md](06-changesets.md) | Dirty page tracking and I/O batching |
| [07-concurrency.md](07-concurrency.md) | Thread safety and synchronization |

## Quick Start

### Requesting a Page for Reading (Epoch-Protected)

```csharp
// Enter epoch scope and access page via raw pointer
var guard = EpochGuard.Enter(epochManager);
var epoch = epochManager.GlobalEpoch;

pagedMMF.RequestPageEpoch(filePageIndex, epoch, out int memPageIndex);
byte* addr = pagedMMF.GetMemPageAddress(memPageIndex);

// Access page data (8000 bytes usable, starting after header)
var data = new ReadOnlySpan<byte>(addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);

guard.Dispose();
```

### Requesting a Page for Writing

```csharp
// Enter epoch scope, get page, latch for exclusive write
var guard = EpochGuard.Enter(epochManager);
var epoch = epochManager.GlobalEpoch;

pagedMMF.RequestPageEpoch(filePageIndex, epoch, out int memPageIndex);
pagedMMF.TryLatchPageExclusive(memPageIndex);

byte* addr = pagedMMF.GetMemPageAddress(memPageIndex);
// Modify data via pointer...

changeSet.AddByMemPageIndex(memPageIndex);
pagedMMF.UnlatchPageExclusive(memPageIndex);
changeSet.SaveChanges();

guard.Dispose();
```

### Allocating Chunks in a Segment

```csharp
// Allocate a single chunk
int chunkId = segment.AllocateChunk(clearContent: true);

// Get chunk data via epoch-based accessor
using var accessor = segment.CreateEpochChunkAccessor(epoch, changeSet);
byte* ptr = accessor.GetChunkAddress(chunkId, dirty: true);
ref MyStruct data = ref Unsafe.AsRef<MyStruct>(ptr);
```

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      DatabaseEngine                             │
│         (Transactions, ComponentTables, Indexes)                │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                     ManagedPagedMMF                             │
│    (Page allocation, Segments, Occupancy tracking)              │
│  ┌──────────────┐  ┌───────────────┐  ┌──────────────────┐      │
│  │ BitmapL3     │  │ LogicalSegment│  │ChunkBasedSegment │      │
│  │ (allocation) │  │ (multi-page)  │  │ (fixed chunks)   │      │
│  └──────────────┘  └───────────────┘  └──────────────────┘      │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                        PagedMMF                                 │
│         (Page cache, Clock-sweep, Async I/O)                    │
│  ┌──────────────┐  ┌───────────────┐  ┌──────────────────┐      │
│  │ PageCache    │  │ PageInfo[]    │  │ EpochManager     │      │
│  │ (pinned mem) │  │ (state/meta)  │  │ (eviction prot.) │      │
│  └──────────────┘  └───────────────┘  └──────────────────┘      │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Operating System                             │
│              (File I/O, Memory mapping)                         │
└─────────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

| Layer | Class | Responsibility |
|-------|-------|----------------|
| **Storage** | `PagedMMF` | Page cache, eviction, async I/O, state machine |
| **Management** | `ManagedPagedMMF` | Page allocation, segments, occupancy bitmaps |
| **Segments** | `LogicalSegment` | Multi-page abstraction with linked pages |
| **Chunks** | `ChunkBasedSegment` | Fixed-size allocation with 3-level bitmap |
| **Protection** | `EpochManager` | Epoch-scoped page eviction protection |
| **Access** | `EpochChunkAccessor` | 16-slot SOA cache for chunk access |

## Key Constants

| Constant | Value | Description |
|----------|-------|-------------|
| `PageSize` | 8,192 bytes | Total page size |
| `PageHeaderSize` | 192 bytes | Header + metadata |
| `PageRawDataSize` | 8,000 bytes | Usable data per page |
| `DefaultCacheSize` | 2 MiB | Default cache (256 pages) |
| `MinimumCacheSize` | 2 MiB | Minimum allowed |
| `MaximumCacheSize` | 4 GiB | Maximum allowed |

## Performance Characteristics

| Operation | Typical Latency | Notes |
|-----------|-----------------|-------|
| Cache hit (epoch read) | < 1 μs | Epoch-protected raw pointer access |
| Cache hit (exclusive latch) | 1-5 μs | State transition Idle → Exclusive |
| Cache miss | 10-100 μs | Disk I/O required |
| Chunk allocation | < 1 μs | Hierarchical bitmap skip |
| Batch write (contiguous) | 50-500 μs | Grouped I/O |

## Related Documentation

- **Architecture**: [claude/overview/03-storage.md](../../overview/03-storage.md)
- **ADRs**: 
  - [006-8kb-page-size.md](../../adr/006-8kb-page-size.md)
  - [007-clock-sweep-eviction.md](../../adr/007-clock-sweep-eviction.md)
  - [008-chunk-based-segments.md](../../adr/008-chunk-based-segments.md)
- **Source Code**:
  - `src/Typhon.Engine/Storage/PagedMMF.cs`
  - `src/Typhon.Engine/Storage/ManagedPagedMMF.cs`
  - `src/Typhon.Engine/Storage/Segments/`



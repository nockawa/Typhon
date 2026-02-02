# Segments: LogicalSegment and ChunkBasedSegment

This document describes Typhon's segment abstractions that provide multi-page storage with automatic growth.

## Overview

Segments abstract over multiple physical pages to provide a contiguous logical address space:

```
┌─────────────────────────────────────────────────────────────────┐
│                        LogicalSegment                            │
│   Provides: Multi-page abstraction, linked page directory       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      ChunkBasedSegment                           │
│   Adds: Fixed-size chunk allocation, 3-level occupancy bitmap   │
└─────────────────────────────────────────────────────────────────┘
```

---

## LogicalSegment

`LogicalSegment` manages a collection of pages with a directory structure that supports growth.

### Core Structure

```csharp
public class LogicalSegment : IDisposable
{
    protected readonly ManagedPagedMMF _manager;
    private volatile int[] _pages;        // Page directory
    private readonly object _growLock = new();
    
    public int Length => _pages.Length;   // Number of pages
    public ReadOnlySpan<int> Pages => _pages;
}
```

### Constants

```csharp
// Root page stores first 500 page indices
internal const int RootHeaderIndexSectionCount = 500;
internal const int RootHeaderIndexSectionLength = 500 * sizeof(int);  // 2000 bytes

// Overflow pages store 2000 indices each
internal const int NextHeadersIndexSectionCount = 2000;  // 8000 / 4
```

### Root Page Layout

The first page (root) has a special structure:

```
Root Page (8192 bytes):
┌─────────────────────────────────────────────────────────────────┐
│ PageBaseHeader (64 bytes)                                        │
│   Flags = IsLogicalSegment | IsLogicalSegmentRoot               │
├─────────────────────────────────────────────────────────────────┤
│ LogicalSegmentHeader (8 bytes, at offset 64)                    │
│   LogicalSegmentNextMapPBID    (int) → next index page          │
│   LogicalSegmentNextRawDataPBID (int) → next data page          │
├─────────────────────────────────────────────────────────────────┤
│ PageMetadata (128 bytes)                                         │
│   Used by ChunkBasedSegment for occupancy bitmap                │
├─────────────────────────────────────────────────────────────────┤
│ Index Section (2000 bytes)                                       │
│   int[500] page indices (0 = end marker)                        │
├─────────────────────────────────────────────────────────────────┤
│ Data Section (6000 bytes)                                        │
│   Available for actual data                                      │
└─────────────────────────────────────────────────────────────────┘
```

### Overflow Index Pages

When a segment exceeds 500 pages, overflow pages store additional indices:

```
Overflow Page (8192 bytes):
┌─────────────────────────────────────────────────────────────────┐
│ PageBaseHeader (64 bytes)                                        │
│   Flags = IsLogicalSegment (NOT root)                           │
├─────────────────────────────────────────────────────────────────┤
│ LogicalSegmentHeader (8 bytes)                                  │
│   LogicalSegmentNextMapPBID → next overflow (or 0)              │
├─────────────────────────────────────────────────────────────────┤
│ PageMetadata (128 bytes)                                         │
├─────────────────────────────────────────────────────────────────┤
│ Index Section (8000 bytes)                                       │
│   int[2000] page indices (0 = end marker)                       │
└─────────────────────────────────────────────────────────────────┘
```

### Capacity Scaling

| Index Pages | Max Segment Pages | Max Data |
|-------------|-------------------|----------|
| 1 (root only) | 500 | ~4 MB |
| 2 | 2,500 | ~20 MB |
| 3 | 4,500 | ~36 MB |
| n | 500 + (n-1) × 2000 | ~8 KB × pages |

### Two Linked Lists

The segment maintains two separate chains:

1. **Map Chain** (`LogicalSegmentNextMapPBID`): Links index/directory pages
2. **Data Chain** (`LogicalSegmentNextRawDataPBID`): Links data pages

```
Map Chain:     Root ──► Overflow1 ──► Overflow2 ──► 0
Data Chain:    Root ──► Data1 ──► Data2 ──► Data3 ──► ... ──► 0
```

### Growth Algorithm

```csharp
public void Grow(int newLength, bool clearNewPages, ChangeSet changeSet = null)
{
    lock (_growLock)
    {
        var curPages = _pages;
        if (newLength <= curPages.Length)
            return;
        
        // Allocate new page array
        var newPages = new int[newLength];
        curPages.CopyTo(newPages.AsSpan());
        
        // Allocate physical pages from ManagedPagedMMF
        Span<int> newPageSpan = newPages.AsSpan(curPages.Length);
        _manager.AllocatePages(ref newPageSpan, curPages.Length, changeSet);
        
        // Update directory structure
        UpdateIndexDirectory(newPages, curPages.Length, changeSet);
        
        // Atomically publish new array
        _pages = newPages;
    }
}
```

### Item Location Calculation

```csharp
// Calculate items per page
public static int GetMaxItemCount<T>(bool firstPage) where T : unmanaged
{
    int dataSize = firstPage 
        ? PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength  // 6000
        : PagedMMF.PageRawDataSize;                                 // 8000
    return dataSize / Marshal.SizeOf<T>();
}

// Map item index to (pageIndex, offsetWithinPage)
public static (int pageIndex, int offset) GetItemLocation(int itemIndex, int itemSize)
{
    int firstPageCapacity = (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength) / itemSize;
    
    if (itemIndex < firstPageCapacity)
        return (0, itemIndex);
    
    int adjusted = itemIndex - firstPageCapacity;
    int pageCapacity = PagedMMF.PageRawDataSize / itemSize;
    int pageIndex = Math.DivRem(adjusted, pageCapacity, out int offset);
    
    return (pageIndex + 1, offset);
}
```

### Page Access

```csharp
// Get shared (read) access to a segment page
public void GetPageSharedAccessor(int segmentPageIndex, out PageAccessor accessor)
{
    int filePageIndex = _pages[segmentPageIndex];
    _manager.RequestPage(filePageIndex, exclusive: false, out accessor);
}

// Get exclusive (write) access
public void GetPageExclusiveAccessor(int segmentPageIndex, out PageAccessor accessor)
{
    int filePageIndex = _pages[segmentPageIndex];
    _manager.RequestPage(filePageIndex, exclusive: true, out accessor);
}
```

---

## ChunkBasedSegment

`ChunkBasedSegment` extends `LogicalSegment` with fixed-size chunk allocation and a 3-level occupancy bitmap.

### Core Structure

```csharp
public partial class ChunkBasedSegment : LogicalSegment
{
    private readonly object _growLock = new();
    private volatile BitmapL3 _map;
    
    // Fast division optimization
    private readonly int _rootChunkCount;
    private readonly int _otherChunkCount;
    private readonly ulong _divMagic;
    
    public int Stride { get; }              // Bytes per chunk
    public int ChunkCountRootPage { get; }  // Chunks on root page
    public int ChunkCountPerPage { get; }   // Chunks on other pages
    public int ChunkCapacity => _map.Capacity;
    public int AllocatedChunkCount => _map.Allocated;
}
```

### Chunk Capacity

```csharp
// Constructor calculates capacities
public ChunkBasedSegment(ManagedPagedMMF manager, int stride) : base(manager)
{
    Stride = stride;
    
    // Root page: 6000 bytes available for chunks
    ChunkCountRootPage = (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength) / stride;
    
    // Other pages: 8000 bytes available
    ChunkCountPerPage = PagedMMF.PageRawDataSize / stride;
    
    // Cache for fast division
    _rootChunkCount = ChunkCountRootPage;
    _otherChunkCount = ChunkCountPerPage;
    
    // Magic multiplier: avoids expensive division in hot path
    _divMagic = (0x1_0000_0000UL + (uint)_otherChunkCount - 1) / (uint)_otherChunkCount;
}
```

**Example** (64-byte stride):
- Root page: 6000 / 64 = **93 chunks**
- Other pages: 8000 / 64 = **125 chunks**

### Chunk Location (Hot Path)

The most critical method - called constantly during B+Tree operations:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
public (int segmentIndex, int offset) GetChunkLocation(int chunkIndex)
{
    // Fast path: chunk is on root page
    if (chunkIndex < _rootChunkCount)
        return (0, chunkIndex);
    
    // Adjust for root page offset
    var adjusted = (uint)(chunkIndex - _rootChunkCount);
    
    // Magic multiplier fast division: quotient = (n * magic) >> 32
    // Replaces 20-80 cycle idiv with 3-4 cycle imul + shift
    var pageIndex = (int)((adjusted * _divMagic) >> 32);
    
    // Remainder
    var offset = (int)(adjusted - (uint)(pageIndex * _otherChunkCount));
    
    return (pageIndex + 1, offset);
}
```

### Chunk Allocation

```csharp
// Allocate single chunk
public int AllocateChunk(bool clearContent)
{
    while (true)
    {
        var map = _map;  // Volatile read
        
        var mem = SingleAlloc.Value;  // ThreadLocal buffer
        if (map.Allocate(mem, clearContent))
            return mem.Span[0];
        
        // Allocation failed - grow segment
        if (!GrowIfNeeded())
            throw new InvalidOperationException("Segment at maximum capacity");
    }
}

// Allocate multiple chunks
public IMemoryOwner<int> AllocateChunks(int count, bool clearContent)
{
    var result = MemoryPool<int>.Shared.Rent(count);
    var memory = result.Memory[..count];
    
    while (true)
    {
        var map = _map;
        if (map.Allocate(memory, clearContent))
            return result;
        
        // Calculate minimum growth needed
        int chunksNeeded = count - map.FreeChunkCount;
        int pagesNeeded = (chunksNeeded + ChunkCountPerPage - 1) / ChunkCountPerPage;
        
        if (!Grow(Length + pagesNeeded, null))
        {
            result.Dispose();
            throw new InvalidOperationException("Cannot grow segment");
        }
    }
}

// Free a chunk
public void FreeChunk(int chunkId) => _map.ClearL0(chunkId);

// Reserve a specific chunk (e.g., chunk 0 as "null")
public void ReserveChunk(int index) => _map.SetL0(index);
```

### Segment Growth

```csharp
public bool Grow(int minNewPageCount = 0, ChangeSet changeSet = null)
{
    lock (_growLock)
    {
        var currentLength = Length;
        var oldMap = _map;
        
        // Double size or use minimum requested
        var newLength = minNewPageCount > 0
            ? Math.Max(currentLength * 2, minNewPageCount)
            : currentLength * 2;
        
        // Clamp to maximum
        newLength = Math.Min(newLength, MaxPageCount);
        
        if (newLength <= currentLength)
            return false;
        
        // 1. Grow underlying LogicalSegment
        base.Grow(newLength, clearNewPages: true, changeSet);
        
        // 2. Clear metadata (bitmap) for new pages
        for (int i = currentLength; i < newLength; i++)
        {
            GetPageExclusiveAccessor(i, out var page);
            using (page)
            {
                // Clear occupancy bitmap in metadata
                int longCount = (ChunkCountPerPage + 63) >> 6;
                page.PageMetadata.Cast<byte, long>().Slice(0, longCount).Clear();
                changeSet?.Add(page);
            }
        }
        
        // 3. Extend bitmap (copies L1/L2 from old, new pages are zeroed)
        _map = new BitmapL3(this, oldMap, currentLength);
        
        return true;
    }
}
```

### Chunk Data Layout

```
Page with 64-byte chunks (non-root):

Offset 0                                              Offset 8192
┌────────────────────┬─────────────────────────────────────────────┐
│ Header (192 bytes) │           Chunk Data (8000 bytes)           │
└────────────────────┼─────┬─────┬─────┬─────┬─────┬───────────────┤
                     │  0  │  1  │  2  │ ... │ 124 │   (padding)   │
                     │64 B │64 B │64 B │     │64 B │               │
                     └─────┴─────┴─────┴─────┴─────┴───────────────┘

Chunks per page = 8000 / 64 = 125
Padding = 8000 - (125 × 64) = 8000 - 8000 = 0 bytes
```

### Occupancy Bitmap (Per-Page)

Each page's metadata (128 bytes) stores the L0 level of the occupancy bitmap:

```
PageMetadata (128 bytes = 1024 bits):

┌──────────────────────────────────────────────────────────────────┐
│ long[0]  │ long[1]  │ long[2]  │ ... │ long[15]                 │
│ bits 0-63│bits 64-127│bits 128-191│   │ bits 960-1023           │
└──────────────────────────────────────────────────────────────────┘

Each bit = 1 chunk slot
1 = allocated, 0 = free
```

### ChunkAccessor Integration

```csharp
// Create accessor for chunk operations
using var accessor = new ChunkAccessor(segment, changeSet);

// Get chunk address
byte* ptr = accessor.GetChunkAddress(chunkId, dirty: true);

// Access as typed data
ref MyComponent comp = ref Unsafe.AsRef<MyComponent>(ptr);
comp.Value = 42;

// Accessor handles:
// - Page caching (16-slot SIMD cache)
// - Dirty tracking
// - Automatic flush on dispose
```

---

## Comparison

| Feature | LogicalSegment | ChunkBasedSegment |
|---------|----------------|-------------------|
| Page management | ✓ | ✓ (inherited) |
| Growth | ✓ | ✓ + bitmap extension |
| Allocation tracking | ✗ | ✓ (BitmapL3) |
| Fixed-size items | ✗ | ✓ (chunks) |
| Variable-size items | ✓ (manual) | ✗ |
| Use cases | Occupancy maps, schemas | Components, B+Trees |

## Summary

- **LogicalSegment**: Multi-page abstraction with directory structure
  - Root page: 500 indices + 6000 bytes data
  - Overflow pages: 2000 indices each
  - Two linked lists: map chain + data chain

- **ChunkBasedSegment**: Fixed-size allocation on top of LogicalSegment
  - Magic multiplier for fast chunk location
  - 3-level bitmap for O(1) allocation
  - Automatic growth with bitmap extension
  - Integrates with ChunkAccessor for cached access

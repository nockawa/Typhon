# Page and Chunk Accessors

This document describes the accessor types that provide safe, efficient access to page and chunk data.

## PageAccessor

`PageAccessor` is a RAII (Resource Acquisition Is Initialization) struct that manages access to a single cached page.

### Structure

```csharp
public unsafe struct PageAccessor : IDisposable
{
    // Owner reference
    private readonly PagedMMF _owner;
    private readonly int _filePageIndex;
    private PagedMMF.PageInfo _pi;
    
    // State tracking
    private PagedMMF.PageState _previousMode;  // For promotion/demotion
    private bool _isReady;                      // Data loaded from disk?
    
    // Direct memory access
    private readonly byte* _pageAddress;        // Raw pointer to page
}
```

### Properties

```csharp
// Full page (8192 bytes)
public Span<byte> WholePage 
    => new Span<byte>(_pageAddress, PagedMMF.PageSize);

// Header section (192 bytes)
public Span<byte> PageHeader 
    => new Span<byte>(_pageAddress, PagedMMF.PageHeaderSize);

// Base header only (64 bytes)
public Span<byte> PageBaseHeader 
    => new Span<byte>(_pageAddress, PagedMMF.PageBaseHeaderSize);

// Metadata section (128 bytes, offset 64)
public Span<byte> PageMetadata 
    => new Span<byte>(_pageAddress + PagedMMF.PageBaseHeaderSize, 
                      PagedMMF.PageMetadataSize);

// Raw data section (8000 bytes, offset 192)
public Span<byte> PageRawData 
    => new Span<byte>(_pageAddress + PagedMMF.PageHeaderSize, 
                      PagedMMF.PageRawDataSize);

// Indices
public int FilePageIndex => _filePageIndex;
public int MemPageIndex => _pi.MemPageIndex;
```

### Typed Header Access

```csharp
// Get reference to header structure at offset
public ref T GetHeader<T>(int offset) where T : unmanaged
{
    EnsureDataReady();
    return ref Unsafe.AsRef<T>(_pageAddress + offset);
}

// Example usage
ref PageBaseHeader baseHeader = ref accessor.GetHeader<PageBaseHeader>(0);
ref LogicalSegmentHeader segHeader = ref accessor.GetHeader<LogicalSegmentHeader>(64);
```

### I/O Synchronization

When a page is loaded from disk, the read may be asynchronous. `EnsureDataReady()` blocks until the data is available:

```csharp
internal void EnsureDataReady()
{
    if (_isReady) return;
    
    var ioTask = _pi.IOReadTask;
    if (ioTask == null || ioTask.IsCompletedSuccessfully)
    {
        _isReady = true;
        _pi.ResetIOCompletionTask();
        return;
    }
    
    // Block until read completes
    ioTask.GetAwaiter().GetResult();
    _isReady = true;
    _pi.ResetIOCompletionTask();
}
```

**Important**: All property accessors call `EnsureDataReady()` internally, so you don't need to call it explicitly.

### Promotion and Demotion

```csharp
// Promote from Shared to Exclusive (must be sole reader)
public bool TryPromoteToExclusive()
{
    if (_pi.ConcurrentSharedCounter != 1)
        return false;
    if (_pi.LockedByThreadId != Environment.CurrentManagedThreadId)
        return false;
    
    _previousMode = PagedMMF.PageState.Shared;
    _pi.PageState = PagedMMF.PageState.Exclusive;
    return true;
}

// Demote from Exclusive back to Shared
public void DemoteFromExclusive()
{
    if (_previousMode == PagedMMF.PageState.Idle)
        return;  // Wasn't promoted
    
    _owner.DemoteExclusive(_pi, _previousMode);
    _previousMode = PagedMMF.PageState.Idle;
}
```

### Dirty Marking

```csharp
// Mark page as modified (for ChangeSet tracking)
public void SetPageDirty()
{
    // Typically called when modifying data
    // ChangeSet.Add() handles the actual dirty tracking
}
```

### Disposal

```csharp
public void Dispose()
{
    if (_pi == null) return;
    
    // If promoted, demote back
    if (_previousMode != PagedMMF.PageState.Idle)
    {
        _owner.DemoteExclusive(_pi, _previousMode);
    }
    // Release access
    else if (_pi.PageState != PagedMMF.PageState.Idle)
    {
        _owner.TransitionPageFromAccessToIdle(_pi);
    }
    
    _pi = null;
}
```

### Usage Patterns

```csharp
// Pattern 1: Simple read
if (pagedMMF.RequestPage(pageIndex, exclusive: false, out var accessor))
{
    using (accessor)
    {
        var data = accessor.PageRawData;
        // Read data...
    }
}

// Pattern 2: Write with dirty tracking
if (pagedMMF.RequestPage(pageIndex, exclusive: true, out var accessor))
{
    using (accessor)
    {
        var data = accessor.PageRawData;
        // Modify data...
        changeSet.Add(accessor);
    }
}

// Pattern 3: Read then write (promotion)
if (pagedMMF.RequestPage(pageIndex, exclusive: false, out var accessor))
{
    using (accessor)
    {
        var data = accessor.PageRawData;
        // Read and decide if write needed...
        
        if (needsWrite && accessor.TryPromoteToExclusive())
        {
            // Now have exclusive access
            // Modify data...
            changeSet.Add(accessor);
        }
    }
}
```

---

## ChunkAccessor

`ChunkAccessor` is a high-performance struct providing a **16-slot L1 cache** for chunk-based segment access. It sits above the page cache and optimizes repeated access patterns common in B+Tree traversal.

### Structure (~1 KiB)

```csharp
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkAccessor : IDisposable
{
    // SOA: Page indices for SIMD search (64 bytes = 2 × Vector256)
    private fixed int _pageIndices[16];
    
    // AoS: Per-slot metadata (256 bytes)
    private SlotDataBuffer _slots;  // 16 × SlotData
    
    // Page accessors (inline array)
    private PageAccessorBuffer _pageAccessors;
    
    // Segment state
    private ChunkBasedSegment _segment;
    private ChangeSet _changeSet;
    
    // Hot-path caches
    private byte _mruSlot;           // Most Recently Used slot
    private byte _usedSlots;         // High water mark (0-16)
    private int _stride;             // Chunk size in bytes
    private int _rootHeaderOffset;   // Offset for root page (2000 bytes)
}
```

### SlotData Structure

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SlotData
{
    public long BaseAddress;      // 8 bytes - cached page raw data pointer
    public int HitCount;          // 4 bytes - LRU tracking
    public short PinCounter;      // 2 bytes - scope protection
    public byte DirtyFlag;        // 1 byte - lazy dirty tracking
    public byte PromoteCounter;   // 1 byte - exclusive promotion count
}
// Total: 16 bytes per slot
```

### Three-Tier Access Optimization

The `GetChunkAddress` method uses three tiers for maximum performance:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
internal byte* GetChunkAddress(int chunkId, bool dirty = false)
{
    // Calculate which page and offset within page
    (int pageIndex, int offset) = _segment.GetChunkLocation(chunkId);

    // === TIER 1: MRU check (3-4 cycles) ===
    var mru = _mruSlot;
    if (_pageIndices[mru] == pageIndex)
    {
        ref var slot = ref _slots[mru];
        if (dirty) slot.DirtyFlag = 1;
        slot.HitCount++;
        
        var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
        return (byte*)slot.BaseAddress + headerOffset + offset * _stride;
    }

    // === TIER 2: SIMD search (10-15 cycles) ===
    fixed (int* indices = _pageIndices)
    {
        var target = Vector256.Create(pageIndex);
        
        // Search slots 0-7
        var v0 = Vector256.Load(indices);
        var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
        if (mask0 != 0)
        {
            var slot = BitOperations.TrailingZeroCount(mask0);
            return GetFromSlot(slot, pageIndex, offset, dirty);
        }
        
        // Search slots 8-15
        var v1 = Vector256.Load(indices + 8);
        var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
        if (mask1 != 0)
        {
            var slot = 8 + BitOperations.TrailingZeroCount(mask1);
            return GetFromSlot(slot, pageIndex, offset, dirty);
        }
    }

    // === TIER 3: Cache miss - load page (100+ cycles) ===
    return LoadAndGet(pageIndex, offset, dirty);
}
```

### MRU Optimization

The Most Recently Used slot is checked first with a simple comparison:

```csharp
// Update MRU on access
private byte* GetFromSlot(int slotIndex, int pageIndex, int offset, bool dirty)
{
    _mruSlot = (byte)slotIndex;  // Update MRU
    
    ref var slot = ref _slots[slotIndex];
    if (dirty) slot.DirtyFlag = 1;
    slot.HitCount++;
    
    var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
    return (byte*)slot.BaseAddress + headerOffset + offset * _stride;
}
```

### SIMD Search Details

Uses AVX2 `Vector256<int>` for parallel comparison of 8 indices at once:

```csharp
// Vector256.Create broadcasts pageIndex to all 8 lanes
var target = Vector256.Create(pageIndex);  // [p, p, p, p, p, p, p, p]

// Vector256.Load reads 8 consecutive ints
var v0 = Vector256.Load(indices);  // [idx0, idx1, ..., idx7]

// Vector256.Equals produces all-ones for matching lanes
var cmp = Vector256.Equals(v0, target);  // [0, -1, 0, 0, 0, 0, 0, 0] if idx1 matches

// ExtractMostSignificantBits gets the sign bit of each lane
var mask = cmp.ExtractMostSignificantBits();  // 0b00000010 if idx1 matches

// TrailingZeroCount finds first set bit
var slot = BitOperations.TrailingZeroCount(mask);  // 1
```

### LRU Eviction

When all 16 slots are in use and a new page is needed:

```csharp
private int FindLRUSlot()
{
    // Fast path: use unused slots first
    if (_usedSlots < 16)
        return _usedSlots++;
    
    // Find slot with minimum hit count (that isn't pinned)
    var minHit = int.MaxValue;
    var minSlot = -1;
    
    for (int i = 0; i < 16; i++)
    {
        ref var slot = ref _slots[i];
        
        // Skip pinned or promoted slots
        if (slot.PinCounter > 0 || slot.PromoteCounter > 0)
            continue;
        
        if (slot.HitCount < minHit)
        {
            minHit = slot.HitCount;
            minSlot = i;
        }
    }
    
    return minSlot;
}
```

### ChunkHandle for Multi-Chunk Operations

When accessing multiple chunks that might be on the same page, use `ChunkHandle` to prevent eviction:

```csharp
public ref struct ChunkHandle : IDisposable
{
    private ref ChunkAccessor _owner;
    private byte* _chunkAddress;
    private int _chunkLength;
    private int _slotIndex;
    
    // Typed access
    public ref T AsRef<T>() where T : unmanaged 
        => ref Unsafe.AsRef<T>(_chunkAddress);
    
    public Span<byte> AsSpan() 
        => new Span<byte>(_chunkAddress, _chunkLength);
    
    public void Dispose()
    {
        if (!Unsafe.IsNullRef(ref _owner))
        {
            _owner.UnpinSlot(_slotIndex);
            _owner = ref Unsafe.NullRef<ChunkAccessor>();
        }
    }
}
```

**Usage**:

```csharp
using var accessor = new ChunkAccessor(segment, changeSet);

// Pin first chunk
using var handle1 = accessor.GetChunkHandle(chunkId1);
ref MyStruct data1 = ref handle1.AsRef<MyStruct>();

// Pin second chunk (first stays pinned)
using var handle2 = accessor.GetChunkHandle(chunkId2);
ref MyStruct data2 = ref handle2.AsRef<MyStruct>();

// Now can safely access both without eviction risk
data1.Value = data2.Value + 1;
```

### Disposal and Flush

```csharp
public void Dispose()
{
    // Flush dirty pages to ChangeSet
    for (int i = 0; i < _usedSlots; i++)
    {
        if (_slots[i].DirtyFlag != 0)
        {
            _changeSet?.Add(_pageAccessors[i]);
        }
        
        _pageAccessors[i].Dispose();
    }
    
    _usedSlots = 0;
}
```

### Performance Comparison

| Access Pattern | Tier Hit | Latency |
|----------------|----------|---------|
| Same chunk repeated | Tier 1 (MRU) | ~3-4 cycles |
| Recent chunk (in cache) | Tier 2 (SIMD) | ~10-15 cycles |
| Cold chunk (cache miss) | Tier 3 (load) | ~100+ cycles |
| B+Tree traversal | Mostly Tier 1-2 | ~5-10 cycles avg |

### Usage Patterns

```csharp
// Pattern 1: Simple chunk access
using var accessor = new ChunkAccessor(segment, changeSet);
byte* ptr = accessor.GetChunkAddress(chunkId, dirty: false);
ref MyStruct data = ref Unsafe.AsRef<MyStruct>(ptr);

// Pattern 2: Modify chunk
byte* ptr = accessor.GetChunkAddress(chunkId, dirty: true);
ref MyStruct data = ref Unsafe.AsRef<MyStruct>(ptr);
data.Value = 42;
// Dirty flag set, will be flushed on Dispose

// Pattern 3: B+Tree traversal (benefits from MRU)
int currentNode = rootChunkId;
while (!IsLeaf(currentNode))
{
    byte* ptr = accessor.GetChunkAddress(currentNode);
    ref BTreeNode node = ref Unsafe.AsRef<BTreeNode>(ptr);
    currentNode = node.Children[FindChildIndex(node, key)];
    // Next iteration likely hits MRU (child often on same page)
}

// Pattern 4: Multi-chunk with pinning
using var h1 = accessor.GetChunkHandle(sourceChunk);
using var h2 = accessor.GetChunkHandle(destChunk);
h2.AsSpan().CopyFrom(h1.AsSpan());
```

## Summary

| Accessor | Purpose | Cache Level | Typical Use |
|----------|---------|-------------|-------------|
| `PageAccessor` | Single page access | L2 (PagedMMF cache) | Direct page operations |
| `ChunkAccessor` | Multi-chunk access | L1 (16-slot SIMD) | B+Tree, component access |
| `ChunkHandle` | Pinned chunk reference | Prevents L1 eviction | Multi-chunk operations |

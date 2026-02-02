# Page Layout and Memory Structure

This document describes the physical layout of pages in Typhon's storage system, including headers, metadata, and data sections.

## Page Structure Overview

Every page in Typhon is exactly **8,192 bytes (8 KiB)** and follows this layout:

```
Offset 0                                              Offset 8192
┌────────────────────┬─────────────────────┬─────────────────────┐
│   PageBaseHeader   │    PageMetadata     │     PageRawData     │
│     (64 bytes)     │    (128 bytes)      │    (8000 bytes)     │
└────────────────────┴─────────────────────┴─────────────────────┘
         └─────────────────────┘
              PageHeader
             (192 bytes)
```

## Constants

```csharp
// From PagedMMF.cs
public const int PageSize = 8192;           // 8 KiB total
public const int PageSizePow2 = 13;         // 2^13 = 8192
public const int PageBaseHeaderSize = 64;   // Base header
public const int PageMetadataSize = 128;    // Metadata section
public const int PageHeaderSize = 192;      // Base + Metadata
public const int PageRawDataSize = 8000;    // Usable data
```

## PageBaseHeader (64 bytes)

The base header occupies the first 64 bytes of every page. Only 8 bytes are currently used; the rest is reserved for future expansion (e.g., CRC32C checksums).

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PageBaseHeader
{
    public PageBlockFlags Flags;      // 1 byte - MUST be first byte
    public PageBlockType Type;        // 1 byte
    public short FormatRevision;      // 2 bytes
    public int ChangeRevision;        // 4 bytes
    // Reserved: 56 bytes
}
```

### PageBlockFlags

```csharp
[Flags]
public enum PageBlockFlags : byte
{
    None                 = 0x00,
    IsFree               = 0x01,  // Page is unallocated
    IsLogicalSegment     = 0x02,  // Part of a logical segment
    IsLogicalSegmentRoot = 0x04   // Root page of a segment
}
```

**Design Note**: `Flags` is positioned as the **first byte** specifically to enable fast direct byte access via pointer without struct overhead:

```csharp
// Fast flag check without loading entire header
byte flags = *pageAddress;
bool isFree = (flags & 0x01) != 0;
```

### PageBlockType

```csharp
public enum PageBlockType : byte
{
    None = 0,
    OccupancyMap = 1,
    // Additional types defined per use case
}
```

### ChangeRevision

The `ChangeRevision` field is **incremented each time the page is written to disk**. This enables:
- Incremental backup detection (only backup pages with changed revision)
- Crash recovery verification
- Debugging and diagnostics

## PageMetadata (128 bytes)

The metadata section (bytes 64-191) is used differently depending on the page type:

### For ChunkBasedSegment Pages

Stores the **L0 occupancy bitmap** for chunk allocation:

```
128 bytes = 16 longs = 1024 bits
Each bit represents one chunk slot (allocated=1, free=0)
```

```csharp
// Access metadata as bitmap
Span<long> bitmap = accessor.PageMetadata.Cast<byte, long>();
// bitmap[0] covers chunks 0-63
// bitmap[1] covers chunks 64-127
// etc.
```

### For Other Page Types

Reserved for future use or page-type-specific metadata.

## PageRawData (8000 bytes)

The data section (bytes 192-8191) contains the actual stored content:

```csharp
// Access raw data
Span<byte> data = accessor.PageRawData;  // 8000 bytes

// Cast to typed data
Span<MyStruct> items = data.Cast<byte, MyStruct>();
int maxItems = 8000 / sizeof(MyStruct);
```

### Root Page Data Layout

For **LogicalSegment root pages**, the data section is split:

```
┌─────────────────────────────────────────────────────────────┐
│ Index Section (2000 bytes)                                  │
│   500 × int page indices (Pages[0] to Pages[499])           │
├─────────────────────────────────────────────────────────────┤
│ Data Section (6000 bytes)                                   │
│   Available for actual data storage                         │
└─────────────────────────────────────────────────────────────┘
```

**Implication**: Root pages have less usable data space:
- Root page: 6,000 bytes
- Other pages: 8,000 bytes

## Memory Cache Layout

The page cache is a contiguous pinned memory buffer:

```
MemPages byte array (pinned via GCHandle)
┌─────────────┬─────────────┬─────────────┬─────┬───────────────┐
│   Page 0    │   Page 1    │   Page 2    │ ... │  Page N-1     │
│ (8192 bytes)│ (8192 bytes)│ (8192 bytes)│     │ (8192 bytes)  │
└─────────────┴─────────────┴─────────────┴─────┴───────────────┘
      ^
      │
_memPagesAddr (raw pointer)
```

### Cache Sizing

```csharp
// Default: 256 pages = 2 MiB
MemPagesCount = (int)(cacheSize / PageSize);

// Address calculation
byte* GetMemPageAddress(int memPageIndex) 
    => &_memPagesAddr[memPageIndex * (long)PageSize];
```

### Why Pinning?

The cache is **pinned** using `GCHandle.Alloc(..., GCHandleType.Pinned)` to:

1. **Prevent GC relocation**: Raw pointers remain valid
2. **Enable zero-copy access**: No marshaling overhead
3. **Support async I/O**: OS can DMA directly to buffer
4. **Allow unsafe pointer arithmetic**: Fast address calculations

```csharp
// Initialization
MemPages = new byte[cacheSize];
_memPagesHandle = GCHandle.Alloc(MemPages, GCHandleType.Pinned);
_memPagesAddr = (byte*)_memPagesHandle.AddrOfPinnedObject();

// Cleanup
_memPagesHandle.Free();
```

## Page Address Calculation

Given a file page index, finding its memory location:

```csharp
// Step 1: Look up memory page index from cache directory
if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out int memPageIndex))
{
    // Step 2: Calculate memory address
    byte* pageAddress = &_memPagesAddr[memPageIndex * PageSize];
    
    // Step 3: Access specific sections
    PageBaseHeader* header = (PageBaseHeader*)pageAddress;
    byte* metadata = pageAddress + PageBaseHeaderSize;     // offset 64
    byte* rawData = pageAddress + PageHeaderSize;          // offset 192
}
```

## Chunk Layout Within Pages

For `ChunkBasedSegment`, chunks are laid out contiguously in the data section:

```
Page with 64-byte chunks:
┌────────────────────────────────────────────────────────────┐
│ Header (192 bytes)                                         │
├────────────────────────────────────────────────────────────┤
│ Chunk 0 │ Chunk 1 │ Chunk 2 │ ... │ Chunk N-1 │ (padding)  │
│ 64 bytes│ 64 bytes│ 64 bytes│     │ 64 bytes  │            │
└────────────────────────────────────────────────────────────┘

Chunks per page = PageRawDataSize / stride = 8000 / 64 = 125 chunks
```

### Root Page Chunk Capacity

```csharp
// Root page has index section overhead
ChunkCountRootPage = (PageRawDataSize - RootHeaderIndexSectionLength) / stride;
// = (8000 - 2000) / 64 = 93 chunks

// Other pages have full capacity
ChunkCountPerPage = PageRawDataSize / stride;
// = 8000 / 64 = 125 chunks
```

## File Layout

The database file is a sequence of 8 KiB pages:

```
File offset 0                                              EOF
┌──────────┬───────────┬──────────┬──────────┬─────┬──────────┐
│  Page 0  │  Page 1   │  Page 2  │  Page 3  │ ... │  Page N  │
│  (Root)  │(Occupancy)│(Reserved)│  (Data)  │     │  (Data)  │
└──────────┴───────────┴──────────┴──────────┴─────┴──────────┘
```

### Initial File Structure

```
Page 0: Root file header (database metadata)
Page 1: Occupancy segment root (tracks page allocation)
Page 2: Reserved for occupancy growth
Page 3: Reserved for occupancy map data
Page 4+: Available for allocation
```

## Summary

| Section | Offset | Size | Purpose |
|---------|--------|------|---------|
| PageBaseHeader | 0 | 64 bytes | Flags, type, revision |
| PageMetadata | 64 | 128 bytes | Chunk occupancy bitmaps |
| PageRawData | 192 | 8000 bytes | Actual data storage |
| **Total** | - | **8192 bytes** | One page |

**Key Points**:
- All pages are exactly 8 KiB for consistent addressing
- First byte is always flags for fast type checking
- Metadata enables per-page chunk tracking
- Root pages sacrifice 2000 bytes for index directory
- Cache is pinned for zero-copy pointer access

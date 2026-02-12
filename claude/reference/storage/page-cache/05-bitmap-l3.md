# Hierarchical Bitmap (BitmapL3)

This document describes Typhon's 3-level hierarchical bitmap for efficient allocation tracking.

## Overview

The hierarchical bitmap achieves near-O(1) allocation by using summary levels that allow skipping fully-occupied regions:

```
Level 2 (L2All):  1 bit per 4096 items   ─┐
                                          │ Summary levels
Level 1 (L1All):  1 bit per 64 items     ─┤ (in memory)
Level 1 (L1Any):  1 bit per 64 items     ─┘
                                           
Level 0 (L0):     1 bit per item         ─── Ground truth (on disk)
```

## The Four Arrays

| Array | Purpose | One bit represents |
|-------|---------|-------------------|
| **L0** | Ground truth allocation state | 1 item (chunk/page) |
| **L1All** | "All 64 bits are set" summary | 64 L0 bits |
| **L1Any** | "At least one bit is set" summary | 64 L0 bits |
| **L2All** | "All 64 L1All bits are set" summary | 64 L1All bits |

### Invariants

- **L0 bit = 1** → Item is allocated (authoritative)
- **L1All bit = 1** → ALL 64 corresponding L0 bits are 1
- **L1Any bit = 1** → AT LEAST ONE corresponding L0 bit is 1
- **L2All bit = 1** → ALL 64 corresponding L1All bits are 1

## Memory Layout

```csharp
internal class BitmapL3
{
    private readonly ChunkBasedSegment _segment;  // L0 stored in page metadata
    
    private readonly Memory<long> _l1All;         // In-memory summary
    private readonly Memory<long> _l1Any;         // In-memory summary
    private readonly Memory<long> _l2All;         // In-memory summary
    
    public int Capacity { get; }                  // Total trackable items
    public int Allocated { get; private set; }    // Currently allocated count
}
```

### Size Calculation

For capacity N:
- **L0**: `ceil(N / 64)` longs (stored in page metadata, not here)
- **L1All/L1Any**: `ceil(L0_count / 64)` longs each
- **L2All**: `ceil(L1_count / 64)` longs

**Example** (1 million items):
- L0: 15,625 longs (stored across pages)
- L1All: 245 longs (~2 KB)
- L1Any: 245 longs (~2 KB)
- L2All: 4 longs (32 bytes)

## Bit Indexing

```csharp
// For item at index `i`:
int l0WordIndex = i >> 6;              // i / 64
int l0BitPosition = i & 0x3F;          // i % 64
long l0Mask = 1L << l0BitPosition;

// L1 index from L0 word index:
int l1WordIndex = l0WordIndex >> 6;    // l0Index / 64
int l1BitPosition = l0WordIndex & 0x3F;
long l1Mask = 1L << l1BitPosition;

// L2 index from L1 word index:
int l2WordIndex = l1WordIndex >> 6;
int l2BitPosition = l1WordIndex & 0x3F;
long l2Mask = 1L << l2BitPosition;
```

## Core Operations

### SetL0 (Allocate Single Item)

```csharp
public bool SetL0(int bitIndex)
{
    // 1. Calculate L0 position
    int l0Offset = bitIndex >> 6;
    long l0Mask = 1L << (bitIndex & 0x3F);
    
    // 2. Get page containing this L0 word
    var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
    byte* addr = _segment.GetPageAddress(pageIndex, epoch, out var memIdx);
    {
        var data = new Span<long>(addr + PagedMMF.PageMetadataOffset, metadataLongCount);
        
        // 3. Atomically set the bit
        long prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
        
        // 4. Check if already set (another thread beat us)
        if ((prevL0 & l0Mask) != 0)
            return false;
        
        // Page dirtied via ChangeSet.AddByMemPageIndex(memIdx) by caller
        
        // 5. Update L1All if L0 word became fully set
        if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
        {
            int l1Offset = l0Offset >> 6;
            long l1Mask = 1L << (l0Offset & 0x3F);
            
            long prevL1 = _l1All.Span[l1Offset];
            _l1All.Span[l1Offset] |= l1Mask;
            
            // Update L2All if L1 word became fully set
            if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
            {
                int l2Offset = l1Offset >> 6;
                long l2Mask = 1L << (l1Offset & 0x3F);
                _l2All.Span[l2Offset] |= l2Mask;
            }
        }
        
        // 6. Update L1Any if L0 word was empty
        if (prevL0 == 0)
        {
            int l1Offset = l0Offset >> 6;
            long l1Mask = 1L << (l0Offset & 0x3F);
            _l1Any.Span[l1Offset] |= l1Mask;
        }
        
        ++Allocated;
        return true;
    }
}
```

### SetL1 (Bulk Allocate 64 Items)

```csharp
public bool SetL1(int l0WordIndex)
{
    var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0WordIndex);
    byte* addr = _segment.GetPageAddress(pageIndex, epoch, out var memIdx);
    {
        var data = new Span<long>(addr + PagedMMF.PageMetadataOffset, metadataLongCount);
        
        // Only succeed if entire word is empty (CompareExchange)
        long prevL0 = Interlocked.CompareExchange(ref data[pageOffset], -1L, 0L);
        
        if (prevL0 != 0)
            return false;  // Word wasn't empty
        
        // Page dirtied via ChangeSet.AddByMemPageIndex(memIdx) by caller
        
        // Update L1All (word is now fully set)
        int l1Offset = l0WordIndex >> 6;
        long l1Mask = 1L << (l0WordIndex & 0x3F);
        
        long prevL1 = _l1All.Span[l1Offset];
        _l1All.Span[l1Offset] |= l1Mask;
        
        // Update L1Any (word has bits set)
        _l1Any.Span[l1Offset] |= l1Mask;
        
        // Update L2All if needed
        if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
        {
            int l2Offset = l1Offset >> 6;
            long l2Mask = 1L << (l1Offset & 0x3F);
            _l2All.Span[l2Offset] |= l2Mask;
        }
        
        Allocated += 64;
        return true;
    }
}
```

### ClearL0 (Free Single Item)

```csharp
public void ClearL0(int bitIndex)
{
    int l0Offset = bitIndex >> 6;
    long bitMask = 1L << (bitIndex & 0x3F);
    long clearMask = ~bitMask;
    
    var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
    byte* addr = _segment.GetPageAddress(pageIndex, epoch, out var memIdx);
    {
        var data = new Span<long>(addr + PagedMMF.PageMetadataOffset, metadataLongCount);
        
        // Atomically clear the bit
        long prevL0 = Interlocked.And(ref data[pageOffset], clearMask);
        
        // Check if bit was actually set
        if ((prevL0 & bitMask) == 0)
            return;  // Already free (idempotent)
        
        // Page dirtied via ChangeSet.AddByMemPageIndex(memIdx) by caller
        
        // Update L1All if L0 word was fully set (now isn't)
        if (prevL0 == -1)
        {
            int l1Offset = l0Offset >> 6;
            long l1Mask = 1L << (l0Offset & 0x3F);
            
            long prevL1 = _l1All.Span[l1Offset];
            _l1All.Span[l1Offset] &= ~l1Mask;
            
            // Update L2All if L1 word was fully set
            if (prevL1 == -1)
            {
                int l2Offset = l1Offset >> 6;
                long l2Mask = 1L << (l1Offset & 0x3F);
                _l2All.Span[l2Offset] &= ~l2Mask;
            }
        }
        
        // Update L1Any if L0 word became empty
        if ((prevL0 & clearMask) == 0)
        {
            int l1Offset = l0Offset >> 6;
            long l1Mask = 1L << (l0Offset & 0x3F);
            _l1Any.Span[l1Offset] &= ~l1Mask;
        }
        
        --Allocated;
    }
}
```

## Finding Free Items

### FindNextUnsetL0 (Find Single Free Slot)

The key optimization: use L1All/L2All to skip fully-occupied regions.

```csharp
private bool FindNextUnsetL0(ref int index, ref long mask)
{
    int c0 = ++index;
    long v0 = mask;
    
    while (c0 < Capacity)
    {
        // Need to fetch a new L0 word?
        if ((c0 & 0x3F) == 0 || v0 == -1)
        {
            // === L0 level scan ===
            for (int i0 = c0 >> 6; i0 < l0WordCount; i0 = c0 >> 6)
            {
                // Fetch L0 word from page
                var (pageId, offset) = GetBitmapMaskLocation(i0);
                long t0 = 1L << (c0 & 0x3F);
                v0 = data[offset] | (t0 - 1);  // Mask out already-checked bits
                
                if (v0 != -1)
                    break;  // Found word with free bits
                
                c0 = ++i0 << 6;  // Skip to next L0 word (64 items)
                
                // === L1 level scan ===
                for (int i1 = c0 >> 12; i1 < l1WordCount; i1 = c0 >> 12)
                {
                    long v1 = _l1All.Span[i1] >> (i0 & 0x3F);
                    
                    if (v1 != -1)
                        break;  // Found L1 region with free space
                    
                    i0 = 0;
                    c0 = ++i1 << 12;  // Skip 64 L0 words (4096 items)
                    
                    // === L2 level scan ===
                    for (int i2 = c0 >> 18; i2 < l2WordCount; i2 = c0 >> 18)
                    {
                        long v2 = _l2All.Span[i2] >> (i1 & 0x3F);
                        
                        if (v2 != -1)
                            break;  // Found L2 region with free space
                        
                        i1 = 0;
                        c0 = ++i2 << 18;  // Skip 64 L1 words (262144 items)
                    }
                }
            }
        }
        
        // Find first unset bit in current word
        int bitPos = BitOperations.TrailingZeroCount(~v0);
        int candidate = (c0 & ~0x3F) + bitPos;
        
        if (candidate >= Capacity)
            return false;  // Beyond capacity
        
        v0 |= (1L << bitPos);  // Mark as checked
        index = candidate;
        mask = v0;
        return true;
    }
    
    return false;  // Bitmap is full
}
```

### Skip Amounts

| Level | Skip Amount | Bits Skipped |
|-------|-------------|--------------|
| L0 | 1 word | 64 items |
| L1 | 1 word | 4,096 items |
| L2 | 1 word | 262,144 items |

### FindNextUnsetL1 (Find Empty 64-Slot Block)

For bulk allocation, find an entirely empty L0 word:

```csharp
private bool FindNextUnsetL1(ref int index, ref long mask)
{
    // Similar to FindNextUnsetL0 but searches for words where
    // L1Any bit is 0 (completely empty L0 word)
    
    // Uses L1Any (not L1All) to find empty regions
    // Skip regions where L1Any bit is set
}
```

## Bulk Allocation Algorithm

```csharp
public bool Allocate(Memory<int> result, bool clearContent)
{
    int length = result.Length;
    int destIndex = 0;
    var span = result.Span;
    
    // Phase 1: Bulk allocate 64 at a time
    while (length >= 64)
    {
        int i = -1;
        long mask = 0;
        
        while (FindNextUnsetL1(ref i, ref mask) && length >= 64)
        {
            if (SetL1(i))  // Atomically claim all 64
            {
                // Record all 64 indices
                int baseIndex = i << 6;
                for (int j = 0; j < 64; j++)
                {
                    span[destIndex++] = baseIndex + j;
                }
                length -= 64;
            }
        }
        
        if (length < 64) break;
    }
    
    // Phase 2: Individual allocation for remainder
    {
        int i = -1;
        long mask = 0;
        
        while (FindNextUnsetL0(ref i, ref mask) && length > 0)
        {
            if (SetL0(i))
            {
                span[destIndex++] = i;
                --length;
            }
        }
    }
    
    // Phase 3: Rollback on failure
    if (length > 0)
    {
        for (int i = 0; i < destIndex; i++)
            ClearL0(span[i]);
        span.Clear();
        return false;
    }
    
    return true;
}
```

**Performance**: Allocating 1000 items:
- Without bulk: ~1000 atomic operations
- With bulk: ~15 SetL1 + ~40 SetL0 = ~55 atomic operations

## Bitmap Extension (Growth)

When the segment grows, the bitmap is extended efficiently:

```csharp
public BitmapL3(ChunkBasedSegment segment, BitmapL3 oldBitmap, int oldPageCount)
{
    // Copy existing L1/L2 state
    oldBitmap._l1All.Span.CopyTo(_l1All.Span);
    oldBitmap._l1Any.Span.CopyTo(_l1Any.Span);
    oldBitmap._l2All.Span.CopyTo(_l2All.Span);
    
    // Copy allocation count (new pages are guaranteed empty)
    Allocated = oldBitmap.Allocated;
    
    // New pages have zeroed metadata, so:
    // - Their L0 bits are already 0
    // - L1/L2 bits for new regions are 0 from array initialization
    // No scanning needed!
}
```

## Concurrency Model

### L0: Authoritative with Atomic Operations

```csharp
// SetL0 uses Interlocked.Or
long prev = Interlocked.Or(ref data[offset], mask);

// SetL1 uses Interlocked.CompareExchange  
long prev = Interlocked.CompareExchange(ref data[offset], -1L, 0L);

// ClearL0 uses Interlocked.And
long prev = Interlocked.And(ref data[offset], ~mask);
```

### L1/L2: Best-Effort Summaries

L1 and L2 levels are hints that may be temporarily inconsistent under high concurrency:

- **False positive in L1All**: Extra scan of L0 (correct but slower)
- **False negative in L1All**: Will find the answer in L0 anyway

No locks required because:
1. L0 is always authoritative
2. Summary inconsistency only affects performance, not correctness
3. Self-correction happens on subsequent operations

## Visualization

```
Finding a free slot (capacity = 256):

L2All: [0b00000001]  (first 4096 items full)
        └─ Skip!

L1All: [0b11111111, 0b00001111, ...]
        └─ Skip!     └─ Check word 1

L0 word 68: [0b11111111_11111111_11111111_11110111]
                                              └─ Free bit at position 3!

Result: index = 68 * 64 + 3 = 4355
```

## Summary

| Aspect | Value |
|--------|-------|
| **Levels** | 3 (L0 ground truth, L1 summary, L2 summary) |
| **Skip multipliers** | 64× (L0→L1), 64× (L1→L2) |
| **Overhead** | ~3% extra memory for summaries |
| **Allocation** | Near O(1) average, O(n) worst case |
| **Concurrency** | Lock-free with atomic operations |
| **Bulk optimization** | 64 items at once via SetL1 |

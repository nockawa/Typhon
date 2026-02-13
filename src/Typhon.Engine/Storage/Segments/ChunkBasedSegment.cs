// unset

using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ChunkBasedSegmentHeader
{
    unsafe public static readonly int Size = sizeof(ChunkBasedSegmentHeader);
    public static readonly int TotalSize =  LogicalSegmentHeader.TotalSize + Size;
    public static readonly int Offset = LogicalSegmentHeader.TotalSize;

    private int _fill0;
}

/// <summary>
/// Logical Segment that stores fixed sized chunk of data.
/// </summary>
/// <remarks>
/// Provides API to allocate chunks; the occupancy map is stored in the Metadata of each page. The minimum chunk size is 8 bytes.
/// </remarks>
public partial class ChunkBasedSegment : LogicalSegment
{
    private readonly Lock _growLock = new();
    private volatile BitmapL3 _map;
    private readonly EpochManager _epochManager;

    // Cached values for fast GetChunkLocation (avoids _map indirection)
    private readonly int _rootChunkCount;
    private readonly int _otherChunkCount;

    // Magic multiplier for fast division: quotient = (n * _divMagic) >> 32
    // This replaces expensive division (~20-80 cycles) with multiply+shift (~3-4 cycles)
    private readonly ulong _divMagic;
    
    internal ChunkBasedSegment(EpochManager epochManager, ManagedPagedMMF manager, int stride) : base(manager)
    {
        if (stride < sizeof(long))
        {
            throw new Exception($"Invalid stride size, given {stride}, but must be at least 8 bytes");
        }

        _epochManager = epochManager;
        
        Stride = stride;
        ChunkCountRootPage = (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength) / stride;
        ChunkCountPerPage = PagedMMF.PageRawDataSize / stride;

        // Cache for fast access in GetChunkLocation
        _rootChunkCount = ChunkCountRootPage;
        _otherChunkCount = ChunkCountPerPage;

        // Precompute magic multiplier for fast division by _otherChunkCount
        // Formula: magic = ceil(2^32 / divisor) = (2^32 + divisor - 1) / divisor
        // This works for divisors where the maximum dividend fits in 32 bits
        _divMagic = (0x1_0000_0000UL + (uint)_otherChunkCount - 1) / (uint)_otherChunkCount;
    }

    internal override bool Create(PageBlockType type, Span<int> filePageIndices, bool clear, ChangeSet changeSet = null)
    {
        if (!base.Create(type, filePageIndices, clear, changeSet))
        {
            return false;
        }

        // Clear the metadata sections that store the chunk's occupancy bitmap
        var epoch = Manager.EpochManager.GlobalEpoch;
        var length = filePageIndices.Length;
        for (int i = 0; i < length; i++)
        {
            var page = GetPageExclusive(i, epoch, out var memPageIdx);
            int longSize = (i == 0 ? (ChunkCountRootPage + 63) : (ChunkCountPerPage + 63)) >> 6;
            page.Metadata<long>(0, longSize).Clear();
            Manager.UnlatchPageExclusive(memPageIdx);
        }

        _map = new BitmapL3(this, false);
        ReserveChunk(0);                    // It's always handy to consider ChunkId:0 as "null", so we reserve the chunk to prevent it is a valid id.
        return true;
    }

    internal override bool Load(int filePageIndex)
    {
        if (!base.Load(filePageIndex))
        {
            return false;
        }

        _map = new BitmapL3(this, true);

        return true;
    }

    /// <summary>
    /// Grows the segment to accommodate more chunks.
    /// </summary>
    /// <param name="minNewPageCount">Minimum number of pages after growth. If 0, doubles the current size.</param>
    /// <param name="changeSet">Optional change set for tracking modifications.</param>
    /// <returns>True if growth occurred, false if already at maximum capacity.</returns>
    /// <remarks>
    /// This method is thread-safe. It uses a lock to ensure only one thread grows the segment at a time.
    /// After growth, a new <see cref="BitmapL3"/> is created by loading state from disk, ensuring
    /// consistency with the expanded segment.
    /// </remarks>
    private bool Grow(int minNewPageCount = 0, ChangeSet changeSet = null)
    {
        lock (_growLock)
        {
            var currentLength = Length;
            var oldMap = _map;
            
            // Calculate new size: double current, or use minimum requested, whichever is larger
            var newLength = minNewPageCount > 0 
                ? Math.Max(currentLength * 2, minNewPageCount) 
                : currentLength * 2;
            
            // Check if we can grow
            if (newLength <= currentLength)
            {
                return false; // Already at maximum capacity
            }
            
            // Grow the underlying logical segment (thread-safe, will allocate new pages)
            base.Grow(newLength, clearNewPages: true, changeSet);
            
            // Clear the page metadata (bitmap) for newly allocated pages
            // This is critical! The base.Grow only clears raw data, not metadata.
            // Without this, InitFromLoad reads garbage and causes crashes.
            {
                var epoch = Manager.EpochManager.GlobalEpoch;
                for (int i = currentLength; i < newLength; i++)
                {
                    var page = GetPageExclusive(i, epoch, out var memPageIdx);
                    int longSize = (ChunkCountPerPage + 63) >> 6;
                    page.Metadata<long>(0, longSize).Clear();
                    changeSet?.AddByMemPageIndex(memPageIdx);
                    Manager.UnlatchPageExclusive(memPageIdx);
                }
            }
            
            // Create new bitmap by extending the old one incrementally.
            // This avoids re-scanning ALL pages (which could deadlock if the caller
            // holds pages via ChunkAccessor). Instead, we copy state from the old
            // bitmap and rely on the fact that new pages are guaranteed empty.
            _map = new BitmapL3(this, oldMap, currentLength);
            
            return true;
        }
    }
    
    /// <summary>
    /// Attempts to grow the segment if capacity is exhausted.
    /// </summary>
    /// <returns>True if growth occurred or capacity was already available, false if at maximum capacity.</returns>
    private bool GrowIfNeeded()
    {
        // Quick check without lock - if we have free chunks, no need to grow
        var map = _map;
        if (map.FreeChunkCount > 0)
        {
            return true;
        }
        
        // Need to grow - acquire lock and double-check
        lock (_growLock)
        {
            map = _map;
            if (map.FreeChunkCount > 0)
            {
                return true; // Another thread grew while we waited
            }
            
            return Grow();
        }
    }

    private static readonly ThreadLocal<Memory<int>> SingleAlloc = new(() => new Memory<int>(new int[1]));

    public void ReserveChunk(int index) => _map.SetL0(index);
    
    /// <summary>
    /// Allocates a single chunk from the segment.
    /// </summary>
    /// <param name="clearContent">Whether to clear the chunk content after allocation.</param>
    /// <returns>The allocated chunk ID.</returns>
    /// <exception cref="ResourceExhaustedException">Thrown when the segment is at maximum capacity and cannot grow.</exception>
    /// <remarks>
    /// This method automatically grows the segment when capacity is exhausted.
    /// The allocation itself is lock-free; only growth operations require synchronization.
    /// </remarks>
    public int AllocateChunk(bool clearContent)
    {
        var mem = SingleAlloc.Value;
        
        while (true)
        {
            var map = _map; // Volatile read
            
            if (map.Allocate(mem, clearContent))
            {
                return mem.Span[0];
            }
            
            // Allocation failed - need to grow
            if (!GrowIfNeeded())
            {
                ThrowHelper.ThrowResourceExhausted("Storage/ChunkBasedSegment/AllocateChunk", ResourceType.Memory, AllocatedChunkCount, ChunkCapacity);
            }
            // Retry with the new (grown) map
        }
    }

    /// <summary>
    /// Allocates multiple chunks from the segment.
    /// </summary>
    /// <param name="count">The number of chunks to allocate.</param>
    /// <param name="clearContent">Whether to clear the chunk content after allocation.</param>
    /// <returns>A memory owner containing the allocated chunk IDs.</returns>
    /// <exception cref="ResourceExhaustedException">Thrown when the segment cannot accommodate the requested chunks.</exception>
    /// <remarks>
    /// This method automatically grows the segment when capacity is exhausted.
    /// Growth is attempted iteratively until the request can be satisfied or maximum capacity is reached.
    /// </remarks>
    public IMemoryOwner<int> AllocateChunks(int count, bool clearContent)
    {
        var res = MemoryPool<int>.Shared.Rent(count);
        var memory = res.Memory[..count]; // Slice to exact count
        
        while (true)
        {
            var map = _map; // Volatile read
            if (map.Allocate(memory, clearContent))
            {
                return res;
            }
            
            // Allocation failed - need to grow
            // Calculate minimum pages needed to accommodate the request
            var chunksNeeded = count - map.FreeChunkCount;
            var pagesNeeded = (chunksNeeded + ChunkCountPerPage - 1) / ChunkCountPerPage;
            var minNewPageCount = Length + pagesNeeded;
            
            if (!Grow(minNewPageCount))
            {
                res.Dispose();
                ThrowHelper.ThrowResourceExhausted("Storage/ChunkBasedSegment/AllocateChunks", ResourceType.Memory, AllocatedChunkCount, ChunkCapacity);
            }
            // Retry with the new (grown) map
        }
    }

    public void FreeChunk(int chunkId) => _map.ClearL0(chunkId);

    /// <summary>
    /// Create an ChunkAccessor using the stored PagedMMF and EpochManager references.
    /// </summary>
    [return: TransfersOwnership]
    internal ChunkAccessor CreateChunkAccessor(ChangeSet changeSet = null) => new(this, Manager, _epochManager, changeSet);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public (int segmentIndex, int offset) GetChunkLocation(int index)
    {
        // Fast path: chunk is on root page (most common for small segments)
        if (index < _rootChunkCount)
        {
            return (0, index);
        }

        // Adjust index relative to non-root pages
        var adjusted = (uint)(index - _rootChunkCount);

        // Fast division using magic multiplier: quotient = (n * magic) >> 32
        // This replaces expensive idiv instruction with imul + shift
        var pageIndex = (int)((adjusted * _divMagic) >> 32);

        // Remainder: offset = adjusted - pageIndex * divisor
        var offset = (int)(adjusted - (uint)(pageIndex * _otherChunkCount));

        var resultPageIndex = pageIndex + 1;
        
        // Safety check: ensure the page index is within the segment's bounds
        // This catches cases where a chunk ID from a grown segment is accessed
        // through a stale reference or invalid chunk ID
        var segmentLength = Length;
        if (resultPageIndex >= segmentLength)
        {
            throw new InvalidOperationException(
                $"ChunkBasedSegment.GetChunkLocation: Computed page index {resultPageIndex} >= segment length {segmentLength}. " +
                $"ChunkId={index}, rootChunkCount={_rootChunkCount}, otherChunkCount={_otherChunkCount}, " +
                $"Capacity={ChunkCapacity}. This may indicate accessing a chunk ID that was never allocated or segment corruption.");
        }

        return (resultPageIndex, offset);
    }

    public int Stride { get; }
    public int ChunkCountRootPage { get; }
    public int ChunkCountPerPage { get; }

    public int ChunkCapacity => _map.Capacity;
    public int AllocatedChunkCount => _map.Allocated;
    public int FreeChunkCount => ChunkCapacity - AllocatedChunkCount;

}
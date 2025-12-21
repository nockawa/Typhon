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
/// Provides API to allocate chunks, the occupancy map is stored in the Metadata of each page. The minimum chunk size is 8 bytes.
/// </remarks>
public partial class ChunkBasedSegment : LogicalSegment
{
    private BitmapL3 _map;

    // Cached values for fast GetChunkLocation (avoids _map indirection)
    private readonly int _rootChunkCount;
    private readonly int _otherChunkCount;

    // Magic multiplier for fast division: quotient = (n * _divMagic) >> 32
    // This replaces expensive division (~20-80 cycles) with multiply+shift (~3-4 cycles)
    private readonly ulong _divMagic;

    internal ChunkBasedSegment(ManagedPagedMMF manager, int stride) : base(manager)
    {
        if (stride < sizeof(long))
        {
            throw new Exception($"Invalid stride size, given {stride}, but must be at least 8 bytes");
        }

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
        var length = filePageIndices.Length;
        for (int i = 0; i < length; i++)
        {
            GetPageExclusiveAccessor(i, out var page);
            using (page)
            {
                page.SetPageDirty();
                int longSize = (i==0 ? (ChunkCountRootPage+63) : (ChunkCountPerPage+63)) >> 6;
                page.PageMetadata.Cast<byte, long>().Slice(0, longSize).Clear();
            }
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

    private static readonly ThreadLocal<Memory<int>> SingleAlloc = new(() => new Memory<int>(new int[1]));

    public void ReserveChunk(int index) => _map.SetL0(index);
    public int AllocateChunk(bool clearContent)
    {
        var mem = SingleAlloc.Value;
        _map.Allocate(mem, clearContent);
        return mem.Span[0];
    }

    public IMemoryOwner<int> AllocateChunks(int count, bool clearContent)
    {
        var res = MemoryPool<int>.Shared.Rent(count);
        _map.Allocate(res.Memory, clearContent);
        return res;
    }

    public void FreeChunk(int chunkId) => _map.ClearL0(chunkId);

    public ChunkRandomAccessor CreateChunkRandomAccessor(int cachedPagesCount = 8, ChangeSet changeSet=null) =>
        ChunkRandomAccessor.GetFromPool(this, cachedPagesCount, changeSet);

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

        return (pageIndex + 1, offset);
    }

    public int Stride { get; }
    public int ChunkCountRootPage { get; }
    public int ChunkCountPerPage { get; }

    public int ChunkCapacity => _map.Capacity;
    public int AllocatedChunkCount => _map.Allocated;
    public int FreeChunkCount => ChunkCapacity - AllocatedChunkCount;

}
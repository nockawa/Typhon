// unset

using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Diagnostics;
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

    // Alignment padding: ensures chunks start at stride-aligned absolute page offsets (for ACLP).
    private readonly int _rootAlignmentPadding;
    private readonly int _otherAlignmentPadding;

    internal ChunkBasedSegment(EpochManager epochManager, ManagedPagedMMF manager, int stride) : base(manager)
    {
        if (stride < sizeof(long))
        {
            throw new Exception($"Invalid stride size, given {stride}, but must be at least 8 bytes");
        }

        _epochManager = epochManager;

        Stride = stride;

        // Alignment padding: ensures chunks start at stride-aligned absolute page offsets.
        // For stride=64: PageHeaderSize (192) % 64 == 0 → zero padding (backward compat).
        // For stride=128: 192 % 128 == 64 → 64-byte non-root padding, 112-byte root padding.
        bool needsAlignment = (PagedMMF.PageHeaderSize % stride) != 0;
        _otherAlignmentPadding = needsAlignment ? stride - (PagedMMF.PageHeaderSize % stride) : 0;
        _rootAlignmentPadding = needsAlignment ? (stride - ((PagedMMF.PageHeaderSize + RootHeaderIndexSectionLength) % stride)) % stride : 0;

        ChunkCountRootPage = (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength - _rootAlignmentPadding) / stride;
        ChunkCountPerPage = (PagedMMF.PageRawDataSize - _otherAlignmentPadding) / stride;

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

            // Clear chunk 0's raw data on the root page so the BTree directory starts clean.
            // We do this inline because ReserveChunk(index, clearContent:true) needs a ChunkAccessor
            // which requires an epoch scope — unavailable during segment creation.
            if (i == 0)
            {
                page.RawData<byte>(RootChunkDataOffset, Stride).Clear();
            }

            Manager.UnlatchPageExclusive(memPageIdx);
        }

        _map = new BitmapL3(this, false);
        ReserveChunk(0);                    // Mark chunk 0 as allocated ("null" sentinel) — data already cleared above
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

            // Prefer the caller's ChangeSet so that DirtyCounter increments for new pages are tracked by the UoW lifecycle
            // (ReleaseExcessDirtyMarks caps at 1, checkpoint writes correct data, DC→0). When no ChangeSet is provided,
            // a local one ensures pages are at least marked dirty — but these DC increments are "orphaned" (no UoW manages
            // their lifecycle), meaning a single checkpoint cycle can write zeros and decrement DC to 0, making the page
            // evictable before the caller protects it.
            var effectiveChangeSet = changeSet ?? new ChangeSet(Manager);

            // Grow the underlying logical segment (thread-safe, will allocate new pages)
            base.Grow(newLength, clearNewPages: true, effectiveChangeSet);

            // Clear the page metadata (bitmap) for newly allocated pages
            // This is critical! The base.Grow only clears raw data, not metadata.
            // Without this, InitFromLoad reads garbage and causes crashes.
            {
                var epoch = Manager.EpochManager.GlobalEpoch;
                for (int i = currentLength; i < newLength; i++)
                {
                    var page = GetPageExclusiveUnchecked(i, epoch, out var memPageIdx);
                    int longSize = (ChunkCountPerPage + 63) >> 6;
                    page.Metadata<long>(0, longSize).Clear();
                    effectiveChangeSet.AddByMemPageIndex(memPageIdx);

                    // Protect new pages against the checkpoint race during Grow→first-access window.
                    // After base.Grow unlatched each page (DC=1, ACW=0), checkpoint may have snapshot zeros,
                    // written to disk, and DecrementDirty→DC=0 before we re-latched here. The ChangeSet add
                    // above is idempotent (already tracked from base.Grow), so it doesn't re-increment DC.
                    // EnsureDirtyAtLeast(2) guarantees DC survives one checkpoint cycle: checkpoint decrements
                    // to 1 (page stays non-evictable) until AllocateBuffer's GetChunkAddress establishes
                    // ACW>0 protection.
                    Manager.EnsureDirtyAtLeast(memPageIdx, 2);

                    Manager.UnlatchPageExclusive(memPageIdx);
                }
            }

            // Create new bitmap by extending the old one incrementally. This avoids re-scanning ALL pages (which could deadlock if the caller holds pages via
            // ChunkAccessor). Instead, we copy state from the old bitmap and rely on the fact that new pages are guaranteed empty.
            _map = new BitmapL3(this, oldMap, currentLength);

            return true;
        }
    }
    
    /// <summary>
    /// Ensures the segment can hold at least <paramref name="minChunkCount"/> chunks by growing if necessary.
    /// Used during schema migration to pre-size new segments before mirroring occupancy bitmaps.
    /// </summary>
    internal void EnsureCapacity(int minChunkCount, ChangeSet changeSet = null)
    {
        while (ChunkCapacity < minChunkCount)
        {
            var pagesNeeded = 1 + ((minChunkCount - ChunkCountRootPage + ChunkCountPerPage - 1) / ChunkCountPerPage);
            if (!Grow(pagesNeeded, changeSet))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Attempts to grow the segment if capacity is exhausted.
    /// </summary>
    /// <param name="changeSet">Optional ChangeSet to track dirty pages from growth. When provided, new pages are tracked
    /// by the caller's ChangeSet (tied to a UoW lifecycle), preventing orphaned DirtyCounter increments that checkpoint
    /// can consume before the caller protects the page.</param>
    /// <returns>True if growth occurred or capacity was already available, false if at maximum capacity.</returns>
    private bool GrowIfNeeded(ChangeSet changeSet = null)
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

            return Grow(changeSet: changeSet);
        }
    }

    private static readonly ThreadLocal<Memory<int>> SingleAlloc = new(() => new Memory<int>(new int[1]));

    public void ReserveChunk(int index) => _map.SetL0(index);

    /// <summary>
    /// Reserves a specific chunk by index. If the chunk was not previously reserved and <paramref name="clearContent"/> is true, the chunk data is zeroed.
    /// </summary>
    /// <returns>True if the chunk was newly reserved; false if it was already reserved.</returns>
    public void ReserveChunk(int index, bool clearContent, ChangeSet changeSet = null)
    {
        _map.SetL0(index);
        if (clearContent)
        {
            var accessor = CreateChunkAccessor(changeSet);
            try
            {
                accessor.ClearChunk(index);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Allocates a single chunk from the segment.
    /// </summary>
    /// <param name="clearContent">Whether to clear the chunk content after allocation.</param>
    /// <param name="changeSet">Optional ChangeSet for tracking dirty pages during segment growth. When provided, growth pages are
    /// tracked by this ChangeSet (tied to a UoW lifecycle) instead of an orphaned local ChangeSet. This prevents a race where checkpoint
    /// writes newly-grown pages (zeros) and decrements their DirtyCounter to 0 before the caller can protect them.</param>
    /// <returns>The allocated chunk ID.</returns>
    /// <exception cref="ResourceExhaustedException">Thrown when the segment is at maximum capacity and cannot grow.</exception>
    /// <remarks>
    /// This method automatically grows the segment when capacity is exhausted.
    /// The allocation itself is lock-free; only growth operations require synchronization.
    /// </remarks>
    public int AllocateChunk(bool clearContent, ChangeSet changeSet = null)
    {
        var mem = SingleAlloc.Value;
        var loopCount = 0;

        while (true)
        {
            var map = _map; // Volatile read

            if (map.Allocate(mem, clearContent))
            {
                var chunkId = mem.Span[0];
                // Verify allocated chunk is within segment bounds
                if (chunkId >= ChunkCapacity)
                {
                    throw new InvalidOperationException(
                        $"ChunkBasedSegment.AllocateChunk: bitmap returned chunkId={chunkId} >= ChunkCapacity={ChunkCapacity} " +
                        $"(pages={Length}, rootChunks={_rootChunkCount}, otherChunks={_otherChunkCount})");
                }
                return chunkId;
            }

            loopCount++;

            if (loopCount > 100)
            {
                throw new InvalidOperationException($"ChunkBasedSegment.AllocateChunk infinite loop: FreeChunkCount={_map.FreeChunkCount}, ChunkCapacity={ChunkCapacity}, AllocatedChunkCount={AllocatedChunkCount}");
            }

            // Allocation failed - need to grow
            if (!GrowIfNeeded(changeSet))
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
    /// <param name="changeSet">Optional ChangeSet for tracking dirty pages during segment growth.</param>
    /// <returns>A memory owner containing the allocated chunk IDs.</returns>
    /// <exception cref="ResourceExhaustedException">Thrown when the segment cannot accommodate the requested chunks.</exception>
    /// <remarks>
    /// This method automatically grows the segment when capacity is exhausted.
    /// Growth is attempted iteratively until the request can be satisfied or maximum capacity is reached.
    /// </remarks>
    public IMemoryOwner<int> AllocateChunks(int count, bool clearContent, ChangeSet changeSet = null)
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

            if (!Grow(minNewPageCount, changeSet))
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
    [AllowCopy]
    [return: TransfersOwnership]
    internal ChunkAccessor CreateChunkAccessor(ChangeSet changeSet = null) => new(this, Manager, _epochManager, changeSet);

    /// <summary>
    /// Single-entry thread-local cache for warm <see cref="ChunkAccessor"/> reuse.
    /// Keeps the 16-entry SIMD page cache warm across repeated BTree operations on the same segment.
    /// </summary>
    private sealed class WarmAccessorCache
    {
        internal ChunkAccessor Accessor;       // 252 bytes — the warm accessor
        internal ChunkBasedSegment Segment;    // which segment this accessor belongs to
        internal long Epoch;                   // GlobalEpoch at creation time
        internal bool IsRented;                // debug guard against double-rent

        [ThreadStatic]
        // ReSharper disable once InconsistentNaming
        private static WarmAccessorCache _instance;
        internal static WarmAccessorCache Instance => _instance ??= new();
    }

    /// <summary>
    /// Rents a warm <see cref="ChunkAccessor"/> from the thread-local cache.
    /// On cache hit (same segment + same epoch): swaps ChangeSet only (~1ns).
    /// On cache miss: disposes old, creates new.
    /// Must be paired with <see cref="ReturnWarmAccessor"/> in a finally block.
    /// </summary>
    [AllowCopy]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ChunkAccessor RentWarmAccessor(ChangeSet changeSet = null)
    {
        var cache = WarmAccessorCache.Instance;
        Debug.Assert(!cache.IsRented, "double-rent (missing ReturnWarmAccessor?)");

        var currentEpoch = _epochManager.GlobalEpoch;
        if (cache.Segment == this && cache.Epoch == currentEpoch)
        {
            // Hot path: swap ChangeSet only, page cache stays warm
            cache.Accessor.ChangeSet = changeSet;
            cache.IsRented = true;
            return ref cache.Accessor;
        }

        // Cold path: different segment or epoch changed
        if (cache.Segment != null)
        {
            cache.Accessor.Dispose();
        }
        cache.Accessor = new ChunkAccessor(this, Manager, _epochManager, changeSet);
        cache.Segment = this;
        cache.Epoch = currentEpoch;
        cache.IsRented = true;
        return ref cache.Accessor;
    }

    /// <summary>
    /// Returns a warm <see cref="ChunkAccessor"/> to the thread-local cache.
    /// Flushes dirty pages via <see cref="ChunkAccessor.CommitChanges"/> but does NOT dispose —
    /// keeps the 16-entry SIMD page cache warm for the next operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReturnWarmAccessor()
    {
        var cache = WarmAccessorCache.Instance;
        Debug.Assert(cache.IsRented, "return without rent");
        cache.Accessor.CommitChanges();  // flush dirty pages, preserve page cache
        cache.IsRented = false;
        // Do NOT Dispose — keep the page cache warm
    }

    /// <summary>
    /// Updates the warm accessor cache's epoch to match the new GlobalEpoch.
    /// Called after <see cref="EpochManager.RefreshScope"/> within a transaction — the accessor's
    /// slot cache remains valid (FilePageIndex validation catches stale slots), so we avoid
    /// the costly cold-path that would re-stamp all hot pages via RequestPageEpoch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RefreshWarmCacheEpoch(long newEpoch)
    {
        var cache = WarmAccessorCache.Instance;
        cache.Epoch = newEpoch;
        var sibCache = WarmSiblingAccessorCache.Instance;
        sibCache.Epoch = newEpoch;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Second warm accessor cache — for B+Tree sibling/horizontal navigation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Second thread-local warm accessor cache dedicated to B+Tree sibling (horizontal) navigation.
    /// Separating vertical (parent→child) and horizontal (sibling) page access prevents sibling
    /// traversal from evicting parent path pages from the 16-slot accessor cache.
    /// Parent pages stay pinned via SlotRefCount in the primary warm accessor while siblings
    /// are loaded into this accessor — doubling the effective working set from 16 to 32 pages.
    /// </summary>
    private sealed class WarmSiblingAccessorCache
    {
        internal ChunkAccessor Accessor;
        internal ChunkBasedSegment Segment;
        internal long Epoch;
        internal bool IsRented;

        [ThreadStatic]
        // ReSharper disable once InconsistentNaming
        private static WarmSiblingAccessorCache _instance;
        internal static WarmSiblingAccessorCache Instance => _instance ??= new();
    }

    [AllowCopy]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ChunkAccessor RentWarmSiblingAccessor(ChangeSet changeSet = null)
    {
        var cache = WarmSiblingAccessorCache.Instance;
        Debug.Assert(!cache.IsRented, "double-rent sibling accessor (missing ReturnWarmSiblingAccessor?)");

        var currentEpoch = _epochManager.GlobalEpoch;
        if (cache.Segment == this && cache.Epoch == currentEpoch)
        {
            cache.Accessor.ChangeSet = changeSet;
            cache.IsRented = true;
            return ref cache.Accessor;
        }

        if (cache.Segment != null)
        {
            cache.Accessor.Dispose();
        }
        cache.Accessor = new ChunkAccessor(this, Manager, _epochManager, changeSet);
        cache.Segment = this;
        cache.Epoch = currentEpoch;
        cache.IsRented = true;
        return ref cache.Accessor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReturnWarmSiblingAccessor()
    {
        var cache = WarmSiblingAccessorCache.Instance;
        Debug.Assert(cache.IsRented, "return sibling without rent");
        cache.Accessor.CommitChanges();
        cache.IsRented = false;
    }

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

    /// <summary>Byte offset from start of raw data to first chunk on the root page (includes index section + alignment padding).</summary>
    internal int RootChunkDataOffset => RootHeaderIndexSectionLength + _rootAlignmentPadding;

    /// <summary>Byte offset from start of raw data to first chunk on non-root pages (alignment padding only).</summary>
    internal int OtherChunkDataOffset => _otherAlignmentPadding;

    public int ChunkCapacity => _map.Capacity;

    public int AllocatedChunkCount => _map.Allocated;
    public int FreeChunkCount => ChunkCapacity - AllocatedChunkCount;

    /// <summary>
    /// Checks whether the given chunk index is marked as allocated in the occupancy bitmap.
    /// </summary>
    public bool IsChunkAllocated(int index) => _map.IsSet(index);

}
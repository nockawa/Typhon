// unset

using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
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
/// Provides API to allocate chunks; the occupancy map is stored in the Metadata of each page.
/// Free-page tracking uses a lock-free Treiber stack of pages with available chunks.
/// The minimum chunk size is 8 bytes.
/// </remarks>
public partial class ChunkBasedSegment : LogicalSegment
{
    private const int EMPTY_PAGE = -1;

    private readonly Lock _growLock = new();
    private readonly EpochManager _epochManager;

    // Cached values for fast GetChunkLocation (avoids indirection)
    private readonly int _rootChunkCount;
    private readonly int _otherChunkCount;

    // Magic multiplier for fast division: quotient = (n * _divMagic) >> 32
    // This replaces expensive division (~20-80 cycles) with multiply+shift (~3-4 cycles)
    private readonly ulong _divMagic;

    // Alignment padding: ensures chunks start at stride-aligned absolute page offsets (for ACLP).
    private readonly int _rootAlignmentPadding;
    private readonly int _otherAlignmentPadding;

    // Bitmap configuration: number of metadata longs used for L0 bitmap per page type
    private readonly int _bitmapLongsRoot;
    private readonly int _bitmapLongsOther;

    // ═══════════════════════════════════════════════════════════════════════
    // Treiber stack allocator state
    // ═══════════════════════════════════════════════════════════════════════

    // Tagged pointer: version:32 | pageIndex:32. EMPTY_PAGE (-1) in lower 32 = empty stack.
    // Reads/writes are naturally atomic on x64. CAS via Interlocked.CompareExchange.
    private long _freeHead;

    // Total allocated chunks across all pages. Updated via Interlocked.Increment/Decrement.
    private int _allocatedCount;

    // Per-page free chunk count. The 0→1 transition triggers PushToFreeStack.
    private int[] _pageFreeCount;

    // Per-page next pointer for Treiber stack linked list. EMPTY_PAGE = tail.
    private int[] _nextPage;

    // Per-page "in free list" flag. Prevents double-push and stack cycles.
    // 0 = not in list, 1 = in list. Set via CAS in PushToFreeStack, cleared on pop.
    private int[] _inFreeList;

    // Total chunk capacity (updated on Grow under _growLock)
    private int _capacity;

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

        // Bitmap longs per page type: ceil(chunks / 64)
        _bitmapLongsRoot = (ChunkCountRootPage + 63) >> 6;
        _bitmapLongsOther = (ChunkCountPerPage + 63) >> 6;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Segment lifecycle: Create, Load, Grow
    // ═══════════════════════════════════════════════════════════════════════

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
            int longSize = i == 0 ? _bitmapLongsRoot : _bitmapLongsOther;
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

        // Initialize allocator state for a fresh (empty) segment
        _capacity = ComputeCapacity(length);
        _allocatedCount = 0;
        _pageFreeCount = new int[length];
        _nextPage = new int[length];
        _inFreeList = new int[length];

        _pageFreeCount[0] = ChunkCountRootPage;
        for (int i = 1; i < length; i++)
        {
            _pageFreeCount[i] = ChunkCountPerPage;
        }

        // Push all pages to free stack (reverse order for sequential fill)
        _freeHead = PackHead(0, EMPTY_PAGE);
        for (int i = length - 1; i >= 0; i--)
        {
            PushToFreeStack(i);
        }

        ReserveChunk(0);                    // Mark chunk 0 as allocated ("null" sentinel) — data already cleared above
        return true;
    }

    internal override bool Load(int filePageIndex)
    {
        if (!base.Load(filePageIndex))
        {
            return false;
        }

        // Rebuild allocator state from L0 bitmaps (source of truth)
        var epoch = Manager.EpochManager.GlobalEpoch;
        var length = Length;
        _capacity = ComputeCapacity(length);
        _allocatedCount = 0;
        _pageFreeCount = new int[length];
        _nextPage = new int[length];
        _inFreeList = new int[length];

        for (int i = 0; i < length; i++)
        {
            var maxChunks = i == 0 ? _rootChunkCount : _otherChunkCount;
            var bitmapLongs = i == 0 ? _bitmapLongsRoot : _bitmapLongsOther;
            var page = GetPage(i, epoch, out _);
            var metadata = page.MetadataReadOnly<long>();

            var popcount = CountAllocatedBits(metadata, bitmapLongs, maxChunks);

            _pageFreeCount[i] = maxChunks - popcount;
            _allocatedCount += popcount;
        }

        // Push pages with free space (reverse order for sequential fill)
        _freeHead = PackHead(0, EMPTY_PAGE);
        for (int i = length - 1; i >= 0; i--)
        {
            if (_pageFreeCount[i] > 0)
            {
                PushToFreeStack(i);
            }
        }

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
    /// After growth, new pages are pushed to the Treiber stack as free pages.
    /// </remarks>
    private bool Grow(int minNewPageCount = 0, ChangeSet changeSet = null)
    {
        lock (_growLock)
        {
            var currentLength = Length;

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

            // Clear the page metadata (bitmap) for newly allocated pages and protect against checkpoint race
            {
                var epoch = Manager.EpochManager.GlobalEpoch;
                for (int i = currentLength; i < newLength; i++)
                {
                    var page = GetPageExclusiveUnchecked(i, epoch, out var memPageIdx);
                    page.Metadata<long>(0, _bitmapLongsOther).Clear();
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

            // Expand allocator arrays — CAS on _freeHead (in PushToFreeStack below) provides memory barrier
            var newFreeCount = new int[newLength];
            var newNextPage = new int[newLength];
            var newInFreeList = new int[newLength];
            Array.Copy(_pageFreeCount, newFreeCount, currentLength);
            Array.Copy(_nextPage, newNextPage, currentLength);
            Array.Copy(_inFreeList, newInFreeList, currentLength);

            for (int i = currentLength; i < newLength; i++)
            {
                newFreeCount[i] = ChunkCountPerPage;
            }

            _pageFreeCount = newFreeCount;
            _nextPage = newNextPage;
            _inFreeList = newInFreeList;
            _capacity = ComputeCapacity(newLength);

            // Push new pages to free stack (reverse order for sequential fill)
            for (int i = newLength - 1; i >= currentLength; i--)
            {
                PushToFreeStack(i);
            }

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
        if (_allocatedCount < _capacity)
        {
            return true;
        }

        // Need to grow - acquire lock and double-check
        lock (_growLock)
        {
            if (_allocatedCount < _capacity)
            {
                return true; // Another thread grew while we waited
            }

            return Grow(changeSet: changeSet);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Allocation and deallocation
    // ═══════════════════════════════════════════════════════════════════════

    public void ReserveChunk(int index)
    {
        var (pageIndex, chunkInPage) = GetChunkLocation(index);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = Manager.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out var memPageIdx);
        var metadata = page.Metadata<long>();

        var prev = Interlocked.Or(ref metadata[wordIndex], mask);
        if ((prev & mask) != 0)
        {
            return; // already reserved
        }

        Manager.EnsureDirtyAtLeast(memPageIdx, 1);
        Interlocked.Increment(ref _allocatedCount);
        Interlocked.Decrement(ref _pageFreeCount[pageIndex]);
    }

    /// <summary>
    /// Reserves a specific chunk by index. If the chunk was not previously reserved and <paramref name="clearContent"/> is true, the chunk data is zeroed.
    /// </summary>
    public void ReserveChunk(int index, bool clearContent, ChangeSet changeSet = null)
    {
        ReserveChunk(index);
        if (clearContent)
        {
            using var accessor = CreateChunkAccessor(changeSet);
            accessor.ClearChunk(index);
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
    /// The allocation itself is lock-free; only growth and rebuild operations require synchronization.
    /// </remarks>
    public int AllocateChunk(bool clearContent, ChangeSet changeSet = null)
    {
        using var accessor = clearContent ? CreateChunkAccessor(changeSet) : default;

        while (true)
        {
            var head = _freeHead;
            var pageIndex = UnpackPage(head);

            if (pageIndex == EMPTY_PAGE)
            {
                if (_allocatedCount < _capacity)
                {
                    // Free space exists but stack is empty — recover orphaned pages
                    RebuildFreeStack();
                    continue;
                }

                // Truly full — need to grow
                if (!GrowIfNeeded(changeSet))
                {
                    ThrowHelper.ThrowResourceExhausted("Storage/ChunkBasedSegment/AllocateChunk", ResourceType.Memory, _allocatedCount, _capacity);
                }
                continue;
            }

            // Try to allocate from the head page
            var maxChunks = pageIndex == 0 ? _rootChunkCount : _otherChunkCount;
            var bitmapLongs = pageIndex == 0 ? _bitmapLongsRoot : _bitmapLongsOther;
            var epoch = Manager.EpochManager.GlobalEpoch;
            var page = GetPage(pageIndex, epoch, out var memPageIdx);
            var metadata = page.Metadata<long>();

            for (int w = 0; w < bitmapLongs; w++)
            {
                var word = metadata[w];
                while (word != -1L)
                {
                    var bit = BitOperations.TrailingZeroCount(~word);
                    var chunkInPage = w * 64 + bit;
                    if (chunkInPage >= maxChunks)
                    {
                        goto pageExhausted;
                    }

                    var mask = 1L << bit;
                    var prev = Interlocked.Or(ref metadata[w], mask);
                    if ((prev & mask) != 0)
                    {
                        // Lost race — update word with current state and retry next bit
                        word = prev | mask;
                        continue;
                    }

                    // SUCCESS — chunk claimed
                    Manager.EnsureDirtyAtLeast(memPageIdx, 1);
                    Interlocked.Increment(ref _allocatedCount);
                    Interlocked.Decrement(ref _pageFreeCount[pageIndex]);

                    var chunkId = PageOffsetToChunkIndex(pageIndex, chunkInPage);

                    if (clearContent)
                    {
                        accessor.ClearChunk(chunkId);
                    }

                    return chunkId;
                }
            }

            pageExhausted:
            // No free bit found on this page (consumed by concurrent allocators) — lazy pop
            TryPopFreeStack(head);
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
        var span = res.Memory.Span;
        var allocated = 0;

        try
        {
            for (int i = 0; i < count; i++)
            {
                span[i] = AllocateChunk(clearContent, changeSet);
                allocated++;
            }
        }
        catch
        {
            // Rollback: free any chunks we already allocated
            for (int i = 0; i < allocated; i++)
            {
                FreeChunk(span[i]);
            }
            res.Dispose();
            throw;
        }

        return res;
    }

    public void FreeChunk(int chunkId)
    {
        var (pageIndex, chunkInPage) = GetChunkLocation(chunkId);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = Manager.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out var memPageIdx);
        var metadata = page.Metadata<long>();

        var prev = Interlocked.And(ref metadata[wordIndex], ~mask);

        // Guard against double-free - only proceed if the bit was actually set
        if ((prev & mask) == 0)
        {
            return;
        }

        // Ensure DC≥1 so checkpoint includes this page in its next snapshot.
        // Without this, the page can be evicted as "clean" and reloaded from the last checkpoint snapshot
        // — which still has the bit SET, causing _allocatedCount to diverge from actual popcount.
        Manager.EnsureDirtyAtLeast(memPageIdx, 1);
        Interlocked.Decrement(ref _allocatedCount);

        var newFreeCount = Interlocked.Increment(ref _pageFreeCount[pageIndex]);
        if (newFreeCount == 1)
        {
            // Page just transitioned full → has-space: push to free stack
            PushToFreeStack(pageIndex);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Treiber stack operations
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackHead(int version, int pageIndex) => ((long)version << 32) | (uint)pageIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UnpackPage(long head) => (int)head;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UnpackVersion(long head) => (int)(head >> 32);

    /// <summary>
    /// Pushes a page onto the free stack. Uses an <see cref="_inFreeList"/> flag to prevent double-push (which would create cycles in the singly-linked list).
    /// </summary>
    private void PushToFreeStack(int pageIndex)
    {
        // CAS flag 0→1: prevents double-push and stack cycles.
        // If the page is already in the list, skip — another thread's push succeeded first.
        if (Interlocked.CompareExchange(ref _inFreeList[pageIndex], 1, 0) != 0)
        {
            return;
        }

        while (true)
        {
            var oldHead = _freeHead;
            _nextPage[pageIndex] = UnpackPage(oldHead);
            var newHead = PackHead(UnpackVersion(oldHead) + 1, pageIndex);
            if (Interlocked.CompareExchange(ref _freeHead, newHead, oldHead) == oldHead)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Attempts to pop the head page from the free stack. Only succeeds if the head hasn't changed since <paramref name="expectedHead"/> was read.
    /// On success, clears the <see cref="_inFreeList"/> flag and re-pushes the page if it still has free chunks (recovers orphans from the race where
    /// a concurrent FreeChunk's 0→1 push was blocked by _inFreeList=1 while the page was the head).
    /// </summary>
    private void TryPopFreeStack(long expectedHead)
    {
        var headPage = UnpackPage(expectedHead);
        if (headPage == EMPTY_PAGE)
        {
            return;
        }

        var nextPage = _nextPage[headPage];
        var newHead = PackHead(UnpackVersion(expectedHead) + 1, nextPage);
        if (Interlocked.CompareExchange(ref _freeHead, newHead, expectedHead) == expectedHead)
        {
            // Pop succeeded — page is no longer in the list.
            _inFreeList[headPage] = 0;

            // Re-push if the page still has free space. This handles the race where a concurrent FreeChunk's 0→1 push was blocked by _inFreeList=1 while
            // the page was the head. If freeCount is transiently wrong (says >0 but page is full), the re-pushed page will be lazily popped on the next
            // scan — freeCount settles within 1-2 iterations.
            if (_pageFreeCount[headPage] > 0)
            {
                PushToFreeStack(headPage);
            }
        }
    }

    /// <summary>
    /// Rebuilds the free stack from L0 bitmap truth when the stack is empty but free space exists.
    /// This recovers pages orphaned by rare concurrent race conditions (push-skip + pop).
    /// </summary>
    private void RebuildFreeStack()
    {
        lock (_growLock)
        {
            // Re-check under lock — another thread may have already rebuilt or grown
            if (UnpackPage(_freeHead) != EMPTY_PAGE)
            {
                return;
            }

            var epoch = Manager.EpochManager.GlobalEpoch;
            var length = Length;
            var totalAllocated = 0;

            for (int i = 0; i < length; i++)
            {
                var maxChunks = i == 0 ? _rootChunkCount : _otherChunkCount;
                var bitmapLongs = i == 0 ? _bitmapLongsRoot : _bitmapLongsOther;
                var page = GetPage(i, epoch, out _);
                var metadata = page.MetadataReadOnly<long>();

                var popcount = CountAllocatedBits(metadata, bitmapLongs, maxChunks);
                var freeCount = maxChunks - popcount;

                // Use Exchange to safely update even if concurrent FreeChunk is modifying this counter
                Interlocked.Exchange(ref _pageFreeCount[i], freeCount);
                totalAllocated += popcount;

                // Push pages with free space — _inFreeList guard in PushToFreeStack prevents duplicates if a concurrent FreeChunk already pushed this page
                if (freeCount > 0)
                {
                    PushToFreeStack(i);
                }
            }

            Interlocked.Exchange(ref _allocatedCount, totalAllocated);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeCapacity(int pageCount) => _rootChunkCount + (pageCount - 1) * _otherChunkCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PageOffsetToChunkIndex(int pageIndex, int chunkInPage)
    {
        if (pageIndex == 0)
        {
            return chunkInPage;
        }
        return _rootChunkCount + (pageIndex - 1) * _otherChunkCount + chunkInPage;
    }

    /// <summary>
    /// Counts allocated (set) bits in L0 bitmap words, masking invalid bits in the last word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountAllocatedBits(ReadOnlySpan<long> metadata, int bitmapLongs, int maxChunks)
    {
        var popcount = 0;
        for (int w = 0; w < bitmapLongs; w++)
        {
            var word = metadata[w];
            if (w == bitmapLongs - 1)
            {
                var validBits = maxChunks - w * 64;
                if (validBits < 64)
                {
                    word &= (1L << validBits) - 1;
                }
            }
            popcount += BitOperations.PopCount((ulong)word);
        }
        return popcount;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ChunkAccessor factory and warm caches
    // ═══════════════════════════════════════════════════════════════════════

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
        internal bool SuppressCommitChanges;   // batch mode: skip CommitChanges on return

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
        if (!cache.SuppressCommitChanges)
        {
            cache.Accessor.CommitChanges();  // flush dirty pages, preserve page cache
        }
        else
        {
            // Batch mode: drain eviction queue only (prevents overflow).
            // Live dirty flags stay set → ACW > 0 → blocks checkpoint (safe under holdoff).
            cache.Accessor.FlushDeferredEvictions();
        }
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

    /// <summary>
    /// Enters batch mode: suppresses <see cref="ChunkAccessor.CommitChanges"/> on warm accessor return.
    /// During batch mode, <c>ActiveChunkWriters</c> stays &gt; 0 on dirty pages, which is safe — the commit
    /// runs under holdoff and completes in bounded time. Call <see cref="ExitBatchMode"/> to flush.
    /// </summary>
    internal static void EnterBatchMode()
    {
        WarmAccessorCache.Instance.SuppressCommitChanges = true;
        WarmSiblingAccessorCache.Instance.SuppressCommitChanges = true;
    }

    /// <summary>
    /// Exits batch mode: re-enables <see cref="ChunkAccessor.CommitChanges"/> on warm accessor return
    /// and performs a single flush of accumulated dirty pages on both warm accessor caches.
    /// </summary>
    internal static void ExitBatchMode()
    {
        var cache = WarmAccessorCache.Instance;
        cache.SuppressCommitChanges = false;
        if (cache.Segment != null)
        {
            cache.Accessor.CommitChanges();
        }

        var sibCache = WarmSiblingAccessorCache.Instance;
        sibCache.SuppressCommitChanges = false;
        if (sibCache.Segment != null)
        {
            sibCache.Accessor.CommitChanges();
        }
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
        internal bool SuppressCommitChanges;   // batch mode: skip CommitChanges on return

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
        if (!cache.SuppressCommitChanges)
        {
            cache.Accessor.CommitChanges();
        }
        else
        {
            cache.Accessor.FlushDeferredEvictions();
        }
        cache.IsRented = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Addressing
    // ═══════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    public int Stride { get; }
    public int ChunkCountRootPage { get; }
    public int ChunkCountPerPage { get; }

    /// <summary>Byte offset from start of raw data to first chunk on the root page (includes index section + alignment padding).</summary>
    internal int RootChunkDataOffset => RootHeaderIndexSectionLength + _rootAlignmentPadding;

    /// <summary>Byte offset from start of raw data to first chunk on non-root pages (alignment padding only).</summary>
    internal int OtherChunkDataOffset => _otherAlignmentPadding;

    public int ChunkCapacity => _capacity;

    public int AllocatedChunkCount => _allocatedCount;
    public int FreeChunkCount => _capacity - _allocatedCount;

    /// <summary>
    /// Checks whether the given chunk index is marked as allocated in the occupancy bitmap.
    /// </summary>
    public bool IsChunkAllocated(int index)
    {
        var (pageIndex, chunkInPage) = GetChunkLocation(index);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = Manager.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out _);
        var data = page.MetadataReadOnly<long>();
        return (data[wordIndex] & mask) != 0L;
    }

}

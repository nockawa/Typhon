using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine;

/// <summary>
/// Epoch-protected chunk accessor with pure SOA layout and SIMD-optimized search.
/// Replaces ref-counted page access with epoch-based protection for page lifetime.
/// ~248 bytes (4 cache lines). Always pass by ref to avoid copies.
/// </summary>
/// <remarks>
/// <para><b>Three-tier hot path:</b></para>
/// <list type="number">
///   <item>MRU check — branch-prediction-friendly for repeated access to same page</item>
///   <item>SIMD Vector256 search — parallel scan of 16 cached page indices</item>
///   <item>Clock-hand eviction — O(1) amortized, cannot fail (no pinned slots)</item>
/// </list>
/// <para>Pages are protected from eviction by their epoch tag, not by ref-counting.
/// Dirty tracking uses a bitmask flushed to <see cref="ChangeSet"/> via
/// <see cref="ChangeSet.AddByMemPageIndex"/>.</para>
/// </remarks>
[NoCopy(Reason = "~248 byte struct with mutable SIMD cache and epoch-pinned pages")]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkAccessor : IDisposable
{
    // === SOA layout for SIMD search (1 cache line) ===
    private fixed int _pageIndices[16];        // 64 bytes — segment page indices, SIMD searchable

    // === Base addresses for direct pointer arithmetic (2 cache lines) ===
    private fixed long _baseAddresses[16];     // 128 bytes — raw data address per slot

    // === Compact state ===
    private ushort _dirtyFlags;                // 2 bytes — bitmask of dirty slots
    private byte _clockHand;                   // 1 byte — eviction cursor
    private byte _mruSlot;                     // 1 byte — most recently used slot
    private byte _usedSlots;                   // 1 byte — high water mark (0-16)

    // === Cached hot-path values ===
    private int _stride;                       // 4 bytes — chunk size in bytes
    private int _rootHeaderOffset;             // 4 bytes — LogicalSegment.RootHeaderIndexSectionLength

    // === References ===
    private ChunkBasedSegment _segment;
    private ChangeSet _changeSet;
    private PagedMMF _pagedMMF;
    private EpochManager _epochManager;

    // === Base address for computing memPageIndex on-demand (saves 64 bytes vs storing _memPageIndices[16]) ===
    private byte* _memPagesBaseAddr;           // 8 bytes

    // === Constants ===
    private const int Capacity = 16;
    private const int InvalidPageIndex = -1;

    public ChunkBasedSegment Segment => _segment;

    /// <summary>
    /// Compute memPageIndex from a slot's base address.
    /// This saves 64 bytes by not storing _memPageIndices[16].
    /// Cost: one subtraction + one shift (2-3 cycles) — only used in slow paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMemPageIndexFromSlot(int slot) =>
        // _baseAddresses[slot] points to raw data (after PageHeaderSize)
        // memPageIndex = (rawDataAddr - PageHeaderSize - _memPagesBaseAddr) / PageSize
        (int)(((byte*)_baseAddresses[slot] - PagedMMF.PageHeaderSize - _memPagesBaseAddr) >> PagedMMF.PageSizePow2);

    /// <summary>
    /// Create a new ChunkAccessor. All storage is stack-allocated — zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkAccessor(ChunkBasedSegment segment, PagedMMF pagedMMF, EpochManager epochManager, ChangeSet changeSet = null)
    {
        Debug.Assert(epochManager.IsCurrentThreadInScope, "ChunkAccessor must be created inside an epoch scope");
        _segment = segment;
        _pagedMMF = pagedMMF;
        _epochManager = epochManager;
        _changeSet = changeSet;
        _mruSlot = 0;
        _usedSlots = 0;
        _clockHand = 0;
        _dirtyFlags = 0;
        _stride = segment.Stride;
        _rootHeaderOffset = LogicalSegment.RootHeaderIndexSectionLength;
        _memPagesBaseAddr = pagedMMF.MemPagesBaseAddress;

        // Initialize page indices to invalid (-1). Other arrays are zero-initialized by struct init.
        fixed (int* pageIndices = _pageIndices)
        {
            Unsafe.InitBlockUnaligned(pageIndices, 0xFF, 64);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Public API — chunk access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get mutable reference to chunk. The returned ref is valid for the lifetime of the
    /// enclosing <see cref="EpochGuard"/> — epoch protection prevents page eviction regardless
    /// of slot eviction within this accessor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T GetChunk<T>(int chunkId, bool dirty = false) where T : unmanaged
        => ref Unsafe.AsRef<T>(GetChunkAddress(chunkId, dirty));

    /// <summary>
    /// Get read-only reference to chunk. Safe for the lifetime of the enclosing <see cref="EpochGuard"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref readonly T GetChunkReadOnly<T>(int chunkId) where T : unmanaged
        => ref GetChunk<T>(chunkId, dirty: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Span<byte> GetChunkAsSpan(int index, bool dirtyPage = false) => new(GetChunkAddress(index, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index) => new(GetChunkAddress(index, false), _stride);

    internal void ClearChunk(int index)
    {
        var addr = GetChunkAddress(index);
        new Span<long>(addr, _stride / 8).Clear();
    }

    /// <summary>
    /// Mark a loaded chunk's slot as dirty without accessing the chunk data.
    /// </summary>
    internal void DirtyChunk(int index)
    {
        (int si, _) = _segment.GetChunkLocation(index);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(si);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                _dirtyFlags |= (ushort)(1 << BitOperations.TrailingZeroCount(mask0));
                return;
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                _dirtyFlags |= (ushort)(1 << (8 + BitOperations.TrailingZeroCount(mask1)));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty flush
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flush dirty bitmask to ChangeSet.
    /// </summary>
    public void CommitChanges()
    {
        if (_changeSet == null || _dirtyFlags == 0)
        {
            return;
        }

        var flags = (int)_dirtyFlags;
        while (flags != 0)
        {
            var bit = BitOperations.TrailingZeroCount(flags);
            _changeSet.AddByMemPageIndex(GetMemPageIndexFromSlot(bit));
            flags &= ~(1 << bit);
        }
        _dirtyFlags = 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Exclusive latch (for B+Tree node splits, etc.)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acquire exclusive latch on an epoch-protected page (Idle → Exclusive).
    /// The chunk must already be loaded into a slot.
    /// </summary>
    public bool TryLatchExclusive(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                return _pagedMMF.TryLatchPageExclusive(GetMemPageIndexFromSlot(BitOperations.TrailingZeroCount(mask0)));
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                return _pagedMMF.TryLatchPageExclusive(GetMemPageIndexFromSlot(8 + BitOperations.TrailingZeroCount(mask1)));
            }
        }

        return false; // Page not cached
    }

    /// <summary>
    /// Release exclusive latch on an epoch-protected page (Exclusive → Idle).
    /// </summary>
    public void UnlatchExclusive(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                _pagedMMF.UnlatchPageExclusive(GetMemPageIndexFromSlot(BitOperations.TrailingZeroCount(mask0)));
                return;
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                _pagedMMF.UnlatchPageExclusive(GetMemPageIndexFromSlot(8 + BitOperations.TrailingZeroCount(mask1)));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Segment header access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Access segment header (for internal ChunkBasedSegment operations).
    /// </summary>
    internal ref T GetChunkBasedSegmentHeader<T>(int offset, bool dirty) where T : unmanaged
    {
        // Page 0 is always the root page containing the header — ensure it's loaded
        GetChunkAddress(0, dirty);

        // _baseAddresses[_mruSlot] points to raw data area (after PageHeaderSize).
        // Walk back to page start to apply absolute offset.
        var rawDataAddr = (byte*)_baseAddresses[_mruSlot];
        var pageStart = rawDataAddr - PagedMMF.PageHeaderSize;
        return ref Unsafe.AsRef<T>(pageStart + offset);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HOT PATH: chunk address resolution
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CRITICAL HOT PATH: Get chunk address with maximum performance.
    /// Three-tier optimization:
    /// 1. MRU check (branch prediction friendly for repeated access)
    /// 2. SIMD search (parallel scan of 16 slots)
    /// 3. Clock-hand eviction (O(1) amortized, cannot fail)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal byte* GetChunkAddress(int chunkId, bool dirty = false)
    {
        (int pageIndex, int offset) = _segment.GetChunkLocation(chunkId);

        // === ULTRA FAST PATH: MRU check ===
        var mru = _mruSlot;
        if (_pageIndices[mru] == pageIndex)
        {
            if (dirty)
            {
                _dirtyFlags |= (ushort)(1 << mru);
            }

            var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
            return (byte*)_baseAddresses[mru] + headerOffset + offset * _stride;
        }

        // === FAST PATH: SIMD search through cache ===
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            // Search first 8 slots
            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                var slot = BitOperations.TrailingZeroCount(mask0);
                return GetFromSlot(slot, pageIndex, offset, dirty);
            }

            // Search second 8 slots
            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                var slot = 8 + BitOperations.TrailingZeroCount(mask1);
                return GetFromSlot(slot, pageIndex, offset, dirty);
            }
        }

        // === SLOW PATH: Cache miss — clock-hand eviction ===
        return LoadAndGet(pageIndex, offset, dirty);
    }

    /// <summary>
    /// Helper for SIMD search hit: update MRU, dirty flag, compute address.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* GetFromSlot(int slotIndex, int pageIndex, int offset, bool dirty)
    {
        _mruSlot = (byte)slotIndex;

        if (dirty)
        {
            _dirtyFlags |= (ushort)(1 << slotIndex);
        }

        var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
        return (byte*)_baseAddresses[slotIndex] + headerOffset + offset * _stride;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SLOW PATH: eviction and page loading
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cache miss slow path: clock-hand eviction, load new page.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // Keep hot paths small
    private byte* LoadAndGet(int pageIndex, int offset, bool dirty)
    {
        var slot = FindEvictionSlot();
        EvictSlot(slot);
        LoadIntoSlot(slot, pageIndex);
        return GetFromSlot(slot, pageIndex, offset, dirty);
    }

    /// <summary>
    /// Clock-hand eviction: O(1) amortized, always succeeds (no pinning mechanism).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindEvictionSlot()
    {
        // Fast path: use unused slots first
        if (_usedSlots < Capacity)
        {
            return _usedSlots++;
        }

        // Clock-hand: advance, skip MRU
        var hand = (byte)((_clockHand + 1) & 0xF);
        if (hand == _mruSlot)
        {
            hand = (byte)((hand + 1) & 0xF);
        }
        _clockHand = hand;
        return hand;
    }

    /// <summary>
    /// Evict a slot: flush dirty state. No page release — epoch protects lifetime.
    /// </summary>
    private void EvictSlot(int slot)
    {
        if (_pageIndices[slot] == InvalidPageIndex)
        {
            return;
        }

        // Flush dirty to ChangeSet before evicting
        var mask = 1 << slot;
        if ((_dirtyFlags & mask) != 0 && _changeSet != null)
        {
            _changeSet.AddByMemPageIndex(GetMemPageIndexFromSlot(slot));
            _dirtyFlags = (ushort)(_dirtyFlags & ~mask);
        }

        _pageIndices[slot] = InvalidPageIndex;
    }

    /// <summary>
    /// Load a page into a slot via epoch-protected access.
    /// </summary>
    private void LoadIntoSlot(int slot, int pageIndex)
    {
        var filePageIndex = _segment.Pages[pageIndex];
        var result = _pagedMMF.RequestPageEpoch(filePageIndex, _epochManager.GlobalEpoch, out var memPageIndex);
        Debug.Assert(result, $"RequestPageEpoch failed for file page {filePageIndex}");

        _pageIndices[slot] = pageIndex;
        _baseAddresses[slot] = (long)_pagedMMF.GetMemPageRawDataAddress(memPageIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dispose accessor: flush dirty pages. No page release — epoch handles lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_segment == null)
        {
            return;
        }

        CommitChanges();
        _usedSlots = 0;
        _segment = null!;
    }
}

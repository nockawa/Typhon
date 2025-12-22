using JetBrains.Annotations;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine;

/// <summary>
/// Per-slot metadata stored in AoS layout for cache locality.
/// 16 bytes per slot = 4 slots per 64-byte cache line.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SlotData
{
    public long BaseAddress;      // 8 bytes - cached page raw data address
    public int HitCount;          // 4 bytes - LRU tracking
    public short PinCounter;      // 2 bytes - scope protection
    public byte DirtyFlag;        // 1 byte - lazy dirty tracking
    public byte PromoteCounter;   // 1 byte - exclusive page promotion
}

/// <summary>
/// Inline array for SlotData storage (C# 12+).
/// </summary>
[InlineArray(16)]
internal struct SlotDataBuffer
{
    private SlotData _element;
}

/// <summary>
/// Inline array for PageAccessor storage (C# 12+).
/// </summary>
[InlineArray(16)]
internal struct PageAccessorBuffer
{
    private PageAccessor _element;
}

/// <summary>
/// Helper for throwing exceptions without inlining the throw site.
/// </summary>
internal static class ThrowHelper
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgument(string message) => throw new ArgumentException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidOp(string message) => throw new InvalidOperationException(message);
}

/// <summary>
/// Stack-allocated chunk accessor combining best of ChunkRandomAccessor and StackChunkAccessor.
/// - Zero heap allocation (struct, always pass by ref)
/// - SIMD-optimized hot paths
/// - MRU cache for repeated access
/// - Scoped safety for multi-chunk operations
/// - Fixed 16-slot capacity for optimal performance
/// WARNING: This struct is ~1KB in size. Always pass by ref to avoid expensive copies.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkAccessor : IDisposable
{
    // === Hybrid SOA+AoS layout for optimal performance ===
    // SOA: page indices for SIMD search
    private fixed int _pageIndices[16];       // 64 bytes - SIMD searchable via Vector256

    // AoS: per-slot metadata for cache locality after SIMD finds the slot
    private SlotDataBuffer _slots;            // 256 bytes - all metadata in one cache line per slot access

    // === Page accessors (inline array, no heap allocation) ===
    private PageAccessorBuffer _pageAccessors; // 16 PageAccessor structs

    // === Segment state ===
    private ChunkBasedSegment _segment;
    private ChangeSet _changeSet;

    // === Cached hot-path values ===
    private byte _mruSlot;                     // Most Recently Used slot for ultra-fast repeat access
    private byte _usedSlots;                   // High water mark (0-16)
    private int _stride;                       // Cached chunk size
    private int _rootHeaderOffset;             // Cached LogicalSegment.RootHeaderIndexSectionLength

    // === Constants ===
    private const int Capacity = 16;
    private const int InvalidPageIndex = -1;
    
    public ChunkBasedSegment Segment => _segment;

    /// <summary>
    /// Create a new ChunkAccessor. All storage is stack-allocated - zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ChunkAccessor Create(ChunkBasedSegment segment, ChangeSet changeSet = null)
    {
        var accessor = new ChunkAccessor
        {
            _segment = segment,
            _changeSet = changeSet,
            _mruSlot = 0,
            _usedSlots = 0,
            _stride = segment.Stride,
            _rootHeaderOffset = LogicalSegment.RootHeaderIndexSectionLength
        };

        // Initialize page indices to invalid (-1). Other arrays are already zero-initialized by 'new'.
        Unsafe.InitBlockUnaligned(accessor._pageIndices, 0xFF, 64);

        return accessor;
    }

    /// <summary>
    /// Get mutable reference to chunk. UNSAFE: ref valid until a different page is accessed.
    /// ULTRA-FAST PATH for hot loops (B+Tree operations, etc.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T GetChunk<T>(int chunkId, bool dirty = false) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(chunkId, dirty));

    /// <summary>
    /// Get read-only reference to chunk. Same performance as Get, safer semantics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref readonly T GetChunkReadOnly<T>(int chunkId) where T : unmanaged => ref GetChunk<T>(chunkId, dirty: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Span<byte> GetChunkAsSpan(int index, bool dirtyPage = false) => new(GetChunkAddress(index, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index, bool dirtyPage = false) => new(GetChunkAddress(index, dirtyPage), _stride);
    
    /// <summary>
    /// Get scoped access to chunk with automatic pinning. SAFE for multi-chunk operations.
    /// Pin prevents eviction until scope is disposed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkHandle GetChunkHandle(int chunkId, bool dirty = false)
    {
        var addr = GetChunkAddressAndPin(chunkId, dirty, out var slotIndex);
        void* selfPtr = Unsafe.AsPointer(ref this);
        return new ChunkHandle(selfPtr, slotIndex, addr, _stride);
    }
    
    internal void ClearChunk(int index)
    {
        var addr = GetChunkAddress(index);
        new Span<long>(addr, _stride / 8).Clear();
    }

    internal void DirtyChunk(int index)
    {
        (int si, _) = _segment.GetChunkLocation(index);
        for (int i = 0, used = 0; used < _usedSlots; i++)
        {
            if (_pageIndices[i] == InvalidPageIndex)
            {
                continue;
            }

            ++used;
            ref var slotData = ref _slots[i];

            if (_pageIndices[i] == si)
            {
                slotData.DirtyFlag = 1;
                return;
            }
        }
    }
    

    /// <summary>
    /// Commit the dirty state of each page and release the shared access.
    /// </summary>
    /// <remarks>
    /// Typically call this method at the end of an atomic operation to update the <see cref="PagedMMF"/> accordingly.
    /// If the page was promoted in exclusive mode, it won't be release, just simply ignored.
    /// </remarks>
    public void CommitChanges()
    {
        for (int i = 0, used = 0; used < _usedSlots; i++)
        {
            if (_pageIndices[i] == InvalidPageIndex)
            {
                continue;
            }

            ++used;
            ref var slotData = ref _slots[i];

            // Lazy dirty tracking: flush on dispose
            if (slotData.DirtyFlag != 0 && _changeSet != null)
            {
                _changeSet.Add(_pageAccessors[i]);
                slotData.DirtyFlag = 0;
            }
        }        
    }
    
    /// <summary>
    /// CRITICAL HOT PATH: Get chunk address with maximum performance.
    /// Three-tier optimization:
    /// 1. MRU check (branch prediction friendly for repeated access)
    /// 2. SIMD search (parallel scan of 16 slots)
    /// 3. LRU eviction (cache miss, load new page)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal byte* GetChunkAddress(int chunkId, bool dirty = false)
    {
        (int pageIndex, int offset) = _segment.GetChunkLocation(chunkId);

        // === ULTRA FAST PATH: MRU check ===
        // Huge win for B+Tree operations that access same node repeatedly
        var mru = _mruSlot;
        if (_pageIndices[mru] == pageIndex)
        {
            ref var slot = ref _slots[mru];
            if (dirty)
            {
                slot.DirtyFlag = 1;
            }

            slot.HitCount++;

            // Compute address with cached header offset (no function call)
            var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
            return (byte*)slot.BaseAddress + headerOffset + offset * _stride;
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

        // === SLOW PATH: Cache miss - evict LRU and load new page ===
        return LoadAndGet(pageIndex, offset, dirty);
    }

    /// <summary>
    /// Get chunk address and pin the slot (for scoped access).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* GetChunkAddressAndPin(int chunkId, bool dirty, out int slotIndex)
    {
        (int pageIndex, int offset) = _segment.GetChunkLocation(chunkId);

        // Check MRU first
        var mru = _mruSlot;
        if (_pageIndices[mru] == pageIndex)
        {
            slotIndex = mru;
            ref var slot = ref _slots[mru];

            if (dirty)
            {
                slot.DirtyFlag = 1;
            }

            slot.HitCount++;
            slot.PinCounter++;

            var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
            return (byte*)slot.BaseAddress + headerOffset + offset * _stride;
        }

        // SIMD search
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                slotIndex = BitOperations.TrailingZeroCount(mask0);
                return GetFromSlotAndPin(slotIndex, pageIndex, offset, dirty);
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                slotIndex = 8 + BitOperations.TrailingZeroCount(mask1);
                return GetFromSlotAndPin(slotIndex, pageIndex, offset, dirty);
            }
        }

        // Cache miss
        return LoadAndGetWithPin(pageIndex, offset, dirty, out slotIndex);
    }

    /// <summary>
    /// Helper for SIMD search hit: update MRU, hit count, dirty flag, compute address.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* GetFromSlot(int slotIndex, int pageIndex, int offset, bool dirty)
    {
        _mruSlot = (byte)slotIndex;
        ref var slot = ref _slots[slotIndex];

        if (dirty)
        {
            slot.DirtyFlag = 1;
        }

        slot.HitCount++;

        var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
        return (byte*)slot.BaseAddress + headerOffset + offset * _stride;
    }

    /// <summary>
    /// Helper for SIMD search hit with pinning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* GetFromSlotAndPin(int slotIndex, int pageIndex, int offset, bool dirty)
    {
        _mruSlot = (byte)slotIndex;
        ref var slot = ref _slots[slotIndex];

        if (dirty)
        {
            slot.DirtyFlag = 1;
        }

        slot.HitCount++;
        slot.PinCounter++;

        var headerOffset = pageIndex == 0 ? _rootHeaderOffset : 0;
        return (byte*)slot.BaseAddress + headerOffset + offset * _stride;
    }

    /// <summary>
    /// Cache miss slow path: find LRU slot, evict, load new page.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // Keep hot paths small
    private byte* LoadAndGet(int pageIndex, int offset, bool dirty)
    {
        var slot = FindLRUSlot();
        if (slot == -1)
        {
            ThrowHelper.ThrowInvalidOp("All 16 cache slots are pinned or promoted. Cannot evict.");
        }

        EvictSlot(slot);
        LoadIntoSlot(slot, pageIndex);

        return GetFromSlot(slot, pageIndex, offset, dirty);
    }

    /// <summary>
    /// Cache miss slow path with pinning.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte* LoadAndGetWithPin(int pageIndex, int offset, bool dirty, out int slotIndex)
    {
        var slot = FindLRUSlot();
        if (slot == -1)
        {
            ThrowHelper.ThrowInvalidOp("All 16 cache slots are pinned or promoted. Cannot evict.");
        }

        EvictSlot(slot);
        LoadIntoSlot(slot, pageIndex);

        slotIndex = slot;
        return GetFromSlotAndPin(slot, pageIndex, offset, dirty);
    }

    /// <summary>
    /// Find the LRU (Least Recently Used) slot for eviction.
    /// Scans for slot with minimum hit count that isn't pinned or promoted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindLRUSlot()
    {
        // Fast path: use unused slots first
        if (_usedSlots < Capacity)
        {
            return _usedSlots++;
        }

        // Scan for minimum hit count among evictable slots
        var minHit = int.MaxValue;
        var minSlot = -1;

        for (int i = 0; i < Capacity; i++)
        {
            ref var slot = ref _slots[i];
            if (slot.PinCounter == 0 && slot.PromoteCounter == 0 && slot.HitCount < minHit)
            {
                minHit = slot.HitCount;
                minSlot = i;
            }
        }

        return minSlot;
    }

    /// <summary>
    /// Evict a slot: flush dirty page, release page accessor.
    /// </summary>
    private void EvictSlot(int slot)
    {
        if (_pageIndices[slot] == InvalidPageIndex)
        {
            return; // Slot never used
        }

        ref var slotData = ref _slots[slot];

        // Lazy dirty tracking: add to ChangeSet on eviction
        if (slotData.DirtyFlag != 0 && _changeSet != null)
        {
            _changeSet.Add(_pageAccessors[slot]);
        }

        _pageAccessors[slot].Dispose();
        _pageIndices[slot] = InvalidPageIndex;
        slotData.DirtyFlag = 0;
        slotData.HitCount = 0;
    }

    /// <summary>
    /// Load a new page into a slot.
    /// </summary>
    private void LoadIntoSlot(int slot, int pageIndex)
    {
        _segment.GetPageSharedAccessor(pageIndex, out _pageAccessors[slot]);
        _pageIndices[slot] = pageIndex;

        ref var slotData = ref _slots[slot];
        slotData.BaseAddress = (long)_pageAccessors[slot].GetRawDataAddr();
        slotData.HitCount = 1; // Initial hit
        slotData.PinCounter = 0;
        slotData.PromoteCounter = 0;
    }

    /// <summary>
    /// Try to promote a chunk's page from Shared to Exclusive access.
    /// Required for certain B+Tree operations. Must call DemoteChunk when done.
    /// </summary>
    public bool TryPromoteChunk(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        // SIMD search for the page
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                return TryPromoteSlot(BitOperations.TrailingZeroCount(mask0));
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                return TryPromoteSlot(8 + BitOperations.TrailingZeroCount(mask1));
            }
        }

        return false; // Page not cached
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPromoteSlot(int slot)
    {
        ref var slotData = ref _slots[slot];

        // Already promoted - increment ref count
        if (slotData.PromoteCounter > 0)
        {
            slotData.PromoteCounter++;
            return true;
        }

        // Try to promote to exclusive
        if (_pageAccessors[slot].TryPromoteToExclusive())
        {
            slotData.PromoteCounter = 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Demote a chunk's page from Exclusive back to Shared access.
    /// </summary>
    public void DemoteChunk(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            var v0 = Vector256.Load(indices);
            var mask0 = Vector256.Equals(v0, target).ExtractMostSignificantBits();
            if (mask0 != 0)
            {
                DemoteSlot(BitOperations.TrailingZeroCount(mask0));
                return;
            }

            var v1 = Vector256.Load(indices + 8);
            var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
            if (mask1 != 0)
            {
                DemoteSlot(8 + BitOperations.TrailingZeroCount(mask1));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DemoteSlot(int slot)
    {
        ref var slotData = ref _slots[slot];
        if (slotData.PromoteCounter > 0 && --slotData.PromoteCounter == 0)
        {
            _pageAccessors[slot].DemoteExclusive();
        }
    }

    /// <summary>
    /// Internal: Unpin a slot (called by ChunkScope disposal).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void UnpinSlot(int slot) => _slots[slot].PinCounter--;

    /// <summary>
    /// Access segment header (for internal ChunkBasedSegment operations).
    /// </summary>
    internal ref T GetChunkBasedSegmentHeader<T>(int offset, bool dirty) where T : unmanaged
    {
        // Page 0 is always the root page containing the header
        var addr = GetChunkAddress(0, dirty);

        // Walk back from chunk data to page header
        var pageHeaderAddr = addr - _rootHeaderOffset - PagedMMF.PageHeaderSize;
        return ref Unsafe.AsRef<T>(pageHeaderAddr + offset);
    }

    /// <summary>
    /// Dispose accessor: flush all dirty pages, release all page locks.
    /// </summary>
    public void Dispose()
    {
        for (int i = 0, used = 0; used < _usedSlots; i++)
        {
            if (_pageIndices[i] == InvalidPageIndex)
            {
                continue;
            }

            ++used;
            ref var slotData = ref _slots[i];

            // Demote any promoted pages
            if (slotData.PromoteCounter > 0)
            {
                _pageAccessors[i].DemoteExclusive();
            }

            // Lazy dirty tracking: flush on dispose
            if (slotData.DirtyFlag != 0 && _changeSet != null)
            {
                _changeSet.Add(_pageAccessors[i]);
            }

            _pageAccessors[i].Dispose();
        }

        _usedSlots = 0;
        _segment = null!;
    }
}

/// <summary>
/// Scoped chunk access with automatic pinning.
/// Prevents eviction until disposed - safe for multi-chunk operations.
/// </summary>
[PublicAPI]
public unsafe struct ChunkHandle : IDisposable
{
    private void* _ownerPtr;       // Pointer to ChunkAccessor on stack
    private byte* _chunkAddress;
    private int _chunkLength;
    private int _slotIndex;

    internal ChunkHandle(void* owner, int slotIndex, byte* chunkAddress, int chunkLength)
    {
        _ownerPtr = owner;
        _slotIndex = slotIndex;
        _chunkAddress = chunkAddress;
        _chunkLength = chunkLength;
    }

    public byte* Address => _chunkAddress;
    
    /// <summary>
    /// Get mutable reference to chunk data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T AsRef<T>() => ref Unsafe.AsRef<T>(_chunkAddress);

    /// <summary>
    /// Get chunk data as mutable span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> AsSpan() => new(_chunkAddress, _chunkLength);

    /// <summary>
    /// Get chunk data as read-only span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsReadOnlySpan() => new(_chunkAddress, _chunkLength);

    /// <summary>
    /// Get chunk data as SpanStream for sequential parsing (revision enumeration).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanStream AsStream() => new(new Span<byte>(_chunkAddress, _chunkLength));

    public bool IsDefault => _ownerPtr == null;
    
    /// <summary>
    /// Dispose scope: unpin the slot, making it evictable again.
    /// </summary>
    public void Dispose()
    {
        if (_ownerPtr != null)
        {
            ref var owner = ref Unsafe.AsRef<ChunkAccessor>(_ownerPtr);
            owner.UnpinSlot(_slotIndex);
            _ownerPtr = null;
        }
    }
}
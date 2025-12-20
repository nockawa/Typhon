using JetBrains.Annotations;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine;

/// <summary>
/// Inline array for PageAccessor storage (C# 12+).
/// Allows fixed-size array of structs to be embedded in ref struct.
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
/// Stack-allocated chunk accessor with scope-based protection.
/// Zero heap allocation, SIMD-optimized, safe for recursion.
///
/// All state is self-contained - no external allocations required.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public unsafe ref struct StackChunkAccessor : IDisposable
{
    private fixed int _pageIndices[16];       // 64 bytes - SIMD searchable
    private fixed long _baseAddresses[16];    // 128 bytes
    private fixed byte _scopeLevels[16];      // 16 bytes - which scope owns each slot
    private fixed byte _dirtyFlags[16];       // 16 bytes
    private fixed byte _promoteCounters[16];  // 16 bytes - promotion reference count per slot

    private PageAccessorBuffer _pageAccessors; // Inline array, no external allocation
    private ChunkBasedSegment _segment;
    private ChangeSet _changeSet;

    // === State ===
    private byte _currentScope;               // Current recursion depth (0-15)
    private byte _usedSlots;                  // High water mark
    private byte _capacity;                   // Max slots (8 or 16)
    private byte _clockHand;
    
    // === Cached data ===
    private int _stride;

    // === Constants ===
    private const byte MaxScope = 15;
    private const byte InvalidSlot = 255;
    private const int InvalidPageIndex = -1;

    /// <summary>
    /// Create a new StackChunkAccessor. All storage is internal - no external allocations needed.
    /// </summary>
    /// <param name="segment">The ChunkBasedSegment to access</param>
    /// <param name="changeSet">Optional change set for dirty page tracking</param>
    /// <param name="capacity">8 (faster, non-recursive) or 16 (recursive with scopes)</param>
    public static StackChunkAccessor Create(ChunkBasedSegment segment, ChangeSet changeSet = null, int capacity = 16)
    {
        if (capacity != 8 && capacity != 16)
        {
            ThrowHelper.ThrowArgument("Capacity must be 8 or 16 for SIMD alignment");
        }

        var accessor = new StackChunkAccessor();
        accessor._segment = segment;
        accessor._changeSet = changeSet;
        accessor._capacity = (byte)capacity;
        accessor._currentScope = 0;
        accessor._usedSlots = 0;
        accessor._stride = segment.Stride;

        Unsafe.InitBlockUnaligned(accessor._pageIndices, 0xFF, 64);
        Unsafe.InitBlockUnaligned(accessor._scopeLevels, 0xFF, 16);
        Unsafe.InitBlockUnaligned(accessor._dirtyFlags, 0x00, 16);
        Unsafe.InitBlockUnaligned(accessor._promoteCounters, 0x00, 16);

        return accessor;
    }

    /// <summary>
    /// Enter a new scope. All chunks acquired after this are protected until ExitScope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void EnterScope()
    {
        if (_currentScope >= MaxScope)
        {
            ThrowHelper.ThrowInvalidOp("Maximum scope depth (15) exceeded");
        }

        _currentScope++;
    }

    /// <summary>
    /// Exit current scope. Chunks acquired in this scope become evictable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ExitScope()
    {
        if (_currentScope == 0)
        {
            ThrowHelper.ThrowInvalidOp("No scope to exit");
        }

        // Mark slots owned by this scope as evictable (scope = 255)
        var currentScope = _currentScope;
        for (int i = 0; i < _usedSlots; i++)
        {
            if (_scopeLevels[i] == currentScope)
            {
                _scopeLevels[i] = 255;  // Now evictable
            }
        }

        _currentScope--;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Span<byte> GetChunkAsSpan(int index, bool dirtyPage = false) => new(GetChunkAddress(index, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index, bool dirtyPage = false) => new(GetChunkAddress(index, dirtyPage), _stride);
    
    /// <summary>
    /// Get mutable reference to chunk. Protected by current scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T Get<T>(int chunkId, bool dirty = false) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(chunkId, dirty));

    /// <summary>
    /// Get read-only reference. Same scope protection as Get.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref readonly T GetReadOnly<T>(int chunkId) where T : unmanaged => ref Get<T>(chunkId, dirty: false);

    /// <summary>
    /// Try to promote a chunk's page from Shared to Exclusive access.
    /// Must call DemoteChunk when done with exclusive access.
    /// </summary>
    /// <returns>True if promotion succeeded, false if page not loaded or promotion failed</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool TryPromoteChunk(int chunkId)
    {
        var (segmentIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(segmentIndex);
            var v0 = Vector256.Load(indices);
            var mask = Vector256.Equals(v0, target).ExtractMostSignificantBits();

            if (mask != 0)
            {
                return TryPromoteSlot(BitOperations.TrailingZeroCount(mask));
            }

            if (_capacity > 8)
            {
                var v1 = Vector256.Load(indices + 8);
                var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
                if (mask1 != 0)
                {
                    return TryPromoteSlot(8 + BitOperations.TrailingZeroCount(mask1));
                }
            }
        }

        return false; // Page not in cache
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryPromoteSlot(int slot)
    {
        if (_promoteCounters[slot] > 0)
        {
            _promoteCounters[slot]++;
            return true;
        }

        if (_pageAccessors[slot].TryPromoteToExclusive())
        {
            _promoteCounters[slot] = 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Demote a chunk's page from Exclusive back to Shared access.
    /// </summary>
    public void DemoteChunk(int chunkId)
    {
        var (segmentIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(segmentIndex);
            var v0 = Vector256.Load(indices);
            var mask = Vector256.Equals(v0, target).ExtractMostSignificantBits();

            if (mask != 0)
            {
                DemoteSlot(BitOperations.TrailingZeroCount(mask));
                return;
            }

            if (_capacity > 8)
            {
                var v1 = Vector256.Load(indices + 8);
                var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
                if (mask1 != 0)
                {
                    DemoteSlot(8 + BitOperations.TrailingZeroCount(mask1));
                }
            }
        }
    }
    
    internal ref T GetChunkBasedSegmentHeader<T>(int offset, bool dirtyPage) where T : unmanaged
    {
        var baseAddress = GetChunkAddress(0, dirtyPage) - (PagedMMF.PageHeaderSize + LogicalSegment.RootHeaderIndexSectionLength);
        return ref Unsafe.AsRef<T>(baseAddress + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void DemoteSlot(int slot)
    {
        if (_promoteCounters[slot] > 0 && --_promoteCounters[slot] == 0)
        {
            _pageAccessors[slot].DemoteExclusive();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private byte* GetFromSlot(int slot, int offset, bool dirty)
    {
        // Claim for current scope if not already owned by a lower (parent) scope
        // This ensures child never takes parent scope entries
        if (_scopeLevels[slot] == 255 || _scopeLevels[slot] > _currentScope)
        {
            _scopeLevels[slot] = _currentScope;
        }

        if (dirty)
        {
            _dirtyFlags[slot] = 1;
        }

        var baseAddr = (byte*)_baseAddresses[slot];
        var headerOffset = _pageIndices[slot] == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0;
        var chunkAddr = baseAddr + headerOffset + offset * _stride;
        return chunkAddr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private byte* GetChunkAddress(int chunkId, bool dirty)
    {
        var (segmentIndex, offset) = _segment.GetChunkLocation(chunkId);

        // SIMD search for existing entry
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(segmentIndex);
            var v0 = Vector256.Load(indices);
            var mask = Vector256.Equals(v0, target).ExtractMostSignificantBits();

            if (mask != 0)
            {
                var slot = BitOperations.TrailingZeroCount(mask);
                return GetFromSlot(slot, offset, dirty);
            }

            if (_capacity > 8)
            {
                var v1 = Vector256.Load(indices + 8);
                var mask1 = Vector256.Equals(v1, target).ExtractMostSignificantBits();
                if (mask1 != 0)
                {
                    var slot = 8 + BitOperations.TrailingZeroCount(mask1);
                    return GetFromSlot(slot, offset, dirty);
                }
            }
        }

        // Cache miss - load into new or evicted slot
        return LoadAndGet(segmentIndex, offset, dirty);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private byte* LoadAndGet(int segmentIndex, int offset, bool dirty)
    {
        var slot = FindEvictableSlot();

        if (slot == InvalidSlot)
        {
            ThrowHelper.ThrowInvalidOp(
                "No evictable slots available. Increase capacity or reduce scope nesting.");
        }

        // Evict if slot was in use
        EvictSlot(slot);

        // Load new page
        LoadIntoSlot(slot, segmentIndex);

        return GetFromSlot(slot, offset, dirty);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private byte FindEvictableSlot()
    {
        // First: use unused slots (fast path)
        if (_usedSlots < _capacity)
        {
            return _usedSlots++;
        }

        // Scan from clock hand position, wrap around
        // Skip slots that are scope-protected or promoted to exclusive
        for (int i = 0; i < _capacity; i++)
        {
            var slot = (_clockHand + i) % _capacity;
            if (_scopeLevels[slot] == 255 && _promoteCounters[slot] == 0)
            {
                _clockHand = (byte)((slot + 1) % _capacity);
                return (byte)slot;
            }
        }

        return InvalidSlot;
    }

    private void EvictSlot(int slot)
    {
        if (_pageIndices[slot] == InvalidPageIndex)
        {
            return;  // Slot was never used
        }

        // Guard: should never evict a promoted slot
        if (_promoteCounters[slot] > 0)
        {
            ThrowHelper.ThrowInvalidOp("Cannot evict promoted slot");
        }

        // Handle dirty page
        if (_dirtyFlags[slot] != 0 && _changeSet != null)
        {
            _changeSet.Add(_pageAccessors[slot]);
        }

        // Release page accessor
        _pageAccessors[slot].Dispose();
        _pageIndices[slot] = InvalidPageIndex;
        _dirtyFlags[slot] = 0;
    }

    private void LoadIntoSlot(int slot, int segmentIndex)
    {
        _segment.GetPageSharedAccessor(segmentIndex, out _pageAccessors[slot]);
        _pageIndices[slot] = segmentIndex;
        _baseAddresses[slot] = (long)_pageAccessors[slot].GetRawDataAddr();
        _scopeLevels[slot] = _currentScope;
    }

    /// <summary>
    /// Dispose accessor. Flushes dirty pages and releases all page locks.
    /// </summary>
    public void Dispose()
    {
        for (int i = 0; i < _usedSlots; i++)
        {
            if (_pageIndices[i] != InvalidPageIndex)
            {
                // Demote any promoted slots before disposing
                if (_promoteCounters[i] > 0)
                {
                    _pageAccessors[i].DemoteExclusive();
                    _promoteCounters[i] = 0;
                }

                if (_dirtyFlags[i] != 0 && _changeSet != null)
                {
                    _changeSet.Add(_pageAccessors[i]);
                }
                _pageAccessors[i].Dispose();
            }
        }

        // Clear state to catch use-after-dispose
        _usedSlots = 0;
        _segment = null!;
    }
}
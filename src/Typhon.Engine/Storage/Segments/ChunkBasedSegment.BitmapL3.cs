using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

public partial class ChunkBasedSegment
{
    internal class BitmapL3
    {
        private readonly ChunkBasedSegment _segment;
        internal readonly int _rootChunkCount;
        internal readonly int _otherChunkCount;

        // Magic multiplier for fast division: quotient = (n * _divMagic) >> 32
        private readonly ulong _divMagic;

        private readonly long[] _l1All;
        private readonly long[] _l2All;
        private readonly long[] _l1Any;

        public BitmapL3(ChunkBasedSegment segment, bool isLoading)
        {
            _segment = segment;
            _rootChunkCount = segment.ChunkCountRootPage;
            _otherChunkCount = segment.ChunkCountPerPage;

            // Precompute magic multiplier for fast division by _otherChunkCount
            _divMagic = (0x1_0000_0000UL + (uint)_otherChunkCount - 1) / (uint)_otherChunkCount;

            var pageCount = segment.Length;
            Capacity = GetChunkCount(pageCount);
            _allocated = 0;

            var length = Math.Max(1, (Capacity + 4095) / 4096);
            _l1All = new long[length];
            _l1Any = new long[length];

            length = Math.Max(1, (length + 63) / 64);
            _l2All = new long[length];

            if (isLoading)
            {
                InitFromLoad();
            }
        }

        /// <summary>
        /// Creates a new BitmapL3 by extending an existing bitmap with newly allocated pages.
        /// This avoids re-scanning existing pages (which may be held by the caller), only scanning new pages.
        /// </summary>
        /// <param name="segment">The segment (with updated Length after growth)</param>
        /// <param name="oldBitmap">The previous bitmap to copy state from</param>
        /// <param name="oldPageCount">The page count before growth (to know where new pages start)</param>
        public BitmapL3(ChunkBasedSegment segment, BitmapL3 oldBitmap, int oldPageCount)
        {
            _segment = segment;
            _rootChunkCount = segment.ChunkCountRootPage;
            _otherChunkCount = segment.ChunkCountPerPage;

            // Precompute magic multiplier for fast division by _otherChunkCount
            _divMagic = (0x1_0000_0000UL + (uint)_otherChunkCount - 1) / (uint)_otherChunkCount;

            var newPageCount = segment.Length;
            Capacity = GetChunkCount(newPageCount);
            var oldCapacity = oldBitmap.Capacity;
            
            // Copy allocated count from old bitmap - new pages are empty (cleared during Grow)
            _allocated = oldBitmap._allocated;

            // Allocate new L1/L2 arrays with expanded capacity
            var newL1Length = Math.Max(1, (Capacity + 4095) / 4096);
            var oldL1Length = oldBitmap._l1All.Length;
            
            _l1All = new long[newL1Length];
            _l1Any = new long[newL1Length];
            
            // Copy existing L1 state
            Array.Copy(oldBitmap._l1All, _l1All, Math.Min(oldL1Length, newL1Length));
            Array.Copy(oldBitmap._l1Any, _l1Any, Math.Min(oldL1Length, newL1Length));

            var newL2Length = Math.Max(1, (newL1Length + 63) / 64);
            var oldL2Length = oldBitmap._l2All.Length;
            
            _l2All = new long[newL2Length];
            
            // Copy existing L2 state
            Array.Copy(oldBitmap._l2All, _l2All, Math.Min(oldL2Length, newL2Length));
            
            // New pages are already cleared (metadata zeroed in Grow()), so their L0 bits are all 0.
            // L1All/L1Any/L2All for new pages are already 0 from array initialization.
            // No need to scan new pages - they're guaranteed empty!
        }

        private void InitFromLoad()
        {
            var epoch = _segment.Manager.EpochManager.GlobalEpoch;
            var curSegPageIndex = -1;
            ReadOnlySpan<long> metadataSpan = default;
            var allocated = 0;
            var segmentLength = _segment.Length;

            for (int i = 0; i < Capacity; i += 64)
            {
                var l0Offset = i >> 6;
                var prevIndex = curSegPageIndex;
                int pageOffset = 0;

                (curSegPageIndex, pageOffset) = GetBitmapMaskLocation(i >> 6);

                // Bounds check: ensure pageIndex is within segment
                if (curSegPageIndex >= segmentLength)
                {
                    throw new InvalidOperationException(
                        $"BitmapL3.InitFromLoad: pageIndex {curSegPageIndex} >= segmentLength {segmentLength}. " +
                        $"Capacity={Capacity}, i={i}, l0Offset={l0Offset}");
                }

                if (curSegPageIndex != prevIndex)
                {
                    var page = _segment.GetPage(curSegPageIndex, epoch, out _);
                    metadataSpan = page.MetadataReadOnly<long>();
                }

                // Bounds check: ensure pageOffset is within PageMetadata (16 longs max)
                if (pageOffset >= 16)
                {
                    throw new InvalidOperationException(
                        $"BitmapL3.InitFromLoad: pageOffset {pageOffset} >= 16. " +
                        $"pageIndex={curSegPageIndex}, i={i}, l0Offset={l0Offset}");
                }

                var mask = metadataSpan[pageOffset];
                allocated += BitOperations.PopCount((ulong)mask);

                if (mask == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All[l1Offset];
                    _l1All[l1Offset] |= l1Mask;

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All[l2Offset] |= l2Mask;
                    }
                }

                if (mask != 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any[l1Offset] |= l1Mask;
                }
            }
            _allocated = allocated;
        }

        private int GetChunkCount(int pageCount) => (pageCount-1) * _otherChunkCount + _rootChunkCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private (int page, int offset) GetChunkLocation(int index)
        {
            // Fast path: chunk is on root page
            if (index < _rootChunkCount)
            {
                return (0, index);
            }

            // Adjust index relative to non-root pages
            var adjusted = (uint)(index - _rootChunkCount);

            // Fast division using magic multiplier: quotient = (n * magic) >> 32
            var pageIndex = (int)((adjusted * _divMagic) >> 32);

            // Remainder: offset = adjusted - pageIndex * divisor
            var offset = (int)(adjusted - (uint)(pageIndex * _otherChunkCount));

            return (pageIndex + 1, offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private (int page, int offset) GetBitmapMaskLocation(int index)
        {
            var (pi, o) = GetChunkLocation(index << 6);
            return (pi, o >> 6);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool SetL0(int bitIndex)
        {
            var l0Offset = bitIndex >> 6;
            var l0Mask = 1L << (bitIndex & 0x3F);

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
            var epoch = _segment.Manager.EpochManager.GlobalEpoch;
            var page = _segment.GetPage(pageIndex, epoch, out _);
            var data = page.Metadata<long>();
            {
                var prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
                if ((prevL0 & l0Mask) != 0)
                {
                    // The bit was concurrently set by someone else
                    return false;
                }

                if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = Interlocked.Or(ref _l1All[l1Offset], l1Mask);

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        Interlocked.Or(ref _l2All[l2Offset], l2Mask);
                    }
                }

                if (prevL0 == 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    Interlocked.Or(ref _l1Any[l1Offset], l1Mask);
                }

                Interlocked.Increment(ref _allocated);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool SetL1(int index)
        {
            var l0Offset = index;
            var l0Mask = -1L;

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
            var epoch = _segment.Manager.EpochManager.GlobalEpoch;
            var page = _segment.GetPage(pageIndex, epoch, out _);
            var data = page.Metadata<long>();
            {
                var prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
                if (prevL0 != 0)
                {
                    // Can't allocate the whole L1, some bits are set at L0
                    return false;
                }

                // prevL0 was 0, now it's -1, so L0 group is full -> set L1All
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = Interlocked.Or(ref _l1All[l1Offset], l1Mask);

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        Interlocked.Or(ref _l2All[l2Offset], l2Mask);
                    }
                }

                // prevL0 was 0, now it has bits -> set L1Any
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    Interlocked.Or(ref _l1Any[l1Offset], l1Mask);
                }

                Interlocked.Add(ref _allocated, 64);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void ClearL0(int index)
        {
            var l0Offset = index >> 6;
            var bitMask = 1L << (index & 0x3F);
            var l0Mask = ~bitMask;

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
            var epoch = _segment.Manager.EpochManager.GlobalEpoch;
            var page = _segment.GetPage(pageIndex, epoch, out _);
            var data = page.Metadata<long>();
            {
                var prevL0 = Interlocked.And(ref data[pageOffset], l0Mask);

                // Guard against double-free - only proceed if the bit was actually set
                if ((prevL0 & bitMask) == 0)
                {
                    // Bit wasn't set - this is a double-free or invalid free, ignore
                    return;
                }

                // Update L1All: clear bit when L0 group transitions from full to not-full
                if (prevL0 == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = Interlocked.And(ref _l1All[l1Offset], ~l1Mask);

                    if (prevL1 == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        Interlocked.And(ref _l2All[l2Offset], ~l2Mask);
                    }
                }

                // Update L1Any: clear bit when L0 group transitions from has-some to empty
                if ((prevL0 & l0Mask) == 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    Interlocked.And(ref _l1Any[l1Offset], ~l1Mask);
                }

                Interlocked.Decrement(ref _allocated);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool IsSet(int index)
        {
            var offset = index >> 6;
            var mask = 1L << (index & 0x3F);

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(offset);
            var epoch = _segment.Manager.EpochManager.GlobalEpoch;
            var page = _segment.GetPage(pageIndex, epoch, out _);
            var data = page.MetadataReadOnly<long>();
            return (data[pageOffset] & mask) != 0L;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool FindNextUnsetL0(ref int index, ref long mask)
        {
            var capacity = Capacity;

            var c0 = ++index;
            long v0 = mask;

            var ll0 = (capacity + 63) / 64;
            var ll1 = _l1All.Length;
            var ll2 = _l2All.Length;

            var epoch = _segment.Manager.EpochManager.GlobalEpoch;
            var curPageId = -1;
            Span<long> metadataSpan = default;

            while (c0 < capacity)
            {
                // Do we have to fetch a new L0?
                if (((c0 & 0x3F) == 0) || (v0 == -1))
                {
                    // Check if we can skip the rest of the level 0
                    int i0;
                    for (i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                    {
                        var (pageId, offset) = GetBitmapMaskLocation(i0);
                        if (pageId != curPageId)
                        {
                            var page = _segment.GetPage(pageId, epoch, out _);
                            metadataSpan = page.Metadata<long>();
                            curPageId = pageId;
                        }
                        var data = metadataSpan;
                        long t0 = 1L << (c0 & 0x3F);
                        v0 = data[offset] | (t0 - 1);

                        if (v0 != -1)
                        {
                            break;
                        }
                        c0 = ++i0 << 6;

                        // Bounds check after skip
                        if (c0 >= capacity)
                        {
                            return false;
                        }

                        // Check if we can skip the rest of the level 1
                        for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                        {
                            var v1 = _l1All[i1] >> (i0 & 0x3F);
                            if (v1 != -1)
                            {
                                break;
                            }

                            i0 = 0;
                            c0 = ++i1 << 12;

                            // Bounds check after skip
                            if (c0 >= capacity)
                            {
                                return false;
                            }

                            // Check if we can skip the rest of the level 2
                            for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                            {
                                var v2 = _l2All[i2] >> (i1 & 0x3F);
                                if (v2 != -1)
                                {
                                    break;
                                }
                                i1 = 0;
                                c0 = ++i2 << 18;

                                // Bounds check after skip
                                if (c0 >= capacity)
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                // Recheck bounds after potential skip operations before computing candidate
                if (c0 >= capacity)
                {
                    return false;
                }

                var bitPos = BitOperations.TrailingZeroCount(~v0);
                var candidateIndex = (c0 & ~0x3F) + bitPos;

                // Critical: verify the candidate index is within capacity bounds
                // The last L0 group may have bits beyond capacity that appear "free"
                if (candidateIndex >= capacity)
                {
                    return false;
                }

                v0 |= (1L << bitPos);
                index = candidateIndex;
                mask = v0;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool FindNextUnsetL1(ref int index, ref long mask)
        {
            var capacity = Capacity;
            
            // Calculate the maximum valid L1 index (each L1 represents 64 chunks)
            // We need at least 64 chunks available from the L1 start position
            var maxL1Index = (capacity - 64) >> 6;  // Highest L1 that has a full 64 chunks
            if (maxL1Index < 0)
            {
                return false;  // Not enough capacity for even one L1 allocation
            }
            
            var c1 = ++index;
            long v1 = mask;
            var ll1 = _l1All.Length;
            var ll2 = _l2All.Length;

            while (c1 <= maxL1Index)
            {
                if (((c1 & 0x3F) == 0) || (v1 == -1))
                {
                    // Check if we can skip the rest of the level 1
                    int i1;
                    for (i1 = c1 >> 6; i1 < ll1; i1 = c1 >> 6)
                    {
                        var t1 = 1L << (c1 & 0x3F);
                        v1 = _l1All[i1] | (t1 - 1);
                        if (v1 != -1)
                        {
                            break;
                        }

                        c1 = ++i1 << 6;
                        
                        // Early exit if we've exceeded the valid range
                        if (c1 > maxL1Index)
                        {
                            return false;
                        }

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c1 >> 12; i2 < ll2; i2 = c1 >> 12)
                        {
                            var v2 = _l2All[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }

                            i1 = 0;
                            c1 = ++i2 << 12;
                            
                            // Early exit if we've exceeded the valid range
                            if (c1 > maxL1Index)
                            {
                                return false;
                            }
                        }
                    }
                }
                
                // Recheck bounds after potential skip operations
                if (c1 > maxL1Index)
                {
                    return false;
                }

                var t = 1L << (c1 & 0x3F);
                v1 = _l1Any[c1 >> 6] | (t - 1);
                var bitPos = BitOperations.TrailingZeroCount(~v1);
                var candidateIndex = (c1 & ~0x3F) + bitPos;
                
                // Verify the candidate L1 index is within bounds
                if (candidateIndex > maxL1Index)
                {
                    return false;
                }
                
                v1 |= (1L << bitPos);
                index = candidateIndex;
                mask = v1;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Allocate(Memory<int> result, bool clearContent)
        {
            var length = result.Length;
            var hasL1 = true;
            var destI = 0;

            var span = result.Span;

            using EpochChunkAccessor epochAccessor = clearContent ? _segment.CreateEpochChunkAccessor() : default;

            // Allocate per bulk of 64 pages as long as we can
            while (hasL1 && (length >= 64))
            {
                int i = -1;
                long mask = 0;
                while (FindNextUnsetL1(ref i, ref mask) && (length >= 64))
                {
                    if (SetL1(i))
                    {
                        for (int j = 0; j < 64; j++)
                        {
                            var chunkIndex = (i << 6) + j;
                            if (clearContent)
                            {
                                epochAccessor.ClearChunk(chunkIndex);
                            }
                            span[destI++] = chunkIndex;
                        }
                        length -= 64;
                    }
                }

                hasL1 = length < 64;
            }

            // Allocate page by page
            {
                int i = -1;
                long mask = 0;
                while (FindNextUnsetL0(ref i, ref mask) && (length > 0))
                {
                    if (SetL0(i))
                    {
                        if (clearContent)
                        {
                            epochAccessor.ClearChunk(i);
                        }
                        span[destI++] = i;
                        --length;
                    }
                }
            }

            // Couldn't satisfy the call, rollback
            if (length > 0)
            {
                for (int i = 0; i < destI; i++)
                {
                    ClearL0(span[i]);
                }
                span.Clear();
                return false;
            }

            return true;
        }

        public void Free(ReadOnlySpan<uint> pages)
        {
            var length = pages.Length;
            for (int i = 0; i < length; i++)
            {
                ClearL0((int)pages[i]);
            }
        }

        public int Capacity { get; }
        
        private int _allocated;
        public int Allocated => _allocated;
        public int FreeChunkCount => Capacity - _allocated;
    }
    
}
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

        private readonly Memory<long> _l1All;
        private readonly Memory<long> _l2All;
        private readonly Memory<long> _l1Any;

        public BitmapL3(ChunkBasedSegment segment, bool isLoading)
        {
            _segment = segment;
            _rootChunkCount = segment.ChunkCountRootPage;
            _otherChunkCount = segment.ChunkCountPerPage;

            var pageCount = segment.Length;
            Capacity = GetChunkCount(pageCount);
            Allocated = 0;

            var length = Math.Max(1, (Capacity + 4095) / 4096);
            _l1All = new long[length];
            _l1Any = new long[length];

            length = Math.Max(1, (length + 63) / 64);
            _l2All = new long[length];

            if (isLoading)
            {
                
            }
        }

        private int GetChunkCount(int pageCount) => (pageCount-1) * _otherChunkCount + _rootChunkCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private (int page, int offset) GetChunkLocation(int index)
        {
            var fs = _rootChunkCount;
            var ss = _otherChunkCount;

            if (index < fs)
            {
                return (0, index);
            }

            var pi = Math.DivRem(index - fs, ss, out var off);
            return (pi + 1, off);
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
            _segment.GetPageSharedAccessor(pageIndex, out var page);
            using (page)
            {
                var data = page.PageMetadata.Cast<byte, long>();
                var prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
                page.SetPageDirty();                                                            // TODO only if changed
                if ((prevL0 & l0Mask) != 0)
                {
                    // The bit was concurrently set by someone else
                    return false;
                }

                if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All.Span[l1Offset];
                    _l1All.Span[l1Offset] |= l1Mask;

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All.Span[l2Offset] |= l2Mask;
                    }
                }

                if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any.Span[l1Offset] |= l1Mask;
                }

                ++Allocated;
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool SetL1(int index)
        {
            var l0Offset = index;
            var l0Mask = -1L;

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
            _segment.GetPageSharedAccessor(pageIndex, out var page);
            using (page)
            {
                var data = page.PageMetadata.Cast<byte, long>();
                var prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
                page.SetPageDirty();                                                            // TODO Only if changed
                if (prevL0 != 0)
                {
                    // Can't allocate the whole L1, some bits are set at L0
                    return false;
                }

                if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All.Span[l1Offset];
                    _l1All.Span[l1Offset] |= l1Mask;

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All.Span[l2Offset] |= l2Mask;
                    }
                }

                if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any.Span[l1Offset] |= l1Mask;
                }

                Allocated += 64;
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void ClearL0(int index)
        {
            var l0Offset = index >> 6;
            var l0Mask = ~(1L << (index & 0x3F));

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
            _segment.GetPageSharedAccessor(pageIndex, out var page);
            using (page)
            {
                var data = page.PageMetadata.Cast<byte, long>();
                var prevL0 = Interlocked.And(ref data[pageOffset], l0Mask);
                page.SetPageDirty();                                                                // TODO dirty only if changed
                if ((prevL0 == -1) && ((prevL0 & l0Mask) != -1))
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All.Span[l1Offset];
                    _l1All.Span[l1Offset] &= l1Mask;

                    if (prevL1 == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All.Span[l2Offset] &= l2Mask;
                    }
                }

                if ((prevL0 != 0) && ((prevL0 & l0Mask) == 0))
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any.Span[l1Offset] &= l1Mask;
                }

                --Allocated;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool IsSet(int index)
        {
            var offset = index >> 6;
            var mask = 1L << (index & 0x3F);

            var (pageIndex, pageOffset) = GetBitmapMaskLocation(offset);
            _segment.GetPageSharedAccessor(pageIndex, out var page);
            using (page)
            {
                var data = page.PageMetadata.Cast<byte, long>();
                return (data[pageOffset] & mask) != 0L;
            }
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

            var curPageId = -1;
            PageAccessor curPage = default;

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
                            curPage.Dispose();
                            _segment.GetPageSharedAccessor(pageId, out curPage);
                            curPageId = pageId;
                        }
                        var data = curPage.PageMetadata.Cast<byte, long>();
                        long t0 = 1L << (c0 & 0x3F);
                        v0 = data[offset] | (t0 - 1);

                        if (v0 != -1)
                        {
                            break;
                        }
                        c0 = ++i0 << 6;

                        // Check if we can skip the rest of the level 1
                        for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                        {
                            var v1 = _l1All.Span[i1] >> (i0 & 0x3F);
                            if (v1 != -1)
                            {
                                break;
                            }

                            i0 = 0;
                            c0 = ++i1 << 12;

                            // Check if we can skip the rest of the level 2
                            for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                            {
                                var v2 = _l2All.Span[i2] >> (i1 & 0x3F);
                                if (v2 != -1)
                                {
                                    break;
                                }
                                i1 = 0;
                                c0 = ++i2 << 18;
                            }
                        }
                    }
                }

                var bitPos = BitOperations.TrailingZeroCount(~v0);
                v0 |= (1L << bitPos);
                index = (c0 & ~0x3F) + bitPos;
                mask = v0;
                curPage.Dispose();
                return true;
            }

            curPage.Dispose();
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool FindNextUnsetL1(ref int index, ref long mask)
        {
            var c1 = ++index;
            long v1 = mask;
            var ll1 = _l1All.Length;
            var ll2 = _l2All.Length;

            while (c1 < (ll1 << 6))
            {
                if (((c1 & 0x3F) == 0) || (v1 == -1))
                {
                    // Check if we can skip the rest of the level 1
                    int i1;
                    for (i1 = c1 >> 6; i1 < ll1; i1 = c1 >> 6)
                    {
                        var t1 = 1L << (c1 & 0x3F);
                        v1 = _l1All.Span[i1] | (t1 - 1);
                        if (v1 != -1)
                        {
                            break;
                        }

                        c1 = ++i1 << 6;

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c1 >> 12; i2 < ll2; i2 = c1 >> 12)
                        {
                            var v2 = _l2All.Span[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }

                            i1 = 0;
                            c1 = ++i2 << 12;
                        }
                    }
                }

                var t = 1L << (c1 & 0x3F);
                v1 = _l1Any.Span[c1 >> 6] | (t - 1);
                var bitPos = BitOperations.TrailingZeroCount(~v1);
                v1 |= (1L << bitPos);
                index = (c1 & ~0x3F) + bitPos;
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

            ChunkRandomAccessor chunkAccessor = null;
            if (clearContent)
            {
                chunkAccessor = _segment.CreateChunkRandomAccessor(8);
            }

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
                                chunkAccessor.ClearChunk(chunkIndex);
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
                            chunkAccessor.ClearChunk(i);
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
        public int Allocated { get; private set; }
    }
    
}
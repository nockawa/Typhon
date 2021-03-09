// unset

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    public readonly struct ChunkReadOnlyRandomAccessor : IDisposable
    {
        private readonly ChunkBasedSegment _owner;
        private readonly int _cachedPagesCount;
        private readonly IMemoryOwner<CachedPages> _cacheMemory;
        private readonly Memory<CachedPages> _cache;
        private readonly int _stride;

        unsafe private struct CachedPages
        {
            public int HitCount;
            public int SegmentIndex;
            public int PinCounter;
            public int PromoteCounter;
            public ReadOnlyPageAccessor PageAccessor;
            public ReadWritePageAccessor PromotedPageAccessor;
            public byte* BaseAddress;
        }

        unsafe public ref readonly T GetChunk<T>(int index) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal void UnpinChunk(int index)
        {
            var (si, _) = _owner.GetChunkLocation(index);
            var caches = _cache.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    --caches[i].PinCounter;
                    return;
                }
            }
        }

        internal bool TryPromoteChunk(int index)
        {
            var (si, _) = _owner.GetChunkLocation(index);

            var caches = _cache.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    ref var page = ref caches[i];
                    if (page.PromoteCounter > 0)
                    {
                        ++page.PromoteCounter;
                        return true;
                    }

                    page.PromotedPageAccessor = page.PageAccessor.TryPromoteToExclusiveReadWrite();
                    if (page.PromotedPageAccessor.IsValid)
                    {
                        page.PromoteCounter = 1;
                        return true;
                    }
                    return false;
                }
            }
            return false;
        }

        internal void DemoteChunk(int index)
        {
            var (si, _) = _owner.GetChunkLocation(index);

            var caches = _cache.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    ref var page = ref caches[i];
                    if (--page.PromoteCounter == 0)
                    {
                        page.PromotedPageAccessor.Dispose();
                        page.PromotedPageAccessor = default;
                    }
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        unsafe internal byte* GetChunkAddress(int index, bool pin = false)
        {
            var (si, off) = _owner.GetChunkLocation(index);

            var lowHit = int.MaxValue;
            var pageI = -1;

            var caches = _cache.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    if (pin)
                    {
                        ++caches[i].PinCounter;
                    }
                    ++caches[i].HitCount;
                    return caches[i].BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
                }

                if ((caches[i].PinCounter == 0) && (caches[i].PromoteCounter == 0) && (caches[i].HitCount < lowHit))
                {
                    lowHit = caches[i].HitCount;
                    pageI = i;
                }
            }

            // Everything is pinned, that's bad...
            if (pageI == -1)
            {
                throw new NotImplementedException("No more available pages slot, all are occupied by pinned pages");
            }

            ref var cache = ref caches[pageI];
            cache.PageAccessor.Dispose();
            cache.HitCount = 1;
            cache.SegmentIndex = si;
            cache.PinCounter = pin ? 1 : 0;
            cache.PromoteCounter = 0;
            cache.PageAccessor = _owner.GetPageReadOnly(si);
            cache.BaseAddress = cache.PageAccessor.PageAddress + VirtualDiskManager.PageHeaderSize;
            return cache.BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
        }

        public ChunkReadOnlyRandomAccessor(ChunkBasedSegment owner, int cachedPagesCount)
        {
            _owner = owner;
            _cachedPagesCount = cachedPagesCount;
            _cacheMemory = MemoryPool<CachedPages>.Shared.Rent(cachedPagesCount);
            _cache = _cacheMemory.Memory;
            _stride = _owner.Stride;

            for (int i = 0; i < _cachedPagesCount; i++)
            {
                _cacheMemory.Memory.Span[i].SegmentIndex = -1;
            }
        }

        public void DisposePageAccessors()
        {
            var span = _cacheMemory.Memory.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                ref var cachedPage = ref span[i];
                cachedPage.PageAccessor.Dispose();
                cachedPage.SegmentIndex = -1;
                cachedPage.HitCount = 0;
            }
        }

        public void Dispose()
        {
            DisposePageAccessors();
            _cacheMemory.Dispose();
        }
    }

    public readonly struct ChunkReadWriteRandomAccessor : IDisposable
    {
        private readonly ChunkBasedSegment _owner;
        private readonly int _cachedPagesCount;
        private readonly IMemoryOwner<CachedPages> _cacheMemory;
        private readonly Memory<CachedPages> _cache;
        private readonly int _stride;

        unsafe private struct CachedPages
        {
            public int HitCount;
            public int SegmentIndex;
            public int PinCounter;
            public ReadWritePageAccessor PageAccessor;
            public byte* BaseAddress;
        }

        /// <summary>
        /// Access a Chunk from the segment. BEWARE: SEE REMARKS !
        /// </summary>
        /// <param name="index">Index of the chunk to get</param>
        /// <returns>The chunk object</returns>
        /// <remarks>
        /// BEWARE: This API is supposed to be as fast as possible, so there are things TO KNOW
        ///  - We assume that <param name="index"></param> is pointing to an allocated chunk and is totally valid (not out of bound)
        ///  - The returned object as to be used as a <c>ref var</c> otherwise you will work on a copy and any data changes won't be made on the actual chunk data, stored in the database
        ///  - IMPORTANT: The chunk is stored on a page, which is loaded and cached by this class, you must NOT make another call to this method before you're done with accessing the returned object.
        ///    The reason is simple, another call to <see cref="GetChunk"/> could Dispose the page where this chunk is and you would probably end up screwing with the database pages cache!!!
        ///  AND DON'T FORGET TO CALL <see cref="Dispose"/> when you're done with this accessor, otherwise the underlying pages won't be disposed!
        /// </remarks>
        unsafe public ref T GetChunk<T>(int index) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal void UnpinChunk(int index)
        {
            var (si, _) = _owner.GetChunkLocation(index);
            var caches = _cache.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    --caches[i].PinCounter;
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        unsafe internal byte* GetChunkAddress(int index, bool pin = false)
        {
            var (si, off) = _owner.GetChunkLocation(index);

            var lowHit = int.MaxValue;
            var pageI = -1;

            var caches = _cache.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    if (pin)
                    {
                        ++caches[i].PinCounter;
                    }
                    ++caches[i].HitCount;
                    return caches[i].BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
                }

                if ((caches[i].PinCounter == 0) && (caches[i].HitCount < lowHit))
                {
                    lowHit = caches[i].HitCount;
                    pageI = i;
                }
            }

            // Everything is pinned, that's bad...
            if (pageI == -1)
            {
                throw new NotImplementedException("No more available pages slot, all are occupied by pinned pages");
            }

            ref var cache = ref caches[pageI];
            cache.PageAccessor.Dispose();
            cache.HitCount = 1;
            cache.SegmentIndex = si;
            cache.PinCounter = pin ? 1 : 0;
            cache.PageAccessor = _owner.GetPageReadWrite(si);
            cache.BaseAddress = cache.PageAccessor.PageAddress + VirtualDiskManager.PageHeaderSize;
            return cache.BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
        }

        unsafe internal void ClearChunk(int index)
        {
            var addr = GetChunkAddress(index);
            new Span<long>(addr, _stride / 8).Clear();
        }

        public ChunkReadWriteRandomAccessor(ChunkBasedSegment owner, int cachedPagesCount)
        {
            _owner = owner;
            _cachedPagesCount = cachedPagesCount;
            _cacheMemory = MemoryPool<CachedPages>.Shared.Rent(cachedPagesCount);
            _cache = _cacheMemory.Memory;
            _stride = _owner.Stride;

            for (int i = 0; i < _cachedPagesCount; i++)
            {
                _cacheMemory.Memory.Span[i].SegmentIndex = -1;
            }
        }

        public void DisposePageAccessors()
        {
            var span = _cacheMemory.Memory.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                ref var cachedPage = ref span[i];
                cachedPage.PageAccessor.Dispose();
                cachedPage.SegmentIndex = -1;
                cachedPage.HitCount = 0;
            }
        }

        public void Dispose()
        {
            DisposePageAccessors();
            _cacheMemory.Dispose();
        }
    }

    public class ChunkBasedSegmentAccessorPool
    {
        public ChunkBasedSegment Segment { get; }
        public ChunkReadOnlyRandomAccessor RO { get; }
        public ChunkReadWriteRandomAccessor RW { get; }

        public ChunkBasedSegmentAccessorPool(ChunkBasedSegment segment, int roCachedCount, int rwCachedCount)
        {
            Segment = segment;
            RO = Segment.GetChunkReadOnlyRandomAccessor(roCachedCount);
            RW = Segment.GetChunkReadWriteRandomAccessor(rwCachedCount);
        }
    }

    public class ChunkBasedSegment : LogicalSegment
    {
        private BitmapL3 _map;

        internal ChunkBasedSegment(LogicalSegmentManager manager, int stride) : base(manager)
        {
            if (stride < sizeof(long))
            {
                throw new Exception($"Invalid stride size, given {stride}, but must be at least 8 bytes");
            }

            Stride = stride;
            ChunkCountRootPage = (VirtualDiskManager.PageRawDataSize - RootHeaderIndexSectionLength) / stride;
            ChunkCountPerPage = VirtualDiskManager.PageRawDataSize / stride;
        }

        internal override bool Create(PageBlockType type, IMemoryOwner<uint> pagesMemOwner, int length)
        {
            base.Create(type, pagesMemOwner, length);

            // Clear the metadata sections that store the chunk's occupancy bitmap
            for (int i = 0; i < length; i++)
            {
                using var page = GetPageReadWrite(i);
                int longSize = (i==0 ? (ChunkCountRootPage+63) : (ChunkCountPerPage+63)) >> 6;
                page.PageMetadata.Cast<byte, long>().Slice(0, longSize).Clear();
            }

            _map = new BitmapL3(length, this);
            ReserveChunk(0);                    // It's always handy to consider ChunkId:0 as "null", so we reserve the chunk to prevent it is a valid id.
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

        public ChunkReadOnlyRandomAccessor GetChunkReadOnlyRandomAccessor(int cachedPagesCount=1) => new(this, cachedPagesCount);
        public ChunkReadWriteRandomAccessor GetChunkReadWriteRandomAccessor(int cachedPagesCount=1) => new(this, cachedPagesCount);


        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public (int segmentIndex, int offset) GetChunkLocation(int index)
        {
            var fs = _map._rootChunkCount;
            var ss = _map._otherChunkCount;

            if (index < fs)
            {
                return (0, index);
            }

            var pi = Math.DivRem(index - fs, ss, out var off);
            return (pi + 1, off);
        }

        public int Stride { get; }
        public int ChunkCountRootPage { get; }
        public int ChunkCountPerPage { get; }

        public int ChunkCapacity => _map.Capacity;
        public int AllocatedChunkCount => _map.Allocated;
        public int FreeChunkCount => ChunkCapacity - AllocatedChunkCount;

        public class BitmapL3
        {
            private readonly ChunkBasedSegment _segment;
            private readonly int _stride;
            internal readonly int _rootChunkCount;
            internal readonly int _otherChunkCount;

            private readonly Memory<long> _l1All;
            private readonly Memory<long> _l2All;
            private readonly Memory<long> _l1Any;

            public BitmapL3(int pageCount, ChunkBasedSegment segment)
            {
                _segment = segment;
                _stride = segment.Stride;
                _rootChunkCount = segment.ChunkCountRootPage;
                _otherChunkCount = segment.ChunkCountPerPage;

                Capacity = GetChunkCount(pageCount);
                Allocated = 0;

                var length = Math.Max(1, (Capacity + 4095) / 4096);
                _l1All = new long[length];
                _l1Any = new long[length];

                length = Math.Max(1, (length + 63) / 64);
                _l2All = new long[length];
            }

            public int GetChunkCount(int pageCount) => (pageCount-1) * _otherChunkCount + _rootChunkCount;

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public (int page, int offset) GetChunkLocation(int index)
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
            public (int page, int offset) GetBitmapMaskLocation(int index)
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
                using var page = _segment.GetPageReadWrite(pageIndex);

                var data = page.PageMetadata.Cast<byte, long>();
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

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public bool SetL1(int index)
            {
                var l0Offset = index;
                var l0Mask = -1L;

                var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
                using var page = _segment.GetPageReadWrite(pageIndex);

                var data = page.PageMetadata.Cast<byte, long>();
                var prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
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

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public void ClearL0(int index)
            {
                var l0Offset = index >> 6;
                var l0Mask = ~(1L << (index & 0x3F));

                var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
                using var page = _segment.GetPageReadWrite(pageIndex);

                var data = page.PageMetadata.Cast<byte, long>();
                var prevL0 = Interlocked.And(ref data[pageOffset], l0Mask);
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

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public bool IsSet(int index)
            {
                var offset = index >> 6;
                var mask = 1L << (index & 0x3F);

                var (pageIndex, pageOffset) = GetBitmapMaskLocation(offset);
                using var page = _segment.GetPageReadWrite(pageIndex);

                var data = page.PageMetadata.Cast<byte, long>();
                return (data[pageOffset] & mask) != 0L;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public bool FindNextUnsetL0(ref int index, ref long mask)
            {
                var capacity = Capacity;

                var c0 = ++index;
                long v0 = mask;
                long t0;

                var ll0 = (capacity + 63) / 64;
                var ll1 = _l1All.Length;
                var ll2 = _l2All.Length;

                var curPageId = -1;
                var i0 = 0;
                ReadOnlyPageAccessor curPage = default;

                while (c0 < capacity)
                {
                    // Do we have to fetch a new L0?
                    if (((c0 & 0x3F) == 0) || (v0 == -1))
                    {
                        // Check if we can skip the rest of the level 0
                        for (i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                        {
                            var (pageId, offset) = GetBitmapMaskLocation(i0);
                            if (pageId != curPageId)
                            {
                                curPage.Dispose();
                                curPage = _segment.GetPageReadOnly(pageId);
                                curPageId = pageId;
                            }
                            var data = curPage.PageMetadata.Cast<byte, long>();
                            t0 = 1L << (c0 & 0x3F);
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
            public bool FindNextUnsetL1(ref int index, ref long mask)
            {
                var c1 = ++index;
                long v1 = mask;
                int i1 = 0;
                var ll1 = _l1All.Length;
                var ll2 = _l2All.Length;

                while (c1 < (ll1 << 6))
                {
                    if (((c1 & 0x3F) == 0) || (v1 == -1))
                    {
                        // Check if we can skip the rest of the level 1
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

                ChunkReadWriteRandomAccessor chunkAccessor = default;
                if (clearContent)
                {
                    chunkAccessor = _segment.GetChunkReadWriteRandomAccessor(8);
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
                        ClearL0((int)span[i]);
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
}
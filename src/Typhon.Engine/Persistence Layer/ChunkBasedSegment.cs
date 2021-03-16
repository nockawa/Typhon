// unset

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    /// <summary>
    /// Allow access to chunks in a Chunk based segment
    /// </summary>
    /// <remarks>
    /// This class is not thread-safe
    /// </remarks>
    public class ChunkRandomAccessor : IDisposable
    {
        private readonly ChunkBasedSegment _owner;
        private readonly int _cachedPagesCount;
        private readonly Memory<PageAccessor> _cachedPages;
        private readonly Memory<CachedEntry> _cachedEntries;    // We will hit this one very often, so we favor cache locality by putting the PageAccessor in another array
        private readonly int _stride;

        [StructLayout(LayoutKind.Sequential)]
        unsafe private struct CachedEntry
        {
            public int SegmentIndex;
            public int HitCount;
            public int PinCounter;
            public short IsDirty;
            public short PromoteCounter;
            public byte* BaseAddress;
        }

        unsafe public ref readonly T GetChunkReadOnly<T>(int index) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index));
        unsafe public ref T GetChunk<T>(int index, bool dirtyPage = false) where T : unmanaged => ref Unsafe.AsRef<T>(GetChunkAddress(index, dirtyPage: dirtyPage));

        public ChunkBasedSegment Segment => _owner;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal void UnpinChunk(int index)
        {
            var (si, _) = _owner.GetChunkLocation(index);
            var caches = _cachedEntries.Span;
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

            var caches = _cachedEntries.Span;
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

                    if (_cachedPages.Span[i].TryPromoteToExclusive())
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

            var caches = _cachedEntries.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    ref var page = ref caches[i];
                    if (--page.PromoteCounter == 0)
                    {
                        _cachedPages.Span[i].DemoteExclusive();
                    }
                    return;
                }
            }
        }

        internal void DirtyChunk(int index)
        {
            var (si, _) = _owner.GetChunkLocation(index);

            var caches = _cachedEntries.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                if (caches[i].SegmentIndex == si)
                {
                    caches[i].IsDirty = 1;
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        unsafe internal byte* GetChunkAddress(int index, bool pin = false, bool dirtyPage = false)
        {
            var (si, off) = _owner.GetChunkLocation(index);

            var lowHit = int.MaxValue;
            var pageI = -1;

            var cachedEntries = _cachedEntries.Span;
            for (int i = 0; i < _cachedPagesCount; i++)
            {
                ref var entry = ref cachedEntries[i];
                if (entry.SegmentIndex == si)
                {
                    if (pin)
                    {
                        ++entry.PinCounter;
                    }

                    if (dirtyPage)
                    {
                        entry.IsDirty = 1;
                    }
                    ++entry.HitCount;
                    return entry.BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
                }

                if ((entry.PinCounter == 0) && (entry.PromoteCounter == 0) && (entry.HitCount < lowHit))
                {
                    lowHit = entry.HitCount;
                    pageI = i;
                }
            }

            // Everything is pinned, that's bad...
            if (pageI == -1)
            {
                throw new NotImplementedException("No more available pages slot, all are occupied by pinned pages");
            }

            ref var cachedEntry = ref _cachedEntries.Span[pageI];
            var cachedPagesAccess = _cachedPages.Span;

            if (cachedEntry.IsDirty != 0)
            {
                cachedPagesAccess[pageI].SetPageDirty();
            }
            cachedPagesAccess[pageI].Dispose();

            cachedEntry.HitCount = 1;
            cachedEntry.SegmentIndex = si;
            cachedEntry.PinCounter = pin ? 1 : 0;
            cachedEntry.PromoteCounter = 0;
            cachedEntry.IsDirty = (short)(dirtyPage ? 1 : 0);

            cachedPagesAccess[pageI] = _owner.GetPageSharedAccessor(si);
            cachedEntry.BaseAddress = cachedPagesAccess[pageI].PageAddress + VirtualDiskManager.PageHeaderSize;

            return cachedEntry.BaseAddress + (si == 0 ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (off * _stride);
        }

        unsafe internal void ClearChunk(int index)
        {
            var addr = GetChunkAddress(index);
            new Span<long>(addr, _stride / 8).Clear();
        }

        public ChunkRandomAccessor(ChunkBasedSegment owner, int cachedPagesCount)
        {
            _owner = owner;
            _cachedPagesCount = cachedPagesCount;
            _cachedPages = new PageAccessor[cachedPagesCount];
            _cachedEntries = new CachedEntry[cachedPagesCount];

            _stride = _owner.Stride;

            var span = _cachedEntries.Span;
            for (int i = 0; i < span.Length; i++)
            {
                span[i].SegmentIndex = -1;
            }
        }

        public void DisposePageAccessors()
        {
            var cachedPages = _cachedPages.Span;
            var cachedEntries = _cachedEntries.Span;

            for (int i = 0; i < _cachedPagesCount; i++)
            {
                ref var cachedPage = ref cachedPages[i];
                ref var cachedEntry = ref cachedEntries[i];

                if (cachedEntry.IsDirty != 0)
                {
                    cachedPage.SetPageDirty();
                    cachedEntry.IsDirty = 0;
                }

                // Can't dispose if there are still operations ongoing that required their counterpart method to finish them
                if ((cachedEntry.PromoteCounter != 0) || (cachedEntry.PinCounter != 0))
                {
                    continue;
                }
                
                cachedPage.Dispose();
                cachedEntry.HitCount = 0;
                cachedEntry.SegmentIndex = -1;
            }
        }

        public void Dispose() => DisposePageAccessors();
    }

    /// <summary>
    /// Logical Segment that stores fixed sized chunk of data.
    /// </summary>
    /// <remarks>
    /// Provides API to allocate chunks, the occupancy map is stored in the Metadata of each page. The minimum chunk size is 8 bytes.
    /// </remarks>
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

        internal override bool Create(PageBlockType type, IMemoryOwner<uint> pagesMemOwner, int length, bool clear)
        {
            base.Create(type, pagesMemOwner, length, clear);

            // Clear the metadata sections that store the chunk's occupancy bitmap
            for (int i = 0; i < length; i++)
            {
                using var page = GetPageExclusiveAccessor(i);
                page.SetPageDirty();
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

        public ChunkRandomAccessor CreateChunkRandomAccessor(int cachedPagesCount=1) => new(this, cachedPagesCount);


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
                using var page = _segment.GetPageSharedAccessor(pageIndex);

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

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public bool SetL1(int index)
            {
                var l0Offset = index;
                var l0Mask = -1L;

                var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
                using var page = _segment.GetPageSharedAccessor(pageIndex);

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

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public void ClearL0(int index)
            {
                var l0Offset = index >> 6;
                var l0Mask = ~(1L << (index & 0x3F));

                var (pageIndex, pageOffset) = GetBitmapMaskLocation(l0Offset);
                using var page = _segment.GetPageSharedAccessor(pageIndex);

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

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public bool IsSet(int index)
            {
                var offset = index >> 6;
                var mask = 1L << (index & 0x3F);

                var (pageIndex, pageOffset) = GetBitmapMaskLocation(offset);
                using var page = _segment.GetPageSharedAccessor(pageIndex);

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
                PageAccessor curPage = default;

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
                                curPage = _segment.GetPageSharedAccessor(pageId);
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

                ChunkRandomAccessor chunkAccessor = default;
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
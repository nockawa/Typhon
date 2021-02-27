// unset

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    public class LogicalSegment : IDisposable
    {
        internal const int RootHeaderIndexSectionCount = 512;
        internal const int RootHeaderIndexSectionLength = RootHeaderIndexSectionCount * 4;

        private readonly LogicalSegmentManager _manager;
        private IMemoryOwner<uint> _pagesMemoryOwner;
        private ReadOnlyMemory<uint> _pages;

        public uint RootPageId { get; private set; }

        public int Length => _pages.Length;
        public ReadOnlySpan<uint> Pages => _pages.Span;
        public ReadWritePageAccessor GetPageReadWrite(int segmentIndex) => _manager.VDM.RequestPageReadWrite(Pages[segmentIndex]);
        public ReadOnlyPageAccessor GetPageReadOnly(int segmentIndex) => _manager.VDM.RequestPageReadOnly(Pages[segmentIndex]);

        internal LogicalSegment(LogicalSegmentManager manager)
        {
            _manager = manager;
        }

        public void Dispose()
        {
            _pagesMemoryOwner?.Dispose();
            _pagesMemoryOwner = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int GetMaxItemCount<T>(bool firstPage) where T : unmanaged => GetMaxItemCount(firstPage, Marshal.SizeOf<T>());
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int GetMaxItemCount(bool firstPage, int itemSize) => (firstPage ? (VirtualDiskManager.PageRawDataSize - RootHeaderIndexSectionLength) : VirtualDiskManager.PageRawDataSize) / itemSize;
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int GetItemCount<T>(int pageCount) where T : unmanaged => GetItemCount(pageCount, Marshal.SizeOf<T>());
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int GetItemCount(int pageCount, int itemSize) => ((pageCount * VirtualDiskManager.PageRawDataSize) - RootHeaderIndexSectionLength) / itemSize;
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static (int, int) GetItemLocation<T>(int itemIndex) => GetItemLocation(itemIndex, Marshal.SizeOf<T>());
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static (int, int) GetItemLocation(int itemIndex, int itemSize)
        {
            var s = itemSize;
            var fs = VirtualDiskManager.PageRawDataSize - RootHeaderIndexSectionLength;
            var ss = VirtualDiskManager.PageRawDataSize;

            var fc = fs / s;
            if (itemIndex < fc)
            {
                return (0, itemIndex);
            }

            var pi = Math.DivRem(itemIndex - fc, ss / s, out var off);
            return (pi + 1, off);
        }

        internal virtual bool Create(PageBlockType type, IMemoryOwner<uint> pagesMemOwner, int length)
        {
            var vdm = _manager.VDM;

            var pagesMemory = pagesMemOwner.Memory.Slice(0, length);
            var pages = pagesMemory.Span;
            RootPageId = pages[0];

            // Initialize the subsequent pages on disk
            var pageLength = Math.Min(pages.Length, RootHeaderIndexSectionCount);
            for (var i = 0; i < pages.Length; i++)
            {
                var pageIndex = pages[i];
                using var page = vdm.RequestPageReadWrite(pageIndex);

                page.InitHeader(PageClearMode.None, PageBlockFlags.IsLogicalSegment|(i==0 ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None), type, 1, 1);

                // Initialize the segment list for the root page
                if (i == 0)
                {
                    var rd = page.PageRawData.Cast<byte, uint>();
                    for (int j = 0; j < pageLength; j++)
                    {
                        rd[j] = pages[j];
                    }
                }

                // Update link list of the pages that make the segment
                ref var h = ref page.Header;
                h.LogicalSegmentNextRawDataPBID = ((i + 1) < pages.Length) ? pages[i + 1] : 0;
            }

            // Overflow, need to store remaining indices in more Index Pages?
            if (pages.Length > pageLength)
            {
                var indexPageCount = (pages.Length - RootHeaderIndexSectionCount + VirtualDiskManager.PageSize - 1) / sizeof(int);

            }

            _pagesMemoryOwner = pagesMemOwner;
            _pages = pagesMemory;

            return true;
        }

        public bool Load(uint pageId)
        {
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < Length; i++)
            {
                using var p = GetPageReadWrite(0);
                p.PageRawData.Clear();
            }
        }

        public void Fill(byte value)
        {
            for (int i = 0; i < Length; i++)
            {
                using var p = GetPageReadWrite(0);
                p.LogicalSegmentData.Fill(value);
            }
        }

        public delegate bool ReadOnlyAction<T>(ref ReadOnlyEnumerator<T> obj) where T : unmanaged;
        public delegate bool ReadWriteAction<T>(ref ReadWriteEnumerator<T> obj) where T : unmanaged;

        /// <summary>
        /// Iterate through all elements of the logical segment for read-only access
        /// </summary>
        /// <typeparam name="T">The type of the element</typeparam>
        /// <param name="action">The lambda to execute on each elements, the iteration will stop if the lambda returns <c>false</c></param>
        /// <remarks>
        /// This way to iterate is much slower than a 2 nested for-loop, use it only when performance are not critical
        /// </remarks>
        public void ForEachReadOnly<T>(ReadOnlyAction<T> action) where T : unmanaged
        {
            var e = new ReadOnlyEnumerator<T>(this);
            try
            {
                while (e.MoveNext())
                {
                    if (action(ref e) == false)
                    {
                        break;
                    }
                }
            }
            finally
            {
                e.Dispose();
            }
        }

        /// <summary>
        /// Iterate through all elements of the logical segment for read-write access
        /// </summary>
        /// <typeparam name="T">The type of the element</typeparam>
        /// <param name="action">The lambda to execute on each elements, the iteration will stop if the lambda returns <c>false</c></param>
        /// <remarks>
        /// This way to iterate is much slower than a 2 nested for-loop, use it only when performance are not critical
        /// </remarks>
        public void ForEachReadWrite<T>(ReadWriteAction<T> action) where T : unmanaged
        {
            var e = new ReadWriteEnumerator<T>(this);
            try
            {
                while (e.MoveNext())
                {
                    if (action(ref e) == false)
                    {
                        break;
                    }
                }
            }
            finally
            {
                e.Dispose();
            }
        }

        public struct ReadOnlyEnumerator<T> : IDisposable where T : unmanaged
        {
            private readonly LogicalSegment _segment;
            private readonly int _elementSize;
            private int _curPageIndex;
            private int _index;
            private unsafe byte* _item;
            private int _nextPageSwitch;
            private ReadOnlyPageAccessor _pageAccessor;

            unsafe public ReadOnlyEnumerator(LogicalSegment segment)
            {
                _segment = segment;
                _curPageIndex = -1;
                _pageAccessor = default;
                _index = -1;
                _item = null;
                _nextPageSwitch = 0;
                _elementSize = Marshal.SizeOf<T>();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
            unsafe internal bool MoveNext()
            {
                if (++_index >= _nextPageSwitch)
                {
                    if (++_curPageIndex >= _segment.Length)
                    {
                        return false;
                    }
                    _pageAccessor.Dispose();
                    _pageAccessor = _segment._manager.VDM.RequestPageReadOnly(_segment.Pages[_curPageIndex]);
                    _nextPageSwitch = LogicalSegment.GetMaxItemCount<T>(_curPageIndex == 0);
                    _index = 0;
                    _item = _pageAccessor.Page;
                }
                else
                {
                    _item += _elementSize;
                }

                return true;
            }

            unsafe public Span<T> AsSpan => new(_item, _elementSize);

            unsafe public T Current => Unsafe.AsRef<T>(_item);
            unsafe public ref readonly T CurrentAsRef => ref Unsafe.AsRef<T>(_item);
            public int CurrentIndex => _index;
            public uint PageId => _pageAccessor.PageId;
            public int CurrentPageCapacity => _nextPageSwitch;

            public void Dispose() => _pageAccessor.Dispose();
        }

        public struct ReadWriteEnumerator<T> : IDisposable where T : unmanaged
        {
            private readonly LogicalSegment _segment;
            private readonly int _elementSize;
            private int _curPageIndex;
            private int _index;
            private unsafe byte* _item;
            private int _nextPageSwitch;
            private ReadWritePageAccessor _pageAccessor;

            unsafe public ReadWriteEnumerator(LogicalSegment segment)
            {
                _segment = segment;
                _curPageIndex = -1;
                _pageAccessor = default;
                _index = -1;
                _item = null;
                _nextPageSwitch = 0;
                _elementSize = Marshal.SizeOf<T>();
            }

            unsafe public bool MoveNext()
            {
                if (++_index >= _nextPageSwitch)
                {
                    if (++_curPageIndex >= _segment.Length)
                    {
                        return false;
                    }
                    _pageAccessor.Dispose();
                    _pageAccessor = _segment._manager.VDM.RequestPageReadWrite(_segment.Pages[_curPageIndex]);
                    _nextPageSwitch = LogicalSegment.GetMaxItemCount<T>(_curPageIndex == 0);
                    _index = 0;
                    _item = _pageAccessor.Page;
                }
                else
                {
                    _item += _elementSize;
                }

                return true;
            }

            unsafe public T Current => Unsafe.AsRef<T>(_item);
            unsafe public ref T CurrentAsRef => ref Unsafe.AsRef<T>(_item);
            public uint PageId => _pageAccessor.PageId;
            public int CurrentPageCapacity => _nextPageSwitch;

            public void Dispose() => _pageAccessor.Dispose();
        }
    }
}
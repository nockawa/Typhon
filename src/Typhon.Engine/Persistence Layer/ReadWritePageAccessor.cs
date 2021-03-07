// unset

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine
{
    unsafe public struct ReadWritePageAccessor : IDisposable
    {
        /// <summary>
        /// Access to the header of the page. Use a <c>ref var</c> variable when writing into it.
        /// </summary>
        public ref PageBaseHeader Header => ref MemoryMarshal.Cast<byte, PageBaseHeader>(PageHeader).GetPinnableReference();
        /// <summary>
        /// Address of the page in memory. See Remarks.
        /// </summary>
        /// <remarks>
        /// Prefer one of the properties that return a <see cref="Span{T}"/>, it's safer, as fast.
        /// Don't exceed the page's size when accessing it.
        /// </remarks>
        public byte* PageAddress { get; }
        /// <summary>
        /// Span of the whole data of the page.
        /// </summary>
        public Span<byte> WholePage => new(PageAddress, VirtualDiskManager.PageSize);
        /// <summary>
        /// Span of the header of the page, you should prefer <see cref="Header"/>.
        /// </summary>
        public Span<byte> PageHeader => new(PageAddress, VirtualDiskManager.PageHeaderSize);
        /// <summary>
        /// Span of the page metadata, it's a 128 bytes zone inside the PageHeader, just right after the BaseHeader
        /// </summary>
        public Span<byte> PageMetadata => new(PageAddress + VirtualDiskManager.PageBaseHeaderSize, VirtualDiskManager.PageMetadataSize);
        /// <summary>
        /// Span of the page raw data, it's a 8000 bytes zone after the header.
        /// </summary>
        public Span<byte> PageRawData => new(PageAddress + VirtualDiskManager.PageHeaderSize, VirtualDiskManager.PageRawDataSize);
        /// <summary>
        /// Span of the Logical Segment's raw data. See Remarks.
        /// </summary>
        /// <remarks>
        /// If the page block is part of a Logical Segment, this will return the span covering its raw data section.
        /// BEWARE: the root page of a Logical Segment is 6000 bytes wide, subsequent pages will be 8000 bytes.
        /// Unpredictable result will occur if using this property on a non Logical Segment Page Block.
        /// </remarks>
        public Span<byte> LogicalSegmentData
        {
            get
            {
                var root = (PageAddress[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
                var offset = root ? LogicalSegment.RootHeaderIndexSectionLength : 0;
                return new(PageAddress + VirtualDiskManager.PageHeaderSize + offset, VirtualDiskManager.PageRawDataSize - offset);
            }
        }

        internal byte* GetElementAddr(int index, int stride, bool isLogicalRoot) =>
            PageAddress + VirtualDiskManager.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (index * stride);

        internal ref T GetElement<T>(int index, bool isLogicalRoot) where T : unmanaged =>
            ref Unsafe.AsRef<T>(PageAddress + VirtualDiskManager.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (index * sizeof(T)));

        /// <summary>
        /// The Disk Page Id the accessor is into
        /// </summary>
        public uint PageId => _pageId;

        private readonly VirtualDiskManager _owner;
        private readonly uint _pageId;
        private VirtualDiskManager.PageInfo _pi;
        private readonly VirtualDiskManager.PagesAccessMode _previousMode;

        internal ReadWritePageAccessor(VirtualDiskManager owner, VirtualDiskManager.PageInfo pi, byte* pageAddress, VirtualDiskManager.PagesAccessMode previousMode)
        {
            _owner = owner;
            _pageId = pi.PageId;    // We want to store this locally i case the PageInfo gets reallocated, we should add some debug check btw...
            _pi = pi;
            _previousMode = previousMode;
            PageAddress = pageAddress;
        }

        public bool IsValid => _pi != null;

        public void Dispose()
        {
            if (_pi == null)
            {
                return;
            }

            if (_previousMode != VirtualDiskManager.PagesAccessMode.Idle)
            {
                _owner.DemoteExclusiveReadWrite(_pi, _previousMode);
            }
            else
            {
                _owner.TransitionPageFromAccessToIdle(_pi);
            }

            _pi = null;
        }

        public void InitHeader(PageClearMode clearMode, PageBlockFlags flags, PageBlockType type, int changeRevision, int formatRevision)
        {
            if (clearMode == PageClearMode.Header)
            {
                PageHeader.Slice(0, VirtualDiskManager.PageHeaderSize).Clear();
            } else if (clearMode == PageClearMode.WholePage)
            {
                PageHeader.Clear();
            }
            ref var header = ref Header;
            header.Flags = flags;
            header.Type = type;
            header.ChangeRevision = 1;
            header.FormatRevision = 1;
        }
    }
}
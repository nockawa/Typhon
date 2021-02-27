// unset

using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine
{
    unsafe public struct ReadOnlyPageAccessor : IDisposable
    {
        /// <summary>
        /// Access to the header of the page. Use a <c>ref var</c> variable when writing into it.
        /// </summary>
        public ref readonly PageBaseHeader Header => ref MemoryMarshal.Cast<byte, PageBaseHeader>(PageHeader).GetPinnableReference();
        /// <summary>
        /// Address of the page in memory. See Remarks
        /// </summary>
        /// <remarks>
        /// Prefer one of the properties that return a <see cref="ReadOnlySpan{T}"/>, it's safer, as fast.
        /// Don't exceed the page's size when accessing it.
        /// DON'T WRITE into it, use <see cref="ReadWritePageAccessor"/> instead.
        /// </remarks>
        public byte* Page { get; }
        /// <summary>
        /// Span of the whole data of the page.
        /// </summary>
        public ReadOnlySpan<byte> WholePage => new(Page, VirtualDiskManager.PageSize);
        /// <summary>
        /// Span of the header of the page, you should prefer <see cref="Header"/>.
        /// </summary>
        public ReadOnlySpan<byte> PageHeader => new(Page, VirtualDiskManager.PageHeaderSize);
        /// <summary>
        /// Span of the page metadata, it's a 128 bytes zone inside the PageHeader, just right after the BaseHeader
        /// </summary>
        public Span<byte> PageMetadata => new(Page + VirtualDiskManager.PageBaseHeaderSize, VirtualDiskManager.PageMetadataSize);
        /// <summary>
        /// Span of the page raw data, it's a 8000 bytes zone after the header.
        /// </summary>
        public ReadOnlySpan<byte> PageRawData => new(Page + VirtualDiskManager.PageHeaderSize, VirtualDiskManager.PageRawDataSize);
        /// <summary>
        /// Span of the Logical Segment's raw data. See Remarks.
        /// </summary>
        /// <remarks>
        /// If the page block is part of a Logical Segment, this will return the span covering its raw data section.
        /// BEWARE: the root page of a Logical Segment is 6000 bytes wide, subsequent pages will be 8000 bytes.
        /// Unpredictable result will occur if using this property on a non Logical Segment Page Block.
        /// </remarks>
        public ReadOnlySpan<byte> LogicalSegmentData
        {
            get
            {
                var root = (Page[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
                var offset = root ? LogicalSegment.RootHeaderIndexSectionLength : 0;
                return new(Page + VirtualDiskManager.PageHeaderSize + offset, VirtualDiskManager.PageRawDataSize - offset);
            }
        }
        /// <summary>
        /// The Disk Page Id the accessor is into
        /// </summary>
        public uint PageId => _pageId;
        
        private readonly VirtualDiskManager _owner;
        private readonly uint _pageId;
        private VirtualDiskManager.PageInfo _pi;

        internal ReadOnlyPageAccessor(VirtualDiskManager owner, VirtualDiskManager.PageInfo pi, byte* page)
        {
            _owner = owner;
            _pageId = pi.PageId;
            _pi = pi;
            Page = page;
        }

        public void Dispose()
        {
            if (_pi == null)
            {
                return;
            }

            _owner.TransitionPageFromAccessToIdle(_pageId, _pi);

            _pi = null;
        }
    }
}
// unset

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

unsafe public struct PageAccessor : IDisposable
{
    #region Read-Only Access

    /// <summary>
    /// Access to the header of the page. Use a <c>ref var</c> variable when writing into it.
    /// </summary>
    public ref readonly PageBaseHeader HeaderReadOnly => ref MemoryMarshal.Cast<byte, PageBaseHeader>(PageHeaderReadOnly).GetPinnableReference();
    /// <summary>
    /// Span of the whole data of the page.
    /// </summary>
    public ReadOnlySpan<byte> WholePageReadOnly => new(PageAddress, PagedMemoryMappedFile.PageSize);
    /// <summary>
    /// Span of the header of the page, you should prefer <see cref="HeaderReadOnly"/>.
    /// </summary>
    public ReadOnlySpan<byte> PageHeaderReadOnly => new(PageAddress, PagedMemoryMappedFile.PageHeaderSize);
    /// <summary>
    /// Span of the page metadata, it's a 128 bytes zone inside the PageHeader, just right after the BaseHeader
    /// </summary>
    public ReadOnlySpan<byte> PageMetadataReadOnly => new(PageAddress + PagedMemoryMappedFile.PageBaseHeaderSize, PagedMemoryMappedFile.PageMetadataSize);
    /// <summary>
    /// Span of the page raw data, it's a 8000 bytes zone after the header.
    /// </summary>
    public ReadOnlySpan<byte> PageRawDataReadOnly => new(PageAddress + PagedMemoryMappedFile.PageHeaderSize, PagedMemoryMappedFile.PageRawDataSize);
    /// <summary>
    /// Span of the Logical Segment's raw data. See Remarks.
    /// </summary>
    /// <remarks>
    /// If the page block is part of a Logical Segment, this will return the span covering its raw data section.
    /// BEWARE: the root page of a Logical Segment is 6000 bytes wide, subsequent pages will be 8000 bytes.
    /// Unpredictable result will occur if using this property on a non Logical Segment Page Block.
    /// </remarks>
    public ReadOnlySpan<byte> LogicalSegmentDataReadOnly
    {
        get
        {
            var root = (PageAddress[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
            var offset = root ? LogicalSegment.RootHeaderIndexSectionLength : 0;
            return new(PageAddress + PagedMemoryMappedFile.PageHeaderSize + offset, PagedMemoryMappedFile.PageRawDataSize - offset);
        }
    }
    internal ref readonly T GetElementReadOnly<T>(int index, bool isLogicalRoot) where T : unmanaged =>
        ref Unsafe.AsRef<T>(PageAddress + PagedMemoryMappedFile.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (index * sizeof(T)));

    #endregion

    #region Read/Write Access

    /// <summary>
    /// Address of the page in memory. See Remarks
    /// </summary>
    /// <remarks>
    /// Prefer one of the properties that return a <see cref="ReadOnlySpan{T}"/>, it's safer, almost as fast.
    /// Don't exceed the page's size when accessing it.
    /// </remarks>
    public byte* PageAddress { get; }

    internal byte* GetElementAddr(int index, int stride, bool isLogicalRoot) =>
        PageAddress + PagedMemoryMappedFile.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (index * stride);

    /// <summary>
    /// Access to the header of the page. Use a <c>ref var</c> variable when writing into it.
    /// </summary>
    public ref PageBaseHeader Header => ref MemoryMarshal.Cast<byte, PageBaseHeader>(PageHeader).GetPinnableReference();
    /// <summary>
    /// Span of the whole data of the page.
    /// </summary>
    public Span<byte> WholePage => new(PageAddress, PagedMemoryMappedFile.PageSize);
    /// <summary>
    /// Span of the header of the page, you should prefer <see cref="Header"/>.
    /// </summary>
    public Span<byte> PageHeader => new(PageAddress, PagedMemoryMappedFile.PageHeaderSize);
    /// <summary>
    /// Span of the page metadata, it's a 128 bytes zone inside the PageHeader, just right after the BaseHeader
    /// </summary>
    public Span<byte> PageMetadata => new(PageAddress + PagedMemoryMappedFile.PageBaseHeaderSize, PagedMemoryMappedFile.PageMetadataSize);
    /// <summary>
    /// Span of the page raw data, it's a 8000 bytes zone after the header.
    /// </summary>
    public Span<byte> PageRawData => new(PageAddress + PagedMemoryMappedFile.PageHeaderSize, PagedMemoryMappedFile.PageRawDataSize);
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
            return new(PageAddress + PagedMemoryMappedFile.PageHeaderSize + offset, PagedMemoryMappedFile.PageRawDataSize - offset);
        }
    }

    internal ref T GetElement<T>(int index, bool isLogicalRoot) where T : unmanaged =>
        ref Unsafe.AsRef<T>(PageAddress + PagedMemoryMappedFile.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (index * sizeof(T)));

    #endregion

    public void SetPageDirty() => _owner.SetPageDirty(_pi);

    public bool TryPromoteToExclusive() => _owner.TryPromoteToExclusive(_pageId, _pi, out _previousMode);

    public void DemoteExclusive()
    {
        _owner.DemoteExclusive(_pi, _previousMode);
        _previousMode = PagedMemoryMappedFile.PagesAccessMode.Idle;
    }

    /// <summary>
    /// The Disk Page Id the accessor is into
    /// </summary>
    public uint PageId => _pageId;
        
    private readonly PagedMemoryMappedFile _owner;
    private readonly uint _pageId;
    private PagedMemoryMappedFile.PageInfo _pi;
    private PagedMemoryMappedFile.PagesAccessMode _previousMode;

    internal PageAccessor(PagedMemoryMappedFile owner, PagedMemoryMappedFile.PageInfo pi, byte* pageAddress)
    {
        _owner = owner;
        _pageId = pi.PageId;
        _pi = pi;
        _previousMode = PagedMemoryMappedFile.PagesAccessMode.Idle;
        PageAddress = pageAddress;
    }

    public bool IsValid => _pi != null;

    public void Dispose()
    {
        if (_pi == null)
        {
            return;
        }

        if (_previousMode != PagedMemoryMappedFile.PagesAccessMode.Idle)
        {
            _owner.DemoteExclusive(_pi, _previousMode);
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
            PageHeader.Slice(0, PagedMemoryMappedFile.PageHeaderSize).Clear();
        }
        else if (clearMode == PageClearMode.WholePage)
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
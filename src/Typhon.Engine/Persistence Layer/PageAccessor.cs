// unset

using System;
using System.Diagnostics;
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
    public Span<byte> PageRawData
    {
        get
        {
            EnsureDataReady();
            return new Span<byte>(PageAddress + PagedMemoryMappedFile.PageHeaderSize, PagedMemoryMappedFile.PageRawDataSize);
        }
    }

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

    public void SetPageDirty()
    {
    }

    public bool TryPromoteToExclusive() => _owner.TryPromoteToExclusive(_filePageIndex, _pi, out _previousMode);

    public void DemoteExclusive()
    {
        _owner.DemoteExclusive(_pi, _previousMode);
        _previousMode = PagedFile.PageState.Idle;
    }

    /// <summary>
    /// The Disk Page ID the accessor is into
    /// </summary>
    public int FilePageIndex => _filePageIndex;
    
    public int MemPageIndex => _pi.MemPageIndex;
        
    private readonly PagedFile _owner;
    private readonly int _filePageIndex;
    private PagedFile.PageInfo _pi;
    private PagedFile.PageState _previousMode;
    private bool _isReady;

    internal PageAccessor(PagedMemoryMappedFile owner, PagedMemoryMappedFile.PageInfo pi, byte* pageAddress)
    {
        /*
        _owner = owner;
        _pageId = pi.PageId;
        _pi = pi;
        _previousMode = PagedMemoryMappedFile.PagesAccessMode.Idle;
        PageAddress = pageAddress;
    */
    }

    internal PageAccessor(PagedFile owner, PagedFile.PageInfo pi)
    {
        _owner = owner;
        _filePageIndex = pi.FilePageIndex;
        _pi = pi;
        _previousMode = PagedFile.PageState.Idle;
        _isReady = pi.IOReadTask==null || pi.IOReadTask.IsCompletedSuccessfully;
        PageAddress = owner.GetMemPageAddress(pi.MemPageIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void EnsureDataReady()
    {
        if (_isReady)
        {
            return;
        }

        Debug.Assert(_pi.IOReadTask!=null);
        var ioTask = _pi.IOReadTask;
        if (ioTask.IsCompletedSuccessfully)
        {
            _isReady = true;
            _pi.ResetIOCompletionTask();
            return;
        }
        
        ioTask.GetAwaiter().GetResult();
        _isReady = true;
        _pi.ResetIOCompletionTask();
    }
    
    public bool IsValid => _pi != null;

    public void Dispose()
    {
        if (_pi == null)
        {
            return;
        }

        if (_previousMode != PagedFile.PageState.Idle)
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
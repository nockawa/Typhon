// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[PublicAPI]
unsafe public struct PageAccessor : IDisposable
{
    #region Read-Only Access

    internal ref T GetHeader<T>(int offset) where T : unmanaged
    {
        EnsureDataReady();
        return ref PageHeader.Slice(offset).Cast<byte, T>()[0];
    }

    /// <summary>
    /// Span of the whole data of the page.
    /// </summary>
    public ReadOnlySpan<byte> WholePageReadOnly
    {
        get
        {
            EnsureDataReady();
            return new ReadOnlySpan<byte>(_pageAddress, PagedMMF.PageSize);
        }
    }

    /// <summary>
    /// Span of the header of the page.
    /// </summary>
    public ReadOnlySpan<byte> PageHeaderReadOnly
    {
        get
        {
            EnsureDataReady();
            return new ReadOnlySpan<byte>(_pageAddress, PagedMMF.PageHeaderSize);
        }
    }

    /// <summary>
    /// Span of the page metadata, it's a 128 bytes zone inside the PageHeader, just right after the BaseHeader
    /// </summary>
    public ReadOnlySpan<byte> PageMetadataReadOnly
    {
        get
        {
            EnsureDataReady();
            return new ReadOnlySpan<byte>(_pageAddress + PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize);
        }
    }

    /// <summary>
    /// Span of the page raw data, it's a 8000 bytes zone after the header.
    /// </summary>
    public ReadOnlySpan<byte> PageRawDataReadOnly
    {
        get
        {
            EnsureDataReady();
            return new ReadOnlySpan<byte>(_pageAddress + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
        }
    }

    /// <summary>
    /// Span of the Logical Segment's raw data. See Remarks.
    /// </summary>
    /// <remarks>
    /// If the page block is part of a Logical Segment, this will return the span covering its raw data section.
    /// BEWARE: the root page of a Logical Segment is 6000 bytes wide, subsequent pages will be 8000 bytes.
    /// Unpredictable result will occur if using this property on a non-Logical Segment Page Block.
    /// </remarks>
    public ReadOnlySpan<byte> LogicalSegmentDataReadOnly
    {
        get
        {
            EnsureDataReady();
            var root = (_pageAddress[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
            var offset = root ? LogicalSegment.RootHeaderIndexSectionLength : 0;
            return new ReadOnlySpan<byte>(_pageAddress + PagedMMF.PageHeaderSize + offset, PagedMMF.PageRawDataSize - offset);
        }
    }
    internal ref readonly T GetElementReadOnly<T>(int index, bool isLogicalRoot) where T : unmanaged
    {
        EnsureDataReady();
        return ref Unsafe.AsRef<T>(_pageAddress + PagedMMF.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) +
                                   (index * sizeof(T)));
    }

    #endregion

    #region Read/Write Access

    /// Address of the page in memory. Data may not be ready yet, use <see cref="EnsureDataReady"/> to ensure the data is loaded.
    private readonly byte* _pageAddress;

    internal byte* GetElementAddr(int index, int stride, bool isLogicalRoot)
    {
        EnsureDataReady();
        return _pageAddress + PagedMMF.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) + (index * stride);
    }

    internal byte* GetRawDataAddr()
    {
        EnsureDataReady();
        return _pageAddress + PagedMMF.PageHeaderSize;
    }
    
    /// <summary>
    /// Span of the whole data of the page.
    /// </summary>
    public Span<byte> WholePage
    {
        get
        {
            EnsureDataReady();
            return new Span<byte>(_pageAddress, PagedMMF.PageSize);
        }
    }

    /// <summary>
    /// Span of the header of the page.
    /// </summary>
    public Span<byte> PageHeader
    {
        get
        {
            EnsureDataReady();
            return new Span<byte>(_pageAddress, PagedMMF.PageHeaderSize);
        }
    }

    /// <summary>
    /// Span of the page metadata, it's a 128 bytes zone inside the PageHeader, just right after the BaseHeader
    /// </summary>
    public Span<byte> PageMetadata
    {
        get
        {
            EnsureDataReady();
            return new Span<byte>(_pageAddress + PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize);
        }
    }

    /// <summary>
    /// Span of the page raw data, it's a 8000 bytes zone after the header.
    /// </summary>
    public Span<byte> PageRawData
    {
        get
        {
            EnsureDataReady();
            return new Span<byte>(_pageAddress + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
        }
    }

    /// <summary>
    /// Span of the Logical Segment's raw data. See Remarks.
    /// </summary>
    /// <remarks>
    /// If the page block is part of a Logical Segment, this will return the span covering its raw data section.
    /// BEWARE: the root page of a Logical Segment is 6000 bytes wide, subsequent pages will be 8000 bytes.
    /// Unpredictable result will occur if using this property on a non-Logical Segment Page Block.
    /// </remarks>
    public Span<byte> LogicalSegmentData
    {
        get
        {
            EnsureDataReady();
            var root = (_pageAddress[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
            var offset = root ? LogicalSegment.RootHeaderIndexSectionLength : 0;
            return new Span<byte>(_pageAddress + PagedMMF.PageHeaderSize + offset, PagedMMF.PageRawDataSize - offset);
        }
    }

    internal ref T GetElement<T>(int index, bool isLogicalRoot) where T : unmanaged
    {
        EnsureDataReady();
        return ref Unsafe.AsRef<T>(_pageAddress + PagedMMF.PageHeaderSize + (isLogicalRoot ? LogicalSegment.RootHeaderIndexSectionLength : 0) +
                                   (index * sizeof(T)));
    }

    #endregion

    public readonly void SetPageDirty()
    {
    }

    public bool TryPromoteToExclusive() => _owner.TryPromoteToExclusive(_filePageIndex, _pi, out _previousMode);

    public void DemoteExclusive()
    {
        _owner.DemoteExclusive(_pi, _previousMode);
        _previousMode = PagedMMF.PageState.Idle;
    }

    /// <summary>
    /// The Disk Page ID the accessor is into
    /// </summary>
    public int FilePageIndex => _filePageIndex;
    
    public int MemPageIndex => _pi.MemPageIndex;
        
    private readonly PagedMMF _owner;
    private readonly int _filePageIndex;
    private PagedMMF.PageInfo _pi;
    private PagedMMF.PageState _previousMode;
    private bool _isReady;

    internal PageAccessor(PagedMMF owner, PagedMMF.PageInfo pi)
    {
        _owner = owner;
        _filePageIndex = pi.FilePageIndex;
        _pi = pi;
        _previousMode = PagedMMF.PageState.Idle;
        _isReady = pi.IOReadTask==null || pi.IOReadTask.IsCompletedSuccessfully;
        _pageAddress = owner.GetMemPageAddress(pi.MemPageIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void EnsureDataReady()
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

        if (_previousMode != PagedMMF.PageState.Idle)
        {
            _owner.DemoteExclusive(_pi, _previousMode);
        }
        else
        {
            _owner.TransitionPageFromAccessToIdle(_pi);
        }

        _pi = null;
    }
    public void InitHeader(PageClearMode clearMode, PageBlockFlags flags, PageBlockType type, short formatRevision)
    {
        if (clearMode == PageClearMode.Header)
        {
            PageHeader.Slice(0, PagedMMF.PageHeaderSize).Clear();
        }
        else if (clearMode == PageClearMode.WholePage)
        {
            PageHeader.Clear();
        }
        ref var header = ref GetHeader<PageBaseHeader>(PageBaseHeader.Offset);
        header.Flags = flags;
        header.Type = type;
        header.FormatRevision = formatRevision;
    }
}
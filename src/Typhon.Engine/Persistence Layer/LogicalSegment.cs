// unset

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Expose a Logical segment of Pages
/// </summary>
/// <remarks>
/// Logical Segment is made of several Pages which IDs are stored in a dedicated private section of the logical segment.
/// The segment can easily be shrink/grown by removing/adding more pages. The first page of the Logical Segment is split in two parts
///  - The Page Directory: 512 entries that reference the first 512 pages of the Logical Segment, overflown data is stored into
///    subsequent dedicated pages.
///  - The segment first raw data, which is 6000 bytes, instead of 8000 for all subsequent pages.
/// The segment also maintain a linked list in the Page Header to allow faster forward traversal.
/// There is some basic API that allow to store/enumerate fixed size elements, indexed into the logical segment.
/// </remarks>
public class LogicalSegment : IDisposable
{
    internal struct SerializationData
    {
        public uint RootPageId;
    }
    internal SerializationData SerializeSettings() => new() { RootPageId = RootPageId };

    internal const int RootHeaderIndexSectionCount = 512;
    internal const int RootHeaderIndexSectionLength = RootHeaderIndexSectionCount * 4;

    private readonly LogicalSegmentManager _manager;
    private uint[] _pages;

    public uint RootPageId { get; private set; }

    public int Length => _pages.Length;
    public ReadOnlySpan<uint> Pages => _pages;
    public PageAccessor GetPageExclusiveAccessor(int segmentIndex) => _manager.PMMF.RequestPageExclusive(Pages[segmentIndex]);
    public PageAccessor GetPageSharedAccessor(int segmentIndex) => _manager.PMMF.RequestPageShared(Pages[segmentIndex]);

    internal LogicalSegment(LogicalSegmentManager manager)
    {
        _manager = manager;
    }

    public void Dispose()
    {
        _pages = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetMaxItemCount<T>(bool firstPage) where T : unmanaged => GetMaxItemCount(firstPage, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetMaxItemCount(bool firstPage, int itemSize) => (firstPage ? (PagedMemoryMappedFile.PageRawDataSize - RootHeaderIndexSectionLength) : PagedMemoryMappedFile.PageRawDataSize) / itemSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetItemCount<T>(int pageCount) where T : unmanaged => GetItemCount(pageCount, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetItemCount(int pageCount, int itemSize) => ((pageCount * PagedMemoryMappedFile.PageRawDataSize) - RootHeaderIndexSectionLength) / itemSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) GetItemLocation<T>(int itemIndex) => GetItemLocation(itemIndex, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) GetItemLocation(int itemIndex, int itemSize)
    {
        var s = itemSize;
        var fs = PagedMemoryMappedFile.PageRawDataSize - RootHeaderIndexSectionLength;
        var ss = PagedMemoryMappedFile.PageRawDataSize;

        var fc = fs / s;
        if (itemIndex < fc)
        {
            return (0, itemIndex);
        }

        var pi = Math.DivRem(itemIndex - fc, ss / s, out var off);
        return (pi + 1, off);
    }

    internal bool Create(PageBlockType type, uint pageId, bool clear)
    {
        Span<uint> ids = stackalloc uint[1];
        ids[0] = pageId;
        return Create(type, ids, clear);
    }
        
    internal virtual bool Create(PageBlockType type, Span<uint> pageIds, bool clear)
    {
        var vdm = _manager.PMMF;

        RootPageId = pageIds[0];

        // Initialize the subsequent pages on disk
        var pageLength = Math.Min(pageIds.Length, RootHeaderIndexSectionCount);
        for (var i = 0; i < pageIds.Length; i++)
        {
            var pageIndex = pageIds[i];
            using var page = vdm.RequestPageExclusive(pageIndex);
            page.SetPageDirty();

            if (clear)
            {
                page.PageRawData.Clear();
            }

            page.InitHeader(PageClearMode.None, PageBlockFlags.IsLogicalSegment|(i==0 ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None), type, 1, 1);

            // Initialize the segment list for the root page
            if (i == 0)
            {
                var rd = page.PageRawData.Cast<byte, uint>();
                int j;
                for (j = 0; j < pageLength; j++)
                {
                    rd[j] = pageIds[j];
                }
                rd[j] = 0;              // Mark the end of the segment list
            }

            // Update link list of the pages that make the segment
            ref var h = ref page.Header;
            h.LogicalSegmentNextRawDataPBID = ((i + 1) < pageIds.Length) ? pageIds[i + 1] : 0;
        }

        // Overflow, need to store remaining indices in more Index Pages?
        if (pageIds.Length > pageLength)
        {
            var indexPageCount = (pageIds.Length - RootHeaderIndexSectionCount + PagedMemoryMappedFile.PageSize - 1) / sizeof(int);
            throw new NotImplementedException();
        }

        _pages = pageIds.ToArray();

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
            using var p = GetPageExclusiveAccessor(i);
            p.SetPageDirty();
            p.PageRawData.Clear();
        }
    }

    public void Fill(byte value)
    {
        for (int i = 0; i < Length; i++)
        {
            using var p = GetPageExclusiveAccessor(i);
            p.SetPageDirty();
            p.LogicalSegmentData.Fill(value);
        }
    }
}
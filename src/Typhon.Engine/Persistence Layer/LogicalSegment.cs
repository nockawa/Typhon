// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

public enum PageClearMode
{
    None = 0,
    Header = 1,
    WholePage = 2
}

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LogicalSegmentHeader
{
    unsafe public static readonly int Size = sizeof(LogicalSegmentHeader);
    public static readonly int TotalSize =  PageBaseHeader.Size + Size;
    public static readonly int Offset = PageBaseHeader.Size;
    
    /// <summary>
    /// If the Page Block is a Logical Segment, will store the index to the next block storing Map Data, 0 if there's none.
    /// </summary>
    public int LogicalSegmentNextMapPBID;
    /// <summary>
    /// If the Page Block is a Logical Segment, will store the index to the next block storing Raw Data, 0 if there's none.
    /// </summary>
    public int LogicalSegmentNextRawDataPBID;
}

/// <summary>
/// Expose a Logical segment of Pages
/// </summary>
/// <remarks>
/// Logical Segment is made of several Pages which IDs are stored in a dedicated private section of its raw data.
/// The segment can easily be shrunk/grown by removing/adding more pages. The first page of the Logical Segment is split in two parts
///  - The Page Directory: 500 entries that reference the first 500 pages of the Logical Segment, overflown data is stored into
///    subsequent dedicated pages that store only indices, so 2000 per page.
///  - The segment first raw data, which is 6000 bytes, instead of 8000 for all subsequent pages.
/// The segment also maintain a linked list in the Page Header to allow faster forward traversal.
/// There is some basic API that allow to store/enumerate fixed size elements, indexed into the logical segment.
/// </remarks>
public class LogicalSegment : IDisposable
{
    /*
    internal struct SerializationData
    {
        public int RootPageId;
    }
    internal SerializationData SerializeSettings() => new() { RootPageId = RootPageIndex };
    */

    internal const int RootHeaderIndexSectionCount = 500;
    internal const int RootHeaderIndexSectionLength = RootHeaderIndexSectionCount * sizeof(int);
    internal const int NextHeadersIndexSectionCount = PagedMMF.PageRawDataSize / sizeof(int);

    private readonly ManagedPagedMMF _manager;
    private int[] _pages;

    public int RootPageIndex { get; private set; }

    public int Length => _pages.Length;
    public ReadOnlySpan<int> Pages => _pages;
    public bool GetPageExclusiveAccessor(int segmentFilePageIndex, out PageAccessor result,
        long timeout = Timeout.Infinite, CancellationToken cancellationToken = default) 
        => _manager.RequestPage(Pages[segmentFilePageIndex], true, out result, timeout, cancellationToken);
    
    public bool GetPageSharedAccessor(int segmentFilePageIndex, out PageAccessor result,
        long timeout = Timeout.Infinite, CancellationToken cancellationToken = default) 
        => _manager.RequestPage(Pages[segmentFilePageIndex], false, out result, timeout, cancellationToken);

    internal LogicalSegment(ManagedPagedMMF manager)
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
    public static int GetMaxItemCount(bool firstPage, int itemSize) => (firstPage ? (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength) : PagedMMF.PageRawDataSize) / itemSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetItemCount<T>(int pageCount) where T : unmanaged => GetItemCount(pageCount, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int GetItemCount(int pageCount, int itemSize) => ((pageCount * PagedMMF.PageRawDataSize) - RootHeaderIndexSectionLength) / itemSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) GetItemLocation<T>(int itemIndex) => GetItemLocation(itemIndex, Marshal.SizeOf<T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int, int) GetItemLocation(int itemIndex, int itemSize)
    {
        var s = itemSize;
        var fs = PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength;
        var ss = PagedMMF.PageRawDataSize;

        var fc = fs / s;
        if (itemIndex < fc)
        {
            return (0, itemIndex);
        }

        var pi = Math.DivRem(itemIndex - fc, ss / s, out var off);
        return (pi + 1, off);
    }

    internal bool Create(PageBlockType type, int filePageIndex, bool clear, ChangeSet changeSet = null)
    {
        Span<int> ids = stackalloc int[1];
        ids[0] = filePageIndex;
        return Create(type, ids, clear, changeSet);
    }
        
    unsafe internal virtual bool Create(PageBlockType type, Span<int> filePageIndices, bool clear, ChangeSet changeSet = null)
    {
        RootPageIndex = filePageIndices[0];

        // Compute the number of subsequent pages needed to store the indices (if they don't fit in the root page)
        // The end of the indices list is marked by a 0 value, we need to save space for this entry too, so the next line is accurate, if you wonder.
        var subIndicesPageCount = (filePageIndices.Length - RootHeaderIndexSectionCount + NextHeadersIndexSectionCount) / NextHeadersIndexSectionCount;
        
        // Store the indices, code is complex because we may need multiple pages to store them all.
        // Reminder of how data is structured:
        // - Each page is 8192 bytes, with 192 bytes of header, and 8000 bytes of raw data.
        // - The first page is the root page, its raw data contains the first 500 indices, and the first 6000 bytes of data.
        // - If the segment is bigger than 500 pages, we allocate dedicated pages to store the remaining indices, so 2000 indices per page.
        // - Subsequent data pages are storing data only, so 8000 bytes each.
        // In the headers, we maintain two linked lists:
        // 1. The logical segment next map page ID (LogicalSegmentNextMapPBID), which is used to traverse the indices pages.
        // 2. The logical segment next raw data page ID(LogicalSegmentNextRawDataPBID), which is used to traverse the data pages.
        // Both of these linked lists are terminated by 0.
        {
            Span<int> indicesPagesIndices = stackalloc int[subIndicesPageCount];
            if (subIndicesPageCount > 0)
            {
                _manager.AllocatePages(ref indicesPagesIndices, changeSet);
            }
            bool isFirstPage = true;
            var remainingIndices = filePageIndices.Length;
            var curIndicesPageIndex = 0;
            var curFilePageIndex = 0;
            
            while (remainingIndices > 0)
            {
                var curIndicesCount = Math.Min(remainingIndices, isFirstPage ? RootHeaderIndexSectionCount : NextHeadersIndexSectionCount);

                _manager.RequestPage(isFirstPage ? filePageIndices[0] : indicesPagesIndices[curIndicesPageIndex++], true, out var pa);
                using (pa)
                {
                    changeSet?.Add(pa);

                    pa.InitHeader(
                        PageClearMode.Header, 
                        PageBlockFlags.IsLogicalSegment | (isFirstPage ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None), 
                        type, 1);

                    ref var lsh = ref pa.GetHeader<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                    lsh.LogicalSegmentNextMapPBID = (curIndicesPageIndex < indicesPagesIndices.Length) ? indicesPagesIndices[curIndicesPageIndex] : 0;
                    
                    var rd = pa.PageRawData.Cast<byte, int>();
                    int j;
                    for (j = 0; j < curIndicesCount; j++)
                    {
                        rd[j] = filePageIndices[curFilePageIndex++];
                    }

                    remainingIndices -= curIndicesCount;
                    if (remainingIndices == 0)
                    {
                        if (j < rd.Length)
                        {
                            rd[j] = 0;
                        }
                        
                        // The current page is full, we need on fetch one more... just to store the termination 0 value
                        else
                        {
                            _manager.RequestPage(indicesPagesIndices[curIndicesPageIndex], true, out var paEnd);
                            using (paEnd)
                            {
                                changeSet?.Add(paEnd);
                                paEnd.PageRawData.Cast<byte, int>()[0] = 0;
                            }
                        }
                    }
                    isFirstPage = false;
                }
            }
        }
        
        // Initialize the subsequent pages on disk
        for (var i = 0; i < filePageIndices.Length; i++)
        {
            var pageIndex = filePageIndices[i];
            _manager.RequestPage(pageIndex, true, out var pa);
            using (pa)
            {
                changeSet?.Add(pa);

                if (clear)
                {
                    pa.LogicalSegmentData.Clear();
                }

                pa.InitHeader(PageClearMode.None, PageBlockFlags.IsLogicalSegment|(i==0 ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None), type, 1);

                // Update link list of the pages that make the segment
                ref var lsh = ref pa.GetHeader<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                lsh.LogicalSegmentNextRawDataPBID = ((i + 1) < filePageIndices.Length) ? filePageIndices[i + 1] : 0;
            }
        }
        
        _pages = filePageIndices.ToArray();

        return true;
    }
    
    internal virtual bool Load(int filePageIndex)
    {
        RootPageIndex = filePageIndex;
        
        _manager.RequestPage(RootPageIndex, true, out var pa);
        var pages = new List<int>();
        var rd = pa.PageRawData.Cast<byte, int>();
        var maxIndicesForPage = RootHeaderIndexSectionCount;
        var i = 0;
        while (rd[i] != 0)
        {
            pages.Add(rd[i]);
            i++;
            if (i == maxIndicesForPage)
            {
                // We reached the end of the root page, we need to load more pages
                ref var lsh = ref pa.GetHeader<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                if (lsh.LogicalSegmentNextMapPBID == 0)
                {
                    break; // No more pages
                }
                pa.Dispose();
                _manager.RequestPage(lsh.LogicalSegmentNextMapPBID, true, out pa);
                rd = pa.PageRawData.Cast<byte, int>();
                i = 0; // Reset index for the new page
                maxIndicesForPage = NextHeadersIndexSectionCount;
            }
        }
        pa.Dispose();
        
        _pages = pages.ToArray();

        return true;
    }

    public void Clear()
    {
        var cs = _manager.CreateChangeSet();
        for (int i = 0; i < Length; i++)
        {
            GetPageExclusiveAccessor(i, out PageAccessor pa);
            using (pa)
            {
                cs.Add(pa);
                pa.PageRawData.Clear();
            }
        }
        cs.SaveChanges();
    }

    public void Fill(byte value)
    {
        var cs = _manager.CreateChangeSet();
        for (int i = 0; i < Length; i++)
        {
            GetPageExclusiveAccessor(i, out var pa);
            using (pa)
            {
                cs.Add(pa);
                pa.LogicalSegmentData.Fill(value);
            }
        }
        cs.SaveChanges();
    }
}
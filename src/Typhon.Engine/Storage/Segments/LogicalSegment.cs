// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#pragma warning disable CS0728 // Possibly incorrect assignment to local which is the argument to a using or lock statement

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
[PublicAPI]
public class LogicalSegment : IDisposable
{
    internal const int RootHeaderIndexSectionCount = 500;
    internal const int RootHeaderIndexSectionLength = RootHeaderIndexSectionCount * sizeof(int);
    internal const int NextHeadersIndexSectionCount = PagedMMF.PageRawDataSize / sizeof(int);

    private readonly ManagedPagedMMF _manager;
    private readonly Lock _growLock = new();
    private volatile int[] _pages;

    public int RootPageIndex
    {
        get
        {
            var pages = _pages;
            if (pages == null || pages.Length == 0)
            {
                throw new InvalidOperationException("Logical segment has not been initialized.");
            }
            return pages[0];
        }
    }

    public int Length => _pages.Length;
    public ReadOnlySpan<int> Pages => _pages;
    public bool GetPageExclusiveAccessor(int segmentFilePageIndex, [TransfersOwnership] out PageAccessor result,
        long timeout = Timeout.Infinite, CancellationToken cancellationToken = default) 
        => _manager.RequestPage(Pages[segmentFilePageIndex], true, out result, timeout, cancellationToken);
    
    public bool GetPageSharedAccessor(int segmentFilePageIndex, [TransfersOwnership] out PageAccessor result,
        long timeout = Timeout.Infinite, CancellationToken cancellationToken = default) 
        => _manager.RequestPage(Pages[segmentFilePageIndex], false, out result, timeout, cancellationToken);

    public delegate bool PageWalkPredicate(int indexInSegment, ref PageAccessor accessor);
    public delegate bool PageMapWalkPredicate(int pageMapIndex, ref PageAccessor accessor);
    public delegate bool PageMapWalkPredicate<in T>(int pageMapIndex, ref PageAccessor accessor, T extra) where T : allows ref struct;

    public void WalkIndicesMap(PageMapWalkPredicate predicate, bool exclusiveAccess)
    {
        var pages = _pages;

        var curPageIndex = pages[0];
        var pageMapIndex = 0;
        while (true)
        {
            _manager.RequestPage(curPageIndex, exclusiveAccess, out var pa);
            using (pa)
            {
                if (predicate(pageMapIndex++, ref pa) == false)
                {
                    break;
                }

                ref var lsh = ref pa.GetHeader<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                curPageIndex = lsh.LogicalSegmentNextMapPBID;
                if (curPageIndex == 0)
                {
                    break;
                }
            }
        }
    }
    public void WalkIndicesMap<T>(PageMapWalkPredicate<T> predicate, bool exclusiveAccess, T extra) where T : allows ref struct
    {
        var pages = _pages;

        var curPageIndex = pages[0];
        var pageMapIndex = 0;
        while (true)
        {
            _manager.RequestPage(curPageIndex, exclusiveAccess, out var pa);
            using (pa)
            {
                if (predicate(pageMapIndex++, ref pa, extra) == false)
                {
                    break;
                }

                ref var lsh = ref pa.GetHeader<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                curPageIndex = lsh.LogicalSegmentNextMapPBID;
                if (curPageIndex == 0)
                {
                    break;
                }
            }
        }
    }
    
    /// <summary>
    /// Grows the logical segment to the specified new length.
    /// </summary>
    /// <param name="newLength">The new length (must be greater than current length).</param>
    /// <param name="clearNewPages">Whether to clear the content of newly allocated pages.</param>
    /// <param name="changeSet">Optional change set for tracking modifications.</param>
    /// <remarks>
    /// This method is thread-safe. Concurrent reads of existing pages remain valid during growth.
    /// The <see cref="_pages"/> field is volatile, ensuring visibility of the new array after growth.
    /// </remarks>
    public void Grow(int newLength, bool clearNewPages, ChangeSet changeSet = null)
    {
        lock (_growLock)
        {
            var curPages = _pages;
            if (curPages == null)
            {
                throw new InvalidOperationException("Logical segment has not been initialized.");
            }
            if (newLength <= curPages.Length)
            {
                // Already at or above requested size (may have been grown by another thread)
                return;
            }

            var newPages = new int[newLength];
            var newPagesAsSpan = newPages.AsSpan();
            curPages.CopyTo(newPagesAsSpan);
            _manager.AllocatePages(ref newPagesAsSpan, curPages.Length, changeSet);

            CreateOrGrow(PageBlockType.None, newPages, curPages.Length, ref NoNextMap, clearNewPages, changeSet);
        }
    }
    
    internal LogicalSegment(ManagedPagedMMF manager)
    {
        _manager = manager;
    }

    public void Dispose() => _pages = null;

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

    private static int NoNextMap;
    
    internal virtual bool Create(PageBlockType type, Span<int> filePageIndices, bool clear, ChangeSet changeSet = null) 
        => CreateOrGrow(type, filePageIndices, 0, ref NoNextMap, clear, changeSet);

    internal bool CreateOrGrow(PageBlockType type, Span<int> filePageIndices, int growFrom, ref int nextMap, bool clear, ChangeSet changeSet)
    {
        // Compute the number of indices map pages needed to store the indices (root + subsequent).
        // The end of the indices list is marked by a 0 value, we need to save space for this entry too, so the next line is accurate, if you wonder.
        var mapPageCount = 1 + ((filePageIndices.Length - RootHeaderIndexSectionCount + NextHeadersIndexSectionCount) / NextHeadersIndexSectionCount);
        
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
            // Start by building and/or allocating the indices pages, considering the growFrom parameter.
            Span<int> mapIndices = stackalloc int[mapPageCount];
            mapIndices[0] = filePageIndices[0];                         // The first page is always the root page, so we set it here.;
            var mapIndexAllocStartFrom = 0;
            
            if (mapPageCount > 1)
            {
                // Need to rebuild the indices pages
                if (growFrom > 0)
                {
                    WalkIndicesMap((i, ref accessor, span) =>
                    {
                        span[i] = accessor.FilePageIndex;
                        mapIndexAllocStartFrom = i + 1;                 // Update the start index for the first page to allocate
                        return true;
                    }, false, mapIndices);
                }
                
                // If a nextMap is provided, we need to use it as the first new map page
                var allocStartFrom = mapIndexAllocStartFrom;
                if ((nextMap != 0) && (allocStartFrom < mapIndices.Length))
                {
                    mapIndices[allocStartFrom++] = nextMap;
                    nextMap = 0;                                        // Signal the caller that we used the given nextMap
                }

                // Allocated the remaining indices pages using the allocator
                allocStartFrom = Math.Max(1, allocStartFrom);           // Ensure we start from the second page, as the first is always the root page
                if (allocStartFrom < mapIndices.Length)
                {
                    var pagesToAllocate = mapIndices[1..];
                    _manager.AllocatePages(ref pagesToAllocate, allocStartFrom - 1, changeSet);
                }
            }
            
            bool isFirstPage = true;
            var remainingIndices = filePageIndices.Length;
            var mapIndexBaseOffset = 0;
            var curIndexMapIndex = 0;
            var curFilePageIndex = 0;
            var curStartPageIndex = growFrom;
            
            while (remainingIndices > 0)
            {
                var curIndicesCount = Math.Min(remainingIndices, isFirstPage ? RootHeaderIndexSectionCount : NextHeadersIndexSectionCount);

                var isNewPage = (curIndexMapIndex >= mapIndexAllocStartFrom) && ((curIndexMapIndex > 0) || (growFrom == 0));
                var isLastAllocated = curIndexMapIndex == (mapIndexAllocStartFrom - 1);
                var curMapPageIndex = mapIndices[curIndexMapIndex];
                var hasAccessor = false;
                PageAccessor pa = default;
                var isPageDirty = false;

                // If it's a new page, initialize it
                if (isNewPage)
                {
                    _manager.RequestPage(curMapPageIndex, true, out pa);
                    hasAccessor = true;
                    
                    pa.InitHeader(
                        PageClearMode.Header, 
                        PageBlockFlags.IsLogicalSegment | (isFirstPage ? PageBlockFlags.IsLogicalSegmentRoot : PageBlockFlags.None), 
                        type, 1);
                    isPageDirty = true;
                }

                // Update the indices map linked list, starting the index map before the first to allocate
                if (isNewPage || isLastAllocated)
                {
                    if (hasAccessor == false)
                    {
                        _manager.RequestPage(curMapPageIndex, true, out pa);
                        hasAccessor = true;
                    }
                    ref var lsh = ref pa.GetHeader<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                    lsh.LogicalSegmentNextMapPBID = ((curIndexMapIndex + 1) < mapIndices.Length) ? mapIndices[curIndexMapIndex + 1] : 0;
                    isPageDirty = true;
                }

                // In the current map, set the page indices it contains
                if ((curStartPageIndex >= mapIndexBaseOffset) && (curStartPageIndex < (mapIndexBaseOffset + curIndicesCount)))
                {
                    if (hasAccessor == false)
                    {
                        _manager.RequestPage(curMapPageIndex, true, out pa);
                        hasAccessor = true;
                    }
                    
                    var rd = pa.PageRawData.Cast<byte, int>();
                    int j = curStartPageIndex - mapIndexBaseOffset;
                    curFilePageIndex += j;
                    for (; j < curIndicesCount; j++)
                    {
                        rd[j] = filePageIndices[curFilePageIndex++];
                    }
                    
                    if ((remainingIndices - curIndicesCount) == 0)
                    {
                        if (j < rd.Length)
                        {
                            rd[j] = 0;
                        }
                    
                        // The current page is full, we need on fetch one more... just to store the termination 0 value
                        else
                        {
                            _manager.RequestPage(mapIndices[curIndexMapIndex + 1], true, out var paEnd);
                            using (paEnd)
                            {
                                paEnd.InitHeader(PageClearMode.Header, PageBlockFlags.IsLogicalSegment, type, 1);
                                changeSet?.Add(paEnd);
                                paEnd.PageRawData.Cast<byte, int>()[0] = 0;
                            }
                        }
                    }
                    isPageDirty = true;
                }
                else
                {
                    curFilePageIndex += curIndicesCount;
                }

                mapIndexBaseOffset += curIndicesCount;
                remainingIndices -= curIndicesCount;

                // Slide the curStartPageIndex range to the next map page if we are after the growFrom index
                // In other words, keep the growFrom index if we didn't reach it yet
                if (curStartPageIndex < mapIndexBaseOffset)
                {
                    curStartPageIndex = mapIndexBaseOffset;
                }
                
                if (isPageDirty)
                {
                    changeSet?.Add(pa);
                }

                if (hasAccessor)
                {
                    pa.Dispose();
                }
                    
                isFirstPage = false;
                curIndexMapIndex++;
            }
        }
        
        // Initialize the subsequent pages on disk
        for (var i = growFrom; i < filePageIndices.Length; i++)
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
        _manager.RequestPage(filePageIndex, true, out var pa);
        var pages = new List<int>();
        var rd = pa.PageRawData.Cast<byte, int>()[..RootHeaderIndexSectionCount];
        var maxIndicesForPage = RootHeaderIndexSectionCount;
        var i = 0;
        while (rd[i] != 0)
        {
            pages.Add(rd[i]);

            if (++i != maxIndicesForPage)
            {
                continue;
            }

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
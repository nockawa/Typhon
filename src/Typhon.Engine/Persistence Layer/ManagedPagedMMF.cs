using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public class ManagedPagedMMFOptions : PagedMMFOptions
{
    
} 

[PublicAPI]
public partial class ManagedPagedMMF : PagedMMF
{
    #region Constants

    private const int OccupancySegmentRootPageIndex = 1;
    internal const string HeaderSignature = "TyphonDatabase";

    #endregion
    
    private ConcurrentDictionary<int, LogicalSegment> _segments;
    private LogicalSegment _occupancySegment;
    private BitmapL3 _occupancyMap;
    private int _occupancyNextReservedPage;

    public ManagedPagedMMF(IServiceProvider serviceProvider, PagedMMFOptions options, TimeManager timeManager, ILogger<PagedMMF> logger) : 
        base(serviceProvider, options, timeManager, logger)
    {
    }
    
    public void AllocatePages(ref Span<int> pageIds, ChangeSet changeSet = null)
    {
        lock (_occupancyMap)
        {
            Span<int> nextPage = stackalloc int[1];

            // Need to grow the occupancy segment if we run out of pages
            while (_occupancyMap.Allocate(ref pageIds, changeSet) == false)
            {
                // Will use _occupancyNextReservedPage to grow the segment of one page
                GrowOccupancySegment(changeSet);
                
                // Now that we can allocate many more pages, reserve the next page to be used when the occupancy map needs to grow again
                AllocatePages(ref nextPage, changeSet);
                _occupancyNextReservedPage = nextPage[0];
            }
        }
    }

    private void GrowOccupancySegment(ChangeSet changeSet)
    {
        
    }

    public bool FreePages(ReadOnlySpan<int> pages, ChangeSet changeSet = null)
    {
        lock (_occupancyMap)
        {
            _occupancyMap.Free(pages, changeSet);
        }

        return false;
    }

    unsafe protected override void OnFileCreating()
    {
        base.OnFileCreating();

        RequestPage(0, true, out var pa);
        using (pa)
        {
            // Set header information
            var cs = CreateChangeSet();
            cs.Add(pa);
            ref var rootFileHeader = ref pa.PageHeader.Cast<byte, RootFileHeader>()[0];
            fixed (byte* headerSignature = rootFileHeader.HeaderSignature)
            {
                StringExtensions.StoreString(HeaderSignature, headerSignature, 32);
            };
            rootFileHeader.DatabaseFormatRevision = DatabaseFormatRevision;
            fixed (byte* databaseName = rootFileHeader.DatabaseName)
            {
                StringExtensions.StoreString(Options.DatabaseName, databaseName, 64);
            }

            rootFileHeader.OccupancyMapSPI = OccupancySegmentRootPageIndex;
            Logger.LogInformation("Initialize DiskPageAllocator service with root at page {PageId}", OccupancySegmentRootPageIndex);
            
            // Initialize the occupancy segment and map
            _segments = new ConcurrentDictionary<int, LogicalSegment>();

            _occupancySegment = CreateOccupancySegment(OccupancySegmentRootPageIndex, PageBlockType.OccupancyMap, 1, cs);

            // ReSharper disable InconsistentlySynchronizedField
            _occupancyMap = new BitmapL3(1, _occupancySegment);

            // The first two pages are already manually allocated (file header and occupancy segment root page)
            _occupancyMap.SetL0(0);
            _occupancyMap.SetL0(1);
            
            // Reserve a page to use when the occupancy map needs to grow
            _occupancyNextReservedPage = 2;
            _occupancyMap.SetL0(_occupancyNextReservedPage);
            // ReSharper restore InconsistentlySynchronizedField
            
            cs.SaveChanges();
        }
    }

    unsafe protected override void OnFileLoading()
    {
        base.OnFileLoading();
        RequestPage(0, false, out var pa);
        using (pa)
        {
            ref var h = ref pa.PageHeader.Cast<byte, RootFileHeader>()[0];

            if (h.HeaderSignatureString != HeaderSignature)
            {
                throw new NotImplementedException();
            }

            if (h.DatabaseNameString != Options.DatabaseName)
            {
                throw new NotImplementedException();
            }
            
            Logger.LogInformation("Load Database '{DatabaseName}' from file '{FilePathName}'", h.DatabaseNameString, Options.BuildDatabasePathFileName());

            // Initialize the occupancy segment and map
            _segments = new ConcurrentDictionary<int, LogicalSegment>();

            _occupancySegment = LoadOccupancySegment(h.OccupancyMapSPI, PageBlockType.OccupancyMap);

            // ReSharper disable once InconsistentlySynchronizedField
            _occupancyMap = new BitmapL3(1, _occupancySegment);
        }
    }
    public LogicalSegment GetSegment(int filePageIndex)
    {
        var dic = _segments;
        return dic?.GetOrAdd(filePageIndex, fpid =>
        {
            var segment = new LogicalSegment(this);
            segment.Load(fpid);
            return segment;
        });
    }

    internal LogicalSegment CreateOccupancySegment(int filePageIndex, PageBlockType type, int length, ChangeSet cs)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var segment = new LogicalSegment(this);
        if (dic.TryAdd(filePageIndex, segment) == false)
        {
            return null;
        }

        if (segment.Create(type, filePageIndex, true, cs) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    internal LogicalSegment LoadOccupancySegment(int filePageIndex, PageBlockType type)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var segment = new LogicalSegment(this);
        if (dic.TryAdd(filePageIndex, segment) == false)
        {
            return null;
        }

        if (segment.Load(filePageIndex) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    public LogicalSegment AllocateSegment(PageBlockType type, int length, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var pages = (length < 64) ? stackalloc int[length] : new int[length];
        AllocatePages(ref pages, changeSet);

        var segment = new LogicalSegment(this);
        if (dic.TryAdd(pages[0], segment) == false)
        {
            Debug.Assert(true);
        }

        if (segment.Create(type, pages, false, changeSet) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    protected override void OnDispose()
    {
        if (IsDisposed)
        {
            return;
        }
        
        var dic = Interlocked.Exchange(ref _segments, null);
        if (dic != null)
        {
            foreach (var segment in dic.Values)
            {
                segment.Dispose();
            }
        }
        
        base.OnDispose();
    }

    public ChunkBasedSegment AllocateChunkBasedSegment(PageBlockType type, int length, int stride, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        Span<int> pages = stackalloc int[length];
        AllocatePages(ref pages, changeSet);

        var segment = new ChunkBasedSegment(this, stride);
        if (dic.TryAdd(pages[0], segment) == false)
        {
            Debug.Assert(true);
        }

        if (segment.Create(type, pages, false, changeSet) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }
    
    public ChunkBasedSegment LoadChunkBasedSegment(int filePageIndex, int stride)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var segment = new ChunkBasedSegment(this, stride);
        if (dic.TryAdd(filePageIndex, segment) == false)
        {
            Debug.Assert(true);
        }

        if (segment.Load(filePageIndex) == false)
        {
            return null;
        }

        Logger.LogDebug("Load Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    public bool DeleteSegment(int filePageIndex, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return false;
        }

        if (dic.TryRemove(filePageIndex, out var segment) == false)
        {
            return false;
        }

        FreePages(segment.Pages, changeSet);
        return true;
    }

    public bool DeleteSegment(LogicalSegment segment, ChangeSet changeSet = null) => DeleteSegment(segment.RootPageIndex, changeSet);
}
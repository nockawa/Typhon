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

    public ManagedPagedMMF(IServiceProvider serviceProvider, PagedMMFOptions options, TimeManager timeManager, ILogger<PagedMMF> logger) : 
        base(serviceProvider, options, timeManager, logger)
    {
    }
    
    public void AllocatePages(ref Span<int> pageIds)
    {
        lock (_occupancyMap)
        {
            _occupancyMap.Allocate(ref pageIds);
        }
    }

    public bool FreePages(ReadOnlySpan<int> pages)
    {
        lock (_occupancyMap)
        {
            _occupancyMap.Free(pages);
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
            var rootFileHeader = (RootFileHeader*)pa.PageAddress;
            StringExtensions.StoreString(HeaderSignature, rootFileHeader->HeaderSignature, 32);
            rootFileHeader->DatabaseFormatRevision = DatabaseFormatRevision;
            StringExtensions.StoreString(Options.DatabaseName, rootFileHeader->DatabaseName, 64);

            rootFileHeader->OccupancyMapSPI = OccupancySegmentRootPageIndex;
            Logger.LogInformation("Initialize DiskPageAllocator service with root at page {PageId}", OccupancySegmentRootPageIndex);

            cs.SaveChanges();
            
            // Initialize the occupancy segment and map
            _segments = new ConcurrentDictionary<int, LogicalSegment>();

            _occupancySegment = CreateOccupancySegment(OccupancySegmentRootPageIndex, PageBlockType.OccupancyMap, 1);

            // ReSharper disable InconsistentlySynchronizedField
            _occupancyMap = new BitmapL3(1, _occupancySegment);

            // The first two pages are already manually allocated
            _occupancyMap.SetL0(0);
            _occupancyMap.SetL0(1);
            // ReSharper restore InconsistentlySynchronizedField
        }
    }

    unsafe protected override void OnFileLoading()
    {
        base.OnFileLoading();
        RequestPage(0, false, out var pa);
        using (pa)
        {
            var h = (RootFileHeader*)pa.PageAddress;

            if (h->HeaderSignatureString != HeaderSignature)
            {
                throw new NotImplementedException();
            }

            if (h->DatabaseNameString != Options.DatabaseName)
            {
                throw new NotImplementedException();
            }
            
            Logger.LogInformation("Load Database '{DatabaseName}' from file '{FilePathName}'", h->DatabaseNameString, Options.BuildDatabasePathFileName());

            // Initialize the occupancy segment and map
            _segments = new ConcurrentDictionary<int, LogicalSegment>();

            _occupancySegment = LoadOccupancySegment(h->OccupancyMapSPI, PageBlockType.OccupancyMap);

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

    internal LogicalSegment CreateOccupancySegment(int filePageIndex, PageBlockType type, int length)
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

        if (segment.Create(type, filePageIndex, true) == false)
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

    public LogicalSegment AllocateSegment(PageBlockType type, int length)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        var pages = (length < 64) ? stackalloc int[length] : new int[length];
        AllocatePages(ref pages);

        var segment = new LogicalSegment(this);
        if (dic.TryAdd(pages[0], segment) == false)
        {
            Debug.Assert(true);
        }

        if (segment.Create(type, pages, false) == false)
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

    public ChunkBasedSegment AllocateChunkBasedSegment(PageBlockType type, int length, int stride)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        Span<int> pages = stackalloc int[length];
        AllocatePages(ref pages);

        var segment = new ChunkBasedSegment(this, stride);
        if (dic.TryAdd(pages[0], segment) == false)
        {
            Debug.Assert(true);
        }

        if (segment.Create(type, pages, false) == false)
        {
            return null;
        }

        Logger.LogDebug("Create Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    public bool DeleteSegment(int filePageIndex)
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

        /*
        _pmmf.FreePages(segment.Pages);
        */
        return true;
    }

    public bool DeleteSegment(LogicalSegment segment) => DeleteSegment(segment.RootPageIndex);
}
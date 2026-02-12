using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential)]
unsafe internal struct RootFileHeader
{
    public fixed byte HeaderSignature[32];
    public int DatabaseFormatRevision;
    public ulong DatabaseFilesChunkSize;
    public fixed byte DatabaseName[64];
    public int OccupancyMapSPI;
    public int SystemSchemaRevision;
    public int ComponentTableSPI;
    public int FieldTableSPI;

    public string HeaderSignatureString
    {
        get
        {
            fixed (byte* s = HeaderSignature)
            {
                return StringExtensions.LoadString(s);
            }
        }
    }
    public string DatabaseNameString
    {
        get
        {
            fixed (byte* s = DatabaseName)
            {
                return StringExtensions.LoadString(s);
            }
        }
    }
}

[PublicAPI]
public class ManagedPagedMMFOptions : PagedMMFOptions
{
}

// ============================================================================================================================================================
// Pages of an empty file
// ------------------------------------------------------------------------------------------------------------------------------------------------------------
// 0: Root file header
// 1: Occupancy segment root page
// 2: Reserved page for occupancy map growth
// 3: Reserved page for occupancy map next map data (in case we need more than 500 pages to store the occupancy map)
// ============================================================================================================================================================

/// <summary>
/// Memory-mapped file manager with page allocation, segment management, and occupancy tracking.
/// </summary>
/// <remarks>
/// <para>
/// ManagedPagedMMF registers itself under the <see cref="ResourceSubsystem.Storage"/> subsystem
/// in the resource tree. It is typically the storage backend for a <see cref="DatabaseEngine"/>.
/// </para>
/// </remarks>
[PublicAPI]
public partial class ManagedPagedMMF : PagedMMF, IMetricSource, IContentionTarget, IDebugPropertiesProvider
{
    #region Constants

    internal const int InitialReservedPageCount = 4;
    private const int OccupancySegmentRootPageIndex = 1;
    internal const string HeaderSignature = "TyphonDatabase";

    #endregion

    private ConcurrentDictionary<int, LogicalSegment> _segments;
    private LogicalSegment _occupancySegment;
    private BitmapL3 _occupancyMap;
    private int _occupancyNextReservedPageIndex;
    private int _occupancyNextReservedMapPageIndex;

    // Synchronization for occupancy map operations (replaces lock(_occupancyMap))
    private AccessControl _occupancyMapAccess;

    // Contention tracking
    private long _contentionWaitCount;
    private long _contentionTotalWaitUs;
    private long _contentionMaxWaitUs;

    // Throughput counters (supplement inherited _metrics)
    private long _evictionCount;

    public ManagedPagedMMF(IResourceRegistry resourceRegistry, EpochManager epochManager, IMemoryAllocator memoryAllocator, PagedMMFOptions options,
        IResource parent, string resourceName, ILogger<PagedMMF> logger) :
        base(memoryAllocator, epochManager, options, parent, $"ManagedPagedMMF_{options?.DatabaseName ?? Guid.NewGuid().ToString("N")}", logger)
    {
    }

    public int AllocatePage(ChangeSet changeSet = null)
    {
        Span<int> pageId = stackalloc int[1];
        AllocatePages(ref pageId, 0, changeSet);
        return pageId[0];
    }

    public void AllocatePages(ref Span<int> pageIds, int startFrom = 0, ChangeSet changeSet = null)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!_occupancyMapAccess.EnterExclusiveAccess(ref wc, target: this))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/AllocatePages", TimeoutOptions.Current.PageCacheLockTimeout);
        }
        try
        {
            AllocatePagesCore(ref pageIds, startFrom, changeSet);
        }
        finally
        {
            _occupancyMapAccess.ExitExclusiveAccess();
        }
    }

    // Core allocation logic - caller must hold _occupancyMapAccess exclusive lock
    private void AllocatePagesCore(ref Span<int> pageIds, int startFrom, ChangeSet changeSet)
    {
        // Need to grow the occupancy segment if we run out of pages
        while (_occupancyMap.Allocate(ref pageIds, startFrom, changeSet) == false)
        {
            // Will use _occupancyNextReservedPage to grow the segment of one page
            GrowOccupancySegment(changeSet);

            // Now that we can allocate many more pages, reserve the next page to be used when the occupancy map needs to grow again
            // Use core method directly to avoid deadlock (we already hold the lock)
            _occupancyNextReservedPageIndex = AllocatePageCore(changeSet);
        }
    }

    // Core single-page allocation - caller must hold _occupancyMapAccess exclusive lock
    private int AllocatePageCore(ChangeSet changeSet)
    {
        Span<int> pageId = stackalloc int[1];
        AllocatePagesCore(ref pageId, 0, changeSet);
        return pageId[0];
    }

    // Under lock of caller
    private void GrowOccupancySegment(ChangeSet changeSet)
    {
        // Note: adding one page will allow to track 8000 * 8 more pages which is 500MiB of data stored in the file
        var length = _occupancySegment.Length + 1;
        var pages = (length < 64) ? stackalloc int[length] : new int[length];
        _occupancySegment.Pages.CopyTo(pages);
        pages[length - 1] = _occupancyNextReservedPageIndex;

        _occupancySegment.CreateOrGrow(PageBlockType.OccupancyMap, pages, length - 1, ref _occupancyNextReservedMapPageIndex, true, changeSet);
        _occupancyMap.Grow();
        
        // If CreateOrGrow uses the reserved page for map extension, the value after the call is 0, so we need to allocate a new one
        if (_occupancyNextReservedMapPageIndex == 0)
        {
            _occupancyNextReservedMapPageIndex = AllocatePage();
        }
    }

    public bool FreePages(ReadOnlySpan<int> pages, int startFrom = 0, ChangeSet changeSet = null)
    {
        _occupancyMapAccess.EnterExclusiveAccess(ref WaitContext.Null, target: this);
        try
        {
            _occupancyMap.Free(pages, startFrom, changeSet);
        }
        finally
        {
            _occupancyMapAccess.ExitExclusiveAccess();
        }

        return false;
    }

    unsafe protected override void OnFileCreating()
    {
        base.OnFileCreating();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = EpochManager.GlobalEpoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        TryLatchPageExclusive(memPageIdx);
        var addr = GetMemPageAddress(memPageIdx);

        // Set header information
        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);
        ref var rootFileHeader = ref System.Runtime.CompilerServices.Unsafe.AsRef<RootFileHeader>(addr);
        fixed (byte* headerSignature = rootFileHeader.HeaderSignature)
        {
            StringExtensions.StoreString(HeaderSignature, headerSignature, 32);
        }
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
        _occupancyMap = new BitmapL3(_occupancySegment);

        // The first two pages are already manually allocated (file header and occupancy segment root page)
        _occupancyMap.SetL0(0);
        _occupancyMap.SetL0(1);

        // Reserve a pages to use when the occupancy map needs to grow, we need to reserve because we can't allocate them by the time the map is full
        _occupancyNextReservedPageIndex = 2;
        _occupancyNextReservedMapPageIndex = 3;
        _occupancyMap.SetL0(_occupancyNextReservedPageIndex);
        // ReSharper restore InconsistentlySynchronizedField

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();
    }

    unsafe protected override void OnFileLoading()
    {
        base.OnFileLoading();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = EpochManager.GlobalEpoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var addr = GetMemPageAddress(memPageIdx);
        ref var h = ref System.Runtime.CompilerServices.Unsafe.AsRef<RootFileHeader>(addr);

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
        _occupancyMap = new BitmapL3(_occupancySegment);
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
        AllocatePages(ref pages, 0, changeSet);

        var segment = new LogicalSegment(this);
        if (dic.TryAdd(pages[0], segment) == false)
        {
            Debug.Assert(true);
        }

        if (segment.Create(type, pages, false, changeSet) == false)
        {
            return null;
        }

        //Logger.LogDebug("Create Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            var dic = Interlocked.Exchange(ref _segments, null);
            if (dic != null)
            {
                foreach (var segment in dic.Values)
                {
                    segment.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }

    public ChunkBasedSegment AllocateChunkBasedSegment(PageBlockType type, int length, int stride, ChangeSet changeSet = null)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        Span<int> pages = stackalloc int[length];
        AllocatePages(ref pages, 0, changeSet);

        var segment = new ChunkBasedSegment(EpochManager, this, stride);
        if (!dic.TryAdd(pages[0], segment))
        {
            Debug.Assert(true);
        }

        if (!segment.Create(type, pages, false, changeSet))
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

        var segment = new ChunkBasedSegment(EpochManager, this, stride);
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

        FreePages(segment.Pages, 0, changeSet);
        return true;
    }

    public bool DeleteSegment(LogicalSegment segment, ChangeSet changeSet = null) => DeleteSegment(segment.RootPageIndex, changeSet);

    #region IMetricSource Implementation

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        var metrics = GetMetrics();

        // Memory: page cache buffer size
        long allocatedBytes = MemPages?.EstimatedMemorySize ?? 0;
        writer.WriteMemory(allocatedBytes, allocatedBytes);

        // Capacity: free vs total memory pages
        long freePages = metrics.FreeMemPageCount;
        long totalPages = MemPagesCount;
        writer.WriteCapacity(totalPages - freePages, totalPages);

        // DiskIO: read/write operations
        writer.WriteDiskIO(
            metrics.ReadFromDiskCount,
            metrics.PageWrittenToDiskCount,
            (long)metrics.ReadFromDiskCount * PageSize,
            (long)metrics.PageWrittenToDiskCount * PageSize);

        // Contention
        writer.WriteContention(_contentionWaitCount, _contentionTotalWaitUs, _contentionMaxWaitUs, 0);

        // Throughput
        writer.WriteThroughput("CacheHits", metrics.MemPageCacheHit);
        writer.WriteThroughput("CacheMisses", metrics.MemPageCacheMiss);
        writer.WriteThroughput("Evictions", _evictionCount);
    }

    /// <inheritdoc />
    public void ResetPeaks() => _contentionMaxWaitUs = 0;

    #endregion

    #region IContentionTarget Implementation

    /// <inheritdoc />
    public TelemetryLevel TelemetryLevel => TelemetryLevel.Light;

    /// <inheritdoc />
    public IResource OwningResource => this;

    /// <inheritdoc />
    public void RecordContention(long waitUs)
    {
        Interlocked.Increment(ref _contentionWaitCount);
        Interlocked.Add(ref _contentionTotalWaitUs, waitUs);

        if (waitUs > _contentionMaxWaitUs)
        {
            _contentionMaxWaitUs = waitUs;
        }
    }

    /// <inheritdoc />
    public void LogLockOperation(LockOperation operation, long durationUs)
    {
        // Deep telemetry not implemented for this resource - Light mode only
    }

    #endregion

    #region IDebugPropertiesProvider Implementation

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var metrics = GetMetrics();
        metrics.GetMemPageExtraInfo(out var extraInfo);

        return new Dictionary<string, object>
        {
            ["PageCache.FreeCount"]             = extraInfo.FreeMemPageCount,
            ["PageCache.AllocatingCount"]       = extraInfo.AllocatingMemPageCount,
            ["PageCache.IdleCount"]             = extraInfo.IdleMemPageCount,
            ["PageCache.ExclusiveCount"]        = extraInfo.ExclusiveMemPageCount,
            ["PageCache.DirtyCount"]            = extraInfo.DirtyPageCount,
            ["PageCache.PendingIOReadCount"]    = extraInfo.PendingIOReadCount,
            ["ClockSweep.MinCounter"]           = extraInfo.MinClockSweepCounter,
            ["ClockSweep.MaxCounter"]           = extraInfo.MaxClockSweepCounter,
            ["Segments.Count"]                  = _segments?.Count ?? 0,
            ["OccupancyMap.Capacity"]           = _occupancyMap?.Capacity ?? 0,
            ["Contention.WaitCount"]            = _contentionWaitCount,
            ["Contention.TotalWaitUs"]          = _contentionTotalWaitUs,
            ["Contention.MaxWaitUs"]            = _contentionMaxWaitUs,
        };
    }

    #endregion
}
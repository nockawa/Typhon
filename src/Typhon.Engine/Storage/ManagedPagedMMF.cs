using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// On-disk header stored in the metadata zone (bytes 64–191) of page 0 of every Typhon database file.
/// Identifies the file format, tracks the database name and format version,
/// and holds the root page indices (SPIs) of the core system segments.
/// </summary>
/// <remarks>
/// Page 0 has a standard <see cref="PageBaseHeader"/> at bytes 0–63 (managed by the infrastructure
/// for seqlock, checksum, and change tracking). The <see cref="RootFileHeader"/> is placed immediately
/// after, at offset <see cref="PagedMMF.PageBaseHeaderSize"/> (64), using
/// <c>page.StructAt&lt;RootFileHeader&gt;(PagedMMF.PageBaseHeaderSize)</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
unsafe internal struct RootFileHeader
{
    /// <summary>UTF-8 magic string (<c>"TyphonDatabase"</c>) used to identify and validate the file format.</summary>
    public fixed byte HeaderSignature[32];

    /// <summary>On-disk format version. Incremented on breaking layout changes to detect incompatible files.</summary>
    public int DatabaseFormatRevision;

    /// <summary>Chunk size (in bytes) used when growing the underlying database files.</summary>
    public ulong DatabaseFilesChunkSize;

    /// <summary>UTF-8 database name (max 64 bytes). Verified on load to prevent opening the wrong file.</summary>
    public fixed byte DatabaseName[64];

    /// <summary>Root page index of the occupancy-map segment (page allocation bitmap).</summary>
    public int OccupancyMapSPI;

    /// <summary>Revision counter for the built-in system schema (component/field table layout).</summary>
    public int SystemSchemaRevision;

    /// <summary>Root page index of the <see cref="ComponentTable"/> segment.</summary>
    public int ComponentTableSPI;

    /// <summary>Root page index of the field-table segment (component field metadata).</summary>
    public int FieldTableSPI;

    /// <summary>Root page index of the <see cref="UowRegistry"/> segment (Unit of Work tracking).</summary>
    public int UowRegistrySPI;

    /// <summary>LSN up to which all WAL records have been checkpointed. Default 0 = scan all WAL segments on recovery.</summary>
    public long CheckpointLSN;

    /// <summary>Pre-allocated page index for the next occupancy map growth.</summary>
    public int OccupancyNextReservedPageIndex;

    /// <summary>Pre-allocated page index for the next occupancy map data page.</summary>
    public int OccupancyNextReservedMapPageIndex;

    // ── Additional system table SPIs (appended to preserve existing offsets) ──

    /// <summary>Root page index of the CompRevTable segment for the FieldR1 system table.</summary>
    public int FieldTableVersionSPI;

    /// <summary>Root page index of the DefaultIndex segment for the FieldR1 system table.</summary>
    public int FieldTableDefaultIndexSPI;

    /// <summary>Root page index of the String64Index segment for the FieldR1 system table.</summary>
    public int FieldTableString64IndexSPI;

    /// <summary>Root page index of the CompRevTable segment for the ComponentR1 system table.</summary>
    public int ComponentTableVersionSPI;

    /// <summary>Root page index of the DefaultIndex segment for the ComponentR1 system table.</summary>
    public int ComponentTableDefaultIndexSPI;

    /// <summary>Root page index of the String64Index segment for the ComponentR1 system table.</summary>
    public int ComponentTableString64IndexSPI;

    /// <summary>Next free Transaction Sequence Number. Restored on reopen so MVCC visibility works across engine restarts.</summary>
    public long NextFreeTSN;

    /// <summary>Root page index of the <see cref="ChunkBasedSegment"/> backing <see cref="ComponentCollection{T}"/> storage for FieldR1 entries.</summary>
    public int FieldCollectionSegmentSPI;

    /// <summary>Monotonic counter bumped on any user component schema change. Used for quick mismatch pre-check.</summary>
    public int UserSchemaVersion;

    // ── Schema History system table SPIs (Phase 5, appended to preserve existing offsets) ──

    /// <summary>Root page index of the SchemaHistoryR1 component segment.</summary>
    public int SchemaHistoryTableSPI;

    /// <summary>Root page index of the CompRevTable segment for SchemaHistoryR1.</summary>
    public int SchemaHistoryVersionSPI;

    /// <summary>Root page index of the DefaultIndex segment for SchemaHistoryR1.</summary>
    public int SchemaHistoryDefaultIndexSPI;

    /// <summary>Root page index of the String64Index segment for SchemaHistoryR1.</summary>
    public int SchemaHistoryString64IndexSPI;

    /// <summary>Returns <see cref="HeaderSignature"/> decoded as a managed string.</summary>
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

    /// <summary>Returns <see cref="DatabaseName"/> decoded as a managed string.</summary>
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

    /// <summary>Maximum number of file pages the occupancy bitmap can track (current capacity).</summary>
    public int OccupancyCapacityPages => _occupancyMap?.Capacity ?? 0;

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

            // Persist the updated reserved page indices to the root file header
            UpdateOccupancyReservedPages();
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
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during file creation");
        var page = GetPage(memPageIdx);

        // Set header information
        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);
        ref var rootFileHeader = ref page.StructAt<RootFileHeader>(PageBaseHeaderSize);
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

        // Reserve pages to use when the occupancy map needs to grow, we need to reserve because we can't allocate them by the time the map is full
        _occupancyNextReservedPageIndex = 2;
        _occupancyNextReservedMapPageIndex = 3;
        _occupancyMap.SetL0(_occupancyNextReservedPageIndex);
        _occupancyMap.SetL0(_occupancyNextReservedMapPageIndex);
        // ReSharper restore InconsistentlySynchronizedField

        rootFileHeader.OccupancyNextReservedPageIndex = _occupancyNextReservedPageIndex;
        rootFileHeader.OccupancyNextReservedMapPageIndex = _occupancyNextReservedMapPageIndex;

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();
        FlushToDisk();
    }

    protected override void OnFileLoading()
    {
        base.OnFileLoading();

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var page = GetPage(memPageIdx);
        ref var h = ref page.StructAt<RootFileHeader>(PageBaseHeaderSize);

        if (h.HeaderSignatureString != HeaderSignature)
        {
            throw new InvalidOperationException(
                $"Invalid database file: expected header signature '{HeaderSignature}', found '{h.HeaderSignatureString}'. File: {Options.BuildDatabasePathFileName()}");
        }

        if (h.DatabaseNameString != Options.DatabaseName)
        {
            throw new InvalidOperationException(
                $"Database name mismatch: expected '{Options.DatabaseName}', found '{h.DatabaseNameString}'. File: {Options.BuildDatabasePathFileName()}");
        }

        if (h.DatabaseFormatRevision != DatabaseFormatRevision)
        {
            throw new InvalidOperationException(
                $"Incompatible database format: file version {h.DatabaseFormatRevision}, engine version {DatabaseFormatRevision}. File: {Options.BuildDatabasePathFileName()}");
        }

        Logger.LogInformation("Load Database '{DatabaseName}' from file '{FilePathName}'", h.DatabaseNameString, Options.BuildDatabasePathFileName());

        // Initialize the occupancy segment and map
        _segments = new ConcurrentDictionary<int, LogicalSegment>();

        _occupancySegment = LoadOccupancySegment(h.OccupancyMapSPI, PageBlockType.OccupancyMap);

        // ReSharper disable InconsistentlySynchronizedField
        _occupancyMap = new BitmapL3(_occupancySegment);

        _occupancyNextReservedPageIndex = h.OccupancyNextReservedPageIndex;
        _occupancyNextReservedMapPageIndex = h.OccupancyNextReservedMapPageIndex;
        // ReSharper restore InconsistentlySynchronizedField
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
        if (!dic.TryAdd(pages[0], segment))
        {
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
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
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
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
            Debug.Fail("Segment root page already registered in dictionary — duplicate allocation");
        }

        if (segment.Load(filePageIndex) == false)
        {
            return null;
        }

        Logger.LogDebug("Load Chunk Based Logical Segment at {StartPageId} using pages {Pages}", segment.Pages[0], segment.Pages.ToArray());
        return segment;
    }

    /// <summary>
    /// Returns a previously loaded segment for the given page index, or loads it if not yet present.
    /// Safe to call when the segment may already be in the registry (e.g., system component segments loaded by the engine constructor).
    /// </summary>
    public ChunkBasedSegment GetOrLoadChunkBasedSegment(int filePageIndex, int stride)
    {
        var dic = _segments;
        if (dic == null)
        {
            return null;
        }

        if (dic.TryGetValue(filePageIndex, out var existing))
        {
            return existing as ChunkBasedSegment;
        }

        return LoadChunkBasedSegment(filePageIndex, stride);
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

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint Support
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Persists the current <see cref="_occupancyNextReservedPageIndex"/> and <see cref="_occupancyNextReservedMapPageIndex"/> values
    /// to the <see cref="RootFileHeader"/> on page 0. Called after the occupancy map grows and new reserved pages are allocated.
    /// </summary>
    /// <remarks>Caller must hold <see cref="_occupancyMapAccess"/> exclusive lock.</remarks>
    private void UpdateOccupancyReservedPages()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during occupancy reserved pages update");

        var page = GetPage(memPageIdx);
        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        ref var header = ref page.StructAt<RootFileHeader>(PageBaseHeaderSize);
        header.OccupancyNextReservedPageIndex = _occupancyNextReservedPageIndex;
        header.OccupancyNextReservedMapPageIndex = _occupancyNextReservedMapPageIndex;

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();
    }

    /// <summary>
    /// Updates the <see cref="RootFileHeader.CheckpointLSN"/> field in page 0 and flushes to disk. Called by the Checkpoint Manager after dirty pages have
    /// been written and fsynced.
    /// </summary>
    /// <param name="checkpointLSN">The new checkpoint LSN to persist.</param>
    /// <param name="epochManager">Epoch manager for page access.</param>
    internal void UpdateCheckpointLSN(long checkpointLSN, EpochManager epochManager)
    {
        using var guard = EpochGuard.Enter(epochManager);
        var epoch = guard.Epoch;

        RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during checkpoint LSN update");

        var page = GetPage(memPageIdx);
        var cs = CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        ref var header = ref page.StructAt<RootFileHeader>(PageBaseHeaderSize);
        header.CheckpointLSN = checkpointLSN;

        UnlatchPageExclusive(memPageIdx);
        cs.SaveChanges();

        // Fsync to make the checkpoint LSN durable
        FlushToDisk();
    }

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
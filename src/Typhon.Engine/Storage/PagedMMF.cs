using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

[PublicAPI]
public partial class PagedMMF : ResourceNode, IMemoryResource
{
    public const int DefaultMemPageCount = 256;

    #region Events

    internal event EventHandler CreatingEvent;
    internal event EventHandler LoadingEvent;

    #endregion

    #region Constants

    internal const int PageHeaderSize           = 192;                                  // Base Header + Metadata
    internal const int PageBaseHeaderSize       = 64;
    internal const int PageMetadataSize         = 128;
    internal const int PageSize                 = 8192;                                 // Base Header + Metadata + RawData
    internal const int PageRawDataSize          = PageSize - PageHeaderSize;
    internal const int PageSizePow2             = 13;                                   // 2^( PageSizePow2 = PageSize
    internal const int DatabaseFormatRevision   = 1;
    internal const ulong MinimumCacheSize       = DefaultMemPageCount * PageSize;
    internal const int WriteCachePageSize       = 1024 * 1024;

    #endregion

    #region Debug Info


    [ExcludeFromCodeCoverage]
    private void GetMemPageExtraInfo(out Metrics.MemPageExtraInfo res)
    {
        int free = 0;
        int allocating = 0;
        int idleCount = 0;
        int exclusiveCount = 0;
        int dirtyCount = 0;
        int lockedByThreadCount = 0;
        int pendingIOReadCount = 0;
        int minClockSweepCounter = int.MaxValue;
        int maxClockSweepCounter = int.MinValue;

        foreach (var pi in _memPagesInfo)
        {
            switch (pi.PageState)
            {
                case PageState.Free:
                    free++;
                    break;
                case PageState.Allocating:
                    allocating++;
                    break;
                case PageState.Idle:
                    idleCount++;
                    break;
                case PageState.Exclusive:
                    exclusiveCount++;
                    break;
            }
            if (pi.DirtyCounter > 0)
            {
                dirtyCount++;
            }
            if (pi.PageExclusiveLatch.LockedByThreadId != 0)
            {
                lockedByThreadCount++;
            }
            if (pi.IOReadTask != null && pi.IOReadTask.IsCompleted == false)
            {
                pendingIOReadCount++;
            }
            if (pi.ClockSweepCounter < minClockSweepCounter)
            {
                minClockSweepCounter = pi.ClockSweepCounter;
            }
            if (pi.ClockSweepCounter > maxClockSweepCounter)
            {
                maxClockSweepCounter = pi.ClockSweepCounter;
            }
        }

        res = new Metrics.MemPageExtraInfo
        {
            FreeMemPageCount = free,
            AllocatingMemPageCount = allocating,
            IdleMemPageCount = idleCount,
            ExclusiveMemPageCount = exclusiveCount,
            DirtyPageCount = dirtyCount,
            LockedByThreadCount = lockedByThreadCount,
            PendingIOReadCount = pendingIOReadCount,
            MinClockSweepCounter = minClockSweepCounter,
            MaxClockSweepCounter = maxClockSweepCounter
        };
    }

    private Metrics _metrics;

    internal Metrics GetMetrics() => _metrics;

    #endregion

    internal enum PageState : ushort
    {
        Free         = 0,   // The page is free, yet to be allocated.
        Allocating   = 1,   // The page is being allocating by a call to AllocateMemoryPage.
        Idle         = 2,   // The page is allocated but idle. Protected from eviction by epoch tag and/or DirtyCounter > 0.
        Exclusive    = 4,   // The page is allocated and accessed exclusively by a given thread via PageExclusiveLatch.
    }

    protected readonly PagedMMFOptions Options;
    protected readonly ILogger<PagedMMF> Logger;
    
    protected readonly PinnedMemoryBlock MemPages;
    private unsafe byte* _memPagesAddr;

    protected readonly int MemPagesCount;
    private int _clockSweepCurrentIndex;
    private PageInfo[] _memPagesInfo;
    
    private SafeFileHandle _fileHandle;
    private long _fileSize;

    /// <summary>
    /// Atomically advances <see cref="_fileSize"/> to at least <paramref name="newSize"/>.
    /// No-op if the tracked size is already &gt;= <paramref name="newSize"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackFileGrowth(long newSize)
    {
        long oldSize;
        do
        {
            oldSize = _fileSize;
            if (newSize <= oldSize)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _fileSize, newSize, oldSize) != oldSize);
    }

    private readonly ConcurrentDictionary<int, int> _memPageIndexByFilePageIndex;
    public EpochManager EpochManager { get; private set; }

    // FPI (Full-Page Image) support — null until EnableFpiCapture() is called (WAL disabled = no FPI)
    private FpiBitmap _fpiBitmap;
    private WalManager _walManager;
    private bool _enableFpiCompression;

    // CRC verification mode — defaults to RecoveryOnly to avoid on-load checks during recovery itself.
    // Set to OnLoad after recovery completes via SetPageChecksumVerification().
    private PageChecksumVerification _pageChecksumVerification = PageChecksumVerification.RecoveryOnly;

    /// <summary>
    /// Sets the page CRC verification mode. Called by <see cref="DatabaseEngine"/> after recovery completes
    /// to enable on-load verification during normal operation.
    /// </summary>
    internal void SetPageChecksumVerification(PageChecksumVerification mode) => _pageChecksumVerification = mode;

    /// <summary>
    /// Enables FPI capture by creating an <see cref="FpiBitmap"/> sized to the page cache and linking to the WAL manager.
    /// Called by <see cref="DatabaseEngine"/> after WAL initialization.
    /// </summary>
    /// <param name="walManager">The WAL manager to write FPI records to.</param>
    /// <param name="enableFpiCompression">When true, FPI page payloads are LZ4-compressed before writing to the WAL.</param>
    internal void EnableFpiCapture(WalManager walManager, bool enableFpiCompression = false)
    {
        _fpiBitmap = new FpiBitmap(MemPagesCount);
        _walManager = walManager;
        _enableFpiCompression = enableFpiCompression;
    }

    /// <summary>
    /// The FPI tracking bitmap. Exposed for <see cref="CheckpointManager"/> to reset at checkpoint start.
    /// Null when FPI capture is not enabled.
    /// </summary>
    internal FpiBitmap FpiBitmap => _fpiBitmap;

    unsafe public PagedMMF(IMemoryAllocator memoryAllocator, EpochManager epochManager, PagedMMFOptions options, IResource parent, string resourceName,
        ILogger<PagedMMF> logger) : base(resourceName, ResourceType.File, parent)
    {
        if (!options.Validate(true, out var errors))
        {
            throw new ArgumentException("Invalid PagedMMF options", nameof(options), new AggregateException(errors));
        }
        
        EpochManager = epochManager;
        Options = options;
        Logger = logger;

        // Create the cache of the page, pin it and keeps its address
        var cacheSize = Options.DatabaseCacheSize;
        MemPages = memoryAllocator.AllocatePinned("PageCache", this, (int)cacheSize, true, 64);
        _memPagesAddr = MemPages.DataAsPointer;

        // Create the Memory Page info table
        MemPagesCount = (int)(cacheSize >> PageSizePow2);
        var pageCount = MemPagesCount;
        _memPagesInfo = new PageInfo[pageCount];
        _clockSweepCurrentIndex = 0;

        for (int i = 0; i < pageCount; i++)
        {
            _memPagesInfo[i] = new PageInfo(i);
        }
        
        _memPageIndexByFilePageIndex = new ConcurrentDictionary<int, int>();

        _metrics = new Metrics (this, MemPagesCount);

        try
        {
            // Init or load the file
            var filePathName = Options.BuildDatabasePathFileName();
            var fi = new FileInfo(filePathName);
            IsDatabaseFileCreating = fi.Exists == false;
            if (IsDatabaseFileCreating)
            {
                CreateFile();
            }
            else
            {
                LoadFile();
            }
            Logger.LogInformation("Virtual Disk Manager service initialized successfully");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Virtual Disk Manager service initialization failed");
            Dispose();
            throw new Exception("Virtual Disk Manager initialization error, check inner exception.", e);
        }
    }

    public void DeleteDatabaseFile()
    {
        var fi = new FileInfo(Options.BuildDatabasePathFileName());
        if (fi.Exists)
        {
            fi.Delete();
        }
    }

    private void CreateFile()
    {
        // Create the Files
        var filePathName = Options.BuildDatabasePathFileName();

        _fileHandle = File.OpenHandle(filePathName, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
        _fileSize = 0L;

        Logger.LogInformation("Create Database '{DatabaseName}' in file '{FilePathName}'", Options.DatabaseName, filePathName);
        
        OnFileCreating();
    }

    protected virtual void OnFileCreating()
    {
        var handler = CreatingEvent;
        handler?.Invoke(this, null!);
    }

    private void LoadFile()
    {
        // Create the Files
        var filePathName = Options.BuildDatabasePathFileName();
        _fileHandle = File.OpenHandle(filePathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous|FileOptions.RandomAccess);
        {
            var fi = new FileInfo(filePathName);
            _fileSize = fi.Length;
        }
        
        OnFileLoading();
    }

    protected virtual void OnFileLoading()
    {
        var handler = LoadingEvent;
        handler?.Invoke(this, null!);
    }
    
    public bool IsDatabaseFileCreating { get; }

    public bool IsDisposed { get; private set; }

    protected unsafe override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            Logger.LogInformation("Disposing Virtual Disk Manager");
            if (_fileHandle != null)
            {
                _fileHandle.Dispose();
                _fileHandle = null;
            }
        
            _memPagesInfo = null;
            _memPagesAddr = null;

            Logger.LogInformation("Virtual Disk Manager disposed");
        }
        IsDisposed = true;
        base.Dispose(disposing);
    }
    
    /// <summary>
    /// Request epoch-tagged shared access to a page. The page is protected from eviction
    /// by its AccessEpoch tag rather than by ref-counting. Caller must be inside an
    /// <see cref="EpochGuard"/> scope.
    /// </summary>
    internal bool RequestPageEpoch(int filePageIndex, long currentEpoch, out int memPageIndex)
    {
        while (true)
        {
            if (!FetchPageToMemory(filePageIndex, out memPageIndex))
            {
                return false;
            }

            var pi = _memPagesInfo[memPageIndex];

            // Tag the page with the current epoch (atomic max — never go backward)
            long existing;
            do
            {
                existing = pi.AccessEpoch;
                if (currentEpoch <= existing)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref pi.AccessEpoch, currentEpoch, existing) != existing);

            // Handle Allocating state from cache miss — transition to Idle
            // (must come AFTER epoch tag so the page is protected before becoming evictable)
            if (pi.PageState == PageState.Allocating)
            {
                pi.PageState = PageState.Idle;
                Interlocked.Increment(ref _metrics.FreeMemPageCount);
            }

            // Race detection: page may have been evicted between FetchPageToMemory and epoch tag
            if (pi.FilePageIndex != filePageIndex)
            {
                continue;  // Retry
            }

            // Ensure data is ready (wait for pending I/O)
            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
                pi.ResetIOCompletionTask();
            }

            pi.IncrementClockSweepCounter();
            EnsurePageVerified(memPageIndex);
            return true;
        }
    }

    /// <summary>
    /// Fetch the requested File Page to memory, allocating a Memory Page if needed.
    /// </summary>
    /// <param name="filePageIndex">Index of the File Page to fetch</param>
    /// <param name="memPageIndex"></param>
    /// <param name="timeout">The time (in tick) the method should wait to return successfully.</param>
    /// <param name="cancellationToken">An optional cancellation token for the user to cancel the call.</param>
    /// <returns><c>true</c> if the call succeeded, <paramref name="memPageIndex"/> will be valid. <c>false</c> if the operation was cancelled or time out
    /// <paramref name="memPageIndex"/> won't be valid.</returns>
    /// <remarks>
    /// This method will enter a wait cycle if the Memory Page is not allocated and there are no free Memory Pages available.
    /// </remarks>
    private bool FetchPageToMemory(int filePageIndex, out int memPageIndex, long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        // Get the memory page from the cache, if it fails we allocate a new one
        if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out memPageIndex) == false)
        {
            ++_metrics.MemPageCacheMiss;
            LogMemPageCacheMiss();

            // At CacheMiss level: create rootless Fetch parent span with link to trigger
            // At CacheMiss level: Fetch is a child of the current activity (RequestPage or Transaction)
            // DiskRead will become a child of Fetch
            Activity fetchActivity = null;
            if (TelemetryConfig.PagedMMFSpanCacheMiss)
            {
                fetchActivity = TyphonActivitySource.StartActivity("PageCache.Fetch");
                fetchActivity?.SetTag(TyphonSpanAttributes.PageId, filePageIndex);
            }

            // Page is not cached, we assign an available Memory Page to it
            if (!AllocateMemoryPage(filePageIndex, out memPageIndex, timeout, cancellationToken))
            {
                fetchActivity?.Dispose();
                return false;
            }

            // Reset CRC verification flag — page is freshly loaded, needs re-verification
            _memPagesInfo[memPageIndex].CrcVerified = false;

            // Load the page from disk, if it's stored there already. (won't be the case for new pages)
            // The load is async and not part of the returned task but stored in the PageInfo
            var pageOffset = filePageIndex * (long)PageSize;
            var loadPage = (pageOffset + PageSize) <= _fileSize;
            if (loadPage)
            {
                LogAllocatePageLoad();
                ++_metrics.ReadFromDiskCount;

                // At IOOnly level: DiskRead is a child of the current activity
                // - If Fetch exists (CacheMiss level): child of Fetch
                // - If no Fetch (IOOnly only): child of RequestPage or Transaction
                Activity diskReadActivity = null;
                if (TelemetryConfig.PagedMMFSpanIOOnly)
                {
                    diskReadActivity = TyphonActivitySource.StartActivity("PageCache.DiskRead");
                    diskReadActivity?.SetTag(TyphonSpanAttributes.PageId, filePageIndex);
                }

                var pi = _memPagesInfo[memPageIndex];
                var readTask = RandomAccess.ReadAsync(_fileHandle, MemPages.DataAsMemory.Slice(memPageIndex * PageSize, PageSize), pageOffset, cancellationToken);

                // Wrap the task to dispose activities when complete
                if (diskReadActivity != null || fetchActivity != null)
                {
                    var wrappedTask = readTask.AsTask().ContinueWith(t =>
                    {
                        diskReadActivity?.Dispose();
                        fetchActivity?.Dispose();
                        return t.Result;
                    }, TaskContinuationOptions.ExecuteSynchronously);
                    pi.SetIOReadTask(new ValueTask<int>(wrappedTask));
                }
                else
                {
                    pi.SetIOReadTask(readTask);
                }
            }
            else
            {
                // No disk read needed - dispose Fetch span now
                // Dispose() automatically restores Activity.Current to the parent
                fetchActivity?.Dispose();
            }
        }
        else
        {
            ++_metrics.MemPageCacheHit;
            LogMemPageCacheHit();
        }

#if TELEMETRY
        using var logMemPageIndex = LogContext.PushProperty("MemPageIndex", memPageIndex);
#endif

        LogRequestPageFound();
        return true;
    }

    private int AdvanceClockHand()
    {
        var curValue = _clockSweepCurrentIndex;
        var newValue = (curValue + 1) % MemPagesCount;
        while (Interlocked.CompareExchange(ref _clockSweepCurrentIndex, newValue, curValue) != curValue)
        {
            curValue = _clockSweepCurrentIndex;
            newValue = (curValue + 1) % MemPagesCount;
        }

        return curValue;
    }

    /// <summary>
    /// Allocate a Memory Page for the given File Page Index.
    /// </summary>
    /// <param name="filePageIndex">The file page index to mount to memory</param>
    /// <param name="memPageIndex">The index of the memory page for the requested file page if the call is successful.</param>
    /// <param name="timeout">The time (in tick) the method should wait to return successfully.</param>
    /// <param name="cancellationToken">An optional cancellation token for the user to cancel the call.</param>
    /// <returns><c>true</c> if the call succeeded, <paramref name="memPageIndex"/> will be valid. <c>false</c> if the operation was cancelled or time out
    /// <paramref name="memPageIndex"/> won't be valid.</returns>
    /// <remarks>
    /// This method will enter a wait cycle if no Memory Page is available, it will wait and loop until it finds one.
    /// Use the clock-sweep algorithm to find a free Memory Page.
    /// </remarks>
    private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        Activity allocateActivity = null;
        if (TelemetryConfig.PagedMMFSpanCacheMiss)
        {
            allocateActivity = TyphonActivitySource.StartActivity("PageCache.AllocatePage");
            allocateActivity?.SetTag(TyphonSpanAttributes.PageId, filePageIndex);
        }

        try
        {
            return AllocateMemoryPageCore(filePageIndex, out memPageIndex, timeout, cancellationToken);
        }
        finally
        {
            allocateActivity?.Dispose();
        }
    }

    private bool AllocateMemoryPageCore(int filePageIndex, out int memPageIndex, long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        var minActiveEpoch = EpochManager?.MinActiveEpoch ?? long.MaxValue;
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        var waiter = new AdaptiveWaiter();

        LogAllocatePageEnter();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                memPageIndex = -1;
                return false;
            }
            
            bool found = false;
            PageInfo pi = null;
            memPageIndex = -1;
            int evictedFilePageIndex = -1;

            // If we already have a MemPage fetch for the FilePage just before the one we allocate, then we try to take the MemPage that follows
            // We request FilePage 123, there's a FilePage 122 allocated to MemPage 34, then we try to allocate 35 for 123, which will allow, if needed,
            //  one file write operation for both pages
            if (filePageIndex > 0 && _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemPageIndex) && ((prevMemPageIndex + 1) < MemPagesCount))
            {
                memPageIndex = prevMemPageIndex + 1;
                pi = _memPagesInfo[memPageIndex];
                evictedFilePageIndex = pi.FilePageIndex;
                if (TryAcquire(pi, minActiveEpoch))
                {
                    LogAllocatePageSequential();
                    found = true;
                }
            }

            // Parse the PageInfo array following the clock-sweep algorithm
            // Basically it's a circular parsing that find the first entry with a counter equals to 0, if the entry is not, then it's decremented (until it reaches
            //  0). When a page is access, its counter is incremented but capped to PageInfo.ClockSweepMaxValue.
            // If we can't find a page fitting this conditions, we do one more loop finding the first available page
            if (found == false)
            {
                int attempts = 0;
                int maxAttempts = MemPagesCount * 2;

                while (attempts < maxAttempts)
                {
                    memPageIndex = AdvanceClockHand();
                    pi = _memPagesInfo[memPageIndex];

                    // If the counter is 0, the page is candidate for eviction, try to acquire it
                    if (pi.ClockSweepCounter == 0)
                    {
                        evictedFilePageIndex = pi.FilePageIndex;
                        if (TryAcquire(pi, minActiveEpoch))
                        {
                            found = true;
                            break;
                        }
                    }

                    // Decrement the counter for this page and loop
                    pi.DecrementClockSweepCounter();
                    attempts++;
                }

                // Should almost never happen, right. ...right?
                // But if it is, loop one more time, same thing, but ignoring the ClockSweepCounter, take the first page available
                if (found == false)
                {
                    attempts = 0;
                    maxAttempts = MemPagesCount;

                    while (attempts < maxAttempts)
                    {
                        memPageIndex = AdvanceClockHand();
                        pi = _memPagesInfo[memPageIndex];

                        // If the counter is 0, the page is candidate for eviction, try to acquire it
                        evictedFilePageIndex = pi.FilePageIndex;
                        if (TryAcquire(pi, minActiveEpoch))
                        {
                            found = true;
                            break;
                        }

                        // Decrement the counter for this page and loop
                        pi.DecrementClockSweepCounter();
                        attempts++;
                    }
                }

                if (!found)
                {
                    if (wc.ShouldStop)
                    {
                        ThrowHelper.ThrowResourceExhausted("Storage/PagedMMF/AllocateMemoryPage", ResourceType.Memory, 
                            MemPagesCount - _metrics.FreeMemPageCount, MemPagesCount);
                    }

                    waiter.Wait();
                    continue;
                }
            }

            pi.FilePageIndex = filePageIndex;
            
            ++_metrics.TotalMemPageAllocatedCount;
            LogAllocatePageFound(memPageIndex);

            // Record eviction event on the parent AllocatePage span when a cached page was displaced
            if (TelemetryConfig.PagedMMFSpanCacheMiss && evictedFilePageIndex >= 0)
            {
                Activity.Current?.AddEvent(new ActivityEvent("PageEvicted",
                    tags: new ActivityTagsCollection
                    {
                        { TyphonSpanAttributes.PageId, evictedFilePageIndex }
                    }));
            }

            if (Options.PagesDebugPattern)
            {
                var pageAddr = MemPages.DataAsMemory.Slice(memPageIndex * PageSize).Span.Cast<byte, int>();
                int i;
                for (i = 0; i < PageHeaderSize >> 2; i++)
                {
                    pageAddr[i] = (filePageIndex << 16) | 0xFF00 | i;
                }

                for (int j = 0; j < PageRawDataSize >> 2; j++, i++)
                {
                    pageAddr[i] = (filePageIndex << 16) | j;
                }
            }

            // There might have been a concurrent allocation for this FilePage, so we Get or Add and check which MemPage is set
            var newMemPageIndex = _memPageIndexByFilePageIndex.GetOrAdd(filePageIndex, memPageIndex);

            // If the returned one is different, another thread beat us, we need to clean up what we did here and consider the other one
            if (newMemPageIndex != memPageIndex)
            {
                // Undo the page allocation, we are not going to use it
                pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
                pi.FilePageIndex = -1;
                pi.PageState = PageState.Free;
                pi.ResetIOCompletionTask();
                pi.ResetClockSweepCounter();
                pi.StateSyncRoot.ExitExclusiveAccess();

                memPageIndex = newMemPageIndex;
                _metrics.TotalMemPageAllocatedCount--;
            }

            return true;
        }
    }

    private bool TryAcquire(PageInfo info, long minActiveEpoch)
    {
        // First pass, check without locking (we won't bother to acquire the lock if the page is not in Free or Idle state)
        var state = info.PageState;
        if (state != PageState.Free && state != PageState.Idle)
        {
            return false;
        }

        // Don't evict pages that are epoch-protected or still dirty
        if (state == PageState.Idle)
        {
            if (info.AccessEpoch >= minActiveEpoch || info.DirtyCounter > 0)
            {
                return false;
            }
        }

        // Second pass, under lock
        try
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
            if (!info.StateSyncRoot.EnterExclusiveAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("PageCache/TryAcquire", TimeoutOptions.Current.PageCacheLockTimeout);
            }

            // Reset the IOMode from read to none for a loading page if the IO read task completed successfully.
            if (info.IOReadTask!=null && info.IOReadTask.IsCompletedSuccessfully)
            {
                info.ResetIOCompletionTask();
            }

            // We need to check the state again, because another thread might have changed between the first and second pass
            if (info.PageState is PageState.Free or PageState.Idle)
            {
                // Re-check epoch + dirty under lock (may have changed since first pass)
                if (info.PageState == PageState.Idle && (info.AccessEpoch >= minActiveEpoch || info.DirtyCounter > 0))
                {
                    return false;
                }

                // Idle page is still referenced in the cache directory, so we remove it
                if (info.PageState == PageState.Idle)
                {
                    _memPageIndexByFilePageIndex.TryRemove(info.FilePageIndex, out _);
                }
                info.ResetClockSweepCounter();
                info.FilePageIndex = -1;
                info.AccessEpoch = 0;  // Clear epoch tag on reallocation
                info.PageState = PageState.Allocating;
                Interlocked.Decrement(ref _metrics.FreeMemPageCount);
                Debug.Assert(info.ExclusiveLatchDepth == 0);
                return true;
            }
            else
            {
                return false;
            }
        }
        finally
        {
            info.StateSyncRoot.ExitExclusiveAccess();
        }
    }
    
    public ChangeSet CreateChangeSet() => new(this);

    /// <summary>
    /// Acquire exclusive latch on an epoch-protected page (Idle → Exclusive).
    /// Re-entrant: if already exclusively held by the current thread, increments
    /// a counter and returns true. This is needed because multiple chunks on the
    /// same page may be latched independently (e.g., in VariableSizedBufferAccessor.NextChunk).
    /// </summary>
    internal bool TryLatchPageExclusive(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        // Re-entrant fast path: already latched by this thread — skip StateSyncRoot entirely
        if (pi.PageExclusiveLatch.IsLockedByCurrentThread)
        {
            pi.ExclusiveLatchDepth++;
            return true;
        }

        // New acquisition: check page state under StateSyncRoot
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/LatchPageExclusive", TimeoutOptions.Current.PageCacheLockTimeout);
        }

        try
        {
            if (pi.PageState != PageState.Idle)
            {
                return false;
            }

            pi.PageState = PageState.Exclusive;
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }

        // Acquire the latch (records thread ownership atomically)
        pi.PageExclusiveLatch.EnterExclusiveAccess(ref WaitContext.Null);
        pi.ExclusiveLatchDepth = 0;

        // Seqlock: signal modification in progress (even -> odd)
        unsafe
        {
            var headerAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
            ++headerAddr->ModificationCounter;

            // FPI: capture full-page before-image on first dirty per checkpoint cycle
            if (_walManager != null && !_fpiBitmap.TestAndSet(memPageIndex))
            {
                WriteFpiRecord(memPageIndex, pi.FilePageIndex, headerAddr);
            }
        }

        return true;
    }

    /// <summary>
    /// Release exclusive latch on an epoch-protected page (Exclusive → Idle).
    /// Decrements the re-entrance counter; only transitions to Idle when it reaches zero.
    /// </summary>
    internal void UnlatchPageExclusive(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        if (pi.ExclusiveLatchDepth > 0)
        {
            pi.ExclusiveLatchDepth--;
            return;
        }

        // Seqlock: signal modification complete (odd -> even)
        unsafe
        {
            var headerAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
            ++headerAddr->ModificationCounter;
        }

        pi.PageExclusiveLatch.ExitExclusiveAccess();

        pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        pi.PageState = PageState.Idle;
        // Reset epoch tag so the page becomes evictable immediately.
        // The exclusive latch already protected the page during writes;
        // once unlatched, epoch protection is no longer needed.
        pi.AccessEpoch = 0;
        pi.StateSyncRoot.ExitExclusiveAccess();
    }

    /// <summary>
    /// Serializes a Full-Page Image (FPI) WAL record capturing the before-image of a page. Called under exclusive latch — the page is stable during the copy.
    /// When <see cref="_enableFpiCompression"/> is true, the page payload is LZ4-compressed to reduce WAL bandwidth.
    /// Incompressible pages (e.g., random data) automatically fall back to uncompressed format.
    /// </summary>
    private unsafe void WriteFpiRecord(int memPageIndex, int filePageIndex, PageBaseHeader* headerAddr)
    {
        var pageAddr = _memPagesAddr + (memPageIndex * (long)PageSize);
        var pageSpan = new ReadOnlySpan<byte>(pageAddr, PageSize);

        byte[] rentedBuffer = null;
        try
        {
            bool useCompression = false;
            int compressedSize = 0;

            // Try LZ4 compression if enabled
            if (_enableFpiCompression)
            {
                rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(FpiCompression.MaxCompressedSize(PageSize));
                compressedSize = FpiCompression.Compress(pageSpan, rentedBuffer);
                useCompression = compressedSize > 0;
            }

            int pagePayloadSize = useCompression ? compressedSize : PageSize;
            int fpiPayloadSize = FpiMetadata.SizeInBytes + pagePayloadSize;
            int fpiTotalRecordSize = WalRecordHeader.SizeInBytes + fpiPayloadSize;

            // Claim WAL buffer space for 1 FPI record
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
            var claim = _walManager.CommitBuffer.TryClaim(fpiTotalRecordSize, 1, ref wc);

            if (!claim.IsValid)
            {
                // WAL back-pressure — FPI is best-effort in this path.
                // The page will still be dirty and the next checkpoint will re-capture if needed.
                return;
            }

            try
            {
                // Build FPI WAL record header
                var header = new WalRecordHeader
                {
                    LSN = claim.FirstLSN,
                    TransactionTSN = 0,
                    TotalRecordLength = (uint)fpiTotalRecordSize,
                    UowEpoch = 0,
                    ComponentTypeId = 0,
                    EntityId = 0,
                    PayloadLength = (ushort)fpiPayloadSize,
                    OperationType = 0,
                    Flags = (byte)(WalRecordFlags.FullPageImage | (useCompression ? WalRecordFlags.Compressed : 0)),
                    PrevCRC = 0,
                    CRC = 0,
                };

                // Write header into claim
                int offset = 0;
                MemoryMarshal.Write(claim.DataSpan[offset..], in header);
                offset += WalRecordHeader.SizeInBytes;

                // Write FPI metadata
                var meta = new FpiMetadata
                {
                    FilePageIndex = filePageIndex,
                    SegmentId = 0,
                    ChangeRevision = headerAddr->ChangeRevision,
                    UncompressedSize = (ushort)PageSize,
                    CompressionAlgo = useCompression ? FpiCompression.AlgoLZ4 : FpiCompression.AlgoNone,
                    Reserved = 0,
                };
                MemoryMarshal.Write(claim.DataSpan[offset..], in meta);
                offset += FpiMetadata.SizeInBytes;

                // Write page data — compressed from rented buffer, or uncompressed from page address
                if (useCompression)
                {
                    new ReadOnlySpan<byte>(rentedBuffer, 0, compressedSize).CopyTo(claim.DataSpan[offset..]);
                }
                else
                {
                    pageSpan.CopyTo(claim.DataSpan[offset..]);
                }
                offset += pagePayloadSize;

                // Compute CRC over entire record (header + metadata + page) with CRC field zeroed
                var crcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
                var crc = WalCrc.ComputeSkipping(claim.DataSpan[..offset], crcFieldOffset, sizeof(uint));
                MemoryMarshal.Write(claim.DataSpan[crcFieldOffset..], in crc);

                _walManager.CommitBuffer.Publish(ref claim);
            }
            catch
            {
                _walManager.CommitBuffer.AbandonClaim(ref claim);
                throw;
            }
        }
        finally
        {
            if (rentedBuffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC Verification & FPI Repair
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lazily verifies the CRC32C checksum of a cached page. Called from <see cref="RequestPageEpoch"/>
    /// after the page data is ready. Skips verification for: already-verified pages, RecoveryOnly mode,
    /// root page (file page 0), and never-checkpointed pages (CRC == 0).
    /// On mismatch, attempts FPI repair via <see cref="TryRepairPageFromFpi"/>; throws
    /// <see cref="PageCorruptionException"/> if repair fails.
    /// </summary>
    private unsafe void EnsurePageVerified(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        // Already verified this load cycle
        if (pi.CrcVerified)
        {
            return;
        }

        // RecoveryOnly mode: skip on-load checks (recovery handles torn pages via WalRecovery Phase 4)
        if (_pageChecksumVerification == PageChecksumVerification.RecoveryOnly)
        {
            pi.CrcVerified = true;
            return;
        }

        // Root page (file page 0) uses a different header format — skip
        if (pi.FilePageIndex <= 0)
        {
            pi.CrcVerified = true;
            return;
        }

        // Read stored CRC from the page header
        var pageAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
        var storedCrc = pageAddr->PageChecksum;

        // CRC == 0 means the page has never been checkpointed (pre-FPI pages) — skip
        if (storedCrc == 0)
        {
            pi.CrcVerified = true;
            return;
        }

        // Compute CRC over the page, skipping the checksum field itself
        var pageSpan = new ReadOnlySpan<byte>((byte*)pageAddr, PageSize);
        var computedCrc = WalCrc.ComputeSkipping(pageSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);

        if (computedCrc == storedCrc)
        {
            pi.CrcVerified = true;
            return;
        }

        // CRC mismatch — attempt FPI repair
        if (TryRepairPageFromFpi(memPageIndex, pi.FilePageIndex, storedCrc, computedCrc))
        {
            pi.CrcVerified = true;
            return;
        }

        // Repair failed — unrecoverable corruption
        throw new PageCorruptionException(pi.FilePageIndex, storedCrc, computedCrc);
    }

    /// <summary>
    /// Attempts to repair a corrupted page by finding the most recent FPI in the WAL and restoring it.
    /// Copies the FPI data into both the in-memory cache page and the on-disk data file.
    /// </summary>
    /// <returns>True if the page was successfully repaired; false if no FPI was available.</returns>
    private unsafe bool TryRepairPageFromFpi(int memPageIndex, int filePageIndex, uint storedCrc, uint computedCrc)
    {
        if (_walManager == null)
        {
            return false;
        }

        var fpiData = _walManager.SearchFpiForPage(filePageIndex);
        if (fpiData == null)
        {
            return false;
        }

        // Validate the FPI data CRC before applying it
        var fpiSpan = new ReadOnlySpan<byte>(fpiData);
        var fpiCrcStored = MemoryMarshal.Read<uint>(fpiSpan.Slice(PageBaseHeader.PageChecksumOffset));
        if (fpiCrcStored != 0)
        {
            var fpiCrcComputed = WalCrc.ComputeSkipping(fpiSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            if (fpiCrcComputed != fpiCrcStored)
            {
                return false; // FPI itself is corrupt — cannot use
            }
        }

        // Copy FPI data into the cache page
        var pageAddr = _memPagesAddr + (memPageIndex * (long)PageSize);
        fpiData.AsSpan().CopyTo(new Span<byte>(pageAddr, PageSize));

        // Write repaired page to disk
        WritePageDirect(filePageIndex, fpiData);

        Logger.LogWarning("Repaired torn page {FilePageIndex}: stored CRC=0x{StoredCrc:X8}, computed=0x{ComputedCrc:X8}, restored from FPI",
            filePageIndex, storedCrc, computedCrc);

        return true;
    }

    /// <summary>
    /// Reads a full page directly from the data file into the destination buffer.
    /// Used by <see cref="WalRecovery"/> for torn page detection during crash recovery.
    /// </summary>
    internal void ReadPageDirect(int filePageIndex, Span<byte> destination) => RandomAccess.Read(_fileHandle, destination, filePageIndex * (long)PageSize);

    /// <summary>
    /// Writes a full page directly to the data file from the source buffer.
    /// Used by <see cref="WalRecovery"/> and <see cref="TryRepairPageFromFpi"/> for torn page repair.
    /// Also updates the tracked file size if the write extends beyond the current end of file.
    /// </summary>
    internal void WritePageDirect(int filePageIndex, ReadOnlySpan<byte> source)
    {
        var pageOffset = filePageIndex * (long)PageSize;
        RandomAccess.Write(_fileHandle, source, pageOffset);
        TrackFileGrowth(pageOffset + PageSize);
    }

    internal void IncrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        Debug.Assert(pi.PageState is PageState.Exclusive or PageState.Idle, "We can't increment the dirty counter for a page that is not Exclusive or Idle.");

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/IncrementDirty", TimeoutOptions.Current.PageCacheLockTimeout);
        }
        ++pi.DirtyCounter;
        pi.StateSyncRoot.ExitExclusiveAccess();
    }
    
    internal void DecrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        --pi.DirtyCounter;
        pi.StateSyncRoot.ExitExclusiveAccess();
    }

    /// <summary>
    /// Writes ALL non-free cached pages to the data file, regardless of their dirty state.
    /// Called after database creation to ensure all pages (including those allocated without a ChangeSet)
    /// are persisted before the first checkpoint cycle.
    /// </summary>
    internal void FlushAllCachedPages()
    {
        var cs = CreateChangeSet();
        int count = 0;
        for (int i = 0; i < MemPagesCount; i++)
        {
            var pi = _memPagesInfo[i];
            if (pi != null && pi.PageState != PageState.Free)
            {
                cs.AddByMemPageIndex(i);
                count++;
            }
        }

        cs.SaveChanges();
        FlushToDisk();
    }

    /// <summary>
    /// Flushes all pending writes to the underlying data file. Calls <c>RandomAccess.FlushToDisk</c> which issues an OS-level fsync.
    /// </summary>
    internal void FlushToDisk()
    {
        if (_fileHandle != null && !_fileHandle.IsInvalid)
        {
            RandomAccess.FlushToDisk(_fileHandle);
        }
    }

    /// <summary>
    /// Scans the in-memory page cache and returns the memory page indices of all dirty pages (DirtyCounter &gt; 0). The scan is approximate
    /// (no locking) — pages dirtied concurrently may be missed, which is safe because they will be caught in the next checkpoint cycle.
    /// </summary>
    internal int[] CollectDirtyMemPageIndices()
    {
        var dirty = new List<int>();
        for (int i = 0; i < MemPagesCount; i++)
        {
            var pi = _memPagesInfo[i];
            if (pi != null && pi.DirtyCounter > 0 && pi.PageState != PageState.Free)
            {
                dirty.Add(i);
            }
        }
        return dirty.ToArray();
    }

    /// <summary>
    /// Copies a live page into a destination buffer using a seqlock read protocol.
    /// Spins while the page's <see cref="PageBaseHeader.ModificationCounter"/> is odd (writer in progress),
    /// then memcpys the page and validates the counter hasn't changed. Retries on torn reads.
    /// </summary>
    private unsafe void CopyPageWithSeqlock(byte* pageAddr, byte* destAddr)
    {
        var sw = new SpinWait();
        while (true)
        {
            // Read the modification counter (must be even = quiescent)
            var counter = ((PageBaseHeader*)pageAddr)->ModificationCounter;
            if ((counter & 1) != 0)
            {
                // Writer in progress — spin and retry
                sw.SpinOnce();
                continue;
            }

            // Copy the full page
            Buffer.MemoryCopy(pageAddr, destAddr, PageSize, PageSize);

            // Validate counter hasn't changed (no torn read)
            if (((PageBaseHeader*)pageAddr)->ModificationCounter == counter)
            {
                return; // Consistent snapshot obtained
            }

            // Counter changed — torn read, retry
            sw.SpinOnce();
        }
    }

    /// <summary>
    /// Writes dirty pages to the data file via staging buffers WITHOUT decrementing their DirtyCounter.
    /// Each page is snapshot-copied through the seqlock protocol, then CRC-stamped on the staging copy,
    /// and written synchronously to the data file. Called on the checkpoint thread.
    /// </summary>
    /// <param name="memPageIndices">Memory page indices of dirty pages to write.</param>
    /// <param name="stagingPool">Pool from which to rent page-sized staging buffers.</param>
    unsafe internal void WritePagesForCheckpoint(int[] memPageIndices, StagingBufferPool stagingPool)
    {
        if (memPageIndices.Length == 0)
        {
            return;
        }

        var memPageBaseAddr = _memPagesAddr;

        for (int i = 0; i < memPageIndices.Length; i++)
        {
            var memPageIndex = memPageIndices[i];
            var pi = _memPagesInfo[memPageIndex];

            // Wait for any pending I/O read to complete
            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
            }

            var livePageAddr = memPageBaseAddr + (memPageIndex * (long)PageSize);

            // Rent a staging buffer and snapshot the live page via seqlock
            using var staging = stagingPool.Rent();
            CopyPageWithSeqlock(livePageAddr, staging.Pointer);

            // Increment ChangeRevision and compute CRC on the staging copy (not the live page)
            if (pi.FilePageIndex > 0)
            {
                var stagingHeader = (PageBaseHeader*)staging.Pointer;
                ++stagingHeader->ChangeRevision;
                stagingHeader->PageChecksum = WalCrc.ComputeSkipping(staging.Span, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            }

            // Write staging buffer to the data file (synchronous — checkpoint runs on dedicated thread)
            var filePageIndex = pi.FilePageIndex;
            var pageOffset = filePageIndex * (long)PageSize;
            RandomAccess.Write(_fileHandle, staging.Span, pageOffset);
            TrackFileGrowth(pageOffset + PageSize);

            _metrics.PageWrittenToDiskCount++;
            _metrics.WrittenOperationCount++;
        }
    }

    unsafe internal Task SavePages(int[] memPageIndices)
    {
        // Flush is a child of the current activity (typically Transaction.Commit or UnitOfWork) DiskWrite spans will become children of Flush
        Activity flushActivity = null;
        if (TelemetryConfig.PagedMMFSpanIOOnly)
        {
            flushActivity = TyphonActivitySource.StartActivity("PageCache.Flush");
            flushActivity?.SetTag(TyphonSpanAttributes.PageCount, memPageIndices.Length);
        }

        // We want to generate as few IO operations as possible, so we sort the pages to identify the ones that are contiguous in the file
        Array.Sort(memPageIndices, (x, y) => x - y);

        var operations = new List<(int memPageIndex, int length)>();

        var curPageInfo = _memPagesInfo[memPageIndices[0]];
        var curOperation = (memPageIndex: memPageIndices[0], length: 1);
        var memPageBaseAddr = _memPagesAddr;

        for (int i = 1; i < memPageIndices.Length; i++)
        {
            // Increment the ChangeRevision for the page (File Page 0 is the file header, it's a different format so ignore it)
            if (curPageInfo.FilePageIndex > 0)
            {
                // Make sure the page to save is properly loaded first (wait for any pending IO read to complete).
                var ioTask = curPageInfo.IOReadTask;
                if (ioTask != null && !ioTask.IsCompletedSuccessfully)
                {
                    ioTask.GetAwaiter().GetResult();
                }

                var headerAddr = (PageBaseHeader*)(memPageBaseAddr + (curPageInfo.MemPageIndex * PageSize));
                ++headerAddr->ChangeRevision;
            }

            var nextMemPageIndex = memPageIndices[i];
            var nextPageInfo = _memPagesInfo[nextMemPageIndex];
            if ((curPageInfo.MemPageIndex+1)==nextPageInfo.MemPageIndex && (curPageInfo.FilePageIndex+1)==nextPageInfo.FilePageIndex)
            {
                // We are contiguous, extend the current operation
                curOperation.length++;
            }
            else
            {
                // We are not contiguous, store the current operation and start a new one
                operations.Add(curOperation);
                curOperation = (nextMemPageIndex, 1);
            }

            curPageInfo = nextPageInfo;
        }

        // Increment ChangeRevision for the last page (the loop above only processes pages before the last one)
        if (curPageInfo.FilePageIndex > 0)
        {
            var ioTask = curPageInfo.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
            }

            var headerAddr = (PageBaseHeader*)(memPageBaseAddr + (curPageInfo.MemPageIndex * PageSize));
            ++headerAddr->ChangeRevision;
        }

        // Don't forget to add the last operation
        operations.Add(curOperation);

        var tasks = new Task[operations.Count];
        for (int i = 0; i < operations.Count; i++)
        {
            tasks[i] = SavePageInternal(operations[i].memPageIndex, operations[i].length).AsTask();
        }

        // Restore Activity.Current to parent (Flush set it to itself during StartActivity)
        // This ensures subsequent code doesn't accidentally become children of Flush
        if (flushActivity != null)
        {
            Activity.Current = flushActivity.Parent;
        }

        var saveTask = Task.WhenAll(tasks).ContinueWith(_ =>
        {
            foreach (int memPageIndex in memPageIndices)
            {
                DecrementDirty(memPageIndex);
            }
            // Dispose the Flush span when all writes complete
            flushActivity?.Dispose();
        });
        return saveTask;
    }
    
    internal ValueTask SavePageInternal(int firstMemPageIndex, int length)
    {
        var pi = _memPagesInfo[firstMemPageIndex];

        // Save the page to disk
        var filePageIndex = pi.FilePageIndex;
        var pageOffset = filePageIndex * (long)PageSize;
        var lengthToWrite = PageSize * length;
        var pageData = MemPages.DataAsMemory.Slice(firstMemPageIndex * PageSize, lengthToWrite);

        TrackFileGrowth(pageOffset + lengthToWrite);

        _metrics.PageWrittenToDiskCount += length;
        _metrics.WrittenOperationCount++;

        // DiskWrite is a child of Flush (or whatever is current if Flush is disabled)
        Activity diskWriteActivity = null;
        if (TelemetryConfig.PagedMMFSpanIOOnly)
        {
            diskWriteActivity = TyphonActivitySource.StartActivity("PageCache.DiskWrite");
            diskWriteActivity?.SetTag(TyphonSpanAttributes.PageId, filePageIndex);
            diskWriteActivity?.SetTag(TyphonSpanAttributes.PageCount, length);
        }

        var writeTask = RandomAccess.WriteAsync(_fileHandle, pageData, pageOffset);

        // Wrap the task to dispose the activity when complete
        if (diskWriteActivity != null)
        {
            return new ValueTask(writeTask.AsTask().ContinueWith(_ => diskWriteActivity.Dispose(), TaskContinuationOptions.ExecuteSynchronously));
        }

        return writeTask;
    }

    internal unsafe byte* GetMemPageAddress(int memPageIndex) => &_memPagesAddr[memPageIndex * (long)PageSize];

    /// <summary>
    /// Get a typed <see cref="PageAccessor"/> for a memory page.
    /// Provides type-safe access to page header, metadata, and raw data regions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe PageAccessor GetPage(int memPageIndex) => new(GetMemPageAddress(memPageIndex));

    /// <summary>
    /// Get the raw data address for a memory page (skips header).
    /// Used by epoch-mode ChunkAccessor which computes chunk addresses directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe byte* GetMemPageRawDataAddress(int memPageIndex)
        => GetMemPageAddress(memPageIndex) + PageHeaderSize;

    /// <summary>
    /// Get the base address of the memory page cache.
    /// Used by ChunkAccessor to compute memPageIndex from raw data addresses.
    /// </summary>
    internal unsafe byte* MemPagesBaseAddress => _memPagesAddr;

    #region Logging helpers

    [Conditional("TELEMETRY")]
    private void LogMemPageCacheHit() => Logger.LogTrace(11, "MemPage Cache Hit");

    [Conditional("TELEMETRY")]
    private void LogMemPageCacheMiss() => Logger.LogTrace(12, "MemPage Cache Miss");

    [Conditional("TELEMETRY")]
    private void LogRequestPageFound() => Logger.LogTrace(13, "Request Page Found");

    [Conditional("TELEMETRY")]
    private void LogRequestPageRace() => Logger.LogTrace(14, "Request Page Race Condition (reallocation)");

    [Conditional("TELEMETRY")]
    private void LogAllocatePageEnter() => Logger.LogTrace(20, "Allocate Page Enter");

    [Conditional("TELEMETRY")]
    private void LogAllocatePageSequential() => Logger.LogTrace(22, "Allocate Page Sequential");

    [Conditional("TELEMETRY")]
    private void LogAllocatePageFound(int memPageIndex) => Logger.LogTrace(24, "Allocate Page Found {MemPageId}", memPageIndex);

    [Conditional("TELEMETRY")]
    private void LogAllocatePageLoad() => Logger.LogTrace(25, "Allocate Page Load From Disk");

    [Conditional("TELEMETRY")]
    private void LogReset() =>
        Logger.LogTrace(44, "Resetting PagedFile instance !!!");

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct PageSnapshot(PageState state, short exclusiveLatchDepth, int dirtyCounter)
    {
        internal readonly PageState _state = state;
        internal readonly short _exclusiveLatchDepth = exclusiveLatchDepth;
        internal readonly int _dirtyCounter = dirtyCounter;
    }

    internal readonly struct StateSnapshot(PageSnapshot[] pages)
    {
        internal readonly PageSnapshot[] _pages = pages;
    }

    internal StateSnapshot SnapshotInternalState()
    {
        var pages = new PageSnapshot[_memPagesInfo.Length];
        for (int i = 0; i < _memPagesInfo.Length; i++)
        {
            var pi = _memPagesInfo[i];
            pages[i] = new PageSnapshot(pi.PageState, pi.ExclusiveLatchDepth, pi.DirtyCounter);
        }
        return new StateSnapshot(pages);
    }

    internal bool CheckInternalState(in StateSnapshot snapshot)
    {
        if (snapshot._pages.Length != _memPagesInfo.Length)
        {
            return false;
        }

        for (int i = 0; i < _memPagesInfo.Length; i++)
        {
            var pi = _memPagesInfo[i];
            ref readonly var snap = ref snapshot._pages[i];
            if (pi.PageState != snap._state ||
                pi.ExclusiveLatchDepth != snap._exclusiveLatchDepth ||
                pi.DirtyCounter != snap._dirtyCounter)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Get the PageInfo for a memory page by its memory index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PageInfo GetPageInfoByMemIndex(int memPageIndex) => _memPagesInfo[memPageIndex];

    /// <summary>Get the AccessEpoch for a memory page (test infrastructure).</summary>
    internal long GetPageAccessEpoch(int memPageIndex) => _memPagesInfo[memPageIndex].AccessEpoch;

    /// <summary>Get the PageState for a memory page (test infrastructure).</summary>
    internal PageState GetPageState(int memPageIndex) => _memPagesInfo[memPageIndex].PageState;

    public int EstimatedMemorySize
    {
        get
        {
            return Unsafe.SizeOf<PageInfo>() * _memPagesInfo.Length;
        }
    }
}
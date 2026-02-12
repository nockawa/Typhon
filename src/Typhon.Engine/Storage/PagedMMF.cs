using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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


    private void GetMemPageExtraInfo(out Metrics.MemPageExtraInfo res)
    {
        int free = 0;
        int allocating = 0;
        int idleCount = 0;
        int sharedCount = 0;
        int exclusiveCount = 0;
        int idleAndDirtyCount = 0;
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
                case PageState.Shared:
                    sharedCount++;
                    break;
                case PageState.Exclusive:
                    exclusiveCount++;
                    break;
                case PageState.IdleAndDirty:
                    idleAndDirtyCount++;
                    break;
            }
            if (pi.LockedByThreadId != 0)
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
            SharedMemPageCount = sharedCount,
            ExclusiveMemPageCount = exclusiveCount,
            IdleAndDirtyMemPageCount = idleAndDirtyCount,
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
        Idle         = 2,   // The page is allocated but idle (nobody is accessing it). So it can be re-used or reallocated to another File Page.
        Shared       = 3,   // The page is allocated and accessed by one/many concurrent threads,
                            //  the ConcurrentSharedCounter will indicate how many concurrent access we have.
        Exclusive    = 4,   // The page is allocated and accessed exclusively by a given thread indicated by LockedByThreadId.
        IdleAndDirty = 5,   // The page access was released but its content yet need to be written to disk.
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

    private readonly ConcurrentDictionary<int, int> _memPageIndexByFilePageIndex;
    private EpochManager _epochManager;   // null until Phase 3 wires it in

    unsafe public PagedMMF(IMemoryAllocator memoryAllocator, PagedMMFOptions options, IResource parent, string resourceName, ILogger<PagedMMF> logger) :
        base(resourceName, ResourceType.File, parent)
    {
        if (!options.Validate(true, out var errors))
        {
            throw new ArgumentException("Invalid PagedMMF options", nameof(options), new AggregateException(errors));
        }
        
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

    /// <summary>
    /// Set the epoch manager for epoch-based page protection.
    /// Called by DatabaseEngine during initialization.
    /// When null (default), epoch checks are skipped in TryAcquire.
    /// </summary>
    internal void SetEpochManager(EpochManager epochManager) => _epochManager = epochManager;

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
    /// Request access to a File Page, either Shared or Exclusive.
    /// </summary>
    /// <param name="filePageIndex">The index of the File Page to access.</param>
    /// <param name="exclusive"><c>true</c> for Exclusive access, <c>false</c> for Shared.</param>
    /// <param name="result">The <see cref="PageAccessor"/> allowing access to the page, valid only if the method returns <c>true</c>.</param>
    /// <param name="timeout">The time (in tick) the method should wait to return successfully.</param>
    /// <param name="cancellationToken">An optional cancellation token for the user to cancel the call.</param>
    /// <returns><c>true</c> if the call succeeded, <paramref name="result"/> will be valid. <c>false</c> if the operation was cancelled or time out
    /// <paramref name="result"/> won't be valid and be set to default.</returns>
    /// <remarks>
    /// This method will enter a wait cycle if:
    /// <li> The Memory Page is not allocated and there are no free Memory Pages available </li>
    /// <li> The requested access requires to wait for the transition to be made (one or many threads hold an access not compatible with the one we request).</li>
    /// <br/>
    /// If the File Page is being loading from disk to memory, the read is completely independent of this operation, the <see cref="PageAccessor"/> will
    ///  wait for it upon its first content access.
    /// </remarks>
    public bool RequestPage(int filePageIndex, bool exclusive, [TransfersOwnership] out PageAccessor result,
        long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
#if TELEMETRY
        using var logId = LogContext.PushProperty("FilePageIndex", filePageIndex);
        using var logRW = LogContext.PushProperty("IsExclusive", exclusive);
        LogRequestPage(filePageIndex, exclusive);
#endif

        Activity requestPageActivity = null;
        if (TelemetryConfig.PagedMMFSpanAll)
        {
            // RequestPage is a child of the current activity (Transaction operation)
            // This gives full call hierarchy visibility at the cost of larger traces
            requestPageActivity = TyphonActivitySource.StartActivity("PageCache.RequestPage");
            requestPageActivity?.SetTag(TyphonSpanAttributes.PageId, filePageIndex);
        }

        try
        {
            // Race condition can occur during TransitionPageToAccessAsync (e.g. waiting for the lock while the memory page is being reallocated for another disk page)
            // So we loop until we get the page in the right state
            AdaptiveWaiter waiter = null;
            while (true)
            {
                // If this returns false, it means we can't request a page right now: we return false
                if (!FetchPageToMemory(filePageIndex, out var memPageIndex, timeout, cancellationToken))
                {
                    result = default;
                    return false;
                }
                var pi = _memPagesInfo[memPageIndex];

                // If this return false, it's most likely a race condition where the Memory Page was reallocated for another File Page
                if (!TransitionPageToAccess(filePageIndex, pi, exclusive, timeout, cancellationToken))
                {
                    waiter ??= new AdaptiveWaiter();
                    waiter.Spin();
                    continue;
                }
                result = new PageAccessor(this, pi);
                return true;
            }
        }
        finally
        {
            requestPageActivity?.Dispose();
        }
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
        var minActiveEpoch = _epochManager?.MinActiveEpoch ?? long.MaxValue;
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        AdaptiveWaiter waiter = null;

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

                    waiter ??= new AdaptiveWaiter();
                    waiter.Spin();
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
                pi.ConcurrentSharedCounter = 0;
                pi.LockedByThreadId = 0;
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

        // Epoch check: if the page was accessed within an active epoch, skip it
        if (state == PageState.Idle && info.AccessEpoch >= minActiveEpoch)
        {
            return false;
        }

        // Second pass, under lock
        try
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
            if (!info.StateSyncRoot.EnterExclusiveAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("PageCache/TryAcquire", TimeoutOptions.Current.PageCacheLockTimeout);
            }

            // PageAccessor is responsible to reset the IOMode from read to none for a loading page, but only if the user creates and uses one, (which is
            //  most of the cases, but not all o them). So we take the opportunity to reset the IOMode here, if needed.
            if (info.IOReadTask!=null && info.IOReadTask.IsCompletedSuccessfully)
            {
                info.ResetIOCompletionTask();
            }

            // We need to check the state again, because another thread might have changed between the first and second pass
            if (info.PageState is PageState.Free or PageState.Idle)
            {
                // Re-check epoch under lock (may have changed since first pass)
                if (info.PageState == PageState.Idle && info.AccessEpoch >= minActiveEpoch)
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
                Debug.Assert(info.ConcurrentSharedCounter == 0);
                Debug.Assert(info.LockedByThreadId == 0);
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
    
    /// <summary>
    /// Transition the page from its current mode to Access (either shared or exclusive), blocking the call if needed.
    /// </summary>
    /// <param name="filePageIndex">Disk Page Id</param>
    /// <param name="pi">The corresponding PageInfo</param>
    /// <param name="exclusive"><c>true</c> if we are doing Exclusive access, otherwise it's Shared access</param>
    /// <param name="timeout">The time (in tick) the method should wait to return successfully.</param>
    /// <param name="cancellationToken">An optional cancellation token for the user to cancel the call.</param>
    /// <returns><c>true</c> is we successfully transitioned to access, <c>false</c> if we failed because of a race condition</returns>
    /// <remarks>
    /// This is...not easy... because there are many cases to cover
    /// Basically, the behavior we want is:
    ///  - If the page is Allocating or Idle: we transition to access, with Shared (Single, we store the Thread ID) or Exclusive by the requested thread.
    ///  - If the page is Shared:
    ///    - If we are requesting Shared again from the same thread: it's a re-entrant request, we keep the Shared Single and allow re-entrance.
    ///    - If we are requesting Shared again from another thread: we allow concurrent Shared but release the Single access from the Thread (no thread own the access anymore).
    ///    - If we are requesting Exclusive from the same thread: we promote the access from Shared Single to Exclusive.
    ///    - If we are requesting Exclusive from another thread: we wait for the Share request(s) to be over.
    ///  - If the page is Exclusive:
    ///    - If we request Shared or Exclusive from the same thread: we allow re-entrance.
    ///    - If we request Shared or Exclusive from another thread, we wait the current access to be over.
    /// All in all, it's common sense. We are being permissive by allowing re-entrant Shared from/to Exclusive when it's the same thread, we
    ///  assume the user knows what (s)he is doing.
    /// Promotion from Shared to Exclusive can only be made from Shared Single, it won't work in a two phases process where we would have
    ///  at first multiple Shared accesses, then all access stop except a remaining one, we don't know which thread remains in Shared mode so
    ///  we can't promote it to Exclusive if the situation would arise. If this is an issue we would have to store an array of threads
    ///  instead of a single field.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TransitionPageToAccess(int filePageIndex, PageInfo pi, bool exclusive, 
        long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
#if TELEMETRY
        int loopCount = 0;
        // ReSharper disable once TooWideLocalVariableScope
        DateTime start = DateTime.UtcNow;
#endif        
        PageState prevState;
        AdaptiveWaiter waiter = null;
        while (true)
        {
            try
            {
                // We want to change the state, so we need to acquire the lock
                var wcAccess = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
                if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wcAccess))
                {
                    ThrowHelper.ThrowLockTimeout("PageCache/TransitionToAccess", TimeoutOptions.Current.PageCacheLockTimeout);
                }

                Debug.Assert(pi.PageState != PageState.Free);
                
#if TELEMETRY
                var memPageId = pi.MemPageIndex;
                prevState = pi.PageState;
                using var logId = LogContext.PushProperty("FilePageIndex", filePageIndex);
                using var logMemPageId = LogContext.PushProperty("MemPageIndex", memPageId);
#endif

                // Safeguard, now we are under lock, check the MemPage is still targeting the PageId we want
                if ((filePageIndex != pi.FilePageIndex))
                {
                    LogTransitionFailed(exclusive ? PageState.Exclusive : PageState.Shared);
                    return false;
                }

                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
                // Each section at this level of indentation is testing the current state of the page 
                
                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
                // If the page is Allocating or Idle (and Dirty), we can transition to Shared or Exclusive very simply
                if (pi.PageState is PageState.Allocating or PageState.Idle or PageState.IdleAndDirty)
                {
                    // Setting the page to Allocating already updated the FreeMemPageCount, but we need to update it for other states
                    if (pi.PageState != PageState.Allocating)
                    {
                        Interlocked.Decrement(ref _metrics.FreeMemPageCount);
                    }
                    
                    pi.PageState = exclusive ? PageState.Exclusive : PageState.Shared;
                    pi.LockedByThreadId = Environment.CurrentManagedThreadId;
                    pi.ConcurrentSharedCounter = 1;
                    pi.IncrementClockSweepCounter();

                    LogTransitionSuccessful(prevState, pi.PageState);
                    return true;
                }
                
                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
                // If the page is in Shared state
                else if (pi.PageState == PageState.Shared)
                {
                    // Single/multiple or Re-entrant Shared mode?
                    if (!exclusive)
                    {
                        // Loose the single access if we now have multiple threads doing Shared access
                        var ct = Environment.CurrentManagedThreadId;
                        pi.LockedByThreadId = (pi.LockedByThreadId == ct) ? ct : 0;

                        ++pi.ConcurrentSharedCounter;
                        pi.IncrementClockSweepCounter();
                        
                        LogTransitionSuccessful(prevState, pi.PageState);
                        return true;
                    }

                    // Promotion from Shared to Exclusive?
                    
                    // Only possible if we are in Shared Single mode with the same thread that requested the Exclusive access
                    if (pi.ConcurrentSharedCounter == 1)
                    {
                        var ct = Environment.CurrentManagedThreadId;
                        if (pi.LockedByThreadId == ct)
                        {
                            // We can promote
                            ++pi.ConcurrentSharedCounter;
                            pi.IncrementClockSweepCounter();
                            pi.PageState = PageState.Exclusive;
                            pi.IncrementClockSweepCounter();

                            LogTransitionSuccessful(prevState, pi.PageState);
                            return true;
                        }
                    }
                    
                    // We need to wait for any other cases, we jump right after the finally block
                }
                
                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
                // From here, the page is in Exclusive state
                else
                {
                    // Check for re-entrant Access: we are in Exclusive access, we want exclusive access, and we are the thread that already owns the page.
                    // In this case we can return immediately
                    if (/*exclusive &&*/ Environment.CurrentManagedThreadId == pi.LockedByThreadId)
                    {
                        ++pi.ConcurrentSharedCounter;
                        pi.IncrementClockSweepCounter();
            
                        LogTransitionSuccessful(prevState, pi.PageState);
                        return true;
                    }
                }
            }
            finally
            {
                pi.StateSyncRoot.ExitExclusiveAccess();
            }

            // We arrive here because we couldn't make the requested transition, in a leap of faith, we wait...and retry.
            // But we log it, because, ya know...
#if TELEMETRY
            if (loopCount.IsPowerOf2())
            {
                LogTransitionWaitAndLoop(prevState, exclusive ? PageState.Exclusive : PageState.Shared, loopCount, DateTime.UtcNow - start);
            }
            loopCount++;
#endif
            
            waiter ??= new AdaptiveWaiter();
            waiter.Spin();
        }
    }

    public ChangeSet CreateChangeSet() => new(this);

    internal bool TryPromoteToExclusive(int filePageIndex, PageInfo pi, out PageState previousMode)
    {
        try
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
            if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("PageCache/PromoteToExclusive", TimeoutOptions.Current.PageCacheLockTimeout);
            }

            previousMode = pi.PageState;

            // Check if the page was reallocated by the time we got the lock
            if (filePageIndex != pi.FilePageIndex)
            {
                return false;
            }

            var ct = Environment.CurrentManagedThreadId;

            // Check if the thread already own exclusive access
            if (pi.LockedByThreadId != ct)
            {
                return false;
            }

            pi.IncrementClockSweepCounter();
            ++pi.ConcurrentSharedCounter;

            // If the thread is already in write mode, there's nothing to do, the call succeeds.
            if (pi.PageState == PageState.Exclusive)
            {
                return true;
            }

            // Switch to write, set the page as dirty
            pi.PageState = PageState.Exclusive;

            return true;
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }
    }

    internal void DemoteExclusive(PageInfo pi, PageState previousMode)
    {
        try
        {
            pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
            --pi.ConcurrentSharedCounter;
            pi.PageState = previousMode;
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Acquire exclusive latch on an epoch-protected page (Idle → Exclusive).
    /// Re-entrant: if already exclusively held by the current thread, increments
    /// a counter and returns true. This is needed because multiple chunks on the
    /// same page may be latched independently (e.g., in VariableSizedBufferAccessor.NextChunk).
    /// </summary>
    internal bool TryLatchPageExclusive(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/LatchPageExclusive", TimeoutOptions.Current.PageCacheLockTimeout);
        }

        try
        {
            // Re-entrant: if already exclusively held by this thread, just bump counter
            if (pi.PageState == PageState.Exclusive && pi.LockedByThreadId == Environment.CurrentManagedThreadId)
            {
                pi.ConcurrentSharedCounter++;
                return true;
            }

            // Only latch Idle pages (epoch-protected)
            if (pi.PageState != PageState.Idle)
            {
                return false;
            }

            pi.PageState = PageState.Exclusive;
            pi.LockedByThreadId = Environment.CurrentManagedThreadId;
            pi.ConcurrentSharedCounter = 1;
            return true;
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Release exclusive latch on an epoch-protected page (Exclusive → Idle).
    /// Decrements the re-entrance counter; only transitions to Idle when it reaches zero.
    /// </summary>
    internal void UnlatchPageExclusive(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            if (--pi.ConcurrentSharedCounter == 0)
            {
                pi.LockedByThreadId = 0;
                pi.PageState = PageState.Idle;
            }
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }
    }

    internal void IncrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        Debug.Assert(pi.PageState is PageState.Shared or PageState.Exclusive or PageState.Idle or PageState.IdleAndDirty, "We can't increment the dirty counter for a page that is not Shared, Exclusive, Idle, or IdleAndDirty.");

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
        if (--pi.DirtyCounter == 0 && pi.PageState == PageState.IdleAndDirty)
        {
            pi.PageState = PageState.Idle;
            Interlocked.Increment(ref _metrics.FreeMemPageCount);
        }
        pi.StateSyncRoot.ExitExclusiveAccess();
    }

    unsafe internal Task SavePages(int[] memPageIndices)
    {
        // Flush is a child of the current activity (typically Transaction.Commit or UnitOfWork)
        // DiskWrite spans will become children of Flush
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
                // Make sure the page to save is properly loaded first (wait for any pending IO read to complete)
                // NOTE: We don't use PageAccessor here because SavePages is called after accessors are disposed,
                // so pages are in IdleAndDirty state, not Shared/Exclusive.
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

        _fileSize = Math.Max(_fileSize, pageOffset + lengthToWrite);

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

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void TransitionPageFromAccessToIdle(PageInfo pi)
    {
#if TELEMETRY
        using var logId = LogContext.PushProperty("FilePageIndex", pi.FilePageIndex);
        using var logMemPageId = LogContext.PushProperty("MemPageIndex", pi.MemPageIndex);
#endif
        try
        {
            pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
            var pageState = pi.PageState;
            Debug.Assert(pageState == PageState.Shared || pageState == PageState.Exclusive);

            if (--pi.ConcurrentSharedCounter != 0)
            {
                return;
            }

            pi.LockedByThreadId = 0;
            if (pi.DirtyCounter > 0)
            {
                pi.PageState = PageState.IdleAndDirty;
            }
            else
            {
                pi.PageState =  PageState.Idle;
                Interlocked.Increment(ref _metrics.FreeMemPageCount);
            }
            
            LogTransitionSuccessful(pageState, PageState.Idle);
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }
    }

    internal unsafe byte* GetMemPageAddress(int memPageIndex) => &_memPagesAddr[memPageIndex * (long)PageSize];

    /// <summary>
    /// Get the raw data address for a memory page (skips header).
    /// Used by epoch-mode ChunkAccessor which computes chunk addresses directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe byte* GetMemPageRawDataAddress(int memPageIndex)
        => GetMemPageAddress(memPageIndex) + PageHeaderSize;

    #region Logging helpers

    [Conditional("TELEMETRY")]
    private void LogRequestPage(int pageId, bool doesWrite) => Logger.LogDebug(10, "Request Disk Page: {PageId}, Write: {IsExclusive}", pageId, doesWrite);
    
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
    private void LogTransitionSuccessful(PageState prevMode, PageState newMode) => 
        Logger.LogTrace(40, "Transition completed from {PrevMode} to {NewMode}", prevMode, newMode);

    [Conditional("TELEMETRY")]
    private void LogTransitionFailed(PageState newMode) => 
        Logger.LogTrace(41, "Transition completed to {NewMode} failed due to reallocation", newMode);


    [Conditional("TELEMETRY")]
    private void LogTransitionWaitAndLoop(PageState prevMode, PageState newMode, int loopCount, TimeSpan duration) => 
        Logger.LogTrace(42, "Transition waiting/reloop from {prevNode} to {NewMode} loop count: {loopCount}, duration: {duration}", prevMode, newMode, loopCount, duration);

 
    [Conditional("TELEMETRY")]
    private void LogReset() => 
        Logger.LogTrace(44, "Resetting PagedFile instance !!!");

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct PageSnapshot(PageState state, int sharedCounter, int dirtyCounter)
    {
        internal readonly PageState _state = state;
        internal readonly int _concurrentSharedCounter = sharedCounter;
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
            pages[i] = new PageSnapshot(pi.PageState, pi.ConcurrentSharedCounter, pi.DirtyCounter);
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
                pi.ConcurrentSharedCounter != snap._concurrentSharedCounter ||
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
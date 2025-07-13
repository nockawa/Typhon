using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Serilog.Context;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

[PublicAPI]
public class PagedFile : IAsyncInitializable, IAsyncDisposable
{
    #region Events

    internal event EventHandler<DatabaseEventArgs> DatabaseCreating;
    internal event EventHandler<DatabaseEventArgs> DatabaseLoading;

    #endregion

    #region Constants

    internal const int PageHeaderSize = 192;        // Base Header + Metadata
    internal const int PageBaseHeaderSize = 64;
    internal const int PageMetadataSize = 128;
    internal const int PageSize = 8192;             // Base Header + Metadata + RawData
    internal const int PageRawDataSize = PageSize - PageHeaderSize;
    internal const int PageSizePow2 = 13; // 2^( PageSizePow2 = PageSize
    internal const int DatabaseFormatRevision = 1;
    internal const ulong MinimumCacheSize = 512 * 1024 * 1024;
    internal const string HeaderSignature = "TyphonDatabase";
    internal const int WriteCachePageSize = 1024 * 1024;

    private const int OccupancySegmentRootPageIndex = 1;
    
    #endregion

    #region Debug Info

    [PublicAPI]
    internal struct DebugInfo
    {
        public int MemPageCacheHit;
        public int MemPageCacheMiss;
        public int ReadFromDiskCount;
        public int WriteToDiskCount;
        public int PagesWrittenCount;
        public int FreeMemPageCount;
        public int MemPageReallocationCount;
    }

    private DebugInfo _debugInfo;

    internal ref DebugInfo GetDebugInfo()
    {
        _debugInfo.FreeMemPageCount = _memPagesCount - _memPageIndexByFilePageIndex.Count;
        return ref _debugInfo;
    }

    #endregion

    internal enum PageState
    {
        Free         = 0,   // The page is free, yet to be allocated.
        Allocating   = 1,   // The page is being allocating by a call to AllocateMemoryPage.
        Idle         = 2,   // The page is allocated but idle (nobody is accessing it).
        Shared       = 3,   // The page is allocated and accessed by one/many concurrent threads,
                            //  the ConcurrentSharedCounter will indicate how many concurrent access we have.
        Exclusive    = 4,   // The page is allocated and accessed exclusively by a given thread indicated by LockedByThreadId.
    }

    internal enum PageIOMode
    {
        None    = 0,
        Read    = 1,                // The page is being loaded from disk
        Write   = 2,                // The page is being saved into disk
    }

    private class PageInfo
    {
        private const int ClockSweepMaxValue = 5;
        
        public readonly int MemPageIndex;
        public int FilePageIndex;
        public int ClockSweepCounter => _clockSweepCounter;
        public PageIOMode IOMode;

        public SmallAccessControl StateSyncRoot;
        public PageState PageState;                 // Must always be changed under StateSyncRoot lock
        public int LockedByThreadId;                // Same
        public int ConcurrentSharedCounter;         // Same

        private int _clockSweepCounter;
        private Lazy<Task<int>> _ioCompletionTask;

        public void SetIOCompletionTask(ValueTask<int> task, PageIOMode ioMode)
        {
            _ioCompletionTask = new Lazy<Task<int>>(task.AsTask);
            IOMode = ioMode;
        }

        public Task<int> IOCompletionTask => _ioCompletionTask?.Value ?? Task.FromResult(0);

        public void ResetIOCompletionTask()
        {
            _ioCompletionTask = null;
            IOMode = PageIOMode.None;
        }
        
        public PageInfo(int memPageIndex)
        {
            MemPageIndex = memPageIndex;
            FilePageIndex = -1;
            _clockSweepCounter = 0;
            ConcurrentSharedCounter = 0;
            StateSyncRoot = new SmallAccessControl();
        }

        public void IncrementClockSweepCounter()
        {
            var curValue = _clockSweepCounter;
            if (curValue == ClockSweepMaxValue)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref _clockSweepCounter, curValue + 1, curValue) != curValue)
            {
                curValue = _clockSweepCounter;
                if (curValue == ClockSweepMaxValue)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void DecrementClockSweepCounter()
        {
            var curValue = _clockSweepCounter;
            if (curValue == 0)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref _clockSweepCounter, curValue - 1, curValue) != curValue)
            {
                curValue = _clockSweepCounter;
                if (curValue == 0)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void ResetClockSweepCounter() => _clockSweepCounter = 0;
    }
    
    private readonly IServiceProvider _sp;
    private readonly DatabaseConfiguration _dbc;
    private readonly TimeManager _tmg;
    private readonly ILogger<PagedMemoryMappedFile> _log;
    private byte[] _memPages;
    private GCHandle _memPagesHandle;
    private unsafe byte* _memPagesAddr;
    
    private readonly int _memPagesCount;
    private int _clockSweepCurrentIndex;
    private PageInfo[] _memPagesInfo;
    
    private SafeFileHandle _fileHandle;
    private long _fileSize;

    private readonly ConcurrentDictionary<int, int> _memPageIndexByFilePageIndex;
    
    unsafe public PagedFile(IServiceProvider sp, IConfiguration<DatabaseConfiguration> dc, TimeManager timeManager, ILogger<PagedMemoryMappedFile> logger)
    {
        _sp = sp;
        _dbc = dc.Value;
        _tmg = timeManager;
        _log = logger;
        
        // Create the cache of the page, pin it and keeps its address
        var cacheSize = _dbc.DatabaseCacheSize;
        _memPages = new byte[cacheSize];

        _memPagesHandle = GCHandle.Alloc(_memPages, GCHandleType.Pinned);
        _memPagesAddr = (byte*)_memPagesHandle.AddrOfPinnedObject();

        // Create the Memory Page info table
        _memPagesCount = (int)(cacheSize >> PageSizePow2);
        var pageCount = _memPagesCount;
        _memPagesInfo = new PageInfo[pageCount];
        _clockSweepCurrentIndex = 0;

        for (int i = 0; i < pageCount; i++)
        {
            _memPagesInfo[i] = new PageInfo(i);
        }
        
        _memPageIndexByFilePageIndex = new ConcurrentDictionary<int, int>();
    }

    public async Task InitializeAsync()
    {
        ++ReferenceCounter;
        if (IsInitialized)
        {
            return;
        }

        try
        {
            // Init or load the file
            var filePathName = BuildDatabasePathFileName();
            var fi = new FileInfo(filePathName);
            var isCreationMode = fi.Exists == false;
            if (isCreationMode || _dbc.RecreateDatabase)
            {
                await CreateDatabaseFileAsync();
            }
            else
            {
                await LoadDatabaseFileAsync();
            }
            _log.LogInformation("Virtual Disk Manager service initialized successfully");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Virtual Disk Manager service initialization failed");
            await DisposeAsync();
            throw new Exception("Virtual Disk Manager initialization error, check inner exception.", e);
        }

        IsInitialized = true;
    }

    private async Task CreateDatabaseFileAsync()
    {
        // Create the Files
        var filePathName = BuildDatabasePathFileName();

        _fileHandle = File.OpenHandle(filePathName, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
        _fileSize = 0L;
        
        var c = _dbc;
        _log.LogInformation("Create Database '{DatabaseName}' in file '{FilePathName}'", c.DatabaseName, filePathName);

        using (var pa = await RequestPageAsync(0, true))
        {
            /*
            pa.SetPageDirty();
            var h = (RootFileHeader*)pa.PageAddress;
            StringExtensions.StoreString(HeaderSignature, h->HeaderSignature, 32);
            h->DatabaseFormatRevision = DatabaseFormatRevision;
            StringExtensions.StoreString(c.DatabaseName, h->DatabaseName, 64);
            */

            /*
            rootFileHeader->OccupancyMapSPI = OccupancySegmentRootPageIndex;
            _log.LogInformation("Initialize DiskPageAllocator service with root at page {PageId}", OccupancySegmentRootPageIndex);

            var lsm = (LogicalSegmentManager)_sp.GetRequiredService(typeof(LogicalSegmentManager));
            _occupancySegment = lsm.CreateOccupancySegment(OccupancySegmentRootPageIndex, PageBlockType.OccupancyMap, 1);
        
            // ReSharper disable InconsistentlySynchronizedField
            _occupancyMap = new BitmapL3(1, _occupancySegment);

            // The first two pages are already manually allocated
            _occupancyMap.SetL0(0);
            _occupancyMap.SetL0(1);
            // ReSharper restore InconsistentlySynchronizedField

            FlushToDiskAsync(false).Wait();
            */
            
            //OnDatabaseCreating(h);
        }
        
    }

    unsafe void OnDatabaseCreating(RootFileHeader* rootFileHeader)
    {
        var handler = DatabaseCreating;
        handler?.Invoke(this, new DatabaseEventArgs(rootFileHeader));
    }
    
    private Task LoadDatabaseFileAsync()
    {
        // Create the Files
        var filePathName = BuildDatabasePathFileName();
        _fileHandle = File.OpenHandle(filePathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous|FileOptions.RandomAccess);
        {
            using var fileStream = new FileStream(_fileHandle, FileAccess.Read);
            _fileSize = fileStream.Length;
        }

        return Task.CompletedTask;
    }

    public bool IsInitialized { get; private set; }
    public bool IsDisposed { get; private set; }
    public int ReferenceCounter { get; private set; }

    unsafe public ValueTask DisposeAsync()
    {
        if (IsDisposed || --ReferenceCounter != 0)
        {
            return ValueTask.CompletedTask;
        }
        _log.LogInformation("Disposing Virtual Disk Manager");

        //FlushToDiskAsync(false).Wait();

        _fileHandle.Dispose();

        if (_dbc.DeleteDatabaseOnDispose)
        {
            //DeleteDatabaseFile();
        }

        _memPagesInfo = null;

        _memPagesHandle.Free();
        _memPagesAddr = null;
        _memPages = null;

        IsDisposed = true;
        _log.LogInformation("Virtual Disk Manager disposed");
        
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PageAccessor> RequestPageAsync(int filePageIndex, bool exclusive)
    {
#if VERBOSELOGGING
        using var logId = LogContext.PushProperty("FilePageIndex", filePageIndex);
        using var logRW = LogContext.PushProperty("IsExclusive", exclusive);
        LogRequestPage(filePageIndex, exclusive);
#endif

        // Race condition can occur during TransitionPageToAccessAsync (e.g. waiting for the lock while the memory page is being reallocated for another disk page)
        // So we loop until we get the page in the right state
        while (true)
        {
            var memPageIndex = await FetchPageToMemoryAsync(filePageIndex, exclusive);
            var pi = _memPagesInfo[memPageIndex];
        
            if (await TransitionPageToAccessAsync(filePageIndex, pi, exclusive))
            {
                break;
            }
        }

        return default;
    }
    
    private async ValueTask<int> FetchPageToMemoryAsync(int filePageIndex, bool exclusive)
    {
        // Get the memory page from the cache, if it fails we allocate a new one
        if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out var memPageIndex) == false)
        {
            ++_debugInfo.MemPageCacheMiss;
            LogMemPageCacheMiss();

            // Page is not cached, we assign an available Memory Page to it
            memPageIndex = await AllocateMemoryPageAsync(filePageIndex);
            
            // Load the page from disk, if it's stored there already. (won't be the case for new pages)
            var pageOffset = filePageIndex * (long)PageSize;
            var loadPage = (pageOffset + PageSize) <= _fileSize;
            if (loadPage)
            {
                LogAllocatePageLoad();
                ++_debugInfo.ReadFromDiskCount;
                
                var pi = _memPagesInfo[memPageIndex];
                pi.SetIOCompletionTask(RandomAccess.ReadAsync(_fileHandle, _memPages.AsMemory(memPageIndex * PageSize, PageSize), pageOffset), PageIOMode.Read);
            }            
        }
        else
        {
            ++_debugInfo.MemPageCacheHit;
            LogMemPageCacheHit();
        }

#if VERBOSELOGGING
        using var logMemPageIndex = LogContext.PushProperty("MemPageIndex", memPageIndex);
#endif

        LogRequestPageFound();
        return memPageIndex;
    }

    private int AdvanceClockHand()
    {
        var curValue = _clockSweepCurrentIndex;
        var newValue = (curValue + 1) % _memPagesCount;
        while (Interlocked.CompareExchange(ref _clockSweepCurrentIndex, newValue, curValue) != curValue)
        {
            curValue = _clockSweepCurrentIndex;
            newValue = (curValue + 1) % _memPagesCount;
        }

        return curValue;
    }

    private async ValueTask<int> AllocateMemoryPageAsync(int filePageIndex)
    {
#if VERBOSELOGGING
        int loopCount = 0;
        DateTime start = DateTime.UtcNow;
#endif        
        AdaptiveWaiter waiter = null;
        
        while (true)
        {
            LogAllocatePageEnter();

            bool found = false;
            PageInfo pi = null;
            int memPageIndex = -1;

            // If we already have a MemPage fetch for the FilePage just before the one we allocate, then we try to take the MemPage that follows
            // We request FilePage 123, there's a FilePage 122 allocated to MemPage 34, then we try to allocate 35 for 123, which will allow, if needed,
            //  one file write operation for both pages
            if (filePageIndex > 0 && _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemPageIndex) && ((prevMemPageIndex + 1) < _memPagesCount))
            {
                memPageIndex = prevMemPageIndex + 1;
                pi = _memPagesInfo[memPageIndex];
                if (TryAcquire(pi))
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
                int maxAttempts = _memPagesCount * 2;

                while (attempts < maxAttempts)
                {
                    memPageIndex = AdvanceClockHand();
                    pi = _memPagesInfo[memPageIndex];

                    // If the counter is 0, the page is candidate for eviction, try to acquire it
                    if (pi.ClockSweepCounter == 0 && TryAcquire(pi))
                    {
                        found = true;
                        break;
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
                    maxAttempts = _memPagesCount;

                    while (attempts < maxAttempts)
                    {
                        memPageIndex = AdvanceClockHand();
                        pi = _memPagesInfo[memPageIndex];

                        // If the counter is 0, the page is candidate for eviction, try to acquire it
                        if (TryAcquire(pi))
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
                    // We'll get here basically if all memory pages are currently in use, so it's very unlikely, except of complete system usage overload
                    // The best (and easiest) thing is to wait and try again
                    LogPendingPageAllocation(filePageIndex, loopCount, DateTime.UtcNow - start);
                    loopCount++;

                    waiter ??= new AdaptiveWaiter();
                    await waiter.SpinAsync();
                    continue;
                }
            }

            pi.FilePageIndex = filePageIndex;
            
            ++_debugInfo.MemPageReallocationCount;
            LogAllocatePageFound(memPageIndex);

            if (_dbc.PagesDebugPattern)
            {
                var pageAddr = _memPages.AsMemory(memPageIndex * PageSize).Span.Cast<byte, int>();
                int i;
                for (i = 0; i < PagedMemoryMappedFile.PageHeaderSize >> 2; i++)
                {
                    pageAddr[i] = (filePageIndex << 16) | 0xFF00 | i;
                }

                for (int j = 0; j < PagedMemoryMappedFile.PageRawDataSize >> 2; j++, i++)
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
                pi.StateSyncRoot.Enter();
                pi.FilePageIndex = -1;
                pi.PageState = PageState.Free;
                pi.ResetIOCompletionTask();
                pi.IOMode = PageIOMode.None;
                pi.ResetClockSweepCounter();
                pi.ConcurrentSharedCounter = 0;
                pi.LockedByThreadId = 0;
                pi.StateSyncRoot.Exit();

                memPageIndex = newMemPageIndex;
                _debugInfo.MemPageReallocationCount--;
            }

            return memPageIndex;
        }
    }

    private bool TryAcquire(PageInfo info)
    {
        try
        {
            info.StateSyncRoot.Enter();

            if (info.PageState is PageState.Free or PageState.Idle)
            {
                info.ResetClockSweepCounter();
                info.FilePageIndex = -1;
                info.PageState = PageState.Allocating;
                Debug.Assert(info.ConcurrentSharedCounter == 0);
                Debug.Assert(info.LockedByThreadId == 0);
                Debug.Assert(info.IOMode == PageIOMode.None);
                return true;
            }
            else
            {
                return false;
            }
        }
        finally
        {
            info.StateSyncRoot.Exit();
        }
    }
    
    /// <summary>
    /// Transition the page from its current mode to Access (either shared or exclusive), blocking the call if needed.
    /// </summary>
    /// <param name="filePageIndex">Disk Page Id</param>
    /// <param name="pi">The corresponding PageInfo</param>
    /// <param name="exclusive"><c>true</c> if we are doing Exclusive access, otherwise it's Shared access</param>
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
    private async Task<bool> TransitionPageToAccessAsync(int filePageIndex, PageInfo pi, bool exclusive)
    {
#if VERBOSELOGGING
        int loopCount = 0;
        // ReSharper disable once TooWideLocalVariableScope
        PageState prevState;
        DateTime start = DateTime.UtcNow;
#endif        
        AdaptiveWaiter waiter = null;
        while (true)
        {
            try
            {
                // We want to change the state, so we need to acquire the lock
                pi.StateSyncRoot.Enter();
                prevState = pi.PageState;
                
                Debug.Assert(pi.PageState != PageState.Free);
                
                var memPageId = pi.MemPageIndex;
#if VERBOSELOGGING
                using var logId = LogContext.PushProperty("PageId", filePageIndex);
                using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
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
                // If the page is Allocating or Idle, we can transition to Shared or Exclusive very simply
                if (pi.PageState is PageState.Allocating or PageState.Idle)
                {
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
                    if (exclusive && Environment.CurrentManagedThreadId == pi.LockedByThreadId)
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
                pi.StateSyncRoot.Exit();
            }

            // We arrive here because we couldn't make the requested transition, in a leap of faith, we wait...and retry.
            // But we log it, because, ya know...
            LogTransitionWaitAndLoop(prevState, exclusive ? PageState.Exclusive : PageState.Shared, loopCount, DateTime.UtcNow - start);
            loopCount++;
            
            waiter ??= new AdaptiveWaiter();
            await waiter.SpinAsync();
        }
    }
    
    private string BuildDatabaseFileName() => $"{_dbc.DatabaseName}.bin";
    private string BuildDatabasePathFileName() => Path.Combine(_dbc.DatabaseAbsoluteDirectory, BuildDatabaseFileName());
    
    #region Logging helpers

    [Conditional("VERBOSELOGGING")]
    private void LogRequestPage(int pageId, bool doesWrite) => _log.LogDebug(10, "Request Disk Page: {PageId}, Write: {IsExclusive}", pageId, doesWrite);
    
    [Conditional("VERBOSELOGGING")]
    private void LogMemPageCacheHit() => _log.LogTrace(11, "MemPage Cache Hit");
    
    [Conditional("VERBOSELOGGING")]
    private void LogMemPageCacheMiss() => _log.LogTrace(12, "MemPage Cache Miss");
    
    [Conditional("VERBOSELOGGING")]
    private void LogRequestPageFound() => _log.LogTrace(13, "Request Page Found");
    
    [Conditional("VERBOSELOGGING")]
    private void LogRequestPageRace() => _log.LogTrace(14, "Request Page Race Condition (reallocation)");

    [Conditional("VERBOSELOGGING")]
    private void LogAllocatePageEnter() => _log.LogTrace(20, "Allocate Page Enter");
    
    [Conditional("VERBOSELOGGING")]
    private void LogAllocatePageSequential() => _log.LogTrace(22, "Allocate Page Sequential");
    
    [Conditional("VERBOSELOGGING")]
    private void LogAllocatePageFound(int memPageIndex) => _log.LogTrace(24, "Allocate Page Found {MemPageId}", memPageIndex);
    
    [Conditional("VERBOSELOGGING")]
    private void LogAllocatePageLoad() => _log.LogTrace(25, "Allocate Page Load From Disk");

    [Conditional("VERBOSELOGGING")]
    private void LogFlushToDiskStart() => _log.LogTrace(30, "Flush To Disk Start");
    
    [Conditional("VERBOSELOGGING")]
    private void LogFlushToDiskConcurrentFlush() => _log.LogTrace(31, "Flush To Disk exiting due to concurrent Flush");
    
    [Conditional("VERBOSELOGGING")]
    private void LogFlushToDiskEnd() => _log.LogTrace(32, "Flush To Disk End");
    
    [Conditional("VERBOSELOGGING")]
    private void LogFlushToDiskWaitPrevious() => _log.LogTrace(33, "Wait Previous Flush To Complete");
    
    /*
    [Conditional("VERBOSELOGGING")]
    private void LogFlushToDiskWritePage(SegmentInfo segment, List<int> fragList, bool isFrag)
    {
        if (isFrag)
        {
            _log.LogTrace(34, "Write fragmented segment StartDiskPage: {StartDiskPageId}, PageCount: {PageCount}, MemPages: {MemPageIdList}",
                segment.StartPageId, segment.PageCount, fragList.GetRange(segment.StartMemPageId, segment.PageCount));
        }
        else
        {
            _log.LogTrace(34, "Write contiguous segment StartDiskPage: {StartDiskPageId}, PageCount: {PageCount}, FirstMemPage: {StartMemPageId}",
                segment.StartPageId, segment.PageCount, segment.StartMemPageId);
        }

    }

    [Conditional("VERBOSELOGGING")]
    private void LogFlushToDiskPages(SortedDictionary<uint, int> pagesToWrite)
    {
        _log.LogDebug(35, "Flush pages {DiskPageList}", pagesToWrite);
    }
    */

    // TransitionPageTo
    [Conditional("VERBOSELOGGING")]
    private void LogTransitionSuccessful(PageState prevMode, PageState newMode) => 
        _log.LogTrace(40, "Transition completed from {PrevMode} to {NewMode}", prevMode, newMode);

    [Conditional("VERBOSELOGGING")]
    private void LogTransitionFailed(PageState newMode) => 
        _log.LogTrace(41, "Transition completed to {NewMode} failed due to reallocation", newMode);


    [Conditional("VERBOSELOGGING")]
    private void LogTransitionWaitAndLoop(PageState prevMode, PageState newMode, int loopCount, TimeSpan duration) => 
        _log.LogTrace(42, "Transition waiting/reloop from {prevNode} to {NewMode} loop count: {loopCount}, duration: {duration}", prevMode, newMode, loopCount, duration);

 
    [Conditional("VERBOSELOGGING")]
    private void LogPendingPageAllocation(int filePageIndex, int loopCount, TimeSpan duration) => 
        _log.LogTrace(43, "Page Allocation pending/reloop for page {filePageIndex} loop count: {loopCount}, duration: {duration}", filePageIndex, loopCount, duration);

    #endregion
}
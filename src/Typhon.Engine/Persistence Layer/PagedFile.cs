using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Serilog.Context;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

public class ChangeSet
{
    private readonly PagedFile _owner;
    private readonly HashSet<int> _changedMemoryPageIndices;
    private Task _saveTask;

    public ChangeSet(PagedFile owner)
    {
        _owner = owner;
        _changedMemoryPageIndices = new HashSet<int>();
    }

    public void Add(PageAccessor accessor)
    {
        if (_changedMemoryPageIndices.Add(accessor.MemPageIndex))
        {
            _owner.IncrementDirty(accessor.MemPageIndex);
        }
    }

    public Task SaveChangesAsync()
    {
        if (_changedMemoryPageIndices.Count == 0)
        {
            return Task.CompletedTask;
        }
        
        var memPageIndices = _changedMemoryPageIndices.ToArray();
        var tasks = new List<Task>(_changedMemoryPageIndices.Count);
        foreach (var memPageIndex in memPageIndices)
        {
            tasks.Add(_owner.SavePage(memPageIndex).AsTask());
        }

        _changedMemoryPageIndices.Clear();
        
        _saveTask = Task.WhenAll(tasks).ContinueWith(_ =>
        {
            foreach (int memPageIndex in memPageIndices)
            {
                _owner.DecrementDirty(memPageIndex);
            }
        });
        return _saveTask;
    }

    public void Reset()
    {
        _changedMemoryPageIndices.Clear();
        _saveTask = null;
    }
}

[PublicAPI]
public class PagedFile : IAsyncInitializable, IAsyncDisposable
{
    public const int DefaultMemPageCount = 256;

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

    /// <summary>
    /// Some real-time metrics.
    /// </summary>
    /// <remarks>
    /// Some fields are documented as "approximately" because they are not updated atomically, so in rare case of read/modify/write by concurrent threads
    ///  some updates will be missed.
    /// </remarks>
    [PublicAPI]
    internal class Metrics
    {
        /// Approximately the number of page requests that successfully hit the cache 
        public int MemPageCacheHit;
        
        /// Approximately the number of page requests that missed the cache and had to be allocated again
        public int MemPageCacheMiss;
        
        /// Approximately the number of pages that were read from disk
        public int ReadFromDiskCount;
        
        /// Approximately the number of pages that were written to disk
        public int WriteToDiskCount;
        
        /// The exact number of Memory Pages that are currently free (and can be used to allocate new file pages).
        public int FreeMemPageCount;
        
        /// 
        public int TotalMemPageAllocatedCount;
    }

    private Metrics _metrics;

    internal Metrics GetMetrics() => _metrics;

    #endregion

    internal enum PageState
    {
        Free         = 0,   // The page is free, yet to be allocated.
        Allocating   = 1,   // The page is being allocating by a call to AllocateMemoryPage.
        Idle         = 2,   // The page is allocated but idle (nobody is accessing it). So it can be re-used or reallocated to another File Page.
        Shared       = 3,   // The page is allocated and accessed by one/many concurrent threads,
                            //  the ConcurrentSharedCounter will indicate how many concurrent access we have.
        Exclusive    = 4,   // The page is allocated and accessed exclusively by a given thread indicated by LockedByThreadId.
        IdleAndDirty = 5,   // The page access was released but its content yet need to be written to disk.
    }

    internal class PageInfo
    {
        private const int ClockSweepMaxValue = 5;
        
        public readonly int MemPageIndex;
        public int FilePageIndex;
        public int ClockSweepCounter => _clockSweepCounter;
        public int DirtyCounter;

        public SmallAccessControl StateSyncRoot;
        public PageState PageState;                 // Must always be changed under StateSyncRoot lock
        public int LockedByThreadId;                // Same
        public int ConcurrentSharedCounter;         // Same

        private int _clockSweepCounter;
        private Lazy<Task<int>> _ioReadTask;

        public void SetIOReadTask(ValueTask<int> task) => _ioReadTask = new Lazy<Task<int>>(task.AsTask);

        public Task<int> IOReadTask => _ioReadTask?.Value ?? Task.FromResult(0);

        public void ResetIOCompletionTask() => _ioReadTask = null;

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

        _metrics = new Metrics { FreeMemPageCount = _memPagesCount };
    }

    // This method is used for internal / testing purposes only, the user knows what (s)he is doing
    internal async Task ResetAsync()
    {
        LogResetAsync();
        
        _memPages.AsSpan().Cast<byte, ulong>().Clear();
        for (int i = 0; i < _memPagesCount; i++)
        {
            _memPagesInfo[i] = new PageInfo(i);
        }
        _clockSweepCurrentIndex = 0;

        _memPageIndexByFilePageIndex.Clear();
        
        // Reset the File
        if (_fileHandle != null)
        {
            _fileHandle.Dispose();
            _fileHandle = null;
        }
        _fileSize = 0L;
        
        // Reset the counters
        ReferenceCounter = 0;
        IsInitialized = false;
        IsDisposed = false;

        _metrics = new Metrics { FreeMemPageCount = _memPagesCount };

        await InitializeAsync();
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
            var fi = new FileInfo(filePathName);
            _fileSize = fi.Length;
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

    /// <summary>
    /// Request access to a File Page, either Shared or Exclusive.
    /// </summary>
    /// <param name="filePageIndex">The index of the File Page to access.</param>
    /// <param name="exclusive"><c>true</c> for Exclusive access, <c>false</c> for Shared.</param>
    /// <returns>The accessor</returns>
    /// <remarks>
    /// This method goes async if the Memory Page is not allocated and there are no free Memory Pages available or if the requested access requires to
    ///  wait for the transition to be made (one or many threads hold an access not compatible with the one we request).
    /// If the File Page is being loading from disk to memory, the read is completely independent of this operation, the <see cref="PageAccessor"/> will
    ///  wait for it upon its first content access.
    /// Going async on this call should be very rare.
    /// </remarks>
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
            var memPageIndex = await FetchPageToMemoryAsync(filePageIndex);
            var pi = _memPagesInfo[memPageIndex];
        
            if (await TransitionPageToAccessAsync(filePageIndex, pi, exclusive))
            {
                return new PageAccessor(this, pi);
            }
        }
    }
    
    /// <summary>
    /// Fetch the requested File Page to memory, allocating a Memory Page if needed.
    /// </summary>
    /// <param name="filePageIndex">Index of the File Page to fetch</param>
    /// <returns>The index of the Memory Page for the request File Page</returns>
    /// <remarks>
    /// This method goes async if the Memory Page is not allocated and there are no free Memory Pages available.
    /// </remarks>
    private async ValueTask<int> FetchPageToMemoryAsync(int filePageIndex)
    {
        // Get the memory page from the cache, if it fails we allocate a new one
        if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out var memPageIndex) == false)
        {
            ++_metrics.MemPageCacheMiss;
            LogMemPageCacheMiss();

            // Page is not cached, we assign an available Memory Page to it
            memPageIndex = await AllocateMemoryPageAsync(filePageIndex);
            
            // Load the page from disk, if it's stored there already. (won't be the case for new pages)
            // The load is async and not part of the returned task but stored in the PageInfo
            var pageOffset = filePageIndex * (long)PageSize;
            var loadPage = (pageOffset + PageSize) <= _fileSize;
            if (loadPage)
            {
                LogAllocatePageLoad();
                ++_metrics.ReadFromDiskCount;
                
                var pi = _memPagesInfo[memPageIndex];
                pi.SetIOReadTask(RandomAccess.ReadAsync(_fileHandle, _memPages.AsMemory(memPageIndex * PageSize, PageSize), pageOffset));
            }            
        }
        else
        {
            ++_metrics.MemPageCacheHit;
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

    /// <summary>
    /// Allocate a Memory Page for the given File Page Index.
    /// </summary>
    /// <param name="filePageIndex">The file page index to mount to memory</param>
    /// <returns>The Memory Page Index</returns>
    /// <remarks>
    /// This method goes async if no Memory Page is available, it will wait and loop until it finds one.
    /// Use the clock-sweep algorithm to find a free Memory Page.
    /// </remarks>
    private async ValueTask<int> AllocateMemoryPageAsync(int filePageIndex)
    {
#if VERBOSELOGGING
        int loopCount = 0;
        DateTime start = DateTime.UtcNow;
#endif        
        AdaptiveWaiter waiter = null;
        
        LogAllocatePageEnter();
        while (true)
        {

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
#if VERBOSELOGGING
                    
                    // We'll get here basically if all memory pages are currently in use, so it's very unlikely, except of complete system usage overload
                    // The best (and easiest) thing is to wait and try again.
                    
                    // /!\ BUT, it may not be enough to solve all the issues, some perfectly good usages can lead to this situation, if that happens we need to
                    //      1. Make sure it doesn't happen by making sure the upper layer monitors the metrics and throttles the usages to prevent catastrophe.
                    //      2. Change this implementation and allocate emergency resources to prevent the starvation, but honestly it feels to me like we
                    //         are just delaying the inevitable or lowering the chances of catastrophe.
                    //
                    // TL;DR: what we have right now and need to do:
                    // The upper layer must monitor the metrics and throttle the usages to prevent starvation.

                    if (loopCount.IsPowerOf2())
                    {
                        LogPendingPageAllocation(filePageIndex, loopCount, DateTime.UtcNow - start);
                    }
                    loopCount++;
#endif
                    waiter ??= new AdaptiveWaiter();
                    await waiter.SpinAsync();
                    continue;
                }
            }

            pi.FilePageIndex = filePageIndex;
            
            ++_metrics.TotalMemPageAllocatedCount;
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
                pi.ResetClockSweepCounter();
                pi.ConcurrentSharedCounter = 0;
                pi.LockedByThreadId = 0;
                pi.StateSyncRoot.Exit();

                memPageIndex = newMemPageIndex;
                _metrics.TotalMemPageAllocatedCount--;
            }

            return memPageIndex;
        }
    }

    private bool TryAcquire(PageInfo info)
    {
        try
        {
            info.StateSyncRoot.Enter();

            // PageAccessor is responsible to reset the IOMode from read to none for a loading page, but only if the user creates and uses one, (which is
            //  most of the cases, but not all o them). So we take the opportunity to reset the IOMode here, if needed.
            if (info.IOReadTask!=null && info.IOReadTask.IsCompletedSuccessfully)
            {
                info.ResetIOCompletionTask();
            }
            
            if (info.PageState is PageState.Free or PageState.Idle)
            {
                info.ResetClockSweepCounter();
                info.FilePageIndex = -1;
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
        DateTime start = DateTime.UtcNow;
#endif        
        PageState prevState;
        AdaptiveWaiter waiter = null;
        while (true)
        {
            try
            {
                // We want to change the state, so we need to acquire the lock
                pi.StateSyncRoot.Enter();
                
                Debug.Assert(pi.PageState != PageState.Free);
                
#if VERBOSELOGGING
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
#if VERBOSELOGGING
            LogTransitionWaitAndLoop(prevState, exclusive ? PageState.Exclusive : PageState.Shared, loopCount, DateTime.UtcNow - start);
            loopCount++;
#endif
            
            waiter ??= new AdaptiveWaiter();
            await waiter.SpinAsync();
        }
    }

    public ChangeSet CreateChangeSet() => new(this);

    internal bool TryPromoteToExclusive(int pageId, PageInfo pi, out PageState previousMode)
    {
        throw new NotImplementedException();
    }

    internal void DemoteExclusive(PageInfo pi, PageState previousMode)
    {
        throw new NotImplementedException();
    }

    internal void IncrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        Debug.Assert(pi.PageState is PageState.Shared or PageState.Exclusive, "We can't increment the dirty counter for a page that is not Shared or Exclusive.");
        
        pi.StateSyncRoot.Enter();
        ++pi.DirtyCounter;
        pi.StateSyncRoot.Exit();
    }
    
    internal void DecrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        pi.StateSyncRoot.Enter();
        if (--pi.DirtyCounter == 0 && pi.PageState == PageState.IdleAndDirty)
        {
            pi.PageState = PageState.Idle;
            Interlocked.Increment(ref _metrics.FreeMemPageCount);
        }
        pi.StateSyncRoot.Exit();
    }

    internal ValueTask SavePage(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        
        // Save the page to disk
        var filePageIndex = pi.FilePageIndex;
        var pageOffset = filePageIndex * (long)PageSize;
        var pageData = _memPages.AsMemory(memPageIndex * PageSize, PageSize);

        ++_metrics.WriteToDiskCount;
        return RandomAccess.WriteAsync(_fileHandle, pageData, pageOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal void TransitionPageFromAccessToIdle(PageInfo pi)
    {
#if VERBOSELOGGING
        using var logId = LogContext.PushProperty("FilePageIndex", pi.FilePageIndex);
        using var logMemPageId = LogContext.PushProperty("MemPageIndex", pi.MemPageIndex);
#endif
        try
        {
            pi.StateSyncRoot.Enter();
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
            pi.StateSyncRoot.Exit();
        }
    }
    
    internal unsafe byte* GetMemPageAddress(int memPageIndex) => &_memPagesAddr[memPageIndex * (long)PageSize];
    
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

 
    [Conditional("VERBOSELOGGING")]
    private void LogResetAsync() => 
        _log.LogTrace(44, "Resetting PagedFile instance !!!");

    #endregion
}
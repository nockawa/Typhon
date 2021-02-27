using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine
{
    #region Event Args

    public enum PageClearMode
    {
        None = 0,
        Header = 1,
        WholePage = 2
    }

    internal unsafe class DatabaseEventArgs : EventArgs
    {
        public DatabaseEventArgs(RootFileHeader* rootFileHeader)
        {
            Header = rootFileHeader;
        }
        public RootFileHeader* Header { get; }
    }

    #endregion

    public partial class VirtualDiskManager : IInitializable, IDisposable
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

        #endregion

        #region private fields

        private readonly DatabaseConfiguration _dbc;
        private readonly ILogger<VirtualDiskManager> _log;
        private readonly TimeManager _tmg;

        private FileStream _file;
        private long _fileSize;

        private byte[] _memPages;
        private GCHandle _memPagesHandle;
        unsafe private byte* _memPagesAddr;

        private readonly int _memPagesCount;
        private int _memPagesUsedCount;
        private PageInfo[] _memPagesInfo;
        private readonly PageActivity[] _memPagesActivities;
        private volatile ConcurrentCollection<PageInfo> _mainLFUD;
        private volatile ConcurrentCollection<PageInfo> _backgroundLFUD;
        private int _mainLRUUsageCounter;
        private readonly int[] _keysToSort;
        private readonly int[] _valuesToSort;
        private float _decayT0;
        private float _decayT1;

        private volatile int _flushRevision;
        private volatile Task _lastFlushTask;
        private readonly object _flushLock;

        private int _writeThreadCount;
        private byte[] _writeCache;
        private GCHandle _writeCacheHandle;
        unsafe private byte* _writeCacheAddr;
        private int _writeCachePageCount;
        private readonly ConcurrentBag<int> _writePagePool;

        private readonly ConcurrentDictionary<uint, int> _memPageIdByPageId;

        private readonly ConcurrentBitmapL3 _dirtyPages;

        #endregion

        #region Debug Info

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
            _debugInfo.FreeMemPageCount = _memPagesCount - _memPageIdByPageId.Count;
            return ref _debugInfo;
        }

        #endregion

        #region Internal types and definitions

        internal enum PagesAccessMode : byte
        {
            Idle         = 0,                 // The page is idle
            Read         = 1,                 // The page is accessed in one/many concurrent read, the ConcurrentReadCounter will indicate how many concurrent read we have
            Write        = 2,                 // The page is accessed in an exclusive read/write by a thread.
        }

        internal enum PageIOMode : byte
        {
            None = 0,
            Loading    = 1,                // The page is being loaded from disk
            Saving     = 2,                // The page is being saved into disk
        }

        internal class PageInfo
        {
            public PageInfo(int memPageId)
            {
                SyncRoot = new SemaphoreSlim(1, 1);
                MemPageId = memPageId;
                PageId = uint.MaxValue;
            }
            public SemaphoreSlim SyncRoot;
            public int LockedByThreadId;
            public volatile uint PageId;       // ID of the Disk Page this memory page stores data
            public readonly int MemPageId;
            public PagesAccessMode AccessMode;
            public PageIOMode IOMode;
            public int ConcurrentUseCounter;   // Concurrent use counter
            public Task IOCompletionTask;
        }

        internal struct PageActivity
        {
            public int LastRequestTime;
            public float HitCount;
        }

        [DebuggerDisplay("Start Page = {" + nameof(StartPageId) + ("}, Count = {" + nameof(PageCount) + "}"))]
        private struct SegmentInfo
        {
            public void Init(uint startPageId, int startMemPageId, int pageCount)
            {
                StartPageId = startPageId;
                StartMemPageId = startMemPageId;
                PageCount = pageCount;
            }
            public uint StartPageId;
            public int PageCount;
            public int StartMemPageId;
            public int WritePageIndex;

        }
        #endregion

        #region Lifetime

        unsafe public VirtualDiskManager(IConfiguration<DatabaseConfiguration> dc, TimeManager timeManager, ILogger<VirtualDiskManager> logger)
        {
            try
            {
                _dbc = dc.Value;
                _tmg = timeManager;

                _log = logger;

                _flushLock = new object();

                // Create the cache of the page, pin it and keeps its address
                var cacheSize = _dbc.DatabaseCacheSize;
                _memPages = new byte[cacheSize];

                _memPagesHandle = GCHandle.Alloc(_memPages, GCHandleType.Pinned);
                _memPagesAddr = (byte*)_memPagesHandle.AddrOfPinnedObject();

                // Create the Memory Page info table
                _memPagesCount = (int)(cacheSize >> PageSizePow2);
                _memPagesUsedCount = 0;
                var pageCount = _memPagesCount;
                _memPagesInfo = new PageInfo[pageCount];
                _decayT0 = 1;
                _decayT1 = 10;

                _writeThreadCount = (int)(Environment.ProcessorCount * _dbc.WriteThreadRatio);
                _writeCache = new byte[_dbc.WriteCacheSize];
                _writeCacheHandle = GCHandle.Alloc(_writeCache, GCHandleType.Pinned);
                _writeCacheAddr = (byte*)_writeCacheHandle.AddrOfPinnedObject();
                _writeCachePageCount = _dbc.WriteCacheSize / WriteCachePageSize;
                _writePagePool = new ConcurrentBag<int>();
                Array.Fill(_writeCache, (byte)0xFF);
                for (int i = 0; i < _writeCachePageCount; i++)
                {
                    _writePagePool.Add(i);
                }

                _debugInfo.FreeMemPageCount = pageCount;

                _memPageIdByPageId = new ConcurrentDictionary<uint, int>();
                _memPagesActivities = new PageActivity[pageCount];

                _mainLFUD = new ConcurrentCollection<PageInfo>(pageCount);
                _backgroundLFUD = new ConcurrentCollection<PageInfo>(pageCount);
                _keysToSort = new int[pageCount];
                _valuesToSort = new int[pageCount];
                _dirtyPages = new ConcurrentBitmapL3(pageCount);
                
                // Fill the MRU with all the pages as marked used for frame 0, init the PageInfo
                for (int i = 0; i < pageCount; i++)
                {
                    var pi = new PageInfo(i);
                    _memPagesInfo[i] = pi;
                    _mainLFUD.Add(pi);
                }
            }
            catch (Exception e)
            {
                _log.LogError(e, "Virtual Disk Manager service initialization failed");
                Dispose();
                throw new Exception("Virtual Disk Manager initialization error, check inner exception.", e);
            }
        }

        public void Initialize()
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
                    CreateDatabaseFile();
                }
                else
                {
                    LoadDatabaseFile();
                }
                _log.LogInformation("Virtual Disk Manager service initialized successfully");
            }
            catch (Exception e)
            {
                _log.LogError(e, "Virtual Disk Manager service initialization failed");
                Dispose();
                throw new Exception("Virtual Disk Manager initialization error, check inner exception.", e);
            }

            IsInitialized = true;
        }

        unsafe public void Dispose()
        {
            if (IsDisposed || --ReferenceCounter != 0)
            {
                return;
            }

            _log.LogInformation("Disposing Virtual Disk Manager");

            FlushToDiskAsync(false).Wait();

            _file.Dispose();

            if (_dbc.DeleteDatabaseOnDispose)
            {
                DeleteDatabaseFile();
            }

            foreach (var pi in _memPagesInfo)
            {
                pi.SyncRoot.Dispose();
            }

            _memPagesInfo = null;

            _memPagesHandle.Free();
            _memPagesAddr = null;
            _memPages = null;

            _writeCacheHandle.Free();
            _writeCacheAddr = null;
            _writeCache = null;

            IsDisposed = true;
            _log.LogInformation("Virtual Disk Manager disposed");
        }

        public bool IsInitialized { get; private set; }
        public bool IsDisposed { get; private set; }
        public int ReferenceCounter { get; private set; }

        // Only for debug/unit test purpose. Should be no operating pending or activity on other threads regarding this service
        internal void ResetDiskManager()
        {
            FlushToDiskAsync(false).Wait();

            _log.LogInformation("Virtual Disk Manager IS RESET");
            Array.Clear(_memPages, 0, _memPages.Length);
            _debugInfo = default;
            _debugInfo.FreeMemPageCount = _memPagesCount;
            
            Array.Clear(_writeCache, 0, _dbc.WriteCacheSize);

            _memPageIdByPageId.Clear();

            Array.Clear(_memPagesActivities, 0, _memPagesActivities.Length);
            _dirtyPages.Reset();

            // Fill the MRU with all the pages as marked used for frame 0
            _mainLFUD.Clear();
            _backgroundLFUD.Clear();
            for (int i = 0; i < _memPagesCount; i++)
            {
                var pi = _memPagesInfo[i];
                pi.SyncRoot.Dispose();
                pi.SyncRoot = new SemaphoreSlim(1, 1);
                pi.AccessMode = PagesAccessMode.Idle;
                pi.IOMode = PageIOMode.None;
                pi.IOCompletionTask = null;
                pi.PageId = uint.MaxValue;
                pi.ConcurrentUseCounter = 0;
                _mainLFUD.Add(pi);
            }

            // Init or load the file
            _file.Dispose();
            var filePathName = BuildDatabasePathFileName();
            var fi = new FileInfo(filePathName);
            var isCreationMode = fi.Exists == false;
            if (isCreationMode)
            {
                CreateDatabaseFile();
            }
            else
            {
                LoadDatabaseFile();
            }
        }

        #endregion

        #region Public API

        unsafe public ReadOnlyPageAccessor RequestPageReadOnly(uint pageId)
        {
            var memPageId = RequestPage(pageId, false);

            var pi = _memPagesInfo[memPageId];
            if (pi.IOCompletionTask != null && pi.IOCompletionTask.IsCompleted == false)
            {
                pi.IOCompletionTask.Wait();
                pi.IOMode = PageIOMode.None;
                pi.IOCompletionTask = null;
            }
            return new ReadOnlyPageAccessor(this, pi, &_memPagesAddr[memPageId* (long)PageSize]);
        }

        unsafe public ReadWritePageAccessor RequestPageReadWrite(uint pageId)
        {
            var memPageId = RequestPage(pageId, true);
            
            var pi = _memPagesInfo[memPageId];
            if (pi.IOCompletionTask != null && pi.IOCompletionTask.IsCompleted == false)
            {
                pi.IOCompletionTask.Wait();
                pi.IOMode = PageIOMode.None;
                pi.IOCompletionTask = null;
            }

            return new ReadWritePageAccessor(this, pi, &_memPagesAddr[memPageId* (long)PageSize]);
        }

        public void DeleteDatabaseFile()
        {
            var fi = new FileInfo(BuildDatabasePathFileName());
            if (fi.Exists)
            {
                fi.Delete();
            }
        }

        #endregion

        #region Internal API

        internal bool GetPageInfoOf(uint pageId, out PageInfo pi)
        {
            if (_memPageIdByPageId.TryGetValue(pageId, out var memPageId) == false)
            {
                pi = null;
                return false;
            }

            pi = _memPagesInfo[memPageId];

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void TransitionPageFromAccessToIdle(uint pageId, PageInfo pi)
        {
#if VERBOSELOGGING
            using var logId = LogContext.PushProperty("PageId", pi.PageId);
            using var logMemPageId = LogContext.PushProperty("MemPageId", pi.MemPageId);
#endif
            lock (pi)
            {
                var pageState = pi.AccessMode;
                Debug.Assert(pageState==PagesAccessMode.Read || pageState==PagesAccessMode.Write);

                if (--pi.ConcurrentUseCounter != 0)
                {
                    return;
                }

                pi.LockedByThreadId = 0;
                pi.AccessMode = PagesAccessMode.Idle;
                LogTransitionSuccessful(pageState, PagesAccessMode.Idle);
            }
            pi.SyncRoot.Release();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryTransitionPageToAccess(uint pageId, PageInfo pi)
        {
            if (pi.IOCompletionTask != null && pi.IOCompletionTask.IsCompleted == false)
            {
                return false;
            }

            var prevPageId = pi.PageId;
            if (pi.SyncRoot.Wait(0) == false)
            {
                // Couldn't get immediate ownership, return with false
                return false;
            }

            // Check if the Memory Page still point to the Disk Page we want
            if (prevPageId != pi.PageId || pageId != pi.PageId)
            {
                pi.SyncRoot.Release();
                return false;
            }

            // Set to write to prevent concurrent reads
            pi.AccessMode = PagesAccessMode.Write;
            pi.ConcurrentUseCounter = 1;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TransitionPageToAccess(uint pageId, PageInfo pi, bool doesWrite, bool reallocate)
        {
            var memPageId = pi.MemPageId;
#if VERBOSELOGGING
            using var logId = LogContext.PushProperty("PageId", pageId);
            using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
#endif
            Task waitIO = null;
            lock (pi)
            {
                // Safeguard, now we are under lock, check the MemPage is still targeting the PageId we want
                if ((reallocate == false) && (pageId != pi.PageId))
                {
                    LogTransitionFailed(doesWrite ? PagesAccessMode.Write : PagesAccessMode.Read);
                    return false;
                }

                // Concurrent ReadOnly mode?
                if ((reallocate == false) && (pi.AccessMode == PagesAccessMode.Read) && (doesWrite == false))
                {
                    ++pi.ConcurrentUseCounter;
                    ++_memPagesActivities[memPageId].HitCount;
                    return true;
                }

                // Check if we have to wait for IO operation to complete (must wait out of the lock)
                if (pi.IOMode != PageIOMode.None && pi.IOCompletionTask != null && pi.IOCompletionTask.IsCompleted == false)
                {
                    waitIO = pi.IOCompletionTask;
                }
            }

            var prevPageId = pi.PageId;

            // Wait IO to complete
            waitIO?.Wait();

            // Check for re-entrant Access
            var curThread = Thread.CurrentThread.ManagedThreadId;

            // This thread currently own read/write access on the page ?
            if (curThread == pi.LockedByThreadId)
            {
                // We are loose, if we want a read-only access and the page is already in read/write, we assume the user knows
                //  what's (s)he's doing (good luck!).
                ++pi.ConcurrentUseCounter;
                return true;

            }

            // Wait for exclusive access to the MemPage
            pi.SyncRoot.Wait();

            if ((prevPageId != pi.PageId) || (reallocate && IsMemPageDirty(memPageId)))
            {
                pi.SyncRoot.Release();
                return false;
            }

            // Exclusive access is only when there's write
            if (doesWrite)
            {
                pi.LockedByThreadId = curThread;
            }

            // Reset if needed
            if (waitIO != null)
            {
                pi.IOMode = PageIOMode.None;
            }

            var prevMode = pi.AccessMode;

            lock (pi)
            {
                pi.IOCompletionTask = null;
                pi.AccessMode = doesWrite ? PagesAccessMode.Write : PagesAccessMode.Read;
                pi.ConcurrentUseCounter = 1;
                ++_memPagesActivities[memPageId].HitCount;
                // Tick is a 64bits value but we want to store in 32bits, so we shift 20bits to the right to have a precision of 100ms,
                //  which is enough for what we want to do
                _memPagesActivities[memPageId].LastRequestTime = (int)(DateTime.UtcNow.Ticks >> 20);

                if (reallocate)
                {
                    if (pi.PageId == uint.MaxValue)
                    {
                        Interlocked.Increment(ref _memPagesUsedCount);
                    }
                    _memPageIdByPageId.TryRemove(pi.PageId, out _);
                    pi.PageId = pageId;
                    _memPagesActivities[memPageId].HitCount = 1;

                    _memPageIdByPageId.TryAdd(pageId, memPageId);
                }

                // Mark the page as accessed in the access map
                if (doesWrite)
                {
                    _dirtyPages.Set(memPageId);
                }
            }
            LogTransitionSuccessful(prevMode, pi.AccessMode);

            return true;
        }

        unsafe internal Task FlushToDiskAsync(bool updateLFUD)
        {
            var flushRevision = _flushRevision;

            LogFlushToDiskStart();

            // Prevent concurrent Flush
            lock (_flushLock)
            {
                // If another thread complete a flush when we were waiting for the lock, just return the task resulting from it
                if (flushRevision < _flushRevision)
                {
                    LogFlushToDiskConcurrentFlush();

                    return _lastFlushTask ?? Task.CompletedTask;
                }

#if VERBOSELOGGING
                using var logFlushRevision = LogContext.PushProperty("FlushRevision", _flushRevision);
#endif

                // Unlikely, but we don't want to fill the WriteBuffer that are maybe still being used by
                //  the WriteAsync of the previous flush
                if (_lastFlushTask!=null && _lastFlushTask.IsCompleted == false)
                {
                    LogFlushToDiskWaitPrevious();
                    _lastFlushTask.Wait();
                }

                // Parse the Dirty Map, detect the pages to write, add them into a sorted dictionary
                var pagesToWrite = new SortedDictionary<uint, int>();
                var maxPage = 0UL;

                foreach (var curI in _dirtyPages)
                {
                    var pi = _memPagesInfo[curI];

                    // Try to get ownership on the page, skip it if we can't
                    if (TryTransitionPageToAccess(pi.PageId, pi) == false)
                    {
                        continue;
                    }

                    pagesToWrite.Add(pi.PageId, curI);
                    maxPage = Math.Max(maxPage, pi.PageId);
                }

                LogFlushToDiskPages(pagesToWrite);

                // Update the file length if needed. We don't want the write operation to perform several SetLength because this is a
                //  significant performance hit.
                var newFileLength = (long)(maxPage + 1) * (long)PageSize;
                if (newFileLength > _file.Length)
                {
                    _file.SetLength(newFileLength);
                    _fileSize = newFileLength;
                }

                var issuedIOP = new ConcurrentBag<Task>();
                var fragSegments = new List<SegmentInfo>();
                var contiguousSegments = new List<SegmentInfo>();
                var fragMemPageIdList = new List<int>();
                int[] fragMemPageIdArray = null;

                void IssueWrite(SegmentInfo segment, bool isFrag)
                {
                    LogFlushToDiskWritePage(segment, fragMemPageIdList, isFrag);

                    var pageSize = PageSize;
                    var writePageAddr = _writeCacheAddr + (segment.WritePageIndex * WriteCachePageSize);

                    // Transition the pages we write to Saving
                    for (int i = 0; i < segment.PageCount; i++)
                    {
                        var memPageId = isFrag ? fragMemPageIdArray[segment.StartMemPageId+i] : (segment.StartMemPageId + i);
                        var pageSrc = _memPagesAddr + (memPageId * (long)PageSize);

                        // Only update the header if it's not the PageId[0] = (database file header, which has nothing to do with a Page Header)
                        if (segment.StartPageId + i > 0)
                        {
                            ref var h = ref new Span<PageBaseHeader>(pageSrc, PageHeaderSize).GetPinnableReference();
                            ++h.ChangeRevision;
                        }

                        if (isFrag)
                        {
                            Buffer.MemoryCopy(pageSrc, writePageAddr + (i * pageSize), pageSize, pageSize);
                        }
                    }

                    byte[] src = null;
                    int srcOffset = 0;
                    if (isFrag)
                    {
                        src = _writeCache;
                        srcOffset = segment.WritePageIndex * WriteCachePageSize;
                    }
                    else
                    {
                        src = _memPages;
                        srcOffset = segment.StartMemPageId * PageSize;
                    }

                    // Issue the async write
                    Task task;
                    lock (_file)
                    {
#if VERBOSELOGGING
                        _log.LogTrace("Write Page StartPageId {PageId}, MemPageId {MemPageId}", segment.StartPageId, segment.StartMemPageId);
#endif
                        _file.Position = segment.StartPageId * (long)PageSize;
                        task = _file.WriteAsync(src, srcOffset, segment.PageCount * PageSize);
                    }

                    // Update the IO task map
                    for (int i = 0; i < segment.PageCount; i++)
                    {
                        var memPageId = isFrag ? fragMemPageIdArray[segment.StartMemPageId+i] : segment.StartMemPageId + i;
                        var pi = _memPagesInfo[memPageId];
                        pi.IOCompletionTask = task;
                        ClearMemPageDirty(memPageId);
                        pi.SyncRoot.Release();                      // Release control
                    }

                    ++_debugInfo.WriteToDiskCount;
                    _debugInfo.PagesWrittenCount += segment.PageCount;

                    issuedIOP.Add(task);
                }

                // The sorted dictionary will list in increasing order the Page to write and their corresponding MemPage. We want to optimize the
                //  number of Write calls, for this we do (in order of preference):
                // 1) Detect contiguous Memory AND Disk Pages, the ideal case as we issue a single write op using the MemPage memory directly.
                // 2) Detect contiguous Disk Pages, as the MemPage are random, we copy them to a buffer and issue a single write op.
                //    Note that we may start by detecting 1), then switch to 2), which we do as long as 2) fits in the write cache.
                // 3) Random single Disk Page, the worst, one Write op per page...
                // We use in-thread for 1 & 3, we spawn working threads for 2 for the copy to be more efficient

                var maxDiskPagesInWritePage = WriteCachePageSize / PageSize;
                var isFragSegment = false;
                var hasWritePage = _writePagePool.TryTake(out var writePageIndex);

                SegmentInfo curSegment = default;
                var prevMemPageId = -2;
                var prevPageId = ulong.MaxValue - 1;

                foreach (var kvp in pagesToWrite)
                {
                    var curPageId = kvp.Key;
                    var curMemPageId = kvp.Value;

                    _memPagesInfo[curMemPageId].IOMode = PageIOMode.Saving;

                    var segGrowing = (prevPageId + 1) == curPageId;
                    var memSegGrowing = (prevMemPageId + 1) == curMemPageId;
                    var writeBufferLimitReached = curSegment.PageCount >= maxDiskPagesInWritePage;

                    // Case 1: contiguous segment growing
                    if ((isFragSegment==false) && segGrowing && memSegGrowing)
                    {
                        ++prevPageId;
                        ++prevMemPageId;
                        ++curSegment.PageCount;
                    }

                    // Case 2: frag segment growing
                    else if (isFragSegment && segGrowing && (writeBufferLimitReached==false))
                    {
                        fragMemPageIdList.Add(curMemPageId);
                        ++prevPageId;
                        ++curSegment.PageCount;
                    }

                    // Case 3: convert contiguous to fragmented segment
                    else if (hasWritePage && (isFragSegment==false) && segGrowing && (writeBufferLimitReached==false))
                    {
                        isFragSegment = true;
                        var posInFragMemPageIdList = fragMemPageIdList.Count;
                        for (int i = 0; i < curSegment.PageCount; i++)
                        {
                            fragMemPageIdList.Add(curSegment.StartMemPageId + i);
                        }
                        fragMemPageIdList.Add(curMemPageId);
                        ++curSegment.PageCount;
                        curSegment.WritePageIndex = writePageIndex;
                        curSegment.StartMemPageId = posInFragMemPageIdList;

                        hasWritePage = _writePagePool.TryTake(out writePageIndex);
                        ++prevPageId;
                    }

                    // Case 4: Issue existing segment (if any) and create a new one
                    else
                    {
                        // Issue any existing segment
                        if (curSegment.PageCount > 0)
                        {
                            if (isFragSegment)  fragSegments.Add(curSegment);
                            else                contiguousSegments.Add(curSegment);
                        }

                        curSegment.Init(curPageId, curMemPageId, 1);
                        isFragSegment = false;
                        prevPageId = curPageId;
                        prevMemPageId = curMemPageId;
                    }
                }

                // Last segment to issue
                if (curSegment.PageCount != 0)
                {
                    if (isFragSegment) fragSegments.Add(curSegment);
                    else               contiguousSegments.Add(curSegment);
                }

                fragMemPageIdArray = fragMemPageIdList.ToArray();

                // Put back a pending WritePage?
                if (hasWritePage)
                {
                    _writePagePool.Add(writePageIndex);
                }

                // Issue the Write Ops for contiguous segments in-thread
                foreach (var si in contiguousSegments)
                {
                    IssueWrite(si, false);
                }

                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
                // Issue the Write Ops for fragmented segments using parallel processing
                //Parallel.ForEach(fragSegments, new ParallelOptions {MaxDegreeOfParallelism = _writeThreadCount}, si =>
                //{
                //    IssueWrite(si, true);
                //});

                // OR sequential...which is faster if all threads are busy...
                foreach (SegmentInfo si in fragSegments)
                {
                    IssueWrite(si, true);
                }
                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

                // Update the LFUD
                if (updateLFUD)
                {
                    UpdateLFUD();
                }

                Interlocked.Increment(ref _flushRevision);

                _lastFlushTask = Task.WhenAll(issuedIOP.ToArray());

                LogFlushToDiskEnd();
                return _lastFlushTask;
            }
        }

        #endregion

        #region Private API

        private int RequestPage(uint pageId, bool doesWrite)
        {
#if VERBOSELOGGING
            using var logId = LogContext.PushProperty("PageId", pageId);
            using var logRW = LogContext.PushProperty("IsReadWrite", doesWrite);
            LogRequestPage(pageId, doesWrite);
#endif
            // Get the memory page from the cache, if it fails we allocate a new one
            if (_memPageIdByPageId.TryGetValue(pageId, out var memPageId) == false)
            {
                ++_debugInfo.MemPageCacheMiss;
                LogMemPageCacheMiss();

                // Page is not cached, we assign an available Memory Page to it
                memPageId = AllocateMemoryPage(pageId, doesWrite);
            }
            else
            {
                ++_debugInfo.MemPageCacheHit;
                LogMemPageCacheHit();

                var pi = _memPagesInfo[memPageId];

                // This is potentially a blocking operation, waiting for other threads to finish their operation on the page
                if (TransitionPageToAccess(pageId, pi, doesWrite, false) == false)
                {
                    LogRequestPageRace();

                    // We ended up here if we couldn't transition to the state we wanted, which is only possible if the page is
                    //  being reallocated. What we do is doing another call to RequestPage for this time to fetch a new MemPage

                    // Note: we may not be happy with this, this could lead to a stack overflow if the synchronization mechanism are
                    //  somehow buggy... Maybe a counter, SpinWait and retrying the _pageInfoIdByPageId would be more robust. If the
                    //  counter reaches zero we throw...
                    return RequestPage(pageId, doesWrite);
                }
            }

#if VERBOSELOGGING
            using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
#endif
            Debug.Assert(pageId == _memPagesInfo[memPageId].PageId, $"Requested {pageId}, locked on MemPageId {memPageId}, but this one is on {_memPagesInfo[memPageId].PageId}");

            LogRequestPageFound();
            return memPageId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsMemPageDirty(int memPageId) => _dirtyPages.IsSet(memPageId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearMemPageDirty(int memPageId) => _dirtyPages.Clear(memPageId);

        private int AllocateMemoryPage(uint pageId, bool doesWrite)
        {
            LogAllocatePageEnter();

            try
            {
                bool found = false;
                PageInfo pi = null;
                int memPageId = -1;

                // Try to access the candidates list of Pages to use for allocation, if null is returned it means there's an Update of the LFUD going on in another thread,
                //  spin wait until we can access the list then.
                var candidates = _mainLFUD;
                if (candidates == null)
                {
                    var sw = new SpinWait();
                    while (candidates == null)
                    {
                        sw.SpinOnce();
                        candidates = _mainLFUD;
                    }
                }

                // Maintain a counter of concurrent AllocateMemoryPage() calls, because we can't flip the LFUD (during UpdateLFUD()) if we have an allocation going on
                Interlocked.Increment(ref _mainLRUUsageCounter);

                // Try to allocate a contiguous Memory Page, if possible
                if (pageId > 0 && _memPageIdByPageId.TryGetValue(pageId - 1, out var prevMemPageId) && ((prevMemPageId + 1) < _memPagesCount))
                {
                    memPageId = prevMemPageId + 1;
                    pi = _memPagesInfo[memPageId];
                    // TODO we should check for the Hit Counter and only reallocate the page if its value is small
                    if ((IsMemPageDirty(memPageId) == false) && pi.SyncRoot.CurrentCount == 1 && TransitionPageToAccess(pageId, pi, doesWrite, true))
                    {
                        LogAllocatePageSequential();
                        found = true;
                    }
                }

                if (found == false)
                {
                    // Iterate from the least frequently used Page to the most one
                    var e = candidates.GetSpecializedEnumerator();
                    while (e.MoveNext())
                    {
                        var i = e.CurrentIndex;
                        if (candidates.Pick(i, out pi) == false)
                        {
                            continue;
                        }
                        memPageId = pi.MemPageId;
                        if ((IsMemPageDirty(memPageId) == false) && pi.SyncRoot.CurrentCount == 1 && TransitionPageToAccess(pageId, pi, doesWrite, true))
                        {
                            candidates.Release(i);
                            memPageId = pi.MemPageId;

                            found = true;
                            break;
                        }
                        else
                        {
                            candidates.PutBack(i, pi);
                        }
                    }
                }

                if (found)
                {
                    ++_debugInfo.MemPageReallocationCount;
                    LogAllocatePageFound(memPageId);

                    var pageOffset = pageId * (long)PageSize;
                    var loadPage = (pageOffset + PageSize) <= _fileSize;

                    // Load the page from disk, if it's stored there already. (won't be the case for new pages)
                    if (loadPage)
                    {
                        LogAllocatePageLoad();

                        ++_debugInfo.ReadFromDiskCount;

                        // Gotta lock all file accesses because of the non atomic file's position change + IO operation... :/
                        Task t;
                        lock (_file)
                        {
                            _file.Position = (long)PageSize * pageId;
                            t = _file.ReadAsync(_memPages.AsMemory(memPageId * PageSize, PageSize)).AsTask();
                        }
                        pi.IOCompletionTask = t;
                        pi.IOMode = PageIOMode.Loading;
                    }

                    return memPageId;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _mainLRUUsageCounter);
            }

            FlushToDiskAsync(true);
            return AllocateMemoryPage(pageId, doesWrite);
        }

        private void UpdateLFUD()
        {
            var pal = _memPagesActivities;
            var step = 0f;
            var t0 = _decayT0;
            var t1 = _decayT1;
            var now = DateTime.UtcNow.Ticks;

            // Apply decay on all pages and copy the values we need for the sorting
            for (int i = 0; i < _memPagesCount; i++)
            {
                ref var pa = ref pal[i];
                if (pa.HitCount < 1f)
                {
                    continue;
                }
                var dT = (float)TimeSpan.FromTicks(now - (long)pa.LastRequestTime << 20).TotalSeconds;
                var f = Math.Clamp((dT - t0) / (t1 - t0), 0, 1);
                pa.HitCount *= 1f-(f * step);
                
                _keysToSort[i] = (int)pa.HitCount;
                _valuesToSort[i] = i;
            }

            // Let's sort all the pages from their Hit Count
            Array.Sort(_keysToSort, _valuesToSort);

            // Update the backup LRU
            _backgroundLFUD.Clear();
            for (int i = 0; i < _memPagesCount; i++)
            {
                _backgroundLFUD.Add(_memPagesInfo[_valuesToSort[i]]);
            }

            // Swap the LRUs
            var newBackground = _mainLFUD;
            _mainLFUD = null;

            // We have to wait that no AllocateMemoryPage() call are running to make the swap, otherwise the ConcurrentCollection will be corrupted
            var spin = new SpinWait();
            while (_mainLRUUsageCounter != 0)
            {
                spin.SpinOnce();
            }
            _mainLFUD = _backgroundLFUD;
            _backgroundLFUD = newBackground;
        }

        unsafe private void CreateDatabaseFile()
        {
            // Create the Files
            var filePathName = BuildDatabasePathFileName();
            _file = new FileStream(filePathName, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 8192, FileOptions.Asynchronous|FileOptions.RandomAccess|FileOptions.WriteThrough);
            _fileSize = 0;

            var c = _dbc;
            _log.LogInformation("Create Database '{DatabaseName}' in file '{FilePathName}'", c.DatabaseName, filePathName);

            using (var pa = RequestPageReadWrite(0))
            {
                var h = (RootFileHeader*)pa.Page;
                StoreString(HeaderSignature, h->HeaderSignature, 32);
                h->DatabaseFormatRevision = DatabaseFormatRevision;
                StoreString(c.DatabaseName, h->DatabaseName, 64);

                OnDatabaseCreating(h);
            }

            FlushToDiskAsync(false).Wait();
        }

        unsafe void OnDatabaseCreating(RootFileHeader* rootFileHeader)
        {
            var handler = DatabaseCreating;
            handler?.Invoke(this, new DatabaseEventArgs(rootFileHeader));
        }

        unsafe private void LoadDatabaseFile()
        {
            // Create the Files
            var filePathName = BuildDatabasePathFileName();
            _file = new FileStream(filePathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 8192, FileOptions.Asynchronous|FileOptions.RandomAccess|FileOptions.WriteThrough);
            _fileSize = _file.Length;

            using (var pa = RequestPageReadOnly(0))
            {
                var h = (RootFileHeader*)pa.Page;
                _log.LogInformation("Load Database '{DatabaseName}' from file '{FilePathName}'", h->DatabaseNameString, filePathName);

                OnDatabaseLoading(h);
            }

        }

        unsafe void OnDatabaseLoading(RootFileHeader* rootFileHeader)
        {
            var handler = DatabaseLoading;
            handler?.Invoke(this, new DatabaseEventArgs(rootFileHeader));
        }

        private string BuildDatabaseFileName() => $"{_dbc.DatabaseName}.bin";
        private string BuildDatabasePathFileName() => Path.Combine(_dbc.DatabaseAbsoluteDirectory, BuildDatabaseFileName());

        #endregion

        #region Helpers

        internal unsafe static bool StoreString(string str, byte* dest, int destMaxSize)
        {
            var l = Encoding.UTF8.GetByteCount(str);
            if (l + 1 > destMaxSize)
            {
                return false;
            }

            fixed (char* c = str)
            {
                Encoding.UTF8.GetBytes(c, str.Length, dest, destMaxSize);
                dest[l] = 0;            // Null terminator
            }

            return true;
        }

        internal unsafe static string LoadString(byte* addr) => Marshal.PtrToStringUTF8((IntPtr)addr);

        #endregion

        #region Logging helpers
        [Conditional("VERBOSELOGGING")]
        private void LogRequestPage(uint pageId, bool doesWrite) => _log.LogDebug(10, "Request Disk Page: {PageId}, Write: {IsReadWrite}", pageId, doesWrite);
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
        private void LogAllocatePageFound(int memPageId) => _log.LogTrace(24, "Allocate Page Found {MemPageId}", memPageId);
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

        // TransitionPageTo
        [Conditional("VERBOSELOGGING")]
        private void LogTransitionSuccessful(PagesAccessMode prevMode, PagesAccessMode newMode) => 
            _log.LogTrace(40, "Transition completed from {PrevMode} to {NewMode}", prevMode, newMode);
        [Conditional("VERBOSELOGGING")]
        private void LogTransitionFailed(PagesAccessMode newMode) => 
            _log.LogTrace(40, "Transition completed to {NewMode} failed due to reallocation", newMode);

        #endregion
    }
}

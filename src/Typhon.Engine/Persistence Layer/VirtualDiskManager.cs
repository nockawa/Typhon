using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;
using System;
using System.Collections;
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
    unsafe public struct ReadOnlyPageAccessor : IDisposable
    {
        public byte* Page { get; }

        private readonly VirtualDiskManager _owner;
        private readonly int _memPageId;
        private readonly uint _pageId;
        private VirtualDiskManager.PageInfo _pi;

        internal ReadOnlyPageAccessor(VirtualDiskManager owner, int memPageId, VirtualDiskManager.PageInfo pi, byte* page)
        {
            _owner = owner;
            _memPageId = memPageId;
            _pageId = pi.PageId;
            _pi = pi;
            Page = page;
        }

        public void Dispose()
        {
            if (_pi == null)
            {
                return;
            }

            _owner.TransitionPageFromAccessToIdle(_pageId, _pi, _memPageId);

            _pi = null;
        }
    }

    unsafe public struct ReadWritePageAccessor : IDisposable
    {
        public byte* Page { get; }

        private readonly VirtualDiskManager _owner;
        private readonly int _memPageId;
        private readonly uint _pageId;
        private VirtualDiskManager.PageInfo _pi;

        internal ReadWritePageAccessor(VirtualDiskManager owner, int memPageId, VirtualDiskManager.PageInfo pi, byte* page)
        {
            _owner = owner;
            _memPageId = memPageId;
            _pageId = pi.PageId;
            _pi = pi;
            Page = page;
        }

        public void Dispose()
        {
            if (_pi == null)
            {
                return;
            }
            _owner.TransitionPageFromAccessToIdle(_pageId, _pi, _memPageId);

            _pi = null;
        }
    }

    public class VirtualDiskManager : IDisposable
    {
        private readonly ILogger<VirtualDiskManager> _logger;
        internal const long PageSize = 8192;
        internal const int PageSizePow2 = 13; // 2^( PageSizePow2 = PageSize
        internal const int DatabaseFormatRevision = 1;
        internal const ulong MinimumCacheSize = 512 * 1024 * 1024;
        internal const string HeaderSignature = "TyphonDatabase";
        internal const int WriteCachePageSize = 1024 * 1024;
        
        private readonly DatabaseConfiguration _configuration;
        private readonly TimeManager _timeManager;

        private bool _isDisposed;

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

        private readonly uint[] _accessedMemPageMap;    // The map store 1 bit for each cached pages, set to one if the page has been accessed in this frame
        private readonly uint[] _dirtyMemPageMap;       // The map store 1 bit for each cached pages, set to ine if the page has been changed and should be written to disk

        internal class ConcurrentCollection<T> : IEnumerable<T> where T : class
        {
            private readonly Memory<T> _data;
            private readonly ConcurrentBitmapL3 _map;
            private readonly int _capacity;
            private int _count;

            public ConcurrentCollection(int capacity)
            {
                _data = new T[capacity];
                _map = new ConcurrentBitmapL3(capacity);
                _capacity = capacity;
                _count = 0;
            }
            public int Count => _count;
            public int Capacity => _capacity;

            public void Clear()
            {
                _data.Span.Clear();
                _map.Reset();
                _count = 0;
            }

            public int Add(T obj)
            {
                var span = _data.Span;
                if (_count >= _capacity)
                {
                    ThrowCapacityReached();
                }
                var index = _count;
                _map.Set(_count);
                span[_count++] = obj;

                return index;
            }

            public bool Pick(int index, out T result)
            {
                result = Interlocked.Exchange(ref _data.Span[index], null);
                return result != null;
            }

            public void PutBack(int index, T obj)
            {
                var prev = Interlocked.CompareExchange(ref _data.Span[index], obj, null);
                if (prev != null)
                {
                    ThrowInvalidPutBack(index);
                }
            }

            public void Release(int index)
            {
                _map.Clear(index);
            }

            private static void ThrowInvalidPutBack(int index) => throw new Exception($"Invalid put back at location {index}");
            private static void ThrowCapacityReached() => throw new Exception("Can add a new element, the array capacity is reached");

            public readonly struct Enumerator : IEnumerator<T>
            {
                private readonly ConcurrentCollection<T> _owner;
                private readonly IEnumerator<int> _e;

                public Enumerator(ConcurrentCollection<T> owner)
                {
                    _owner = owner;
                    _e = _owner._map.GetEnumerator();
                }
                public bool MoveNext() => _e.MoveNext();
                public void Reset() => _e.Reset();
                public int CurrentIndex => _e.Current;
                public T Current => _owner._data.Span[_e.Current];
                object IEnumerator.Current => Current;
                public void Dispose() => _e?.Dispose();
            }

            public IEnumerator<T> GetEnumerator() => new Enumerator(this);
            public Enumerator GetSpecializedEnumerator() => new Enumerator(this);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

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

        #region Logging helpers
        [Conditional("VERBOSELOGGING")]
        private void LogRequestPage(uint pageId, bool doesWrite) => _logger.LogDebug(10, "Request Disk Page {PageId}, {IsReadWrite}", pageId, doesWrite);
        [Conditional("VERBOSELOGGING")]
        private void LogMemPageCacheHit() => _logger.LogDebug(11, "MemPage Cache Hit");
        [Conditional("VERBOSELOGGING")]
        private void LogMemPageCacheMiss() => _logger.LogDebug(12, "MemPage Cache Miss");
        [Conditional("VERBOSELOGGING")]
        private void LogRequestPageFound() => _logger.LogDebug(13, "Request Page Found");
        [Conditional("VERBOSELOGGING")]
        private void LogRequestPageRace() => _logger.LogDebug(14, "Request Page Race Condition (reallocation)");

        [Conditional("VERBOSELOGGING")]
        private void LogAllocatePageEnter() => _logger.LogDebug(20, "Allocate Page Enter");
        [Conditional("VERBOSELOGGING")]
        private void LogAllocatePageSequential() => _logger.LogDebug(22, "Allocate Page Sequential");
        [Conditional("VERBOSELOGGING")]
        private void LogAllocatePageFound(int memPageId) => _logger.LogDebug(24, "Allocate Page Found {MemPageId}", memPageId);
        [Conditional("VERBOSELOGGING")]
        private void LogAllocatePageLoad() => _logger.LogDebug(25, "Allocate Page Load From Disk");

        [Conditional("VERBOSELOGGING")]
        private void LogFlushToDiskStart() => _logger.LogDebug(30, "Flush To Disk Start");
        [Conditional("VERBOSELOGGING")]
        private void LogFlushToDiskConcurrentFlush() => _logger.LogDebug(31, "Flush To Disk exiting due to concurrent Flush");
        [Conditional("VERBOSELOGGING")]
        private void LogFlushToDiskEnd() => _logger.LogDebug(32, "Flush To Disk End");
        [Conditional("VERBOSELOGGING")]
        private void LogFlushToDiskWaitPrevious() => _logger.LogDebug(33, "Wait Previous Flush To Complete");
        [Conditional("VERBOSELOGGING")]
        private void LogFlushToDiskWritePage(SegmentInfo segment, List<int> fragList, bool isFrag)
        {
            if (isFrag)
            {
                _logger.LogDebug(34, "Write fragmented segment StartDiskPage: {StartDiskPageId}, PageCount: {PageCount}, MemPages: {MemPageIdList}",
                    segment.StartPageId, segment.PageCount, fragList.GetRange(segment.StartMemPageId, segment.PageCount));
            }
            else
            {
                _logger.LogDebug(34, "Write contiguous segment StartDiskPage: {StartDiskPageId}, PageCount: {PageCount}, FirstMemPage: {StartMemPageId}",
                    segment.StartPageId, segment.PageCount, segment.StartMemPageId);
            }

        }
        [Conditional("VERBOSELOGGING")]
        private void LogFlushToDiskPages(SortedDictionary<uint, int> pagesToWrite)
        {
            _logger.LogDebug(35, "Flush pages {DiskPageList}", pagesToWrite);
        }

        // TransitionPageTo
        [Conditional("VERBOSELOGGING")]
        private void LogTransitionSuccessful(PagesAccessMode prevMode, PagesAccessMode newMode) => 
            _logger.LogDebug(40, "Transition completed from {PrevMode} to {NewMode}", prevMode, newMode);
        [Conditional("VERBOSELOGGING")]
        private void LogTransitionFailed(PagesAccessMode newMode) => 
            _logger.LogDebug(40, "Transition completed to {NewMode} failed due to reallocation", newMode);

        #endregion

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

        unsafe public VirtualDiskManager(IConfiguration<DatabaseConfiguration> dc, TimeManager timeManager, ILogger<VirtualDiskManager> logger)
        {
            try
            {
                _configuration = dc.Value;
                _timeManager = timeManager;

                _logger = logger;

                _flushLock = new object();

                // Create the cache of the page, pin it and keeps its address
                var cacheSize = _configuration.DatabaseCacheSize;
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

                _writeThreadCount = (int)(Environment.ProcessorCount * _configuration.WriteThreadRatio);
                _writeCache = new byte[_configuration.WriteCacheSize];
                _writeCacheHandle = GCHandle.Alloc(_writeCache, GCHandleType.Pinned);
                _writeCacheAddr = (byte*)_writeCacheHandle.AddrOfPinnedObject();
                _writeCachePageCount = _configuration.WriteCacheSize / WriteCachePageSize;
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
                _accessedMemPageMap = new uint[(pageCount + 31) / 32];
                _dirtyMemPageMap    = new uint[(pageCount + 31) / 32];
                
                // Fill the MRU with all the pages as marked used for frame 0, init the PageInfo
                for (int i = 0; i < pageCount; i++)
                {
                    var pi = new PageInfo(i);
                    _memPagesInfo[i] = pi;
                    _mainLFUD.Add(pi);
                }

                // Init or load the file
                var filePathName = BuildDatabasePathFileName();
                var fi = new FileInfo(filePathName);
                var isCreationMode = fi.Exists == false;
                if (isCreationMode || _configuration.RecreateDatabase)
                {
                    CreateDatabaseFile();
                }
                else
                {
                    LoadDatabaseFile();
                }
                _logger.LogInformation("Virtual Disk Manager service initialized successfully");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Virtual Disk Manager service initialization failed");
                Dispose();
                throw new Exception("Virtual Disk Manager initialization error, check inner exception.", e);
            }
        }

        // Only for debug/unit test purpose. Should be no operating pending or activity on other threads regarding this service
        internal void ResetDiskManager()
        {
            FlushToDiskAsync(false).Wait();

            _logger.LogInformation("Service reset");
            Array.Clear(_memPages, 0, _memPages.Length);
            _debugInfo = default;
            _debugInfo.FreeMemPageCount = _memPagesCount;
            
            Array.Clear(_writeCache, 0, _configuration.WriteCacheSize);

            _memPageIdByPageId.Clear();

            Array.Clear(_accessedMemPageMap, 0, _accessedMemPageMap.Length);
            Array.Clear(_dirtyMemPageMap, 0, _dirtyMemPageMap.Length);
            Array.Clear(_memPagesActivities, 0, _memPagesActivities.Length);

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
            return new ReadOnlyPageAccessor(this, memPageId, pi, &_memPagesAddr[memPageId*PageSize]);
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

            return new ReadWritePageAccessor(this, memPageId, pi, &_memPagesAddr[memPageId*PageSize]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void TransitionPageFromAccessToIdle(uint pageId, PageInfo pi, int memPageId)
        {
#if VERBOSELOGGING
            using var logId = LogContext.PushProperty("PageId", pi.PageId);
            using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
#endif
            lock (pi)
            {
                var pageState = pi.AccessMode;
                Debug.Assert(pageState==PagesAccessMode.Read || pageState==PagesAccessMode.Write);

                if ((pageState == PagesAccessMode.Read) && (--pi.ConcurrentUseCounter != 0))
                {
                    return;
                }
                pi.AccessMode = PagesAccessMode.Idle;
                LogTransitionSuccessful(pageState, PagesAccessMode.Idle);
            }
            pi.SyncRoot.Release();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TransitionPageToAccess(uint pageId, PageInfo pi, int memPageId, bool doesWrite, bool reallocate)
        {
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

            // Wait for exclusive access to the MemPage
            pi.SyncRoot.Wait();

            if ((prevPageId != pi.PageId) || (reallocate && IsMemPageDirty(memPageId)))
            {
                pi.SyncRoot.Release();
                return false;
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
                var offset = memPageId >> 5;
                var index = (memPageId & 0x1F);
                var mask = (uint)(1 << index);
                Interlocked.Or(ref _accessedMemPageMap[offset], mask);
                if (doesWrite)
                {
                    Interlocked.Or(ref _dirtyMemPageMap[offset], mask);
                }
            }
            LogTransitionSuccessful(prevMode, pi.AccessMode);

            return true;
        }

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
                if (TransitionPageToAccess(pageId, pi, memPageId, doesWrite, false) == false)
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
        private bool IsMemPageDirty(int memPageId)
        {
            var offset = memPageId >> 5;
            var mask = 1 << (memPageId & 0x1F);
            return (_dirtyMemPageMap[offset] & mask) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearMemPageDirty(int memPageId)
        {
            var offset = memPageId >> 5;
            var mask = (uint)~(1 << (memPageId & 0x1F));
            Interlocked.And(ref _dirtyMemPageMap[offset], mask);
        }

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
                    if ((IsMemPageDirty(memPageId) == false) && pi.SyncRoot.CurrentCount == 1 && TransitionPageToAccess(pageId, pi, memPageId, doesWrite, true))
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
                        if ((IsMemPageDirty(memPageId) == false) && pi.SyncRoot.CurrentCount == 1 && TransitionPageToAccess(pageId, pi, memPageId, doesWrite, true))
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

                    var pageOffset = pageId * PageSize;
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
                            _file.Position = PageSize * pageId;
                            t = _file.ReadAsync(_memPages.AsMemory((int)(memPageId * PageSize), (int)PageSize)).AsTask();
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
                var mapLength = _dirtyMemPageMap.Length;
                var curI = 0;
                var pagesToWrite = new SortedDictionary<uint, int>();
                var maxPage = 0UL;

                for (int i = 0; i < mapLength && curI < _memPagesCount; i++)
                {
                    uint dirtyMask = _dirtyMemPageMap[i];

                    for (int j = 0; j < 32 && curI < _memPagesCount; j++)
                    {
                        var pi = _memPagesInfo[curI];
                        if ((dirtyMask & 1) == 1)
                        {
                            pagesToWrite.Add(pi.PageId, curI);
                            maxPage = Math.Max(maxPage, pi.PageId);
                        }

                        dirtyMask >>= 1;
                        ++curI;
                    }
                }

                LogFlushToDiskPages(pagesToWrite/*, updateMRU, accessPages*/);

                // Update the file length if needed. We don't want the write operation to perform several SetLength because this is a
                //  significant performance hit.
                var newFileLength = (long)(maxPage + 1) * PageSize;
                if (newFileLength > _file.Length)
                {
                    _file.SetLength(newFileLength);
                    _fileSize = newFileLength;
                }

                // Parse the dirty map to detect which page(s) changed and issue write IOP to save to disk
                var issuedIOP = new ConcurrentBag<Task>();

                var fragSegments = new List<SegmentInfo>();
                var contiguousSegments = new List<SegmentInfo>();
                var fragMemPageIdList = new List<int>();
                int[] fragMemPageIdArray = null;

                void IssueWrite(SegmentInfo segment, bool isFrag)
                {
                    LogFlushToDiskWritePage(segment, fragMemPageIdList, isFrag);

                    var pageSize = (int)PageSize;
                    var writePageAddr = _writeCacheAddr + (segment.WritePageIndex * WriteCachePageSize);

                    // Transition the pages we write to Saving
                    for (int i = 0; i < segment.PageCount; i++)
                    {
                        var memPageId = isFrag ? fragMemPageIdArray[segment.StartMemPageId+i] : (segment.StartMemPageId + i);
                        var pi = _memPagesInfo[memPageId];
                        pi.SyncRoot.Wait();                         // Wait for Idle and prevent other thread to change Access Mode
                        pi.IOMode = PageIOMode.Saving;
                        if (isFrag)
                        {
                            Buffer.MemoryCopy(_memPagesAddr + (memPageId * PageSize), writePageAddr + (i*pageSize), pageSize, pageSize);
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
                        srcOffset = segment.StartMemPageId * (int)PageSize;
                    }

                    // Issue the async write
                    Task task;
                    lock (_file)
                    {
#if VERBOSELOGGING
                        Log.Logger.Information("Write Page StartPageId {PageId}, MemPageId {MemPageId}", segment.StartPageId, segment.StartMemPageId);
#endif
                        _file.Position = segment.StartPageId * PageSize;
                        task = _file.WriteAsync(src, srcOffset, segment.PageCount * (int)PageSize);
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

                var maxDiskPagesInWritePage = WriteCachePageSize / (int)PageSize;
                var isFragSegment = false;
                var hasWritePage = _writePagePool.TryTake(out var writePageIndex);

                SegmentInfo curSegment = default;
                var prevMemPageId = -2;
                var prevPageId = ulong.MaxValue - 1;

                foreach (var kvp in pagesToWrite)
                {
                    var curPageId = kvp.Key;
                    var curMemPageId = kvp.Value;

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

        private void UpdateLFUD(/*List<int> accessPages*/)
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

            var c = _configuration;

            using (var pa = RequestPageReadWrite(0))
            {
                var h = (RootFileHeader*)pa.Page;
                StoreString(HeaderSignature, h->HeaderSignature, 32);
                h->DatabaseFormatRevision = DatabaseFormatRevision;
                StoreString(c.DatabaseName, h->DatabaseName, 64);
            }

            FlushToDiskAsync(false/*, out _*/).Wait();
        }

        private void LoadDatabaseFile()
        {
            // Create the Files
            var filePathName = BuildDatabasePathFileName();
            _file = new FileStream(filePathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 8192, FileOptions.Asynchronous|FileOptions.RandomAccess|FileOptions.WriteThrough);
            _fileSize = _file.Length;
        }

        public void DeleteDatabaseFile()
        {
            var fi = new FileInfo(BuildDatabasePathFileName());
            if (fi.Exists)
            {
                fi.Delete();
            }
        }

        unsafe public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _logger.LogInformation("Service disposing");

            FlushToDiskAsync(false).Wait();

            _file.Dispose();

            if (_configuration.DeleteDatabaseOnDispose)
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

            _isDisposed = true;
            _logger.LogInformation("Service disposed");
        }

        private string BuildDatabaseFileName() => $"{_configuration.DatabaseName}.bin";
        private string BuildDatabasePathFileName() => Path.Combine(_configuration.DatabaseAbsoluteDirectory, BuildDatabaseFileName());
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
    }
}

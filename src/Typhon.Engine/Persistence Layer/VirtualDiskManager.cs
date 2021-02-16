using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Events;
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
        private PageInfo[] _memPagesInfo;
        private volatile int _flushRevision;
        private volatile Task _lastFlushTask;
        private object _flushLock;
        private volatile int _lastMRUEntriesAddedCount;

        private int _writeThreadCount;
        private byte[] _writeCache;
        private GCHandle _writeCacheHandle;
        unsafe private byte* _writeCacheAddr;
        private int _writeCachePageCount;
        private readonly ConcurrentBag<int> _writePagePool;

        private readonly ConcurrentDictionary<uint, int> _memPageIdByPageId;

        private SortedDictionary<int, List<int>> _MRU;
        
        private readonly uint[] _accessedMemPageMap;    // The map store 1 bit for each cached pages, set to one if the page has been accessed in this frame
        private readonly uint[] _dirtyMemPageMap;       // The map store 1 bit for each cached pages, set to ine if the page has been changed and should be written to disk

        internal struct DebugInfo
        {
            public int MemPageCacheHit;
            public int MemPageCacheMiss;
            public int ReadFromDiskCount;
            public int WriteToDiskCount;
            public int PagesWrittenCount;
            public int FreeMemPageCount;
            public int PageTransitionModeRaceCondition;
            public int PageReallocationMRURaceConditionCount;       // Another thread Requested for access the same MemPage we're attempting to reallocate
            public int PageReallocationRaceConditionCount;          // Another thread allocated a MemPage for the same DiskPage faster
            public int MemPageReallocationCount;
            public int WaitForIOCount;
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
            public PageInfo()
            {
                SyncRoot = new SemaphoreSlim(1, 1);
            }
            public SemaphoreSlim SyncRoot;
            public volatile uint PageId;       // ID of the Disk Page this memory page stores data
            public int PreviousUsedFrame;      // ID of the Frame where the page has its MRU info stored in
            public int IndexInCandidatesMap;
            public int HitCounter;             // Every time this page is accessed in read or write this counter is incremented, used for MRU
            public PagesAccessMode AccessMode;
            public PageIOMode IOMode;
            public int ConcurrentUseCounter;   // Concurrent use counter
            public Task IOCompletionTask;
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
        private void LogAllocatePageRace() => _logger.LogDebug(21, "Allocate Page Race Condition");
        [Conditional("VERBOSELOGGING")]
        private void LogAllocatePageSequential() => _logger.LogDebug(22, "Allocate Page Sequential");
        [Conditional("VERBOSELOGGING")]
        private void LogAllocatePageMRURace() => _logger.LogDebug(23, "Allocate Page MRU Race");
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
        private void LogFlushToDiskPages(SortedDictionary<uint, int> pagesToWrite, bool mruUpdated, List<int> accessedPages)
        {
            _logger.LogDebug(35, "Flush pages {DiskPageList}", pagesToWrite);
            if (mruUpdated)
            {
                _logger.LogDebug(35, "Update Disk Pages MRU for Mem Pages {DiskPageList}", accessedPages);
            }
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
                _memPagesInfo = new PageInfo[_memPagesCount];

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

                _debugInfo.FreeMemPageCount = _memPagesCount;

                _memPageIdByPageId = new ConcurrentDictionary<uint, int>();

                _MRU = new SortedDictionary<int, List<int>>();
                _accessedMemPageMap = new uint[(_memPagesCount + 31) / 32];
                _dirtyMemPageMap    = new uint[(_memPagesCount + 31) / 32];
                
                // Fill the MRU with all the pages as marked used for frame 0, init the PageInfo
                var allPages = new List<int>(_memPagesCount);
                for (int i = _memPagesCount-1; i >=0; i--)
                {
                    var pi = new PageInfo
                    {
                        PreviousUsedFrame = 0, 
                        PageId = UInt32.MaxValue, 
                        IndexInCandidatesMap = allPages.Count
                    };
                    _memPagesInfo[i] = pi;
                    allPages.Add(i);
                }
                _MRU.Add(0, allPages);

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

        // Only for debug/unit test purpose. Should be no operating pending or activity on other threads regarging this service
        internal void ResetDiskManager()
        {
            FlushToDiskAsync(false, out _).Wait();

            _logger.LogInformation("Service reset");
            Array.Clear(_memPages, 0, _memPages.Length);
            _debugInfo = default;
            _debugInfo.FreeMemPageCount = _memPagesCount;
            
            Array.Clear(_writeCache, 0, _configuration.WriteCacheSize);

            _memPageIdByPageId.Clear();

            _MRU.Clear();
            Array.Clear(_accessedMemPageMap, 0, _accessedMemPageMap.Length);
            Array.Clear(_dirtyMemPageMap, 0, _dirtyMemPageMap.Length);
            
            // Fill the MRU with all the pages as marked used for frame 0
            var allPages = new List<int>(_memPagesCount);
            for (int i = _memPagesCount-1; i >=0; i--)
            {
                var pi = _memPagesInfo[i];
                pi.SyncRoot.Dispose();
                pi.SyncRoot = new SemaphoreSlim(1, 1);
                pi.AccessMode = PagesAccessMode.Idle;
                pi.IOMode = PageIOMode.None;
                pi.IOCompletionTask = null;
                pi.PreviousUsedFrame = 0;
                pi.PageId = UInt32.MaxValue;
                pi.IndexInCandidatesMap = allPages.Count;
                pi.HitCounter = 0;
                pi.ConcurrentUseCounter = 0;
                allPages.Add(i);
            }
            _MRU.Add(0, allPages);

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

            return new ReadOnlyPageAccessor(this, memPageId, pi, &_memPagesAddr[memPageId*PageSize]);
        }

        unsafe public ReadWritePageAccessor RequestPageReadWrite(uint pageId)
        {
            var memPageId = RequestPage(pageId, true);
            
            var pi = _memPagesInfo[memPageId];

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
                    ++pi.HitCounter;
                    return true;
                }

                // Check if we have to wait for IO operation to complete (must wait out of the lock)
                if (pi.IOMode != PageIOMode.None && pi.IOCompletionTask != null && pi.IOCompletionTask.IsCompleted == false)
                {
                    waitIO = pi.IOCompletionTask;
                }
            }

            // Wait IO to complete
            waitIO?.Wait();

            // Wait for exclusive access to the MemPage
            pi.SyncRoot.Wait();

            if (((reallocate == false) && (pi.PageId != pageId)) || (reallocate && IsMemPageDirty(memPageId)))
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
                ++pi.HitCounter;

                if (reallocate)
                {
                    _memPageIdByPageId.TryRemove(pi.PageId, out _);
                    _memPageIdByPageId.TryAdd(pageId, memPageId);
                    pi.PageId = pageId;
                    pi.HitCounter = 1;
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
                //if (pageId != _memPagesInfo[memPageId].PageId)
                //{
                //    Log.Logger.Fatal($"*** 1 Requested {pageId}, locked on MemPageId {memPageId}, but this one is on {_memPagesInfo[memPageId].PageId}");
                //    throw new Exception();
                //}
            }
            else
            {
                //if (pageId != _memPagesInfo[memPageId].PageId)
                //{
                //    Log.Logger.Fatal($"*** 2 Requested {pageId}, locked on MemPageId {memPageId}, but this one is on {_memPagesInfo[memPageId].PageId}");
                //    throw new Exception();
                //}
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
                if (pageId != _memPagesInfo[memPageId].PageId)
                {
                    Log.Logger.Fatal($"*** 3 Requested {pageId}, locked on MemPageId {memPageId}, but this one is on {_memPagesInfo[memPageId].PageId}");
                    throw new Exception();
                }
            }

#if VERBOSELOGGING
            using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
#endif
            if (pageId != _memPagesInfo[memPageId].PageId)
            {
                Log.Logger.Fatal($"*** 4 Requested {pageId}, locked on MemPageId {memPageId}, but this one is on {_memPagesInfo[memPageId].PageId}");
                throw new Exception();
            }

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

            bool found = false;
            PageInfo pi = null;
            int memPageId = 0;

            // The lock is pretty wide, could be optimized if the right amount of work was spent...
            lock (_MRU)
            {
                // We ended in AllocateMemoryPage because RequestPage() couldn't find the page but another thread may have beat us
                //  to allocate it, so make a second check, this time under the lock that is responsible to allocate them
                if (_memPageIdByPageId.TryGetValue(pageId, out memPageId))
                {
                    ++_debugInfo.PageReallocationRaceConditionCount;
                    LogAllocatePageRace();

                    Debug.Assert(pageId == _memPagesInfo[memPageId].PageId);
                    return memPageId;
                }

                // Try to allocate a contiguous Memory Page, if possible
                if (pageId > 0 && _memPageIdByPageId.TryGetValue(pageId - 1, out var prevMemPageId) && ((prevMemPageId+1) < _memPagesCount) && (IsMemPageDirty(prevMemPageId+1) == false))
                {
                    memPageId = prevMemPageId + 1;
                    pi = _memPagesInfo[memPageId];
                    if ((pi.IndexInCandidatesMap!=-1) && pi.SyncRoot.CurrentCount==1 && TransitionPageToAccess(pageId, pi, memPageId, doesWrite, true))
                    {
                        LogAllocatePageSequential();
                        found = true;
                    }
                }

                if (found == false)
                {
                    var mruFramesToRemove = new List<int>(16);
                    var curFrame = _timeManager.ExecutionFrame;

                    // Iterate from the oldest frame to the more recent one
                    foreach (var kvp in _MRU)
                    {
                        List<int> candidates = kvp.Value;
                        var candidatesCount = candidates.Count;

                        if (kvp.Key != curFrame && candidates.Count == 0)
                        {
                            mruFramesToRemove.Add(kvp.Key);
                        }

                        // Candidates are listed from the most recent used to the least ones
                        // So we iterate from the end to the beginning
                        for (int i = candidatesCount-1; i >= 0; i--)
                        {
                            memPageId = candidates[i];
                            if (memPageId == -1)
                            {
                                continue;
                            }
                            pi = _memPagesInfo[memPageId];

                            if ((IsMemPageDirty(memPageId) == false) && pi.SyncRoot.CurrentCount==1 && (pi.IndexInCandidatesMap!=-1) && TransitionPageToAccess(pageId, pi, memPageId, doesWrite, true))
                            {
                                found = true;
                                break;
                            }

                            //++_debugInfo.PageReallocationMRURaceConditionCount;
                            //LogAllocatePageMRURace();
                        }

                        if (found)
                        {
                            break;
                        }
                    }

                    foreach (var k in mruFramesToRemove)
                    {
                        _MRU.Remove(k);
                    }
                }

                if (found)
                {
                    // Remove the memPageId from the candidates list
                    if (_MRU.TryGetValue(pi.PreviousUsedFrame, out var candidates))
                    {
                        if (candidates.Count == pi.IndexInCandidatesMap + 1)
                        {
                            candidates.RemoveAt(pi.IndexInCandidatesMap);
                        }
                        else
                        {
                            candidates[pi.IndexInCandidatesMap] = -1;
                        }
                    }

                    pi.PreviousUsedFrame = -1;
                    pi.IndexInCandidatesMap = -1;
                }
            }

            if (found)
            {
                ++_debugInfo.MemPageReallocationCount;
                LogAllocatePageFound(memPageId);

                var pageOffset = pageId*PageSize;
                var loadPage = (pageOffset+PageSize) <= _fileSize;

                // Load the page from disk, if it's stored there already. (won't be the case for new pages)
                if (loadPage)
                {
                    LogAllocatePageLoad();

                    ++_debugInfo.ReadFromDiskCount;

                    // Gotta lock all file accesses because of the non atomic file's position change + IO operation... :/
                    lock (_file)
                    {
                        _file.Position = PageSize * pageId;
                        var t = _file.ReadAsync(_memPages.AsMemory((int)(memPageId * PageSize), (int)PageSize)).AsTask();
                        t.Wait();   // TODO REMOVE
                        //pi.IOCompletionTask = t;
                        //pi.IOMode = PageIOMode.Loading;
                    }
                }

                return memPageId;
            }

#if VERBOSELOGGING
            var logFlushCounter = 1;
#endif
            var temp = FlushToDiskAsync(true, out var newEntriesCount);
            //temp.Wait(); //TODO REMOVE

            // The previous flush didn't make room? Let's wait one does
            if (newEntriesCount == 0)
            {
                var sw = new SpinWait();
                while (newEntriesCount == 0 && sw.NextSpinWillYield==false)
                {
#if VERBOSELOGGING
                    ++logFlushCounter;
#endif
                    FlushToDiskAsync(true, out newEntriesCount).Wait();
                    sw.SpinOnce();
                }
            }
#if VERBOSELOGGING
            using var logFlush = LogContext.PushProperty("FlushWaitCounter", logFlushCounter);
#endif
            return AllocateMemoryPage(pageId, doesWrite);
        }

        unsafe internal Task FlushToDiskAsync(bool updateMRU, out int mruEntriesAddedCount)
        {
            var flushRevision = _flushRevision;

            LogFlushToDiskStart();

            // We lock the file because we need to prevent concurrent reads/writes.
            lock (_flushLock)
            {
                // If another thread complete a flush when we were waiting for the lock, just return the task resulting from it
                if (flushRevision < _flushRevision)
                {
                    LogFlushToDiskConcurrentFlush();

                    mruEntriesAddedCount = _lastMRUEntriesAddedCount;
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
                var accessPages = new List<int>(256);
                var maxPage = 0UL;

                for (int i = 0; i < mapLength && curI < _memPagesCount; i++)
                {
                    uint accessMask = 0;
                    uint dirtyMask = _dirtyMemPageMap[i];
                    if (updateMRU)
                    {
                        accessMask = Interlocked.Exchange(ref _accessedMemPageMap[i], 0);
                    }

                    for (int j = 0; j < 32 && curI < _memPagesCount; j++)
                    {
                        var pi = _memPagesInfo[curI];
                        if ((dirtyMask & 1) == 1)
                        {
                            pagesToWrite.Add(pi.PageId, curI);
                            maxPage = Math.Max(maxPage, pi.PageId);
                        }

                        if (updateMRU && (accessMask & 1) == 1)
                        {
                            accessPages.Add(curI);
                        }

                        dirtyMask >>= 1;
                        accessMask >>= 1;
                        ++curI;
                    }
                }

                LogFlushToDiskPages(pagesToWrite, updateMRU, accessPages);

                mruEntriesAddedCount = updateMRU ? accessPages.Count : 0;
                _lastMRUEntriesAddedCount = mruEntriesAddedCount;

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
                        fixed (byte* addr = src)
                        {
                            Log.Logger.Information("Write Page StartPageId {PageId}, MemPageId {MemPageId}, Value {Value}", segment.StartPageId, segment.StartMemPageId, *(int*)(addr+srcOffset));
                        }
#endif
                        _file.Position = segment.StartPageId * PageSize;
                        task = _file.WriteAsync(src, srcOffset, segment.PageCount * (int)PageSize);
                        //task.Wait(); // TODO REMOVE!!!
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

                // Issue the Write Ops for fragmented segments using parallel processing
                //Parallel.ForEach(fragSegments, new ParallelOptions {MaxDegreeOfParallelism = _writeThreadCount}, si =>
                //{
                //    IssueWrite(si, true);
                //});

                foreach (SegmentInfo si in fragSegments)
                {
                    IssueWrite(si, true);
                }

                // Update the MRU
                if (updateMRU)
                {
                    // Sort the MRU for this frame, we put the MemPage with the highest HitCount before the lowest,
                    //  because when reallocating a page, this is done from the end of the MRU list (so the least used) up to the start.
                    // Note: MRU Update should occur only once per frame and during the last flush, otherwise the accessPages won't reflect
                    //  an accurate state and the candidates list (if it already exists because of prior MRU Update for this frame) won't be
                    //  sorted properly.
                    accessPages.Sort((x, y) =>
                    {
                        int hitDiff = _memPagesInfo[y].HitCounter - _memPagesInfo[x].HitCounter;

                        // Sort by HitCounter then MemPageId decreasing
                        return hitDiff != 0 ? hitDiff : (y - x);
                    });

                    lock (_MRU)
                    {
                        var curFrame = _timeManager.ExecutionFrame;
                        List<int> candidates = null;
                        var candidatesFrame = -1;
                        if (_MRU.TryGetValue(curFrame, out var newCandidates) == false)
                        {
                            newCandidates = new List<int>();
                            _MRU.Add(curFrame, newCandidates);
                        }

                        // We have to remove the pages that were accessed during this frame from their current (so to be previous) candidate list
                        //  and add them to the candidate list of this frame
                        for (int i = 0; i < accessPages.Count; i++)
                        {
                            var memPageId = accessPages[i];
                            var pi = _memPagesInfo[memPageId];

                            // If PreviousUsedFrame is used, we have to remove the reference of the MemPage in the MRU for that frame
                            if ((pi.PreviousUsedFrame != -1) && (pi.IndexInCandidatesMap != -1))
                            {
                                // Let's cache the Candidate list for the "current" frame we're processing
                                if (candidatesFrame != pi.PreviousUsedFrame)
                                {
                                    _MRU.TryGetValue(pi.PreviousUsedFrame, out candidates);
                                    candidatesFrame = pi.PreviousUsedFrame;
                                }

                                //Debug.Assert(pi.IndexInCandidatesMap==-1 ||candidates[pi.IndexInCandidatesMap] == memPageId, $"MRU Candidates list integrity failure, Frame {candidatesFrame}, Index: {pi.IndexInCandidatesMap}, MemPageId {memPageId}.");

                                // Remove the page, we don't call RemoveAt because the PageInfo stores index into this list, we can't alter the indices
                                candidates[pi.IndexInCandidatesMap] = -1;
                            }
                            
                            // Reference the page into the new MRU Frame
                            pi.PreviousUsedFrame = curFrame;
                            pi.IndexInCandidatesMap = newCandidates.Count;
                            newCandidates.Add(memPageId);
                        }
                    }
                    // TODO We could defragment the Candidates list of the Frame we removed some pages, this would requires to adjust the
                    //  'IndexInCandidatesMap' field in the PageInfo but we would get rid of the invalid entries (the ones that contain -1)
                }

                Interlocked.Increment(ref _flushRevision);

                _lastFlushTask = Task.WhenAll(issuedIOP.ToArray());

                LogFlushToDiskEnd();
                return _lastFlushTask;
            }
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

            FlushToDiskAsync(false, out _).Wait();
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

            FlushToDiskAsync(false, out _).Wait();

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

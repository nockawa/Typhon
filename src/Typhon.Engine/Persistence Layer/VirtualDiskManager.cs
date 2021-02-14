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
        private VirtualDiskManager.PageInfo* _pi;

        internal ReadOnlyPageAccessor(VirtualDiskManager owner, int memPageId, VirtualDiskManager.PageInfo* pi, byte* page)
        {
            _owner = owner;
            _memPageId = memPageId;
            _pageId = pi->PageId;
            _pi = pi;
            ++_pi->HitCounter;
            Page = page;
        }

        public void Dispose()
        {
            if (_pi == null)
            {
                return;
            }

            if (Interlocked.Decrement(ref _pi->ConcurrentUseCounter) == 0)
            {
                _owner.TransitionPageTo(_pageId, _pi, _memPageId, VirtualDiskManager.PagesAccessMode.Idle, VirtualDiskManager.PagesAccessMode.Read);
            }

            _pi = null;
        }
    }

    unsafe public struct ReadWritePageAccessor : IDisposable
    {
        public byte* Page { get; }

        private readonly VirtualDiskManager _owner;
        private readonly int _memPageId;
        private readonly uint _pageId;
        private VirtualDiskManager.PageInfo* _pi;

        internal ReadWritePageAccessor(VirtualDiskManager owner, int memPageId, VirtualDiskManager.PageInfo* pi, byte* page)
        {
            _owner = owner;
            _memPageId = memPageId;
            _pageId = pi->PageId;
            _pi = pi;
            ++_pi->HitCounter;
            Page = page;
        }

        unsafe public void Dispose()
        {
            if (_pi == null)
            {
                return;
            }
            _owner.TransitionPageTo(_pageId, _pi, _memPageId, VirtualDiskManager.PagesAccessMode.Idle, VirtualDiskManager.PagesAccessMode.Write);

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
        private GCHandle _memPageInfosHandle;
        unsafe private PageInfo* _memPageInfosAddr;
        internal readonly Task[] _IOCompletionTasks;
        private volatile int _flushRevision;
        private volatile Task _lastFlushTask;
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

        internal enum PagesAccessMode : int
        {
            Idle         = 0,   // The page is idle
            Read         = 1,   // The page is accessed in one/many concurrent read, the ConcurrentReadCounter will indicate how many concurrent read we have
            Write        = 2,   // The page is accessed in an exclusive read/write by a thread.
            Loading      = 3,   // The page is being loaded from disk
            Saving       = 4,   // The page is being saved into disk
            Reallocating = 5    // The page is being reallocating to serve another Disk Page
        }

        internal struct PageInfo
        {
            volatile public uint PageId;       // ID of the Disk Page this memory page stores data
            public int PreviousUsedFrame;      // ID of the Frame where the page has its MRU info stored in
            public int IndexInCandidatesMap;
            public int HitCounter;             // Every time this page is accessed in read or write this counter is incremented, used for MRU
            public volatile int AccessMode;
            public int ConcurrentUseCounter;   // Concurrent use counter
        }

        #region Logging helpers
        [Conditional("VERBOSELOGGING")]
        private void LogRequestPage(uint pageId, bool readWrite) => _logger.LogDebug(10, "Request Disk Page {PageId}, {IsReadWrite}", pageId, readWrite);
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
        private void LogTransitionSuccessfulWithSpin(PagesAccessMode from, PagesAccessMode to, int spin) => 
            _logger.LogDebug(40, "Transition completed from {PrevMode} to {NewMode} with {SpinCount} spin count", @from, to, spin);

        [Conditional("VERBOSELOGGING")]
        private void LogTransitionWaitIO(PagesAccessMode current) => 
            _logger.LogDebug(41, "Transition wait for IO {CurrentMode} to complete", current);

        [Conditional("VERBOSELOGGING")]
        private void LogTransitionFailedPageRelocating(PagesAccessMode from, PagesAccessMode to) => 
            _logger.LogDebug(42, "Transition failed from {PrevMode} to {NewMode}, concurrent relocation", @from, to);

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

                // Create the cache of the page, pin it and keeps its address
                var cacheSize = _configuration.DatabaseCacheSize;
                _memPages = new byte[cacheSize];
                _memPagesHandle = GCHandle.Alloc(_memPages, GCHandleType.Pinned);
                _memPagesAddr = (byte*)_memPagesHandle.AddrOfPinnedObject();

                // Create the Memory Page info table
                _memPagesCount = (int)(cacheSize >> PageSizePow2);
                _memPagesInfo = new PageInfo[_memPagesCount];
                _memPageInfosHandle = GCHandle.Alloc(_memPagesInfo, GCHandleType.Pinned);
                _memPageInfosAddr = (PageInfo*)_memPageInfosHandle.AddrOfPinnedObject();

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

                _IOCompletionTasks = new Task[_memPagesCount];

                _memPageIdByPageId = new ConcurrentDictionary<uint, int>();

                _MRU = new SortedDictionary<int, List<int>>();
                _accessedMemPageMap = new uint[(_memPagesCount + 31) / 32];
                _dirtyMemPageMap    = new uint[(_memPagesCount + 31) / 32];
                
                // Fill the MRU with all the pages as marked used for frame 0, init the PageInfo
                var curPI = _memPageInfosAddr + (_memPagesCount - 1);
                var allPages = new List<int>(_memPagesCount);
                for (int i = _memPagesCount-1; i >=0; i--)
                {
                    curPI->PreviousUsedFrame = 0;
                    curPI->PageId = UInt32.MaxValue;
                    curPI->IndexInCandidatesMap = allPages.Count;
                    allPages.Add(i);
                    --curPI;
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
        unsafe internal void ResetDiskManager()
        {
            _logger.LogInformation("Service reset");
            Array.Clear(_memPages, 0, _memPages.Length);
            Array.Clear(_memPagesInfo, 0, _memPagesInfo.Length);
            _debugInfo = default;
            _debugInfo.FreeMemPageCount = _memPagesCount;
            
            Array.Clear(_writeCache, 0, _configuration.WriteCacheSize);

            Array.Clear(_IOCompletionTasks, 0, _IOCompletionTasks.Length);
            _memPageIdByPageId.Clear();

            _MRU.Clear();
            Array.Clear(_accessedMemPageMap, 0, _accessedMemPageMap.Length);
            Array.Clear(_dirtyMemPageMap, 0, _dirtyMemPageMap.Length);
            
            // Fill the MRU with all the pages as marked used for frame 0
            var curPI = _memPageInfosAddr + (_memPagesCount - 1);
            var allPages = new List<int>(_memPagesCount);
            for (int i = _memPagesCount-1; i >=0; i--)
            {
                curPI->PreviousUsedFrame = 0;
                curPI->PageId = UInt32.MaxValue;
                curPI->IndexInCandidatesMap = allPages.Count;
                allPages.Add(i);
                --curPI;
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

        unsafe internal bool GetPageInfoOf(uint pageId, out PageInfo* pi)
        {
            if (_memPageIdByPageId.TryGetValue(pageId, out var memPageId) == false)
            {
                pi = null;
                return false;
            }

            pi = _memPageInfosAddr + memPageId;

            return true;
        }

        unsafe public ReadOnlyPageAccessor RequestPageReadOnly(uint pageId)
        {
            var memPageId = RequestPage(pageId, false);

            var pi = &_memPageInfosAddr[memPageId];

            return new ReadOnlyPageAccessor(this, memPageId, pi, &_memPagesAddr[memPageId*PageSize]);
        }

        unsafe public ReadWritePageAccessor RequestPageReadWrite(uint pageId)
        {
            var memPageId = RequestPage(pageId, true);
            
            var pi = &_memPageInfosAddr[memPageId];

            return new ReadWritePageAccessor(this, memPageId, pi, &_memPagesAddr[memPageId*PageSize]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe internal bool TransitionPageTo(uint pageId, PageInfo* pi, int memPageId, PagesAccessMode newMode, PagesAccessMode currentMode)
        {
#if VERBOSELOGGING
            using var logId = LogContext.PushProperty("PageId", pi->PageId);
            using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
#endif
            if ((newMode!=PagesAccessMode.Reallocating) && pi->PageId != pageId)
            {
                return false;
            }

            var prevMode = (PagesAccessMode)Interlocked.CompareExchange(ref pi->AccessMode, (int)newMode, (int)currentMode);

            // Any transition from Idle are valid, exit
            if (prevMode == currentMode)
            {
                if ((newMode!=PagesAccessMode.Reallocating) && pi->PageId != pageId)
                {
                    return false;
                }
                LogTransitionSuccessful(currentMode, newMode);
                return true;
            }

            if (prevMode == PagesAccessMode.Reallocating)
            {
                LogTransitionFailedPageRelocating(currentMode, newMode);
                return false;
            }

            // If we requested Read and were in read already, exit as concurrent read are possible
            if (prevMode == PagesAccessMode.Read && newMode == PagesAccessMode.Read)
            {
                // From there we don't know for sure the MemPage is still allocated to the DiskPage we want, we have to make the check
                if (pi->PageId != pageId)
                {
                    LogTransitionFailedPageRelocating(currentMode, newMode);
                    return false;
                }
                return true;
            }

            // If the current mode is an IO operation, we have a task to wait for
            if (prevMode == PagesAccessMode.Loading || prevMode == PagesAccessMode.Saving)
            {
                var ioTask = _IOCompletionTasks[memPageId];
                if (ioTask!= null && ioTask.IsCompleted == false)
                {
                    ++_debugInfo.WaitForIOCount;
                    LogTransitionWaitIO(prevMode);

                    // Fetch the task's result to wait for its completion
                    ioTask.Wait();
                }

                _IOCompletionTasks[memPageId] = null;

                // Attempt to switch from the IOP mode to the new one, we may be beat by another thread...
                var lastMode = (PagesAccessMode)Interlocked.CompareExchange(ref pi->AccessMode, (int)newMode, (int)prevMode);
                
                // Another thread beat us and switch to reallocation? Exit with false to notify the transition failed
                if (lastMode == PagesAccessMode.Reallocating)
                {
                    LogTransitionFailedPageRelocating(currentMode, newMode);
                    return false;
                }

                // We successfully transitioned?
                if (lastMode == prevMode)
                {
                    if ((newMode!=PagesAccessMode.Reallocating) && pi->PageId != pageId)
                    {
                        return false;
                    }
                    LogTransitionSuccessful(currentMode, newMode);

                    var offset = memPageId >> 5;
                    var mask = ~(uint)(1 << (memPageId & 0x1F));

                    if (prevMode == PagesAccessMode.Saving)
                    {
                        Interlocked.And(ref _dirtyMemPageMap[offset], mask);
                    }

                    return true;
                }

                // If we end up here it means another thread beat us and transitioned to another mode, we step through the code
                //  below to wait for our turn
            }

            // We don't wait when switching to reallocation (if we got beat by another thread)
            if (newMode == PagesAccessMode.Reallocating)
            {
                LogTransitionFailedPageRelocating(currentMode, newMode);
                return false;
            }

            ++_debugInfo.PageTransitionModeRaceCondition;

            var spinCounter = 0;
            var sw = new SpinWait();
            while (true)
            {
                sw.SpinOnce();
                ++spinCounter;

                // It is possible that we miss a reallocation phase, so let's check if ownership changed
                if (pi->PageId != pageId)
                {
                    return false;
                }

                // Note: we rely on CompareExchange instead of a lock, so it means if the contention is greater than 2 it won't be a "first arrived,
                //  first served" basis, but a random choice instead. I don't think it's that bad because the contention really should be low...
                prevMode = (PagesAccessMode)Interlocked.CompareExchange(ref pi->AccessMode, (int)newMode, (int)currentMode);
                if (prevMode == currentMode)
                {
                    if (pi->PageId != pageId)
                    {
                        return false;
                    }
                    LogTransitionSuccessfulWithSpin(currentMode, newMode, spinCounter);
                    return true;
                }

                if (prevMode == PagesAccessMode.Reallocating)
                {
                    LogTransitionFailedPageRelocating(currentMode, newMode);
                    return false;
                }
            } 
        }

        unsafe private int RequestPage(uint pageId, bool readWrite)
        {
#if VERBOSELOGGING
            using var logId = LogContext.PushProperty("PageId", pageId);
            using var logRW = LogContext.PushProperty("IsReadWrite", readWrite);
            LogRequestPage(pageId, readWrite);
#endif
            // Get the memory page from the cache, if it fails we allocate a new one
            if (_memPageIdByPageId.TryGetValue(pageId, out var memPageId) == false)
            {
                ++_debugInfo.MemPageCacheMiss;
                LogMemPageCacheMiss();

                // Page is not cached, we assign an available Memory Page to it
                memPageId = AllocateMemoryPage(pageId);
            }
            else
            {
                ++_debugInfo.MemPageCacheHit;
                LogMemPageCacheHit();
            }

#if VERBOSELOGGING
            using var logMemPageId = LogContext.PushProperty("MemPageId", memPageId);
#endif

            var pi = &_memPageInfosAddr[memPageId];

            // This is potentially a blocking operation, waiting for other threads to finish their operation on the page
            if (TransitionPageTo(pageId, pi, memPageId, readWrite ? PagesAccessMode.Write : PagesAccessMode.Read, PagesAccessMode.Idle) == false)
            {
                LogRequestPageRace();

                // We ended up here if we couldn't transition to the state we wanted, which is only possible if the page is
                //  being reallocated. What we do is doing another call to RequestPage for this time to fetch a new MemPage

                // Note: we may not be happy with this, this could lead to a stack overflow if the synchronization mechanism are
                //  somehow buggy... Maybe a counter, SpinWait and retrying the _pageInfoIdByPageId would be more robust. If the
                //  counter reaches zero we throw...
                return RequestPage(pageId, readWrite);
            }
            
            // Read-only mode allow for concurrent read-only accesses, we need to maintain how many concurrent reads
            //  there are to properly switch back to Idle mode
            if (readWrite == false)
            {
                Interlocked.Increment(ref pi->ConcurrentUseCounter);
            }

            // Mark the page as accessed in the access map
            var offset = memPageId >> 5;
            var index = (memPageId & 0x1F);
            var mask = (uint)(1 << index);
            Interlocked.Or(ref _accessedMemPageMap[offset], mask);
            if (readWrite)
            {
                Interlocked.Or(ref _dirtyMemPageMap[offset], mask);
            }

            Debug.Assert(pageId == _memPageInfosAddr[memPageId].PageId);

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

        private static int allocCounter = 0;

        unsafe private int AllocateMemoryPage(uint pageId)
        {
            LogAllocatePageEnter();

            bool found = false;
            PageInfo* pi = null;
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

                    Debug.Assert(pageId == _memPageInfosAddr[memPageId].PageId);
                    return memPageId;
                }

                // Try to allocate a contiguous Memory Page, if possible
                if (pageId > 0 && _memPageIdByPageId.TryGetValue(pageId - 1, out var prevMemPageId) && ((prevMemPageId+1) < _memPagesCount) && (IsMemPageDirty(prevMemPageId+1) == false))
                {
                    var cMemPageId = prevMemPageId + 1;
                    var cPI = &_memPageInfosAddr[cMemPageId];
                    if ((cPI->IndexInCandidatesMap!=-1) && TransitionPageTo(pageId, cPI, cMemPageId, PagesAccessMode.Reallocating, PagesAccessMode.Idle))
                    {
                        LogAllocatePageSequential();

                        // We need to remove the page from the _memPageIdByPageId before we reassign it
                        if (_memPageIdByPageId.TryRemove(cPI->PageId, out var tpsId))
                        {
                            Debug.Assert(tpsId == cMemPageId);
                        }

                        // Add the new relation
                        _memPageIdByPageId.TryAdd(pageId, cMemPageId);

                        // Remove the memPageId from the candidates list
                        if (_MRU.TryGetValue(cPI->PreviousUsedFrame, out var candidates))
                        {
                            if (candidates.Count == cPI->IndexInCandidatesMap + 1)
                            {
                                candidates.RemoveAt(cPI->IndexInCandidatesMap);
                            }
                            else
                            {
                                candidates[cPI->IndexInCandidatesMap] = -1;
                            }
                        }

                        pi = cPI;
                        memPageId = cMemPageId;
                        found = true;
                    }
                }

                if (found == false)
                {
                    // Iterate from the oldest frame to the more recent one
                    foreach (var kvp in _MRU)
                    {
                        List<int> candidates = kvp.Value;
                        var candidatesCount = candidates.Count;

                        // Candidates are listed from the most recent used to the least ones
                        // So we iterate from the end to the beginning
                        for (int i = candidatesCount-1; i >= 0; i--)
                        {
                            memPageId = candidates[i];
                            if (memPageId == -1)
                            {
                                continue;
                            }
                            pi = &_memPageInfosAddr[memPageId];

                            if ((IsMemPageDirty(memPageId) == false) && (pi->IndexInCandidatesMap!=-1) && TransitionPageTo(pageId, pi, memPageId, PagesAccessMode.Reallocating, PagesAccessMode.Idle))
                            {
                                // We need to remove the page from the _memPageIdByPageId before we reassign it
                                if (_memPageIdByPageId.TryRemove(pi->PageId, out var tpsId))
                                {
                                    Debug.Assert(tpsId == memPageId);
                                }

                                // Add the new relation
                                _memPageIdByPageId.TryAdd(pageId, memPageId);

                                // Remove the memPageId from the candidates list (must preserve index order)
                                if (candidates.Count == i + 1)
                                {
                                    candidates.RemoveAt(i);
                                }
                                else
                                {
                                    candidates[i] = -1;
                                }

                                found = true;
                                break;
                            }

                            ++_debugInfo.PageReallocationMRURaceConditionCount;
                            LogAllocatePageMRURace();
                        }

                        if (found)
                        {
                            break;
                        }
                    }
                }

                if (found)
                {
                    //Assign the memory page to the requested disk page while we are under the MRU lock to prevent another thread to jump on this MemPage too soon
                    pi->PageId = pageId;
                    pi->HitCounter = 0;
                    pi->ConcurrentUseCounter = 0;
                    pi->PreviousUsedFrame = -1;
                    pi->IndexInCandidatesMap = -1;

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
                        _IOCompletionTasks[memPageId] = _file.ReadAsync(_memPages.AsMemory((int)(memPageId*PageSize), (int)PageSize)).AsTask();
                    }
                }

                if (TransitionPageTo(pageId, pi, memPageId, loadPage ? PagesAccessMode.Loading : PagesAccessMode.Idle, PagesAccessMode.Reallocating) == false)
                {
                    return AllocateMemoryPage(pageId);
                }

                //Debug.Assert(pageId == _memPageInfosAddr[memPageId].PageId);

                return memPageId;
            }

#if VERBOSELOGGING
            var logFlushCounter = 1;
#endif
            FlushToDiskAsync(true, out var newEntriesCount).Wait();
            
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
            allocCounter++;
            if (allocCounter >= 100)
            {
                int pipo = 0;
            }
            return AllocateMemoryPage(pageId);
        }

        unsafe internal Task FlushToDiskAsync(bool updateMRU, out int mruEntriesAddedCount)
        {
            var flushRevision = _flushRevision;

            LogFlushToDiskStart();

            // We lock the file because we need to prevent concurrent reads/writes.
            lock (_file)
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
                var curPI = _memPageInfosAddr;
                var pagesToWrite = new SortedDictionary<uint, int>();
                var accessPages = new List<int>(256);
                var maxPage = 0UL;
                uint dirtyMask, accessMask = 0;

                for (int i = 0; i < mapLength; i++)
                {
                    dirtyMask = _dirtyMemPageMap[i];
                    if (updateMRU)
                    {
                        accessMask = Interlocked.Exchange(ref _accessedMemPageMap[i], 0);
                    }

                    for (int j = 0; j < 32; j++)
                    {
                        if ((dirtyMask & 1) == 1)
                        {
                            pagesToWrite.Add(curPI->PageId, curI);
                            maxPage = Math.Max(maxPage, curPI->PageId);
                        }

                        if (updateMRU && (accessMask & 1) == 1)
                        {
                            accessPages.Add(curI);
                        }

                        dirtyMask >>= 1;
                        accessMask >>= 1;
                        ++curI;
                        ++curPI;
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
                        var pi = &_memPageInfosAddr[memPageId];
                        var pageId = pi->PageId;

                        if (isFrag)
                        {
                            Buffer.MemoryCopy(_memPagesAddr + (memPageId * PageSize), writePageAddr + (i*pageSize), pageSize, pageSize);
                        }
                        TransitionPageTo(pageId, pi, memPageId, PagesAccessMode.Saving, PagesAccessMode.Idle);
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
                    _file.Position = segment.StartPageId * PageSize;
                    var task = _file.WriteAsync(src, srcOffset, segment.PageCount * (int)PageSize);
                    task.Wait(); //TODO REMOVE

                    // Update the IO task map
                    for (int i = 0; i < segment.PageCount; i++)
                    {
                        var memPageId = isFrag ? fragMemPageIdArray[segment.StartMemPageId+i] : segment.StartMemPageId + i;
                        _IOCompletionTasks[memPageId] = task;
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
                        int hitDiff = _memPageInfosAddr[y].HitCounter - _memPageInfosAddr[x].HitCounter;

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
                            var pi = &_memPageInfosAddr[memPageId];

                            // If PreviousUsedFrame is used, we have to remove the reference of the MemPage in the MRU for that frame
                            if ((pi->PreviousUsedFrame != -1) && (pi->IndexInCandidatesMap != -1))
                            {
                                // Let's cache the Candidate list for the "current" frame we're processing
                                if (candidatesFrame != pi->PreviousUsedFrame)
                                {
                                    _MRU.TryGetValue(pi->PreviousUsedFrame, out candidates);
                                    candidatesFrame = pi->PreviousUsedFrame;
                                }

                                //Debug.Assert(pi->IndexInCandidatesMap==-1 ||candidates[pi->IndexInCandidatesMap] == memPageId, $"MRU Candidates list integrity failure, Frame {candidatesFrame}, Index: {pi->IndexInCandidatesMap}, MemPageId {memPageId}.");

                                // Remove the page, we don't call RemoveAt because the PageInfo stores index into this list, we can't alter the indices
                                candidates[pi->IndexInCandidatesMap] = -1;
                            }
                            
                            // Reference the page into the new MRU Frame
                            pi->PreviousUsedFrame = curFrame;
                            pi->IndexInCandidatesMap = newCandidates.Count;
                            newCandidates.Add(memPageId);
                        }
                    }
                    // TODO We could defragment the Candidates list of the Frame we removed some pages, this would requires to adjust the
                    //  'IndexInCandidatesMap' field in the PageInfo but we would get rid of the invalid entries (the ones that contain -1)
                }

                Interlocked.Increment(ref _flushRevision);

                _lastFlushTask = Task.WhenAll(issuedIOP.ToArray());
                _lastFlushTask.Wait();

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

            _memPageInfosHandle.Free();
            _memPageInfosAddr = null;
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

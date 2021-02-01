using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
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

        private VirtualDiskManager.PageInfo* _owner;

        internal ReadOnlyPageAccessor(VirtualDiskManager.PageInfo* owner, byte* page)
        {
            _owner = owner;
            Interlocked.Increment(ref _owner->UseCounter);

            Page = page;
        }

        public void Dispose()
        {
            if (_owner == null)
            {
                return;
            }

            Interlocked.Decrement(ref _owner->UseCounter);

            _owner = null;
        }
    }

    unsafe public struct PageAccessor : IDisposable
    {
        public byte* Page { get; }

        private VirtualDiskManager.PageInfo* _owner;

        internal PageAccessor(VirtualDiskManager.PageInfo* owner, byte* page)
        {
            _owner = owner;
            Page = page;
        }

        unsafe public void Dispose()
        {
            if (_owner == null)
            {
                return;
            }
            Interlocked.Decrement(ref _owner->UseCounter);

            _owner = null;
        }
    }

    public class VirtualDiskManager : IDisposable
    {
        internal const long PageSize = 8192;
        internal const int PageSizePow2 = 13; // 2^( PageSizePow2 = PageSize
        internal const int DatabaseFormatRevision = 1;
        internal const int MinimumDatabaseFileChunkSize = 64 * 1024 * 1024;
        internal const ulong MinimumCacheSize = 512 * 1024 * 1024;
        
        private readonly DatabaseConfiguration _configuration;
        private readonly TimeManager _timeManager;

        private bool _isDisposed;

        private FileStream _file;

        private byte[] _memPages;
        private GCHandle _memPagesHandle;
        unsafe private byte* _memPagesAddr;

        private readonly int _memPagesCount;
        private PageInfo[] _memPagesInfo;
        private GCHandle _memPageInfosHandle;
        unsafe private PageInfo* _memPageInfosAddr;
        private readonly ConcurrentDictionary<uint, int> _memPageIdByPageID;

        private SortedDictionary<int, List<int>> _MRU;
        
        // The map store 2 bits for each cached pages. The first bit is set when the page is accessed during the frame, the
        //  second is set if the page was modified. At the end of the frame we use these info to save the modified page and
        //  update the MRU & PreviousUsedFrame.
        private readonly uint[] _accessedMemPageMap;

        internal struct PageInfo
        {
            public uint PageID;
            public uint PreviousUsedFrame;
            public int UseCounter;
            public int HitCounter;
        }

        public VirtualDiskManager(IConfiguration<DatabaseConfiguration> dc, TimeManager timeManager)
        {
            try
            {
                _configuration = dc.Value;
                _timeManager = timeManager;

                // Create the cache of the page, pin it and keeps its address
                var cacheSize = _configuration.DatabaseCacheSize;
                _memPages = new byte[cacheSize];
                _memPagesHandle = GCHandle.Alloc(_memPages, GCHandleType.Pinned);
                unsafe
                {
                    _memPagesAddr = (byte*)_memPageInfosHandle.AddrOfPinnedObject();
                }

                // Create the Memory Page info table
                _memPagesCount = (int)(cacheSize >> PageSizePow2);
                _memPagesInfo = new PageInfo[_memPagesCount];
                _memPageInfosHandle = GCHandle.Alloc(_memPagesInfo, GCHandleType.Pinned);
                unsafe
                {
                    _memPageInfosAddr = (PageInfo*)_memPageInfosHandle.AddrOfPinnedObject();
                }

                _memPageIdByPageID = new ConcurrentDictionary<uint, int>();

                _MRU = new SortedDictionary<int, List<int>>();
                _accessedMemPageMap = new uint[(_memPagesCount + 15) / 16];   // 2 bits per pages
                
                // Fill the MRU with all the pages as marked used for frame 0
                var allPages = new List<int>(_memPagesCount);
                for (int i = 0; i < _memPagesCount; i++)
                {
                    allPages.Add(i);
                }
                _MRU.Add(0, allPages);

                // Init or load the file
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
            catch
            {
                Dispose();
            }
        }

        unsafe public ReadOnlyPageAccessor RequestPageRead(uint pageId)
        {
            var memPageId = RequestPage(pageId, false);

            return new ReadOnlyPageAccessor(&_memPageInfosAddr[memPageId], &_memPagesAddr[memPageId*PageSize]);
        }

        unsafe public PageAccessor RequestPageReadWrite(uint pageId)
        {
            var memPageId = RequestPage(pageId, true);
            
            return new PageAccessor(&_memPageInfosAddr[memPageId], &_memPagesAddr[memPageId*PageSize]);
        }

        unsafe private int RequestPage(uint pageId, bool readWrite)
        {
            // Get the memory page from the cache, if it fails we allocate a new one
            if (_memPageIdByPageID.TryGetValue(pageId, out var memPageId) == false)
            {
                // Page is not cached, we assign an available Memory Page to it
                memPageId = AllocateMemoryPage(pageId);
            }
            var pageInfo = &_memPageInfosAddr[memPageId];

            // Increment the Use Counter
            // We want to address this in a thread-safe way, avoiding race condition, to succeed:
            // We increment atomically the page usage counter
            //  - If the result is greater than 0: the page can be safely used
            //  - If the result less or equal to 0: the page is about to be reallocated for another Disk Page, we can't use it, so
            //    we decrement the counter to release our usage (to balance the increment we did above) and call request again.
            if (Interlocked.Increment(ref pageInfo->UseCounter) <= 0)
            {
                Interlocked.Decrement(ref pageInfo->UseCounter);

                // Note: we may not be happy with this, this could lead to a stack overflow if the synchronization mechanism are
                //  somehow buggy... Maybe a counter, SpinWait and retrying the _pageInfoIdByPageID would be more robust. If the
                //  counter reaches zero we throw...
                return RequestPage(pageId, readWrite);
            }

            // Mark the page as accessed in the access map
            var offset = memPageId >> 4;
            var index = (memPageId & 0xF);
            var mask = (uint)(1 << index  | (readWrite ? (2 << index) : 0));
            Interlocked.Or(ref _accessedMemPageMap[offset], mask);

            return memPageId;
        }

        unsafe private int AllocateMemoryPage(uint pageId)
        {
            // Iterate from the oldest frame to the more recent one
            foreach (var kvp in _MRU)
            {
                var candidates = kvp.Value;
                var candidatesCount = candidates.Count;

                // Candidates are listed from the most recent used to the least ones
                // So we iterate from the end to the beginning
                for (int i = candidatesCount-1; i >= 0; i--)
                {
                    // Decrement the Use Counter to attempt to signal for release
                    // We want to address this in a thread-safe way, avoiding race condition, to succeed:
                    // We decrement atomically the page usage counter
                    //  - If the result is -1: the page can be safely used
                    //  - Otherwise we move to the next one
                    int memPageId = candidates[i];
                    var pi = &_memPageInfosAddr[memPageId];
                    if (Interlocked.Decrement(ref pi->UseCounter) == -1)
                    {
                        // We need to remove the page from the _memPageIdByPageID before we reassign it
                        if (_memPageIdByPageID.TryRemove(pi->PageID, out var tpsId))
                        {
                            Debug.Assert(tpsId == memPageId);
                        }

                        //Assign the memory page to the requested disk page
                        pi->PageID = pageId;
                        pi->PreviousUsedFrame = _timeManager.ExecutionFrame;
                        pi->HitCounter = 0;
                        if (Interlocked.Increment(ref pi->UseCounter) != 0)
                        {
                            // TODO Spin wait to wait value to reach 0, then throw if it doesn't happen in a meaningful time?
                        }

                        // Remove the memPageId from the candidates list
                        candidates.RemoveAt(i);

                        // Update
                        _memPageIdByPageID.TryAdd(pageId, memPageId);

                        return memPageId;
                    }

                }
            }

            throw new NotImplementedException();
        }

        unsafe private void CreateDatabaseFile()
        {
            // Create the Files
            var filePathName = BuildDatabasePathFileName();
            using (var fs = File.Create(filePathName))
            {
                fs.SetLength((long)PageSize);
            }

            _file = File.Open(filePathName, FileMode.Open, FileAccess.ReadWrite);

            var c = _configuration;

            using (var pa = RequestPageReadWrite(0))
            {
                var h = (RootFileHeader*)pa.Page;
                StoreString("TyphonDatabase", h->HeaderSignature, 32);
                h->DatabaseFormatRevision = DatabaseFormatRevision;
                StoreString(c.DatabaseName, h->DatabaseName, 64);
            }
        }

        private void LoadDatabaseFile()
        {
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

            _memPageInfosHandle.Free();
            _memPageInfosAddr = null;
            _memPagesInfo = null;

            _memPagesHandle.Free();
            _memPagesAddr = null;
            _memPages = null;

            _isDisposed = true;
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
    }
}

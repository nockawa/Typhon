// unset

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    /// <summary>
    /// Segment to store variable size buffer of elements
    /// </summary>
    /// <remarks>
    /// The segment stores multiple buffers containing a variable size of a uniform element type.
    /// The internal structure is simple:
    ///  - The segment is based from <see cref="ChunkBasedSegment"/>, each chunk store a given number of elements (may be variable because we also use
    ///    the chunk's data for internal data storage).
    ///  - Chunks are linked together to form a forward linked list allowing a sequential processing of the buffer (we maintain two linked-list, one for enumeration
    ///    using the Accessor and the other one to locate fre chunks).
    ///  - Grow is fast as it's just allocating one more chunk and link it. Append is relatively fast as we know where to put the element using a linked-list or
    ///    chunks containing free entries.
    ///  - Elements can be removed, the chunk is then packed to store the occupied entries at first positions, elements are located by their ChunkId and then
    ///    a linear search into it.
    ///  - Reading the whole buffer requires nested loop pattern using the <see cref="VariableSizedBufferReadOnlyAccessor{T}"/> accessor.
    ///  - Empty chunks are being removed (if exclusive access can be made) during enumeration via the ReadOnlyAccessor.
    ///  - There is no API for Random access of an element inside a given buffer, it could be done but would be slow.
    /// </remarks>
    public class VariableSizedBufferSegment<T> where T : unmanaged
    {
        internal readonly int ElementSize;
        internal readonly int ElementCountRootChunk;
        internal readonly int ElementCountPerChunk;

        protected ChunkBasedSegmentAccessorPool SegmentAccessorPool;
        protected internal ChunkBasedSegment Segment => SegmentAccessorPool.Segment;

        unsafe public VariableSizedBufferSegment(ChunkBasedSegment segment, ChunkBasedSegmentAccessorPool segmentAccessorPool = null)
        {
            ElementSize = sizeof(T);
            var stride = segment.Stride;
            ElementCountRootChunk = (stride - sizeof(VariableSizedBufferRootHeader)) / ElementSize;
            ElementCountPerChunk = (stride - sizeof(int)) / ElementSize;
            SegmentAccessorPool = segmentAccessorPool ?? new ChunkBasedSegmentAccessorPool(segment, 4, 4);
        }

        unsafe public int AllocateBuffer()
        {
            // Allocate and initialize the first chunk of the Buffer
            var segment = Segment;
            var chunkId = segment.AllocateChunk(false);
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(SegmentAccessorPool.RW.GetChunkAddress(chunkId));
            rh.Control = 0;
            rh.ReadReferenceCounter = 0;
            rh.FirstFreeChunkId = chunkId;
            rh.FirstStoredChunkId = 0;
            rh.Header.NextChunkId = 0;
            rh.Header.ElementCount = 0;
            return chunkId;
        }

        unsafe public int AddElement(int bufferId, T value)
        {
            // Fetch the chunk, pin it to prevent its page to be discarded by subsequent chunk accesses
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(SegmentAccessorPool.RW.GetChunkAddress(bufferId, true));
            try
            {
                LockBuffer(ref rh);
                int curFreeChunkId = rh.FirstFreeChunkId;

                var curFreeChunkAddr = SegmentAccessorPool.RW.GetChunkAddress(curFreeChunkId);
                ref var curFreeChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curFreeChunkAddr);
                
                var isRoot = bufferId == curFreeChunkId;
                var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

                if (curFreeChunkHeader.ElementCount == chunkCapacity)
                {

                }


                var baseElementAddr = (T*)(curFreeChunkAddr + (isRoot ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader)));
                baseElementAddr[curFreeChunkHeader.ElementCount++] = value;

/*
                // If we are filling the chunk for the first time, link it to the stored chunk linked list
                if (curFreeChunkHeader.ElementCount == 1)
                {
                    curFreeChunkHeader.PrevChunkId = 0;
                    curFreeChunkHeader.NextChunkId = rh.FirstStoredChunkId;
                    rh.FirstStoredChunkId = curFreeChunkId;
                    if (rh.FirstStoredChunkId != 0)
                    {

                    }
                }

                // The chunk is full?
                if (curFreeChunkHeader.ElementCount == chunkCapacity)
                {
                    // Allocate a new chunk if needed
                    if (curFreeChunkHeader.NextChunkId == 0)
                    {
                        var newChunkId = Segment.AllocateChunk(false);
                        curFreeChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(SegmentAccessorPool.RW.GetChunkAddress(newChunkId));
                        curFreeChunkHeader.ElementCount = 0;
                        curFreeChunkHeader.NextChunkId = 0;
                        curFreeChunkHeader.PrevChunkId = 0;

                        rh.FirstFreeChunkId = newChunkId;
                    }
                    else
                    {
                        // Make the next free chunk the new first
                        rh.FirstFreeChunkId = curFreeChunkHeader.NextChunkId;
                    }
                } 
*/
                return curFreeChunkId;
            }
            finally
            {
                ReleaseLockOnBuffer(ref rh);
                SegmentAccessorPool.RW.UnpinChunk(bufferId);
            }
        }

        public VariableSizedBufferReadOnlyAccessor<T> GetReadOnlyAccessor(int bufferId) => new(this, bufferId);

        private void LockBuffer(ref VariableSizedBufferRootHeader rh)
        {
            var threadId = Thread.CurrentThread.ManagedThreadId;
            if (Interlocked.CompareExchange(ref rh.Control, threadId, 0) != 0)
            {
                var sw = new SpinWait();
                while (Interlocked.CompareExchange(ref rh.Control, threadId, 0) != 0)
                {
                    sw.SpinOnce();
                }
            }
        }

        private void ReleaseLockOnBuffer(ref VariableSizedBufferRootHeader header) => header.Control = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VariableSizedBufferRootHeader
    {
        public VariableSizedBufferChunkHeader Header;   // Must be first member
        public volatile int Control;
        public volatile int ReadReferenceCounter;
        public int FirstFreeChunkId;
        public int FirstStoredChunkId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VariableSizedBufferChunkHeader
    {
        public int NextChunkId;
        public int ElementCount;
    }

    public struct VariableSizedBufferReadOnlyAccessor<T> : IDisposable where T : unmanaged
    {
        private readonly ChunkBasedSegment _segment;
        private int _rootChunkId;
        private readonly int _stride;
        
        private ReadOnlyPageAccessor _pageAccessor;

        private unsafe byte* _headerAddr;
        private unsafe byte* _elementAddr;
        private int _elementCount;

        public bool IsValid => _rootChunkId != 0;
        public unsafe ReadOnlySpan<T> Elements => new(_elementAddr, _elementCount);

        unsafe public VariableSizedBufferReadOnlyAccessor(VariableSizedBufferSegment<T> owner, int rootChunkId)
        {
            _segment = owner.Segment;
            _rootChunkId = rootChunkId;
            var (segmentIndex, offset) = _segment.GetChunkLocation(_rootChunkId);
            _pageAccessor = _segment.GetPageReadOnly(segmentIndex);
            _stride = _segment.Stride;

            // The page accessor is Read-Only but we modify the content of the chunk!!!
            // It's ok because we modify concurrency synchronization variables only, we don't want changes to be detected because of this and we want to ensure multiple
            //  concurrent read accesses.
            var chunkAddr = _pageAccessor.GetElementAddr(offset, _stride, segmentIndex == 0);
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(chunkAddr);

            // Extend lifetime on read usage by incrementing the reference counter
            Interlocked.Increment(ref rh.ReadReferenceCounter);
            // Make sure there's no ongoing write
            if (rh.Control != 0)
            {
                var sw = new SpinWait();
                while (rh.Control != 0)
                {
                    sw.SpinOnce();
                }
            }

            // Switch to the first chunk that contains stored data
            var firstChunkId = rh.FirstStoredChunkId;
            (segmentIndex, offset) = _segment.GetChunkLocation(firstChunkId);
            if (_pageAccessor.PageId != _segment.Pages[segmentIndex])
            {
                if (_pageAccessor.IsValid) _pageAccessor.Dispose();
                _pageAccessor = _segment.GetPageReadOnly(segmentIndex);
            }

            _headerAddr = _pageAccessor.GetElementAddr(offset, _stride, segmentIndex == 0);
            ref var h = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(_headerAddr);
            _elementAddr = _headerAddr + (firstChunkId==rootChunkId ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader));
            _elementCount = h.ElementCount;
        }

        unsafe public bool NextChunk()
        {
            ref var h = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(_headerAddr);
            var nextChunkId = h.NextChunkId;
            if (nextChunkId == 0)
            {
                return false;
            }

            var (segmentIndex, offset) = _segment.GetChunkLocation(nextChunkId);
            if (_pageAccessor.PageId != _segment.Pages[segmentIndex])
            {
                if (_pageAccessor.IsValid) _pageAccessor.Dispose();
                _pageAccessor = _segment.GetPageReadOnly(segmentIndex);
            }

            _headerAddr = _pageAccessor.GetElementAddr(offset, _stride, segmentIndex == 0);
            _elementAddr = _headerAddr + (nextChunkId == _rootChunkId ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader));
            _elementCount = ((VariableSizedBufferChunkHeader*)_headerAddr)->ElementCount;

            return true;
        }

        unsafe public void Dispose()
        {
            if (IsValid == false) return;

            _pageAccessor.Dispose();

            var (segmentIndex, offset) = _segment.GetChunkLocation(_rootChunkId);
            using var accessor = _segment.GetPageReadWrite(segmentIndex);
            ref var h = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(accessor.GetElementAddr(offset, _segment.Stride, segmentIndex == 0));

            // Decrement Read reference counter to release usage
            Interlocked.Decrement(ref h.ReadReferenceCounter);
            _rootChunkId = 0;
        }
    }
}
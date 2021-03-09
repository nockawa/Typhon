// unset

using System;
using System.Collections.Generic;
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
            rh.Lock.Reset();
            rh.FirstFreeChunkId = 0;
            rh.FirstStoredChunkId = chunkId;
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
                // Lock the whole buffer as we are going to update it
                LockBuffer(ref rh);

                // Get the first chunk containing free space
                int curChunkId = rh.FirstStoredChunkId;

                var curChunkAddr = SegmentAccessorPool.RW.GetChunkAddress(curChunkId);
                ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);
                
                var isRoot = bufferId == curChunkId;
                var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

                // If we reached capacity, get a new chunk
                if (curChunkHeader.ElementCount == chunkCapacity)
                {
                    var nextChunkId = curChunkId;

                    // Take a free chunk or allocate a new one
                    var hasFreeChunk = rh.FirstFreeChunkId != 0;
                    curChunkId = hasFreeChunk ? rh.FirstFreeChunkId : Segment.AllocateChunk(false);

                    // Fetch the new chunk
                    curChunkAddr = SegmentAccessorPool.RW.GetChunkAddress(curChunkId);
                    curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                    // If we've allocated a new chunk, initialize it
                    if (hasFreeChunk == false)
                    {
                        curChunkHeader.ElementCount = 0;
                        curChunkHeader.NextChunkId = 0;
                    }

                    // Update the link to the first free chunk with the next of the one we're taking
                    rh.FirstFreeChunkId = curChunkHeader.NextChunkId;

                    // Link our new free chunk to the previous (full) one
                    curChunkHeader.NextChunkId = nextChunkId;

                    // Update the first stored chunk to the new one
                    rh.FirstStoredChunkId = curChunkId;

                    // Update root and capacity as we switched to a new chunk
                    isRoot = bufferId == curChunkId;
                }

                // Add our element to the chunk
                var baseElementAddr = (T*)(curChunkAddr + (isRoot ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader)));
                baseElementAddr[curChunkHeader.ElementCount++] = value;

                return curChunkId;
            }
            finally
            {
                ReleaseLockOnBuffer(ref rh);
                SegmentAccessorPool.RW.UnpinChunk(bufferId);
            }
        }

        unsafe public bool DeleteElement(int bufferId, int elementId, T element)
        {
            // Fetch the chunk, pin it to prevent its page to be discarded by subsequent chunk accesses
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(SegmentAccessorPool.RW.GetChunkAddress(bufferId, true));
            try
            {
                // Lock the whole buffer as we are going to update it
                LockBuffer(ref rh);

                // Fetch the chunk storing the element
                var elementChunk = SegmentAccessorPool.RW.GetChunkAddress(elementId);
                ref var elementChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(elementChunk);
                var isRoot = bufferId == elementId;
                var baseElementAddr = (T*)(elementChunk + (isRoot ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader)));

                // Look for our element
                var count = elementChunkHeader.ElementCount;
                int i;
                for (i = 0; i < count; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(baseElementAddr[i], element))
                    {
                        break;
                    }
                }

                if (i == count) return false;

                // Replace this slot by the last element to keep an un-fragmented collection
                baseElementAddr[i] = baseElementAddr[count - 1];
#if DEBUG
                baseElementAddr[count - 1] = default(T);
#endif
                --elementChunkHeader.ElementCount;

                return true;
            }
            finally
            {
                ReleaseLockOnBuffer(ref rh);
            }
        }

        public VariableSizedBufferReadOnlyAccessor<T> GetReadOnlyAccessor(int bufferId) => new(this, bufferId);

        private void LockBuffer(ref VariableSizedBufferRootHeader rh) => rh.Lock.EnterWrite();

        private void ReleaseLockOnBuffer(ref VariableSizedBufferRootHeader header) => header.Lock.ExitWrite();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VariableSizedBufferRootHeader
    {
        public VariableSizedBufferChunkHeader Header;   // Must be first member
        public ReaderWriterSpinLock Lock;
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
        private readonly int _stride;

        private int _rootChunkId;
        private readonly unsafe byte* _rootChunkAddr;

        private readonly ChunkReadOnlyRandomAccessor _accessor;

        private int _curChunkId;
        private unsafe byte* _curChunkAddr;

        private unsafe byte* _elementAddr;
        private int _elementCount;

        public bool IsValid => _rootChunkId != 0;
        public unsafe ReadOnlySpan<T> Elements => new(_elementAddr, _elementCount);

        unsafe public VariableSizedBufferReadOnlyAccessor(VariableSizedBufferSegment<T> owner, int rootChunkId)
        {
            _segment = owner.Segment;
            _rootChunkId = rootChunkId;
            _stride = _segment.Stride;

            _accessor = new ChunkReadOnlyRandomAccessor(_segment, 8);

            // The page accessor is Read-Only but we modify the content of the chunk!!!
            // It's ok because we modify concurrency synchronization variables only, we don't want changes to be detected because of this and we want to ensure multiple
            //  concurrent read accesses.
            _rootChunkAddr = _accessor.GetChunkAddress(_rootChunkId, true);
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);

            // Enter read mode
            rh.Lock.EnterRead();

            // Switch to the first chunk that contains stored data
            _curChunkId = rh.FirstStoredChunkId;
            _curChunkAddr = _accessor.GetChunkAddress(_curChunkId, true);

            _elementAddr = _curChunkAddr + (_curChunkId==rootChunkId ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader));
            _elementCount = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->ElementCount;
        }

        unsafe public bool NextChunk()
        {
            // Read next chunk from the current header
            var nextChunkId = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->NextChunkId;
            
            // Quit if there's no more
            if (nextChunkId == 0)
            {
                return false;
            }

            // Fetch the new chunk
            var nextChunkAddr = _accessor.GetChunkAddress(nextChunkId, true);
            var nextChunkElementCount = ((VariableSizedBufferChunkHeader*)nextChunkAddr)->ElementCount;

            // Check if the chunk is empty, then try to remove it from the storage list
            if (nextChunkElementCount == 0)
            {
                ref var rootChunk = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);

                // Try to promote the Buffer from read to read/write because we need to make changes
                if (rootChunk.Lock.TryPromoteToWrite())
                {
                    // Try to promote the root chunk to read/write access
                    if (_accessor.TryPromoteChunk(_rootChunkId))
                    {
                        // Setup our forward link list info
                        var prevChunkId = _curChunkId;
                        var curChunkId = nextChunkId;
                        var prevChunk = (VariableSizedBufferChunkHeader*)_accessor.GetChunkAddress(prevChunkId);
                        var curChunk = (VariableSizedBufferChunkHeader*)_accessor.GetChunkAddress(curChunkId, true);

                        // We jump other empty chunk as long as there are some
                        while ((curChunk != null) && (curChunk->ElementCount == 0))
                        {
                            // To collect an empty chunk we need to promote both the previous and current chunks.
                            // We can't make modification otherwise
                            // BEWARE: Each successful Promotion need its corresponding demotion call!
                            if (_accessor.TryPromoteChunk(prevChunkId))
                            {
                                if (_accessor.TryPromoteChunk(curChunkId))
                                {
                                    // Fix the storage link-list by removing the empty chunk
                                    prevChunk->NextChunkId = curChunk->NextChunkId;

                                    // Link the empty chunk to the rest of the free link-list
                                    curChunk->NextChunkId = rootChunk.FirstFreeChunkId;

                                    // First empty chunk is pointing to the one we just pop
                                    rootChunk.FirstFreeChunkId = curChunkId;

                                    _accessor.DemoteChunk(curChunkId);
                                }
                                _accessor.DemoteChunk(prevChunkId);
                            }

                            // Cur Chunk is the empty one, we don't need it as we're stepping over, so release the pin
                            _accessor.UnpinChunk(curChunkId);

                            // Update the new current chunk to be the next in line
                            curChunkId = prevChunk->NextChunkId;
                            curChunk = (curChunkId != 0) ? (VariableSizedBufferChunkHeader*)_accessor.GetChunkAddress(curChunkId, true) : null;
                        }

                        // Update members needed for the end of the method
                        nextChunkId = curChunkId;
                        nextChunkAddr = (byte*)curChunk;

                        // Release pin and lock
                        // NOTE TODO : I'm not sure UnpinChunk is call appropriately for all corresponding pin ones...
                        _accessor.UnpinChunk(curChunkId);
                        _accessor.DemoteChunk(_rootChunkId);
                    }
                    rootChunk.Lock.DemoteWriteAccess();
                }
            }

            if (nextChunkAddr == null)
            {
                return false;
            }

            _curChunkId = nextChunkId;
            _curChunkAddr = nextChunkAddr;
            _elementAddr = _curChunkAddr + (_curChunkId == _rootChunkId ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader));
            _elementCount = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->ElementCount;

            return true;
        }

        unsafe public void Dispose()
        {
            if (IsValid == false) return;

            ref var h = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
            h.Lock.ExitRead();

            _accessor.UnpinChunk(_rootChunkId);
            _accessor.Dispose();
            _rootChunkId = 0;
        }
    }
}
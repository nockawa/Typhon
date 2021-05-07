// unset

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine
{
    /// <summary>
    /// Segment to store variable size buffer of elements
    /// </summary>
    /// <remarks>
    /// The segment stores multiple buffers containing a variable size of a uniform element type.
    /// The internal structure is simple:
    ///  - The segment is based from <see cref="ChunkBasedSegment"/>, each chunk stores a given number of elements (may be variable because we also use
    ///    the chunk's data for internal data storage).
    ///  - Chunks are linked together to form a forward linked list allowing a sequential processing of the buffer (we maintain two linked-list, one for enumeration
    ///    using the Accessor and the other one to locate free chunks).
    ///  - Grow is fast as it's just allocating one more chunk and link it. Append is relatively fast as we know where to put the element using a linked-list or
    ///    chunks containing free entries.
    ///  - Elements can be removed, the chunk is then packed to store the occupied entries at first positions, elements are located by their ChunkId and then
    ///    a linear search into it.
    ///  - Reading the whole buffer requires nested loop pattern using the <see cref="VariableSizedBufferAccessor{T}"/> accessor.
    ///  - Empty chunks are being removed (if exclusive access can be made) during enumeration via the ReadOnlyAccessor.
    ///  - There is no API for Random access of an element inside a given buffer, it could be done but would be slow.
    /// </remarks>
    public class VariableSizedBufferSegment<T> where T : unmanaged
    {
        internal readonly int ElementSize;
        internal readonly int ElementCountRootChunk;
        internal readonly int ElementCountPerChunk;

        protected ChunkRandomAccessor ChunkAccessor;
        protected internal ChunkBasedSegment Segment => ChunkAccessor.Segment;

        unsafe public VariableSizedBufferSegment(ChunkBasedSegment segment, ChunkRandomAccessor accessor = null)
        {
            ElementSize = sizeof(T);
            var stride = segment.Stride;
            ElementCountRootChunk = (stride - sizeof(VariableSizedBufferRootHeader)) / ElementSize;
            ElementCountPerChunk = (stride - sizeof(VariableSizedBufferChunkHeader)) / ElementSize;
            ChunkAccessor = accessor ?? segment.CreateChunkRandomAccessor(4);
        }

        unsafe public int AllocateBuffer()
        {
            // Allocate and initialize the first chunk of the Buffer
            var segment = Segment;
            var chunkId = segment.AllocateChunk(false);
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(ChunkAccessor.GetChunkAddress(chunkId, dirtyPage: true));
            rh.Lock.Reset();
            rh.FirstFreeChunkId = 0;
            rh.FirstStoredChunkId = chunkId;
            rh.TotalCount = 0;
            rh.TotalFreeChunk = 0;
            rh.Header.NextChunkId = 0;
            rh.Header.ElementCount = 0;
            return chunkId;
        }

        unsafe public void DeleteBuffer(int bufferId)
        {
            // Fetch the root chunk, pin it to prevent its page to be discarded by subsequent chunk accesses
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(ChunkAccessor.GetChunkAddress(bufferId, true, true));
            try
            {
                // Lock the whole buffer as we are going to update it
                LockBuffer(ref rh);

                // Get the first chunk containing free space
                int curChunkId = rh.FirstStoredChunkId;

                while (curChunkId != 0)
                {
                    var curChunkAddr = ChunkAccessor.GetChunkAddress(curChunkId, dirtyPage: true);
                    ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                    var toDeleteChunkId = curChunkId;
                    curChunkId = curChunkHeader.NextChunkId;

                    if (toDeleteChunkId != bufferId)
                    {
                        Segment.FreeChunk(toDeleteChunkId);
                    }
                }
            }
            finally
            {
                ReleaseLockOnBuffer(ref rh);
                Segment.FreeChunk(bufferId);
                ChunkAccessor.UnpinChunk(bufferId);
            }
        }

        unsafe public int AddElement(int bufferId, T value)
        {
            // Fetch the root chunk, pin it to prevent its page to be discarded by subsequent chunk accesses
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(ChunkAccessor.GetChunkAddress(bufferId, true, true));
            try
            {
                // Lock the whole buffer as we are going to update it
                LockBuffer(ref rh);

                // Get the first chunk containing free space
                int curChunkId = rh.FirstStoredChunkId;

                var curChunkAddr = ChunkAccessor.GetChunkAddress(curChunkId);
                ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);
                
                var isRoot = bufferId == curChunkId;
                var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

                // If we reached capacity, get a new chunk
                if (curChunkHeader.ElementCount == chunkCapacity)
                {
                    var nextChunkId = curChunkId;

                    // Take a free chunk or allocate a new one
                    var hasFreeChunk = rh.FirstFreeChunkId != 0;
                    if (hasFreeChunk)
                    {
                        curChunkId = rh.FirstFreeChunkId;
                        --rh.TotalFreeChunk;
                    }
                    else
                    {
                        curChunkId = Segment.AllocateChunk(false);
                    }

                    // Fetch the new chunk
                    curChunkAddr = ChunkAccessor.GetChunkAddress(curChunkId, dirtyPage: true);
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
                
                ++rh.TotalCount;
                return curChunkId;
            }
            finally
            {
                ReleaseLockOnBuffer(ref rh);
                ChunkAccessor.UnpinChunk(bufferId);
            }
        }

        unsafe public int DeleteElement(int bufferId, int elementId, T element)
        {
            // Fetch the chunk, pin it to prevent its page to be discarded by subsequent chunk accesses
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(ChunkAccessor.GetChunkAddress(bufferId, true, true));
            try
            {
                // Lock the whole buffer as we are going to update it
                LockBuffer(ref rh);

                // Fetch the chunk storing the element
                var elementChunk = ChunkAccessor.GetChunkAddress(elementId, dirtyPage: true);
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

                if (i == count) return -1;

                // Replace this slot by the last element to keep an un-fragmented collection
                baseElementAddr[i] = baseElementAddr[count - 1];
#if DEBUG
                baseElementAddr[count - 1] = default(T);
#endif
                --rh.TotalCount;
                --elementChunkHeader.ElementCount;

                return rh.TotalCount;
            }
            finally
            {
                ReleaseLockOnBuffer(ref rh);
                ChunkAccessor.UnpinChunk(bufferId);
            }
        }

        public VariableSizedBufferAccessor<T> GetReadOnlyAccessor(int bufferId) => new(this, bufferId);

        private void LockBuffer(ref VariableSizedBufferRootHeader rh) => rh.Lock.EnterExclusiveAccess();

        private void ReleaseLockOnBuffer(ref VariableSizedBufferRootHeader header) => header.Lock.ExitExclusiveAccess();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VariableSizedBufferRootHeader
    {
        public VariableSizedBufferChunkHeader Header;   // Must be first member
        public AccessControl Lock;
        public int FirstFreeChunkId;
        public int FirstStoredChunkId;
        public int TotalCount;
        public int TotalFreeChunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VariableSizedBufferChunkHeader
    {
        public int NextChunkId;
        public int ElementCount;
    }

    public struct VariableSizedBufferAccessor<T> : IDisposable where T : unmanaged
    {
        private readonly VariableSizedBufferSegment<T> _owner;
        private readonly ChunkBasedSegment _segment;

        private int _rootChunkId;
        private readonly unsafe byte* _rootChunkAddr;

        private readonly ChunkRandomAccessor _accessor;

        private int _curChunkId;
        private unsafe byte* _curChunkAddr;

        private unsafe byte* _elementAddr;
        private int _elementCount;

        public bool IsValid => _rootChunkId != 0;
        public unsafe ReadOnlySpan<T> ReadOnlyElements => _elementAddr==null ? default : new(_elementAddr, _elementCount);
        public unsafe Span<T> Elements => new(_elementAddr, _elementCount);
        public void DirtyChunk() => _accessor.DirtyChunk(_curChunkId);

        unsafe public VariableSizedBufferAccessor(VariableSizedBufferSegment<T> owner, int rootChunkId)
        {
            _owner = owner;
            _segment = owner.Segment;
            _rootChunkId = rootChunkId;

            _accessor = ChunkRandomAccessor.GetFromPool(_segment, 8);

            _rootChunkAddr = _accessor.GetChunkAddress(_rootChunkId);
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);

            // Enter read mode
            rh.Lock.EnterSharedAccess();

            // Switch to the first chunk that contains stored data
            _curChunkId = rh.FirstStoredChunkId;
            _curChunkAddr = _accessor.GetChunkAddress(_curChunkId, true);

            _elementAddr = _curChunkAddr + (_curChunkId==rootChunkId ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader));
            _elementCount = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->ElementCount;

            if (_elementCount == 0) NextChunk();
        }

        unsafe public bool NextChunk()
        {
            // Read next chunk from the current header
            var nextChunkId = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->NextChunkId;
            var prevChunkId = _curChunkId;
            var prevChunk = (VariableSizedBufferChunkHeader*)_curChunkAddr;

            // Quit if there's no more
            if (nextChunkId == 0)
            {
                _accessor.UnpinChunk(_curChunkId);
                _curChunkId = 0;
                _curChunkAddr = null;
                _elementAddr = null;
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
                if (rootChunk.Lock.TryPromoteToExclusiveAccess())
                {
                    // Try to promote the root chunk to read/write access
                    if (_accessor.TryPromoteChunk(_rootChunkId))
                    {
                        // Setup our forward link list info
                        var curChunkId  = nextChunkId;
                        var curChunk  = (VariableSizedBufferChunkHeader*)nextChunkAddr;

                        // We don't want to chain to the free-list all the empty chunks, would be a waste of space.
                        // Let's keep to grow the current count by 25%, approximately, with a minimum of 8 free chunks
                        var epc = _owner.ElementCountRootChunk;
                        var tc = rootChunk.TotalCount;
                        var freeChunkThreshold = Math.Max(tc / (epc * 4), 8);

                        // To collect an empty chunk we need to promote both the previous and current chunks.
                        // We can't make modification otherwise
                        // BEWARE: Each successful Promotion need its corresponding demotion call!
                        if (_accessor.TryPromoteChunk(prevChunkId))
                        {
                            // We jump other empty chunk as long as there are some
                            while ((curChunk != null) && (curChunk->ElementCount == 0))
                            {
                                if (_accessor.TryPromoteChunk(curChunkId))
                                {
                                    // Fix the storage link-list by removing the empty chunk
                                    prevChunk->NextChunkId = curChunk->NextChunkId;

                                    // Check if we must free the chunk or link it to the free list
                                    if (rootChunk.TotalFreeChunk > freeChunkThreshold)
                                    {
                                        _segment.FreeChunk(curChunkId);
                                    }
                                    else
                                    {
                                        // Link the empty chunk to the rest of the free link-list
                                        curChunk->NextChunkId = rootChunk.FirstFreeChunkId;

                                        // First empty chunk is pointing to the one we just pop
                                        rootChunk.FirstFreeChunkId = curChunkId;
                                        ++rootChunk.TotalFreeChunk;
                                    }

                                    _accessor.DemoteChunk(curChunkId);
                                }

                                // Cur Chunk is the empty one, we don't need it as we're stepping over, so release the pin
                                _accessor.UnpinChunk(curChunkId);

                                // Update the new current chunk to be the next in line
                                curChunkId = prevChunk->NextChunkId;
                                curChunk = (curChunkId != 0) ? (VariableSizedBufferChunkHeader*)_accessor.GetChunkAddress(curChunkId, true) : null;
                            }

                            _accessor.DemoteChunk(prevChunkId);
                        }

                        // Update members needed for the end of the method
                        nextChunkId = curChunkId;
                        nextChunkAddr = (byte*)curChunk;

                        // Demote write access
                        _accessor.DemoteChunk(_rootChunkId);
                    }
                    rootChunk.Lock.DemoteFromExclusiveAccess();
                }
            }

            _accessor.UnpinChunk(prevChunkId);
            // Check if we reached the end of the VSB
            if (nextChunkAddr == null)
            {
                _curChunkId = 0;
                _curChunkAddr = null;
                _elementAddr = null;
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
            h.Lock.ExitSharedAccess();

            if (_curChunkId != 0)
            {
                _accessor.UnpinChunk(_curChunkId);
            }
            _accessor.Dispose();
            _rootChunkId = 0;
        }
    }
}
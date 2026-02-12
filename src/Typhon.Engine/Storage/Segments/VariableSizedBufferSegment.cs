// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential)]
internal struct VariableSizedBufferRootHeader
{
    public VariableSizedBufferChunkHeader Header;   // Must be first member
    public AccessControl Lock;
    public int FirstFreeChunkId;
    public int FirstStoredChunkId;
    public int TotalCount;
    public short TotalFreeChunk;
    public short RefCounter;

    internal void EnterBufferLockForTest() => Lock.EnterExclusiveAccess(ref WaitContext.Null);
    internal void ExitBufferLockForTest() => Lock.ExitExclusiveAccess();
}

[StructLayout(LayoutKind.Sequential)]
internal struct VariableSizedBufferChunkHeader
{
    public int NextChunkId;
    public int ElementCount;
}

[PublicAPI]
public unsafe class VariableSizedBufferSegmentBase
{
    private readonly int _elementSize;
    protected internal readonly int ElementCountRootChunk;
    protected readonly int ElementCountPerChunk;
    public readonly ChunkBasedSegment Segment;

    protected VariableSizedBufferSegmentBase(ChunkBasedSegment segment, int elementSize)
    {
        _elementSize = elementSize;
        var stride = segment.Stride;
        var headerSize = sizeof(VariableSizedBufferRootHeader);
        Debug.Assert(headerSize <= stride, $"Error, stride is too small, should be at least, {headerSize} bytes.");
        
        ElementCountRootChunk = (stride - headerSize) / _elementSize;
        ElementCountPerChunk = (stride - sizeof(VariableSizedBufferChunkHeader)) / _elementSize;
        Segment = segment;
    }

    public int AllocateBuffer(ref EpochChunkAccessor accessor)
    {
        // Allocate and initialize the first chunk of the Buffer
        var segment = accessor.Segment;
        var chunkId = segment.AllocateChunk(false);
        ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(accessor.GetChunkAddress(chunkId, true));
        rh.Lock.Reset();
        rh.FirstFreeChunkId = 0;
        rh.FirstStoredChunkId = chunkId;
        rh.TotalCount = 0;
        rh.TotalFreeChunk = 0;
        rh.RefCounter = 1;
        rh.Header.NextChunkId = 0;
        rh.Header.ElementCount = 0;
        return chunkId;
    }

    public int BufferAddRef(int bufferId, ref EpochChunkAccessor accessor)
    {
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            return ++rh.RefCounter;
        }
        finally
        {
            ReleaseLockOnBuffer(ref rh);
        }
    }

    public int BufferRelease(int bufferId, ref EpochChunkAccessor accessor)
    {
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            var newValue = --rh.RefCounter;
            if (newValue == 0)
            {
                DeleteBuffer(bufferId, ref accessor);
            }
            return newValue;
        }
        finally
        {
            ReleaseLockOnBuffer(ref rh);
        }
    }

    public void DeleteBuffer(int bufferId, ref EpochChunkAccessor accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        var unlock = false;
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            if (!rh.Lock.IsLockedByCurrentThread)
            {
                LockBuffer(ref rh);
                unlock = true;
            }

            if (--rh.RefCounter == 0)
            {
                // Get the first chunk containing free space
                int curChunkId = rh.FirstStoredChunkId;

                while (curChunkId != 0)
                {
                    var curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                    ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                    var toDeleteChunkId = curChunkId;
                    curChunkId = curChunkHeader.NextChunkId;

                    if (toDeleteChunkId != bufferId)
                    {
                        accessor.Segment.FreeChunk(toDeleteChunkId);
                    }
                }
            }
        }
        finally
        {
            if (unlock)
            {
                ReleaseLockOnBuffer(ref rh);
            }
            accessor.Segment.FreeChunk(bufferId);
        }
    }

    internal void LockBuffer(ref VariableSizedBufferRootHeader rh)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rh.Lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/LockBuffer", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }
    }
    internal void ReleaseLockOnBuffer(ref VariableSizedBufferRootHeader header) => header.Lock.ExitExclusiveAccess();
}

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
[PublicAPI]
public class VariableSizedBufferSegment<T> : VariableSizedBufferSegmentBase where T : unmanaged
{
    // protected ChunkRandomAccessor EpochChunkAccessor;

    unsafe public VariableSizedBufferSegment(ChunkBasedSegment segment) : base(segment, sizeof(T))
    {
    }

    unsafe public int AddElement(int bufferId, T value, ref EpochChunkAccessor accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            // Get the first chunk containing free space
            int curChunkId = rh.FirstStoredChunkId;

            var curChunkAddr = accessor.GetChunkAddress(curChunkId);
            ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);
                
            var isRoot = bufferId == curChunkId;
            var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

            // If we reached capacity, get a new chunk
            if (curChunkHeader.ElementCount == chunkCapacity)
            {
                // Take a free chunk or allocate a new one
                var hasFreeChunk = rh.FirstFreeChunkId != 0;
                if (hasFreeChunk)
                {
                    curChunkId = rh.FirstFreeChunkId;
                    --rh.TotalFreeChunk;
                }
                else
                {
                    curChunkId = accessor.Segment.AllocateChunk(false);
                }

                curChunkHeader.NextChunkId = curChunkId;
                
                // Fetch the new chunk
                curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                curChunkHeader.ElementCount = 0;
                curChunkHeader.NextChunkId = 0;

                // Update the link to the first free chunk with the next of the one we're taking
                rh.FirstFreeChunkId = curChunkHeader.NextChunkId;

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
        }
    }

    unsafe public void AddElements(int bufferId, ReadOnlySpan<T> items, ref EpochChunkAccessor accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            // Get the first chunk containing free space
            int curChunkId = rh.FirstStoredChunkId;

            var curChunkAddr = accessor.GetChunkAddress(curChunkId);
            ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

            var curSourceIndex = 0;
            var itemsLeftToCopy = items.Length;
            while (itemsLeftToCopy > 0)
            {
                var isRoot = bufferId == curChunkId;
                var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

                // If we reached capacity, get a new chunk
                if (curChunkHeader.ElementCount == chunkCapacity)
                {
                    // Take a free chunk or allocate a new one
                    var hasFreeChunk = rh.FirstFreeChunkId != 0;
                    if (hasFreeChunk)
                    {
                        curChunkId = rh.FirstFreeChunkId;
                        --rh.TotalFreeChunk;
                    }
                    else
                    {
                        curChunkId = accessor.Segment.AllocateChunk(false);
                    }

                    curChunkHeader.NextChunkId = curChunkId;
                
                    // Fetch the new chunk
                    curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                    curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                    curChunkHeader.ElementCount = 0;
                    curChunkHeader.NextChunkId = 0;

                    // Update the link to the first free chunk with the next of the one we're taking
                    rh.FirstFreeChunkId = curChunkHeader.NextChunkId;

                    // Update the first stored chunk to the new one
                    rh.FirstStoredChunkId = curChunkId;

                    // Update root and capacity as we switched to a new chunk
                    isRoot = bufferId == curChunkId;
                }

                var copyLength = Math.Min(chunkCapacity - curChunkHeader.ElementCount, itemsLeftToCopy);
                var dstSpan = new Span<T>((curChunkAddr + (isRoot ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader))),
                    chunkCapacity);
                items.Slice(curSourceIndex, copyLength).CopyTo(dstSpan.Slice(curChunkHeader.ElementCount));
                
                rh.TotalCount += copyLength;
                curChunkHeader.ElementCount += copyLength;
                itemsLeftToCopy -= copyLength;
            }
        }
        finally
        {
            ReleaseLockOnBuffer(ref rh);
        }
    }

    unsafe public int DeleteElement(int bufferId, int elementId, T element, ref EpochChunkAccessor accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            // Fetch the chunk storing the element
            var elementChunk = accessor.GetChunkAddress(elementId, true);
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
        }
    }

    public VariableSizedBufferAccessor<T> GetReadOnlyAccessor(int bufferId) => new(this, bufferId);
    public VariableSizedBufferAccessor<T> GetAccessor(int bufferId, ChangeSet changeSet) => new(this, bufferId, changeSet);

    /// <summary>
    /// Returns a zero-allocation enumerator for iterating over all elements in the buffer.
    /// </summary>
    /// <param name="bufferId">The buffer identifier</param>
    /// <returns>A ref struct enumerator that can be used in foreach loops</returns>
    public BufferEnumerator<T> EnumerateBuffer(int bufferId) => new(this, bufferId);

    public int CloneBuffer(int sourceBufferId, ref EpochChunkAccessor accessor)
    {
        var destBufferId = AllocateBuffer(ref accessor);
        using var source = GetReadOnlyAccessor(sourceBufferId);
        do
        {
            AddElements(destBufferId, source.Elements, ref accessor);
        } while (source.NextChunk());

        return destBufferId;
    }
}

/// <summary>
/// Zero-allocation enumerator for iterating over all elements in a variable-sized buffer.
/// This is a ref struct to ensure stack allocation and zero GC pressure.
/// </summary>
/// <typeparam name="T">The unmanaged element type</typeparam>
[PublicAPI]
public ref struct BufferEnumerator<T> where T : unmanaged
{
    private VariableSizedBufferAccessor<T> _accessor;
    private int _currentIndex;
    private int _currentChunkLength;
    private bool _isValid;

    internal BufferEnumerator(VariableSizedBufferSegment<T> owner, int bufferId)
    {
        _accessor = owner.GetReadOnlyAccessor(bufferId);
        _currentIndex = -1;
        _currentChunkLength = _accessor.ReadOnlyElements.Length;
        _isValid = _currentChunkLength > 0;
    }

    /// <summary>
    /// Returns this enumerator (required for ForEach pattern)
    /// </summary>
    public BufferEnumerator<T> GetEnumerator() => this;

    /// <summary>
    /// Gets the current element as a readonly reference (zero-copy)
    /// </summary>
    public ref readonly T Current
    {
        get => ref _accessor.ReadOnlyElements[_currentIndex];
    }

    /// <summary>
    /// Advances to the next element, automatically traversing chunks as needed
    /// </summary>
    public bool MoveNext()
    {
        if (!_isValid)
        {
            return false;
        }

        _currentIndex++;

        // Check if we're still within the current chunk
        if (_currentIndex < _currentChunkLength)
        {
            return true;
        }

        // Try to move to the next chunk
        if (_accessor.NextChunk())
        {
            _currentIndex = 0;
            _currentChunkLength = _accessor.ReadOnlyElements.Length;
            return _currentChunkLength > 0;
        }

        _isValid = false;
        return false;
    }

    /// <summary>
    /// Disposes the underlying accessor and releases locks
    /// </summary>
    public void Dispose() => _accessor.Dispose();
}

[PublicAPI]
public ref struct VariableSizedBufferAccessor<T> : IDisposable where T : unmanaged
{
    private readonly VariableSizedBufferSegment<T> _owner;
    private readonly ChunkBasedSegment _segment;

    private int _rootChunkId;
    private unsafe byte* _rootChunkAddr;
    private EpochChunkAccessor _accessor;

    private int _curChunkId;
    private unsafe byte* _curChunkAddr;

    private unsafe byte* _elementAddr;
    private int _elementCount;

    public bool IsValid => _rootChunkId != 0;
    public unsafe ReadOnlySpan<T> ReadOnlyElements => _elementAddr==null ? default : new(_elementAddr, _elementCount);
    public unsafe Span<T> Elements => new(_elementAddr, _elementCount);
    public void DirtyChunk() => _accessor.DirtyChunk(_curChunkId);

    unsafe public int TotalCount
    {
        get
        {
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
            return rh.TotalCount;
        }
    }

    unsafe public int RefCounter
    {
        get
        {
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
            return rh.RefCounter;
        }
    }

    unsafe public VariableSizedBufferAccessor(VariableSizedBufferSegment<T> owner, int rootChunkId, ChangeSet changeSet = null)
    {
        _owner = owner;
        _segment = owner.Segment;
        _rootChunkId = rootChunkId;

        _accessor = _segment.CreateEpochChunkAccessor(changeSet);

        _rootChunkAddr = _accessor.GetChunkAddress(rootChunkId);
        ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);

        // Enter read mode
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rh.Lock.EnterSharedAccess(ref wc))
        {
            _accessor.Dispose();
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/BufferRead", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        // Switch to the first chunk that contains stored data
        _curChunkId = _rootChunkId;
        _curChunkAddr = _accessor.GetChunkAddress(_curChunkId);

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
            _curChunkId = 0;
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
            var wcPromote = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
            if (rootChunk.Lock.TryPromoteToExclusiveAccess(ref wcPromote))
            {
                // Try to latch the root chunk for exclusive write access
                if (_accessor.TryLatchExclusive(_rootChunkId))
                {
                    // Setup our forward link list info
                    var curChunkId  = nextChunkId;
                    var curChunk  = (VariableSizedBufferChunkHeader*)nextChunkAddr;

                    // We don't want to chain to the free-list all the empty chunks, would be a waste of space.
                    // Let's keep to grow the current count by 25%, approximately, with a minimum of 8 free chunks
                    var epc = _owner.ElementCountRootChunk;
                    var tc = rootChunk.TotalCount;
                    var freeChunkThreshold = Math.Max(tc / (epc * 4), 8);

                    // To collect an empty chunk we need to latch both the previous and current chunks.
                    // We can't make modifications otherwise
                    // BEWARE: Each successful latch needs its corresponding unlatch call!
                    if (_accessor.TryLatchExclusive(prevChunkId))
                    {
                        // We jump over empty chunks as long as there are some
                        while ((curChunk != null) && (curChunk->ElementCount == 0))
                        {
                            if (_accessor.TryLatchExclusive(curChunkId))
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

                                _accessor.UnlatchExclusive(curChunkId);
                            }

                            // Update the new current chunk to be the next in line
                            curChunkId = prevChunk->NextChunkId;
                            curChunk = (curChunkId != 0) ? (VariableSizedBufferChunkHeader*)_accessor.GetChunkAddress(curChunkId, true) : null;
                        }

                        _accessor.UnlatchExclusive(prevChunkId);
                    }

                    // Update members needed for the end of the method
                    nextChunkId = curChunkId;
                    nextChunkAddr = (byte*)curChunk;

                    // Release exclusive latch on root
                    _accessor.UnlatchExclusive(_rootChunkId);
                }
                rootChunk.Lock.DemoteFromExclusiveAccess();
            }
        }

        // Check if we reached the end of the VSB
        if (nextChunkAddr == null)
        {
            _curChunkId = 0;
            _elementAddr = null;
            return false;
        }

        _curChunkId = nextChunkId;
        _curChunkAddr = _accessor.GetChunkAddress(_curChunkId);
        _elementAddr = _curChunkAddr + (_curChunkId == _rootChunkId ? sizeof(VariableSizedBufferRootHeader) : sizeof(VariableSizedBufferChunkHeader));
        _elementCount = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->ElementCount;

        return true;
    }

    public unsafe void Dispose()
    {
        if (!IsValid)
        {
            // Still need to dispose accessor if it was created
            _accessor.Dispose();
            return;
        }

        ref var h = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
        h.Lock.ExitSharedAccess();

        _accessor.Dispose();
        _rootChunkId = 0;
        _curChunkId = 0;
    }
}
// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public unsafe abstract class BlockAllocatorBase : IDisposable
{
    private ValueTuple<IntPtr, byte[]>[] _pages;
    private readonly ConcurrentBitmapL3All _blockMap;
    private readonly int _entryCountPerPage;
    private readonly int _pageShift;

    protected BlockAllocatorBase(int stride, int entryCountPerPage)
    {
        if (MathHelpers.IsPow2(entryCountPerPage) == false)
        {
            throw new Exception($"Entry count per page must be a power of 2 but {entryCountPerPage} was given");
        }

        var size = stride * entryCountPerPage;
        var page = GC.AllocateUninitializedArray<byte>(size, true);

        Stride = stride;
        _entryCountPerPage = entryCountPerPage;
        _pageShift = BitOperations.Log2((uint)entryCountPerPage);
        _pages = new (IntPtr, byte[])[1];
        _pages[0] = (Marshal.UnsafeAddrOfPinnedArrayElement(page, 0), page);

        _blockMap = new ConcurrentBitmapL3All(entryCountPerPage);
    }

    public int Capacity => _blockMap.Capacity;
    public int AllocatedCount => _blockMap.TotalBitSet;
    protected readonly int Stride;

    protected Span<byte> AllocateBlockAsSpanInternal(out int blockId) => new(AllocateBlockInternal(out blockId), Stride);
    protected byte* AllocateBlockInternal(out int blockId)
    {
        var pages = _pages;
        var map = _blockMap;

        if (map.IsFull)
        {
            Resize(pages.Length + 1);
            pages = _pages;
            map = _blockMap;
        }

        blockId = -1;
        var mask = 0L;
        var count = 1;
        while ((count > 0) && map.FindNextUnsetL0(ref blockId, ref mask))
        {
            if (map.SetL0(blockId))
            {
                --count;
            }
        }

        if (count > 0)
        {
            return AllocateBlockInternal(out blockId);
        }

        var pageIndex = blockId >> _pageShift;
        var offset = blockId % _entryCountPerPage;

        return (byte*)pages[pageIndex].Item1.ToPointer() + (Stride * offset);
    }

    protected Span<byte> GetBlockAsSpanInternal(int blockId) => new(GetBlockInternal(blockId), Stride);
    protected byte* GetBlockInternal(int blockId)
    {
        Debug.Assert(blockId >= 0, "Block id must be positive");
        var pageIndex = blockId >> _pageShift;
        var offset = blockId & (_entryCountPerPage - 1);

        return (byte*)_pages[pageIndex].Item1.ToPointer() + (Stride * offset);
    }

    protected void FreeBlockInternal(int blockId)
    {
        Debug.Assert(blockId >= 0, "Block id must be positive");
        _blockMap.ClearL0(blockId);
    }

    private void Resize(int length)
    {
        var curPages = _pages;
        var newPages = new (IntPtr, byte[])[length];
        new Span<(IntPtr, byte[])>(curPages).CopyTo(newPages);

        var size = Stride * _entryCountPerPage;
        for (int i = curPages.Length; i < length; i++)
        {
            var page = GC.AllocateUninitializedArray<byte>(size, true);
            newPages[i] = (Marshal.UnsafeAddrOfPinnedArrayElement(page, 0), page);
        }

        if (Interlocked.CompareExchange(ref _pages, newPages, curPages) != curPages)
        {
            if (_pages.Length < length)
            {
                Resize(length);
            }
        }
        else
        {
            _blockMap.Resize(_entryCountPerPage * length);
        }
    }

    public void Dispose()
    {
        if (_pages == null) return;

        var pages = _pages;
        if (Interlocked.CompareExchange(ref _pages, null, pages) == pages && pages != null)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Item2 = null;
            }
        }
    }
}

[PublicAPI]
public unsafe class BlockAllocator : BlockAllocatorBase
{
    public BlockAllocator(int stride, int entryCountPerPage) : base(stride, entryCountPerPage)
    {
    }

    public Span<byte> AllocateBlock(out int blockId) => new(AllocateBlockInternal(out blockId), Stride);
    public Span<byte> GetBlock(int blockId) => new(GetBlockInternal(blockId), Stride);
    public void FreeBlock(int blockId) => FreeBlockInternal(blockId);
}

[PublicAPI]
public class ChainedBlockAllocator<T> : ChainedBlockAllocatorBase where T : struct
{
    public ChainedBlockAllocator(int entryCountPerPage, int? strideOverride = null) : base(strideOverride ?? Unsafe.SizeOf<T>(), entryCountPerPage)
    {
        Debug.Assert(Stride >= Unsafe.SizeOf<T>(), "If you override the stride, it must be at least the size of the type you want to allocate");
    }
    
    public ref T Allocate(out int blockId)
    {
        ref var o = ref base.AllocateBlock(out blockId).Cast<byte, T>()[0];
        o = default;                                // Clear the content
        return ref o;
    }

    public ref T Get(int blockId) => ref base.GetBlockData(blockId).Cast<byte, T>()[0];
    public ref T Next(ref T blockData)
    {
        var asSpan = MemoryMarshal.CreateSpan(ref blockData, 1).Cast<T, byte>();
        var nextSpan = base.NextBlock(asSpan);
        if (nextSpan.IsEmpty)
        {
            return ref Unsafe.NullRef<T>();
        }
        return ref nextSpan.Cast<byte, T>()[0];
    }

    public new void Free(int blockId)
    {
        // EnumerateChainedBlock includes blockId as the first item, so we don't need to free it separately
        foreach (var curBlockId in base.EnumerateChainedBlock(blockId))
        {
            ref var block = ref Get(curBlockId);
            block = default;                        // Reset content to null to clear potential GC Objects that we no longer want
            base.FreeBlockInternal(curBlockId);
        }
    }

    public unsafe ref T SafeAppend(ref T block)
    {
        var headerPtr = (int*)Unsafe.AsPointer(ref block) - 1;
        ref var header = ref Unsafe.AsRef<int>(headerPtr);
        
        var span = AllocateBlockAsSpanInternal(out var newBlockId);
        span.Clear();
        
        var prevBlockId = Interlocked.CompareExchange(ref header, newBlockId, 0);
        
        // Another thread beat us, free the block we just allocated, get the Span of the one already there to replace ours
        if (prevBlockId != 0)
        {
            Free(newBlockId);
            span = GetBlockAsSpanInternal(prevBlockId);
        }

        // Skip header
        return ref span.Slice(4).Cast<byte, T>()[0];
    }
    
    [PublicAPI]
    public new readonly struct Enumerable
    {
        private readonly ChainedBlockAllocator<T> _owner;
        private readonly int _blockId;

        public Enumerable(ChainedBlockAllocator<T> owner, int blockId)
        {
            _owner = owner;
            _blockId = blockId;
        }
        
        public Enumerator GetEnumerator() => new(_owner, _blockId);
    }
    
    [PublicAPI]
    public new ref struct Enumerator
    {
        private readonly ChainedBlockAllocator<T> _owner;
        private int _currentBlockId;
        private int _nextBlockId;
        private int _blockSize;

        public Enumerator(ChainedBlockAllocator<T> owner, int blockId)
        {
            _owner = owner;
            _currentBlockId = 0;
            _nextBlockId = blockId;
            _blockSize = _owner.Stride;
        }

        public ref T Current => ref _owner.Get(_currentBlockId);

        public bool MoveNext()
        {
            if (_nextBlockId == 0)
            {
                return false;
            }
            
            _currentBlockId = _nextBlockId;
            _nextBlockId = _owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, int>().Slice(0, 1)[0];

            return true;
        }
    }
}

[PublicAPI]
public class ChainedBlockAllocator : ChainedBlockAllocatorBase
{
    public ChainedBlockAllocator(int stride, int entryCountPerPage) : base(stride, entryCountPerPage)
    {
    }
    public new Span<byte> AllocateBlock(out int blockId) => base.AllocateBlock(out blockId);
    public new Span<byte> GetBlockData(int blockId) => base.GetBlockData(blockId);
    public new Span<byte> NextBlock(Span<byte> blockData) => base.NextBlock(blockData);
    public new int Stride => base.Stride;
}

[PublicAPI]
public unsafe abstract class ChainedBlockAllocatorBase : BlockAllocatorBase
{
    /// <summary>
    /// Create a new instance
    /// </summary>
    /// <param name="stride">The size of each allocated block. An extra 4 bytes will be added to store the next block in the chain</param>
    /// <param name="entryCountPerPage">The count of block to allocate memory as one bulk (1 GC block allocated
    /// for <paramref name="entryCountPerPage"/> blocks).</param>
    protected ChainedBlockAllocatorBase(int stride, int entryCountPerPage) : base(stride+4, entryCountPerPage)
    {
        // Reserve block 0 as sentinel - 0 means "no next block" in the chain header
        AllocateBlockInternal(out _);
    }
    
    /// <summary>
    /// The block size, it excludes the 4 bytes used to store the next block in the chain.
    /// </summary>
    protected new int Stride => base.Stride - 4;

    /// <summary>
    /// Allocate a new block
    /// </summary>
    /// <param name="blockId">The id of the allocated block</param>
    /// <returns>Return span of the block data.</returns>
    protected Span<byte> AllocateBlock(out int blockId)
    {
        var span = AllocateBlockAsSpanInternal(out blockId);
        span.Cast<byte, int>()[0] = 0;
        return span.Slice(4);  // Skip the 4-byte chain header
    }

    /// <summary>
    /// Chain two blocks together.
    /// </summary>
    /// <param name="blockId">The id of the block to chain another next to it.</param>
    /// <param name="nextBlockId">The id of the block to chain after <paramref name="blockId"/>. Can be 0 to break the chain.</param>
    /// <remarks>If <paramref name="blockId"/> is already chained to a following block, this block will be added at the end of the chain
    /// of <paramref name="nextBlockId"/>.
    /// For instance if <paramref name="blockId"/> was: A with the chain [A, B, C] and <paramref name="nextBlockId"/> was D with the chain [D, E, F],
    /// the resulted chain will be [A, D, E, F, B, C]
    /// </remarks>
    public void Chain(int blockId, int nextBlockId)
    {
        var blockSpan = GetBlockAsSpanInternal(blockId);
        var chainHeader = blockSpan.Cast<byte, int>().Slice(0, 1);
        var prevBlockId = chainHeader[0];
        chainHeader[0] = nextBlockId;

        if (nextBlockId != 0)
        {
            var nextSpan = GetBlockAsSpanInternal(nextBlockId);
            var nextChainHeader = nextSpan.Cast<byte, int>().Slice(0, 1);
            while (nextChainHeader[0] != 0)
            {
                nextSpan = GetBlockAsSpanInternal(nextChainHeader[0]);
                nextChainHeader = nextSpan.Cast<byte, int>().Slice(0, 1);
            }
            nextChainHeader[0] = prevBlockId;
        }
    }

    /// <summary>
    /// Thread-safe append a new block after the given one
    /// </summary>
    /// <param name="blockId">The id of the block to append after</param>
    /// <param name="newBlockId">The id of the block that is after</param>
    /// <returns>The Span data of the block that is after</returns>
    /// <remarks>If there's already a block following, nothing will be changed. Another thread may have beaten us, we just do nothing and return its block.</remarks>
    public Span<byte> SafeAppend(int blockId, out int newBlockId)
    {
        ref var curHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, int>()[0];
        var span = AllocateBlockAsSpanInternal(out newBlockId);
        
        var prevBlockId = Interlocked.CompareExchange(ref curHeader, newBlockId, 0);
        
        // Another thread beat us, free the block we just allocated, get the Span of the one already there to replace ours
        if (prevBlockId != 0)
        {
            Free(newBlockId);
            newBlockId = prevBlockId;
            span = GetBlockAsSpanInternal(prevBlockId);
        }

        // Skip header
        return span.Slice(4);
    }

    public int RemoveNextBlock(int blockId)
    {
        if (blockId == 0)
        {
            return 0;
        }

        // Say A, B, C. blockId is A.

        // Get the next block (B)
        var blockSpan = GetBlockAsSpanInternal(blockId);
        var chainHeader = blockSpan.Cast<byte, int>().Slice(0, 1);

        // (B)
        var nextBlockId = chainHeader[0];

        // No B, nothing to do
        if (nextBlockId == 0)
        {
            return 0;
        }

        // Take B addr
        var nextBlockSpan = GetBlockAsSpanInternal(nextBlockId);
        var nextChainHeader = nextBlockSpan.Cast<byte, int>().Slice(0, 1);

        // Take C
        var afterNextId = nextChainHeader[0];

        // A.next = C (C can be 0)
        chainHeader[0] = afterNextId;

        // Make sure B.next = 0
        nextChainHeader[0] = 0;

        return nextBlockId;
    }
    
    /// <summary>
    /// Get the address of a block's data
    /// </summary>
    /// <param name="blockId"></param>
    /// <returns>The address of the block data, excluding the chain header.</returns>
    protected Span<byte> GetBlockData(int blockId)
    {
        if (blockId == 0)
        {
            return null;
        }
        return GetBlockAsSpanInternal(blockId).Slice(4);
    }

    /// <summary>
    /// Get the address of the next block in the chain, or null if the block is not chained.
    /// </summary>
    /// <param name="blockData">The span of the block data to get the next block from.</param>
    /// <returns>The data span of the next block in the chain, or <c>Span&lt;byte&gt;.Empty</c> if the block is not chained.</returns>
    protected Span<byte> NextBlock(Span<byte> blockData)
    {
        fixed (byte* addr = blockData)
        {
            var nextBlockId = *((int*)(addr - 4));
            if (nextBlockId == 0)
            {
                return Span<byte>.Empty;
            }

            return GetBlockData(nextBlockId);
        }
    }

    /// <summary>
    /// Free a block and all the blocks chained after it.
    /// </summary>
    /// <param name="blockId">The id of the block to start the free operation from.</param>
    public void Free(int blockId)
    {
        // EnumerateChainedBlock includes blockId as the first item, so we don't need to free it separately
        foreach (var curBlockId in EnumerateChainedBlock(blockId))
        {
            base.FreeBlockInternal(curBlockId);
        }
    }

    public Enumerable EnumerateChainedBlock(int rootBlockId) => new(this, rootBlockId);

    internal int GetNextInChain(Span<byte> blockData)
    {
        fixed (byte* addr = blockData)
        {
            return *((int*)(addr - 4));
        }
    }
    
    internal IntPtr AsIntPtr(int blockId) => (IntPtr)GetBlockInternal(blockId);

    [PublicAPI]
    public readonly struct Enumerable
    {
        private readonly ChainedBlockAllocatorBase _owner;
        private readonly int _blockId;

        public Enumerable(ChainedBlockAllocatorBase owner, int blockId)
        {
            _owner = owner;
            _blockId = blockId;
        }
        
        public Enumerator GetEnumerator() => new(_owner, _blockId);
    }
    
    [PublicAPI]
    public ref struct Enumerator
    {
        private readonly ChainedBlockAllocatorBase _owner;
        private int _nextBlockId;
        private int _blockSize;

        public Enumerator(ChainedBlockAllocatorBase owner, int blockId)
        {
            _owner = owner;
            Current = 0;
            _nextBlockId = blockId;
            _blockSize = _owner.Stride;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            if (_nextBlockId == 0)
            {
                return false;
            }
            
            Current = _nextBlockId;
            _nextBlockId = _owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, int>().Slice(0, 1)[0];

            return true;
        }
    }
}

internal readonly unsafe struct StoreSpan
{
    public StoreSpan(Span<byte> span)
    {
        fixed (byte* ptr = span)
        {
            _address = ptr;
            _length = span.Length;
        }
    }
    
    public Span<T> ToSpan<T>() where T : unmanaged => new(_address, _length / sizeof(T));
    
    public static explicit operator StoreSpan(Span<byte> span) => new(span);
    public static explicit operator Span<byte>(StoreSpan span) => new(span._address, span._length);
    
    private readonly void* _address;
    private readonly int _length;
}

internal static class StoreSpanExtensions
{
    public static StoreSpan ToStoreSpan<T>(this Span<T> span) where T : unmanaged => new(span.Cast<T, byte>());
}

[PublicAPI]
public unsafe class UnmanagedStructAllocator<T> : BlockAllocatorBase where T : unmanaged
{
    public ref T Allocate(out int blockId) => ref Unsafe.AsRef<T>(AllocateBlockInternal(out blockId));
    public ref T Get(int blockId) => ref Unsafe.AsRef<T>(GetBlockInternal(blockId));
    public void Free(int blockId) => FreeBlockInternal(blockId);

    public UnmanagedStructAllocator(int entryCountPerPage) : base(sizeof(T), entryCountPerPage)
    {
    }
}

[PublicAPI]
public interface ICleanable
{
    void Cleanup();
}

public unsafe class StructAllocator<T> : BlockAllocatorBase where T : struct, ICleanable
{
    public ref T Allocate(out int blockId) => ref Unsafe.AsRef<T>(AllocateBlockInternal(out blockId));
    public ref T Get(int blockId) => ref Unsafe.AsRef<T>(GetBlockInternal(blockId));
    public void Free(int blockId)
    {
        var addr = GetBlockInternal(blockId);
        Unsafe.AsRef<T>(addr).Cleanup();

        FreeBlockInternal(blockId);
    }

    public StructAllocator(int entryCountPerPage) : base(Unsafe.SizeOf<T>(), entryCountPerPage)
    {
    }
}
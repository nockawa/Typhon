// unset

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

public unsafe abstract class BlockAllocatorBase : IDisposable
{
    private ValueTuple<IntPtr, byte[]>[] _pages;
    private readonly ConcurrentBitmapL3All _blockMap;
    private readonly int _stride;
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

        _stride = stride;
        _entryCountPerPage = entryCountPerPage;
        _pageShift = BitOperations.Log2((uint)entryCountPerPage);
        _pages = new (IntPtr, byte[])[1];
        _pages[0] = (Marshal.UnsafeAddrOfPinnedArrayElement(page, 0), page);

        _blockMap = new ConcurrentBitmapL3All(entryCountPerPage);
    }

    public int Capacity => _blockMap.Capacity;
    public int AllocatedCount => _blockMap.TotalBitSet;

    protected byte* AllocateBlock(out int blockId)
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
            return AllocateBlock(out blockId);
        }

        var pageIndex = blockId >> _pageShift;
        var offset = blockId % _entryCountPerPage;

        return (byte*)pages[pageIndex].Item1.ToPointer() + (_stride * offset);
    }

    protected byte* GetBlockAddress(int blockId)
    {
        var pageIndex = blockId >> _pageShift;
        var offset = blockId & (_entryCountPerPage - 1);

        return (byte*)_pages[pageIndex].Item1.ToPointer() + (_stride * offset);
    }

    protected void FreeBlock(int blockId) => _blockMap.ClearL0(blockId);

    private void Resize(int length)
    {
        var curPages = _pages;
        var newPages = new (IntPtr, byte[])[length];
        new Span<(IntPtr, byte[])>(curPages).CopyTo(newPages);

        var size = _stride * _entryCountPerPage;
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

public unsafe class BlockAllocator : BlockAllocatorBase
{
    public BlockAllocator(int stride, int entryCountPerPage) : base(stride, entryCountPerPage)
    {
    }

    public byte* Allocate(out int blockId) => AllocateBlock(out blockId);
    public byte* GetAddress(int blockId) => GetBlockAddress(blockId);
    public void Free(int blockId) => FreeBlock(blockId);
}

public unsafe class UnmanagedStructAllocator<T> : BlockAllocatorBase where T : unmanaged
{
    public ref T Allocate(out int blockId) => ref Unsafe.AsRef<T>(AllocateBlock(out blockId));
    public ref T Get(int blockId) => ref Unsafe.AsRef<T>(GetBlockAddress(blockId));
    public void Free(int blockId) => FreeBlock(blockId);

    public UnmanagedStructAllocator(int entryCountPerPage) : base(sizeof(T), entryCountPerPage)
    {
    }
}

public interface ICleanable
{
    void Cleanup();
}

public unsafe class StructAllocator<T> : BlockAllocatorBase where T : struct, ICleanable
{
    public ref T Allocate(out int blockId) => ref Unsafe.AsRef<T>(AllocateBlock(out blockId));
    public ref T Get(int blockId) => ref Unsafe.AsRef<T>(GetBlockAddress(blockId));
    public void Free(int blockId)
    {
        var addr = GetBlockAddress(blockId);
        Unsafe.AsRef<T>(addr).Cleanup();

        FreeBlock(blockId);
    }

    public StructAllocator(int entryCountPerPage) : base(Unsafe.SizeOf<T>(), entryCountPerPage)
    {
    }
}
using JetBrains.Annotations;
using System;
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
    private Lock _lock;

    /// <summary>
    /// Creates a block allocator with the specified stride and page capacity.
    /// </summary>
    /// <param name="stride">Size of each block in bytes.</param>
    /// <param name="entryCountPerPage">Number of entries per page (must be power of 2).</param>
    /// <param name="parent">Parent resource for the internal bitmap (defaults to Allocation subsystem).</param>
    protected BlockAllocatorBase(int stride, int entryCountPerPage, IResource parent = null)
    {
        if (MathHelpers.IsPow2(entryCountPerPage) == false)
        {
            throw new ArgumentException($"Entry count per page must be a power of 2 but {entryCountPerPage} was given", nameof(entryCountPerPage));
        }

        // Default to Allocation subsystem if no parent specified
        parent ??= TyphonServices.ResourceRegistry.Allocation;

        var size = stride * entryCountPerPage;
        var page = GC.AllocateUninitializedArray<byte>(size, true);

        Stride = stride;
        _entryCountPerPage = entryCountPerPage;
        _pageShift = BitOperations.Log2((uint)entryCountPerPage);
        _pages = new (IntPtr, byte[])[1];
        _pages[0] = (Marshal.UnsafeAddrOfPinnedArrayElement(page, 0), page);

        _blockMap = new ConcurrentBitmapL3All($"{GetType().Name}BlockMap", parent, entryCountPerPage);
        _lock = new Lock();
    }

    public int Capacity => _blockMap.Capacity;
    public int AllocatedCount => _blockMap.TotalBitSet;
    protected readonly int Stride;

    protected Span<byte> AllocateBlockAsSpanInternal(out int blockId) => new(AllocateBlockInternal(out blockId), Stride);

    protected byte* AllocateBlockInternal(out int blockId)
    {
        while (true)
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
            var count = 1;
            while ((count > 0) && map.FindNextUnsetL0(ref blockId))
            {
                if (map.SetL0(blockId))
                {
                    --count;
                }
            }

            if (count > 0)
            {
                continue;
            }

            var pageIndex = blockId >> _pageShift;
            if (pageIndex >= pages.Length)
            {
                // Another thread resized the bitmap but we have a stale pages reference
                map.ClearL0(blockId);
                continue;
            }
            var offset = blockId % _entryCountPerPage;

            return (byte*)pages[pageIndex].Item1.ToPointer() + (Stride * offset);
        }
    }

    protected Span<byte> GetBlockAsSpanInternal(int blockId) => new(GetBlockInternal(blockId), Stride);
    protected ref T GetBlockAs<T>(int blockId)
    {
        Debug.Assert(blockId >= 0, "Block id must be positive");
        return ref Unsafe.AsRef<T>(GetBlockInternal(blockId));
    }

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
        lock (_lock)
        {
            // Step 1: Resize bitmap FIRST (only grows, never shrinks)
            // This ensures bitmap capacity is always >= what we need before pages are extended
            while (true)
            {
                var newCapacity = _entryCountPerPage * length;
                if (_blockMap.Capacity >= newCapacity)
                {
                    break;
                }
                _blockMap.Grow();
            }

            // Step 2: Resize pages with CAS loop
            var curPages = _pages;
            if (curPages.Length >= length)
            {
                return;  // Another thread already resized big enough
            }

            var newPages = new (IntPtr, byte[])[length];
            new Span<(IntPtr, byte[])>(curPages).CopyTo(newPages);

            var size = Stride * _entryCountPerPage;
            for (int i = curPages.Length; i < length; i++)
            {
                var page = GC.AllocateUninitializedArray<byte>(size, true);
                newPages[i] = (Marshal.UnsafeAddrOfPinnedArrayElement(page, 0), page);
            }

            _pages = newPages;
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
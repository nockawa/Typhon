// unset

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine
{
    public unsafe class BlockAllocator
    {
        private ValueTuple<IntPtr, byte[]>[] _pages;
        private readonly ConcurrentBitmapL3All _blockMap;
        private readonly int _stride;
        private readonly int _entryCountPerPage;
        private readonly int _pageShift;

        public BlockAllocator(int stride, int entryCountPerPage)
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

        public byte* AllocateBlock(out int blockId)
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
            while (map.FindNextUnsetL0(ref blockId, ref mask) && (count > 0))
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
            var offset = blockId & (_entryCountPerPage - 1);

            return (byte*)pages[pageIndex].Item1.ToPointer() + (_stride * offset);
        }

        public void ReleaseBlock(int blockId) => _blockMap.ClearL0(blockId);

        private void Resize(int length)
        {
            var curPages = _pages;
            var newPages = new (IntPtr, byte[])[length];
            new Span<(IntPtr, byte[])>(curPages).CopyTo(newPages);

            if (Interlocked.CompareExchange(ref _pages, newPages, curPages) != curPages)
            {
                if (_pages.Length < length)
                {
                    Resize(length);
                }
            }
        }
    }
}
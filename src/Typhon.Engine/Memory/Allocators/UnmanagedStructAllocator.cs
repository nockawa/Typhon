using JetBrains.Annotations;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

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
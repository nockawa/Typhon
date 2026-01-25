using JetBrains.Annotations;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

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


[PublicAPI]
public interface ICleanable
{
    void Cleanup();
}
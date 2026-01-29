using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[PublicAPI]
public unsafe class PinnedMemoryBlock : MemoryBlockBase
{
    private byte[] _array;
    public int Alignment { get; }
    public byte* DataAsPointer { get; private set; }
    public IntPtr DataAsIntPtr => (IntPtr)DataAsPointer;

    public override int Size { get; }
    public override bool IsDisposed => _array == null;
    public override Span<byte> DataAsSpan => new(DataAsPointer, Size);
    public new void Dispose()
    {
        base.Dispose();
        _array = null;
        DataAsPointer = null;
    }

    public override IEnumerable<IResource> Children { get; }

    public PinnedMemoryBlock(MemoryAllocator allocator, byte[] block, int size, int alignment, string resourceId, IResource parent) : 
        base(allocator, resourceId, parent)
    {
        _array = block;
        Alignment = alignment;
        var baseAddr = Marshal.UnsafeAddrOfPinnedArrayElement(block, 0);
        var mask = alignment - 1;
        DataAsPointer = (byte*)((baseAddr.ToInt64() + mask) & ~mask);
        Size = size;
    }
}
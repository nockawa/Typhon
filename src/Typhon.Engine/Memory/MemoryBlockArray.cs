using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

[PublicAPI]
public class MemoryBlockArray : MemoryBlockBase
{
    public byte[] DataAsArray { get; private set; }
    public MemoryBlockArray(MemoryAllocator allocator, byte[] block, string resourceId, IResource parent) : 
        base(allocator, resourceId ?? Guid.NewGuid().ToString(), parent)
    {
        DataAsArray = block;
    }

    public override int Size => DataAsArray.Length;
    public override bool IsDisposed => DataAsArray == null;
    public override Span<byte> DataAsSpan => DataAsArray.AsSpan();
    public new void Dispose()
    {
        base.Dispose();
        DataAsArray = null;
    }

    public override IEnumerable<IResource> Children { get; }
}
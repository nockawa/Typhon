// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

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
using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public unsafe abstract class ChainedBlockAllocatorBase : BlockAllocatorBase
{
    protected static readonly int BlockHeaderSize = sizeof(BlockHeader);
    private static int NextChainGeneration;

    protected struct BlockHeader
    {
        // If the bit is set, there's a pending free request, which must prevent further usage
        const uint FreeRequested    = 0x8000_0000U;

        // 15 bits that store the chain traversal in progress
        const uint UsageCounterMask = 0x7FFF_0000U;
        const int UsageCounterShift = 16;

        // 16 bits that store:
        // - 0xFFFF: block is free
        // - 0x0000: block is allocated but not linked to a chain
        // - 0x0001 - 0xFFFE: generation of the chain the block is linked to
        const uint ChainGenerationMask   = 0x0000_FFFFU;
        
        public bool IsFreeRequested
        {
            get => (_data & FreeRequested) != 0;
            set
            {
                SpinWait? sw = null;
                while (true)
                {
                    var curData = _data;
                    var newData = curData & ~FreeRequested | (value ? FreeRequested : 0);

                    if (Interlocked.CompareExchange(ref _data, newData, curData) == curData)
                    {
                        break;
                    }
                    sw ??= new SpinWait();
                    sw.Value.SpinOnce();
                }
            }
        }

        public int UsageCounter => (int)((_data&UsageCounterMask) >> UsageCounterShift);

        public int ChainGeneration
        {
            get => (int)(_data & ChainGenerationMask);
            set => _data = (_data & ~ChainGenerationMask) | (uint)value;
        }

        public void ResetBlock()
        {
            _data = 0;
            NextBlockId = 0;
        }

        private uint _data;
        public int NextBlockId;

        public bool RequestEnumeration(out int chainGeneration)
        {
            SpinWait? sw = null;
            while (true)
            {
                var curData = _data;
                if ((curData & FreeRequested) != 0)
                {
                    chainGeneration = 0;
                    return false;
                }

                // Increment usage counter
                var newData = curData + (1<<UsageCounterShift);

                var prevData = Interlocked.CompareExchange(ref _data, newData, curData);
                if (prevData == curData)
                {
                    chainGeneration = (int)(newData & ChainGenerationMask);
                    return true;
                }
                if ((prevData & FreeRequested) != 0)
                {
                    chainGeneration = 0;
                    return false;
                }

                sw ??= new SpinWait();
                sw.Value.SpinOnce();
            }
        }

        public void EndEnumeration(int chainGeneration)
        {
            while (true)
            {
                var curData = _data;
                if (chainGeneration != (curData & ChainGenerationMask))
                {
                    return;
                }
                var newData = curData - (1<<UsageCounterShift);
                var prevData = Interlocked.CompareExchange(ref _data, newData, curData);
                if (prevData == curData)
                {
                    return;
                }
            }
        }

        public void MarkFree()
        {
            NextBlockId = 0;
            _data = 0xFFFFFFFF;
        }
    }

    /// <summary>
    /// Create a new instance
    /// </summary>
    /// <param name="stride">The size of each allocated block. An extra 8 bytes will be added to store the block header</param>
    /// <param name="entryCountPerPage">The count of block to allocate memory as one bulk (1 GC block allocated
    /// for <paramref name="entryCountPerPage"/> blocks).</param>
    protected ChainedBlockAllocatorBase(int stride, int entryCountPerPage) : base(stride+BlockHeaderSize, entryCountPerPage)
    {
        // Reserve block 0 as sentinel - 0 means "no next block" in the chain header
        AllocateBlockInternal(out _);
    }

    /// <summary>
    /// The block size, it excludes the 8 bytes used for the block header.
    /// </summary>
    protected new int Stride => base.Stride - BlockHeaderSize;

    /// <summary>
    /// Allocate a new block
    /// </summary>
    /// <param name="blockId">The id of the allocated block</param>
    /// <param name="chainRoot"><c>true</c> if the block should be the root of a new chain, <c>false</c> otherwise.</param>
    /// <returns>Return span of the block data.</returns>
    protected Span<byte> AllocateBlock(out int blockId, bool chainRoot)
    {
        var span = AllocateBlockAsSpanInternal(out blockId);
        ref var header = ref span.Cast<byte, BlockHeader>()[0];
        header.ResetBlock();
        if (chainRoot)
        {
            header.ChainGeneration = Interlocked.Increment(ref NextChainGeneration);
        }

        return span.Slice(BlockHeaderSize);  // Skip the 8-byte chain header
    }

    /// <summary>
    /// Chain two blocks together.
    /// </summary>
    /// <param name="blockId">The id of the block to chain another next to it.</param>
    /// <param name="nextBlockId">The id of the block to chain after <paramref name="blockId"/>. Can be 0 to break the chain.</param>
    /// <returns>The id of the block that was previously chained after <paramref name="blockId"/>. If <paramref name="nextBlockId"/> was 0, every block
    /// following <paramref name="blockId"/> were detach and are now orphans with the chain generation of 0. It is the responsibility of the caller to free
    /// these blocks or to chain them somewhere else.</returns>
    /// <remarks>If <paramref name="blockId"/> is already chained to a following block, this block will be added at the end of the chain
    /// of <paramref name="nextBlockId"/>.
    /// For instance if <paramref name="blockId"/> was: A with the chain [A, B, C] and <paramref name="nextBlockId"/> was D with the chain [D, E, F],
    /// the resulted chain will be [A, D, E, F, B, C]
    /// The chained blocks will use the same chain generation than the one they are chained to.
    /// </remarks>
    public int Chain(int blockId, int nextBlockId)
    {
        while (true)
        {
            ref var rootHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
            var oldNextBlockId = rootHeader.NextBlockId;

            // Safely detach what's following blockId, temporarily ending the chain at blockId
            while (true)
            {
                if (Interlocked.CompareExchange(ref rootHeader.NextBlockId, 0, oldNextBlockId) == oldNextBlockId)
                {
                    break;
                }

                oldNextBlockId = rootHeader.NextBlockId;
            }

            var chainGen = rootHeader.ChainGeneration;

            // Find the end of the right chain to link the old next of the left chain. E.g.: goes, D, E, F then link B to F.
            // We replace the chain generation of the right chain to match the one of the left chain.
            if (nextBlockId != 0)
            {
                ref var nextChainHeader = ref GetBlockAsSpanInternal(nextBlockId).Cast<byte, BlockHeader>()[0];
                nextChainHeader.ChainGeneration = chainGen;
                while (nextChainHeader.NextBlockId != 0)
                {
                    nextChainHeader = ref GetBlockAsSpanInternal(nextChainHeader.NextBlockId).Cast<byte, BlockHeader>()[0];
                    nextChainHeader.ChainGeneration = chainGen;
                }

                nextChainHeader.NextBlockId = oldNextBlockId;
            }

            // Detach the blocks after, we need to reset the generation to make them orphans
            else
            {
                ref var nextChainHeader = ref GetBlockAsSpanInternal(oldNextBlockId).Cast<byte, BlockHeader>()[0];
                while (nextChainHeader.NextBlockId != 0)
                {
                    nextChainHeader.ChainGeneration = 0;
                    nextChainHeader = ref GetBlockAsSpanInternal(nextChainHeader.NextBlockId).Cast<byte, BlockHeader>()[0];
                }
            }

            // We finally link the right chain to the left one, there should be a 0 replaced, if there's something else, a concurrent Chain operation was made
            // living us no choice to try again the whole operation.
            var newNextBlockId = Interlocked.CompareExchange(ref rootHeader.NextBlockId, nextBlockId, 0);
            if (newNextBlockId != 0)
            {
                continue;
            }

            return oldNextBlockId;
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
        ref var rootHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
        var span = AllocateBlock(out newBlockId, false);

        var prevBlockId = Interlocked.CompareExchange(ref rootHeader.NextBlockId, newBlockId, 0);

        // Another thread beat us, free the block we just allocated, get the Span of the one already there to replace ours
        if (prevBlockId != 0)
        {
            Free(newBlockId);
            newBlockId = prevBlockId;
            span = GetBlockAsSpanInternal(prevBlockId);
        }

        // Skip header
        return span.Slice(BlockHeaderSize);
    }

    public int RemoveNextBlock(int blockId)
    {
        if (blockId == 0)
        {
            return 0;
        }

        // Say A, B, C. blockId is A.

        // Get the next block (B)
        ref var rootHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];

        // (B)
        var nextBlockId = rootHeader.NextBlockId;

        // No B, nothing to do
        if (nextBlockId == 0)
        {
            return 0;
        }

        // Take B addr
        ref var nextHeader = ref GetBlockAsSpanInternal(nextBlockId).Cast<byte, BlockHeader>()[0];

        // Take C
        var afterNextId = nextHeader.NextBlockId;

        // A.next = C (C can be 0)
        rootHeader.NextBlockId = afterNextId;

        // Make sure B.next = 0
        nextHeader.NextBlockId = 0;

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
        return GetBlockAsSpanInternal(blockId).Slice(BlockHeaderSize);
    }

    protected Span<byte> NextBlock(Span<byte> blockDataSpan)
    {
        var nextBlockId = GetNextBlockInternal(blockDataSpan);
        return nextBlockId == 0 ? Span<byte>.Empty : GetBlockData(nextBlockId);
    }

    protected int NextBlock(int blockId) => GetNextBlockInternal(GetBlockData(blockId));

    private int GetNextBlockInternal(Span<byte> blockDataSpan)
    {
        fixed (byte* blockPtr = blockDataSpan)
        {
            var blockHeader = (BlockHeader*)blockPtr - 1;
            if (blockHeader->NextBlockId == 0)
            {
                return 0;
            }

            var nextChainGeneration = GetBlockAsSpanInternal(blockHeader->NextBlockId).Cast<byte, BlockHeader>()[0].ChainGeneration;
            if (blockHeader->ChainGeneration != nextChainGeneration)
            {
                blockHeader->NextBlockId = 0;
                return 0;
            }

            return blockHeader->NextBlockId;
        }
    }

    /// <summary>
    /// Free a block and all the blocks chained after it.
    /// </summary>
    /// <param name="blockId">The id of the block to start the free operation from.</param>
    public void Free(int blockId)
    {
        if (blockId == 0)
        {
            return;
        }

        var curBlockId = blockId;
        var curSpan = GetBlockAsSpanInternal(blockId);
        ref var curHeader = ref curSpan.Cast<byte, BlockHeader>()[0];

        // Signal we want to free the block
        curHeader.IsFreeRequested = true;

        // Wait until all usages are done
        var sw = new SpinWait();
        while (curHeader.UsageCounter > 0)
        {
            sw.SpinOnce();
        }

        while (true)
        {
            var nextBlockId = NextBlock(curBlockId);

            // We are ready to free the block
            curHeader.MarkFree();
            base.FreeBlockInternal(curBlockId);

            if (nextBlockId == 0)
            {
                break;
            }

            curBlockId = nextBlockId;
            curHeader = ref GetBlockAsSpanInternal(curBlockId).Cast<byte, BlockHeader>()[0];
        }
    }

    public bool RequestEnumeration(int blockId, out int chainGeneration) => GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0].RequestEnumeration(out chainGeneration);
    public void EndEnumeration(int blockId, int chainGeneration) => GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0].EndEnumeration(chainGeneration);

    public Enumerable EnumerateChainedBlock(int rootBlockId) => new(this, rootBlockId);

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
    public ref struct Enumerator : IDisposable
    {
        private readonly ChainedBlockAllocatorBase _owner;
        private int _nextBlockId;
        private ref BlockHeader _rootHeader;
        private int _chainGeneration;

        public Enumerator(ChainedBlockAllocatorBase owner, int blockId)
        {
            Current = 0;
            _nextBlockId = blockId;
            _rootHeader = ref owner.GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
            _owner = _rootHeader.RequestEnumeration(out _chainGeneration) ? owner : null;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            if (_owner == null || _nextBlockId == 0)
            {
                return false;
            }

            Current = _nextBlockId;

            _nextBlockId =  _owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, BlockHeader>()[0].NextBlockId;
            if (_owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, BlockHeader>()[0].ChainGeneration != _chainGeneration)
            {
                _nextBlockId = 0;
            }

            return true;
        }

        public void Dispose()
        {
            if (_owner != null)
            {
                _rootHeader.EndEnumeration(_chainGeneration);
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Allocator for deep-mode operation logs.
/// Each resource in deep mode owns a chain of blocks in this allocator.
/// Chain grows unbounded until resource disables deep mode and frees the chain.
/// </summary>
[ExcludeFromCodeCoverage]
public class ResourceTelemetryAllocator : IDisposable
{
    private readonly ChainedBlockAllocator<ResourceOperationBlock> _allocator;

    /// <summary>
    /// Creates a new instance of the telemetry allocator.
    /// </summary>
    /// <param name="parent">Parent resource for resource tree registration.</param>
    /// <param name="memoryAllocator">Memory allocator for internal storage.</param>
    public ResourceTelemetryAllocator(IResource parent, IMemoryAllocator memoryAllocator)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(memoryAllocator);

        // 64K blocks, each block can hold 6 entries
        // 6 entries x 16 bytes = 96 bytes per block (plus header)
        _allocator = new ChainedBlockAllocator<ResourceOperationBlock>(65536, parent, memoryAllocator);
    }

    /// <summary>
    /// Allocates a new chain for a resource entering deep mode.
    /// </summary>
    /// <param name="blockId">The allocated block ID (chain root).</param>
    /// <returns>True if allocation succeeded.</returns>
    public bool AllocateChain(out int blockId)
    {
        _allocator.Allocate(out blockId, rootChain: true);
        return blockId != 0;
    }

    /// <summary>
    /// Frees an entire chain when a resource exits deep mode.
    /// </summary>
    public void FreeChain(int blockId)
        => _allocator.FreeChain(blockId);

    /// <summary>
    /// Gets a reference to a specific block.
    /// </summary>
    internal ref ResourceOperationBlock GetBlock(int blockId)
        => ref _allocator.Get(blockId);

    /// <summary>
    /// Appends an operation entry to the chain, allocating new blocks as needed.
    /// </summary>
    /// <param name="blockId">Root block ID of the chain. May be updated if chain needs to be allocated.</param>
    /// <param name="entry">The operation entry to append.</param>
    public void AppendOperation(ref int blockId, in ResourceOperationEntry entry)
    {
        if (blockId == 0)
        {
            if (!AllocateChain(out blockId))
                return; // Allocator exhausted
        }

        var lastBlockId = _allocator.GetLastBlockInChain(blockId);
        ref var block = ref _allocator.Get(lastBlockId);

        // Find first empty slot in current block
        for (int i = 0; i < ResourceOperationBlock.Count; i++)
        {
            // Try to reserve the entry with CAS on the first int (LockOperation byte will be non-zero)
            ref var intPtr = ref Unsafe.As<ResourceOperationEntry, int>(ref block[i]);
            if (Interlocked.CompareExchange(ref intPtr, 1, 0) == 0)
            {
                // Successfully reserved, copy the full entry
                block[i] = entry;
                return;
            }
        }

        // Block is full, allocate a new one and append there
        ref var newBlock = ref _allocator.SafeAppend(ref block);
        if (!Unsafe.IsNullRef(ref newBlock))
        {
            newBlock[0] = entry;
        }
    }

    /// <summary>
    /// Enumerates all operation entries in a chain.
    /// Note: This is not thread-safe for concurrent writes.
    /// </summary>
    public List<ResourceOperationEntry> GetChainEntries(int blockId)
    {
        var result = new List<ResourceOperationEntry>();
        if (blockId == 0)
            return result;

        foreach (var chainBlockId in _allocator.EnumerateChainedBlock(blockId))
        {
            ref var block = ref _allocator.Get(chainBlockId);
            for (int i = 0; i < ResourceOperationBlock.Count; i++)
            {
                if (block[i].IsEmpty)
                    return result;
                result.Add(block[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the number of allocated blocks (for diagnostics).
    /// </summary>
    public int AllocatedCount => _allocator.AllocatedCount;

    public void Dispose() => _allocator.Dispose();
}

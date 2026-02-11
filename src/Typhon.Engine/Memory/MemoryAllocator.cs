using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine;

[PublicAPI]
public interface IMemoryAllocator
{
    MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false);
    PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0);
}

[PublicAPI]
public class MemoryAllocatorOptions
{
    public string Name { get; set; } = "Default Memory Allocator";
}

[PublicAPI]
public class MemoryAllocator : ResourceNode, IMemoryAllocator, IMetricSource, IDebugPropertiesProvider
{
    private ConcurrentCollection<MemoryBlockBase> _blocks;

    // Allocation tracking
    private long _totalAllocatedBytes;
    private long _peakAllocatedBytes;
    private long _cumulativeAllocations;
    private long _cumulativeDeallocations;

    public MemoryAllocator(IResourceRegistry resourceRegistry, MemoryAllocatorOptions options) :
        base(options?.Name ?? "Default Memory Allocator", ResourceType.Service, resourceRegistry.Allocation)
    {
        _blocks = new ConcurrentCollection<MemoryBlockBase>();
    }

    public MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
        }
        var block = zeroed ? GC.AllocateArray<byte>(size) : GC.AllocateUninitializedArray<byte>(size);

        var mb = new MemoryBlockArray(this, block, id, parent);
        _blocks.Add(mb);

        // Update allocation tracking
        var newTotal = Interlocked.Add(ref _totalAllocatedBytes, size);
        if (newTotal > _peakAllocatedBytes)
        {
            _peakAllocatedBytes = newTotal;
        }
        Interlocked.Increment(ref _cumulativeAllocations);

        return mb;
    }
    
    public PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
        }

        if (alignment < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment cannot be negative");
        }

        if (!BitOperations.IsPow2(alignment))
        {
            throw new ArgumentException("Alignment must be a power of 2", nameof(alignment));
        }

        var mb = new PinnedMemoryBlock(this, size, alignment, id, parent);
        if (zeroed)
        {
            mb.DataAsSpan.Clear();
        }
        _blocks.Add(mb);

        // Update allocation tracking
        var newTotal = Interlocked.Add(ref _totalAllocatedBytes, size);
        if (newTotal > _peakAllocatedBytes)
        {
            _peakAllocatedBytes = newTotal;
        }
        Interlocked.Increment(ref _cumulativeAllocations);

        return mb;
    }
    
    internal void Remove(MemoryBlockBase block)
    {
        Interlocked.Add(ref _totalAllocatedBytes, -block.MemoryBlockSize);
        Interlocked.Increment(ref _cumulativeDeallocations);
        _blocks.Remove(block);
    }

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Memory: total bytes across all blocks
        writer.WriteMemory(_totalAllocatedBytes, _peakAllocatedBytes);

        // Capacity: active block count (no hard limit)
        long blockCount = _blocks.Count;
        writer.WriteCapacity(blockCount, long.MaxValue);

        // Throughput: allocation lifecycle
        writer.WriteThroughput("Allocations", _cumulativeAllocations);
        writer.WriteThroughput("Deallocations", _cumulativeDeallocations);
    }

    /// <inheritdoc />
    public void ResetPeaks() => _peakAllocatedBytes = _totalAllocatedBytes;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var blocks = _blocks.ToArray(); // Snapshot for consistency

        long arrayBlocks = 0, pinnedBlocks = 0;
        long arrayBytes = 0, pinnedBytes = 0;

        foreach (var block in blocks)
        {
            if (block is MemoryBlockArray)
            {
                arrayBlocks++;
                arrayBytes += block.EstimatedMemorySize;
            }
            else if (block is PinnedMemoryBlock)
            {
                pinnedBlocks++;
                pinnedBytes += block.EstimatedMemorySize;
            }
        }

        return new Dictionary<string, object>
        {
            // Overall stats
            ["Blocks.Total"] = blocks.Length,
            ["Bytes.Total"] = _totalAllocatedBytes,
            ["Bytes.Peak"] = _peakAllocatedBytes,

            // By type breakdown
            ["ArrayBlocks.Count"] = arrayBlocks,
            ["ArrayBlocks.Bytes"] = arrayBytes,
            ["PinnedBlocks.Count"] = pinnedBlocks,
            ["PinnedBlocks.Bytes"] = pinnedBytes,

            // Lifecycle counters
            ["Cumulative.Allocations"] = _cumulativeAllocations,
            ["Cumulative.Deallocations"] = _cumulativeDeallocations,
        };
    }
}
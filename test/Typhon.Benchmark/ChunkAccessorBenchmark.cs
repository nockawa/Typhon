using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine;

namespace Typhon.Benchmark;

/// <summary>
/// Test data structure representing a typical chunk payload (64 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TestChunkData
{
    public long Id;
    public long ParentId;
    public long NextId;
    public long PrevId;
    public int Counter;
    public int Flags;
    public float Value1;
    public float Value2;
    public double Timestamp;
    public long Reserved;
}

/// <summary>
/// Benchmark comparing ChunkRandomAccessor vs StackChunkAccessor performance
/// across typical chunk manipulation scenarios.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class ChunkAccessorBenchmark
{
    private const int ChunkCount = 10_000;
    private const int RandomAccessCount = 50_000;

    private string CurrentDatabaseName => "ChunkAccessorBenchmark_database";
    private ServiceProvider _serviceProvider;
    private ManagedPagedMMF _pmmf;
    private ChunkBasedSegment _segment;
    private int[] _allocatedChunks;
    private int[] _randomAccessPattern;
    private Random _random;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Configure large enough cache to avoid disk I/O dominating
        var dcs = 100 * 1024 * PagedMMF.PageSize;

        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole();
                builder.SetMinimumLevel(LogLevel.Critical);
            })
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        // Allocate segment with enough pages for our chunks
        var pagesNeeded = (ChunkCount * Unsafe.SizeOf<TestChunkData>() / PagedMMF.PageRawDataSize) + 50;
        _segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, pagesNeeded, Unsafe.SizeOf<TestChunkData>());

        // Pre-allocate all chunks and initialize with data
        _allocatedChunks = new int[ChunkCount];
        using var initAccessor = _segment.CreateChunkRandomAccessor(8);
        for (int i = 0; i < ChunkCount; i++)
        {
            _allocatedChunks[i] = _segment.AllocateChunk(false);
            ref var chunk = ref initAccessor.GetChunk<TestChunkData>(_allocatedChunks[i], dirtyPage: true);
            chunk.Id = i;
            chunk.ParentId = i > 0 ? _allocatedChunks[i - 1] : 0;
            chunk.Counter = 0;
            chunk.Value1 = i * 1.5f;
            chunk.Value2 = i * 2.5f;
            chunk.Timestamp = DateTime.UtcNow.Ticks;
        }

        // Link chunks (next/prev) - simulating a linked list structure
        for (int i = 0; i < ChunkCount; i++)
        {
            ref var chunk = ref initAccessor.GetChunk<TestChunkData>(_allocatedChunks[i], dirtyPage: true);
            chunk.NextId = i < ChunkCount - 1 ? _allocatedChunks[i + 1] : 0;
            chunk.PrevId = i > 0 ? _allocatedChunks[i - 1] : 0;
        }

        // Generate random access pattern for random access benchmarks
        _random = new Random(42); // Fixed seed for reproducibility
        _randomAccessPattern = new int[RandomAccessCount];
        for (int i = 0; i < RandomAccessCount; i++)
        {
            _randomAccessPattern[i] = _allocatedChunks[_random.Next(ChunkCount)];
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _pmmf?.Dispose();
        _pmmf = null;
        _serviceProvider?.Dispose();
    }

    // ========================================================================
    // Scenario 1: Sequential Read - Iterate through all chunks multiple times
    // Simulates: Leaf iteration in B+Tree, full table scan
    // Note: StackChunkAccessor uses scopes to enable eviction
    // ========================================================================

    [Benchmark(Description = "Sequential Read - ChunkRandomAccessor")]
    public long SequentialRead_ChunkRandomAccessor()
    {
        long sum = 0;
        using var accessor = _segment.CreateChunkRandomAccessor(8);
        for (int i = 0; i < ChunkCount; i++)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<TestChunkData>(_allocatedChunks[i]);
            sum += chunk.Id + chunk.Counter;
        }
        return sum;
    }

    [Benchmark(Description = "Sequential Read - StackChunkAccessor")]
    public long SequentialRead_StackChunkAccessor()
    {
        long sum = 0;
        using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);

        // Use scope-based iteration: enter scope, access batch, exit scope to allow eviction
        const int batchSize = 6; // Leave room within 8 capacity
        for (int batch = 0; batch < ChunkCount; batch += batchSize)
        {
            accessor.EnterScope();
            int end = Math.Min(batch + batchSize, ChunkCount);
            for (int i = batch; i < end; i++)
            {
                ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_allocatedChunks[i]);
                sum += chunk.Id + chunk.Counter;
            }
            accessor.ExitScope(); // Marks slots as evictable
        }
        return sum;
    }

    // ========================================================================
    // Scenario 2: Random Access - Access chunks in random order
    // Simulates: B+Tree lookups, index traversal
    // ========================================================================

    [Benchmark(Description = "Random Access - ChunkRandomAccessor")]
    public long RandomAccess_ChunkRandomAccessor()
    {
        long sum = 0;
        using var accessor = _segment.CreateChunkRandomAccessor(8);
        for (int i = 0; i < RandomAccessCount; i++)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<TestChunkData>(_randomAccessPattern[i]);
            sum += chunk.Id;
        }
        return sum;
    }

    [Benchmark(Description = "Random Access - StackChunkAccessor")]
    public long RandomAccess_StackChunkAccessor()
    {
        long sum = 0;
        using var accessor = StackChunkAccessor.Create(_segment, capacity: 16);

        // Process in small batches - random access can hit many pages
        // Batch size must be <= capacity to avoid exhausting slots within a scope
        const int batchSize = 12;
        for (int batch = 0; batch < RandomAccessCount; batch += batchSize)
        {
            accessor.EnterScope();
            int end = Math.Min(batch + batchSize, RandomAccessCount);
            for (int i = batch; i < end; i++)
            {
                ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_randomAccessPattern[i]);
                sum += chunk.Id;
            }
            accessor.ExitScope();
        }
        return sum;
    }

    // ========================================================================
    // Scenario 3: Mixed Read/Write - Read and update chunks
    // Simulates: Counter updates, modification operations
    // ========================================================================

    [Benchmark(Description = "Mixed Read/Write - ChunkRandomAccessor")]
    public long MixedReadWrite_ChunkRandomAccessor()
    {
        long sum = 0;
        using var accessor = _segment.CreateChunkRandomAccessor(8);
        for (int i = 0; i < ChunkCount; i++)
        {
            ref var chunk = ref accessor.GetChunk<TestChunkData>(_allocatedChunks[i], dirtyPage: true);
            sum += chunk.Counter;
            chunk.Counter++;
            chunk.Timestamp = DateTime.UtcNow.Ticks;
        }
        return sum;
    }

    [Benchmark(Description = "Mixed Read/Write - StackChunkAccessor")]
    public long MixedReadWrite_StackChunkAccessor()
    {
        long sum = 0;
        using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);

        const int batchSize = 6;
        for (int batch = 0; batch < ChunkCount; batch += batchSize)
        {
            accessor.EnterScope();
            int end = Math.Min(batch + batchSize, ChunkCount);
            for (int i = batch; i < end; i++)
            {
                ref var chunk = ref accessor.Get<TestChunkData>(_allocatedChunks[i], dirty: true);
                sum += chunk.Counter;
                chunk.Counter++;
                chunk.Timestamp = DateTime.UtcNow.Ticks;
            }
            accessor.ExitScope();
        }
        return sum;
    }

    // ========================================================================
    // Scenario 4: Linked List Traversal - Follow next/prev pointers
    // Simulates: B+Tree leaf chain traversal, cursor navigation
    // ========================================================================

    [Benchmark(Description = "Linked Traversal - ChunkRandomAccessor")]
    public long LinkedTraversal_ChunkRandomAccessor()
    {
        long sum = 0;
        using var accessor = _segment.CreateChunkRandomAccessor(8);

        // Forward traversal
        int currentId = _allocatedChunks[0];
        while (currentId != 0)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<TestChunkData>(currentId);
            sum += chunk.Id;
            currentId = (int)chunk.NextId;
        }
        return sum;
    }

    [Benchmark(Description = "Linked Traversal - StackChunkAccessor")]
    public long LinkedTraversal_StackChunkAccessor()
    {
        long sum = 0;
        using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);

        // Forward traversal with periodic scope refresh
        int currentId = _allocatedChunks[0];
        int count = 0;
        const int batchSize = 6;

        accessor.EnterScope();
        while (currentId != 0)
        {
            ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(currentId);
            sum += chunk.Id;
            currentId = (int)chunk.NextId;

            // Refresh scope periodically to allow eviction
            if (++count >= batchSize)
            {
                accessor.ExitScope();
                accessor.EnterScope();
                count = 0;
            }
        }
        accessor.ExitScope();
        return sum;
    }

    // ========================================================================
    // Scenario 5: Scoped Access (Recursive Pattern)
    // Simulates: B+Tree insert/delete with parent protection
    // ========================================================================

    [Benchmark(Description = "Scoped Recursive - ChunkRandomAccessor")]
    public long ScopedRecursive_ChunkRandomAccessor()
    {
        using var accessor = _segment.CreateChunkRandomAccessor(16);
        return RecursiveTraverseWithHandles(accessor, 0, 0);
    }

    [Benchmark(Description = "Scoped Recursive - StackChunkAccessor")]
    public long ScopedRecursive_StackChunkAccessor()
    {
        var accessor = StackChunkAccessor.Create(_segment, capacity: 16);
        try
        {
            return RecursiveTraverse(ref accessor, 0, 0);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private long RecursiveTraverse(ref StackChunkAccessor accessor, int depth, int startIndex)
    {
        const int MaxDepth = 5;
        const int ChunksPerLevel = 10;

        if (depth >= MaxDepth || startIndex >= ChunkCount)
            return 0;

        accessor.EnterScope();
        try
        {
            long sum = 0;
            int endIndex = Math.Min(startIndex + ChunksPerLevel, ChunkCount);

            // Access chunks at this level
            for (int i = startIndex; i < endIndex; i++)
            {
                ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_allocatedChunks[i]);
                sum += chunk.Id;
            }

            // Recurse to next level
            sum += RecursiveTraverse(ref accessor, depth + 1, endIndex);

            // After recursion, original refs are still valid due to scope protection
            for (int i = startIndex; i < endIndex; i++)
            {
                ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_allocatedChunks[i]);
                sum += chunk.Counter;
            }

            return sum;
        }
        finally
        {
            accessor.ExitScope();
        }
    }

    private unsafe long RecursiveTraverseWithHandles(ChunkRandomAccessor accessor, int depth, int startIndex)
    {
        const int MaxDepth = 5;
        const int ChunksPerLevel = 10;

        if (depth >= MaxDepth || startIndex >= ChunkCount)
            return 0;

        long sum = 0;
        int endIndex = Math.Min(startIndex + ChunksPerLevel, ChunkCount);
        int handleCount = endIndex - startIndex;

        // Track chunk indices for unpinning later
        Span<int> pinnedChunks = stackalloc int[ChunksPerLevel];

        // Pin pages and access chunks at this level
        for (int i = 0; i < handleCount; i++)
        {
            var chunkId = _allocatedChunks[startIndex + i];
            pinnedChunks[i] = chunkId;
            ref readonly var chunk = ref accessor.GetChunkReadOnly<TestChunkData>(chunkId);
            sum += chunk.Id;
            // Pin the chunk so it survives recursive calls
            // Note: GetChunkAddress with pin=true pins the page
            _ = accessor.GetChunkAsReadOnlySpan(chunkId, pin: true);
        }

        // Recurse to next level - pinned pages stay valid
        sum += RecursiveTraverseWithHandles(accessor, depth + 1, endIndex);

        // After recursion, original refs are still valid due to pinning
        for (int i = 0; i < handleCount; i++)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<TestChunkData>(pinnedChunks[i]);
            sum += chunk.Counter;
        }

        // Release pins
        for (int i = 0; i < handleCount; i++)
        {
            accessor.UnpinChunk(pinnedChunks[i]);
        }

        return sum;
    }

    // ========================================================================
    // Scenario 6: High-frequency accessor creation
    // Simulates: Per-operation accessor pattern
    // ========================================================================

    [Benchmark(Description = "Accessor Creation - ChunkRandomAccessor")]
    public long AccessorCreation_ChunkRandomAccessor()
    {
        long sum = 0;
        for (int i = 0; i < ChunkCount; i++)
        {
            using var accessor = _segment.CreateChunkRandomAccessor(8);
            ref readonly var chunk = ref accessor.GetChunkReadOnly<TestChunkData>(_allocatedChunks[i]);
            sum += chunk.Id;
        }
        return sum;
    }

    [Benchmark(Description = "Accessor Creation - StackChunkAccessor")]
    public long AccessorCreation_StackChunkAccessor()
    {
        long sum = 0;
        for (int i = 0; i < ChunkCount; i++)
        {
            using var accessor = StackChunkAccessor.Create(_segment, capacity: 8);
            ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_allocatedChunks[i]);
            sum += chunk.Id;
        }
        return sum;
    }
}

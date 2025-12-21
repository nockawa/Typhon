using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
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
[SimpleJob(warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
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

    [BenchmarkCategory("Sequential Read"), Benchmark(Baseline = true)]
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

    [BenchmarkCategory("Sequential Read"), Benchmark]
    public long SequentialRead_ChunkAccessor()
    {
        long sum = 0;
        using var accessor = ChunkAccessor.Create(_segment);

        // No scopes needed - automatic LRU eviction
        for (int i = 0; i < ChunkCount; i++)
        {
            ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_allocatedChunks[i]);
            sum += chunk.Id + chunk.Counter;
        }
        return sum;
    }

    // ========================================================================
    // Scenario 2: Random Access - Access chunks in random order
    // Simulates: B+Tree lookups, index traversal
    // ========================================================================

    [BenchmarkCategory("Random Access"), Benchmark(Baseline = true)]
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

    [BenchmarkCategory("Random Access"), Benchmark]
    public long RandomAccess_ChunkAccessor()
    {
        long sum = 0;
        using var accessor = ChunkAccessor.Create(_segment);

        // No scopes - MRU optimization + automatic LRU eviction
        for (int i = 0; i < RandomAccessCount; i++)
        {
            ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_randomAccessPattern[i]);
            sum += chunk.Id;
        }
        return sum;
    }

    // ========================================================================
    // Scenario 3: Mixed Read/Write - Read and update chunks
    // Simulates: Counter updates, modification operations
    // ========================================================================

    [BenchmarkCategory("Mixed Read/Write"), Benchmark(Baseline = true)]
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

    [BenchmarkCategory("Mixed Read/Write"), Benchmark]
    public long MixedReadWrite_ChunkAccessor()
    {
        long sum = 0;
        using var accessor = ChunkAccessor.Create(_segment);

        for (int i = 0; i < ChunkCount; i++)
        {
            ref var chunk = ref accessor.Get<TestChunkData>(_allocatedChunks[i], dirty: true);
            sum += chunk.Counter;
            chunk.Counter++;
            chunk.Timestamp = DateTime.UtcNow.Ticks;
        }
        return sum;
    }

    // ========================================================================
    // Scenario 4: Linked List Traversal - Follow next/prev pointers
    // Simulates: B+Tree leaf chain traversal, cursor navigation
    // ========================================================================

    [BenchmarkCategory("Linked Traversal"), Benchmark(Baseline = true)]
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

    [BenchmarkCategory("Linked Traversal"), Benchmark]
    public long LinkedTraversal_ChunkAccessor()
    {
        long sum = 0;
        using var accessor = ChunkAccessor.Create(_segment);

        // Forward traversal - no scope management needed
        int currentId = _allocatedChunks[0];
        while (currentId != 0)
        {
            ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(currentId);
            sum += chunk.Id;
            currentId = (int)chunk.NextId;
        }
        return sum;
    }

    // ========================================================================
    // Scenario 5: Scoped Access (Recursive Pattern)
    // Simulates: B+Tree insert/delete with parent protection
    // ========================================================================

    [BenchmarkCategory("Scoped Recursive"), Benchmark(Baseline = true)]
    public long ScopedRecursive_ChunkRandomAccessor()
    {
        using var accessor = _segment.CreateChunkRandomAccessor(16);
        return RecursiveTraverseWithHandles(accessor, 0, 0);
    }

    [BenchmarkCategory("Scoped Recursive"), Benchmark]
    public long ScopedRecursive_ChunkAccessor()
    {
        var accessor = ChunkAccessor.Create(_segment);
        try
        {
            return RecursiveTraverseChunkAccessor(ref accessor, 0, 0);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ========================================================================
    // Helper method for ChunkAccessor recursive traversal
    // ========================================================================

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

    private unsafe long RecursiveTraverseChunkAccessor(ref ChunkAccessor accessor, int depth, int startIndex)
    {
        const int MaxDepth = 5;
        const int ChunksPerLevel = 10;

        if (depth >= MaxDepth || startIndex >= ChunkCount)
            return 0;

        long sum = 0;
        int endIndex = Math.Min(startIndex + ChunksPerLevel, ChunkCount);
        int scopeCount = endIndex - startIndex;

        var scopes = stackalloc ChunkScope<TestChunkData>[scopeCount];

        try
        {
            // Access chunks at this level with scoped protection
            for (int i = 0; i < scopeCount; i++)
            {
                var scope = accessor.GetScoped<TestChunkData>(_allocatedChunks[startIndex + i]);
                scopes[i] = scope;
                sum += scope.AsRef().Id;
            }

            // Recurse to next level - scoped chunks are pinned and safe
            sum += RecursiveTraverseChunkAccessor(ref accessor, depth + 1, endIndex);

            // After recursion, scoped refs are still valid
            for (int i = 0; i < scopeCount; i++)
            {
                sum += scopes[i].AsRef().Counter;
            }
        }
        finally
        {
            // Dispose scopes (unpin) in reverse order
            for (int i = scopeCount - 1; i >= 0; i--)
            {
                scopes[i].Dispose();
            }
        }

        return sum;
    }
    
    // ========================================================================
    // Scenario 6: High-frequency accessor creation
    // Simulates: Per-operation accessor pattern
    // ========================================================================

    [BenchmarkCategory("Accessor Creation"), Benchmark(Baseline = true)]
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

    [BenchmarkCategory("Accessor Creation"), Benchmark]
    public long AccessorCreation_ChunkAccessor()
    {
        long sum = 0;
        for (int i = 0; i < ChunkCount; i++)
        {
            using var accessor = ChunkAccessor.Create(_segment);
            ref readonly var chunk = ref accessor.GetReadOnly<TestChunkData>(_allocatedChunks[i]);
            sum += chunk.Id;
        }
        return sum;
    }
}

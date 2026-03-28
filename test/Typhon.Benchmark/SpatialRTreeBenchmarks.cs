using System;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// 1. INSERT BENCHMARKS
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "Insert")]
public unsafe class SpatialInsertBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatInsert";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.OverrideDatabaseCacheMinSize = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    private void RunInsert(int count, SpatialVariant variant, double worldSize, bool clustered)
    {
        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var seg = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(seg, variant);
        var rng = new Random(42);
        int halfCoord = desc.CoordCount / 2;
        var accessor = seg.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < count; i++)
            {
                Span<double> coords = stackalloc double[desc.CoordCount];
                for (int d = 0; d < halfCoord; d++)
                {
                    double v = clustered ? 5000 + rng.NextDouble() * 100 : rng.NextDouble() * worldSize;
                    coords[d] = v;
                    coords[d + halfCoord] = v + rng.NextDouble() * 8 + 2;
                }
                tree.Insert(i + 1, coords, ref accessor);
            }
        }
        finally { accessor.Dispose(); }
    }

    [Benchmark] public void Insert_Sparse_2D_1K() => RunInsert(1_000, SpatialVariant.R2Df32, 10000, false);
    [Benchmark] public void Insert_Sparse_2D_10K() => RunInsert(10_000, SpatialVariant.R2Df32, 10000, false);
    [Benchmark] public void Insert_Sparse_2D_100K() => RunInsert(100_000, SpatialVariant.R2Df32, 10000, false);
    [Benchmark] public void Insert_Clustered_2D_10K() => RunInsert(10_000, SpatialVariant.R2Df32, 10000, true);
    [Benchmark] public void Insert_3D_10K() => RunInsert(10_000, SpatialVariant.R3Df32, 10000, false);
}

// ═══════════════════════════════════════════════════════════════════════
// 2. QUERY BENCHMARKS (AABB + Frustum)
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "Query")]
public unsafe class SpatialQueryBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;
    private SpatialRTree<PersistentStore> _tree;
    private SpatialVariant _variant;

    [Params(1_000, 10_000, 100_000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatQuery";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.OverrideDatabaseCacheMinSize = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();
        _variant = SpatialVariant.R2Df32;

        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(_variant);
        var seg = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        _tree = new SpatialRTree<PersistentStore>(seg, _variant);
        var rng = new Random(42);
        var accessor = seg.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < EntityCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _tree.Insert(i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
            }
        }
        finally { accessor.Dispose(); }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _tree = null;
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    [Benchmark]
    public int Query_SmallBox_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            double cx = (q % 10) * 1000 + 450;
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { cx, 4500, cx + 100, 5500 }))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Query_LargeBox_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { 0, 0, 5000, 5000 }))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Query_MissAll_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryAABB(stackalloc double[] { 20000, 20000, 20100, 20100 }))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Frustum_Narrow_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        Span<double> planes = stackalloc double[]
        {
            1, 0, -4900, -1, 0, 5100, 0, 1, 0, 0, -1, 10000,
        };
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryFrustum(planes, 4))
            {
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int Frustum_Wide_2D()
    {
        using var guard = EpochGuard.Enter(_em);
        int total = 0;
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0, -1, 0, 5000, 0, 1, 0, 0, -1, 5000,
        };
        for (int q = 0; q < 100; q++)
        {
            foreach (var _ in _tree.QueryFrustum(planes, 4))
            {
                total++;
            }
        }
        return total;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// 3. GAME TICK MIXED WORKLOAD
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[JsonExporterAttribute.Full]
[BenchmarkCategory("SpatialRTree", "GameTick")]
public unsafe class SpatialGameTickBenchmarks
{
    private ServiceCollection _sc;
    private ServiceProvider _sp;
    private ManagedPagedMMF _pmmf;
    private EpochManager _em;
    private SpatialRTree<PersistentStore> _tree;
    private ChunkBasedSegment<PersistentStore> _segment;
    private double[] _entityCoords;
    private int _entityCount;

    [Params(10_000, 50_000, 100_000, 200_000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sc = new ServiceCollection();
        _sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "SpatTick";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.OverrideDatabaseCacheMinSize = true;
            });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _sp = _sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _pmmf = _sp.GetRequiredService<ManagedPagedMMF>();
        _em = _sp.GetRequiredService<EpochManager>();

        using var guard = EpochGuard.Enter(_em);
        var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32);
        _segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 512, desc.Stride);
        _tree = new SpatialRTree<PersistentStore>(_segment, SpatialVariant.R2Df32);
        _entityCoords = new double[EntityCount * 4];
        var rng = new Random(42);
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < EntityCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _entityCoords[i * 4] = x; _entityCoords[i * 4 + 1] = y;
                _entityCoords[i * 4 + 2] = x + 5; _entityCoords[i * 4 + 3] = y + 5;
                _tree.Insert(i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
            }
        }
        finally { accessor.Dispose(); }
        _entityCount = EntityCount;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _tree = null; _segment = null; _entityCoords = null;
        _em?.Dispose(); _pmmf?.Dispose(); _sp?.Dispose();
    }

    [Benchmark]
    public int GameTick()
    {
        using var guard = EpochGuard.Enter(_em);
        var rng = new Random(123);
        var accessor = _segment.CreateChunkAccessor();
        int ops = 0;
        try
        {
            // 5% spawn
            int spawnCount = _entityCount / 20;
            for (int i = 0; i < spawnCount; i++)
            {
                double x = rng.NextDouble() * 10000, y = rng.NextDouble() * 10000;
                _tree.Insert(_entityCount + i + 1, stackalloc double[] { x, y, x + 5, y + 5 }, ref accessor);
                ops++;
            }

            // 30% containment checks (2% escape rate)
            int moveCount = (int)(_entityCount * 0.3);
            Span<double> fat = stackalloc double[4];
            Span<double> tight = stackalloc double[4];
            for (int i = 0; i < moveCount; i++)
            {
                int idx = rng.Next(_entityCount);
                fat[0] = _entityCoords[idx * 4] - 5; fat[1] = _entityCoords[idx * 4 + 1] - 5;
                fat[2] = _entityCoords[idx * 4 + 2] + 5; fat[3] = _entityCoords[idx * 4 + 3] + 5;
                double move = (i % 50 == 0) ? 20.0 : 0.5;
                tight[0] = _entityCoords[idx * 4] + move; tight[1] = _entityCoords[idx * 4 + 1] + move;
                tight[2] = _entityCoords[idx * 4 + 2] + move; tight[3] = _entityCoords[idx * 4 + 3] + move;
                CoordsContained(fat, tight, 4);
                ops++;
            }

            // 10 queries
            for (int q = 0; q < 10; q++)
            {
                double cx = rng.NextDouble() * 9000, cy = rng.NextDouble() * 9000;
                foreach (var _ in _tree.QueryAABB(stackalloc double[] { cx, cy, cx + 500, cy + 500 }))
                {
                    ops++;
                }
            }
        }
        finally { accessor.Dispose(); }
        return ops;
    }

    private static bool CoordsContained(ReadOnlySpan<double> fat, ReadOnlySpan<double> tight, int coordCount)
    {
        int half = coordCount / 2;
        for (int i = 0; i < half; i++) if (fat[i] > tight[i]) return false;
        for (int i = half; i < coordCount; i++) if (fat[i] < tight[i]) return false;
        return true;
    }
}

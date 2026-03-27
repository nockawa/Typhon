using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tree-level query tests for all spatial query types. Compares R-Tree against BruteForceSpatialIndex oracle.
/// </summary>
[TestFixture]
public class SpatialQueryTests
{
    private IServiceProvider _serviceProvider;
    private string _testDatabaseDir;

    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', '"', '.', '<', '>', '+', ' ' })
            {
                testName = testName.Replace(c, '_');
            }
            if (testName.Length > 30)
            {
                testName = testName[^30..];
            }
            return $"SQT{testName}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialQueryTests");
        Directory.CreateDirectory(_testDatabaseDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "mm:ss.fff "; });
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = CurrentDatabaseName;
                o.DatabaseDirectory = _testDatabaseDir;
                o.DatabaseCacheSize = (ulong)(PagedMMF.DefaultMemPageCount * PagedMMF.PageSize);
                o.OverrideDatabaseCacheMinSize = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        if (_testDatabaseDir != null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_testDatabaseDir))
                {
                    File.Delete(file);
                }
            }
            catch { /* ignore cleanup errors */ }
        }
    }

    private (SpatialRTree<PersistentStore> tree, BruteForceSpatialIndex oracle, ChunkBasedSegment<PersistentStore> segment)
        CreateTreeAndOracle(ManagedPagedMMF pmmf, int entityCount, SpatialVariant variant = SpatialVariant.R2Df32, int seed = 42)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        int coordCount = desc.CoordCount;
        var oracle = new BruteForceSpatialIndex(coordCount);
        var rng = new Random(seed);

        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < entityCount; i++)
            {
                int halfCoord = coordCount / 2;
                Span<double> coords = stackalloc double[coordCount];
                for (int d = 0; d < halfCoord; d++)
                {
                    double v = rng.NextDouble() * 1000;
                    double size = rng.NextDouble() * 10 + 1;
                    coords[d] = v;
                    coords[d + halfCoord] = v + size;
                }
                tree.Insert(i + 1, coords, ref accessor);
                oracle.Insert(i + 1, coords);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (tree, oracle, segment);
    }

    // ── Radius Query ─────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryRadius_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> center = stackalloc double[] { 500, 500 };
        double radius = 100;

        var treeResults = new HashSet<long>();
        foreach (var hit in tree.QueryRadius(center, radius))
        {
            treeResults.Add(hit.EntityId);
        }

        var oracleResults = oracle.QueryRadius(center, radius);
        Assert.That(treeResults, Is.EquivalentTo(new HashSet<long>(oracleResults)));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void QueryRadius_EmptyRegion_ReturnsEmpty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 50);

        Span<double> center = stackalloc double[] { 5000, 5000 };
        int count = 0;
        foreach (var _ in tree.QueryRadius(center, 10))
        {
            count++;
        }
        Assert.That(count, Is.EqualTo(0));
        guard.Dispose();
    }

    // ── Ray Query ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryRay_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        Span<double> origin = stackalloc double[] { 0, 500 };
        Span<double> direction = stackalloc double[] { 1, 0 };
        double maxDist = 1500;

        var treeResults = new HashSet<long>();
        foreach (var hit in tree.QueryRay(origin, direction, maxDist))
        {
            treeResults.Add(hit.EntityId);
        }

        var oracleResults = oracle.QueryRay(origin, direction, maxDist);
        var oracleIds = new HashSet<long>();
        foreach (var (id, _) in oracleResults)
        {
            oracleIds.Add(id);
        }

        Assert.That(treeResults, Is.EquivalentTo(oracleIds));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void QueryRay_MissAll_ReturnsEmpty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 50);

        Span<double> origin = stackalloc double[] { 5000, 0 };
        Span<double> direction = stackalloc double[] { 0, 1 };

        int count = 0;
        foreach (var _ in tree.QueryRay(origin, direction, 1000))
        {
            count++;
        }
        Assert.That(count, Is.EqualTo(0));
        guard.Dispose();
    }

    // ── Frustum Query ────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryFrustum_MatchesBruteForce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, oracle, _) = CreateTreeAndOracle(pmmf, 200);

        // Box frustum [200..800, 200..800] — 4 planes, 3 doubles each (2D: normalX, normalY, distance)
        Span<double> planes = stackalloc double[]
        {
            1, 0, -200,    // x >= 200
            -1, 0, 800,    // x <= 800
            0, 1, -200,    // y >= 200
            0, -1, 800,    // y <= 800
        };

        var treeResults = new HashSet<long>();
        foreach (var hit in tree.QueryFrustum(planes, 4))
        {
            treeResults.Add(hit.EntityId);
        }

        var oracleResults = oracle.QueryFrustum(planes, 4);
        Assert.That(treeResults, Is.EquivalentTo(new HashSet<long>(oracleResults)));
        guard.Dispose();
    }

    // ── kNN Query ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    public void QueryKNN_ExactK()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 100);

        Span<double> center = stackalloc double[] { 500, 500 };
        Span<(long entityId, double distSq)> results = stackalloc (long, double)[5];

        int count = tree.QueryKNN(center, 5, results);
        Assert.That(count, Is.EqualTo(5));
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public void QueryKNN_FewerThanK_ReturnsAll()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var (tree, _, _) = CreateTreeAndOracle(pmmf, 10);

        Span<double> center = stackalloc double[] { 500, 500 };
        Span<(long entityId, double distSq)> results = stackalloc (long, double)[100];

        int count = tree.QueryKNN(center, 100, results);
        Assert.That(count, Is.EqualTo(10));
        guard.Dispose();
    }

    // ── Geometry helper tests ────────────────────────────────────────────

    [Test]
    public void RayAABBIntersect_HitFromOutside()
    {
        Span<double> origin = stackalloc double[] { 0, 5 };
        Span<double> invDir = stackalloc double[] { 1.0, 0 }; // horizontal ray (invDir.Y = 0 → not infinity for this test since no Y component)
        Span<double> aabb = stackalloc double[] { 10, 0, 20, 10 };

        // Need proper invDir: 1/dirX=1, 1/dirY=MaxValue (direction.Y=0)
        invDir[1] = double.MaxValue;

        var (hit, tEntry) = SpatialGeometry.RayAABBIntersect(origin, invDir, aabb, 4);
        Assert.That(hit, Is.True);
        Assert.That(tEntry, Is.EqualTo(10.0).Within(0.001));
    }

    [Test]
    public void RayAABBIntersect_Miss()
    {
        Span<double> origin = stackalloc double[] { 0, 15 };
        Span<double> invDir = stackalloc double[] { 1.0, double.MaxValue };
        Span<double> aabb = stackalloc double[] { 10, 0, 20, 10 };

        var (hit, _) = SpatialGeometry.RayAABBIntersect(origin, invDir, aabb, 4);
        Assert.That(hit, Is.False);
    }

    [Test]
    public void ClassifyAABB_Inside()
    {
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0,
            -1, 0, 100,
            0, 1, 0,
            0, -1, 100,
        };
        Span<double> aabb = stackalloc double[] { 20, 20, 80, 80 };
        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(aabb, planes, 4, 2);
        Assert.That(cls, Is.EqualTo(SpatialGeometry.FrustumInside));
    }

    [Test]
    public void ClassifyAABB_Outside()
    {
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0,
            -1, 0, 100,
            0, 1, 0,
            0, -1, 100,
        };
        Span<double> aabb = stackalloc double[] { 200, 200, 300, 300 };
        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(aabb, planes, 4, 2);
        Assert.That(cls, Is.EqualTo(SpatialGeometry.FrustumOutside));
    }

    [Test]
    public void ClassifyAABB_Intersecting()
    {
        Span<double> planes = stackalloc double[]
        {
            1, 0, 0,
            -1, 0, 100,
            0, 1, 0,
            0, -1, 100,
        };
        Span<double> aabb = stackalloc double[] { 50, 50, 150, 150 };
        int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(aabb, planes, 4, 2);
        Assert.That(cls, Is.EqualTo(SpatialGeometry.FrustumIntersecting));
    }

    [Test]
    public void SquaredDistanceToCenter_Correct()
    {
        Span<double> point = stackalloc double[] { 0, 0 };
        Span<double> aabb = stackalloc double[] { 10, 20, 30, 40 };
        double distSq = SpatialGeometry.SquaredDistanceToCenter(point, aabb, 4);
        Assert.That(distSq, Is.EqualTo(1300.0).Within(0.001));
    }
}

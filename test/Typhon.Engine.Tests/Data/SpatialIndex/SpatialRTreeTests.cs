using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
[NonParallelizable]
public class SpatialRTreeTests
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
            return $"SRT{testName}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialRTreeTests");
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

        // Clean up database files
        if (_testDatabaseDir != null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_testDatabaseDir))
                {
                    try { File.Delete(file); }
                    catch { /* ignore cleanup failures */ }
                }
            }
            catch { /* ignore */ }
        }
    }

private static double[] MakeCoords(SpatialVariant variant, double minX, double minY, double maxX, double maxY)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var coords = new double[desc.CoordCount];
        int h = desc.CoordCount / 2;
        coords[0] = minX;
        coords[1] = minY;
        if (h == 3) { coords[2] = 0; }
        coords[h] = maxX;
        coords[h + 1] = maxY;
        if (h == 3) { coords[h + 2] = 1; }
        return coords;
    }

    private static List<long> CollectQueryResults(SpatialRTree<PersistentStore> tree, double[] queryCoords)
    {
        var results = new List<long>();
        foreach (var result in tree.QueryAABB(queryCoords))
        {
            results.Add(result.EntityId);
        }
        return results;
    }

    // ── Structural Correctness ──────────────────────────────────────────────

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void Insert_SingleEntity(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);

        var accessor = segment.CreateChunkAccessor();
        var coords = MakeCoords(variant, 1, 2, 3, 4);
        var (leafId, slot) = tree.Insert(100, coords, ref accessor);

        Assert.That(tree.EntityCount, Is.EqualTo(1));
        Assert.That(leafId, Is.GreaterThan(0));
        Assert.That(slot, Is.EqualTo(0));

        TreeValidator.Validate(tree);
        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void Insert_FillLeaf(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        for (int i = 0; i < desc.LeafCapacity; i++)
        {
            var coords = MakeCoords(variant, i * 10, i * 10, i * 10 + 5, i * 10 + 5);
            tree.Insert(i + 1, coords, ref accessor);
        }

        Assert.That(tree.EntityCount, Is.EqualTo(desc.LeafCapacity));
        Assert.That(tree.Depth, Is.EqualTo(1), "No split should occur when exactly filling leaf");
        TreeValidator.Validate(tree);
        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void Insert_TriggerSplit(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        int count = desc.LeafCapacity + 1;
        for (int i = 0; i < count; i++)
        {
            var coords = MakeCoords(variant, i * 10, i * 10, i * 10 + 5, i * 10 + 5);
            tree.Insert(i + 1, coords, ref accessor);
        }

        Assert.That(tree.EntityCount, Is.EqualTo(count));
        Assert.That(tree.Depth, Is.EqualTo(2), "Split should increase depth to 2");
        Assert.That(tree.NodeCount, Is.EqualTo(3), "Root (internal) + 2 leaves after split");
        TreeValidator.Validate(tree);
        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(10000)]
public void Insert_CascadingSplit(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        int targetCount = desc.LeafCapacity * desc.InternalCapacity + 1;
        for (int i = 0; i < targetCount; i++)
        {
            var coords = MakeCoords(variant, i * 10, i * 10, i * 10 + 5, i * 10 + 5);
            tree.Insert(i + 1, coords, ref accessor);
        }

        Assert.That(tree.EntityCount, Is.EqualTo(targetCount));
        Assert.That(tree.Depth, Is.GreaterThanOrEqualTo(3));
        TreeValidator.Validate(tree);
        accessor.Dispose();
        guard.Dispose();
    }

    // ── Remove ──────────────────────────────────────────────────────────────

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void Remove_SwapWithLast(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        var slots = new (int leafId, int slot)[3];
        for (int i = 0; i < 3; i++)
        {
            var coords = MakeCoords(variant, i * 10, 0, i * 10 + 5, 5);
            slots[i] = tree.Insert(i + 1, coords, ref accessor);
        }

        long swapped = tree.Remove(slots[0].leafId, slots[0].slot, ref accessor);
        Assert.That(tree.EntityCount, Is.EqualTo(2));
        Assert.That(swapped, Is.GreaterThan(0), "First-slot removal should swap with last");
        TreeValidator.Validate(tree);
        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void Remove_AllEntities_ReverseOrder(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        int count = 5;
        var rootLeafId = 0;
        for (int i = 0; i < count; i++)
        {
            var coords = MakeCoords(variant, i * 10, 0, i * 10 + 5, 5);
            var (leafId, _) = tree.Insert(i + 1, coords, ref accessor);
            rootLeafId = leafId;
        }

        // Remove all from last to first (no swap issues)
        for (int i = count - 1; i >= 0; i--)
        {
            tree.Remove(rootLeafId, i, ref accessor);
        }

        Assert.That(tree.EntityCount, Is.EqualTo(0));
        TreeValidator.Validate(tree);
        accessor.Dispose();
        guard.Dispose();
    }

    // ── Query Correctness ───────────────────────────────────────────────────

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void QueryAABB_SmallBox(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        tree.Insert(1, MakeCoords(variant, 0, 0, 10, 10), ref accessor);
        tree.Insert(2, MakeCoords(variant, 50, 50, 60, 60), ref accessor);
        tree.Insert(3, MakeCoords(variant, 100, 100, 110, 110), ref accessor);

        var results = CollectQueryResults(tree, MakeCoords(variant, -5, -5, 5, 5));

        Assert.That(results, Does.Contain(1L));
        Assert.That(results, Does.Not.Contain(2L));
        Assert.That(results, Does.Not.Contain(3L));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void QueryAABB_EntireTree(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        for (int i = 0; i < 10; i++)
        {
            tree.Insert(i + 1, MakeCoords(variant, i * 10, i * 10, i * 10 + 5, i * 10 + 5), ref accessor);
        }

        var results = CollectQueryResults(tree, MakeCoords(variant, -100, -100, 1000, 1000));
        Assert.That(results.Count, Is.EqualTo(10), "All 10 entities should be found");

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(5000)]
    public void QueryAABB_EmptyRegion(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        for (int i = 0; i < 5; i++)
        {
            tree.Insert(i + 1, MakeCoords(variant, i * 10, 0, i * 10 + 5, 5), ref accessor);
        }

        var results = CollectQueryResults(tree, MakeCoords(variant, 500, 500, 600, 600));
        Assert.That(results.Count, Is.EqualTo(0));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [TestCase(SpatialVariant.R2Df64)]
    [TestCase(SpatialVariant.R3Df64)]
    [CancelAfter(10000)]
public void QueryAABB_vs_BruteForce_RandomData(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();
        var oracle = new BruteForceSpatialIndex(desc.CoordCount);
        var rand = new Random(12345);

        int entityCount = desc.LeafCapacity * 5;
        for (int i = 0; i < entityCount; i++)
        {
            double x = rand.NextDouble() * 1000;
            double y = rand.NextDouble() * 1000;
            var coords = MakeCoords(variant, x, y, x + rand.NextDouble() * 20, y + rand.NextDouble() * 20);
            tree.Insert(i + 1, coords, ref accessor);
            oracle.Insert(i + 1, coords);
        }

        TreeValidator.Validate(tree);

        for (int q = 0; q < 20; q++)
        {
            double qx = rand.NextDouble() * 900;
            double qy = rand.NextDouble() * 900;
            var queryCoords = MakeCoords(variant, qx, qy, qx + rand.NextDouble() * 100, qy + rand.NextDouble() * 100);

            var treeResults = new HashSet<long>(CollectQueryResults(tree, queryCoords));
            var oracleResults = new HashSet<long>(oracle.QueryAABB(queryCoords));

            Assert.That(oracleResults.IsSubsetOf(treeResults), Is.True,
                $"Query {q}: oracle found entities not in R-Tree results");
        }

        accessor.Dispose();
        guard.Dispose();
    }
}

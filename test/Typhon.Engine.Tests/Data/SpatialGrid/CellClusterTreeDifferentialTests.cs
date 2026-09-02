using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-9.1</c> — the per-cell R-Tree must agree with a brute-force scan of the same clusters, over randomised populations, query boxes and cluster extents.
/// This is the <c>SQ-01</c> guard for #872 step 9.
/// </summary>
/// <remarks>
/// <para>The oracle is a plain loop over <see cref="CellSpatialIndex"/>'s SoA arrays — the structure the tree replaces. That is deliberate and mirrors what
/// step 8 did with <c>DenseSpatialGridReference</c>: an oracle that shares an optimisation with the thing it checks cannot catch a bug in that optimisation,
/// and the linear scan shares none of the tree's pruning, node layout or split logic.</para>
/// <para>Comparison is on the SET of cluster ids, never the order. A tree returns them in descent order and a scan in insertion order; asserting order would
/// pin an implementation detail and fail for a reason that has nothing to do with correctness.</para>
/// </remarks>
[TestFixture]
unsafe class CellClusterTreeDifferentialTests
{
    private IServiceProvider _serviceProvider;
    private string _dir;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "CellClusterTreeDiff");
        Directory.CreateDirectory(_dir);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "CCTDiff";
                o.DatabaseDirectory = _dir;
                o.DatabaseCacheSize = 2048UL * PagedMMF.PageSize;
                o.TestMode = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private ChunkBasedSegment<TransientStore> CreateSegment(out TransientStore store)
    {
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var allocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        var registry = _serviceProvider.GetRequiredService<IResourceRegistry>();
        var desc = SpatialNodeDescriptor.ForVariant(CellClusterTree.Variant);

        store = new TransientStore(new TransientOptions(), allocator, em, registry.Root);

        // Pages allocated on the store BEFORE the segment is constructed: TransientStore is a struct and the segment copies it by value, so allocating after
        // construction would leave the segment's copy at _pageCount = 0 and the first Grow would re-issue the same page indices.
        Span<int> pages = stackalloc int[8];
        store.AllocatePages(ref pages, 0, null);
        var segment = new ChunkBasedSegment<TransientStore>(em, store, desc.Stride);
        segment.Create(PageBlockType.None, StorageSegmentKind.Cluster, pages, false);
        return segment;
    }

    private static ClusterSpatialAabb Box(float minX, float minY, float maxX, float maxY, uint mask = uint.MaxValue) => new()
    {
        MinX = minX,
        MinY = minY,
        MinZ = float.PositiveInfinity,
        MaxX = maxX,
        MaxY = maxY,
        MaxZ = float.NegativeInfinity,
        CategoryMask = mask,
    };

    /// <summary>Brute force: every cluster in the oracle whose box overlaps the query, in XY (2D clusters carry the Z sentinel).</summary>
    private static HashSet<int> ScanOracle(CellSpatialIndex oracle, float minX, float minY, float maxX, float maxY)
    {
        var hits = new HashSet<int>();
        for (int i = 0; i < oracle.ClusterCount; i++)
        {
            if (oracle.MaxX[i] < minX || oracle.MinX[i] > maxX) { continue; }
            if (oracle.MaxY[i] < minY || oracle.MinY[i] > maxY) { continue; }
            hits.Add(oracle.ClusterIds[i]);
        }
        return hits;
    }

    private static HashSet<int> QueryTree(CellClusterTree tree, float minX, float minY, float maxX, float maxY)
    {
        Span<double> coords = stackalloc double[6];
        CellClusterTree.QueryToCoords(minX, minY, float.NegativeInfinity, maxX, maxY, float.PositiveInfinity, coords);

        var hits = new HashSet<int>();
        foreach (var r in tree.Query(coords, 0))
        {
            hits.Add((int)r.EntityId);
        }
        return hits;
    }

    [Test]
    [CancelAfter(60_000)]
    public void Tree_AgreesWithLinearScan_OverRandomisedPopulationsAndQueries()
    {
        // ChunkAccessor creation asserts an epoch scope; production callers are always inside the tick fence's.
        using var epoch = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());

        const int ClusterCount = 220;   // well past LeafCapacity 11 — forces several splits and at least one root split
        var backPointers = new int[ClusterCount + 1];
        Array.Fill(backPointers, SpatialRTree<TransientStore>.NullHandle);

        var segment = CreateSegment(out _);
        var tree = new CellClusterTree(segment, backPointers);
        var oracle = new CellSpatialIndex();

        var rng = new Random(20260902);
        var boxes = new ClusterSpatialAabb[ClusterCount + 1];

        for (int id = 1; id <= ClusterCount; id++)
        {
            // Extents deliberately span three orders of magnitude: a population of uniform boxes never exercises the overlap-minimising split.
            float x = (float)rng.NextDouble() * 100f;
            float y = (float)rng.NextDouble() * 100f;
            float w = (float)Math.Pow(10, rng.NextDouble() * 1.5) * 0.1f;
            var box = Box(x, y, x + w, y + w);

            boxes[id] = box;
            backPointers[id] = tree.Add(id, in box);
            oracle.Add(id, in box);
        }

        Assert.That(tree.ClusterCount, Is.EqualTo(oracle.ClusterCount), "both structures must hold the same population before anything is compared");

        int totalHits = 0;
        for (int q = 0; q < 500; q++)
        {
            float qx = (float)rng.NextDouble() * 110f - 5f;
            float qy = (float)rng.NextDouble() * 110f - 5f;
            // Query extents from ~0.2 to ~100 units against a 100-unit population: the small end exercises deep pruning and the large end exercises the
            // "most of the tree matches" case, where a wrong subtree skip is easiest to hide.
            float qw = (float)Math.Pow(10, rng.NextDouble() * 2.7) * 0.2f;

            var expected = ScanOracle(oracle, qx, qy, qx + qw, qy + qw);
            var actual = QueryTree(tree, qx, qy, qx + qw, qy + qw);
            totalHits += expected.Count;

            Assert.That(actual, Is.EquivalentTo(expected),
                $"query [{qx:F2},{qy:F2} .. {qx + qw:F2},{qy + qw:F2}] disagreed — a missing id is an SQ-01 false negative");
        }

        // A differential that never matched anything would pass just as loudly.
        // Non-vacuity, not a performance claim: a differential over queries that match nothing passes for the wrong reason. The bar is deliberately well
        // under what the current mix produces, so a change to the population shape does not turn this into a flaky threshold.
        Assert.That(totalHits, Is.GreaterThan(2_000), $"the query set must actually hit clusters to be a test at all (hits={totalHits})");
        TestContext.Out.WriteLine($"CCTDIFF clusters={ClusterCount} queries=500 totalHits={totalHits} treeNodes={tree.Tree.NodeCount} depth={tree.Tree.Depth}");
    }

    [Test]
    [CancelAfter(60_000)]
    public void EscapeBoundUpdate_KeepsTheTreeAgreeingWithTheScan()
    {
        // ChunkAccessor creation asserts an epoch scope; production callers are always inside the tick fence's.
        using var epoch = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());

        const int ClusterCount = 120;
        var backPointers = new int[ClusterCount + 1];
        Array.Fill(backPointers, SpatialRTree<TransientStore>.NullHandle);

        var segment = CreateSegment(out _);
        var tree = new CellClusterTree(segment, backPointers);
        var oracle = new CellSpatialIndex();
        var oracleSlot = new int[ClusterCount + 1];

        var rng = new Random(424242);
        for (int id = 1; id <= ClusterCount; id++)
        {
            float x = (float)rng.NextDouble() * 100f;
            float y = (float)rng.NextDouble() * 100f;
            var box = Box(x, y, x + 3f, y + 3f);
            backPointers[id] = tree.Add(id, in box);
            oracleSlot[id] = oracle.Add(id, in box);
        }

        int inPlace = 0;
        int escapes = 0;
        for (int round = 0; round < 12; round++)
        {
            for (int id = 1; id <= ClusterCount; id++)
            {
                // A mix of tiny drifts (which should stay inside the leaf) and jumps (which must escape) — a test that only drifts never exercises reinsert.
                float step = rng.NextDouble() < 0.8 ? 0.05f : 25f;
                float x = Math.Clamp((float)rng.NextDouble() * step + (id % 100), 0f, 100f);
                float y = Math.Clamp((float)rng.NextDouble() * step + (id % 97), 0f, 100f);
                var box = Box(x, y, x + 3f, y + 3f);

                backPointers[id] = tree.UpdateAt(backPointers[id], id, in box, out bool escaped);
                if (escaped) { escapes++; } else { inPlace++; }
                oracle.UpdateAt(oracleSlot[id], in box);
            }
        }

        TestContext.Out.WriteLine($"CCTDIFF update inPlace={inPlace} escapes={escapes} ({100.0 * escapes / (inPlace + escapes):F1}% escaped)");
        Assert.Multiple(() =>
        {
            Assert.That(inPlace, Is.GreaterThan(0), "the in-place path must be taken, or C5 is not being exercised");
            Assert.That(escapes, Is.GreaterThan(0), "the escape path must be taken, or reinsert is untested");
        });

        for (int q = 0; q < 300; q++)
        {
            float qx = (float)rng.NextDouble() * 110f - 5f;
            float qy = (float)rng.NextDouble() * 110f - 5f;
            float qw = (float)rng.NextDouble() * 20f + 1f;

            var expected = ScanOracle(oracle, qx, qy, qx + qw, qy + qw);
            var actual = QueryTree(tree, qx, qy, qx + qw, qy + qw);
            Assert.That(actual, Is.EquivalentTo(expected), $"after escape-bound updates, query {q} disagreed with the scan");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void HandlesStayValid_AcrossUpdatesAndRemovals()
    {
        // ChunkAccessor creation asserts an epoch scope; production callers are always inside the tick fence's.
        using var epoch = EpochGuard.Enter(_serviceProvider.GetRequiredService<EpochManager>());

        const int ClusterCount = 90;
        var backPointers = new int[ClusterCount + 1];
        Array.Fill(backPointers, SpatialRTree<TransientStore>.NullHandle);

        var segment = CreateSegment(out _);
        var tree = new CellClusterTree(segment, backPointers);

        var rng = new Random(9090);
        for (int id = 1; id <= ClusterCount; id++)
        {
            float x = (float)rng.NextDouble() * 100f;
            var box = Box(x, x, x + 2f, x + 2f);
            backPointers[id] = tree.Add(id, in box);
        }

        // Remove a third, then confirm every survivor's handle still names its own payload — the property the whole back-pointer mechanism exists for.
        var removed = new HashSet<int>();
        for (int id = 3; id <= ClusterCount; id += 3)
        {
            tree.RemoveAt(backPointers[id]);
            removed.Add(id);
        }

        var accessor = segment.CreateChunkAccessor();
        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(CellClusterTree.Variant);
            for (int id = 1; id <= ClusterCount; id++)
            {
                if (removed.Contains(id))
                {
                    Assert.That(SpatialRTree<TransientStore>.IsNullHandle(backPointers[id]), Is.True, $"removed cluster {id} must have a retired handle");
                    continue;
                }

                var (leaf, slot) = SpatialRTree<TransientStore>.UnpackHandle(backPointers[id]);
                byte* nodeBase = accessor.GetChunkAddress(leaf);
                Assert.That(SpatialNodeHelper.IsLeaf(nodeBase), Is.True, $"cluster {id}'s handle names chunk {leaf}, which is not a leaf");
                Assert.That(slot, Is.LessThan(SpatialNodeHelper.GetCount(nodeBase)), $"cluster {id}'s slot {slot} is outside its leaf's live range");
                Assert.That(SpatialNodeHelper.ReadLeafEntityId(nodeBase, slot, desc), Is.EqualTo((long)id), $"cluster {id}'s handle names another payload");
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }
}

using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[TestFixture]
[NonParallelizable]
class ClusterSpatialAabbRecomputeTests : TestBase<ClusterSpatialAabbRecomputeTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClCohUnit>.Touch();
    }

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngineWithGrid(float cellSize = 100f, float worldMax = 1000f)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(worldMax, worldMax),
            cellSize: cellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState GetClusterState(DatabaseEngine dbe)
    {
        var meta = Archetype<ClCohUnit>.Metadata;
        return dbe._archetypeStates[meta.ArchetypeId].ClusterState;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RebuildClusterAabbs
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RebuildClusterAabbs_FreshEngineWithNoEntities_IsNoOp()
    {
        using var dbe = SetupEngineWithGrid();
        var cs = GetClusterState(dbe);

        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        // No clusters → no AABBs allocated.
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(0));
        // ClusterAabbs may still be null (early-return path).
    }

    [Test]
    public void RebuildClusterAabbs_SingleEntity_ComputesTightAabbMatchingEntityBounds()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        var cs = GetClusterState(dbe);

        // Simulate a fresh startup: call RebuildClusterAabbs explicitly. The production path calls it
        // inside InitializeArchetypes at reopen time; calling it again here is idempotent for a single
        // entity (it re-derives the same AABB from the same position).
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        Assert.That(cs.ClusterAabbs, Is.Not.Null, "ClusterAabbs should be allocated after rebuild");

        int chunkId = cs.ActiveClusterIds[0];
        ClusterSpatialAabb aabb = cs.ClusterAabbs[chunkId];
        Assert.That(aabb.MinX, Is.EqualTo(50f));
        Assert.That(aabb.MinY, Is.EqualTo(50f));
        Assert.That(aabb.MaxX, Is.EqualTo(50f));
        Assert.That(aabb.MaxY, Is.EqualTo(50f));
        Assert.That(aabb.CategoryMask, Is.EqualTo(uint.MaxValue),
            "Phase 1 default category mask is uint.MaxValue (every query matches)");
    }

    [Test]
    public void RebuildClusterAabbs_MultipleEntitiesInSameCluster_UnionsAllBounds()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn 5 entities in the same cell → all go to the same cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(10f, 20f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(30f, 40f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 15f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(25f, 70f)));
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(45f, 55f)));
            tx.Commit();
        }

        var cs = GetClusterState(dbe);
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        Assert.That(cs.ActiveClusterCount, Is.EqualTo(1),
            "all 5 entities should be in the same cluster (same cell)");
        int chunkId = cs.ActiveClusterIds[0];
        ClusterSpatialAabb aabb = cs.ClusterAabbs[chunkId];

        // Tight union of the 5 points:
        //   x: 10..50, y: 15..70
        Assert.That(aabb.MinX, Is.EqualTo(10f));
        Assert.That(aabb.MinY, Is.EqualTo(15f));
        Assert.That(aabb.MaxX, Is.EqualTo(50f));
        Assert.That(aabb.MaxY, Is.EqualTo(70f));
    }

    [Test]
    public void RebuildClusterAabbs_EntitiesInMultipleCells_AllocatesPerCellIndex()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        // 3 cells: (0,0), (1,0), (0,1). Each gets its own cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));   // cell (0,0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 50f)));  // cell (1,0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 150f)));  // cell (0,1)
            tx.Commit();
        }

        var cs = GetClusterState(dbe);
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        Assert.That(cs.ActiveClusterCount, Is.EqualTo(3),
            "three distinct cells → three clusters");
        Assert.That(cs.PerCellIndex, Is.Not.Null);

        int nonNullSlots = 0;
        int totalClustersInIndex = 0;
        for (int i = 0; i < cs.PerCellIndex.Length; i++)
        {
            if (cs.PerCellIndex[i] != null && cs.PerCellIndex[i].DynamicIndex != null)
            {
                nonNullSlots++;
                totalClustersInIndex += cs.PerCellIndex[i].DynamicIndex.ClusterCount;
            }
        }
        Assert.That(nonNullSlots, Is.EqualTo(3), "three cells should have per-cell spatial slots");
        Assert.That(totalClustersInIndex, Is.EqualTo(3), "each cell holds exactly one cluster");
    }

    [Test]
    public void RebuildClusterAabbs_BackPointersConsistentWithPerCellIndexSlots()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));   // cell (0,0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 150f))); // cell (1,1)
            tx.Commit();
        }

        var cs = GetClusterState(dbe);
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

        // For each active cluster, verify the back-pointer correctly locates it in its cell's DynamicIndex.
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int chunkId = cs.ActiveClusterIds[i];
            int backPointer = cs.ClusterSpatialIndexSlot[chunkId];
            Assert.That(backPointer, Is.GreaterThanOrEqualTo(0),
                $"cluster {chunkId} must have a valid back-pointer into its cell's DynamicIndex");

            int cellKey = cs.ClusterCellMap[chunkId];
            var dynamicIndex = cs.PerCellIndex[cellKey].DynamicIndex;
            Assert.That(dynamicIndex.ClusterIds[backPointer], Is.EqualTo(chunkId),
                $"back-pointer must resolve to the same cluster id " +
                $"(chunkId={chunkId}, backPointer={backPointer})");
        }
    }

    [Test]
    public void RebuildClusterAabbs_WithoutGridOptIn_IsNoOp()
    {
        // Fresh engine, NO ConfigureSpatialGrid call.
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.InitializeArchetypes();

        try
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
                tx.Commit();
            }

            var cs = GetClusterState(dbe);
            using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildClusterAabbs();
        }

            // Without grid opt-in, per-cell index should not be populated.
            // (ClusterAabbs might get allocated because SpatialSlot.Tree is non-null, but PerCellIndex
            // allocation is driven by AddClusterToPerCellIndex which we skip in the no-grid path.)
            // The safer assertion: PerCellIndex remains null (we never called AddClusterToPerCellIndex).
            Assert.That(cs.PerCellIndex, Is.Null);
        }
        finally
        {
            dbe.Dispose();
        }
    }

}

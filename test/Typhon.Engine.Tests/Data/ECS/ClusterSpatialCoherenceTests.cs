using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only components for spatial coherence tests (issue #229 Phase 1+2).
// Uses AABB2F (the only field type supported by the Phase 1+2 spatial grid).
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClCoh.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClCohPos
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    [Field]
    public float Mass;
}

[Archetype(840)]
partial class ClCohUnit : Archetype<ClCohUnit>
{
    public static readonly Comp<ClCohPos> Pos = Register<ClCohPos>();
}

// Secondary spatial archetype used only by the multi-archetype guard test.
[Component("Typhon.Test.ClCoh.Pos2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClCohPos2
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Archetype(841)]
partial class ClCohUnit2 : Archetype<ClCohUnit2>
{
    public static readonly Comp<ClCohPos2> Pos = Register<ClCohPos2>();
}

[TestFixture]
[NonParallelizable]
class ClusterSpatialCoherenceTests : TestBase<ClusterSpatialCoherenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClCohUnit>.Touch();
        Archetype<ClCohUnit2>.Touch();
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

    private DatabaseEngine SetupEngineWithoutGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        // Deliberately no ConfigureSpatialGrid — spatial archetype falls back to legacy ClaimSlot path.
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Grid configuration + opt-in behaviour
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConfigureSpatialGrid_AfterInitializeArchetypes_Throws()
    {
        using var dbe = SetupEngineWithGrid();
        Assert.Throws<System.InvalidOperationException>(() =>
            dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector2(0, 0), new Vector2(500, 500), 50f)));
    }

    [Test]
    public void SpatialArchetype_WithoutGridConfig_UsesLegacySpawnPath()
    {
        using var dbe = SetupEngineWithoutGrid();
        Assert.That(dbe.SpatialGrid, Is.Null);

        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f)));
        tx.Commit();

        // Legacy ClaimSlot path: ClusterCellMap stays null (opt-in feature)
        var meta = Archetype<ClCohUnit>.Metadata;
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(clusterState.ClusterCellMap, Is.Null,
            "Spatial archetype without configured grid must NOT allocate ClusterCellMap");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn placement — same cell, different cells, overflow
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_ManyEntitiesInSameCell_LandInSameCluster()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            // All positions inside cell (1, 2) — world (100..200, 200..300)
            for (int i = 0; i < 5; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f + i, 250f)));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        // All 5 entities landed in a single cluster (cluster size is much larger than 5)
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(1));

        // That cluster is attached to exactly one cell (cell (1, 2))
        int expectedCellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        ref var cell = ref dbe.SpatialGrid.GetCell(expectedCellKey);
        Assert.That(cell.EntityCount, Is.EqualTo(5));
        Assert.That(cell.ClusterCount, Is.EqualTo(1));

        // And the cluster-cell map agrees
        int chunkId = clusterState.ActiveClusterIds[0];
        Assert.That(clusterState.ClusterCellMap[chunkId], Is.EqualTo(expectedCellKey));
    }

    [Test]
    public void Spawn_InDifferentCells_LandInDifferentClusters()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));      // cell (0, 0)
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(550f, 350f)));    // cell (5, 3)
            tx.Commit();
        }

        // Two clusters, each in its own cell with one entity.
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(2));

        int cellA = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int cellB = dbe.SpatialGrid.WorldToCellKey(550f, 350f);
        Assert.That(cellA, Is.Not.EqualTo(cellB));
        Assert.That(dbe.SpatialGrid.GetCell(cellA).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellA).EntityCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellB).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellB).EntityCount, Is.EqualTo(1));

        // Active clusters belong to exactly these two cells (order undefined)
        int c0 = clusterState.ActiveClusterIds[0];
        int c1 = clusterState.ActiveClusterIds[1];
        var mapped = new System.Collections.Generic.HashSet<int>
        {
            clusterState.ClusterCellMap[c0],
            clusterState.ClusterCellMap[c1],
        };
        Assert.That(mapped, Is.EquivalentTo(new[] { cellA, cellB }));
    }

    [Test]
    public void Spawn_BeyondClusterCapacity_AllocatesSecondClusterInSameCell()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;
        int clusterSize = meta.ClusterLayout.ClusterSize;

        using (var tx = dbe.CreateQuickTransaction())
        {
            // Spawn enough entities to overflow one cluster inside a single cell (50, 50)
            for (int i = 0; i < clusterSize + 3; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            }
            tx.Commit();
        }

        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(clusterState.ActiveClusterCount, Is.EqualTo(2),
            "overflowing one cluster should allocate a second one in the same cell");

        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        ref var cell = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cell.ClusterCount, Is.EqualTo(2));
        Assert.That(cell.EntityCount, Is.EqualTo(clusterSize + 3));

        // Both clusters are mapped to the same cell
        int c0 = clusterState.ActiveClusterIds[0];
        int c1 = clusterState.ActiveClusterIds[1];
        Assert.That(clusterState.ClusterCellMap[c0], Is.EqualTo(cellKey));
        Assert.That(clusterState.ClusterCellMap[c1], Is.EqualTo(cellKey));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy — cell state maintenance
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_LastEntityInCluster_RemovesClusterFromCell()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f)));
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).ClusterCount, Is.EqualTo(1));
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(1));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        ref var cellAfter = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cellAfter.ClusterCount, Is.EqualTo(0), "empty cluster must detach from its cell");
        Assert.That(cellAfter.EntityCount, Is.EqualTo(0));
    }

    [Test]
    public void Destroy_OneOfManyEntities_DecrementsCellCount_KeepsCluster()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        EntityId toKill;
        using (var tx = dbe.CreateQuickTransaction())
        {
            toKill = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            for (int i = 0; i < 4; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f + i, 50f)));
            }
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(5));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(toKill);
            tx.Commit();
        }

        ref var cellAfter = ref dbe.SpatialGrid.GetCell(cellKey);
        Assert.That(cellAfter.EntityCount, Is.EqualTo(4));
        Assert.That(cellAfter.ClusterCount, Is.EqualTo(1), "cluster still contains other entities, must stay");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Validation of unsupported spatial field types
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ValidateSupportedFieldType_AABB2F_Accepted()
    {
        // Positive control — ClCohUnit uses AABB2F and the engine initialised fine in earlier tests.
        // This explicit check documents the supported set.
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB2F, "MyArch");
        SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere2F, "MyArch");
    }

    [Test]
    public void ValidateSupportedFieldType_3DVariants_Throw()
    {
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB3F, "MyArch"));
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.AABB3D, "MyArch"));
        Assert.Throws<System.NotSupportedException>(
            () => SpatialGrid.ValidateSupportedFieldType(SpatialFieldType.BSphere3F, "MyArch"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reopen — RebuildCellState reconstructs the mapping from persisted data
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reopen_RebuildsClusterCellMap_FromPersistedData()
    {
        var dbName = $"T_ClusterCellRebuild_{Environment.ProcessId}";

        int cellKey1, cellKey2;
        int chunkId1, chunkId2;

        // Session 1: spawn entities in two distinct cells, note the cluster→cell mapping
        {
            using var dbe = CreateNamedEngineWithGrid(dbName);
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f))); // cell (1, 2)
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(550f, 750f))); // cell (5, 7)
                tx.Commit();
            }

            cellKey1 = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
            cellKey2 = dbe.SpatialGrid.WorldToCellKey(550f, 750f);

            var meta = Archetype<ClCohUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            Assert.That(cs.ActiveClusterCount, Is.EqualTo(2));

            // Record which cluster chunk IDs correspond to which cell
            chunkId1 = chunkId2 = 0;
            for (int i = 0; i < cs.ActiveClusterCount; i++)
            {
                int id = cs.ActiveClusterIds[i];
                if (cs.ClusterCellMap[id] == cellKey1) { chunkId1 = id; }
                else if (cs.ClusterCellMap[id] == cellKey2) { chunkId2 = id; }
            }
            Assert.That(chunkId1, Is.GreaterThan(0));
            Assert.That(chunkId2, Is.GreaterThan(0));
        }

        // Session 2: reopen, verify cell state reconstructed by RebuildCellState
        {
            using var dbe = CreateNamedEngineWithGrid(dbName);
            var meta = Archetype<ClCohUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

            Assert.That(cs.ActiveClusterCount, Is.EqualTo(2), "active clusters preserved after reopen");
            Assert.That(cs.ClusterCellMap, Is.Not.Null, "ClusterCellMap rebuilt on reopen");
            Assert.That(cs.ClusterCellMap[chunkId1], Is.EqualTo(cellKey1),
                "cluster 1 must re-attach to its original cell");
            Assert.That(cs.ClusterCellMap[chunkId2], Is.EqualTo(cellKey2),
                "cluster 2 must re-attach to its original cell");

            // Each cell has one cluster with one entity after rebuild
            Assert.That(dbe.SpatialGrid.GetCell(cellKey1).ClusterCount, Is.EqualTo(1));
            Assert.That(dbe.SpatialGrid.GetCell(cellKey1).EntityCount, Is.EqualTo(1));
            Assert.That(dbe.SpatialGrid.GetCell(cellKey2).ClusterCount, Is.EqualTo(1));
            Assert.That(dbe.SpatialGrid.GetCell(cellKey2).EntityCount, Is.EqualTo(1));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Guard tests for code-review fixes
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Multiple_Spatial_Archetypes_With_Grid_Throws()
    {
        // Registering two spatial archetypes with a SpatialGrid configured must fail fast because
        // Phase 1+2's per-cell cluster list is shared across archetypes (design doc Decision Q10
        // follow-up). Fix #1 from the code review.
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.RegisterComponentFromAccessor<ClCohPos2>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));

        var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
        Assert.That(ex.Message, Does.Contain("at most one spatial archetype"));
        dbe.Dispose();
    }

    [Test]
    public void Multiple_Spatial_Archetypes_Without_Grid_Succeeds()
    {
        // Without a configured grid, the single-archetype limitation doesn't apply — both
        // archetypes run on the legacy per-entity R-Tree path.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.RegisterComponentFromAccessor<ClCohPos2>();
        Assert.DoesNotThrow(() => dbe.InitializeArchetypes());
        Assert.That(dbe.SpatialGrid, Is.Null);
    }

    [Test]
    public unsafe void ReleaseSlot_OnAlreadyEmptySlot_DoesNotUnderflowEntityCount()
    {
        // Spawn one entity, then directly call ReleaseSlot on a slot that was never occupied.
        // The wasOccupied guard (Fix #3) should leave cell.EntityCount untouched.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<ClCohUnit>.Metadata;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 250f)));
            tx.Commit();
        }

        int cellKey = dbe.SpatialGrid.WorldToCellKey(150f, 250f);
        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(1));

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int chunkId = cs.ActiveClusterIds[0];

        // Pick a slot that was never occupied. The spawned entity is in slot 0; slot 5 is empty.
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            var changeSet = dbe.MMF.CreateChangeSet();
            var accessor = cs.ClusterSegment.CreateChunkAccessor(changeSet);
            try
            {
                cs.ReleaseSlot(ref accessor, chunkId, slotIndex: 5, changeSet, dbe.SpatialGrid);
            }
            finally
            {
                accessor.Dispose();
                changeSet.SaveChanges();
            }
        }

        Assert.That(dbe.SpatialGrid.GetCell(cellKey).EntityCount, Is.EqualTo(1),
            "releasing a never-occupied slot must not decrement EntityCount");
    }

    private static DatabaseEngine CreateNamedEngineWithGrid(string dbName)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = dbName;
              o.DatabaseCacheSize = (ulong)(50 * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine(o => { o.Wal = null; });

        var sp = sc.BuildServiceProvider();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }
}

using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only spatial SV components for cluster spatial integration (Phase 3b)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClSp.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialPos
{
    [Field]
    [SpatialIndex(5.0f)]
    public AABB3F Bounds;

    [Field]
    public float Speed;
}

[Component("Typhon.Test.ClSp.Meta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialMeta
{
    [Field]
    public long Tag;
}

[Archetype(830)]
partial class ClSpatialUnit : Archetype<ClSpatialUnit>
{
    public static readonly Comp<ClSpatialPos> Pos = Register<ClSpatialPos>();
    public static readonly Comp<ClSpatialMeta> Meta = Register<ClSpatialMeta>();
}

// Non-cluster archetype sharing the same spatial component (Versioned → not cluster-eligible)
[Component("Typhon.Test.ClSp.VData", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialVData
{
    [Field]
    public int Value;
    [Field]
    public int Padding;
}

// Static spatial component for Phase 3c test
[Component("Typhon.Test.ClSp.StaticPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClSpatialStaticPos
{
    [Field]
    [SpatialIndex(0.0f, Mode = SpatialMode.Static)]
    public AABB3F Bounds;
}

[Archetype(831)]
partial class ClSpatialNonClusterUnit : Archetype<ClSpatialNonClusterUnit>
{
    public static readonly Comp<ClSpatialPos> Pos = Register<ClSpatialPos>();
    public static readonly Comp<ClSpatialVData> VData = Register<ClSpatialVData>();
}

[Archetype(832)]
partial class ClSpatialStaticUnit : Archetype<ClSpatialStaticUnit>
{
    public static readonly Comp<ClSpatialStaticPos> StaticPos = Register<ClSpatialStaticPos>();
    public static readonly Comp<ClSpatialMeta> Meta = Register<ClSpatialMeta>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: Per-archetype Spatial R-Tree integration with cluster storage
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class ClusterSpatialTests : TestBase<ClusterSpatialTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClSpatialUnit>.Touch();
        Archetype<ClSpatialNonClusterUnit>.Touch();
        Archetype<ClSpatialStaticUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.RegisterComponentFromAccessor<ClSpatialVData>();
        dbe.RegisterComponentFromAccessor<ClSpatialStaticPos>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ClSpatialPos MakePos(float x, float y, float z, float size = 1.0f, float speed = 0f) =>
        new() { Bounds = new AABB3F { MinX = x - size, MinY = y - size, MinZ = z - size, MaxX = x + size, MaxY = y + size, MaxZ = z + size }, Speed = speed };

    // ═══════════════════════════════════════════════════════════════════════
    // Infrastructure verification
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ClusterEligible_DynamicSpatial_HasClusterSpatialTrue()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClSpatialUnit>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Dynamic SV spatial archetype should be cluster-eligible");
        Assert.That(meta.HasClusterSpatial, Is.True, "Should have per-archetype spatial");

        var es = dbe._archetypeStates[meta.ArchetypeId];
        Assert.That(es.ClusterState, Is.Not.Null);
        Assert.That(es.ClusterState.SpatialSlot.Tree, Is.Not.Null, "Per-archetype R-Tree should exist");
        Assert.That(es.ClusterState.SpatialSlot.BackPointerSegment, Is.Not.Null, "Per-archetype BP segment should exist");
        Assert.That(es.ClusterState.ClusterDirtyRing, Is.Not.Null, "Per-archetype DirtyBitmapRing should exist");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_WithSpatialField_PerArchetypeRTreeContainsEntry()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = MakePos(10, 20, 30);
        var met = new ClSpatialMeta { Tag = 42 };
        tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        tx.Commit();

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(1), "Per-archetype R-Tree should have 1 entry");
    }

    [Test]
    public void Spawn_MultipleEntities_AllInTree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        for (int i = 0; i < 50; i++)
        {
            var pos = MakePos(i * 10, 0, 0);
            var met = new ClSpatialMeta { Tag = i };
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        }
        tx.Commit();

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(50));
    }

    [Test]
    public void Destroy_RemovesFromPerArchetypeRTree()
    {
        using var dbe = SetupEngine();
        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 20, 30);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(1));

        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Destroy(id);
            tx.Commit();
        }

        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(0), "Destroyed entity removed from R-Tree");
    }

    [Test]
    public void SharedRTree_EmptyAfterClusterEntitySpawn()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = MakePos(10, 20, 30);
        var met = new ClSpatialMeta { Tag = 1 };
        tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        tx.Commit();

        var table = dbe.GetComponentTable<ClSpatialPos>();
        Assert.That(table.SpatialIndex, Is.Not.Null);
        Assert.That(table.SpatialIndex.DynamicTree.EntityCount, Is.EqualTo(0),
            "Shared per-table R-Tree should be empty — cluster entities use per-archetype tree");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial Query
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpatialQuery_AABB_ReturnsClusterEntities()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = MakePos(10, 10, 10);
            var pos2 = MakePos(100, 100, 100);
            var met = new ClSpatialMeta { Tag = 1 };
            id1 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos2), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query near (10,10,10) should find id1 but not id2
        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 20, 20, 20).Execute();
        Assert.That(results, Does.Contain(id1));
        Assert.That(results, Does.Not.Contain(id2));
    }

    [Test]
    public void SpatialQuery_Radius_ReturnsClusterEntities()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = MakePos(5, 5, 5);
            var pos2 = MakePos(500, 500, 500);
            var met = new ClSpatialMeta { Tag = 1 };
            id1 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos2), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(5, 5, 5, 15).Execute();
        Assert.That(results, Does.Contain(id1));
        Assert.That(results, Does.Not.Contain(id2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tick Fence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TickFence_MovedEntity_FatAABBEscape_RTreeUpdated()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Write new position far away (escapes fat AABB)
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(200, 200, 200);
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Query at new position should find entity
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(190, 190, 190, 210, 210, 210).Execute();
            Assert.That(results, Does.Contain(id), "Entity should be findable at new position after tick fence");
        }

        // Query at old position should NOT find entity
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 20, 20, 20).Execute();
            Assert.That(results, Does.Not.Contain(id), "Entity should NOT be at old position after tick fence");
        }
    }

    [Test]
    public void TickFence_SmallMove_NoEscape_FastPath()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Write small move (within margin=5 → fat AABB [4,4,4,16,16,16])
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(11, 11, 11); // +1 from center, within fat AABB
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Entity should still be queryable
        using var tx2 = dbe.CreateQuickTransaction();
        var results = tx2.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(11, 11, 11, 10).Execute();
        Assert.That(results, Does.Contain(id));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnAndDestroySameTx_NoSpatialLeak()
    {
        using var dbe = SetupEngine();

        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            var id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Destroy(id);
            tx.Commit();
        }

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(0), "Spawn+destroy in same tx: no R-Tree leak");
    }

    [Test]
    public void BulkSpawn_100Entities_AllInTree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        for (int i = 0; i < 100; i++)
        {
            var pos = MakePos(i * 20, 0, 0, 2.0f);
            var met = new ClSpatialMeta { Tag = i };
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
        }
        tx.Commit();

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(100));
    }

    [Test]
    public void TickFence_DestroyedEntity_NoCrash()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(10, 10, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Write then destroy in separate tx
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(200, 200, 200);
            tx.Destroy(id);
            tx.Commit();
        }

        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(0));
    }

    [Test]
    public void MultipleSpawnAndDestroy_TreeCountCorrect()
    {
        using var dbe = SetupEngine();
        var ids = new EntityId[10];

        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 10; i++)
            {
                var pos = MakePos(i * 20, 0, 0);
                var met = new ClSpatialMeta { Tag = i };
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            }
            tx.Commit();
        }

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(10));

        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 5; i++)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(5));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mixed cluster + non-cluster archetypes sharing same spatial component
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Mixed_ClusterAndNonCluster_BothQueryable()
    {
        using var dbe = SetupEngine();

        // Both archetypes are cluster-eligible since Phase 5 (mixed SV+Versioned allowed)
        var metaCluster = Archetype<ClSpatialUnit>.Metadata;
        var metaNonCluster = Archetype<ClSpatialNonClusterUnit>.Metadata;
        Assert.That(metaCluster.HasClusterSpatial, Is.True);
        Assert.That(metaNonCluster.IsClusterEligible, Is.True); // Phase 5: SV+Versioned → cluster-eligible

        EntityId clusterEntityId, nonClusterEntityId;
        {
            using var tx = dbe.CreateQuickTransaction();
            // Cluster entity at (10,10,10)
            var pos1 = MakePos(10, 10, 10);
            var met1 = new ClSpatialMeta { Tag = 1 };
            clusterEntityId = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met1));

            // Non-cluster entity at (20,20,20) — same ClSpatialPos component, different archetype
            var pos2 = MakePos(20, 20, 20);
            var vd = new ClSpatialVData { Value = 42 };
            nonClusterEntityId = tx.Spawn<ClSpatialNonClusterUnit>(ClSpatialNonClusterUnit.Pos.Set(in pos2), ClSpatialNonClusterUnit.VData.Set(in vd));
            tx.Commit();
        }

        // Phase 5: both archetypes are cluster-eligible — both use per-archetype spatial trees
        var cs = dbe._archetypeStates[metaCluster.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(1), "Per-archetype tree should have cluster entity");

        var csNonCluster = dbe._archetypeStates[metaNonCluster.ArchetypeId].ClusterState;
        Assert.That(csNonCluster.SpatialSlot.Tree.EntityCount, Is.EqualTo(1), "Per-archetype tree should have non-cluster entity (now cluster-eligible)");

        // Spatial query covering both should find BOTH entities
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 30, 30, 30).Execute();
            Assert.That(results, Does.Contain(clusterEntityId), "Cluster entity should be found");
            // Note: non-cluster entity is a different archetype, Query<ClSpatialUnit> filters by archetype mask
        }

        // Query for non-cluster archetype should find only its entity via per-archetype tree
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialNonClusterUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 30, 30, 30).Execute();
            Assert.That(results, Does.Contain(nonClusterEntityId), "Non-cluster entity should be found");
            Assert.That(results, Does.Not.Contain(clusterEntityId), "Cluster entity should NOT be in non-cluster query");
        }
    }

    [Test]
    public void Mixed_TriggerRegion_DetectsEntitiesFromBothPaths()
    {
        using var dbe = SetupEngine();

        var metaCluster = Archetype<ClSpatialUnit>.Metadata;
        var metaNonCluster = Archetype<ClSpatialNonClusterUnit>.Metadata;

        // Spawn one entity per archetype in the same spatial region
        EntityId clusterEntityId, nonClusterEntityId;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = MakePos(10, 10, 10);
            var met1 = new ClSpatialMeta { Tag = 1 };
            clusterEntityId = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met1));

            var pos2 = MakePos(15, 15, 15);
            var vd = new ClSpatialVData { Value = 1 };
            nonClusterEntityId = tx.Spawn<ClSpatialNonClusterUnit>(ClSpatialNonClusterUnit.Pos.Set(in pos2), ClSpatialNonClusterUnit.VData.Set(in vd));
            tx.Commit();
        }

        // Create trigger region covering both entities
        var table = dbe.GetComponentTable<ClSpatialPos>();
        var triggerSystem = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        double[] regionBounds = { 0, 0, 0, 30, 30, 30 };
        var handle = triggerSystem.CreateRegion(regionBounds);

        // First evaluation: both entities should enter
        var result = triggerSystem.EvaluateRegion(handle, 1);
        Assert.That(result.Entered.Length, Is.EqualTo(2), "Both cluster and non-cluster entities should enter the region");

        // Second evaluation: both should stay (no change)
        var result2 = triggerSystem.EvaluateRegion(handle, 2);
        Assert.That(result2.Entered.Length, Is.EqualTo(0), "No new entries on second eval");
        Assert.That(result2.StayCount, Is.EqualTo(2), "Both entities should stay");

        triggerSystem.DestroyRegion(handle);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Back-pointer swap consistency under bulk remove
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BulkRemove_BackPointerConsistency_AllRemainingQueryable()
    {
        using var dbe = SetupEngine();
        var ids = new EntityId[50];

        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 50; i++)
            {
                var pos = MakePos(i * 20, 0, 0, 2.0f);
                var met = new ClSpatialMeta { Tag = i };
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            }
            tx.Commit();
        }

        var meta = Archetype<ClSpatialUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(50));

        // Remove every other entity (forces many swap-on-remove operations)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 50; i += 2)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(25));

        // Every surviving entity should be queryable at its correct position
        for (int i = 1; i < 50; i += 2)
        {
            float x = i * 20;
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>()
                .WhereInAABB<ClSpatialPos>(x - 10, -10, -10, x + 10, 10, 10).Execute();
            Assert.That(results, Does.Contain(ids[i]), $"Entity {i} at x={x} should be queryable after bulk remove");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Static spatial mode (Phase 3c)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void StaticSpatial_ClusterEligible_SpawnAndQuery()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<ClSpatialStaticUnit>.Metadata;
        Assert.That(meta.IsClusterEligible, Is.True, "Static SV spatial archetype should be cluster-eligible");
        Assert.That(meta.HasClusterSpatial, Is.True);

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.Tree, Is.Not.Null, "Per-archetype R-Tree should exist for Static spatial");

        // Spawn static entities
        EntityId id1, id2;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos1 = new ClSpatialStaticPos { Bounds = new AABB3F { MinX = 10, MinY = 10, MinZ = 10, MaxX = 12, MaxY = 12, MaxZ = 12 } };
            var pos2 = new ClSpatialStaticPos { Bounds = new AABB3F { MinX = 50, MinY = 50, MinZ = 50, MaxX = 52, MaxY = 52, MaxZ = 52 } };
            var met = new ClSpatialMeta { Tag = 1 };
            id1 = tx.Spawn<ClSpatialStaticUnit>(ClSpatialStaticUnit.StaticPos.Set(in pos1), ClSpatialStaticUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialStaticUnit>(ClSpatialStaticUnit.StaticPos.Set(in pos2), ClSpatialStaticUnit.Meta.Set(in met));
            tx.Commit();
        }

        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(2));

        // Query should find both
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialStaticUnit>().WhereInAABB<ClSpatialStaticPos>(0, 0, 0, 60, 60, 60).Execute();
            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results, Does.Contain(id1));
            Assert.That(results, Does.Contain(id2));
        }

        // Tick fence should NOT process static spatial (no crash, no tree modification)
        dbe.WriteTickFence(1);
        Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(2), "Static tree unaffected by tick fence");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Database reopen with persisted spatial data
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reopen_PerArchetypeSpatialLoaded_QueryWorks()
    {
        var dbName = $"T_ClSpatialReopen_{Environment.ProcessId}";
        EntityId id1, id2;

        // Session 1: create database, spawn entities
        {
            using var dbe = CreateNamedEngine(dbName);
            var pos1 = MakePos(10, 10, 10);
            var pos2 = MakePos(50, 50, 50);
            var met = new ClSpatialMeta { Tag = 1 };
            using var tx = dbe.CreateQuickTransaction();
            id1 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos1), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            id2 = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos2), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();

            var meta = Archetype<ClSpatialUnit>.Metadata;
            Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.SpatialSlot.Tree.EntityCount, Is.EqualTo(2));
        }

        // Session 2: reopen, verify spatial query works
        {
            using var dbe = CreateNamedEngine(dbName);
            var meta = Archetype<ClSpatialUnit>.Metadata;
            var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            Assert.That(cs, Is.Not.Null, "ClusterState should exist after reopen");
            Assert.That(cs.SpatialSlot.Tree, Is.Not.Null, "Per-archetype R-Tree should exist after reopen");
            Assert.That(cs.SpatialSlot.Tree.EntityCount, Is.EqualTo(2), "R-Tree should have 2 entities after reopen");

            // Spatial query should find both entities
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 60, 60, 60).Execute();
            Assert.That(results.Count, Is.EqualTo(2), "Both entities should be queryable after reopen");
        }
    }

    private DatabaseEngine CreateNamedEngine(string dbName)
    {
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Critical))
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
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.RegisterComponentFromAccessor<ClSpatialVData>();
        dbe.RegisterComponentFromAccessor<ClSpatialStaticPos>();
        dbe.InitializeArchetypes();
        return dbe;
    }
}

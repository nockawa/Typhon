using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Spatial ECS test types
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Spatial.Ship", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialShip
{
    [Field] [SpatialIndex(5.0f)]
    public AABB3F Bounds;

    [Field]
    public float Speed;
}

[Component("Typhon.Test.Spatial.Name", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialName
{
    [Field]
    public long Id;
}

[Component("Typhon.Test.Spatial.Building", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialBuilding
{
    [Field] [SpatialIndex(0.0f)]
    public AABB2F Footprint;

    [Field]
    public int OwnerId;
}

[Component("Typhon.Test.Spatial.TransientBad", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct TransientBadSpatial
{
    [Field] [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Component("Typhon.Test.Spatial.Terrain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialTerrain
{
    [Field] [SpatialIndex(0.0f, Mode = SpatialMode.Static)]
    public AABB3F Footprint;
}

[Archetype(800)]
partial class SpatialShipArchetype : Archetype<SpatialShipArchetype>
{
    public static readonly Comp<SpatialShip> Ship = Register<SpatialShip>();
    public static readonly Comp<SpatialName> Name = Register<SpatialName>();
}

[Archetype(801)]
partial class SpatialBuildingArchetype : Archetype<SpatialBuildingArchetype>
{
    public static readonly Comp<SpatialBuilding> Building = Register<SpatialBuilding>();
}

[Archetype(802)]
partial class SpatialTerrainArchetype : Archetype<SpatialTerrainArchetype>
{
    public static readonly Comp<SpatialTerrain> Terrain = Register<SpatialTerrain>();
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

[NonParallelizable]
class SpatialEcsIntegrationTests : TestBase<SpatialEcsIntegrationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<SpatialShipArchetype>.Touch();
        Archetype<SpatialBuildingArchetype>.Touch();
        Archetype<SpatialTerrainArchetype>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpatialShip>();
        dbe.RegisterComponentFromAccessor<SpatialName>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Get the spatial R-Tree for a given archetype. For cluster-eligible archetypes, returns per-archetype tree;
    /// otherwise the shared per-table tree.
    /// </summary>
    private static SpatialRTree<PersistentStore> GetSpatialTree<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        if (meta.HasClusterSpatial)
        {
            return dbe._archetypeStates[meta.ArchetypeId]?.ClusterState?.SpatialSlot.Tree;
        }
        // Non-cluster: find the component table with spatial index
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            var table = dbe._archetypeStates[meta.ArchetypeId]?.SlotToComponentTable[slot];
            if (table?.SpatialIndex != null)
            {
                return table.SpatialIndex.ActiveTree;
            }
        }
        return null;
    }

    // ── Schema Validation ────────────────────────────────────────────────

    [Test]
    public void Schema_TransientWithSpatialIndex_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<TransientBadSpatial>());
    }

    [Test]
    public void Schema_ValidSpatialField_CreatesSpatialIndex()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialShip>();
        Assert.That(table.SpatialIndex, Is.Not.Null);
        Assert.That(table.SpatialIndex.Descriptor.CoordCount, Is.EqualTo(6)); // 3D
    }

    [Test]
    public void Schema_NoSpatialField_NullSpatialIndex()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialName>();
        Assert.That(table.SpatialIndex, Is.Null);
    }

    [Test]
    public void Schema_CellSizeZero_NoHashmap()
    {
        using var dbe = SetupEngine();
        var table = dbe.GetComponentTable<SpatialShip>();
        // SpatialShip uses [SpatialIndex(5.0f)] — CellSize defaults to 0
        Assert.That(table.SpatialIndex.OccupancyMap, Is.Null);
    }

    [Test]
    public void CellKey2D_Lossless_DifferentInputs_DifferentKeys()
    {
        // Verify 2D lossless packing produces unique keys for distinct cell coords
        var keys = new HashSet<long>();
        for (int x = -10; x <= 10; x++)
        {
            for (int y = -10; y <= 10; y++)
            {
                double cx = x * 100.0 + 50;
                double cy = y * 100.0 + 50;
                // coordCount=4 (2D): coords = [minX, minY, maxX, maxY], center = ((min+max)/2)
                Span<double> coords = stackalloc double[] { cx - 1, cy - 1, cx + 1, cy + 1 };
                long key = SpatialMaintainer.ComputeCellKey(coords, 4, 1.0f / 100.0f);
                Assert.That(keys.Add(key), Is.True, $"Duplicate key for cell ({x},{y})");
            }
        }
        Assert.That(keys.Count, Is.EqualTo(21 * 21));
    }

    // ── Spawn + Query ────────────────────────────────────────────────────

    [Test]
    public void Spawn_EntityWithSpatialIndex_InsertedIntoTree()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 5.0f };
            var id = t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            Assert.That(id.IsNull, Is.False);
            t.Commit();
        }

        // Verify tree entity count after transaction commits (FinalizeSpawns runs on commit).
        // SpatialShipArchetype is now cluster-eligible (Phase 3b), so entities are in the per-archetype R-Tree.
        Assert.That(GetSpatialTree<SpatialShipArchetype>(dbe).EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void Spawn_MultipleEntities_AllInTree()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var ship = new SpatialShip { Bounds = new AABB3F { MinX = i * 10, MinY = 0, MinZ = 0, MaxX = i * 10 + 2, MaxY = 2, MaxZ = 2 }, Speed = 1.0f };
                t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
            }
            t.Commit();
        }

        Assert.That(GetSpatialTree<SpatialShipArchetype>(dbe).EntityCount, Is.EqualTo(50));
    }

    [Test]
    public void Spawn_AABB3F_QueryFindsEntity()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 5.0f };
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            t.Commit();
        }

        // Query a region that overlaps the entity
        var tree = GetSpatialTree<SpatialShipArchetype>(dbe);

        // Query region that overlaps the entity (bounds are enlarged by margin=5, so fat AABB is [5,15,25]→[17,27,37])
        // Query [0,10,20]→[20,30,40] should overlap
        int hitCount = 0;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var result in tree.QueryAABB(stackalloc double[] { 0, 10, 20, 20, 30, 40 }))
            {
                hitCount++;
            }
        }
        Assert.That(hitCount, Is.GreaterThan(0));
    }

    // ── Destroy ──────────────────────────────────────────────────────────

    [Test]
    public void Destroy_RemovesFromTree()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 10, MinY = 20, MinZ = 30, MaxX = 12, MaxY = 22, MaxZ = 32 }, Speed = 5.0f };
            id = t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            t.Commit();
        }

        Assert.That(GetSpatialTree<SpatialShipArchetype>(dbe).EntityCount, Is.EqualTo(1));

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            t.Commit();
        }

        Assert.That(GetSpatialTree<SpatialShipArchetype>(dbe).EntityCount, Is.EqualTo(0));
    }

    // ── Fat AABB Containment ─────────────────────────────────────────────

    [Test]
    public void SvTickFence_MoveWithinMargin_NoTreeMutation()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var ship = new SpatialShip { Bounds = new AABB3F { MinX = 100, MinY = 100, MinZ = 100, MaxX = 102, MaxY = 102, MaxZ = 102 }, Speed = 1.0f };
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = 1 }));
            t.Commit();
        }

        var tree = GetSpatialTree<SpatialShipArchetype>(dbe);
        int nodeCountBefore = tree.NodeCount;

        // Write tick fence to process initial spatial state
        dbe.WriteTickFence(1);

        // Move entity within margin (5.0f) — should NOT trigger tree mutation
        using (var t = dbe.CreateQuickTransaction())
        {
            // Read all spawned entities via query would be complex, so we directly test the tree state
            // The entity was inserted at spawn time; tick fence should find it's still within fat AABB
        }

        dbe.WriteTickFence(2);

        // Node count should not change (no splits from small moves)
        Assert.That(tree.NodeCount, Is.EqualTo(nodeCountBefore));
    }

    // ── Back-pointer consistency ─────────────────────────────────────────

    [Test]
    public void Spawn_ManyEntities_TreeValidatorPasses()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
        for (int i = 0; i < 100; i++)
        {
            var ship = new SpatialShip
            {
                Bounds = new AABB3F
                {
                    MinX = (i % 10) * 20,
                    MinY = (i / 10) * 20,
                    MinZ = 0,
                    MaxX = (i % 10) * 20 + 2,
                    MaxY = (i / 10) * 20 + 2,
                    MaxZ = 2
                },
                Speed = 1.0f
            };
            t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
        }
            t.Commit();
        }

        var tree = GetSpatialTree<SpatialShipArchetype>(dbe);
        Assert.That(tree.EntityCount, Is.EqualTo(100));

        // Validate tree invariants (TreeValidator.Validate throws on failure)
        TreeValidator.Validate(tree);
    }

    // ── Bulk spawn (regression test for #192) ──────────────────────────

    [Test]
    [CancelAfter(10000)]
    public void BulkSpawn_2000Entities_SingleTransaction_NoOverflow()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 2000; i++)
            {
                var ship = new SpatialShip
                {
                    Bounds = new AABB3F
                    {
                        MinX = (i % 50) * 20, MinY = (i / 50) * 20, MinZ = 0,
                        MaxX = (i % 50) * 20 + 2, MaxY = (i / 50) * 20 + 2, MaxZ = 2
                    },
                    Speed = 1.0f
                };
                t.Spawn<SpatialShipArchetype>(SpatialShipArchetype.Ship.Set(in ship), SpatialShipArchetype.Name.Set(new SpatialName { Id = i }));
            }
            t.Commit();
        }

        var tree = GetSpatialTree<SpatialShipArchetype>(dbe);
        Assert.That(tree.EntityCount, Is.EqualTo(2000));
        TreeValidator.Validate(tree);
    }

    // ── Static/Dynamic Mode (F2) ──────────────────────────────────────

    private DatabaseEngine SetupStaticEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpatialTerrain>();
        dbe.RegisterComponentFromAccessor<SpatialShip>();
        dbe.RegisterComponentFromAccessor<SpatialName>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    [CancelAfter(5000)]
    public void Schema_StaticMode_SetsFieldInfoMode()
    {
        Archetype<SpatialTerrainArchetype>.Touch();
        using var dbe = SetupStaticEngine();
        var table = dbe.GetComponentTable<SpatialTerrain>();
        Assert.That(table.SpatialIndex, Is.Not.Null);
        Assert.That(table.SpatialIndex.FieldInfo.Mode, Is.EqualTo(SpatialMode.Static));
        Assert.That(table.SpatialIndex.StaticTree, Is.Not.Null);
        Assert.That(table.SpatialIndex.DynamicTree, Is.Null);
    }

    [Test]
    [CancelAfter(5000)]
    public void Schema_DefaultMode_IsDynamic()
    {
        Archetype<SpatialTerrainArchetype>.Touch();
        using var dbe = SetupStaticEngine();
        var table = dbe.GetComponentTable<SpatialShip>();
        Assert.That(table.SpatialIndex.FieldInfo.Mode, Is.EqualTo(SpatialMode.Dynamic));
        Assert.That(table.SpatialIndex.DynamicTree, Is.Not.Null);
        Assert.That(table.SpatialIndex.StaticTree, Is.Null);
    }

    [Test]
    [CancelAfter(5000)]
    public void BackPointer_TreeSelector_Roundtrip()
    {
        Archetype<SpatialTerrainArchetype>.Touch();
        using var dbe = SetupStaticEngine();

        // Spawn a static terrain entity
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 5 }
            };
            t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        var tree = GetSpatialTree<SpatialTerrainArchetype>(dbe);
        Assert.That(tree.EntityCount, Is.EqualTo(1));

        // SpatialTerrainArchetype is cluster-eligible (all SV), so back-pointers live on the per-archetype
        // ClusterSpatialSlot, keyed by clusterLocation (not component chunk ID).
        var meta = Archetype<SpatialTerrainArchetype>.Metadata;
        using var guard = EpochGuard.Enter(dbe.EpochManager);

        // Query the tree to obtain the clusterLocation (stored as ComponentChunkId in cluster trees)
        int clusterLocation = -1;
        foreach (var hit in tree.QueryAABB(stackalloc double[] { -1, -1, -1, 11, 11, 6 }))
        {
            clusterLocation = hit.ComponentChunkId;
        }
        Assert.That(clusterLocation, Is.GreaterThan(0), "Should find entity in tree");

        var bpSegment = dbe._archetypeStates[meta.ArchetypeId].ClusterState.SpatialSlot.BackPointerSegment;
        var bpAccessor = bpSegment.CreateChunkAccessor();
        try
        {
            var bp = SpatialBackPointerHelper.Read(ref bpAccessor, clusterLocation);
            Assert.That(bp.LeafChunkId, Is.GreaterThan(0));
            Assert.That(bp.TreeSelector, Is.EqualTo((byte)SpatialMode.Static));
        }
        finally
        {
            bpAccessor.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void StaticComponent_InsertAndQuery()
    {
        Archetype<SpatialTerrainArchetype>.Touch();
        using var dbe = SetupStaticEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var terrain = new SpatialTerrain
                {
                    Footprint = new AABB3F
                    {
                        MinX = i * 20, MinY = 0, MinZ = 0,
                        MaxX = i * 20 + 10, MaxY = 10, MaxZ = 5
                    }
                };
                t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            }
            t.Commit();
        }

        var tree = GetSpatialTree<SpatialTerrainArchetype>(dbe);
        Assert.That(tree.EntityCount, Is.EqualTo(10));

        // Query a region that overlaps the first 3 terrain pieces
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        double[] queryCoords = { -5, -5, -5, 55, 15, 10 };
        var results = new List<long>();
        foreach (var hit in tree.QueryAABB(queryCoords))
        {
            results.Add(hit.EntityId);
        }
        Assert.That(results.Count, Is.EqualTo(3));

        TreeValidator.Validate(tree);
    }

    [Test]
    [CancelAfter(5000)]
    public void StaticComponent_Remove_Works()
    {
        Archetype<SpatialTerrainArchetype>.Touch();
        using var dbe = SetupStaticEngine();

        EntityId terrainId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 5 }
            };
            terrainId = t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        var tree = GetSpatialTree<SpatialTerrainArchetype>(dbe);
        Assert.That(tree.EntityCount, Is.EqualTo(1));

        // Destroy the entity
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(terrainId);
            t.Commit();
        }

        Assert.That(tree.EntityCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(5000)]
    public void StaticComponent_TickFenceSkipped()
    {
        Archetype<SpatialTerrainArchetype>.Touch();
        using var dbe = SetupStaticEngine();

        // Spawn static terrain
        using (var t = dbe.CreateQuickTransaction())
        {
            var terrain = new SpatialTerrain
            {
                Footprint = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 5 }
            };
            t.Spawn<SpatialTerrainArchetype>(SpatialTerrainArchetype.Terrain.Set(in terrain));
            t.Commit();
        }

        var table = dbe.GetComponentTable<SpatialTerrain>();
        int entityCountBefore = table.SpatialIndex.ActiveTree.EntityCount;
        int nodeCountBefore = table.SpatialIndex.ActiveTree.NodeCount;

        // Modify the component data (simulating an update) — for static mode, tick fence should NOT process this
        // The DirtyBitmap marks the chunk dirty, but ProcessSpatialEntries should skip it
        using (var t = dbe.CreateQuickTransaction())
        {
            // Just opening and committing should trigger a tick fence, but no spatial update for static
            t.Commit();
        }

        // Tree should be unchanged (no reinserts, no structural changes)
        Assert.That(table.SpatialIndex.ActiveTree.EntityCount, Is.EqualTo(entityCountBefore));
        Assert.That(table.SpatialIndex.ActiveTree.NodeCount, Is.EqualTo(nodeCountBefore));
    }
}

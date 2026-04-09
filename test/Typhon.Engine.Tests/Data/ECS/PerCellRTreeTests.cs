using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Integration tests for the issue #230 Phase 1 per-cell cluster R-Tree path (lazy per-cell spatial
/// index + <see cref="ClusterSpatialQuery{TArch}"/>). Reuses the <see cref="ClCohUnit"/> archetype
/// from <see cref="ClusterSpatialCoherenceTests"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
class PerCellRTreeTests : TestBase<PerCellRTreeTests>
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

    private static List<long> CollectQueryResults(DatabaseEngine dbe,
        float minX, float minY, float maxX, float maxY, uint categoryMask = uint.MaxValue)
    {
        var results = new List<long>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var query = dbe.ClusterSpatialQuery<ClCohUnit>();
        var box = new AABB2F { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
        var enumerator = query.AABB<AABB2F>(in box, categoryMask);
        try
        {
            while (enumerator.MoveNext())
            {
                results.Add(enumerator.Current.EntityId);
            }
        }
        finally
        {
            enumerator.Dispose();
        }
        return results;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Basic single-cell queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Query_EmptyArchetype_ReturnsNothing()
    {
        using var dbe = SetupEngineWithGrid();
        var results = CollectQueryResults(dbe, 0f, 0f, 1000f, 1000f);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Query_SingleEntity_IntersectsQueryAabb_ReturnsIt()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        var results = CollectQueryResults(dbe, 0f, 0f, 100f, 100f);
        Assert.That(results, Is.EquivalentTo(new[] { (long)id.RawValue }));
    }

    [Test]
    public void Query_SingleEntity_OutsideQueryAabb_ReturnsNothing()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        // Query a distant region — no overlap with entity or cluster AABB.
        var results = CollectQueryResults(dbe, 500f, 500f, 600f, 600f);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Query_MultipleEntitiesInSameCluster_ReturnsAllInsideQuery()
    {
        using var dbe = SetupEngineWithGrid();

        // 5 points in the same cell → same cluster. Query overlaps cluster AABB; narrowphase
        // should pick up only the 3 that fall inside the query rectangle.
        EntityId a, b, c, d, e;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(10f, 10f)));
            b = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(20f, 20f)));
            c = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(30f, 30f)));
            d = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(40f, 40f)));
            e = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        // Query [15, 15] - [35, 35] should match b (20,20) and c (30,30) only.
        var results = CollectQueryResults(dbe, 15f, 15f, 35f, 35f);
        Assert.That(results, Is.EquivalentTo(new[] { (long)b.RawValue, (long)c.RawValue }));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multi-cell queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Query_SpanningTwoCells_ReturnsEntitiesFromBothWithoutDuplicates()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));   // cell (0,0)
            b = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 50f)));  // cell (1,0)
            tx.Commit();
        }

        // Query spans both cells.
        var results = CollectQueryResults(dbe, 0f, 0f, 200f, 100f);
        Assert.That(results, Is.EquivalentTo(new[] { (long)a.RawValue, (long)b.RawValue }));
    }

    [Test]
    public void Query_FourCellExpansion_ReturnsAllEntitiesInTouchedCells()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        EntityId a, b, c, d;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));    // cell (0,0)
            b = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 50f)));   // cell (1,0)
            c = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 150f)));   // cell (0,1)
            d = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(150f, 150f)));  // cell (1,1)
            tx.Commit();
        }

        // Query covers all four cells exactly.
        var results = CollectQueryResults(dbe, 0f, 0f, 200f, 200f);
        Assert.That(results, Is.EquivalentTo(new[]
        {
            (long)a.RawValue, (long)b.RawValue, (long)c.RawValue, (long)d.RawValue,
        }));
    }

    [Test]
    public void Query_DisjointCells_DoesNotReturnEntitiesFromUnqueriedCells()
    {
        using var dbe = SetupEngineWithGrid(cellSize: 100f);

        EntityId near;
        using (var tx = dbe.CreateQuickTransaction())
        {
            near = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));       // cell (0,0) — queried
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(550f, 550f)));            // cell (5,5) — NOT queried
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(750f, 750f)));            // cell (7,7) — NOT queried
            tx.Commit();
        }

        // Query only the first cell.
        var results = CollectQueryResults(dbe, 0f, 0f, 100f, 100f);
        Assert.That(results, Is.EquivalentTo(new[] { (long)near.RawValue }));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy + migration: per-cell index stays consistent
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Query_AfterDestroy_EntityNoLongerReturned()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId keep, destroy;
        using (var tx = dbe.CreateQuickTransaction())
        {
            keep = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(20f, 20f)));
            destroy = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(40f, 40f)));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(destroy);
            tx.Commit();
        }

        var results = CollectQueryResults(dbe, 0f, 0f, 100f, 100f);
        Assert.That(results, Is.EquivalentTo(new[] { (long)keep.RawValue }));
    }

    [Test]
    public void Query_AfterLastEntityDestroyed_ClusterRemovedFromPerCellIndex()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId only;
        using (var tx = dbe.CreateQuickTransaction())
        {
            only = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        // Cluster has one entity → per-cell index has 1 entry.
        var meta = Archetype<ClCohUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        Assert.That(cs.PerCellIndex[cellKey].DynamicIndex.ClusterCount, Is.EqualTo(1));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(only);
            tx.Commit();
        }

        // After the last entity is destroyed, the cluster is freed and ReleaseSlot →
        // FinaliseEmptyClusterCellState removes it from the per-cell index. The per-cell
        // slot's DynamicIndex now has zero clusters (or the slot itself may still exist
        // with an empty index — either is acceptable for Phase 1).
        var slot = cs.PerCellIndex[cellKey];
        if (slot?.DynamicIndex != null)
        {
            Assert.That(slot.DynamicIndex.ClusterCount, Is.EqualTo(0));
        }

        // And the query returns nothing.
        var results = CollectQueryResults(dbe, 0f, 0f, 100f, 100f);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Query_AfterMigration_EntityFoundInNewCell()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));  // cell (0,0)
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClCohUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 550f, MinY = 550f, MaxX = 550f, MaxY = 550f };
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Query the OLD cell — no entities.
        var oldCell = CollectQueryResults(dbe, 0f, 0f, 100f, 100f);
        Assert.That(oldCell, Is.Empty,
            "post-migration the src cluster was empty and was removed from the per-cell index");

        // Query the NEW cell — entity should be there.
        var newCell = CollectQueryResults(dbe, 500f, 500f, 600f, 600f);
        Assert.That(newCell, Is.EquivalentTo(new[] { (long)id.RawValue }));
    }

    // Note: reopen-rebuild coverage is in ClusterSpatialAabbRecomputeTests (which exercises
    // RebuildClusterAabbs directly) and ClusterSpatialCoherenceTests (which tests the full
    // persist-then-reopen flow for the spatial grid). Duplicating that here would only add
    // fixture wiring without new coverage.

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2.5: Generic ClusterSpatialQuery<TArch>.AABB<TBox> signature
    //
    // These tests exercise the new generic entry point directly (not via the CollectQueryResults
    // helper) to ensure the ISpatialBox + tier-match design is wired correctly at the public API
    // layer. The helper-based tests above provide coverage of the enumeration semantics; these
    // tests focus on the generic dispatch and error semantics.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Happy path: the new generic signature <c>AABB&lt;AABB2F&gt;(in box, mask)</c> returns the same
    /// results as the old scalar signature did in Phase 1/2. This test calls the generic method
    /// directly (bypassing the <see cref="CollectQueryResults"/> helper) so the new-style call shape
    /// is explicit in the test source.
    /// </summary>
    [Test]
    public void AABB_GenericAABB2F_MatchesArchetype_ReturnsEntitiesInBox()
    {
        using var dbe = SetupEngineWithGrid();

        EntityId idInside, idOutside;
        using (var tx = dbe.CreateQuickTransaction())
        {
            idInside = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(25f, 25f)));
            idOutside = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(500f, 500f)));
            tx.Commit();
        }

        // Direct generic call — the point of the test is the explicit AABB2F parameter.
        var results = new List<long>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            var query = dbe.ClusterSpatialQuery<ClCohUnit>();
            var box = new AABB2F { MinX = 0f, MinY = 0f, MaxX = 100f, MaxY = 100f };
            var enumerator = query.AABB<AABB2F>(in box);
            try
            {
                while (enumerator.MoveNext())
                {
                    results.Add(enumerator.Current.EntityId);
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        Assert.That(results, Is.EquivalentTo(new[] { (long)idInside.RawValue }),
            "new generic signature must return only the in-box entity");
        Assert.That(results, Does.Not.Contain((long)idOutside.RawValue),
            "new generic signature must exclude the out-of-box entity");
    }

    /// <summary>
    /// Cross-tier rejection: passing an <c>AABB3F</c> (Tier3F) query against a 2D f32 archetype
    /// (Tier2F) must throw <see cref="InvalidOperationException"/> with a message that identifies both
    /// tiers. Proves the <c>queryTier != storageTier</c> check fires before any dispatch branch
    /// is reached, and surfaces a diagnosable error to the caller.
    /// </summary>
    /// <remarks>
    /// Note: the 3D NotSupportedException branches in the dispatch switch are unreachable from a test
    /// today because <c>SpatialGrid.ValidateSupportedFieldType</c> rejects 3D field types at
    /// <c>ConfigureSpatialGrid</c> time — there is no way to set up a 3D archetype with the grid
    /// enabled. Phase 3 will extend the grid to 3D and unblock that test path; for now, the tier-mismatch
    /// check is the only reachable error surface.
    /// </remarks>
    [Test]
    public void AABB_GenericAABB3F_Against2DArchetype_ThrowsTierMismatch()
    {
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(50f, 50f)));
            tx.Commit();
        }

        InvalidOperationException caught = null;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            var query = dbe.ClusterSpatialQuery<ClCohUnit>();
            var box3F = new AABB3F { MinX = 0f, MinY = 0f, MinZ = 0f, MaxX = 100f, MaxY = 100f, MaxZ = 100f };
            try
            {
                _ = query.AABB<AABB3F>(in box3F);
                Assert.Fail("expected InvalidOperationException from tier mismatch, but no exception was thrown");
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
        }

        Assert.That(caught, Is.Not.Null);
        Assert.That(caught.Message, Does.Contain("Tier2F"),
            "error message should name the archetype's storage tier");
        Assert.That(caught.Message, Does.Contain("Tier3F"),
            "error message should name the query's tier");
        Assert.That(caught.Message, Does.Contain("AABB3F"),
            "error message should name the concrete TBox that caused the mismatch");
        Assert.That(caught.Message, Does.Contain("ClCohUnit"),
            "error message should name the archetype for quick diagnosis");
    }
}

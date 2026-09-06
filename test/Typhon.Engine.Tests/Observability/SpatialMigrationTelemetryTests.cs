using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════
// #872 step 1 — the spatial-partitioning telemetry surface.
//
// Own archetype rather than a borrowed one: ArchetypeRegistry is process-global
// and unsynchronised across parallel fixtures (#720), so a fixture that shares
// another's archetype inherits its flakes.
// ══════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.SpTel.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpTelPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class SpTelUnit : Archetype<SpTelUnit>
{
    public static readonly Comp<SpTelPos> Pos = Register<SpTelPos>();
}

[TestFixture]
[NonParallelizable]
class SpatialMigrationTelemetryTests : TestBase<SpatialMigrationTelemetryTests>
{
    // 100-unit cells over a 1000x1000 world. The hysteresis margin is the default 5 % of cell size = 5 world units, so a
    // crossing landing < 5 units past a boundary is absorbed and one landing further is a migration. Several tests below
    // depend on exactly that split.
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpTelPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static SpTelPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static EntityId Spawn(DatabaseEngine dbe, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SpTelUnit>(SpTelUnit.Pos.Set(PointAt(x, y)));
        tx.Commit();
        return id;
    }

    private static void MoveTo(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var eref = tx.OpenMut(id);
        ref var pos = ref eref.Write(SpTelUnit.Pos);
        pos.Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y };
        tx.Commit();
    }

    private static int ArchetypeId => Archetype<SpTelUnit>.Metadata.ArchetypeId;

    /// <summary>
    /// Finds an entity's cluster slot, then moves it through <c>ClusterRef.WriteSpatial</c> — the barrier API. A test that
    /// declares <c>SetSpatialBarrierOnly</c> and then writes through <c>OpenMut</c>/<c>Write</c> has broken the contract it
    /// just declared: the fence skips its legacy scan on the promise that every spatial write goes through this path.
    /// </summary>
    private static unsafe void WriteSpatialTo(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        var (chunkId, slot) = LocateSlot(dbe, id);
        Assert.That(chunkId, Is.GreaterThanOrEqualTo(0), "entity must be resident in a cluster before a barrier write");

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<SpTelUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkId)
                {
                    continue;
                }

                cluster.WriteSpatial(SpTelUnit.Pos, slot, PointAt(x, y));
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    private static unsafe (int ChunkId, int Slot) LocateSlot(DatabaseEngine dbe, EntityId id)
    {
        var cs = dbe._archetypeStates[Archetype<SpTelUnit>.Metadata.ArchetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return (cid, slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return (-1, 0);
    }

    /// <summary>Collects one observable pass over the spatial instruments, keyed by instrument name.</summary>
    private static (Dictionary<string, long> Longs, Dictionary<string, double> Doubles) ScrapeSpatialInstruments(EcsMetricsExporter exporter)
    {
        var longs = new Dictionary<string, long>();
        var doubles = new Dictionary<string, double>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EcsMetricsExporter.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => longs[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => doubles[instrument.Name] = value);
        listener.Start();
        listener.RecordObservableInstruments();

        return (longs, doubles);
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.1 — the numbers appear, on both surfaces
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void MigratingWorkload_PublishesNonZeroCounters()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 150f, 250f);   // two cells away — well past the hysteresis margin
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.MigrationCount, Is.EqualTo(1), "one entity crossed a cell boundary, so one migration executed");
            Assert.That(t.TotalMigrations, Is.EqualTo(1), "the cumulative counter tracks the per-tick one on the first tick");
            Assert.That(t.ActiveClusterCount, Is.GreaterThan(0), "a migration implies at least one live cluster");
            Assert.That(t.MigrationExecuteMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN,
                "duration is measured, not derived — it may round to zero, never to NaN");
        });

        var total = dbe.GetSpatialTelemetryTotal();
        Assert.That(total.MigrationCount, Is.EqualTo(t.MigrationCount), "engine-wide total must include this archetype's migration");
    }

    [Test]
    public void MeterListener_ObservesSameValuesAsAccessor()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var expected = dbe.GetSpatialTelemetry(ArchetypeId);
        // Precondition, not decoration: without it every assertion below degenerates to 0 == 0 and the test passes just as
        // happily against an exporter that reports nothing at all.
        Assert.That(expected.MigrationCount, Is.EqualTo(1), "precondition: the accessor must have a non-zero value to agree ON");

        using var exporter = new EcsMetricsExporter(dbe);
        var (longs, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(longs, Does.ContainKey("typhon.ecs.spatial.migrations"), "the instrument must be published, not merely defined");
            Assert.That(longs["typhon.ecs.spatial.migrations"], Is.EqualTo(expected.MigrationCount), "OTel and the accessor read the same field");
            Assert.That(longs["typhon.ecs.spatial.migrations_total"], Is.EqualTo(expected.TotalMigrations));
            Assert.That(longs["typhon.ecs.spatial.active_clusters"], Is.EqualTo(expected.ActiveClusterCount));
            Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.migration_duration_ms"));
            Assert.That(doubles["typhon.ecs.spatial.migration_duration_ms"], Is.EqualTo(expected.MigrationExecuteMs).Within(1e-9));
        });
    }

    [Test]
    public void OpenRebuildTimings_AreReadableAndFinite()
    {
        // A freshly created database has no persisted clusters, so both rebuild passes are skipped and both figures are
        // legitimately zero. What this asserts is that they are READABLE and well-formed at all. The non-zero case needs a
        // reopen with data on disk, which this fixture's harness does not do — it is stated here rather than deferred to a
        // fixture that does not exist.
        using var dbe = SetupEngineWithGrid();

        Assert.Multiple(() =>
        {
            Assert.That(dbe.OpenCellStateRebuildMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN);
            Assert.That(dbe.OpenClusterAabbRebuildMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.2 — zero, not stale
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void PerTickCounters_ResetToZero_OnATickWithoutMigration()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.EqualTo(1), "precondition: tick 1 migrated");

        dbe.WriteTickFence(2);   // nothing moved

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.MigrationCount, Is.Zero, "a quiet tick migrated nothing");
            Assert.That(t.HysteresisAbsorbedCount, Is.Zero, "a quiet tick absorbed nothing");
            Assert.That(t.MigrationExecuteMs, Is.Zero, "a quiet tick spent no time migrating");
            Assert.That(t.TotalMigrations, Is.EqualTo(1), "the CUMULATIVE counter must not be reset — that is the whole point of having it");
        });
    }

    [Test]
    public void HysteresisAbsorbed_IsRecomputedEachTick_NotLatched()
    {
        // Note what "absorbed" means, because it is not what the name suggests: an entity parked inside the margin is
        // re-evaluated on EVERY tick, so the count is a standing condition rather than a one-shot event. Bringing it home
        // is what ends the absorption.
        //
        // This is NOT the regression test for the missing fence reset — detection still runs on both ticks here, so the
        // assignment inside DetectClusterMigrations would zero the counter with or without the fix. That case is
        // HysteresisAbsorbed_IsZeroed_WhenDetectionDoesNotRun below, and the distinction is worth keeping explicit.
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 103f, 50f);   // crosses x=100 but lands 3 units in — inside the 5-unit margin
        dbe.WriteTickFence(1);

        var absorbedTick = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(absorbedTick.HysteresisAbsorbedCount, Is.EqualTo(1), "a crossing inside the margin is absorbed, not migrated");
            Assert.That(absorbedTick.MigrationCount, Is.Zero, "absorbed means no migration executed");
            Assert.That(absorbedTick.TotalHysteresisAbsorbed, Is.EqualTo(1));
        });

        MoveTo(dbe, id, 50f, 50f);    // back to the middle of its home cell — nothing left to absorb
        dbe.WriteTickFence(2);

        var homeTick = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(homeTick.HysteresisAbsorbedCount, Is.Zero, "with the entity home, detection runs and finds nothing to absorb");
            Assert.That(homeTick.TotalHysteresisAbsorbed, Is.EqualTo(1), "the cumulative twin keeps the history the per-tick counter drops");
        });
    }

    [Test]
    public void HysteresisAbsorbed_IsZeroed_WhenDetectionDoesNotRun()
    {
        // The reachable staleness path, and the one that justifies the fence reset. PrepareArchetypeFence gates the
        // clean-bitmap spatial refresh — the branch that calls DetectClusterMigrations on an otherwise quiet tick — on
        // ActiveClusterCount > 0. Empty the archetype and detection stops running entirely, so nothing assigns the counter
        // and, without the fence-time reset, it reports the last tick that HAD entities: a live-looking reading of a
        // population that no longer exists. Ablating the reset reddens this test and nothing else.
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 103f, 50f);
        dbe.WriteTickFence(1);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).HysteresisAbsorbedCount, Is.EqualTo(1), "precondition: tick 1 absorbed a crossing");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        dbe.WriteTickFence(3);   // by now the cluster is gone and the detection branch is gated off

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ActiveClusterCount, Is.Zero, "precondition: the archetype is empty, so detection no longer runs");
            Assert.That(t.HysteresisAbsorbedCount, Is.Zero, "an empty archetype absorbs nothing — this must not still read the last populated tick");
            Assert.That(t.MigrationCount, Is.Zero);
            Assert.That(t.TotalHysteresisAbsorbed, Is.EqualTo(1), "the cumulative counter keeps what the per-tick one drops");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // The barrier-only path — where the counter used to be a structural zero
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void HysteresisAbsorbed_IsCounted_OnTheBarrierOnlyPath()
    {
        // DetectClusterMigrations only increments inside step (b), its legacy dirty-bits scan, and the SpatialBarrierOnly
        // branch returns before reaching it. Both demos opt into barrier-only, so the one number that tunes
        // MigrationHysteresisRatio read 0/N on precisely the path it was needed for. Counting now happens where the
        // decision is made — at write time, in ClusterRef.MaybeFlagMigration.
        using var dbe = SetupEngineWithGrid();
        dbe.SetSpatialBarrierOnly<SpTelUnit>();

        var id = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        WriteSpatialTo(dbe, id, 103f, 50f);   // crosses x=100 but lands 3 units in — inside the 5-unit margin
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.HysteresisAbsorbedCount, Is.EqualTo(1), "the barrier-only path must count what the margin swallowed");
            Assert.That(t.MigrationCount, Is.Zero, "absorbed means no migration executed");
            Assert.That(t.TotalHysteresisAbsorbed, Is.EqualTo(1), "and the cumulative twin must see it too");
        });
    }

    [Test]
    public void HysteresisAbsorbed_OnBarrierOnly_DoesNotCountAMoveThatStaysWellInsideTheCell()
    {
        // Guards the other half of the definition: "absorbed" is a crossing the margin swallowed, NOT any move that failed
        // to leave the cell. Without the raw-boundary re-test this would count every spatial write in the database.
        using var dbe = SetupEngineWithGrid();
        dbe.SetSpatialBarrierOnly<SpTelUnit>();

        var id = Spawn(dbe, 20f, 20f);
        dbe.WriteTickFence(1);

        WriteSpatialTo(dbe, id, 60f, 60f);   // moved 40 units, never approached a boundary
        dbe.WriteTickFence(2);

        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).HysteresisAbsorbedCount, Is.Zero,
            "a move that never crossed the raw cell boundary was not absorbed by anything");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Adjacent bug found in review: the pending-migration queue pre-size
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void PendingMigrationQueue_IsPreSizedFromThePreviousTick_NotFromTheZeroedCounter()
    {
        // PrepareArchetypeFence zeroes LastTickMigrationCount, and DetectClusterMigrations then pre-sized the pending queue
        // by reading that same field — a few hundred lines later in the SAME fence. The estimate was therefore always
        // Max(16, 0), so the queue regrew by doubling from 16 on every migration-heavy tick and the amortisation the
        // pre-size exists to provide never happened.
        //
        // TWO vacuity traps, both of which this test fell into before ablation caught them. (1) Left in place, the array is
        // ALREADY grown from the previous tick's doubling — 40 migrations leaves it at 64 — so the `Length < expected`
        // guard is false under both the fixed and the broken formula. (2) If the measured tick also migrates,
        // EnqueueMigration grows the array during that same tick and the final length is 64 whichever formula sized it.
        // Nulling the array AND choosing a non-migrating nudge is what makes the remaining length the sizing decision.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<SpTelUnit>.Metadata;
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        const int Population = 40;
        var ids = new EntityId[Population];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = tx.Spawn<SpTelUnit>(SpTelUnit.Pos.Set(PointAt(50f, 50f)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        for (var i = 0; i < ids.Length; i++)
        {
            MoveTo(dbe, ids[i], 250f, 250f);
        }
        dbe.WriteTickFence(2);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.EqualTo(Population), "precondition: tick 2 migrated every entity");

        clusterState.PendingMigrations = null;

        // A nudge inside cell (2,2), which spans 200-300 on both axes — dirty writes, but no migration.
        for (var i = 0; i < ids.Length; i++)
        {
            MoveTo(dbe, ids[i], 260f, 260f);
        }
        dbe.WriteTickFence(3);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.Zero, "precondition: tick 3 must not migrate, or growth would mask the pre-size");

        const int ExpectedFloor = Population + (Population >> 2);   // the formula: prev + prev/4 = 50
        Assert.Multiple(() =>
        {
            Assert.That(clusterState.PreviousTickMigrationCount, Is.EqualTo(Population),
                "the snapshot must survive the reset that clears LastTickMigrationCount");
            Assert.That(clusterState.PendingMigrations, Is.Not.Null);
            Assert.That(clusterState.PendingMigrations.Length, Is.GreaterThanOrEqualTo(ExpectedFloor),
                $"the queue must be pre-sized for the previous tick's {Population} migrations, not left at the 16-entry floor");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.3 / AC-1.5 — reading costs nothing, degenerate inputs read zero
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void Accessor_AllocatesNothing()
    {
        using var dbe = SetupEngineWithGrid();
        Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        for (var i = 0; i < 64; i++)
        {
            _ = dbe.GetSpatialTelemetry(ArchetypeId);
            _ = dbe.GetSpatialTelemetryTotal();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = dbe.GetSpatialTelemetry(ArchetypeId);
            _ = dbe.GetSpatialTelemetryTotal();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, "the accessors return a struct read from live fields — nothing on this path may allocate");
    }

    [Test]
    public void ZeroActiveClusters_ReadsZero_WithoutThrowing()
    {
        using var dbe = SetupEngineWithGrid();   // registered, initialised, nothing spawned
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ActiveClusterCount, Is.Zero);
            Assert.That(t.MigrationCount, Is.Zero);
            Assert.That(t.MigrationExecuteMs, Is.Zero.And.Not.NaN, "no clusters must not produce a 0/0");
            Assert.That(t.ReclusterBudgetUsedMs, Is.Zero.And.Not.NaN);
            Assert.That(dbe.GetSpatialTelemetryTotal().ActiveClusterCount, Is.Zero);
        });
    }

    [Test]
    public void OutOfRangeArchetypeId_ReturnsDefault()
    {
        using var dbe = SetupEngineWithGrid();

        Assert.Multiple(() =>
        {
            Assert.That(dbe.GetSpatialTelemetry(-1).MigrationCount, Is.Zero, "a negative id must not index the array");
            Assert.That(dbe.GetSpatialTelemetry(int.MaxValue).MigrationCount, Is.Zero, "an id past the end must not index the array");
            Assert.That(dbe.GetSpatialTelemetry(-1).ActiveClusterCount, Is.Zero);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.6 — the step 10/11 counters are wired and read zero
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void FutureCounters_AreDeclaredAndPublishedAsZero()
    {
        // These have no producer yet. Declaring them now means steps 10 and 11 add a WRITER only — no new plumbing, no new
        // instrument, and no window in which the surface is half-built. They must be published AS ZERO, not absent: a
        // consumer that cannot see the series cannot tell "not built" from "the exporter is broken".
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ClustersScanned, Is.Zero, "no intra-cell drifter scan exists yet (step 10)");
            Assert.That(t.DriftersDetected, Is.Zero, "no drifter detection exists yet (step 10)");
            Assert.That(t.ReclusterBudgetUsedMs, Is.Zero, "no re-clustering budget exists yet (step 11)");
        });

        using var exporter = new EcsMetricsExporter(dbe);
        var (longs, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(longs, Does.ContainKey("typhon.ecs.spatial.clusters_scanned"));
            Assert.That(longs["typhon.ecs.spatial.clusters_scanned"], Is.Zero);
            Assert.That(longs, Does.ContainKey("typhon.ecs.spatial.drifters_detected"));
            Assert.That(longs["typhon.ecs.spatial.drifters_detected"], Is.Zero);
            Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.recluster_budget_ms"));
            Assert.That(doubles["typhon.ecs.spatial.recluster_budget_ms"], Is.Zero);
            Assert.That(doubles, Does.ContainKey("typhon.ecs.open.cellstate_rebuild_ms"));
            Assert.That(doubles, Does.ContainKey("typhon.ecs.open.cluster_aabb_rebuild_ms"));
        });
    }
}

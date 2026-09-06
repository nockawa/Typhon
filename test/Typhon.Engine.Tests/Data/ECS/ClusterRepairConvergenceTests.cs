using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The repair path's termination and its coexistence with step 10's delta path (#872 step 12).
/// </summary>
/// <remarks>
/// <para><b>Why these are not in <c>ClusterRepairTests</c>.</b> That fixture answers "does one pass do the right thing".
/// These answer "what happens on the ticks after", which is a different failure mode and a quieter one: a repair that
/// re-runs forever is green on every per-pass assertion while costing a full gather and sort per tick, and two
/// mechanisms sharing one queue corrupt each other without either one's own tests noticing.</para>
/// <para><b>The combined fixture exists because the four step-10 fixtures now pin <c>reclusterBudgetMs: 0f</c>.</b> That
/// pinning is correct scoping — repair legitimately preempts relocation, so a fixture measuring the delta path must
/// switch repair off — but it left nothing exercising the two together, and they share one <c>PendingMigrations</c>
/// array, one unstable sort and one Migrate phase. This is the seam that leaves.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterRepairConvergenceTests : TestBase<ClusterRepairConvergenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const int Population = 1000;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>
    /// An engine with repair armed and, optionally, step 10's delta path armed alongside it.
    /// </summary>
    /// <remarks>
    /// <paramref name="driftRatio"/> at 100 makes the drift gate unreachable — a cluster confined to one cell can never
    /// exceed a hundred cell-widths — which is how the delta path is switched off without changing anything else.
    /// </remarks>
    private DatabaseEngine SetupEngine(float driftRatio = 100f)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterTargetExtentRatio: driftRatio,
            clusterRepairExtentRatio: 0.75f,
            reclusterBudgetMs: 100f, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairWorstClustersPerUnit: 0));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static List<EntityId> SpawnDegradedCell(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(Population);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var x = 4f + ((i * 37) % 92) + ((i / 92) % 4) * 0.2f;
                var y = 4f + ((i * 61) % 92) + ((i / 92) % 4) * 0.2f;
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i))));
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return ids;
    }

    private static unsafe int TotalOccupancy(DatabaseEngine dbe)
    {
        var total = 0;
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                total += System.Numerics.BitOperations.PopCount(cluster.OccupancyBits);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return total;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Termination
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cell that has been repaired is not repaired again while nothing moves — the repair terminates rather than
    /// re-packing the same layout every tick.
    /// </summary>
    /// <remarks>
    /// <para><b>The livelock this rules out is not hypothetical; it was measured.</b> Nomination fires on extent alone and
    /// a Morton packing does not in general bring every cluster under the threshold, so a converged cell keeps nominating.
    /// Before the guard existed, a 2 000-entity cell re-packed all 2 000 entities on five consecutive ticks with its mean
    /// extent pinned at 23.0 the whole time: full cost, zero gain, and every per-pass assertion green.</para>
    /// <para><b>Mutations this rejects:</b> deleting <c>IsAlreadyPackedInSortOrder</c> (repairs continue forever), and
    /// deleting the geometry memo in <c>RepairOneCell</c> (the moves stop but the gather and sort do not, which the
    /// entity count below cannot see — hence the second assertion, on units rather than entities).</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-03")]
    public void ARepairedCellIsNotRepairedAgainWhileNothingMoves()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);

        var repairs = 0;
        var lastRepairTick = 0;
        for (var tick = 2; tick <= 12; tick++)
        {
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            if (t.RepairUnitCount > 0)
            {
                repairs++;
                lastRepairTick = tick;
            }
        }

        Assert.That(repairs, Is.GreaterThan(0), "nothing was ever repaired, so termination is not what this test measured");
        Assert.That(repairs, Is.LessThanOrEqualTo(2),
            $"the cell was repaired {repairs} times over 11 still ticks (last on tick {lastRepairTick}) — a converged layout is being re-packed");
        Assert.That(lastRepairTick, Is.LessThanOrEqualTo(4), $"repairs were still happening on tick {lastRepairTick}, well after the layout settled");
        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population), "the repeated repairs lost or duplicated entities");
    }

    /// <summary>
    /// Destroying a quarter of the population punches holes in the packing, and the cell converges again rather than
    /// oscillating.
    /// </summary>
    /// <remarks>
    /// <c>IsAlreadyPackedInSortOrder</c> tests whether each capacity-sized group draws from one source cluster, which is
    /// exact only while the source clusters are near-full. Holes make the groups straddle sources, so the cell repacks —
    /// correctly, since packing the survivors tightly is an improvement — and the question this pins is whether that
    /// settles. It must, because a re-pack produces full clusters again, but "must" is an argument and this is the
    /// measurement.
    /// </remarks>
    [Test]
    public void ACellConvergesAgainAfterDestroysPunchHolesInThePacking()
    {
        var dbe = SetupEngine();
        var ids = SpawnDegradedCell(dbe);

        for (var tick = 2; tick <= 6; tick++)
        {
            dbe.WriteTickFence(tick);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Count; i += 4)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        var survivors = Population - ((Population + 3) / 4);
        var repairs = 0;
        var lastRepairTick = 0;
        for (var tick = 7; tick <= 20; tick++)
        {
            dbe.WriteTickFence(tick);
            if (dbe.GetSpatialTelemetry(ArchetypeId).RepairUnitCount > 0)
            {
                repairs++;
                lastRepairTick = tick;
            }
        }

        Assert.That(TotalOccupancy(dbe), Is.EqualTo(survivors), "the post-destroy repairs lost or duplicated survivors");
        Assert.That(repairs, Is.LessThanOrEqualTo(3),
            $"the cell repaired {repairs} times over 14 ticks after the destroys (last on tick {lastRepairTick}) — it is not converging");
        Assert.That(lastRepairTick, Is.LessThanOrEqualTo(12), $"repairs were still happening on tick {lastRepairTick}, long after the destroys settled");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Coexistence with the delta path
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With BOTH the repair path and step 10's relocation armed, and the population moving every tick, no entity is lost,
    /// duplicated or left resolving to the wrong slot.
    /// </summary>
    /// <remarks>
    /// The two mechanisms file into one <c>PendingMigrations</c> array, are reordered by one unstable sort and are drained
    /// by one Migrate phase. A repair request pins a slot in a cluster it allocated this tick; a relocation request pins
    /// only a cluster and can fall through to first fit into that same fresh cluster and take the slot. Nothing here
    /// asserts the LAYOUT — that is scheduling-dependent by design and <c>MigrationRequest</c> documents the pin as a
    /// preference — only that the collision resolves without losing anything.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void RepairAndRelocationCoexistWithoutLosingAnEntity()
    {
        // The default 0.25 drift ratio, so step 10's gate is live alongside repair.
        var dbe = SetupEngine(driftRatio: 0.25f);
        var ids = SpawnDegradedCell(dbe);

        var rng = new Random(20260904);
        for (var tick = 2; tick <= 20; tick++)
        {
            JitterEveryEntity(dbe, rng);
            dbe.WriteTickFence(tick);
        }

        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population), "occupancy and the population disagree after 19 ticks of both mechanisms");

        var telemetry = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(ClusterStateOf(dbe).TotalMigrationCount, Is.GreaterThan(0), "nothing ever migrated, so the two mechanisms were never actually mixed");

        // Every id must still resolve to its own entity. A repair and a relocation racing for one slot would show up here
        // as an id resolving to a neighbour's tag — silent in a storage walk, fatal in production.
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Count; i++)
        {
            var eref = tx.Open(ids[i]);
            ref readonly var pos = ref eref.Read(ClMigUnit.Pos);
            Assert.That(pos.Tag, Is.EqualTo(i), $"EntityMap resolved entity {i} to a slot holding tag {pos.Tag}");
        }

        TestContext.Out.WriteLine($"coexistence: {ClusterStateOf(dbe).TotalMigrationCount} migrations, "
            + $"last tick drifters={telemetry.DriftersDetected} repairUnits={telemetry.RepairUnitCount} refused={telemetry.RepairUnitsRefused}");
    }

    /// <summary>Move every entity a short random step, reading the current position so the motion is local rather than a teleport.</summary>
    private static unsafe void JitterEveryEntity(DatabaseEngine dbe, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read to derive the next position from the current one.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var b = ref positions[slot].Bounds;
                    var x = Math.Clamp((0.5f * (b.MinX + b.MaxX)) + ((float)rng.NextDouble() - 0.5f) * 4f, 2f, 98f);
                    var y = Math.Clamp((0.5f * (b.MinY + b.MaxY)) + ((float)rng.NextDouble() - 0.5f) * 4f, 2f, 98f);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y, positions[slot].Tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The predicate itself
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>IsAlreadyPackedInSortOrder</c> answers exactly "would the sorted packing reproduce the current partition".
    /// </summary>
    /// <remarks>
    /// Driven directly rather than through a fence, because this one predicate decides repair-versus-no-op and therefore
    /// carries both failure modes at once: return true too readily and a degraded cell is never repaired; return false too
    /// readily and a converged cell re-packs forever. Neither is visible in a single-pass end-to-end test.
    /// </remarks>
    [Test]
    public void TheAlreadyPackedPredicateAnswersPartitionEquality()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);
        var state = ClusterStateOf(dbe);
        var capacity = System.Numerics.BitOperations.PopCount(state.Layout.FullMask);
        Assert.That(capacity, Is.GreaterThan(2), "the construction below needs a cluster to hold at least three entities");

        // Two full groups, each drawn entirely from one source cluster: the packing already IS the sorted one.
        var packed = new ArchetypeClusterState.RepairEntry[capacity * 2];
        for (var i = 0; i < capacity; i++)
        {
            packed[i] = new ArchetypeClusterState.RepairEntry((ulong)i, 7L * 64 + i);
            packed[capacity + i] = new ArchetypeClusterState.RepairEntry((ulong)(capacity + i), 9L * 64 + i);
        }

        Assert.That(state.IsAlreadyPackedInSortOrder(packed, packed.Length), Is.True,
            "two groups each drawn from a single source cluster ARE the sorted partition, so a re-pack would change nothing");

        // One entity of group 0 lives in the other cluster — the groups straddle sources, so a re-pack regroups them.
        var straddling = (ArchetypeClusterState.RepairEntry[])packed.Clone();
        straddling[1] = new ArchetypeClusterState.RepairEntry(straddling[1].MortonKey, 9L * 64 + 63);
        Assert.That(state.IsAlreadyPackedInSortOrder(straddling, straddling.Length), Is.False,
            "a group drawing from two source clusters is not the sorted partition");

        // A trailing partial group is still homogeneous, and must not be read as straddling merely for being short.
        Assert.That(state.IsAlreadyPackedInSortOrder(packed, capacity + 2), Is.True,
            "a partial final group drawn from one cluster is still packed; the length must not decide the verdict");
    }
}

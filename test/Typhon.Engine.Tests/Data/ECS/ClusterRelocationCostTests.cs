using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-10.7</c> — what intra-cell detection and relocation actually cost, per entity, single-threaded and W-scaled
/// (#872 step 10).
/// </summary>
/// <remarks>
/// <para><b><c>[Explicit]</c>, and it must stay that way.</b> These are measurements, not assertions. A timing threshold in
/// the suite is a flake generator on shared CI hardware, and a measurement that has been weakened until it stops flaking
/// no longer measures anything. What this fixture owes step 11 is NUMBERS to calibrate <c>ClusterTargetExtentRatio</c> and
/// the re-clustering budget against — the design leaves both TBD precisely because nobody had any.</para>
///
/// <para><b>Run it in Release.</b> A Debug figure is dominated by unelided bounds checks and uninlined accessors on the
/// exact per-slot loop being measured, so it does not merely scale the answer — it changes which half is expensive:</para>
/// <code>
/// dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Release --filter "FullyQualifiedName~ClusterRelocationCostTests"
/// </code>
///
/// <para><b>The two halves are budgeted three orders of magnitude apart (§5.2), so they are reported separately.</b>
/// Detection is a scan over everything that moved, budgeted at <b>0.576 ns/entity</b>; relocation is a full migration per
/// drifter. Folding them into one figure would hide which one a regression landed in — and would hide the asymmetry that
/// is the design's entire premise.</para>
///
/// <para><b>Per-entity relocation cost comes from <c>MigrationExecuteMs</c>, which is summed across workers rather than
/// wall-clock.</b> That makes it CPU time per entity and therefore comparable across worker counts: a figure that stayed
/// flat as W rose would mean perfect scaling and no added contention, and a figure that climbs is the contention itself.
/// Wall-clock per entity would fall with W whether or not the work got cheaper, which is the less interesting question.</para>
/// </remarks>
[TestFixture]
[Explicit("Measurement, not an assertion — run manually, in Release. See AC-10.7.")]
// Manual, not Nightly, and the distinction is the point. The nightly runs on the shared c6id gate box alongside eight
// test shards, so a per-entity nanosecond figure taken there measures whatever else was resident — and a number nobody
// can act on is worse than no number, because it gets quoted. These figures exist to calibrate P4 and step 11's budget,
// which needs a quiet machine and a Release build. There is no assertion here to regress, so nothing is lost by CI
// skipping it; what would be lost is trust in the numbers.
[Category("Manual")]
[NonParallelizable]
class ClusterRelocationCostTests : TestBase<ClusterRelocationCostTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Entities per cell — enough that per-cluster fixed costs are amortised across ~40 clusters.</summary>
    private const int EntitiesPerCell = 2_000;

    /// <summary>Cells used, laid out along X. Several so the fence has slices to distribute.</summary>
    private const int CellCount = 8;

    private const int MeasuredTicks = 20;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[ArchetypeId].ClusterState;

    /// <summary>
    /// Spawns a population whose clusters are ALREADY spread across their cell, then frees a fraction of the slots.
    /// </summary>
    /// <remarks>
    /// <para><b>Spread, not tight.</b> The first version spawned everything at one point and relied on per-tick jitter to
    /// spread it; a ±1.5-unit random walk covers about 6.7 units over 20 ticks against a 25-unit target extent, so the
    /// gate never opened and the run measured 0 drifters and 0 migrations. The steady state this step exists for is a world
    /// whose placement has ALREADY decayed — first-fit put these entities together and motion pulled them apart — so the
    /// measurement starts from decayed and asks what the repair costs.</para>
    /// <para><b>The destroy pass is what makes relocation possible at all.</b> Clusters hold 49 slots and
    /// <c>ClaimSlotInCell</c> packs first-fit, so a freshly spawned cell is a row of full clusters plus one remainder — and
    /// <c>ChooseRelocationTarget</c> skips full clusters, so almost every drifter would find nowhere to go. Freeing one slot
    /// in five models ordinary churn and gives placement something to work with. The size of the remaining gap between
    /// drifters and migrations is itself part of what this fixture reports.</para>
    /// </remarks>
    private static void SpawnPopulation(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(CellCount * EntitiesPerCell);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int cell = 0; cell < CellCount; cell++)
            {
                float originX = cell * CellSize;
                for (int i = 0; i < EntitiesPerCell; i++)
                {
                    uint h = (uint)((cell * 0x9E3779B1) ^ (i * 0x85EBCA6B));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 12;
                    uint g = h * 0x27D4EB2F;
                    g ^= g >> 15;

                    float x = originX + 3f + (h % 10_000) * (94f / 10_000f);
                    float y = 3f + (g % 10_000) * (94f / 10_000f);
                    ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, (cell * EntitiesPerCell) + i))));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Count; i += 5)
            {
                tx.Destroy(ids[i]);
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Jitters every entity within its own cell — the steady-state motion the step is designed for, not a teleport.
    /// </summary>
    /// <remarks>
    /// Deliberately intra-cell. An entity that crosses a cell boundary is the CELL-CROSSING detector's business and would
    /// mix that path's cost into a figure that is supposed to describe intra-cell drift alone.
    /// </remarks>
    private static unsafe void JitterEveryEntity(DatabaseEngine dbe, int tick)
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
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var b = ref positions[slot].Bounds;
                    float x = 0.5f * (b.MinX + b.MaxX);
                    float y = 0.5f * (b.MinY + b.MaxY);

                    uint h = (uint)((cluster.ChunkId * 0x9E3779B1) ^ (slot * 0x85EBCA6B) ^ (tick * 0x27D4EB2F));
                    h ^= h >> 15;
                    h *= 0x2C1B3C6D;
                    h ^= h >> 13;

                    // ±1.5 world units per tick per axis, clamped inside the entity's own cell.
                    float cellOriginX = MathF.Floor(x / CellSize) * CellSize;
                    float nx = Math.Clamp(x + (((h & 0xFF) / 255f) - 0.5f) * 3f, cellOriginX + 1f, cellOriginX + CellSize - 1f);
                    float ny = Math.Clamp(y + ((((h >> 8) & 0xFF) / 255f) - 0.5f) * 3f, 1f, CellSize - 1f);

                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(nx, ny));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private readonly record struct Totals(long Scanned, long Drifters, long Absorbed, long Migrations, double MigrationMs, double FenceMs)
    {
        internal Totals Add(SpatialMigrationTelemetry t, double fenceMs) =>
            new(Scanned + t.ClustersScanned, Drifters + t.DriftersDetected, Absorbed + t.DriftAbsorbedCount,
                Migrations + t.MigrationCount, MigrationMs + t.MigrationExecuteMs, FenceMs + fenceMs);

        internal void Report(StringBuilder sb, string label, int clusterCapacity)
        {
            // Scanned counts CLUSTERS; the per-entity budget in §5.2 is per SLOT, and the scan visits every slot of a
            // cluster it opens. Multiplying by capacity is the honest conversion and is stated rather than hidden.
            long slots = Scanned * clusterCapacity;
            sb.AppendLine($"  {label,-22} clustersScanned={Scanned,-9:N0} slots≈{slots,-11:N0} drifters={Drifters,-8:N0} absorbed={Absorbed,-8:N0} "
                + $"migrations={Migrations,-8:N0} unplaced={Drifters - Migrations,-8:N0} "
                + $"({(Drifters > 0 ? 100.0 * (Drifters - Migrations) / Drifters : 0),5:F1}%)");
            // "n/a", not 0.000. The parallel arm has no wall-clock term to divide, and a zero printed in a column labelled
            // "upper bound" reads as a measurement of a very fast thing rather than as the absence of one.
            var detection = FenceMs > 0d && slots > 0
                ? $"{FenceMs * 1e6 / slots,8:F3} ns/slot (whole fence, upper bound)"
                : $"{"n/a",8} (no wall-clock term on this arm)";
            sb.AppendLine($"  {"",-22} detection≈{detection}   "
                + $"relocation={(Migrations > 0 ? MigrationMs * 1e6 / Migrations : 0),9:F1} ns/entity (CPU, summed over workers)");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>AC-10.7</c> — single-threaded cost, driven by the serial fence.</summary>
    /// <remarks>
    /// The serial arm reports detection as <i>whole fence time ÷ slots visited</i>, which is an UPPER bound rather than the
    /// isolated figure: the same fence also refreshes AABBs, drains shadow entries and executes migrations. Stated as a
    /// bound because an unqualified number here would be read as the 0.576 ns/entity budget's counterpart and quietly
    /// compared against it. Isolating detection needs a probe inside the slice, which is step 11's business.
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_SerialFence()
    {
        using var dbe = SetupEngine();
        SpawnPopulation(dbe);
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        var sb = new StringBuilder();
        sb.AppendLine($"AC-10.7 — intra-cell drift cost, {CellCount * EntitiesPerCell:N0} entities over {CellCount} cells, "
            + $"{cs.ActiveClusterCount:N0} clusters, {MeasuredTicks} ticks");
        sb.AppendLine("  🔴 WORST CASE, NOT STEADY STATE. The population is scattered uniformly across each cell, so every cluster starts maximally spread and");
        sb.AppendLine("the drifter fraction stays near 100% per tick — §5.2's model assumes ~1%. Relocation cannot converge against a random layout, so");
        sb.AppendLine("these");
        sb.AppendLine("  ns/entity figures are an upper bound on the repair cost and the drifter counts are NOT a prediction of a real workload. A converging");
        sb.AppendLine("  population is what step 11 needs to calibrate the budget against; this fixture measures the ceiling.");

        // One untimed warm-up tick: the first jitter dirties every cluster for the first time and pays page-cache and
        // JIT costs that no steady-state tick pays.
        JitterEveryEntity(dbe, 0);
        dbe.WriteTickFence(3);

        var totals = default(Totals);
        var sw = new Stopwatch();
        for (int tick = 0; tick < MeasuredTicks; tick++)
        {
            JitterEveryEntity(dbe, tick + 1);
            sw.Restart();
            dbe.WriteTickFence(4 + tick);
            sw.Stop();
            var tel = dbe.GetSpatialTelemetry(ArchetypeId);

            // Per-tick rows, because the aggregate hides the trend that matters. `clusters` falling is the step working:
            // relocation packs entities into fewer, tighter clusters, and a run where it stays flat means the repair is
            // detecting drift it cannot act on. `queued` is the exactly-once check — it must track this tick's drifters, not
            // accumulate — and watching it grow without bound is how the drain-prefix bug was found.
            sb.AppendLine($"    tick {tick,2}: scanned={tel.ClustersScanned,-6} drifters={tel.DriftersDetected,-7} migrated={tel.MigrationCount,-7} "
                + $"queued={cs.PendingMigrationCount,-7} clusters={cs.ActiveClusterCount}");
            totals = totals.Add(tel, sw.Elapsed.TotalMilliseconds);
        }

        totals.Report(sb, "serial (W=1)", cs.Layout.ClusterSize);
        Assert.Pass(sb.ToString());
    }

    /// <summary><c>AC-10.7</c> — the same cost under the parallel fence, at increasing worker counts.</summary>
    /// <remarks>
    /// <para><b>One arm per test case, not a loop inside one test.</b> The first version looped over the worker counts and
    /// called <c>SetupEngine()</c> each time; <c>TestBase</c> resolves ONE <c>DatabaseEngine</c> per test from the root
    /// provider, so the second call threw <c>ConfigureSpatialGrid must be called before InitializeArchetypes</c> and only
    /// <c>W=1</c> was ever measured. The W-scaling this fixture exists to produce was silently absent, and
    /// <c>[Explicit]</c> meant CI would never have said so.</para>
    /// <para>Read the arms together: the per-entity relocation figure is CPU time summed across workers, so it should stay
    /// roughly flat, and the amount by which it does not is the contention the parallel fence adds.</para>
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public void Cost_ParallelFence([Values(1, 2, 4, 8)] int workerCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AC-10.7 — intra-cell drift cost under the parallel fence, {CellCount * EntitiesPerCell:N0} entities over {CellCount} cells");
        sb.AppendLine(MeasureParallel(workerCount));
        Assert.Pass(sb.ToString());
    }

    private string MeasureParallel(int workerCount)
    {
        var dbe = SetupEngine();
        SpawnPopulation(dbe);
        dbe.WriteTickFence(2);

        var cs = ClusterStateOf(dbe);
        var samples = new List<SpatialMigrationTelemetry>();
        var ticks = 0;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Cost").CallbackSystem("Jitter", _ =>
                   {
                       // Sampled BEFORE this tick's jitter, so it describes the previous tick's completed fence — the same
                       // ordering the serial arm gets for free by reading after WriteTickFence returns.
                       int n = Interlocked.Increment(ref ticks);
                       if (n > 2)
                       {
                           // Locked, like the sibling fixture does for the same pattern. A callback system is not guaranteed
                           // to be the only writer, and an unsynchronised List<T> that grows under two threads loses entries
                           // or throws — in a measurement fixture that would silently change the denominator.
                           lock (samples)
                           {
                               samples.Add(dbe.GetSpatialTelemetry(ArchetypeId));
                           }
                       }

                       JitterEveryEntity(dbe, n);
                   });
               }, new RuntimeOptions
               {
                   WorkerCount = workerCount,
                   // 100 Hz. A rate the fence cannot keep up with makes ticks overlap, and the figures then describe an
                   // overrunning engine rather than the work being measured (AC-4's finding).
                   BaseTickRate = 100,
                   EnableParallelFence = true,
               }))
        {
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= MeasuredTicks + 2, TimeSpan.FromSeconds(120));
            runtime.Shutdown();

            Assert.That(unhandled, Is.Null, $"the parallel fence threw while being measured: {unhandled}");
        }

        var totals = default(Totals);
        lock (samples)
        {
            foreach (var sample in samples)
            {
                // No wall-clock term: the fence runs on the tick thread's own schedule here, so there is nothing to time from
                // outside it. Only the CPU-time figures are meaningful for this arm — the report suppresses the detection
                // column rather than printing a zero that would read as a measured bound.
                totals = totals.Add(sample, 0d);
            }
        }

        var sb = new StringBuilder();
        totals.Report(sb, $"parallel (W={workerCount})", cs.Layout.ClusterSize);
        dbe.Dispose();
        return sb.ToString().TrimEnd();
    }
}

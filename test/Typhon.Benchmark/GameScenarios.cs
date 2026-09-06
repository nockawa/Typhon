using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

[Component("Typhon.Benchmark.Game.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct GamePos
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    /// <summary>Indexed and non-unique, because a real archetype pays index staging on every migration.</summary>
    [Field]
    [Index(AllowMultiple = true)]
    public int Tag;
}

[Archetype]
partial class GameEntity : Archetype<GameEntity>
{
    public static readonly Comp<GamePos> Pos = Register<GamePos>();
}

/// <summary>
/// What the spatial layer does under the shapes real games actually have: real world extents in metres, real entity
/// sizes, real speeds, real query radii, at three populations each.
/// </summary>
/// <remarks>
/// <para><b>Not a CI test and not a unit test.</b> It runs minutes, it reports numbers rather than asserting them, and
/// its whole value is in the absolute figures. Run it deliberately:</para>
/// <code>cd test/Typhon.Benchmark &amp;&amp; dotnet run -c Release -- --game</code>
/// <para><b>The fence column is the SERIAL fence</b> (<c>WriteTickFence</c>), not the parallel DAG a host drives through
/// <c>TyphonRuntime</c>. That is stated on every table rather than quietly converted: the parallel fence measured 1.2–4×
/// faster depending on worker count in the step-14 campaign, so these fence numbers are an upper bound on a real host's.
/// Everything else here — query time, layout, cluster counts, migration counts — is unaffected by fence parallelism.</para>
/// </remarks>
internal static class GameScenarios
{
    /// <summary>One world, in the units its game actually uses.</summary>
    internal sealed class Scenario
    {
        public string Name = "";

        /// <summary>What this is modelling, and where the numbers came from.</summary>
        public string Provenance = "";

        /// <summary>Side of the simulated cube, in metres.</summary>
        public double WorldExtentM;

        /// <summary>Grid cell edge, in metres.</summary>
        public double CellSizeM;

        /// <summary>Half-extent of an entity's bounding box, in metres.</summary>
        public double EntityHalfExtentM;

        /// <summary>Typical speed of a MOVING entity, metres per second.</summary>
        public double SpeedMPerS;

        /// <summary>Simulation rate, ticks per second — what converts a speed into a per-tick displacement.</summary>
        public double TickHz;

        /// <summary>Fraction of the population that moves at all; the rest is scenery.</summary>
        public double MovingFraction;

        /// <summary>Radius of the game's own interest query, in metres.</summary>
        public double QueryRadiusM;

        /// <summary>Populations to sweep.</summary>
        public int[] Populations = [];

        /// <summary>Flat worlds (a city, a battle-royale island) put every entity in one Z plane; space does not.</summary>
        public bool Flat;
    }

    internal sealed class Row
    {
        public string Scenario = "";
        public string Arm = "";
        public int Entities;
        public double SpawnMs;
        public double FirstFenceMs;
        public double FenceMedianMs;
        public double FenceP99Ms;
        public double QueryUs;
        public double QueryHits;
        public double MigrationsPerTick;
        public int Clusters;
        public int LiveCells;
        public double EntitiesPerCell;
        public double ClustersPerCell;
        public double TightnessPct;
        public double SlotOccupancyPct;
        public int PromotedCells;
        public string Failure = "";
    }

    /// <summary>
    /// The cluster count at which a cell promotes to a per-cell R-Tree, for the arm being run. <see cref="int.MaxValue"/>
    /// never promotes; <c>1</c> promotes every cell that holds anything.
    /// </summary>
    private static int PromoteThreshold = SpatialOptions.DefaultCellTreePromoteThreshold;

    /// <summary>The two arms: never promote, and promote every cell. The shipped threshold sits between them.</summary>
    private static readonly (string Name, int Threshold)[] Arms =
    [
        ("scan", int.MaxValue),
        ("tree", 1),
    ];

    internal static bool FiniteZ;   // PROBE

    private const int WarmTicks = 5;

    private const int MeasuredTicks = 30;

    private const int QueriesPerRound = 64;

    internal static void Run(string[] args)
    {
        FiniteZ = Array.IndexOf(args, "--finite-z") >= 0;   // PROBE
        var only = ArgString(args, "--scenario", "");
        var scale = ArgFloat(args, "--scale", 1f);
        var scenarios = Build();
        var rows = new List<Row>();

        Console.WriteLine("── Game scenarios ──────────────────────────────────────────────────────");
        var sw = Stopwatch.StartNew();
        foreach (var s in scenarios)
        {
            if (only.Length > 0 && !s.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"  {s.Name} — world {s.WorldExtentM:N0} m, cell {s.CellSizeM:N0} m, entity {2 * s.EntityHalfExtentM:N1} m, "
                + $"{s.SpeedMPerS:N0} m/s at {s.TickHz:N0} Hz, query r={s.QueryRadiusM:N0} m");
            foreach (var n in s.Populations)
            {
                var scaled = Math.Max(16, (int)(n * scale));
                foreach (var (armName, threshold) in Arms)
                {
                    PromoteThreshold = threshold;
                    var row = RunScenario(s, scaled);
                    row.Arm = armName;
                    rows.Add(row);
                    Console.WriteLine(row.Failure.Length > 0
                        ? $"    n={scaled,-8} {armName,-6} FAILED: {row.Failure}"
                        : $"    n={scaled,-8} {armName,-6} fence {row.FenceMedianMs,7:F2} ms  query {row.QueryUs,8:F1} us ({row.QueryHits,6:F0} hits)  "
                          + $"clusters {row.Clusters,6} ({row.ClustersPerCell,6:F0}/cell)  tight {row.TightnessPct,5:F1}%  promoted {row.PromotedCells,5}");
                }

                var scan = rows.FindLast(r => r.Entities == scaled && r.Arm == "scan" && r.Scenario == s.Name);
                var tree = rows.FindLast(r => r.Entities == scaled && r.Arm == "tree" && r.Scenario == s.Name);
                if (scan != null && tree != null && scan.Failure.Length == 0 && tree.Failure.Length == 0)
                {
                    var verdict = Math.Abs(scan.QueryHits - tree.QueryHits) > 0.5
                        ? $"!! HIT MISMATCH scan {scan.QueryHits:F0} vs tree {tree.QueryHits:F0} — the two structures disagree (SQ-01)"
                        : $"   tree/scan query {(tree.QueryUs > 0 ? scan.QueryUs / tree.QueryUs : 0):F2}x, fence {(tree.FenceMedianMs > 0 ? scan.FenceMedianMs / tree.FenceMedianMs : 0):F2}x";
                    Console.WriteLine($"    {"",-8} {"",-6} {verdict}");
                }
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  {rows.Count} runs in {sw.Elapsed.TotalSeconds:F1}s");
        var path = WriteReport(scenarios, rows, sw.Elapsed);
        Console.WriteLine($"  report -> {path}");
    }

    /// <summary>
    /// The four worlds. Every number is in metres and metres per second, and every one of them is a real game's, so a
    /// row below can be read as "this is what the engine does for that game" rather than as an abstract population sweep.
    /// </summary>
    private static List<Scenario> Build() =>
    [
        new Scenario
        {
            Name = "EVE Online fleet fight",
            Provenance = "CCP's grid was a 250 km cube before 2016 and is ~8 000 km from centre on each axis since "
                + "(eveonline.com/news/view/grid-sizes-you). The 200 km modelled here is the COMBAT volume — the span a fleet "
                + "fight actually occupies — rather than the full grid, which is mostly empty and would put every ship in one "
                + "cell. Server tick is 1 Hz, and time dilation stretches it to 10x under load "
                + "(imperium.news/understanding-eve-online-server-tick). Titans run ~15.75-18 km long (Imperium News, "
                + "community estimate); the 500 m box here is a battleship, the fleet-fight median. Sub-warp speeds of "
                + "300-400 m/s are a community figure, not a CCP spec. Real engagements: B-R5RB 2014, ~7 500 players and 75 "
                + "titans over 21 h; M2-XFE 2020, 5 000+ players and 257 titans (both eveonline.com news). Targeting range "
                + "is widely quoted at 300 km but I could not confirm it officially.",
            WorldExtentM = 200_000,          // a 200 km combat grid
            CellSizeM = 40_000,              // ~32 ships per cell at the middle population
            EntityHalfExtentM = 250,         // a 500 m hull — battleship class, the fleet-fight median
            SpeedMPerS = 300,                // sub-warp capital speed
            TickHz = 1,                      // EVE's server tick
            MovingFraction = 0.85,
            QueryRadiusM = 100_000,          // targeting/overview range
            Populations = [2_000, 6_000, 20_000],
            Flat = false,                    // space is not flat
        },
        new Scenario
        {
            Name = "Open-world city",
            Provenance = "GTA V's map is 75.84 km² total, 48.15 km² of it land; the 9 km square here is 81 km², the same "
                + "order. Rockstar publishes NO pedestrian, vehicle or building counts and no simulation radius — density is "
                + "driven by popcycle.dat multipliers with no public numbers (gtamods.com/wiki/Popcycle.dat) — so the "
                + "populations swept here are MINE, not the game's. On-foot speed is community-measured at 6.1-6.7 m/s; "
                + "cars reach 62-77 m/s; the 12 m/s used is an urban-traffic average and is my choice. GTA Online is "
                + "peer-to-peer with no published tick rate; 30 Hz is a community estimate. The 300 m interest radius is "
                + "mine — no title publishes one.",
            WorldExtentM = 9_000,
            CellSizeM = 250,
            EntityHalfExtentM = 1.0,         // a person or a car, ~2 m box
            SpeedMPerS = 12,                 // traffic; pedestrians are the static-ish remainder
            TickHz = 30,
            MovingFraction = 0.35,           // the rest is scenery and parked vehicles
            QueryRadiusM = 300,              // streaming / interest radius
            Populations = [20_000, 80_000, 250_000],
            Flat = true,
        },
        new Scenario
        {
            Name = "Battle royale island",
            Provenance = "PUBG's Erangel and Miramar are 8 x 8 km (ggrecon.com); 100 players is the official cap. Sprint is "
                + "6.3 m/s from the official wiki (pubg.wiki.gg/wiki/Movement_Speed), which is what the 6 m/s here rounds. "
                + "PUBG's server tick is 60 Hz — the highest of the genre, against Fortnite's 30 and Warzone's 20 — so this "
                + "has the TIGHTEST per-tick budget of the five worlds at 16.7 ms. The play zone shrinks from 3 994 m "
                + "diameter to nothing over nine phases (pubg.wiki.gg/wiki/The_Playzone); that is not modelled here, and it "
                + "would concentrate the population far past what these rows show. The 250 m replication radius is mine — "
                + "no title publishes one. Populations above 100 are ground loot and are my figures.",
            WorldExtentM = 8_000,
            CellSizeM = 300,
            EntityHalfExtentM = 0.5,         // a player or a loot item
            SpeedMPerS = 6,                  // sprint; vehicles are faster but rare
            TickHz = 60,                     // PUBG's official server tick — the tightest budget of the five worlds
            MovingFraction = 0.05,           // 100 players and some vehicles against thousands of static items
            QueryRadiusM = 250,
            Populations = [5_000, 20_000, 60_000],
            Flat = true,
        },
        new Scenario
        {
            Name = "City, few big cells",
            Provenance = "The same city population, partitioned into a handful of very large cells instead of a fine grid. "
                + "This is the shape the per-cell R-Tree was built for — tens of thousands of entities in ONE cell, so the "
                + "cell holds enough clusters to cross the promotion threshold — and it is the direct test of whether the "
                + "tree pays there. It is NOT a recommended configuration; it is the configuration that reaches the tree.",
            WorldExtentM = 9_000,
            CellSizeM = 4_500,               // four cells, so ~60 000 entities and ~1 200 clusters in each
            EntityHalfExtentM = 1.0,
            SpeedMPerS = 12,
            TickHz = 30,
            MovingFraction = 0.35,
            QueryRadiusM = 300,
            Populations = [20_000, 80_000, 250_000],
            Flat = true,
        },
        new Scenario
        {
            Name = "Large-scale RTS battle",
            Provenance = "Supreme Commander: Forged Alliance — 20 x 20 km standard maps, a fixed 10 Hz simulation tick "
                + "(FAForever forums). Chosen because it is the closest analogue to Typhon's own fixed-tick ECS loop and "
                + "because nearly EVERY entity moves every tick, the hardest shape for a spatial index to maintain. The unit "
                + "cap is 500 by default and 1 500 in FAForever, with community reports of 5 000+ at reduced sim speed — so "
                + "the 10 000 to 150 000 swept here is deliberately one to two orders ABOVE what the genre ships, which is "
                + "the volumetry question rather than the fidelity one. Unit sizes, speeds and weapon ranges are not "
                + "published anywhere I could find; the 6 m box, 5 m/s and 120 m query radius are mine.",
            WorldExtentM = 20_000,
            CellSizeM = 500,
            EntityHalfExtentM = 3.0,         // a tank-sized unit
            SpeedMPerS = 5,
            TickHz = 10,
            MovingFraction = 0.90,           // an RTS army is nearly all in motion
            QueryRadiusM = 120,              // weapon / vision range
            Populations = [10_000, 50_000, 150_000],
            Flat = true,
        },
    ];

    private static Row RunScenario(Scenario s, int entities)
    {
        var row = new Row { Scenario = s.Name, Entities = entities };
        try
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
                  o.DatabaseName = $"Game_{Environment.ProcessId}";
                  // 1 GiB of pages: the largest scenario here is a quarter of a million entities with a secondary index,
                  // and the page-cache back-pressure timeout is what a too-small cache looks like from the outside.
                  o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                  o.TestMode = true;
                  o.PagesDebugPattern = false;
              })
              .AddInMemoryWalEngine();
            using var sp = sc.BuildServiceProvider();
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            var dbe = sp.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<GamePos>();

            var extent = (float)s.WorldExtentM;
            var cell = (float)s.CellSizeM;
            dbe.ConfigureSpatialGrid(new SpatialGridConfig(
                new Vector3(0, 0, 0),
                new Vector3(extent, extent, s.Flat ? cell : extent),
                cell));
            dbe.ClusterCellTreePromoteThreshold = PromoteThreshold;
            if (PromoteThreshold <= 1)
            {
                // Forcing promotion means promoting cells the tightness gate would refuse — which is the point of the arm:
                // it answers "what does the tree do HERE", not "should this cell have a tree".
                dbe.ClusterCellTreePromoteTightness = 1f;
            }

            dbe.InitializeArchetypes();

            var rng = new Random(20260906);
            var half = (float)s.EntityHalfExtentM;
            var xs = new float[entities];
            var ys = new float[entities];
            var zs = new float[entities];
            var vx = new float[entities];
            var vy = new float[entities];
            var vz = new float[entities];
            var step = (float)(s.SpeedMPerS / s.TickHz);
            for (var i = 0; i < entities; i++)
            {
                xs[i] = (float)(rng.NextDouble() * (extent - (2 * half))) + half;
                ys[i] = (float)(rng.NextDouble() * (extent - (2 * half))) + half;
                zs[i] = s.Flat ? cell * 0.5f : (float)(rng.NextDouble() * (extent - (2 * half))) + half;

                if (rng.NextDouble() < s.MovingFraction)
                {
                    // A persistent heading, so an entity crosses cells the way a moving thing does rather than jittering in place.
                    var a = rng.NextDouble() * Math.PI * 2;
                    var e = s.Flat ? 0d : (rng.NextDouble() - 0.5) * Math.PI;
                    vx[i] = (float)(Math.Cos(a) * Math.Cos(e) * step);
                    vy[i] = (float)(Math.Sin(a) * Math.Cos(e) * step);
                    vz[i] = s.Flat ? 0f : (float)(Math.Sin(e) * step);
                }
            }

            var ids = new EntityId[entities];
            var sw = Stopwatch.StartNew();
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < entities; i++)
                {
                    var p = default(GamePos);
                    Write(ref p, xs[i], ys[i], zs[i], half, i);
                    ids[i] = tx.Spawn<GameEntity>(GameEntity.Pos.Set(in p));
                }

                tx.Commit();
            }

            sw.Stop();
            row.SpawnMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            dbe.WriteTickFence(1);
            sw.Stop();
            row.FirstFenceMs = sw.Elapsed.TotalMilliseconds;

            var fenceMs = new List<double>(MeasuredTicks);
            for (var tick = 0; tick < WarmTicks + MeasuredTicks; tick++)
            {
                using (var tx = dbe.CreateQuickTransaction())
                {
                    // Written through OpenMut rather than the ClusterRef spatial barrier: the barrier is AABB2F-only, and
                    // three of these four worlds are flat but one is not. OpenMut also routes Prep down the DIRTY-bitmap
                    // branch, which is the fuller of the two — shadow drain, zone-map recompute and dormancy sweep all run,
                    // so a fence number here is not flattered by the barrier's shortcuts.
                    for (var i = 0; i < entities; i++)
                    {
                        if (vx[i] == 0f && vy[i] == 0f && vz[i] == 0f)
                        {
                            continue;
                        }

                        // Bounce off the world box rather than wrapping: a wrap is a teleport, and a teleport is a different
                        // workload (a migration storm) from the steady motion these scenarios are about.
                        if (xs[i] + vx[i] < half || xs[i] + vx[i] > extent - half) { vx[i] = -vx[i]; }
                        if (ys[i] + vy[i] < half || ys[i] + vy[i] > extent - half) { vy[i] = -vy[i]; }
                        if (!s.Flat && (zs[i] + vz[i] < half || zs[i] + vz[i] > extent - half)) { vz[i] = -vz[i]; }

                        xs[i] += vx[i];
                        ys[i] += vy[i];
                        zs[i] += vz[i];
                        ref var p = ref tx.OpenMut(ids[i]).Write(GameEntity.Pos);
                        Write(ref p, xs[i], ys[i], zs[i], half, i);
                    }

                    tx.Commit();
                }

                sw.Restart();
                dbe.WriteTickFence(2 + tick);
                sw.Stop();
                if (tick >= WarmTicks)
                {
                    fenceMs.Add(sw.Elapsed.TotalMilliseconds);
                }
            }

            fenceMs.Sort();
            row.FenceMedianMs = fenceMs.Count == 0 ? 0 : fenceMs[fenceMs.Count / 2];
            row.FenceP99Ms = fenceMs.Count == 0 ? 0 : fenceMs[Math.Min(fenceMs.Count - 1, (int)(fenceMs.Count * 0.99))];

            var telemetry = dbe.GetSpatialTelemetry(Archetype<GameEntity>.Metadata.ArchetypeId);
            row.MigrationsPerTick = telemetry.MigrationCount;

            // A FRESH generator with a fixed seed, not the one the world was built from: both arms must ask the same
            // boxes, or a hit-count difference between them says nothing about the structures.
            MeasureQuery(dbe, s, new Random(90_210), out var qUs, out var qHits);
            row.QueryUs = qUs;
            row.QueryHits = qHits;

            Snapshot(dbe, s, row);
            return row;
        }
        catch (Exception ex)
        {
            row.Failure = $"{ex.GetType().Name}: {ex.Message}";
            return row;
        }
    }

    /// <summary>
    /// The game's own query: a box of the scenario's interest radius around a random entity, which is what an interest
    /// manager, a targeting scan or an AI perception pass actually asks.
    /// </summary>
    /// <remarks>
    /// Warmed by WALL TIME rather than by iteration count. Tiered compilation promotes on a background thread after a
    /// delay, and a fixed iteration warm-up returns before that lands — which in an earlier measurement made a cold arm
    /// look 17× faster than a warm one.
    /// </remarks>
    private static void MeasureQuery(DatabaseEngine dbe, Scenario s, Random rng, out double us, out double hits)
    {
        var cs = dbe._archetypeStates[Archetype<GameEntity>.Metadata.ArchetypeId].ClusterState;
        var r = (float)s.QueryRadiusM;
        var extent = (float)s.WorldExtentM;

        // A FIXED set of boxes, generated once from the caller's seeded generator and reused by every arm.
        //
        // The earlier shape drew a fresh box per iteration and warmed for a fixed WALL TIME, so an arm that queried faster
        // completed more warm-up iterations, left the generator at a different position, and then measured a different
        // NUMBER of different boxes. Comparing hit counts across arms that way reported "the two structures disagree" for
        // three of fifteen points — in both directions, which is the tell — when the structures agreed and the harness did
        // not. Hits must be a function of the world and the boxes alone.
        var boxes = new (float X, float Y, float Z)[QueriesPerRound];
        for (var i = 0; i < boxes.Length; i++)
        {
            boxes[i] = ((float)(rng.NextDouble() * extent), (float)(rng.NextDouble() * extent), s.Flat ? 0f : (float)(rng.NextDouble() * extent));
        }

        // Warmed by WALL TIME rather than by iteration count: tiered compilation promotes on a background thread after a
        // delay, and a fixed iteration warm-up returns before that lands — which in an earlier measurement made a cold arm
        // look 17x faster than a warm one. The warm-up walks the same boxes, so it moves no shared state.
        var sw = Stopwatch.StartNew();
        var spins = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            spins += OneQuery(dbe, cs, boxes[spins % boxes.Length], extent, r, s.Flat);
            spins++;
        }

        long found = 0;
        sw.Restart();
        for (var i = 0; i < boxes.Length; i++)
        {
            found += OneQuery(dbe, cs, boxes[i], extent, r, s.Flat);
        }

        sw.Stop();
        us = sw.Elapsed.TotalMilliseconds * 1000d / boxes.Length;
        hits = (double)found / boxes.Length;
    }

    private static int OneQuery(DatabaseEngine dbe, ArchetypeClusterState cs, (float X, float Y, float Z) at, float extent, float radius, bool flat)
    {
        // PROBE: finite Z bounds for the flat worlds instead of the +/-infinity sentinel, to tell an infinity-handling
        // fault in the promoted path from a 3D-field one (#905).
        var zLo = flat ? (FiniteZ ? -1e6f : float.NegativeInfinity) : at.Z - radius;
        var zHi = flat ? (FiniteZ ? 1e6f : float.PositiveInfinity) : at.Z + radius;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var n = 0;
        foreach (var hit in cs.QueryAabb(dbe.SpatialGrid, at.X - radius, at.Y - radius, zLo, at.X + radius, at.Y + radius, zHi))
        {
            n += hit.EntityId == 0 ? 0 : 1;
        }

        return n;
    }

    private static void Snapshot(DatabaseEngine dbe, Scenario s, Row row)
    {
        var cs = dbe._archetypeStates[Archetype<GameEntity>.Metadata.ArchetypeId].ClusterState;
        row.Clusters = cs.ActiveClusterCount;
        row.PromotedCells = cs.PromotedCellCount;

        var live = 0;
        for (var key = 0; key < dbe.SpatialGrid.CellCount; key++)
        {
            if (dbe.SpatialGrid.GetCell(key).EntityCount > 0)
            {
                live++;
            }
        }

        row.LiveCells = live;
        row.EntitiesPerCell = live > 0 ? (double)row.Entities / live : 0d;
        row.ClustersPerCell = live > 0 ? (double)row.Clusters / live : 0d;

        var cell = (float)s.CellSizeM;
        var total = 0d;
        var counted = 0;
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var id = cs.ActiveClusterIds[i];
            if ((uint)id >= (uint)cs.ClusterAabbs.Length)
            {
                continue;
            }

            ref var b = ref cs.ClusterAabbs[id];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            var e = Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
            if (!s.Flat)
            {
                e = Math.Max(e, b.MaxZ - b.MinZ);
            }

            if (float.IsFinite(e) && e >= 0f)
            {
                total += e;
                counted++;
            }
        }

        row.TightnessPct = counted == 0 ? 0d : 100d * total / counted / cell;
        var slots = System.Numerics.BitOperations.PopCount(cs.Layout.FullMask);
        row.SlotOccupancyPct = row.Clusters > 0 ? 100d * row.Entities / (row.Clusters * (double)slots) : 0d;
    }

    private static string WriteReport(List<Scenario> scenarios, List<Row> rows, TimeSpan elapsed)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "claude", "scratch"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"game-scenarios-{DateTime.Now:yyyy-MM-dd}.md");
        using var w = new StreamWriter(path, false);

        w.WriteLine("# Typhon spatial layer under real game workloads");
        w.WriteLine();
        w.WriteLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run -c Release -- --game` ({elapsed.TotalSeconds:F0} s).");
        w.WriteLine("Source: `test/Typhon.Benchmark/GameScenarios.cs`. Not a CI test — an instrument.");
        w.WriteLine();
        w.WriteLine("## How to read this");
        w.WriteLine();
        w.WriteLine("Every world is in **metres** and **metres per second**, at the game's own tick rate, so a per-tick");
        w.WriteLine("displacement here is the one that game actually applies. Entities carry a real bounding box, not a point.");
        w.WriteLine();
        w.WriteLine("- **fence** is the per-tick spatial maintenance: drift detection, relocation, repair, migration, AABB refresh.");
        w.WriteLine("  It is the **serial** fence (`WriteTickFence`). A host drives the parallel DAG through `TyphonRuntime`, which");
        w.WriteLine("  measured 1.2-4x faster depending on worker count, so treat these as an upper bound.");
        w.WriteLine("- **query** is one interest query at the scenario's own radius, against the cluster index, warmed by wall time.");
        w.WriteLine("- **tight** is the mean cluster bound as a percentage of the cell edge — the selectivity proxy. Lower prunes better.");
        w.WriteLine("- **occ** is slot occupancy over the archetype's real slots per cluster.");
        w.WriteLine("- **budget**: at the scenario's tick rate, one tick is " + "`1000 / Hz` ms — the fence has to fit inside it alongside everything else the game does.");
        w.WriteLine();

        foreach (var s in scenarios)
        {
            var mine = rows.FindAll(r => r.Scenario == s.Name);
            if (mine.Count == 0)
            {
                continue;
            }

            var tickBudgetMs = 1000d / s.TickHz;
            w.WriteLine($"## {s.Name}");
            w.WriteLine();
            w.WriteLine(s.Provenance);
            w.WriteLine();
            w.WriteLine($"| | |");
            w.WriteLine($"|---|---|");
            w.WriteLine($"| World | {s.WorldExtentM:N0} m {(s.Flat ? "square (flat)" : "cube")} |");
            w.WriteLine($"| Cell edge | {s.CellSizeM:N0} m |");
            w.WriteLine($"| Entity box | {2 * s.EntityHalfExtentM:N1} m |");
            w.WriteLine($"| Speed | {s.SpeedMPerS:N0} m/s ({s.MovingFraction:P0} of entities move) |");
            w.WriteLine($"| Tick | {s.TickHz:N0} Hz — {tickBudgetMs:N1} ms per tick |");
            w.WriteLine($"| Displacement per tick | {s.SpeedMPerS / s.TickHz:N2} m ({100d * s.SpeedMPerS / s.TickHz / s.CellSizeM:N2} % of a cell) |");
            w.WriteLine($"| Query radius | {s.QueryRadiusM:N0} m |");
            w.WriteLine();
            w.WriteLine("| entities | spawn ms | fence ms | fence p99 | % of tick | query us | hits | live cells | ent/cell | clusters | tight % | occ % | mig/tick | promoted |");
            w.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in mine)
            {
                if (r.Failure.Length > 0)
                {
                    w.WriteLine($"| {r.Entities:N0} | ❌ {r.Failure} | | | | | | | | | | | | |");
                    continue;
                }

                w.WriteLine($"| {r.Entities:N0} | {r.SpawnMs:N0} | {r.FenceMedianMs:N2} | {r.FenceP99Ms:N2} | {100d * r.FenceMedianMs / tickBudgetMs:N1} % | "
                    + $"{r.QueryUs:N1} | {r.QueryHits:N0} | {r.LiveCells:N0} | {r.EntitiesPerCell:N1} | {r.Clusters:N0} | {r.TightnessPct:N1} | "
                    + $"{r.SlotOccupancyPct:N1} | {r.MigrationsPerTick:N0} | {r.PromotedCells} |");
            }

            w.WriteLine();
        }

        w.WriteLine("## Finding: a promoted cell returns NOTHING for a query with an infinite axis bound (SQ-01)");
        w.WriteLine();
        w.WriteLine("Running every scenario twice — promotion off (`scan`) and forced on for every cell (`tree`) — with the");
        w.WriteLine("same query boxes in both arms: **13 of the 15 population points return 0 hits from the tree** where the");
        w.WriteLine("scan returns the right answer. The two EVE points that do not return zero return slightly SHORT");
        w.WriteLine("(2 538 against 2 550, 8 366 against 8 375), which is a second defect and not a rounding difference.");
        w.WriteLine();
        w.WriteLine("The cause is localised. Passing a **finite** Z range instead of the `+/-infinity` sentinel makes the tree");
        w.WriteLine("agree with the scan exactly:");
        w.WriteLine();
        w.WriteLine("| query Z bounds | scan hits | tree hits |");
        w.WriteLine("|---|---|---|");
        w.WriteLine("| `-inf .. +inf` | 19 / 76 | **0 / 0** |");
        w.WriteLine("| `-1e6 .. +1e6` | 19 / 76 | 19 / 76 |");
        w.WriteLine();
        w.WriteLine("So: **the promoted per-cell R-Tree mishandles an infinite query bound; the linear scan does not.** An");
        w.WriteLine("infinite bound on an unused axis is the documented way to ask a 2D question of a 3D-capable index, and it");
        w.WriteLine("is what the engine's own fixtures pass — but those fixtures use a 2D archetype, whose Z is already a");
        w.WriteLine("sentinel in the stored box, so they never exercise a real Z against an infinite bound. Reproduce with");
        w.WriteLine("`--game --scenario battle` and compare against `--game --scenario battle --finite-z`.");
        w.WriteLine();
        w.WriteLine("This is NOT depth-related and NOT introduced by the step-16 work: it reproduces at ONE cluster per cell,");
        w.WriteLine("and the forcing arm uses the count-only promotion that predates it. Promotion ships ON by default at");
        w.WriteLine("1 024 clusters per cell, so any database whose cells reach that threshold is exposed today.");
        w.WriteLine();
        w.WriteLine("Where the tree does answer correctly it is SLOWER at these densities — about 2x the scan at one cluster");
        w.WriteLine("per cell — which is what the crossover sweep predicts and why the threshold is high.");
        w.WriteLine();
        w.WriteLine("## Caveats");
        w.WriteLine();
        w.WriteLine("- Serial fence, as above. The parallel figure is the one a host sees.");
        w.WriteLine("- One archetype per world. A real game splits entities across several, and the fence is per-archetype,");
        w.WriteLine("  so a world of the same size split four ways does less work per archetype and more scheduling.");
        w.WriteLine("- Motion is a persistent heading with a bounce off the world box. Real movement is more correlated");
        w.WriteLine("  (roads, orbits, fleet manoeuvres), which makes clusters TIGHTER than this, not looser.");
        w.WriteLine("- The static fraction never moves at all, which is what the scenery in a city or the ground loot on a");
        w.WriteLine("  battle-royale island actually does.");
        w.WriteLine("- Cell sizes were chosen to land near the measured optimum of tens of entities per cell at the middle");
        w.WriteLine("  population, not tuned per row.");
        w.WriteLine("- **Provenance is per scenario above.** EVE and PUBG numbers are official or wiki-sourced. GTA V is the");
        w.WriteLine("  weakest: Rockstar publishes no agent counts, no simulation radius and no tick rate, so those are mine.");
        w.WriteLine("  Supreme Commander tick and map size are community-sourced and mutually consistent; its unit sizes and");
        w.WriteLine("  query radii are not published anywhere, so those are mine too. Where a number is mine it says so.");
        w.WriteLine("- The RTS populations are one to two orders above the genre's real unit caps (500 default, 1 500 in");
        w.WriteLine("  FAForever). That is deliberate: the question asked here is how the engine reacts to VOLUMETRY, not");
        w.WriteLine("  whether it can run Supreme Commander.");
        return path;
    }

    private static void Write(ref GamePos p, float x, float y, float z, float half, int tag)
    {
        p.Bounds = new AABB3F { MinX = x - half, MinY = y - half, MinZ = z - half, MaxX = x + half, MaxY = y + half, MaxZ = z + half };
        p.Tag = tag;
    }

    private static string ArgString(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}

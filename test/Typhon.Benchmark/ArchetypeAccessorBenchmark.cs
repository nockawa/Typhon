using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ── Components matching AntHill pattern (SV) ────────────────────────────
[Component("Typhon.Benchmark.AA.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchPosition
{
    public float X, Y;
    public AaBenchPosition(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Benchmark.AA.Movement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchMovement
{
    public float VX, VY;
    public AaBenchMovement(float vx, float vy) { VX = vx; VY = vy; }
}

[Archetype(510)]
partial class AaBenchAnt : Archetype<AaBenchAnt>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchMovement> Movement = Register<AaBenchMovement>();
}

/// <summary>
/// Compares per-entity access cost: standard EntityAccessor.Open vs ArchetypeAccessor.Open.
/// Mimics the AntHill movement system: Read(Movement) + Write(Position) per entity.
/// </summary>
static class ArchetypeAccessorBenchmark
{
    const float WorldSize = 10_000f;

    public static void Run(int entityCount = 50_000, int iterations = 500)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  ArchetypeAccessor Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");

        using var env = CreateEnv(entityCount);
        var dbe = env.Dbe;
        var entityIds = env.EntityIds;

        // Warm up both paths
        RunStandard(dbe, entityIds, 10);
        RunArchetypeAccessor(dbe, entityIds, 10);

        // ── Benchmark: Standard path ────────────────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = Stopwatch.StartNew();
        RunStandard(dbe, entityIds, iterations);
        sw.Stop();
        double standardUs = sw.Elapsed.TotalMicroseconds;
        double standardPerEntity = standardUs / (iterations * entityCount) * 1000; // ns

        // ── Benchmark: ArchetypeAccessor path ───────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunArchetypeAccessor(dbe, entityIds, iterations);
        sw.Stop();
        double accessorUs = sw.Elapsed.TotalMicroseconds;
        double accessorPerEntity = accessorUs / (iterations * entityCount) * 1000; // ns

        Console.WriteLine();
        Console.WriteLine($"  Standard EntityAccessor:    {standardUs / iterations,8:F0} µs/iter  ({standardPerEntity:F1} ns/entity)");
        Console.WriteLine($"  ArchetypeAccessor:          {accessorUs / iterations,8:F0} µs/iter  ({accessorPerEntity:F1} ns/entity)");
        Console.WriteLine($"  Speedup:                    {standardUs / accessorUs:F2}x");
        Console.WriteLine();
    }

    /// <summary>Run with standard EntityAccessor.Open/OpenMut path (for profiling).</summary>
    public static void ProfileStandard(int entityCount = 50_000, int iterations = 500)
    {
        using var env = CreateEnv(entityCount);
        RunStandard(env.Dbe, env.EntityIds, 10); // warmup
        RunStandard(env.Dbe, env.EntityIds, iterations);
    }

    /// <summary>Run with ArchetypeAccessor path (for profiling).</summary>
    public static void ProfileAccessor(int entityCount = 50_000, int iterations = 500)
    {
        using var env = CreateEnv(entityCount);
        RunArchetypeAccessor(env.Dbe, env.EntityIds, 10); // warmup
        RunArchetypeAccessor(env.Dbe, env.EntityIds, iterations);
    }

    /// <summary>Run both runtime paths at increasing entity counts to find saturation.</summary>
    public static void ScaleTest()
    {
        int[] counts = [50_000, 100_000, 200_000, 500_000];
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Runtime Scale Test — Movement system only, 4 workers, 60Hz target");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  {"Entities",10} | {"Std tick",10} | {"AA tick",10} | {"Speedup",8} | {"Std ns/e",10} | {"AA ns/e",10}");
        Console.WriteLine(new string('-', 75));

        foreach (var n in counts)
        {
            var (stdMs, aaMs) = MeasureRuntimePair(n);
            double stdNs = stdMs * 1_000_000 / n;
            double aaNs = aaMs * 1_000_000 / n;
            Console.WriteLine($"  {n,10:N0} | {stdMs,8:F2}ms | {aaMs,8:F2}ms | {stdMs / aaMs,7:F2}x | {stdNs,8:F1}ns | {aaNs,8:F1}ns");
        }
        Console.WriteLine();
    }

    static (double stdMs, double aaMs) MeasureRuntimePair(int entityCount)
    {
        double stdMs = MeasureRuntimeTick(entityCount, useAccessor: false);
        double aaMs = MeasureRuntimeTick(entityCount, useAccessor: true);
        return (stdMs, aaMs);
    }

    static double MeasureRuntimeTick(int entityCount, bool useAccessor)
    {
        using var env = CreateEnv(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        var view = txView.Query<AaBenchAnt>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.QuerySystem("Movement", ctx =>
            {
                if (useAccessor)
                {
                    var ants = ctx.Accessor.For<AaBenchAnt>();
                    foreach (var id in ctx.Entities)
                    {
                        var entity = ants.OpenMut(id);
                        ref var pos = ref entity.Write(AaBenchAnt.Position);
                        ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                        pos.X += mov.VX * ctx.DeltaTime;
                        pos.Y += mov.VY * ctx.DeltaTime;
                        if (pos.X < 0f) pos.X += WorldSize;
                        else if (pos.X >= WorldSize) pos.X -= WorldSize;
                        if (pos.Y < 0f) pos.Y += WorldSize;
                        else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                    }
                    ants.Dispose();
                }
                else
                {
                    foreach (var id in ctx.Entities)
                    {
                        var entity = ctx.Accessor.OpenMut(id);
                        ref var pos = ref entity.Write(AaBenchAnt.Position);
                        ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                        pos.X += mov.VX * ctx.DeltaTime;
                        pos.Y += mov.VY * ctx.DeltaTime;
                        if (pos.X < 0f) pos.X += WorldSize;
                        else if (pos.X >= WorldSize) pos.X -= WorldSize;
                        if (pos.Y < 0f) pos.Y += WorldSize;
                        else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                    }
                }
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        // Let it stabilize for 2s, then sample for 3s
        System.Threading.Thread.Sleep(2000);
        var telemetry = runtime.Telemetry;
        long startTick = telemetry.NewestTick;
        System.Threading.Thread.Sleep(3000);
        long endTick = telemetry.NewestTick;

        // Average system duration across sampled ticks
        double totalSystemUs = 0;
        int samples = 0;
        for (long t = startTick + 1; t <= endTick && t >= telemetry.OldestAvailableTick; t++)
        {
            var systems = telemetry.GetSystemMetrics(t);
            if (systems.Length > 0 && !systems[0].WasSkipped)
            {
                totalSystemUs += systems[0].DurationUs;
                samples++;
            }
        }

        runtime.Shutdown();
        view.Dispose();

        return samples > 0 ? totalSystemUs / samples / 1000.0 : 0; // ms
    }

    /// <summary>Profile the STANDARD path through TyphonRuntime (parallel QuerySystem + PTA), same as AntHill.</summary>
    public static void ProfileRuntimeStandard(int entityCount = 50_000, int runSeconds = 10)
    {
        Console.WriteLine($"ProfileRuntimeStandard: {entityCount:N0} entities, {runSeconds}s via TyphonRuntime");
        using var env = CreateEnv(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        var view = txView.Query<AaBenchAnt>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.QuerySystem("Movement", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    var entity = ctx.Accessor.OpenMut(id);
                    ref var pos = ref entity.Write(AaBenchAnt.Position);
                    ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                    pos.X += mov.VX * ctx.DeltaTime;
                    pos.Y += mov.VY * ctx.DeltaTime;
                    if (pos.X < 0f) pos.X += WorldSize;
                    else if (pos.X >= WorldSize) pos.X -= WorldSize;
                    if (pos.Y < 0f) pos.Y += WorldSize;
                    else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                }
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        System.Threading.Thread.Sleep(runSeconds * 1000);
        runtime.Shutdown();
        view.Dispose();
        Console.WriteLine("Done.");
    }

    /// <summary>Profile the ARCHETYPE ACCESSOR path through TyphonRuntime, same as optimized AntHill.</summary>
    public static void ProfileRuntimeAccessor(int entityCount = 100_000, int runSeconds = 10)
    {
        Console.WriteLine($"ProfileRuntimeAccessor: {entityCount:N0} entities, {runSeconds}s via TyphonRuntime");
        using var env = CreateEnv(entityCount);
        using var txView = env.Dbe.CreateQuickTransaction();
        var view = txView.Query<AaBenchAnt>().ToView();

        using var runtime = TyphonRuntime.Create(env.Dbe, schedule =>
        {
            schedule.QuerySystem("Movement", ctx =>
            {
                var ants = ctx.Accessor.For<AaBenchAnt>();
                foreach (var id in ctx.Entities)
                {
                    var entity = ants.OpenMut(id);
                    ref var pos = ref entity.Write(AaBenchAnt.Position);
                    ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                    pos.X += mov.VX * ctx.DeltaTime;
                    pos.Y += mov.VY * ctx.DeltaTime;
                    if (pos.X < 0f) pos.X += WorldSize;
                    else if (pos.X >= WorldSize) pos.X -= WorldSize;
                    if (pos.Y < 0f) pos.Y += WorldSize;
                    else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                }
                ants.Dispose();
            }, input: () => view, parallel: true);
        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        System.Threading.Thread.Sleep(runSeconds * 1000);
        runtime.Shutdown();
        view.Dispose();
        Console.WriteLine("Done.");
    }

    static void RunStandard(DatabaseEngine dbe, EntityId[] ids, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < ids.Length; i++)
            {
                var entity = tx.OpenMut(ids[i]);
                ref var pos = ref entity.Write(AaBenchAnt.Position);
                ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                pos.X += mov.VX * 0.016f;
                pos.Y += mov.VY * 0.016f;
                if (pos.X < 0f) pos.X += WorldSize;
                else if (pos.X >= WorldSize) pos.X -= WorldSize;
                if (pos.Y < 0f) pos.Y += WorldSize;
                else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
            }
            tx.Commit();
        }
    }

    static void RunArchetypeAccessor(DatabaseEngine dbe, EntityId[] ids, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var ants = tx.For<AaBenchAnt>();
            for (int i = 0; i < ids.Length; i++)
            {
                var entity = ants.OpenMut(ids[i]);
                ref var pos = ref entity.Write(AaBenchAnt.Position);
                ref readonly var mov = ref entity.Read(AaBenchAnt.Movement);
                pos.X += mov.VX * 0.016f;
                pos.Y += mov.VY * 0.016f;
                if (pos.X < 0f) pos.X += WorldSize;
                else if (pos.X >= WorldSize) pos.X -= WorldSize;
                if (pos.Y < 0f) pos.Y += WorldSize;
                else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
            }
            ants.Dispose();
            tx.Commit();
        }
    }

    sealed class BenchEnv : IDisposable
    {
        public DatabaseEngine Dbe { get; }
        public EntityId[] EntityIds { get; }
        private readonly ServiceProvider _sp;

        public static BenchEnv Create(int entityCount, bool enableWal = false)
        {
            var name = $"AABench_{Environment.ProcessId}";
            var sc = new ServiceCollection();
            sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
              .AddResourceRegistry()
              .AddMemoryAllocator()
              .AddEpochManager()
              .AddHighResolutionSharedTimer()
              .AddDeadlineWatchdog()
              .AddScopedManagedPagedMemoryMappedFile(o =>
              {
                  o.DatabaseName = name;
                  o.DatabaseCacheSize = (ulong)(200 * 1024 * PagedMMF.PageSize);
                  o.PagesDebugPattern = false;
              })
              .AddScopedDatabaseEngine(o =>
              {
                  if (enableWal)
                  {
                      o.Wal = new WalWriterOptions { WalDirectory = Path.Combine(Path.GetTempPath(), $"AABench_wal_{Environment.ProcessId}") };
                  }
                  else
                  {
                      o.Wal = null;
                  }
              });

            var sp = sc.BuildServiceProvider();
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            var dbe = sp.GetRequiredService<DatabaseEngine>();

            Archetype<AaBenchAnt>.Touch();
            dbe.RegisterComponentFromAccessor<AaBenchPosition>();
            dbe.RegisterComponentFromAccessor<AaBenchMovement>();
            dbe.InitializeArchetypes();

            var rng = new Random(42);
            var ids = new EntityId[entityCount];
            int remaining = entityCount;
            int offset = 0;
            while (remaining > 0)
            {
                int batch = Math.Min(1000, remaining);
                remaining -= batch;
                using var tx = dbe.CreateQuickTransaction();
                for (int i = 0; i < batch; i++)
                {
                    var pos = new AaBenchPosition((float)(rng.NextDouble() * WorldSize), (float)(rng.NextDouble() * WorldSize));
                    float angle = (float)(rng.NextDouble() * Math.PI * 2);
                    float speed = 20f + (float)(rng.NextDouble() * 60);
                    var mov = new AaBenchMovement(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
                    ids[offset + i] = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
                }
                tx.Commit();
                offset += batch;
            }

            return new BenchEnv(sp, dbe, ids);
        }

        BenchEnv(ServiceProvider sp, DatabaseEngine dbe, EntityId[] ids) { _sp = sp; Dbe = dbe; EntityIds = ids; }

        public void Dispose()
        {
            Dbe?.Dispose();
            _sp?.Dispose();
            try { File.Delete($"AABench_{Environment.ProcessId}.bin"); } catch { }
            try
            {
                var walDir = Path.Combine(Path.GetTempPath(), $"AABench_wal_{Environment.ProcessId}");
                if (Directory.Exists(walDir)) { Directory.Delete(walDir, true); }
            }
            catch { }
        }
    }

    static BenchEnv CreateEnv(int entityCount) => BenchEnv.Create(entityCount);
    static BenchEnv CreateClusterEnv(int entityCount) => BenchEnv.Create(entityCount);
    static BenchEnv CreateWalEnv(int entityCount) => BenchEnv.Create(entityCount, enableWal: true);

    // ── Cluster iteration benchmark ─────────────────────────────────────

    public static void RunCluster(int entityCount = 50_000, int iterations = 500)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Cluster Iteration Benchmark — {entityCount:N0} entities, {iterations} iterations");
        Console.WriteLine("═══════════════════════════════════════════════════════");

        using var env = CreateClusterEnv(entityCount);
        var dbe = env.Dbe;
        var entityIds = env.EntityIds;

        // Warm up all three paths
        RunStandard(dbe, entityIds, 10);
        RunArchetypeAccessor(dbe, entityIds, 10);
        RunClusterIteration(dbe, 10);

        // ── Benchmark: Standard path ────────────────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RunStandard(dbe, entityIds, iterations);
        sw.Stop();
        double standardUs = sw.Elapsed.TotalMicroseconds;
        double standardPerEntity = standardUs / (iterations * entityCount) * 1000;

        // ── Benchmark: ArchetypeAccessor path ───────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunArchetypeAccessor(dbe, entityIds, iterations);
        sw.Stop();
        double accessorUs = sw.Elapsed.TotalMicroseconds;
        double accessorPerEntity = accessorUs / (iterations * entityCount) * 1000;

        // ── Benchmark: Cluster iteration path ───────────────────────
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        sw.Restart();
        RunClusterIteration(dbe, iterations);
        sw.Stop();
        double clusterUs = sw.Elapsed.TotalMicroseconds;
        double clusterPerEntity = clusterUs / (iterations * entityCount) * 1000;

        Console.WriteLine();
        Console.WriteLine($"  Standard EntityAccessor:    {standardUs / iterations,8:F0} µs/iter  ({standardPerEntity:F1} ns/entity)");
        Console.WriteLine($"  ArchetypeAccessor:          {accessorUs / iterations,8:F0} µs/iter  ({accessorPerEntity:F1} ns/entity)");
        Console.WriteLine($"  ClusterIteration:           {clusterUs / iterations,8:F0} µs/iter  ({clusterPerEntity:F1} ns/entity)");
        Console.WriteLine($"  Speedup (Cluster vs Std):   {standardUs / clusterUs:F2}x");
        Console.WriteLine($"  Speedup (Cluster vs AA):    {accessorUs / clusterUs:F2}x");
        Console.WriteLine();
    }

    static void RunClusterIteration(DatabaseEngine dbe, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var ants = tx.For<AaBenchAnt>();
            foreach (var cluster in ants.GetClusterEnumerator())
            {
                var positions = cluster.GetSpan<AaBenchPosition>(AaBenchAnt.Position);
                var movements = cluster.GetReadOnlySpan<AaBenchMovement>(AaBenchAnt.Movement);
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int idx = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref var pos = ref positions[idx];
                    ref readonly var mov = ref movements[idx];
                    pos.X += mov.VX * 0.016f;
                    pos.Y += mov.VY * 0.016f;
                    if (pos.X < 0f) pos.X += WorldSize;
                    else if (pos.X >= WorldSize) pos.X -= WorldSize;
                    if (pos.Y < 0f) pos.Y += WorldSize;
                    else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                }
            }
            ants.Dispose();
            tx.Commit();
        }
    }

    // ── Tick fence benchmark ───────────────────────────────────────────

    public static void RunTickFenceBench()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("  Tick Fence Benchmark — With WAL Serialization");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        int[] entityCounts = [10_000, 50_000, 100_000, 200_000];

        foreach (int count in entityCounts)
        {
            using var env = CreateWalEnv(count);
            var dbe = env.Dbe;
            var ids = env.EntityIds;

            // Check if cluster storage is active
            var meta = Archetype<AaBenchAnt>.Metadata;
            var clusterState = dbe._archetypeStates[meta.ArchetypeId]?.ClusterState;
            bool hasClusters = clusterState != null;

            // Warmup: write all entities + tick fence (10 rounds with drain time)
            for (int w = 0; w < 10; w++)
            {
                WriteAllEntities(dbe, ids);
                dbe.WriteTickFence(w + 1);
                Thread.Sleep(5); // Let WAL writer drain the commit buffer
            }

            // Measure: write all entities, then time WriteTickFence
            const int iterations = 50;
            var tickFenceTimes = new double[iterations];

            for (int i = 0; i < iterations; i++)
            {
                WriteAllEntities(dbe, ids);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                dbe.WriteTickFence(100 + i);
                sw.Stop();
                tickFenceTimes[i] = sw.Elapsed.TotalMicroseconds;
                Thread.Sleep(5); // Let WAL writer drain before next iteration
            }

            Array.Sort(tickFenceTimes);
            double p50 = tickFenceTimes[iterations / 2];
            double p90 = tickFenceTimes[(int)(iterations * 0.90)];
            double p99 = tickFenceTimes[(int)(iterations * 0.99)];
            double mean = 0;
            for (int i = 0; i < iterations; i++) mean += tickFenceTimes[i];
            mean /= iterations;

            Console.WriteLine($"  {count,8:N0} entities (cluster={hasClusters}):");
            Console.WriteLine($"    Mean:  {mean,10:F1} µs  ({mean / count * 1000:F2} ns/entity)");
            Console.WriteLine($"    P50:   {p50,10:F1} µs");
            Console.WriteLine($"    P90:   {p90,10:F1} µs");
            Console.WriteLine($"    P99:   {p99,10:F1} µs");
            Console.WriteLine();
        }
    }

    static void WriteAllEntities(DatabaseEngine dbe, EntityId[] ids)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < ids.Length; i++)
        {
            ref var pos = ref tx.OpenMut(ids[i]).Write(AaBenchAnt.Position);
            pos.X += 0.1f;
        }
        tx.Commit();
    }
}

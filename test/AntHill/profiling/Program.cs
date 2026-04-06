using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace AntHill.ProfileRunner;

[Component("AntHill.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    public float X, Y;
    public Position(float x, float y) { X = x; Y = y; }
}

[Component("AntHill.Movement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Movement
{
    public float VX, VY;
    public Movement(float vx, float vy) { VX = vx; VY = vy; }
}

[Archetype(100)]
partial class Ant : Archetype<Ant>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Movement> Movement = Register<Movement>();
}

public static class Program
{
    const float WorldSize = 50_000f;
    const int Stride = 12;
    const int RunSeconds = 8;

    static float[] _renderBuffer;
    static int _renderBufferWriteIdx;

    public static void Main(string[] args)
    {
        int[] antCounts = [50_000, 100_000, 200_000, 500_000, 1_000_000];
        int[] workerCounts = [1, 2, 4, 8, 16, 32];
        bool useCluster = true;

        // Parse args
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ants" && i + 1 < args.Length)
            {
                antCounts = [int.Parse(args[++i])];
            }
            else if (args[i] == "--workers" && i + 1 < args.Length)
            {
                workerCounts = [int.Parse(args[++i])];
            }
            else if (args[i] == "--no-cluster")
            {
                useCluster = false;
            }
        }

        Console.WriteLine($"AntHill ProfileRunner: {RunSeconds}s per config, cluster={useCluster}");
        Console.WriteLine($"{"Ants",10} {"Workers",8} {"Tick ms",10} {"AntMov µs",12} {"FillBuf µs",12} {"ns/ent",10} {"Mode",12}");
        Console.WriteLine(new string('─', 76));

        foreach (var antCount in antCounts)
        {
            foreach (var workerCount in workerCounts)
            {
                RunConfig(antCount, workerCount, useCluster);
            }
        }
    }

    static void RunConfig(int antCount, int workerCount, bool useCluster)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(cfg => cfg.AddConsole().SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                opt.DatabaseName = $"AntHillProfile_{antCount}_{workerCount}";
                opt.DatabaseDirectory = AppContext.BaseDirectory;
                opt.DatabaseCacheSize = 512 * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(opt => { opt.Wal = null; });

        using var sp = services.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var scope = sp.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Archetype<Ant>.Touch();
        dbe.RegisterComponentFromAccessor<Position>();
        dbe.RegisterComponentFromAccessor<Movement>();
        dbe.InitializeArchetypes();

        // Spawn
        var rng = new Random(42);
        int remaining = antCount;
        while (remaining > 0)
        {
            int count = Math.Min(1000, remaining);
            remaining -= count;
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                var pos = new Position((float)(rng.NextDouble() * WorldSize), (float)(rng.NextDouble() * WorldSize));
                float angle = (float)(rng.NextDouble() * Math.PI * 2);
                float speed = 20f + (float)(rng.NextDouble() * 60);
                var mov = new Movement(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
                tx.Spawn<Ant>(Ant.Position.Set(in pos), Ant.Movement.Set(in mov));
            }
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var antView = txView.Query<Ant>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            if (useCluster)
            {
                // Cluster iteration path — scoped per worker
                schedule.QuerySystem("AntMovement", ctx =>
                {
                    var ants = ctx.Accessor.For<Ant>();
                    using var clusters = ctx.EndClusterIndex > ctx.StartClusterIndex
                        ? ants.GetClusterEnumerator(ctx.StartClusterIndex, ctx.EndClusterIndex)
                        : ants.GetClusterEnumerator();
                    foreach (var cluster in clusters)
                    {
                        ulong bits = cluster.OccupancyBits;
                        var positions = cluster.GetSpan(Ant.Position);
                        var movements = cluster.GetReadOnlySpan(Ant.Movement);
                        float dt = ctx.DeltaTime;
                        while (bits != 0)
                        {
                            int idx = BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            ref var pos = ref positions[idx];
                            ref readonly var mov = ref movements[idx];
                            pos.X += mov.VX * dt;
                            pos.Y += mov.VY * dt;
                            if (pos.X < 0f) pos.X += WorldSize;
                            else if (pos.X >= WorldSize) pos.X -= WorldSize;
                            if (pos.Y < 0f) pos.Y += WorldSize;
                            else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                        }
                    }
                }, input: () => antView, parallel: true);

                schedule.CallbackSystem("PrepareRenderBuffer", ctx =>
                {
                    int needed = antView.Count * Stride;
                    if (_renderBuffer == null || _renderBuffer.Length != needed)
                        _renderBuffer = new float[needed];
                    _renderBufferWriteIdx = 0;
                }, after: "AntMovement");

                schedule.QuerySystem("FillRenderBuffer", ctx =>
                {
                    var ants = ctx.Accessor.For<Ant>();
                    using var clusters = ctx.EndClusterIndex > ctx.StartClusterIndex
                        ? ants.GetClusterEnumerator(ctx.StartClusterIndex, ctx.EndClusterIndex)
                        : ants.GetClusterEnumerator();
                    foreach (var cluster in clusters)
                    {
                        int liveCount = cluster.LiveCount;
                        int startSlot = Interlocked.Add(ref _renderBufferWriteIdx, liveCount) - liveCount;
                        int localIdx = 0;
                        ulong bits = cluster.OccupancyBits;
                        var positions = cluster.GetReadOnlySpan(Ant.Position);
                        while (bits != 0)
                        {
                            int idx = BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            ref readonly var pos = ref positions[idx];
                            int off = (startSlot + localIdx) * Stride;
                            _renderBuffer[off + 0] = 1f;  _renderBuffer[off + 1] = 0f;
                            _renderBuffer[off + 2] = 0f;  _renderBuffer[off + 3] = pos.X;
                            _renderBuffer[off + 4] = 0f;  _renderBuffer[off + 5] = 1f;
                            _renderBuffer[off + 6] = 0f;  _renderBuffer[off + 7] = pos.Y;
                            _renderBuffer[off + 8] = 1f;  _renderBuffer[off + 9] = 1f;
                            _renderBuffer[off + 10] = 1f; _renderBuffer[off + 11] = 1f;
                            localIdx++;
                        }
                    }
                }, input: () => antView, parallel: true, after: "PrepareRenderBuffer");
            }
            else
            {
                // Per-entity iteration path (baseline)
                schedule.QuerySystem("AntMovement", ctx =>
                {
                    var ants = ctx.Accessor.For<Ant>();
                    foreach (var id in ctx.Entities)
                    {
                        var entity = ants.OpenMut(id);
                        ref var pos = ref entity.Write(Ant.Position);
                        ref readonly var mov = ref entity.Read(Ant.Movement);
                        pos.X += mov.VX * ctx.DeltaTime;
                        pos.Y += mov.VY * ctx.DeltaTime;
                        if (pos.X < 0f) pos.X += WorldSize;
                        else if (pos.X >= WorldSize) pos.X -= WorldSize;
                        if (pos.Y < 0f) pos.Y += WorldSize;
                        else if (pos.Y >= WorldSize) pos.Y -= WorldSize;
                    }
                    ants.Dispose();
                }, input: () => antView, parallel: true);

                schedule.CallbackSystem("PrepareRenderBuffer", ctx =>
                {
                    int needed = antView.Count * Stride;
                    if (_renderBuffer == null || _renderBuffer.Length != needed)
                        _renderBuffer = new float[needed];
                    _renderBufferWriteIdx = 0;
                }, after: "AntMovement");

                schedule.QuerySystem("FillRenderBuffer", ctx =>
                {
                    int chunkCount = ctx.Entities.Count;
                    int startSlot = Interlocked.Add(ref _renderBufferWriteIdx, chunkCount) - chunkCount;
                    int localIdx = 0;
                    var ants = ctx.Accessor.For<Ant>();
                    foreach (var id in ctx.Entities)
                    {
                        var entity = ants.Open(id);
                        ref readonly var pos = ref entity.Read(Ant.Position);
                        int off = (startSlot + localIdx) * Stride;
                        _renderBuffer[off + 0] = 1f;  _renderBuffer[off + 1] = 0f;
                        _renderBuffer[off + 2] = 0f;  _renderBuffer[off + 3] = pos.X;
                        _renderBuffer[off + 4] = 0f;  _renderBuffer[off + 5] = 1f;
                        _renderBuffer[off + 6] = 0f;  _renderBuffer[off + 7] = pos.Y;
                        _renderBuffer[off + 8] = 1f;  _renderBuffer[off + 9] = 1f;
                        _renderBuffer[off + 10] = 1f; _renderBuffer[off + 11] = 1f;
                        localIdx++;
                    }
                    ants.Dispose();
                }, input: () => antView, parallel: true, after: "PrepareRenderBuffer");
            }

            schedule.CallbackSystem("PublishRenderFrame", ctx => { }, after: "FillRenderBuffer");

        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = workerCount });

        runtime.Start();

        // Warm up for 2 seconds, then measure for RunSeconds
        Thread.Sleep(2000);

        var telemetry = runtime.Telemetry;
        long startTick = telemetry.NewestTick;
        Thread.Sleep(RunSeconds * 1000);
        long endTick = telemetry.NewestTick;

        // Collect metrics from the last tick
        if (telemetry.TotalTicksRecorded > 0)
        {
            ref readonly var tick = ref telemetry.GetTick(endTick);
            var systems = telemetry.GetSystemMetrics(endTick);
            var sysDefs = runtime.Systems;

            double tickMs = tick.ActualDurationMs;
            double antMovUs = 0, fillBufUs = 0;
            for (int s = 0; s < systems.Length && s < sysDefs.Length; s++)
            {
                if (sysDefs[s].Name == "AntMovement") antMovUs = systems[s].DurationUs;
                if (sysDefs[s].Name == "FillRenderBuffer") fillBufUs = systems[s].DurationUs;
            }
            double nsPerEnt = antMovUs * 1000.0 / antCount;
            string mode = useCluster ? "cluster" : "per-entity";

            Console.WriteLine($"{antCount,10:N0} {workerCount,8} {tickMs,10:F2} {antMovUs,12:F0} {fillBufUs,12:F0} {nsPerEnt,10:F1} {mode,12}");
        }

        runtime.Shutdown();
        try { antView.Dispose(); } catch { /* no-WAL mode may throw */ }
        try { scope.Dispose(); } catch { /* ignore */ }
        try { sp.Dispose(); } catch { /* ignore shutdown errors in no-WAL mode */ }
    }
}

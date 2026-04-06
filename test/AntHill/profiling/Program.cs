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
    const int AntCount = 50_000;
    const float WorldSize = 10_000f;
    const int Stride = 12;
    const int RunSeconds = 10;

    static float[] _renderBuffer;
    static int _renderBufferWriteIdx;

    public static void Main()
    {
        Console.WriteLine($"AntHill ProfileRunner: {AntCount:N0} ants, {RunSeconds}s run");

        var services = new ServiceCollection();
        services
            .AddLogging(cfg => cfg.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                opt.DatabaseName = "AntHillProfile";
                opt.DatabaseDirectory = AppContext.BaseDirectory;
                opt.DatabaseCacheSize = 128 * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(opt => { opt.Wal = null; });

        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Archetype<Ant>.Touch();
        dbe.RegisterComponentFromAccessor<Position>();
        dbe.RegisterComponentFromAccessor<Movement>();
        dbe.InitializeArchetypes();

        // Spawn
        var rng = new Random(42);
        int remaining = AntCount;
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
        Console.WriteLine($"Spawned {AntCount:N0} ants.");

        using var txView = dbe.CreateQuickTransaction();
        var antView = txView.Query<Ant>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.QuerySystem("AntMovement", ctx =>
            {
                var ants = ctx.Accessor.For<Ant>();
                using var clusters = ants.GetClusterEnumerator();
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
                int c = antView.Count;
                int needed = c * Stride;
                if (_renderBuffer == null || _renderBuffer.Length != needed)
                    _renderBuffer = new float[needed];
                _renderBufferWriteIdx = 0;
            }, after: "AntMovement");

            schedule.QuerySystem("FillRenderBuffer", ctx =>
            {
                var ants = ctx.Accessor.For<Ant>();
                using var clusters = ants.GetClusterEnumerator();
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
                        _renderBuffer[off + 0] = 1f;
                        _renderBuffer[off + 1] = 0f;
                        _renderBuffer[off + 2] = 0f;
                        _renderBuffer[off + 3] = pos.X;
                        _renderBuffer[off + 4] = 0f;
                        _renderBuffer[off + 5] = 1f;
                        _renderBuffer[off + 6] = 0f;
                        _renderBuffer[off + 7] = pos.Y;
                        _renderBuffer[off + 8] = 1f;
                        _renderBuffer[off + 9] = 1f;
                        _renderBuffer[off + 10] = 1f;
                        _renderBuffer[off + 11] = 1f;
                        localIdx++;
                    }
                }
            }, input: () => antView, parallel: true, after: "PrepareRenderBuffer");

            schedule.CallbackSystem("PublishRenderFrame", ctx => { }, after: "FillRenderBuffer");

        }, new RuntimeOptions { BaseTickRate = 60, WorkerCount = 4 });

        runtime.Start();
        Console.WriteLine($"Runtime started. Profiling for {RunSeconds}s...");

        for (int elapsed = 0; elapsed < RunSeconds; elapsed += 2)
        {
            Thread.Sleep(2000);
            var telemetry = runtime.Telemetry;
            if (telemetry.TotalTicksRecorded > 0)
            {
                var tickNum = telemetry.NewestTick;
                ref readonly var tick = ref telemetry.GetTick(tickNum);
                var systems = telemetry.GetSystemMetrics(tickNum);
                var sysDefs = runtime.Systems;
                Console.Write($"[Perf] Tick {tick.ActualDurationMs:F1}ms");
                for (int i = 0; i < systems.Length && i < sysDefs.Length; i++)
                {
                    ref readonly var s = ref systems[i];
                    if (!s.WasSkipped)
                        Console.Write($" | {sysDefs[i].Name}: {s.DurationUs:F0}us/{s.EntitiesProcessed}e");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("Done.");
        runtime.Shutdown();
    }
}

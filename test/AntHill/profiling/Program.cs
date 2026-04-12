using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Profiler;
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
    const float WorldSize = 20_000f;
    const int RunSeconds = 8;

    public static void Main(string[] args)
    {
        int antCount = 100_000;
        int[] workerCounts = [4, 8, 16, 30];
        string traceFile = null;
        int livePort = -1;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ants" && i + 1 < args.Length)
            {
                antCount = int.Parse(args[++i]);
            }
            else if (args[i] == "--workers" && i + 1 < args.Length)
            {
                workerCounts = [int.Parse(args[++i])];
            }
            else if (args[i] == "--trace")
            {
                traceFile = i + 1 < args.Length && !args[i + 1].StartsWith("--")
                    ? args[++i]
                    : Path.Combine(AppContext.BaseDirectory, "anthill.typhon-trace");
            }
            else if (args[i] == "--live")
            {
                livePort = i + 1 < args.Length && int.TryParse(args[i + 1], out var p)
                    ? args[++i] != null ? p : 9001
                    : 9001;
            }
        }

        if (livePort >= 0)
        {
            Console.WriteLine($"AntHill ProfileRunner: LIVE MODE — {antCount:N0} ants, {workerCounts[0]} workers, port {livePort}");
            Console.WriteLine($"  Listening on port {livePort}... Press Ctrl+C to stop");
            RunConfig(antCount, workerCounts[0], null, livePort);
            return;
        }

        if (traceFile != null)
        {
            Console.WriteLine($"AntHill ProfileRunner: TRACE MODE — {antCount:N0} ants, {workerCounts[0]} workers, {RunSeconds}s");
            Console.WriteLine($"  Trace file: {traceFile}");
            RunConfig(antCount, workerCounts[0], traceFile);

            // Export to Chrome Trace JSON for immediate visualization
            var jsonFile = Path.ChangeExtension(traceFile, ".json");
            Console.WriteLine($"  Exporting Chrome Trace to: {jsonFile}");
            using var jsonStream = File.Create(jsonFile);
            ChromeTraceExporter.Export(traceFile, jsonStream);
            Console.WriteLine($"  Done! Open {jsonFile} in https://ui.perfetto.dev");
            return;
        }

        Console.WriteLine($"AntHill ProfileRunner: {RunSeconds}s per config, {antCount:N0} ants");
        Console.WriteLine($"{"Workers",8} {"Tick p50",10} {"Tick p99",10} {"AntMov p50",12} {"AntMov p99",12} {"ns/ent",10} {"Ticks",8}");
        Console.WriteLine(new string('─', 76));

        foreach (var workerCount in workerCounts)
        {
            RunConfig(antCount, workerCount);
        }
    }

    static void RunConfig(int antCount, int workerCount, string traceFile = null, int livePort = -1)
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
            schedule.QuerySystem("AntMovement", ctx =>
            {
                using var clusters = ctx.EndClusterIndex > ctx.StartClusterIndex
                    ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex)
                    : ctx.Accessor.GetClusterEnumerator<Ant>();
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
        }, new RuntimeOptions
        {
            BaseTickRate = 60,
            WorkerCount = workerCount,
            Inspector = livePort >= 0
                ? new TcpStreamInspector(livePort)
                : traceFile != null ? new TraceFileInspector(traceFile) : null
        });

        runtime.Start();

        // Live mode: run until Ctrl+C
        if (livePort >= 0)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); } catch (AggregateException) { }
            Console.WriteLine("\nShutting down...");
            runtime.Shutdown();
            try { antView.Dispose(); } catch { }
            try { scope.Dispose(); } catch { }
            try { sp.Dispose(); } catch { }
            return;
        }

        // Warm up 2s, then measure
        Thread.Sleep(2000);
        var telemetry = runtime.Telemetry;
        long startTick = telemetry.NewestTick;
        Thread.Sleep(RunSeconds * 1000);
        long endTick = telemetry.NewestTick;

        if (telemetry.TotalTicksRecorded > 0 && endTick > startTick)
        {
            var sysDefs = runtime.Systems;
            int antMovIdx = -1;
            for (int s = 0; s < sysDefs.Length; s++)
            {
                if (sysDefs[s].Name == "AntMovement") { antMovIdx = s; break; }
            }

            // Clamp to ring buffer's available range
            long oldest = telemetry.OldestAvailableTick;
            long from = Math.Max(startTick + 1, oldest);
            int count = (int)(endTick - from + 1);
            var tickDurations = new float[count];
            var antMovDurations = new float[count];

            for (int i = 0; i < count; i++)
            {
                long t = from + i;
                ref readonly var tick = ref telemetry.GetTick(t);
                tickDurations[i] = tick.ActualDurationMs;
                if (antMovIdx >= 0)
                {
                    var systems = telemetry.GetSystemMetrics(t);
                    antMovDurations[i] = systems[antMovIdx].DurationUs;
                }
            }

            Array.Sort(tickDurations);
            Array.Sort(antMovDurations);
            float tickP50 = tickDurations[count / 2];
            float tickP99 = tickDurations[(int)(count * 0.99)];
            float antP50 = antMovDurations[count / 2];
            float antP99 = antMovDurations[(int)(count * 0.99)];
            double nsPerEnt = antP50 * 1000.0 / antCount;

            Console.WriteLine($"{workerCount,8} {tickP50,10:F2} {tickP99,10:F2} {antP50,12:F0} {antP99,12:F0} {nsPerEnt,10:F1} {count,8}");
        }

        runtime.Shutdown();
        try { antView.Dispose(); } catch { }
        try { scope.Dispose(); } catch { }
        try { sp.Dispose(); } catch { }
    }
}

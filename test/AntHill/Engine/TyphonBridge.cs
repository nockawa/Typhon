using System;
using System.Numerics;
using System.Threading;
using AntHill.ECS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace AntHill.Engine;

/// <summary>
/// Encapsulates all Typhon engine setup: DI, DatabaseEngine, entity spawning, Runtime + systems.
/// Owns the lifecycle — call Dispose to shut everything down cleanly.
/// </summary>
public sealed class TyphonBridge : IDisposable
{
    public const int AntCount = 50_000;
    public const float WorldSize = 10_000f;

    private ServiceProvider _serviceProvider;
    private IServiceScope _scope;
    private DatabaseEngine _dbe;
    private TyphonRuntime _runtime;
    private EcsView<Ant> _antView;
    private readonly RenderBridge _renderBridge = new();

    // Shared state for parallel render buffer fill
    private const int Stride = 12;
    private float[] _renderBuffer;
    private int _renderBufferWriteIdx;

    public RenderBridge RenderBridge => _renderBridge;
    public TyphonRuntime Runtime => _runtime;
    public DatabaseEngine Engine => _dbe;

    public void Initialize()
    {
        // 1. Build DI container
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
                opt.DatabaseName = "AntHill";
                opt.DatabaseDirectory = AppContext.BaseDirectory;
                opt.DatabaseCacheSize = 128 * 1024 * 1024; // 128 MB cache
            })
            .AddScopedDatabaseEngine(opt =>
            {
                // No WAL for now — pure perf testing, no persistence needed
                opt.Wal = null;
            });

        _serviceProvider = services.BuildServiceProvider();

        // Create a scope so scoped services resolve properly (must store ref to prevent GC)
        _scope = _serviceProvider.CreateScope();
        _dbe = _scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        // 2. Register components & archetypes
        Archetype<Ant>.Touch();
        _dbe.RegisterComponentFromAccessor<Position>();
        _dbe.RegisterComponentFromAccessor<Movement>();
        _dbe.InitializeArchetypes();

        // 3. Spawn ants
        SpawnAnts();

        // 4. Create view (all ants, no filter)
        using var txView = _dbe.CreateQuickTransaction();
        _antView = txView.Query<Ant>().ToView();

        // 5. Create Runtime with systems
        _runtime = TyphonRuntime.Create(_dbe, schedule =>
        {
            // Movement system: parallel, cluster iteration (fast path)
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
            }, input: () => _antView, parallel: true);

            // ── Render buffer pipeline (3 systems) ──────────────────────
            // PrepareRenderBuffer: allocate/reset shared buffer
            schedule.CallbackSystem("PrepareRenderBuffer", ctx =>
            {
                int count = _antView.Count;
                int needed = count * Stride;
                if (_renderBuffer == null || _renderBuffer.Length != needed)
                {
                    _renderBuffer = new float[needed];
                }
                _renderBufferWriteIdx = 0;
            }, after: "AntMovement");

            // FillRenderBuffer: parallel read via cluster iteration (fast path)
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
                        _renderBuffer[off + 0] = 1f;    // cos
                        _renderBuffer[off + 1] = 0f;    // -sin
                        _renderBuffer[off + 2] = 0f;    // padding
                        _renderBuffer[off + 3] = pos.X; // origin.x
                        _renderBuffer[off + 4] = 0f;    // sin
                        _renderBuffer[off + 5] = 1f;    // cos
                        _renderBuffer[off + 6] = 0f;    // padding
                        _renderBuffer[off + 7] = pos.Y; // origin.y
                        _renderBuffer[off + 8] = 1f;    // R
                        _renderBuffer[off + 9] = 1f;    // G
                        _renderBuffer[off + 10] = 1f;   // B
                        _renderBuffer[off + 11] = 1f;   // A
                        localIdx++;
                    }
                }
            }, input: () => _antView, parallel: true, after: "PrepareRenderBuffer");

            // PublishRenderFrame: push completed buffer to Godot
            schedule.CallbackSystem("PublishRenderFrame", ctx =>
            {
                int count = _renderBufferWriteIdx;
                if (count > 0)
                {
                    _renderBridge.Publish(new RenderFrame { Buffer = _renderBuffer, Count = count });
                }
            }, after: "FillRenderBuffer");

        }, new RuntimeOptions
        {
            BaseTickRate = 60,
            WorkerCount = 4
        });
    }

    public void Start() => _runtime.Start();

    private void SpawnAnts()
    {
        var rng = new Random(42);
        const int batchSize = 1_000;
        int remaining = AntCount;

        while (remaining > 0)
        {
            int count = Math.Min(batchSize, remaining);
            remaining -= count;
            using var tx = _dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                // Random position across the world
                var pos = new Position(
                    (float)(rng.NextDouble() * WorldSize),
                    (float)(rng.NextDouble() * WorldSize));

                // Random velocity: speed 20-80 units/sec, random direction
                float angle = (float)(rng.NextDouble() * Math.PI * 2);
                float speed = 20f + (float)(rng.NextDouble() * 60);
                var mov = new Movement(
                    MathF.Cos(angle) * speed,
                    MathF.Sin(angle) * speed);

                tx.Spawn<Ant>(
                    Ant.Position.Set(in pos),
                    Ant.Movement.Set(in mov));
            }

            tx.Commit();
        }
    }

    /// <summary>Get per-system timing info from the latest tick for display.</summary>
    public string GetTimingInfo()
    {
        var telemetry = _runtime?.Telemetry;
        if (telemetry == null || telemetry.TotalTicksRecorded == 0) return "no ticks";

        try
        {
            var tickNum = telemetry.NewestTick;
            ref readonly var tick = ref telemetry.GetTick(tickNum);
            var systems = telemetry.GetSystemMetrics(tickNum);
            var sysDefs = _runtime.Systems;

            var parts = new System.Text.StringBuilder();
            parts.Append($"Tick {tick.ActualDurationMs:F1}ms");

            for (int i = 0; i < systems.Length && i < sysDefs.Length; i++)
            {
                ref readonly var s = ref systems[i];
                if (s.WasSkipped) continue;
                parts.Append($" | {sysDefs[i].Name}: {s.DurationUs:F0}us/{s.EntitiesProcessed}e");
            }

            return parts.ToString();
        }
        catch
        {
            return "telemetry error";
        }
    }

    public void Dispose()
    {
        try { _runtime?.Shutdown(); } catch { /* ignore shutdown errors */ }
        try { _runtime?.Dispose(); } catch { /* ignore */ }
        try { _antView?.Dispose(); } catch { /* ignore */ }
        try { _serviceProvider?.Dispose(); } catch { /* ignore — no-WAL mode throws on PersistArchetypeState */ }
    }
}

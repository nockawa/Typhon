using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace AntHill;

public sealed class TyphonBridge : IDisposable
{
    public const int AntCount = 200_000;
    public const int FoodCount = 50;
    public const int NestCount = 5;
    public const float WorldSize = 20_000f;
    public const float CellSize = 1000f;
    public const float InvCellSize = 1f / CellSize;
    public const int GridCells = 20; // WorldSize / CellSize

    private ServiceProvider _serviceProvider;
    private IServiceScope _scope;
    private DatabaseEngine _dbe;
    private TyphonRuntime _runtime;
    private EcsView<Ant> _antView;

    private const int Stride = 12;

    // Per-worker render buffers — each worker writes to its own, no synchronization needed
    private RenderWorkerBuffer[] _workerBuffers;
    private RenderWorkerBuffer _overlayBuffer; // food + nests

    // Pheromone heatmap double-buffered RGBA (200×200×4)
    private const int HeatmapPixels = RenderFrame.HeatmapSize * RenderFrame.HeatmapSize;
    private byte[] _heatmapRGBA = new byte[HeatmapPixels * 4];
    private byte[] _heatmapRGBARead = new byte[HeatmapPixels * 4];
    private readonly float[] _heatMaxFood = new float[HeatmapPixels];
    private readonly float[] _heatMaxHome = new float[HeatmapPixels];
    private volatile bool _heatmapEnabled;

    // Tier counts tracked during FillRenderBuffer
    private readonly int[] _tierCounts = new int[4];
    public ReadOnlySpan<int> TierCounts => _tierCounts;

    // Ant state counters
    private readonly int[] _stateCounts = new int[2]; // [0]=Foraging, [1]=Carrying
    public ReadOnlySpan<int> StateCounts => _stateCounts;

    // Camera world-space AABB (updated from Godot render thread)
    private volatile float _camMinX;
    private volatile float _camMinY;
    private volatile float _camMaxX = 20_000f;
    private volatile float _camMaxY = 20_000f;

    // Time control
    private volatile float _timeScale = 1f;
    public float TimeScale { get => _timeScale; set => _timeScale = value; }

    // Tier radii
    private float _tier0Radius = 2000f;
    private const float Tier2Radius = 8000f;

    // Tier mirror for rendering — linear indexed [cy * GridCells + cx]
    private readonly byte[] _tierMirror = new byte[GridCells * GridCells];

    // Nest data
    private (float x, float y)[] _nestPositions;
    private int[] _nestFoodStock;
    private const int InitialNestFood = 10_000;

    // Food data
    private (float x, float y, float remaining)[] _foodCache;
    private int[] _foodRemainingInt;
    private int _foodDelivered;
    private int _deathCount;

    // Food spatial grid: 40×40 cells (500-unit cells), per-cell list of food indices
    private const int FoodGridCells = 40;
    private const float FoodGridCellSize = WorldSize / FoodGridCells; // 500
    private const float FoodGridInvCellSize = 1f / FoodGridCellSize;
    private int[][] _foodGrid; // [cellIndex] → array of food source indices (null = empty)

    // Pheromone grid
    private readonly PheromoneGrid _pheromones = new();

    // Render bridge
    private readonly RenderBridge _renderBridge = new();
    public RenderBridge RenderBridge => _renderBridge;

    // Migration tracking
    private int _cellCrossingsThisTick;
    private int _crossingsAccum;
    private int _crossingsTickCount;
    public int CrossingsPerSecond { get; private set; }

    // Public stats
    public int VisibleAnts { get; private set; }
    public int FoodDelivered => _foodDelivered;
    public int DeathCount => _deathCount;
    public int TotalNestFood
    {
        get
        {
            if (_nestFoodStock == null) return 0;
            int total = 0;
            for (int i = 0; i < _nestFoodStock.Length; i++) total += Math.Max(0, _nestFoodStock[i]);
            return total;
        }
    }
    public int FoodSourcesRemaining
    {
        get
        {
            if (_foodRemainingInt == null) return 0;
            int count = 0;
            for (int i = 0; i < _foodRemainingInt.Length; i++)
            {
                if (_foodRemainingInt[i] > 0) count++;
            }
            return count;
        }
    }

    public void UpdateCamera(float minX, float minY, float maxX, float maxY)
    {
        _camMinX = minX;
        _camMinY = minY;
        _camMaxX = maxX;
        _camMaxY = maxY;
    }

    public void Initialize(IRuntimeInspector inspector = null)
    {
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
                opt.DatabaseCacheSize = 512 * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(opt =>
            {
                opt.Wal = new WalWriterOptions();
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _scope = _serviceProvider.CreateScope();
        _dbe = _scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Archetype<Ant>.Touch();
        Archetype<Food>.Touch();
        Archetype<Nest>.Touch();
        _dbe.RegisterComponentFromAccessor<Position>();
        _dbe.RegisterComponentFromAccessor<Genetics>();
        _dbe.RegisterComponentFromAccessor<AntState>();
        _dbe.RegisterComponentFromAccessor<FoodSource>();
        _dbe.RegisterComponentFromAccessor<NestInfo>();

        _dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: Vector2.Zero,
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize,
            migrationHysteresisRatio: 0.05f));

        _dbe.InitializeArchetypes();

        SpawnNests();
        SpawnFood();
        SpawnAnts();

        using var txView = _dbe.CreateQuickTransaction();
        _antView = txView.Query<Ant>().ToView();

        const int workerCount = 16;
        _runtime = TyphonRuntime.Create(_dbe, BuildSystemDAG, new RuntimeOptions
        {
            BaseTickRate = 60,
            WorkerCount = workerCount,
            Inspector = inspector,
        });

        // Per-worker render buffers: each parallel FillRender worker writes to its own buffer
        _workerBuffers = new RenderWorkerBuffer[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workerBuffers[i] = new RenderWorkerBuffer(AntCount / workerCount + 1024);
        }
        _overlayBuffer = new RenderWorkerBuffer(FoodCount + NestCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // System DAG
    // ═══════════════════════════════════════════════════════════════════

    private void BuildSystemDAG(RuntimeSchedule schedule)
    {
        schedule.CallbackSystem("TierAssignment", TierAssignment, priority: SystemPriority.High);

        // MoveAll and Metabolism are INDEPENDENT — run in parallel across all workers
        schedule.QuerySystem("MoveAll", MoveAllAnts,
            input: () => _antView, parallel: true, after: "TierAssignment");
        schedule.QuerySystem("Metabolism_T0", MetabolismTick,
            input: () => _antView, tier: SimTier.Tier0, cellAmortize: 1, parallel: true, after: "TierAssignment");
        schedule.QuerySystem("Metabolism_T1", MetabolismTick,
            input: () => _antView, tier: SimTier.Tier1, cellAmortize: 8, parallel: true, after: "TierAssignment");
        schedule.QuerySystem("Metabolism_T2", MetabolismTick,
            input: () => _antView, tier: SimTier.Tier2, cellAmortize: 30, parallel: true, after: "TierAssignment");
        schedule.QuerySystem("Metabolism_T3", MetabolismTick,
            input: () => _antView, tier: SimTier.Tier3, cellAmortize: 60, parallel: true, after: "TierAssignment");

        // FoodDetect: lightweight food proximity — every tick, all ants. Handles smell + pickup + nest drop.
        schedule.QuerySystem("FoodDetect", FoodDetectTick,
            input: () => _antView, parallel: true, after: "MoveAll");

        // AntBrain: pheromone sensing + steering + wander. After FoodDetect (state may have changed)
        schedule.QuerySystem("Brain_T0", AntBrainTick,
            input: () => _antView, tier: SimTier.Tier0, cellAmortize: 1, parallel: true,
            afterAll: new[] { "FoodDetect", "Metabolism_T0" });
        schedule.QuerySystem("Brain_T1", AntBrainTick,
            input: () => _antView, tier: SimTier.Tier1, cellAmortize: 8, parallel: true,
            afterAll: new[] { "FoodDetect", "Metabolism_T1" });
        schedule.QuerySystem("Brain_T2", AntBrainTick,
            input: () => _antView, tier: SimTier.Tier2, cellAmortize: 30, parallel: true,
            afterAll: new[] { "FoodDetect", "Metabolism_T2" });
        schedule.QuerySystem("Brain_T3", AntBrainTick,
            input: () => _antView, tier: SimTier.Tier3, cellAmortize: 60, parallel: true,
            afterAll: new[] { "FoodDetect", "Metabolism_T3" });

        // PheromoneDeposit: after Brain (state transitions may have changed)
        schedule.QuerySystem("PheroDep_T0", PheromoneDepositTick,
            input: () => _antView, tier: SimTier.Tier0, cellAmortize: 1, parallel: true, after: "Brain_T0");
        schedule.QuerySystem("PheroDep_T1", PheromoneDepositTick,
            input: () => _antView, tier: SimTier.Tier1, cellAmortize: 2, parallel: true, after: "Brain_T1");
        schedule.QuerySystem("PheroDep_T2", PheromoneDepositTick,
            input: () => _antView, tier: SimTier.Tier2, cellAmortize: 4, parallel: true, after: "Brain_T2");
        schedule.QuerySystem("PheroDep_T3", PheromoneDepositTick,
            input: () => _antView, tier: SimTier.Tier3, cellAmortize: 8, parallel: true, after: "Brain_T3");

        // PheromoneDecay: evaporate grid after deposits are done
        schedule.CallbackSystem("PheroDecay", PheromoneDecayTick,
            afterAll: new[] { "PheroDep_T0", "PheroDep_T1", "PheroDep_T2", "PheroDep_T3" });

        // Render pipeline
        schedule.CallbackSystem("PrepareRenderBuffer", PrepareRender,
            afterAll: new[] { "Brain_T0", "Brain_T1", "Brain_T2", "Brain_T3", "PheroDecay" });
        schedule.QuerySystem("FillRenderBuffer", FillRender,
            input: () => _antView, parallel: true, after: "PrepareRenderBuffer");
        schedule.CallbackSystem("PublishRenderFrame", PublishRender, after: "FillRenderBuffer");
    }

    // ═══════════════════════════════════════════════════════════════════
    // TierAssignment
    // ═══════════════════════════════════════════════════════════════════

    private void TierAssignment(TickContext ctx)
    {
        float camX = (_camMinX + _camMaxX) * 0.5f;
        float camY = (_camMinY + _camMaxY) * 0.5f;

        var grid = ctx.SpatialGrid;
        grid.ResetAllTiers(SimTier.Tier3);

        float r0sq = _tier0Radius * _tier0Radius;
        float r1sq = (_tier0Radius * 3f) * (_tier0Radius * 3f);
        float r2sq = Tier2Radius * Tier2Radius;

        for (int cy = 0; cy < GridCells; cy++)
        {
            for (int cx = 0; cx < GridCells; cx++)
            {
                float cellCenterX = cx * CellSize + CellSize * 0.5f;
                float cellCenterY = cy * CellSize + CellSize * 0.5f;
                float dx = cellCenterX - camX;
                float dy = cellCenterY - camY;
                float distSq = dx * dx + dy * dy;

                SimTier tier;
                if (distSq < r0sq) tier = SimTier.Tier0;
                else if (distSq < r1sq) tier = SimTier.Tier1;
                else if (distSq < r2sq) tier = SimTier.Tier2;
                else
                {
                    _tierMirror[cy * GridCells + cx] = 3;
                    continue;
                }

                grid.SetCellTier(cx, cy, tier);
                _tierMirror[cy * GridCells + cx] = (byte)BitOperations.TrailingZeroCount((uint)(byte)tier);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // MoveAll — position update from velocity, every frame, all ants
    // ═══════════════════════════════════════════════════════════════════

    private const int AntArchetypeId = 100;

    private void MoveAllAnts(TickContext ctx)
    {
        float dt = ctx.DeltaTime * _timeScale;
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var positions = cluster.GetSpan(Ant.Position);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var pos = ref positions[idx];

                float x = pos.Bounds.MinX + pos.VelocityX * dt;
                float y = pos.Bounds.MinY + pos.VelocityY * dt;
                float vx = pos.VelocityX;
                float vy = pos.VelocityY;

                if (x < 0f) { x = -x; vx = -vx; }
                else if (x > WorldSize) { x = 2f * WorldSize - x; vx = -vx; }
                if (y < 0f) { y = -y; vy = -vy; }
                else if (y > WorldSize) { y = 2f * WorldSize - y; vy = -vy; }

                pos.Bounds.MinX = x;
                pos.Bounds.MaxX = x;
                pos.Bounds.MinY = y;
                pos.Bounds.MaxY = y;
                pos.VelocityX = vx;
                pos.VelocityY = vy;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Metabolism — energy decay + death/respawn (all tiers)
    // Reads: AntState, Genetics, Position  |  Writes: AntState, Position (on respawn)
    // ═══════════════════════════════════════════════════════════════════

    private const float BaseDt = 1f / 60f;
    private const float EnergyDrainRate = 0.15f;

    private void MetabolismTick(TickContext ctx)
    {
        float dtScale = ctx.AmortizedDeltaTime / BaseDt * _timeScale;
        var nests = _nestPositions;
        var nestStock = _nestFoodStock;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var positions = cluster.GetSpan(Ant.Position);
            var states = cluster.GetSpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var state = ref states[idx];
                ref readonly var gen = ref genetics[idx];

                state.Energy -= EnergyDrainRate * dtScale;

                if (state.Energy <= 0f)
                {
                    Interlocked.Increment(ref _deathCount);
                    int ni = gen.HomeNestIndex;

                    float freeE = gen.BaseEnergy * 0.5f;
                    float bonusE = 0f;
                    if (ni >= 0 && ni < NestCount &&
                        Interlocked.Add(ref nestStock[ni], -gen.EatAmount) >= 0)
                    {
                        bonusE = gen.BaseEnergy * 0.5f;
                    }
                    else if (ni >= 0 && ni < NestCount)
                    {
                        Interlocked.Add(ref nestStock[ni], gen.EatAmount);
                    }
                    state.Energy = freeE + bonusE;
                    state.State = AntState.Foraging;

                    // Teleport to nest + random heading immediately (same pass, no heuristic)
                    ref var pos = ref positions[idx];
                    pos.X = nests[ni].x;
                    pos.Y = nests[ni].y;

                    uint h = (uint)(idx * 2654435761 + cluster.ChunkId * 40503 + ctx.TickNumber);
                    float angle = (h % 6283u) * 0.001f; // 0 to ~2π
                    float speed = gen.Speed * 40f;
                    pos.VelocityX = MathF.Cos(angle) * speed;
                    pos.VelocityY = MathF.Sin(angle) * speed;
                    _dbe.MarkClusterSlotDirty(AntArchetypeId, cluster.ChunkId, idx);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // FoodDetect — food smell/pickup + nest drop. Every tick, all ants.
    // Reads: Position, AntState, Genetics  |  Writes: Position (heading), AntState (state)
    // ═══════════════════════════════════════════════════════════════════

    private const float FoodPickupRange = 30f;
    private const float FoodSmellRange = 250f;
    private const float NestDropRange = 40f;
    private const float FoodPickupRangeSq = FoodPickupRange * FoodPickupRange;
    private const float FoodSmellRangeSq = FoodSmellRange * FoodSmellRange;
    private const float NestDropRangeSq = NestDropRange * NestDropRange;

    private void FoodDetectTick(TickContext ctx)
    {
        var food = _foodCache;
        var foodRemaining = _foodRemainingInt;
        var foodGrid = _foodGrid;
        var nests = _nestPositions;
        var nestStock = _nestFoodStock;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var positions = cluster.GetSpan(Ant.Position);
            var states = cluster.GetSpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var pos = ref positions[idx];
                ref var state = ref states[idx];

                if (state.Energy <= 0f) continue;

                if (state.State == AntState.Foraging)
                {
                    // Grid lookup: only check food sources in this cell
                    int gcx = Math.Clamp((int)(pos.X * FoodGridInvCellSize), 0, FoodGridCells - 1);
                    int gcy = Math.Clamp((int)(pos.Y * FoodGridInvCellSize), 0, FoodGridCells - 1);
                    var candidates = foodGrid[gcy * FoodGridCells + gcx];
                    if (candidates == null) continue;

                    float bestDistSq = float.MaxValue;
                    int bestIdx = -1;
                    for (int ci = 0; ci < candidates.Length; ci++)
                    {
                        int fi = candidates[ci];
                        if (foodRemaining[fi] <= 0) continue;
                        float dx = pos.X - food[fi].x;
                        float dy = pos.Y - food[fi].y;
                        float distSq = dx * dx + dy * dy;

                        if (distSq < FoodPickupRangeSq)
                        {
                            if (Interlocked.Decrement(ref foodRemaining[fi]) >= 0)
                            {
                                state.State = AntState.ReturningFrom(fi);
                                state.Energy = genetics[idx].BaseEnergy;
                                pos.VelocityX = -pos.VelocityX;
                                pos.VelocityY = -pos.VelocityY;
                            }
                            else
                            {
                                Interlocked.Increment(ref foodRemaining[fi]);
                            }
                            bestIdx = -1;
                            break;
                        }

                        if (distSq < FoodSmellRangeSq && distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestIdx = fi;
                        }
                    }

                    if (bestIdx >= 0)
                    {
                        float heading = MathF.Atan2(food[bestIdx].y - pos.Y, food[bestIdx].x - pos.X);
                        float speed = MathF.Sqrt(pos.VelocityX * pos.VelocityX + pos.VelocityY * pos.VelocityY);
                        ref readonly var gen = ref genetics[idx];
                        if (speed < 0.01f) speed = gen.Speed * 40f;
                        pos.VelocityX = MathF.Cos(heading) * speed;
                        pos.VelocityY = MathF.Sin(heading) * speed;
                    }
                }
                else // Returning
                {
                    ref readonly var gen = ref genetics[idx];
                    int ni = gen.HomeNestIndex;
                    float dx = pos.X - nests[ni].x;
                    float dy = pos.Y - nests[ni].y;
                    if (dx * dx + dy * dy < NestDropRangeSq)
                    {
                        Interlocked.Add(ref nestStock[ni], 3);
                        Interlocked.Increment(ref _foodDelivered);
                        state.State = AntState.Foraging;
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // AntBrain — pheromone sensing + steering + wander (tier-gated)
    // Reads: Position, AntState, Genetics + pheromone grid
    // Writes: Position (velocity)
    // ═══════════════════════════════════════════════════════════════════

    private const float SensorDistance = 40f;
    private const float SensorAngle = 0.52f;   // ~30 degrees
    private const float SteerStrength = 0.3f;   // radians per tick toward best sensor
    private const float WanderJitter = 0.03f;    // tiny per-tick jitter (±1.7°)
    private const int WanderChangeTicks = 90;     // change direction every ~1.5s
    private const float WanderTurnMax = 0.8f;     // max turn on direction change (~45°)

    private void AntBrainTick(TickContext ctx)
    {
        var phero = _pheromones;
        var nests = _nestPositions;
        long tick = ctx.TickNumber;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var positions = cluster.GetSpan(Ant.Position);
            var states = cluster.GetReadOnlySpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var pos = ref positions[idx];
                ref readonly var state = ref states[idx];
                ref readonly var gen = ref genetics[idx];

                if (state.Energy <= 0f) continue;

                float speed = MathF.Sqrt(pos.VelocityX * pos.VelocityX + pos.VelocityY * pos.VelocityY);
                if (speed < 0.01f) speed = gen.Speed * 40f;
                float heading = MathF.Atan2(pos.VelocityY, pos.VelocityX);

                bool steered = false;

                if (state.State == AntState.Foraging)
                {
                    // Pheromone trail following (food trail)
                    float sL = phero.Food[PheromoneGrid.WorldToIndex(
                        pos.X + MathF.Cos(heading - SensorAngle) * SensorDistance,
                        pos.Y + MathF.Sin(heading - SensorAngle) * SensorDistance)];
                    float sC = phero.Food[PheromoneGrid.WorldToIndex(
                        pos.X + MathF.Cos(heading) * SensorDistance,
                        pos.Y + MathF.Sin(heading) * SensorDistance)];
                    float sR = phero.Food[PheromoneGrid.WorldToIndex(
                        pos.X + MathF.Cos(heading + SensorAngle) * SensorDistance,
                        pos.Y + MathF.Sin(heading + SensorAngle) * SensorDistance)];

                    if (sL > sC && sL > sR) { heading -= SteerStrength; steered = true; }
                    else if (sR > sC && sR > sL) { heading += SteerStrength; steered = true; }
                    else if (sC > 0.1f) { steered = true; }
                }
                else // Returning — pheromone + nest direction validation
                {
                    int ni = gen.HomeNestIndex;
                    float toNestX = nests[ni].x - pos.X;
                    float toNestY = nests[ni].y - pos.Y;
                    float nestHeading = MathF.Atan2(toNestY, toNestX);

                    float sL = phero.Home[PheromoneGrid.WorldToIndex(
                        pos.X + MathF.Cos(heading - SensorAngle) * SensorDistance,
                        pos.Y + MathF.Sin(heading - SensorAngle) * SensorDistance)];
                    float sC = phero.Home[PheromoneGrid.WorldToIndex(
                        pos.X + MathF.Cos(heading) * SensorDistance,
                        pos.Y + MathF.Sin(heading) * SensorDistance)];
                    float sR = phero.Home[PheromoneGrid.WorldToIndex(
                        pos.X + MathF.Cos(heading + SensorAngle) * SensorDistance,
                        pos.Y + MathF.Sin(heading + SensorAngle) * SensorDistance)];

                    float pheroHeading = heading;
                    if (sL > sC && sL > sR) pheroHeading -= SteerStrength;
                    else if (sR > sC && sR > sL) pheroHeading += SteerStrength;

                    // Validate: does pheromone heading take us closer to nest?
                    float dot = MathF.Cos(pheroHeading) * toNestX + MathF.Sin(pheroHeading) * toNestY;
                    heading = dot > 0f ? pheroHeading : nestHeading;
                    steered = true;
                }

                // Wander: tiny jitter + periodic direction change
                if (!steered)
                {
                    uint h = (uint)(idx * 2654435761 + cluster.ChunkId * 40503);

                    float jitter = ((h + (uint)tick * 2246822519u) % 1000u / 1000f - 0.5f) * 2f * WanderJitter;
                    heading += jitter;

                    uint epoch = (uint)(tick / WanderChangeTicks);
                    uint prevEpoch = (uint)((tick - (long)ctx.AmortizedDeltaTime * 60) / WanderChangeTicks);
                    if (epoch != prevEpoch)
                    {
                        float turn = ((h * 48271u + epoch * 16807u) % 1000u / 1000f - 0.5f) * 2f * WanderTurnMax;
                        heading += turn;
                    }
                }

                pos.VelocityX = MathF.Cos(heading) * speed;
                pos.VelocityY = MathF.Sin(heading) * speed;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PheromoneDeposit — deposit trail markers (T0/T1 only)
    // Reads: Position, AntState, Genetics  |  Writes: pheromone grid
    // ═══════════════════════════════════════════════════════════════════

    private const float BaseDeposit = 5f;
    private const float NearFoodMultiplier = 10f;  // deposit more food-pheromone near food
    private const float DepositFalloffRange = 200f;
    private const float DepositFalloffRangeSq = DepositFalloffRange * DepositFalloffRange;

    private void PheromoneDepositTick(TickContext ctx)
    {
        var phero = _pheromones;
        var food = _foodCache;
        var nests = _nestPositions;

        // Scale deposit by amortization so all tiers produce the same pheromone per second
        float amortScale = ctx.AmortizedDeltaTime / BaseDt;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var positions = cluster.GetReadOnlySpan(Ant.Position);
            var states = cluster.GetReadOnlySpan(Ant.State);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var pos = ref positions[idx];
                ref readonly var state = ref states[idx];

                if (state.Energy <= 0f) continue;

                int cellIdx = PheromoneGrid.WorldToIndex(pos.X, pos.Y);

                if (state.State == AntState.Foraging)
                {
                    // Foraging ants leave only a faint home trail — strong trails come from successful food runs
                    PheromoneGrid.Deposit(phero.Home, cellIdx, BaseDeposit * 0.1f * amortScale);
                }
                else
                {
                    // Returning: deposit food pheromone, stronger near the food source this ant came from
                    float deposit = BaseDeposit;
                    int fi = state.FoodSourceIndex;
                    if (fi >= 0 && fi < food.Length)
                    {
                        float dx = pos.X - food[fi].x;
                        float dy = pos.Y - food[fi].y;
                        float distSq = dx * dx + dy * dy;
                        if (distSq < DepositFalloffRangeSq)
                        {
                            deposit += (1f - MathF.Sqrt(distSq) / DepositFalloffRange) * NearFoodMultiplier;
                        }
                    }
                    PheromoneGrid.Deposit(phero.Food, cellIdx, deposit * amortScale);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PheromoneDecay — evaporate grid (callback, every tick)
    // ═══════════════════════════════════════════════════════════════════

    private const float PheroDecayFactor = 0.995f;

    private void PheromoneDecayTick(TickContext ctx)
    {
        _pheromones.Evaporate(PheroDecayFactor);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Render Pipeline
    // ═══════════════════════════════════════════════════════════════════

    private void PrepareRender(TickContext ctx)
    {
        int crossings = Interlocked.Exchange(ref _cellCrossingsThisTick, 0);
        _crossingsAccum += crossings;
        _crossingsTickCount++;
        if (_crossingsTickCount >= 60)
        {
            CrossingsPerSecond = _crossingsAccum;
            _crossingsAccum = 0;
            _crossingsTickCount = 0;
        }

        _tierCounts[0] = 0; _tierCounts[1] = 0; _tierCounts[2] = 0; _tierCounts[3] = 0;
        _stateCounts[0] = 0; _stateCounts[1] = 0;

        // Anchor instances: two invisible points per worker buffer at world corners.
        // Forces Godot's computed AABB to span the world so it never culls the node.
        for (int w = 0; w < _workerBuffers.Length; w++)
        {
            var wb = _workerBuffers[w];
            wb.EnsureCapacity(2);
            var buf = wb.Data;
            // Anchor at (0, 0) — scale 0, alpha 0
            buf[0] = 0f; buf[1] = 0f; buf[2] = 0f; buf[3] = 0f;
            buf[4] = 0f; buf[5] = 0f; buf[6] = 0f; buf[7] = 0f;
            buf[8] = 0f; buf[9] = 0f; buf[10] = 0f; buf[11] = 0f;
            // Anchor at (WorldSize, WorldSize)
            buf[12] = 0f; buf[13] = 0f; buf[14] = 0f; buf[15] = WorldSize;
            buf[16] = 0f; buf[17] = 0f; buf[18] = 0f; buf[19] = WorldSize;
            buf[20] = 0f; buf[21] = 0f; buf[22] = 0f; buf[23] = 0f;
            wb.Count = 2;
        }

        // Food + nests into overlay buffer (small, runs once)
        _overlayBuffer.Reset();
        _overlayBuffer.EnsureCapacity(FoodCount + NestCount);
        var oBuf = _overlayBuffer.Data;
        int oi = 0;

        for (int fi = 0; fi < _foodCache.Length; fi++)
        {
            var (fx, fy, initial) = _foodCache[fi];
            int rem = _foodRemainingInt[fi];
            float ratio = initial > 0 ? Math.Max(rem, 0) / initial : 0f;
            float scale = 2f + ratio * 10f;
            float green = 0.3f + ratio * 0.7f;
            int off = oi * Stride;
            oBuf[off + 0] = scale; oBuf[off + 1] = 0f; oBuf[off + 2] = 0f; oBuf[off + 3] = fx;
            oBuf[off + 4] = 0f; oBuf[off + 5] = scale; oBuf[off + 6] = 0f; oBuf[off + 7] = fy;
            oBuf[off + 8] = 0.2f; oBuf[off + 9] = green; oBuf[off + 10] = 0.2f; oBuf[off + 11] = 0.5f;
            oi++;
        }

        for (int ni = 0; ni < _nestPositions.Length; ni++)
        {
            var (nx, ny) = _nestPositions[ni];
            int stock = _nestFoodStock[ni];
            float nestRatio = Math.Clamp(stock / (float)InitialNestFood, 0f, 3f);
            float nestScale = 3f + nestRatio * 20f;
            int off = oi * Stride;
            oBuf[off + 0] = nestScale; oBuf[off + 1] = 0f; oBuf[off + 2] = 0f; oBuf[off + 3] = nx;
            oBuf[off + 4] = 0f; oBuf[off + 5] = nestScale; oBuf[off + 6] = 0f; oBuf[off + 7] = ny;
            oBuf[off + 8] = 0.6f; oBuf[off + 9] = 0.3f; oBuf[off + 10] = 0.1f; oBuf[off + 11] = 0.5f;
            oi++;
        }

        _overlayBuffer.Count = oi;

        // Downsample pheromone grid only when overlay is visible
        if (_heatmapEnabled)
        {
            const int hs = RenderFrame.HeatmapSize; // 200
            const int gs = PheromoneGrid.GridSize;   // 1000
            var foodSrc = _pheromones.Food;
            var homeSrc = _pheromones.Home;
            var maxF = _heatMaxFood;
            var maxH = _heatMaxHome;

            // Linear scan over source arrays — fully sequential reads, cache-friendly
            for (int sy = 0; sy < gs; sy++)
            {
                int hy = sy / 5;
                int srcRow = sy * gs;
                int hiRow = hy * hs;
                for (int sx = 0; sx < gs; sx++)
                {
                    int hi = hiRow + sx / 5;
                    int si = srcRow + sx;
                    float f = foodSrc[si];
                    float h = homeSrc[si];
                    if (f > maxF[hi]) maxF[hi] = f;
                    if (h > maxH[hi]) maxH[hi] = h;
                }
            }

            // Convert to RGBA + reset accumulators in one pass
            float invMax = 255f / PheromoneGrid.MaxPheromone;
            var rgba = _heatmapRGBA;
            for (int i = 0; i < HeatmapPixels; i++)
            {
                byte gv = (byte)Math.Min(maxF[i] * invMax, 255f);
                byte bv = (byte)Math.Min(maxH[i] * invMax, 255f);
                int p = i * 4;
                rgba[p + 0] = 0;
                rgba[p + 1] = gv;
                rgba[p + 2] = bv;
                rgba[p + 3] = Math.Max(gv, bv);
                maxF[i] = 0f;
                maxH[i] = 0f;
            }
        }
    }

    private void FillRender(TickContext ctx)
    {
        var wb = _workerBuffers[ctx.WorkerId];
        var tierMirror = _tierMirror;
        float invCellSize = 1f / CellSize;

        // Snapshot camera AABB for clipping
        float clipMinX = _camMinX;
        float clipMinY = _camMinY;
        float clipMaxX = _camMaxX;
        float clipMaxY = _camMaxY;

        Span<int> localTiers = stackalloc int[4];
        int sForaging = 0, sCarrying = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var liveCount = cluster.LiveCount;

            // Fast reject: cluster tight AABB fully outside camera
            ref readonly var bounds = ref cluster.SpatialBounds;
            if (bounds.MaxX < clipMinX || bounds.MinX > clipMaxX || bounds.MaxY < clipMinY || bounds.MinY > clipMaxY)
            {
                continue;
            }

            // Cluster overlaps camera — render visible ants
            var positions = cluster.GetReadOnlySpan(Ant.Position);
            var statesVis = cluster.GetReadOnlySpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);

            // Tier from first entity position
            var bitsVis = cluster.OccupancyBits;
            int firstIdx = BitOperations.TrailingZeroCount(bitsVis);
            ref readonly var firstPos = ref positions[firstIdx];
            int tcx = Math.Clamp((int)(firstPos.X * invCellSize), 0, GridCells - 1);
            int tcy = Math.Clamp((int)(firstPos.Y * invCellSize), 0, GridCells - 1);
            localTiers[Math.Min((int)tierMirror[tcy * GridCells + tcx], 3)] += liveCount;

            wb.EnsureCapacity(liveCount);
            var buf = wb.Data;
            var writeIdx = wb.Count;

            while (bitsVis != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bitsVis);
                bitsVis &= bitsVis - 1;
                ref readonly var pos = ref positions[idx];
                ref readonly var state = ref statesVis[idx];

                sForaging += 1 - state.State;
                sCarrying += state.State;

                // Per-entity clip for clusters that straddle the camera edge
                if (pos.X < clipMinX || pos.X > clipMaxX || pos.Y < clipMinY || pos.Y > clipMaxY)
                {
                    continue;
                }

                ref readonly var gen = ref genetics[idx];
                float energyRatio = gen.BaseEnergy > 0f ? Math.Clamp(state.Energy / gen.BaseEnergy, 0f, 1f) : 0f;
                float alpha = 0.15f + energyRatio * 0.70f;

                float r = 1f, g = 0.3f, b = 0.3f;
                if (state.IsReturning) { r = 0.3f; g = 1f; b = 0.3f; }

                int off = writeIdx * Stride;
                buf[off + 0] = 1f;  buf[off + 1] = 0f; buf[off + 2] = 0f; buf[off + 3] = pos.X;
                buf[off + 4] = 0f;  buf[off + 5] = 1f; buf[off + 6] = 0f; buf[off + 7] = pos.Y;
                buf[off + 8] = r;   buf[off + 9] = g;  buf[off + 10] = b; buf[off + 11] = alpha;
                writeIdx++;
            }

            wb.Count = writeIdx;
        }

        Interlocked.Add(ref _tierCounts[0], localTiers[0]);
        Interlocked.Add(ref _tierCounts[1], localTiers[1]);
        Interlocked.Add(ref _tierCounts[2], localTiers[2]);
        Interlocked.Add(ref _tierCounts[3], localTiers[3]);
        Interlocked.Add(ref _stateCounts[0], sForaging);
        Interlocked.Add(ref _stateCounts[1], sCarrying);
    }

    private void PublishRender(TickContext ctx)
    {
        // Snapshot current Data/Count into immutable frame — Godot reads only this
        var buffers = new BufferSnapshot[_workerBuffers.Length];
        int total = 0;
        for (int i = 0; i < _workerBuffers.Length; i++)
        {
            buffers[i] = new BufferSnapshot { Data = _workerBuffers[i].Data, Count = _workerBuffers[i].Count };
            total += _workerBuffers[i].Count;
        }
        VisibleAnts = total;

        var frame = new RenderFrame
        {
            Buffers = buffers,
            Overlay = new BufferSnapshot { Data = _overlayBuffer.Data, Count = _overlayBuffer.Count },
            VisibleAnts = total,
            HeatmapRGBA = _heatmapRGBARead,
        };

        _renderBridge.Publish(frame);

        // Swap heatmap buffer
        (_heatmapRGBA, _heatmapRGBARead) = (_heatmapRGBARead, _heatmapRGBA);

        // Swap all render buffers AFTER publish — next frame writes to the other slot
        for (int i = 0; i < _workerBuffers.Length; i++)
        {
            _workerBuffers[i].Reset();
        }
        _overlayBuffer.Reset();

        // Console timing dump every ~2s (120 ticks at 60fps)
        if (ctx.TickNumber % 120 == 0 && _runtime?.Telemetry != null)
        {
            var telemetry = _runtime.Telemetry;
            if (telemetry.TotalTicksRecorded > 0)
            {
                var tickNum = telemetry.NewestTick;
                ref readonly var tick = ref telemetry.GetTick(tickNum);
                var systems = telemetry.GetSystemMetrics(tickNum);
                var sysDefs = _runtime.Systems;

                Console.Write($"T{ctx.TickNumber,6} {tick.ActualDurationMs:F1}ms | T0:{_tierCounts[0]} T1:{_tierCounts[1]} T2:{_tierCounts[2]} T3:{_tierCounts[3]} |");
                for (int i = 0; i < systems.Length && i < sysDefs.Length; i++)
                {
                    ref readonly var s = ref systems[i];
                    if (!s.WasSkipped && s.DurationUs > 1f)
                    {
                        Console.Write($" {sysDefs[i].Name}:{s.DurationUs:F0}");
                    }
                }
                Console.WriteLine();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Entity Spawning
    // ═══════════════════════════════════════════════════════════════════

    private void SpawnAnts()
    {
        var rng = new Random(42);
        const int batchSize = 1_000;
        int antsPerNest = AntCount / NestCount;
        float spawnRadius = 200f;

        for (int nestIdx = 0; nestIdx < NestCount; nestIdx++)
        {
            var (nx, ny) = _nestPositions[nestIdx];
            int remaining = (nestIdx == NestCount - 1) ? AntCount - antsPerNest * (NestCount - 1) : antsPerNest;

            while (remaining > 0)
            {
                var count = Math.Min(batchSize, remaining);
                remaining -= count;
                using var tx = _dbe.CreateQuickTransaction();

                for (var i = 0; i < count; i++)
                {
                    float angle = (float)(rng.NextDouble() * Math.PI * 2);
                    float dist = (float)(rng.NextDouble() * spawnRadius);
                    float x = Math.Clamp(nx + MathF.Cos(angle) * dist, 0f, WorldSize);
                    float y = Math.Clamp(ny + MathF.Sin(angle) * dist, 0f, WorldSize);
                    float headAngle = (float)(rng.NextDouble() * Math.PI * 2);
                    float baseSpeed = 40f + (float)(rng.NextDouble() * 40);
                    float speedMul = 0.8f + (float)(rng.NextDouble() * 0.7f);
                    float finalSpeed = baseSpeed * speedMul; // baked into velocity
                    float baseEnergy = 800f + (float)(rng.NextDouble() * 800f);
                    int eatAmount = 1 + rng.Next(3);

                    var pos = new Position
                    {
                        Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                        VelocityX = MathF.Cos(headAngle) * finalSpeed,
                        VelocityY = MathF.Sin(headAngle) * finalSpeed
                    };
                    var genetics = new Genetics
                    {
                        Speed = speedMul,
                        HomeNestX = nx,
                        HomeNestY = ny,
                        BaseEnergy = baseEnergy,
                        EatAmount = eatAmount,
                        HomeNestIndex = nestIdx
                    };
                    var state = new AntState
                    {
                        State = AntState.Foraging,
                        Energy = baseEnergy * (0.5f + (float)rng.NextDouble() * 0.5f)
                    };

                    tx.Spawn<Ant>(
                        Ant.Position.Set(in pos),
                        Ant.Genetics.Set(in genetics),
                        Ant.State.Set(in state));
                }

                tx.Commit();
            }
        }
    }

    private void SpawnFood()
    {
        var rng = new Random(123);
        _foodCache = new (float, float, float)[FoodCount];
        _foodRemainingInt = new int[FoodCount];
        using var tx = _dbe.CreateQuickTransaction();
        for (int i = 0; i < FoodCount; i++)
        {
            float x = (float)(rng.NextDouble() * WorldSize);
            float y = (float)(rng.NextDouble() * WorldSize);
            float remaining = 5000f + (float)(rng.NextDouble() * 15000f);
            _foodCache[i] = (x, y, remaining);
            _foodRemainingInt[i] = (int)remaining;
            var source = new FoodSource
            {
                Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                RemainingFood = remaining
            };
            tx.Spawn<Food>(Food.Source.Set(in source));
        }
        tx.Commit();
        BuildFoodGrid();
    }

    private void BuildFoodGrid()
    {
        // Bucket each food source into all cells whose area overlaps the smell range
        var lists = new System.Collections.Generic.List<int>[FoodGridCells * FoodGridCells];
        int smellCells = (int)MathF.Ceiling(FoodSmellRange * FoodGridInvCellSize); // cells radius

        for (int fi = 0; fi < _foodCache.Length; fi++)
        {
            var (fx, fy, _) = _foodCache[fi];
            int cx = Math.Clamp((int)(fx * FoodGridInvCellSize), 0, FoodGridCells - 1);
            int cy = Math.Clamp((int)(fy * FoodGridInvCellSize), 0, FoodGridCells - 1);

            int minCx = Math.Max(0, cx - smellCells);
            int maxCx = Math.Min(FoodGridCells - 1, cx + smellCells);
            int minCy = Math.Max(0, cy - smellCells);
            int maxCy = Math.Min(FoodGridCells - 1, cy + smellCells);

            for (int gy = minCy; gy <= maxCy; gy++)
            {
                for (int gx = minCx; gx <= maxCx; gx++)
                {
                    int gi = gy * FoodGridCells + gx;
                    lists[gi] ??= new System.Collections.Generic.List<int>();
                    lists[gi].Add(fi);
                }
            }
        }

        _foodGrid = new int[FoodGridCells * FoodGridCells][];
        for (int i = 0; i < lists.Length; i++)
        {
            _foodGrid[i] = lists[i]?.ToArray();
        }
    }

    private void SpawnNests()
    {
        _nestPositions = new (float, float)[]
        {
            (5000f, 5000f), (15000f, 5000f), (10000f, 10000f), (5000f, 15000f), (15000f, 15000f)
        };
        _nestFoodStock = new int[NestCount];
        for (int i = 0; i < NestCount; i++)
        {
            _nestFoodStock[i] = InitialNestFood;
        }

        using var tx = _dbe.CreateQuickTransaction();
        foreach (var (nx, ny) in _nestPositions)
        {
            var info = new NestInfo
            {
                Bounds = new AABB2F { MinX = nx, MinY = ny, MaxX = nx, MaxY = ny },
                FoodStored = 0f,
                Population = AntCount / NestCount
            };
            tx.Spawn<Nest>(Nest.Info.Set(in info));
        }
        tx.Commit();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════

    public void Start() => _runtime.Start();
    public void SetHeatmapEnabled(bool enabled) => _heatmapEnabled = enabled;
    public TickTelemetryRing Telemetry => _runtime?.Telemetry;
    public SystemDefinition[] Systems => _runtime?.Systems;

    public string GetTimingInfo()
    {
        var telemetry = _runtime?.Telemetry;
        if (telemetry == null || telemetry.TotalTicksRecorded == 0)
        {
            return "no ticks";
        }

        try
        {
            var tickNum = telemetry.NewestTick;
            ref readonly var tick = ref telemetry.GetTick(tickNum);
            var systems = telemetry.GetSystemMetrics(tickNum);
            var sysDefs = _runtime.Systems;

            var parts = new System.Text.StringBuilder();
            parts.Append($"Tick: {tick.ActualDurationMs:F1}ms");

            for (var i = 0; i < systems.Length && i < sysDefs.Length; i++)
            {
                ref readonly var s = ref systems[i];
                if (s.WasSkipped)
                {
                    continue;
                }

                parts.Append($"\n  {sysDefs[i].Name}: {s.DurationUs:F0}us");
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
        try { _runtime?.Shutdown(); } catch { }
        try { _runtime?.Dispose(); } catch { }
        try { _antView?.Dispose(); } catch { }
        try { _serviceProvider?.Dispose(); } catch { }
    }
}

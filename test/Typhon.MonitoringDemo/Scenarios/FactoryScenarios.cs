using System.Diagnostics;
using Typhon.Engine;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Bootstraps a new factory: creates buildings, belts, and resources.
/// Heavy on CREATE operations.
/// </summary>
public class FactoryBootstrapScenario : IScenario
{
    public string Name => "Factory Bootstrap";
    public string Description => "Creates buildings, conveyor belts, and resource nodes. Heavy CREATE load.";

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();

                    // Create a batch of factory entities
                    var batchSize = localRand.Next(5, 20);

                    for (var i = 0; i < batchSize && !ct.IsCancellationRequested; i++)
                    {
                        var choice = localRand.Next(100);

                        if (choice < 30)
                        {
                            // Create a building
                            var building = FactoryBuilding.Create(localRand, localRand.Next(0, 5));
                            t.Spawn<FactoryBuildingArch>(FactoryBuildingArch.Building.Set(in building));
                        }
                        else if (choice < 60)
                        {
                            // Create a resource node
                            var node = ResourceNode.Create(localRand, localRand.Next(0, 6));
                            t.Spawn<ResourceNodeArch>(ResourceNodeArch.Node.Set(in node));
                        }
                        else if (choice < 80)
                        {
                            // Create a recipe
                            var recipes = new[] { "Iron Plate", "Copper Wire", "Circuit Board", "Steel Beam" };
                            var recipe = Recipe.Create(localRand, recipes[localRand.Next(recipes.Length)], localRand.Next(1, 50));
                            t.Spawn<RecipeArch>(RecipeArch.Recipe.Set(in recipe));
                        }
                        else
                        {
                            // Create a power grid
                            var grid = PowerGrid.Create(localRand, localRand.Next(1, 10));
                            t.Spawn<PowerGridArch>(PowerGridArch.Grid.Set(in grid));
                        }

                        stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                    }

                    if (t.Commit())
                    {
                        stats.RecordCommit();
                    }
                    else
                    {
                        stats.RecordRollback();
                    }
                }
                catch (Exception ex)
                {
                    stats.RecordFailure(ex);
                }

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }
}

/// <summary>
/// Simulates ongoing factory production: updates building progress, item stacks.
/// Heavy on UPDATE operations.
/// </summary>
public class FactoryProductionScenario : IScenario
{
    public string Name => "Factory Production";
    public string Description => "Updates production progress, item quantities. Heavy UPDATE load.";

    private readonly List<EntityId> _buildingIds = [];
    private readonly List<EntityId> _itemStackIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // First, bootstrap some entities to update
        await BootstrapEntitiesAsync(engine, rand, ct);

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();
                    var updates = localRand.Next(5, 15);

                    for (var i = 0; i < updates && !ct.IsCancellationRequested; i++)
                    {
                        if (_buildingIds.Count > 0 && localRand.Next(2) == 0)
                        {
                            // Update a building's production progress
                            var id = _buildingIds[localRand.Next(_buildingIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var building = entity.Read(FactoryBuildingArch.Building);
                                ref var wb = ref t.OpenMut(id).Write(FactoryBuildingArch.Building);
                                wb.Progress = (building.Progress + 0.1f) % 1.0f;
                                wb.IsActive = localRand.Next(10) > 0;
                                wb.Efficiency = 0.8f + (float)(localRand.NextDouble() * 0.4);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (_itemStackIds.Count > 0)
                        {
                            // Update item stack quantity
                            var id = _itemStackIds[localRand.Next(_itemStackIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var stack = entity.Read(ItemStackArch.Stack);
                                ref var ws = ref t.OpenMut(id).Write(ItemStackArch.Stack);
                                ws.Quantity = Math.Min(stack.MaxStackSize, stack.Quantity + localRand.Next(-5, 10));
                                ws.Quantity = Math.Max(0, ws.Quantity);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                    }

                    if (t.Commit())
                    {
                        stats.RecordCommit();
                    }
                    else
                    {
                        stats.RecordRollback();
                    }
                }
                catch (Exception ex)
                {
                    stats.RecordFailure(ex);
                }

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        // Create initial entities for updates
        using var t = engine.CreateQuickTransaction();

        for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++)
        {
            var building = FactoryBuilding.Create(rand, rand.Next(0, 5));
            var id = t.Spawn<FactoryBuildingArch>(FactoryBuildingArch.Building.Set(in building));
            _buildingIds.Add(id);

            var stack = ItemStack.Create(rand, rand.Next(1, 20), (long)id.RawValue);
            var stackId = t.Spawn<ItemStackArch>(ItemStackArch.Stack.Set(in stack));
            _itemStackIds.Add(stackId);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simulates supply chain: belts moving items, read-heavy with queries.
/// Mixed READ/UPDATE operations.
/// </summary>
public class FactorySupplyChainScenario : IScenario
{
    public string Name => "Factory Supply Chain";
    public string Description => "Simulates belts, item movement, and logistics queries. Mixed READ/UPDATE.";

    private readonly List<EntityId> _beltIds = [];
    private readonly List<EntityId> _buildingIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap supply chain entities
        await BootstrapEntitiesAsync(engine, rand, ct);

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();
                    var ops = localRand.Next(10, 30);

                    for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                    {
                        var opType = localRand.Next(100);

                        if (opType < 40 && _beltIds.Count > 0)
                        {
                            // Read belt status (logistics query)
                            var id = _beltIds[localRand.Next(_beltIds.Count)];
                            t.TryOpen(id, out _);
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (opType < 70 && _beltIds.Count > 0)
                        {
                            // Update belt item count (item movement)
                            var id = _beltIds[localRand.Next(_beltIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var belt = entity.Read(ConveyorBeltArch.Belt);
                                ref var wb = ref t.OpenMut(id).Write(ConveyorBeltArch.Belt);
                                wb.ItemCount = Math.Max(0, belt.ItemCount + localRand.Next(-3, 5));
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (opType < 90 && _buildingIds.Count > 0)
                        {
                            // Read building for supply check
                            var id = _buildingIds[localRand.Next(_buildingIds.Count)];
                            t.TryOpen(id, out _);
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else
                        {
                            // Create new belt connection
                            if (_buildingIds.Count >= 2)
                            {
                                var src = _buildingIds[localRand.Next(_buildingIds.Count)];
                                var dst = _buildingIds[localRand.Next(_buildingIds.Count)];
                                var belt = ConveyorBelt.Create(localRand, localRand.Next(0, 5), (long)src.RawValue, (long)dst.RawValue);
                                var id = t.Spawn<ConveyorBeltArch>(ConveyorBeltArch.Belt.Set(in belt));
                                lock (_beltIds)
                                {
                                    _beltIds.Add(id);
                                }
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                    }

                    if (t.Commit())
                    {
                        stats.RecordCommit();
                    }
                    else
                    {
                        stats.RecordRollback();
                    }
                }
                catch (Exception ex)
                {
                    stats.RecordFailure(ex);
                }

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        // Create buildings first
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            var building = FactoryBuilding.Create(rand, rand.Next(0, 5));
            var id = t.Spawn<FactoryBuildingArch>(FactoryBuildingArch.Building.Set(in building));
            _buildingIds.Add(id);
        }

        // Create belts between buildings
        for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++)
        {
            var src = _buildingIds[rand.Next(_buildingIds.Count)];
            var dst = _buildingIds[rand.Next(_buildingIds.Count)];
            var belt = ConveyorBelt.Create(rand, rand.Next(0, 5), (long)src.RawValue, (long)dst.RawValue);
            var id = t.Spawn<ConveyorBeltArch>(ConveyorBeltArch.Belt.Set(in belt));
            _beltIds.Add(id);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

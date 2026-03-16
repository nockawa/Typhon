using System.Diagnostics;
using Typhon.Engine;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Runs both factory and RPG workloads simultaneously.
/// Tests mixed component types and transaction isolation.
/// </summary>
public class MixedWorkloadScenario : IScenario
{
    public string Name => "Mixed Workload";
    public string Description => "Factory + RPG simultaneously. Tests mixed component types and isolation.";

    private readonly List<EntityId> _factoryIds = [];
    private readonly List<EntityId> _characterIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap both domains
        await BootstrapEntitiesAsync(engine, rand, ct);

        // Split workers between factory and RPG
        var factoryWorkers = config.WorkerCount / 2;
        var rpgWorkers = config.WorkerCount - factoryWorkers;

        var workers = new List<Task>();

        // Factory workers
        for (var workerId = 0; workerId < factoryWorkers; workerId++)
        {
            var wid = workerId;
            workers.Add(Task.Run(async () =>
            {
                var localRand = new Random(42 + wid);

                while (!ct.IsCancellationRequested)
                {
                    var sw = Stopwatch.GetTimestamp();
                    try
                    {
                        using var t = engine.CreateQuickTransaction();
                        var ops = localRand.Next(5, 15);

                        for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                        {
                            var opType = localRand.Next(100);

                            if (opType < 30)
                            {
                                var building = FactoryBuilding.Create(localRand, localRand.Next(0, 5));
                                var id = t.Spawn<FactoryBuildingArch>(FactoryBuildingArch.Building.Set(in building));
                                lock (_factoryIds)
                                {
                                    _factoryIds.Add(id);
                                }
                            }
                            else if (opType < 70 && _factoryIds.Count > 0)
                            {
                                var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                                if (t.TryOpen(id, out var entity))
                                {
                                    var building = entity.Read(FactoryBuildingArch.Building);
                                    ref var wb = ref t.OpenMut(id).Write(FactoryBuildingArch.Building);
                                    wb.Progress = (building.Progress + 0.05f) % 1.0f;
                                }
                            }
                            else if (_factoryIds.Count > 0)
                            {
                                var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                                t.TryOpen(id, out _);
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
            }, ct));
        }

        // RPG workers
        for (var workerId = 0; workerId < rpgWorkers; workerId++)
        {
            var wid = workerId + factoryWorkers;
            workers.Add(Task.Run(async () =>
            {
                var localRand = new Random(42 + wid);

                while (!ct.IsCancellationRequested)
                {
                    var sw = Stopwatch.GetTimestamp();
                    try
                    {
                        using var t = engine.CreateQuickTransaction();
                        var ops = localRand.Next(5, 15);

                        for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                        {
                            var opType = localRand.Next(100);

                            if (opType < 30)
                            {
                                var names = new[] { "Warrior", "Mage", "Rogue", "Paladin" };
                                var character = Character.Create(localRand, names[localRand.Next(names.Length)], localRand.Next(2) == 0);
                                var id = t.Spawn<CharacterArch>(CharacterArch.Character.Set(in character));
                                lock (_characterIds)
                                {
                                    _characterIds.Add(id);
                                }
                            }
                            else if (opType < 70 && _characterIds.Count > 0)
                            {
                                var id = _characterIds[localRand.Next(_characterIds.Count)];
                                if (t.TryOpen(id, out var entity))
                                {
                                    var character = entity.Read(CharacterArch.Character);
                                    ref var wc = ref t.OpenMut(id).Write(CharacterArch.Character);
                                    wc.Health = Math.Min(character.MaxHealth, character.Health + localRand.Next(-20, 30));
                                    wc.Experience = character.Experience + localRand.Next(10, 100);
                                }
                            }
                            else if (_characterIds.Count > 0)
                            {
                                var id = _characterIds[localRand.Next(_characterIds.Count)];
                                t.TryOpen(id, out _);
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
            }, ct));
        }

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        // Factory entities
        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            var building = FactoryBuilding.Create(rand, rand.Next(0, 5));
            var id = t.Spawn<FactoryBuildingArch>(FactoryBuildingArch.Building.Set(in building));
            _factoryIds.Add(id);
        }

        // RPG entities
        var names = new[] { "Hero", "Villain", "NPC", "Monster" };
        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            var character = Character.Create(rand, names[rand.Next(names.Length)], i > 10);
            var id = t.Spawn<CharacterArch>(CharacterArch.Character.Set(in character));
            _characterIds.Add(id);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Intentionally creates high contention by having many workers update the same entities.
/// Used to test lock behavior and observe contention metrics.
/// </summary>
public class HighContentionScenario : IScenario
{
    public string Name => "High Contention";
    public string Description => "Multiple workers updating same entities. Stress tests locking and MVCC.";

    private readonly List<EntityId> _hotspotIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;

        // Create a small number of "hotspot" entities that everyone fights over
        await BootstrapHotspotsAsync(engine, rand, ct);

        // No rate limiting - we want maximum contention
        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();

                    // All workers try to update the SAME hotspot entities
                    var updateCount = localRand.Next(3, 8);

                    for (var i = 0; i < updateCount && !ct.IsCancellationRequested; i++)
                    {
                        // Always pick from the small hotspot pool (high contention)
                        var id = _hotspotIds[localRand.Next(_hotspotIds.Count)];

                        if (t.TryOpen(id, out var entity))
                        {
                            var grid = entity.Read(PowerGridArch.Grid);
                            // Every worker tries to update power grid stats
                            ref var wg = ref t.OpenMut(id).Write(PowerGridArch.Grid);
                            wg.Production = grid.Production + (float)(localRand.NextDouble() * 10);
                            wg.Consumption = grid.Consumption + (float)(localRand.NextDouble() * 5);
                            wg.BatteryStored = Math.Min(grid.BatteryCapacity, grid.BatteryStored + (float)(localRand.NextDouble() * 100));
                            wg.IsOverloaded = wg.Consumption > wg.Production;

                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
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
                catch (Exception)
                {
                    stats.RecordFailure();
                }

                // Minimal delay to maximize contention
                await Task.Yield();
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapHotspotsAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        // Create only 5 power grids - these will be the hotspots
        for (var i = 0; i < 5 && !ct.IsCancellationRequested; i++)
        {
            var grid = PowerGrid.Create(rand, i + 1);
            var id = t.Spawn<PowerGridArch>(PowerGridArch.Grid.Set(in grid));
            _hotspotIds.Add(id);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

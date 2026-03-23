using System.Diagnostics;
using Typhon.Engine;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Simulates an active RPG world: NPCs moving, players interacting.
/// Balanced CREATE/READ/UPDATE operations.
/// </summary>
public class RpgWorldSimulationScenario : IScenario
{
    public string Name => "RPG World Simulation";
    public string Description => "Simulates NPC movement, player interactions. Balanced CRUD operations.";

    private readonly List<EntityId> _characterIds = [];
    private readonly List<EntityId> _positionIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap world entities
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
                    var ops = localRand.Next(5, 20);

                    for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                    {
                        var opType = localRand.Next(100);

                        if (opType < 20)
                        {
                            // Spawn new NPC
                            var npcNames = new[] { "Goblin", "Orc", "Wolf", "Skeleton", "Bandit" };
                            var character = Character.Create(localRand, npcNames[localRand.Next(npcNames.Length)], true);
                            var id = t.Spawn<CharacterArch>(CharacterArch.Character.Set(in character));
                            lock (_characterIds)
                            {
                                _characterIds.Add(id);
                            }

                            var pos = WorldPosition.Create(localRand, (long)id.RawValue, localRand.Next(1, 20));
                            var posId = t.Spawn<WorldPositionArch>(WorldPositionArch.Position.Set(in pos));
                            lock (_positionIds)
                            {
                                _positionIds.Add(posId);
                            }

                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (opType < 60 && _positionIds.Count > 0)
                        {
                            // Update position (movement)
                            var id = _positionIds[localRand.Next(_positionIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var pos = entity.Read(WorldPositionArch.Position);
                                ref var wp = ref t.OpenMut(id).Write(WorldPositionArch.Position);
                                wp.X = pos.X + (float)(localRand.NextDouble() - 0.5) * 10;
                                wp.Y = pos.Y + (float)(localRand.NextDouble() - 0.5) * 10;
                                wp.Rotation = (float)(localRand.NextDouble() * 360);
                                wp.IsMoving = localRand.Next(2) == 1;
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (opType < 80 && _characterIds.Count > 0)
                        {
                            // Read character (visibility check, AI)
                            var id = _characterIds[localRand.Next(_characterIds.Count)];
                            t.TryOpen(id, out _);
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (_characterIds.Count > 0)
                        {
                            // Update character stats
                            var id = _characterIds[localRand.Next(_characterIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var character = entity.Read(CharacterArch.Character);
                                ref var wc = ref t.OpenMut(id).Write(CharacterArch.Character);
                                wc.Health = Math.Min(character.MaxHealth, character.Health + localRand.Next(-10, 20));
                                wc.Mana = Math.Min(character.MaxMana, character.Mana + localRand.Next(-5, 10));
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

        // Create initial characters
        var playerNames = new[] { "Hero", "Warrior", "Mage", "Rogue", "Cleric" };
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            var character = Character.Create(rand, playerNames[rand.Next(playerNames.Length)], i > 10);
            var id = t.Spawn<CharacterArch>(CharacterArch.Character.Set(in character));
            _characterIds.Add(id);

            var pos = WorldPosition.Create(rand, (long)id.RawValue, rand.Next(1, 20));
            var posId = t.Spawn<WorldPositionArch>(WorldPositionArch.Position.Set(in pos));
            _positionIds.Add(posId);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simulates intense combat: damage calculation, health updates.
/// UPDATE-heavy with high frequency.
/// </summary>
public class RpgCombatScenario : IScenario
{
    public string Name => "RPG Combat";
    public string Description => "Intense battle simulation: damage, healing, skill cooldowns. High-frequency UPDATEs.";

    private readonly List<EntityId> _characterIds = [];
    private readonly List<EntityId> _combatStatsIds = [];
    private readonly List<EntityId> _skillIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap combat entities
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

                    // Simulate a combat round
                    var actions = localRand.Next(5, 15);

                    for (var i = 0; i < actions && !ct.IsCancellationRequested; i++)
                    {
                        var actionType = localRand.Next(100);

                        if (actionType < 40 && _characterIds.Count > 0)
                        {
                            // Deal damage / heal
                            var id = _characterIds[localRand.Next(_characterIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var character = entity.Read(CharacterArch.Character);
                                var damage = localRand.Next(-50, 100); // Negative = heal
                                ref var wc = ref t.OpenMut(id).Write(CharacterArch.Character);
                                wc.Health = Math.Clamp(character.Health - damage, 0, character.MaxHealth);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (actionType < 70 && _combatStatsIds.Count > 0)
                        {
                            // Update combat stats
                            var id = _combatStatsIds[localRand.Next(_combatStatsIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var combatStats = entity.Read(CombatStatsArch.Stats);
                                ref var ws = ref t.OpenMut(id).Write(CombatStatsArch.Stats);
                                ws.DamageDealt = combatStats.DamageDealt + localRand.Next(0, 500);
                                ws.DamageTaken = combatStats.DamageTaken + localRand.Next(0, 200);
                                ws.Kills = combatStats.Kills;
                                if (localRand.Next(10) == 0)
                                {
                                    ws.Kills++;
                                }
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (_skillIds.Count > 0)
                        {
                            // Toggle skill cooldown
                            var id = _skillIds[localRand.Next(_skillIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var skill = entity.Read(SkillArch.Skill);
                                ref var wsk = ref t.OpenMut(id).Write(SkillArch.Skill);
                                wsk.OnCooldown = !skill.OnCooldown;
                                wsk.Cooldown = wsk.OnCooldown ? (float)localRand.NextDouble() * 10 : 0;
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

        var combatantNames = new[] { "Fighter", "Berserker", "Archer", "Wizard", "Healer" };
        var skillNames = new[] { "Slash", "Fireball", "Heal", "Shield Bash", "Backstab" };

        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            // Create combatant
            var character = Character.Create(rand, combatantNames[rand.Next(combatantNames.Length)], i > 5);
            var charId = t.Spawn<CharacterArch>(CharacterArch.Character.Set(in character));
            _characterIds.Add(charId);

            // Create combat stats
            var combatStats = CombatStats.Create(rand, (long)charId.RawValue);
            var statsId = t.Spawn<CombatStatsArch>(CombatStatsArch.Stats.Set(in combatStats));
            _combatStatsIds.Add(statsId);

            // Create skills
            for (var j = 0; j < 3; j++)
            {
                var skill = Skill.Create(rand, (long)charId.RawValue, skillNames[rand.Next(skillNames.Length)]);
                var skillId = t.Spawn<SkillArch>(SkillArch.Skill.Set(in skill));
                _skillIds.Add(skillId);
            }
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simulates quest system: accepting, progressing, completing quests.
/// Mixed operations with inventory management.
/// </summary>
public class RpgQuestingScenario : IScenario
{
    public string Name => "RPG Questing";
    public string Description => "Quest acceptance, progress tracking, inventory rewards. Mixed CRUD.";

    private readonly List<EntityId> _characterIds = [];
    private readonly List<EntityId> _questIds = [];
    private readonly List<EntityId> _inventoryIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap quest entities
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
                    var ops = localRand.Next(5, 15);

                    for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                    {
                        var opType = localRand.Next(100);

                        if (opType < 20 && _characterIds.Count > 0)
                        {
                            // Accept new quest
                            var charId = _characterIds[localRand.Next(_characterIds.Count)];
                            var quest = Quest.Create(localRand, (long)charId.RawValue, localRand.Next(1, 100));
                            quest.Status = 0; // Active
                            quest.ObjectiveProgress = 0;
                            var questId = t.Spawn<QuestArch>(QuestArch.Quest.Set(in quest));
                            lock (_questIds)
                            {
                                _questIds.Add(questId);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (opType < 50 && _questIds.Count > 0)
                        {
                            // Progress quest
                            var id = _questIds[localRand.Next(_questIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var quest = entity.Read(QuestArch.Quest);
                                if (quest.Status == 0 && quest.ObjectiveProgress < quest.ObjectiveTarget)
                                {
                                    ref var wq = ref t.OpenMut(id).Write(QuestArch.Quest);
                                    wq.ObjectiveProgress = quest.ObjectiveProgress + 1;
                                    if (wq.ObjectiveProgress >= quest.ObjectiveTarget)
                                    {
                                        wq.Status = 1; // Completed
                                    }
                                }
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (opType < 70 && _characterIds.Count > 0)
                        {
                            // Add inventory item (reward)
                            var charId = _characterIds[localRand.Next(_characterIds.Count)];
                            var slot = localRand.Next(0, 40);
                            var item = Inventory.Create(localRand, (long)charId.RawValue, slot);
                            var itemId = t.Spawn<InventoryArch>(InventoryArch.Item.Set(in item));
                            lock (_inventoryIds)
                            {
                                _inventoryIds.Add(itemId);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (_inventoryIds.Count > 0)
                        {
                            // Read inventory
                            var id = _inventoryIds[localRand.Next(_inventoryIds.Count)];
                            t.TryOpen(id, out _);
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

        var heroNames = new[] { "Aldric", "Elara", "Theron", "Lyra", "Gareth" };

        for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            var character = Character.Create(rand, heroNames[rand.Next(heroNames.Length)], false);
            var charId = t.Spawn<CharacterArch>(CharacterArch.Character.Set(in character));
            _characterIds.Add(charId);

            // Give each character some starting inventory
            for (var j = 0; j < 5; j++)
            {
                var item = Inventory.Create(rand, (long)charId.RawValue, j);
                var itemId = t.Spawn<InventoryArch>(InventoryArch.Item.Set(in item));
                _inventoryIds.Add(itemId);
            }
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

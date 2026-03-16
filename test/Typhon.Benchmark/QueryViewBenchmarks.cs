using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using Typhon.ARPG.Schema;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Query & View: ARPG ItemData real-world benchmarks
// ═══════════════════════════════════════════════════════════════════════

[Archetype(506)]
class BenchItemArch : Archetype<BenchItemArch>
{
    public static readonly Comp<ItemData> Item = Register<ItemData>();
}

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Query", "View")]
public class QueryViewBenchmarks : IDisposable
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private string _databaseName;

    // Pre-created view for refresh benchmarks
    private ViewBase _view;

    // Entities for game-loop update cycling
    private EntityId[] _cyclePKs;
    private ItemData[] _cycleItems;
    private int _updateCursor;

    // Top player PK for Count benchmark
    private long _topPlayerPK;

    private const int EntityCount = 10_000;
    private const int PlayerCount = 100;
    private const int CycleEntityCount = 20;

    // Zipf-like rarity distribution: Common=0, Uncommon=1, Rare=2, Epic=3, Legendary=4, Mythic=5
    private static readonly int[] RarityWeights = [40, 25, 20, 10, 4, 1];
    private static readonly int CategoryCount = 10;
    private static readonly int ItemTypeCount = 50;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"QueryViewBench_{Environment.ProcessId}";

        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<ItemData>();

        Archetype<BenchItemArch>.Touch();
        _dbe.InitializeArchetypes();

        var rng = new Random(42);

        // Build weighted rarity lookup table for fast sampling
        var rarityLookup = BuildWeightedLookup(RarityWeights, rng);

        // Build Zipf-like owner distribution: player 1 gets more items than player 100
        var ownerWeights = new int[PlayerCount];
        for (var i = 0; i < PlayerCount; i++)
        {
            ownerWeights[i] = Math.Max(1, 1000 / (i + 1)); // Zipf: 1000, 500, 333, 250, ...
        }
        var ownerLookup = BuildWeightedLookup(ownerWeights, rng);

        // Player PKs are just 1..100 (not actual entities — ItemData.OwnerId is a foreign key)
        _topPlayerPK = 1; // Zipf top player

        // Insert entities in batches
        var matchingPKs = new System.Collections.Generic.List<EntityId>();
        const int batchSize = 500;
        for (var batch = 0; batch < EntityCount / batchSize; batch++)
        {
            using var tx = _dbe.CreateQuickTransaction();
            for (var i = 0; i < batchSize; i++)
            {
                var item = new ItemData
                {
                    ItemTypeId = rng.Next(ItemTypeCount),
                    Rarity = rarityLookup[rng.Next(rarityLookup.Length)],
                    ItemCategory = rng.Next(CategoryCount),
                    OwnerId = ownerLookup[rng.Next(ownerLookup.Length)],
                    ItemLevel = rng.Next(1, 100),
                    RequiredLevel = rng.Next(1, 70),
                    StackCount = 1,
                    MaxStack = 1,
                    IsEquipped = false,
                    BaseMinDamage = rng.Next(1, 500),
                    BaseMaxDamage = rng.Next(500, 2000),
                    BaseArmor = rng.Next(0, 1000),
                    BaseBlockChance = rng.Next(0, 30)
                };

                var eid = tx.Spawn<BenchItemArch>(BenchItemArch.Item.Set(in item));

                // Track Epic+ items for view cycling
                if (item.Rarity >= 3)
                {
                    matchingPKs.Add(eid);
                }
            }
            tx.Commit();
        }

        // Pre-create view for refresh benchmarks: Rarity >= 3 (Epic+)
        _view = _dbe.Query<ItemData>().Where(i => i.Rarity >= 3).ToView();

        // Pre-select entities for game-loop cycling (boundary crossers)
        _cyclePKs = new EntityId[CycleEntityCount];
        _cycleItems = new ItemData[CycleEntityCount];
        for (var i = 0; i < CycleEntityCount && i < matchingPKs.Count; i++)
        {
            _cyclePKs[i] = matchingPKs[i];
            using var readTx = _dbe.CreateQuickTransaction();
            var entity = readTx.Open(_cyclePKs[i]);
            _cycleItems[i] = entity.Read(BenchItemArch.Item);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _view?.Dispose();
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Single predicate on AllowMultiple index: Rarity >= 3 (Epic+).
    /// Selectivity ~15% -> ~1,500 items.
    /// Exercises: QueryBuilder -> PlanBuilder -> AllowMultiple primary stream -> RangeMultipleEnumerator -> VSBS expansion.
    /// </summary>
    [Benchmark]
    public int Execute_SinglePredicate()
    {
        using var tx = _dbe.CreateQuickTransaction();
        return _dbe.Query<ItemData>().Where(i => i.Rarity >= 3).Execute(tx).Count;
    }

    /// <summary>
    /// Chained predicates: Rarity >= 3 AND ItemCategory == 5.
    /// Selectivity ~1.5% -> ~150 items.
    /// Exercises: chained Where, selectivity ordering, AllowMultiple primary + filter short-circuit.
    /// </summary>
    [Benchmark]
    public int Execute_ChainedPredicates()
    {
        using var tx = _dbe.CreateQuickTransaction();
        return _dbe.Query<ItemData>().Where(i => i.Rarity >= 3).Where(i => i.ItemCategory == 5).Execute(tx).Count;
    }

    /// <summary>
    /// Count items owned by the most active player (Zipf top).
    /// Selectivity ~5-10% for player 1 due to Zipf distribution.
    /// Exercises: equality on AllowMultiple OwnerId index, Count aggregation.
    /// </summary>
    [Benchmark]
    public int Count_PlayerItems()
    {
        using var tx = _dbe.CreateQuickTransaction();
        return _dbe.Query<ItemData>().Where(i => i.OwnerId == _topPlayerPK).Count(tx);
    }

    /// <summary>
    /// Simulates one game tick: update 10 items (toggle Rarity across view boundary), commit, refresh view.
    /// Exercises: entity updates + commit + ring buffer drain + boundary crossing evaluation + delta tracking.
    /// </summary>
    [Benchmark]
    public int View_GameLoopRefresh()
    {
        // Mutate: toggle 10 items across the Rarity >= 3 boundary
        using (var tx = _dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var idx = _updateCursor++ % _cyclePKs.Length;
                ref var item = ref _cycleItems[idx];
                item.Rarity = item.Rarity >= 3 ? 0 : 3;
                var entity = tx.OpenMut(_cyclePKs[idx]);
                entity.Write(BenchItemArch.Item) = item;
            }
            tx.Commit();
        }

        // Refresh view
        using var readTx = _dbe.CreateQuickTransaction();
        _view.Refresh(readTx);
        var count = _view.Count;
        _view.ClearDelta();
        return count;
    }

    /// <summary>
    /// Measures full view lifecycle: create -> populate -> dispose.
    /// Exercises: QueryBuilder -> PlanBuilder -> PipelineExecutor -> View construction + ViewRegistry registration + initial entity set + dispose.
    /// </summary>
    [Benchmark]
    public int View_InitialPopulation()
    {
        using var view = _dbe.Query<ItemData>().Where(i => i.Rarity >= 3).ToView();
        return view.Count;
    }

    /// <summary>Builds a weighted lookup table for fast O(1) sampling from a discrete distribution.</summary>
    private static int[] BuildWeightedLookup(int[] weights, Random rng)
    {
        var total = 0;
        foreach (var w in weights)
        {
            total += w;
        }

        var lookup = new int[total];
        var pos = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            for (var j = 0; j < weights[i]; j++)
            {
                lookup[pos++] = i;
            }
        }

        return lookup;
    }

    public void Dispose()
    {
        GlobalCleanup();
    }
}

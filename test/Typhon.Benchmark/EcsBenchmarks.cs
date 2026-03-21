using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Benchmark-only archetypes (IDs 510–515, avoid collision with BenchArch 501)
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.EcsBase", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsBenchBase
{
    [Index(AllowMultiple = true)]
    public int Category;
    public float Value;
}

[Component("Typhon.Benchmark.EcsChild", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsBenchChild
{
    public int Score;
    public int _pad;
}

[Component("Typhon.Benchmark.EcsGrandChild", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsBenchGrandChild
{
    public float Weight;
    public int _pad;
}

[Archetype(510)]
class BenchBaseArch : Archetype<BenchBaseArch>
{
    public static readonly Comp<EcsBenchBase> Data = Register<EcsBenchBase>();
}

[Archetype(511)]
class BenchChildArch : Archetype<BenchChildArch, BenchBaseArch>
{
    public static readonly Comp<EcsBenchChild> Child = Register<EcsBenchChild>();
}

[Archetype(512)]
class BenchGrandChildArch : Archetype<BenchGrandChildArch, BenchChildArch>
{
    public static readonly Comp<EcsBenchGrandChild> Grand = Register<EcsBenchGrandChild>();
}

// ═══════════════════════════════════════════════════════════════════════
// Benchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("ECS", "Regression")]
public class EcsBenchmarks
{
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;

    // Pre-populated entity IDs for query benchmarks
    private EntityId[] _baseIds;
    private EntityId[] _childIds;
    private EntityId[] _grandChildIds;

    private const int BaseCount = 1000;
    private const int ChildCount = 1000;
    private const int GrandChildCount = 1000;
    private const int SpawnCount = 1000;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"EcsBench_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(16 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<EcsBenchBase>();
        _dbe.RegisterComponentFromAccessor<EcsBenchChild>();
        _dbe.RegisterComponentFromAccessor<EcsBenchGrandChild>();
        Archetype<BenchBaseArch>.Touch();
        Archetype<BenchChildArch>.Touch();
        Archetype<BenchGrandChildArch>.Touch();
        _dbe.InitializeArchetypes();

        // Pre-populate: 1K base + 1K child + 1K grandchild entities
        _baseIds = new EntityId[BaseCount];
        _childIds = new EntityId[ChildCount];
        _grandChildIds = new EntityId[GrandChildCount];

        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < BaseCount; i++)
        {
            var data = new EcsBenchBase { Category = i % 10, Value = i * 1.5f };
            _baseIds[i] = t.Spawn<BenchBaseArch>(BenchBaseArch.Data.Set(in data));
        }
        for (int i = 0; i < ChildCount; i++)
        {
            var baseData = new EcsBenchBase { Category = i % 10, Value = i };
            var childData = new EcsBenchChild { Score = i * 2 };
            _childIds[i] = t.Spawn<BenchChildArch>(BenchBaseArch.Data.Set(in baseData), BenchChildArch.Child.Set(in childData));
        }
        for (int i = 0; i < GrandChildCount; i++)
        {
            var baseData = new EcsBenchBase { Category = i % 10, Value = i };
            var childData = new EcsBenchChild { Score = i };
            var grandData = new EcsBenchGrandChild { Weight = i * 0.1f };
            _grandChildIds[i] = t.Spawn<BenchGrandChildArch>(
                BenchBaseArch.Data.Set(in baseData), BenchChildArch.Child.Set(in childData), BenchGrandChildArch.Grand.Set(in grandData));
        }
        t.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Spawn: single vs batch
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "Spawn 1K entities (loop)")]
    public void Spawn_Loop()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < SpawnCount; i++)
        {
            var data = new EcsBenchBase { Category = i, Value = i };
            t.Spawn<BenchBaseArch>(BenchBaseArch.Data.Set(in data));
        }
        t.Commit();
    }

    [Benchmark(Description = "SpawnBatch 1K entities")]
    public void SpawnBatch_1K()
    {
        using var t = _dbe.CreateQuickTransaction();
        var ids = new EntityId[SpawnCount];
        var data = new EcsBenchBase { Category = 0, Value = 0 };
        t.SpawnBatch<BenchBaseArch>(ids, BenchBaseArch.Data.Set(in data));
        t.Commit();
    }

    // ═══════════════════════════════════════════════════════════════
    // Polymorphic queries
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "Query<Base> polymorphic (3K entities, 3 archetypes)")]
    public int Query_Polymorphic()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchBaseArch>().Count();
    }

    [Benchmark(Description = "QueryExact<Base> (1K entities, 1 archetype)")]
    public int QueryExact_Base()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.QueryExact<BenchBaseArch>().Count();
    }

    [Benchmark(Description = "Query<Child> polymorphic (2K entities, 2 archetypes)")]
    public int Query_Child_Polymorphic()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchChildArch>().Count();
    }

    // ═══════════════════════════════════════════════════════════════
    // Query execution modes
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "Query.Execute() → HashSet (3K polymorphic)")]
    public int Query_Execute()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchBaseArch>().Execute().Count;
    }

    [Benchmark(Description = "Query.Any() short-circuit (3K polymorphic)")]
    public bool Query_Any()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchBaseArch>().Any();
    }

    [Benchmark(Description = "Query.Count() (3K polymorphic)")]
    public int Query_Count()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchBaseArch>().Count();
    }

    [Benchmark(Description = "Query with Enabled filter (3K polymorphic)")]
    public int Query_Enabled()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchBaseArch>().Enabled<EcsBenchBase>().Count();
    }

    [Benchmark(Description = "Query.WhereField indexed (3K, Category > 5)")]
    public int Query_WhereField()
    {
        using var t = _dbe.CreateQuickTransaction();
        return t.Query<BenchBaseArch>().WhereField<EcsBenchBase>(b => b.Category > 5).Execute().Count;
    }

    // ═══════════════════════════════════════════════════════════════
    // Foreach iteration
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "foreach + Read (1K exact)")]
    public float Foreach_ReadComponent()
    {
        using var t = _dbe.CreateQuickTransaction();
        float sum = 0;
        foreach (var entity in t.QueryExact<BenchBaseArch>())
        {
            sum += entity.Read(BenchBaseArch.Data).Value;
        }
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════
    // Enable/Disable throughput
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "Enable/Disable toggle 1K entities")]
    public void EnableDisable_Toggle()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < BaseCount; i++)
        {
            var mut = t.OpenMut(_baseIds[i]);
            mut.Disable(BenchBaseArch.Data);
            mut.Enable(BenchBaseArch.Data);
        }
        t.Commit();
    }
}

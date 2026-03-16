using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Workload: Synthetic Use-Case Compound Benchmarks
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.WorkComp", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct WorkComp
{
    [Field]
    public int Value;

    [Field]
    public long Timestamp;
}

[Component("Typhon.Benchmark.WorkCompB", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct WorkCompB
{
    [Field]
    public float Score;

    [Field]
    public int Category;
}

[Archetype(502)]
class WorkArch : Archetype<WorkArch>
{
    public static readonly Comp<WorkComp> Work = Register<WorkComp>();
}

[Archetype(503)]
class WorkMultiArch : Archetype<WorkMultiArch>
{
    public static readonly Comp<WorkComp> Work = Register<WorkComp>();
    public static readonly Comp<WorkCompB> WorkB = Register<WorkCompB>();
}

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[BenchmarkCategory("Workload", "Regression")]
public class WorkloadBenchmarks
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private EntityId[] _entityIds;
    private string _databaseName;

    private const int PrePopulateCount = 1000;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"WorkloadBench_{Environment.ProcessId}";

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
        _dbe.RegisterComponentFromAccessor<WorkComp>();
        _dbe.RegisterComponentFromAccessor<WorkCompB>();

        Archetype<WorkArch>.Touch();
        Archetype<WorkMultiArch>.Touch();
        _dbe.InitializeArchetypes();

        // Pre-populate entities for read-heavy benchmarks
        _entityIds = new EntityId[PrePopulateCount];
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < PrePopulateCount; i++)
        {
            var comp = new WorkComp { Value = i, Timestamp = DateTime.UtcNow.Ticks };
            _entityIds[i] = t.Spawn<WorkArch>(WorkArch.Work.Set(in comp));
        }
        t.Commit();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Full ECS lifecycle: spawn -> read -> update -> destroy in a single transaction.
    /// Measures the complete entity lifecycle cost.
    /// </summary>
    [Benchmark]
    public void CrudLifecycle()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp = new WorkComp { Value = 42, Timestamp = 12345 };
        var eid = t.Spawn<WorkArch>(WorkArch.Work.Set(in comp));
        var entity = t.Open(eid);
        _ = entity.Read(WorkArch.Work);
        var mutEntity = t.OpenMut(eid);
        mutEntity.Write(WorkArch.Work).Value = 99;
        t.Destroy(eid);
        t.Commit();
    }

    /// <summary>
    /// Read-heavy workload: 90 reads + 10 updates per transaction.
    /// Simulates typical OLTP read-dominated access patterns.
    /// </summary>
    [Benchmark]
    public void ReadHeavy_90_10()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 90; i++)
        {
            var entity = t.Open(_entityIds[i]);
            _ = entity.Read(WorkArch.Work);
        }
        for (int i = 0; i < 10; i++)
        {
            var entity = t.OpenMut(_entityIds[i]);
            ref var comp = ref entity.Write(WorkArch.Work);
            comp.Value = i + 1000;
            comp.Timestamp = DateTime.UtcNow.Ticks;
        }
        t.Commit();
    }

    /// <summary>
    /// Write-heavy batch: spawn 100 entities in a single transaction.
    /// Measures bulk insertion throughput.
    /// </summary>
    [Benchmark]
    public void WriteHeavy_Batch()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++)
        {
            var comp = new WorkComp { Value = i, Timestamp = 12345 };
            t.Spawn<WorkArch>(WorkArch.Work.Set(in comp));
        }
        t.Commit();
    }

    /// <summary>
    /// Multi-component ECS: spawn entity with two components, read, update, commit.
    /// Exercises the multi-component Spawn/Open/Write paths.
    /// </summary>
    [Benchmark]
    public void MultiComponent_Crud()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp1 = new WorkComp { Value = 42, Timestamp = 12345 };
        var comp2 = new WorkCompB { Score = 3.14f, Category = 1 };
        var eid = t.Spawn<WorkMultiArch>(WorkMultiArch.Work.Set(in comp1), WorkMultiArch.WorkB.Set(in comp2));
        var entity = t.Open(eid);
        _ = entity.Read(WorkMultiArch.Work);
        var mutEntity = t.OpenMut(eid);
        mutEntity.Write(WorkMultiArch.Work).Value = 99;
        t.Commit();
    }
}

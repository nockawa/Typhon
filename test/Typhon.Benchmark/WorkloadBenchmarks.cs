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

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[BenchmarkCategory("Workload", "Regression")]
public class WorkloadBenchmarks
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private long[] _entityIds;
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

        // Pre-populate entities for read-heavy benchmarks
        _entityIds = new long[PrePopulateCount];
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < PrePopulateCount; i++)
        {
            var comp = new WorkComp { Value = i, Timestamp = DateTime.UtcNow.Ticks };
            _entityIds[i] = t.CreateEntity(ref comp);
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
    /// Full CRUD lifecycle: create → read → update → delete in a single transaction.
    /// Measures the complete entity lifecycle cost.
    /// </summary>
    [Benchmark]
    public void CrudLifecycle()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp = new WorkComp { Value = 42, Timestamp = 12345 };
        var eid = t.CreateEntity(ref comp);
        t.ReadEntity(eid, out WorkComp _);
        comp.Value = 99;
        t.UpdateEntity(eid, ref comp);
        t.DeleteEntity<WorkComp>(eid);
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
            t.ReadEntity(_entityIds[i], out WorkComp _);
        }
        for (int i = 0; i < 10; i++)
        {
            var comp = new WorkComp { Value = i + 1000, Timestamp = DateTime.UtcNow.Ticks };
            t.UpdateEntity(_entityIds[i], ref comp);
        }
        t.Commit();
    }

    /// <summary>
    /// Write-heavy batch: create 100 entities in a single transaction.
    /// Measures bulk insertion throughput.
    /// </summary>
    [Benchmark]
    public void WriteHeavy_Batch()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++)
        {
            var comp = new WorkComp { Value = i, Timestamp = 12345 };
            t.CreateEntity(ref comp);
        }
        t.Commit();
    }

    /// <summary>
    /// Multi-component CRUD: create entity with two components, read, update, commit.
    /// Exercises the multi-component Transaction API paths.
    /// </summary>
    [Benchmark]
    public void MultiComponent_Crud()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp1 = new WorkComp { Value = 42, Timestamp = 12345 };
        var comp2 = new WorkCompB { Score = 3.14f, Category = 1 };
        var eid = t.CreateEntity(ref comp1, ref comp2);
        t.ReadEntity(eid, out WorkComp _);
        comp1.Value = 99;
        t.UpdateEntity(eid, ref comp1);
        t.Commit();
    }
}

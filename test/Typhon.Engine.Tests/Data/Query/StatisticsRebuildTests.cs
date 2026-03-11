using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

class StatisticsRebuildTests : TestBase<StatisticsRebuildTests>
{
    private static long CreateAndCommitCompD(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var pk = t.CreateEntity(ref d);
        t.Commit();
        return pk;
    }

    private static long CreateAndCommitCompF(DatabaseEngine dbe, int gold, int rank)
    {
        using var t = dbe.CreateQuickTransaction();
        var f = new CompF(gold, rank);
        var pk = t.CreateEntity(ref f);
        t.Commit();
        return pk;
    }

    [Test]
    public void RebuildAll_PopulatesHLL_MCV_Histogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with B from 0 to 199
        for (int i = 0; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // HLL should estimate ~200 distinct values
        var stats = ct.IndexStats[1]; // B field
        Assert.That(stats.HyperLogLog, Is.Not.Null);
        Assert.That(stats.DistinctValues, Is.InRange(180, 220));

        // MCV should be populated
        Assert.That(stats.MostCommonValues, Is.Not.Null);

        // Histogram should be populated and correct
        Assert.That(stats.Histogram, Is.Not.Null);
        Assert.That(stats.Histogram.TotalCount, Is.EqualTo(200));
    }

    [Test]
    public void RebuildAll_AllIndexedFields_SinglePass()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, i * 1.5f, i, i * 2.5);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // All 3 indexed fields (A, B, C) should have statistics
        for (int f = 0; f < ct.IndexStats.Length; f++)
        {
            Assert.That(ct.IndexStats[f].HyperLogLog, Is.Not.Null, $"Field {f} HLL missing");
            Assert.That(ct.IndexStats[f].MostCommonValues, Is.Not.Null, $"Field {f} MCV missing");
            Assert.That(ct.IndexStats[f].Histogram, Is.Not.Null, $"Field {f} Histogram missing");
            Assert.That(ct.IndexStats[f].Histogram.TotalCount, Is.EqualTo(100), $"Field {f} total count wrong");
        }
    }

    [Test]
    public void RebuildAll_SkewedData_MCVCapturesTopValues()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompF>();

        // Gold (AllowMultiple): 80 entities with Gold=42, 20 with Gold=99
        for (int i = 0; i < 80; i++)
        {
            CreateAndCommitCompF(dbe, 42, i);
        }
        for (int i = 0; i < 20; i++)
        {
            CreateAndCommitCompF(dbe, 99, 80 + i);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        var mcv = ct.IndexStats[0].MostCommonValues; // Gold field (index 0)
        Assert.That(mcv, Is.Not.Null);

        Assert.That(mcv.TryGetCount(42, out long count42), Is.True);
        Assert.That(count42, Is.EqualTo(80));

        Assert.That(mcv.TryGetCount(99, out long count99), Is.True);
        Assert.That(count99, Is.EqualTo(20));
    }

    [Test]
    public void RebuildAll_AtomicSwap_NoTornReads()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // First rebuild
        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var firstHll = ct.IndexStats[1].HyperLogLog;
        var firstMcv = ct.IndexStats[1].MostCommonValues;
        var firstHisto = ct.IndexStats[1].Histogram;

        Assert.That(firstHll, Is.Not.Null);
        Assert.That(firstMcv, Is.Not.Null);
        Assert.That(firstHisto, Is.Not.Null);

        // Add more data and rebuild again
        for (int i = 100; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // New references should be different objects (atomic swap, not in-place mutation)
        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Not.SameAs(firstHll));
        Assert.That(ct.IndexStats[1].MostCommonValues, Is.Not.SameAs(firstMcv));
        Assert.That(ct.IndexStats[1].Histogram, Is.Not.SameAs(firstHisto));
        Assert.That(ct.IndexStats[1].Histogram.TotalCount, Is.EqualTo(200));
    }

    [Test]
    public void MutationCounter_IncrementedOnIndexChange()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        Assert.That(ct.MutationsSinceRebuild, Is.EqualTo(0));

        // Create increments
        var pk = CreateAndCommitCompD(dbe, 1.0f, 10, 1.0);
        Assert.That(ct.MutationsSinceRebuild, Is.GreaterThan(0));

        int afterCreate = ct.MutationsSinceRebuild;

        // Update with changed index field increments further
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(2.0f, 20, 2.0); // all fields changed
        t.UpdateEntity(pk, ref d);
        t.Commit();

        Assert.That(ct.MutationsSinceRebuild, Is.GreaterThan(afterCreate));
    }

    [Test]
    public void MutationCounter_ResetByWorkerSimulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 10; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        Assert.That(ct.MutationsSinceRebuild, Is.GreaterThan(0));

        // Simulate what StatisticsWorker does: reset before rebuild
        ct.MutationsSinceRebuild = 0;
        Assert.That(ct.MutationsSinceRebuild, Is.EqualTo(0));
    }

    [Test]
    public void RebuildAll_EmptyTable_NoOp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        // Should not throw on empty table
        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // No statistics built (0 entities)
        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Null);
    }

    [Test]
    public void RebuildAll_AllowMultiple_CountsEntities()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompF>();

        // Gold is AllowMultiple: 30 entities with Gold=10, 20 with Gold=50
        for (int i = 0; i < 30; i++)
        {
            CreateAndCommitCompF(dbe, 10, i);
        }
        for (int i = 0; i < 20; i++)
        {
            CreateAndCommitCompF(dbe, 50, 30 + i);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Histogram total should count entities, not distinct keys
        var stats = ct.IndexStats[0]; // Gold field
        Assert.That(stats.Histogram.TotalCount, Is.EqualTo(50));

        // MCV should capture both values
        Assert.That(stats.MostCommonValues.TryGetCount(10, out long c10), Is.True);
        Assert.That(c10, Is.EqualTo(30));
        Assert.That(stats.MostCommonValues.TryGetCount(50, out long c50), Is.True);
        Assert.That(c50, Is.EqualTo(20));
    }

    [Test]
    public void Worker_StartsAndStops_Lifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var options = new StatisticsOptions { Enabled = true, PollIntervalMs = 100 };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);
        worker.Start();

        Assert.That(worker.IsRunning, Is.True);

        worker.Dispose();

        // After dispose, thread should have stopped (allow brief delay)
        Assert.That(worker.IsRunning, Is.False);
    }

    [Test]
    public void Worker_ForceRebuild_WakesImmediately()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // Set mutation count above threshold
        ct.MutationsSinceRebuild = 2000;

        var options = new StatisticsOptions
        {
            Enabled = true,
            PollIntervalMs = 60000, // Very long poll — won't trigger naturally
            MutationThreshold = 1000,
            MinEntitiesForRebuild = 50
        };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);
        worker.Start();

        // Force rebuild should wake the thread immediately
        worker.ForceRebuild();

        // Wait briefly for the rebuild to complete
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ct.IndexStats[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(10);
        }

        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Not.Null, "ForceRebuild should trigger statistics rebuild");
    }
}

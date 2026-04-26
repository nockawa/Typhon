using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
public class MetricTypesTests
{
    #region MemoryMetrics Tests

    [Test]
    public void MemoryMetrics_StoresValues()
    {
        var metrics = new MemoryMetrics(1024, 2048);

        Assert.That(metrics.AllocatedBytes, Is.EqualTo(1024));
        Assert.That(metrics.PeakBytes, Is.EqualTo(2048));
    }

    [Test]
    public void MemoryMetrics_ValueEquality()
    {
        var a = new MemoryMetrics(100, 200);
        var b = new MemoryMetrics(100, 200);
        var c = new MemoryMetrics(100, 300);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
    }

    [Test]
    public void MemoryMetrics_DefaultIsZero()
    {
        var metrics = default(MemoryMetrics);

        Assert.That(metrics.AllocatedBytes, Is.EqualTo(0));
        Assert.That(metrics.PeakBytes, Is.EqualTo(0));
    }

    #endregion

    #region CapacityMetrics Tests

    [Test]
    public void CapacityMetrics_StoresValues()
    {
        var metrics = new CapacityMetrics(50, 100);

        Assert.That(metrics.Current, Is.EqualTo(50));
        Assert.That(metrics.Maximum, Is.EqualTo(100));
    }

    [Test]
    public void CapacityMetrics_Utilization_ComputesCorrectly()
    {
        var metrics = new CapacityMetrics(75, 100);

        Assert.That(metrics.Utilization, Is.EqualTo(0.75).Within(0.0001));
    }

    [Test]
    public void CapacityMetrics_Utilization_ZeroMaximum_ReturnsZero()
    {
        var metrics = new CapacityMetrics(0, 0);

        Assert.That(metrics.Utilization, Is.EqualTo(0.0));
    }

    [Test]
    public void CapacityMetrics_Utilization_Full_ReturnsOne()
    {
        var metrics = new CapacityMetrics(256, 256);

        Assert.That(metrics.Utilization, Is.EqualTo(1.0).Within(0.0001));
    }

    [Test]
    public void CapacityMetrics_Utilization_Empty_ReturnsZero()
    {
        var metrics = new CapacityMetrics(0, 100);

        Assert.That(metrics.Utilization, Is.EqualTo(0.0));
    }

    [Test]
    public void CapacityMetrics_ValueEquality()
    {
        var a = new CapacityMetrics(50, 100);
        var b = new CapacityMetrics(50, 100);
        var c = new CapacityMetrics(60, 100);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
    }

    #endregion

    #region DiskIOMetrics Tests

    [Test]
    public void DiskIOMetrics_StoresValues()
    {
        var metrics = new DiskIOMetrics(100, 50, 8192, 4096);

        Assert.That(metrics.ReadOps, Is.EqualTo(100));
        Assert.That(metrics.WriteOps, Is.EqualTo(50));
        Assert.That(metrics.ReadBytes, Is.EqualTo(8192));
        Assert.That(metrics.WriteBytes, Is.EqualTo(4096));
    }

    [Test]
    public void DiskIOMetrics_ValueEquality()
    {
        var a = new DiskIOMetrics(10, 20, 30, 40);
        var b = new DiskIOMetrics(10, 20, 30, 40);
        var c = new DiskIOMetrics(10, 20, 30, 50);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
    }

    [Test]
    public void DiskIOMetrics_DefaultIsZero()
    {
        var metrics = default(DiskIOMetrics);

        Assert.That(metrics.ReadOps, Is.EqualTo(0));
        Assert.That(metrics.WriteOps, Is.EqualTo(0));
        Assert.That(metrics.ReadBytes, Is.EqualTo(0));
        Assert.That(metrics.WriteBytes, Is.EqualTo(0));
    }

    #endregion

    #region ThroughputMetric Tests

    [Test]
    public void ThroughputMetric_StoresValues()
    {
        var metric = new ThroughputMetric("CacheHits", 1000);

        Assert.That(metric.Name, Is.EqualTo("CacheHits"));
        Assert.That(metric.Count, Is.EqualTo(1000));
    }

    [Test]
    public void ThroughputMetric_ValueEquality()
    {
        var a = new ThroughputMetric("CacheHits", 100);
        var b = new ThroughputMetric("CacheHits", 100);
        var c = new ThroughputMetric("CacheHits", 200);
        var d = new ThroughputMetric("CacheMisses", 100);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
        Assert.That(a, Is.Not.EqualTo(d));
    }

    [Test]
    public void ThroughputMetric_WithMetricNames()
    {
        var metric = new ThroughputMetric(MetricNames.CacheHits, 500);

        Assert.That(metric.Name, Is.EqualTo("CacheHits"));
        Assert.That(metric.Count, Is.EqualTo(500));
    }

    #endregion

    #region DurationMetric Tests

    [Test]
    public void DurationMetric_StoresValues()
    {
        var metric = new DurationMetric("Checkpoint", 100, 150, 500);

        Assert.That(metric.Name, Is.EqualTo("Checkpoint"));
        Assert.That(metric.LastUs, Is.EqualTo(100));
        Assert.That(metric.AvgUs, Is.EqualTo(150));
        Assert.That(metric.MaxUs, Is.EqualTo(500));
    }

    [Test]
    public void DurationMetric_ValueEquality()
    {
        var a = new DurationMetric("Flush", 50, 75, 200);
        var b = new DurationMetric("Flush", 50, 75, 200);
        var c = new DurationMetric("Flush", 50, 75, 300);
        var d = new DurationMetric("Commit", 50, 75, 200);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
        Assert.That(a, Is.Not.EqualTo(d));
    }

    [Test]
    public void DurationMetric_WithMetricNames()
    {
        var metric = new DurationMetric(MetricNames.CheckpointFlush, 100, 120, 500);

        Assert.That(metric.Name, Is.EqualTo("CheckpointFlush"));
    }

    #endregion

    #region MetricNames Tests

    [Test]
    public void MetricNames_CacheMetrics_AreConsistent()
    {
        Assert.That(MetricNames.CacheHits, Is.EqualTo("CacheHits"));
        Assert.That(MetricNames.CacheMisses, Is.EqualTo("CacheMisses"));
        Assert.That(MetricNames.Evictions, Is.EqualTo("Evictions"));
    }

    [Test]
    public void MetricNames_TransactionMetrics_AreConsistent()
    {
        Assert.That(MetricNames.Created, Is.EqualTo("Created"));
        Assert.That(MetricNames.Committed, Is.EqualTo("Committed"));
        Assert.That(MetricNames.RolledBack, Is.EqualTo("RolledBack"));
        Assert.That(MetricNames.Conflicts, Is.EqualTo("Conflicts"));
    }

    [Test]
    public void MetricNames_IndexMetrics_AreConsistent()
    {
        Assert.That(MetricNames.Lookups, Is.EqualTo("Lookups"));
        Assert.That(MetricNames.RangeScans, Is.EqualTo("RangeScans"));
        Assert.That(MetricNames.Inserts, Is.EqualTo("Inserts"));
        Assert.That(MetricNames.Deletes, Is.EqualTo("Deletes"));
        Assert.That(MetricNames.Splits, Is.EqualTo("Splits"));
        Assert.That(MetricNames.Merges, Is.EqualTo("Merges"));
    }

    [Test]
    public void MetricNames_DurabilityMetrics_AreConsistent()
    {
        Assert.That(MetricNames.RecordsWritten, Is.EqualTo("RecordsWritten"));
        Assert.That(MetricNames.Flushes, Is.EqualTo("Flushes"));
        Assert.That(MetricNames.CheckpointsCompleted, Is.EqualTo("CheckpointsCompleted"));
    }

    [Test]
    public void MetricNames_DurationNames_AreConsistent()
    {
        Assert.That(MetricNames.TransactionLifetime, Is.EqualTo("TransactionLifetime"));
        Assert.That(MetricNames.CheckpointFlush, Is.EqualTo("CheckpointFlush"));
        Assert.That(MetricNames.Flush, Is.EqualTo("Flush"));
        Assert.That(MetricNames.Commit, Is.EqualTo("Commit"));
        Assert.That(MetricNames.SnapshotCreation, Is.EqualTo("SnapshotCreation"));
    }

    #endregion

    #region Zero-Allocation Pattern Tests

    [Test]
    public void MetricTypes_AreValueTypes()
    {
        // Verify all metric types are structs (value types) for zero-allocation
        Assert.That(typeof(MemoryMetrics).IsValueType, Is.True);
        Assert.That(typeof(CapacityMetrics).IsValueType, Is.True);
        Assert.That(typeof(DiskIOMetrics).IsValueType, Is.True);
        Assert.That(typeof(ThroughputMetric).IsValueType, Is.True);
        Assert.That(typeof(DurationMetric).IsValueType, Is.True);
    }

    [Test]
    public void MetricTypes_CanBePassedByValue()
    {
        // This test documents that metrics can be passed without boxing
        var memory = new MemoryMetrics(100, 200);
        var capacity = new CapacityMetrics(50, 100);
        var diskIO = new DiskIOMetrics(10, 20, 30, 40);
        var throughput = new ThroughputMetric("Test", 100);
        var duration = new DurationMetric("Test", 50, 60, 100);

        // Pass by value (copies on stack)
        AssertMemoryMetrics(memory);
        AssertCapacityMetrics(capacity);
        AssertDiskIOMetrics(diskIO);
        AssertThroughputMetric(throughput);
        AssertDurationMetric(duration);
    }

    private static void AssertMemoryMetrics(MemoryMetrics m) => Assert.That(m.AllocatedBytes, Is.GreaterThanOrEqualTo(0));
    private static void AssertCapacityMetrics(CapacityMetrics m) => Assert.That(m.Utilization, Is.InRange(0.0, 1.0));
    private static void AssertDiskIOMetrics(DiskIOMetrics m) => Assert.That(m.ReadOps, Is.GreaterThanOrEqualTo(0));
    private static void AssertThroughputMetric(ThroughputMetric m) => Assert.That(m.Name, Is.Not.Null);
    private static void AssertDurationMetric(DurationMetric m) => Assert.That(m.Name, Is.Not.Null);

    #endregion
}

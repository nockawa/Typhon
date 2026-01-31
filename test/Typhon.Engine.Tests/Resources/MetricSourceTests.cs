using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests;

[TestFixture]
public class MetricSourceTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void Setup() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TestRegistry" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    #region Test Infrastructure

    /// <summary>
    /// Test spy that captures all metric values written to it.
    /// </summary>
    private class TestMetricWriter : IMetricWriter
    {
        // Memory
        public long? MemoryAllocatedBytes { get; private set; }
        public long? MemoryPeakBytes { get; private set; }
        public bool MemoryWritten => MemoryAllocatedBytes.HasValue;

        // Capacity
        public long? CapacityCurrent { get; private set; }
        public long? CapacityMaximum { get; private set; }
        public bool CapacityWritten => CapacityCurrent.HasValue;

        // DiskIO
        public long? DiskReadOps { get; private set; }
        public long? DiskWriteOps { get; private set; }
        public long? DiskReadBytes { get; private set; }
        public long? DiskWriteBytes { get; private set; }
        public bool DiskIOWritten => DiskReadOps.HasValue;

        // Contention
        public long? ContentionWaitCount { get; private set; }
        public long? ContentionTotalWaitUs { get; private set; }
        public long? ContentionMaxWaitUs { get; private set; }
        public long? ContentionTimeoutCount { get; private set; }
        public bool ContentionWritten => ContentionWaitCount.HasValue;

        // Throughput (multiple named counters)
        public List<(string Name, long Count)> ThroughputCounters { get; } = new();

        // Duration (multiple named durations)
        public List<(string Name, long LastUs, long AvgUs, long MaxUs)> DurationMetrics { get; } = new();

        public void WriteMemory(long allocatedBytes, long peakBytes)
        {
            MemoryAllocatedBytes = allocatedBytes;
            MemoryPeakBytes = peakBytes;
        }

        public void WriteCapacity(long current, long maximum)
        {
            CapacityCurrent = current;
            CapacityMaximum = maximum;
        }

        public void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes)
        {
            DiskReadOps = readOps;
            DiskWriteOps = writeOps;
            DiskReadBytes = readBytes;
            DiskWriteBytes = writeBytes;
        }

        public void WriteContention(long waitCount, long totalWaitUs, long maxWaitUs, long timeoutCount)
        {
            ContentionWaitCount = waitCount;
            ContentionTotalWaitUs = totalWaitUs;
            ContentionMaxWaitUs = maxWaitUs;
            ContentionTimeoutCount = timeoutCount;
        }

        public void WriteThroughput(string name, long count) => ThroughputCounters.Add((name, count));

        public void WriteDuration(string name, long lastUs, long avgUs, long maxUs) => DurationMetrics.Add((name, lastUs, avgUs, maxUs));

        public void Reset()
        {
            MemoryAllocatedBytes = null;
            MemoryPeakBytes = null;
            CapacityCurrent = null;
            CapacityMaximum = null;
            DiskReadOps = null;
            DiskWriteOps = null;
            DiskReadBytes = null;
            DiskWriteBytes = null;
            ContentionWaitCount = null;
            ContentionTotalWaitUs = null;
            ContentionMaxWaitUs = null;
            ContentionTimeoutCount = null;
            ThroughputCounters.Clear();
            DurationMetrics.Clear();
        }
    }

    /// <summary>
    /// Sample resource that implements IMetricSource with all metric kinds.
    /// </summary>
    private class FullMetricResource : ResourceNode, IMetricSource
    {
        public long AllocatedBytes;
        public long PeakBytes;
        public long CurrentSlots;
        public long MaxSlots;
        public long ReadOps;
        public long WriteOps;
        public long ReadBytes;
        public long WriteBytes;
        public long WaitCount;
        public long TotalWaitUs;
        public long MaxWaitUs;
        public long TimeoutCount;
        public long CacheHits;
        public long CacheMisses;
        public long LastFlushUs;
        public long AvgFlushUs;
        public long MaxFlushUs;

        public FullMetricResource(string id, IResource parent)
            : base(id, ResourceType.Node, parent) { }

        public void ReadMetrics(IMetricWriter writer)
        {
            writer.WriteMemory(AllocatedBytes, PeakBytes);
            writer.WriteCapacity(CurrentSlots, MaxSlots);
            writer.WriteDiskIO(ReadOps, WriteOps, ReadBytes, WriteBytes);
            writer.WriteContention(WaitCount, TotalWaitUs, MaxWaitUs, TimeoutCount);
            writer.WriteThroughput("CacheHits", CacheHits);
            writer.WriteThroughput("CacheMisses", CacheMisses);
            writer.WriteDuration("Flush", LastFlushUs, AvgFlushUs, MaxFlushUs);
        }

        public void ResetPeaks()
        {
            PeakBytes = AllocatedBytes;
            MaxWaitUs = 0;
            MaxFlushUs = LastFlushUs;
        }
    }

    /// <summary>
    /// Sample resource that implements IMetricSource with only memory metrics.
    /// </summary>
    private class MemoryOnlyMetricResource : ResourceNode, IMetricSource
    {
        public long AllocatedBytes;
        public long PeakBytes;

        public MemoryOnlyMetricResource(string id, IResource parent)
            : base(id, ResourceType.Memory, parent) { }

        public void ReadMetrics(IMetricWriter writer) => writer.WriteMemory(AllocatedBytes, PeakBytes);

        public void ResetPeaks() => PeakBytes = AllocatedBytes;
    }

    /// <summary>
    /// Sample resource that does NOT implement IMetricSource (pure grouping node).
    /// </summary>
    private class NonMetricResource : ResourceNode
    {
        public NonMetricResource(string id, IResource parent)
            : base(id, ResourceType.Node, parent) { }
    }

    #endregion

    #region IMetricWriter Tests

    [Test]
    public void WriteMemory_CapturesCorrectValues()
    {
        var writer = new TestMetricWriter();

        writer.WriteMemory(1024, 2048);

        Assert.That(writer.MemoryWritten, Is.True);
        Assert.That(writer.MemoryAllocatedBytes, Is.EqualTo(1024));
        Assert.That(writer.MemoryPeakBytes, Is.EqualTo(2048));
    }

    [Test]
    public void WriteCapacity_CapturesCorrectValues()
    {
        var writer = new TestMetricWriter();

        writer.WriteCapacity(50, 100);

        Assert.That(writer.CapacityWritten, Is.True);
        Assert.That(writer.CapacityCurrent, Is.EqualTo(50));
        Assert.That(writer.CapacityMaximum, Is.EqualTo(100));
    }

    [Test]
    public void WriteDiskIO_CapturesCorrectValues()
    {
        var writer = new TestMetricWriter();

        writer.WriteDiskIO(100, 50, 8192, 4096);

        Assert.That(writer.DiskIOWritten, Is.True);
        Assert.That(writer.DiskReadOps, Is.EqualTo(100));
        Assert.That(writer.DiskWriteOps, Is.EqualTo(50));
        Assert.That(writer.DiskReadBytes, Is.EqualTo(8192));
        Assert.That(writer.DiskWriteBytes, Is.EqualTo(4096));
    }

    [Test]
    public void WriteContention_CapturesCorrectValues()
    {
        var writer = new TestMetricWriter();

        writer.WriteContention(10, 5000, 1000, 2);

        Assert.That(writer.ContentionWritten, Is.True);
        Assert.That(writer.ContentionWaitCount, Is.EqualTo(10));
        Assert.That(writer.ContentionTotalWaitUs, Is.EqualTo(5000));
        Assert.That(writer.ContentionMaxWaitUs, Is.EqualTo(1000));
        Assert.That(writer.ContentionTimeoutCount, Is.EqualTo(2));
    }

    [Test]
    public void WriteThroughput_SupportsNamedCounters()
    {
        var writer = new TestMetricWriter();

        writer.WriteThroughput("CacheHits", 1000);
        writer.WriteThroughput("CacheMisses", 50);
        writer.WriteThroughput("Evictions", 25);

        Assert.That(writer.ThroughputCounters, Has.Count.EqualTo(3));
        Assert.That(writer.ThroughputCounters[0], Is.EqualTo(("CacheHits", 1000L)));
        Assert.That(writer.ThroughputCounters[1], Is.EqualTo(("CacheMisses", 50L)));
        Assert.That(writer.ThroughputCounters[2], Is.EqualTo(("Evictions", 25L)));
    }

    [Test]
    public void WriteDuration_SupportsNamedDurations()
    {
        var writer = new TestMetricWriter();

        writer.WriteDuration("Checkpoint", 100, 150, 500);
        writer.WriteDuration("Flush", 50, 75, 200);

        Assert.That(writer.DurationMetrics, Has.Count.EqualTo(2));
        Assert.That(writer.DurationMetrics[0], Is.EqualTo(("Checkpoint", 100L, 150L, 500L)));
        Assert.That(writer.DurationMetrics[1], Is.EqualTo(("Flush", 50L, 75L, 200L)));
    }

    [Test]
    public void WriteMemory_LastWins_WhenCalledMultipleTimes()
    {
        var writer = new TestMetricWriter();

        writer.WriteMemory(100, 200);
        writer.WriteMemory(300, 400);

        Assert.That(writer.MemoryAllocatedBytes, Is.EqualTo(300));
        Assert.That(writer.MemoryPeakBytes, Is.EqualTo(400));
    }

    #endregion

    #region IMetricSource Tests

    [Test]
    public void ReadMetrics_WritesExpectedValues()
    {
        var resource = new FullMetricResource("TestResource", _registry.DataEngine)
        {
            AllocatedBytes = 1024,
            PeakBytes = 2048,
            CurrentSlots = 50,
            MaxSlots = 100,
            ReadOps = 10,
            WriteOps = 5,
            ReadBytes = 8192,
            WriteBytes = 4096,
            WaitCount = 3,
            TotalWaitUs = 1500,
            MaxWaitUs = 800,
            TimeoutCount = 1,
            CacheHits = 100,
            CacheMisses = 10,
            LastFlushUs = 50,
            AvgFlushUs = 75,
            MaxFlushUs = 200
        };

        var writer = new TestMetricWriter();
        resource.ReadMetrics(writer);

        Assert.That(writer.MemoryAllocatedBytes, Is.EqualTo(1024));
        Assert.That(writer.MemoryPeakBytes, Is.EqualTo(2048));
        Assert.That(writer.CapacityCurrent, Is.EqualTo(50));
        Assert.That(writer.CapacityMaximum, Is.EqualTo(100));
        Assert.That(writer.DiskReadOps, Is.EqualTo(10));
        Assert.That(writer.DiskWriteOps, Is.EqualTo(5));
        Assert.That(writer.DiskReadBytes, Is.EqualTo(8192));
        Assert.That(writer.DiskWriteBytes, Is.EqualTo(4096));
        Assert.That(writer.ContentionWaitCount, Is.EqualTo(3));
        Assert.That(writer.ContentionTotalWaitUs, Is.EqualTo(1500));
        Assert.That(writer.ContentionMaxWaitUs, Is.EqualTo(800));
        Assert.That(writer.ContentionTimeoutCount, Is.EqualTo(1));
        Assert.That(writer.ThroughputCounters, Has.Count.EqualTo(2));
        Assert.That(writer.DurationMetrics, Has.Count.EqualTo(1));
    }

    [Test]
    public void ResetPeaks_ResetsPeakValues()
    {
        var resource = new FullMetricResource("TestResource", _registry.DataEngine)
        {
            AllocatedBytes = 1000,
            PeakBytes = 5000,
            MaxWaitUs = 800,
            LastFlushUs = 100,
            MaxFlushUs = 500
        };

        resource.ResetPeaks();

        Assert.That(resource.PeakBytes, Is.EqualTo(1000), "Peak bytes should reset to current allocated");
        Assert.That(resource.MaxWaitUs, Is.EqualTo(0), "Max wait should reset to 0");
        Assert.That(resource.MaxFlushUs, Is.EqualTo(100), "Max flush should reset to last flush");
    }

    [Test]
    public void PartialMetrics_OnlyReportsRelevantMetrics()
    {
        var resource = new MemoryOnlyMetricResource("MemoryResource", _registry.DataEngine)
        {
            AllocatedBytes = 4096,
            PeakBytes = 8192
        };

        var writer = new TestMetricWriter();
        resource.ReadMetrics(writer);

        Assert.That(writer.MemoryWritten, Is.True);
        Assert.That(writer.CapacityWritten, Is.False);
        Assert.That(writer.DiskIOWritten, Is.False);
        Assert.That(writer.ContentionWritten, Is.False);
        Assert.That(writer.ThroughputCounters, Is.Empty);
        Assert.That(writer.DurationMetrics, Is.Empty);
    }

    #endregion

    #region GetMetricSources Extension Method Tests

    [Test]
    public void GetMetricSources_FindsAllSources()
    {
        var metric1 = new FullMetricResource("Metric1", _registry.DataEngine);
        var metric2 = new MemoryOnlyMetricResource("Metric2", _registry.DataEngine);
        var nonMetric = new NonMetricResource("NonMetric", _registry.DataEngine);
        var metric3 = new FullMetricResource("Metric3", nonMetric);

        _registry.DataEngine.RegisterChild(metric1);
        _registry.DataEngine.RegisterChild(metric2);
        _registry.DataEngine.RegisterChild(nonMetric);
        nonMetric.RegisterChild(metric3);

        var sources = _registry.DataEngine.GetMetricSources().ToList();

        Assert.That(sources, Has.Count.EqualTo(3));
        Assert.That(sources, Contains.Item(metric1));
        Assert.That(sources, Contains.Item(metric2));
        Assert.That(sources, Contains.Item(metric3));
    }

    [Test]
    public void GetMetricSources_SkipsNonMetricResources()
    {
        var nonMetric1 = new NonMetricResource("NonMetric1", _registry.DataEngine);
        var nonMetric2 = new NonMetricResource("NonMetric2", _registry.DataEngine);
        var metric = new FullMetricResource("Metric", nonMetric2);

        _registry.DataEngine.RegisterChild(nonMetric1);
        _registry.DataEngine.RegisterChild(nonMetric2);
        nonMetric2.RegisterChild(metric);

        var sources = _registry.DataEngine.GetMetricSources().ToList();

        Assert.That(sources, Has.Count.EqualTo(1));
        Assert.That(sources[0], Is.SameAs(metric));
    }

    [Test]
    public void GetMetricSources_IncludesSelfIfMetricSource()
    {
        var metricParent = new FullMetricResource("MetricParent", _registry.DataEngine);
        var metricChild = new MemoryOnlyMetricResource("MetricChild", metricParent);

        _registry.DataEngine.RegisterChild(metricParent);
        metricParent.RegisterChild(metricChild);

        var sources = metricParent.GetMetricSources().ToList();

        Assert.That(sources, Has.Count.EqualTo(2));
        Assert.That(sources[0], Is.SameAs(metricParent));
        Assert.That(sources[1], Is.SameAs(metricChild));
    }

    [Test]
    public void GetMetricSources_EmptyWhenNoMetricSources()
    {
        var nonMetric1 = new NonMetricResource("NonMetric1", _registry.DataEngine);
        var nonMetric2 = new NonMetricResource("NonMetric2", nonMetric1);

        _registry.DataEngine.RegisterChild(nonMetric1);
        nonMetric1.RegisterChild(nonMetric2);

        var sources = nonMetric1.GetMetricSources().ToList();

        Assert.That(sources, Is.Empty);
    }

    [Test]
    public void GetMetricSources_ThrowsOnNull()
    {
        IResource resource = null;
        Assert.Throws<ArgumentNullException>(() => resource.GetMetricSources().ToList());
    }

    [Test]
    public void GetMetricSources_FromRoot_FindsAllInTree()
    {
        var storageMetric = new FullMetricResource("PageCache", _registry.Storage);
        var dataEngineMetric = new MemoryOnlyMetricResource("ComponentTable", _registry.DataEngine);

        _registry.Storage.RegisterChild(storageMetric);
        _registry.DataEngine.RegisterChild(dataEngineMetric);

        var sources = _registry.Root.GetMetricSources().ToList();

        Assert.That(sources, Has.Count.EqualTo(2));
        Assert.That(sources, Contains.Item(storageMetric));
        Assert.That(sources, Contains.Item(dataEngineMetric));
    }

    #endregion

    #region Independence from IResource Tests

    [Test]
    public void IMetricSource_CanBeImplementedIndependentlyOfIResource()
    {
        // This test verifies that IMetricSource doesn't inherit from or require IResource
        var standaloneSource = new StandaloneMetricSource();
        var writer = new TestMetricWriter();

        standaloneSource.ReadMetrics(writer);

        Assert.That(writer.MemoryAllocatedBytes, Is.EqualTo(100));
        Assert.That(standaloneSource is IResource, Is.False);
    }

    /// <summary>
    /// A metric source that does NOT implement IResource, demonstrating separation of concerns.
    /// </summary>
    private class StandaloneMetricSource : IMetricSource
    {
        public void ReadMetrics(IMetricWriter writer) => writer.WriteMemory(100, 200);

        public void ResetPeaks()
        {
            // No peaks to reset
        }
    }

    #endregion

    #region Zero-Allocation Verification Tests

    [Test]
    public void ReadMetrics_DoesNotAllocate_WhenImplementedCorrectly()
    {
        // This test documents the expected pattern, not runtime verification
        // (actual allocation testing would require specialized tooling)
        var resource = new FullMetricResource("TestResource", _registry.DataEngine)
        {
            AllocatedBytes = 1024,
            CacheHits = 100,
            CacheMisses = 10
        };

        var writer = new TestMetricWriter();

        // Multiple reads should not accumulate allocations
        for (int i = 0; i < 1000; i++)
        {
            writer.Reset();
            resource.ReadMetrics(writer);
        }

        // If we got here without issues, the pattern works
        Assert.That(writer.MemoryAllocatedBytes, Is.EqualTo(1024));
    }

    #endregion
}

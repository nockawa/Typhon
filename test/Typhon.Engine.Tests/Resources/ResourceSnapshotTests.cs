using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the ResourceSnapshot and query API.
/// </summary>
[TestFixture]
public class ResourceSnapshotTests
{
    private ResourceRegistry _registry;
    private ResourceGraph _graph;

    [SetUp]
    public void Setup()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TestRegistry" });
        _graph = new ResourceGraph(_registry);
    }

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    #region Test Infrastructure

    /// <summary>
    /// Sample resource that implements IMetricSource with configurable metrics.
    /// </summary>
    private class TestMetricResource : ResourceNode, IMetricSource
    {
        public long AllocatedBytes;
        public long PeakBytes;
        public long CapacityCurrent;
        public long CapacityMax;
        public long ContentionWaitCount;
        public long ContentionTotalWaitUs;
        public long ContentionMaxWaitUs;
        public long CacheHits;
        public long CacheMisses;

        public TestMetricResource(string id, IResource parent)
            : base(id, ResourceType.Node, parent) { }

        public void ReadMetrics(IMetricWriter writer)
        {
            if (AllocatedBytes > 0 || PeakBytes > 0)
                writer.WriteMemory(AllocatedBytes, PeakBytes);

            if (CapacityMax > 0)
                writer.WriteCapacity(CapacityCurrent, CapacityMax);

            if (ContentionWaitCount > 0)
                writer.WriteContention(ContentionWaitCount, ContentionTotalWaitUs, ContentionMaxWaitUs, 0);

            // Always write throughput metrics (even if zero) to enable rate computation
            writer.WriteThroughput("CacheHits", CacheHits);
            writer.WriteThroughput("CacheMisses", CacheMisses);
        }

        public void ResetPeaks()
        {
            PeakBytes = AllocatedBytes;
            ContentionMaxWaitUs = 0;
        }
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

    #region Basic Snapshot Tests

    [Test]
    public void GetSnapshot_ReturnsNonNull()
    {
        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.Timestamp, Is.LessThanOrEqualTo(DateTime.UtcNow));
        Assert.That(snapshot.Timestamp, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-1)));
    }

    [Test]
    public void GetSnapshot_IncludesRootAndSubsystems()
    {
        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot.Nodes.ContainsKey("Root"), Is.True);
        Assert.That(snapshot.Nodes.ContainsKey("Root/Storage"), Is.True);
        Assert.That(snapshot.Nodes.ContainsKey("Root/DataEngine"), Is.True);
        Assert.That(snapshot.Nodes.ContainsKey("Root/Durability"), Is.True);
        Assert.That(snapshot.Nodes.ContainsKey("Root/Allocation"), Is.True);
    }

    [Test]
    public void GetSnapshot_FirstSnapshotHasNullRates()
    {
        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot.Rates, Is.Null,
            "First snapshot should have null rates (no previous to compare)");
    }

    [Test]
    public void GetSnapshot_IncludesRegisteredResources()
    {
        var resource = new TestMetricResource("TestResource", _registry.DataEngine);
        _registry.DataEngine.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot.Nodes.ContainsKey("Root/DataEngine/TestResource"), Is.True);
    }

    [Test]
    public void GetSnapshot_CapturesMetrics()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            AllocatedBytes = 1024000,
            PeakBytes = 2048000,
            CapacityCurrent = 500,
            CapacityMax = 1000
        };
        _registry.Storage.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();

        var node = snapshot.Nodes["Root/Storage/PageCache"];
        Assert.That(node.Memory.HasValue, Is.True);
        Assert.That(node.Memory.Value.AllocatedBytes, Is.EqualTo(1024000));
        Assert.That(node.Memory.Value.PeakBytes, Is.EqualTo(2048000));
        Assert.That(node.Capacity.HasValue, Is.True);
        Assert.That(node.Capacity.Value.Current, Is.EqualTo(500));
        Assert.That(node.Capacity.Value.Maximum, Is.EqualTo(1000));
        Assert.That(node.Capacity.Value.Utilization, Is.EqualTo(0.5));
    }

    [Test]
    public void GetSnapshot_NonMetricResourceHasNullMetrics()
    {
        var resource = new NonMetricResource("GroupingNode", _registry.DataEngine);
        _registry.DataEngine.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();

        var node = snapshot.Nodes["Root/DataEngine/GroupingNode"];
        Assert.That(node.Memory.HasValue, Is.False);
        Assert.That(node.Capacity.HasValue, Is.False);
        Assert.That(node.Contention.HasValue, Is.False);
        Assert.That(node.DiskIO.HasValue, Is.False);
        Assert.That(node.Throughput, Is.Empty);
        Assert.That(node.Duration, Is.Empty);
    }

    #endregion

    #region Subtree Snapshot Tests

    [Test]
    public void GetSnapshot_Subtree_OnlyIncludesSubtreeNodes()
    {
        var storageResource = new TestMetricResource("PageCache", _registry.Storage);
        var dataResource = new TestMetricResource("ComponentTable", _registry.DataEngine);
        _registry.Storage.RegisterChild(storageResource);
        _registry.DataEngine.RegisterChild(dataResource);

        var snapshot = _graph.GetSnapshot(_registry.Storage);

        // Should include Storage and its children
        Assert.That(snapshot.Nodes.ContainsKey("Root/Storage"), Is.True);
        Assert.That(snapshot.Nodes.ContainsKey("Root/Storage/PageCache"), Is.True);

        // Should NOT include DataEngine
        Assert.That(snapshot.Nodes.ContainsKey("Root/DataEngine"), Is.False);
        Assert.That(snapshot.Nodes.ContainsKey("Root/DataEngine/ComponentTable"), Is.False);
    }

    #endregion

    #region Rate Computation Tests

    [Test]
    public void GetSnapshot_SecondSnapshotHasRates()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CacheHits = 100,
            CacheMisses = 10
        };
        _registry.Storage.RegisterChild(resource);

        // First snapshot
        _graph.GetSnapshot();

        // Wait a bit and update counters
        Thread.Sleep(50);
        resource.CacheHits = 200;
        resource.CacheMisses = 15;

        // Second snapshot
        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot.Rates, Is.Not.Null,
            "Second snapshot should have rates");
        Assert.That(snapshot.Rates.ContainsNode("Root/Storage/PageCache"), Is.True);
    }

    [Test]
    public void GetSnapshot_RatesComputedCorrectly()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CacheHits = 0,
            CacheMisses = 0
        };
        _registry.Storage.RegisterChild(resource);

        // First snapshot
        _graph.GetSnapshot();

        // Wait 100ms and add 100 hits
        Thread.Sleep(100);
        resource.CacheHits = 100;

        // Second snapshot
        var snapshot = _graph.GetSnapshot();

        // Should be approximately 1000 ops/sec (100 ops in 0.1 sec)
        var rate = snapshot.Rates.GetRate("Root/Storage/PageCache", "CacheHits");
        Assert.That(rate, Is.GreaterThan(500), "Rate should be > 500 ops/sec");
        Assert.That(rate, Is.LessThan(2000), "Rate should be < 2000 ops/sec");
    }

    [Test]
    public void ThroughputRates_IndexerReturnsEmptyForUnknownPath()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CacheHits = 100
        };
        _registry.Storage.RegisterChild(resource);

        _graph.GetSnapshot();
        Thread.Sleep(10);
        resource.CacheHits = 200;
        var snapshot = _graph.GetSnapshot();

        var unknownRates = snapshot.Rates["NonExistent/Path"];
        Assert.That(unknownRates, Is.Empty);
    }

    [Test]
    public void ThroughputRates_GetRateReturnsZeroForUnknown()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CacheHits = 100
        };
        _registry.Storage.RegisterChild(resource);

        _graph.GetSnapshot();
        Thread.Sleep(10);
        resource.CacheHits = 200;
        var snapshot = _graph.GetSnapshot();

        var rate = snapshot.Rates.GetRate("Root/Storage/PageCache", "NonExistentMetric");
        Assert.That(rate, Is.EqualTo(0));
    }

    #endregion

    #region Query Method Tests

    [Test]
    public void GetSubtreeMemory_SumsAllDescendants()
    {
        var resource1 = new TestMetricResource("Cache1", _registry.Storage) { AllocatedBytes = 1000 };
        var resource2 = new TestMetricResource("Cache2", _registry.Storage) { AllocatedBytes = 2000 };
        var child = new TestMetricResource("SubCache", resource1) { AllocatedBytes = 500 };

        _registry.Storage.RegisterChild(resource1);
        _registry.Storage.RegisterChild(resource2);
        resource1.RegisterChild(child);

        var snapshot = _graph.GetSnapshot();
        var total = snapshot.GetSubtreeMemory("Root/Storage");

        Assert.That(total, Is.EqualTo(3500), "Total should be 1000 + 2000 + 500");
    }

    [Test]
    public void GetSubtreeMemory_ReturnsZeroWhenNoMemoryMetrics()
    {
        var resource = new NonMetricResource("NoMetrics", _registry.DataEngine);
        _registry.DataEngine.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();
        var total = snapshot.GetSubtreeMemory("Root/DataEngine");

        Assert.That(total, Is.EqualTo(0));
    }

    [Test]
    public void FindMostUtilized_ReturnsHighestUtilization()
    {
        var resource1 = new TestMetricResource("Low", _registry.Storage) { CapacityCurrent = 10, CapacityMax = 100 };
        var resource2 = new TestMetricResource("High", _registry.Storage) { CapacityCurrent = 90, CapacityMax = 100 };
        var resource3 = new TestMetricResource("Medium", _registry.Storage) { CapacityCurrent = 50, CapacityMax = 100 };

        _registry.Storage.RegisterChild(resource1);
        _registry.Storage.RegisterChild(resource2);
        _registry.Storage.RegisterChild(resource3);

        var snapshot = _graph.GetSnapshot();
        var mostUtilized = snapshot.FindMostUtilized();

        Assert.That(mostUtilized, Is.Not.Null);
        Assert.That(mostUtilized.Id, Is.EqualTo("High"));
        Assert.That(mostUtilized.Capacity.Value.Utilization, Is.EqualTo(0.9));
    }

    [Test]
    public void FindMostUtilized_WithThreshold_FiltersCorrectly()
    {
        var resource1 = new TestMetricResource("Low", _registry.Storage) { CapacityCurrent = 10, CapacityMax = 100 };
        var resource2 = new TestMetricResource("High", _registry.Storage) { CapacityCurrent = 90, CapacityMax = 100 };
        var resource3 = new TestMetricResource("Medium", _registry.Storage) { CapacityCurrent = 70, CapacityMax = 100 };

        _registry.Storage.RegisterChild(resource1);
        _registry.Storage.RegisterChild(resource2);
        _registry.Storage.RegisterChild(resource3);

        var snapshot = _graph.GetSnapshot();
        var highUtilized = snapshot.FindMostUtilized(0.65).ToList();

        Assert.That(highUtilized, Has.Count.EqualTo(2));
        Assert.That(highUtilized[0].Id, Is.EqualTo("High"));
        Assert.That(highUtilized[1].Id, Is.EqualTo("Medium"));
    }

    [Test]
    public void FindMostUtilized_ReturnsNull_WhenNoCapacityMetrics()
    {
        var resource = new NonMetricResource("NoCapacity", _registry.Storage);
        _registry.Storage.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();
        var mostUtilized = snapshot.FindMostUtilized();

        Assert.That(mostUtilized, Is.Null);
    }

    [Test]
    public void FindContentionHotspots_ReturnsSortedByTotalWaitUs()
    {
        var resource1 = new TestMetricResource("LowContention", _registry.Storage)
        {
            ContentionWaitCount = 5,
            ContentionTotalWaitUs = 100
        };
        var resource2 = new TestMetricResource("HighContention", _registry.Storage)
        {
            ContentionWaitCount = 10,
            ContentionTotalWaitUs = 1000
        };
        var resource3 = new TestMetricResource("MediumContention", _registry.Storage)
        {
            ContentionWaitCount = 7,
            ContentionTotalWaitUs = 500
        };

        _registry.Storage.RegisterChild(resource1);
        _registry.Storage.RegisterChild(resource2);
        _registry.Storage.RegisterChild(resource3);

        var snapshot = _graph.GetSnapshot();
        var hotspots = snapshot.FindContentionHotspots().ToList();

        Assert.That(hotspots, Has.Count.EqualTo(3));
        Assert.That(hotspots[0].Id, Is.EqualTo("HighContention"));
        Assert.That(hotspots[1].Id, Is.EqualTo("MediumContention"));
        Assert.That(hotspots[2].Id, Is.EqualTo("LowContention"));
    }

    [Test]
    public void FindContentionHotspots_WithThreshold_FiltersCorrectly()
    {
        var resource1 = new TestMetricResource("Low", _registry.Storage)
        {
            ContentionWaitCount = 1,
            ContentionTotalWaitUs = 50
        };
        var resource2 = new TestMetricResource("High", _registry.Storage)
        {
            ContentionWaitCount = 10,
            ContentionTotalWaitUs = 500
        };

        _registry.Storage.RegisterChild(resource1);
        _registry.Storage.RegisterChild(resource2);

        var snapshot = _graph.GetSnapshot();
        var hotspots = snapshot.FindContentionHotspots(100).ToList();

        Assert.That(hotspots, Has.Count.EqualTo(1));
        Assert.That(hotspots[0].Id, Is.EqualTo("High"));
    }

    [Test]
    public void FindContentionHotspots_ExcludesZeroWaitCount()
    {
        var resource = new TestMetricResource("NoWait", _registry.Storage)
        {
            ContentionWaitCount = 0,
            ContentionTotalWaitUs = 0
        };
        _registry.Storage.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();
        var hotspots = snapshot.FindContentionHotspots().ToList();

        Assert.That(hotspots.Any(h => h.Id == "NoWait"), Is.False);
    }

    #endregion

    #region IResourceGraph Tests

    [Test]
    public void Root_ReturnsRegistryRoot() => Assert.That(_graph.Root, Is.SameAs(_registry.Root));

    [Test]
    public void FindByPath_FindsExistingResource()
    {
        var resource = new TestMetricResource("TestNode", _registry.DataEngine);
        _registry.DataEngine.RegisterChild(resource);

        var found = _graph.FindByPath("DataEngine/TestNode");

        Assert.That(found, Is.SameAs(resource));
    }

    [Test]
    public void FindByPath_ReturnsNullForNonExistent()
    {
        var found = _graph.FindByPath("DataEngine/NonExistent");

        Assert.That(found, Is.Null);
    }

    [Test]
    public void FindByPath_EmptyReturnsRoot()
    {
        var found = _graph.FindByPath("");

        Assert.That(found, Is.SameAs(_registry.Root));
    }

    [Test]
    public void FindByType_ReturnsMatchingResources()
    {
        var serviceNode = new ResourceNode("TestService", ResourceType.Service, _registry.DataEngine);
        _registry.DataEngine.RegisterChild(serviceNode);

        var services = _graph.FindByType(ResourceType.Service).ToList();

        Assert.That(services, Has.Count.EqualTo(1));
        Assert.That(services[0], Is.SameAs(serviceNode));
    }

    [Test]
    public void ResetAllPeaks_CallsResetOnAllSources()
    {
        var resource1 = new TestMetricResource("R1", _registry.Storage)
        {
            AllocatedBytes = 100,
            PeakBytes = 500,
            ContentionMaxWaitUs = 1000
        };
        var resource2 = new TestMetricResource("R2", _registry.DataEngine)
        {
            AllocatedBytes = 200,
            PeakBytes = 800,
            ContentionMaxWaitUs = 2000
        };
        _registry.Storage.RegisterChild(resource1);
        _registry.DataEngine.RegisterChild(resource2);

        _graph.ResetAllPeaks();

        Assert.That(resource1.PeakBytes, Is.EqualTo(100), "Peak should reset to current");
        Assert.That(resource1.ContentionMaxWaitUs, Is.EqualTo(0), "Max wait should reset to 0");
        Assert.That(resource2.PeakBytes, Is.EqualTo(200), "Peak should reset to current");
        Assert.That(resource2.ContentionMaxWaitUs, Is.EqualTo(0), "Max wait should reset to 0");
    }

    #endregion

    #region NodeSnapshot Tests

    [Test]
    public void NodeSnapshot_HasCorrectPathAndId()
    {
        var resource = new TestMetricResource("MyResource", _registry.DataEngine);
        _registry.DataEngine.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();
        var node = snapshot.Nodes["Root/DataEngine/MyResource"];

        Assert.That(node.Path, Is.EqualTo("Root/DataEngine/MyResource"));
        Assert.That(node.Id, Is.EqualTo("MyResource"));
        Assert.That(node.Type, Is.EqualTo(ResourceType.Node));
    }

    [Test]
    public void ResourceSnapshot_GetNode_ReturnsNodeOrNull()
    {
        var resource = new TestMetricResource("TestNode", _registry.Storage);
        _registry.Storage.RegisterChild(resource);

        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot.GetNode("Root/Storage/TestNode"), Is.Not.Null);
        Assert.That(snapshot.GetNode("Root/Storage/NonExistent"), Is.Null);
    }

    [Test]
    public void ResourceSnapshot_FindByType_Works()
    {
        var memoryNode = new ResourceNode("MemPool", ResourceType.Memory, _registry.Allocation);
        _registry.Allocation.RegisterChild(memoryNode);

        var snapshot = _graph.GetSnapshot();
        var memoryNodes = snapshot.FindByType(ResourceType.Memory).ToList();

        Assert.That(memoryNodes, Has.Count.EqualTo(1));
        Assert.That(memoryNodes[0].Id, Is.EqualTo("MemPool"));
    }

    [Test]
    public void ResourceSnapshot_GetSubtree_ReturnsCorrectNodes()
    {
        var child1 = new TestMetricResource("Child1", _registry.Storage);
        var child2 = new TestMetricResource("Child2", _registry.Storage);
        var grandchild = new TestMetricResource("Grandchild", child1);
        _registry.Storage.RegisterChild(child1);
        _registry.Storage.RegisterChild(child2);
        child1.RegisterChild(grandchild);

        var snapshot = _graph.GetSnapshot();
        var subtree = snapshot.GetSubtree("Root/Storage").ToList();

        Assert.That(subtree.Select(n => n.Path), Contains.Item("Root/Storage"));
        Assert.That(subtree.Select(n => n.Path), Contains.Item("Root/Storage/Child1"));
        Assert.That(subtree.Select(n => n.Path), Contains.Item("Root/Storage/Child2"));
        Assert.That(subtree.Select(n => n.Path), Contains.Item("Root/Storage/Child1/Grandchild"));
    }

    #endregion

    #region Thread Safety Tests

    [Test]
    public void GetSnapshot_ThreadSafe_ConcurrentCalls()
    {
        var resource = new TestMetricResource("Concurrent", _registry.Storage)
        {
            CacheHits = 0
        };
        _registry.Storage.RegisterChild(resource);

        const int threadCount = 4;
        const int iterationsPerThread = 100;
        var exceptions = new Exception[threadCount];

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        resource.CacheHits++;
                        var snapshot = _graph.GetSnapshot();
                        Assert.That(snapshot, Is.Not.Null);
                        Assert.That(snapshot.Nodes, Is.Not.Empty);
                    }
                }
                catch (Exception ex)
                {
                    exceptions[threadIndex] = ex;
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        foreach (var ex in exceptions)
        {
            Assert.That(ex, Is.Null, $"Thread threw exception: {ex}");
        }
    }

    #endregion
}

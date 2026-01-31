using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Typhon.Engine.Tests.Observability.Bridge;

/// <summary>
/// Tests for the Observability Bridge components.
/// </summary>
[TestFixture]
public class ObservabilityBridgeTests
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
        public long DiskReadOps;
        public long DiskWriteOps;
        public long DiskReadBytes;
        public long DiskWriteBytes;
        public long CacheHits;
        public long CacheMisses;
        public long CheckpointLastUs;
        public long CheckpointAvgUs;
        public long CheckpointMaxUs;

        public TestMetricResource(string id, IResource parent)
            : base(id, ResourceType.Node, parent) { }

        public void ReadMetrics(IMetricWriter writer)
        {
            if (AllocatedBytes > 0 || PeakBytes > 0)
            {
                writer.WriteMemory(AllocatedBytes, PeakBytes);
            }

            if (CapacityMax > 0)
            {
                writer.WriteCapacity(CapacityCurrent, CapacityMax);
            }

            if (ContentionWaitCount > 0)
            {
                writer.WriteContention(ContentionWaitCount, ContentionTotalWaitUs, ContentionMaxWaitUs, 0);
            }

            if (DiskReadOps > 0 || DiskWriteOps > 0)
            {
                writer.WriteDiskIO(DiskReadOps, DiskWriteOps, DiskReadBytes, DiskWriteBytes);
            }

            if (CacheHits > 0 || CacheMisses > 0)
            {
                writer.WriteThroughput("CacheHits", CacheHits);
                writer.WriteThroughput("CacheMisses", CacheMisses);
            }

            if (CheckpointLastUs > 0)
            {
                writer.WriteDuration("Checkpoint", CheckpointLastUs, CheckpointAvgUs, CheckpointMaxUs);
            }
        }

        public void ResetPeaks()
        {
            PeakBytes = AllocatedBytes;
            ContentionMaxWaitUs = 0;
            CheckpointMaxUs = 0;
        }
    }

    #endregion

    #region OTelMetricNameBuilder Tests

    [Test]
    public void OTelMetricNameBuilder_Build_CreatesCorrectName()
    {
        var name = OTelMetricNameBuilder.Build("typhon.resource", "Storage/PageCache", "memory", "allocated_bytes");

        Assert.That(name, Is.EqualTo("typhon.resource.storage.page_cache.memory.allocated_bytes"));
    }

    [Test]
    public void OTelMetricNameBuilder_NormalizePath_RemovesRootPrefix()
    {
        var normalized = OTelMetricNameBuilder.NormalizePath("Root/Storage/PageCache");

        Assert.That(normalized, Is.EqualTo("storage.page_cache"));
    }

    [Test]
    public void OTelMetricNameBuilder_NormalizePath_HandlesPathWithoutRoot()
    {
        var normalized = OTelMetricNameBuilder.NormalizePath("Storage/PageCache");

        Assert.That(normalized, Is.EqualTo("storage.page_cache"));
    }

    [Test]
    public void OTelMetricNameBuilder_NormalizePath_ConvertsPascalCaseToSnakeCase()
    {
        var normalized = OTelMetricNameBuilder.NormalizePath("DataEngine/TransactionPool");

        Assert.That(normalized, Is.EqualTo("data_engine.transaction_pool"));
    }

    [Test]
    public void OTelMetricNameBuilder_NormalizePath_HandlesEmptyPath()
    {
        var normalized = OTelMetricNameBuilder.NormalizePath("");

        Assert.That(normalized, Is.EqualTo(string.Empty));
    }

    [Test]
    public void OTelMetricNameBuilder_NormalizePath_HandlesRootOnly()
    {
        var normalized = OTelMetricNameBuilder.NormalizePath("Root/");

        // After removing "Root/", we get empty string, which returns "root"
        Assert.That(normalized, Is.EqualTo("root"));
    }

    [Test]
    public void OTelMetricNameBuilder_ToSnakeCase_ConvertsCorrectly()
    {
        Assert.That(OTelMetricNameBuilder.ToSnakeCase("PageCache"), Is.EqualTo("page_cache"));
        Assert.That(OTelMetricNameBuilder.ToSnakeCase("WALRingBuffer"), Is.EqualTo("walring_buffer")); // All uppercase → lowercase
        Assert.That(OTelMetricNameBuilder.ToSnakeCase("simple"), Is.EqualTo("simple"));
        Assert.That(OTelMetricNameBuilder.ToSnakeCase(""), Is.EqualTo(string.Empty));
    }

    [Test]
    public void OTelMetricNameBuilder_BuildNamed_CreatesCorrectName()
    {
        var name = OTelMetricNameBuilder.BuildNamed(
            "typhon.resource",
            "Storage/PageCache",
            "throughput",
            "CacheHits",
            "count");

        Assert.That(name, Is.EqualTo("typhon.resource.storage.page_cache.throughput.cache_hits.count"));
    }

    #endregion

    #region ResourceMetricsExporter Tests

    [Test]
    public void ResourceMetricsExporter_Constructor_TakesInitialSnapshot()
    {
        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);

        Assert.That(exporter.CurrentSnapshot, Is.Not.Null);
        Assert.That(exporter.CurrentSnapshot.Timestamp, Is.LessThanOrEqualTo(DateTime.UtcNow));
    }

    [Test]
    public void ResourceMetricsExporter_UpdateSnapshot_ReturnsNewSnapshot()
    {
        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);

        var original = exporter.CurrentSnapshot;
        var updated = exporter.UpdateSnapshot();

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated.Timestamp, Is.GreaterThanOrEqualTo(original.Timestamp));
    }

    [Test]
    public void ResourceMetricsExporter_Meter_HasCorrectName()
    {
        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);

        Assert.That(exporter.Meter.Name, Is.EqualTo("Typhon.Resources"));
        Assert.That(exporter.Meter.Version, Is.EqualTo("1.0.0"));
    }

    [Test]
    public void ResourceMetricsExporter_ObservableCallbacks_ReturnCorrectValues()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            AllocatedBytes = 1024000,
            PeakBytes = 2048000,
            CapacityCurrent = 500,
            CapacityMax = 1000
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        exporter.UpdateSnapshot();

        // Verify the snapshot contains the expected values
        var snapshot = exporter.CurrentSnapshot;
        var node = snapshot.Nodes["Root/Storage/PageCache"];

        Assert.That(node.Memory.Value.AllocatedBytes, Is.EqualTo(1024000));
        Assert.That(node.Memory.Value.PeakBytes, Is.EqualTo(2048000));
        Assert.That(node.Capacity.Value.Current, Is.EqualTo(500));
        Assert.That(node.Capacity.Value.Maximum, Is.EqualTo(1000));
        Assert.That(node.Capacity.Value.Utilization, Is.EqualTo(0.5));
    }

    [Test]
    public void ResourceMetricsExporter_WithMeterListener_ReceivesMetrics()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            AllocatedBytes = 1024000,
            CapacityCurrent = 500,
            CapacityMax = 1000
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);

        var measuredInstruments = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Typhon.Resources")
            {
                measuredInstruments.Add(instrument.Name);
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        // Observable instruments are registered
        Assert.That(measuredInstruments, Contains.Item("typhon.resource.memory.allocated_bytes"));
        Assert.That(measuredInstruments, Contains.Item("typhon.resource.capacity.utilization"));
    }

    [Test]
    public void ResourceMetricsExporter_DisabledMetricKinds_NotExported()
    {
        var options = new ObservabilityBridgeOptions
        {
            ExportMemoryMetrics = false,
            ExportCapacityMetrics = true
        };
        using var exporter = new ResourceMetricsExporter(_graph, options);

        var measuredInstruments = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Typhon.Resources")
            {
                measuredInstruments.Add(instrument.Name);
            }
        };
        listener.Start();

        // Memory metrics should not be present
        Assert.That(measuredInstruments.Any(n => n.Contains("memory")), Is.False);
        // Capacity metrics should be present
        Assert.That(measuredInstruments.Any(n => n.Contains("capacity")), Is.True);
    }

    #endregion

    #region ResourceHealthChecker Tests

    [Test]
    public void ResourceHealthChecker_CheckHealth_ReturnsHealthyWhenBelowThreshold()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 50,
            CapacityMax = 100 // 50% utilization
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var status = checker.CheckHealth();

        Assert.That(status, Is.EqualTo(HealthStatus.Healthy));
    }

    [Test]
    public void ResourceHealthChecker_CheckHealth_ReturnsDegradedWhenAboveDegradedThreshold()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 85,
            CapacityMax = 100 // 85% utilization
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var status = checker.CheckHealth();

        Assert.That(status, Is.EqualTo(HealthStatus.Degraded));
    }

    [Test]
    public void ResourceHealthChecker_CheckHealth_ReturnsUnhealthyWhenAboveUnhealthyThreshold()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 96,
            CapacityMax = 100 // 96% utilization
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var status = checker.CheckHealth();

        Assert.That(status, Is.EqualTo(HealthStatus.Unhealthy));
    }

    [Test]
    public void ResourceHealthChecker_CheckHealth_UsesCustomThresholds()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 55,
            CapacityMax = 100 // 55% utilization
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        options.Thresholds["Storage/PageCache"] = new HealthThresholds(0.50, 0.70); // 50% degraded, 70% unhealthy

        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var status = checker.CheckHealth();

        Assert.That(status, Is.EqualTo(HealthStatus.Degraded));
    }

    [Test]
    public void ResourceHealthChecker_GetDetailedResult_IncludesMostUtilizedNode()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 90,
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var result = checker.GetDetailedResult();

        Assert.That(result.MostUtilizedNode, Is.Not.Null);
        Assert.That(result.MostUtilizedNode.Id, Is.EqualTo("PageCache"));
        Assert.That(result.Data.ContainsKey("most_utilized_path"), Is.True);
        Assert.That(result.Data.ContainsKey("most_utilized_percent"), Is.True);
    }

    [Test]
    public void ResourceHealthChecker_GetDetailedResult_CompositeHealth_WorstOfAll()
    {
        var healthy = new TestMetricResource("Healthy", _registry.Storage)
        {
            CapacityCurrent = 50,
            CapacityMax = 100 // 50%
        };
        var degraded = new TestMetricResource("Degraded", _registry.Storage)
        {
            CapacityCurrent = 85,
            CapacityMax = 100 // 85%
        };
        _registry.Storage.RegisterChild(healthy);
        _registry.Storage.RegisterChild(degraded);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var result = checker.GetDetailedResult();

        // Should be degraded (worst of healthy=50%, degraded=85%)
        Assert.That(result.Status, Is.EqualTo(HealthStatus.Degraded));
    }

    [Test]
    public void ResourceHealthChecker_GetDetailedResult_IncludesContentionHotspots()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            ContentionWaitCount = 100,
            ContentionTotalWaitUs = 5000
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);

        var result = checker.GetDetailedResult();

        Assert.That(result.Data.ContainsKey("contention_hotspots"), Is.True);
        var hotspots = (List<string>)result.Data["contention_hotspots"];
        Assert.That(hotspots, Contains.Item("Root/Storage/PageCache"));
    }

    #endregion

    #region ResourceAlertGenerator Tests

    [Test]
    public void ResourceAlertGenerator_GenerateAlert_ReturnsNullForHealthyResource()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 50,
            CapacityMax = 100 // 50% - healthy
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        var generator = new ResourceAlertGenerator(options);
        var snapshot = _graph.GetSnapshot();

        var alert = generator.GenerateAlert(snapshot, "Root/Storage/PageCache");

        Assert.That(alert, Is.Null);
    }

    [Test]
    public void ResourceAlertGenerator_GenerateAlert_ReturnsWarningForDegradedResource()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 85,
            CapacityMax = 100 // 85% - degraded
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        var generator = new ResourceAlertGenerator(options);
        var snapshot = _graph.GetSnapshot();

        var alert = generator.GenerateAlert(snapshot, "Root/Storage/PageCache");

        Assert.That(alert, Is.Not.Null);
        Assert.That(alert.Severity, Is.EqualTo(AlertSeverity.Warning));
        Assert.That(alert.Title, Contains.Substring("85"));
    }

    [Test]
    public void ResourceAlertGenerator_GenerateAlert_ReturnsCriticalForUnhealthyResource()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 96,
            CapacityMax = 100 // 96% - unhealthy
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions();
        var generator = new ResourceAlertGenerator(options);
        var snapshot = _graph.GetSnapshot();

        var alert = generator.GenerateAlert(snapshot, "Root/Storage/PageCache");

        Assert.That(alert, Is.Not.Null);
        Assert.That(alert.Severity, Is.EqualTo(AlertSeverity.Critical));
    }

    [Test]
    public void ResourceAlertGenerator_GenerateAlert_IncludesRootCauseAttribution()
    {
        // Set up a scenario where TransactionPool is symptomatic
        // but WALRingBuffer is the root cause
        var transactionPool = new TestMetricResource("TransactionPool", _registry.DataEngine)
        {
            CapacityCurrent = 95,
            CapacityMax = 100
        };
        _registry.DataEngine.RegisterChild(transactionPool);

        // The WALRingBuffer is a known dependency of TransactionPool
        var walBuffer = new TestMetricResource("WALRingBuffer", _registry.Durability)
        {
            CapacityCurrent = 98,
            CapacityMax = 100
        };
        _registry.Durability.RegisterChild(walBuffer);

        var options = new ObservabilityBridgeOptions();
        var generator = new ResourceAlertGenerator(options);
        var snapshot = _graph.GetSnapshot();

        var alert = generator.GenerateAlert(snapshot, "Root/DataEngine/TransactionPool");

        Assert.That(alert, Is.Not.Null);
        Assert.That(alert.SymptomPath, Is.EqualTo("Root/DataEngine/TransactionPool"));
        // Root cause should trace back to WALRingBuffer
        Assert.That(alert.RootCausePath, Is.EqualTo("Root/Durability/WALRingBuffer"));
        Assert.That(alert.RootCauseUtilization, Is.EqualTo(0.98).Within(0.01));
    }

    [Test]
    public void ResourceAlertGenerator_GenerateAlerts_ReturnsAllDegradedOrUnhealthyResources()
    {
        var healthy = new TestMetricResource("Healthy", _registry.Storage)
        {
            CapacityCurrent = 50,
            CapacityMax = 100
        };
        var degraded = new TestMetricResource("Degraded", _registry.Storage)
        {
            CapacityCurrent = 85,
            CapacityMax = 100
        };
        var unhealthy = new TestMetricResource("Unhealthy", _registry.Storage)
        {
            CapacityCurrent = 96,
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(healthy);
        _registry.Storage.RegisterChild(degraded);
        _registry.Storage.RegisterChild(unhealthy);

        var options = new ObservabilityBridgeOptions();
        var generator = new ResourceAlertGenerator(options);
        var snapshot = _graph.GetSnapshot();

        var alerts = generator.GenerateAlerts(snapshot).ToList();

        Assert.That(alerts.Count, Is.EqualTo(2)); // Only degraded and unhealthy
        Assert.That(alerts.Any(a => a.SymptomPath.Contains("Degraded")), Is.True);
        Assert.That(alerts.Any(a => a.SymptomPath.Contains("Unhealthy")), Is.True);
        Assert.That(alerts.Any(a => a.SymptomPath.Contains("Healthy")), Is.False);
    }

    [Test]
    public void ResourceAlertGenerator_GenerateAlert_IncludesCascadingEffects()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 85,
            CapacityMax = 100
        };
        var hotspot = new TestMetricResource("Hotspot", _registry.Storage)
        {
            ContentionWaitCount = 100,
            ContentionTotalWaitUs = 5000
        };
        _registry.Storage.RegisterChild(resource);
        _registry.Storage.RegisterChild(hotspot);

        var options = new ObservabilityBridgeOptions();
        var generator = new ResourceAlertGenerator(options);
        var snapshot = _graph.GetSnapshot();

        var alert = generator.GenerateAlert(snapshot, "Root/Storage/PageCache");

        Assert.That(alert, Is.Not.Null);
        Assert.That(alert.CascadingEffects, Is.Not.Null);
        // Hotspot should be listed as cascading effect (it has contention)
        Assert.That(alert.CascadingEffects, Contains.Item("Root/Storage/Hotspot"));
    }

    #endregion

    #region ResourceMetricsService Tests

    [Test]
    public void ResourceMetricsService_Start_SetsIsRunning()
    {
        var options = new ObservabilityBridgeOptions { SnapshotInterval = TimeSpan.FromMinutes(10) };
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);
        var generator = new ResourceAlertGenerator(options);
        using var service = new ResourceMetricsService(exporter, checker, generator, options);

        service.Start();

        Assert.That(service.IsRunning, Is.True);
    }

    [Test]
    public void ResourceMetricsService_Stop_ClearsIsRunning()
    {
        var options = new ObservabilityBridgeOptions { SnapshotInterval = TimeSpan.FromMinutes(10) };
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);
        var generator = new ResourceAlertGenerator(options);
        using var service = new ResourceMetricsService(exporter, checker, generator, options);

        service.Start();
        service.Stop();

        Assert.That(service.IsRunning, Is.False);
    }

    [Test]
    public void ResourceMetricsService_ForceUpdate_UpdatesSnapshot()
    {
        var options = new ObservabilityBridgeOptions { SnapshotInterval = TimeSpan.FromMinutes(10) };
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);
        var generator = new ResourceAlertGenerator(options);
        using var service = new ResourceMetricsService(exporter, checker, generator, options);

        var original = exporter.CurrentSnapshot.Timestamp;
        System.Threading.Thread.Sleep(10);
        service.ForceUpdate();
        var updated = exporter.CurrentSnapshot.Timestamp;

        Assert.That(updated, Is.GreaterThan(original));
    }

    [Test]
    public void ResourceMetricsService_HealthStatusChanged_RaisedOnTransition()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 50, // Start healthy
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions { SnapshotInterval = TimeSpan.FromMinutes(10) };
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);
        var generator = new ResourceAlertGenerator(options);
        using var service = new ResourceMetricsService(exporter, checker, generator, options);

        var statusChanges = new List<(HealthStatus From, HealthStatus To)>();
        service.HealthStatusChanged += (sender, e) =>
        {
            statusChanges.Add((e.PreviousStatus, e.NewStatus));
        };

        service.Start();
        Assert.That(service.CurrentStatus, Is.EqualTo(HealthStatus.Healthy));

        // Change to degraded
        resource.CapacityCurrent = 85;
        service.ForceUpdate();

        Assert.That(statusChanges.Count, Is.EqualTo(1));
        Assert.That(statusChanges[0].From, Is.EqualTo(HealthStatus.Healthy));
        Assert.That(statusChanges[0].To, Is.EqualTo(HealthStatus.Degraded));
    }

    [Test]
    public void ResourceMetricsService_AlertRaised_OnDegradation()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 50, // Start healthy
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions { SnapshotInterval = TimeSpan.FromMinutes(10) };
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);
        var generator = new ResourceAlertGenerator(options);
        using var service = new ResourceMetricsService(exporter, checker, generator, options);

        var alerts = new List<ResourceAlert>();
        service.AlertRaised += (sender, alert) =>
        {
            alerts.Add(alert);
        };

        service.Start();

        // Change to degraded
        resource.CapacityCurrent = 85;
        service.ForceUpdate();

        Assert.That(alerts.Count, Is.EqualTo(1));
        Assert.That(alerts[0].Severity, Is.EqualTo(AlertSeverity.Warning));
    }

    [Test]
    public void ResourceMetricsService_NoAlertOnRecovery()
    {
        var resource = new TestMetricResource("PageCache", _registry.Storage)
        {
            CapacityCurrent = 85, // Start degraded
            CapacityMax = 100
        };
        _registry.Storage.RegisterChild(resource);

        var options = new ObservabilityBridgeOptions { SnapshotInterval = TimeSpan.FromMinutes(10) };
        using var exporter = new ResourceMetricsExporter(_graph, options);
        var checker = new ResourceHealthChecker(exporter, options);
        var generator = new ResourceAlertGenerator(options);
        using var service = new ResourceMetricsService(exporter, checker, generator, options);

        var alerts = new List<ResourceAlert>();
        service.AlertRaised += (sender, alert) =>
        {
            alerts.Add(alert);
        };

        service.Start();
        var initialAlertCount = alerts.Count;

        // Recover to healthy
        resource.CapacityCurrent = 50;
        service.ForceUpdate();

        // No new alerts on recovery
        Assert.That(alerts.Count, Is.EqualTo(initialAlertCount));
    }

    #endregion

    #region HealthThresholds Tests

    [Test]
    public void HealthThresholds_Default_Has80And95()
    {
        var defaults = HealthThresholds.Default;

        Assert.That(defaults.DegradedThreshold, Is.EqualTo(0.80));
        Assert.That(defaults.UnhealthyThreshold, Is.EqualTo(0.95));
    }

    [Test]
    public void HealthThresholds_Critical_Has60And80()
    {
        var critical = HealthThresholds.Critical;

        Assert.That(critical.DegradedThreshold, Is.EqualTo(0.60));
        Assert.That(critical.UnhealthyThreshold, Is.EqualTo(0.80));
    }

    #endregion

    #region ObservabilityBridgeOptions Tests

    [Test]
    public void ObservabilityBridgeOptions_DefaultValues()
    {
        var options = new ObservabilityBridgeOptions();

        Assert.That(options.SnapshotInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(options.MetricNamePrefix, Is.EqualTo("typhon.resource"));
        Assert.That(options.ExportMemoryMetrics, Is.True);
        Assert.That(options.ExportCapacityMetrics, Is.True);
        Assert.That(options.ExportDiskIOMetrics, Is.True);
        Assert.That(options.ExportContentionMetrics, Is.True);
        Assert.That(options.ExportThroughputMetrics, Is.True);
        Assert.That(options.ExportDurationMetrics, Is.True);
        Assert.That(options.Thresholds, Is.Empty);
    }

    [Test]
    public void ObservabilityBridgeOptions_SectionName_IsCorrect()
    {
        Assert.That(ObservabilityBridgeOptions.SectionName, Is.EqualTo("Typhon:ObservabilityBridge"));
    }

    #endregion

    #region HealthCheckResult Tests

    [Test]
    public void HealthCheckResult_Healthy_ReturnsCorrectDefaults()
    {
        var result = HealthCheckResult.Healthy();

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
        Assert.That(result.Description, Is.EqualTo("All resources healthy"));
        Assert.That(result.MostUtilizedNode, Is.Null);
        Assert.That(result.Data, Is.Empty);
    }

    #endregion

    #region HealthStatusChangedEventArgs Tests

    [Test]
    public void HealthStatusChangedEventArgs_IsDegradation_TrueWhenWorse()
    {
        var args = new HealthStatusChangedEventArgs(HealthStatus.Healthy, HealthStatus.Degraded);

        Assert.That(args.IsDegradation, Is.True);
        Assert.That(args.IsRecovery, Is.False);
    }

    [Test]
    public void HealthStatusChangedEventArgs_IsRecovery_TrueWhenBetter()
    {
        var args = new HealthStatusChangedEventArgs(HealthStatus.Degraded, HealthStatus.Healthy);

        Assert.That(args.IsRecovery, Is.True);
        Assert.That(args.IsDegradation, Is.False);
    }

    #endregion
}

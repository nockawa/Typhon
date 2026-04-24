using System.IO;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Fixtures;

/// <summary>
/// Smoke tests for {@link TraceFixtureBuilder}. If these fail, every downstream integration test
/// relying on the builder is also broken — so the signal is immediate and specific.
/// </summary>
[TestFixture]
public sealed class TraceFixtureBuilderTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-fixture-builder", System.Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void BuildMinimalTrace_ProducesValidHeader()
    {
        var path = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 2, instantsPerTick: 3);
        Assert.That(File.Exists(path), "fixture file should exist on disk");

        using var fs = File.OpenRead(path);
        using var reader = new TraceFileReader(fs);
        reader.ReadHeader();
        Assert.That(reader.Header.Magic, Is.EqualTo(TraceFileHeader.MagicValue));
        Assert.That(reader.Header.Version, Is.EqualTo(TraceFileHeader.CurrentVersion));
    }

    [Test]
    public void BuildMinimalTrace_CanBeCacheBuilt()
    {
        // The end-to-end smoke test: fixture → TraceFileCacheBuilder → readable cache. If this passes,
        // every TraceSessionRuntime integration test has a viable input.
        var path = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 3, instantsPerTick: 2);
        var cachePath = Typhon.Workbench.Sessions.Profiler.TraceFileCacheBuilder.GetCachePathFor(path);
        var result = Typhon.Workbench.Sessions.Profiler.TraceFileCacheBuilder.Build(path, cachePath);

        Assert.That(result.TickCount, Is.EqualTo(3));
        // Each tick emits: TickStart + 2 Instant + TickEnd = 4 records.
        Assert.That(result.EventCount, Is.EqualTo(3 * 4));
        Assert.That(File.Exists(cachePath));
    }

    [Test]
    public void BuildBadMagic_FailsTraceFileReader()
    {
        var path = TraceFixtureBuilder.BuildBadMagic(_tempDir);
        Assert.That(File.Exists(path));
        using var fs = File.OpenRead(path);
        using var reader = new TraceFileReader(fs);
        Assert.Throws<System.IO.InvalidDataException>(() => reader.ReadHeader(),
            "ReadHeader must reject non-TYTR magic");
    }
}

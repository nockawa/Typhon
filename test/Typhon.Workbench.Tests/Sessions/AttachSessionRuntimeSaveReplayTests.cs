using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Round-trip coverage for the live save-as-replay flow. Drives an <see cref="AttachSessionRuntime"/> through synthetic
/// Init + Block frames via <see cref="MockTcpProfilerServer"/>, calls <see cref="AttachSessionRuntime.SaveSessionAsync"/>,
/// reopens the resulting file via <see cref="TraceSessionRuntime"/>, and asserts the saved file is byte-format-valid and
/// reads back with consistent tick / chunk / metadata counts.
/// </summary>
[TestFixture]
public sealed class AttachSessionRuntimeSaveReplayTests
{
    [Test]
    public async Task SaveSessionAsync_ProducesSelfContainedReplay_WhichTraceSessionRuntimeOpens()
    {
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(25),
            MaxBlocks = 5,
        };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);

        // Wait for at least 3 finalized ticks so the save flow has real chunk data to relocate.
        var tickDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (runtime.TickCount < 3 && DateTime.UtcNow < tickDeadline)
        {
            await Task.Delay(25, cts.Token);
        }
        Assert.That(runtime.TickCount, Is.GreaterThanOrEqualTo(3),
            "Need at least 3 finalized ticks before saving — fewer leaves the manifest empty.");

        var savePath = Path.Combine(Path.GetTempPath(), $"save-replay-{Guid.NewGuid():N}.typhon-replay");
        try
        {
            // ─── Save ───
            var bytesWritten = await runtime.SaveSessionAsync(savePath, cts.Token);
            Assert.That(File.Exists(savePath), Is.True, "save target must exist after SaveSessionAsync returns");
            Assert.That(bytesWritten, Is.GreaterThan(0), "save file should be non-empty");
            Assert.That(new FileInfo(savePath).Length, Is.EqualTo(bytesWritten),
                "reported bytesWritten should match the on-disk size");

            // ─── Verify magic + IsSelfContained flag at the byte level ───
            var first8 = new byte[8];
            using (var fs = File.OpenRead(savePath))
            {
                fs.ReadExactly(first8);
            }
            var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(first8);
            Assert.That(magic, Is.EqualTo(CacheHeader.MagicValue), "header magic must be TPCH");
            var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(first8.AsSpan(4, 2));
            Assert.That(version, Is.EqualTo(CacheHeader.CurrentVersion));
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(first8.AsSpan(6, 2));
            Assert.That(flags & CacheHeaderFlags.IsSelfContained, Is.EqualTo(CacheHeaderFlags.IsSelfContained),
                "IsSelfContained flag must be set on the saved replay header");

            // ─── Reopen via TraceSessionRuntime as a .typhon-replay ───
            using var trace = TraceSessionRuntime.Start(savePath, NullLogger.Instance);
            Assert.That(trace.IsReplayFile, Is.True, "TraceSessionRuntime should detect the replay extension");

            var traceMetadata = await trace.MetadataReady.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(traceMetadata, Is.Not.Null);

            // The reopened metadata's tick & chunk counts must match what the live runtime had when we saved.
            // Note: live tick processing may have added more ticks BETWEEN the save and the assertion below; the
            // saved file is a snapshot, so we compare against AT-LEAST counts at save time, not equal counts now.
            Assert.That(traceMetadata.TickSummaries.Count, Is.GreaterThanOrEqualTo(3),
                "saved replay must contain at least the ticks finalized before SaveSessionAsync was called");
            Assert.That(traceMetadata.ChunkManifest.Count, Is.GreaterThan(0),
                "saved replay must contain at least one chunk");
            Assert.That(traceMetadata.Header.TimestampFrequency, Is.EqualTo(10_000_000),
                "Header projection from embedded SourceMetadata must recover the source timestamp frequency");
            Assert.That(traceMetadata.Header.WorkerCount, Is.EqualTo(1),
                "Header projection from embedded SourceMetadata must recover worker count");
        }
        finally
        {
            if (File.Exists(savePath)) File.Delete(savePath);
        }
    }

    [Test]
    public async Task SaveSessionAsync_RejectsNonExistentParentDirectory()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 1, BlockInterval = TimeSpan.FromMilliseconds(25) };
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtime = await AttachSessionRuntime.StartAsync(
            $"127.0.0.1:{server.Port}", NullLogger.Instance, cts.Token);
        await runtime.MetadataReady.WaitAsync(TimeSpan.FromSeconds(5));

        var nonExistent = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}", "out.typhon-replay");
        Assert.ThrowsAsync<DirectoryNotFoundException>(
            async () => await runtime.SaveSessionAsync(nonExistent, cts.Token),
            "save must reject a path whose parent directory does not exist");
    }
}

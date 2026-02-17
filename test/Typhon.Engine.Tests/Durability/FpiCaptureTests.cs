using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for FPI (Full-Page Image) capture in the page latch path.
/// Verifies that <see cref="PagedMMF.TryLatchPageExclusive"/> writes FPI records
/// to the WAL commit buffer on first dirty per checkpoint cycle.
/// </summary>
[TestFixture]
public class FpiCaptureTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;
    private ManagedPagedMMF _mmf;
    private EpochManager _epochManager;
    private WalManager _walManager;

    private static string CurrentDatabaseName => $"T_FpiCapture_{TestContext.CurrentContext.Test.Name}_db";

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_fpi_test_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        _walManager?.Dispose();
        _walManager = null;
        _mmf?.Dispose();
        _mmf = null;
        _fileIO?.Dispose();
        _fileIO = null;
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    /// <summary>
    /// Creates a ManagedPagedMMF with epoch manager.
    /// </summary>
    private void CreateTestInfrastructure()
    {
        _epochManager = new EpochManager("TestEpochManager", AllocationResource);

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        var options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = Path.GetTempPath(),
            DatabaseName = CurrentDatabaseName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
        };
        options.EnsureFileDeleted();

        _mmf = new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, options, AllocationResource, "TestMMF", logger);
    }

    /// <summary>
    /// Creates and initializes a WalManager with InMemoryWalFileIO.
    /// </summary>
    private WalManager CreateWalManager(int commitBufferCapacity = 64 * 1024)
    {
        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 2,
            SegmentSize = 1024 * 1024,
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };

        var mgr = new WalManager(options, MemoryAllocator, _fileIO, AllocationResource, commitBufferCapacity);
        mgr.Initialize();
        mgr.Start();

        SpinWait.SpinUntil(() => mgr.IsRunning, 2000);

        return mgr;
    }

    /// <summary>
    /// Latches a page exclusively and returns the memPageIndex. Leaves the page latched.
    /// </summary>
    private int LatchPage(int filePageIndex, long epoch)
    {
        _mmf.RequestPageEpoch(filePageIndex, epoch, out var memPageIndex);
        var latched = _mmf.TryLatchPageExclusive(memPageIndex);
        Assert.That(latched, Is.True, $"Failed to latch page {filePageIndex}");
        return memPageIndex;
    }

    // ═══════════════════════════════════════════════════════════════
    // Basic FPI Capture Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void FirstDirty_WritesFpiRecord()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager);

        var lsnBefore = _walManager.CommitBuffer.NextLsn;

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        // FPI record should have consumed an LSN
        var lsnAfter = _walManager.CommitBuffer.NextLsn;
        Assert.That(lsnAfter, Is.GreaterThan(lsnBefore), "FPI record should have consumed at least one LSN");
    }

    [Test]
    [CancelAfter(5000)]
    public void SecondDirty_SameCycle_NoAdditionalFpi()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager);

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            // First latch — should write FPI
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        var lsnAfterFirst = _walManager.CommitBuffer.NextLsn;

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            // Second latch of same page — should NOT write another FPI
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        var lsnAfterSecond = _walManager.CommitBuffer.NextLsn;
        Assert.That(lsnAfterSecond, Is.EqualTo(lsnAfterFirst), "No additional FPI should be written for same page in same checkpoint cycle");
    }

    [Test]
    [CancelAfter(5000)]
    public void FpiAfterBitmapReset_WritesNewFpi()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager);

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        var lsnAfterFirst = _walManager.CommitBuffer.NextLsn;

        // Simulate checkpoint start — reset bitmap
        _mmf.FpiBitmap.ClearAll();

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            // Same page, but bitmap was cleared — should write new FPI
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        var lsnAfterSecond = _walManager.CommitBuffer.NextLsn;
        Assert.That(lsnAfterSecond, Is.GreaterThan(lsnAfterFirst), "FPI should be written again after bitmap reset");
    }

    // ═══════════════════════════════════════════════════════════════
    // FPI Record Format Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void FpiRecord_CorrectHeaderFields()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager);

        var lsnBefore = _walManager.CommitBuffer.NextLsn;

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        // Drain the commit buffer to inspect the FPI record
        Assert.That(_walManager.CommitBuffer.TryDrain(out var data, out var frameCount), Is.True, "Should have data to drain");
        Assert.That(frameCount, Is.GreaterThanOrEqualTo(1));

        // Walk frames and find the FPI record
        bool foundFpi = false;
        WalCommitBuffer.WalkFrames(data, (payload, recordCount) =>
        {
            if (recordCount == 0)
            {
                return;
            }

            // Read the WalRecordHeader from the payload
            var header = MemoryMarshal.Read<WalRecordHeader>(payload);

            if ((header.Flags & (byte)WalRecordFlags.FullPageImage) != 0)
            {
                foundFpi = true;

                // Verify header fields
                Assert.That(header.LSN, Is.EqualTo(lsnBefore), "FPI should have the expected LSN");
                Assert.That(header.UowEpoch, Is.EqualTo(0), "FPI records are not UoW-scoped");
                Assert.That(header.TransactionTSN, Is.EqualTo(0), "FPI is page-level, not transaction-level");
                Assert.That(header.ComponentTypeId, Is.EqualTo(0));
                Assert.That(header.EntityId, Is.EqualTo(0));

                // PayloadLength = FpiMetadata (16) + PageSize (8192) = 8208
                Assert.That(header.PayloadLength, Is.EqualTo(FpiMetadata.SizeInBytes + PagedMMF.PageSize));

                // TotalRecordLength = WalRecordHeader (48) + PayloadLength (8208) = 8256
                Assert.That(header.TotalRecordLength, Is.EqualTo((uint)(WalRecordHeader.SizeInBytes + header.PayloadLength)));

                // Verify CRC is non-zero (was computed)
                Assert.That(header.CRC, Is.Not.EqualTo(0), "CRC should be computed");
            }
        });

        Assert.That(foundFpi, Is.True, "FPI record should be found in the WAL commit buffer");
    }

    [Test]
    [CancelAfter(5000)]
    public void FpiRecord_CorrectMetadata()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager);

        const int targetFilePageIndex = 2;

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var memPageIdx = LatchPage(targetFilePageIndex, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        Assert.That(_walManager.CommitBuffer.TryDrain(out var data, out _), Is.True);

        bool foundFpi = false;
        WalCommitBuffer.WalkFrames(data, (payload, recordCount) =>
        {
            if (recordCount == 0)
            {
                return;
            }

            var header = MemoryMarshal.Read<WalRecordHeader>(payload);
            if ((header.Flags & (byte)WalRecordFlags.FullPageImage) == 0)
            {
                return;
            }

            foundFpi = true;

            // Read FpiMetadata from after the WalRecordHeader
            var meta = MemoryMarshal.Read<FpiMetadata>(payload[WalRecordHeader.SizeInBytes..]);

            Assert.That(meta.FilePageIndex, Is.EqualTo(targetFilePageIndex));
            Assert.That(meta.SegmentId, Is.EqualTo(0), "SegmentId should be 0 (no multi-segment yet)");
            Assert.That(meta.UncompressedSize, Is.EqualTo(PagedMMF.PageSize));
            Assert.That(meta.CompressionAlgo, Is.EqualTo(0), "No compression in Phase 3");
        });

        Assert.That(foundFpi, Is.True);
    }

    // ═══════════════════════════════════════════════════════════════
    // No-WAL Safety
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void NoWal_NoFpi_NoCrash()
    {
        CreateTestInfrastructure();
        // Do NOT call EnableFpiCapture — WAL is disabled

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var memPageIdx = LatchPage(1, guard.Epoch);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        // If we got here without an exception, the test passes
        Assert.Pass("Latch without FPI enabled does not crash");
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent FPI Capture
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_DifferentPages_OneFpiPerPage()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager);

        const int threadCount = 4;
        var lsnBefore = _walManager.CommitBuffer.NextLsn;

        using var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            // Each thread targets a different page (1, 2, 3, 4) — page 0 is the root file header
            var filePageIndex = i + 1;
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                using var guard = EpochGuard.Enter(_epochManager);
                var memPageIdx = LatchPage(filePageIndex, guard.Epoch);
                _mmf.UnlatchPageExclusive(memPageIdx);
            });
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        // Each page should have consumed exactly one FPI LSN
        var lsnAfter = _walManager.CommitBuffer.NextLsn;
        Assert.That(lsnAfter - lsnBefore, Is.EqualTo(threadCount), $"Expected {threadCount} FPI records (one per page)");
    }

    // ═══════════════════════════════════════════════════════════════
    // FpiMetadata Struct Size Validation
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void FpiMetadata_SizeIs16Bytes()
    {
        unsafe
        {
            Assert.That(sizeof(FpiMetadata), Is.EqualTo(FpiMetadata.SizeInBytes));
            Assert.That(sizeof(FpiMetadata), Is.EqualTo(16));
        }
    }
}

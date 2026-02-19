using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="FpiCompression"/> helper class, compressed FPI write path, and on-the-fly repair path.
/// </summary>
[TestFixture]
public class FpiCompressionTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;
    private ManagedPagedMMF _mmf;
    private EpochManager _epochManager;
    private WalManager _walManager;

    private static string CurrentDatabaseName => $"T_FC_{TestContext.CurrentContext.Test.Name}_db";

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_fpi_compress_test_{Guid.NewGuid():N}");
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

        // Do NOT call mgr.Start() — the WalWriter background thread is the single consumer
        // of the MPSC CommitBuffer and would drain FPI records before the test can inspect them.
        // None of the tests in this class need the writer thread running.

        return mgr;
    }

    private int LatchPage(int filePageIndex, long epoch)
    {
        _mmf.RequestPageEpoch(filePageIndex, epoch, out var memPageIndex);
        var latched = _mmf.TryLatchPageExclusive(memPageIndex);
        Assert.That(latched, Is.True, $"Failed to latch page {filePageIndex}");
        return memPageIndex;
    }

    // ═══════════════════════════════════════════════════════════════
    // FpiCompression Helper Unit Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Compress_ZeroPage_HighRatio()
    {
        var page = new byte[PagedMMF.PageSize]; // All zeros — highly compressible
        var target = new byte[FpiCompression.MaxCompressedSize(page.Length)];

        var compressedSize = FpiCompression.Compress(page, target);

        Assert.That(compressedSize, Is.GreaterThan(0), "Zeroed page should compress successfully");
        Assert.That(compressedSize, Is.LessThan(page.Length / 2), "Zeroed page should achieve significant compression");
    }

    [Test]
    public void Compress_RandomData_Incompressible()
    {
        var rng = new Random(42);
        var page = new byte[PagedMMF.PageSize];
        rng.NextBytes(page);

        var target = new byte[FpiCompression.MaxCompressedSize(page.Length)];

        var compressedSize = FpiCompression.Compress(page, target);

        Assert.That(compressedSize, Is.EqualTo(-1), "Random data should be incompressible");
    }

    [Test]
    public void RoundTrip_ProducesIdenticalData()
    {
        // Create a page with a repeating pattern — compressible but non-trivial
        var original = new byte[PagedMMF.PageSize];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i % 37);
        }

        var compressed = new byte[FpiCompression.MaxCompressedSize(original.Length)];
        var compressedSize = FpiCompression.Compress(original, compressed);
        Assert.That(compressedSize, Is.GreaterThan(0), "Pattern data should compress");

        var decompressed = new byte[original.Length];
        var decompressedSize = FpiCompression.Decompress(compressed.AsSpan(0, compressedSize), decompressed);

        Assert.That(decompressedSize, Is.EqualTo(original.Length));
        Assert.That(decompressed, Is.EqualTo(original), "Round-trip should produce identical data");
    }

    [Test]
    public void Decompress_InvalidData_ReturnsNegative()
    {
        var garbage = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0x01, 0x02 };
        var target = new byte[PagedMMF.PageSize];

        var result = FpiCompression.Decompress(garbage, target);

        Assert.That(result, Is.EqualTo(-1), "Garbage input should return -1");
    }

    // ═══════════════════════════════════════════════════════════════
    // Compressed FPI Write Path Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void FpiCapture_CompressionEnabled_WritesCompressedRecord()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager, enableFpiCompression: true);

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            // Page 1 starts as all-zeros (highly compressible)
            var memPageIdx = LatchPage(1, guard.Epoch);
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

            // Verify Compressed flag is set
            Assert.That(header.Flags & (byte)WalRecordFlags.Compressed, Is.Not.EqualTo(0), "Compressed flag should be set");

            // Verify payload is smaller than uncompressed (48 header + 16 meta + 8192 page = 8256)
            Assert.That(header.TotalRecordLength, Is.LessThan((uint)(WalRecordHeader.SizeInBytes + FpiMetadata.SizeInBytes + PagedMMF.PageSize)),
                "Compressed record should be smaller than uncompressed");

            // Verify metadata
            var meta = MemoryMarshal.Read<FpiMetadata>(payload[WalRecordHeader.SizeInBytes..]);
            Assert.That(meta.CompressionAlgo, Is.EqualTo(FpiCompression.AlgoLZ4));
            Assert.That(meta.UncompressedSize, Is.EqualTo(PagedMMF.PageSize));
        });

        Assert.That(foundFpi, Is.True, "FPI record should be found");
    }

    [Test]
    [CancelAfter(5000)]
    public void FpiCapture_CompressionDisabled_WritesUncompressedRecord()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager, enableFpiCompression: false);

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var memPageIdx = LatchPage(1, guard.Epoch);
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

            // Verify Compressed flag is NOT set
            Assert.That(header.Flags & (byte)WalRecordFlags.Compressed, Is.EqualTo(0), "Compressed flag should NOT be set");

            // Verify standard uncompressed size
            Assert.That(header.PayloadLength, Is.EqualTo(FpiMetadata.SizeInBytes + PagedMMF.PageSize));

            // Verify metadata shows no compression
            var meta = MemoryMarshal.Read<FpiMetadata>(payload[WalRecordHeader.SizeInBytes..]);
            Assert.That(meta.CompressionAlgo, Is.EqualTo(FpiCompression.AlgoNone));
        });

        Assert.That(foundFpi, Is.True, "FPI record should be found");
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void FpiCapture_IncompressiblePage_FallsBackToUncompressed()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        _mmf.EnableFpiCapture(_walManager, enableFpiCompression: true);

        // Fill the page with random data (incompressible) BEFORE latching
        var rng = new Random(42);
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            _mmf.RequestPageEpoch(1, guard.Epoch, out var memPageIndex);

            // Write random data into page memory directly (before FPI is captured)
            var pageAddr = _mmf.GetMemPageAddress(memPageIndex);
            var pageSpan = new Span<byte>(pageAddr, PagedMMF.PageSize);
            var randomBytes = new byte[PagedMMF.PageSize];
            rng.NextBytes(randomBytes);
            randomBytes.AsSpan().CopyTo(pageSpan);

            // Now latch — triggers FPI capture with random data
            var latched = _mmf.TryLatchPageExclusive(memPageIndex);
            Assert.That(latched, Is.True);
            _mmf.UnlatchPageExclusive(memPageIndex);
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

            // Random data is incompressible — should fall back to uncompressed
            Assert.That(header.Flags & (byte)WalRecordFlags.Compressed, Is.EqualTo(0),
                "Incompressible page should fall back to uncompressed");
            Assert.That(header.PayloadLength, Is.EqualTo(FpiMetadata.SizeInBytes + PagedMMF.PageSize));

            var meta = MemoryMarshal.Read<FpiMetadata>(payload[WalRecordHeader.SizeInBytes..]);
            Assert.That(meta.CompressionAlgo, Is.EqualTo(FpiCompression.AlgoNone));
        });

        Assert.That(foundFpi, Is.True, "FPI record should be found");
    }

    // ═══════════════════════════════════════════════════════════════
    // SearchFpiForPage with Compressed FPI
    // ═══════════════════════════════════════════════════════════════

    private const uint TestSegmentSize = 64 * 1024;

    internal static byte[] BuildPageWithCrc(int filePageIndex, byte fillByte = 0xAA)
    {
        var page = new byte[PagedMMF.PageSize];

        ref var header = ref Unsafe.As<byte, PageBaseHeader>(ref page[0]);
        header.Flags = PageBlockFlags.None;
        header.Type = PageBlockType.None;
        header.FormatRevision = 1;
        header.ChangeRevision = 1;
        header.ModificationCounter = 0;
        header.PageChecksum = 0;

        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            page[i] = fillByte;
        }

        var crc = WalCrc.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Unsafe.As<byte, uint>(ref page[PageBaseHeader.PageChecksumOffset]) = crc;

        return page;
    }

    /// <summary>
    /// Builds a WAL segment containing a compressed FPI record.
    /// </summary>
    internal static unsafe byte[] BuildSegmentWithCompressedFpi(long segmentId, long firstLSN, int filePageIndex, byte[] pageData,
        uint segmentSize = TestSegmentSize)
    {
        // Compress the page data
        var compressedBuffer = new byte[FpiCompression.MaxCompressedSize(pageData.Length)];
        var compressedSize = FpiCompression.Compress(pageData, compressedBuffer);
        Assert.That(compressedSize, Is.GreaterThan(0), "Page data must be compressible for this test");

        var data = new byte[segmentSize];

        // Write segment header
        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, segmentSize);
            header.ComputeAndSetCrc();
        }

        var payloadLen = FpiMetadata.SizeInBytes + compressedSize;
        var frameLength = WalFrameHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payloadLen;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = 1;

        // Write FPI record header
        var recordOffset = offset + WalFrameHeader.SizeInBytes;
        ref var recHeader = ref Unsafe.As<byte, WalRecordHeader>(ref data[recordOffset]);
        recHeader.LSN = firstLSN;
        recHeader.TransactionTSN = 0;
        recHeader.TotalRecordLength = (uint)(WalRecordHeader.SizeInBytes + payloadLen);
        recHeader.UowEpoch = 0;
        recHeader.ComponentTypeId = 0;
        recHeader.EntityId = 0;
        recHeader.PayloadLength = (ushort)payloadLen;
        recHeader.OperationType = 0;
        recHeader.Flags = (byte)(WalRecordFlags.FullPageImage | WalRecordFlags.Compressed);
        recHeader.PrevCRC = 0;
        recHeader.CRC = 0;

        // Write FpiMetadata
        var metaOffset = recordOffset + WalRecordHeader.SizeInBytes;
        ref var meta = ref Unsafe.As<byte, FpiMetadata>(ref data[metaOffset]);
        meta.FilePageIndex = filePageIndex;
        meta.SegmentId = 0;
        meta.ChangeRevision = 1;
        meta.UncompressedSize = (ushort)PagedMMF.PageSize;
        meta.CompressionAlgo = FpiCompression.AlgoLZ4;
        meta.Reserved = 0;

        // Write compressed page data
        compressedBuffer.AsSpan(0, compressedSize).CopyTo(data.AsSpan(metaOffset + FpiMetadata.SizeInBytes));

        // Compute CRC
        var recordSpan = data.AsSpan(recordOffset, WalRecordHeader.SizeInBytes + payloadLen);
        var crcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
        var computedCrc = WalCrc.ComputeSkipping(recordSpan, crcFieldOffset, sizeof(uint));
        Unsafe.As<byte, uint>(ref data[recordOffset + crcFieldOffset]) = computedCrc;

        return data;
    }

    private void CreateWalSegmentFile(long segmentId, byte[] segmentData)
    {
        Directory.CreateDirectory(_walDir);
        var fileName = $"{segmentId:D16}.wal";
        var filePath = Path.Combine(_walDir, fileName);

        File.WriteAllBytes(filePath, segmentData);

        using var handle = _fileIO.OpenSegment(filePath, withFUA: false);
        _fileIO.WriteAligned(handle, 0, segmentData);
    }

    [Test]
    [CancelAfter(5000)]
    public void SearchFpiForPage_CompressedFpi_Decompresses()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        const int targetPage = 3;

        // Build a compressible page with recognizable pattern
        var goodPageData = BuildPageWithCrc(targetPage, 0xBB);

        // Build WAL segment with compressed FPI
        var segmentData = BuildSegmentWithCompressedFpi(1, 1, targetPage, goodPageData);
        CreateWalSegmentFile(1, segmentData);

        // Search for the FPI — should decompress and return the page data
        var foundData = _walManager.SearchFpiForPage(targetPage);

        Assert.That(foundData, Is.Not.Null, "SearchFpiForPage should find the compressed FPI");
        Assert.That(foundData.Length, Is.EqualTo(PagedMMF.PageSize), "Decompressed data should be full page size");
        Assert.That(foundData[PagedMMF.PageHeaderSize], Is.EqualTo(0xBB), "Data should match original page content");
    }
}

/// <summary>
/// Recovery integration tests for compressed FPI records. Uses <see cref="TestBase{T}"/> to get a full
/// <see cref="DatabaseEngine"/> with <see cref="UowRegistry"/> for the recovery path.
/// </summary>
[TestFixture]
class FpiCompressionRecoveryTests : TestBase<FpiCompressionRecoveryTests>
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_fpi_compress_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_walDir);
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    private void CreateWalSegmentFile(long segmentId, byte[] segmentData)
    {
        var fileName = $"{segmentId:D16}.wal";
        var filePath = Path.Combine(_walDir, fileName);

        File.WriteAllBytes(filePath, segmentData);

        using var handle = _fileIO.OpenSegment(filePath, withFUA: false);
        _fileIO.WriteAligned(handle, 0, segmentData);
    }

    [Test]
    [CancelAfter(5000)]
    public void Recovery_CompressedFpi_DecompressesAndRepairs()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        registry.LoadFromDiskRaw();

        const int targetPage = 5;

        // Build a valid page with recognizable pattern + CRC
        var goodPageData = FpiCompressionTests.BuildPageWithCrc(targetPage, 0xAA);

        // Write the good page to disk first
        mmf.WritePageDirect(targetPage, goodPageData);

        // Build WAL segment with compressed FPI of the good page
        var segmentData = FpiCompressionTests.BuildSegmentWithCompressedFpi(1, 1, targetPage, goodPageData);
        CreateWalSegmentFile(1, segmentData);

        // Corrupt the page on disk (change data but keep original CRC → mismatch)
        var corruptedPage = (byte[])goodPageData.Clone();
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            corruptedPage[i] = 0xFF;
        }
        mmf.WritePageDirect(targetPage, corruptedPage);

        // Run recovery — Phase 4 should decompress FPI and repair the torn page
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.FpiRecordsApplied, Is.EqualTo(1), "Compressed FPI should decompress and repair the page");

        // Verify the repaired page matches original
        var repairedPage = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, repairedPage);
        Assert.That(repairedPage[PagedMMF.PageHeaderSize], Is.EqualTo(0xAA), "Repaired page should match original data");
    }
}

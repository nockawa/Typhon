using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// End-to-end pipeline tests that exercise the full durability stack:
/// transaction → dirty pages → checkpoint (CRC stamp) → crash simulation → FPI recovery.
/// Uses <see cref="TestBase{T}"/> for full DatabaseEngine integration.
/// </summary>
[TestFixture]
class EndToEndPipelineTests : TestBase<EndToEndPipelineTests>
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_e2e_test_{Guid.NewGuid():N}");
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

    /// <summary>
    /// Creates a page buffer with a valid CRC32C checksum in the PageBaseHeader.
    /// </summary>
    private static byte[] BuildPageWithCrc(byte fillByte = 0xAA)
    {
        var page = new byte[PagedMMF.PageSize];

        ref var header = ref Unsafe.As<byte, PageBaseHeader>(ref page[0]);
        header.Flags = PageBlockFlags.None;
        header.Type = PageBlockType.None;
        header.FormatRevision = 1;
        header.ChangeRevision = 1;
        header.ModificationCounter = 0;

        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            page[i] = fillByte;
        }

        var crc = WalCrc.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Unsafe.As<byte, uint>(ref page[PageBaseHeader.PageChecksumOffset]) = crc;

        return page;
    }

    // ═══════════════════════════════════════════════════════════════
    // Full Pipeline: Transaction → Checkpoint → Crash → FPI Recovery
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void Transaction_Checkpoint_CrashSimulation_Recovery_DataIntact()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var mmf = dbe.MMF;
        var registry = dbe.UowRegistry;

        // Step 1: Register components and create an entity via a real transaction.
        // This exercises the full write path: transaction → page allocation → data write → commit.
        RegisterComponents(dbe);
        var compData = new CompA(42, 3.14f, 2.718);
        var t = dbe.CreateQuickTransaction();
        var entityId = t.CreateEntity(ref compData);
        t.Commit();
        t.Dispose();

        // Verify entity exists
        var readTx = dbe.CreateQuickTransaction();
        readTx.ReadEntity(entityId, out CompA readBack);
        Assert.That(readBack.A, Is.EqualTo(42), "Entity should be readable after commit");
        readTx.Dispose();

        // Step 2: Simulate checkpoint by writing a known page to disk with valid CRC.
        // In a real system, WritePagesForCheckpoint handles seqlock + CRC stamping.
        // Here we manually build and write a page to isolate the recovery pipeline.
        const int targetPage = 5;
        var goodPageData = BuildPageWithCrc(0xBB);
        mmf.WritePageDirect(targetPage, goodPageData);

        // Verify the page has valid CRC
        var verify = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, verify);
        var storedCrc = Unsafe.As<byte, uint>(ref verify[PageBaseHeader.PageChecksumOffset]);
        Assert.That(storedCrc, Is.Not.EqualTo(0u), "Page should have non-zero CRC");
        var computedCrc = WalCrc.ComputeSkipping(verify, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Assert.That(computedCrc, Is.EqualTo(storedCrc), "Page CRC should be valid");

        // Step 3: Build WAL segment with FPI record for the page (simulates FPI capture)
        var fpiSegment = BuildFpiSegment(1, 1, targetPage, goodPageData);
        CreateWalSegmentFile(1, fpiSegment);

        // Step 4: Simulate crash — corrupt the page on disk
        var corruptedPage = (byte[])goodPageData.Clone();
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            corruptedPage[i] = 0xFF; // Overwrite data
        }
        mmf.WritePageDirect(targetPage, corruptedPage);

        // Verify corruption: CRC should mismatch
        var corruptRead = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, corruptRead);
        var corruptCrc = WalCrc.ComputeSkipping(corruptRead, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        var corruptStored = Unsafe.As<byte, uint>(ref corruptRead[PageBaseHeader.PageChecksumOffset]);
        Assert.That(corruptCrc, Is.Not.EqualTo(corruptStored), "Corrupted page should have CRC mismatch");

        // Step 5: Run WAL recovery — Phase 4 should detect CRC mismatch and repair from FPI
        registry.LoadFromDiskRaw();
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.FpiRecordsApplied, Is.EqualTo(1), "Recovery should repair the torn page from FPI");

        // Step 6: Verify repaired page matches original
        var repairedPage = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, repairedPage);

        // Verify CRC is valid
        var repairedCrc = WalCrc.ComputeSkipping(repairedPage, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        var repairedStoredCrc = Unsafe.As<byte, uint>(ref repairedPage[PageBaseHeader.PageChecksumOffset]);
        Assert.That(repairedCrc, Is.EqualTo(repairedStoredCrc), "Repaired page should have valid CRC");

        // Verify data
        Assert.That(repairedPage[PagedMMF.PageHeaderSize], Is.EqualTo(0xBB),
            "Repaired page should contain original data");

        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            Assert.That(repairedPage[i], Is.EqualTo(goodPageData[i]), $"Data mismatch at offset {i}");
        }
    }

    /// <summary>
    /// Builds a WAL segment containing an uncompressed FPI record for a single page.
    /// </summary>
    private static unsafe byte[] BuildFpiSegment(long segmentId, long firstLSN, int filePageIndex, byte[] pageData)
    {
        const uint segmentSize = 64 * 1024;
        var data = new byte[segmentSize];

        // Write segment header
        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, segmentSize);
            header.ComputeAndSetCrc();
        }

        var payloadLen = FpiMetadata.SizeInBytes + PagedMMF.PageSize;
        var frameLength = WalFrameHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payloadLen;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = 1;

        // Write FPI record
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
        recHeader.Flags = (byte)WalRecordFlags.FullPageImage;
        recHeader.PrevCRC = 0;
        recHeader.CRC = 0;

        // Write FpiMetadata
        var metaOffset = recordOffset + WalRecordHeader.SizeInBytes;
        ref var meta = ref Unsafe.As<byte, FpiMetadata>(ref data[metaOffset]);
        meta.FilePageIndex = filePageIndex;
        meta.SegmentId = 0;
        meta.ChangeRevision = 1;
        meta.UncompressedSize = (ushort)PagedMMF.PageSize;
        meta.CompressionAlgo = 0;
        meta.Reserved = 0;

        // Write page data
        pageData.AsSpan().CopyTo(data.AsSpan(metaOffset + FpiMetadata.SizeInBytes));

        // Compute CRC
        var recordSpan = data.AsSpan(recordOffset, WalRecordHeader.SizeInBytes + payloadLen);
        var crcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
        var computedCrc = WalCrc.ComputeSkipping(recordSpan, crcFieldOffset, sizeof(uint));
        Unsafe.As<byte, uint>(ref data[recordOffset + crcFieldOffset]) = computedCrc;

        return data;
    }
}

using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="WalSegmentReader"/> — the sequential WAL segment reader used during crash recovery.
/// Builds raw segment binary data in-memory and verifies the reader parses records correctly,
/// detects CRC breaks, and handles truncation.
/// </summary>
[TestFixture]
public class WalSegmentReaderTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private const uint TestSegmentSize = 64 * 1024; // 64KB for tests
    private const string TestSegmentPath = "/tmp/test_segment_001.wal";

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        base.TearDown();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Build WAL segment binary data
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a complete WAL segment with header, frames, and records in a byte array.
    /// </summary>
    private static unsafe byte[] BuildSegment(long segmentId, long firstLSN, params RecordDef[] records)
    {
        var data = new byte[TestSegmentSize];

        // Write segment header
        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, TestSegmentSize);
            header.ComputeAndSetCrc();
        }

        if (records.Length == 0)
        {
            return data;
        }

        // Calculate frame size: WalFrameHeader + all records (headers + payloads)
        var totalRecordBytes = 0;
        foreach (var rec in records)
        {
            totalRecordBytes += WalRecordHeader.SizeInBytes + (rec.Payload?.Length ?? 0);
        }

        var frameLength = WalFrameHeader.SizeInBytes + totalRecordBytes;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = records.Length;

        // Write records
        var recordOffset = offset + WalFrameHeader.SizeInBytes;
        var lsn = firstLSN;
        uint prevCrc = 0;

        for (int i = 0; i < records.Length; i++)
        {
            var rec = records[i];
            var payloadLen = rec.Payload?.Length ?? 0;

            ref var recHeader = ref Unsafe.As<byte, WalRecordHeader>(ref data[recordOffset]);
            recHeader.LSN = lsn++;
            recHeader.TransactionTSN = rec.TSN;
            recHeader.TotalRecordLength = (uint)(WalRecordHeader.SizeInBytes + payloadLen);
            recHeader.UowEpoch = rec.UowId;
            recHeader.ComponentTypeId = rec.ComponentTypeId;
            recHeader.EntityId = rec.EntityId;
            recHeader.PayloadLength = (ushort)payloadLen;
            recHeader.OperationType = rec.OperationType;
            recHeader.Flags = rec.Flags;
            recHeader.PrevCRC = prevCrc;
            recHeader.CRC = 0;

            // Write payload
            if (payloadLen > 0)
            {
                rec.Payload.AsSpan().CopyTo(data.AsSpan(recordOffset + WalRecordHeader.SizeInBytes));
            }

            // Compute CRC over header+payload with CRC field zeroed
            var recordSpan = data.AsSpan(recordOffset, WalRecordHeader.SizeInBytes + payloadLen);
            var crcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
            var computedCrc = WalCrc.ComputeSkipping(recordSpan, crcFieldOffset, sizeof(uint));

            // Write CRC back
            Unsafe.As<byte, uint>(ref data[recordOffset + crcFieldOffset]) = computedCrc;
            prevCrc = computedCrc;

            recordOffset += WalRecordHeader.SizeInBytes + payloadLen;
        }

        return data;
    }

    /// <summary>Loads segment data into InMemoryWalFileIO so WalSegmentReader can read it.</summary>
    private void LoadSegmentIntoFileIO(string path, byte[] segmentData)
    {
        using var handle = _fileIO.OpenSegment(path, withFUA: false);
        _fileIO.WriteAligned(handle, 0, segmentData);
    }

    /// <summary>Defines a WAL record for test segment building.</summary>
    private record struct RecordDef(
        ushort UowId,
        byte Flags,
        byte OperationType = (byte)WalOperationType.Create,
        ushort ComponentTypeId = 1,
        long EntityId = 100,
        long TSN = 1,
        byte[] Payload = null);

    // ═══════════════════════════════════════════════════════════════
    // Segment Header Validation
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void OpenSegment_ValidHeader_ReturnsTrue()
    {
        var segmentData = BuildSegment(segmentId: 1, firstLSN: 1);
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);

        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);
        Assert.That(reader.SegmentHeader.SegmentId, Is.EqualTo(1));
        Assert.That(reader.SegmentHeader.FirstLSN, Is.EqualTo(1));
    }

    [Test]
    public void OpenSegment_InvalidMagic_ReturnsFalse()
    {
        var segmentData = BuildSegment(segmentId: 1, firstLSN: 1);
        // Corrupt the magic number (first 4 bytes)
        segmentData[0] = 0xFF;
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);

        Assert.That(reader.OpenSegment(TestSegmentPath), Is.False);
    }

    [Test]
    public void OpenSegment_NonExistentPath_ReturnsFalse()
    {
        using var reader = new WalSegmentReader(_fileIO);

        Assert.That(reader.OpenSegment("/tmp/nonexistent.wal"), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════
    // Record Reading
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void TryReadNext_SingleRecord_ReadsCorrectly()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 5, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit),
                OperationType: (byte)WalOperationType.Create, EntityId: 42, TSN: 100, Payload: payload));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out var header, out var readPayload), Is.True);

        Assert.That(header.LSN, Is.EqualTo(1));
        Assert.That(header.UowEpoch, Is.EqualTo(5));
        Assert.That(header.EntityId, Is.EqualTo(42));
        Assert.That(header.TransactionTSN, Is.EqualTo(100));
        Assert.That(header.PayloadLength, Is.EqualTo(4));
        Assert.That(header.OperationType, Is.EqualTo((byte)WalOperationType.Create));
        Assert.That(header.Flags, Is.EqualTo((byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit)));
        Assert.That(readPayload.ToArray(), Is.EqualTo(payload));

        // No more records
        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.False);
        Assert.That(reader.LastValidLSN, Is.EqualTo(1));
        Assert.That(reader.RecordsRead, Is.EqualTo(1));
    }

    [Test]
    public void TryReadNext_MultipleRecords_ReadsAll()
    {
        var payload1 = new byte[] { 0x01, 0x02 };
        var payload2 = new byte[] { 0x03, 0x04, 0x05 };
        var payload3 = new byte[] { 0x06 };

        var segmentData = BuildSegment(1, 10,
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload1),
            new RecordDef(UowId: 1, Flags: 0, Payload: payload2),
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload3));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        // Record 1
        Assert.That(reader.TryReadNext(out var h1, out var p1), Is.True);
        Assert.That(h1.LSN, Is.EqualTo(10));
        Assert.That(h1.Flags & (byte)WalRecordFlags.UowBegin, Is.Not.EqualTo(0));
        Assert.That(p1.ToArray(), Is.EqualTo(payload1));

        // Record 2
        Assert.That(reader.TryReadNext(out var h2, out var p2), Is.True);
        Assert.That(h2.LSN, Is.EqualTo(11));
        Assert.That(p2.ToArray(), Is.EqualTo(payload2));

        // Record 3
        Assert.That(reader.TryReadNext(out var h3, out var p3), Is.True);
        Assert.That(h3.LSN, Is.EqualTo(12));
        Assert.That(h3.Flags & (byte)WalRecordFlags.UowCommit, Is.Not.EqualTo(0));
        Assert.That(p3.ToArray(), Is.EqualTo(payload3));

        // End of data
        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.RecordsRead, Is.EqualTo(3));
        Assert.That(reader.LastValidLSN, Is.EqualTo(12));
    }

    [Test]
    public void TryReadNext_RecordWithNoPayload_ReadsCorrectly()
    {
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit),
                OperationType: (byte)WalOperationType.Delete, EntityId: 99, Payload: null));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out var header, out var payload), Is.True);
        Assert.That(header.OperationType, Is.EqualTo((byte)WalOperationType.Delete));
        Assert.That(header.PayloadLength, Is.EqualTo(0));
        Assert.That(payload.Length, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty Segment
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void TryReadNext_EmptySegment_ReturnsFalse()
    {
        // Segment with valid header but no records
        var segmentData = BuildSegment(1, 1);
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.False);
        Assert.That(reader.RecordsRead, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC Corruption Detection
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void TryReadNext_CorruptedCRC_SetsTruncatedFlag()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        // Corrupt a byte in the record CRC field
        var crcFieldOffset = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes +
                             (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
        segmentData[crcFieldOffset] ^= 0xFF;

        // Reload with corrupted data
        _fileIO.Dispose();
        _fileIO = new InMemoryWalFileIO();
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.True, "CRC corruption should set WasTruncated");
    }

    [Test]
    public void TryReadNext_CorruptedPayload_SetsTruncatedFlag()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        // Corrupt a byte in the payload area (CRC won't match)
        var payloadOffset = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes + WalRecordHeader.SizeInBytes;
        segmentData[payloadOffset] ^= 0xFF;

        _fileIO.Dispose();
        _fileIO = new InMemoryWalFileIO();
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.True);
    }

    [Test]
    public void TryReadNext_CrcChainBreak_SecondRecord_SetsTruncated()
    {
        var payload1 = new byte[] { 0x01 };
        var payload2 = new byte[] { 0x02 };

        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload1),
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload2));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        // Corrupt the PrevCRC field of the second record to break the chain
        var record2Offset = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes +
                            WalRecordHeader.SizeInBytes + payload1.Length;
        var prevCrcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.PrevCRC));

        // Read the current PrevCRC and modify it
        ref var prevCrc = ref Unsafe.As<byte, uint>(ref segmentData[record2Offset + prevCrcFieldOffset]);
        var originalPrevCrc = prevCrc;
        prevCrc = originalPrevCrc ^ 0xDEADBEEF;

        // Recompute the CRC of record 2 (since we changed PrevCRC, the record's own CRC is now wrong)
        var crcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
        Unsafe.As<byte, uint>(ref segmentData[record2Offset + crcFieldOffset]) = 0;
        var rec2Span = segmentData.AsSpan(record2Offset, WalRecordHeader.SizeInBytes + payload2.Length);
        var newCrc = WalCrc.ComputeSkipping(rec2Span, crcFieldOffset, sizeof(uint));
        Unsafe.As<byte, uint>(ref segmentData[record2Offset + crcFieldOffset]) = newCrc;

        _fileIO.Dispose();
        _fileIO = new InMemoryWalFileIO();
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        // First record should read fine
        Assert.That(reader.TryReadNext(out _, out _), Is.True);

        // Second record — PrevCRC doesn't match first record's CRC → chain break
        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.True);
        Assert.That(reader.RecordsRead, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // Multiple Segments
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void OpenSegment_MultipleCalls_ResetsState()
    {
        var payload = new byte[] { 0xAA };

        var seg1 = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));
        var seg2 = BuildSegment(2, 100,
            new RecordDef(UowId: 2, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));

        LoadSegmentIntoFileIO("/tmp/seg1.wal", seg1);
        LoadSegmentIntoFileIO("/tmp/seg2.wal", seg2);

        using var reader = new WalSegmentReader(_fileIO);

        // Read segment 1
        Assert.That(reader.OpenSegment("/tmp/seg1.wal"), Is.True);
        Assert.That(reader.TryReadNext(out var h1, out _), Is.True);
        Assert.That(h1.LSN, Is.EqualTo(1));
        Assert.That(h1.UowEpoch, Is.EqualTo(1));

        // Read segment 2 — state should be fully reset
        Assert.That(reader.OpenSegment("/tmp/seg2.wal"), Is.True);
        Assert.That(reader.WasTruncated, Is.False);
        Assert.That(reader.RecordsRead, Is.EqualTo(0));
        Assert.That(reader.TryReadNext(out var h2, out _), Is.True);
        Assert.That(h2.LSN, Is.EqualTo(100));
        Assert.That(h2.UowEpoch, Is.EqualTo(2));
    }
}

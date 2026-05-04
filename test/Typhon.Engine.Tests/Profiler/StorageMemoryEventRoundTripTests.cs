using NUnit.Framework;
using System;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 8 Storage &amp; Memory event codecs added in Phase 5 (#283) +
/// the wire-additive payload extension on the existing <see cref="TraceEventKind.PageEvicted"/> (kind 55).
/// </summary>
[TestFixture]
public class StorageMemoryEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    // ─────────────────────────────────────────────────────────────────────
    // PageEvicted wire-additive +dirtyBit (kind 55, OptDirtyBit = 0x02)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void PageEvicted_Extended_RoundTrip(byte dirtyBit)
    {
        const int filePageIndex = 0x12345678;
        const byte optMask = PageCacheEventCodec.OptDirtyBit;

        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageEvicted, hasTraceContext: false, optMask);
        Span<byte> buf = stackalloc byte[size];
        PageCacheEventCodec.Encode(buf, EndTs, TraceEventKind.PageEvicted, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            filePageIndex, pageCount: 0, optMask, out var bytesWritten, dirtyBit: dirtyBit);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = PageCacheEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.PageEvicted));
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.FilePageIndex, Is.EqualTo(filePageIndex));
        Assert.That(d.HasDirtyBit, Is.True);
        Assert.That(d.DirtyBit, Is.EqualTo(dirtyBit));
    }

    [Test]
    public void PageEvicted_LegacyShape_RoundTrip()
    {
        // Legacy producer (no dirty bit, optMask=0) — verify decoder still works without HasDirtyBit being set.
        const int filePageIndex = 99;
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageEvicted, hasTraceContext: false, optMask: 0);
        Span<byte> buf = stackalloc byte[size];
        PageCacheEventCodec.Encode(buf, EndTs, TraceEventKind.PageEvicted, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            filePageIndex, pageCount: 0, optMask: 0, out _);

        var d = PageCacheEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.PageEvicted));
        Assert.That(d.FilePageIndex, Is.EqualTo(filePageIndex));
        Assert.That(d.HasDirtyBit, Is.False);
        Assert.That(d.DirtyBit, Is.EqualTo(0));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Storage:PageCache:DirtyWalk (kind 165, span)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void StoragePageCacheDirtyWalk_RoundTrip()
    {
        var size = StorageMiscEventCodec.ComputeSizeDirtyWalk(hasTraceContext: false);
        Span<byte> buf = stackalloc byte[size];
        StorageMiscEventCodec.EncodeDirtyWalk(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            rangeStart: 100, rangeLen: 256, dirtyMs: 12, out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = StorageMiscEventCodec.DecodeDirtyWalk(buf);
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.StartTimestamp, Is.EqualTo(StartTs));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
        Assert.That(d.SpanId, Is.EqualTo(SpanId));
        Assert.That(d.ParentSpanId, Is.EqualTo(ParentSpanId));
        Assert.That(d.RangeStart, Is.EqualTo(100));
        Assert.That(d.RangeLen, Is.EqualTo(256));
        Assert.That(d.DirtyMs, Is.EqualTo(12));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Storage:Segment Create/Grow/Load (kinds 166-168)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void StorageSegmentCreate_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[StorageSegmentEventCodec.CreateLoadSize];
        StorageSegmentEventCodec.WriteCreate(buf, ThreadSlot, StartTs, segmentId: 42, pageCount: 16);
        var d = StorageSegmentEventCodec.DecodeCreateOrLoad(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.StorageSegmentCreate));
        Assert.That(d.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(d.Timestamp, Is.EqualTo(StartTs));
        Assert.That(d.SegmentId, Is.EqualTo(42));
        Assert.That(d.PageCount, Is.EqualTo(16));
    }

    [Test]
    public void StorageSegmentGrow_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[StorageSegmentEventCodec.GrowSize];
        StorageSegmentEventCodec.WriteGrow(buf, ThreadSlot, StartTs, segmentId: 42, oldLen: 16, newLen: 32);
        var d = StorageSegmentEventCodec.DecodeGrow(buf);
        Assert.That(d.SegmentId, Is.EqualTo(42));
        Assert.That(d.OldLen, Is.EqualTo(16));
        Assert.That(d.NewLen, Is.EqualTo(32));
    }

    [Test]
    public void StorageSegmentLoad_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[StorageSegmentEventCodec.CreateLoadSize];
        StorageSegmentEventCodec.WriteLoad(buf, ThreadSlot, StartTs, segmentId: 7, pageCount: 4);
        var d = StorageSegmentEventCodec.DecodeCreateOrLoad(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.StorageSegmentLoad));
        Assert.That(d.SegmentId, Is.EqualTo(7));
        Assert.That(d.PageCount, Is.EqualTo(4));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Storage:ChunkSegment:Grow (kind 169)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void StorageChunkSegmentGrow_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[StorageMiscEventCodec.ChunkSegmentGrowSize];
        StorageMiscEventCodec.WriteChunkSegmentGrow(buf, ThreadSlot, StartTs, stride: 256, oldCap: 128, newCap: 512);
        var d = StorageMiscEventCodec.DecodeChunkSegmentGrow(buf);
        Assert.That(d.Stride, Is.EqualTo(256));
        Assert.That(d.OldCap, Is.EqualTo(128));
        Assert.That(d.NewCap, Is.EqualTo(512));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Storage:FileHandle (kind 170, op variant)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((byte)0, (byte)2)]   // open with FileMode.Create
    [TestCase((byte)1, (byte)0)]   // close, no reason
    public void StorageFileHandle_RoundTrip(byte op, byte modeOrReason)
    {
        Span<byte> buf = stackalloc byte[StorageMiscEventCodec.FileHandleSize];
        StorageMiscEventCodec.WriteFileHandle(buf, ThreadSlot, StartTs, op, filePathId: 0xDEADBEEF.GetHashCode(), modeOrReason);
        var d = StorageMiscEventCodec.DecodeFileHandle(buf);
        Assert.That(d.Op, Is.EqualTo(op));
        Assert.That(d.ModeOrReason, Is.EqualTo(modeOrReason));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Storage:OccupancyMap:Grow (kind 171)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void StorageOccupancyMapGrow_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[StorageMiscEventCodec.OccupancyMapGrowSize];
        StorageMiscEventCodec.WriteOccupancyMapGrow(buf, ThreadSlot, StartTs, oldCap: 1024, newCap: 2048);
        var d = StorageMiscEventCodec.DecodeOccupancyMapGrow(buf);
        Assert.That(d.OldCap, Is.EqualTo(1024));
        Assert.That(d.NewCap, Is.EqualTo(2048));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Memory:AlignmentWaste (kind 172)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void MemoryAlignmentWaste_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[MemoryAlignmentWasteEventCodec.Size];
        MemoryAlignmentWasteEventCodec.Write(buf, ThreadSlot, StartTs, size: 100, alignment: 64, wastePctHundredths: 2800);
        var d = MemoryAlignmentWasteEventCodec.Decode(buf);
        Assert.That(d.Size, Is.EqualTo(100));
        Assert.That(d.Alignment, Is.EqualTo(64));
        Assert.That(d.WastePctHundredths, Is.EqualTo(2800));
    }

    // ─────────────────────────────────────────────────────────────────────
    // IsSpan classification — Phase 5 instants must NOT be classified as spans
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void Phase5_IsSpan_ClassifiesCorrectly()
    {
        // Spans
        Assert.That(TraceEventKind.StoragePageCacheDirtyWalk.IsSpan(), Is.True);
        // Instants
        Assert.That(TraceEventKind.StorageSegmentCreate.IsSpan(), Is.False);
        Assert.That(TraceEventKind.StorageSegmentGrow.IsSpan(), Is.False);
        Assert.That(TraceEventKind.StorageSegmentLoad.IsSpan(), Is.False);
        Assert.That(TraceEventKind.StorageChunkSegmentGrow.IsSpan(), Is.False);
        Assert.That(TraceEventKind.StorageFileHandle.IsSpan(), Is.False);
        Assert.That(TraceEventKind.StorageOccupancyMapGrow.IsSpan(), Is.False);
        Assert.That(TraceEventKind.MemoryAlignmentWaste.IsSpan(), Is.False);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Phase 5 GaugeId additions — sanity check that PerTickSnapshotEventCodec
    // accepts the new IDs as part of a mixed-kind round-trip.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void Phase5_Gauges_RoundTrip()
    {
        var values = new[]
        {
            GaugeValue.FromU64(GaugeId.PageCacheFileSizeBytes, 0x1_0000_0000UL),
            GaugeValue.FromU64(GaugeId.PageCacheLRUAgeTicks, 123456789UL),
            GaugeValue.FromPercentHundredths(GaugeId.MemoryFragmentationPctHundredths, 1234u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride64, 100u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride128, 200u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride256, 300u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride512, 400u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride1024, 500u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride2048, 600u),
            GaugeValue.FromU32(GaugeId.MemoryPoolFreeBlocksStride4096, 700u),
        };

        var size = PerTickSnapshotEventCodec.ComputeSize(values);
        var buf = new byte[size];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(
            buf,
            threadSlot: ThreadSlot,
            timestamp: StartTs,
            tickNumber: 1u,
            flags: 0u,
            values,
            out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var decoded = PerTickSnapshotEventCodec.DecodePerTickSnapshot(buf);
        Assert.That(decoded.Values.Length, Is.EqualTo(values.Length));
        for (var i = 0; i < values.Length; i++)
        {
            Assert.That(decoded.Values[i].Id, Is.EqualTo(values[i].Id), $"Gauge[{i}] Id mismatch");
            Assert.That(decoded.Values[i].Kind, Is.EqualTo(values[i].Kind), $"Gauge[{i}] Kind mismatch");
            Assert.That(decoded.Values[i].RawValue, Is.EqualTo(values[i].RawValue), $"Gauge[{i}] RawValue mismatch");
        }
    }
}

using NUnit.Framework;
using System;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-codec source-loc roundtrip tests. For every span codec that supports source attribution
/// (siteId via interceptors), encode with sourceLocationId=0xABCD and verify the decoder
/// reads it back correctly — both with and without trace context.
///
/// Originally added when issue #293 step-2 remediation discovered that ~30 codecs had
/// asymmetric encode/decode (Encode wrote source-loc but Decode used 1-arg SpanHeaderSize,
/// reading source-loc bytes as the first payload field).
/// </summary>
[TestFixture]
public class SourceLocationCodecRoundTripTests
{
    private const ushort SiteId = 0xABCD;
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_000;
    private const long EndTs = 2_000;
    private const ulong SpanId = 0x1111_2222_3333_4444UL;
    private const ulong ParentSpanId = 0x5555_6666_7777_8888UL;
    private const ulong TraceIdHi = 0xAAAA_BBBB_CCCC_DDDDUL;
    private const ulong TraceIdLo = 0x0011_2233_4455_6677UL;

    // ── RuntimePhaseSpan (the AntHill-breaking codec) ──
    [TestCase(false, TestName = "RuntimePhaseSpan_NoTraceCtx_RoundTrip")]
    [TestCase(true, TestName = "RuntimePhaseSpan_WithTraceCtx_RoundTrip")]
    public void RuntimePhaseSpan_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var hasTC = thi != 0 || tlo != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation: true) + 1;
        Span<byte> buf = stackalloc byte[size];
        RuntimePhaseSpanEventCodec.Encode(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo, phase: 3, out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = RuntimePhaseSpanEventCodec.Decode(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
        Assert.That(d.Phase, Is.EqualTo((byte)3));
        if (withTraceCtx)
        {
            Assert.That(d.TraceIdHi, Is.EqualTo(thi));
            Assert.That(d.TraceIdLo, Is.EqualTo(tlo));
        }
    }

    // ── BTree no-payload (already correct, regression test) ──
    [TestCase(false)]
    [TestCase(true)]
    public void BTreeInsert_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var hasTC = thi != 0 || tlo != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation: true);
        Span<byte> buf = stackalloc byte[size];
        BTreeEventCodec.EncodeNoPayload(buf, EndTs, TraceEventKind.BTreeInsert, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo, out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = BTreeEventCodec.Decode(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
    }

    // ── Checkpoint (payload after source-loc — was broken in Decode) ──
    [TestCase(false)]
    [TestCase(true)]
    public void CheckpointCycle_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var optMask = CheckpointEventCodec.OptDirtyPageCount;
        var size = CheckpointEventCodec.ComputeCycleSize(thi != 0 || tlo != 0, optMask) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        CheckpointEventCodec.EncodeCycle(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            targetLsn: 0x1234_5678L, reason: 1, optMask, dirtyPageCount: 42,
            out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = CheckpointEventCodec.Decode(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
        Assert.That(d.TargetLsn, Is.EqualTo(0x1234_5678L), "TargetLsn must NOT be polluted by source-loc bytes");
        Assert.That(d.DirtyPageCount, Is.EqualTo(42));
    }

    // ── Transaction commit (payload after source-loc) ──
    [TestCase(false)]
    [TestCase(true)]
    public void TransactionCommit_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var optMask = (byte)(TransactionEventCodec.OptComponentCount | TransactionEventCodec.OptConflictDetected);
        var size = TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommit, thi != 0 || tlo != 0, optMask) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        TransactionEventCodec.Encode(buf, EndTs, TraceEventKind.TransactionCommit, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo, tsn: 0x9999L, componentTypeId: 0,
            optMask, componentCount: 5, conflictDetected: true, out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = TransactionEventCodec.Decode(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
        Assert.That(d.Tsn, Is.EqualTo(0x9999L), "Tsn must NOT be polluted by source-loc bytes");
        Assert.That(d.ComponentCount, Is.EqualTo(5));
    }

    // ── Wal (payload after source-loc) ──
    [TestCase(false)]
    [TestCase(true)]
    public void WalFlush_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var size = WalEventCodec.ComputeSize(TraceEventKind.WalFlush, thi != 0 || tlo != 0) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        WalEventCodec.Encode(buf, EndTs, TraceEventKind.WalFlush, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            batchByteCount: 0x1234, frameCount: 5, highLsn: 0xDEAD_BEEFL, newSegmentIndex: 0, targetLsn: 0,
            out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = WalEventCodec.Decode(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
        Assert.That(d.HighLsn, Is.EqualTo(0xDEAD_BEEFL), "HighLsn must NOT be polluted");
    }

    // ── DataIndexBTreeRangeScan (uses WriteSpanPreamble — was broken end-to-end) ──
    [TestCase(false)]
    [TestCase(true)]
    public void DataIndexBTreeRangeScan_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var size = DataIndexBTreeEventCodec.ComputeSizeRangeScan(thi != 0 || tlo != 0) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        DataIndexBTreeEventCodec.EncodeRangeScan(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            resultCount: 12345, restartCount: 2, out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = DataIndexBTreeEventCodec.DecodeRangeScan(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
        Assert.That(d.ResultCount, Is.EqualTo(12345), "ResultCount must NOT be polluted");
        Assert.That(d.RestartCount, Is.EqualTo((byte)2));
    }

    // ── QueryEventCodec.Parse (uses WriteSpanPreamble) ──
    [TestCase(false)]
    [TestCase(true)]
    public void QueryParse_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var size = QueryEventCodec.ComputeSizeParse(thi != 0 || tlo != 0) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        QueryEventCodec.EncodeParse(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            predicateCount: 7, branchCount: 3, out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = QueryEventCodec.DecodeParse(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.HasSourceLocation, Is.True);
        Assert.That(d.PredicateCount, Is.EqualTo((ushort)7), "PredicateCount must NOT be polluted");
        Assert.That(d.BranchCount, Is.EqualTo((byte)3));
    }

    // ── SpatialQueryAabb (was overlap codec — payload clobbered source-loc) ──
    [TestCase(false)]
    [TestCase(true)]
    public void SpatialQueryAabb_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var size = SpatialQueryEventCodec.ComputeSizeAabb(thi != 0 || tlo != 0) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        SpatialQueryEventCodec.EncodeAabb(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            nodesVisited: 15, leavesEntered: 8, resultCount: 4, restartCount: 0, categoryMask: 0xFF,
            out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = SpatialQueryEventCodec.DecodeAabb(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.NodesVisited, Is.EqualTo((ushort)15), "NodesVisited must NOT be polluted by source-loc bytes");
        Assert.That(d.ResultCount, Is.EqualTo((ushort)4));
    }

    // ── SchedulerWorkerIdle (overlap codec) ──
    [TestCase(false)]
    [TestCase(true)]
    public void SchedulerWorkerIdle_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var size = SchedulerWorkerEventCodec.ComputeSizeIdle(thi != 0 || tlo != 0) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        SchedulerWorkerEventCodec.EncodeIdle(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            workerId: 5, spinCount: 99, idleUs: 0x1234_5678U,
            out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = SchedulerWorkerEventCodec.DecodeIdle(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.IdleUs, Is.EqualTo(0x1234_5678U), "IdleUs must NOT be polluted");
        Assert.That(d.WorkerId, Is.EqualTo((byte)5));
    }

    // ── DurabilityWalQueueDrain (Group D codec — was missing all write logic) ──
    [TestCase(false)]
    [TestCase(true)]
    public void DurabilityWalQueueDrain_RoundTrip(bool withTraceCtx)
    {
        var thi = withTraceCtx ? TraceIdHi : 0UL;
        var tlo = withTraceCtx ? TraceIdLo : 0UL;
        var size = DurabilityWalEventCodec.ComputeSizeQueueDrain(thi != 0 || tlo != 0) + TraceRecordHeader.SourceLocationIdSize;
        Span<byte> buf = stackalloc byte[size];
        DurabilityWalEventCodec.EncodeQueueDrain(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, thi, tlo,
            bytesAligned: 0xCAFE, frameCount: 99,
            out var bytesWritten, sourceLocationId: SiteId);
        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = DurabilityWalEventCodec.DecodeQueueDrain(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(SiteId));
        Assert.That(d.BytesAligned, Is.EqualTo(0xCAFE), "BytesAligned must NOT be polluted");
        Assert.That(d.FrameCount, Is.EqualTo(99));
    }

    // ── Boundary: ushort.MaxValue ──
    [Test]
    public void RuntimePhaseSpan_MaxSiteId_RoundTrip()
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext: false, hasSourceLocation: true) + 1;
        Span<byte> buf = stackalloc byte[size];
        RuntimePhaseSpanEventCodec.Encode(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, 0, 0, phase: 1, out _, sourceLocationId: ushort.MaxValue);

        var d = RuntimePhaseSpanEventCodec.Decode(buf);
        Assert.That(d.SourceLocationId, Is.EqualTo(ushort.MaxValue));
    }

    // ── Backward compat: siteId=0 produces legacy wire bytes ──
    [Test]
    public void RuntimePhaseSpan_ZeroSiteId_NoSourceLocFlag()
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext: false) + 1;
        Span<byte> buf = stackalloc byte[size];
        RuntimePhaseSpanEventCodec.Encode(buf, EndTs, ThreadSlot, StartTs,
            SpanId, ParentSpanId, 0, 0, phase: 1, out var bytesWritten, sourceLocationId: 0);
        Assert.That(bytesWritten, Is.EqualTo(size), "Legacy size — no extra source-loc bytes");

        var d = RuntimePhaseSpanEventCodec.Decode(buf);
        Assert.That(d.HasSourceLocation, Is.False);
        Assert.That(d.SourceLocationId, Is.EqualTo((ushort)0));
    }
}

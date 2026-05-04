using NUnit.Framework;
using System;
using Typhon.Profiler;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 14 Data plane event codecs added in Phase 6 (#284) +
/// the wire-additive payload extensions on existing kinds 21 (TransactionRollback +reason)
/// and 22 (TransactionCommitComponent +rowCount).
/// </summary>
[TestFixture]
public class DataPlaneEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    // ─────────────────────────────────────────────────────────────────────
    // TransactionRollback (kind 21) — wire-additive +reason u8 (D3)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase(TransactionRollbackReason.Explicit)]
    [TestCase(TransactionRollbackReason.AutoOnDispose)]
    [TestCase(TransactionRollbackReason.Conflict)]
    [TestCase(TransactionRollbackReason.TimedOut)]
    public void TransactionRollback_Extended_RoundTrip(TransactionRollbackReason reason)
    {
        const long tsn = 0x1234567890ABCDEFL;
        var optMask = (byte)(TransactionEventCodec.OptComponentCount | TransactionEventCodec.OptReason);
        var size = TransactionEventCodec.ComputeSize(TraceEventKind.TransactionRollback, hasTraceContext: false, optMask);
        Span<byte> buf = stackalloc byte[size];
        TransactionEventCodec.Encode(buf, EndTs, TraceEventKind.TransactionRollback, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, tsn, componentTypeId: 0, optMask, componentCount: 5,
            conflictDetected: false, out var bytesWritten, reason: reason);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = TransactionEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.TransactionRollback));
        Assert.That(d.Tsn, Is.EqualTo(tsn));
        Assert.That(d.HasComponentCount, Is.True);
        Assert.That(d.ComponentCount, Is.EqualTo(5));
        Assert.That(d.HasReason, Is.True);
        Assert.That(d.Reason, Is.EqualTo(reason));
    }

    [Test]
    public void TransactionRollback_LegacyShape_RoundTrip()
    {
        // Legacy producer (no reason byte) — verify decoder still works.
        const long tsn = unchecked((long)0xFEEDBEEFL);
        var optMask = TransactionEventCodec.OptComponentCount;
        var size = TransactionEventCodec.ComputeSize(TraceEventKind.TransactionRollback, hasTraceContext: false, optMask);
        Span<byte> buf = stackalloc byte[size];
        TransactionEventCodec.Encode(buf, EndTs, TraceEventKind.TransactionRollback, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, tsn, componentTypeId: 0, optMask, componentCount: 3,
            conflictDetected: false, out _);

        var d = TransactionEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.TransactionRollback));
        Assert.That(d.HasReason, Is.False);
        Assert.That(d.Reason, Is.EqualTo(TransactionRollbackReason.Explicit));  // default
    }

    // ─────────────────────────────────────────────────────────────────────
    // TransactionCommitComponent (kind 22) — wire-additive +rowCount i32
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void TransactionCommitComponent_Extended_RoundTrip()
    {
        const long tsn = unchecked((long)0xDEADBEEFCAFEBABEUL);
        const int componentTypeId = 42;
        var optMask = TransactionEventCodec.OptRowCount;
        var size = TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommitComponent, hasTraceContext: false, optMask);
        Span<byte> buf = stackalloc byte[size];
        TransactionEventCodec.Encode(buf, EndTs, TraceEventKind.TransactionCommitComponent, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, tsn, componentTypeId, optMask, componentCount: 0,
            conflictDetected: false, out var bytesWritten, rowCount: 1234);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var d = TransactionEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.TransactionCommitComponent));
        Assert.That(d.Tsn, Is.EqualTo(tsn));
        Assert.That(d.ComponentTypeId, Is.EqualTo(componentTypeId));
        Assert.That(d.HasRowCount, Is.True);
        Assert.That(d.RowCount, Is.EqualTo(1234));
    }

    [Test]
    public void TransactionCommitComponent_LegacyShape_RoundTrip()
    {
        // Legacy producer (no rowCount) — decoder still works.
        const long tsn = unchecked((long)0xDEADBEEFCAFEBABEUL);
        const int componentTypeId = 42;
        var size = TransactionEventCodec.ComputeSize(TraceEventKind.TransactionCommitComponent, hasTraceContext: false, optMask: 0);
        Span<byte> buf = stackalloc byte[size];
        TransactionEventCodec.Encode(buf, EndTs, TraceEventKind.TransactionCommitComponent, ThreadSlot, StartTs,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, tsn, componentTypeId, optMask: 0, componentCount: 0,
            conflictDetected: false, out _);

        var d = TransactionEventCodec.Decode(buf);
        Assert.That(d.Kind, Is.EqualTo(TraceEventKind.TransactionCommitComponent));
        Assert.That(d.ComponentTypeId, Is.EqualTo(componentTypeId));
        Assert.That(d.HasRowCount, Is.False);
        Assert.That(d.RowCount, Is.EqualTo(0));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Data:Transaction (kinds 173-177)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void DataTransactionInit_RoundTrip()
    {
        var ev = new DataTransactionInitEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 9999,
            UowId = 12,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataTransactionEventCodec.DecodeInit(buf);
        Assert.That(d.Tsn, Is.EqualTo(9999));
        Assert.That(d.UowId, Is.EqualTo(12));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
    }

    [Test]
    public void DataTransactionPrepare_RoundTrip()
    {
        var ev = new DataTransactionPrepareEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 100,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataTransactionEventCodec.DecodePrepare(buf);
        Assert.That(d.Tsn, Is.EqualTo(100));
    }

    [Test]
    public void DataTransactionValidate_RoundTrip()
    {
        var ev = new DataTransactionValidateEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 200,
            EntryCount = 32,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataTransactionEventCodec.DecodeValidate(buf);
        Assert.That(d.Tsn, Is.EqualTo(200));
        Assert.That(d.EntryCount, Is.EqualTo(32));
    }

    [TestCase((byte)0)]  // handler-based
    [TestCase((byte)1)]  // index-based
    public void DataTransactionConflict_RoundTrip(byte conflictType)
    {
        Span<byte> buf = stackalloc byte[DataTransactionEventCodec.ConflictSize];
        DataTransactionEventCodec.WriteConflict(buf, ThreadSlot, StartTs, tsn: 300, pk: 0x4242, componentTypeId: 7, conflictType);
        var d = DataTransactionEventCodec.DecodeConflict(buf);
        Assert.That(d.Tsn, Is.EqualTo(300));
        Assert.That(d.Pk, Is.EqualTo(0x4242));
        Assert.That(d.ComponentTypeId, Is.EqualTo(7));
        Assert.That(d.ConflictType, Is.EqualTo(conflictType));
    }

    [Test]
    public void DataTransactionCleanup_RoundTrip()
    {
        var ev = new DataTransactionCleanupEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 400,
            EntityCount = 16,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataTransactionEventCodec.DecodeCleanup(buf);
        Assert.That(d.Tsn, Is.EqualTo(400));
        Assert.That(d.EntityCount, Is.EqualTo(16));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Data:MVCC (kinds 178-179)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((byte)0)]  // visible
    [TestCase((byte)1)]  // SnapshotInvisible
    [TestCase((byte)2)]  // Deleted
    public void DataMvccChainWalk_RoundTrip(byte visibility)
    {
        Span<byte> buf = stackalloc byte[DataMvccEventCodec.ChainWalkSize];
        DataMvccEventCodec.WriteChainWalk(buf, ThreadSlot, StartTs, tsn: 500, chainLen: 8, visibility);
        var d = DataMvccEventCodec.DecodeChainWalk(buf);
        Assert.That(d.Tsn, Is.EqualTo(500));
        Assert.That(d.ChainLen, Is.EqualTo(8));
        Assert.That(d.Visibility, Is.EqualTo(visibility));
    }

    [Test]
    public void DataMvccVersionCleanup_RoundTrip()
    {
        var ev = new DataMvccVersionCleanupEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Pk = 0xDEADBEEF,
            EntriesFreed = 17,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataMvccEventCodec.DecodeVersionCleanup(buf);
        Assert.That(d.Pk, Is.EqualTo(0xDEADBEEF));
        Assert.That(d.EntriesFreed, Is.EqualTo(17));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Data:Index:BTree (kinds 180-186)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void DataIndexBTreeSearch_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DataIndexBTreeEventCodec.SearchSize];
        DataIndexBTreeEventCodec.WriteSearch(buf, ThreadSlot, StartTs, retryReason: 2, restartCount: 5);
        var d = DataIndexBTreeEventCodec.DecodeSearch(buf);
        Assert.That(d.RetryReason, Is.EqualTo(2));
        Assert.That(d.RestartCount, Is.EqualTo(5));
    }

    [Test]
    public void DataIndexBTreeRangeScan_RoundTrip()
    {
        var ev = new DataIndexBTreeRangeScanEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ResultCount = 1500,
            RestartCount = 3,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataIndexBTreeEventCodec.DecodeRangeScan(buf);
        Assert.That(d.ResultCount, Is.EqualTo(1500));
        Assert.That(d.RestartCount, Is.EqualTo(3));
    }

    [Test]
    public void DataIndexBTreeRangeScanRevalidate_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DataIndexBTreeEventCodec.RevalidateSize];
        DataIndexBTreeEventCodec.WriteRevalidate(buf, ThreadSlot, StartTs, restartCount: 2);
        var d = DataIndexBTreeEventCodec.DecodeRevalidate(buf);
        Assert.That(d.RestartCount, Is.EqualTo(2));
    }

    [TestCase((byte)0)]  // LeafFull
    [TestCase((byte)1)]  // OlcFail
    public void DataIndexBTreeRebalanceFallback_RoundTrip(byte reason)
    {
        Span<byte> buf = stackalloc byte[DataIndexBTreeEventCodec.RebalanceFallbackSize];
        DataIndexBTreeEventCodec.WriteRebalanceFallback(buf, ThreadSlot, StartTs, reason);
        var d = DataIndexBTreeEventCodec.DecodeRebalanceFallback(buf);
        Assert.That(d.Reason, Is.EqualTo(reason));
    }

    [Test]
    public void DataIndexBTreeBulkInsert_RoundTrip()
    {
        var ev = new DataIndexBTreeBulkInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            BufferId = 99,
            EntryCount = 256,
        };
        Span<byte> buf = stackalloc byte[ev.ComputeSize()];
        ev.EncodeTo(buf, EndTs, out _);
        var d = DataIndexBTreeEventCodec.DecodeBulkInsert(buf);
        Assert.That(d.BufferId, Is.EqualTo(99));
        Assert.That(d.EntryCount, Is.EqualTo(256));
    }

    [TestCase((byte)0)]  // Init
    [TestCase((byte)1)]  // Split
    public void DataIndexBTreeRoot_RoundTrip(byte op)
    {
        Span<byte> buf = stackalloc byte[DataIndexBTreeEventCodec.RootSize];
        DataIndexBTreeEventCodec.WriteRoot(buf, ThreadSlot, StartTs, op, rootChunkId: 0xAB, height: 4);
        var d = DataIndexBTreeEventCodec.DecodeRoot(buf);
        Assert.That(d.Op, Is.EqualTo(op));
        Assert.That(d.RootChunkId, Is.EqualTo(0xAB));
        Assert.That(d.Height, Is.EqualTo(4));
    }

    [Test]
    public void DataIndexBTreeNodeCow_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DataIndexBTreeEventCodec.NodeCowSize];
        DataIndexBTreeEventCodec.WriteNodeCow(buf, ThreadSlot, StartTs, srcChunkId: 100, dstChunkId: 101);
        var d = DataIndexBTreeEventCodec.DecodeNodeCow(buf);
        Assert.That(d.SrcChunkId, Is.EqualTo(100));
        Assert.That(d.DstChunkId, Is.EqualTo(101));
    }

    // ─────────────────────────────────────────────────────────────────────
    // IsSpan classification — Phase 6 mixed shape
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void Phase6_IsSpan_ClassifiesCorrectly()
    {
        // Spans
        Assert.That(TraceEventKind.DataTransactionInit.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DataTransactionPrepare.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DataTransactionValidate.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DataTransactionCleanup.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DataMvccVersionCleanup.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DataIndexBTreeRangeScan.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DataIndexBTreeBulkInsert.IsSpan(), Is.True);
        // Instants
        Assert.That(TraceEventKind.DataTransactionConflict.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DataMvccChainWalk.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DataIndexBTreeSearch.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DataIndexBTreeRangeScanRevalidate.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DataIndexBTreeRebalanceFallback.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DataIndexBTreeRoot.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DataIndexBTreeNodeCow.IsSpan(), Is.False);
    }
}

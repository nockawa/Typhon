using NUnit.Framework;
using System;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind round-trip tests for the 21 Durability event codecs added in Phase 8 (#286).
/// Existing kind 80 (WalFlush) stays as-is — Q1 chose additive over hard-split.
/// </summary>
[TestFixture]
public class DurabilityEventRoundTripTests
{
    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_567_990L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;
    private const ulong TraceIdHi = 0;
    private const ulong TraceIdLo = 0;

    // ─────────────────────────────────────────────────────────────────────
    // Durability:WAL (kinds 214-221)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void DurabilityWalQueueDrain_RoundTrip()
    {
        var size = DurabilityWalEventCodec.ComputeSizeQueueDrain(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityWalEventCodec.EncodeQueueDrain(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            bytesAligned: 4096, frameCount: 8, out _);
        var d = DurabilityWalEventCodec.DecodeQueueDrain(buf);
        Assert.That(d.BytesAligned, Is.EqualTo(4096));
        Assert.That(d.FrameCount, Is.EqualTo(8));
        Assert.That(d.DurationTicks, Is.EqualTo(EndTs - StartTs));
    }

    [Test]
    public void DurabilityWalOsWrite_RoundTrip()
    {
        var size = DurabilityWalEventCodec.ComputeSizeOsWrite(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityWalEventCodec.EncodeOsWrite(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            bytesAligned: 8192, frameCount: 16, highLsn: 0x123456789ABCDEFL, out _);
        var d = DurabilityWalEventCodec.DecodeOsWrite(buf);
        Assert.That(d.BytesAligned, Is.EqualTo(8192));
        Assert.That(d.FrameCount, Is.EqualTo(16));
        Assert.That(d.HighLsn, Is.EqualTo(0x123456789ABCDEFL));
    }

    [Test]
    public void DurabilityWalSignal_RoundTrip()
    {
        var size = DurabilityWalEventCodec.ComputeSizeSignal(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityWalEventCodec.EncodeSignal(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            highLsn: 999_888_777L, out _);
        var d = DurabilityWalEventCodec.DecodeSignal(buf);
        Assert.That(d.HighLsn, Is.EqualTo(999_888_777L));
    }

    [Test]
    public void DurabilityWalGroupCommit_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DurabilityWalEventCodec.GroupCommitSize];
        DurabilityWalEventCodec.WriteGroupCommit(buf, ThreadSlot, StartTs, triggerMs: 5, producerThread: 1234);
        var d = DurabilityWalEventCodec.DecodeGroupCommit(buf);
        Assert.That(d.TriggerMs, Is.EqualTo(5));
        Assert.That(d.ProducerThread, Is.EqualTo(1234));
    }

    [Test]
    public void DurabilityWalQueue_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DurabilityWalEventCodec.QueueSize];
        DurabilityWalEventCodec.WriteQueue(buf, ThreadSlot, StartTs, drainAttempt: 1, dataLen: 50000, waitReason: 2);
        var d = DurabilityWalEventCodec.DecodeQueue(buf);
        Assert.That(d.DrainAttempt, Is.EqualTo(1));
        Assert.That(d.DataLen, Is.EqualTo(50000));
        Assert.That(d.WaitReason, Is.EqualTo(2));
    }

    [Test]
    public void DurabilityWalBuffer_RoundTrip()
    {
        var size = DurabilityWalEventCodec.ComputeSizeBuffer(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityWalEventCodec.EncodeBuffer(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            bytesAligned: 1024, pad: 32, out _);
        var d = DurabilityWalEventCodec.DecodeBuffer(buf);
        Assert.That(d.BytesAligned, Is.EqualTo(1024));
        Assert.That(d.Pad, Is.EqualTo(32));
    }

    [Test]
    public void DurabilityWalFrame_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DurabilityWalEventCodec.FrameSize];
        DurabilityWalEventCodec.WriteFrame(buf, ThreadSlot, StartTs, frameCount: 12, crcStart: 0xDEADBEEFu);
        var d = DurabilityWalEventCodec.DecodeFrame(buf);
        Assert.That(d.FrameCount, Is.EqualTo(12));
        Assert.That(d.CrcStart, Is.EqualTo(0xDEADBEEFu));
    }

    [Test]
    public void DurabilityWalBackpressure_RoundTrip()
    {
        var size = DurabilityWalEventCodec.ComputeSizeBackpressure(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityWalEventCodec.EncodeBackpressure(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            waitUs: 250_000u, producerThread: 5678, out _);
        var d = DurabilityWalEventCodec.DecodeBackpressure(buf);
        Assert.That(d.WaitUs, Is.EqualTo(250_000u));
        Assert.That(d.ProducerThread, Is.EqualTo(5678));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Durability:Checkpoint depth (kinds 222-224)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void DurabilityCheckpointWriteBatch_RoundTrip()
    {
        var size = DurabilityCheckpointEventCodec.ComputeSizeWriteBatch(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityCheckpointEventCodec.EncodeWriteBatch(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            writeBatchSize: 256, stagingAllocated: 1_048_576, out _);
        var d = DurabilityCheckpointEventCodec.DecodeWriteBatch(buf);
        Assert.That(d.WriteBatchSize, Is.EqualTo(256));
        Assert.That(d.StagingAllocated, Is.EqualTo(1_048_576));
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void DurabilityCheckpointBackpressure_RoundTrip(byte exhausted)
    {
        var size = DurabilityCheckpointEventCodec.ComputeSizeBackpressure(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityCheckpointEventCodec.EncodeBackpressure(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            waitMs: 500u, exhausted, out _);
        var d = DurabilityCheckpointEventCodec.DecodeBackpressure(buf);
        Assert.That(d.WaitMs, Is.EqualTo(500u));
        Assert.That(d.Exhausted, Is.EqualTo(exhausted));
    }

    [Test]
    public void DurabilityCheckpointSleep_RoundTrip()
    {
        var size = DurabilityCheckpointEventCodec.ComputeSizeSleep(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityCheckpointEventCodec.EncodeSleep(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            sleepMs: 1000u, wakeReason: 3, out _);
        var d = DurabilityCheckpointEventCodec.DecodeSleep(buf);
        Assert.That(d.SleepMs, Is.EqualTo(1000u));
        Assert.That(d.WakeReason, Is.EqualTo(3));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Durability:Recovery (kinds 225-232)
    // ─────────────────────────────────────────────────────────────────────

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void DurabilityRecoveryStart_RoundTrip(byte reason)
    {
        Span<byte> buf = stackalloc byte[DurabilityRecoveryEventCodec.StartSize];
        DurabilityRecoveryEventCodec.WriteStart(buf, ThreadSlot, StartTs, checkpointLsn: 12345L, reason);
        var d = DurabilityRecoveryEventCodec.DecodeStart(buf);
        Assert.That(d.CheckpointLsn, Is.EqualTo(12345L));
        Assert.That(d.Reason, Is.EqualTo(reason));
    }

    [Test]
    public void DurabilityRecoveryDiscover_RoundTrip()
    {
        var size = DurabilityRecoveryEventCodec.ComputeSizeDiscover(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityRecoveryEventCodec.EncodeDiscover(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            segCount: 42, totalBytes: 100_000_000L, firstSegId: 7, out _);
        var d = DurabilityRecoveryEventCodec.DecodeDiscover(buf);
        Assert.That(d.SegCount, Is.EqualTo(42));
        Assert.That(d.TotalBytes, Is.EqualTo(100_000_000L));
        Assert.That(d.FirstSegId, Is.EqualTo(7));
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void DurabilityRecoverySegment_RoundTrip(byte truncated)
    {
        var size = DurabilityRecoveryEventCodec.ComputeSizeSegment(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityRecoveryEventCodec.EncodeSegment(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            segId: 9, recCount: 5000, bytes: 1_048_576L, truncated, out _);
        var d = DurabilityRecoveryEventCodec.DecodeSegment(buf);
        Assert.That(d.SegId, Is.EqualTo(9));
        Assert.That(d.RecCount, Is.EqualTo(5000));
        Assert.That(d.Bytes, Is.EqualTo(1_048_576L));
        Assert.That(d.Truncated, Is.EqualTo(truncated));
    }

    [Test]
    public void DurabilityRecoveryRecord_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DurabilityRecoveryEventCodec.RecordSize];
        DurabilityRecoveryEventCodec.WriteRecord(buf, ThreadSlot, StartTs, chunkType: 5, lsn: 99_999_999L, size: 256);
        var d = DurabilityRecoveryEventCodec.DecodeRecord(buf);
        Assert.That(d.ChunkType, Is.EqualTo(5));
        Assert.That(d.Lsn, Is.EqualTo(99_999_999L));
        Assert.That(d.Size, Is.EqualTo(256));
    }

    [Test]
    public void DurabilityRecoveryFpi_RoundTrip()
    {
        var size = DurabilityRecoveryEventCodec.ComputeSizeFpi(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityRecoveryEventCodec.EncodeFpi(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            fpiCount: 100, repairedCount: 7, mismatches: 2, out _);
        var d = DurabilityRecoveryEventCodec.DecodeFpi(buf);
        Assert.That(d.FpiCount, Is.EqualTo(100));
        Assert.That(d.RepairedCount, Is.EqualTo(7));
        Assert.That(d.Mismatches, Is.EqualTo(2));
    }

    [Test]
    public void DurabilityRecoveryRedo_RoundTrip()
    {
        var size = DurabilityRecoveryEventCodec.ComputeSizeRedo(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityRecoveryEventCodec.EncodeRedo(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            recordsReplayed: 12345, uowsReplayed: 678, durUs: 50_000u, out _);
        var d = DurabilityRecoveryEventCodec.DecodeRedo(buf);
        Assert.That(d.RecordsReplayed, Is.EqualTo(12345));
        Assert.That(d.UowsReplayed, Is.EqualTo(678));
        Assert.That(d.DurUs, Is.EqualTo(50_000u));
    }

    [Test]
    public void DurabilityRecoveryUndo_RoundTrip()
    {
        var size = DurabilityRecoveryEventCodec.ComputeSizeUndo(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityRecoveryEventCodec.EncodeUndo(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            voidedUowCount: 4, out _);
        var d = DurabilityRecoveryEventCodec.DecodeUndo(buf);
        Assert.That(d.VoidedUowCount, Is.EqualTo(4));
    }

    [Test]
    public void DurabilityRecoveryTickFence_RoundTrip()
    {
        var size = DurabilityRecoveryEventCodec.ComputeSizeTickFence(hasTC: false);
        Span<byte> buf = stackalloc byte[size];
        DurabilityRecoveryEventCodec.EncodeTickFence(buf, EndTs, ThreadSlot, StartTs, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            tickFenceCount: 3, entries: 50, tickNumber: 1_000_000L, out _);
        var d = DurabilityRecoveryEventCodec.DecodeTickFence(buf);
        Assert.That(d.TickFenceCount, Is.EqualTo(3));
        Assert.That(d.Entries, Is.EqualTo(50));
        Assert.That(d.TickNumber, Is.EqualTo(1_000_000L));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Durability:UoW (kinds 233-234)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void DurabilityUowState_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[DurabilityUowEventCodec.StateSize];
        DurabilityUowEventCodec.WriteState(buf, ThreadSlot, StartTs, from: 1, to: 2, uowId: 100, reason: 0);
        var d = DurabilityUowEventCodec.DecodeState(buf);
        Assert.That(d.From, Is.EqualTo(1));
        Assert.That(d.To, Is.EqualTo(2));
        Assert.That(d.UowId, Is.EqualTo(100));
        Assert.That(d.Reason, Is.EqualTo(0));
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void DurabilityUowDeadline_RoundTrip(byte expired)
    {
        Span<byte> buf = stackalloc byte[DurabilityUowEventCodec.DeadlineSize];
        DurabilityUowEventCodec.WriteDeadline(buf, ThreadSlot, StartTs, deadline: 5000L, remaining: 2500L, expired);
        var d = DurabilityUowEventCodec.DecodeDeadline(buf);
        Assert.That(d.Deadline, Is.EqualTo(5000L));
        Assert.That(d.Remaining, Is.EqualTo(2500L));
        Assert.That(d.Expired, Is.EqualTo(expired));
    }

    // ─────────────────────────────────────────────────────────────────────
    // IsSpan classification
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void Phase8_IsSpan_ClassifiesCorrectly()
    {
        // Spans
        Assert.That(TraceEventKind.DurabilityWalQueueDrain.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityWalOsWrite.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityWalSignal.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityWalBuffer.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityWalBackpressure.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityCheckpointWriteBatch.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityCheckpointBackpressure.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityCheckpointSleep.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityRecoveryDiscover.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityRecoverySegment.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityRecoveryFpi.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityRecoveryRedo.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityRecoveryUndo.IsSpan(), Is.True);
        Assert.That(TraceEventKind.DurabilityRecoveryTickFence.IsSpan(), Is.True);
        // Instants
        Assert.That(TraceEventKind.DurabilityWalGroupCommit.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DurabilityWalQueue.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DurabilityWalFrame.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DurabilityRecoveryStart.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DurabilityRecoveryRecord.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DurabilityUowState.IsSpan(), Is.False);
        Assert.That(TraceEventKind.DurabilityUowDeadline.IsSpan(), Is.False);
    }
}

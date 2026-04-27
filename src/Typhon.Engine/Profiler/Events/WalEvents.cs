using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.WalFlush"/>. Required: <c>batchByteCount</c>, <c>frameCount</c>, <c>highLsn</c>.
/// </summary>
/// <remarks>
/// Payload: <c>[i32 batchByteCount][i32 frameCount][i64 highLsn]</c> = 16 bytes after the span header.
/// </remarks>
public ref struct WalFlushEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.WalFlush;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public int BatchByteCount;
    public int FrameCount;
    public long HighLsn;

    public readonly int ComputeSize()
        => WalEventCodec.ComputeSize(TraceEventKind.WalFlush, TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => WalEventCodec.Encode(destination, endTimestamp, TraceEventKind.WalFlush, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, BatchByteCount, FrameCount, HighLsn, 0, 0, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.WalSegmentRotate"/>. Required: <c>newSegmentIndex</c>.
/// </summary>
/// <remarks>
/// Payload: <c>[i32 newSegmentIndex]</c> = 4 bytes after the span header.
/// </remarks>
public ref struct WalSegmentRotateEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.WalSegmentRotate;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public int NewSegmentIndex;

    public readonly int ComputeSize()
        => WalEventCodec.ComputeSize(TraceEventKind.WalSegmentRotate, TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => WalEventCodec.Encode(destination, endTimestamp, TraceEventKind.WalSegmentRotate, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, 0, 0, 0, NewSegmentIndex, 0,
            out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.WalWait"/>. Required: <c>targetLsn</c>.
/// Emitted on the calling thread, not the WAL writer.
/// </summary>
/// <remarks>
/// Payload: <c>[i64 targetLsn]</c> = 8 bytes after the span header.
/// </remarks>
public ref struct WalWaitEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.WalWait;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long TargetLsn;

    public readonly int ComputeSize()
        => WalEventCodec.ComputeSize(TraceEventKind.WalWait, TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => WalEventCodec.Encode(destination, endTimestamp, TraceEventKind.WalWait, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, 0, 0, 0, 0, TargetLsn,
            out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


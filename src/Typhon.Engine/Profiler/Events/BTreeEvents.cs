using System;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// B+Tree insert span — no typed payload (the event kind alone carries the operation identity). Used for the insert path in <c>BTree.Add</c>.
/// </summary>
/// <remarks>
/// <b>Size:</b> 37 bytes without trace context, 53 bytes with — that's just the span header. The old fixed 64 B struct wasted 27 B per insert on
/// fields that were always zero for this event type. At ~1M inserts/sec during an AntHill spawn burst, that's 27 MB/sec of wasted ring buffer
/// reclaimed.
/// </remarks>
public ref struct BTreeInsertEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.BTreeInsert;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;

    public readonly int ComputeSize()
    {
        var hasTraceContext = TraceIdHi != 0 || TraceIdLo != 0;
        return TraceRecordHeader.SpanHeaderSize(hasTraceContext, SourceLocationId != 0);
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeInsert, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>B+Tree delete span — same no-payload shape as <see cref="BTreeInsertEvent"/>.</summary>
public ref struct BTreeDeleteEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.BTreeDelete;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0, SourceLocationId != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeDelete, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>B+Tree node split span — same no-payload shape.</summary>
public ref struct BTreeNodeSplitEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.BTreeNodeSplit;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0, SourceLocationId != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeNodeSplit, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>B+Tree node merge span — same no-payload shape.</summary>
public ref struct BTreeNodeMergeEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.BTreeNodeMerge;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0, SourceLocationId != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeNodeMerge, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


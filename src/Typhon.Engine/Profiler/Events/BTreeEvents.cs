using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Data-class representation of a decoded B+Tree span event — used by readers/tests/viewer. Plain <c>readonly struct</c> so it can flow through
/// code that doesn't support ref structs (assertion libraries, dictionaries, collection APIs).
/// </summary>
public readonly struct BTreeEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    public BTreeEventData(TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
    }

    /// <summary><c>true</c> when <see cref="TraceIdHi"/> and <see cref="TraceIdLo"/> are non-zero (the record carried distributed-trace context).</summary>
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
}

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
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public readonly int ComputeSize()
    {
        var hasTraceContext = TraceIdHi != 0 || TraceIdLo != 0;
        return TraceRecordHeader.SpanHeaderSize(hasTraceContext);
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeInsert, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>B+Tree delete span — same no-payload shape as <see cref="BTreeInsertEvent"/>.</summary>
public ref struct BTreeDeleteEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeDelete, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>B+Tree node split span — same no-payload shape.</summary>
public ref struct BTreeNodeSplitEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeNodeSplit, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>B+Tree node merge span — same no-payload shape.</summary>
public ref struct BTreeNodeMergeEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => BTreeEventCodec.EncodeNoPayload(destination, endTimestamp, TraceEventKind.BTreeNodeMerge, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Shared encode/decode for the four B+Tree event kinds. All four have identical wire shape (span header only, no typed payload); only the kind
/// byte in the header differs. Decode yields <see cref="BTreeEventData"/>.
/// </summary>
public static class BTreeEventCodec
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeNoPayload(
        Span<byte> destination,
        long endTimestamp,
        TraceEventKind kind,
        byte threadSlot,
        long startTimestamp,
        ulong spanId,
        ulong parentSpanId,
        ulong traceIdHi,
        ulong traceIdLo,
        out int bytesWritten)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks: endTimestamp - startTimestamp,
            spanId: spanId,
            parentSpanId: parentSpanId,
            spanFlags: spanFlags);

        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        bytesWritten = size;
    }

    /// <summary>
    /// Decode a B+Tree event record. Works for any of the 4 B+Tree kinds — the caller can use <see cref="BTreeEventData.Kind"/> to disambiguate.
    /// </summary>
    public static BTreeEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        ulong traceIdHi = 0, traceIdLo = 0;
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        return new BTreeEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo);
    }
}

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

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
    /// <summary>Compile-time site id (0 = absent / not attributed). See `claude/design/observability/09-profiler-source-attribution.md`.</summary>
    public ushort SourceLocationId { get; }

    public BTreeEventData(TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId = 0)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
    }

    /// <summary><c>true</c> when <see cref="TraceIdHi"/> and <see cref="TraceIdLo"/> are non-zero (the record carried distributed-trace context).</summary>
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    /// <summary><c>true</c> when <see cref="SourceLocationId"/> is non-zero (the record was emitted via an intercepted call site).</summary>
    public bool HasSourceLocation => SourceLocationId != 0;
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
        out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks: endTimestamp - startTimestamp,
            spanId: spanId,
            parentSpanId: parentSpanId,
            spanFlags: spanFlags);

        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
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

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        ushort sourceLocationId = 0;
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        return new BTreeEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId);
    }
}


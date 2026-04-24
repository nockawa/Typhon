using System;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.GcSuspension"/> record.
/// </summary>
public readonly struct GcSuspensionData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public GcSuspendReason Reason { get; }
    public byte OptMask { get; }

    public GcSuspensionData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId, GcSuspendReason reason, byte optMask)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        Reason = reason;
        OptMask = optMask;
    }
}

/// <summary>
/// Wire-format codec for <see cref="TraceEventKind.GcSuspension"/>. Direct writer (no ref-struct Begin/Dispose) because open / close are separated
/// by intervening ETW events and held as <c>GcIngestionThread</c> state.
/// </summary>
public static class GcSuspensionEventCodec
{
    /// <summary>
    /// Fixed wire size: 12 B common header + 25 B span header extension + 1 B reason + 1 B optMask = 39 B. No trace context (always zero).
    /// </summary>
    public const int Size = TraceRecordHeader.CommonHeaderSize + TraceRecordHeader.SpanHeaderExtensionSize + 2;

    /// <summary>
    /// Encode a <see cref="TraceEventKind.GcSuspension"/> record. <paramref name="parentSpanId"/> should be 0 (process-level).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> destination, byte threadSlot, long startTimestamp, long endTimestamp, ulong spanId, ulong parentSpanId, 
        GcSuspendReason reason, out int bytesWritten)
    {
        TraceRecordHeader.WriteCommonHeader(destination, Size, TraceEventKind.GcSuspension, threadSlot, startTimestamp);
        TraceRecordHeader.WriteSpanHeaderExtension(
            destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks: endTimestamp - startTimestamp,
            spanId: spanId,
            parentSpanId: parentSpanId,
            spanFlags: 0);
        var payloadOffset = TraceRecordHeader.CommonHeaderSize + TraceRecordHeader.SpanHeaderExtensionSize;
        destination[payloadOffset] = (byte)reason;
        destination[payloadOffset + 1] = 0;
        bytesWritten = Size;
    }

    /// <summary>Decode a <see cref="TraceEventKind.GcSuspension"/> record.</summary>
    public static GcSuspensionData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var startTimestamp);
        if (kind != TraceEventKind.GcSuspension)
        {
            throw new ArgumentException($"Expected GcSuspension, got {kind}", nameof(source));
        }
        TraceRecordHeader.ReadSpanHeaderExtension(
            source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out _);
        var payloadOffset = TraceRecordHeader.CommonHeaderSize + TraceRecordHeader.SpanHeaderExtensionSize;
        return new GcSuspensionData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, reason: (GcSuspendReason)source[payloadOffset], 
            optMask: source[payloadOffset + 1]);
    }
}

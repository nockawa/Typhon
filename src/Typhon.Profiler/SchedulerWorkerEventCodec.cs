using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Worker Idle span. Payload: <c>workerId u8, spinCount u16, idleUs u32</c> (7 B).</summary>
[PublicAPI]
public readonly struct SchedulerWorkerIdleData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte WorkerId { get; }
    public ushort SpinCount { get; }
    public uint IdleUs { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SchedulerWorkerIdleData(byte threadSlot, long startTimestamp, long durationTicks, byte workerId, ushort spinCount, uint idleUs, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; WorkerId = workerId; SpinCount = spinCount; IdleUs = idleUs; SourceLocationId = srcLoc; }
}

/// <summary>Decoded Worker Wake instant. Payload: <c>workerId u8, delayUs u32</c> (5 B).</summary>
[PublicAPI]
public readonly struct SchedulerWorkerWakeData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte WorkerId { get; }
    public uint DelayUs { get; }
    public SchedulerWorkerWakeData(byte threadSlot, long timestamp, byte workerId, uint delayUs)
    { ThreadSlot = threadSlot; Timestamp = timestamp; WorkerId = workerId; DelayUs = delayUs; }
}

/// <summary>Decoded Worker BetweenTick span. Payload: <c>workerId u8, waitUs u32, wakeReason u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct SchedulerWorkerBetweenTickData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte WorkerId { get; }
    public uint WaitUs { get; }
    public byte WakeReason { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SchedulerWorkerBetweenTickData(byte threadSlot, long startTimestamp, long durationTicks, byte workerId, uint waitUs, byte wakeReason, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; WorkerId = workerId; WaitUs = waitUs; WakeReason = wakeReason; SourceLocationId = srcLoc; }
}

/// <summary>Wire codec for Scheduler:Worker events (kinds 150-152).</summary>
public static class SchedulerWorkerEventCodec
{
    private const int IdlePayload = 1 + 2 + 4;                                    // 7
    public const int WakeSize = TraceRecordHeader.CommonHeaderSize + 1 + 4;       // 17
    private const int BetweenTickPayload = 1 + 4 + 1;                             // 6

    public static int ComputeSizeIdle(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + IdlePayload;
    public static int ComputeSizeBetweenTick(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + BetweenTickPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTraceContext, ushort sourceLocationId)
    {
        var hasSourceLocation = sourceLocationId != 0;
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
    }

    public static void EncodeIdle(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        byte workerId, ushort spinCount, uint idleUs, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeIdle(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.SchedulerWorkerIdle, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        p[0] = workerId;
        BinaryPrimitives.WriteUInt16LittleEndian(p[1..], spinCount);
        BinaryPrimitives.WriteUInt32LittleEndian(p[3..], idleUs);
        bytesWritten = size;
    }

    public static SchedulerWorkerIdleData DecodeIdle(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
        }
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        return new SchedulerWorkerIdleData(threadSlot, startTimestamp, durationTicks,
            p[0], BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), BinaryPrimitives.ReadUInt32LittleEndian(p[3..]), sourceLocationId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteWake(Span<byte> destination, byte threadSlot, long timestamp, byte workerId, uint delayUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, WakeSize, TraceEventKind.SchedulerWorkerWake, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = workerId;
        BinaryPrimitives.WriteUInt32LittleEndian(p[1..], delayUs);
    }

    public static SchedulerWorkerWakeData DecodeWake(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerWorkerWakeData(threadSlot, timestamp, p[0], BinaryPrimitives.ReadUInt32LittleEndian(p[1..]));
    }

    public static void EncodeBetweenTick(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        byte workerId, uint waitUs, byte wakeReason, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeBetweenTick(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.SchedulerWorkerBetweenTick, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        p[0] = workerId;
        BinaryPrimitives.WriteUInt32LittleEndian(p[1..], waitUs);
        p[5] = wakeReason;
        bytesWritten = size;
    }

    public static SchedulerWorkerBetweenTickData DecodeBetweenTick(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
        }
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        return new SchedulerWorkerBetweenTickData(threadSlot, startTimestamp, durationTicks,
            p[0], BinaryPrimitives.ReadUInt32LittleEndian(p[1..]), p[5], sourceLocationId);
    }
}

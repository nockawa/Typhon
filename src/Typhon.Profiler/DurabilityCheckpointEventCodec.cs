using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

[PublicAPI]
public readonly struct DurabilityCheckpointWriteBatchData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int WriteBatchSize { get; }
    public int StagingAllocated { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityCheckpointWriteBatchData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int bs, int sa)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; WriteBatchSize = bs; StagingAllocated = sa; }
}

[PublicAPI]
public readonly struct DurabilityCheckpointBackpressureData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint WaitMs { get; }
    public byte Exhausted { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityCheckpointBackpressureData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, uint wm, byte ex)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; WaitMs = wm; Exhausted = ex; }
}

[PublicAPI]
public readonly struct DurabilityCheckpointSleepData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint SleepMs { get; }
    public byte WakeReason { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityCheckpointSleepData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, uint sm, byte wr)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; SleepMs = sm; WakeReason = wr; }
}

public static class DurabilityCheckpointEventCodec
{
    private const int WriteBatchPayload   = 4 + 4;     // 8
    private const int BackpressurePayload = 4 + 1;     // 5
    private const int SleepPayload        = 4 + 1;     // 5

    public static int ComputeSizeWriteBatch(bool hasTC)   => TraceRecordHeader.SpanHeaderSize(hasTC) + WriteBatchPayload;
    public static int ComputeSizeBackpressure(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + BackpressurePayload;
    public static int ComputeSizeSleep(bool hasTC)        => TraceRecordHeader.SpanHeaderSize(hasTC) + SleepPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTC)
    {
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTC ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTC)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadSpanPreamble(ReadOnlySpan<byte> source,
        out byte threadSlot, out long startTs, out long dur, out ulong spanId, out ulong parentSpanId,
        out ulong traceIdHi, out ulong traceIdLo)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out threadSlot, out startTs);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out dur, out spanId, out parentSpanId, out var spanFlags);
        traceIdHi = 0; traceIdLo = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        return source[TraceRecordHeader.SpanHeaderSize(hasTC)..];
    }

    public static void EncodeWriteBatch(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, int writeBatchSize, int stagingAllocated, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeWriteBatch(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.DurabilityCheckpointWriteBatch, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteInt32LittleEndian(p, writeBatchSize);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], stagingAllocated);
        bytesWritten = size;
    }

    public static DurabilityCheckpointWriteBatchData DecodeWriteBatch(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityCheckpointWriteBatchData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    public static void EncodeBackpressure(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, uint waitMs, byte exhausted, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeBackpressure(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.DurabilityCheckpointBackpressure, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, waitMs);
        p[4] = exhausted;
        bytesWritten = size;
    }

    public static DurabilityCheckpointBackpressureData DecodeBackpressure(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityCheckpointBackpressureData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p), p[4]);
    }

    public static void EncodeSleep(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, uint sleepMs, byte wakeReason, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeSleep(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.DurabilityCheckpointSleep, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, sleepMs);
        p[4] = wakeReason;
        bytesWritten = size;
    }

    public static DurabilityCheckpointSleepData DecodeSleep(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityCheckpointSleepData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p), p[4]);
    }
}

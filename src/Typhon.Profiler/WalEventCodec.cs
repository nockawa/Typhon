using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a WAL span event (<see cref="TraceEventKind.WalFlush"/>, <see cref="TraceEventKind.WalSegmentRotate"/>,
/// or <see cref="TraceEventKind.WalWait"/>). Which payload fields are valid depends on the <see cref="Kind"/> — see the per-event
/// ref structs for the contract.
/// </summary>
public readonly struct WalEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required for <see cref="TraceEventKind.WalFlush"/> — total bytes written in this flush cycle. Zero for other kinds.</summary>
    public int BatchByteCount { get; }

    /// <summary>Required for <see cref="TraceEventKind.WalFlush"/> — number of WAL frames written. Zero for other kinds.</summary>
    public int FrameCount { get; }

    /// <summary>Required for <see cref="TraceEventKind.WalFlush"/> — highest LSN in this flush batch. Zero for other kinds.</summary>
    public long HighLsn { get; }

    /// <summary>Required for <see cref="TraceEventKind.WalSegmentRotate"/> — index of the newly activated WAL segment. Zero for other kinds.</summary>
    public int NewSegmentIndex { get; }

    /// <summary>Required for <see cref="TraceEventKind.WalWait"/> — the LSN the caller is blocking for. Zero for other kinds.</summary>
    public long TargetLsn { get; }

    /// <summary><c>true</c> when <see cref="TraceIdHi"/> and <see cref="TraceIdLo"/> are non-zero (the record carried distributed-trace context).</summary>
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public WalEventData(
        TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int batchByteCount, int frameCount, long highLsn,
        int newSegmentIndex, long targetLsn)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        BatchByteCount = batchByteCount;
        FrameCount = frameCount;
        HighLsn = highLsn;
        NewSegmentIndex = newSegmentIndex;
        TargetLsn = targetLsn;
    }
}

/// <summary>
/// Shared wire codec for the three WAL event kinds. All three share the same span header; only the per-kind payload differs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Payload layout for <see cref="TraceEventKind.WalFlush"/> (after span header):</b>
/// <code>
/// [i32 batchByteCount]  // 4 B
/// [i32 frameCount]      // 4 B
/// [i64 highLsn]         // 8 B
/// </code>
/// Total payload: 16 B.
/// </para>
/// <para>
/// <b>Payload layout for <see cref="TraceEventKind.WalSegmentRotate"/> (after span header):</b>
/// <code>
/// [i32 newSegmentIndex] // 4 B
/// </code>
/// Total payload: 4 B.
/// </para>
/// <para>
/// <b>Payload layout for <see cref="TraceEventKind.WalWait"/> (after span header):</b>
/// <code>
/// [i64 targetLsn]       // 8 B
/// </code>
/// Total payload: 8 B.
/// </para>
/// </remarks>
public static class WalEventCodec
{
    private const int BatchByteCountSize = 4;
    private const int FrameCountSize = 4;
    private const int HighLsnSize = 8;
    private const int FlushPayloadSize = BatchByteCountSize + FrameCountSize + HighLsnSize; // 16

    private const int NewSegmentIndexSize = 4;
    private const int SegmentRotatePayloadSize = NewSegmentIndexSize; // 4

    private const int TargetLsnSize = 8;
    private const int WaitPayloadSize = TargetLsnSize; // 8

    /// <summary>Compute total record size for a WAL event of the given <paramref name="kind"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(TraceEventKind kind, bool hasTraceContext)
    {
        var payloadSize = kind switch
        {
            TraceEventKind.WalFlush => FlushPayloadSize,
            TraceEventKind.WalSegmentRotate => SegmentRotatePayloadSize,
            TraceEventKind.WalWait => WaitPayloadSize,
            _ => 0,
        };
        return TraceRecordHeader.SpanHeaderSize(hasTraceContext) + payloadSize;
    }

    /// <summary>
    /// Encode a WAL event record. The <paramref name="kind"/> determines which payload fields are written — unused fields are ignored.
    /// </summary>
    internal static void Encode(
        Span<byte> destination,
        long endTimestamp,
        TraceEventKind kind,
        byte threadSlot,
        long startTimestamp,
        ulong spanId,
        ulong parentSpanId,
        ulong traceIdHi,
        ulong traceIdLo,
        int batchByteCount,
        int frameCount,
        long highLsn,
        int newSegmentIndex,
        long targetLsn,
        out int bytesWritten)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSize(kind, hasTraceContext);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        var payload = destination[headerSize..];
        switch (kind)
        {
            case TraceEventKind.WalFlush:
                BinaryPrimitives.WriteInt32LittleEndian(payload, batchByteCount);
                BinaryPrimitives.WriteInt32LittleEndian(payload[BatchByteCountSize..], frameCount);
                BinaryPrimitives.WriteInt64LittleEndian(payload[(BatchByteCountSize + FrameCountSize)..], highLsn);
                break;

            case TraceEventKind.WalSegmentRotate:
                BinaryPrimitives.WriteInt32LittleEndian(payload, newSegmentIndex);
                break;

            case TraceEventKind.WalWait:
                BinaryPrimitives.WriteInt64LittleEndian(payload, targetLsn);
                break;
        }

        bytesWritten = size;
    }

    /// <summary>
    /// Decode a WAL event record. Works for any of the three WAL kinds — the caller can use <see cref="WalEventData.Kind"/> to disambiguate.
    /// </summary>
    public static WalEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        ulong traceIdHi = 0, traceIdLo = 0;
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0);
        var payload = source[headerSize..];

        int batchByteCount = 0, frameCount = 0, newSegmentIndex = 0;
        long highLsn = 0, targetLsn = 0;

        switch (kind)
        {
            case TraceEventKind.WalFlush:
                batchByteCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
                frameCount = BinaryPrimitives.ReadInt32LittleEndian(payload[BatchByteCountSize..]);
                highLsn = BinaryPrimitives.ReadInt64LittleEndian(payload[(BatchByteCountSize + FrameCountSize)..]);
                break;

            case TraceEventKind.WalSegmentRotate:
                newSegmentIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
                break;

            case TraceEventKind.WalWait:
                targetLsn = BinaryPrimitives.ReadInt64LittleEndian(payload);
                break;
        }

        return new WalEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            batchByteCount, frameCount, highLsn, newSegmentIndex, targetLsn);
    }
}


using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of a <see cref="TraceEventKind.StatisticsRebuild"/> event.</summary>
public readonly struct StatisticsRebuildEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required — total entity count in the component table at rebuild time.</summary>
    public int EntityCount { get; }

    /// <summary>Required — number of mutations since last statistics rebuild.</summary>
    public int MutationCount { get; }

    /// <summary>Required — sampling interval used for the rebuild (e.g. every N-th entity).</summary>
    public int SamplingInterval { get; }

    /// <summary>Compile-time site id (0 = absent / not attributed).</summary>
    public ushort SourceLocationId { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public bool HasSourceLocation => SourceLocationId != 0;

    public StatisticsRebuildEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, int entityCount, int mutationCount, int samplingInterval, ushort sourceLocationId = 0)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        EntityCount = entityCount;
        MutationCount = mutationCount;
        SamplingInterval = samplingInterval;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.StatisticsRebuild"/>. Payload: <c>i32 entityCount</c>, <c>i32 mutationCount</c>, <c>i32 samplingInterval</c>.</summary>
public static class StatisticsRebuildEventCodec
{
    private const int EntityCountSize = 4;
    private const int MutationCountSize = 4;
    private const int SamplingIntervalSize = 4;
    private const int PayloadSize = EntityCountSize + MutationCountSize + SamplingIntervalSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    internal static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int entityCount, int mutationCount, int samplingInterval, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSize(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.StatisticsRebuild, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var payload = destination[headerSize..];
        BinaryPrimitives.WriteInt32LittleEndian(payload, entityCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload[EntityCountSize..], mutationCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload[(EntityCountSize + MutationCountSize)..], samplingInterval);

        bytesWritten = size;
    }

    public static StatisticsRebuildEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var entityCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var mutationCount = BinaryPrimitives.ReadInt32LittleEndian(payload[EntityCountSize..]);
        var samplingInterval = BinaryPrimitives.ReadInt32LittleEndian(payload[(EntityCountSize + MutationCountSize)..]);

        return new StatisticsRebuildEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            entityCount, mutationCount, samplingInterval, sourceLocationId);
    }
}


using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Profiler;

/// <summary>Decoded form of a <see cref="TraceEventKind.ClusterMigration"/> event.</summary>
public readonly struct ClusterMigrationEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required — archetype ID whose entities are being migrated between spatial cells.</summary>
    public ushort ArchetypeId { get; }

    /// <summary>Required — number of entities migrated this call.</summary>
    public int MigrationCount { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public ClusterMigrationEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort archetypeId, int migrationCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        ArchetypeId = archetypeId;
        MigrationCount = migrationCount;
    }
}

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.ClusterMigration"/>. Two required fields, no optionals.
/// </summary>
public ref struct ClusterMigrationEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeId;
    public int MigrationCount;

    public readonly int ComputeSize()
        => ClusterMigrationEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => ClusterMigrationEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeId, MigrationCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Wire codec for <see cref="TraceEventKind.ClusterMigration"/>. Payload: <c>u16 archetypeId</c>, <c>i32 migrationCount</c>.</summary>
public static class ClusterMigrationEventCodec
{
    private const int ArchetypeIdSize = 2;
    private const int MigrationCountSize = 4;
    private const int PayloadSize = ArchetypeIdSize + MigrationCountSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    internal static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort archetypeId, int migrationCount, out int bytesWritten)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSize(hasTraceContext);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.ClusterMigration, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        var payload = destination[headerSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, archetypeId);
        BinaryPrimitives.WriteInt32LittleEndian(payload[ArchetypeIdSize..], migrationCount);

        bytesWritten = size;
    }

    public static ClusterMigrationEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        ulong traceIdHi = 0, traceIdLo = 0;
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0);
        var payload = source[headerSize..];
        var archetypeId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var migrationCount = BinaryPrimitives.ReadInt32LittleEndian(payload[ArchetypeIdSize..]);

        return new ClusterMigrationEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            archetypeId, migrationCount);
    }
}

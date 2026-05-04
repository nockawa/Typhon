using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Maintain Insert span. Payload: <c>entityPK i64, componentTypeId u16, didDegenerate u8</c> (11 B).</summary>
[PublicAPI]
public readonly struct SpatialMaintainInsertData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public long EntityPK { get; }
    public ushort ComponentTypeId { get; }
    public byte DidDegenerate { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialMaintainInsertData(byte threadSlot, long startTimestamp, long durationTicks, long entityPK, ushort componentTypeId, byte didDegenerate, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; EntityPK = entityPK; ComponentTypeId = componentTypeId; DidDegenerate = didDegenerate; SourceLocationId = srcLoc; }
}

/// <summary>Decoded Maintain UpdateSlowPath span. Payload: <c>entityPK i64, componentTypeId u16, escapeDistSq f32</c> (14 B).</summary>
[PublicAPI]
public readonly struct SpatialMaintainUpdateSlowPathData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public long EntityPK { get; }
    public ushort ComponentTypeId { get; }
    public float EscapeDistSq { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialMaintainUpdateSlowPathData(byte threadSlot, long startTimestamp, long durationTicks, long entityPK, ushort componentTypeId, float escapeDistSq, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; EntityPK = entityPK; ComponentTypeId = componentTypeId; EscapeDistSq = escapeDistSq; SourceLocationId = srcLoc; }
}

/// <summary>Decoded Maintain AabbValidate instant. Payload: <c>entityPK i64, componentTypeId u16, opcode u8</c> (11 B).</summary>
[PublicAPI]
public readonly struct SpatialMaintainAabbValidateData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long EntityPK { get; }
    public ushort ComponentTypeId { get; }
    public byte Opcode { get; }

    public SpatialMaintainAabbValidateData(byte threadSlot, long timestamp, long entityPK, ushort componentTypeId, byte opcode)
    { ThreadSlot = threadSlot; Timestamp = timestamp; EntityPK = entityPK; ComponentTypeId = componentTypeId; Opcode = opcode; }
}

/// <summary>Decoded Maintain BackPointerWrite instant. Payload: <c>componentChunkId i32, leafChunkId i32, slotIndex u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct SpatialMaintainBackPointerWriteData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int ComponentChunkId { get; }
    public int LeafChunkId { get; }
    public ushort SlotIndex { get; }

    public SpatialMaintainBackPointerWriteData(byte threadSlot, long timestamp, int componentChunkId, int leafChunkId, ushort slotIndex)
    { ThreadSlot = threadSlot; Timestamp = timestamp; ComponentChunkId = componentChunkId; LeafChunkId = leafChunkId; SlotIndex = slotIndex; }
}

/// <summary>
/// Wire codec for Spatial Maintain events: Insert/UpdateSlowPath spans (kinds 138-139) + AabbValidate/BackPointerWrite instants (kinds 140-141).
/// </summary>
public static class SpatialMaintainEventCodec
{
    private const int InsertPayload = 8 + 2 + 1;            // 11
    private const int UpdateSlowPathPayload = 8 + 2 + 4;    // 14
    public const int AabbValidateSize = TraceRecordHeader.CommonHeaderSize + 8 + 2 + 1;       // 23
    public const int BackPointerWriteSize = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 2;   // 22

    public static int ComputeSizeInsert(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + InsertPayload;
    public static int ComputeSizeUpdateSlowPath(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + UpdateSlowPathPayload;

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

    public static void EncodeInsert(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        long entityPK, ushort componentTypeId, byte didDegenerate, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeInsert(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.SpatialMaintainInsert, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, entityPK);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], componentTypeId);
        p[10] = didDegenerate;
        bytesWritten = size;
    }

    public static SpatialMaintainInsertData DecodeInsert(ReadOnlySpan<byte> source)
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
        return new SpatialMaintainInsertData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            p[10], sourceLocationId);
    }

    public static void EncodeUpdateSlowPath(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        long entityPK, ushort componentTypeId, float escapeDistSq, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeUpdateSlowPath(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.SpatialMaintainUpdateSlowPath, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, entityPK);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], componentTypeId);
        BinaryPrimitives.WriteSingleLittleEndian(p[10..], escapeDistSq);
        bytesWritten = size;
    }

    public static SpatialMaintainUpdateSlowPathData DecodeUpdateSlowPath(ReadOnlySpan<byte> source)
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
        return new SpatialMaintainUpdateSlowPathData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[10..]), sourceLocationId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAabbValidate(Span<byte> destination, byte threadSlot, long timestamp, long entityPK, ushort componentTypeId, byte opcode)
    {
        TraceRecordHeader.WriteCommonHeader(destination, AabbValidateSize, TraceEventKind.SpatialMaintainAabbValidate, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, entityPK);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], componentTypeId);
        p[10] = opcode;
    }

    public static SpatialMaintainAabbValidateData DecodeAabbValidate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialMaintainAabbValidateData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            p[10]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBackPointerWrite(Span<byte> destination, byte threadSlot, long timestamp, int componentChunkId, int leafChunkId, ushort slotIndex)
    {
        TraceRecordHeader.WriteCommonHeader(destination, BackPointerWriteSize, TraceEventKind.SpatialMaintainBackPointerWrite, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, componentChunkId);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], leafChunkId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], slotIndex);
    }

    public static SpatialMaintainBackPointerWriteData DecodeBackPointerWrite(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialMaintainBackPointerWriteData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
    }
}

using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Segment Create/Load instant. Payload: <c>segmentId i32, pageCount i32</c> (8 B).</summary>
[PublicAPI]
public readonly struct StorageSegmentCreateLoadData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int SegmentId { get; }
    public int PageCount { get; }
    public StorageSegmentCreateLoadData(TraceEventKind kind, byte threadSlot, long timestamp, int segmentId, int pageCount)
    { Kind = kind; ThreadSlot = threadSlot; Timestamp = timestamp; SegmentId = segmentId; PageCount = pageCount; }
}

/// <summary>Decoded Segment Grow instant. Payload: <c>segmentId i32, oldLen i32, newLen i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct StorageSegmentGrowData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int SegmentId { get; }
    public int OldLen { get; }
    public int NewLen { get; }
    public StorageSegmentGrowData(byte threadSlot, long timestamp, int segmentId, int oldLen, int newLen)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SegmentId = segmentId; OldLen = oldLen; NewLen = newLen; }
}

/// <summary>Wire codec for Storage:Segment events (kinds 166/167/168).</summary>
public static class StorageSegmentEventCodec
{
    public const int CreateLoadSize = TraceRecordHeader.CommonHeaderSize + 4 + 4;       // 20
    public const int GrowSize       = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 4;   // 24

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCreate(Span<byte> destination, byte threadSlot, long timestamp, int segmentId, int pageCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, CreateLoadSize, TraceEventKind.StorageSegmentCreate, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, segmentId);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], pageCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLoad(Span<byte> destination, byte threadSlot, long timestamp, int segmentId, int pageCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, CreateLoadSize, TraceEventKind.StorageSegmentLoad, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, segmentId);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], pageCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGrow(Span<byte> destination, byte threadSlot, long timestamp, int segmentId, int oldLen, int newLen)
    {
        TraceRecordHeader.WriteCommonHeader(destination, GrowSize, TraceEventKind.StorageSegmentGrow, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, segmentId);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], oldLen);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], newLen);
    }

    public static StorageSegmentCreateLoadData DecodeCreateOrLoad(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new StorageSegmentCreateLoadData(kind, threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    public static StorageSegmentGrowData DecodeGrow(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new StorageSegmentGrowData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }
}

using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Cell:Index Add instant. Payload: <c>cellKey i32, slot i32, clusterChunkId i32, capacity i32</c> (16 B).</summary>
[PublicAPI]
public readonly struct SpatialCellIndexAddData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int CellKey { get; }
    public int Slot { get; }
    public int ClusterChunkId { get; }
    public int Capacity { get; }

    public SpatialCellIndexAddData(byte threadSlot, long timestamp, int cellKey, int slot, int clusterChunkId, int capacity)
    { ThreadSlot = threadSlot; Timestamp = timestamp; CellKey = cellKey; Slot = slot; ClusterChunkId = clusterChunkId; Capacity = capacity; }
}

/// <summary>Decoded Cell:Index Update instant. Payload: <c>cellKey i32, slot i32</c> (8 B).</summary>
[PublicAPI]
public readonly struct SpatialCellIndexUpdateData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int CellKey { get; }
    public int Slot { get; }

    public SpatialCellIndexUpdateData(byte threadSlot, long timestamp, int cellKey, int slot)
    { ThreadSlot = threadSlot; Timestamp = timestamp; CellKey = cellKey; Slot = slot; }
}

/// <summary>Decoded Cell:Index Remove instant. Payload: <c>cellKey i32, slot i32, swappedClusterId i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct SpatialCellIndexRemoveData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int CellKey { get; }
    public int Slot { get; }
    public int SwappedClusterId { get; }

    public SpatialCellIndexRemoveData(byte threadSlot, long timestamp, int cellKey, int slot, int swappedClusterId)
    { ThreadSlot = threadSlot; Timestamp = timestamp; CellKey = cellKey; Slot = slot; SwappedClusterId = swappedClusterId; }
}

/// <summary>Wire codec for Spatial Cell:Index instant records (kinds 130-132).</summary>
public static class SpatialCellIndexEventCodec
{
    public const int AddSize = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 4 + 4;       // 28
    public const int UpdateSize = TraceRecordHeader.CommonHeaderSize + 4 + 4;            // 20
    public const int RemoveSize = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 4;        // 24

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAdd(Span<byte> destination, byte threadSlot, long timestamp, int cellKey, int slot, int clusterChunkId, int capacity)
    {
        TraceRecordHeader.WriteCommonHeader(destination, AddSize, TraceEventKind.SpatialCellIndexAdd, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, cellKey);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], slot);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], clusterChunkId);
        BinaryPrimitives.WriteInt32LittleEndian(p[12..], capacity);
    }

    public static SpatialCellIndexAddData DecodeAdd(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialCellIndexAddData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[12..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUpdate(Span<byte> destination, byte threadSlot, long timestamp, int cellKey, int slot)
    {
        TraceRecordHeader.WriteCommonHeader(destination, UpdateSize, TraceEventKind.SpatialCellIndexUpdate, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, cellKey);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], slot);
    }

    public static SpatialCellIndexUpdateData DecodeUpdate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialCellIndexUpdateData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRemove(Span<byte> destination, byte threadSlot, long timestamp, int cellKey, int slot, int swappedClusterId)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RemoveSize, TraceEventKind.SpatialCellIndexRemove, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, cellKey);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], slot);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], swappedClusterId);
    }

    public static SpatialCellIndexRemoveData DecodeRemove(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialCellIndexRemoveData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }
}

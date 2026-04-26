using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Grid CellTierChange instant. Payload: <c>cellKey i32, oldTier u8, newTier u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct SpatialGridCellTierChangeData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int CellKey { get; }
    public byte OldTier { get; }
    public byte NewTier { get; }

    public SpatialGridCellTierChangeData(byte threadSlot, long timestamp, int cellKey, byte oldTier, byte newTier)
    { ThreadSlot = threadSlot; Timestamp = timestamp; CellKey = cellKey; OldTier = oldTier; NewTier = newTier; }
}

/// <summary>Decoded Grid OccupancyChange instant. Payload: <c>cellKey i32, delta i8, occBefore u16, occAfter u16</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialGridOccupancyChangeData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int CellKey { get; }
    public sbyte Delta { get; }
    public ushort OccBefore { get; }
    public ushort OccAfter { get; }

    public SpatialGridOccupancyChangeData(byte threadSlot, long timestamp, int cellKey, sbyte delta, ushort occBefore, ushort occAfter)
    { ThreadSlot = threadSlot; Timestamp = timestamp; CellKey = cellKey; Delta = delta; OccBefore = occBefore; OccAfter = occAfter; }
}

/// <summary>Decoded Grid ClusterCellAssign instant. Payload: <c>clusterChunkId i32, cellKey i32, archetypeId u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct SpatialGridClusterCellAssignData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int ClusterChunkId { get; }
    public int CellKey { get; }
    public ushort ArchetypeId { get; }

    public SpatialGridClusterCellAssignData(byte threadSlot, long timestamp, int clusterChunkId, int cellKey, ushort archetypeId)
    { ThreadSlot = threadSlot; Timestamp = timestamp; ClusterChunkId = clusterChunkId; CellKey = cellKey; ArchetypeId = archetypeId; }
}

/// <summary>Wire codec for Spatial Grid instant records (kinds 127-129). Instants: 12-byte common header + payload.</summary>
public static class SpatialGridEventCodec
{
    public const int CellTierChangeSize = TraceRecordHeader.CommonHeaderSize + 4 + 1 + 1;       // 18
    public const int OccupancyChangeSize = TraceRecordHeader.CommonHeaderSize + 4 + 1 + 2 + 2;  // 21
    public const int ClusterCellAssignSize = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 2;    // 22

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCellTierChange(Span<byte> destination, byte threadSlot, long timestamp, int cellKey, byte oldTier, byte newTier)
    {
        TraceRecordHeader.WriteCommonHeader(destination, CellTierChangeSize, TraceEventKind.SpatialGridCellTierChange, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, cellKey);
        p[4] = oldTier;
        p[5] = newTier;
    }

    public static SpatialGridCellTierChangeData DecodeCellTierChange(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialGridCellTierChangeData(threadSlot, timestamp, BinaryPrimitives.ReadInt32LittleEndian(p), p[4], p[5]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOccupancyChange(Span<byte> destination, byte threadSlot, long timestamp, int cellKey, sbyte delta, ushort occBefore, ushort occAfter)
    {
        TraceRecordHeader.WriteCommonHeader(destination, OccupancyChangeSize, TraceEventKind.SpatialGridOccupancyChange, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, cellKey);
        p[4] = (byte)delta;
        BinaryPrimitives.WriteUInt16LittleEndian(p[5..], occBefore);
        BinaryPrimitives.WriteUInt16LittleEndian(p[7..], occAfter);
    }

    public static SpatialGridOccupancyChangeData DecodeOccupancyChange(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialGridOccupancyChangeData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            (sbyte)p[4],
            BinaryPrimitives.ReadUInt16LittleEndian(p[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[7..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteClusterCellAssign(Span<byte> destination, byte threadSlot, long timestamp, int clusterChunkId, int cellKey, ushort archetypeId)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ClusterCellAssignSize, TraceEventKind.SpatialGridClusterCellAssign, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, clusterChunkId);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], cellKey);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], archetypeId);
    }

    public static SpatialGridClusterCellAssignData DecodeClusterCellAssign(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialGridClusterCellAssignData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
    }
}

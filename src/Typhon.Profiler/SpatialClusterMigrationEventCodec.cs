using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded ClusterMigration Detect instant. Payload: <c>archetypeId u16, clusterChunkId i32, oldCellKey i32, newCellKey i32</c> (14 B).</summary>
[PublicAPI]
public readonly struct SpatialClusterMigrationDetectData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ArchetypeId { get; }
    public int ClusterChunkId { get; }
    public int OldCellKey { get; }
    public int NewCellKey { get; }

    public SpatialClusterMigrationDetectData(byte threadSlot, long timestamp, ushort archetypeId, int clusterChunkId, int oldCellKey, int newCellKey)
    { ThreadSlot = threadSlot; Timestamp = timestamp; ArchetypeId = archetypeId; ClusterChunkId = clusterChunkId; OldCellKey = oldCellKey; NewCellKey = newCellKey; }
}

/// <summary>Decoded ClusterMigration Queue instant. Payload: <c>archetypeId u16, clusterChunkId i32, queueLen u16</c> (8 B).</summary>
[PublicAPI]
public readonly struct SpatialClusterMigrationQueueData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ArchetypeId { get; }
    public int ClusterChunkId { get; }
    public ushort QueueLen { get; }

    public SpatialClusterMigrationQueueData(byte threadSlot, long timestamp, ushort archetypeId, int clusterChunkId, ushort queueLen)
    { ThreadSlot = threadSlot; Timestamp = timestamp; ArchetypeId = archetypeId; ClusterChunkId = clusterChunkId; QueueLen = queueLen; }
}

/// <summary>Decoded ClusterMigration Hysteresis instant. Payload: <c>archetypeId u16, clusterChunkId i32, escapeDistSq f32</c> (10 B).</summary>
[PublicAPI]
public readonly struct SpatialClusterMigrationHysteresisData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ArchetypeId { get; }
    public int ClusterChunkId { get; }
    public float EscapeDistSq { get; }

    public SpatialClusterMigrationHysteresisData(byte threadSlot, long timestamp, ushort archetypeId, int clusterChunkId, float escapeDistSq)
    { ThreadSlot = threadSlot; Timestamp = timestamp; ArchetypeId = archetypeId; ClusterChunkId = clusterChunkId; EscapeDistSq = escapeDistSq; }
}

/// <summary>Wire codec for ClusterMigration Detect/Queue/Hysteresis instants (kinds 133-135). Note: Execute (existing kind 60) lives in <see cref="ClusterMigrationEventCodec"/>.</summary>
public static class SpatialClusterMigrationEventCodec
{
    public const int DetectSize = TraceRecordHeader.CommonHeaderSize + 2 + 4 + 4 + 4;     // 26
    public const int QueueSize = TraceRecordHeader.CommonHeaderSize + 2 + 4 + 2;          // 20
    public const int HysteresisSize = TraceRecordHeader.CommonHeaderSize + 2 + 4 + 4;     // 22

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDetect(Span<byte> destination, byte threadSlot, long timestamp, ushort archetypeId, int clusterChunkId, int oldCellKey, int newCellKey)
    {
        TraceRecordHeader.WriteCommonHeader(destination, DetectSize, TraceEventKind.SpatialClusterMigrationDetect, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, archetypeId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], clusterChunkId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], oldCellKey);
        BinaryPrimitives.WriteInt32LittleEndian(p[10..], newCellKey);
    }

    public static SpatialClusterMigrationDetectData DecodeDetect(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialClusterMigrationDetectData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[10..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteQueue(Span<byte> destination, byte threadSlot, long timestamp, ushort archetypeId, int clusterChunkId, ushort queueLen)
    {
        TraceRecordHeader.WriteCommonHeader(destination, QueueSize, TraceEventKind.SpatialClusterMigrationQueue, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, archetypeId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], clusterChunkId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], queueLen);
    }

    public static SpatialClusterMigrationQueueData DecodeQueue(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialClusterMigrationQueueData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[6..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteHysteresis(Span<byte> destination, byte threadSlot, long timestamp, ushort archetypeId, int clusterChunkId, float escapeDistSq)
    {
        TraceRecordHeader.WriteCommonHeader(destination, HysteresisSize, TraceEventKind.SpatialClusterMigrationHysteresis, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, archetypeId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], clusterChunkId);
        BinaryPrimitives.WriteSingleLittleEndian(p[6..], escapeDistSq);
    }

    public static SpatialClusterMigrationHysteresisData DecodeHysteresis(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialClusterMigrationHysteresisData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[6..]));
    }
}

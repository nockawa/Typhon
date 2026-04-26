using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Dispense instant. Payload: <c>sysIdx u16, chunkIdx i32, workerId u8</c> (7 B).</summary>
[PublicAPI]
public readonly struct SchedulerDispenseData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SysIdx { get; }
    public int ChunkIdx { get; }
    public byte WorkerId { get; }
    public SchedulerDispenseData(byte threadSlot, long timestamp, ushort sysIdx, int chunkIdx, byte workerId)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SysIdx = sysIdx; ChunkIdx = chunkIdx; WorkerId = workerId; }
}

/// <summary>Wire codec for Scheduler:Dispense instant (kind 153).</summary>
public static class SchedulerDispenseEventCodec
{
    public const int Size = TraceRecordHeader.CommonHeaderSize + 2 + 4 + 1;  // 19

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> destination, byte threadSlot, long timestamp, ushort sysIdx, int chunkIdx, byte workerId)
    {
        TraceRecordHeader.WriteCommonHeader(destination, Size, TraceEventKind.SchedulerDispense, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, sysIdx);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], chunkIdx);
        p[6] = workerId;
    }

    public static SchedulerDispenseData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerDispenseData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            p[6]);
    }
}

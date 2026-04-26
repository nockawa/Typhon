using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

[PublicAPI]
public readonly struct DurabilityUowStateData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte From { get; }
    public byte To { get; }
    public ushort UowId { get; }
    public byte Reason { get; }
    public DurabilityUowStateData(byte ts, long t, byte f, byte to, ushort id, byte r)
    { ThreadSlot = ts; Timestamp = t; From = f; To = to; UowId = id; Reason = r; }
}

[PublicAPI]
public readonly struct DurabilityUowDeadlineData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Deadline { get; }
    public long Remaining { get; }
    public byte Expired { get; }
    public DurabilityUowDeadlineData(byte ts, long t, long d, long r, byte e)
    { ThreadSlot = ts; Timestamp = t; Deadline = d; Remaining = r; Expired = e; }
}

public static class DurabilityUowEventCodec
{
    public const int StateSize    = TraceRecordHeader.CommonHeaderSize + 1 + 1 + 2 + 1;  // 17
    public const int DeadlineSize = TraceRecordHeader.CommonHeaderSize + 8 + 8 + 1;       // 29

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteState(Span<byte> destination, byte threadSlot, long timestamp, byte from, byte to, ushort uowId, byte reason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, StateSize, TraceEventKind.DurabilityUowState, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = from;
        p[1] = to;
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], uowId);
        p[4] = reason;
    }

    public static DurabilityUowStateData DecodeState(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityUowStateData(threadSlot, timestamp,
            p[0], p[1],
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            p[4]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDeadline(Span<byte> destination, byte threadSlot, long timestamp, long deadline, long remaining, byte expired)
    {
        TraceRecordHeader.WriteCommonHeader(destination, DeadlineSize, TraceEventKind.DurabilityUowDeadline, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, deadline);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], remaining);
        p[16] = expired;
    }

    public static DurabilityUowDeadlineData DecodeDeadline(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityUowDeadlineData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]),
            p[16]);
    }
}

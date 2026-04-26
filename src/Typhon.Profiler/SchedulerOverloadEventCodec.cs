using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Overload LevelChange instant. Payload: <c>prevLvl u8, newLvl u8, ratio f32, queueDepth i32, oldMul u8, newMul u8</c> (12 B).</summary>
[PublicAPI]
public readonly struct SchedulerOverloadLevelChangeData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte PrevLvl { get; }
    public byte NewLvl { get; }
    public float Ratio { get; }
    public int QueueDepth { get; }
    public byte OldMul { get; }
    public byte NewMul { get; }
    public SchedulerOverloadLevelChangeData(byte threadSlot, long timestamp, byte prevLvl, byte newLvl, float ratio, int queueDepth, byte oldMul, byte newMul)
    { ThreadSlot = threadSlot; Timestamp = timestamp; PrevLvl = prevLvl; NewLvl = newLvl; Ratio = ratio; QueueDepth = queueDepth; OldMul = oldMul; NewMul = newMul; }
}

/// <summary>Decoded Overload SystemShed instant. Payload: <c>sysIdx u16, level u8, divisor u16, decision u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct SchedulerOverloadSystemShedData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SysIdx { get; }
    public byte Level { get; }
    public ushort Divisor { get; }
    public byte Decision { get; }
    public SchedulerOverloadSystemShedData(byte threadSlot, long timestamp, ushort sysIdx, byte level, ushort divisor, byte decision)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SysIdx = sysIdx; Level = level; Divisor = divisor; Decision = decision; }
}

/// <summary>Decoded Overload TickMultiplier instant. Payload: <c>tick i64, multiplier u8, intervalTicks u8</c> (10 B).</summary>
[PublicAPI]
public readonly struct SchedulerOverloadTickMultiplierData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tick { get; }
    public byte Multiplier { get; }
    public byte IntervalTicks { get; }
    public SchedulerOverloadTickMultiplierData(byte threadSlot, long timestamp, long tick, byte multiplier, byte intervalTicks)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tick = tick; Multiplier = multiplier; IntervalTicks = intervalTicks; }
}

/// <summary>Wire codec for Scheduler:Overload events (kinds 156-158).</summary>
public static class SchedulerOverloadEventCodec
{
    public const int LevelChangeSize    = TraceRecordHeader.CommonHeaderSize + 1 + 1 + 4 + 4 + 1 + 1;  // 24
    public const int SystemShedSize     = TraceRecordHeader.CommonHeaderSize + 2 + 1 + 2 + 1;          // 18
    public const int TickMultiplierSize = TraceRecordHeader.CommonHeaderSize + 8 + 1 + 1;              // 22

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLevelChange(Span<byte> destination, byte threadSlot, long timestamp,
        byte prevLvl, byte newLvl, float ratio, int queueDepth, byte oldMul, byte newMul)
    {
        TraceRecordHeader.WriteCommonHeader(destination, LevelChangeSize, TraceEventKind.SchedulerOverloadLevelChange, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = prevLvl;
        p[1] = newLvl;
        BinaryPrimitives.WriteSingleLittleEndian(p[2..], ratio);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], queueDepth);
        p[10] = oldMul;
        p[11] = newMul;
    }

    public static SchedulerOverloadLevelChangeData DecodeLevelChange(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerOverloadLevelChangeData(threadSlot, timestamp,
            p[0], p[1],
            BinaryPrimitives.ReadSingleLittleEndian(p[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            p[10], p[11]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSystemShed(Span<byte> destination, byte threadSlot, long timestamp, ushort sysIdx, byte level, ushort divisor, byte decision)
    {
        TraceRecordHeader.WriteCommonHeader(destination, SystemShedSize, TraceEventKind.SchedulerOverloadSystemShed, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, sysIdx);
        p[2] = level;
        BinaryPrimitives.WriteUInt16LittleEndian(p[3..], divisor);
        p[5] = decision;
    }

    public static SchedulerOverloadSystemShedData DecodeSystemShed(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerOverloadSystemShedData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            p[2],
            BinaryPrimitives.ReadUInt16LittleEndian(p[3..]),
            p[5]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTickMultiplier(Span<byte> destination, byte threadSlot, long timestamp, long tick, byte multiplier, byte intervalTicks)
    {
        TraceRecordHeader.WriteCommonHeader(destination, TickMultiplierSize, TraceEventKind.SchedulerOverloadTickMultiplier, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tick);
        p[8] = multiplier;
        p[9] = intervalTicks;
    }

    public static SchedulerOverloadTickMultiplierData DecodeTickMultiplier(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerOverloadTickMultiplierData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p), p[8], p[9]);
    }
}

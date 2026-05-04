using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.GcStart"/> record.
/// </summary>
public readonly struct GcStartData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte Generation { get; }
    public GcReason Reason { get; }
    public GcType Type { get; }
    public uint Count { get; }

    public GcStartData(byte threadSlot, long timestamp, byte generation, GcReason reason, GcType type, uint count)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Generation = generation;
        Reason = reason;
        Type = type;
        Count = count;
    }
}

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.GcEnd"/> record.
/// </summary>
public readonly struct GcEndData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte Generation { get; }
    public uint Count { get; }
    public long PauseDurationTicks { get; }
    public ulong PromotedBytes { get; }
    public ulong Gen0SizeAfter { get; }
    public ulong Gen1SizeAfter { get; }
    public ulong Gen2SizeAfter { get; }
    public ulong LohSizeAfter { get; }
    public ulong PohSizeAfter { get; }
    public ulong TotalCommittedBytes { get; }

    public GcEndData(
        byte threadSlot, long timestamp, byte generation, uint count, long pauseDurationTicks,
        ulong promotedBytes, ulong gen0, ulong gen1, ulong gen2, ulong loh, ulong poh, ulong committed)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Generation = generation;
        Count = count;
        PauseDurationTicks = pauseDurationTicks;
        PromotedBytes = promotedBytes;
        Gen0SizeAfter = gen0;
        Gen1SizeAfter = gen1;
        Gen2SizeAfter = gen2;
        LohSizeAfter = loh;
        PohSizeAfter = poh;
        TotalCommittedBytes = committed;
    }
}

/// <summary>
/// Wire-format codec for the two <see cref="TraceEventKind.GcStart"/> / <see cref="TraceEventKind.GcEnd"/> instant records. Direct
/// encoder helpers (no ref-struct Begin/Dispose) because GC events are emitted from the dedicated ingestion thread, never from a using-scope.
/// </summary>
public static class GcInstantEventCodec
{
    /// <summary>Fixed wire size of a <see cref="TraceEventKind.GcStart"/> record: 12 B common header + 1+1+1+4 = 7 B payload = 19 B.</summary>
    public const int GcStartSize = TraceRecordHeader.CommonHeaderSize + 7;

    /// <summary>Fixed wire size of a <see cref="TraceEventKind.GcEnd"/> record: 12 B common header + 1+4+8+8+5*8+8 = 69 B payload = 81 B.</summary>
    public const int GcEndSize = TraceRecordHeader.CommonHeaderSize + 1 + 4 + 8 + 8 + (5 * 8) + 8;

    /// <summary>
    /// Encode a <see cref="TraceEventKind.GcStart"/> record into <paramref name="destination"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGcStart(Span<byte> destination, byte threadSlot, long timestamp, byte generation, GcReason reason, GcType type, uint count, 
        out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        TraceRecordHeader.WriteCommonHeader(destination, GcStartSize, TraceEventKind.GcStart, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = generation;
        p[1] = (byte)reason;
        p[2] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(p[3..], count);
        bytesWritten = GcStartSize;
    }

    /// <summary>
    /// Encode a <see cref="TraceEventKind.GcEnd"/> record into <paramref name="destination"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGcEnd(Span<byte> destination, byte threadSlot, long timestamp, byte generation, uint count, long pauseDurationTicks, 
        ulong promotedBytes, ulong gen0SizeAfter, ulong gen1SizeAfter, ulong gen2SizeAfter, ulong lohSizeAfter, ulong pohSizeAfter, ulong totalCommittedBytes,
        out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        TraceRecordHeader.WriteCommonHeader(destination, GcEndSize, TraceEventKind.GcEnd, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = generation;
        BinaryPrimitives.WriteUInt32LittleEndian(p[1..], count);
        BinaryPrimitives.WriteInt64LittleEndian(p[5..], pauseDurationTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(p[13..], promotedBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(p[21..], gen0SizeAfter);
        BinaryPrimitives.WriteUInt64LittleEndian(p[29..], gen1SizeAfter);
        BinaryPrimitives.WriteUInt64LittleEndian(p[37..], gen2SizeAfter);
        BinaryPrimitives.WriteUInt64LittleEndian(p[45..], lohSizeAfter);
        BinaryPrimitives.WriteUInt64LittleEndian(p[53..], pohSizeAfter);
        BinaryPrimitives.WriteUInt64LittleEndian(p[61..], totalCommittedBytes);
        bytesWritten = GcEndSize;
    }

    /// <summary>Decode a <see cref="TraceEventKind.GcStart"/> record from <paramref name="source"/>.</summary>
    public static GcStartData DecodeGcStart(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.GcStart)
        {
            throw new ArgumentException($"Expected GcStart, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new GcStartData(
            threadSlot, timestamp,
            generation: p[0],
            reason: (GcReason)p[1],
            type: (GcType)p[2],
            count: BinaryPrimitives.ReadUInt32LittleEndian(p[3..]));
    }

    /// <summary>Decode a <see cref="TraceEventKind.GcEnd"/> record from <paramref name="source"/>.</summary>
    public static GcEndData DecodeGcEnd(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.GcEnd)
        {
            throw new ArgumentException($"Expected GcEnd, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new GcEndData(
            threadSlot, timestamp,
            generation: p[0],
            count: BinaryPrimitives.ReadUInt32LittleEndian(p[1..]),
            pauseDurationTicks: BinaryPrimitives.ReadInt64LittleEndian(p[5..]),
            promotedBytes: BinaryPrimitives.ReadUInt64LittleEndian(p[13..]),
            gen0: BinaryPrimitives.ReadUInt64LittleEndian(p[21..]),
            gen1: BinaryPrimitives.ReadUInt64LittleEndian(p[29..]),
            gen2: BinaryPrimitives.ReadUInt64LittleEndian(p[37..]),
            loh: BinaryPrimitives.ReadUInt64LittleEndian(p[45..]),
            poh: BinaryPrimitives.ReadUInt64LittleEndian(p[53..]),
            committed: BinaryPrimitives.ReadUInt64LittleEndian(p[61..]));
    }
}

using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.MemoryAllocEvent"/> record.
/// </summary>
[PublicAPI]
public readonly struct MemoryAllocEventData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public MemoryAllocDirection Direction { get; }
    public ushort SourceTag { get; }
    public ulong SizeBytes { get; }
    public ulong TotalAfterBytes { get; }

    public MemoryAllocEventData(byte threadSlot, long timestamp, MemoryAllocDirection direction, ushort sourceTag, ulong sizeBytes, ulong totalAfterBytes)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Direction = direction;
        SourceTag = sourceTag;
        SizeBytes = sizeBytes;
        TotalAfterBytes = totalAfterBytes;
    }
}

/// <summary>
/// Wire-format codec for the <see cref="TraceEventKind.MemoryAllocEvent"/> instant record — one discrete unmanaged alloc or free.
/// </summary>
/// <remarks>
/// Layout after the 12-byte common header:
/// <code>
/// offset 12      u8   Direction        // 0 = alloc, 1 = free (see MemoryAllocDirection)
/// offset 13..14  u16  SourceTag        // interned call-site tag (see MemoryAllocSource)
/// offset 15..22  u64  SizeBytes        // bytes (allocated on alloc, freed on free)
/// offset 23..30  u64  TotalAfterBytes  // MemoryAllocator running total AFTER this op
/// </code>
/// Total wire size: 12 + 1 + 2 + 8 + 8 = 31 bytes.
/// </remarks>
public static class MemoryAllocEventCodec
{
    /// <summary>Fixed wire size of a <see cref="TraceEventKind.MemoryAllocEvent"/> record: 12 B common header + 19 B payload = 31 B.</summary>
    public const int EventSize = TraceRecordHeader.CommonHeaderSize + 1 + 2 + 8 + 8;

    /// <summary>
    /// Encode a <see cref="TraceEventKind.MemoryAllocEvent"/> record into <paramref name="destination"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMemoryAllocEvent(Span<byte> destination, byte threadSlot, long timestamp, MemoryAllocDirection direction, ushort sourceTag, 
        ulong sizeBytes, ulong totalAfterBytes, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        TraceRecordHeader.WriteCommonHeader(destination, EventSize, TraceEventKind.MemoryAllocEvent, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = (byte)direction;
        BinaryPrimitives.WriteUInt16LittleEndian(p[1..], sourceTag);
        BinaryPrimitives.WriteUInt64LittleEndian(p[3..], sizeBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(p[11..], totalAfterBytes);
        bytesWritten = EventSize;
    }

    /// <summary>Decode a <see cref="TraceEventKind.MemoryAllocEvent"/> record from <paramref name="source"/>.</summary>
    public static MemoryAllocEventData DecodeMemoryAllocEvent(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.MemoryAllocEvent)
        {
            throw new ArgumentException($"Expected MemoryAllocEvent, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new MemoryAllocEventData(
            threadSlot, timestamp,
            direction: (MemoryAllocDirection)p[0],
            sourceTag: BinaryPrimitives.ReadUInt16LittleEndian(p[1..]),
            sizeBytes: BinaryPrimitives.ReadUInt64LittleEndian(p[3..]),
            totalAfterBytes: BinaryPrimitives.ReadUInt64LittleEndian(p[11..]));
    }
}

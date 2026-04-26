using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Memory:AlignmentWaste instant. Payload: <c>size i32, alignment i32, wastePctHundredths u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct MemoryAlignmentWasteData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int Size { get; }
    public int Alignment { get; }
    /// <summary>Percentage expressed as hundredths (e.g. 1234 = 12.34%).</summary>
    public ushort WastePctHundredths { get; }
    public MemoryAlignmentWasteData(byte threadSlot, long timestamp, int size, int alignment, ushort wastePctHundredths)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Size = size; Alignment = alignment; WastePctHundredths = wastePctHundredths; }
}

/// <summary>Wire codec for the <see cref="TraceEventKind.MemoryAlignmentWaste"/> instant (kind 172).</summary>
public static class MemoryAlignmentWasteEventCodec
{
    public const int Size = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 2;  // 22

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> destination, byte threadSlot, long timestamp, int size, int alignment, ushort wastePctHundredths)
    {
        TraceRecordHeader.WriteCommonHeader(destination, Size, TraceEventKind.MemoryAlignmentWaste, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, size);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], alignment);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], wastePctHundredths);
    }

    public static MemoryAlignmentWasteData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new MemoryAlignmentWasteData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
    }
}

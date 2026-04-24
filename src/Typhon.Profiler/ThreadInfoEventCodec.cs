using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.ThreadInfo"/> record — per-slot thread identity (managed thread ID + UTF-8 name) emitted once
/// when a producer thread claims its slot. Lets the viewer label lanes with something meaningful instead of just "Slot N".
/// </summary>
public readonly struct ThreadInfoEventData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int ManagedThreadId { get; }

    /// <summary>UTF-8-encoded name bytes, sliced from the original record. Caller converts to string on demand.</summary>
    public ReadOnlyMemory<byte> NameUtf8 { get; }

    public ThreadInfoEventData(byte threadSlot, long timestamp, int managedThreadId, ReadOnlyMemory<byte> nameUtf8)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        ManagedThreadId = managedThreadId;
        NameUtf8 = nameUtf8;
    }

    /// <summary>Decode <see cref="NameUtf8"/> as a <see cref="string"/>. Allocates.</summary>
    public string GetName() => Encoding.UTF8.GetString(NameUtf8.Span);
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.ThreadInfo"/>. Layout after the 12-byte common header:
/// <code>
/// offset 12..15  i32  ManagedThreadId
/// offset 16..17  u16  NameByteCount
/// offset 18+     byte[NameByteCount] NameUtf8
/// </code>
/// Total wire size: 12 + 4 + 2 + NameByteCount bytes.
/// </summary>
public static class ThreadInfoEventCodec
{
    private const int PrefixSize = TraceRecordHeader.CommonHeaderSize + 4 + 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(int nameByteCount) => PrefixSize + nameByteCount;

    /// <summary>
    /// Encode a ThreadInfo record. Caller has pre-computed <paramref name="nameUtf8"/> (via <see cref="Encoding.UTF8"/>).
    /// </summary>
    public static void WriteThreadInfo(Span<byte> destination, byte threadSlot, long timestamp, int managedThreadId, ReadOnlySpan<byte> nameUtf8, 
        out int bytesWritten)
    {
        if (nameUtf8.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"Thread name too long: {nameUtf8.Length} bytes (max {ushort.MaxValue})", nameof(nameUtf8));
        }

        var size = ComputeSize(nameUtf8.Length);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.ThreadInfo, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, managedThreadId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)nameUtf8.Length);
        nameUtf8.CopyTo(p[6..]);

        bytesWritten = size;
    }

    /// <summary>Decode a <see cref="TraceEventKind.ThreadInfo"/> record from <paramref name="source"/>.</summary>
    public static ThreadInfoEventData Decode(ReadOnlyMemory<byte> source)
    {
        var span = source.Span;
        TraceRecordHeader.ReadCommonHeader(span, out var size, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ThreadInfo)
        {
            throw new ArgumentException($"Expected ThreadInfo, got {kind}", nameof(source));
        }
        if (size > span.Length)
        {
            throw new ArgumentException($"Record size {size} exceeds source buffer {span.Length}", nameof(source));
        }

        var p = span[TraceRecordHeader.CommonHeaderSize..];
        var managedThreadId = BinaryPrimitives.ReadInt32LittleEndian(p);
        var nameByteCount = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        var nameMemory = source.Slice(PrefixSize, nameByteCount);

        return new ThreadInfoEventData(threadSlot, timestamp, managedThreadId, nameMemory);
    }
}

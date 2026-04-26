using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of any of the four <c>AccessControlSmall</c> acquire/release records (kinds 96–99). Unified shape — just <c>threadId</c>.
/// </summary>
[PublicAPI]
public readonly struct ConcurrencyAccessControlSmallEventData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public TraceEventKind Kind { get; }
    public ushort ThreadId { get; }

    public ConcurrencyAccessControlSmallEventData(byte threadSlot, long timestamp, TraceEventKind kind, ushort threadId)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Kind = kind;
        ThreadId = threadId;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyAccessControlSmallContention"/>. Empty payload.</summary>
[PublicAPI]
public readonly struct ConcurrencyAccessControlSmallContentionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }

    public ConcurrencyAccessControlSmallContentionData(byte threadSlot, long timestamp)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
    }
}

/// <summary>
/// Wire-format codec for the AccessControlSmall Concurrency event family (kinds 96–100). All instant-style records.
/// Acquire/Release records: 12-byte common header + 2-byte payload (threadId u16) = 14 B. Contention: 12 B.
/// </summary>
public static class ConcurrencyAccessControlSmallEventCodec
{
    public const int EventSize = TraceRecordHeader.CommonHeaderSize + 2; // 14
    public const int ContentionSize = TraceRecordHeader.CommonHeaderSize; // 12

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteEvent(Span<byte> destination, TraceEventKind kind, byte threadSlot, long timestamp, ushort threadId)
    {
        TraceRecordHeader.WriteCommonHeader(destination, EventSize, kind, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], threadId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteContention(Span<byte> destination, byte threadSlot, long timestamp) => 
        TraceRecordHeader.WriteCommonHeader(destination, ContentionSize, TraceEventKind.ConcurrencyAccessControlSmallContention, threadSlot, timestamp);

    public static ConcurrencyAccessControlSmallEventData DecodeEvent(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAccessControlSmallSharedAcquire
            && kind != TraceEventKind.ConcurrencyAccessControlSmallSharedRelease
            && kind != TraceEventKind.ConcurrencyAccessControlSmallExclusiveAcquire
            && kind != TraceEventKind.ConcurrencyAccessControlSmallExclusiveRelease)
        {
            throw new ArgumentException($"Expected an AccessControlSmall acquire/release kind, got {kind}", nameof(source));
        }
        return new ConcurrencyAccessControlSmallEventData(threadSlot, timestamp, kind, 
            BinaryPrimitives.ReadUInt16LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    public static ConcurrencyAccessControlSmallContentionData DecodeContention(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAccessControlSmallContention)
        {
            throw new ArgumentException($"Expected ConcurrencyAccessControlSmallContention, got {kind}", nameof(source));
        }
        return new ConcurrencyAccessControlSmallContentionData(threadSlot, timestamp);
    }
}

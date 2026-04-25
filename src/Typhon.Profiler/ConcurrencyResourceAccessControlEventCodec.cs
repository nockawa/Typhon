using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyResourceAccessing"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyResourceAccessingData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public bool Success { get; }
    public byte AccessingCount { get; }
    public ushort ElapsedUs { get; }

    public ConcurrencyResourceAccessingData(byte threadSlot, long timestamp, bool success, byte accessingCount, ushort elapsedUs)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Success = success;
        AccessingCount = accessingCount;
        ElapsedUs = elapsedUs;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyResourceModify"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyResourceModifyData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public bool Success { get; }
    public ushort ThreadId { get; }
    public ushort ElapsedUs { get; }

    public ConcurrencyResourceModifyData(byte threadSlot, long timestamp, bool success, ushort threadId, ushort elapsedUs)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Success = success;
        ThreadId = threadId;
        ElapsedUs = elapsedUs;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyResourceDestroy"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyResourceDestroyData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public bool Success { get; }
    public ushort ElapsedUs { get; }

    public ConcurrencyResourceDestroyData(byte threadSlot, long timestamp, bool success, ushort elapsedUs)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Success = success;
        ElapsedUs = elapsedUs;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyResourceModifyPromotion"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyResourceModifyPromotionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ElapsedUs { get; }

    public ConcurrencyResourceModifyPromotionData(byte threadSlot, long timestamp, ushort elapsedUs)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        ElapsedUs = elapsedUs;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyResourceContention"/>. Empty payload.</summary>
[PublicAPI]
public readonly struct ConcurrencyResourceContentionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }

    public ConcurrencyResourceContentionData(byte threadSlot, long timestamp)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
    }
}

/// <summary>
/// Wire-format codec for the ResourceAccessControl Concurrency event family (kinds 101–105). All instant-style records.
/// </summary>
public static class ConcurrencyResourceAccessControlEventCodec
{
    public const int AccessingSize = TraceRecordHeader.CommonHeaderSize + 1 + 1 + 2; // 16
    public const int ModifySize = TraceRecordHeader.CommonHeaderSize + 1 + 2 + 2;    // 17
    public const int DestroySize = TraceRecordHeader.CommonHeaderSize + 1 + 2;       // 15
    public const int ModifyPromotionSize = TraceRecordHeader.CommonHeaderSize + 2;   // 14
    public const int ContentionSize = TraceRecordHeader.CommonHeaderSize;            // 12

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAccessing(Span<byte> destination, byte threadSlot, long timestamp, bool success, byte accessingCount, ushort elapsedUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, AccessingSize, TraceEventKind.ConcurrencyResourceAccessing, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = success ? (byte)1 : (byte)0;
        p[1] = accessingCount;
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], elapsedUs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteModify(Span<byte> destination, byte threadSlot, long timestamp, bool success, ushort threadId, ushort elapsedUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ModifySize, TraceEventKind.ConcurrencyResourceModify, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(p[1..], threadId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[3..], elapsedUs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDestroy(Span<byte> destination, byte threadSlot, long timestamp, bool success, ushort elapsedUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, DestroySize, TraceEventKind.ConcurrencyResourceDestroy, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(p[1..], elapsedUs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteModifyPromotion(Span<byte> destination, byte threadSlot, long timestamp, ushort elapsedUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ModifyPromotionSize, TraceEventKind.ConcurrencyResourceModifyPromotion, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], elapsedUs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteContention(Span<byte> destination, byte threadSlot, long timestamp)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ContentionSize, TraceEventKind.ConcurrencyResourceContention, threadSlot, timestamp);
    }

    public static ConcurrencyResourceAccessingData DecodeAccessing(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyResourceAccessing)
        {
            throw new ArgumentException($"Expected ConcurrencyResourceAccessing, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyResourceAccessingData(threadSlot, timestamp, p[0] != 0, p[1], BinaryPrimitives.ReadUInt16LittleEndian(p[2..]));
    }

    public static ConcurrencyResourceModifyData DecodeModify(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyResourceModify)
        {
            throw new ArgumentException($"Expected ConcurrencyResourceModify, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyResourceModifyData(threadSlot, timestamp, p[0] != 0, BinaryPrimitives.ReadUInt16LittleEndian(p[1..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[3..]));
    }

    public static ConcurrencyResourceDestroyData DecodeDestroy(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyResourceDestroy)
        {
            throw new ArgumentException($"Expected ConcurrencyResourceDestroy, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyResourceDestroyData(threadSlot, timestamp, p[0] != 0, BinaryPrimitives.ReadUInt16LittleEndian(p[1..]));
    }

    public static ConcurrencyResourceModifyPromotionData DecodeModifyPromotion(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyResourceModifyPromotion)
        {
            throw new ArgumentException($"Expected ConcurrencyResourceModifyPromotion, got {kind}", nameof(source));
        }
        return new ConcurrencyResourceModifyPromotionData(threadSlot, timestamp, BinaryPrimitives.ReadUInt16LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    public static ConcurrencyResourceContentionData DecodeContention(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyResourceContention)
        {
            throw new ArgumentException($"Expected ConcurrencyResourceContention, got {kind}", nameof(source));
        }
        return new ConcurrencyResourceContentionData(threadSlot, timestamp);
    }
}

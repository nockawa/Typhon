using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

// ═══════════════════════════════════════════════════════════════════════════════════════
// Decoded data types
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>Decoded form of a <see cref="TraceEventKind.ConcurrencyAccessControlSharedAcquire"/> or <see cref="TraceEventKind.ConcurrencyAccessControlExclusiveAcquire"/> record.</summary>
[PublicAPI]
public readonly struct ConcurrencyAccessControlAcquireData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public TraceEventKind Kind { get; }
    public ushort ThreadId { get; }
    public bool HadToWait { get; }
    public ushort ElapsedUs { get; }

    public ConcurrencyAccessControlAcquireData(byte threadSlot, long timestamp, TraceEventKind kind, ushort threadId, bool hadToWait, ushort elapsedUs)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Kind = kind;
        ThreadId = threadId;
        HadToWait = hadToWait;
        ElapsedUs = elapsedUs;
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.ConcurrencyAccessControlSharedRelease"/> or <see cref="TraceEventKind.ConcurrencyAccessControlExclusiveRelease"/> record.</summary>
[PublicAPI]
public readonly struct ConcurrencyAccessControlReleaseData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public TraceEventKind Kind { get; }
    public ushort ThreadId { get; }

    public ConcurrencyAccessControlReleaseData(byte threadSlot, long timestamp, TraceEventKind kind, ushort threadId)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Kind = kind;
        ThreadId = threadId;
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.ConcurrencyAccessControlPromotion"/> record. Variant byte: 0 = promote (shared→exclusive), 1 = demote (exclusive→shared).</summary>
[PublicAPI]
public readonly struct ConcurrencyAccessControlPromotionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ElapsedUs { get; }
    public byte Variant { get; }

    public ConcurrencyAccessControlPromotionData(byte threadSlot, long timestamp, ushort elapsedUs, byte variant)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        ElapsedUs = elapsedUs;
        Variant = variant;
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.ConcurrencyAccessControlContention"/> record. Empty payload — instant marker.</summary>
[PublicAPI]
public readonly struct ConcurrencyAccessControlContentionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }

    public ConcurrencyAccessControlContentionData(byte threadSlot, long timestamp)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Wire-format codec
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Wire-format codec for the AccessControl Concurrency event family (kinds 90–95). All instant-style records: 12-byte common header + tight payload.
/// </summary>
/// <remarks>
/// <para><b>SharedAcquire / ExclusiveAcquire (kinds 90, 92):</b> 5-byte payload — <c>threadId u16, hadToWait u8, elapsedUs u16</c>. Total wire size: 17 B.</para>
/// <para><b>SharedRelease / ExclusiveRelease (kinds 91, 93):</b> 2-byte payload — <c>threadId u16</c>. Total wire size: 14 B.</para>
/// <para><b>Promotion (kind 94):</b> 3-byte payload — <c>elapsedUs u16, variant u8</c>. Total wire size: 15 B.</para>
/// <para><b>Contention (kind 95):</b> empty payload. Total wire size: 12 B (common header only).</para>
/// </remarks>
public static class ConcurrencyAccessControlEventCodec
{
    public const int AcquireSize = TraceRecordHeader.CommonHeaderSize + 2 + 1 + 2; // 17
    public const int ReleaseSize = TraceRecordHeader.CommonHeaderSize + 2;          // 14
    public const int PromotionSize = TraceRecordHeader.CommonHeaderSize + 2 + 1;    // 15
    public const int ContentionSize = TraceRecordHeader.CommonHeaderSize;           // 12

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAcquire(Span<byte> destination, TraceEventKind kind, byte threadSlot, long timestamp, ushort threadId, bool hadToWait, ushort elapsedUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, AcquireSize, kind, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, threadId);
        p[2] = hadToWait ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(p[3..], elapsedUs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRelease(Span<byte> destination, TraceEventKind kind, byte threadSlot, long timestamp, ushort threadId)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ReleaseSize, kind, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], threadId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePromotion(Span<byte> destination, byte threadSlot, long timestamp, ushort elapsedUs, byte variant)
    {
        TraceRecordHeader.WriteCommonHeader(destination, PromotionSize, TraceEventKind.ConcurrencyAccessControlPromotion, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, elapsedUs);
        p[2] = variant;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteContention(Span<byte> destination, byte threadSlot, long timestamp) => 
        TraceRecordHeader.WriteCommonHeader(destination, ContentionSize, TraceEventKind.ConcurrencyAccessControlContention, threadSlot, timestamp);

    public static ConcurrencyAccessControlAcquireData DecodeAcquire(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAccessControlSharedAcquire && kind != TraceEventKind.ConcurrencyAccessControlExclusiveAcquire)
        {
            throw new ArgumentException($"Expected SharedAcquire or ExclusiveAcquire, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyAccessControlAcquireData(threadSlot, timestamp, kind, BinaryPrimitives.ReadUInt16LittleEndian(p), p[2] != 0, 
            BinaryPrimitives.ReadUInt16LittleEndian(p[3..]));
    }

    public static ConcurrencyAccessControlReleaseData DecodeRelease(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAccessControlSharedRelease && kind != TraceEventKind.ConcurrencyAccessControlExclusiveRelease)
        {
            throw new ArgumentException($"Expected SharedRelease or ExclusiveRelease, got {kind}", nameof(source));
        }
        return new ConcurrencyAccessControlReleaseData(threadSlot, timestamp, kind, 
            BinaryPrimitives.ReadUInt16LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    public static ConcurrencyAccessControlPromotionData DecodePromotion(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAccessControlPromotion)
        {
            throw new ArgumentException($"Expected ConcurrencyAccessControlPromotion, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyAccessControlPromotionData(threadSlot, timestamp, BinaryPrimitives.ReadUInt16LittleEndian(p), p[2]);
    }

    public static ConcurrencyAccessControlContentionData DecodeContention(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAccessControlContention)
        {
            throw new ArgumentException($"Expected ConcurrencyAccessControlContention, got {kind}", nameof(source));
        }
        return new ConcurrencyAccessControlContentionData(threadSlot, timestamp);
    }
}

using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyOlcLatchWriteLockAttempt"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyOlcLatchWriteLockAttemptData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint VersionBefore { get; }
    public bool Success { get; }

    public ConcurrencyOlcLatchWriteLockAttemptData(byte threadSlot, long timestamp, uint versionBefore, bool success)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        VersionBefore = versionBefore;
        Success = success;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyOlcLatchWriteUnlock"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyOlcLatchWriteUnlockData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint OldVersion { get; }
    public uint NewVersion { get; }

    public ConcurrencyOlcLatchWriteUnlockData(byte threadSlot, long timestamp, uint oldVersion, uint newVersion)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        OldVersion = oldVersion;
        NewVersion = newVersion;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyOlcLatchMarkObsolete"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyOlcLatchMarkObsoleteData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint Version { get; }

    public ConcurrencyOlcLatchMarkObsoleteData(byte threadSlot, long timestamp, uint version)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Version = version;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyOlcLatchValidationFail"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyOlcLatchValidationFailData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint ExpectedVersion { get; }
    public uint ActualVersion { get; }

    public ConcurrencyOlcLatchValidationFailData(byte threadSlot, long timestamp, uint expectedVersion, uint actualVersion)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}

/// <summary>
/// Wire-format codec for the OlcLatch Concurrency event family (kinds 113–116). All instant-style records.
/// </summary>
public static class ConcurrencyOlcLatchEventCodec
{
    public const int WriteLockAttemptSize = TraceRecordHeader.CommonHeaderSize + 4 + 1;  // 17
    public const int WriteUnlockSize = TraceRecordHeader.CommonHeaderSize + 4 + 4;       // 20
    public const int MarkObsoleteSize = TraceRecordHeader.CommonHeaderSize + 4;          // 16
    public const int ValidationFailSize = TraceRecordHeader.CommonHeaderSize + 4 + 4;    // 20

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteWriteLockAttempt(Span<byte> destination, byte threadSlot, long timestamp, uint versionBefore, bool success)
    {
        TraceRecordHeader.WriteCommonHeader(destination, WriteLockAttemptSize, TraceEventKind.ConcurrencyOlcLatchWriteLockAttempt, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, versionBefore);
        p[4] = success ? (byte)1 : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteWriteUnlock(Span<byte> destination, byte threadSlot, long timestamp, uint oldVersion, uint newVersion)
    {
        TraceRecordHeader.WriteCommonHeader(destination, WriteUnlockSize, TraceEventKind.ConcurrencyOlcLatchWriteUnlock, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, oldVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], newVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMarkObsolete(Span<byte> destination, byte threadSlot, long timestamp, uint version)
    {
        TraceRecordHeader.WriteCommonHeader(destination, MarkObsoleteSize, TraceEventKind.ConcurrencyOlcLatchMarkObsolete, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteValidationFail(Span<byte> destination, byte threadSlot, long timestamp, uint expectedVersion, uint actualVersion)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ValidationFailSize, TraceEventKind.ConcurrencyOlcLatchValidationFail, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, expectedVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], actualVersion);
    }

    public static ConcurrencyOlcLatchWriteLockAttemptData DecodeWriteLockAttempt(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyOlcLatchWriteLockAttempt)
        {
            throw new ArgumentException($"Expected ConcurrencyOlcLatchWriteLockAttempt, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyOlcLatchWriteLockAttemptData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(p), p[4] != 0);
    }

    public static ConcurrencyOlcLatchWriteUnlockData DecodeWriteUnlock(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyOlcLatchWriteUnlock)
        {
            throw new ArgumentException($"Expected ConcurrencyOlcLatchWriteUnlock, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyOlcLatchWriteUnlockData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[4..]));
    }

    public static ConcurrencyOlcLatchMarkObsoleteData DecodeMarkObsolete(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyOlcLatchMarkObsolete)
        {
            throw new ArgumentException($"Expected ConcurrencyOlcLatchMarkObsolete, got {kind}", nameof(source));
        }
        return new ConcurrencyOlcLatchMarkObsoleteData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    public static ConcurrencyOlcLatchValidationFailData DecodeValidationFail(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyOlcLatchValidationFail)
        {
            throw new ArgumentException($"Expected ConcurrencyOlcLatchValidationFail, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyOlcLatchValidationFailData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[4..]));
    }
}

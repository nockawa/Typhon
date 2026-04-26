using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// AdaptiveWaiter yield-or-sleep transition kind.
/// </summary>
[PublicAPI]
public enum AdaptiveWaiterTransitionKind : byte
{
    /// <summary>The current SpinOnce yielded the thread (Thread.Yield / Sleep(0)).</summary>
    Yield = 1,

    /// <summary>The current SpinOnce slept (Sleep(1) or longer).</summary>
    Sleep = 2,
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyAdaptiveWaiterYieldOrSleep"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyAdaptiveWaiterYieldOrSleepData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SpinCountBefore { get; }
    public AdaptiveWaiterTransitionKind Kind { get; }

    public ConcurrencyAdaptiveWaiterYieldOrSleepData(byte threadSlot, long timestamp, ushort spinCountBefore, AdaptiveWaiterTransitionKind kind)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        SpinCountBefore = spinCountBefore;
        Kind = kind;
    }
}

/// <summary>
/// Wire-format codec for the AdaptiveWaiter Concurrency event family (kind 112). Single instant kind — transition only, NOT per-spin.
/// </summary>
/// <remarks>
/// Layout after the 12-byte common header: <c>spinCountBefore u16, kind u8</c>. Total wire size: 15 B.
/// </remarks>
public static class ConcurrencyAdaptiveWaiterEventCodec
{
    public const int EventSize = TraceRecordHeader.CommonHeaderSize + 2 + 1; // 15

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteYieldOrSleep(Span<byte> destination, byte threadSlot, long timestamp, ushort spinCountBefore, AdaptiveWaiterTransitionKind kind)
    {
        TraceRecordHeader.WriteCommonHeader(destination, EventSize, TraceEventKind.ConcurrencyAdaptiveWaiterYieldOrSleep, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, spinCountBefore);
        p[2] = (byte)kind;
    }

    public static ConcurrencyAdaptiveWaiterYieldOrSleepData DecodeYieldOrSleep(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyAdaptiveWaiterYieldOrSleep)
        {
            throw new ArgumentException($"Expected ConcurrencyAdaptiveWaiterYieldOrSleep, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyAdaptiveWaiterYieldOrSleepData(threadSlot, timestamp, BinaryPrimitives.ReadUInt16LittleEndian(p), (AdaptiveWaiterTransitionKind)p[2]);
    }
}

using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyEpochScopeEnter"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyEpochScopeEnterData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint Epoch { get; }
    public byte DepthBefore { get; }
    public bool IsDormantToActive { get; }

    public ConcurrencyEpochScopeEnterData(byte threadSlot, long timestamp, uint epoch, byte depthBefore, bool isDormantToActive)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Epoch = epoch;
        DepthBefore = depthBefore;
        IsDormantToActive = isDormantToActive;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyEpochScopeExit"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyEpochScopeExitData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint Epoch { get; }
    public bool IsOutermost { get; }

    public ConcurrencyEpochScopeExitData(byte threadSlot, long timestamp, uint epoch, bool isOutermost)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Epoch = epoch;
        IsOutermost = isOutermost;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyEpochAdvance"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyEpochAdvanceData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint NewEpoch { get; }

    public ConcurrencyEpochAdvanceData(byte threadSlot, long timestamp, uint newEpoch)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        NewEpoch = newEpoch;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyEpochRefresh"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyEpochRefreshData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public uint OldEpoch { get; }
    public uint NewEpoch { get; }

    public ConcurrencyEpochRefreshData(byte threadSlot, long timestamp, uint oldEpoch, uint newEpoch)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        OldEpoch = oldEpoch;
        NewEpoch = newEpoch;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyEpochSlotClaim"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyEpochSlotClaimData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SlotIndex { get; }
    public ushort ThreadId { get; }
    public ushort ActiveCount { get; }

    public ConcurrencyEpochSlotClaimData(byte threadSlot, long timestamp, ushort slotIndex, ushort threadId, ushort activeCount)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        SlotIndex = slotIndex;
        ThreadId = threadId;
        ActiveCount = activeCount;
    }
}

/// <summary>Decoded form of <see cref="TraceEventKind.ConcurrencyEpochSlotReclaim"/>.</summary>
[PublicAPI]
public readonly struct ConcurrencyEpochSlotReclaimData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SlotIndex { get; }
    public ushort OldOwner { get; }
    public ushort NewOwner { get; }

    public ConcurrencyEpochSlotReclaimData(byte threadSlot, long timestamp, ushort slotIndex, ushort oldOwner, ushort newOwner)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        SlotIndex = slotIndex;
        OldOwner = oldOwner;
        NewOwner = newOwner;
    }
}

/// <summary>
/// Wire-format codec for the Epoch Concurrency event family (kinds 106–111). All instant-style records.
/// </summary>
public static class ConcurrencyEpochEventCodec
{
    public const int ScopeEnterSize = TraceRecordHeader.CommonHeaderSize + 4 + 1 + 1; // 18
    public const int ScopeExitSize = TraceRecordHeader.CommonHeaderSize + 4 + 1;       // 17
    public const int AdvanceSize = TraceRecordHeader.CommonHeaderSize + 4;             // 16
    public const int RefreshSize = TraceRecordHeader.CommonHeaderSize + 4 + 4;         // 20
    public const int SlotClaimSize = TraceRecordHeader.CommonHeaderSize + 2 + 2 + 2;   // 18
    public const int SlotReclaimSize = TraceRecordHeader.CommonHeaderSize + 2 + 2 + 2; // 18

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteScopeEnter(Span<byte> destination, byte threadSlot, long timestamp, uint epoch, byte depthBefore, bool isDormantToActive)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ScopeEnterSize, TraceEventKind.ConcurrencyEpochScopeEnter, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, epoch);
        p[4] = depthBefore;
        p[5] = isDormantToActive ? (byte)1 : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteScopeExit(Span<byte> destination, byte threadSlot, long timestamp, uint epoch, bool isOutermost)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ScopeExitSize, TraceEventKind.ConcurrencyEpochScopeExit, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, epoch);
        p[4] = isOutermost ? (byte)1 : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteAdvance(Span<byte> destination, byte threadSlot, long timestamp, uint newEpoch)
    {
        TraceRecordHeader.WriteCommonHeader(destination, AdvanceSize, TraceEventKind.ConcurrencyEpochAdvance, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], newEpoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRefresh(Span<byte> destination, byte threadSlot, long timestamp, uint oldEpoch, uint newEpoch)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RefreshSize, TraceEventKind.ConcurrencyEpochRefresh, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, oldEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], newEpoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotClaim(Span<byte> destination, byte threadSlot, long timestamp, ushort slotIndex, ushort threadId, ushort activeCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, SlotClaimSize, TraceEventKind.ConcurrencyEpochSlotClaim, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, slotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], threadId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], activeCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSlotReclaim(Span<byte> destination, byte threadSlot, long timestamp, ushort slotIndex, ushort oldOwner, ushort newOwner)
    {
        TraceRecordHeader.WriteCommonHeader(destination, SlotReclaimSize, TraceEventKind.ConcurrencyEpochSlotReclaim, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, slotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], oldOwner);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], newOwner);
    }

    public static ConcurrencyEpochScopeEnterData DecodeScopeEnter(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyEpochScopeEnter)
        {
            throw new ArgumentException($"Expected ConcurrencyEpochScopeEnter, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyEpochScopeEnterData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(p), p[4], p[5] != 0);
    }

    public static ConcurrencyEpochScopeExitData DecodeScopeExit(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyEpochScopeExit)
        {
            throw new ArgumentException($"Expected ConcurrencyEpochScopeExit, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyEpochScopeExitData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(p), p[4] != 0);
    }

    public static ConcurrencyEpochAdvanceData DecodeAdvance(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyEpochAdvance)
        {
            throw new ArgumentException($"Expected ConcurrencyEpochAdvance, got {kind}", nameof(source));
        }
        return new ConcurrencyEpochAdvanceData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    public static ConcurrencyEpochRefreshData DecodeRefresh(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyEpochRefresh)
        {
            throw new ArgumentException($"Expected ConcurrencyEpochRefresh, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyEpochRefreshData(threadSlot, timestamp, BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[4..]));
    }

    public static ConcurrencyEpochSlotClaimData DecodeSlotClaim(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyEpochSlotClaim)
        {
            throw new ArgumentException($"Expected ConcurrencyEpochSlotClaim, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyEpochSlotClaimData(threadSlot, timestamp, BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
    }

    public static ConcurrencyEpochSlotReclaimData DecodeSlotReclaim(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.ConcurrencyEpochSlotReclaim)
        {
            throw new ArgumentException($"Expected ConcurrencyEpochSlotReclaim, got {kind}", nameof(source));
        }
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new ConcurrencyEpochSlotReclaimData(threadSlot, timestamp, BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
    }
}

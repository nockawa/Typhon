using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of an instant event (any kind &lt; 10 on the enum). Instant events do not use the span header extension — they carry only the
/// 12 B common header plus a tiny type-specific payload. Which payload fields are valid depends on <see cref="Kind"/>.
/// </summary>
/// <remarks>
/// <para>
/// Instant kinds and their payloads:
/// <list type="bullet">
///   <item><see cref="TraceEventKind.TickStart"/>: no payload.</item>
///   <item><see cref="TraceEventKind.TickEnd"/>: <c>u8 overloadLevel</c>, <c>u8 tickMultiplier</c> (<see cref="P1"/>, <see cref="P2"/>).</item>
///   <item><see cref="TraceEventKind.PhaseStart"/>, <see cref="TraceEventKind.PhaseEnd"/>: <c>u8 phase</c> (<see cref="P1"/>).</item>
///   <item><see cref="TraceEventKind.SystemReady"/>: <c>u16 systemIdx</c>, <c>u16 predecessorCount</c> (<see cref="P1"/> low / high byte packs).</item>
///   <item><see cref="TraceEventKind.SystemSkipped"/>: <c>u16 systemIdx</c>, <c>u8 skipReason</c>.</item>
///   <item><see cref="TraceEventKind.Instant"/>: <c>i32 nameId</c>, <c>i32 payload</c> (the generic marker variant).</item>
/// </list>
/// </para>
/// <para>
/// All instant events share a minimal decoded struct because the viewer treats them as a flat list of time-stamped markers — there's no ref struct
/// producer API for each kind, just a single static <see cref="InstantEventCodec"/> with <c>Write*</c> methods per kind.
/// </para>
/// </remarks>
public readonly struct InstantEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long Timestamp { get; }

    /// <summary>Primary byte payload — overloadLevel / phase / systemIdx low byte / skipReason / nameId high. Meaning depends on <see cref="Kind"/>.</summary>
    public int P1 { get; }

    /// <summary>Secondary byte payload — tickMultiplier / predecessorCount / systemIdx high byte / generic payload. Meaning depends on <see cref="Kind"/>.</summary>
    public int P2 { get; }

    public InstantEventData(TraceEventKind kind, byte threadSlot, long timestamp, int p1, int p2)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        P1 = p1;
        P2 = p2;
    }
}

/// <summary>
/// Static codec for instant events — single <c>WriteXxx</c> per kind (no ref struct producer API; instants are emitted from scheduler internals
/// via a direct call). Payload layouts are documented on <see cref="InstantEventData"/>.
/// </summary>
public static class InstantEventCodec
{
    /// <summary>Tick start — no payload. Size = 12 B.</summary>
    public static void WriteTickStart(Span<byte> destination, byte threadSlot, long timestamp, out int bytesWritten)
        => WriteHeaderOnly(destination, TraceEventKind.TickStart, threadSlot, timestamp, out bytesWritten);

    /// <summary>Tick end — payload: <c>u8 overloadLevel</c>, <c>u8 tickMultiplier</c>. Size = 14 B.</summary>
    public static void WriteTickEnd(Span<byte> destination, byte threadSlot, long timestamp, byte overloadLevel, byte tickMultiplier, out int bytesWritten)
    {
        const int size = TraceRecordHeader.CommonHeaderSize + 2;
        TraceRecordHeader.WriteCommonHeader(destination, size, TraceEventKind.TickEnd, threadSlot, timestamp);
        destination[TraceRecordHeader.CommonHeaderSize] = overloadLevel;
        destination[TraceRecordHeader.CommonHeaderSize + 1] = tickMultiplier;
        bytesWritten = size;
    }

    /// <summary>Phase start — payload: <c>u8 phase</c>. Size = 13 B.</summary>
    public static void WritePhaseStart(Span<byte> destination, byte threadSlot, long timestamp, TickPhase phase, out int bytesWritten)
        => WritePhaseBoundary(destination, TraceEventKind.PhaseStart, threadSlot, timestamp, phase, out bytesWritten);

    /// <summary>Phase end — payload: <c>u8 phase</c>. Size = 13 B.</summary>
    public static void WritePhaseEnd(Span<byte> destination, byte threadSlot, long timestamp, TickPhase phase, out int bytesWritten)
        => WritePhaseBoundary(destination, TraceEventKind.PhaseEnd, threadSlot, timestamp, phase, out bytesWritten);

    /// <summary>System ready — payload: <c>u16 systemIdx</c>, <c>u16 predecessorCount</c>. Size = 16 B.</summary>
    public static void WriteSystemReady(Span<byte> destination, byte threadSlot, long timestamp, ushort systemIndex, ushort predecessorCount, out int bytesWritten)
    {
        const int size = TraceRecordHeader.CommonHeaderSize + 4;
        TraceRecordHeader.WriteCommonHeader(destination, size, TraceEventKind.SystemReady, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], systemIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(TraceRecordHeader.CommonHeaderSize + 2)..], predecessorCount);
        bytesWritten = size;
    }

    /// <summary>System skipped — payload: <c>u16 systemIdx</c>, <c>u8 skipReason</c>. Size = 15 B.</summary>
    public static void WriteSystemSkipped(Span<byte> destination, byte threadSlot, long timestamp, ushort systemIndex, byte skipReason, out int bytesWritten)
    {
        const int size = TraceRecordHeader.CommonHeaderSize + 3;
        TraceRecordHeader.WriteCommonHeader(destination, size, TraceEventKind.SystemSkipped, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], systemIndex);
        destination[TraceRecordHeader.CommonHeaderSize + 2] = skipReason;
        bytesWritten = size;
    }

    /// <summary>Generic instant marker — payload: <c>i32 nameId</c>, <c>i32 payload</c>. Size = 20 B.</summary>
    public static void WriteInstant(Span<byte> destination, byte threadSlot, long timestamp, int nameId, int payload, out int bytesWritten)
    {
        const int size = TraceRecordHeader.CommonHeaderSize + 8;
        TraceRecordHeader.WriteCommonHeader(destination, size, TraceEventKind.Instant, threadSlot, timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], nameId);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(TraceRecordHeader.CommonHeaderSize + 4)..], payload);
        bytesWritten = size;
    }

    /// <summary>Decode any instant event. Reads the common header + a few payload bytes depending on kind.</summary>
    public static InstantEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        var payload = source[TraceRecordHeader.CommonHeaderSize..];

        int p1 = 0, p2 = 0;
        switch (kind)
        {
            case TraceEventKind.TickStart:
                break;
            case TraceEventKind.TickEnd:
                p1 = payload[0];
                p2 = payload[1];
                break;
            case TraceEventKind.PhaseStart:
            case TraceEventKind.PhaseEnd:
                p1 = payload[0];
                break;
            case TraceEventKind.SystemReady:
                p1 = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                p2 = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
                break;
            case TraceEventKind.SystemSkipped:
                p1 = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                p2 = payload[2];
                break;
            case TraceEventKind.Instant:
                p1 = BinaryPrimitives.ReadInt32LittleEndian(payload);
                p2 = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
                break;
        }

        return new InstantEventData(kind, threadSlot, timestamp, p1, p2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHeaderOnly(Span<byte> destination, TraceEventKind kind, byte threadSlot, long timestamp, out int bytesWritten)
    {
        TraceRecordHeader.WriteCommonHeader(destination, TraceRecordHeader.CommonHeaderSize, kind, threadSlot, timestamp);
        bytesWritten = TraceRecordHeader.CommonHeaderSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePhaseBoundary(Span<byte> destination, TraceEventKind kind, byte threadSlot, long timestamp, TickPhase phase, out int bytesWritten)
    {
        const int size = TraceRecordHeader.CommonHeaderSize + 1;
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, timestamp);
        destination[TraceRecordHeader.CommonHeaderSize] = (byte)phase;
        bytesWritten = size;
    }
}

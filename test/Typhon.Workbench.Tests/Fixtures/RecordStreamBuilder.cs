using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Tests.Fixtures;

/// <summary>
/// Builds raw v3-record byte streams in memory for <c>RecordDecoder</c> tests. Complements
/// <see cref="TraceFixtureBuilder"/> (which writes full .typhon-trace files) — this one returns a
/// size-prefixed record blob directly, with no LZ4 compression / block header / file I/O around it.
///
/// Every method appends to the builder's buffer; call <see cref="Build"/> to get the final byte
/// array and <see cref="RecordCount"/> for the number of records written.
///
/// Only "instant" (kind &lt; 10) records are supported for now — that's enough to exercise
/// tick-counter semantics, malformed-record rejection, and unknown-kind handling. Extend with span
/// emitters if/when we need tests that produce SchedulerChunk / Transaction / BTree records.
/// </summary>
internal sealed class RecordStreamBuilder
{
    private readonly List<byte> _buffer = new(capacity: 1024);

    public int RecordCount { get; private set; }

    /// <summary>Append a <see cref="TraceEventKind.TickStart"/> record.</summary>
    public RecordStreamBuilder TickStart(long timestamp = 100, byte threadSlot = 0)
        => AppendInstant(TraceEventKind.TickStart, timestamp, threadSlot);

    /// <summary>Append a <see cref="TraceEventKind.TickEnd"/> record. Carries 2 bytes of payload
    /// (overloadLevel + tickMultiplier) per the wire format; defaults to zeros.</summary>
    public RecordStreamBuilder TickEnd(long timestamp = 200, byte threadSlot = 0, byte overloadLevel = 0, byte tickMultiplier = 1)
        => AppendInstantWithPayload(TraceEventKind.TickEnd, timestamp, threadSlot, [overloadLevel, tickMultiplier]);

    /// <summary>Append a generic <see cref="TraceEventKind.Instant"/> record (nameId + payload).</summary>
    public RecordStreamBuilder Instant(long timestamp = 150, byte threadSlot = 0, int nameId = 0, int payload = 0)
    {
        Span<byte> pl = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pl, nameId);
        BinaryPrimitives.WriteInt32LittleEndian(pl[4..], payload);
        return AppendInstantWithPayload(TraceEventKind.Instant, timestamp, threadSlot, pl.ToArray());
    }

    /// <summary>Append a <see cref="TraceEventKind.PhaseStart"/> record. Payload: <c>u8 phase</c>.</summary>
    public RecordStreamBuilder PhaseStart(long timestamp = 110, byte threadSlot = 0, byte phase = 0)
        => AppendInstantWithPayload(TraceEventKind.PhaseStart, timestamp, threadSlot, [phase]);

    /// <summary>Append a <see cref="TraceEventKind.PhaseEnd"/> record. Payload: <c>u8 phase</c>.</summary>
    public RecordStreamBuilder PhaseEnd(long timestamp = 180, byte threadSlot = 0, byte phase = 0)
        => AppendInstantWithPayload(TraceEventKind.PhaseEnd, timestamp, threadSlot, [phase]);

    /// <summary>Append a <see cref="TraceEventKind.SystemReady"/> record. Payload: <c>u16 systemIdx</c>, <c>u16 predecessorCount</c>.</summary>
    public RecordStreamBuilder SystemReady(long timestamp = 120, byte threadSlot = 0, ushort systemIdx = 0, ushort predecessorCount = 0)
    {
        Span<byte> pl = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(pl, systemIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(pl[2..], predecessorCount);
        return AppendInstantWithPayload(TraceEventKind.SystemReady, timestamp, threadSlot, pl.ToArray());
    }

    /// <summary>Append a <see cref="TraceEventKind.SystemSkipped"/> record. Payload: <c>u16 systemIdx</c>, <c>u8 skipReason</c>.</summary>
    public RecordStreamBuilder SystemSkipped(long timestamp = 130, byte threadSlot = 0, ushort systemIdx = 0, byte skipReason = 0)
    {
        Span<byte> pl = stackalloc byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(pl, systemIdx);
        pl[2] = skipReason;
        return AppendInstantWithPayload(TraceEventKind.SystemSkipped, timestamp, threadSlot, pl.ToArray());
    }

    /// <summary>Append a record with an unrecognised kind byte. Used for forward-compat tests —
    /// the decoder must skip unknown kinds, not crash.</summary>
    public RecordStreamBuilder UnknownKind(byte kindByte = 250, long timestamp = 100)
    {
        const int size = 12; // common header only
        var start = _buffer.Count;
        _buffer.AddRange(new byte[size]);
        BinaryPrimitives.WriteUInt16LittleEndian(CollectionsMarshalSpan(_buffer, start, 2), size);
        _buffer[start + 2] = kindByte;
        _buffer[start + 3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(CollectionsMarshalSpan(_buffer, start + 4, 8), timestamp);
        RecordCount++;
        return this;
    }

    /// <summary>Append a record whose declared size is SMALLER than the common header. Triggers
    /// the malformed-record fast-exit in <c>DecodeBlock</c>.</summary>
    public RecordStreamBuilder MalformedUndersized()
    {
        const int size = 8; // less than CommonHeaderSize = 12
        var start = _buffer.Count;
        _buffer.AddRange(new byte[size]);
        BinaryPrimitives.WriteUInt16LittleEndian(CollectionsMarshalSpan(_buffer, start, 2), size);
        _buffer[start + 2] = (byte)TraceEventKind.Instant;
        RecordCount++;
        return this;
    }

    public byte[] Build() => _buffer.ToArray();

    // ─── Internals ────────────────────────────────────────────────────────────────────────────

    private RecordStreamBuilder AppendInstant(TraceEventKind kind, long timestamp, byte threadSlot)
    {
        const int size = 12; // common header only
        var start = _buffer.Count;
        _buffer.AddRange(new byte[size]);
        BinaryPrimitives.WriteUInt16LittleEndian(CollectionsMarshalSpan(_buffer, start, 2), size);
        _buffer[start + 2] = (byte)kind;
        _buffer[start + 3] = threadSlot;
        BinaryPrimitives.WriteInt64LittleEndian(CollectionsMarshalSpan(_buffer, start + 4, 8), timestamp);
        RecordCount++;
        return this;
    }

    private RecordStreamBuilder AppendInstantWithPayload(TraceEventKind kind, long timestamp, byte threadSlot, byte[] payload)
    {
        var size = 12 + payload.Length;
        var start = _buffer.Count;
        _buffer.AddRange(new byte[size]);
        BinaryPrimitives.WriteUInt16LittleEndian(CollectionsMarshalSpan(_buffer, start, 2), (ushort)size);
        _buffer[start + 2] = (byte)kind;
        _buffer[start + 3] = threadSlot;
        BinaryPrimitives.WriteInt64LittleEndian(CollectionsMarshalSpan(_buffer, start + 4, 8), timestamp);
        for (var i = 0; i < payload.Length; i++)
        {
            _buffer[start + 12 + i] = payload[i];
        }
        RecordCount++;
        return this;
    }

    // List<byte> doesn't expose a writable Span directly; this helper wraps the internal array.
    // It's fine for tests — allocation pattern dominated by the per-record AddRange anyway.
    private static Span<byte> CollectionsMarshalSpan(List<byte> list, int start, int length)
    {
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(start, length);
    }
}

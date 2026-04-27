using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.SchedulerChunk"/>. Four required fields (SystemIndex, ChunkIndex, TotalChunks,
/// EntitiesProcessed), no optionals. Span duration covers the chunk execution bracket.
/// </summary>
public ref struct SchedulerChunkEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SchedulerChunk;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort SystemIndex;
    public ushort ChunkIndex;
    public ushort TotalChunks;
    public int EntitiesProcessed;

    public readonly int ComputeSize()
        => SchedulerChunkEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SchedulerChunkEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, SystemIndex, ChunkIndex, TotalChunks, EntitiesProcessed, out bytesWritten);

    // Intentionally no Dispose() method: SchedulerChunkEvent is emitted in one shot via TyphonEvent.EmitSchedulerChunk, never via `using var`.
    // Not exposing Dispose prevents a future maintainer from writing `using var chunk = new SchedulerChunkEvent { ... };` and silently dropping
    // the record (the ref struct would go out of scope with no ring publish ever happening).
}


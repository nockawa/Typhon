using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.SchedulerChunk"/>. Four required fields (SystemIndex, ChunkIndex, TotalChunks,
/// EntitiesProcessed), no optionals. Span duration covers the chunk execution bracket.
/// </summary>
[TraceEvent(TraceEventKind.SchedulerChunk, GenerateFactory = false, EmitEncoder = true)]
public ref partial struct SchedulerChunkEvent
{
    public ushort SystemIndex;
    public ushort ChunkIndex;
    public ushort TotalChunks;
    public int EntitiesProcessed;

    // Intentionally no Dispose() method: SchedulerChunkEvent is emitted in one shot via TyphonEvent.EmitSchedulerChunk, never via `using var`.
    // Not exposing Dispose prevents a future maintainer from writing `using var chunk = new SchedulerChunkEvent { ... };` and silently dropping
    // the record (the ref struct would go out of scope with no ring publish ever happening).
}


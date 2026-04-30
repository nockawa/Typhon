using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.ClusterMigration"/>. Two required fields, no optionals.
/// </summary>
public ref struct ClusterMigrationEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.ClusterMigration;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeId;
    public int MigrationCount;
    /// <summary>
    /// Total component instances moved across this batch — set by the producer to <c>MigrationCount × archetype.componentCount</c>.
    /// Lets the viewer report data-movement cost (vs. just entity count). Optional at producer site; left at 0 when unset.
    /// </summary>
    public int ComponentCount;

    public readonly int ComputeSize()
        => ClusterMigrationEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => ClusterMigrationEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeId, MigrationCount, ComponentCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


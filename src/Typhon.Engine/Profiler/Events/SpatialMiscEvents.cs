using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialTierIndexRebuild"/>.</summary>
public ref struct SpatialTierIndexRebuildEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialTierIndexRebuild;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeId;
    public int ClusterCount;
    public int OldVersion;
    public int NewVersion;

    public readonly int ComputeSize() => SpatialTierIndexEventCodec.ComputeSizeRebuild(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialTierIndexEventCodec.EncodeRebuild(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, ArchetypeId, ClusterCount, OldVersion, NewVersion, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialTriggerEval"/>.</summary>
public ref struct SpatialTriggerEvalEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialTriggerEval;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort RegionId;
    public ushort OccupantCount;
    public ushort EnterCount;
    public ushort LeaveCount;

    public readonly int ComputeSize() => SpatialTriggerEventCodec.ComputeSizeEval(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialTriggerEventCodec.EncodeEval(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, RegionId, OccupantCount, EnterCount, LeaveCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

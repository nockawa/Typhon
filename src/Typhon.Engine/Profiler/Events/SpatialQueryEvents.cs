using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryAabb"/>. Stats payload, no coords.</summary>
public ref struct SpatialQueryAabbEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialQueryAabb;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort NodesVisited;
    public ushort LeavesEntered;
    public ushort ResultCount;
    public byte RestartCount;
    public uint CategoryMask;

    public readonly int ComputeSize() => SpatialQueryEventCodec.ComputeSizeAabb(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeAabb(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, LeavesEntered, ResultCount, RestartCount, CategoryMask, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryRadius"/>.</summary>
public ref struct SpatialQueryRadiusEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialQueryRadius;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort NodesVisited;
    public ushort ResultCount;
    public float Radius;
    public byte RestartCount;

    public readonly int ComputeSize() => SpatialQueryEventCodec.ComputeSizeRadius(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeRadius(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, ResultCount, Radius, RestartCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryRay"/>.</summary>
public ref struct SpatialQueryRayEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialQueryRay;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort NodesVisited;
    public ushort ResultCount;
    public float MaxDist;
    public byte RestartCount;

    public readonly int ComputeSize() => SpatialQueryEventCodec.ComputeSizeRay(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeRay(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, ResultCount, MaxDist, RestartCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryFrustum"/>.</summary>
public ref struct SpatialQueryFrustumEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialQueryFrustum;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort NodesVisited;
    public ushort ResultCount;
    public byte PlaneCount;
    public byte RestartCount;

    public readonly int ComputeSize() => SpatialQueryEventCodec.ComputeSizeFrustum(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeFrustum(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, ResultCount, PlaneCount, RestartCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryKnn"/>.</summary>
public ref struct SpatialQueryKnnEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialQueryKnn;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort K;
    public byte IterCount;
    public float FinalRadius;
    public ushort ResultCount;

    public readonly int ComputeSize() => SpatialQueryEventCodec.ComputeSizeKnn(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeKnn(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, K, IterCount, FinalRadius, ResultCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryCount"/>.</summary>
public ref struct SpatialQueryCountEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.SpatialQueryCount;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public byte Variant;
    public ushort NodesVisited;
    public int ResultCount;

    public readonly int ComputeSize() => SpatialQueryEventCodec.ComputeSizeCount(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeCount(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, Variant, NodesVisited, ResultCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort NodesVisited;
    public ushort LeavesEntered;
    public ushort ResultCount;
    public byte RestartCount;
    public uint CategoryMask;

    public readonly int ComputeSize()
    {
        var s = SpatialQueryEventCodec.ComputeSizeAabb(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeAabb(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, LeavesEntered, ResultCount, RestartCount, CategoryMask, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort NodesVisited;
    public ushort ResultCount;
    public float Radius;
    public byte RestartCount;

    public readonly int ComputeSize()
    {
        var s = SpatialQueryEventCodec.ComputeSizeRadius(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeRadius(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, ResultCount, Radius, RestartCount, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort NodesVisited;
    public ushort ResultCount;
    public float MaxDist;
    public byte RestartCount;

    public readonly int ComputeSize()
    {
        var s = SpatialQueryEventCodec.ComputeSizeRay(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeRay(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, ResultCount, MaxDist, RestartCount, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort NodesVisited;
    public ushort ResultCount;
    public byte PlaneCount;
    public byte RestartCount;

    public readonly int ComputeSize()
    {
        var s = SpatialQueryEventCodec.ComputeSizeFrustum(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeFrustum(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, NodesVisited, ResultCount, PlaneCount, RestartCount, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort K;
    public byte IterCount;
    public float FinalRadius;
    public ushort ResultCount;

    public readonly int ComputeSize()
    {
        var s = SpatialQueryEventCodec.ComputeSizeKnn(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeKnn(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, K, IterCount, FinalRadius, ResultCount, out bytesWritten, SourceLocationId);

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

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public byte Variant;
    public ushort NodesVisited;
    public int ResultCount;

    public readonly int ComputeSize()
    {
        var s = SpatialQueryEventCodec.ComputeSizeCount(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => SpatialQueryEventCodec.EncodeCount(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, Variant, NodesVisited, ResultCount, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

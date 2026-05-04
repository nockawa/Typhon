using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

// ═══════════════════════════════════════════════════════════════════════════════════════
// Decoded data types for Spatial query spans (kinds 117-122)
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>Decoded form of a <see cref="TraceEventKind.SpatialQueryAabb"/> record. Payload: <c>nodesVisited u16, leavesEntered u16, resultCount u16, restartCount u8, categoryMask u32</c> (11 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryAabbData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ushort NodesVisited { get; }
    public ushort LeavesEntered { get; }
    public ushort ResultCount { get; }
    public byte RestartCount { get; }
    public uint CategoryMask { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialQueryAabbData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ushort nodesVisited, ushort leavesEntered, ushort resultCount, byte restartCount, uint categoryMask, ushort srcLoc = 0)
    { 
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        NodesVisited = nodesVisited;
        LeavesEntered = leavesEntered;
        ResultCount = resultCount;
        RestartCount = restartCount;
        CategoryMask = categoryMask; SourceLocationId = srcLoc; }
}

/// <summary>Decoded form of a Radius query record. Payload: <c>nodesVisited u16, resultCount u16, radius f32, restartCount u8</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryRadiusData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort NodesVisited { get; }
    public ushort ResultCount { get; }
    public float Radius { get; }
    public byte RestartCount { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialQueryRadiusData(byte threadSlot, long startTimestamp, long durationTicks, ushort nodesVisited, ushort resultCount, float radius, byte restartCount, ushort srcLoc = 0)
    { 
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
        Radius = radius;
        RestartCount = restartCount; SourceLocationId = srcLoc; }
}

/// <summary>Decoded form of a Ray query record. Payload: <c>nodesVisited u16, resultCount u16, maxDist f32, restartCount u8</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryRayData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort NodesVisited { get; }
    public ushort ResultCount { get; }
    public float MaxDist { get; }
    public byte RestartCount { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialQueryRayData(byte threadSlot, long startTimestamp, long durationTicks, ushort nodesVisited, ushort resultCount, float maxDist, byte restartCount, ushort srcLoc = 0)
    { 
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
        MaxDist = maxDist;
        RestartCount = restartCount; SourceLocationId = srcLoc; }
}

/// <summary>Decoded form of a Frustum query record. Payload: <c>nodesVisited u16, resultCount u16, planeCount u8, restartCount u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryFrustumData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort NodesVisited { get; }
    public ushort ResultCount { get; }
    public byte PlaneCount { get; }
    public byte RestartCount { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialQueryFrustumData(byte threadSlot, long startTimestamp, long durationTicks, ushort nodesVisited, ushort resultCount, byte planeCount, byte restartCount, ushort srcLoc = 0)
    { 
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
        PlaneCount = planeCount;
        RestartCount = restartCount; SourceLocationId = srcLoc; }
}

/// <summary>Decoded form of a KNN query record. Payload: <c>k u16, iterCount u8, finalRadius f32, resultCount u16</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryKnnData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort K { get; }
    public byte IterCount { get; }
    public float FinalRadius { get; }
    public ushort ResultCount { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialQueryKnnData(byte threadSlot, long startTimestamp, long durationTicks, ushort k, byte iterCount, float finalRadius, ushort resultCount, ushort srcLoc = 0)
    { 
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        K = k;
        IterCount = iterCount;
        FinalRadius = finalRadius;
        ResultCount = resultCount; SourceLocationId = srcLoc; }
}

/// <summary>Decoded form of a Count query record. Payload: <c>variant u8 (0=AABB,1=Radius), nodesVisited u16, resultCount i32</c> (7 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryCountData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte Variant { get; }
    public ushort NodesVisited { get; }
    public int ResultCount { get; }

    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public SpatialQueryCountData(byte threadSlot, long startTimestamp, long durationTicks, byte variant, ushort nodesVisited, int resultCount, ushort srcLoc = 0)
    { 
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        Variant = variant;
        NodesVisited = nodesVisited;
        ResultCount = resultCount; SourceLocationId = srcLoc; }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Wire codec
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Wire codec for Spatial query span records (kinds 117-122). All spans: 12-byte common header + 25-byte span extension + payload.
/// </summary>
public static class SpatialQueryEventCodec
{
    private const int AabbPayloadSize = 2 + 2 + 2 + 1 + 4;     // 11
    private const int RadiusPayloadSize = 2 + 2 + 4 + 1;       // 9
    private const int RayPayloadSize = 2 + 2 + 4 + 1;          // 9
    private const int FrustumPayloadSize = 2 + 2 + 1 + 1;      // 6
    private const int KnnPayloadSize = 2 + 1 + 4 + 2;          // 9
    private const int CountPayloadSize = 1 + 2 + 4;            // 7

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeAabb(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + AabbPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeRadius(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RadiusPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeRay(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RayPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeFrustum(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + FrustumPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeKnn(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + KnnPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeCount(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + CountPayloadSize;

    public static void EncodeAabb(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort nodesVisited, ushort leavesEntered, ushort resultCount, byte restartCount, uint categoryMask, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeAabb(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.SpatialQueryAabb, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
        var payload = destination[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, nodesVisited);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[2..], leavesEntered);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], resultCount);
        payload[6] = restartCount;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[7..], categoryMask);
        bytesWritten = size;
    }

    public static SpatialQueryAabbData DecodeAabb(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryAabbData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
            payload[6],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[7..]), sourceLocationId);
    }

    public static void EncodeRadius(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort nodesVisited, ushort resultCount, float radius, byte restartCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeRadius(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.SpatialQueryRadius, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
        var payload = destination[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, nodesVisited);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[2..], resultCount);
        BinaryPrimitives.WriteSingleLittleEndian(payload[4..], radius);
        payload[8] = restartCount;
        bytesWritten = size;
    }

    public static SpatialQueryRadiusData DecodeRadius(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryRadiusData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[4..]),
            payload[8], sourceLocationId);
    }

    public static void EncodeRay(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort nodesVisited, ushort resultCount, float maxDist, byte restartCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeRay(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.SpatialQueryRay, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
        var payload = destination[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, nodesVisited);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[2..], resultCount);
        BinaryPrimitives.WriteSingleLittleEndian(payload[4..], maxDist);
        payload[8] = restartCount;
        bytesWritten = size;
    }

    public static SpatialQueryRayData DecodeRay(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryRayData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[4..]),
            payload[8], sourceLocationId);
    }

    public static void EncodeFrustum(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort nodesVisited, ushort resultCount, byte planeCount, byte restartCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeFrustum(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.SpatialQueryFrustum, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
        var payload = destination[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, nodesVisited);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[2..], resultCount);
        payload[4] = planeCount;
        payload[5] = restartCount;
        bytesWritten = size;
    }

    public static SpatialQueryFrustumData DecodeFrustum(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryFrustumData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            payload[4], payload[5], sourceLocationId);
    }

    public static void EncodeKnn(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort k, byte iterCount, float finalRadius, ushort resultCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeKnn(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.SpatialQueryKnn, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
        var payload = destination[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, k);
        payload[2] = iterCount;
        BinaryPrimitives.WriteSingleLittleEndian(payload[3..], finalRadius);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[7..], resultCount);
        bytesWritten = size;
    }

    public static SpatialQueryKnnData DecodeKnn(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryKnnData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            payload[2],
            BinaryPrimitives.ReadSingleLittleEndian(payload[3..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[7..]), sourceLocationId);
    }

    public static void EncodeCount(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        byte variant, ushort nodesVisited, int resultCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeCount(hasTraceContext);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.SpatialQueryCount, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }
        var payload = destination[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        payload[0] = variant;
        BinaryPrimitives.WriteUInt16LittleEndian(payload[1..], nodesVisited);
        BinaryPrimitives.WriteInt32LittleEndian(payload[3..], resultCount);
        bytesWritten = size;
    }

    public static SpatialQueryCountData DecodeCount(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryCountData(threadSlot, startTimestamp, durationTicks,
            payload[0],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[3..]), sourceLocationId);
    }
}

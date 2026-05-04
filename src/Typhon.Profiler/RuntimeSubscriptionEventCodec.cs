using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

[PublicAPI]
public readonly struct RuntimeSubscriptionSubscriberData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint SubscriberId { get; }
    public ushort ViewId { get; }
    public int DeltaCount { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionSubscriberData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        uint subId, ushort vi, int dc, ushort srcLoc = 0)
    {  ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; SubscriberId = subId; ViewId = vi; DeltaCount = dc; SourceLocationId = srcLoc; }
}

[PublicAPI]
public readonly struct RuntimeSubscriptionDeltaBuildData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort ViewId { get; }
    public int Added { get; }
    public int Removed { get; }
    public int Modified { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionDeltaBuildData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        ushort vi, int a, int r, int m, ushort srcLoc = 0)
    {  ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ViewId = vi; Added = a; Removed = r; Modified = m; SourceLocationId = srcLoc; }
}

[PublicAPI]
public readonly struct RuntimeSubscriptionDeltaSerializeData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint ClientId { get; }
    public ushort ViewId { get; }
    public int Bytes { get; }
    public byte Format { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionDeltaSerializeData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        uint cid, ushort vi, int b, byte f, ushort srcLoc = 0)
    {  ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ClientId = cid; ViewId = vi; Bytes = b; Format = f; SourceLocationId = srcLoc; }
}

[PublicAPI]
public readonly struct RuntimeSubscriptionTransitionBeginSyncData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint ClientId { get; }
    public ushort ViewId { get; }
    public int EntitySnapshot { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionTransitionBeginSyncData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        uint cid, ushort vi, int es, ushort srcLoc = 0)
    {  ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ClientId = cid; ViewId = vi; EntitySnapshot = es; SourceLocationId = srcLoc; }
}

[PublicAPI]
public readonly struct RuntimeSubscriptionOutputCleanupData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int DeadCount { get; }
    public int DeregCount { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionOutputCleanupData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        int dc, int rc, ushort srcLoc = 0)
    {  ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; DeadCount = dc; DeregCount = rc; SourceLocationId = srcLoc; }
}

[PublicAPI]
public readonly struct RuntimeSubscriptionDeltaDirtyBitmapSupplementData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int ModifiedFromRing { get; }
    public int SupplementCount { get; }
    public int UnionSize { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionDeltaDirtyBitmapSupplementData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        int mfr, int sc, int us, ushort srcLoc = 0)
    {  ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ModifiedFromRing = mfr; SupplementCount = sc; UnionSize = us; SourceLocationId = srcLoc; }
}

public static class RuntimeSubscriptionEventCodec
{
    private const int SubscriberPayload    = 4 + 2 + 4;             // 10
    private const int DeltaBuildPayload    = 2 + 4 + 4 + 4;         // 14
    private const int DeltaSerializePayload = 4 + 2 + 4 + 1;        // 11
    private const int TransitionPayload    = 4 + 2 + 4;             // 10
    private const int OutputCleanupPayload = 4 + 4;                  // 8
    private const int DirtyBitmapSupplementPayload = 4 + 4 + 4;     // 12

    public static int ComputeSizeSubscriber(bool hasTC)             => TraceRecordHeader.SpanHeaderSize(hasTC) + SubscriberPayload;
    public static int ComputeSizeDeltaBuild(bool hasTC)             => TraceRecordHeader.SpanHeaderSize(hasTC) + DeltaBuildPayload;
    public static int ComputeSizeDeltaSerialize(bool hasTC)         => TraceRecordHeader.SpanHeaderSize(hasTC) + DeltaSerializePayload;
    public static int ComputeSizeTransitionBeginSync(bool hasTC)    => TraceRecordHeader.SpanHeaderSize(hasTC) + TransitionPayload;
    public static int ComputeSizeOutputCleanup(bool hasTC)          => TraceRecordHeader.SpanHeaderSize(hasTC) + OutputCleanupPayload;
    public static int ComputeSizeDirtyBitmapSupplement(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + DirtyBitmapSupplementPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTC, ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTC ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTC)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTC)..], sourceLocationId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadSpanPreamble(ReadOnlySpan<byte> source,
        out byte threadSlot, out long startTs, out long dur, out ulong spanId, out ulong parentSpanId,
        out ulong traceIdHi, out ulong traceIdLo, out ushort sourceLocationId)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out threadSlot, out startTs);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out dur, out spanId, out parentSpanId, out var spanFlags);
        traceIdHi = 0; traceIdLo = 0;
        sourceLocationId = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
        }
        return source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
    }

    // ── Subscriber ──
    public static void EncodeSubscriber(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        uint subscriberId, ushort viewId, int deltaCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeSubscriber(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionSubscriber, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, subscriberId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], deltaCount);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionSubscriberData DecodeSubscriber(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo, out var srcLoc);
        return new RuntimeSubscriptionSubscriberData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            srcLoc);
    }

    // ── DeltaBuild ──
    public static void EncodeDeltaBuild(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort viewId, int added, int removed, int modified, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeDeltaBuild(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionDeltaBuild, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], added);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], removed);
        BinaryPrimitives.WriteInt32LittleEndian(p[10..], modified);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionDeltaBuildData DecodeDeltaBuild(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo, out var srcLoc);
        return new RuntimeSubscriptionDeltaBuildData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[10..]),
            srcLoc);
    }

    // ── DeltaSerialize ──
    public static void EncodeDeltaSerialize(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        uint clientId, ushort viewId, int bytes, byte format, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeDeltaSerialize(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionDeltaSerialize, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, clientId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], bytes);
        p[10] = format;
        bytesWritten = size;
    }

    public static RuntimeSubscriptionDeltaSerializeData DecodeDeltaSerialize(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo, out var srcLoc);
        return new RuntimeSubscriptionDeltaSerializeData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            p[10],
            srcLoc);
    }

    // ── TransitionBeginSync ──
    public static void EncodeTransitionBeginSync(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        uint clientId, ushort viewId, int entitySnapshot, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeTransitionBeginSync(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionTransitionBeginSync, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, clientId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], entitySnapshot);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionTransitionBeginSyncData DecodeTransitionBeginSync(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo, out var srcLoc);
        return new RuntimeSubscriptionTransitionBeginSyncData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            srcLoc);
    }

    // ── OutputCleanup ──
    public static void EncodeOutputCleanup(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int deadCount, int deregCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeOutputCleanup(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionOutputCleanup, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt32LittleEndian(p, deadCount);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], deregCount);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionOutputCleanupData DecodeOutputCleanup(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo, out var srcLoc);
        return new RuntimeSubscriptionOutputCleanupData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            srcLoc);
    }

    // ── DirtyBitmapSupplement ──
    public static void EncodeDirtyBitmapSupplement(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int modifiedFromRing, int supplementCount, int unionSize, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeDirtyBitmapSupplement(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt32LittleEndian(p, modifiedFromRing);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], supplementCount);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], unionSize);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionDeltaDirtyBitmapSupplementData DecodeDirtyBitmapSupplement(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo, out var srcLoc);
        return new RuntimeSubscriptionDeltaDirtyBitmapSupplementData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
            srcLoc);
    }
}

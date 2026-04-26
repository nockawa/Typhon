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
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionSubscriberData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        uint subId, ushort vi, int dc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; SubscriberId = subId; ViewId = vi; DeltaCount = dc; }
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
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionDeltaBuildData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        ushort vi, int a, int r, int m)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ViewId = vi; Added = a; Removed = r; Modified = m; }
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
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionDeltaSerializeData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        uint cid, ushort vi, int b, byte f)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ClientId = cid; ViewId = vi; Bytes = b; Format = f; }
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
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionTransitionBeginSyncData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        uint cid, ushort vi, int es)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ClientId = cid; ViewId = vi; EntitySnapshot = es; }
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
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionOutputCleanupData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        int dc, int rc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; DeadCount = dc; DeregCount = rc; }
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
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public RuntimeSubscriptionDeltaDirtyBitmapSupplementData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo,
        int mfr, int sc, int us)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ModifiedFromRing = mfr; SupplementCount = sc; UnionSize = us; }
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
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTC)
    {
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTC ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTC)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadSpanPreamble(ReadOnlySpan<byte> source,
        out byte threadSlot, out long startTs, out long dur, out ulong spanId, out ulong parentSpanId,
        out ulong traceIdHi, out ulong traceIdLo)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out threadSlot, out startTs);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out dur, out spanId, out parentSpanId, out var spanFlags);
        traceIdHi = 0; traceIdLo = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        return source[TraceRecordHeader.SpanHeaderSize(hasTC)..];
    }

    // ── Subscriber ──
    public static void EncodeSubscriber(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        uint subscriberId, ushort viewId, int deltaCount, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeSubscriber(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionSubscriber, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, subscriberId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], deltaCount);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionSubscriberData DecodeSubscriber(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new RuntimeSubscriptionSubscriberData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]));
    }

    // ── DeltaBuild ──
    public static void EncodeDeltaBuild(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort viewId, int added, int removed, int modified, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeDeltaBuild(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionDeltaBuild, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], added);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], removed);
        BinaryPrimitives.WriteInt32LittleEndian(p[10..], modified);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionDeltaBuildData DecodeDeltaBuild(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new RuntimeSubscriptionDeltaBuildData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[10..]));
    }

    // ── DeltaSerialize ──
    public static void EncodeDeltaSerialize(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        uint clientId, ushort viewId, int bytes, byte format, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeDeltaSerialize(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionDeltaSerialize, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, clientId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], bytes);
        p[10] = format;
        bytesWritten = size;
    }

    public static RuntimeSubscriptionDeltaSerializeData DecodeDeltaSerialize(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new RuntimeSubscriptionDeltaSerializeData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            p[10]);
    }

    // ── TransitionBeginSync ──
    public static void EncodeTransitionBeginSync(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        uint clientId, ushort viewId, int entitySnapshot, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeTransitionBeginSync(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionTransitionBeginSync, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, clientId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], viewId);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], entitySnapshot);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionTransitionBeginSyncData DecodeTransitionBeginSync(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new RuntimeSubscriptionTransitionBeginSyncData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]));
    }

    // ── OutputCleanup ──
    public static void EncodeOutputCleanup(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int deadCount, int deregCount, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeOutputCleanup(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionOutputCleanup, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteInt32LittleEndian(p, deadCount);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], deregCount);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionOutputCleanupData DecodeOutputCleanup(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new RuntimeSubscriptionOutputCleanupData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    // ── DirtyBitmapSupplement ──
    public static void EncodeDirtyBitmapSupplement(Span<byte> destination, long endTs, byte threadSlot, long startTs,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int modifiedFromRing, int supplementCount, int unionSize, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeDirtyBitmapSupplement(hasTC);
        WriteSpanPreamble(destination, TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement, (ushort)size, threadSlot, startTs, endTs - startTs,
            spanId, parentSpanId, traceIdHi, traceIdLo, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteInt32LittleEndian(p, modifiedFromRing);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], supplementCount);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], unionSize);
        bytesWritten = size;
    }

    public static RuntimeSubscriptionDeltaDirtyBitmapSupplementData DecodeDirtyBitmapSupplement(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new RuntimeSubscriptionDeltaDirtyBitmapSupplementData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }
}

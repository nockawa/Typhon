using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of any page-cache span event. Covers six page-cache kinds: <see cref="TraceEventKind.PageCacheFetch"/>,
/// <see cref="TraceEventKind.PageCacheDiskRead"/>, <see cref="TraceEventKind.PageCacheDiskWrite"/>,
/// <see cref="TraceEventKind.PageCacheAllocatePage"/>, <see cref="TraceEventKind.PageCacheFlush"/>, and <see cref="TraceEventKind.PageEvicted"/>
/// (zero-duration marker span). Which of <see cref="FilePageIndex"/> and <see cref="PageCount"/> are set depends on the kind.
/// </summary>
public readonly struct PageCacheEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required for Fetch/DiskRead/DiskWrite/AllocatePage; meaningless for Flush.</summary>
    public int FilePageIndex { get; }

    /// <summary>Required for Flush; optional for DiskWrite (contiguous run length); meaningless for Fetch/DiskRead/AllocatePage.</summary>
    public int PageCount { get; }

    public byte OptionalFieldMask { get; }

    public bool HasPageCount => (OptionalFieldMask & PageCacheEventCodec.OptPageCount) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public PageCacheEventData(TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int filePageIndex, int pageCount, byte optionalFieldMask)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        FilePageIndex = filePageIndex;
        PageCount = pageCount;
        OptionalFieldMask = optionalFieldMask;
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.PageCacheBackpressure"/> event.</summary>
public readonly struct PageCacheBackpressureEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int RetryCount { get; }
    public int DirtyCount { get; }
    public int EpochCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public PageCacheBackpressureEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, int retryCount, int dirtyCount, int epochCount)
    {
        ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks;
        SpanId = spanId; ParentSpanId = parentSpanId; TraceIdHi = traceIdHi; TraceIdLo = traceIdLo;
        RetryCount = retryCount; DirtyCount = dirtyCount; EpochCount = epochCount;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.PageCacheBackpressure"/>. Payload: <c>[i32 retryCount][i32 dirtyCount][i32 epochCount]</c>.</summary>
public static class PageCacheBackpressureCodec
{
    private const int PayloadSize = 12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    internal static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int retryCount, int dirtyCount, int epochCount, out int bytesWritten)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSize(hasTraceContext);
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.PageCacheBackpressure, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);
        if (hasTraceContext) TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        var payload = destination[headerSize..];
        BinaryPrimitives.WriteInt32LittleEndian(payload, retryCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], dirtyCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload[8..], epochCount);
        bytesWritten = size;
    }

    public static PageCacheBackpressureEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0)
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        var headerSize = TraceRecordHeader.SpanHeaderSize((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0);
        var payload = source[headerSize..];
        var retryCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var dirtyCount = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        var epochCount = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        return new PageCacheBackpressureEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId,
            traceIdHi, traceIdLo, retryCount, dirtyCount, epochCount);
    }
}

/// <summary>
/// Shared wire codec for all five page-cache event kinds. Every kind writes a 4-byte "primary value" slot (<c>FilePageIndex</c> for most,
/// <c>PageCount</c> for Flush) plus a 1-byte <c>optMask</c>, plus an optional 4-byte secondary <c>PageCount</c> for DiskWrite.
/// </summary>
public static class PageCacheEventCodec
{
    /// <summary>Optional-mask bit 0 — <c>PageCount</c> on DiskWrite (contiguous run length).</summary>
    public const byte OptPageCount = 0x01;

    private const int FilePageIndexSize = 4;
    private const int OptMaskSize = 1;
    private const int PageCountSize = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(TraceEventKind kind, bool hasTraceContext, byte optMask)
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext) + FilePageIndexSize + OptMaskSize;
        if ((optMask & OptPageCount) != 0) size += PageCountSize;
        return size;
    }

    internal static void Encode(Span<byte> destination, long endTimestamp, TraceEventKind kind, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int filePageIndex, int pageCount, byte optMask, out int bytesWritten)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSize(kind, hasTraceContext, optMask);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        var payload = destination[headerSize..];
        BinaryPrimitives.WriteInt32LittleEndian(payload, filePageIndex);
        payload[FilePageIndexSize] = optMask;
        var cursor = FilePageIndexSize + OptMaskSize;

        if ((optMask & OptPageCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], pageCount);
            cursor += PageCountSize;
        }

        bytesWritten = size;
    }

    public static PageCacheEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        ulong traceIdHi = 0, traceIdLo = 0;
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize((spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0);
        var payload = source[headerSize..];
        var filePageIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var optMask = payload[FilePageIndexSize];
        var cursor = FilePageIndexSize + OptMaskSize;

        int pageCount = 0;
        if ((optMask & OptPageCount) != 0)
        {
            pageCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += PageCountSize;
        }

        return new PageCacheEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            filePageIndex, pageCount, optMask);
    }
}


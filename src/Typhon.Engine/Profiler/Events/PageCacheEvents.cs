using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side <c>ref struct</c> for <see cref="TraceEventKind.PageCacheFetch"/>. Required: <c>FilePageIndex</c>. No optionals.
/// </summary>
/// <remarks>
/// Used via <c>using var</c> on the begin thread only — PagedMMF no longer captures fetch scopes in <c>ContinueWith</c> lambdas, so there's
/// no need for the plain-struct shape that the old async-completion path required. Same-thread dispose means <see cref="TyphonEvent.PublishEvent"/>
/// can publish directly to the captured slot with a proper TLS unwind.
/// </remarks>
public ref struct PageCacheFetchEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.PageCacheFetch;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int FilePageIndex;

    public readonly int ComputeSize()
    {
        var s = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFetch, TraceIdHi != 0 || TraceIdLo != 0, 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheFetch, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FilePageIndex, 0, 0, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Producer-side <c>ref struct</c> for <see cref="TraceEventKind.PageCacheDiskRead"/>. Required: <c>FilePageIndex</c>. No optionals.
/// Same-thread dispose via <see cref="TyphonEvent.PublishEvent"/>.
/// </summary>
public ref struct PageCacheDiskReadEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.PageCacheDiskRead;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int FilePageIndex;

    public readonly int ComputeSize()
    {
        var s = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskRead, TraceIdHi != 0 || TraceIdLo != 0, 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheDiskRead, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FilePageIndex, 0, 0, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct PageCacheDiskWriteEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.PageCacheDiskWrite;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;

    public int FilePageIndex;

    private int _pageCount;
    private byte _optMask;

    public int PageCount
    {
        readonly get => _pageCount;
        set { _pageCount = value; _optMask |= PageCacheEventCodec.OptPageCount; }
    }

    public readonly int ComputeSize()
    {
        var s = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskWrite, TraceIdHi != 0 || TraceIdLo != 0, _optMask);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheDiskWrite, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FilePageIndex, _pageCount, _optMask, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct PageCacheAllocatePageEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.PageCacheAllocatePage;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int FilePageIndex;

    public readonly int ComputeSize()
    {
        var s = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheAllocatePage, TraceIdHi != 0 || TraceIdLo != 0, 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheAllocatePage, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FilePageIndex, 0, 0, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct PageCacheFlushEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.PageCacheFlush;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int PageCount;

    public readonly int ComputeSize()
    {
        var s = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFlush, TraceIdHi != 0 || TraceIdLo != 0, 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheFlush, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, PageCount, 0, 0, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>
/// Page cache backpressure wait — clock-sweep retry loop couldn't find a free page. Payload: 3 × i32 diagnostic counters.
/// </summary>
public ref struct PageCacheBackpressureEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.PageCacheBackpressure;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int RetryCount;
    public int DirtyCount;
    public int EpochCount;

    public readonly int ComputeSize()
        => PageCacheBackpressureCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheBackpressureCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, RetryCount, DirtyCount, EpochCount, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


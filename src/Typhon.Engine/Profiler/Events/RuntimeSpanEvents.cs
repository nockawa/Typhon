using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>.</summary>
public ref struct RuntimeTransactionLifecycleEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeTransactionLifecycle;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public ushort SysIdx;
    public uint TxDurUs;
    public byte Success;

    public readonly int ComputeSize()
    {
        var s = RuntimeEventCodec.ComputeSizeLifecycle(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeEventCodec.EncodeLifecycle(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, SysIdx, TxDurUs, Success, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeSubscriptionOutputExecute"/>.</summary>
public ref struct RuntimeSubscriptionOutputExecuteEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionOutputExecute;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public long Tick;
    public byte Level;
    public ushort ClientCount;
    public ushort ViewsRefreshed;
    public uint DeltasPushed;
    public ushort OverflowCount;

    public readonly int ComputeSize()
    {
        var s = RuntimeEventCodec.ComputeSizeOutputExecute(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeEventCodec.EncodeOutputExecute(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, Tick, Level, ClientCount, ViewsRefreshed, DeltasPushed, OverflowCount, out bytesWritten, SourceLocationId);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

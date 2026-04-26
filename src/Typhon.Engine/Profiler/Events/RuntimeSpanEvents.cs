using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>.</summary>
public ref struct RuntimeTransactionLifecycleEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort SysIdx;
    public uint TxDurUs;
    public byte Success;

    public readonly int ComputeSize() => RuntimeEventCodec.ComputeSizeLifecycle(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeEventCodec.EncodeLifecycle(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, SysIdx, TxDurUs, Success, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeSubscriptionOutputExecute"/>.</summary>
public ref struct RuntimeSubscriptionOutputExecuteEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public long Tick;
    public byte Level;
    public ushort ClientCount;
    public ushort ViewsRefreshed;
    public uint DeltasPushed;
    public ushort OverflowCount;

    public readonly int ComputeSize() => RuntimeEventCodec.ComputeSizeOutputExecute(TraceIdHi != 0 || TraceIdLo != 0);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeEventCodec.EncodeOutputExecute(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId,
            TraceIdHi, TraceIdLo, Tick, Level, ClientCount, ViewsRefreshed, DeltasPushed, OverflowCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

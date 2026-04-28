using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 9 Subscription dispatch ref structs (all spans).
// ═════════════════════════════════════════════════════════════════════════════

public ref struct RuntimeSubscriptionSubscriberEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionSubscriber;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public uint SubscriberId;
    public ushort ViewId;
    public int DeltaCount;
    public readonly int ComputeSize() => RuntimeSubscriptionEventCodec.ComputeSizeSubscriber(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeSubscriptionEventCodec.EncodeSubscriber(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            SubscriberId, ViewId, DeltaCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct RuntimeSubscriptionDeltaBuildEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionDeltaBuild;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public ushort ViewId;
    public int Added;
    public int Removed;
    public int Modified;
    public readonly int ComputeSize() => RuntimeSubscriptionEventCodec.ComputeSizeDeltaBuild(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeSubscriptionEventCodec.EncodeDeltaBuild(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            ViewId, Added, Removed, Modified, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct RuntimeSubscriptionDeltaSerializeEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionDeltaSerialize;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public uint ClientId;
    public ushort ViewId;
    public int Bytes;
    public byte Format;
    public readonly int ComputeSize() => RuntimeSubscriptionEventCodec.ComputeSizeDeltaSerialize(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeSubscriptionEventCodec.EncodeDeltaSerialize(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            ClientId, ViewId, Bytes, Format, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct RuntimeSubscriptionTransitionBeginSyncEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionTransitionBeginSync;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public uint ClientId;
    public ushort ViewId;
    public int EntitySnapshot;
    public readonly int ComputeSize() => RuntimeSubscriptionEventCodec.ComputeSizeTransitionBeginSync(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeSubscriptionEventCodec.EncodeTransitionBeginSync(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            ClientId, ViewId, EntitySnapshot, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct RuntimeSubscriptionOutputCleanupEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionOutputCleanup;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int DeadCount;
    public int DeregCount;
    public readonly int ComputeSize() => RuntimeSubscriptionEventCodec.ComputeSizeOutputCleanup(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeSubscriptionEventCodec.EncodeOutputCleanup(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            DeadCount, DeregCount, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    public int ModifiedFromRing;
    public int SupplementCount;
    public int UnionSize;
    public readonly int ComputeSize() => RuntimeSubscriptionEventCodec.ComputeSizeDirtyBitmapSupplement(TraceIdHi != 0 || TraceIdLo != 0);
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => RuntimeSubscriptionEventCodec.EncodeDirtyBitmapSupplement(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo,
            ModifiedFromRing, SupplementCount, UnionSize, out bytesWritten);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

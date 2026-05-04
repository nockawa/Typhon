using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 8 Durability ref structs (span events only — instants emit via EmitX).
// ═════════════════════════════════════════════════════════════════════════════

// ── WAL spans ──

public ref struct DurabilityWalQueueDrainEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityWalQueueDrain;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int BytesAligned;
    public int FrameCount;
    public readonly int ComputeSize()
    {
        var s = DurabilityWalEventCodec.ComputeSizeQueueDrain(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityWalEventCodec.EncodeQueueDrain(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, BytesAligned, FrameCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityWalOsWriteEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityWalOsWrite;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int BytesAligned;
    public int FrameCount;
    public long HighLsn;
    public readonly int ComputeSize()
    {
        var s = DurabilityWalEventCodec.ComputeSizeOsWrite(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityWalEventCodec.EncodeOsWrite(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, BytesAligned, FrameCount, HighLsn, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityWalSignalEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityWalSignal;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public long HighLsn;
    public readonly int ComputeSize()
    {
        var s = DurabilityWalEventCodec.ComputeSizeSignal(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityWalEventCodec.EncodeSignal(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, HighLsn, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityWalBufferEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityWalBuffer;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int BytesAligned;
    public int Pad;
    public readonly int ComputeSize()
    {
        var s = DurabilityWalEventCodec.ComputeSizeBuffer(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityWalEventCodec.EncodeBuffer(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, BytesAligned, Pad, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityWalBackpressureEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityWalBackpressure;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public uint WaitUs;
    public int ProducerThread;
    public readonly int ComputeSize()
    {
        var s = DurabilityWalEventCodec.ComputeSizeBackpressure(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityWalEventCodec.EncodeBackpressure(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, WaitUs, ProducerThread, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ── Checkpoint depth spans ──

public ref struct DurabilityCheckpointWriteBatchEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityCheckpointWriteBatch;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int WriteBatchSize;
    public int StagingAllocated;
    public readonly int ComputeSize()
    {
        var s = DurabilityCheckpointEventCodec.ComputeSizeWriteBatch(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityCheckpointEventCodec.EncodeWriteBatch(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, WriteBatchSize, StagingAllocated, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityCheckpointBackpressureEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityCheckpointBackpressure;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public uint WaitMs;
    public byte Exhausted;
    public readonly int ComputeSize()
    {
        var s = DurabilityCheckpointEventCodec.ComputeSizeBackpressure(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityCheckpointEventCodec.EncodeBackpressure(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, WaitMs, Exhausted, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityCheckpointSleepEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityCheckpointSleep;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public uint SleepMs;
    public byte WakeReason;
    public readonly int ComputeSize()
    {
        var s = DurabilityCheckpointEventCodec.ComputeSizeSleep(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityCheckpointEventCodec.EncodeSleep(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, SleepMs, WakeReason, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ── Recovery spans ──

public ref struct DurabilityRecoveryDiscoverEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityRecoveryDiscover;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int SegCount;
    public long TotalBytes;
    public int FirstSegId;
    public readonly int ComputeSize()
    {
        var s = DurabilityRecoveryEventCodec.ComputeSizeDiscover(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityRecoveryEventCodec.EncodeDiscover(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, SegCount, TotalBytes, FirstSegId, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityRecoverySegmentEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityRecoverySegment;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int SegId;
    public int RecCount;
    public long Bytes;
    public byte Truncated;
    public readonly int ComputeSize()
    {
        var s = DurabilityRecoveryEventCodec.ComputeSizeSegment(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityRecoveryEventCodec.EncodeSegment(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, SegId, RecCount, Bytes, Truncated, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityRecoveryFpiEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityRecoveryFpi;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int FpiCount;
    public int RepairedCount;
    public int Mismatches;
    public readonly int ComputeSize()
    {
        var s = DurabilityRecoveryEventCodec.ComputeSizeFpi(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityRecoveryEventCodec.EncodeFpi(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, FpiCount, RepairedCount, Mismatches, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityRecoveryRedoEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityRecoveryRedo;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int RecordsReplayed;
    public int UowsReplayed;
    public uint DurUs;
    public readonly int ComputeSize()
    {
        var s = DurabilityRecoveryEventCodec.ComputeSizeRedo(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityRecoveryEventCodec.EncodeRedo(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, RecordsReplayed, UowsReplayed, DurUs, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityRecoveryUndoEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityRecoveryUndo;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int VoidedUowCount;
    public readonly int ComputeSize()
    {
        var s = DurabilityRecoveryEventCodec.ComputeSizeUndo(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityRecoveryEventCodec.EncodeUndo(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, VoidedUowCount, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct DurabilityRecoveryTickFenceEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.DurabilityRecoveryTickFence;

    public byte ThreadSlot; public long StartTimestamp; public ulong SpanId; public ulong ParentSpanId; public ulong PreviousSpanId; public ulong TraceIdHi; public ulong TraceIdLo;
    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>
    internal ushort SourceLocationId;
    public int TickFenceCount;
    public int Entries;
    public long TickNumber;
    public readonly int ComputeSize()
    {
        var s = DurabilityRecoveryEventCodec.ComputeSizeTickFence(TraceIdHi != 0 || TraceIdLo != 0);
        if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;
        return s;
    }
    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => DurabilityRecoveryEventCodec.EncodeTickFence(destination, endTimestamp, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, TickFenceCount, Entries, TickNumber, out bytesWritten, SourceLocationId);
    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

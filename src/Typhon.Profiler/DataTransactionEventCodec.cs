using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Data:Transaction:Init span. Payload: <c>tsn i64, uowId u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct DataTransactionInitData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public long Tsn { get; }
    public ushort UowId { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionInitData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, long tsn, ushort uowId, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; Tsn = tsn; UowId = uowId; SourceLocationId = srcLoc; }
}

/// <summary>Decoded Data:Transaction:Prepare span. Payload: <c>tsn i64</c> (8 B).</summary>
[PublicAPI]
public readonly struct DataTransactionPrepareData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public long Tsn { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionPrepareData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, long tsn, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; Tsn = tsn; SourceLocationId = srcLoc; }
}

/// <summary>Decoded Data:Transaction:Validate span. Payload: <c>tsn i64, entryCount i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct DataTransactionValidateData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public long Tsn { get; }
    public int EntryCount { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionValidateData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, long tsn, int entryCount, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; Tsn = tsn; EntryCount = entryCount; SourceLocationId = srcLoc; }
}

/// <summary>Decoded Data:Transaction:Conflict instant. Payload: <c>tsn i64, pk i64, componentTypeId i32, conflictType u8</c> (21 B).</summary>
[PublicAPI]
public readonly struct DataTransactionConflictData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tsn { get; }
    public long Pk { get; }
    public int ComponentTypeId { get; }
    public byte ConflictType { get; }
    public DataTransactionConflictData(byte threadSlot, long timestamp, long tsn, long pk, int componentTypeId, byte conflictType)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tsn = tsn; Pk = pk; ComponentTypeId = componentTypeId; ConflictType = conflictType; }
}

/// <summary>Decoded Data:Transaction:Cleanup span. Payload: <c>tsn i64, entityCount i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct DataTransactionCleanupData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public long Tsn { get; }
    public int EntityCount { get; }
    public ushort SourceLocationId { get; }
    public bool HasSourceLocation => SourceLocationId != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionCleanupData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, long tsn, int entityCount, ushort srcLoc = 0)
    {  ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; Tsn = tsn; EntityCount = entityCount; SourceLocationId = srcLoc; }
}

/// <summary>Wire codec for Data:Transaction events (kinds 173-177).</summary>
public static class DataTransactionEventCodec
{
    public const int ConflictSize = TraceRecordHeader.CommonHeaderSize + 8 + 8 + 4 + 1;  // 33

    private const int InitPayload     = 8 + 2;
    private const int PreparePayload  = 8;
    private const int ValidatePayload = 8 + 4;
    private const int CleanupPayload  = 8 + 4;

    public static int ComputeSizeInit(bool hasTC)     => TraceRecordHeader.SpanHeaderSize(hasTC) + InitPayload;
    public static int ComputeSizePrepare(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + PreparePayload;
    public static int ComputeSizeValidate(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + ValidatePayload;
    public static int ComputeSizeCleanup(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + CleanupPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTC,
        bool hasSourceLocation, ushort sourceLocationId)
    {
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

    public static void EncodeInit(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, long tsn, ushort uowId, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeInit(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.DataTransactionInit, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, hasSourceLocation, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], uowId);
        bytesWritten = size;
    }

    public static DataTransactionInitData DecodeInit(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        ushort sourceLocationId = 0;
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
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        return new DataTransactionInitData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            sourceLocationId);
    }

    public static void EncodePrepare(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, long tsn, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizePrepare(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.DataTransactionPrepare, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, hasSourceLocation, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        bytesWritten = size;
    }

    public static DataTransactionPrepareData DecodePrepare(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        ushort sourceLocationId = 0;
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
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        return new DataTransactionPrepareData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            sourceLocationId);
    }

    public static void EncodeValidate(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, long tsn, int entryCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeValidate(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.DataTransactionValidate, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, hasSourceLocation, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], entryCount);
        bytesWritten = size;
    }

    public static DataTransactionValidateData DecodeValidate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        ushort sourceLocationId = 0;
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
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        return new DataTransactionValidateData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
            sourceLocationId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteConflict(Span<byte> destination, byte threadSlot, long timestamp, long tsn, long pk, int componentTypeId, byte conflictType)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ConflictSize, TraceEventKind.DataTransactionConflict, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], pk);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], componentTypeId);
        p[20] = conflictType;
    }

    public static DataTransactionConflictData DecodeConflict(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DataTransactionConflictData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[16..]),
            p[20]);
    }

    public static void EncodeCleanup(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, long tsn, int entityCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeCleanup(hasTC);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;
        WriteSpanPreamble(destination, TraceEventKind.DataTransactionCleanup, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo, hasTC, hasSourceLocation, sourceLocationId);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], entityCount);
        bytesWritten = size;
    }

    public static DataTransactionCleanupData DecodeCleanup(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        ushort sourceLocationId = 0;
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
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];
        return new DataTransactionCleanupData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
            sourceLocationId);
    }
}

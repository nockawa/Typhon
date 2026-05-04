using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of an <see cref="TraceEventKind.EcsSpawn"/> event.</summary>
public readonly struct EcsSpawnEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required — archetype ID.</summary>
    public ushort ArchetypeId { get; }

    public byte OptionalFieldMask { get; }

    /// <summary>Optional — the entity ID assigned by <c>SpawnInternal</c>. Valid iff <see cref="HasEntityId"/>.</summary>
    public ulong EntityId { get; }

    /// <summary>Optional — enclosing transaction's TSN. Valid iff <see cref="HasTsn"/>.</summary>
    public long Tsn { get; }

    /// <summary>Compile-time site id (0 = absent / not attributed). See `claude/design/observability/09-profiler-source-attribution.md`.</summary>
    public ushort SourceLocationId { get; }

    public bool HasEntityId => (OptionalFieldMask & EcsSpawnEventCodec.OptEntityId) != 0;
    public bool HasTsn => (OptionalFieldMask & EcsSpawnEventCodec.OptTsn) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    /// <summary><c>true</c> when <see cref="SourceLocationId"/> is non-zero (the record was emitted via an intercepted call site).</summary>
    public bool HasSourceLocation => SourceLocationId != 0;

    public EcsSpawnEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort archetypeId, byte optionalFieldMask, ulong entityId, long tsn,
        ushort sourceLocationId = 0)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        ArchetypeId = archetypeId;
        OptionalFieldMask = optionalFieldMask;
        EntityId = entityId;
        Tsn = tsn;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.EcsSpawn"/>. Payload: <c>u16 archetypeId</c>, <c>u8 optMask</c>, <c>u64 entityId?</c>, <c>i64 tsn?</c>.</summary>
public static class EcsSpawnEventCodec
{
    public const byte OptEntityId = 0x01;
    public const byte OptTsn = 0x02;

    private const int ArchetypeIdSize = 2;
    private const int OptMaskSize = 1;
    private const int EntityIdSize = 8;
    private const int TsnSize = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext, byte optMask)
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext) + ArchetypeIdSize + OptMaskSize;
        if ((optMask & OptEntityId) != 0) size += EntityIdSize;
        if ((optMask & OptTsn) != 0) size += TsnSize;
        return size;
    }

    internal static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort archetypeId, byte optMask, ulong entityId, long tsn, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputeSize(hasTraceContext, optMask);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.EcsSpawn, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var payload = destination[headerSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, archetypeId);
        payload[ArchetypeIdSize] = optMask;
        var cursor = ArchetypeIdSize + OptMaskSize;

        if ((optMask & OptEntityId) != 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(payload[cursor..], entityId);
            cursor += EntityIdSize;
        }
        if ((optMask & OptTsn) != 0)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload[cursor..], tsn);
            cursor += TsnSize;
        }

        bytesWritten = size;
    }

    public static EcsSpawnEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var archetypeId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var optMask = payload[ArchetypeIdSize];
        var cursor = ArchetypeIdSize + OptMaskSize;

        ulong entityId = 0;
        long tsn = 0;
        if ((optMask & OptEntityId) != 0)
        {
            entityId = BinaryPrimitives.ReadUInt64LittleEndian(payload[cursor..]);
            cursor += EntityIdSize;
        }
        if ((optMask & OptTsn) != 0)
        {
            tsn = BinaryPrimitives.ReadInt64LittleEndian(payload[cursor..]);
            cursor += TsnSize;
        }

        return new EcsSpawnEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            archetypeId, optMask, entityId, tsn, sourceLocationId);
    }
}

/// <summary>Decoded form of an <see cref="TraceEventKind.EcsDestroy"/> event.</summary>
public readonly struct EcsDestroyEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required — entity ID being destroyed.</summary>
    public ulong EntityId { get; }

    public byte OptionalFieldMask { get; }

    /// <summary>Optional — total entities destroyed in the cascade (set only when &gt; 1). Valid iff <see cref="HasCascadeCount"/>.</summary>
    public int CascadeCount { get; }

    /// <summary>Optional — enclosing transaction's TSN. Valid iff <see cref="HasTsn"/>.</summary>
    public long Tsn { get; }

    /// <summary>Compile-time site id (0 = absent / not attributed). See `claude/design/observability/09-profiler-source-attribution.md`.</summary>
    public ushort SourceLocationId { get; }

    public bool HasCascadeCount => (OptionalFieldMask & EcsDestroyEventCodec.OptCascadeCount) != 0;
    public bool HasTsn => (OptionalFieldMask & EcsDestroyEventCodec.OptTsn) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    /// <summary><c>true</c> when <see cref="SourceLocationId"/> is non-zero (the record was emitted via an intercepted call site).</summary>
    public bool HasSourceLocation => SourceLocationId != 0;

    public EcsDestroyEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ulong entityId, byte optionalFieldMask, int cascadeCount, long tsn,
        ushort sourceLocationId = 0)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        EntityId = entityId;
        OptionalFieldMask = optionalFieldMask;
        CascadeCount = cascadeCount;
        Tsn = tsn;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.EcsDestroy"/>. Payload: <c>u64 entityId</c>, <c>u8 optMask</c>, <c>i32 cascadeCount?</c>, <c>i64 tsn?</c>.</summary>
public static class EcsDestroyEventCodec
{
    public const byte OptCascadeCount = 0x01;
    public const byte OptTsn = 0x02;

    private const int EntityIdSize = 8;
    private const int OptMaskSize = 1;
    private const int CascadeCountSize = 4;
    private const int TsnSize = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext, byte optMask)
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext) + EntityIdSize + OptMaskSize;
        if ((optMask & OptCascadeCount) != 0) size += CascadeCountSize;
        if ((optMask & OptTsn) != 0) size += TsnSize;
        return size;
    }

    internal static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ulong entityId, byte optMask, int cascadeCount, long tsn, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputeSize(hasTraceContext, optMask);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.EcsDestroy, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var payload = destination[headerSize..];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, entityId);
        payload[EntityIdSize] = optMask;
        var cursor = EntityIdSize + OptMaskSize;

        if ((optMask & OptCascadeCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], cascadeCount);
            cursor += CascadeCountSize;
        }
        if ((optMask & OptTsn) != 0)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload[cursor..], tsn);
            cursor += TsnSize;
        }

        bytesWritten = size;
    }

    public static EcsDestroyEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var entityId = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var optMask = payload[EntityIdSize];
        var cursor = EntityIdSize + OptMaskSize;

        int cascadeCount = 0;
        long tsn = 0;
        if ((optMask & OptCascadeCount) != 0)
        {
            cascadeCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += CascadeCountSize;
        }
        if ((optMask & OptTsn) != 0)
        {
            tsn = BinaryPrimitives.ReadInt64LittleEndian(payload[cursor..]);
            cursor += TsnSize;
        }

        return new EcsDestroyEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            entityId, optMask, cascadeCount, tsn, sourceLocationId);
    }
}

/// <summary>Decoded form of an <see cref="TraceEventKind.EcsViewRefresh"/> event.</summary>
public readonly struct EcsViewRefreshEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required — archetype type ID.</summary>
    public ushort ArchetypeTypeId { get; }

    public byte OptionalFieldMask { get; }

    /// <summary>Optional — which refresh path was taken. Valid iff <see cref="HasMode"/>.</summary>
    public EcsViewRefreshMode Mode { get; }

    /// <summary>Optional — post-refresh view entity count. Valid iff <see cref="HasResultCount"/>.</summary>
    public int ResultCount { get; }

    /// <summary>Optional — delta entries processed (incremental mode only). Valid iff <see cref="HasDeltaCount"/>.</summary>
    public int DeltaCount { get; }

    /// <summary>Compile-time site id (0 = absent / not attributed). See `claude/design/observability/09-profiler-source-attribution.md`.</summary>
    public ushort SourceLocationId { get; }

    public bool HasMode => (OptionalFieldMask & EcsViewRefreshEventCodec.OptMode) != 0;
    public bool HasResultCount => (OptionalFieldMask & EcsViewRefreshEventCodec.OptResultCount) != 0;
    public bool HasDeltaCount => (OptionalFieldMask & EcsViewRefreshEventCodec.OptDeltaCount) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    /// <summary><c>true</c> when <see cref="SourceLocationId"/> is non-zero (the record was emitted via an intercepted call site).</summary>
    public bool HasSourceLocation => SourceLocationId != 0;

    public EcsViewRefreshEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort archetypeTypeId, byte optionalFieldMask,
        EcsViewRefreshMode mode, int resultCount, int deltaCount,
        ushort sourceLocationId = 0)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        ArchetypeTypeId = archetypeTypeId;
        OptionalFieldMask = optionalFieldMask;
        Mode = mode;
        ResultCount = resultCount;
        DeltaCount = deltaCount;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.EcsViewRefresh"/>. Payload: <c>u16 archetypeTypeId</c>, <c>u8 optMask</c>, <c>u8 mode?</c>, <c>i32 resultCount?</c>, <c>i32 deltaCount?</c>.</summary>
public static class EcsViewRefreshEventCodec
{
    public const byte OptMode = 0x01;
    public const byte OptResultCount = 0x02;
    public const byte OptDeltaCount = 0x04;

    private const int ArchetypeTypeIdSize = 2;
    private const int OptMaskSize = 1;
    private const int ModeSize = 1;
    private const int ResultCountSize = 4;
    private const int DeltaCountSize = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext, byte optMask)
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext) + ArchetypeTypeIdSize + OptMaskSize;
        if ((optMask & OptMode) != 0) size += ModeSize;
        if ((optMask & OptResultCount) != 0) size += ResultCountSize;
        if ((optMask & OptDeltaCount) != 0) size += DeltaCountSize;
        return size;
    }

    internal static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort archetypeTypeId, byte optMask, EcsViewRefreshMode mode, int resultCount, int deltaCount, out int bytesWritten,
        ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputeSize(hasTraceContext, optMask);
        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.EcsViewRefresh, threadSlot, startTimestamp);
        var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)
                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var payload = destination[headerSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, archetypeTypeId);
        payload[ArchetypeTypeIdSize] = optMask;
        var cursor = ArchetypeTypeIdSize + OptMaskSize;

        if ((optMask & OptMode) != 0)
        {
            payload[cursor] = (byte)mode;
            cursor += ModeSize;
        }
        if ((optMask & OptResultCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], resultCount);
            cursor += ResultCountSize;
        }
        if ((optMask & OptDeltaCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], deltaCount);
            cursor += DeltaCountSize;
        }

        bytesWritten = size;
    }

    public static EcsViewRefreshEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var archetypeTypeId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var optMask = payload[ArchetypeTypeIdSize];
        var cursor = ArchetypeTypeIdSize + OptMaskSize;

        EcsViewRefreshMode mode = EcsViewRefreshMode.Pull;
        int resultCount = 0;
        int deltaCount = 0;

        if ((optMask & OptMode) != 0)
        {
            mode = (EcsViewRefreshMode)payload[cursor];
            cursor += ModeSize;
        }
        if ((optMask & OptResultCount) != 0)
        {
            resultCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += ResultCountSize;
        }
        if ((optMask & OptDeltaCount) != 0)
        {
            deltaCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += DeltaCountSize;
        }

        return new EcsViewRefreshEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            archetypeTypeId, optMask, mode, resultCount, deltaCount, sourceLocationId);
    }
}


using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS Spawn
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsSpawn"/>. Required at begin: archetype ID. Optional: entity ID (set after
/// <c>SpawnInternal</c> returns), TSN (set once the transaction is known).
/// </summary>
public ref struct EcsSpawnEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsSpawn;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeId;

    private ulong _entityId;
    private long _tsn;
    private byte _optMask;

    public ulong EntityId
    {
        readonly get => _entityId;
        set { _entityId = value; _optMask |= EcsSpawnEventCodec.OptEntityId; }
    }

    public long Tsn
    {
        readonly get => _tsn;
        set { _tsn = value; _optMask |= EcsSpawnEventCodec.OptTsn; }
    }

    public readonly int ComputeSize()
        => EcsSpawnEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsSpawnEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeId, _optMask, _entityId, _tsn, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS Destroy
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsDestroy"/>. Required: entity ID. Optional: cascade count, TSN.
/// </summary>
public ref struct EcsDestroyEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsDestroy;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ulong EntityId;

    private int _cascadeCount;
    private long _tsn;
    private byte _optMask;

    public int CascadeCount
    {
        readonly get => _cascadeCount;
        set { _cascadeCount = value; _optMask |= EcsDestroyEventCodec.OptCascadeCount; }
    }

    public long Tsn
    {
        readonly get => _tsn;
        set { _tsn = value; _optMask |= EcsDestroyEventCodec.OptTsn; }
    }

    public readonly int ComputeSize()
        => EcsDestroyEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsDestroyEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, EntityId, _optMask, _cascadeCount, _tsn, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS View Refresh
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsViewRefresh"/>. Required: archetype type ID. Optional: mode enum, result count,
/// delta count.
/// </summary>
public ref struct EcsViewRefreshEvent : ITraceEventEncoder
{
    public static byte Kind => (byte)TraceEventKind.EcsViewRefresh;

    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeTypeId;

    private EcsViewRefreshMode _mode;
    private int _resultCount;
    private int _deltaCount;
    private byte _optMask;

    public EcsViewRefreshMode Mode
    {
        readonly get => _mode;
        set { _mode = value; _optMask |= EcsViewRefreshEventCodec.OptMode; }
    }

    public int ResultCount
    {
        readonly get => _resultCount;
        set { _resultCount = value; _optMask |= EcsViewRefreshEventCodec.OptResultCount; }
    }

    public int DeltaCount
    {
        readonly get => _deltaCount;
        set { _deltaCount = value; _optMask |= EcsViewRefreshEventCodec.OptDeltaCount; }
    }

    public readonly int ComputeSize()
        => EcsViewRefreshEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsViewRefreshEventCodec.Encode(destination, endTimestamp, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeTypeId, _optMask, _mode, _resultCount, _deltaCount, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


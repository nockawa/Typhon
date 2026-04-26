using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsQueryExecute"/>. Lifecycle: caller constructs with required fields, assigns optional
/// fields via setters (each setter sets its bit in <c>_optMask</c>), caller invokes <see cref="EncodeTo"/> to serialize. Zero allocation — all state
/// lives in the stack frame of the using scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage pattern</b> (what the engine call sites will look like in Phase 4):
/// <code>
/// using var e = TyphonEvent.BeginEcsQueryExecute(archetypeTypeId);
/// // ... compute ...
/// e.ResultCount = results.Count;
/// e.ScanMode = EcsQueryScanMode.Targeted;
/// </code>
/// </para>
/// <para>
/// <b>Size:</b> minimum 40 B (37 B span header + 2 B archetype + 1 B mask) without trace context or optional fields. Maximum 62 B (53 B with trace
/// context + 2 B archetype + 1 B mask + 4 B result count + 1 B scan mode). Down from the old fixed 64 B struct's wasted space.
/// </para>
/// </remarks>
public ref struct EcsQueryExecuteEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    /// <summary>Required — archetype type ID.</summary>
    public ushort ArchetypeTypeId;

    private int _resultCount;
    private EcsQueryScanMode _scanMode;
    private byte _optMask;

    public int ResultCount
    {
        readonly get => _resultCount;
        set { _resultCount = value; _optMask |= EcsQueryEventCodec.OptResultCount; }
    }

    public EcsQueryScanMode ScanMode
    {
        readonly get => _scanMode;
        set { _scanMode = value; _optMask |= EcsQueryEventCodec.OptScanMode; }
    }

    public readonly int ComputeSize()
        => EcsQueryEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryEventCodec.Encode(destination, endTimestamp, TraceEventKind.EcsQueryExecute, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeTypeId, _optMask, _resultCount, _scanMode, false, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsQueryCountEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeTypeId;

    private int _resultCount;
    private EcsQueryScanMode _scanMode;
    private byte _optMask;

    public int ResultCount
    {
        readonly get => _resultCount;
        set { _resultCount = value; _optMask |= EcsQueryEventCodec.OptResultCount; }
    }

    public EcsQueryScanMode ScanMode
    {
        readonly get => _scanMode;
        set { _scanMode = value; _optMask |= EcsQueryEventCodec.OptScanMode; }
    }

    public readonly int ComputeSize()
        => EcsQueryEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryEventCodec.Encode(destination, endTimestamp, TraceEventKind.EcsQueryCount, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeTypeId, _optMask, _resultCount, _scanMode, false, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}

public ref struct EcsQueryAnyEvent : ITraceEventEncoder
{
    public byte ThreadSlot;
    public long StartTimestamp;
    public ulong SpanId;
    public ulong ParentSpanId;
    public ulong PreviousSpanId;
    public ulong TraceIdHi;
    public ulong TraceIdLo;

    public ushort ArchetypeTypeId;

    private bool _found;
    private EcsQueryScanMode _scanMode;
    private byte _optMask;

    public bool Found
    {
        readonly get => _found;
        set { _found = value; _optMask |= EcsQueryEventCodec.OptFound; }
    }

    public EcsQueryScanMode ScanMode
    {
        readonly get => _scanMode;
        set { _scanMode = value; _optMask |= EcsQueryEventCodec.OptScanMode; }
    }

    public readonly int ComputeSize()
        => EcsQueryEventCodec.ComputeSize(TraceIdHi != 0 || TraceIdLo != 0, _optMask);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryEventCodec.Encode(destination, endTimestamp, TraceEventKind.EcsQueryAny, ThreadSlot, StartTimestamp,
            SpanId, ParentSpanId, TraceIdHi, TraceIdLo, ArchetypeTypeId, _optMask, 0, _scanMode, _found, out bytesWritten);

    public void Dispose() => TyphonEvent.PublishEvent(ref this, ThreadSlot, PreviousSpanId, SpanId);
}


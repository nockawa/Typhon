namespace Typhon.Profiler.Server;

/// <summary>
/// JSON-serializable trace record DTO for the live SSE stream and the file-based <c>/api/trace/events</c> endpoint. Flat shape with nullable
/// kind-specific fields — the server emits only the fields relevant for a given <see cref="Kind"/>, and <c>DefaultIgnoreCondition.WhenWritingNull</c>
/// elides the rest from the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always present:</b> <see cref="Kind"/>, <see cref="ThreadSlot"/>, <see cref="TickNumber"/>, <see cref="TimestampUs"/>.
/// </para>
/// <para>
/// <b>Span records (Kind ≥ 10):</b> also carry <see cref="DurationUs"/>, <see cref="SpanId"/>, <see cref="ParentSpanId"/>, and — when the record
/// has a distributed-trace context — <see cref="TraceIdHi"/> + <see cref="TraceIdLo"/>.
/// </para>
/// <para>
/// <b>Kind-specific fields</b> (populated only for the relevant kind):
/// <list type="bullet">
///   <item>Instant — <see cref="TraceEventKind.TickEnd"/>: <see cref="OverloadLevel"/>, <see cref="TickMultiplier"/>.</item>
///   <item>Instant — PhaseStart/End: <see cref="Phase"/>.</item>
///   <item>Instant — SystemReady/Skipped: <see cref="SystemIndex"/>, <see cref="SkipReason"/>.</item>
///   <item>Span — <see cref="TraceEventKind.SchedulerChunk"/>: <see cref="SystemIndex"/>, <see cref="ChunkIndex"/>, <see cref="TotalChunks"/>, <see cref="EntitiesProcessed"/>.</item>
///   <item>Span — Transaction.*: <see cref="Tsn"/>, <see cref="ComponentTypeId"/>, <see cref="ComponentCount"/>, <see cref="ConflictDetected"/>.</item>
///   <item>Span — ECS.Spawn: <see cref="ArchetypeId"/>, <see cref="EntityId"/>, <see cref="Tsn"/>.</item>
///   <item>Span — ECS.Destroy: <see cref="EntityId"/>, <see cref="CascadeCount"/>, <see cref="Tsn"/>.</item>
///   <item>Span — ECS.Query.*: <see cref="ArchetypeId"/>, <see cref="ResultCount"/>, <see cref="ScanMode"/>, <see cref="Found"/>.</item>
///   <item>Span — ECS.View.Refresh: <see cref="ArchetypeId"/>, <see cref="Mode"/>, <see cref="ResultCount"/>, <see cref="DeltaCount"/>.</item>
///   <item>Span — PageCache.*: <see cref="FilePageIndex"/>, <see cref="PageCount"/>.</item>
///   <item>Span — Cluster.Migration: <see cref="ArchetypeId"/>, <see cref="MigrationCount"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>64-bit IDs as hex strings:</b> <see cref="SpanId"/>, <see cref="ParentSpanId"/>, <see cref="TraceIdHi"/>, <see cref="TraceIdLo"/>,
/// <see cref="EntityId"/>, and <see cref="Tsn"/> are serialized as lowercase hex because JavaScript's <c>Number</c> can't represent the full
/// <c>ulong</c>/<c>long</c> range. The browser viewer treats them as opaque strings — formatting only, no arithmetic.
/// </para>
/// </remarks>
public sealed class LiveTraceEvent
{
    public int Kind { get; init; }
    public byte ThreadSlot { get; init; }
    public int TickNumber { get; init; }
    public double TimestampUs { get; init; }

    public double? DurationUs { get; init; }
    public string SpanId { get; init; }
    public string ParentSpanId { get; init; }
    public string TraceIdHi { get; init; }
    public string TraceIdLo { get; init; }

    // Instant-event fields
    public int? Phase { get; init; }
    public int? SystemIndex { get; init; }
    public int? SkipReason { get; init; }
    public int? OverloadLevel { get; init; }
    public int? TickMultiplier { get; init; }

    // Scheduler chunk
    public int? ChunkIndex { get; init; }
    public int? TotalChunks { get; init; }
    public int? EntitiesProcessed { get; init; }

    // Transaction
    public string Tsn { get; init; }                // i64 as hex
    public int? ComponentTypeId { get; init; }
    public int? ComponentCount { get; init; }
    public bool? ConflictDetected { get; init; }

    // ECS
    public int? ArchetypeId { get; init; }
    public string EntityId { get; init; }            // u64 as hex
    public int? CascadeCount { get; init; }
    public int? ResultCount { get; init; }
    public int? ScanMode { get; init; }              // EcsQueryScanMode
    public bool? Found { get; init; }
    public int? Mode { get; init; }                  // EcsViewRefreshMode
    public int? DeltaCount { get; init; }

    // Page cache
    public int? FilePageIndex { get; init; }
    public int? PageCount { get; init; }

    // Cluster migration
    public int? MigrationCount { get; init; }

    // Transaction persist
    public string WalLsn { get; init; }

    // Page cache backpressure
    public int? RetryCount { get; init; }
    public int? DirtyCount { get; init; }
    public int? EpochCount { get; init; }

    // WAL
    public int? BatchByteCount { get; init; }
    public int? FrameCount { get; init; }
    public string HighLsn { get; init; }
    public int? NewSegmentIndex { get; init; }
    public string TargetLsn { get; init; }

    // Checkpoint
    public int? DirtyPageCount { get; init; }
    public int? Reason { get; init; }
    public int? WrittenCount { get; init; }
    public int? TransitionedCount { get; init; }
    public int? RecycledCount { get; init; }

    // Statistics
    public int? EntityCount { get; init; }
    public int? MutationCount { get; init; }
    public int? SamplingInterval { get; init; }

    // Memory allocation (kind 9 — instant)
    public int? Direction { get; init; }       // 0 = alloc, 1 = free (MemoryAllocDirection)
    public int? SourceTag { get; init; }        // u16 interned tag (MemoryAllocSource)
    public double? SizeBytes { get; init; }     // bytes for this alloc/free
    public double? TotalAfterBytes { get; init; }  // running total after this op

    // Per-tick gauge snapshot (kind 76 — instant-ish, tick-boundaried). Keys are GaugeId as u16 → double values. Fields absent for non-snapshot records.
    public uint? Flags { get; init; }
    public System.Collections.Generic.Dictionary<int, double> Gauges { get; init; }

    // GC events (kinds 7, 8 — instant). Triangle markers on the GC gauge track, colored by generation.
    public int? Generation { get; init; }
    public int? GcReason { get; init; }
    public int? GcType { get; init; }
    public uint? GcCount { get; init; }
    public double? GcPauseDurationUs { get; init; }
    public double? GcPromotedBytes { get; init; }

    // Thread info (kind 77 — instant). Emitted once when a thread claims its slot; the viewer aggregates these into a slot→name map for lane labels.
    public int? ManagedThreadId { get; init; }
    public string ThreadName { get; init; }
}

namespace Typhon.Profiler;

/// <summary>
/// JSON-serializable trace record DTO for the live SSE stream and the file-based chunk endpoint. Flat shape with nullable
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
/// <b>64-bit IDs as decimal strings:</b> <see cref="SpanId"/>, <see cref="ParentSpanId"/>, <see cref="TraceIdHi"/>, <see cref="TraceIdLo"/>,
/// <see cref="EntityId"/>, and <see cref="Tsn"/> are serialized as decimal strings because JavaScript's <c>Number</c> can't represent the full
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
    public string Tsn { get; init; }
    public int? ComponentTypeId { get; init; }
    /// <summary>
    /// Component-instance count. For Transaction events: number of components committed / rolled back. For
    /// <see cref="TraceEventKind.ClusterMigration"/>: total component slots moved across the batch
    /// (entities × per-entity slot count). Disambiguated by <see cref="Kind"/>.
    /// </summary>
    public int? ComponentCount { get; init; }
    public bool? ConflictDetected { get; init; }

    // ECS
    public int? ArchetypeId { get; init; }
    public string EntityId { get; init; }
    public int? CascadeCount { get; init; }
    public int? ResultCount { get; init; }
    public int? ScanMode { get; init; }
    public bool? Found { get; init; }
    public int? Mode { get; init; }
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

    // Memory allocation
    public int? Direction { get; init; }
    public int? SourceTag { get; init; }
    public double? SizeBytes { get; init; }
    public double? TotalAfterBytes { get; init; }

    // Per-tick gauge snapshot
    public uint? Flags { get; init; }
    public System.Collections.Generic.Dictionary<int, double> Gauges { get; init; }

    // GC events
    public int? Generation { get; init; }
    public int? GcReason { get; init; }
    public int? GcType { get; init; }
    public uint? GcCount { get; init; }
    public double? GcPauseDurationUs { get; init; }
    public double? GcPromotedBytes { get; init; }

    // Thread info
    public int? ManagedThreadId { get; init; }
    public string ThreadName { get; init; }
    /// <summary>Producer-thread category — drives the viewer's filter tree's Main / Workers / Other split.</summary>
    public ThreadKind? ThreadKind { get; init; }
}

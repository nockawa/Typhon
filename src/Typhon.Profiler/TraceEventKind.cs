namespace Typhon.Profiler;

/// <summary>
/// Discriminant for a trace record. Every record starts with a 12-byte common header whose third byte carries this value; the kind determines how
/// the bytes after the header are interpreted (span header or minimal instant header, then per-kind typed payload).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire stability:</b> numeric values are part of the <c>.typhon-trace</c> file format. Never renumber or reuse an ID — append new entries only.
/// Gaps in numbering are intentional so related categories can grow contiguously without renumbering existing kinds.
/// </para>
/// <para>
/// <b>Span vs instant:</b> kinds &lt; 10 are <i>instant</i> events — they carry only the 12-byte common header plus optional minimal payload, no
/// span extension (no duration, no spanId, no parent linkage). Kinds ≥ 10 are <i>span</i> events — they include the 25-byte span header extension
/// plus an optional 16-byte trace context and per-kind typed payload. Use <see cref="TraceEventKindExtensions.IsSpan"/> for the range check.
/// </para>
/// </remarks>
public enum TraceEventKind : byte
{
    // ── Instant events (no span header — minimal 12-byte common header + optional tiny payload) ──

    /// <summary>Scheduler tick started (empty payload). Emitted at the top of each <c>DagScheduler.RunTick</c>.</summary>
    TickStart = 0,

    /// <summary>Scheduler tick ended. Payload: <c>overloadLevel: u8</c>, <c>tickMultiplier: u8</c>.</summary>
    TickEnd = 1,

    /// <summary>Tick phase started. Payload: <c>phase: u8</c> (TickPhase enum).</summary>
    PhaseStart = 2,

    /// <summary>Tick phase ended. Payload: <c>phase: u8</c>.</summary>
    PhaseEnd = 3,

    /// <summary>A system became ready (all predecessors completed). Payload: <c>systemIdx: u16</c>, <c>predecessorCount: u16</c>.</summary>
    SystemReady = 4,

    /// <summary>A system was skipped. Payload: <c>systemIdx: u16</c>, <c>skipReason: u8</c>.</summary>
    SystemSkipped = 5,

    /// <summary>Generic instant marker. Payload: <c>nameId: i32</c> (interned), <c>payload: i32</c>.</summary>
    Instant = 6,

    /// <summary>
    /// .NET runtime GC boundary — a garbage collection has started. Payload: <c>u8 generation</c>, <c>u8 reason</c> (<see cref="GcReason"/>),
    /// <c>u8 type</c> (<see cref="GcType"/>), <c>u32 count</c>. Emitted by the profiler's GC ingestion thread on observing <c>GCStart_V2</c>.
    /// Only produced when <c>TelemetryConfig.ProfilerGcTracingActive</c> is set at class load.
    /// </summary>
    GcStart = 7,

    /// <summary>
    /// .NET runtime GC boundary — a garbage collection has ended. Payload: <c>u8 generation</c>, <c>u32 count</c>, <c>i64 pauseDurationTicks</c>,
    /// <c>u64 promotedBytes</c>, five <c>u64</c> per-generation size-after values (Gen0/Gen1/Gen2/LOH/POH), <c>u64 totalCommittedBytes</c>.
    /// Sizes are snapshotted by the ingestion thread via <see cref="System.GC.GetGCMemoryInfo()"/> on the <c>GCEnd_V1</c> event.
    /// </summary>
    GcEnd = 8,

    /// <summary>
    /// Discrete unmanaged-memory allocation or free event. Every <c>PinnedMemoryBlock</c> construct/dispose emits one of these when
    /// <c>TelemetryConfig.ProfilerMemoryAllocationsActive</c> is set. Payload: <c>u8 direction</c> (<see cref="MemoryAllocDirection"/>),
    /// <c>u16 sourceTag</c> (<see cref="MemoryAllocSource"/>), <c>u64 sizeBytes</c>, <c>u64 totalAfterBytes</c>. Wire size: 31 B.
    /// </summary>
    MemoryAllocEvent = 9,

    // ── Span events (span header extension: 25B + optional 16B trace context, then typed payload) ──

    /// <summary>Scheduler chunk executed on a worker. Payload: <c>systemIdx: u16</c>, <c>chunkIdx: u16</c>, <c>totalChunks: u16</c>, <c>entitiesProcessed: i32</c>.</summary>
    SchedulerChunk = 10,

    // ── Transaction (span) ──

    /// <summary>Transaction commit. Required: <c>tsn: i64</c>. Optional: <c>componentCount: i32</c>, <c>conflictDetected: bool</c>.</summary>
    TransactionCommit = 20,

    /// <summary>Transaction rollback. Required: <c>tsn: i64</c>. Optional: <c>componentCount: i32</c>.</summary>
    TransactionRollback = 21,

    /// <summary>Per-component commit sub-span. Required: <c>tsn: i64</c>, <c>componentTypeId: i32</c>.</summary>
    TransactionCommitComponent = 22,

    /// <summary>WAL serialization inside Transaction.Commit. Required: <c>tsn: i64</c>. Optional: <c>walLsn: i64</c>.</summary>
    TransactionPersist = 23,

    // ── ECS (span) ──

    /// <summary>Entity spawn. Required: <c>archetypeId: u16</c>. Optional: <c>entityId: u64</c>, <c>tsn: i64</c>.</summary>
    EcsSpawn = 30,

    /// <summary>Entity destroy. Required: <c>entityId: u64</c>. Optional: <c>cascadeCount: i32</c>, <c>tsn: i64</c>.</summary>
    EcsDestroy = 31,

    /// <summary>Query execute. Required: <c>archetypeTypeId: u16</c>. Optional: <c>resultCount: i32</c>, <c>scanMode: u8</c>.</summary>
    EcsQueryExecute = 32,

    /// <summary>Query count. Required: <c>archetypeTypeId: u16</c>. Optional: <c>resultCount: i32</c>, <c>scanMode: u8</c>.</summary>
    EcsQueryCount = 33,

    /// <summary>Query any. Required: <c>archetypeTypeId: u16</c>. Optional: <c>found: bool</c>, <c>scanMode: u8</c>.</summary>
    EcsQueryAny = 34,

    /// <summary>View refresh. Required: <c>archetypeTypeId: u16</c>. Optional: <c>mode: u8</c>, <c>resultCount: i32</c>, <c>deltaCount: i32</c>.</summary>
    EcsViewRefresh = 35,

    // ── B+Tree (span) ──

    /// <summary>B+Tree insert. No payload — kind is the only data.</summary>
    BTreeInsert = 40,

    /// <summary>B+Tree delete. No payload.</summary>
    BTreeDelete = 41,

    /// <summary>B+Tree node split. No payload.</summary>
    BTreeNodeSplit = 42,

    /// <summary>B+Tree node merge. No payload.</summary>
    BTreeNodeMerge = 43,

    // ── Page cache (span) ──

    /// <summary>Page cache fetch. Required: <c>filePageIndex: i32</c>.</summary>
    PageCacheFetch = 50,

    /// <summary>Page cache disk read. Required: <c>filePageIndex: i32</c>.</summary>
    PageCacheDiskRead = 51,

    /// <summary>Page cache disk write. Required: <c>filePageIndex: i32</c>. Optional: <c>pageCount: i32</c>.</summary>
    PageCacheDiskWrite = 52,

    /// <summary>Page cache allocate page. Required: <c>filePageIndex: i32</c>.</summary>
    PageCacheAllocatePage = 53,

    /// <summary>Page cache flush. Required: <c>pageCount: i32</c>.</summary>
    PageCacheFlush = 54,

    /// <summary>
    /// A cached page was displaced by <see cref="PageCacheAllocatePage"/> to make room for a new page being fetched. Required:
    /// <c>filePageIndex: i32</c> (the displaced page's file index, NOT the incoming page).
    /// </summary>
    /// <remarks>
    /// Emitted as a zero-duration span (start == end). Parents under the enclosing <see cref="PageCacheAllocatePage"/> span via TLS, so the
    /// viewer renders eviction events as instant markers nested inside the AllocatePage bar — "allocation ran for 12 µs and kicked out page N."
    /// Reuses the <c>PageCacheEventCodec</c> wire shape (common header + span extension + 4 B filePageIndex + 1 B optMask), so no new codec
    /// is needed. Suppressed by default alongside the other PageCache.* kinds.
    /// </remarks>
    PageEvicted = 55,

    /// <summary>
    /// Completion marker for an async <see cref="PageCacheDiskRead"/>. Required: <c>filePageIndex: i32</c>. The record carries the original
    /// DiskRead span's <c>SpanId</c> as its own <c>SpanId</c> (same value — the viewer uses this to correlate kickoff → completion) and a
    /// <c>StartTimestamp</c> equal to the original's <c>StartTimestamp</c>, so the record's <c>durationTicks</c> field is the full async tail:
    /// <c>completionTimestamp - beginTimestamp</c>. Emitted from the thread-pool worker that completes the <c>RandomAccess.ReadAsync</c>.
    /// </summary>
    /// <remarks>
    /// Suppressed by default alongside the other PageCache.* kinds. Opt in with <c>TyphonEvent.UnsuppressKind(PageCacheDiskReadCompleted)</c>
    /// (must ALSO have <see cref="PageCacheDiskRead"/> unsuppressed, otherwise there's no kickoff span to correlate with and the completion
    /// is skipped at the call site). Reuses <see cref="PageCacheEventCodec"/> wire shape verbatim — decoder needs only a match-arm entry.
    /// </remarks>
    PageCacheDiskReadCompleted = 56,

    /// <summary>
    /// Completion marker for an async <see cref="PageCacheDiskWrite"/>. Required: <c>filePageIndex: i32</c>. Same correlation pattern as
    /// <see cref="PageCacheDiskReadCompleted"/> — record's <c>SpanId</c> matches the original DiskWrite span, <c>durationTicks</c> is the
    /// full async tail including the OS write completion.
    /// </summary>
    PageCacheDiskWriteCompleted = 57,

    /// <summary>
    /// Completion marker for an async <see cref="PageCacheFlush"/>. Required: <c>pageCount: i32</c> (stored in the primary <c>filePageIndex</c>
    /// slot per Flush convention). Record's <c>durationTicks</c> covers the full flush tail: <c>max(WriteAsync completions)</c> + <c>fsync</c>.
    /// The delta between this record's duration and the max of the enclosed <see cref="PageCacheDiskWriteCompleted"/> events is pure fsync cost.
    /// </summary>
    PageCacheFlushCompleted = 58,

    /// <summary>Page cache backpressure wait — clock-sweep retry loop couldn't find a free page. Required: <c>retryCount: i32</c>,
    /// <c>dirtyCount: i32</c>, <c>epochCount: i32</c>. Suppressed by default alongside other PageCache.* kinds.</summary>
    PageCacheBackpressure = 59,

    // ── Cluster migration (span) ──

    /// <summary>Cluster migration between spatial cells. Required: <c>archetypeId: u16</c>, <c>migrationCount: i32</c>.</summary>
    ClusterMigration = 60,

    // ── .NET runtime GC suspension (span) ──

    /// <summary>
    /// .NET runtime Execution-Engine suspension window. Opened on <c>GCSuspendEEBegin_V1</c> (ETW id 9), closed on <c>GCRestartEEEnd_V1</c> (ETW id 3).
    /// Payload: <c>u8 reason</c> (<see cref="GcSuspendReason"/>), <c>u8 optMask</c> (reserved).
    /// <c>ParentSpanId</c> is always 0 (process-level, not caller-attributed). No <see cref="System.Diagnostics.Activity"/> capture.
    /// </summary>
    GcSuspension = 75,

    /// <summary>
    /// Per-tick gauge snapshot — packed bundle of (gaugeId, value) pairs emitted once per tick by the scheduler thread at end-of-tick.
    /// Instant-style record: no span header extension, no duration semantics. Common header + fixed prefix
    /// (<c>u32 tickNumber</c>, <c>u16 fieldCount</c>, <c>u32 flags</c>) then repeated
    /// <c>{u16 gaugeId; u8 valueKind; [4 or 8 B] value}</c> entries. Gated on <c>TelemetryConfig.ProfilerGaugesActive</c>.
    /// See <see cref="GaugeId"/> for the wire-stable gauge ID registry.
    /// </summary>
    /// <remarks>
    /// Although this kind's numeric value is ≥ 10, it is <b>not</b> a span record. <see cref="TraceEventKindExtensions.IsSpan"/> explicitly
    /// excludes it so the consumer never tries to read the 25-byte span header extension after the common header.
    /// </remarks>
    PerTickSnapshot = 76,

    /// <summary>
    /// Per-slot thread identity — emitted once when a producer thread claims its slot. Carries the managed thread ID and a UTF-8 thread name
    /// so the viewer can label lanes with something meaningful ("DagScheduler", "TyphonProfilerConsumer", pool worker name, ...) instead of
    /// just the numeric slot index. Wire layout after the 12-byte common header: <c>i32 managedThreadId</c>, <c>u16 nameByteCount</c>,
    /// <c>byte[nameByteCount] nameUtf8</c>. Instant-style (no span-header extension); see <see cref="TraceEventKindExtensions.IsSpan"/>.
    /// </summary>
    ThreadInfo = 77,

    // ── WAL (span) ──

    /// <summary>WAL writer drain-write-signal cycle. Required: <c>batchByteCount: i32</c>, <c>frameCount: i32</c>, <c>highLsn: i64</c>.</summary>
    WalFlush = 80,

    /// <summary>WAL segment rotation. Required: <c>newSegmentIndex: i32</c>.</summary>
    WalSegmentRotate = 81,

    /// <summary>Thread blocked waiting for WAL durability. Required: <c>targetLsn: i64</c>. Emitted on the calling thread, not the WAL writer.</summary>
    WalWait = 82,

    // ── Checkpoint (span) ──

    /// <summary>Full checkpoint cycle. Required: <c>targetLsn: i64</c>, <c>reason: u8</c> (<see cref="CheckpointReason"/>). Optional: <c>dirtyPageCount: i32</c>.</summary>
    CheckpointCycle = 83,

    /// <summary>Checkpoint phase: collect dirty page indices.</summary>
    CheckpointCollect = 84,

    /// <summary>Checkpoint phase: write dirty pages to data file. Optional: <c>writtenCount: i32</c>.</summary>
    CheckpointWrite = 85,

    /// <summary>Checkpoint phase: fsync data file.</summary>
    CheckpointFsync = 86,

    /// <summary>Checkpoint phase: transition UoW entries from WalDurable to Committed. Optional: <c>transitionedCount: i32</c>.</summary>
    CheckpointTransition = 87,

    /// <summary>Checkpoint phase: recycle WAL segments below checkpoint LSN. Optional: <c>recycledCount: i32</c>.</summary>
    CheckpointRecycle = 88,

    // ── Statistics (span) ──

    /// <summary>Statistics rebuild for a ComponentTable. Required: <c>entityCount: i32</c>, <c>mutationCount: i32</c>, <c>samplingInterval: i32</c>.</summary>
    StatisticsRebuild = 89,

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // Concurrency tracing (Phase 2, #280) — INSTANT events 90–116, no span header extension.
    // All gated on TelemetryConfig.Concurrency*Active leaf flags (Phase 1 Tier-2 mechanism).
    // See claude/design/observability/07-tracing-instrumentation/02-concurrency.md for details.
    //
    // TODO: per-resource contention metrics regression. The pre-#280 IContentionTarget pathway fed
    // ManagedPagedMMF/ComponentTable counters that surfaced as Contention.* columns in the resource
    // graph + Workbench Schema Inspector. Phase 2 deleted the producer (per Q2). Consumer-side
    // plumbing (IMetricWriter.WriteContention, ContentionMetrics, NodeSnapshot.Contention,
    // ResourceSnapshot.FindContentionHotspots, ResourceMetricsExporter contention OTel exports,
    // ResourceAlert.CascadingEffects, ResourceHealthChecker hotspot reporting) was removed in the
    // same change. To restore: either (a) add a per-lock id field to the wire format below and
    // build a trace-ring-fed aggregator, or (b) accept the loss as the new baseline. Tracked by
    // the umbrella issue at #277.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    // ── AccessControl (instant, large ref-counted lock) ──

    /// <summary>AccessControl shared (reader) acquire success. Payload: <c>threadId: u16</c>, <c>hadToWait: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyAccessControlSharedAcquire = 90,

    /// <summary>AccessControl shared (reader) release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSharedRelease = 91,

    /// <summary>AccessControl exclusive (writer) acquire success. Payload: <c>threadId: u16</c>, <c>hadToWait: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyAccessControlExclusiveAcquire = 92,

    /// <summary>AccessControl exclusive (writer) release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlExclusiveRelease = 93,

    /// <summary>AccessControl shared→exclusive promotion (or exclusive→shared demotion via variant byte). Payload: <c>elapsedUs: u16</c>, <c>variant: u8</c> (0=promote, 1=demote).</summary>
    ConcurrencyAccessControlPromotion = 94,

    /// <summary>AccessControl contention marker — flag-set instant, fires when a thread enters a wait. Payload: empty.</summary>
    ConcurrencyAccessControlContention = 95,

    // ── AccessControlSmall (instant, compact 4-byte lock) ──

    /// <summary>AccessControlSmall shared acquire. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallSharedAcquire = 96,

    /// <summary>AccessControlSmall shared release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallSharedRelease = 97,

    /// <summary>AccessControlSmall exclusive acquire. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallExclusiveAcquire = 98,

    /// <summary>AccessControlSmall exclusive release. Payload: <c>threadId: u16</c>.</summary>
    ConcurrencyAccessControlSmallExclusiveRelease = 99,

    /// <summary>AccessControlSmall contention marker. Payload: empty.</summary>
    ConcurrencyAccessControlSmallContention = 100,

    // ── ResourceAccessControl (instant, three-mode lock: Accessing/Modify/Destroy) ──

    /// <summary>ResourceAccessControl Accessing-mode acquire (try or wait). Payload: <c>success: u8</c>, <c>accessingCount: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceAccessing = 101,

    /// <summary>ResourceAccessControl Modify-mode acquire (try or wait). Payload: <c>success: u8</c>, <c>threadId: u16</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceModify = 102,

    /// <summary>ResourceAccessControl Destroy-mode acquire. Payload: <c>success: u8</c>, <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceDestroy = 103,

    /// <summary>ResourceAccessControl Modify promotion slow path (wait for accessors to drain). Payload: <c>elapsedUs: u16</c>.</summary>
    ConcurrencyResourceModifyPromotion = 104,

    /// <summary>ResourceAccessControl contention marker. Payload: empty.</summary>
    ConcurrencyResourceContention = 105,

    // ── Epoch (instant, EBR scope lifecycle) ──

    /// <summary>EpochGuard scope enter (PinCurrentThread). Payload: <c>currentEpoch: u32</c>, <c>depthBefore: u8</c>, <c>isDormantToActive: u8</c>.</summary>
    ConcurrencyEpochScopeEnter = 106,

    /// <summary>EpochGuard scope exit (Dispose). Payload: <c>newEpoch: u32</c>, <c>isOutermost: u8</c>.</summary>
    ConcurrencyEpochScopeExit = 107,

    /// <summary>GlobalEpoch advance (Interlocked.Increment in ExitScope/ExitScopeUnordered). Payload: <c>newEpoch: u32</c>.</summary>
    ConcurrencyEpochAdvance = 108,

    /// <summary>RefreshScope — bump epoch mid-scope to release retired memory while staying pinned. Payload: <c>oldEpoch: u32</c>, <c>newEpoch: u32</c>.</summary>
    ConcurrencyEpochRefresh = 109,

    /// <summary>EpochThreadRegistry slot claim. Payload: <c>slotIndex: u16</c>, <c>threadId: u16</c>, <c>activeCount: u16</c>.</summary>
    ConcurrencyEpochSlotClaim = 110,

    /// <summary>EpochThreadRegistry dead-thread slot reclaim. Payload: <c>slotIndex: u16</c>, <c>oldOwner: u16</c>, <c>newOwner: u16</c>.</summary>
    ConcurrencyEpochSlotReclaim = 111,

    // ── AdaptiveWaiter (instant, transition only) ──

    /// <summary>AdaptiveWaiter Wait() call yielded or slept (transition only — NOT per-spin). Payload: <c>spinCountBefore: u16</c>, <c>kind: u8</c> (1=yield, 2=sleep).</summary>
    ConcurrencyAdaptiveWaiterYieldOrSleep = 112,

    // ── OlcLatch (instant, optimistic-latch coordinator) ──

    /// <summary>OlcLatch.TryWriteLock failed (raced or write-locked). Payload: <c>versionBefore: u32</c>, <c>success: u8</c> (always 0 on emit).</summary>
    ConcurrencyOlcLatchWriteLockAttempt = 113,

    /// <summary>OlcLatch.WriteUnlock — version bumped from oldVersion to newVersion. Payload: <c>oldVersion: u32</c>, <c>newVersion: u32</c>.</summary>
    ConcurrencyOlcLatchWriteUnlock = 114,

    /// <summary>OlcLatch.MarkObsolete — node retired, future readers will fail validation. Payload: <c>version: u32</c>.</summary>
    ConcurrencyOlcLatchMarkObsolete = 115,

    /// <summary>OlcLatch.ValidateVersion failed — version mismatch detected on optimistic re-read. Payload: <c>expectedVersion: u32</c>, <c>actualVersion: u32</c>.</summary>
    ConcurrencyOlcLatchValidationFail = 116,

    // ── Fallback ──

    /// <summary>User-defined span with inline UTF-8 null-terminated name. Used for dynamic-string call sites (tests, demo code). Payload: null-terminated UTF-8 bytes.</summary>
    NamedSpan = 200,
}

/// <summary>
/// Helpers for <see cref="TraceEventKind"/> range classification. Used by readers and the consumer drain loop to decide which header shape to parse.
/// </summary>
public static class TraceEventKindExtensions
{
    /// <summary>
    /// <c>true</c> when the kind uses the span header extension (25-byte span preamble after the 12-byte common header, plus optional 16-byte trace context).
    /// Instant kinds (&lt; 10) use only the common header + optional tiny payload.
    /// </summary>
    /// <remarks>
    /// <see cref="TraceEventKind.PerTickSnapshot"/> is explicitly excluded: its numeric ID is ≥ 10 for category grouping with other metric
    /// records, but its wire shape is instant (no span header extension). Any future instant-style kind placed above 9 must be added to this
    /// exclusion — otherwise the consumer will misread the 25 bytes immediately after the common header as span metadata.
    /// <para>
    /// Concurrency tracing kinds 90–116 (Phase 2) are also instant-style and excluded as a contiguous range.
    /// </para>
    /// </remarks>
    public static bool IsSpan(this TraceEventKind kind)
    {
        var v = (byte)kind;
        if (v < 10)
        {
            return false;
        }
        if (kind == TraceEventKind.PerTickSnapshot || kind == TraceEventKind.ThreadInfo)
        {
            return false;
        }
        // Concurrency tracing instant range (Phase 2, #280): 90–116.
        if (v >= 90 && v <= 116)
        {
            return false;
        }
        return true;
    }
}

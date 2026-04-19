/**
 * Matches the TraceEventKind enum in Typhon.Engine.Profiler.Events.
 * Instant kinds < 10 carry no span header; span kinds ≥ 10 include SpanId/ParentSpanId/duration and optional trace context.
 */
export const enum TraceEventKind {
  // Instant
  TickStart = 0,
  TickEnd = 1,
  PhaseStart = 2,
  PhaseEnd = 3,
  SystemReady = 4,
  SystemSkipped = 5,
  Instant = 6,
  GcStart = 7,
  GcEnd = 8,
  MemoryAllocEvent = 9,

  // Span
  SchedulerChunk = 10,

  TransactionCommit = 20,
  TransactionRollback = 21,
  TransactionCommitComponent = 22,
  TransactionPersist = 23,

  EcsSpawn = 30,
  EcsDestroy = 31,
  EcsQueryExecute = 32,
  EcsQueryCount = 33,
  EcsQueryAny = 34,
  EcsViewRefresh = 35,

  BTreeInsert = 40,
  BTreeDelete = 41,
  BTreeNodeSplit = 42,
  BTreeNodeMerge = 43,

  PageCacheFetch = 50,
  PageCacheDiskRead = 51,
  PageCacheDiskWrite = 52,
  PageCacheAllocatePage = 53,
  PageCacheFlush = 54,
  PageEvicted = 55,
  PageCacheDiskReadCompleted = 56,
  PageCacheDiskWriteCompleted = 57,
  PageCacheFlushCompleted = 58,
  PageCacheBackpressure = 59,

  ClusterMigration = 60,

  WalFlush = 80,
  WalSegmentRotate = 81,
  WalWait = 82,

  CheckpointCycle = 83,
  CheckpointCollect = 84,
  CheckpointWrite = 85,
  CheckpointFsync = 86,
  CheckpointTransition = 87,
  CheckpointRecycle = 88,

  StatisticsRebuild = 89,

  GcSuspension = 75,
  /**
   * Per-tick gauge snapshot. Numerically ≥ 10 for category grouping with metric records, but wire-shape is INSTANT (no span header
   * extension) — <c>IsSpan()</c> excludes it on both server (C#) and client (this decoder). Special-cased at the top of
   * <c>decodeSpan</c> in <c>chunkDecoder.ts</c>.
   */
  PerTickSnapshot = 76,

  /**
   * Per-slot thread identity — emitted once when a producer thread claims its slot. Carries the managed thread ID and a UTF-8 thread
   * name so the viewer can label lanes with something meaningful. Wire shape is instant (no span header extension); the chunk decoder
   * routes it through its own case, and the chunk cache accumulates the entries into a slot→name map on <c>TraceMetadata</c>.
   */
  ThreadInfo = 77,

  NamedSpan = 200,
}

/**
 * Gauge identifiers. Mirrors <c>GaugeId</c> in <c>Typhon.Engine.Profiler.Events</c>. Wire-stable — numeric values are part of the
 * trace file format. IDs are grouped by category in 0x10 increments so future gauges in a category slot into their range without
 * renumbering existing entries.
 */
export const enum GaugeId {
  // Unmanaged memory (PinnedMemoryBlock via NativeMemory) — 0x0100
  MemoryUnmanagedTotalBytes = 0x0100,
  MemoryUnmanagedPeakBytes = 0x0101,
  MemoryUnmanagedLiveBlocks = 0x0102,

  // GC heap — sampled from GC.GetGCMemoryInfo() — 0x0110
  GcHeapGen0Bytes = 0x0110,
  GcHeapGen1Bytes = 0x0111,
  GcHeapGen2Bytes = 0x0112,
  GcHeapLohBytes = 0x0113,
  GcHeapPohBytes = 0x0114,
  GcHeapCommittedBytes = 0x0115,

  // Persistent store / page cache — 0x0200
  PageCacheTotalPages = 0x0200,           // fixed at init
  PageCacheFreePages = 0x0201,
  PageCacheCleanUsedPages = 0x0202,        // mutually-exclusive bucket
  PageCacheDirtyUsedPages = 0x0203,        // mutually-exclusive bucket
  PageCacheExclusivePages = 0x0204,
  PageCacheEpochProtectedPages = 0x0205,
  PageCachePendingIoReads = 0x0206,

  // Transient store — 0x0210
  TransientStoreBytesUsed = 0x0210,
  TransientStoreMaxBytes = 0x0211,         // fixed at init

  // WAL — 0x0300
  WalCommitBufferUsedBytes = 0x0300,
  WalCommitBufferCapacityBytes = 0x0301,   // fixed
  WalInflightFrames = 0x0302,
  WalStagingPoolRented = 0x0303,
  WalStagingPoolPeakRented = 0x0304,
  WalStagingPoolCapacity = 0x0305,         // fixed
  WalStagingTotalRentsCumulative = 0x0306, // cumulative — viewer derives rate from deltas

  // Transactions + UoW — 0x0400
  TxChainActiveCount = 0x0400,
  TxChainPoolSize = 0x0401,
  UowRegistryActiveCount = 0x0402,
  UowRegistryVoidCount = 0x0403,

  // Cumulative throughput counters — viewer derives per-tick deltas by subtracting consecutive snapshots — 0x0410
  TxChainCommitTotal = 0x0410,
  TxChainRollbackTotal = 0x0411,
  UowRegistryCreatedTotal = 0x0412,
  UowRegistryCommittedTotal = 0x0413,
  TxChainCreatedTotal = 0x0414,
}

/** On-wire value kind for a single gauge — mirrors <c>GaugeValueKind</c>. Not directly used on the client (the server already decoded); included for reference. */
export const enum GaugeValueKind {
  U32Count = 0,
  U64Bytes = 1,
  I64Signed = 2,
  U32PercentHundredths = 3,
}

/**
 * Set of gauge IDs whose values are fixed at initialization time (capacities). The engine emits these only in the first snapshot of a
 * session; subsequent snapshots omit them. The viewer caches the first-seen value so subsequent tick renders can still show the ceiling.
 */
export const FIXED_AT_INIT_GAUGES: ReadonlySet<GaugeId> = new Set<GaugeId>([
  GaugeId.PageCacheTotalPages,
  GaugeId.TransientStoreMaxBytes,
  GaugeId.WalCommitBufferCapacityBytes,
  GaugeId.WalStagingPoolCapacity,
]);

/** Direction of a MemoryAllocEvent — mirrors <c>MemoryAllocDirection</c>. */
export const enum MemoryAllocDirection {
  Alloc = 0,
  Free = 1,
}

/** Matches TickPhase enum in Typhon.Engine.Profiler.Events */
export const enum TickPhase {
  SystemDispatch = 0,
  UowFlush = 1,
  WriteTickFence = 2,
  OutputPhase = 3,
  TierIndexRebuild = 4,
  DormancySweep = 5
}

export const TickPhaseNames: Record<number, string> = {
  0: 'System Dispatch',
  1: 'UoW Flush',
  2: 'Write Tick Fence',
  3: 'Output Phase',
  4: 'Tier Index Rebuild',
  5: 'Dormancy Sweep'
};

export const SkipReasonNames: Record<number, string> = {
  0: 'Not Skipped',
  1: 'RunIf False',
  2: 'Empty Input',
  3: 'Empty Events',
  4: 'Throttled',
  5: 'Shed',
  6: 'Exception',
  7: 'Dependency Failed'
};

/**
 * Display name for each span kind. Used by the viewer whenever it needs to render a human-readable
 * label next to a span record. Update when new TraceEventKind values are added on the engine side.
 */
export const SpanKindNames: Record<number, string> = {
  [TraceEventKind.SchedulerChunk]: 'Scheduler.Chunk',
  [TraceEventKind.TransactionCommit]: 'Transaction.Commit',
  [TraceEventKind.TransactionRollback]: 'Transaction.Rollback',
  [TraceEventKind.TransactionCommitComponent]: 'Transaction.CommitComponent',
  [TraceEventKind.EcsSpawn]: 'ECS.Spawn',
  [TraceEventKind.EcsDestroy]: 'ECS.Destroy',
  [TraceEventKind.EcsQueryExecute]: 'ECS.Query.Execute',
  [TraceEventKind.EcsQueryCount]: 'ECS.Query.Count',
  [TraceEventKind.EcsQueryAny]: 'ECS.Query.Any',
  [TraceEventKind.EcsViewRefresh]: 'ECS.View.Refresh',
  [TraceEventKind.BTreeInsert]: 'BTree.Insert',
  [TraceEventKind.BTreeDelete]: 'BTree.Delete',
  [TraceEventKind.BTreeNodeSplit]: 'BTree.NodeSplit',
  [TraceEventKind.BTreeNodeMerge]: 'BTree.NodeMerge',
  [TraceEventKind.PageCacheFetch]: 'PageCache.Fetch',
  [TraceEventKind.PageCacheDiskRead]: 'PageCache.DiskRead',
  [TraceEventKind.PageCacheDiskWrite]: 'PageCache.DiskWrite',
  [TraceEventKind.PageCacheAllocatePage]: 'PageCache.AllocatePage',
  [TraceEventKind.PageCacheFlush]: 'PageCache.Flush',
  [TraceEventKind.PageEvicted]: 'PageCache.Evicted',
  [TraceEventKind.PageCacheDiskReadCompleted]: 'PageCache.DiskRead.Completed',
  [TraceEventKind.PageCacheDiskWriteCompleted]: 'PageCache.DiskWrite.Completed',
  [TraceEventKind.PageCacheFlushCompleted]: 'PageCache.Flush.Completed',
  [TraceEventKind.PageCacheBackpressure]: 'PageCache.Backpressure',
  [TraceEventKind.TransactionPersist]: 'Transaction.Persist',
  [TraceEventKind.WalFlush]: 'WAL.Flush',
  [TraceEventKind.WalSegmentRotate]: 'WAL.SegmentRotate',
  [TraceEventKind.WalWait]: 'WAL.Wait',
  [TraceEventKind.CheckpointCycle]: 'Checkpoint.Cycle',
  [TraceEventKind.CheckpointCollect]: 'Checkpoint.Collect',
  [TraceEventKind.CheckpointWrite]: 'Checkpoint.Write',
  [TraceEventKind.CheckpointFsync]: 'Checkpoint.Fsync',
  [TraceEventKind.CheckpointTransition]: 'Checkpoint.Transition',
  [TraceEventKind.CheckpointRecycle]: 'Checkpoint.Recycle',
  [TraceEventKind.StatisticsRebuild]: 'Statistics.Rebuild',
  [TraceEventKind.ClusterMigration]: 'Cluster.Migration',
  [TraceEventKind.GcSuspension]: 'GC.Suspension',
  [TraceEventKind.NamedSpan]: 'NamedSpan',
};

export interface TraceMetadata {
  header: {
    version: number;
    timestampFrequency: number;
    baseTickRate: number;
    workerCount: number;
    systemCount: number;
    archetypeCount: number;
    componentTypeCount: number;
    createdUtc: string;
    samplingSessionStartQpc: number;
  };
  systems: SystemDef[];
  archetypes: ArchetypeDef[];
  componentTypes: ComponentTypeDef[];
  /**
   * Per-slot thread names, populated as `ThreadInfo` records (kind 77) are decoded from the chunk stream. Sparse — slots without a
   * captured name have no entry. The viewer uses this to label lanes; missing entries fall back to "Slot {n}".
   */
  threadNames?: Record<number, string>;
}

export interface SystemDef {
  index: number;
  name: string;
  type: number;
  priority: number;
  isParallel: boolean;
  tierFilter: number;
  predecessors: number[];
  successors: number[];
}

export interface ArchetypeDef {
  archetypeId: number;
  name: string;
}

export interface ComponentTypeDef {
  componentTypeId: number;
  name: string;
}

/**
 * Flat trace record DTO — matches the server's LiveTraceEvent class. Fields are grouped by presence:
 *
 * - Always present: kind, threadSlot, tickNumber, timestampUs.
 * - Span kinds (≥ 10): also carry durationUs, spanId, parentSpanId; traceIdHi/Lo when the span captured an Activity context.
 * - Kind-specific: phase (PhaseStart/End), systemIndex (SystemReady/Skipped/SchedulerChunk), skipReason (SystemSkipped),
 *   chunkIndex/totalChunks/entitiesProcessed (SchedulerChunk), overloadLevel/tickMultiplier (TickEnd).
 *
 * 64-bit IDs arrive as 16-char lowercase hex strings because JavaScript's Number can't hold the full ulong range.
 */
export interface TraceEvent {
  kind: TraceEventKind;
  threadSlot: number;
  tickNumber: number;
  timestampUs: number;

  durationUs?: number;
  spanId?: string;
  parentSpanId?: string;
  traceIdHi?: string;
  traceIdLo?: string;

  // Instant-event fields
  phase?: number;
  systemIndex?: number;
  skipReason?: number;
  overloadLevel?: number;
  tickMultiplier?: number;

  // Scheduler chunk span
  chunkIndex?: number;
  totalChunks?: number;
  entitiesProcessed?: number;

  // Transaction spans
  tsn?: string;                // i64 hex string
  componentTypeId?: number;
  componentCount?: number;
  conflictDetected?: boolean;

  // ECS spans
  archetypeId?: number;
  entityId?: string;           // u64 hex string
  cascadeCount?: number;
  resultCount?: number;
  scanMode?: number;
  found?: boolean;
  mode?: number;
  deltaCount?: number;

  // Page-cache spans
  filePageIndex?: number;
  pageCount?: number;

  // Cluster migration span
  migrationCount?: number;

  // Transaction persist
  walLsn?: string;

  // Page cache backpressure
  retryCount?: number;
  dirtyCount?: number;
  epochCount?: number;

  // WAL spans
  batchByteCount?: number;
  frameCount?: number;
  highLsn?: string;
  newSegmentIndex?: number;
  targetLsn?: string;

  // Checkpoint spans
  dirtyPageCount?: number;
  reason?: number;
  writtenCount?: number;
  transitionedCount?: number;
  recycledCount?: number;

  // Statistics
  entityCount?: number;
  mutationCount?: number;
  samplingInterval?: number;

  // Memory allocation (kind 9 — instant)
  direction?: number;        // 0 = alloc, 1 = free
  sourceTag?: number;         // u16 interned tag
  sizeBytes?: number;
  totalAfterBytes?: number;

  // Per-tick gauge snapshot (kind 76). Keys are GaugeId as number → double values. Server emits them as a JSON object,
  // which deserializes here as <c>Record&lt;number, number&gt;</c> (TypeScript numeric keys are strings at the JSON layer
  // but flow through as numbers via <c>Number.parseInt</c> at consumption).
  flags?: number;
  gauges?: Record<number, number>;

  // GC events (kinds 7, 8). Generation + counts on both; reason/type on GcStart; pause duration + promoted bytes on GcEnd.
  generation?: number;
  gcReason?: number;
  gcType?: number;
  gcCount?: number;
  gcPauseDurationUs?: number;
  gcPromotedBytes?: number;

  // ThreadInfo (kind 77). Emitted once per slot claim; carries the managed thread ID and the thread's name (if set).
  managedThreadId?: number;
  threadName?: string;
}

/**
 * One per-tick summary row from /api/trace/open. Matches the server's TickSummary struct (24 bytes). Used to drive the timeline overview without
 * loading any detail events — populated from the sidecar cache, which is built lazily on first open and reused thereafter.
 */
export interface TickSummary {
  tickNumber: number;
  /** Absolute start timestamp in microseconds (same origin as globalStartUs). Enables viewport-range → tick-number lookup without loading chunks. */
  startUs: number;
  durationUs: number;
  eventCount: number;
  maxSystemDurationUs: number;
  /** 64-bit bitmask serialized as a decimal string (exact precision preserved). Bit N set iff system index N ran in this tick (capped at 64). */
  activeSystemsBitmask: string;
}

/** Aggregate duration per system across the whole trace. */
export interface SystemAggregate {
  systemIndex: number;
  invocationCount: number;
  totalDurationUs: number;
}

/** Global metrics computed once by the sidecar cache build. Available immediately on open. */
export interface GlobalMetrics {
  globalStartUs: number;
  globalEndUs: number;
  maxTickDurationUs: number;
  maxSystemDurationUs: number;
  p95TickDurationUs: number;
  totalEvents: number;
  totalTicks: number;
  systemAggregates: SystemAggregate[];
}

/** One entry in the chunk manifest — a tick range the server can serve as a single chunk. */
export interface ChunkManifestEntry {
  fromTick: number;
  toTick: number;
  eventCount: number;
}

/** /api/trace/open response shape — metadata + summary + global metrics + chunk manifest in one payload. */
export interface OpenTraceResponse {
  status: 'ready' | 'building';
  /**
   * Hex-encoded SHA-256 fingerprint of the source trace file (computed from mtime, length, and a prefix/suffix sample).
   * Used by the client's OPFS chunk store as the invalidation key: same fingerprint = chunks still valid; different fingerprint
   * (source rebuilt) = cached chunks become orphaned and get garbage-collected by the global cleanup sweep.
   */
  fingerprint: string;
  header: TraceMetadata['header'];
  systems: TraceMetadata['systems'];
  archetypes: TraceMetadata['archetypes'];
  componentTypes: TraceMetadata['componentTypes'];
  spanNames: Record<number, string>;
  globalMetrics: GlobalMetrics;
  tickSummaries: TickSummary[];
  chunkManifest: ChunkManifestEntry[];
  /**
   * Full GC-suspension list for the whole trace, delivered at open time so the per-tick pause-time chart in the GC track can use a
   * STABLE yMax across all chunk load/evict cycles. Without this, yMax was derived from whichever chunks were currently resident and
   * rescaled visibly as the LRU turned over. The server computes this once per session-slot lifetime by walking every chunk and
   * filtering GcSuspension records — cached thereafter. Typical payload is a few hundred entries × ~40 bytes = under 20 KB.
   */
  gcSuspensions: { startUs: number; durationUs: number; threadSlot: number }[];
}

/** /api/trace/chunk response shape — events for a specific tick range. */
export interface ChunkResponse {
  fromTick: number;
  toTick: number;
  events: TraceEvent[];
}

/** A tick source abstracts over file-based and live-streamed data */
export interface TickSource {
  readonly metadata: TraceMetadata;
  getEvents(fromTick: number, toTick: number): Promise<TraceEvent[]>;
}

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

  NamedSpan = 200,
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
  header: TraceMetadata['header'];
  systems: TraceMetadata['systems'];
  archetypes: TraceMetadata['archetypes'];
  componentTypes: TraceMetadata['componentTypes'];
  spanNames: Record<number, string>;
  globalMetrics: GlobalMetrics;
  tickSummaries: TickSummary[];
  chunkManifest: ChunkManifestEntry[];
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

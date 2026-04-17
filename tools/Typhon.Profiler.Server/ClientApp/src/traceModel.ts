import {
  TraceEventKind,
  type TraceEvent,
  type SystemDef,
  type TraceMetadata,
  SpanKindNames,
} from './types';

/** A chunk span: one rectangle in the Gantt chart */
export interface ChunkSpan {
  systemIndex: number;
  systemName: string;
  chunkIndex: number;
  threadSlot: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  entitiesProcessed: number;
  totalChunks: number;
  isParallel: boolean;
}

/** A tick phase span */
export interface PhaseSpan {
  phase: number;
  phaseName: string;
  startUs: number;
  endUs: number;
  durationUs: number;
}

/** Skip event for a system */
export interface SkipEvent {
  systemIndex: number;
  systemName: string;
  reason: number;
  reasonName: string;
  timestampUs: number;
}

/**
 * A generic Typhon span captured via `TyphonEvent.BeginXxx` (B+Tree / Transaction / ECS / PageCache / Cluster / NamedSpan).
 * Every span-kind record arrives already with its duration in the wire format, so no Start/End pairing is needed.
 *
 * **Async-completion enrichment.** For the three PageCache.* kinds that can emit a paired completion record
 * (DiskRead / DiskWrite / Flush), the kickoff span is pushed first with `durationUs` set to the synchronous kickoff cost, then
 * replaced in-place when the matching `*Completed` record arrives: `endUs` and `durationUs` are rewritten to cover the full async
 * tail, and `kickoffDurationUs` preserves the original synchronous kickoff cost so the viewer can render both numbers. When no
 * completion record arrives (completion kind suppressed, or the record was dropped from the ring), the span stays as kickoff-only
 * and `kickoffDurationUs` is undefined.
 */
export interface SpanData {
  kind: TraceEventKind;
  name: string;
  threadSlot: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  spanId?: string;
  parentSpanId?: string;
  traceIdHi?: string;
  traceIdLo?: string;
  /** Original synchronous-kickoff duration in µs, set only after the async-completion record has folded into this span. */
  kickoffDurationUs?: number;
  /**
   * Parent-chain nesting depth. 0 = no parent (or parent not captured in this tick). Computed at tick processing time by walking
   * parentSpanId links up to the root. Used by the GraphArea OTel spans track to assign each span a stable row by hierarchical
   * depth rather than by kind — which is what the OTel model actually describes: spans form a tree, row-by-depth renders that
   * tree the same way a flame graph does. Sanity-capped at 32 to prevent infinite loops if the chain ever cycles.
   */
  depth?: number;
  /** Full DTO event — carried so the DetailPane can show kind-specific fields without duplicating them on SpanData. */
  rawEvent?: TraceEvent;
}

/** All data for a single tick */
export interface TickData {
  tickNumber: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  chunks: ChunkSpan[];
  phases: PhaseSpan[];
  skips: SkipEvent[];
  spans: SpanData[];
  /**
   * Parallel to <c>spans</c>: running max of <c>spans[0..i].endUs</c>. Enables O(log N) left-edge visibility search —
   * binary-search this for the first index whose running-max ≥ viewStartUs and iteration can start there with zero
   * off-screen-left scanning. Without this, a naive walk-back from the sorted-by-startUs binary search misses any long
   * parent span that was interrupted by shorter children finishing before the viewport.
   */
  spanEndMaxRunning: Float64Array;
  /**
   * Spans grouped by owning thread slot. Each inner array is sorted by startUs (inherited from the global sort on <c>spans</c>).
   * Populated in <see cref="processTickEvents"/>. Enables per-slot rendering in <c>GraphArea</c> where OTel span bars are drawn
   * beneath the chunk bar of their owning thread's lane instead of in a separate track.
   */
  spansByThreadSlot: Map<number, SpanData[]>;
  /**
   * Per-slot running-max endUs array, keyed by thread slot — same shape as <see cref="spanEndMaxRunning"/> but scoped to
   * one slot at a time, so the render loop can binary-search each lane's span list independently.
   */
  spanEndMaxByThreadSlot: Map<number, Float64Array>;
  /**
   * Deepest observed span nesting per thread slot, used by <c>GraphArea.buildLayout</c> to grow each slot lane just tall
   * enough to contain its deepest span chain. A slot with no spans has no entry in the map (renders with only the chunk row).
   */
  spanMaxDepthByThreadSlot: Map<number, number>;
  /**
   * Disk read operations (PageCacheDiskRead + orphan DiskReadCompleted) projected across all thread slots, sorted by startUs.
   * Used by the Disk IO track to render green bars on the "reads" sub-row. When async-completion folding is active, each
   * span's <c>durationUs</c> is the full device occupancy time (kickoff → completion), not just the synchronous kickoff.
   */
  diskReads: SpanData[];
  diskReadsEndMax: Float64Array;
  /**
   * Disk write operations (PageCacheDiskWrite + PageCacheFlush + orphan WriteCompleted/FlushCompleted) projected across all
   * thread slots, sorted by startUs. Used by the Disk IO track to render blue bars on the "writes" sub-row.
   */
  diskWrites: SpanData[];
  diskWritesEndMax: Float64Array;
  /** Cache-miss chain (Fetch + AllocatePage + PageEvicted) projected across all slots, sorted by startUs. Each kind draws
   *  in its own color on the same "Misses" sub-row so overlapping parent/child spans (Fetch wrapping Allocate) are visible. */
  cacheMisses: SpanData[];
  cacheMissesEndMax: Float64Array;
  /** Page cache flush operations (checkpoint coordination) projected across all slots, sorted by startUs. */
  cacheFlushes: SpanData[];
  cacheFlushesEndMax: Float64Array;
  cacheFetch: SpanData[];
  cacheFetchEndMax: Float64Array;
  cacheAlloc: SpanData[];
  cacheAllocEndMax: Float64Array;
  cacheEvict: SpanData[];
  cacheEvictEndMax: Float64Array;
  // Transaction projection
  txCommits: SpanData[];
  txCommitsEndMax: Float64Array;
  txRollbacks: SpanData[];
  txRollbacksEndMax: Float64Array;
  txPersists: SpanData[];
  txPersistsEndMax: Float64Array;
  // WAL projection
  walFlushes: SpanData[];
  walFlushesEndMax: Float64Array;
  walWaits: SpanData[];
  walWaitsEndMax: Float64Array;
  // Checkpoint projection
  checkpointCycles: SpanData[];
  checkpointCyclesEndMax: Float64Array;
  /** Per-system aggregate duration in µs (for heat map) */
  systemDurations: Map<number, number>;
}

/** Processed trace ready for rendering */
export interface ProcessedTrace {
  metadata: TraceMetadata;
  ticks: TickData[];
  /** All tick numbers, sorted */
  tickNumbers: number[];
  /** Min/max timestamps across all ticks */
  globalStartUs: number;
  globalEndUs: number;
  /** Max per-system duration across all ticks (for heat map color scaling) */
  maxSystemDurationUs: number;
  /** Max tick duration */
  maxTickDurationUs: number;
  /** P95 tick duration for timeline bar height scaling */
  p95TickDurationUs: number;
  /** System color palette */
  systemColors: string[];
  /**
   * Per-tick summary from the sidecar cache, one entry per tick in the whole file. Always-resident across the full timeline so the overview can
   * render immediately on open without loading any detail events. Present when a `/api/trace/open` response has been consumed; undefined for
   * live-mode traces (no cache in that path yet).
   */
  summary?: import('./types').TickSummary[];
}

const PHASE_NAMES: Record<number, string> = {
  0: 'System Dispatch',
  1: 'UoW Flush',
  2: 'Write Tick Fence',
  3: 'Output Phase',
  4: 'Tier Index Rebuild',
  5: 'Dormancy Sweep'
};

const SKIP_REASON_NAMES: Record<number, string> = {
  0: 'Not Skipped',
  1: 'RunIf False',
  2: 'Empty Input',
  3: 'Empty Events',
  4: 'Throttled',
  5: 'Shed',
  6: 'Exception',
  7: 'Dependency Failed'
};

/** Color palette for systems — visually distinct, dark-theme friendly */
const SYSTEM_PALETTE = [
  '#e94560', '#4ecdc4', '#ffe66d', '#95e1d3', '#f38181',
  '#aa96da', '#a8d8ea', '#fcbad3', '#c3aed6', '#b8de6f',
  '#ff9a76', '#679b9b', '#ffc4a3', '#e8a87c', '#41b3a3',
  '#d63447', '#f57b51', '#f6efa6', '#5ee7df', '#b490ca',
];

/** Generate system color based on index */
function generateSystemColors(count: number): string[] {
  const colors: string[] = [];
  for (let i = 0; i < count; i++) {
    colors.push(SYSTEM_PALETTE[i % SYSTEM_PALETTE.length]);
  }
  return colors;
}

/** Process raw records into renderable tick structures */
export function processTrace(metadata: TraceMetadata, events: TraceEvent[]): ProcessedTrace {
  const systems = metadata.systems;
  const systemColors = generateSystemColors(systems.length);

  // Group records by derived tick number (the server already assigns tickNumber on every record).
  const tickMap = new Map<number, TraceEvent[]>();
  for (const evt of events) {
    let list = tickMap.get(evt.tickNumber);
    if (!list) {
      list = [];
      tickMap.set(evt.tickNumber, list);
    }
    list.push(evt);
  }

  const ticks: TickData[] = [];
  let maxSystemDurationUs = 0;
  let maxTickDurationUs = 0;

  for (const [tickNumber, tickEvents] of tickMap) {
    const tickData = processTickEvents(tickNumber, tickEvents, systems);
    if (tickData.durationUs > maxTickDurationUs) {
      maxTickDurationUs = tickData.durationUs;
    }
    for (const dur of tickData.systemDurations.values()) {
      if (dur > maxSystemDurationUs) {
        maxSystemDurationUs = dur;
      }
    }
    ticks.push(tickData);
  }

  ticks.sort((a, b) => a.tickNumber - b.tickNumber);
  const tickNumbers = ticks.map(t => t.tickNumber);

  const globalStartUs = ticks.length > 0 ? ticks[0].startUs : 0;
  const globalEndUs = ticks.length > 0 ? ticks[ticks.length - 1].endUs : 0;

  const sortedDurations = ticks.map(t => t.durationUs).sort((a, b) => a - b);
  const p95Idx = Math.floor(sortedDurations.length * 0.95);
  const p95TickDurationUs = sortedDurations.length > 0
    ? sortedDurations[Math.min(p95Idx, sortedDurations.length - 1)]
    : 0;

  return {
    metadata,
    ticks,
    tickNumbers,
    globalStartUs,
    globalEndUs,
    maxSystemDurationUs,
    maxTickDurationUs,
    p95TickDurationUs,
    systemColors
  };
}

/**
 * Find all ticks that overlap with the given absolute time range. Uses strict half-open semantics: a tick is considered overlapping iff
 * `tick.endUs > startUs && tick.startUs < endUs`. Back-to-back ticks that only TOUCH the boundary (tick N+1 starts exactly where tick N
 * ends) are NOT counted as overlapping — otherwise a viewRange covering exactly tick N would spill GraphArea's render list into ticks
 * N-1 and N+1, which is what "tick 2 shows no bar" was actually caused by (the empty sliver gets hidden between its much-larger neighbours).
 */
export function getTicksInRange(trace: ProcessedTrace, startUs: number, endUs: number): TickData[] {
  return trace.ticks.filter(t => t.endUs > startUs && t.startUs < endUs);
}

/** Find the tick index (in trace.ticks array) for a given tick number */
export function findTickIndex(trace: ProcessedTrace, tickNumber: number): number {
  return trace.ticks.findIndex(t => t.tickNumber === tickNumber);
}

export function processTickEvents(tickNumber: number, events: TraceEvent[], systems: SystemDef[]): TickData {
  let startUs = Infinity;
  let endUs = -Infinity;

  const chunks: ChunkSpan[] = [];
  const phases: PhaseSpan[] = [];
  const skips: SkipEvent[] = [];
  const spans: SpanData[] = [];
  const systemDurations = new Map<number, number>();

  // Phases are still emitted as Start/End instant pairs — keep a short-lived map to pair them up.
  const openPhases = new Map<number, TraceEvent>();

  // ── Async-completion folding (PageCache.DiskRead/DiskWrite/Flush + their *Completed counterparts) ──
  //
  // Kickoff and completion records share the same SpanId (by design — the correlator). The consumer-side sort is by start
  // timestamp, and BOTH records carry the same begin-time as their start, so when the kickoff and completion tie the sort
  // order is undefined (introsort is not stable). We need to handle BOTH orderings without duplicating the span:
  //
  //   • kickoffById: maps spanId → the already-pushed kickoff span so a later completion can rewrite its duration in-place.
  //   • pendingCompletion: maps spanId → the whole TraceEvent for completions that arrived BEFORE their kickoff. When the
  //     kickoff is later processed it consults this map and folds immediately. Any entries left at tick end (orphan
  //     completions with no matching kickoff in this tick) are pushed as standalone spans so the data isn't silently lost.
  //
  // Cross-tick completions (I/O taking longer than one tick) land in a different `events` array and won't find their match
  // in either map — the completion is pushed as a standalone span at the end-of-tick orphan flush below, so the data is
  // never lost, just not visually paired with its kickoff. That's rare on a healthy SSD and can be inspected via SpanId
  // search in the detail pane.
  const kickoffById = new Map<string, SpanData>();
  const pendingCompletion = new Map<string, TraceEvent>();

  for (const evt of events) {
    switch (evt.kind) {
      case TraceEventKind.TickStart:
        startUs = evt.timestampUs;
        break;

      case TraceEventKind.TickEnd:
        endUs = evt.timestampUs;
        break;

      case TraceEventKind.PhaseStart:
        if (evt.phase !== undefined) {
          openPhases.set(evt.phase, evt);
        }
        break;

      case TraceEventKind.PhaseEnd: {
        if (evt.phase === undefined) break;
        const open = openPhases.get(evt.phase);
        if (open) {
          const dur = evt.timestampUs - open.timestampUs;
          phases.push({
            phase: evt.phase,
            phaseName: PHASE_NAMES[evt.phase] ?? `Phase ${evt.phase}`,
            startUs: open.timestampUs,
            endUs: evt.timestampUs,
            durationUs: dur
          });
          openPhases.delete(evt.phase);
        }
        break;
      }

      case TraceEventKind.SystemSkipped: {
        const sysIdx = evt.systemIndex ?? 0;
        const reason = evt.skipReason ?? 0;
        skips.push({
          systemIndex: sysIdx,
          systemName: systems[sysIdx]?.name ?? `System[${sysIdx}]`,
          reason,
          reasonName: SKIP_REASON_NAMES[reason] ?? `Unknown(${reason})`,
          timestampUs: evt.timestampUs
        });
        break;
      }

      case TraceEventKind.SchedulerChunk: {
        // Single-record span: timestampUs is the start, durationUs is the full width. No pairing.
        const sysIdx = evt.systemIndex ?? 0;
        const duration = evt.durationUs ?? 0;
        const sys = systems[sysIdx];
        chunks.push({
          systemIndex: sysIdx,
          systemName: sys?.name ?? `System[${sysIdx}]`,
          chunkIndex: evt.chunkIndex ?? 0,
          threadSlot: evt.threadSlot,
          startUs: evt.timestampUs,
          endUs: evt.timestampUs + duration,
          durationUs: duration,
          entitiesProcessed: evt.entitiesProcessed ?? 0,
          totalChunks: evt.totalChunks ?? 1,
          isParallel: sys?.isParallel ?? false
        });

        const existing = systemDurations.get(sysIdx) ?? 0;
        systemDurations.set(sysIdx, existing + duration);
        break;
      }

      default: {
        // Every other span kind (≥ 10) is surfaced as a generic SpanData entry — spans already carry duration directly.
        if (evt.kind >= 10 && evt.durationUs !== undefined) {
          const duration = evt.durationUs;

          // Async-completion fold path: if this is one of the three *Completed kinds, try to rewrite an existing kickoff span
          // with the full async duration. If the kickoff hasn't been seen yet (sort ties flipped the order), stash the duration
          // in pendingCompletion so the kickoff can fold it in when it arrives.
          const isCompletion =
            evt.kind === TraceEventKind.PageCacheDiskReadCompleted ||
            evt.kind === TraceEventKind.PageCacheDiskWriteCompleted ||
            evt.kind === TraceEventKind.PageCacheFlushCompleted;

          if (isCompletion && evt.spanId !== undefined) {
            const kickoff = kickoffById.get(evt.spanId);
            if (kickoff !== undefined) {
              // Found the kickoff — rewrite its duration to the full async tail, preserving the original kickoff duration for the viewer.
              kickoff.kickoffDurationUs = kickoff.durationUs;
              kickoff.durationUs = duration;
              kickoff.endUs = kickoff.startUs + duration;
            } else {
              // Kickoff hasn't been processed yet — stash the full event; the kickoff branch (or the end-of-tick orphan flush) folds it in.
              pendingCompletion.set(evt.spanId, evt);
            }
            break;
          }

          // Kickoff / generic span path: push a new SpanData. If a pending completion already exists for this SpanId (the sort
          // put the completion record ahead of the kickoff), fold immediately so only ONE span shows up for the pair.
          const span: SpanData = {
            kind: evt.kind,
            name: SpanKindNames[evt.kind] ?? `Kind[${evt.kind}]`,
            threadSlot: evt.threadSlot,
            startUs: evt.timestampUs,
            endUs: evt.timestampUs + duration,
            durationUs: duration,
            spanId: evt.spanId,
            parentSpanId: evt.parentSpanId,
            traceIdHi: evt.traceIdHi,
            traceIdLo: evt.traceIdLo,
            rawEvent: evt,
          };
          spans.push(span);

          if (evt.spanId !== undefined) {
            kickoffById.set(evt.spanId, span);
            const pending = pendingCompletion.get(evt.spanId);
            if (pending !== undefined && pending.durationUs !== undefined) {
              span.kickoffDurationUs = span.durationUs;
              span.durationUs = pending.durationUs;
              span.endUs = span.startUs + pending.durationUs;
              pendingCompletion.delete(evt.spanId);
            }
          }
        }
        break;
      }
    }
  }

  // Orphan completion flush: any completion records whose kickoff never arrived in this tick (because the kickoff was
  // suppressed, dropped from the ring, or landed in a different tick) are pushed as standalone spans. They'll render under
  // their *Completed display name with the full async duration — the viewer just won't show the kickoff-vs-tail split.
  if (pendingCompletion.size > 0) {
    for (const evt of pendingCompletion.values()) {
      if (evt.durationUs === undefined) continue;
      spans.push({
        kind: evt.kind,
        name: SpanKindNames[evt.kind] ?? `Kind[${evt.kind}]`,
        threadSlot: evt.threadSlot,
        startUs: evt.timestampUs,
        endUs: evt.timestampUs + evt.durationUs,
        durationUs: evt.durationUs,
        spanId: evt.spanId,
        parentSpanId: evt.parentSpanId,
        traceIdHi: evt.traceIdHi,
        traceIdLo: evt.traceIdLo,
      });
    }
  }

  if (startUs === Infinity) startUs = 0;
  if (endUs === -Infinity) endUs = startUs;

  // Sort spans by (startUs, kind, spanId) so the render loop iterates in a canonical order and binary-searching for the first
  // visible span by startUs works correctly. Secondary sort keys (kind, then spanId) break ties deterministically — without them
  // two spans that share a start timestamp (like a kickoff and its folded completion before rewrite) could swap positions on
  // repeat re-processing, which would show up as visual flicker in the render layer.
  spans.sort((a, b) => {
    if (a.startUs !== b.startUs) return a.startUs - b.startUs;
    if (a.kind !== b.kind) return a.kind - b.kind;
    const as = a.spanId ?? '';
    const bs = b.spanId ?? '';
    return as < bs ? -1 : as > bs ? 1 : 0;
  });

  // ── Depth computation ──────────────────────────────────────────────────────
  // Walk parentSpanId links to build each span's parent-chain depth. Row N in the OTel track = depth N, so this is the only
  // place that controls "which row does this span draw on".
  //
  // **Zero detection is critical here.** The server encodes SpanIds as 16-char lowercase hex via `value.ToString("x16")`, so
  // a "no parent" span comes through with `parentSpanId == "0000000000000000"`, NOT empty string and NOT the literal "0".
  // The previous version of this walk only filtered out '' and '0', leaving "0000000000000000" to fall through into a
  // failed map lookup that always landed at depth 0 — so every span was at depth 0 and every bar stacked on row 0. The
  // ZERO_HEX_SPANID constant + explicit equality check is the actual fix for "bars not staying in their depth row".
  const ZERO_HEX_SPANID = '0000000000000000';
  const spanById = new Map<string, SpanData>();
  for (const s of spans) {
    if (s.spanId !== undefined && s.spanId !== ZERO_HEX_SPANID) {
      spanById.set(s.spanId, s);
    }
  }
  for (const s of spans) {
    let depth = 0;
    let parentId = s.parentSpanId;
    const visited = new Set<string>();
    // Loop continues only when parentId is a real, non-zero span ID. Treat undefined / null / empty / 16-zeroes / single "0"
    // all as "no parent" so any future encoding tweak still terminates the walk cleanly.
    while (parentId !== undefined && parentId !== null && parentId !== '' && parentId !== '0' && parentId !== ZERO_HEX_SPANID) {
      if (visited.has(parentId)) break;  // cycle guard — shouldn't happen with monotonic SpanId generation but cheap to check
      visited.add(parentId);
      const parent = spanById.get(parentId);
      if (parent === undefined) break;   // parent not in this tick — truncate here
      depth++;
      if (depth > 32) break;             // sanity cap — anything deeper is almost certainly a runaway walk
      parentId = parent.parentSpanId;
    }
    s.depth = depth;
  }

  // Running-max endUs array — see TickData.spanEndMaxRunning for rationale. Built after the sort so the order lines up with
  // spans[]. Float64Array keeps it compact (~7 MB for a 900K-span tick vs ~25 MB for a boxed number[]).
  const spanEndMaxRunning = new Float64Array(spans.length);
  {
    let runMax = -Infinity;
    for (let i = 0; i < spans.length; i++) {
      const e = spans[i].endUs;
      if (e > runMax) runMax = e;
      spanEndMaxRunning[i] = runMax;
    }
  }

  // Per-slot indexes. Because the global spans[] is already sorted by startUs, filtering by threadSlot preserves that
  // order — no per-slot re-sort is needed. We also walk once to track max depth per slot so the layout pass can size
  // each slot's lane without a second full traversal.
  const spansByThreadSlot = new Map<number, SpanData[]>();
  const spanMaxDepthByThreadSlot = new Map<number, number>();
  for (const s of spans) {
    let arr = spansByThreadSlot.get(s.threadSlot);
    if (arr === undefined) {
      arr = [];
      spansByThreadSlot.set(s.threadSlot, arr);
    }
    arr.push(s);
    const d = s.depth ?? 0;
    const prev = spanMaxDepthByThreadSlot.get(s.threadSlot) ?? -1;
    if (d > prev) spanMaxDepthByThreadSlot.set(s.threadSlot, d);
  }
  const spanEndMaxByThreadSlot = new Map<number, Float64Array>();
  for (const [slot, arr] of spansByThreadSlot) {
    const em = new Float64Array(arr.length);
    let runMax = -Infinity;
    for (let i = 0; i < arr.length; i++) {
      const e = arr[i].endUs;
      if (e > runMax) runMax = e;
      em[i] = runMax;
    }
    spanEndMaxByThreadSlot.set(slot, em);
  }

  // Per-kind projections — one pass over spans[], switch(kind) dispatch. spans[] is already sorted by (startUs, kind, spanId),
  // so every bucket inherits that order and no per-bucket sort is needed. `cacheMisses` is the union of Fetch + AllocatePage +
  // PageEvicted and is populated alongside the per-kind sub-buckets in the same iteration. Running-max Float64Arrays are built
  // after bucketing because their size is only known then.
  const diskReads: SpanData[] = [];
  const diskWrites: SpanData[] = [];
  const cacheMisses: SpanData[] = [];
  const cacheFlushes: SpanData[] = [];
  const cacheFetch: SpanData[] = [];
  const cacheAlloc: SpanData[] = [];
  const cacheEvict: SpanData[] = [];
  const txCommits: SpanData[] = [];
  const txRollbacks: SpanData[] = [];
  const txPersists: SpanData[] = [];
  const walFlushes: SpanData[] = [];
  const walWaits: SpanData[] = [];
  const checkpointCycles: SpanData[] = [];

  for (const s of spans) {
    switch (s.kind) {
      case TraceEventKind.PageCacheDiskRead:
      case TraceEventKind.PageCacheDiskReadCompleted:
        diskReads.push(s);
        break;
      case TraceEventKind.PageCacheDiskWrite:
      case TraceEventKind.PageCacheDiskWriteCompleted:
        diskWrites.push(s);
        break;
      case TraceEventKind.PageCacheFetch:
        cacheMisses.push(s);
        cacheFetch.push(s);
        break;
      case TraceEventKind.PageCacheAllocatePage:
        cacheMisses.push(s);
        cacheAlloc.push(s);
        break;
      case TraceEventKind.PageEvicted:
        cacheMisses.push(s);
        cacheEvict.push(s);
        break;
      case TraceEventKind.PageCacheFlush:
      case TraceEventKind.PageCacheFlushCompleted:
        cacheFlushes.push(s);
        break;
      case TraceEventKind.TransactionCommit:
        txCommits.push(s);
        break;
      case TraceEventKind.TransactionRollback:
        txRollbacks.push(s);
        break;
      case TraceEventKind.TransactionPersist:
        txPersists.push(s);
        break;
      case TraceEventKind.WalFlush:
      case TraceEventKind.WalSegmentRotate:
        walFlushes.push(s);
        break;
      case TraceEventKind.WalWait:
        walWaits.push(s);
        break;
      case TraceEventKind.CheckpointCycle:
        checkpointCycles.push(s);
        break;
    }
  }

  const buildRunMax = (arr: SpanData[]) => {
    const em = new Float64Array(arr.length);
    let rm = -Infinity;
    for (let i = 0; i < arr.length; i++) {
      if (arr[i].endUs > rm) rm = arr[i].endUs;
      em[i] = rm;
    }
    return em;
  };
  const diskReadsEndMax = buildRunMax(diskReads);
  const diskWritesEndMax = buildRunMax(diskWrites);
  const cacheMissesEndMax = buildRunMax(cacheMisses);
  const cacheFlushesEndMax = buildRunMax(cacheFlushes);
  const cacheFetchEndMax = buildRunMax(cacheFetch);
  const cacheAllocEndMax = buildRunMax(cacheAlloc);
  const cacheEvictEndMax = buildRunMax(cacheEvict);
  const txCommitsEndMax = buildRunMax(txCommits);
  const txRollbacksEndMax = buildRunMax(txRollbacks);
  const txPersistsEndMax = buildRunMax(txPersists);
  const walFlushesEndMax = buildRunMax(walFlushes);
  const walWaitsEndMax = buildRunMax(walWaits);
  const checkpointCyclesEndMax = buildRunMax(checkpointCycles);

  return {
    tickNumber,
    startUs,
    endUs,
    durationUs: endUs - startUs,
    chunks,
    phases,
    skips,
    spans,
    spanEndMaxRunning,
    spansByThreadSlot,
    spanEndMaxByThreadSlot,
    spanMaxDepthByThreadSlot,
    diskReads,
    diskReadsEndMax,
    diskWrites,
    diskWritesEndMax,
    cacheMisses,
    cacheMissesEndMax,
    cacheFlushes,
    cacheFlushesEndMax,
    cacheFetch, cacheFetchEndMax,
    cacheAlloc, cacheAllocEndMax,
    cacheEvict, cacheEvictEndMax,
    txCommits, txCommitsEndMax,
    txRollbacks, txRollbacksEndMax,
    txPersists, txPersistsEndMax,
    walFlushes, walFlushesEndMax,
    walWaits, walWaitsEndMax,
    checkpointCycles, checkpointCyclesEndMax,
    systemDurations
  };
}

/** Maximum ticks to keep in memory during live streaming (~50s at 60Hz). */
const MAX_LIVE_TICKS = 3000;

/** Create an empty ProcessedTrace for live session initialization. */
export function createEmptyTrace(metadata: TraceMetadata): ProcessedTrace {
  return {
    metadata,
    ticks: [],
    tickNumbers: [],
    globalStartUs: 0,
    globalEndUs: 0,
    maxSystemDurationUs: 0,
    maxTickDurationUs: 0,
    p95TickDurationUs: 0,
    systemColors: generateSystemColors(metadata.systems.length)
  };
}

/**
 * Incrementally process a single tick and append it to an existing ProcessedTrace.
 * Used in live streaming mode to avoid reprocessing the entire trace on every tick.
 */
export function processTickAndAppend(
  trace: ProcessedTrace,
  tickNumber: number,
  events: TraceEvent[]
): ProcessedTrace {
  const tickData = processTickEvents(tickNumber, events, trace.metadata.systems);

  const ticks = [...trace.ticks, tickData];
  const tickNumbers = [...trace.tickNumbers, tickNumber];

  if (ticks.length > MAX_LIVE_TICKS) {
    const excess = ticks.length - MAX_LIVE_TICKS;
    ticks.splice(0, excess);
    tickNumbers.splice(0, excess);
  }

  let maxSystemDurationUs = trace.maxSystemDurationUs;
  for (const dur of tickData.systemDurations.values()) {
    if (dur > maxSystemDurationUs) {
      maxSystemDurationUs = dur;
    }
  }

  let p95TickDurationUs = trace.p95TickDurationUs;
  if (ticks.length % 100 === 0 && ticks.length > 0) {
    const sorted = ticks.map(t => t.durationUs).sort((a, b) => a - b);
    const idx = Math.floor(sorted.length * 0.95);
    p95TickDurationUs = sorted[Math.min(idx, sorted.length - 1)];
  }

  return {
    ...trace,
    ticks,
    tickNumbers,
    globalStartUs: ticks[0]?.startUs ?? 0,
    globalEndUs: tickData.endUs,
    maxTickDurationUs: Math.max(trace.maxTickDurationUs, tickData.durationUs),
    maxSystemDurationUs,
    p95TickDurationUs,
  };
}

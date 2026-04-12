import { TraceEventType, type TraceEvent, type SystemDef, type TraceMetadata } from './types';

/** A chunk span: one rectangle in the Gantt chart */
export interface ChunkSpan {
  systemIndex: number;
  systemName: string;
  chunkIndex: number;
  workerId: number;
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

/** An OTel Activity span captured during tick execution */
export interface SpanData {
  nameId: number;
  name: string;
  workerId: number;
  startUs: number;
  endUs: number;
  durationUs: number;
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
  /** Per-system aggregate duration in µs (for heat map) */
  systemDurations: Map<number, number>;
}

/** Processed trace ready for rendering */
export interface ProcessedTrace {
  metadata: TraceMetadata;
  ticks: TickData[];
  /** Interned span name lookup (ID → name) */
  spanNames: Record<number, string>;
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

/** Process raw events into renderable tick structures */
export function processTrace(metadata: TraceMetadata, events: TraceEvent[], spanNames: Record<number, string> = {}): ProcessedTrace {
  const systems = metadata.systems;
  const systemColors = generateSystemColors(systems.length);

  // Group events by tick
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
    const tickData = processTickEvents(tickNumber, tickEvents, systems, spanNames);
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

  // Sort ticks by tick number
  ticks.sort((a, b) => a.tickNumber - b.tickNumber);
  const tickNumbers = ticks.map(t => t.tickNumber);

  const globalStartUs = ticks.length > 0 ? ticks[0].startUs : 0;
  const globalEndUs = ticks.length > 0 ? ticks[ticks.length - 1].endUs : 0;

  // Compute P95 tick duration for timeline bar height scaling
  const sortedDurations = ticks.map(t => t.durationUs).sort((a, b) => a - b);
  const p95Idx = Math.floor(sortedDurations.length * 0.95);
  const p95TickDurationUs = sortedDurations.length > 0 ? sortedDurations[Math.min(p95Idx, sortedDurations.length - 1)] : 0;

  return {
    metadata,
    ticks,
    spanNames,
    tickNumbers,
    globalStartUs,
    globalEndUs,
    maxSystemDurationUs,
    maxTickDurationUs,
    p95TickDurationUs,
    systemColors
  };
}

/** Find all ticks that overlap with the given absolute time range */
export function getTicksInRange(trace: ProcessedTrace, startUs: number, endUs: number): TickData[] {
  return trace.ticks.filter(t => t.endUs >= startUs && t.startUs <= endUs);
}

/** Find the tick index (in trace.ticks array) for a given tick number */
export function findTickIndex(trace: ProcessedTrace, tickNumber: number): number {
  return trace.ticks.findIndex(t => t.tickNumber === tickNumber);
}

export function processTickEvents(tickNumber: number, events: TraceEvent[], systems: SystemDef[], spanNames: Record<number, string>): TickData {
  let startUs = Infinity;
  let endUs = -Infinity;

  const chunks: ChunkSpan[] = [];
  const phases: PhaseSpan[] = [];
  const skips: SkipEvent[] = [];
  const spans: SpanData[] = [];
  const systemDurations = new Map<number, number>();

  // Track open chunk/phase/span events
  const openChunks = new Map<string, TraceEvent>(); // key: sysIdx-chunkIdx-workerId
  const openPhases = new Map<number, TraceEvent>();  // key: phase enum
  const openSpans = new Map<string, TraceEvent>();   // key: nameId-workerId

  for (const evt of events) {
    switch (evt.eventType) {
      case TraceEventType.TickStart:
        startUs = evt.timestampUs;
        break;

      case TraceEventType.TickEnd:
        endUs = evt.timestampUs;
        break;

      case TraceEventType.PhaseStart:
        openPhases.set(evt.phase, evt);
        break;

      case TraceEventType.PhaseEnd: {
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

      case TraceEventType.ChunkStart: {
        const key = `${evt.systemIndex}-${evt.chunkIndex}-${evt.workerId}`;
        openChunks.set(key, evt);
        break;
      }

      case TraceEventType.ChunkEnd: {
        const key = `${evt.systemIndex}-${evt.chunkIndex}-${evt.workerId}`;
        const open = openChunks.get(key);
        if (open) {
          const dur = evt.timestampUs - open.timestampUs;
          const sys = systems[evt.systemIndex];
          chunks.push({
            systemIndex: evt.systemIndex,
            systemName: sys?.name ?? `System[${evt.systemIndex}]`,
            chunkIndex: evt.chunkIndex,
            workerId: evt.workerId,
            startUs: open.timestampUs,
            endUs: evt.timestampUs,
            durationUs: dur,
            entitiesProcessed: evt.entitiesProcessed,
            totalChunks: open.payload,
            isParallel: sys?.isParallel ?? false
          });

          // Accumulate per-system duration (max across workers for parallel systems)
          const existing = systemDurations.get(evt.systemIndex) ?? 0;
          systemDurations.set(evt.systemIndex, existing + dur);

          openChunks.delete(key);
        }
        break;
      }

      case TraceEventType.SystemSkipped:
        skips.push({
          systemIndex: evt.systemIndex,
          systemName: systems[evt.systemIndex]?.name ?? `System[${evt.systemIndex}]`,
          reason: evt.skipReason,
          reasonName: SKIP_REASON_NAMES[evt.skipReason] ?? `Unknown(${evt.skipReason})`,
          timestampUs: evt.timestampUs
        });
        break;

      case TraceEventType.SpanStart: {
        const spanKey = `${evt.payload}-${evt.workerId}`;
        openSpans.set(spanKey, evt);
        break;
      }

      case TraceEventType.SpanEnd: {
        const spanKey = `${evt.payload}-${evt.workerId}`;
        const open = openSpans.get(spanKey);
        if (open) {
          const dur = evt.timestampUs - open.timestampUs;
          spans.push({
            nameId: evt.payload,
            name: spanNames[evt.payload] ?? `Span[${evt.payload}]`,
            workerId: evt.workerId,
            startUs: open.timestampUs,
            endUs: evt.timestampUs,
            durationUs: dur
          });
          openSpans.delete(spanKey);
        }
        break;
      }
    }
  }

  if (startUs === Infinity) startUs = 0;
  if (endUs === -Infinity) endUs = startUs;

  return {
    tickNumber,
    startUs,
    endUs,
    durationUs: endUs - startUs,
    chunks,
    phases,
    skips,
    spans,
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
    spanNames: {},
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
  events: TraceEvent[],
  newSpanNames?: Record<number, string>
): ProcessedTrace {
  const spanNames = newSpanNames
    ? { ...trace.spanNames, ...newSpanNames }
    : trace.spanNames;

  const tickData = processTickEvents(tickNumber, events, trace.metadata.systems, spanNames);

  const ticks = [...trace.ticks, tickData];
  const tickNumbers = [...trace.tickNumbers, tickNumber];

  // Evict oldest ticks beyond the cap
  if (ticks.length > MAX_LIVE_TICKS) {
    const excess = ticks.length - MAX_LIVE_TICKS;
    ticks.splice(0, excess);
    tickNumbers.splice(0, excess);
  }

  // Update aggregate stats incrementally
  let maxSystemDurationUs = trace.maxSystemDurationUs;
  for (const dur of tickData.systemDurations.values()) {
    if (dur > maxSystemDurationUs) {
      maxSystemDurationUs = dur;
    }
  }

  // Recompute P95 every 100 ticks (cheap: sort durations array)
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
    spanNames,
    globalStartUs: ticks[0]?.startUs ?? 0,
    globalEndUs: tickData.endUs,
    maxTickDurationUs: Math.max(trace.maxTickDurationUs, tickData.durationUs),
    maxSystemDurationUs,
    p95TickDurationUs,
  };
}

/** Merge span names into an existing trace without adding ticks. */
export function mergeSpanNames(
  trace: ProcessedTrace,
  newSpanNames: Record<number, string>
): ProcessedTrace {
  return {
    ...trace,
    spanNames: { ...trace.spanNames, ...newSpanNames }
  };
}

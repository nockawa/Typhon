/** Matches TraceEventType enum from Typhon.Profiler */
export const enum TraceEventType {
  TickStart = 0,
  TickEnd = 1,
  PhaseStart = 2,
  PhaseEnd = 3,
  SystemReady = 4,
  ChunkStart = 5,
  ChunkEnd = 6,
  SystemSkipped = 7,
  SpanStart = 8,
  SpanEnd = 9
}

/** Matches TickPhase enum */
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

export interface TraceMetadata {
  header: {
    version: number;
    timestampFrequency: number;
    baseTickRate: number;
    workerCount: number;
    systemCount: number;
    createdUtc: string;
    samplingSessionStartQpc: number;
  };
  systems: SystemDef[];
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

export interface TraceEvent {
  timestampUs: number;
  tickNumber: number;
  systemIndex: number;
  chunkIndex: number;
  workerId: number;
  eventType: TraceEventType;
  phase: TickPhase;
  skipReason: number;
  entitiesProcessed: number;
  payload: number;
}

/** A tick source abstracts over file-based and live-streamed data */
export interface TickSource {
  readonly metadata: TraceMetadata;
  getEvents(fromTick: number, toTick: number): Promise<TraceEvent[]>;
}

import type { TraceMetadata, TraceEvent, TickSource } from './types';

/**
 * Live streaming source. Implements the {@link TickSource} interface so both file-based and
 * live data sources share the same shape for consumers that don't care which one is active.
 *
 * Unlike {@link FileTickSource}, the live source buffers ticks as they arrive and <code>getEvents</code>
 * returns only the ticks currently in that in-memory buffer. Older ticks may have been evicted.
 */
export class LiveTickSource implements TickSource {
  public metadata: TraceMetadata;
  private readonly _buffer = new Map<number, TraceEvent[]>();

  constructor(metadata: TraceMetadata) {
    this.metadata = metadata;
  }

  /** Add a tick's events to the in-memory buffer. */
  pushTick(tickNumber: number, events: TraceEvent[]): void {
    this._buffer.set(tickNumber, events);
  }

  /** Evict ticks older than the given threshold (called by App.tsx after processing). */
  evictBefore(tickNumber: number): void {
    for (const key of this._buffer.keys()) {
      if (key < tickNumber) {
        this._buffer.delete(key);
      }
    }
  }

  async getEvents(fromTick: number, toTick: number): Promise<TraceEvent[]> {
    const result: TraceEvent[] = [];
    for (const [tick, events] of this._buffer) {
      if (tick >= fromTick && tick <= toTick) {
        result.push(...events);
      }
    }
    return result;
  }
}

/** Callbacks for live streaming events from the SSE connection. */
export interface LiveCallbacks {
  /** Called when the server sends session metadata (header + systems + span names). */
  onMetadata: (metadata: TraceMetadata, spanNames: Record<number, string>) => void;
  /** Called for each tick batch received. */
  onTick: (tickNumber: number, events: TraceEvent[], newSpanNames?: Record<number, string>) => void;
  /** Called when the SSE connection is lost. */
  onDisconnect: () => void;
  /** Called on connection errors. */
  onError: (error: string) => void;
}

/** Tick batch received from the SSE stream. */
interface LiveTickBatch {
  tickNumber: number;
  events: TraceEvent[];
  newSpanNames?: Record<number, string>;
}

/** Metadata event from the SSE stream. */
interface LiveMetadataEvent {
  header: TraceMetadata['header'];
  systems: TraceMetadata['systems'];
  spanNames?: Record<number, string>;
}

/**
 * Connect to the live SSE endpoint and receive real-time tick data.
 * Returns the EventSource instance for lifecycle management (close to disconnect).
 */
export function connectLive(callbacks: LiveCallbacks): EventSource {
  const es = new EventSource('/api/live/events');

  es.addEventListener('metadata', (e: MessageEvent) => {
    try {
      const data = JSON.parse(e.data) as LiveMetadataEvent;
      const metadata: TraceMetadata = {
        header: data.header,
        systems: data.systems,
      };
      callbacks.onMetadata(metadata, data.spanNames ?? {});
    } catch (err) {
      callbacks.onError(`Failed to parse metadata: ${err}`);
    }
  });

  es.addEventListener('tick', (e: MessageEvent) => {
    try {
      const batch = JSON.parse(e.data) as LiveTickBatch;
      // Span names (if any) are attached to the first batch of each engine flush, so they
      // arrive with — or before — the events that reference them. No sentinel needed.
      callbacks.onTick(batch.tickNumber, batch.events, batch.newSpanNames ?? undefined);
    } catch (err) {
      callbacks.onError(`Failed to parse tick data: ${err}`);
    }
  });

  es.addEventListener('heartbeat', () => {
    // Connection alive — no action needed
  });

  es.onerror = () => {
    if (es.readyState === EventSource.CLOSED) {
      callbacks.onDisconnect();
    } else {
      callbacks.onError('SSE connection error (will retry automatically)');
    }
  };

  return es;
}

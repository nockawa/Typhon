import type { TraceMetadata, TraceEvent, TickSource } from './types';

/**
 * Live streaming source. Implements the {@link TickSource} interface so both file-based and
 * live data sources share the same shape for consumers that don't care which one is active.
 *
 * Unlike {@link FileTickSource}, the live source buffers ticks as they arrive and `getEvents`
 * returns only the ticks currently in that in-memory buffer. Older ticks may have been evicted.
 */
export class LiveTickSource implements TickSource {
  public metadata: TraceMetadata;
  private readonly _buffer = new Map<number, TraceEvent[]>();

  constructor(metadata: TraceMetadata) {
    this.metadata = metadata;
  }

  /** Add a tick's records to the in-memory buffer. */
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
  /** Called when the server sends session metadata (header + systems + archetypes + componentTypes). */
  onMetadata: (metadata: TraceMetadata) => void;
  /** Called for each tick batch received. */
  onTick: (tickNumber: number, events: TraceEvent[]) => void;
  /** Called when the SSE connection is lost. */
  onDisconnect: () => void;
  /** Called on connection errors. */
  onError: (error: string) => void;
}

/** Tick batch received from the SSE stream. */
interface LiveTickBatch {
  tickNumber: number;
  events: TraceEvent[];
}

/**
 * Connect to the live SSE endpoint and receive real-time tick data.
 * Returns the EventSource instance for lifecycle management (close to disconnect).
 */
export function connectLive(callbacks: LiveCallbacks): EventSource {
  const es = new EventSource('/api/live/events');

  es.addEventListener('metadata', (e: MessageEvent) => {
    try {
      const metadata = JSON.parse(e.data) as TraceMetadata;
      callbacks.onMetadata(metadata);
    } catch (err) {
      callbacks.onError(`Failed to parse metadata: ${err}`);
    }
  });

  es.addEventListener('tick', (e: MessageEvent) => {
    try {
      const batch = JSON.parse(e.data) as LiveTickBatch;
      callbacks.onTick(batch.tickNumber, batch.events);
    } catch (err) {
      callbacks.onError(`Failed to parse tick data: ${err}`);
    }
  });

  es.addEventListener('heartbeat', () => {
    // Connection alive — no action needed
  });

  es.onerror = () => {
    // EventSource has three readyStates after an error: CONNECTING (it's auto-retrying internally), CLOSED (permanent failure — server
    // returned non-2xx or the tab was unloaded), and rarely OPEN (transient wobble). We let CONNECTING retries proceed — that's the
    // browser reconnecting on its own every ~3 s, which is exactly what we want when the Typhon server restarts. We only explicitly
    // close + signal disconnect when readyState is CLOSED (EventSource won't retry from that state). The onError callback still fires
    // so the UI can show a transient "reconnecting..." indicator without having to poll readyState.
    if (es.readyState === EventSource.CLOSED) {
      callbacks.onDisconnect();
    } else {
      // CONNECTING (auto-retry in progress) or OPEN (rare). Surface the error for UX feedback but let EventSource keep retrying.
      callbacks.onError('SSE connection interrupted — retrying...');
    }
  };

  return es;
}

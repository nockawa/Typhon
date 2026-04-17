/**
 * Main-thread wrapper around the chunk-processing Web Worker. Creates the worker lazily on first use (so a viewer session that never loads
 * any chunks — e.g., live mode — doesn't pay the startup cost), manages in-flight requests via a monotonic ID, and exposes a simple
 * Promise-returning API.
 *
 * A single worker instance is shared across all chunk processing. Serial in the worker — one tick-data build at a time — but the main
 * thread can queue new requests while one is in flight, so pipeline concurrency naturally amortizes worker-start latency across a burst
 * of viewport-triggered loads.
 *
 * Fallback: if the browser can't create a module worker (SSR / ancient browsers), `processEventsInWorker` falls back to running the same
 * pipeline synchronously on the main thread. Feature detection guards the Worker construction.
 */
import type { TickData } from './traceModel';
import { processTickEvents } from './traceModel';
import type { TraceEvent, SystemDef } from './types';
import { decompressLz4Block } from './lz4Block';
import { decodeChunkBinary } from './chunkDecoder';

interface WorkerMessage {
  type: 'processed' | 'error';
  requestId: number;
  tickData?: TickData[];
  error?: string;
}

interface PendingEntry {
  resolve: (tickData: TickData[]) => void;
  reject: (err: Error) => void;
}

let worker: Worker | null = null;
let workerUnavailable = false;
let requestCounter = 0;
const pending = new Map<number, PendingEntry>();

function ensureWorker(): Worker | null {
  if (worker) return worker;
  if (workerUnavailable) return null;

  try {
    // Vite resolves this URL at build time and splits the worker into its own chunk. The `{ type: 'module' }` option is required because our
    // worker source uses ES module imports (traceModel, types).
    worker = new Worker(new URL('./chunkWorker.ts', import.meta.url), { type: 'module' });
  } catch (err) {
    // Module workers may not be available (very old browsers, or Jest/SSR contexts). Fall back to synchronous main-thread processing so the
    // viewer still works — just without the off-thread benefit.
    console.warn('chunkWorker unavailable, falling back to main-thread processing:', err);
    workerUnavailable = true;
    return null;
  }

  worker.onmessage = (e: MessageEvent<WorkerMessage>) => {
    const msg = e.data;
    const entry = pending.get(msg.requestId);
    if (!entry) return;
    pending.delete(msg.requestId);
    if (msg.type === 'processed' && msg.tickData) {
      entry.resolve(msg.tickData);
    } else {
      entry.reject(new Error(msg.error ?? 'Unknown worker error'));
    }
  };

  worker.onerror = (e) => {
    // Catastrophic worker failure — reject any in-flight requests so callers don't hang forever, then nuke the module-scope reference so the
    // NEXT call to ensureWorker() creates a fresh worker. Without the null, subsequent postMessage calls land on a dead worker that will
    // never reply, leaving their pending Promises pending forever. Snapshot the entries before iterating so mutation during .delete() doesn't
    // skip items.
    console.error('chunkWorker error:', e);
    const snapshot = Array.from(pending.entries());
    pending.clear();
    for (const [, entry] of snapshot) {
      entry.reject(new Error(`Worker error: ${e.message}`));
    }
    worker = null;
  };

  return worker;
}

/**
 * Build TickData[] from a chunk's events. Dispatches to the Web Worker when available, falls back to synchronous main-thread processing
 * otherwise. Callers await a Promise — the fallback path still returns a resolved Promise so the call shape is identical.
 */
export function processEventsInWorker(events: TraceEvent[], systems: SystemDef[]): Promise<TickData[]> {
  const w = ensureWorker();
  if (!w) {
    return Promise.resolve(buildTickDataSync(events, systems));
  }

  const requestId = ++requestCounter;
  return new Promise<TickData[]>((resolve, reject) => {
    pending.set(requestId, { resolve, reject });
    w.postMessage({ type: 'process', requestId, events, systems });
  });
}

/**
 * Binary variant of <see cref="processEventsInWorker"/>. Takes the raw LZ4-compressed chunk bytes + decode parameters and runs the full
 * decompress → binary decode → processTickEvents pipeline inside the Worker. The <paramref name="compressed"/> ArrayBuffer is transferred
 * (zero-copy) via the postMessage transfer list, which means the caller's view of it becomes detached — caller MUST not read from it after
 * this call.
 */
export function processBinaryInWorker(
  compressed: ArrayBuffer,
  uncompressedBytes: number,
  fromTick: number,
  ticksPerUs: number,
  systems: SystemDef[],
): Promise<TickData[]> {
  const w = ensureWorker();
  if (!w) {
    // Synchronous fallback when module workers are unavailable. Runs the same pipeline inline — no transfer semantics needed because the
    // main thread already owns the ArrayBuffer.
    const raw = decompressLz4Block(new Uint8Array(compressed), uncompressedBytes);
    const events = decodeChunkBinary(raw, fromTick, ticksPerUs);
    return Promise.resolve(buildTickDataSync(events, systems));
  }

  const requestId = ++requestCounter;
  return new Promise<TickData[]>((resolve, reject) => {
    pending.set(requestId, { resolve, reject });
    // Third arg is the transfer list — moves ownership of the ArrayBuffer into the worker without copying. After this call, `compressed` is
    // a detached buffer in the caller's frame (reads will throw). chunkCache.ts discards its reference immediately after posting.
    w.postMessage({
      type: 'processBinary',
      requestId,
      compressed,
      uncompressedBytes,
      fromTick,
      ticksPerUs,
      systems,
    }, [compressed]);
  });
}

function buildTickDataSync(events: TraceEvent[], systems: SystemDef[]): TickData[] {
  if (events.length === 0) return [];
  const byTick = new Map<number, TraceEvent[]>();
  for (const evt of events) {
    let bucket = byTick.get(evt.tickNumber);
    if (!bucket) {
      bucket = [];
      byTick.set(evt.tickNumber, bucket);
    }
    bucket.push(evt);
  }
  const result: TickData[] = [];
  for (const [tickNumber, bucket] of byTick) {
    result.push(processTickEvents(tickNumber, bucket, systems));
  }
  result.sort((a, b) => a.tickNumber - b.tickNumber);
  return result;
}

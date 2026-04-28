/**
 * Main-thread wrapper around a POOL of chunk-processing Web Workers. Creates workers lazily on first use (so a viewer session that never
 * loads any chunks — e.g., live mode — doesn't pay the startup cost), manages in-flight requests via a monotonic ID, and exposes a simple
 * Promise-returning API.
 *
 * Pool design: each worker processes one request at a time (serial in-worker), but the pool has N workers so N decodes can run in parallel.
 * New requests are dispatched to the least-busy worker — an idle one whenever possible, else the one with the fewest queued requests.
 * This means a pathologically slow decode (e.g., a single tick with millions of events) occupies one worker while the others stay
 * responsive to subsequent viewport changes. Before the pool refactor, that same slow decode blocked EVERY subsequent chunk behind it.
 *
 * Pool size is sized to `navigator.hardwareConcurrency` with a cap: too few defeats the purpose on multicore desktops, too many wastes
 * memory (each worker holds its own module copies + scratch buffers) and actually degrades perf once thread contention beats parallelism.
 * The cap of 4 is empirically good for typical profiler workloads where decode is the hot stage.
 *
 * Per-worker failure isolation: each worker has its own onerror handler that only rejects requests dispatched to THAT worker — an
 * unrelated worker failure doesn't poison the rest of the pool. The failed worker is nulled; the next request routed to its slot
 * lazily respawns it.
 *
 * Fallback: if the browser can't create any module worker (SSR / ancient browsers), every processX call runs synchronously on the main
 * thread. Feature detection happens on first-worker-attempt; subsequent attempts short-circuit via `workerUnavailable`.
 */
import type { TickData } from '../model/traceModel';
import type { TraceEvent, SystemDef } from '../model/types';
import { decompressLz4Block } from './lz4Block';
import { decodeChunkBinary } from './chunkDecoder';
import { buildTickDataFromEvents } from './tickBuilder';

interface WorkerMessage {
  type: 'processed' | 'error';
  requestId: number;
  tickData?: TickData[];
  error?: string;
}

interface PendingEntry {
  resolve: (tickData: TickData[]) => void;
  reject: (err: Error) => void;
  /** Which pool slot this request was dispatched to — used by per-worker onerror to reject only the requests it owned. */
  slotIdx: number;
}

interface WorkerSlot {
  worker: Worker | null;
  /** How many in-flight requests this slot currently owns. Used by {@link pickSlot} to route new work to the least-busy slot. */
  inFlightCount: number;
}

/**
 * Pool size. Clamped to [1, 4]. Browsers expose hardware concurrency as logical-core count; decode is CPU-bound, but each worker also
 * runs its own JS VM + module instances, so oversubscribing trades wall-clock for memory pressure and diminishing returns from thread
 * contention. 4 is the sweet spot on 8+ core desktops; 1-2 is right on low-power laptops. The clamp avoids both pathological extremes.
 */
const POOL_SIZE = Math.max(1, Math.min(4, (typeof navigator !== 'undefined' && navigator.hardwareConcurrency) || 2));

const pool: WorkerSlot[] = [];
let workerUnavailable = false;
let requestCounter = 0;
const pending = new Map<number, PendingEntry>();

function ensureSlot(idx: number): Worker | null {
  if (workerUnavailable) return null;

  let slot = pool[idx];
  if (!slot) {
    slot = { worker: null, inFlightCount: 0 };
    pool[idx] = slot;
  }
  if (slot.worker) return slot.worker;

  try {
    // Vite resolves this URL at build time and splits the worker into its own chunk. The `{ type: 'module' }` option is required because our
    // worker source uses ES module imports (traceModel, types).
    slot.worker = new Worker(new URL('./chunkWorker.ts', import.meta.url), { type: 'module' });
  } catch (err) {
    // Module workers may not be available (very old browsers, or Jest/SSR contexts). Fall back to synchronous main-thread processing so the
    // viewer still works — just without the off-thread benefit. Marked at the pool level so neither this slot nor any other retries.
    console.warn('chunkWorker unavailable, falling back to main-thread processing:', err);
    workerUnavailable = true;
    slot.worker = null;
    return null;
  }

  const w = slot.worker;
  w.onmessage = (e: MessageEvent<WorkerMessage>) => {
    const msg = e.data;
    const entry = pending.get(msg.requestId);
    if (!entry) return;
    pending.delete(msg.requestId);
    slot.inFlightCount = Math.max(0, slot.inFlightCount - 1);
    if (msg.type === 'processed' && msg.tickData) {
      entry.resolve(msg.tickData);
    } else {
      entry.reject(new Error(msg.error ?? 'Unknown worker error'));
    }
  };

  w.onerror = (e) => {
    // Catastrophic failure of THIS slot's worker. Reject only the pending requests this slot owned — the other pool members are still
    // fine and should keep running. Null the worker so the next request routed here triggers a fresh spawn via ensureSlot. Without the
    // null, subsequent postMessage calls would land on a dead worker that never replies, leaving promises pending forever.
    console.error(`chunkWorker slot ${idx} error:`, e);
    const toReject: [number, PendingEntry][] = [];
    for (const [id, entry] of pending) {
      if (entry.slotIdx === idx) toReject.push([id, entry]);
    }
    for (const [id, entry] of toReject) {
      pending.delete(id);
      entry.reject(new Error(`Worker error: ${e.message}`));
    }
    slot.worker = null;
    slot.inFlightCount = 0;
  };

  return w;
}

/**
 * Choose the best pool slot for a new request. Prefers an idle slot (inFlightCount === 0) over a busy one; among busy slots picks the
 * least-loaded. Starts from slot 0 so early requests stay on a single worker before spreading — keeps single-chunk workloads from paying
 * the cost of spawning all N workers.
 *
 * Also lazily spawns slots: if we hit an idle-but-unspawned slot during the scan we return its index so ensureSlot creates the worker
 * on demand. This means the Nth worker only comes into existence when we genuinely have N concurrent requests.
 */
function pickSlot(): number {
  let bestIdx = 0;
  let bestCount = Infinity;
  for (let i = 0; i < POOL_SIZE; i++) {
    const slot = pool[i];
    if (!slot || slot.worker === null) {
      // Unspawned slot — effectively idle. Prefer it over any busy spawned slot.
      if (0 < bestCount) {
        bestCount = 0;
        bestIdx = i;
      }
      continue;
    }
    if (slot.inFlightCount < bestCount) {
      bestCount = slot.inFlightCount;
      bestIdx = i;
      if (bestCount === 0) break;   // idle spawned slot — can't do better, stop scanning.
    }
  }
  return bestIdx;
}

/**
 * Build TickData[] from a chunk's events. Dispatches to the least-busy pool worker when available, falls back to synchronous main-thread
 * processing otherwise. Callers await a Promise — the fallback path still returns a resolved Promise so the call shape is identical.
 */
export function processEventsInWorker(events: TraceEvent[], systems: SystemDef[]): Promise<TickData[]> {
  const slotIdx = pickSlot();
  const w = ensureSlot(slotIdx);
  if (!w) {
    return Promise.resolve(buildTickDataFromEvents(events, systems));
  }

  const slot = pool[slotIdx];
  slot.inFlightCount++;
  const requestId = ++requestCounter;
  return new Promise<TickData[]>((resolve, reject) => {
    pending.set(requestId, { resolve, reject, slotIdx });
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
  isContinuation: boolean,
): Promise<TickData[]> {
  const slotIdx = pickSlot();
  const w = ensureSlot(slotIdx);
  if (!w) {
    // Synchronous fallback when module workers are unavailable. Runs the same pipeline inline — no transfer semantics needed because the
    // main thread already owns the ArrayBuffer.
    const raw = decompressLz4Block(new Uint8Array(compressed), uncompressedBytes);
    const events = decodeChunkBinary(raw, fromTick, ticksPerUs, isContinuation);
    return Promise.resolve(buildTickDataFromEvents(events, systems));
  }

  const slot = pool[slotIdx];
  slot.inFlightCount++;
  const requestId = ++requestCounter;
  return new Promise<TickData[]>((resolve, reject) => {
    pending.set(requestId, { resolve, reject, slotIdx });
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
      isContinuation,
    }, [compressed]);
  });
}


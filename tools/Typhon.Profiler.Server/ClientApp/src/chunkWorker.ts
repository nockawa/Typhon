/// <reference lib="webworker" />
/**
 * Web Worker for chunk processing. Two request shapes supported:
 *
 *   `process` — legacy JSON path. Receives pre-parsed TraceEvent[] from the main thread (which already ran JSON.parse on the server's
 *   /api/trace/chunk response) and runs the same processTickEvents pipeline. Kept for fallback when binary path fails.
 *
 *   `processBinary` — new binary path. Receives the raw LZ4-compressed chunk bytes + decode parameters. The worker itself decompresses the
 *   LZ4 block, walks the records via the TypeScript decoder, then runs processTickEvents. This keeps ALL the CPU-heavy work — decompression,
 *   binary walk, tick grouping, depth walks — off the main thread. The compressed ArrayBuffer is a Transferable, so it moves to the worker
 *   without a memcopy.
 *
 * Why this lives off the main thread: processTickEvents alone is 5-20 ms per dense tick; add decompression + binary decode on top and you've
 * got 30-80 ms of pure CPU per chunk. Without the worker those milliseconds hit canvas redraws + input dispatch, visible as jank during pan.
 */
import { processTickEvents, type TickData } from './traceModel';
import type { TraceEvent, SystemDef } from './types';
import { decompressLz4Block } from './lz4Block';
import { decodeChunkBinary } from './chunkDecoder';

interface ProcessJsonRequest {
  type: 'process';
  requestId: number;
  events: TraceEvent[];
  systems: SystemDef[];
}

interface ProcessBinaryRequest {
  type: 'processBinary';
  requestId: number;
  /** Raw LZ4-compressed chunk bytes, transferred from main thread (zero-copy). */
  compressed: ArrayBuffer;
  uncompressedBytes: number;
  fromTick: number;
  ticksPerUs: number;
  systems: SystemDef[];
}

type WorkerRequest = ProcessJsonRequest | ProcessBinaryRequest;

interface ProcessedResponse {
  type: 'processed';
  requestId: number;
  tickData: TickData[];
}

interface ErrorResponse {
  type: 'error';
  requestId: number;
  error: string;
}

type WorkerResponse = ProcessedResponse | ErrorResponse;

const ctx = self as unknown as {
  onmessage: ((e: MessageEvent<WorkerRequest>) => void) | null;
  postMessage: (msg: WorkerResponse) => void;
};

ctx.onmessage = (e: MessageEvent<WorkerRequest>) => {
  const msg = e.data;
  try {
    let tickData: TickData[];
    if (msg.type === 'process') {
      tickData = buildTickDataFromEvents(msg.events, msg.systems);
    } else {
      // Binary path: decompress → decode → group + derive per-tick shapes. ArrayBuffer was transferred into the worker and is now owned here,
      // so the Uint8Array view is safe to use without copying.
      const compressed = new Uint8Array(msg.compressed);
      const raw = decompressLz4Block(compressed, msg.uncompressedBytes);
      const events = decodeChunkBinary(raw, msg.fromTick, msg.ticksPerUs);
      tickData = buildTickDataFromEvents(events, msg.systems);
    }
    ctx.postMessage({ type: 'processed', requestId: msg.requestId, tickData });
  } catch (err) {
    const errMsg = err instanceof Error ? err.message : String(err);
    ctx.postMessage({ type: 'error', requestId: msg.requestId, error: errMsg });
  }
};

function buildTickDataFromEvents(events: TraceEvent[], systems: SystemDef[]): TickData[] {
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

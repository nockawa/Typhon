import type { TraceMetadata, TraceEvent, OpenTraceResponse, ChunkResponse } from './types';

const BASE = '/api';

/** Fetch trace file metadata (header + system / archetype / component-type tables) */
export async function fetchMetadata(path: string): Promise<TraceMetadata> {
  const res = await fetch(`${BASE}/trace/metadata?path=${encodeURIComponent(path)}`);
  if (!res.ok) throw new Error(`Failed to load metadata: ${res.statusText}`);
  return res.json();
}

/**
 * Open a trace file for viewing. Triggers the sidecar-cache build on first call for a given path, hits the cache on subsequent calls. Returns
 * metadata + per-tick summaries + global metrics in a single response — enough to render the timeline overview without any detail load.
 */
export async function openTrace(path: string): Promise<OpenTraceResponse> {
  const res = await fetch(`${BASE}/trace/open?path=${encodeURIComponent(path)}`);
  if (!res.ok) throw new Error(`Failed to open trace: ${res.statusText}`);
  return res.json();
}

/** One progress frame from /api/trace/build-progress. Mirrors the server's BuildProgress record struct. */
export interface BuildProgress {
  bytesRead: number;
  totalBytes: number;
  tickCount: number;
  eventCount: number;
}

/**
 * Subscribe to the progressive cache-build feed for <paramref name="path"/>. Resolves when the build is complete (or the cache was already
 * fresh — in that case <paramref name="onProgress"/> is never called and the promise resolves immediately on the `done` event). Rejects on
 * an `error` event or a transport-level EventSource failure. <paramref name="onProgress"/> runs synchronously in the SSE delivery loop —
 * keep it fast (it just updates React state in practice).
 *
 * The caller is responsible for calling <c>openTrace</c> AFTER this promise resolves — this function only pumps the build feed; it doesn't
 * return the trace metadata. The separation keeps /api/trace/open's contract unchanged and lets callers opt into progress UI independently.
 */
export function subscribeBuildProgress(path: string, onProgress: (p: BuildProgress) => void): Promise<void> {
  return new Promise((resolve, reject) => {
    const es = new EventSource(`${BASE}/trace/build-progress?path=${encodeURIComponent(path)}`);
    es.addEventListener('progress', (e) => {
      try {
        const data = JSON.parse((e as MessageEvent).data) as BuildProgress;
        onProgress(data);
      } catch {
        // Malformed frame — ignore, don't fail the whole build. Next frame will land normally.
      }
    });
    es.addEventListener('done', () => {
      es.close();
      resolve();
    });
    es.addEventListener('error', (e) => {
      // SSE fires a generic 'error' event both for server-sent `event: error` frames AND for transport-level failures. Try to extract a
      // server-side message; fall back to a generic transport error.
      const data = (e as MessageEvent).data;
      es.close();
      if (typeof data === 'string' && data.length > 0) {
        try {
          const parsed = JSON.parse(data) as { message?: string };
          reject(new Error(parsed.message ?? 'Build failed'));
          return;
        } catch { /* fall through */ }
      }
      reject(new Error('Build feed disconnected'));
    });
  });
}

/**
 * Fetch one chunk's events by manifest index. `chunkIdx` must match the position of the entry in
 * <see cref="OpenTraceResponse.chunkManifest"/>. Pass <paramref name="signal"/> to abort in-flight fetches on viewport change.
 */
export async function fetchChunk(
  path: string,
  chunkIdx: number,
  signal?: AbortSignal,
): Promise<ChunkResponse> {
  const params = new URLSearchParams({ path, chunkIdx: String(chunkIdx) });
  const res = await fetch(`${BASE}/trace/chunk?${params}`, { signal });
  if (!res.ok) throw new Error(`Failed to load chunk #${chunkIdx}: ${res.statusText}`);
  return res.json();
}

/** Binary-chunk response — raw LZ4 compressed payload + chunk metadata from response headers. */
export interface BinaryChunkResponse {
  fromTick: number;
  toTick: number;
  eventCount: number;
  uncompressedBytes: number;
  /**
   * True iff this chunk is a mid-tick continuation (intra-tick split, cache v8+). Parsed from the
   * `X-Chunk-Is-Continuation` response header. Consumers MUST honour this when seeding the tick counter in the decoder —
   * normal chunks seed at `fromTick - 1`; continuation chunks seed at `fromTick` directly.
   */
  isContinuation: boolean;
  /** Source trace's timestamp frequency (ticks per second). Clients divide timestamps by (freq / 1e6) to get microseconds. */
  timestampFrequency: number;
  /** Raw LZ4-block-compressed record bytes. Client must decompress via lz4Block.decompressLz4Block. */
  compressed: Uint8Array;
}

/**
 * Binary variant of <see cref="fetchChunk"/>. Fetches the raw LZ4 bytes from /api/trace/chunk-binary with metadata in response headers —
 * no JSON serialization involved. Caller is responsible for decompressing + decoding in a Web Worker via <c>chunkDecoder</c>.
 */
export async function fetchChunkBinary(
  path: string,
  chunkIdx: number,
  signal?: AbortSignal,
): Promise<BinaryChunkResponse> {
  const params = new URLSearchParams({ path, chunkIdx: String(chunkIdx) });
  const res = await fetch(`${BASE}/trace/chunk-binary?${params}`, { signal });
  if (!res.ok) throw new Error(`Failed to load binary chunk #${chunkIdx}: ${res.statusText}`);

  // Parse integer metadata from response headers. All mandatory — missing any means the server and client are out of sync on the protocol
  // shape, which would silently corrupt decoding if we defaulted to 0. Throw loudly instead.
  const headerInt = (name: string): number => {
    const raw = res.headers.get(name);
    if (raw === null) throw new Error(`Binary chunk response missing required header: ${name}`);
    const n = Number(raw);
    if (!Number.isFinite(n)) throw new Error(`Binary chunk response header ${name} not a number: ${raw}`);
    return n;
  };

  const fromTick = headerInt('X-Chunk-From-Tick');
  const toTick = headerInt('X-Chunk-To-Tick');
  const eventCount = headerInt('X-Chunk-Event-Count');
  const uncompressedBytes = headerInt('X-Chunk-Uncompressed-Bytes');
  const timestampFrequency = headerInt('X-Timestamp-Frequency');
  // X-Chunk-Is-Continuation is a 0/1 flag. Parse strictly — treat any non-"1" value as false so a future extension that
  // sends multi-bit flag strings doesn't silently interpret them as "true."
  const isContinuation = res.headers.get('X-Chunk-Is-Continuation') === '1';
  const buffer = await res.arrayBuffer();

  return {
    fromTick,
    toTick,
    eventCount,
    uncompressedBytes,
    isContinuation,
    timestampFrequency,
    compressed: new Uint8Array(buffer),
  };
}

/** Upload result from POST /api/trace/upload */
export interface UploadResult {
  path: string;
  metadata: TraceMetadata;
}

/** Upload trace files to the server (trace + optional nettrace companion) */
export async function uploadTrace(files: File[]): Promise<UploadResult> {
  const formData = new FormData();
  for (const file of files) {
    if (file.name.endsWith('.typhon-trace')) {
      formData.append('trace', file);
    } else if (file.name.endsWith('.nettrace')) {
      formData.append('nettrace', file);
    }
  }

  const res = await fetch(`${BASE}/trace/upload`, {
    method: 'POST',
    body: formData,
  });
  if (!res.ok) throw new Error(`Upload failed: ${res.statusText}`);
  return res.json();
}

/** Flame graph node from the server */
export interface FlameNode {
  name: string;
  total: number;
  self: number;
  children: FlameNode[];
}

/** Fetch aggregated flame graph for a time range */
export async function fetchFlameGraph(
  path: string,
  fromUs: number,
  toUs: number,
  threadId?: number
): Promise<{ totalSamples: number; hasSamples: boolean; root: FlameNode }> {
  const params = new URLSearchParams({
    path,
    fromUs: String(fromUs),
    toUs: String(toUs),
  });
  if (threadId !== undefined && threadId >= 0) {
    params.set('threadId', String(threadId));
  }

  const res = await fetch(`${BASE}/trace/flamegraph?${params}`);
  if (!res.ok) throw new Error(`Failed to load flame graph: ${res.statusText}`);
  return res.json();
}


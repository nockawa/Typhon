import type { TraceMetadata, TraceEvent, TickSource } from './types';

const BASE = '/api';

/** Fetch trace file metadata (header + system definitions) */
export async function fetchMetadata(path: string): Promise<TraceMetadata> {
  const res = await fetch(`${BASE}/trace/metadata?path=${encodeURIComponent(path)}`);
  if (!res.ok) throw new Error(`Failed to load metadata: ${res.statusText}`);
  return res.json();
}

/** Events response from the server */
export interface EventsResponse {
  totalEvents: number;
  spanNames: Record<number, string>;
  events: TraceEvent[];
}

/** Fetch trace events, optionally filtered by tick range */
export async function fetchEvents(
  path: string,
  fromTick?: number,
  toTick?: number
): Promise<EventsResponse> {
  const params = new URLSearchParams({ path });
  if (fromTick !== undefined) params.set('fromTick', String(fromTick));
  if (toTick !== undefined) params.set('toTick', String(toTick));

  const res = await fetch(`${BASE}/trace/events?${params}`);
  if (!res.ok) throw new Error(`Failed to load events: ${res.statusText}`);
  return res.json();
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

/** File-based tick source — loads from a .typhon-trace file via the REST API */
export class FileTickSource implements TickSource {
  readonly metadata: TraceMetadata;
  private readonly _path: string;

  constructor(path: string, metadata: TraceMetadata) {
    this._path = path;
    this.metadata = metadata;
  }

  async getEvents(fromTick: number, toTick: number): Promise<TraceEvent[]> {
    const resp = await fetchEvents(this._path, fromTick, toTick);
    return resp.events;
  }

  static async open(path: string): Promise<FileTickSource> {
    const metadata = await fetchMetadata(path);
    return new FileTickSource(path, metadata);
  }
}

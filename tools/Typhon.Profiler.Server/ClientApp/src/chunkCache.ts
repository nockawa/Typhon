/**
 * Memory-bounded LRU cache over trace chunks. Keeps the client from ever materializing the whole trace — only the chunks overlapping the
 * current viewport (plus optional prefetch margin) stay resident. Off-range chunks are evicted when the total estimated byte size exceeds
 * the budget.
 *
 * Design constraints:
 *  - The cache has no opinion about rendering. It answers "for tick range [a, b), what ticks do we have loaded?" and returns them in order.
 *  - Chunk identity = index into the server-provided chunkManifest. That's stable across the life of an open trace session.
 *  - Loading is idempotent: repeated calls to ensureRange with the same viewport after a load completes do nothing (fast path).
 *  - Concurrent in-flight loads are deduped — calling ensureRange again while a chunk is mid-fetch doesn't kick off a duplicate request.
 *  - Eviction never drops a chunk that overlaps the current viewport (pin-on-visible). LRU only evicts off-screen chunks.
 */
import type { ChunkManifestEntry, TickSummary, TraceEvent, TraceMetadata } from './types';
import type { TickData } from './traceModel';
import { fetchChunk, fetchChunkBinary } from './api';
import { processEventsInWorker, processBinaryInWorker } from './chunkWorkerClient';

/** One loaded-and-processed chunk, resident in memory. */
export interface LoadedChunk {
  chunkIdx: number;
  fromTick: number;
  toTick: number;
  tickData: TickData[];
  /** Rough byte estimate for LRU math. Computed at load time from event count × avg-DTO-size heuristic. */
  byteSize: number;
  /** Monotonic access counter — bumped whenever the chunk is touched. Smallest value = least-recently-used. */
  lastAccessTick: number;
}

/** Internal state — don't mutate externally; call the exported helpers instead. */
export interface ChunkCacheState {
  entries: Map<number, LoadedChunk>;
  totalBytes: number;
  /** In-flight fetches keyed by chunkIdx so concurrent ensureRange calls dedup. */
  inFlight: Map<number, Promise<LoadedChunk>>;
  accessCounter: number;
  budgetBytes: number;
}

const DEFAULT_BUDGET = 200 * 1024 * 1024;     // 200 MB client-side cache
const AVG_BYTES_PER_EVENT = 200;              // heuristic — tightens LRU math when chunks vary in event density

/**
 * Transport selector for chunk loading. `true` uses the binary endpoint (/api/trace/chunk-binary + LZ4 + TS decoder); `false` falls back to
 * the legacy JSON endpoint. Flip to false if a decoder fidelity bug surfaces — the legacy path is preserved exactly, so switching is
 * zero-risk. Module-scope const rather than a runtime flag because we want it tree-shaken out of one code path in production builds.
 */
const USE_BINARY_CHUNK_TRANSPORT = true;

export function createChunkCache(budgetBytes: number = DEFAULT_BUDGET): ChunkCacheState {
  return {
    entries: new Map(),
    totalBytes: 0,
    inFlight: new Map(),
    accessCounter: 0,
    budgetBytes,
  };
}

/** How many adjacent chunks on each side of the visible range to speculatively load when stationary. Keeps panning smooth. */
export const DEFAULT_PREFETCH_CHUNKS = 2;

/**
 * Load every chunk overlapping [fromTick, toTick), plus adjacent chunks on each side of the visible range, evicting off-range chunks if
 * needed to stay under budget. Returns the loaded chunks covering the VISIBLE range (not the prefetch range), sorted by fromTick. Prefetched
 * chunks enter the cache but aren't included in the return value — they're opportunistic for the next viewport change, not for immediate
 * rendering.
 *
 * <paramref name="prefetchBefore"/> and <paramref name="prefetchAfter"/> are asymmetric so callers can bias prefetch toward the direction the
 * user is panning. Moving forward (wheeling through time) → larger `prefetchAfter`, smaller `prefetchBefore`. Moving backward → inverse. When
 * stationary, both default to DEFAULT_PREFETCH_CHUNKS.
 *
 * Idempotent — already-loaded chunks in the range are returned without refetching. Pass <paramref name="signal"/> to propagate cancellation
 * from the caller (viewport effect cleanup); aborted fetches leave no residue in the cache.
 */
export async function ensureRangeLoaded(
  cache: ChunkCacheState,
  path: string,
  metadata: TraceMetadata,
  manifest: ChunkManifestEntry[],
  fromTick: number,
  toTick: number,
  prefetchBefore: number = DEFAULT_PREFETCH_CHUNKS,
  prefetchAfter: number = DEFAULT_PREFETCH_CHUNKS,
  signal?: AbortSignal,
): Promise<LoadedChunk[]> {
  const visibleIndices = findChunksOverlapping(manifest, fromTick, toTick);
  if (visibleIndices.length === 0) return [];

  // Expand to include prefetch neighbours. Bounded at manifest edges so we don't fetch chunks that don't exist.
  const firstVisible = visibleIndices[0];
  const lastVisible = visibleIndices[visibleIndices.length - 1];
  const prefetchFrom = Math.max(0, firstVisible - Math.max(0, prefetchBefore));
  const prefetchTo = Math.min(manifest.length - 1, lastVisible + Math.max(0, prefetchAfter));
  const allIndices: number[] = [];
  for (let i = prefetchFrom; i <= prefetchTo; i++) allIndices.push(i);

  // Pin set (immune to LRU eviction) is only the VISIBLE range. Prefetched chunks are LRU-evictable if budget pressure hits.
  const pinnedIdxs = new Set(visibleIndices);
  const visibleLoadPromises: Promise<LoadedChunk>[] = [];

  for (const idx of allIndices) {
    const existing = cache.entries.get(idx);
    if (existing) {
      existing.lastAccessTick = ++cache.accessCounter;
      if (pinnedIdxs.has(idx)) visibleLoadPromises.push(Promise.resolve(existing));
      continue;
    }
    const inFlight = cache.inFlight.get(idx);
    if (inFlight) {
      if (pinnedIdxs.has(idx)) visibleLoadPromises.push(inFlight);
      continue;
    }
    // Pass the abort signal ONLY to visible-range fetches. Prefetch fetches must NOT be cancellable — they're speculative loads that should
    // complete to populate the cache, so a subsequent wheel event can find the chunk already-loaded. If we aborted prefetches on every
    // viewport change, rapid wheel navigation would cancel every chunk before it finishes, forcing the final stop to refetch from scratch.
    // With this split, rapid wheels still land on a cache full of in-flight or completed prefetches — the final visible chunk almost always
    // resolves from cache (or a reusable inFlight promise), not a cold fetch.
    const isPinned = pinnedIdxs.has(idx);
    const fetchSignal = isPinned ? signal : undefined;
    const promise = loadChunk(cache, path, metadata, manifest[idx], idx, fetchSignal);
    cache.inFlight.set(idx, promise);
    if (isPinned) {
      visibleLoadPromises.push(promise);
    } else {
      // Prefetch-only path: nothing awaits this promise, so if it rejects (speculative fetch 500, network blip) we'd surface an
      // "Uncaught (in promise)" warning in the browser console. Attach a silent tail so the rejection is considered handled — the chunk
      // simply won't land in the cache, and the next viewport change will try again if it's still needed.
      promise.catch(() => {});
    }
  }

  const visible = await Promise.all(visibleLoadPromises);
  // Post-load eviction: drop chunks not in the full (visible + prefetch) set if we're over budget.
  evictIfOverBudget(cache, new Set(allIndices));
  return visible.sort((a, b) => a.fromTick - b.fromTick);
}

/**
 * Assemble a flat, sorted array of TickData from the currently-cached chunks. This is what `ProcessedTrace.ticks` should point to after any
 * cache mutation — consumers (GraphArea, DetailPane) read from this view without knowing about chunk boundaries.
 *
 * The returned array is newly allocated on every call; callers should re-reference (setTrace) to trigger Preact re-render.
 * Prefer <see cref="assembleTickViewAndNumbers"/> when the caller needs both arrays — saves a second sort + second iteration.
 */
export function assembleTickView(cache: ChunkCacheState): TickData[] {
  return assembleTickViewAndNumbers(cache).tickData;
}

/**
 * Build both the flat TickData array AND the parallel tickNumbers array in a single pass. Single sort of cache entries (was duplicated
 * across `assembleTickView` and `assembleTickNumbers`), single inner-loop traversal of each chunk's ticks. For a 200 MB cache budget that's
 * ~50 chunks — the savings here aren't huge per call, but the function fires on every chunk load during pan/zoom, so halving the work is
 * worth the few extra lines.
 */
export function assembleTickViewAndNumbers(cache: ChunkCacheState): { tickData: TickData[]; tickNumbers: number[] } {
  const chunks = Array.from(cache.entries.values()).sort((a, b) => a.fromTick - b.fromTick);
  const tickData: TickData[] = [];
  const tickNumbers: number[] = [];
  for (const chunk of chunks) {
    for (const tick of chunk.tickData) {
      tickData.push(tick);
      tickNumbers.push(tick.tickNumber);
    }
  }
  return { tickData, tickNumbers };
}

/**
 * Map an absolute-µs range to a half-open tick-number range [fromTick, toTick). Uses binary search over the summary (sorted by startUs).
 * Strict half-open overlap: a tick [tickStart, tickEnd) overlaps [fromUs, toUs) iff tickEnd > fromUs && tickStart < toUs. Boundary touches
 * (tickStart == toUs or tickEnd == fromUs) do NOT count — otherwise selecting a single tick would spill into adjacent chunks whose startUs
 * kisses the selection's endUs.
 *
 * This runs on every wheel event at 60 Hz; on a 500K-tick summary the prior linear scan was ~5 ms × 60 = 300 ms/sec of pure CPU just on
 * range mapping. Binary search drops that to ~3 µs per call.
 */
export function viewRangeToTickRange(
  summary: TickSummary[],
  fromUs: number,
  toUs: number,
): { fromTick: number; toTick: number } | null {
  if (summary.length === 0) return null;

  // `first` = smallest i such that summary[i].startUs + durationUs > fromUs (tick extends past viewport start).
  // Binary search on the monotone predicate P(i) := endUs(i) > fromUs. Standard lower-bound.
  let lo = 0;
  let hi = summary.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    const s = summary[mid];
    if (s.startUs + s.durationUs > fromUs) hi = mid;
    else lo = mid + 1;
  }
  const firstIdx = lo;
  if (firstIdx >= summary.length) return null;               // all ticks end at or before fromUs

  // `last` = largest i such that summary[i].startUs < toUs. Binary search for upper-bound of startUs < toUs.
  lo = firstIdx;
  hi = summary.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (summary[mid].startUs < toUs) lo = mid + 1;
    else hi = mid;
  }
  const lastIdx = lo - 1;                                   // last i satisfying the predicate
  if (lastIdx < firstIdx) return null;                      // no tick overlaps [fromUs, toUs)

  return { fromTick: summary[firstIdx].tickNumber, toTick: summary[lastIdx].tickNumber + 1 };
}

/** Extract a sorted array of tickNumbers from the current cache — updated alongside assembleTickView. */
export function assembleTickNumbers(cache: ChunkCacheState): number[] {
  return assembleTickViewAndNumbers(cache).tickNumbers;
}

// ─────────────────────────────────────────────────────────────────────────────
// Internals
// ─────────────────────────────────────────────────────────────────────────────

async function loadChunk(
  cache: ChunkCacheState,
  path: string,
  metadata: TraceMetadata,
  entry: ChunkManifestEntry,
  chunkIdx: number,
  signal?: AbortSignal,
): Promise<LoadedChunk> {
  try {
    let tickData: TickData[];
    let byteSize: number;

    if (USE_BINARY_CHUNK_TRANSPORT) {
      // Binary path: fetch raw LZ4 bytes + metadata headers, transfer the ArrayBuffer into the Worker (zero-copy), and let it decompress,
      // decode, and build TickData in one hop. The main thread is idle for the whole CPU-heavy portion — only the fetch lands here.
      const response = await fetchChunkBinary(path, entry, signal);
      const ticksPerUs = response.timestampFrequency / 1_000_000;
      // The ArrayBuffer becomes detached in the caller's frame after postMessage's transfer list kicks in — assigning to `.buffer` of a
      // Uint8Array is a read, not a copy, so we pass the underlying buffer directly.
      tickData = await processBinaryInWorker(
        // `fetch().arrayBuffer()` always returns a real ArrayBuffer (never SharedArrayBuffer). TS types the Uint8Array.buffer as
        // ArrayBufferLike to cover the shared case too — narrow it here. Safe because api.fetchChunkBinary ALWAYS originates from arrayBuffer().
        response.compressed.buffer as ArrayBuffer,
        response.uncompressedBytes,
        entry.fromTick,
        ticksPerUs,
        metadata.systems,
      );
      // Byte-size estimate: compressed wire is what the cache "costs" in flight. Decompressed + TickData structures are derived; using
      // eventCount × AVG_BYTES_PER_EVENT overestimates but keeps the LRU math comparable to the JSON path's accounting.
      byteSize = response.eventCount * AVG_BYTES_PER_EVENT;
    } else {
      // Legacy JSON path — server decodes, emits JSON, client runs through the Worker on already-parsed events.
      const response = await fetchChunk(path, entry, signal);
      tickData = await processEventsInWorker(response.events, metadata.systems);
      byteSize = response.events.length * AVG_BYTES_PER_EVENT;
    }

    const loaded: LoadedChunk = {
      chunkIdx,
      fromTick: entry.fromTick,
      toTick: entry.toTick,
      tickData,
      byteSize,
      lastAccessTick: ++cache.accessCounter,
    };
    cache.entries.set(chunkIdx, loaded);
    cache.totalBytes += byteSize;
    return loaded;
  } finally {
    cache.inFlight.delete(chunkIdx);
  }
}

/** Find all manifest indices whose [fromTick, toTick) range overlaps the requested [fromTick, toTick). */
function findChunksOverlapping(manifest: ChunkManifestEntry[], fromTick: number, toTick: number): number[] {
  const result: number[] = [];
  for (let i = 0; i < manifest.length; i++) {
    const entry = manifest[i];
    // Overlap condition for half-open ranges: !(entry.toTick <= fromTick || entry.fromTick >= toTick)
    if (entry.toTick > fromTick && entry.fromTick < toTick) {
      result.push(i);
    }
  }
  return result;
}

/**
 * If totalBytes exceeds the budget, evict LRU-ordered entries that are NOT in the pinned set. Pinned entries are the ones overlapping the
 * current viewport — evicting them would leave visible gaps, so they're immune regardless of age.
 */
function evictIfOverBudget(cache: ChunkCacheState, pinnedIdxs: Set<number>): void {
  if (cache.totalBytes <= cache.budgetBytes) return;

  // Sort candidates (unpinned) by lastAccessTick ascending — oldest first.
  const candidates: LoadedChunk[] = [];
  for (const entry of cache.entries.values()) {
    if (!pinnedIdxs.has(entry.chunkIdx)) candidates.push(entry);
  }
  candidates.sort((a, b) => a.lastAccessTick - b.lastAccessTick);

  for (const victim of candidates) {
    if (cache.totalBytes <= cache.budgetBytes) break;
    cache.entries.delete(victim.chunkIdx);
    cache.totalBytes -= victim.byteSize;
  }
}

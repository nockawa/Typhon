import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  assembleTickViewAndNumbers,
  computePendingRangesUs,
  createChunkCache,
  ensureRangeLoaded,
  type ChunkCacheState,
  viewRangeToTickRange,
} from '@/libs/profiler/cache/chunkCache';
import {
  convertChunkManifest,
  convertProfilerMetadata,
  convertTickSummaries,
} from '@/libs/profiler/cache/dtoConverters';
import type {
  GaugeSeries,
  GcEvent,
  GcSuspensionEvent,
  MemoryAllocEventData,
  TickData,
} from '@/libs/profiler/model/traceModel';
import type { ChunkManifestEntry, GaugeId, TickSummary, TraceMetadata } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

/**
 * Chunk-cache lifecycle + viewRange-driven loader for trace sessions. Creates a cache on first
 * metadata arrival, converts the OpenAPI DTO to the internal `TraceMetadata` + manifest shape,
 * kicks `ensureRangeLoaded` whenever the viewport changes, and returns the currently-resident
 * `TickData[]` via `assembleTickViewAndNumbers`.
 *
 * **Trace mode only.** Attach (live) mode's data arrives via SSE and is accumulated as
 * `useProfilerSessionStore.recentTicks` — converting that path to the same `TickData[]` shape is a
 * separate concern that lands alongside live-rendering. For now the hook returns an empty array
 * when `sessionId` is null or the session is live.
 */
/**
 * Aggregate of everything the chunk cache assembles beyond the plain tick array. Kept as a
 * single bundle so consumers (TimeArea, gauges) receive a stable reference when none of the
 * fields have changed, and so one cache-version bump re-assembles the whole set atomically.
 */
export interface ProfilerGaugeData {
  gaugeSeries: Map<GaugeId, GaugeSeries>;
  gaugeCapacities: Map<GaugeId, number>;
  memoryAllocEvents: MemoryAllocEventData[];
  gcEvents: GcEvent[];
  gcSuspensions: GcSuspensionEvent[];
  threadNames: Map<number, string>;
}

const EMPTY_GAUGE_DATA: ProfilerGaugeData = {
  gaugeSeries: new Map(),
  gaugeCapacities: new Map(),
  memoryAllocEvents: [],
  gcEvents: [],
  gcSuspensions: [],
  threadNames: new Map(),
};

export function useProfilerCache(sessionId: string | null, isLive: boolean): {
  ticks: TickData[];
  traceMetadata: TraceMetadata | null;
  gaugeData: ProfilerGaugeData;
  /**
   * µs-ranges within the current viewport whose chunk data is not yet resident in the cache.
   * Empty when everything is loaded or when the session is live / empty. TimeArea overlays a
   * diagonal-stripe pattern on each range as a "loading here" cue.
   */
  pendingRangesUs: Array<{ startUs: number; endUs: number }>;
} {
  const metadataDto = useProfilerSessionStore((s) => s.metadata);
  const viewRange = useProfilerViewStore((s) => s.viewRange);

  // `{0, 0}` = "no selection" — TimeArea renders an empty-state placeholder in that case, so no
  // point loading chunks. A real range only flows through once the user drags in the overview
  // (or zooms in the main area). Keeps bandwidth + OPFS writes idle until intent is expressed.
  const hasSelection = viewRange.endUs > viewRange.startUs;

  // ── Stable references derived from the DTO ──────────────────────────────────────────────────
  // TanStack Query only emits a new `metadataDto` reference when the underlying data actually
  // changes, so depending on it directly is enough — the conversion cost is trivial and orval's
  // per-poll object churn isn't a real concern in our flow (polling stops once metadata resolves).
  const fingerprint = metadataDto?.fingerprint ?? null;

  const traceMetadata: TraceMetadata | null = useMemo(() => {
    if (!metadataDto) return null;
    return convertProfilerMetadata(metadataDto);
  }, [metadataDto]);

  const manifest: ChunkManifestEntry[] = useMemo(() => {
    if (!metadataDto) return [];
    return convertChunkManifest(metadataDto.chunkManifest);
  }, [metadataDto]);

  const tickSummaries: TickSummary[] = useMemo(() => {
    if (!metadataDto) return [];
    return convertTickSummaries(metadataDto.tickSummaries);
  }, [metadataDto]);

  // ── Cache instance (one per session) ─────────────────────────────────────────────────────────
  const cacheRef = useRef<ChunkCacheState | null>(null);
  useEffect(() => {
    // Create a fresh cache on session or fingerprint change. Old cache is GC'd once its ref drops.
    if (!sessionId || isLive) {
      cacheRef.current = null;
      return;
    }
    cacheRef.current = createChunkCache();
    setEntriesVersion(0); // reset so the first assembleTickView runs on the new empty cache
  }, [sessionId, isLive, fingerprint]);

  // ── Load driver ──────────────────────────────────────────────────────────────────────────────
  // Every viewRange change kicks ensureRangeLoaded; the returned promise ticks `entriesVersion`
  // on completion so the memo below re-runs against the expanded cache.
  const [entriesVersion, setEntriesVersion] = useState(0);
  const inFlightController = useRef<AbortController | null>(null);

  const loadRange = useCallback(async (range: TimeRange): Promise<void> => {
    const cache = cacheRef.current;
    if (!cache || !traceMetadata || !sessionId || manifest.length === 0 || tickSummaries.length === 0) return;
    const tr = viewRangeToTickRange(tickSummaries, range.startUs, range.endUs);
    if (!tr) return;

    // Cancel any prior visible-range fetch. Prefetches keep running (ensureRangeLoaded doesn't
    // pass the signal to them).
    inFlightController.current?.abort();
    const ac = new AbortController();
    inFlightController.current = ac;

    try {
      await ensureRangeLoaded(
        cache, sessionId, traceMetadata, manifest,
        tr.fromTick, tr.toTick,
        undefined, undefined,
        ac.signal,
      );
      if (!ac.signal.aborted) {
        setEntriesVersion((v) => v + 1);
      }
    } catch (err) {
      // AbortError is expected on rapid viewport changes. Log others so we have breadcrumbs.
      if (err !== null && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError') return;
      console.warn('[useProfilerCache] ensureRangeLoaded failed:', err);
    }
  }, [traceMetadata, sessionId, manifest, tickSummaries]);

  useEffect(() => {
    if (isLive) return;
    if (!hasSelection) return; // skip loads when no selection — TimeArea renders a placeholder
    void loadRange(viewRange);
    // No cleanup — the controller is reassigned on the next call.
  }, [viewRange, hasSelection, loadRange, isLive]);

  // ── Assembled tick view + gauge data ─────────────────────────────────────────────────────────
  // Single memo → single traversal of the cache per version bump. Returns both the tick array
  // (consumed by TimeArea/2b) and the gauge bundle (consumed by the 2c gauge renderers). Keeping
  // them in one memo means we don't scan the cache twice when `entriesVersion` bumps.
  //
  // `entriesVersion` isn't read inside the body — it's a tick from the loader that signals "the
  // cache was mutated imperatively, please re-assemble." Reference it via `void` so ESLint's
  // exhaustive-deps check sees the use and stops flagging the dep as unnecessary.
  const assembled = useMemo(() => {
    void entriesVersion;
    const cache = cacheRef.current;
    if (!cache || !traceMetadata) return null;
    return assembleTickViewAndNumbers(cache, traceMetadata.systems);
  }, [entriesVersion, traceMetadata]);

  const ticks: TickData[] = assembled?.tickData ?? [];
  const gaugeData: ProfilerGaugeData = assembled
    ? {
        gaugeSeries: assembled.gaugeSeries,
        gaugeCapacities: assembled.gaugeCapacities,
        memoryAllocEvents: assembled.memoryAllocEvents,
        gcEvents: assembled.gcEvents,
        gcSuspensions: assembled.gcSuspensions,
        threadNames: assembled.threadNames,
      }
    : EMPTY_GAUGE_DATA;

  // Pending ranges — recomputed per version bump (same cadence as the assembly). Deps include
  // `viewRange` so panning / zooming re-evaluates which chunks still need to land.
  const pendingRangesUs = useMemo(() => {
    void entriesVersion;
    if (!hasSelection) return [];
    return computePendingRangesUs(cacheRef.current, manifest, tickSummaries, viewRange);
  }, [entriesVersion, manifest, tickSummaries, viewRange, hasSelection]);

  return { ticks, traceMetadata, gaugeData, pendingRangesUs };
}

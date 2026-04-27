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
import {
  aggregateGaugeData,
  processTickEvents,
  type GaugeSeries,
  type GcEvent,
  type GcSuspensionEvent,
  type MemoryAllocEventData,
  type TickData,
} from '@/libs/profiler/model/traceModel';
import type { ChunkManifestEntry, GaugeId, SystemDef, TickSummary, TraceEvent, TraceMetadata } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerSessionStore, type LiveTickBatch } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

/**
 * Maximum number of unique tick numbers retained in the live tick cache. The SSE-side ring buffer
 * (`LIVE_TICK_BUFFER_CAP` in `useProfilerSessionStore`) caps per BATCH, which is the wrong unit:
 * one big tick (e.g. AntHill's 200K-spawn tick 1) can span hundreds of batches and starve the buffer
 * of every other tick. The cache below caps per UNIQUE TICK, evicting oldest tickNumber when over.
 * Sized for ~3 minutes of 60 Hz capture, well above what any reasonable interactive session needs.
 */
const LIVE_TICK_CACHE_CAP = 10_000;

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

  // ── Live-mode tick assembly (Attach sessions) ────────────────────────────────────────────────
  // Live ticks arrive via SSE and accumulate in `useProfilerSessionStore.recentTicks` (capped at
  // LIVE_TICK_BUFFER_CAP, drop-oldest). We mirror the trace cache's per-tick TickData shape so the
  // downstream renderers (TimeArea, TickOverview) need no live-aware branching.
  //
  // The cache is per-tickNumber; entries are rebuilt only when the event count for that tick grew
  // (multi-block ticks append events from later batches). Ticks no longer in the ring buffer get
  // pruned on each pass. This keeps per-render cost ≈ "size of new event arrivals" rather than
  // "rebuild all 1000 ticks from scratch."
  const recentTicks = useProfilerSessionStore((s) => s.recentTicks);
  const liveTickCacheRef = useRef<Map<number, { eventCount: number; tickData: TickData }>>(new Map());

  // Persistent slot → thread-name map. ThreadInfo records come from the tick-0 catch-up batch (or
  // mid-session slot claims) and are inherently session-wide metadata, NOT per-tick data. Without
  // persistence, the names disappear from the timeline as soon as the tick-0 batch falls out of the
  // 1000-batch ring buffer in `recentTicks` (~16 s at 60 Hz). Mirrors `ChunkCacheState.threadNames`
  // for trace mode. Cleared only when we leave live mode or the session changes.
  const liveThreadNamesRef = useRef<Map<number, string>>(new Map());

  // Reset the live cache whenever metadata flips (new attach session) or we leave live mode.
  useEffect(() => {
    if (!isLive) {
      liveTickCacheRef.current.clear();
      liveThreadNamesRef.current.clear();
    }
  }, [isLive, sessionId, fingerprint]);

  // SystemDef[] used by processTickEvents — the trace path builds this once per metadata change
  // via convertProfilerMetadata; live mode reuses the same conversion for parity.
  const liveSystems: SystemDef[] = useMemo(() => traceMetadata?.systems ?? [], [traceMetadata]);

  const liveAssembled = useMemo(() => {
    if (!isLive) return null;

    // Group batches by tickNumber so we can detect event-count drift (a tick split across blocks
    // produces multiple batches with the same tickNumber arriving over time).
    const batchesByTick = new Map<number, LiveTickBatch[]>();
    for (const batch of recentTicks) {
      const arr = batchesByTick.get(batch.tickNumber);
      if (arr) {
        arr.push(batch);
      } else {
        batchesByTick.set(batch.tickNumber, [batch]);
      }
    }

    const cache = liveTickCacheRef.current;

    // NB: do NOT prune cache entries when their batches age out of `recentTicks`. The recentTicks ring
    // is per-BATCH (LIVE_TICK_BUFFER_CAP = 1000); a single big tick (e.g. AntHill's 200K-spawn tick 1)
    // can span hundreds of batches, evicting older completed ticks and leaving the user with a tiny
    // visible window. Once we've decoded a TickData we keep it. The cache itself caps at LIVE_TICK_CACHE_CAP
    // unique ticks below, evicting oldest tickNumber when over.

    // Rebuild ticks whose event count grew (new batches arrived for an in-flight multi-block tick).
    // Crucially we only rebuild when total > cached.eventCount — if batches AGED OUT of the ring,
    // total < cached.eventCount, and rebuilding would discard already-decoded events.
    for (const [tickNumber, batches] of batchesByTick) {
      let total = 0;
      for (const b of batches) total += b.events.length;
      const cached = cache.get(tickNumber);
      if (cached && total <= cached.eventCount) continue;

      const combined: TraceEvent[] = new Array(total);
      let idx = 0;
      for (const b of batches) {
        for (const e of b.events) combined[idx++] = e;
      }

      // Live mode: pass `suppressMalformedWarn = true` so the missing-TickStart warning doesn't fire on every
      // multi-block tick that's transiting through the recentTicks FIFO eviction window (where the first block —
      // carrying TickStart — has been evicted but a tail block is still present). No actual upstream data loss.
      const tickData = processTickEvents(tickNumber, combined, liveSystems, /*isContinuation*/ false, /*suppressMalformedWarn*/ true);

      // Live-stream salvage. The engine drops TickStart / TickEnd records under producer-ring
      // pressure (`TyphonEvent.STickStartDroppedRingFull`), so live ticks frequently come without
      // those boundary markers. processTickEvents falls back to (0, 0) when both are missing, and
      // sets endUs = startUs when only TickEnd is missing — both produce durationUs = 0 which then
      // breaks downstream layout. Derive a best-effort tick window from the events' own timestamps:
      //   - if startUs is unset (== 0): take min(events.timestampUs)
      //   - if endUs is degenerate (<= startUs): take max(events.timestampUs + durationUs)
      // This gives every tick that has any real events a valid render window. Lone-TickStart stubs
      // (only event = TickStart with no payload duration) produce minTs == maxTs → durationUs stays
      // 0 and they get filtered below.
      if (combined.length > 0) {
        let minTs = Infinity;
        let maxTs = -Infinity;
        for (const e of combined) {
          if (e.timestampUs < minTs) minTs = e.timestampUs;
          const end = e.timestampUs + (e.durationUs ?? 0);
          if (end > maxTs) maxTs = end;
        }
        if (minTs !== Infinity) {
          if (tickData.startUs === 0) tickData.startUs = minTs;
          if (tickData.endUs <= tickData.startUs) tickData.endUs = maxTs > minTs ? maxTs : tickData.startUs;
          tickData.durationUs = tickData.endUs - tickData.startUs;
        }
      }

      cache.set(tickNumber, { eventCount: total, tickData });

      // Per-tick cap on the cache: evict the oldest tickNumber once we exceed LIVE_TICK_CACHE_CAP. NB
      // tickNumber 0 is the pre-tick bucket carrying ThreadInfo records — we keep its DECODED payload
      // (already drained into liveThreadNamesRef above) but its raw tickData entry can be evicted like
      // any other.
      while (cache.size > LIVE_TICK_CACHE_CAP) {
        const oldestKey: number | undefined = cache.keys().next().value;
        if (oldestKey === undefined) break;
        cache.delete(oldestKey);
      }

      // Harvest any ThreadInfo records from this tick into the persistent slot→name map. Even though
      // tick 0's tickData is in liveTickCacheRef, it'll be pruned the moment tick 0 falls out of the
      // ring buffer (~16 s after attach at 60 Hz). The thread-name mapping is session-wide metadata
      // and must survive that eviction.
      if (tickData.threadInfos.length > 0) {
        for (const info of tickData.threadInfos) {
          liveThreadNamesRef.current.set(info.threadSlot, info.name);
        }
      }
    }

    // Filter out "stub" ticks — buckets with no useful timing window. After the salvage above the
    // only ticks left at durationUs <= 0 are pure TickStart-only stubs (the engine dropped every
    // other event for that tick under ring pressure, so even the salvage couldn't widen the
    // window). They appear as misleading 0 ms / 1-event bars in the overview and confuse the
    // selection-rect math, so we hide them entirely. The user still sees the dropped-tick signal
    // via the producer-side `TickStartDroppedRingFull` counter.
    //
    // tickNumber === 0 is the PRE-TICK bucket, not a real engine tick — `ThreadInfo` records
    // (and other startup-time instants emitted before the first TickStart) live there. It always
    // has durationUs == 0 because there's no TickStart/TickEnd inside it; if we apply the stub
    // filter to it the cross-tick aggregator never sees those ThreadInfo records and lane labels
    // permanently fall back to "Slot N". Preserve it unconditionally.
    const tickData: TickData[] = Array.from(cache.values(), (v) => v.tickData)
      .filter((t) => t.tickNumber === 0 || t.durationUs > 0)
      .sort((a, b) => a.tickNumber - b.tickNumber);
    const tickNumbers = tickData.map((t) => t.tickNumber);
    const aggregated = aggregateGaugeData(tickData);
    // Override the per-assembly threadNames with the persistent map. The aggregator only sees ticks
    // currently in tickData; once the tick-0 batch (which carries the catch-up ThreadInfo records)
    // falls out of the recentTicks ring buffer, aggregated.threadNames goes empty. The persistent
    // ref accumulates from every tick we ever process, so it survives ring-buffer eviction.
    return { tickData, tickNumbers, ...aggregated, threadNames: liveThreadNamesRef.current };
  }, [isLive, recentTicks, liveSystems]);

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

  // Eager metadata-chunk prefetch (trace mode). Chunk 1 carries the prepended pre-tick events from
  // the cache builder — `ThreadInfo` records (lane labels), early `MemoryAllocEvent`s, etc. — that
  // are MEANT to be metadata-shaped (one-shot, session-wide) but live in chunk-1's binary anyway.
  // Without this, a user who drags a viewRange into a later region of the trace never loads chunk 1
  // and the chunk-cache `threadNames` map stays empty forever. Fire a single non-cancellable load
  // for the first manifest entry as soon as the manifest is available; cheap, runs once per session,
  // and the result is harvested into `cache.threadNames` (persists across LRU eviction).
  useEffect(() => {
    if (isLive) return;
    if (!sessionId || !traceMetadata || manifest.length === 0) return;
    const cache = cacheRef.current;
    if (!cache) return;
    const firstChunk = manifest[0];
    if (firstChunk === undefined) return;
    void ensureRangeLoaded(
      cache, sessionId, traceMetadata, manifest,
      firstChunk.fromTick, firstChunk.toTick,
      undefined, undefined,
      undefined,
    ).then(() => {
      // Bump the version so a follow-up assembly sees the harvested threadNames even if the user
      // hasn't dragged a viewRange yet — `assembleTickViewAndNumbers` returns the cached map and
      // `gaugeData.threadNames` flows into `TimeArea` immediately.
      setEntriesVersion((v) => v + 1);
    }).catch((err) => {
      // Network blips and AbortErrors are silent in the regular load path — same here.
      if (err !== null && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError') return;
      console.warn('[useProfilerCache] eager chunk-1 load failed:', err);
    });
  }, [isLive, sessionId, traceMetadata, manifest]);

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

  // Live mode picks the live assembly branch; trace mode uses the chunk cache assembly.
  const sourceAssembled = isLive ? liveAssembled : assembled;
  const ticks: TickData[] = sourceAssembled?.tickData ?? [];
  const gaugeData: ProfilerGaugeData = sourceAssembled
    ? {
        gaugeSeries: sourceAssembled.gaugeSeries,
        gaugeCapacities: sourceAssembled.gaugeCapacities,
        memoryAllocEvents: sourceAssembled.memoryAllocEvents,
        gcEvents: sourceAssembled.gcEvents,
        gcSuspensions: sourceAssembled.gcSuspensions,
        threadNames: sourceAssembled.threadNames,
      }
    : EMPTY_GAUGE_DATA;

  // Pending ranges — recomputed per version bump (same cadence as the assembly). Deps include
  // `viewRange` so panning / zooming re-evaluates which chunks still need to land. Live mode has
  // no chunk fetching (data is push-only via SSE), so pending ranges are always empty.
  const pendingRangesUs = useMemo(() => {
    void entriesVersion;
    if (isLive) return [];
    if (!hasSelection) return [];
    return computePendingRangesUs(cacheRef.current, manifest, tickSummaries, viewRange);
  }, [entriesVersion, manifest, tickSummaries, viewRange, hasSelection, isLive]);

  return { ticks, traceMetadata, gaugeData, pendingRangesUs };
}

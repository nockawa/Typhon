import { useState, useCallback, useRef, useEffect } from 'preact/hooks';
import { uploadTrace, openTrace, subscribeBuildProgress, type BuildProgress } from './api';
import { processTrace, createEmptyTrace, processTickAndAppend, type ProcessedTrace, type ChunkSpan, type SpanData, type MarkerSelection } from './traceModel';
import type { TimeRange } from './uiTypes';
import type { TraceEvent, TraceMetadata, ChunkManifestEntry } from './types';
import { MenuBar } from './MenuBar';
import { TickTimeline } from './TickTimeline';
import { Workspace } from './Workspace';
import { DIM_TEXT, SPAN_PALETTE } from './canvasUtils';
import { connectLive } from './liveSource';
import { useNavHistory } from './useNavHistory';
import {
  loadPersistedGaugeRegionVisible, persistGaugeRegionVisible,
  loadPersistedLegendsVisible, persistLegendsVisible,
} from './gaugeRegion';
import {
  createChunkCache,
  ensureRangeLoaded,
  assembleTickViewAndNumbers,
  viewRangeToTickRange,
  DEFAULT_PREFETCH_CHUNKS,
  type ChunkCacheState,
} from './chunkCache';
import { OpfsChunkStore } from './opfsChunkStore';

/** Buffered tick data waiting to be processed into the trace. */
interface BufferedTick {
  tickNumber: number;
  events: TraceEvent[];
}

/**
 * Three load-identity fields that always transition together (file open, live session start, live tick flush). Consolidated into
 * one useState so the post-await transition in handleFileSelected commits in a single render regardless of whether Preact's
 * microtask scheduler is coalescing calls across the await boundary.
 */
interface LoadedTraceBundle {
  trace: ProcessedTrace | null;
  tracePath: string | null;
  fileName: string | null;
}

const EMPTY_BUNDLE: LoadedTraceBundle = { trace: null, tracePath: null, fileName: null };

/**
 * Maximum wall-clock time we'll wait for the server's build-progress SSE feed to complete. A 15 GB source trace builds its cache in under a
 * minute on normal hardware; 120 s is a safe upper bound that still catches genuinely stuck backends (server hung, proxy eating SSE, slow
 * filesystem). Shorter than this would risk killing legitimate large-trace builds mid-run; longer would leave the user staring at a spinner
 * with no recovery path. Fires a controlled reject with a user-actionable message rather than letting `setLoading(true)` stick forever.
 */
const BUILD_PROGRESS_TIMEOUT_MS = 120_000;

/** Race a promise against a timeout. Resolves/rejects with whichever wins. */
function withTimeout<T>(promise: Promise<T>, ms: number, message: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => reject(new Error(message)), ms);
    promise.then(
      (v) => { window.clearTimeout(timer); resolve(v); },
      (e) => { window.clearTimeout(timer); reject(e); },
    );
  });
}

export function App() {
  const [loaded, setLoaded] = useState<LoadedTraceBundle>(EMPTY_BUNDLE);
  const { trace, tracePath, fileName } = loaded;
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  /** Set while the server is actively building the sidecar cache for a trace we're opening. Drives the build-progress banner/overlay. */
  const [buildProgress, setBuildProgress] = useState<BuildProgress | null>(null);
  /** True while a viewport-triggered chunk fetch is in flight. Distinct from `loading`, which is only set during the initial file upload +
   * open. Used to surface a subtle "loading detail..." indicator during pan/zoom so the user knows fresh data is on the way. */
  const [isLoadingChunks, setIsLoadingChunks] = useState(false);
  const [viewRange, setViewRange] = useState<TimeRange>({ startUs: 0, endUs: 0 });
  const [selectedChunk, setSelectedChunk] = useState<ChunkSpan | null>(null);
  const [selectedSpan, setSelectedSpan] = useState<SpanData | null>(null);
  const [selectedMarker, setSelectedMarker] = useState<MarkerSelection | null>(null);
  const navHistory = useNavHistory();

  // Live mode state
  const [isLive, setIsLive] = useState(false);
  const liveRef = useRef<EventSource | null>(null);
  const tickBufferRef = useRef<BufferedTick[]>([]);
  const traceRef = useRef<ProcessedTrace | null>(null);
  const flushIntervalRef = useRef<number>(0);

  // View toggles — owned by App so both the <b>View</b> menu and the global keyboard shortcuts share one source of truth and flip
  // the same state. Persisted to localStorage via the helpers in <see cref="gaugeRegion.ts"/>.
  const [gaugeRegionVisible, setGaugeRegionVisible] = useState<boolean>(() => loadPersistedGaugeRegionVisible());
  const [legendsVisible, setLegendsVisible] = useState<boolean>(() => loadPersistedLegendsVisible());
  // Gutter width measured by <see cref="GraphArea"/>. Lifted to App so sibling <see cref="TickTimeline"/> can align its "?" help glyph with
  // the gutter's right-edge X. 80 matches <c>MIN_GUTTER_WIDTH</c> — safe fallback until the first render measures the real width.
  const [gutterWidth, setGutterWidth] = useState<number>(80);
  useEffect(() => { persistGaugeRegionVisible(gaugeRegionVisible); }, [gaugeRegionVisible]);
  useEffect(() => { persistLegendsVisible(legendsVisible); }, [legendsVisible]);
  const toggleGaugeRegion = useCallback(() => setGaugeRegionVisible(v => !v), []);
  const toggleLegends = useCallback(() => setLegendsVisible(v => !v), []);

  // 'g' / 'l' keyboard shortcuts — global listener, same guard pattern as before: skip when focus is inside an editable element,
  // Global OPFS cleanup — runs once on app mount. Enumerates every trace directory under typhon-chunks/*, sums per-trace sizes,
  // and deletes oldest-accessed trace directories until total disk usage is under the global cap. Background task; failures are
  // silent (see OpfsChunkStore.globalCleanup).
  useEffect(() => {
    // Fire-and-forget; no deps → runs once. Failure to clean up is not user-visible and doesn't block any functionality.
    void OpfsChunkStore.globalCleanup();
  }, []);

  // skip when any modifier is held (Ctrl+L is a browser-reserved bind — if we stole it the URL bar would fight us).
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const target = e.target as HTMLElement | null;
      if (target) {
        const tag = target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || target.isContentEditable) return;
      }
      if (e.key === 'g' || e.key === 'G') { e.preventDefault(); toggleGaugeRegion(); }
      else if (e.key === 'l' || e.key === 'L') { e.preventDefault(); toggleLegends(); }
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [toggleGaugeRegion, toggleLegends]);

  // Phase 2b: chunked-file state. Lives in refs — mutated during viewport loads without triggering re-renders on every chunk.
  const chunkCacheRef = useRef<ChunkCacheState | null>(null);
  const chunkManifestRef = useRef<ChunkManifestEntry[] | null>(null);
  const metadataRef = useRef<TraceMetadata | null>(null);
  /**
   * Monotonic counter bumped at the start of every trace-open handler (file upload or open-by-path). Each handler captures
   * its own epoch locally and verifies the epoch is still current before committing any shared state (refs or `setLoaded`).
   * Protects against the two-handler race where a user clicks File→Open A and then File→Open B before A finishes: without
   * this gate, A's `await` could resolve AFTER B has already written refs + setLoaded, causing A's stale values to overwrite
   * B's fresh ones. The viewport effect would then trigger with A's refs but B's tracePath and fetch chunks from the wrong
   * cache.
   */
  const loadEpochRef = useRef<number>(0);
  // Velocity tracking for directional prefetch. Records the last viewport shift so we can measure ticks-per-second over a
  // short window and bias prefetch toward the leading edge when the user is panning fast. Stationary → symmetric default.
  const lastViewportShiftRef = useRef<{ fromTick: number; toTick: number; at: number } | null>(null);
  // Explicit follow-mode flag. True = auto-scroll viewRange to the latest tick on each flush.
  // Set false as soon as the user interacts with the timeline/graph (pan, zoom, click).
  // Lives in a ref so changing it doesn't re-render, and the flush callback always sees the latest value.
  const followRef = useRef<boolean>(true);

  // Keep traceRef in sync with state
  useEffect(() => {
    traceRef.current = trace;
  }, [trace]);

  // File loading handler (existing)
  const handleFileSelected = useCallback(async (files: File[]) => {
    // Disconnect live if active
    if (liveRef.current) {
      liveRef.current.close();
      liveRef.current = null;
      setIsLive(false);
    }

    // Claim a fresh epoch before any await. Captures the value locally so we can detect if another open-handler has
    // started since — see `isStaleEpoch` calls below. Two concurrent handlers' local `myEpoch` values will differ; the
    // later one wins, the earlier one becomes a no-op from its first stale check onward.
    const myEpoch = ++loadEpochRef.current;
    const isStaleEpoch = () => loadEpochRef.current !== myEpoch;

    setLoading(true);
    setError(null);
    setBuildProgress(null);
    try {
      const result = await uploadTrace(files);
      if (isStaleEpoch()) return;
      // Subscribe to build-progress BEFORE calling open — open blocks until the cache is built, so if we wait to subscribe we'd miss the
      // progress stream entirely. If the cache is already fresh (re-opening a recently-uploaded trace), the build-progress feed sends `done`
      // immediately and resolves without any progress ticks. Errors here surface the same way as open errors.
      await withTimeout(
        subscribeBuildProgress(result.path, setBuildProgress),
        BUILD_PROGRESS_TIMEOUT_MS,
        'Build progress feed timed out — the server may be stuck.',
      );
      if (isStaleEpoch()) return;
      setBuildProgress(null);
      // Phase 2b: open returns metadata + summary + chunk manifest. No detail events are loaded here — the viewport effect below handles that
      // lazily based on the visible tick range. First-paint is therefore O(summary size), not O(full trace) — indifferent to trace file size.
      const opened = await openTrace(result.path);
      if (isStaleEpoch()) return;

      // Build an empty-but-metadata-rich trace. Ticks populate on demand as the viewport effect fires. globalStart/End/max/p95 come from the
      // server-computed metrics so the timeline can render the full file shape without any detail chunks loaded.
      const processed = processTrace(result.metadata, []);
      processed.summary = opened.tickSummaries;
      processed.globalStartUs = opened.globalMetrics.globalStartUs;
      processed.globalEndUs = opened.globalMetrics.globalEndUs;
      processed.maxTickDurationUs = opened.globalMetrics.maxTickDurationUs;
      processed.maxSystemDurationUs = opened.globalMetrics.maxSystemDurationUs;
      processed.p95TickDurationUs = opened.globalMetrics.p95TickDurationUs;
      // Full GC-suspension list — stable for the trace's lifetime, NOT derived from per-chunk decoding. Lets the GC renderer compute
      // a yMax that doesn't fluctuate as chunks get loaded/evicted. Server wire-format omits tickNumber (unused downstream); we fill
      // it with 0 to satisfy the GcSuspensionEvent shape without a second round-trip.
      processed.gcSuspensions = (opened.gcSuspensions ?? []).map(s => ({
        tickNumber: 0,
        startUs: s.startUs,
        durationUs: s.durationUs,
        threadSlot: s.threadSlot,
      }));

      // OPFS-backed persistent chunk store keyed by source fingerprint. init() is idempotent; it sets up (or reuses) a subdirectory
      // under typhon-chunks/{fingerprint}/. On subsequent opens of the same trace, chunks fetched in a prior session are read from
      // OPFS instead of round-tripping the server.
      const opfsStore = new OpfsChunkStore();
      await opfsStore.init(opened.fingerprint);
      if (isStaleEpoch()) return;

      // Initialize chunk cache and associated refs. Replaced on every new file open so stale chunks from a previous trace don't linger.
      // All ref writes and the setLoaded are guarded by the epoch check above — if another handler claimed a later epoch while we
      // were awaiting, we return BEFORE touching any shared state, so the other handler's refs and trace stay authoritative.
      chunkCacheRef.current = createChunkCache(undefined, opfsStore);
      chunkManifestRef.current = opened.chunkManifest;
      metadataRef.current = result.metadata;

      const traceFile = files.find(f => f.name.endsWith('.typhon-trace'));
      setLoaded({
        trace: processed,
        tracePath: result.path,
        fileName: traceFile?.name ?? files[0].name,
      });
      // Clear ALL selection refs on trace load, not just chunks. Span/marker refs would otherwise keep pointing at objects
      // from the previous trace — the detail pane would render fields from a trace that's no longer loaded, and the next
      // click anywhere would appear to "unselect" phantom state. Three nulls mirror the three selection kinds.
      setSelectedChunk(null);
      setSelectedSpan(null);
      setSelectedMarker(null);

      // Initial viewport: first tick's absolute range from the summary. Setting this triggers the viewport effect, which fetches the first
      // chunk and populates trace.ticks.
      if (opened.tickSummaries.length > 0) {
        const first = opened.tickSummaries[0];
        const initialRange = { startUs: first.startUs, endUs: first.startUs + first.durationUs };
        setViewRange(initialRange);
        navHistory.push(initialRange);
      }
    } catch (e: unknown) {
      if (!isStaleEpoch()) setError(e instanceof Error ? e.message : 'Failed to load trace');
    } finally {
      // Only toggle the global loading/build-progress UI if we're still the current handler. Otherwise a stale handler's
      // finally would set loading=false and clobber a fresh handler that has already set loading=true.
      if (!isStaleEpoch()) {
        setLoading(false);
        setBuildProgress(null);
      }
    }
  }, []);

  /**
   * Open a trace by server-side filesystem path — skips the upload-to-TEMP step that handleFileSelected uses. The sidecar cache file
   * therefore lands alongside the source .typhon-trace, not in the user's TEMP folder, and nothing needs to be cleaned up later.
   * Metadata (header / systems / archetypes / componentTypes) is pulled directly from the /api/trace/open response — no separate
   * /api/trace/metadata call needed because the open response already carries everything.
   */
  const handleOpenByPath = useCallback(async (path: string) => {
    if (liveRef.current) {
      liveRef.current.close();
      liveRef.current = null;
      setIsLive(false);
    }

    // Epoch claim — mirror the pattern in handleFileSelected. See the detailed comment there.
    const myEpoch = ++loadEpochRef.current;
    const isStaleEpoch = () => loadEpochRef.current !== myEpoch;

    setLoading(true);
    setError(null);
    setBuildProgress(null);
    try {
      await withTimeout(
        subscribeBuildProgress(path, setBuildProgress),
        BUILD_PROGRESS_TIMEOUT_MS,
        'Build progress feed timed out — the server may be stuck.',
      );
      if (isStaleEpoch()) return;
      setBuildProgress(null);
      const opened = await openTrace(path);
      if (isStaleEpoch()) return;

      const metadata: TraceMetadata = {
        header: opened.header,
        systems: opened.systems,
        archetypes: opened.archetypes,
        componentTypes: opened.componentTypes,
      };

      const processed = processTrace(metadata, []);
      processed.summary = opened.tickSummaries;
      processed.globalStartUs = opened.globalMetrics.globalStartUs;
      processed.globalEndUs = opened.globalMetrics.globalEndUs;
      processed.maxTickDurationUs = opened.globalMetrics.maxTickDurationUs;
      processed.maxSystemDurationUs = opened.globalMetrics.maxSystemDurationUs;
      processed.p95TickDurationUs = opened.globalMetrics.p95TickDurationUs;
      // See identical block in handleFileSelected — full GC-suspension list at open time for stable GC chart yMax across chunk churn.
      processed.gcSuspensions = (opened.gcSuspensions ?? []).map(s => ({
        tickNumber: 0,
        startUs: s.startUs,
        durationUs: s.durationUs,
        threadSlot: s.threadSlot,
      }));

      const opfsStore = new OpfsChunkStore();
      await opfsStore.init(opened.fingerprint);
      if (isStaleEpoch()) return;
      chunkCacheRef.current = createChunkCache(undefined, opfsStore);
      chunkManifestRef.current = opened.chunkManifest;
      metadataRef.current = metadata;

      // Derive display name from the path — last segment after forward OR backward slash (Windows-friendly).
      const fileName = path.split(/[\\/]/).pop() ?? path;

      setLoaded({
        trace: processed,
        tracePath: path,
        fileName,
      });
      // Clear ALL selection refs on trace load — see parallel comment in handleFileSelected.
      setSelectedChunk(null);
      setSelectedSpan(null);
      setSelectedMarker(null);

      if (opened.tickSummaries.length > 0) {
        const first = opened.tickSummaries[0];
        const initialRange = { startUs: first.startUs, endUs: first.startUs + first.durationUs };
        setViewRange(initialRange);
        navHistory.push(initialRange);
      }
    } catch (e: unknown) {
      if (!isStaleEpoch()) setError(e instanceof Error ? e.message : 'Failed to open trace');
    } finally {
      // See handleFileSelected's finally for rationale — guard the global loading/progress toggles against a stale handler.
      if (!isStaleEpoch()) {
        setLoading(false);
        setBuildProgress(null);
      }
    }
  }, []);

  // Viewport effect — fires whenever viewRange changes. Maps µs range → tick range via summary, calls ensureRangeLoaded (with prefetch +
  // AbortController cancellation), then rebuilds trace.ticks from the cache. Rapid pan/zoom cancels in-flight chunk fetches at the browser
  // fetch layer so the server doesn't keep serving chunks the viewport has already passed.
  useEffect(() => {
    const path = loaded.tracePath;
    const cache = chunkCacheRef.current;
    const manifest = chunkManifestRef.current;
    const metadata = metadataRef.current;
    const summary = loaded.trace?.summary;
    if (!path || !cache || !manifest || !metadata || !summary) return;
    if (viewRange.endUs <= viewRange.startUs) return;

    const tickRange = viewRangeToTickRange(summary, viewRange.startUs, viewRange.endUs);
    if (!tickRange) return;

    // ── Velocity-aware prefetch ──
    // Compare the new tick range to the previous one (if recent enough to be the same "gesture"). When the leading edge is
    // moving fast in one direction, bias prefetch toward that direction so rapid wheel/pan lands on already-loaded chunks
    // instead of round-tripping the server for each stop. Stationary / first viewport → symmetric default on both sides.
    const now = performance.now();
    const prev = lastViewportShiftRef.current;
    const GESTURE_WINDOW_MS = 400;       // shifts older than this are considered a new gesture — velocity resets
    const FAST_TICKS_PER_SEC = 8;        // above this, switch to asymmetric prefetch
    const LEADING_CHUNKS = 5;             // prefetch horizon on the direction of travel when fast
    const TRAILING_CHUNKS = 1;            // trailing edge shrinks to 1 — just enough to cushion the user backing up
    let prefetchBefore = DEFAULT_PREFETCH_CHUNKS;
    let prefetchAfter = DEFAULT_PREFETCH_CHUNKS;
    if (prev && now - prev.at < GESTURE_WINDOW_MS) {
      // Directional velocity in ticks/sec based on leading-edge shift. fromTick-delta captures "pan" motion; toTick-delta would
      // conflate pan with zoom-out. Leading edge = toTick when moving forward, fromTick when moving backward — using fromTick
      // here captures both cases consistently (a backward pan moves fromTick left, a forward pan moves it right).
      const deltaTicks = tickRange.fromTick - prev.fromTick;
      const dtSec = Math.max(0.02, (now - prev.at) / 1000);
      const ticksPerSec = deltaTicks / dtSec;
      if (Math.abs(ticksPerSec) >= FAST_TICKS_PER_SEC) {
        if (ticksPerSec > 0) { prefetchBefore = TRAILING_CHUNKS; prefetchAfter = LEADING_CHUNKS; }
        else                 { prefetchBefore = LEADING_CHUNKS;  prefetchAfter = TRAILING_CHUNKS; }
      }
    }
    lastViewportShiftRef.current = { fromTick: tickRange.fromTick, toTick: tickRange.toTick, at: now };

    const controller = new AbortController();
    setIsLoadingChunks(true);
    ensureRangeLoaded(cache, path, metadata, manifest, tickRange.fromTick, tickRange.toTick, prefetchBefore, prefetchAfter, controller.signal)
      .then(() => {
        if (controller.signal.aborted) return;
        // Single traversal of the cache entries for tick arrays + gauge aggregates — halves the per-chunk-load sort + iteration cost.
        const { tickData: newTicks, tickNumbers: newTickNumbers, gaugeSeries, gaugeCapacities, memoryAllocEvents, gcEvents, gcSuspensions, threadNames } =
          assembleTickViewAndNumbers(cache, metadata.systems);

        // Merge thread names into metadata. threadNames accumulates monotonically: a ThreadInfo record for slot N only arrives once
        // (at slot claim), so re-running aggregation over a superset of cached ticks never SHRINKS the map. Keep the existing entries
        // on metadata (they may come from earlier chunks no longer in the LRU window) and union in the freshly-aggregated names.
        if (threadNames.size > 0 && metadata) {
          const merged: Record<number, string> = { ...(metadata.threadNames ?? {}) };
          for (const [slot, name] of threadNames) {
            merged[slot] = name;
          }
          metadata.threadNames = merged;
        }

        setLoaded(prev => {
          if (!prev.trace) return prev;
          return {
            ...prev,
            trace: {
              ...prev.trace,
              ticks: newTicks,
              tickNumbers: newTickNumbers,
              gaugeSeries,
              gaugeCapacities,
              memoryAllocEvents,
              gcEvents,
              // gcSuspensions intentionally NOT overwritten here — it's seeded from the /open response with the FULL list (not derived from
              // the resident chunks) and must remain stable across chunk load/evict cycles. Overwriting with the per-chunk-aggregated list
              // would recreate the yMax-rescaling bug the server-side full-list change was meant to fix.
              // Re-reference metadata so the render sees the updated threadNames. Shallow clone — only the threadNames field changed.
              metadata: metadata ? { ...metadata } : prev.trace.metadata,
            },
          };
        });
      })
      .catch(err => {
        if (controller.signal.aborted) return;
        // AbortError is never-a-real-error — swallow. Other failures surface to the error banner.
        if (err && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError') return;
        setError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsLoadingChunks(false);
      });

    return () => {
      controller.abort();
    };
  }, [viewRange, loaded.tracePath]);

  // Live mode: flush buffered ticks into trace state (called at ~10Hz)
  const flushTickBuffer = useCallback(() => {
    const buffer = tickBufferRef.current;
    if (buffer.length === 0) return;

    // Grab all buffered ticks and clear the buffer
    const ticks = buffer.splice(0, buffer.length);

    let current: ProcessedTrace | null = traceRef.current;
    if (!current) return;

    for (const { tickNumber, events } of ticks) {
      if (events.length > 0) {
        current = processTickAndAppend(current, tickNumber, events);
      }
    }

    if (!current) return;
    // Functional update preserves tracePath/fileName (live mode leaves both null; file mode keeps whatever was loaded).
    const captured = current;
    setLoaded(prev => ({ ...prev, trace: captured }));
    traceRef.current = current;

    // If the selected chunk's tick has been evicted from the ring buffer,
    // clear it so the detail pane doesn't keep rendering stale data.
    setSelectedChunk(prev => {
      if (prev == null) return prev;
      if (current && prev.startUs < current.globalStartUs) {
        return null;
      }
      return prev;
    });

    // Auto-scroll to the latest tick when in follow mode. Manual interaction disables follow
    // via handleViewRangeChange / onChunkSelect — this callback just reads the flag.
    if (followRef.current && current.ticks.length > 0) {
      const latest = current.ticks[current.ticks.length - 1];
      const showCount = Math.min(3, current.ticks.length);
      const firstShown = current.ticks[current.ticks.length - showCount];
      setViewRange({ startUs: firstShown.startUs, endUs: latest.endUs });
    }
  }, []);

  // Live connect handler
  const handleLiveConnect = useCallback(() => {
    setError(null);
    setLoading(true);

    followRef.current = true;

    const es = connectLive({
      onMetadata: (metadata) => {
        const empty = createEmptyTrace(metadata);
        setLoaded({ trace: empty, tracePath: null, fileName: null });
        traceRef.current = empty;
        setSelectedChunk(null);
        setIsLive(true);
        setLoading(false);
        setViewRange({ startUs: 0, endUs: 0 });
      },
      onTick: (tickNumber, events) => {
        tickBufferRef.current.push({ tickNumber, events });
      },
      onDisconnect: () => {
        setIsLive(false);
        setLoading(false);
        if (flushIntervalRef.current) {
          clearInterval(flushIntervalRef.current);
          flushIntervalRef.current = 0;
        }
        // Flush any remaining buffered ticks
        flushTickBuffer();
      },
      onError: (msg) => {
        setError(msg);
      },
    });

    liveRef.current = es;

    // Set up 100ms flush interval (10Hz state updates)
    flushIntervalRef.current = window.setInterval(flushTickBuffer, 100);
  }, [flushTickBuffer]);

  // Live disconnect handler
  const handleLiveDisconnect = useCallback(() => {
    if (liveRef.current) {
      liveRef.current.close();
      liveRef.current = null;
    }
    if (flushIntervalRef.current) {
      clearInterval(flushIntervalRef.current);
      flushIntervalRef.current = 0;
    }
    setIsLive(false);
    followRef.current = false;
    // Flush remaining
    flushTickBuffer();
  }, [flushTickBuffer]);

  // Debounced nav push: any view range change resets a 500ms timer. When the timer fires (user stopped interacting),
  // the current range is pushed onto the nav stack. This centralizes recording so ALL sources of view range changes
  // (wheel, pan, zoom-to-selection, tick selection, live mode) are captured without per-handler wiring.
  // Declared above the unmount-cleanup effect so that effect can clear a pending debounce on teardown.
  const navDebounceRef = useRef(0);

  // Cleanup on unmount. Closes live SSE, stops the flush interval, and cancels any pending nav-debounce timer so a late-firing push doesn't
  // call setState on a dead component (React would log a warning in dev, and in prod the callback would mutate navHistory pointlessly).
  useEffect(() => {
    return () => {
      if (liveRef.current) liveRef.current.close();
      if (flushIntervalRef.current) clearInterval(flushIntervalRef.current);
      if (navDebounceRef.current) clearTimeout(navDebounceRef.current);
    };
  }, []);

  // Error-pill lifecycle. The MenuBar now hosts errors inline (next to the ◀▶ nav buttons). We auto-dismiss on a length-scaled timer so
  // trivial errors ("Disconnected") don't linger and long errors ("Failed to parse chunk… [200 char stack]") stay long enough to read.
  //   Formula: max(5 s, min(15 s, 5 s + length × 40 ms))
  //   ≈ average adult reading speed of 5 chars/sec + a 5 s minimum floor and 15 s ceiling.
  // The × button on the pill bypasses the timer for impatient users.
  const handleDismissError = useCallback(() => setError(null), []);
  useEffect(() => {
    if (!error) return;
    const durationMs = Math.max(5000, Math.min(15000, 5000 + error.length * 40));
    const id = window.setTimeout(() => setError(null), durationMs);
    return () => window.clearTimeout(id);
  }, [error]);

  const handleViewRangeChange = useCallback((range: TimeRange) => {
    followRef.current = false;

    // Clamp the requested range into trace bounds before committing. Without this, rapid shift+wheel or middle-drag pans can push
    // offsetX arbitrarily far outside [0, traceEnd] — at which point no tick overlaps the viewport, some downstream render path
    // (gauge binary searches, pending-chunk overlay, etc.) hits an assumption that "at least one tick is visible" and throws,
    // clearing the whole canvas (the "blank span area including gutter" bug reported on pan-left-past-the-end). Clamping here
    // keeps the viewport window's WIDTH intact and slides its position back into bounds — matches the UX of every timeline
    // viewer (Chrome DevTools, Perfetto, SpeedScope): you hit an invisible "wall" at each end instead of pan-to-emptiness.
    //
    // Semantics:
    //   - Empty trace (no summary) → pass through unchanged (nothing to clamp against).
    //   - Range wider than the whole trace → show the full trace [0, traceEnd].
    //   - Range past left edge (startUs < 0) → snap startUs = 0, keep width.
    //   - Range past right edge (endUs > traceEnd) → snap endUs = traceEnd, keep width.
    const summary = loaded.trace?.summary;
    if (summary && summary.length > 0) {
      const traceStartUs = summary[0].startUs;
      const traceLast = summary[summary.length - 1];
      const traceEndUs = traceLast.startUs + traceLast.durationUs;
      const width = range.endUs - range.startUs;
      if (width > 0) {
        if (width >= traceEndUs - traceStartUs) {
          range = { startUs: traceStartUs, endUs: traceEndUs };
        } else if (range.startUs < traceStartUs) {
          range = { startUs: traceStartUs, endUs: traceStartUs + width };
        } else if (range.endUs > traceEndUs) {
          range = { startUs: traceEndUs - width, endUs: traceEndUs };
        }
      }
    }

    setViewRange(range);
    setSelectedChunk(null);

    // Debounced nav push — suppressed during undo/redo replay (isNavigating flag) so animated transitions don't fork the stack.
    clearTimeout(navDebounceRef.current);
    navDebounceRef.current = window.setTimeout(() => {
      navHistory.push(range);
    }, 500);
  }, [navHistory, loaded.trace]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <MenuBar
        trace={trace}
        fileName={fileName}
        loading={loading}
        isLive={isLive}
        onFileSelected={handleFileSelected}
        onOpenByPath={handleOpenByPath}
        onLiveConnect={handleLiveConnect}
        onLiveDisconnect={handleLiveDisconnect}
        navHistory={navHistory}
        onViewRangeChange={handleViewRangeChange}
        gaugeRegionVisible={gaugeRegionVisible}
        legendsVisible={legendsVisible}
        onToggleGauges={toggleGaugeRegion}
        onToggleLegends={toggleLegends}
        error={error}
        onErrorDismiss={handleDismissError}
        chunksLoading={isLoadingChunks && !loading && !!trace}
      />

      {buildProgress && loading && (
        <BuildProgressOverlay progress={buildProgress} />
      )}

      {trace ? (
        <>
          <TickTimeline
            trace={trace}
            viewRange={viewRange}
            onViewRangeChange={handleViewRangeChange}
            isLive={isLive}
            gutterWidth={gutterWidth}
            legendsVisible={legendsVisible}
          />
          <Workspace
            trace={trace}
            tracePath={tracePath}
            viewRange={viewRange}
            onViewRangeChange={handleViewRangeChange}
            selectedChunk={selectedChunk}
            onChunkSelect={setSelectedChunk}
            selectedSpan={selectedSpan}
            onSpanSelect={setSelectedSpan}
            selectedMarker={selectedMarker}
            onMarkerSelect={setSelectedMarker}
            navHistory={navHistory}
            gaugeRegionVisible={gaugeRegionVisible}
            legendsVisible={legendsVisible}
            onGutterWidthChange={setGutterWidth}
            chunkCacheRef={chunkCacheRef}
          />
        </>
      ) : (
        <div style={{
          flex: 1,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          flexDirection: 'column',
          gap: '12px',
          color: DIM_TEXT,
          fontFamily: 'monospace',
        }}>
          <div style={{ fontSize: '24px', color: SPAN_PALETTE[7] }}>Typhon Profiler</div>
          <div style={{ fontSize: '13px' }}>File &gt; Load to open a .typhon-trace file</div>
          <div style={{ fontSize: '13px' }}>File &gt; Connect Live for real-time streaming</div>
        </div>
      )}
    </div>
  );
}

/**
 * Full-screen overlay shown while the server is building a sidecar cache for a newly-opened trace. Replaces the generic "Loading..." text with
 * concrete progress numbers (% of source bytes scanned, tick count, event count) so the user understands that something's actually happening on
 * large files. Hidden automatically when <see cref="BuildProgress"/> in App clears (build done, error, or cache already fresh).
 */
function BuildProgressOverlay({ progress }: { progress: BuildProgress }) {
  const pct = progress.totalBytes > 0 ? (progress.bytesRead / progress.totalBytes) * 100 : 0;
  const mbRead = (progress.bytesRead / (1024 * 1024)).toFixed(1);
  const mbTotal = (progress.totalBytes / (1024 * 1024)).toFixed(1);
  return (
    <div style={{
      flex: 1,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      flexDirection: 'column',
      gap: '14px',
      color: DIM_TEXT,
      fontFamily: 'monospace',
      background: '#101012',
    }}>
      <div style={{ fontSize: '16px', color: '#a8aab0' }}>Building sidecar cache…</div>
      <div style={{
        width: '320px',
        height: '6px',
        background: '#242428',
        borderRadius: '3px',
        overflow: 'hidden',
      }}>
        <div style={{
          width: `${pct.toFixed(1)}%`,
          height: '100%',
          background: 'linear-gradient(90deg, #6a6b72 0%, #a5a6ac 100%)',
          transition: 'width 120ms ease-out',
        }} />
      </div>
      <div style={{ fontSize: '12px' }}>{pct.toFixed(1)}% · {mbRead} / {mbTotal} MB</div>
      <div style={{ fontSize: '11px', color: '#555' }}>
        {progress.tickCount.toLocaleString()} ticks · {progress.eventCount.toLocaleString()} events
      </div>
      <div style={{ fontSize: '10px', color: '#444', maxWidth: '360px', textAlign: 'center', lineHeight: 1.5 }}>
        First open of this trace builds a .typhon-trace-cache sidecar for fast re-opens. Subsequent opens will skip this step.
      </div>
    </div>
  );
}

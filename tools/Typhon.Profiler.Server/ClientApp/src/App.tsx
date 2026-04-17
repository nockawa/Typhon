import { useState, useCallback, useRef, useEffect } from 'preact/hooks';
import { uploadTrace, openTrace, subscribeBuildProgress, type BuildProgress } from './api';
import { processTrace, createEmptyTrace, processTickAndAppend, type ProcessedTrace, type ChunkSpan, type SpanData } from './traceModel';
import type { TimeRange } from './uiTypes';
import type { TraceEvent, TraceMetadata, ChunkManifestEntry } from './types';
import { MenuBar } from './MenuBar';
import { TickTimeline } from './TickTimeline';
import { Workspace } from './Workspace';
import { DIM_TEXT } from './canvasUtils';
import { connectLive } from './liveSource';
import { useNavHistory } from './useNavHistory';
import {
  createChunkCache,
  ensureRangeLoaded,
  assembleTickViewAndNumbers,
  viewRangeToTickRange,
  DEFAULT_PREFETCH_CHUNKS,
  type ChunkCacheState,
} from './chunkCache';

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
  const navHistory = useNavHistory();

  // Live mode state
  const [isLive, setIsLive] = useState(false);
  const liveRef = useRef<EventSource | null>(null);
  const tickBufferRef = useRef<BufferedTick[]>([]);
  const traceRef = useRef<ProcessedTrace | null>(null);
  const flushIntervalRef = useRef<number>(0);

  // Phase 2b: chunked-file state. Lives in refs — mutated during viewport loads without triggering re-renders on every chunk.
  const chunkCacheRef = useRef<ChunkCacheState | null>(null);
  const chunkManifestRef = useRef<ChunkManifestEntry[] | null>(null);
  const metadataRef = useRef<TraceMetadata | null>(null);
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

    setLoading(true);
    setError(null);
    setBuildProgress(null);
    try {
      const result = await uploadTrace(files);
      // Subscribe to build-progress BEFORE calling open — open blocks until the cache is built, so if we wait to subscribe we'd miss the
      // progress stream entirely. If the cache is already fresh (re-opening a recently-uploaded trace), the build-progress feed sends `done`
      // immediately and resolves without any progress ticks. Errors here surface the same way as open errors.
      await withTimeout(
        subscribeBuildProgress(result.path, setBuildProgress),
        BUILD_PROGRESS_TIMEOUT_MS,
        'Build progress feed timed out — the server may be stuck.',
      );
      setBuildProgress(null);
      // Phase 2b: open returns metadata + summary + chunk manifest. No detail events are loaded here — the viewport effect below handles that
      // lazily based on the visible tick range. First-paint is therefore O(summary size), not O(full trace) — indifferent to trace file size.
      const opened = await openTrace(result.path);

      // Build an empty-but-metadata-rich trace. Ticks populate on demand as the viewport effect fires. globalStart/End/max/p95 come from the
      // server-computed metrics so the timeline can render the full file shape without any detail chunks loaded.
      const processed = processTrace(result.metadata, []);
      processed.summary = opened.tickSummaries;
      processed.globalStartUs = opened.globalMetrics.globalStartUs;
      processed.globalEndUs = opened.globalMetrics.globalEndUs;
      processed.maxTickDurationUs = opened.globalMetrics.maxTickDurationUs;
      processed.maxSystemDurationUs = opened.globalMetrics.maxSystemDurationUs;
      processed.p95TickDurationUs = opened.globalMetrics.p95TickDurationUs;

      // Initialize chunk cache and associated refs. Replaced on every new file open so stale chunks from a previous trace don't linger.
      chunkCacheRef.current = createChunkCache();
      chunkManifestRef.current = opened.chunkManifest;
      metadataRef.current = result.metadata;

      const traceFile = files.find(f => f.name.endsWith('.typhon-trace'));
      setLoaded({
        trace: processed,
        tracePath: result.path,
        fileName: traceFile?.name ?? files[0].name,
      });
      setSelectedChunk(null);

      // Initial viewport: first tick's absolute range from the summary. Setting this triggers the viewport effect, which fetches the first
      // chunk and populates trace.ticks.
      if (opened.tickSummaries.length > 0) {
        const first = opened.tickSummaries[0];
        const initialRange = { startUs: first.startUs, endUs: first.startUs + first.durationUs };
        setViewRange(initialRange);
        navHistory.push(initialRange);
      }
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to load trace');
    } finally {
      setLoading(false);
      setBuildProgress(null);
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

    setLoading(true);
    setError(null);
    setBuildProgress(null);
    try {
      await withTimeout(
        subscribeBuildProgress(path, setBuildProgress),
        BUILD_PROGRESS_TIMEOUT_MS,
        'Build progress feed timed out — the server may be stuck.',
      );
      setBuildProgress(null);
      const opened = await openTrace(path);

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

      chunkCacheRef.current = createChunkCache();
      chunkManifestRef.current = opened.chunkManifest;
      metadataRef.current = metadata;

      // Derive display name from the path — last segment after forward OR backward slash (Windows-friendly).
      const fileName = path.split(/[\\/]/).pop() ?? path;

      setLoaded({
        trace: processed,
        tracePath: path,
        fileName,
      });
      setSelectedChunk(null);

      if (opened.tickSummaries.length > 0) {
        const first = opened.tickSummaries[0];
        const initialRange = { startUs: first.startUs, endUs: first.startUs + first.durationUs };
        setViewRange(initialRange);
        navHistory.push(initialRange);
      }
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to open trace');
    } finally {
      setLoading(false);
      setBuildProgress(null);
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
        // Single traversal of the cache entries for both arrays — halves the per-chunk-load sort + iteration cost.
        const { tickData: newTicks, tickNumbers: newTickNumbers } = assembleTickViewAndNumbers(cache);
        setLoaded(prev => {
          if (!prev.trace) return prev;
          return {
            ...prev,
            trace: { ...prev.trace, ticks: newTicks, tickNumbers: newTickNumbers },
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

  const handleViewRangeChange = useCallback((range: TimeRange) => {
    followRef.current = false;
    setViewRange(range);
    setSelectedChunk(null);

    // Debounced nav push — suppressed during undo/redo replay (isNavigating flag) so animated transitions don't fork the stack.
    clearTimeout(navDebounceRef.current);
    navDebounceRef.current = window.setTimeout(() => {
      navHistory.push(range);
    }, 500);
  }, [navHistory]);

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
      />

      {error && (
        <div style={{
          padding: '6px 16px',
          background: '#5c1a1a',
          color: '#ff6b6b',
          fontSize: '12px',
          fontFamily: 'monospace',
          flexShrink: 0,
        }}>
          {error}
        </div>
      )}

      {isLoadingChunks && !loading && trace && (
        <div style={{
          padding: '4px 16px',
          background: '#1a2a3c',
          color: '#7bb3e0',
          fontSize: '11px',
          fontFamily: 'monospace',
          flexShrink: 0,
          opacity: 0.85,
        }}>
          ⟳ Loading trace detail…
        </div>
      )}

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
            navHistory={navHistory}
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
          <div style={{ fontSize: '24px', color: '#e94560' }}>Typhon Profiler</div>
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
      background: '#0a0e1a',
    }}>
      <div style={{ fontSize: '16px', color: '#7bb3e0' }}>Building sidecar cache…</div>
      <div style={{
        width: '320px',
        height: '6px',
        background: '#1a2540',
        borderRadius: '3px',
        overflow: 'hidden',
      }}>
        <div style={{
          width: `${pct.toFixed(1)}%`,
          height: '100%',
          background: 'linear-gradient(90deg, #4a7ab8 0%, #7bb3e0 100%)',
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

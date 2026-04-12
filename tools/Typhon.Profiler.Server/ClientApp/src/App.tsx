import { useState, useCallback, useRef, useEffect } from 'preact/hooks';
import { uploadTrace, fetchEvents } from './api';
import { processTrace, createEmptyTrace, processTickAndAppend, mergeSpanNames, type ProcessedTrace, type ChunkSpan } from './traceModel';
import type { TimeRange } from './uiTypes';
import type { TraceEvent } from './types';
import { MenuBar } from './MenuBar';
import { TickTimeline } from './TickTimeline';
import { Workspace } from './Workspace';
import { DIM_TEXT } from './canvasUtils';
import { connectLive } from './liveSource';

/** Buffered tick data waiting to be processed into the trace. */
interface BufferedTick {
  tickNumber: number;
  events: TraceEvent[];
  spanNames?: Record<number, string>;
}

export function App() {
  const [trace, setTrace] = useState<ProcessedTrace | null>(null);
  const [tracePath, setTracePath] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [viewRange, setViewRange] = useState<TimeRange>({ startUs: 0, endUs: 0 });
  const [selectedChunk, setSelectedChunk] = useState<ChunkSpan | null>(null);

  // Live mode state
  const [isLive, setIsLive] = useState(false);
  const liveRef = useRef<EventSource | null>(null);
  const tickBufferRef = useRef<BufferedTick[]>([]);
  const traceRef = useRef<ProcessedTrace | null>(null);
  const flushIntervalRef = useRef<number>(0);
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
    try {
      const result = await uploadTrace(files);
      const eventsData = await fetchEvents(result.path);
      const processed = processTrace(result.metadata, eventsData.events, eventsData.spanNames ?? {});
      setTrace(processed);
      setTracePath(result.path);
      const traceFile = files.find(f => f.name.endsWith('.typhon-trace'));
      setFileName(traceFile?.name ?? files[0].name);
      setSelectedChunk(null);
      // Default view: first tick
      if (processed.ticks.length > 0) {
        const first = processed.ticks[0];
        setViewRange({ startUs: first.startUs, endUs: first.endUs });
      }
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to load trace');
    } finally {
      setLoading(false);
    }
  }, []);

  // Live mode: flush buffered ticks into trace state (called at ~10Hz)
  const flushTickBuffer = useCallback(() => {
    const buffer = tickBufferRef.current;
    if (buffer.length === 0) return;

    // Grab all buffered ticks and clear the buffer
    const ticks = buffer.splice(0, buffer.length);

    let current = traceRef.current;
    if (!current) return;

    for (const { tickNumber, events, spanNames } of ticks) {
      // Span names, if present, are merged into the trace ahead of processing the events that
      // may reference them (processTickAndAppend does the merge internally).
      if (events.length > 0) {
        current = processTickAndAppend(current, tickNumber, events, spanNames);
      } else if (spanNames) {
        current = mergeSpanNames(current, spanNames);
      }
    }

    setTrace(current);
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
      onMetadata: (metadata, spanNames) => {
        const empty = createEmptyTrace(metadata);
        const withSpans = spanNames && Object.keys(spanNames).length > 0
          ? mergeSpanNames(empty, spanNames)
          : empty;
        setTrace(withSpans);
        traceRef.current = withSpans;
        setTracePath(null);
        setFileName(null);
        setSelectedChunk(null);
        setIsLive(true);
        setLoading(false);
        setViewRange({ startUs: 0, endUs: 0 });
      },
      onTick: (tickNumber, events, newSpanNames) => {
        tickBufferRef.current.push({ tickNumber, events, spanNames: newSpanNames });
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

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (liveRef.current) liveRef.current.close();
      if (flushIntervalRef.current) clearInterval(flushIntervalRef.current);
    };
  }, []);

  const handleViewRangeChange = useCallback((range: TimeRange) => {
    // Any manual pan/zoom/click that moves the view range cancels follow mode.
    // The user is exploring history — don't yank them back to the latest tick.
    followRef.current = false;
    setViewRange(range);
    setSelectedChunk(null);
  }, []);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <MenuBar
        trace={trace}
        fileName={fileName}
        loading={loading}
        isLive={isLive}
        onFileSelected={handleFileSelected}
        onLiveConnect={handleLiveConnect}
        onLiveDisconnect={handleLiveDisconnect}
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

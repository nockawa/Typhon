import { useRef, useEffect, useCallback, useMemo } from 'preact/hooks';
import type { ProcessedTrace } from './traceModel';
import type { TimeRange } from './uiTypes';
import { setupCanvas, drawTooltip, formatDuration, BG_COLOR, BORDER_COLOR, DIM_TEXT, SELECTED_COLOR } from './canvasUtils';

interface TickTimelineProps {
  trace: ProcessedTrace;
  viewRange: TimeRange;
  onViewRangeChange: (range: TimeRange) => void;
  /** When true, auto-scroll to keep latest ticks visible. */
  isLive?: boolean;
}

const TIMELINE_HEIGHT = 80;
const BAR_COLOR = '#4ecdc4';
const BAR_OVER_P95_COLOR = '#e94560';
const OVERLAY_COLOR = 'rgba(255, 165, 0, 0.25)';
const OVERLAY_BORDER = 'rgba(255, 165, 0, 0.7)';
const MAX_BAR_WIDTH = 10;

/** Minimal row shape shared between server-supplied summary entries and live-mode TickData. Everything the overview needs to render. */
interface TickRow {
  tickNumber: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  eventCount: number;
}

export function TickTimeline({ trace, viewRange, onViewRangeChange, isLive }: TickTimelineProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  // Prefer the server-supplied per-tick summary (always complete, delivered by /api/trace/open) when available. Fall back to trace.ticks for
  // live-mode (no summary). Split the two derivations into separate memos so the expensive summary-map doesn't rebuild when trace.ticks
  // churns on every chunk load — for a 500K-tick summary, the prior combined memo re-ran the full .map on every load, producing tens of MB
  // of transient allocations. With the split, the summary-derived rows are allocated exactly ONCE per trace open and reused forever.
  const summaryRows = useMemo<TickRow[] | null>(() => {
    if (!trace.summary || trace.summary.length === 0) return null;
    return trace.summary.map(s => ({
      tickNumber: s.tickNumber,
      startUs: s.startUs,
      endUs: s.startUs + s.durationUs,
      durationUs: s.durationUs,
      eventCount: s.eventCount,
    }));
  }, [trace.summary]);

  const liveRows = useMemo<TickRow[]>(() => {
    return trace.ticks.map(t => ({
      tickNumber: t.tickNumber,
      startUs: t.startUs,
      endUs: t.endUs,
      durationUs: t.durationUs,
      eventCount: 0,
    }));
  }, [trace.ticks]);

  const tickRows = summaryRows ?? liveRows;

  const scrollRangeRef = useRef({ startIdx: 0, endIdx: tickRows.length });
  const hoverRef = useRef<{ tickIdx: number; x: number; y: number } | null>(null);
  const rafRef = useRef(0);

  useEffect(() => {
    if (isLive) {
      // In live mode, show a sliding window of the latest ticks
      const maxVisible = 200;
      const endIdx = tickRows.length;
      const startIdx = Math.max(0, endIdx - maxVisible);
      scrollRangeRef.current = { startIdx, endIdx };
    } else {
      scrollRangeRef.current = { startIdx: 0, endIdx: tickRows.length };
    }
  }, [tickRows, isLive]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const { width, height } = setupCanvas(canvas);
    const ticks = tickRows;
    const p95 = trace.p95TickDurationUs || 1;
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0) return;

    ctx.fillStyle = BG_COLOR;
    ctx.fillRect(0, 0, width, height);

    ctx.strokeStyle = BORDER_COLOR;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, height - 0.5);
    ctx.lineTo(width, height - 0.5);
    ctx.stroke();

    const barAreaHeight = height - 18;
    const barAreaTop = 2;

    // P95 reference line
    ctx.strokeStyle = '#333';
    ctx.lineWidth = 0.5;
    ctx.setLineDash([4, 4]);
    ctx.beginPath();
    ctx.moveTo(0, barAreaTop);
    ctx.lineTo(width, barAreaTop);
    ctx.stroke();
    ctx.setLineDash([]);

    ctx.fillStyle = DIM_TEXT;
    ctx.font = '8px monospace';
    ctx.textAlign = 'left';
    ctx.fillText(`P95: ${formatDuration(p95)}`, 4, barAreaTop + 8);

    const barWidth = Math.min(width / visibleCount, MAX_BAR_WIDTH);
    const barsOffsetX = 0;

    // Draw bars. A minimum 1 px floor on bar height keeps very short ticks visible (e.g., a fast ForceCheckpoint might be 0.1 ms against a p95
    // of 80 ms, which is ratio ~0.001 → sub-pixel bar; without the floor the tick looks empty).
    for (let i = sr.startIdx; i < sr.endIdx; i++) {
      const tick = ticks[i];
      const ratio = Math.min(tick.durationUs / p95, 1.0);
      const barH = Math.max(1, ratio * barAreaHeight);
      const x = barsOffsetX + (i - sr.startIdx) * barWidth;
      const y = barAreaTop + barAreaHeight - barH;

      if (tick.durationUs > p95) {
        ctx.fillStyle = BAR_OVER_P95_COLOR;
      } else {
        ctx.fillStyle = BAR_COLOR;
      }

      ctx.fillRect(x + 0.5, y, Math.max(barWidth - 1, 1), barH);
    }

    // Orange overlay — ticks that fall within viewRange. Strict half-open semantics (endUs > startUs, startUs < endUs) so a neighbouring
    // tick that only TOUCHES the boundary (tick N+1 starts exactly where tick N ends) isn't falsely counted as overlapping. With inclusive
    // comparisons, selecting tick 2 would highlight ticks 1+2+3 because their boundaries kiss.
    let overlayStartX = -1;
    let overlayEndX = -1;
    for (let i = sr.startIdx; i < sr.endIdx; i++) {
      const tick = ticks[i];
      if (tick.endUs > viewRange.startUs && tick.startUs < viewRange.endUs) {
        const x = barsOffsetX + (i - sr.startIdx) * barWidth;
        if (overlayStartX < 0) overlayStartX = x;
        overlayEndX = x + barWidth;
      }
    }

    if (overlayStartX >= 0) {
      ctx.fillStyle = OVERLAY_COLOR;
      ctx.fillRect(overlayStartX, barAreaTop, overlayEndX - overlayStartX, barAreaHeight);
      ctx.strokeStyle = OVERLAY_BORDER;
      ctx.lineWidth = 1.5;
      ctx.strokeRect(overlayStartX, barAreaTop, overlayEndX - overlayStartX, barAreaHeight);
    }

    // Tick number labels
    ctx.fillStyle = DIM_TEXT;
    ctx.font = '8px monospace';
    ctx.textAlign = 'center';
    const labelEvery = Math.max(1, Math.floor(50 / barWidth));
    for (let i = sr.startIdx; i < sr.endIdx; i += labelEvery) {
      const x = barsOffsetX + (i - sr.startIdx) * barWidth + barWidth / 2;
      ctx.fillText(`${ticks[i].tickNumber}`, x, height - 3);
    }

    // Hover
    const hover = hoverRef.current;
    if (hover && hover.tickIdx >= sr.startIdx && hover.tickIdx < sr.endIdx) {
      const x = barsOffsetX + (hover.tickIdx - sr.startIdx) * barWidth;
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 1;
      ctx.strokeRect(x, barAreaTop, barWidth, barAreaHeight);

      const tick = ticks[hover.tickIdx];
      drawTooltip(ctx, hover.x, hover.y, [
        `Tick ${tick.tickNumber}`,
        `Duration: ${formatDuration(tick.durationUs)}`,
        `Events: ${tick.eventCount.toLocaleString()}`,
      ], width, height);
    }
  }, [tickRows, trace.p95TickDurationUs, viewRange]);

  const scheduleRender = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => render());
  }, [render]);

  useEffect(() => {
    scheduleRender();
    const obs = new ResizeObserver(() => scheduleRender());
    if (containerRef.current) obs.observe(containerRef.current);
    return () => { obs.disconnect(); cancelAnimationFrame(rafRef.current); };
  }, [scheduleRender]);

  const hitTestTickIdx = useCallback((e: MouseEvent): number => {
    const canvas = canvasRef.current;
    if (!canvas) return -1;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    const barWidth = Math.min(rect.width / visibleCount, MAX_BAR_WIDTH);
    const barsOffsetX = 0;
    const localX = mx - barsOffsetX;
    if (localX < 0 || localX >= visibleCount * barWidth) return -1;
    return sr.startIdx + Math.floor(localX / barWidth);
  }, []);

  // Click: set viewRange to that tick's [startUs, endUs]
  const onClick = useCallback((e: MouseEvent) => {
    const idx = hitTestTickIdx(e);
    if (idx >= 0 && idx < tickRows.length) {
      const tick = tickRows[idx];
      onViewRangeChange({ startUs: tick.startUs, endUs: tick.endUs });
    }
  }, [tickRows, onViewRangeChange, hitTestTickIdx]);

  // Mousewheel: navigate between ticks. Plain wheel = prev/next tick. Shift+wheel = expand/contract range.
  // Direction is inverted from the browser's raw deltaY so wheel-up = next tick / expand, wheel-down = prev tick / contract. This matches the
  // convention most viewers use for timeline scrubbing (scroll up to go forward in time, like a film reel advancing).
  const onWheel = useCallback((e: WheelEvent) => {
    e.preventDefault();

    const ticks = tickRows;
    if (ticks.length === 0) return;

    // Find current first and last tick indices overlapping the viewRange using binary search — strict half-open overlap (end > start,
    // start < end) so a neighbouring tick that only touches the boundary isn't falsely counted. Previous impl used findIndex + linear walk,
    // which at 500K ticks × 60 Hz wheel events was ~300 ms/sec of CPU just on this lookup.
    // firstIdx = lower-bound on (endUs > viewRange.startUs); lastIdx = upper-bound on (startUs < viewRange.endUs) - 1.
    let lo = 0;
    let hi = ticks.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (ticks[mid].endUs > viewRange.startUs) hi = mid;
      else lo = mid + 1;
    }
    let firstIdx = lo;
    if (firstIdx >= ticks.length || ticks[firstIdx].startUs >= viewRange.endUs) {
      firstIdx = -1;
    }
    let lastIdx = firstIdx;
    if (firstIdx >= 0) {
      lo = firstIdx;
      hi = ticks.length;
      while (lo < hi) {
        const mid = (lo + hi) >>> 1;
        if (ticks[mid].startUs < viewRange.endUs) lo = mid + 1;
        else hi = mid;
      }
      lastIdx = lo - 1;
    }

    if (firstIdx < 0) {
      firstIdx = 0;
      lastIdx = 0;
    }

    // Unified mental model: wheel FORWARD (deltaY < 0) = "advance / add", wheel BACKWARD (deltaY > 0) = "retreat / remove".
    // Plain wheel is inverted from the browser's raw deltaY so forward scrolls to the next tick (not the previous one). Shift+wheel keeps
    // its natural direction: forward expands the selection (adds next tick), backward contracts.
    if (e.shiftKey) {
      // Shift+wheel: forward (up) adds next tick to the right end, backward (down) removes the last tick.
      if (e.deltaY < 0) {
        lastIdx = Math.min(ticks.length - 1, lastIdx + 1);
      } else {
        if (lastIdx > firstIdx) lastIdx--;
      }
    } else {
      // Plain wheel (inverted): forward → next tick, backward → previous tick.
      if (e.deltaY < 0) {
        if (lastIdx < ticks.length - 1) { firstIdx++; lastIdx++; }
      } else {
        if (firstIdx > 0) { firstIdx--; lastIdx--; }
      }
    }

    onViewRangeChange({
      startUs: ticks[firstIdx].startUs,
      endUs: ticks[lastIdx].endUs
    });
  }, [tickRows, viewRange, onViewRangeChange]);

  const onMouseMove = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const idx = hitTestTickIdx(e);

    if (idx >= scrollRangeRef.current.startIdx && idx < scrollRangeRef.current.endIdx && idx < tickRows.length) {
      hoverRef.current = { tickIdx: idx, x: mx, y: my };
    } else {
      hoverRef.current = null;
    }
    scheduleRender();
  }, [tickRows, scheduleRender, hitTestTickIdx]);

  const onMouseLeave = useCallback(() => {
    hoverRef.current = null;
    scheduleRender();
  }, [scheduleRender]);

  return (
    <div
      ref={containerRef}
      style={{ width: '100%', height: `${TIMELINE_HEIGHT}px`, flexShrink: 0, borderBottom: `1px solid ${BORDER_COLOR}` }}
    >
      <canvas
        ref={canvasRef}
        style={{ width: '100%', height: '100%', cursor: 'pointer' }}
        onWheel={onWheel}
        onClick={onClick}
        onMouseMove={onMouseMove}
        onMouseLeave={onMouseLeave}
      />
    </div>
  );
}

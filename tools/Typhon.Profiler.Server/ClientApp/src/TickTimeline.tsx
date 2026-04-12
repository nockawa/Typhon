import { useRef, useEffect, useCallback } from 'preact/hooks';
import type { ProcessedTrace } from './traceModel';
import type { TimeRange } from './uiTypes';
import { setupCanvas, drawTooltip, BG_COLOR, BORDER_COLOR, DIM_TEXT, SELECTED_COLOR } from './canvasUtils';

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

export function TickTimeline({ trace, viewRange, onViewRangeChange, isLive }: TickTimelineProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const scrollRangeRef = useRef({ startIdx: 0, endIdx: trace.ticks.length });
  const hoverRef = useRef<{ tickIdx: number; x: number; y: number } | null>(null);
  const rafRef = useRef(0);

  useEffect(() => {
    if (isLive) {
      // In live mode, show a sliding window of the latest ticks
      const maxVisible = 200;
      const endIdx = trace.ticks.length;
      const startIdx = Math.max(0, endIdx - maxVisible);
      scrollRangeRef.current = { startIdx, endIdx };
    } else {
      scrollRangeRef.current = { startIdx: 0, endIdx: trace.ticks.length };
    }
  }, [trace, isLive]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const { width, height } = setupCanvas(canvas);
    const ticks = trace.ticks;
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
    ctx.fillText(`P95: ${p95.toFixed(0)}us`, 4, barAreaTop + 8);

    const barWidth = width / visibleCount;

    // Draw bars
    for (let i = sr.startIdx; i < sr.endIdx; i++) {
      const tick = ticks[i];
      const ratio = Math.min(tick.durationUs / p95, 1.0);
      const barH = ratio * barAreaHeight;
      const x = (i - sr.startIdx) * barWidth;
      const y = barAreaTop + barAreaHeight - barH;

      if (tick.durationUs > p95) {
        ctx.fillStyle = BAR_OVER_P95_COLOR;
      } else {
        ctx.fillStyle = BAR_COLOR;
      }

      ctx.fillRect(x + 0.5, y, Math.max(barWidth - 1, 1), barH);
    }

    // Orange overlay — ticks that fall within viewRange
    let overlayStartX = -1;
    let overlayEndX = -1;
    for (let i = sr.startIdx; i < sr.endIdx; i++) {
      const tick = ticks[i];
      if (tick.endUs >= viewRange.startUs && tick.startUs <= viewRange.endUs) {
        const x = (i - sr.startIdx) * barWidth;
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
      const x = (i - sr.startIdx) * barWidth + barWidth / 2;
      ctx.fillText(`${ticks[i].tickNumber}`, x, height - 3);
    }

    // Hover
    const hover = hoverRef.current;
    if (hover && hover.tickIdx >= sr.startIdx && hover.tickIdx < sr.endIdx) {
      const x = (hover.tickIdx - sr.startIdx) * barWidth;
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 1;
      ctx.strokeRect(x, barAreaTop, barWidth, barAreaHeight);

      const tick = ticks[hover.tickIdx];
      drawTooltip(ctx, hover.x, hover.y, [
        `Tick ${tick.tickNumber}`,
        `Duration: ${tick.durationUs.toFixed(0)} us`,
        `Chunks: ${tick.chunks.length}`,
        `Skipped: ${tick.skips.length}`,
      ], width, height);
    }
  }, [trace, viewRange]);

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
    const barWidth = rect.width / visibleCount;
    return sr.startIdx + Math.floor(mx / barWidth);
  }, []);

  // Click: set viewRange to that tick's [startUs, endUs]
  const onClick = useCallback((e: MouseEvent) => {
    const idx = hitTestTickIdx(e);
    if (idx >= 0 && idx < trace.ticks.length) {
      const tick = trace.ticks[idx];
      onViewRangeChange({ startUs: tick.startUs, endUs: tick.endUs });
    }
  }, [trace, onViewRangeChange, hitTestTickIdx]);

  // Shift+mousewheel: expand/contract the viewRange by whole ticks
  const onWheel = useCallback((e: WheelEvent) => {
    if (!e.shiftKey) return;
    e.preventDefault();

    const ticks = trace.ticks;
    if (ticks.length === 0) return;

    // Find current first and last tick indices in the viewRange
    let firstIdx = ticks.findIndex(t => t.endUs >= viewRange.startUs && t.startUs <= viewRange.endUs);
    let lastIdx = firstIdx;
    for (let i = firstIdx + 1; i < ticks.length; i++) {
      if (ticks[i].startUs <= viewRange.endUs) {
        lastIdx = i;
      } else {
        break;
      }
    }

    if (firstIdx < 0) {
      firstIdx = 0;
      lastIdx = 0;
    }

    if (e.deltaY < 0) {
      // Scroll up: add one tick to end
      lastIdx = Math.min(ticks.length - 1, lastIdx + 1);
    } else {
      // Scroll down: remove one tick from end (min 1)
      if (lastIdx > firstIdx) {
        lastIdx--;
      }
    }

    onViewRangeChange({
      startUs: ticks[firstIdx].startUs,
      endUs: ticks[lastIdx].endUs
    });
  }, [trace, viewRange, onViewRangeChange]);

  const onMouseMove = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const idx = hitTestTickIdx(e);

    if (idx >= scrollRangeRef.current.startIdx && idx < scrollRangeRef.current.endIdx && idx < trace.ticks.length) {
      hoverRef.current = { tickIdx: idx, x: mx, y: my };
    } else {
      hoverRef.current = null;
    }
    scheduleRender();
  }, [trace, scheduleRender, hitTestTickIdx]);

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

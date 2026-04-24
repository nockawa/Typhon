import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  DRAG_THRESHOLD_PX,
  MIN_BAR_WIDTH,
  TIMELINE_HEIGHT,
  computeSelectionIdxRange,
  drawTickOverview,
  hitTestTick,
  isInHelpHitZone,
  type TickRow,
} from '@/libs/profiler/canvas/tickOverview';
import { formatDuration } from '@/libs/profiler/canvas/canvasUtils';
import { getStudioThemeTokens } from '@/libs/profiler/canvas/theme';
import { HelpOverlay } from '@/panels/profiler/components/HelpOverlay';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useThemeStore } from '@/stores/useThemeStore';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * Tick-overview strip — the thin bar at the top of the Profiler panel. One bar per tick (height ∝ duration,
 * clamped at P95), orange overlay for the ticks inside the main viewport, drag-to-range-select, wheel to pan,
 * Shift+wheel to grow the selection edge.
 *
 * Reads tick data from `useProfilerSessionStore.metadata.tickSummaries` (trace mode — pre-aggregated by the
 * server's cache builder). Attach mode has no tick summaries yet (live aggregation lands later); the strip
 * renders as an empty placeholder until per-tick durations arrive.
 *
 * All state goes through stores:
 *  - `useProfilerViewStore.viewRange` / `legendsVisible` — read + write
 *  - `useProfilerSelectionStore.setSelected({kind:'tick', ...})` — on single-tick click (Phase 2e wires
 *    DetailPanel; for now the store just records the choice)
 */
const OVERVIEW_HELP_LINES: string[] = [
  'Overview timeline',
  '',
  'What\'s drawn:',
  '  One bar per tick, height ∝ tick duration (clamped at the',
  '    P95 reference — dashed line at top). Bars taller than P95',
  '    are drawn in a warning hue.',
  '  Orange overlay = ticks overlapping the current viewport.',
  '  ◀ / ▶ chevrons mean the selection extends past the visible',
  '    window.',
  '',
  'Key + Mouse:',
  '',
  '  Left click',
  '    Click a single tick bar, no drag.',
  '    → Viewport selection jumps to that single tick.',
  '',
  '  Left click + drag',
  '    → On release, viewport selection becomes that range.',
  '',
  '  Middle click + drag',
  '    → Overview window scrolls left/right.',
  '    → Viewport selection is NOT changed.',
  '',
  '  Wheel (no modifier)',
  '    Step viewport selection forward/backward by 1 tick.',
  '',
  '  Shift + Wheel',
  '    Expand/contract the selection\'s right edge by 1.',
  '',
  '  Ctrl + Shift + Wheel',
  '    Same, by 5 ticks — accelerator for wide traces.',
  '',
  '  Ctrl + Wheel',
  '    Pan the overview window ≈10% horizontally.',
];

interface Props {
  /** True when the active session is Attach-mode; toggles live-follow behavior. */
  isLive?: boolean;
}

export default function TickOverview({ isLive = false }: Props) {
  const metadata = useProfilerSessionStore((s) => s.metadata);
  const viewRange = useProfilerViewStore((s) => s.viewRange);
  const setViewRange = useProfilerViewStore((s) => s.setViewRange);
  const legendsVisible = useProfilerViewStore((s) => s.legendsVisible);

  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  // Derive TickRow[] from server metadata. TickSummaryDto fields are typed as `number | string` by orval's
  // regex hint; coerce defensively. Memo on the tickSummaries reference — doesn't re-run per view change.
  const tickRows: TickRow[] = useMemo(() => {
    const summaries = metadata?.tickSummaries;
    if (!summaries || summaries.length === 0) return [];
    return summaries.map((s) => {
      const start = Number(s.startUs);
      const duration = Number(s.durationUs);
      return {
        tickNumber: Number(s.tickNumber),
        startUs: start,
        endUs: start + duration,
        durationUs: duration,
        eventCount: Number(s.eventCount),
      };
    });
  }, [metadata?.tickSummaries]);

  const p95 = Number(metadata?.globalMetrics?.p95TickDurationUs ?? 0);

  // Scroll window — which ticks are visible in the overview (pan state). Separate from viewRange.
  const scrollRangeRef = useRef({ startIdx: 0, endIdx: tickRows.length });
  const hoverRef = useRef<{ tickIdx: number; x: number; y: number } | null>(null);
  const dragRef = useRef<
    | { mode: 'select'; startClientX: number; startTickIdx: number; currentTickIdx: number; moved: boolean }
    | { mode: 'pan'; startClientX: number; startStartIdx: number; moved: boolean }
    | null
  >(null);
  const canvasWidthRef = useRef(0);
  const selectionIdxRef = useRef<{ first: number; last: number }>({ first: -1, last: -1 });
  const rafRef = useRef(0);

  // Help tooltip is a DOM overlay so it can overflow the canvas's 80px height.
  const [helpTooltipPos, setHelpTooltipPos] = useState<{ clientX: number; clientY: number } | null>(null);
  const helpTooltipPosRef = useRef(helpTooltipPos);
  helpTooltipPosRef.current = helpTooltipPos;

  // Hovered-tick tooltip — DOM overlay rendered BELOW the strip (clientY = canvas.bottom) so the
  // tooltip never covers adjacent bars the user might want to read while hovering. HelpOverlay
  // anchors `top: clientY + 14` so passing the canvas bottom puts the tooltip 14 px under the strip.
  const [tickTooltipState, setTickTooltipState] = useState<
    { lines: readonly string[]; clientX: number; clientY: number } | null
  >(null);

  const clampStart = useCallback((startIdx: number, visibleCount: number) => {
    return Math.max(0, Math.min(tickRows.length - visibleCount, startIdx));
  }, [tickRows]);

  // Initial + on-tickRows-change scroll-window setup.
  useEffect(() => {
    if (tickRows.length === 0) {
      scrollRangeRef.current = { startIdx: 0, endIdx: 0 };
      return;
    }
    if (isLive) {
      const maxVisible = 200;
      const endIdx = tickRows.length;
      const startIdx = Math.max(0, endIdx - maxVisible);
      scrollRangeRef.current = { startIdx, endIdx };
    } else {
      const w = canvasWidthRef.current;
      const visible = w > 0 ? Math.max(1, Math.min(tickRows.length, Math.floor(w / MIN_BAR_WIDTH))) : tickRows.length;
      scrollRangeRef.current = { startIdx: 0, endIdx: Math.min(visible, tickRows.length) };
    }
  }, [tickRows, isLive]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    const rect = canvas.getBoundingClientRect();
    canvasWidthRef.current = rect.width;

    // Width-aware self-correction — init effect runs before first layout, so scrollWindow may be larger than the
    // canvas can show. Clamp down to what fits at MIN_BAR_WIDTH.
    if (!isLive && tickRows.length > 0 && rect.width > 0) {
      const maxVisible = Math.max(1, Math.min(tickRows.length, Math.floor(rect.width / MIN_BAR_WIDTH)));
      const sr0 = scrollRangeRef.current;
      const currentVisible = sr0.endIdx - sr0.startIdx;
      if (currentVisible > maxVisible || sr0.endIdx > tickRows.length) {
        const newStart = Math.max(0, Math.min(tickRows.length - maxVisible, sr0.startIdx));
        scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + maxVisible };
      }
    }

    drawTickOverview(canvas, {
      ticks: tickRows,
      viewRange,
      scrollWindow: scrollRangeRef.current,
      selection: selectionIdxRef.current,
      dragPreview: dragRef.current?.mode === 'select'
        ? { startIdx: dragRef.current.startTickIdx, currentIdx: dragRef.current.currentTickIdx, moved: dragRef.current.moved }
        : null,
      hover: hoverRef.current,
      p95TickDurationUs: p95,
      legendsVisible,
      helpHovered: helpTooltipPosRef.current !== null,
    }, getStudioThemeTokens());
  }, [tickRows, viewRange, p95, legendsVisible, isLive]);

  const scheduleRender = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => render());
  }, [render]);

  // Repaint on relevant state changes + resize.
  useEffect(() => {
    scheduleRender();
    const obs = new ResizeObserver(() => scheduleRender());
    if (containerRef.current) obs.observe(containerRef.current);
    return () => { obs.disconnect(); cancelAnimationFrame(rafRef.current); };
  }, [scheduleRender]);

  // Theme toggle (`Alt+Shift+T`) doesn't touch any of the draw-loop deps, so the canvas wouldn't repaint on
  // its own. Subscribe to the theme store and force a redraw — `getStudioThemeTokens()` at the top of each
  // draw then picks up the new CSS variable values.
  const theme = useThemeStore((s) => s.theme);
  useEffect(() => {
    scheduleRender();
  }, [theme, scheduleRender]);

  // When viewRange or tickRows change, recompute the selection-idx range + auto-scroll to keep it visible.
  useEffect(() => {
    selectionIdxRef.current = computeSelectionIdxRange(tickRows, viewRange);
    const sel = selectionIdxRef.current;
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0 || sel.first < 0) {
      scheduleRender();
      return;
    }
    if (sel.first >= sr.startIdx && sel.last < sr.endIdx) {
      scheduleRender();
      return;
    }
    const selMid = Math.floor((sel.first + sel.last) / 2);
    const newStart = clampStart(selMid - Math.floor(visibleCount / 2), visibleCount);
    scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
    scheduleRender();
  }, [viewRange, tickRows, clampStart, scheduleRender]);

  const getCanvasLocal = useCallback((e: { clientX: number; clientY: number }): { mx: number; my: number } | null => {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const rect = canvas.getBoundingClientRect();
    return { mx: e.clientX - rect.left, my: e.clientY - rect.top };
  }, []);

  const hitTest = useCallback((clientX: number): number => {
    const canvas = canvasRef.current;
    if (!canvas) return -1;
    const rect = canvas.getBoundingClientRect();
    return hitTestTick(clientX - rect.left, rect.width, scrollRangeRef.current);
  }, []);

  const panBy = useCallback((deltaTicks: number) => {
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0) return;
    const newStart = clampStart(sr.startIdx + deltaTicks, visibleCount);
    if (newStart === sr.startIdx) return;
    scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
    scheduleRender();
  }, [clampStart, scheduleRender]);

  const applyViewRange = useCallback((r: TimeRange) => {
    setViewRange(r);
  }, [setViewRange]);

  // React attaches wheel handlers as **passive** — `e.preventDefault()` is silently ignored, which lets the
  // browser still fire Ctrl+wheel zoom or page scroll. Attach natively via a `{passive:false}` listener to
  // suppress those defaults. Kept inside a ref so the installer runs once and the latest deps are read live.
  const handleWheelRef = useRef<(e: WheelEvent) => void>(() => {});
  handleWheelRef.current = (e: WheelEvent) => {
    if (tickRows.length === 0) return;
    e.preventDefault();

    if (e.ctrlKey && !e.shiftKey) {
      const sr = scrollRangeRef.current;
      const visibleCount = sr.endIdx - sr.startIdx;
      const step = Math.max(1, Math.floor(visibleCount * 0.1));
      panBy(e.deltaY < 0 ? step : -step);
      return;
    }

    let firstIdx = selectionIdxRef.current.first;
    let lastIdx = selectionIdxRef.current.last;
    if (firstIdx < 0) {
      firstIdx = 0;
      lastIdx = 0;
    }

    if (e.ctrlKey && e.shiftKey) {
      const step = 5;
      if (e.deltaY < 0) lastIdx = Math.min(tickRows.length - 1, lastIdx + step);
      else lastIdx = Math.max(firstIdx, lastIdx - step);
    } else if (e.shiftKey) {
      if (e.deltaY < 0) lastIdx = Math.min(tickRows.length - 1, lastIdx + 1);
      else if (lastIdx > firstIdx) lastIdx--;
    } else {
      if (e.deltaY < 0) {
        if (lastIdx < tickRows.length - 1) { firstIdx++; lastIdx++; }
      } else {
        if (firstIdx > 0) { firstIdx--; lastIdx--; }
      }
    }

    applyViewRange({ startUs: tickRows[firstIdx].startUs, endUs: tickRows[lastIdx].endUs });
  };

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const listener = (e: WheelEvent) => handleWheelRef.current(e);
    canvas.addEventListener('wheel', listener, { passive: false });
    return () => canvas.removeEventListener('wheel', listener);
  }, []);

  const onPointerDown = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const local = getCanvasLocal(e);
    if (!local) return;

    const canvas = canvasRef.current;
    const canvasWidth = canvas?.getBoundingClientRect().width ?? 0;
    if (isInHelpHitZone(local.mx, local.my, canvasWidth, legendsVisible)) {
      e.preventDefault();
      return;
    }

    if (e.button !== 0 && e.button !== 1) return;

    const mode: 'select' | 'pan' = e.button === 0 ? 'select' : 'pan';
    if (mode === 'select') {
      const startIdx = hitTest(e.clientX);
      if (startIdx < 0) return;
      e.preventDefault();
      dragRef.current = {
        mode: 'select',
        startClientX: e.clientX,
        startTickIdx: startIdx,
        currentTickIdx: startIdx,
        moved: false,
      };
    } else {
      e.preventDefault();
      dragRef.current = {
        mode: 'pan',
        startClientX: e.clientX,
        startStartIdx: scrollRangeRef.current.startIdx,
        moved: false,
      };
    }
    // Pointer capture keeps the drag live when the cursor leaves the canvas — pointermove/pointerup continue
    // firing on this element until release.
    try { canvas?.setPointerCapture(e.pointerId); } catch { /* safari private mode */ }
  }, [getCanvasLocal, hitTest, legendsVisible]);

  const onPointerMove = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    const drag = dragRef.current;
    if (drag) {
      const dx = e.clientX - drag.startClientX;
      if (!drag.moved && Math.abs(dx) < DRAG_THRESHOLD_PX) return;
      drag.moved = true;

      if (drag.mode === 'pan') {
        const sr = scrollRangeRef.current;
        const visibleCount = sr.endIdx - sr.startIdx;
        if (visibleCount <= 0) return;
        const barWidth = Math.min(rect.width / visibleCount, 10);
        if (barWidth <= 0) return;
        const deltaIdx = -Math.round(dx / barWidth);
        const newStart = clampStart(drag.startStartIdx + deltaIdx, visibleCount);
        if (newStart !== sr.startIdx) {
          scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
        }
      } else {
        const idx = hitTest(e.clientX);
        if (idx >= 0) drag.currentTickIdx = idx;
      }
      hoverRef.current = null;
      if (helpTooltipPosRef.current !== null) {
        helpTooltipPosRef.current = null;
        setHelpTooltipPos(null);
      }
      if (tickTooltipState !== null) setTickTooltipState(null);
      scheduleRender();
      return;
    }

    if (isInHelpHitZone(mx, my, rect.width, legendsVisible)) {
      hoverRef.current = null;
      if (tickTooltipState !== null) setTickTooltipState(null);
      if (helpTooltipPosRef.current === null) {
        const pos = { clientX: e.clientX, clientY: e.clientY };
        helpTooltipPosRef.current = pos;
        setHelpTooltipPos(pos);
      }
      scheduleRender();
      return;
    }
    if (helpTooltipPosRef.current !== null) {
      helpTooltipPosRef.current = null;
      setHelpTooltipPos(null);
    }

    const idx = hitTest(e.clientX);
    const sr = scrollRangeRef.current;
    if (idx >= sr.startIdx && idx < sr.endIdx && idx < tickRows.length) {
      hoverRef.current = { tickIdx: idx, x: mx, y: my };
      const tick = tickRows[idx];
      setTickTooltipState({
        lines: [
          `Tick ${tick.tickNumber}`,
          `Duration: ${formatDuration(tick.durationUs)}`,
          `Events: ${tick.eventCount.toLocaleString()}`,
        ],
        clientX: e.clientX,
        clientY: rect.bottom,
      });
    } else {
      hoverRef.current = null;
      if (tickTooltipState !== null) setTickTooltipState(null);
    }
    scheduleRender();
  }, [tickRows, hitTest, clampStart, scheduleRender, legendsVisible, tickTooltipState]);

  const onPointerUp = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragRef.current;
    dragRef.current = null;
    try { canvasRef.current?.releasePointerCapture(e.pointerId); } catch { /* noop */ }
    if (!drag) return;

    if (drag.mode === 'select') {
      if (drag.moved) {
        const a = Math.min(drag.startTickIdx, drag.currentTickIdx);
        const b = Math.max(drag.startTickIdx, drag.currentTickIdx);
        if (a >= 0 && b < tickRows.length) {
          applyViewRange({ startUs: tickRows[a].startUs, endUs: tickRows[b].endUs });
        }
      } else {
        const idx = hitTest(e.clientX);
        if (idx >= 0 && idx < tickRows.length) {
          const tick = tickRows[idx];
          applyViewRange({ startUs: tick.startUs, endUs: tick.endUs });
        }
      }
    }
    scheduleRender();
  }, [tickRows, applyViewRange, hitTest, scheduleRender]);

  const onPointerLeave = useCallback(() => {
    // Only clear hover state on leave — in-flight drags are pointer-captured so pointermove still fires.
    if (dragRef.current) return;
    hoverRef.current = null;
    if (helpTooltipPosRef.current !== null) {
      helpTooltipPosRef.current = null;
      setHelpTooltipPos(null);
    }
    if (tickTooltipState !== null) setTickTooltipState(null);
    scheduleRender();
  }, [scheduleRender, tickTooltipState]);

  // Help-tooltip overlay — portaled so dockview's transformed ancestors and pane separators don't
  // misplace or paint over it. See HelpOverlay.tsx for the rationale.
  const helpOverlay = helpTooltipPos !== null ? (
    <HelpOverlay
      lines={OVERVIEW_HELP_LINES}
      clientX={helpTooltipPos.clientX}
      clientY={helpTooltipPos.clientY}
    />
  ) : null;

  // Hovered-tick tooltip — anchored at `clientY = canvas.bottom` with a 2 px gap so the tooltip
  // sits flush against the strip's bottom edge without overlapping any bars.
  const tickOverlay = tickTooltipState !== null ? (
    <HelpOverlay
      lines={tickTooltipState.lines}
      clientX={tickTooltipState.clientX}
      clientY={tickTooltipState.clientY}
      gap={2}
    />
  ) : null;

  if (tickRows.length === 0) {
    return (
      <div
        ref={containerRef}
        className="flex w-full shrink-0 select-none items-center justify-center border-b border-border bg-card text-[11px] text-muted-foreground"
        style={{ height: `${TIMELINE_HEIGHT}px` }}
      >
        {isLive ? 'Live tick overview — aggregation lands in a later phase.' : 'No tick summaries available.'}
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      className="w-full shrink-0 select-none border-b border-border"
      style={{ height: `${TIMELINE_HEIGHT}px` }}
    >
      <canvas
        ref={canvasRef}
        className="h-full w-full cursor-pointer touch-none"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onPointerLeave={onPointerLeave}
      />
      {helpOverlay}
      {tickOverlay}
    </div>
  );
}

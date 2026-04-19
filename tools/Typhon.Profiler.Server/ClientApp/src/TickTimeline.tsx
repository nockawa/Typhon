import { useRef, useEffect, useCallback, useMemo, useState } from 'preact/hooks';
import type { ProcessedTrace } from './traceModel';
import type { TimeRange } from './uiTypes';
import { setupCanvas, drawTooltip, formatDuration, BG_COLOR, BORDER_COLOR, DIM_TEXT, TEXT_COLOR, SELECTED_COLOR, OVERVIEW_PALETTE } from './canvasUtils';

interface TickTimelineProps {
  trace: ProcessedTrace;
  viewRange: TimeRange;
  onViewRangeChange: (range: TimeRange) => void;
  /** When true, auto-scroll to keep latest ticks visible. */
  isLive?: boolean;
  /**
   * Current gutter width used by the sibling <see cref="GraphArea"/>. The overview aligns its help "?" glyph to the gutter's right-edge
   * X (same convention used by GraphArea's per-track "?" glyphs) so the two columns of help affordances line up vertically across the
   * screen. Optional — falls back to 80 px (MIN_GUTTER_WIDTH) until the first GraphArea render measures the real value.
   */
  gutterWidth?: number;
  /**
   * View-menu / 'l' toggle. When false the help "?" glyph is hidden — matches the gating GraphArea uses for its per-track "?" icons so
   * the two stay in lockstep: turning legends off hides ALL help affordances at once. Defaults to true so the glyph is visible by default.
   */
  legendsVisible?: boolean;
}

const TIMELINE_HEIGHT = 80;
// Overview colors — sourced from the dedicated <c>OVERVIEW_PALETTE</c>. Alpha hex suffixes derive the selection fill + border from the
// single palette color: "40" = 64/255 ≈ 25% (fill), "B3" = 179/255 ≈ 70% (border). One hue, two transparencies, zero extra palette slots.
const BAR_COLOR = OVERVIEW_PALETTE.bar;
const BAR_OVER_P95_COLOR = OVERVIEW_PALETTE.overP95;
const OVERLAY_COLOR = OVERVIEW_PALETTE.selection + '40';
const OVERLAY_BORDER = OVERVIEW_PALETTE.selection + 'B3';
const MAX_BAR_WIDTH = 10;
// Per-bar floor so individual ticks stay legible at high tick counts. At 2000 ticks on a typical 1920-px canvas, the unconstrained math yielded
// ~0.96 px per bar — visually a continuous smear. A 4-px floor caps the visible window at floor(width / 4) ticks and lets the user pan to see
// the rest. 4 px (vs. the earlier 3 px) leaves 3 px of solid fill after the <c>barWidth - 1</c> spacing shrink, so bar-height deltas remain
// visually legible even for sub-P95 ticks.
const MIN_BAR_WIDTH = 4;
// Pixel movement between mousedown and mouseup that separates "click" (select this tick) from "drag" (range-select / pan). Matches the
// threshold used by most OS window managers and avoids accidental drag on a jittery click.
const DRAG_THRESHOLD_PX = 3;
// Help glyph sizing — mirrors <see cref="GraphArea"/>: 11 px monospace "?" with right-aligned anchor. Hit-zone padding keeps the tooltip
// forgiving to a 1–2 px mouse aim error while the visual glyph stays at its drawn position.
const HELP_GLYPH_PAD_RIGHT = 7;      // matches GraphArea: x = gutterWidth - GUTTER_PAD_RIGHT + 1 = gutterWidth - 7
const HELP_GLYPH_Y_BASELINE = 14;    // below the P95 dashed line (at y=2) so the glyph sits in the top of the bar area without clashing
const HELP_ICON_HIT_PAD = 3;
const HELP_ICON_GLYPH_WIDTH = 10;    // ~7 px glyph + slop for hit zone

/** Minimal row shape shared between server-supplied summary entries and live-mode TickData. Everything the overview needs to render. */
interface TickRow {
  tickNumber: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  eventCount: number;
}

/**
 * Multi-line help text shown when the user hovers the "?" glyph in the overview corner. Verbose on purpose — the whole point of the
 * tooltip is to replace tribal knowledge about which mouse button / modifier does what.
 *
 * Rendered as a DOM <div> overlay (not canvas) so it can extend well beyond the overview row's 80-px height without being clipped.
 *
 * Layout: one section per input-device family (pointer buttons, wheel), one block per combination. Each block leads with the
 * gesture, then describes the action taken and the effect on the viewer state (viewport selection vs. overview window — two
 * distinct concerns users routinely confuse).
 */
const OVERVIEW_HELP_LINES: string[] = [
  'Overview timeline',
  '',
  'What\'s drawn:',
  '  One bar per tick, height ∝ tick duration (clamped at the',
  '    P95 reference — dashed line at top). Bars taller than P95',
  '    are drawn in a warning hue.',
  '  Orange overlay = ticks overlapping the current viewport',
  '    (the range visible in the main graph).',
  '  ◀ / ▶ chevrons on the edges mean the selection extends',
  '    past the visible window.',
  '',
  'Two distinct concerns:',
  '  Viewport selection → the time range the main graph shows.',
  '    Rendered here as the orange overlay.',
  '  Overview window    → the slice of ticks currently visible',
  '    in this strip. Pan it without changing the viewport.',
  '',
  'Key + Mouse combinations:',
  '',
  '  Left click',
  '    Click a single tick bar, no drag.',
  '    → Viewport selection jumps to that single tick.',
  '',
  '  Left click + drag',
  '    Press-hold on a bar, drag to another bar, release.',
  '    A dashed preview rectangle tracks the dragged range.',
  '    → On release, viewport selection becomes that range.',
  '',
  '  Middle click + drag',
  '    Press-hold the middle button, drag horizontally.',
  '    → Overview window scrolls left/right.',
  '    → Viewport selection is NOT changed.',
  '',
  '  Wheel (no modifier)',
  '    Wheel up   → step viewport selection forward 1 tick.',
  '    Wheel down → step viewport selection back 1 tick.',
  '    The selection\'s width stays the same; both edges move.',
  '',
  '  Shift + Wheel',
  '    Wheel up   → extend the selection\'s right edge by 1.',
  '    Wheel down → pull the right edge back by 1.',
  '    The left edge of the selection stays anchored; only the',
  '    right end grows or shrinks.',
  '',
  '  Ctrl + Shift + Wheel',
  '    Same as Shift + Wheel but by 5 ticks per wheel click.',
  '    Accelerator for wide traces (2000+ ticks) where stepping',
  '    one-at-a-time is impractical.',
  '',
  '  Ctrl + Wheel',
  '    Wheel up   → pan the overview window ≈10% to the right.',
  '    Wheel down → pan the overview window ≈10% to the left.',
  '    → Viewport selection is NOT changed.',
  '',
  'Selection follow:',
  '  When you zoom/pan the main graph, the overview window',
  '  auto-scrolls so the orange overlay stays on-screen. If the',
  '  selection is wider than the window, edge chevrons appear.',
];

export function TickTimeline({ trace, viewRange, onViewRangeChange, isLive, gutterWidth = 80, legendsVisible = true }: TickTimelineProps) {
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
  // Cached canvas CSS width, stamped by render() each frame so the mouse/effect code paths have a recent width to compute visibleCount without
  // forcing another DOM measure on the hot path.
  const canvasWidthRef = useRef(0);
  // Drag state. Two modes:
  //   'select' — initiated by left-click; drag distance determines click-vs-range-select at mouseup. Start + current tick indices drive the
  //              orange preview overlay rendered over the bars.
  //   'pan'    — initiated by middle-click; drag distance translates to a scroll of the visible window. Doesn't touch viewRange.
  // The <c>moved</c> flag differentiates a plain click (no movement, fires the single-tick click path) from a completed drag.
  const dragRef = useRef<
    | { mode: 'select'; startClientX: number; startTickIdx: number; currentTickIdx: number; moved: boolean }
    | { mode: 'pan'; startClientX: number; startStartIdx: number; moved: boolean }
    | null
  >(null);
  // Help tooltip state + ref mirror. State drives the DOM <div> overlay render; ref lets the mouse handlers read the latest value without
  // closure-staleness issues (useCallback deps change → handler reference changes → extra re-renders). The tooltip is positioned via
  // <c>position: fixed</c> in viewport (client) coordinates so it can extend well beyond the 80-px overview row without being clipped by
  // the canvas's bounding box — the original bug the user reported.
  const [helpTooltipPos, setHelpTooltipPos] = useState<{ clientX: number; clientY: number } | null>(null);
  const helpTooltipPosRef = useRef(helpTooltipPos);
  helpTooltipPosRef.current = helpTooltipPos;
  // Cached selection index range (ticks overlapping viewRange). Computed on viewRange change; reused by the auto-follow effect and the edge
  // clip indicators. The <c>-1</c> sentinel signals "no overlap".
  const selectionIdxRef = useRef<{ first: number; last: number }>({ first: -1, last: -1 });

  // Compute min(total, floor(width / MIN_BAR_WIDTH)) clamped ≥ 1. Uses the cached canvas width if available, otherwise falls back to the full
  // tick count (safe on first render, before canvas has been measured).
  const computeMaxVisible = useCallback(() => {
    const w = canvasWidthRef.current;
    if (w <= 0) return tickRows.length;
    return Math.max(1, Math.min(tickRows.length, Math.floor(w / MIN_BAR_WIDTH)));
  }, [tickRows]);

  // Clamp startIdx into [0, total - visibleCount]. Used by every writer that shifts scrollRangeRef (drag, wheel, auto-follow).
  const clampStart = useCallback((startIdx: number, visibleCount: number) => {
    return Math.max(0, Math.min(tickRows.length - visibleCount, startIdx));
  }, [tickRows]);

  // Initial scrollRange setup — on trace open (tickRows identity change), or when switching modes. Offline: show the first window-sized slice
  // starting at 0. Live: slide to the latest.
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
      const visible = computeMaxVisible();
      scrollRangeRef.current = { startIdx: 0, endIdx: visible };
    }
  }, [tickRows, isLive, computeMaxVisible]);

  // Binary-search the [first, last] index range of ticks overlapping viewRange. Strict half-open semantics match the overlap check used for the
  // orange overlay (tick.endUs > startUs AND tick.startUs < endUs) — two neighbouring ticks that merely kiss boundaries never both count as
  // "selected". Returns { first: -1, last: -1 } when no tick overlaps.
  const findSelectionIdxRange = useCallback((vr: TimeRange) => {
    const ticks = tickRows;
    if (ticks.length === 0) return { first: -1, last: -1 };
    let lo = 0;
    let hi = ticks.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (ticks[mid].endUs > vr.startUs) hi = mid;
      else lo = mid + 1;
    }
    const first = lo;
    if (first >= ticks.length || ticks[first].startUs >= vr.endUs) {
      return { first: -1, last: -1 };
    }
    lo = first;
    hi = ticks.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (ticks[mid].startUs < vr.endUs) lo = mid + 1;
      else hi = mid;
    }
    return { first, last: lo - 1 };
  }, [tickRows]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const { width, height } = setupCanvas(canvas);
    canvasWidthRef.current = width;
    const ticks = tickRows;
    const p95 = trace.p95TickDurationUs || 1;

    // Width-aware self-correction. The init useEffect fires BEFORE the first render, so when it runs canvasWidthRef is still 0 and the visible
    // window defaults to the full tick count — which would render as a smear. Clamp here using the now-known width. Also handles canvas resize
    // (narrower canvas → fewer visible ticks). Skipped in live mode because live mode runs its own 200-tick sliding window logic.
    if (!isLive) {
      const maxVisible = Math.max(1, Math.min(ticks.length, Math.floor(width / MIN_BAR_WIDTH)));
      const sr0 = scrollRangeRef.current;
      const currentVisible = sr0.endIdx - sr0.startIdx;
      if (currentVisible > maxVisible || sr0.endIdx > ticks.length) {
        const newStart = Math.max(0, Math.min(ticks.length - maxVisible, sr0.startIdx));
        scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + maxVisible };
      }
    }

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

    // Orange overlay — ticks that fall within viewRange. Previously a linear scan of all visible ticks; now reuses the cached selection-idx
    // range computed by findSelectionIdxRange (binary search) so this is O(1) even at 500K ticks. Clamps the drawn range to the visible window
    // so selections partially off-screen still render the in-view slice.
    const sel = selectionIdxRef.current;
    if (sel.first >= 0) {
      const drawFirst = Math.max(sel.first, sr.startIdx);
      const drawLast = Math.min(sel.last, sr.endIdx - 1);
      if (drawFirst <= drawLast) {
        const overlayStartX = barsOffsetX + (drawFirst - sr.startIdx) * barWidth;
        const overlayEndX = barsOffsetX + (drawLast - sr.startIdx + 1) * barWidth;
        ctx.fillStyle = OVERLAY_COLOR;
        ctx.fillRect(overlayStartX, barAreaTop, overlayEndX - overlayStartX, barAreaHeight);
        ctx.strokeStyle = OVERLAY_BORDER;
        ctx.lineWidth = 1.5;
        ctx.strokeRect(overlayStartX, barAreaTop, overlayEndX - overlayStartX, barAreaHeight);

        // "N frames" caption inside the overlay. Uses the TOTAL selection count (sel.first..sel.last inclusive) even when the overlay
        // is partially clipped off-screen — the user cares "my selection covers N frames" regardless of what's currently visible in
        // this strip. Shown only when the visible portion of the overlay is wide enough to fit the text + a bit of breathing room,
        // otherwise the caption would overflow the overlay and look like it belongs to adjacent bars.
        const totalFrames = sel.last - sel.first + 1;
        const label = totalFrames === 1 ? '1 frame' : `${totalFrames} frames`;
        ctx.font = '10px monospace';
        const textWidth = ctx.measureText(label).width;
        const overlayWidthPx = overlayEndX - overlayStartX;
        if (textWidth + 12 <= overlayWidthPx) {
          ctx.fillStyle = TEXT_COLOR;
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(label, (overlayStartX + overlayEndX) / 2, barAreaTop + barAreaHeight / 2);
          ctx.textBaseline = 'alphabetic';  // restore default so the tick-number label loop below keeps its expected baseline
        }
      }

      // Edge chevrons when selection is clipped off-screen. Happens when the selection spans more ticks than the visible window (clamping then
      // glues one edge but can't show both). Draws a small ◀/▶ triangle in OVERLAY_BORDER color on the clipped side.
      const clippedLeft = sel.first < sr.startIdx;
      const clippedRight = sel.last >= sr.endIdx;
      if (clippedLeft) {
        ctx.fillStyle = OVERLAY_BORDER;
        ctx.beginPath();
        const cy = barAreaTop + barAreaHeight / 2;
        ctx.moveTo(6, cy);
        ctx.lineTo(12, cy - 5);
        ctx.lineTo(12, cy + 5);
        ctx.closePath();
        ctx.fill();
      }
      if (clippedRight) {
        ctx.fillStyle = OVERLAY_BORDER;
        ctx.beginPath();
        const cy = barAreaTop + barAreaHeight / 2;
        ctx.moveTo(width - 6, cy);
        ctx.lineTo(width - 12, cy - 5);
        ctx.lineTo(width - 12, cy + 5);
        ctx.closePath();
        ctx.fill();
      }
    }

    // Tick number labels. Font bumped from 8 → 10 px for legibility at HiDPI; baseline moved up 2 px (height-3 → height-5) so the
    // larger glyphs don't kiss the bottom border. Labels still stop clear of the bar area — 10 px ascent + baseline at height-5
    // puts text top around height-13, which is still below the bars' y=barAreaTop+barAreaHeight=height-16 edge.
    ctx.fillStyle = DIM_TEXT;
    ctx.font = '10px monospace';
    ctx.textAlign = 'center';
    // Bump the min-label-spacing from 50 to 60 px to keep pace with the wider glyphs — prevents neighbour labels from touching at
    // wide barWidths with long tick numbers ("T12345" etc.).
    const labelEvery = Math.max(1, Math.floor(60 / barWidth));
    for (let i = sr.startIdx; i < sr.endIdx; i += labelEvery) {
      const x = barsOffsetX + (i - sr.startIdx) * barWidth + barWidth / 2;
      ctx.fillText(`${ticks[i].tickNumber}`, x, height - 5);
    }

    // Selection drag preview. Only drawn while an in-progress left-drag has exceeded the DRAG_THRESHOLD. Renders as a translucent overlay
    // between the drag-start tick and the current tick — a live preview of the range that will become viewRange at mouseup.
    const drag = dragRef.current;
    if (drag && drag.mode === 'select' && drag.moved) {
      const a = Math.min(drag.startTickIdx, drag.currentTickIdx);
      const b = Math.max(drag.startTickIdx, drag.currentTickIdx);
      const clampedA = Math.max(sr.startIdx, a);
      const clampedB = Math.min(sr.endIdx - 1, b);
      if (clampedA <= clampedB) {
        const x1 = barsOffsetX + (clampedA - sr.startIdx) * barWidth;
        const x2 = barsOffsetX + (clampedB - sr.startIdx + 1) * barWidth;
        // Same color family as the final selection overlay but more translucent — "this is a preview, not committed yet". Uses the "30"
        // alpha suffix (~18%) vs the committed overlay's "40" (~25%) so the two are distinguishable when the preview momentarily overlaps
        // the old selection.
        ctx.fillStyle = OVERVIEW_PALETTE.selection + '30';
        ctx.fillRect(x1, barAreaTop, x2 - x1, barAreaHeight);
        ctx.strokeStyle = OVERLAY_BORDER;
        ctx.setLineDash([4, 3]);
        ctx.lineWidth = 1;
        ctx.strokeRect(x1, barAreaTop, x2 - x1, barAreaHeight);
        ctx.setLineDash([]);

        // Live "N frames" caption — same rule as the committed overlay, but counted from the UNCLAMPED drag range (a..b) so the number
        // keeps climbing as the user drags past the visible window. Gives live feedback of what the selection will become on mouseup.
        const dragFrames = b - a + 1;
        const dragLabel = dragFrames === 1 ? '1 frame' : `${dragFrames} frames`;
        ctx.font = '10px monospace';
        const dragTextWidth = ctx.measureText(dragLabel).width;
        const previewWidthPx = x2 - x1;
        if (dragTextWidth + 12 <= previewWidthPx) {
          ctx.fillStyle = TEXT_COLOR;
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(dragLabel, (x1 + x2) / 2, barAreaTop + barAreaHeight / 2);
          ctx.textBaseline = 'alphabetic';
        }
      }
    }

    // Help "?" glyph — right-aligned at the gutter's right-edge X so it lines up vertically with the per-track "?" glyphs GraphArea draws
    // below. Bright TEXT_COLOR when hovered (confirms interactivity under cursor), DIM_TEXT otherwise (decoration tone). Gated on
    // <c>legendsVisible</c> — matches the GraphArea convention so turning legends off (keyboard 'l' / View menu) hides ALL "?" glyphs
    // across the viewer in one gesture.
    if (legendsVisible) {
      const helpHovered = helpTooltipPosRef.current !== null;
      ctx.fillStyle = helpHovered ? TEXT_COLOR : DIM_TEXT;
      ctx.font = '11px monospace';
      ctx.textAlign = 'right';
      ctx.fillText('?', gutterWidth - HELP_GLYPH_PAD_RIGHT, HELP_GLYPH_Y_BASELINE);
    }

    // Tick-hover tooltip (kept on canvas — it's short and fits in 80 px). The verbose help tooltip is now rendered as a DOM overlay in the
    // JSX below so it can overflow the overview's 80-px height without being clipped by the canvas's bounding box. When help is active we
    // suppress the tick tooltip so the two don't fight for the same visual slot.
    if (helpTooltipPosRef.current === null) {
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
    }
  }, [tickRows, trace.p95TickDurationUs, isLive, gutterWidth, legendsVisible]);

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

  // Auto-follow selection: when viewRange changes (because the user zoomed/panned in the main GraphArea) or tickRows changes (trace open), we
  // recompute the selection's tick-index range and shift scrollRangeRef so the selection stays visible. Clamp semantics:
  //   - Selection fits within visibleCount: center the window on the selection.
  //   - Selection wider than visibleCount: glue window to the nearest edge (left edge if selection extends leftward, right edge if rightward).
  // The clamping never grows visibleCount — if the user zoomed the main view to cover 1000 ticks but the overview only fits 640, we show 640
  // and signal the clipping via edge chevrons.
  useEffect(() => {
    selectionIdxRef.current = findSelectionIdxRange(viewRange);
    const sel = selectionIdxRef.current;
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0 || sel.first < 0) {
      scheduleRender();
      return;
    }
    if (sel.first >= sr.startIdx && sel.last < sr.endIdx) {
      // Already visible — no scroll change needed.
      scheduleRender();
      return;
    }
    // Re-center: put the selection midpoint at the visible-window midpoint, then clamp.
    const selMid = Math.floor((sel.first + sel.last) / 2);
    const newStart = clampStart(selMid - Math.floor(visibleCount / 2), visibleCount);
    scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
    scheduleRender();
  }, [viewRange, tickRows, findSelectionIdxRange, clampStart, scheduleRender]);

  const hitTestTickIdx = useCallback((e: MouseEvent | PointerEvent): number => {
    const canvas = canvasRef.current;
    if (!canvas) return -1;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0) return -1;
    const barWidth = Math.min(rect.width / visibleCount, MAX_BAR_WIDTH);
    const barsOffsetX = 0;
    const localX = mx - barsOffsetX;
    if (localX < 0 || localX >= visibleCount * barWidth) return -1;
    return sr.startIdx + Math.floor(localX / barWidth);
  }, []);

  // Pan startidx by deltaTicks, clamp into valid range, repaint. Used by drag and Ctrl+wheel.
  const panBy = useCallback((deltaTicks: number) => {
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0) return;
    const newStart = clampStart(sr.startIdx + deltaTicks, visibleCount);
    if (newStart === sr.startIdx) return;
    scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
    scheduleRender();
  }, [clampStart, scheduleRender]);

  // True when the given canvas-space (mx, my) falls inside the "?" help glyph's hit zone. Matches the visual glyph position exactly
  // (right-aligned at <c>gutterWidth - HELP_GLYPH_PAD_RIGHT</c>) with <c>HELP_ICON_HIT_PAD</c> slop for mouse-aim forgiveness. Returns
  // false unconditionally when legends are hidden — the glyph isn't drawn, so no hit zone exists.
  const isInHelpHitZone = useCallback((mx: number, my: number) => {
    if (!legendsVisible) return false;
    const glyphRightX = gutterWidth - HELP_GLYPH_PAD_RIGHT;
    const glyphLeftX = glyphRightX - HELP_ICON_GLYPH_WIDTH;
    const glyphTop = HELP_GLYPH_Y_BASELINE - 12;
    const glyphBottom = HELP_GLYPH_Y_BASELINE + 2;
    return mx >= glyphLeftX - HELP_ICON_HIT_PAD
        && mx <= glyphRightX + HELP_ICON_HIT_PAD
        && my >= glyphTop - HELP_ICON_HIT_PAD
        && my <= glyphBottom + HELP_ICON_HIT_PAD;
  }, [gutterWidth, legendsVisible]);

  // Mousewheel gesture table (checked top-down; first match wins):
  //   - Ctrl+Shift+wheel → expand/contract the selection by 5 ticks (accelerator for Shift+wheel, useful at >1000-tick traces).
  //   - Shift+wheel      → expand/contract the selection by 1 tick.
  //   - Ctrl+wheel       → pan the overview window horizontally (step = ~10% of visible count, minimum 1).
  //   - Plain wheel      → step viewRange forward/backward by 1 tick.
  // Note the order: Ctrl+Shift MUST be checked before Shift alone, otherwise the plain-Shift branch swallows the combo.
  // Direction convention: forward wheel (deltaY < 0) = advance/add, matching the film-reel metaphor used elsewhere.
  const onWheel = useCallback((e: WheelEvent) => {
    e.preventDefault();

    const ticks = tickRows;
    if (ticks.length === 0) return;

    if (e.ctrlKey && !e.shiftKey) {
      const sr = scrollRangeRef.current;
      const visibleCount = sr.endIdx - sr.startIdx;
      const step = Math.max(1, Math.floor(visibleCount * 0.1));
      panBy(e.deltaY < 0 ? step : -step);
      return;
    }

    // Use the cached selection range instead of re-running binary search on every wheel tick. Fallback to [0, 0] when there's no selection.
    let firstIdx = selectionIdxRef.current.first;
    let lastIdx = selectionIdxRef.current.last;
    if (firstIdx < 0) {
      firstIdx = 0;
      lastIdx = 0;
    }

    if (e.ctrlKey && e.shiftKey) {
      // Ctrl+Shift+wheel — 5-tick expand/contract. Semantics match Shift+wheel scaled by 5; forward extends the right end, backward
      // retreats it. Clamped so we never push lastIdx past the end of the trace or below firstIdx.
      const step = 5;
      if (e.deltaY < 0) {
        lastIdx = Math.min(ticks.length - 1, lastIdx + step);
      } else {
        lastIdx = Math.max(firstIdx, lastIdx - step);
      }
    } else if (e.shiftKey) {
      if (e.deltaY < 0) {
        lastIdx = Math.min(ticks.length - 1, lastIdx + 1);
      } else {
        if (lastIdx > firstIdx) lastIdx--;
      }
    } else {
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
  }, [tickRows, onViewRangeChange, panBy]);

  // Mousedown — splits by button:
  //   - Left (0)   → start a potential range-select drag. On mouseup, if no drag occurred → single-tick click (existing behavior). If the
  //                  drag exceeded threshold → the range between start and current tick becomes the new viewRange.
  //   - Middle (1) → start a pan drag (viewport-only, doesn't change viewRange).
  //   - Other      → ignored.
  // Click on the "?" help glyph is also ignored (the help tooltip is display-only). We check the help hit zone first so a click right on
  // the glyph doesn't accidentally select a tick behind it.
  const onMouseDown = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    if (isInHelpHitZone(mx, my)) {
      e.preventDefault();
      return;
    }

    if (e.button === 0) {
      const startIdx = hitTestTickIdx(e);
      if (startIdx < 0) return;
      e.preventDefault();
      dragRef.current = {
        mode: 'select',
        startClientX: e.clientX,
        startTickIdx: startIdx,
        currentTickIdx: startIdx,
        moved: false,
      };
    } else if (e.button === 1) {
      e.preventDefault();
      dragRef.current = {
        mode: 'pan',
        startClientX: e.clientX,
        startStartIdx: scrollRangeRef.current.startIdx,
        moved: false,
      };
    }
  }, [hitTestTickIdx, isInHelpHitZone]);

  const onMouseMove = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    // Branch 1 — in-progress drag. Two sub-modes handled separately.
    const drag = dragRef.current;
    if (drag) {
      const dx = e.clientX - drag.startClientX;
      if (!drag.moved && Math.abs(dx) < DRAG_THRESHOLD_PX) return;
      drag.moved = true;

      if (drag.mode === 'pan') {
        // Translate pixel delta to tick-index delta using current barWidth. Drag-right (dx > 0) shows earlier ticks → negative index
        // delta; drag-left shows later ticks. Using <c>startStartIdx + deltaIdx</c> (not incremental) avoids accumulated rounding
        // error over a long drag.
        const sr = scrollRangeRef.current;
        const visibleCount = sr.endIdx - sr.startIdx;
        if (visibleCount <= 0) return;
        const barWidth = Math.min(rect.width / visibleCount, MAX_BAR_WIDTH);
        if (barWidth <= 0) return;
        const deltaIdx = -Math.round(dx / barWidth);
        const newStart = clampStart(drag.startStartIdx + deltaIdx, visibleCount);
        if (newStart !== sr.startIdx) {
          scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
        }
      } else {
        // Select mode — update currentTickIdx so the render loop redraws the preview. Clamp to the visible window so the preview
        // overlay doesn't extend beyond the edges (actual viewRange on mouseup still uses unclamped start/end).
        const idx = hitTestTickIdx(e);
        if (idx >= 0) drag.currentTickIdx = idx;
      }
      hoverRef.current = null;
      if (helpTooltipPosRef.current !== null) {
        helpTooltipPosRef.current = null;
        setHelpTooltipPos(null);
      }
      scheduleRender();
      return;
    }

    // Branch 2 — cursor over the "?" help glyph. Show the help tooltip (as a DOM overlay, not canvas — it needs to overflow the 80-px row)
    // and suppress the per-tick hover so the two tooltips don't clash. Position is anchored on first entry and NOT updated on every sample
    // while the cursor stays inside the zone — keeps the tooltip visually stable rather than tracking every jitter.
    if (isInHelpHitZone(mx, my)) {
      hoverRef.current = null;
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

    // Branch 3 — plain hover over a tick bar.
    const idx = hitTestTickIdx(e);
    if (idx >= scrollRangeRef.current.startIdx && idx < scrollRangeRef.current.endIdx && idx < tickRows.length) {
      hoverRef.current = { tickIdx: idx, x: mx, y: my };
    } else {
      hoverRef.current = null;
    }
    scheduleRender();
  }, [tickRows, scheduleRender, hitTestTickIdx, clampStart, isInHelpHitZone]);

  // Mouseup:
  //   - Pan drag (moved)        → clear, nothing else (viewport already updated live).
  //   - Select drag (moved)     → set viewRange to [startUs(min), endUs(max)] across the dragged tick range.
  //   - Select no-drag (click)  → set viewRange to the single clicked tick (legacy behavior).
  //   - Pan no-drag             → ignored (middle-click with no movement does nothing).
  const onMouseUp = useCallback((e: MouseEvent) => {
    const drag = dragRef.current;
    dragRef.current = null;
    if (!drag) return;

    if (drag.mode === 'select') {
      if (drag.moved) {
        const a = Math.min(drag.startTickIdx, drag.currentTickIdx);
        const b = Math.max(drag.startTickIdx, drag.currentTickIdx);
        if (a >= 0 && b < tickRows.length) {
          onViewRangeChange({ startUs: tickRows[a].startUs, endUs: tickRows[b].endUs });
        }
      } else {
        // No-drag click — single-tick select (existing behavior).
        const idx = hitTestTickIdx(e);
        if (idx >= 0 && idx < tickRows.length) {
          const tick = tickRows[idx];
          onViewRangeChange({ startUs: tick.startUs, endUs: tick.endUs });
        }
      }
    }
    // Pan mouseup needs no viewRange update — the window was already scrolled in place during mousemove.
    scheduleRender();
  }, [tickRows, onViewRangeChange, hitTestTickIdx, scheduleRender]);

  const onMouseLeave = useCallback(() => {
    hoverRef.current = null;
    if (helpTooltipPosRef.current !== null) {
      helpTooltipPosRef.current = null;
      setHelpTooltipPos(null);
    }
    // Cancel any in-progress drag so we don't get stuck in drag mode if the user releases outside the canvas.
    dragRef.current = null;
    scheduleRender();
  }, [scheduleRender]);

  // DOM tooltip overlay. Rendered outside the canvas so it can extend beyond the 80-px overview row without being clipped (canvas draws are
  // bounded by the element's CSS box — there's no "overflow: visible" equivalent for canvas). Positioning uses <c>position: fixed</c> with
  // viewport (client) coordinates so the tooltip escapes any ancestor <c>overflow: hidden</c> containers on the way up. Offset +14 px below
  // the cursor so the tooltip doesn't sit ON the cursor and block its own hit zone (which would start an enter/leave flicker loop).
  // <c>pointer-events: none</c> lets mousemove events pass through the tooltip to the canvas underneath — otherwise the moment the tooltip
  // covers the cursor the canvas stops receiving events, mouseleave never fires, and the tooltip is stuck on-screen.
  let helpOverlay = null;
  if (helpTooltipPos) {
    // Approximate tooltip size — only used for smart edge-flipping; exact layout comes from the DOM. A 60-line × 11-px tooltip at ~16 px
    // line height comes out to ~960 px. Use fixed estimates rather than measuring so we decide placement synchronously on first render.
    const estW = 420;
    const estH = OVERVIEW_HELP_LINES.length * 16 + 16;
    const viewportW = typeof window !== 'undefined' ? window.innerWidth : 1920;
    const viewportH = typeof window !== 'undefined' ? window.innerHeight : 1080;
    let left = helpTooltipPos.clientX + 14;
    let top = helpTooltipPos.clientY + 14;
    // Flip left if we'd overflow the right edge.
    if (left + estW > viewportW - 8) {
      left = Math.max(8, helpTooltipPos.clientX - estW - 14);
    }
    // Flip up if we'd overflow the bottom. For the overview row (top of screen) the tooltip almost always flows downward; flip only when
    // the window is short enough that downward placement would clip.
    if (top + estH > viewportH - 8) {
      top = Math.max(8, viewportH - estH - 8);
    }
    helpOverlay = (
      <div
        style={{
          position: 'fixed',
          left: `${left}px`,
          top: `${top}px`,
          maxWidth: `calc(100vw - 16px)`,
          maxHeight: `calc(100vh - 16px)`,
          background: 'rgba(29, 30, 32, 0.95)',
          border: `1px solid ${BORDER_COLOR}`,
          color: TEXT_COLOR,
          font: '11px monospace',
          lineHeight: '16px',
          padding: '8px 12px',
          whiteSpace: 'pre',
          overflow: 'hidden',
          zIndex: 10000,
          pointerEvents: 'none',
          borderRadius: '2px',
        }}
      >
        {OVERVIEW_HELP_LINES.join('\n')}
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      style={{ width: '100%', height: `${TIMELINE_HEIGHT}px`, flexShrink: 0, borderBottom: `1px solid ${BORDER_COLOR}`, userSelect: 'none' }}
    >
      <canvas
        ref={canvasRef}
        style={{ width: '100%', height: '100%', cursor: 'pointer' }}
        onWheel={onWheel}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={onMouseUp}
        onMouseLeave={onMouseLeave}
      />
      {helpOverlay}
    </div>
  );
}

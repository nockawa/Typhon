import { useRef, useEffect, useCallback, useState, useMemo } from 'preact/hooks';
import type { ProcessedTrace, ChunkSpan, TickData, SpanData } from './traceModel';
import { getTicksInRange } from './traceModel';
import type { Viewport, TrackLayout, TimeRange } from './uiTypes';
import type { NavHistory } from './useNavHistory';
import {
  setupCanvas, computeGridStep, formatRulerLabel, formatDuration, drawTooltip, getSystemColor,
  PHASE_COLOR, BG_COLOR, HEADER_BG, BORDER_COLOR, TEXT_COLOR, DIM_TEXT, GRID_COLOR
} from './canvasUtils';

interface GraphAreaProps {
  trace: ProcessedTrace;
  tracePath: string | null;
  viewRange: TimeRange;
  onViewRangeChange: (range: TimeRange) => void;
  selectedChunk: ChunkSpan | null;
  onChunkSelect: (chunk: ChunkSpan | null) => void;
  selectedSpan: SpanData | null;
  onSpanSelect: (span: SpanData | null) => void;
  navHistory: NavHistory;
}

const GUTTER_WIDTH = 100;
const RULER_HEIGHT = 24;
const TRACK_HEIGHT = 28;
const TRACK_GAP = 2;
const PHASE_TRACK_HEIGHT = 20;
const COLLAPSED_HEIGHT = 4;
const LABEL_ROW_HEIGHT = 18;
const MINI_ROW_HEIGHT = 11;  // 9px text + 1px top margin + 1px bottom — used by Page Cache + Disk IO projection tracks
const MIN_RECT_WIDTH = 1;
const FLAME_ROW_HEIGHT = 14;
const FLAME_TRACK_DEFAULT_HEIGHT = 120;

const TICK_BOUNDARY_COLOR = 'rgba(100, 100, 200, 0.3)';

const COAL_MAX_DEPTH = 8;
const _coalPool = {
  x1: new Float64Array(COAL_MAX_DEPTH),
  x2: new Float64Array(COAL_MAX_DEPTH),
  count: new Int32Array(COAL_MAX_DEPTH),
  sy: new Float64Array(COAL_MAX_DEPTH),
};

function shortenName(name: string): string {
  const parenIdx = name.indexOf('(');
  const base = parenIdx >= 0 ? name.substring(0, parenIdx) : name;
  const parts = base.split('.');
  return parts.length >= 2 ? parts.slice(-2).join('.') : base;
}

const _nameColorCache = new Map<string, string>();
function nameToColor(name: string): string {
  let c = _nameColorCache.get(name);
  if (c !== undefined) return c;
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) - hash + name.charCodeAt(i)) | 0;
  }
  c = `hsl(${(hash & 0xFFFF) % 360}, 50%, 42%)`;
  _nameColorCache.set(name, c);
  return c;
}

// Pending-chunk diagonal-stripe pattern. Built once per context (cached via WeakMap since each canvas has its own 2D context).
// Rendered over tick ranges whose chunks haven't landed yet so rapid panning doesn't leave empty holes — the user sees
// "something's loading here at this exact time range" instead of a blank space.
const _pendingPatternCache = new WeakMap<CanvasRenderingContext2D, CanvasPattern>();
function getPendingPattern(ctx: CanvasRenderingContext2D): CanvasPattern {
  const cached = _pendingPatternCache.get(ctx);
  if (cached) return cached;
  const tile = document.createElement('canvas');
  tile.width = 12; tile.height = 12;
  const tctx = tile.getContext('2d')!;
  tctx.fillStyle = 'rgba(40, 52, 72, 0.55)';
  tctx.fillRect(0, 0, 12, 12);
  tctx.strokeStyle = 'rgba(130, 155, 200, 0.35)';
  tctx.lineWidth = 1.5;
  tctx.beginPath();
  // Two diagonals per tile wrap cleanly across the 12 px edge so the stripe is continuous when the pattern repeats.
  tctx.moveTo(-2, 4); tctx.lineTo(8, -6);
  tctx.moveTo(-2, 16); tctx.lineTo(16, -2);
  tctx.moveTo(4, 16); tctx.lineTo(16, 4);
  tctx.stroke();
  const pat = ctx.createPattern(tile, 'repeat')!;
  _pendingPatternCache.set(ctx, pat);
  return pat;
}

export function GraphArea({ trace, tracePath, viewRange, onViewRangeChange, selectedChunk, onChunkSelect, selectedSpan, onSpanSelect, navHistory }: GraphAreaProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const vpRef = useRef<Viewport>({ offsetX: viewRange.startUs, scaleX: 0.5, scrollY: 0 });
  const hoverRef = useRef<{ x: number; y: number; lines: string[] } | null>(null);
  // Raw mouse X in canvas coordinates. Updated on every mouse move so the crosshair cursor line + timestamp label render
  // continuously, not just when hovering over an interactive element. -1 = cursor outside the canvas/gutter.
  const mouseXRef = useRef(-1);
  const isDraggingRef = useRef(false);
  // 'pan' = middle-drag or shift+left-drag (old viewport-pan behavior). 'select' = left-drag zoom-to-selection.
  const dragModeRef = useRef<'pan' | 'select'>('select');
  const dragStartRef = useRef({ x: 0, y: 0, offsetX: 0, scrollY: 0 });
  // Zoom-to-selection overlay. Two canvas-space X coordinates; the render pass draws the overlay between them.
  const zoomSelRef = useRef<{ x1: number; x2: number } | null>(null);
  // (Nav history recording is centralized in App.tsx's handleViewRangeChange with a 500ms debounce.)
  // Animated zoom transition. When set, the render loop interpolates the viewport from `from` to `to` over 800 ms using
  // an ease-out curve. The animation self-clears when t reaches 1.
  const zoomAnimRef = useRef<{
    from: { startUs: number; endUs: number };
    to: { startUs: number; endUs: number };
    startTime: number;
  } | null>(null);
  const rafRef = useRef(0);
  const [collapseState, setCollapseState] = useState<Record<string, boolean>>({});

  // Data-driven lane discovery. Each observed thread slot gets its own lane, regardless of whether the slot emitted scheduler
  // chunks, OTel spans, or both. A slot might be the main test thread emitting only spans, a thread-pool completion worker
  // emitting only async spans, or a scheduler worker emitting both — all three should show up as their own lane.
  //
  // We track three pieces of info per slot:
  //   - activeSlots: sorted list of all slot indices to render (ordered lane list)
  //   - slotsWithChunks: which of those slots ever emitted a scheduler chunk (drives whether to reserve a chunk row)
  //   - spanMaxDepthBySlot: deepest observed span depth per slot (drives span region height)
  //
  // slotsWithChunks is critical for layout correctness: a slot that NEVER emits chunks shouldn't waste TRACK_HEIGHT pixels at
  // the top of its lane for an empty chunk row, because the empty row visually looks like "depth 0 is blank" and pushes the
  // first real span down to where depth 1 should be. Only chunk-bearing slots reserve the chunk row.
  const { activeSlots, slotsWithChunks, spanMaxDepthBySlot } = useMemo(() => {
    const slotSet = new Set<number>();
    const withChunks = new Set<number>();
    const depthBySlot = new Map<number, number>();
    for (const tick of trace.ticks) {
      for (const chunk of tick.chunks) {
        slotSet.add(chunk.threadSlot);
        withChunks.add(chunk.threadSlot);
      }
      for (const [slot, d] of tick.spanMaxDepthByThreadSlot) {
        slotSet.add(slot);
        const prev = depthBySlot.get(slot) ?? -1;
        if (d > prev) depthBySlot.set(slot, d);
      }
    }
    return {
      activeSlots: Array.from(slotSet).sort((a, b) => a - b),
      slotsWithChunks: withChunks,
      spanMaxDepthBySlot: depthBySlot,
    };
  }, [trace]);
  const SPAN_ROW_HEIGHT = 22;

  // Sync viewport on external viewRange change
  useEffect(() => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const contentWidth = rect.width - GUTTER_WIDTH;
    const rangeUs = viewRange.endUs - viewRange.startUs;
    if (rangeUs > 0 && contentWidth > 0) {
      vp.offsetX = viewRange.startUs;
      vp.scaleX = contentWidth / rangeUs;
    }
    scheduleRender();
  }, [viewRange]);

  const buildLayout = useCallback((): TrackLayout[] => {
    const layout: TrackLayout[] = [];
    let y = 0;

    layout.push({ id: 'ruler', label: 'Time', y, height: RULER_HEIGHT, collapsedHeight: RULER_HEIGHT, collapsed: false, collapsible: false });
    y += RULER_HEIGHT;

    // One lane per observed thread slot. Each lane contains an OPTIONAL chunk row at the top (only when the slot actually has
    // chunks) plus a variable-height span region below it, sized to fit the deepest span captured on that slot. Slots with no
    // spans render as a single chunk row; slots with no chunks AND spans render with their first depth row at the very top of
    // the lane (no empty chunk row). Spans draw starting at (trackY + chunkRowHeight) with per-depth vertical offset, so
    // setting chunkRowHeight to 0 for span-only slots means depth-0 spans land at ty exactly — no visual off-by-one.
    for (const slotIdx of activeSlots) {
      const id = `slot-${slotIdx}`;
      const collapsed = collapseState[id] ?? false;
      const hasChunks = slotsWithChunks.has(slotIdx);
      const slotChunkRowHeight = hasChunks ? TRACK_HEIGHT : 0;
      const slotMaxDepth = spanMaxDepthBySlot.get(slotIdx);
      const spanRegionHeight = slotMaxDepth === undefined ? 0 : (slotMaxDepth + 1) * SPAN_ROW_HEIGHT + 2;
      // If a slot somehow has neither chunks nor spans (shouldn't happen — activeSlots filters against both) fall back to the
      // chunk-row height so the lane has at least some visible body.
      const expandedHeight = Math.max(TRACK_HEIGHT, slotChunkRowHeight + spanRegionHeight);
      const totalExpandedHeight = expandedHeight + TRACK_GAP;
      const h = collapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : totalExpandedHeight;
      layout.push({
        id,
        label: `Slot ${slotIdx}`,
        y,
        height: expandedHeight,
        collapsedHeight: COLLAPSED_HEIGHT,
        collapsed,
        collapsible: true,
        chunkRowHeight: slotChunkRowHeight,
      });
      y += h;
    }

    // Phases track
    const phasesCollapsed = collapseState['phases'] ?? false;
    const ph = phasesCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : PHASE_TRACK_HEIGHT + TRACK_GAP;
    layout.push({ id: 'phases', label: 'Phases', y, height: PHASE_TRACK_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: phasesCollapsed, collapsible: true });
    y += ph;

    // Page Cache track — 4 mini-rows: Fetch, AllocatePage, Evicted, Flush. One row per kind.
    const cacheCollapsed = collapseState['page-cache'] ?? false;
    const CACHE_ROW_COUNT = 4;
    const CACHE_HEIGHT = CACHE_ROW_COUNT * MINI_ROW_HEIGHT;
    const ch = cacheCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : CACHE_HEIGHT + TRACK_GAP;
    layout.push({ id: 'page-cache', label: 'Page Cache', y, height: CACHE_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: cacheCollapsed, collapsible: true });
    y += ch;

    // Disk IO track — 2 mini-rows: Read, Write.
    const diskIoCollapsed = collapseState['disk-io'] ?? false;
    const DISK_IO_ROW_COUNT = 2;
    const DISK_IO_HEIGHT = DISK_IO_ROW_COUNT * MINI_ROW_HEIGHT;
    const dh = diskIoCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : DISK_IO_HEIGHT + TRACK_GAP;
    layout.push({ id: 'disk-io', label: 'Disk IO', y, height: DISK_IO_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: diskIoCollapsed, collapsible: true });
    y += dh;

    // Transactions track — 3 mini-rows: Commits, Rollbacks, Persists.
    const txCollapsed = collapseState['transactions'] ?? false;
    const TX_HEIGHT = 3 * MINI_ROW_HEIGHT;
    const txh = txCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : TX_HEIGHT + TRACK_GAP;
    layout.push({ id: 'transactions', label: 'Transactions', y, height: TX_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: txCollapsed, collapsible: true });
    y += txh;

    // WAL track — 2 mini-rows: Flushes, Waits.
    const walCollapsed = collapseState['wal'] ?? false;
    const WAL_HEIGHT = 2 * MINI_ROW_HEIGHT;
    const walh = walCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : WAL_HEIGHT + TRACK_GAP;
    layout.push({ id: 'wal', label: 'WAL', y, height: WAL_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: walCollapsed, collapsible: true });
    y += walh;

    // Checkpoint track — 1 mini-row: Cycles.
    const cpCollapsed = collapseState['checkpoint'] ?? false;
    const CP_HEIGHT = MINI_ROW_HEIGHT;
    const cph = cpCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : CP_HEIGHT + TRACK_GAP;
    layout.push({ id: 'checkpoint', label: 'Checkpoint', y, height: CP_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: cpCollapsed, collapsible: true });
    y += cph;

    return layout;
  }, [activeSlots, slotsWithChunks, collapseState, spanMaxDepthBySlot]);

  const getVisibleTicks = useCallback((): TickData[] => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return [];
    const rect = canvas.getBoundingClientRect();
    const contentWidth = rect.width - GUTTER_WIDTH;
    return getTicksInRange(trace, vp.offsetX, vp.offsetX + contentWidth / vp.scaleX);
  }, [trace]);

  const render = useCallback(() => {
    const renderStart = performance.now();
    let rectCount = 0;
    let textCount = 0;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d')!;

    // Counting wrappers — intercept fillRect/fillText to count draw calls without touching every call site.
    const origFillRect = ctx.fillRect;
    const origFillText = ctx.fillText;
    ctx.fillRect = (x: number, y: number, w: number, h: number) => { rectCount++; origFillRect.call(ctx, x, y, w, h); };
    ctx.fillText = (text: string, x: number, y: number) => { textCount++; origFillText.call(ctx, text, x, y); };
    const { width, height } = setupCanvas(canvas);
    const vp = vpRef.current;
    const contentWidth = width - GUTTER_WIDTH;

    // ── Animated zoom transition ──
    // When zoomAnimRef is set, interpolate the viewport from `from` to `to` using an ease-out curve over 800 ms.
    // Each frame applies the interpolated range to the viewport and requests the next frame until t >= 1.
    const anim = zoomAnimRef.current;
    if (anim) {
      const elapsed = performance.now() - anim.startTime;
      const duration = 800;
      const rawT = Math.min(elapsed / duration, 1);
      // Ease-out cubic: fast start, smooth deceleration into the target.
      const t = 1 - (1 - rawT) * (1 - rawT) * (1 - rawT);
      const curStart = anim.from.startUs + (anim.to.startUs - anim.from.startUs) * t;
      const curEnd = anim.from.endUs + (anim.to.endUs - anim.from.endUs) * t;
      const curRange = curEnd - curStart;
      vp.offsetX = curStart;
      vp.scaleX = curRange > 0 ? contentWidth / curRange : vp.scaleX;
      if (rawT >= 1) {
        zoomAnimRef.current = null;
        onViewRangeChange({ startUs: anim.to.startUs, endUs: anim.to.endUs });
        navHistory.setNavigating(false);
      } else {
        onViewRangeChange({ startUs: curStart, endUs: curEnd });
        // Schedule the next animation frame. Can't use scheduleRender here (circular dep), so direct rAF.
        cancelAnimationFrame(rafRef.current);
        rafRef.current = requestAnimationFrame(() => render());
      }
    }

    const layout = buildLayout();
    const visibleTicks = getVisibleTicks();

    ctx.fillStyle = BG_COLOR;
    ctx.fillRect(0, 0, width, height);

    if (visibleTicks.length === 0) {
      ctx.fillStyle = DIM_TEXT;
      ctx.font = '14px monospace';
      ctx.textAlign = 'center';
      ctx.fillText('No ticks in view', width / 2, height / 2);
      return;
    }

    const pxOfUs = (us: number) => GUTTER_WIDTH + (us - vp.offsetX) * vp.scaleX;
    const visStartUs = vp.offsetX;
    const visEndUs = vp.offsetX + contentWidth / vp.scaleX;

    // ── Left gutter ──
    ctx.fillStyle = HEADER_BG;
    ctx.fillRect(0, 0, GUTTER_WIDTH, height);
    ctx.strokeStyle = BORDER_COLOR;
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(GUTTER_WIDTH - 0.5, 0); ctx.lineTo(GUTTER_WIDTH - 0.5, height); ctx.stroke();

    for (const track of layout) {
      const ty = track.y - vp.scrollY;
      ctx.fillStyle = DIM_TEXT;
      ctx.font = '10px monospace';
      ctx.textAlign = 'left';
      if (track.collapsible) {
        ctx.fillText(`${track.collapsed ? '\u25B6' : '\u25BC'} ${track.label}`, 6, ty + 12);
      } else {
        ctx.fillText(track.label, 6, ty + 12);
      }
      ctx.strokeStyle = BORDER_COLOR;
      ctx.lineWidth = 0.5;
      const sepY = track.y + (track.collapsed ? LABEL_ROW_HEIGHT + COLLAPSED_HEIGHT : track.height + TRACK_GAP) - vp.scrollY - 0.5;
      ctx.beginPath(); ctx.moveTo(0, sepY); ctx.lineTo(width, sepY); ctx.stroke();
    }

    // ── Content area ──
    ctx.save();
    ctx.beginPath(); ctx.rect(GUTTER_WIDTH, 0, contentWidth, height); ctx.clip();

    for (const track of layout) {
      const ty = track.y - vp.scrollY;

      if (track.id === 'ruler') {
        // ── Absolute anchor at the left edge + relative offset labels ──
        // The leftmost label shows the absolute elapsed time at the viewport's left edge (relative to the first tick's start).
        // All subsequent grid labels show offsets from that anchor: +100ms, +200ms, etc. This way the user reads "left edge
        // is at 8.00s, right edge is at 9.50s, the grid shows +0 / +250ms / +500ms / +750ms / +1.00s / +1.25s / +1.50s"
        // immediately without mental arithmetic.
        const gridStep = computeGridStep(visEndUs - visStartUs, contentWidth, 90);
        const baseUs = visibleTicks[0]?.startUs ?? 0;
        const leftEdgeUs = visStartUs;
        const gridStart = Math.ceil(leftEdgeUs / gridStep) * gridStep;

        // Left-edge absolute label — drawn in the gutter's content zone at the ruler row's Y position.
        ctx.fillStyle = TEXT_COLOR; ctx.font = '9px monospace'; ctx.textAlign = 'left';
        ctx.fillText(formatRulerLabel(leftEdgeUs - baseUs), GUTTER_WIDTH + 4, ty + 16);

        // Grid lines + offset labels
        for (let t = gridStart; t <= visEndUs; t += gridStep) {
          const x = pxOfUs(t);
          if (x < GUTTER_WIDTH + 60) continue;  // don't crowd the absolute anchor label
          ctx.strokeStyle = GRID_COLOR; ctx.lineWidth = 0.5;
          ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke();
          ctx.fillStyle = DIM_TEXT; ctx.font = '9px monospace'; ctx.textAlign = 'center';
          ctx.fillText(`+${formatRulerLabel(t - leftEdgeUs)}`, x, ty + 16);
        }

        // Tick boundaries (dashed lines + tick number labels)
        ctx.setLineDash([3, 3]); ctx.strokeStyle = TICK_BOUNDARY_COLOR; ctx.lineWidth = 1;
        for (const tick of visibleTicks) {
          const x = pxOfUs(tick.startUs);
          if (x >= GUTTER_WIDTH) { ctx.beginPath(); ctx.moveTo(x, RULER_HEIGHT); ctx.lineTo(x, height); ctx.stroke(); }
        }
        ctx.setLineDash([]);
        ctx.fillStyle = '#666'; ctx.font = '8px monospace'; ctx.textAlign = 'left';
        for (const tick of visibleTicks) {
          const x = pxOfUs(tick.startUs);
          if (x >= GUTTER_WIDTH) ctx.fillText(`T${tick.tickNumber}`, x + 2, ty + 8);
        }
        continue;
      }

      if (track.collapsed) {
        ctx.fillStyle = '#222';
        ctx.fillRect(GUTTER_WIDTH, ty + LABEL_ROW_HEIGHT, contentWidth, COLLAPSED_HEIGHT);
        continue;
      }

      // ── Thread-slot lanes ──
      // A slot lane has two vertical regions:
      //   [ty .. ty + chunkRowHeight]       — scheduler chunk bar row
      //   [ty + chunkRowHeight .. trackBottom] — span rows, one per depth, drawn beneath the chunk row
      // Both regions share the same horizontal viewport transform. Spans and chunks on the same slot naturally interleave in
      // time because they share a single thread's timeline.
      if (track.id.startsWith('slot-')) {
        const threadSlot = parseInt(track.id.split('-')[1]);
        const chunkRowHeight = track.chunkRowHeight ?? 0;
        const spanRegionTop = ty + chunkRowHeight;
        const trackBottom = ty + track.height;

        // ── Chunk bar row (top of lane) ── only when this slot actually has chunks to draw. Span-only slots skip the entire
        // chunk pass so there's no phantom empty row before the first span.
        if (chunkRowHeight > 0) {
          for (const tick of visibleTicks) {
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              const x1 = pxOfUs(chunk.startUs);
              const x2 = pxOfUs(chunk.endUs);
              if (x2 < GUTTER_WIDTH || x1 > width) continue;
              const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
              ctx.fillStyle = getSystemColor(chunk.systemIndex);
              ctx.fillRect(x1, ty + 1, w, chunkRowHeight - 2);
              if (selectedChunk && chunk.systemIndex === selectedChunk.systemIndex &&
                  chunk.chunkIndex === selectedChunk.chunkIndex && chunk.threadSlot === selectedChunk.threadSlot &&
                  Math.abs(chunk.startUs - selectedChunk.startUs) < 0.01) {
                ctx.strokeStyle = '#fff'; ctx.lineWidth = 2;
                ctx.strokeRect(x1, ty + 1, w, chunkRowHeight - 2);
              }
              if (w > 40) {
                ctx.save();
                ctx.beginPath();
                ctx.rect(x1, ty + 1, w, chunkRowHeight - 2);
                ctx.clip();
                ctx.fillStyle = '#000'; ctx.font = '10px monospace'; ctx.textAlign = 'left';
                const label = chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName;
                ctx.fillText(label, x1 + 3, ty + chunkRowHeight / 2 + 3);
                ctx.restore();
              }
            }
          }
        }

        // ── Span rows (below chunk bar) ──
        // Each slot carries its own pre-sorted span array + running-max endUs table, so we binary-search the same way the
        // previous unified OTel track did, but per-slot. Rows are laid out by span.depth so the stable depth axis from
        // traceModel.ts is preserved — a given span always renders at a fixed row within its slot lane.
        const viewStartUs = vp.offsetX;
        const viewEndUs = vp.offsetX + contentWidth / vp.scaleX;
        ctx.font = '11px monospace';
        ctx.textAlign = 'left';

        for (const tick of visibleTicks) {
          const slotSpans = tick.spansByThreadSlot.get(threadSlot);
          if (!slotSpans || slotSpans.length === 0) continue;
          const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
          if (!endMax) continue;

          // Binary search for left-edge visibility.
          let lo = 0, hi = slotSpans.length;
          while (lo < hi) {
            const mid = (lo + hi) >>> 1;
            if (endMax[mid] < viewStartUs) lo = mid + 1; else hi = mid;
          }

          // Per-depth coalescing with INDEPENDENT state per depth level. Spans are sorted by startUs, not by depth, so
          // bars at different depths interleave constantly in the iteration order. The old single-depth state machine
          // flushed every time the depth changed — which was every other bar in a typical hierarchy — so runs never
          // reached 2 and coalescing never kicked in. Tracking state per depth independently means depth-0 bars
          // accumulate into a depth-0 run even when depth-1 and depth-2 bars appear between them in the sorted stream.
          //
          // Fixed-size arrays indexed by depth (max 8 levels — anything deeper is vanishingly rare in Typhon traces
          // and falls through to a clamped bucket). No heap allocation per frame.
          const { x1: coalX1, x2: coalX2, count: coalCount, sy: coalSy } = _coalPool;
          coalX1.fill(0); coalX2.fill(0); coalCount.fill(0); coalSy.fill(0);
          let prevFill = '';

          const flushDepth = (d: number) => {
            if (coalCount[d] >= 2) {
              const cw = Math.max(coalX2[d] - coalX1[d], 2);
              const cf = 'rgba(85, 85, 85, 0.5)';
              if (cf !== prevFill) { ctx.fillStyle = cf; prevFill = cf; }
              ctx.fillRect(coalX1[d], coalSy[d], cw, SPAN_ROW_HEIGHT - 2);
              if (cw > 50) {
                ctx.fillStyle = '#aaa'; ctx.font = '9px monospace'; ctx.textAlign = 'left';
                ctx.fillText(`${coalCount[d]} spans — zoom in`, coalX1[d] + 3, coalSy[d] + SPAN_ROW_HEIGHT - 7);
                prevFill = '#aaa';
              }
            }
            coalCount[d] = 0;
          };

          const flushAllDepths = () => {
            for (let d = 0; d < COAL_MAX_DEPTH; d++) flushDepth(d);
          };

          for (let i = lo; i < slotSpans.length; i++) {
            const span = slotSpans[i];
            if (span.startUs > viewEndUs) break;
            const x1 = pxOfUs(span.startUs);
            const x2 = pxOfUs(span.endUs);
            if (x2 < GUTTER_WIDTH) continue;

            const depth = span.depth ?? 0;
            const d = depth < COAL_MAX_DEPTH ? depth : COAL_MAX_DEPTH - 1;
            const sy = spanRegionTop + depth * SPAN_ROW_HEIGHT;
            if (sy + SPAN_ROW_HEIGHT - 2 > trackBottom) continue;

            const actualWidth = x2 - x1;
            const w = actualWidth < MIN_RECT_WIDTH ? MIN_RECT_WIDTH : actualWidth;

            if (actualWidth <= 1) {
              // Try to extend the run at this depth.
              if (coalCount[d] > 0 && x1 <= coalX2[d] + 1) {
                coalCount[d]++;
                if (x2 > coalX2[d]) coalX2[d] = x2;
                if (coalX2[d] < coalX1[d] + 1) coalX2[d] = coalX1[d] + 1;
                continue;
              }
              // Can't extend — flush this depth's old run, draw bar individually, start new run at this depth.
              flushDepth(d);
              { const c = nameToColor(span.name); if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; } }
              ctx.fillRect(x1, sy, w, SPAN_ROW_HEIGHT - 2);
              coalX1[d] = x1;
              coalX2[d] = x1 + w;
              coalSy[d] = sy;
              coalCount[d] = 1;
              continue;
            }

            // Wide bar — flush only THIS depth's pending run, then draw individually.
            flushDepth(d);

            { const c = nameToColor(span.name); if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; } }
            ctx.fillRect(x1, sy, w, SPAN_ROW_HEIGHT - 2);

            if (selectedSpan && selectedSpan.spanId !== undefined && selectedSpan.spanId === span.spanId) {
              ctx.strokeStyle = '#fff';
              ctx.lineWidth = 2;
              ctx.strokeRect(x1, sy, w, SPAN_ROW_HEIGHT - 2);
            }

            if (actualWidth > 10) {
              ctx.save();
              ctx.beginPath();
              ctx.rect(x1, sy, actualWidth, SPAN_ROW_HEIGHT - 2);
              ctx.clip();
              ctx.fillStyle = '#eee';
              ctx.font = '11px monospace';
              ctx.fillText(`${span.name} (${formatDuration(span.durationUs)})`, x1 + 3, sy + SPAN_ROW_HEIGHT - 7);
              ctx.restore();
            }
          }
          flushAllDepths();
        }
      }

      // ── Phases track ──
      if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            const x1 = pxOfUs(phase.startUs);
            const x2 = pxOfUs(phase.endUs);
            if (x2 < GUTTER_WIDTH || x1 > width) continue;
            const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
            ctx.fillStyle = PHASE_COLOR;
            ctx.fillRect(x1, ty + 1, w, PHASE_TRACK_HEIGHT - 2);
            if (w > 50) {
              ctx.save();
              ctx.beginPath();
              ctx.rect(x1, ty + 1, w, PHASE_TRACK_HEIGHT - 2);
              ctx.clip();
              ctx.fillStyle = TEXT_COLOR; ctx.font = '9px monospace'; ctx.textAlign = 'left';
              ctx.fillText(`${phase.phaseName} (${formatDuration(phase.durationUs)})`, x1 + 3, ty + 12);
              ctx.restore();
            }
          }
        }
      }

      // ── Shared helper: draw a projection mini-row with coalescing ──
      // Used by both Page Cache and Disk IO tracks. Each row is MINI_ROW_HEIGHT (11 px) tall.
      if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
        const viewStartUs = vp.offsetX;
        const viewEndUs = vp.offsetX + contentWidth / vp.scaleX;
        const MRH = MINI_ROW_HEIGHT;
        const barH = MRH - 1;

        const drawMiniRow = (ops: SpanData[], endMax: Float64Array, rowY: number, colorFn: (op: SpanData) => string) => {
          if (ops.length === 0) return;
          let lo = 0, hi = ops.length;
          while (lo < hi) {
            const mid = (lo + hi) >>> 1;
            if (endMax[mid] < viewStartUs) lo = mid + 1; else hi = mid;
          }
          let coalX1 = 0, coalX2 = 0, coalCount = 0;
          let mrPrevFill = '';
          const flushCoal = () => {
            if (coalCount >= 2) {
              const cw = Math.max(coalX2 - coalX1, 2);
              const cf = 'rgba(85, 85, 85, 0.5)';
              if (cf !== mrPrevFill) { ctx.fillStyle = cf; mrPrevFill = cf; }
              ctx.fillRect(coalX1, rowY, cw, barH);
            }
            coalCount = 0;
          };
          for (let i = lo; i < ops.length; i++) {
            const op = ops[i];
            if (op.startUs > viewEndUs) break;
            const x1 = pxOfUs(op.startUs);
            const x2 = pxOfUs(op.endUs);
            if (x2 < GUTTER_WIDTH) continue;
            const actualWidth = x2 - x1;
            const w = actualWidth < MIN_RECT_WIDTH ? MIN_RECT_WIDTH : actualWidth;
            if (actualWidth <= 1) {
              if (coalCount > 0 && x1 <= coalX2 + 1) {
                coalCount++;
                if (x2 > coalX2) coalX2 = x2;
                if (coalX2 < coalX1 + 1) coalX2 = coalX1 + 1;
                continue;
              }
              flushCoal();
              { const c = colorFn(op); if (c !== mrPrevFill) { ctx.fillStyle = c; mrPrevFill = c; } }
              ctx.fillRect(x1, rowY, w, barH);
              coalX1 = x1; coalX2 = x1 + w; coalCount = 1;
              continue;
            }
            flushCoal();
            { const c = colorFn(op); if (c !== mrPrevFill) { ctx.fillStyle = c; mrPrevFill = c; } }
            ctx.fillRect(x1, rowY, w, barH);
          }
          flushCoal();
        };

        // Row config per track type.
        type RowDef = { label: string; labelColor: string; barColor: string; getOps: (t: TickData) => SpanData[]; getEndMax: (t: TickData) => Float64Array };
        const rows: RowDef[] =
          track.id === 'page-cache' ? [
            { label: 'Fetch',    labelColor: 'rgba(180, 100, 220, 0.8)', barColor: 'rgba(180, 100, 220, 0.15)', getOps: t => t.cacheFetch,   getEndMax: t => t.cacheFetchEndMax },
            { label: 'Allocate', labelColor: 'rgba(100, 180, 255, 0.8)', barColor: 'rgba(100, 180, 255, 0.15)', getOps: t => t.cacheAlloc,   getEndMax: t => t.cacheAllocEndMax },
            { label: 'Evicted',  labelColor: 'rgba(240, 220, 80, 0.8)',  barColor: 'rgba(240, 220, 80, 0.15)',  getOps: t => t.cacheEvict,   getEndMax: t => t.cacheEvictEndMax },
            { label: 'Flush',    labelColor: 'rgba(230, 160, 60, 0.8)',  barColor: 'rgba(230, 160, 60, 0.15)',  getOps: t => t.cacheFlushes, getEndMax: t => t.cacheFlushesEndMax },
          ] : track.id === 'disk-io' ? [
            { label: 'Reads',  labelColor: 'rgba(80, 200, 120, 0.8)', barColor: 'rgba(80, 200, 120, 0.15)', getOps: t => t.diskReads,  getEndMax: t => t.diskReadsEndMax },
            { label: 'Writes', labelColor: 'rgba(80, 140, 240, 0.8)', barColor: 'rgba(80, 140, 240, 0.15)', getOps: t => t.diskWrites, getEndMax: t => t.diskWritesEndMax },
          ] : track.id === 'transactions' ? [
            { label: 'Commits',   labelColor: 'rgba(80, 200, 120, 0.8)',  barColor: 'rgba(80, 200, 120, 0.15)',  getOps: t => t.txCommits,   getEndMax: t => t.txCommitsEndMax },
            { label: 'Rollbacks', labelColor: 'rgba(240, 80, 80, 0.8)',   barColor: 'rgba(240, 80, 80, 0.15)',   getOps: t => t.txRollbacks, getEndMax: t => t.txRollbacksEndMax },
            { label: 'Persists',  labelColor: 'rgba(180, 160, 220, 0.8)', barColor: 'rgba(180, 160, 220, 0.15)', getOps: t => t.txPersists,  getEndMax: t => t.txPersistsEndMax },
          ] : track.id === 'wal' ? [
            { label: 'Flushes', labelColor: 'rgba(255, 180, 60, 0.8)',  barColor: 'rgba(255, 180, 60, 0.15)',  getOps: t => t.walFlushes, getEndMax: t => t.walFlushesEndMax },
            { label: 'Waits',   labelColor: 'rgba(255, 100, 100, 0.8)', barColor: 'rgba(255, 100, 100, 0.15)', getOps: t => t.walWaits,   getEndMax: t => t.walWaitsEndMax },
          ] : /* checkpoint */ [
            { label: 'Cycles', labelColor: 'rgba(100, 200, 200, 0.8)', barColor: 'rgba(100, 200, 200, 0.15)', getOps: t => t.checkpointCycles, getEndMax: t => t.checkpointCyclesEndMax },
          ];

        ctx.font = '9px monospace'; ctx.textAlign = 'left';
        const labelPad = 3;

        for (let r = 0; r < rows.length; r++) {
          const row = rows[r];
          const rowY = ty + r * MRH;

          // Label pill
          const lw = ctx.measureText(row.label).width + labelPad * 2;
          ctx.fillStyle = 'rgba(22, 33, 62, 0.85)';
          ctx.fillRect(GUTTER_WIDTH + 2, rowY + 1, lw, MRH - 1);
          ctx.fillStyle = row.labelColor;
          ctx.fillText(row.label, GUTTER_WIDTH + 2 + labelPad, rowY + MRH - 2);

          // Draw bars — colorFn hoisted out of the tick loop to avoid closure allocation per tick.
          const colorFn = (_: SpanData) => row.barColor;
          for (const tick of visibleTicks) {
            const ops = row.getOps(tick);
            const endMax = row.getEndMax(tick);
            drawMiniRow(ops, endMax, rowY, colorFn);
          }
        }
      }

    }

    // ── Mouse crosshair cursor line + timestamp label ──
    // A thin vertical line at the cursor's X position with the exact absolute time (relative to first tick start) displayed
    // at the bottom of the line. Drawn after all tracks so it overlays everything. Visible whenever the cursor is over the
    // content area (mx >= GUTTER_WIDTH); hidden during drag (mouseXRef.current == -1) so it doesn't jitter while panning.
    const crosshairX = mouseXRef.current;
    if (crosshairX >= GUTTER_WIDTH) {
      const cursorUs = vp.offsetX + (crosshairX - GUTTER_WIDTH) / vp.scaleX;
      const baseUs = visibleTicks[0]?.startUs ?? 0;
      // Vertical line
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.3)';
      ctx.lineWidth = 1;
      ctx.setLineDash([]);
      ctx.beginPath();
      ctx.moveTo(crosshairX, RULER_HEIGHT);
      ctx.lineTo(crosshairX, height);
      ctx.stroke();
      // Timestamp label at the bottom of the line, in a small pill background so it's readable over any content.
      const label = formatRulerLabel(cursorUs - baseUs);
      ctx.font = '9px monospace';
      const labelWidth = ctx.measureText(label).width + 8;
      const labelX = Math.min(crosshairX - labelWidth / 2, width - labelWidth - 2);
      const labelY = height - 18;
      ctx.fillStyle = 'rgba(22, 33, 62, 0.9)';
      ctx.fillRect(labelX, labelY, labelWidth, 16);
      ctx.fillStyle = TEXT_COLOR;
      ctx.textAlign = 'center';
      ctx.fillText(label, crosshairX, labelY + 12);
    }

    // ── Zoom-selection overlay ──
    // During a drag-to-zoom gesture, draw a semi-transparent highlight over the selected region with a center line and
    // a duration label. The overlay spans the full canvas height (minus the ruler) so the user can see the time range
    // they're about to zoom into relative to all tracks.
    const sel = zoomSelRef.current;
    if (sel && sel.x2 > sel.x1) {
      const selW = sel.x2 - sel.x1;
      // Semi-transparent blue fill
      ctx.fillStyle = 'rgba(70, 130, 220, 0.2)';
      ctx.fillRect(sel.x1, RULER_HEIGHT, selW, height - RULER_HEIGHT);
      // Left + right edge lines
      ctx.strokeStyle = 'rgba(70, 130, 220, 0.7)';
      ctx.lineWidth = 1;
      ctx.setLineDash([]);
      ctx.beginPath();
      ctx.moveTo(sel.x1, RULER_HEIGHT); ctx.lineTo(sel.x1, height);
      ctx.moveTo(sel.x2, RULER_HEIGHT); ctx.lineTo(sel.x2, height);
      ctx.stroke();
      // Horizontal center line
      const centerY = (RULER_HEIGHT + height) / 2;
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.5)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(sel.x1, centerY);
      ctx.lineTo(sel.x2, centerY);
      ctx.stroke();
      // Duration label centered below the center line
      const selStartUs = vp.offsetX + (sel.x1 - GUTTER_WIDTH) / vp.scaleX;
      const selEndUs = vp.offsetX + (sel.x2 - GUTTER_WIDTH) / vp.scaleX;
      const selDuration = selEndUs - selStartUs;
      const durLabel = formatRulerLabel(selDuration);
      ctx.font = '11px monospace';
      ctx.textAlign = 'center';
      const durLabelX = (sel.x1 + sel.x2) / 2;
      // Pill background
      const durLabelW = ctx.measureText(durLabel).width + 10;
      ctx.fillStyle = 'rgba(22, 33, 62, 0.9)';
      ctx.fillRect(durLabelX - durLabelW / 2, centerY + 4, durLabelW, 18);
      ctx.fillStyle = '#fff';
      ctx.fillText(durLabel, durLabelX, centerY + 17);
    }

    // ── Pending-chunk placeholders ──
    // Paint a diagonal-stripe overlay over any summary tick whose chunks aren't resident yet. During rapid pan/wheel this
    // prevents the empty content area from looking broken: the user sees striped rectangles at the exact time ranges that are
    // about to fill in. Summary is authoritative (always loaded at open time); trace.ticks is the resident subset. The overlay
    // is clipped to [RULER_HEIGHT, height] so the tick-number labels in the ruler remain visible on top.
    // Empty in live mode (no summary) and a no-op when all visible ticks are already resident.
    if (trace.summary && trace.summary.length > 0) {
      const residentNums = new Set<number>(trace.tickNumbers);
      if (residentNums.size < trace.summary.length) {
        const pattern = getPendingPattern(ctx);
        for (const s of trace.summary) {
          const tickEndUs = s.startUs + s.durationUs;
          if (tickEndUs <= visStartUs) continue;
          if (s.startUs >= visEndUs) break;               // summary is sorted by startUs — safe to early-exit
          if (residentNums.has(s.tickNumber)) continue;
          const x1 = Math.max(GUTTER_WIDTH, pxOfUs(s.startUs));
          const x2 = Math.min(width, pxOfUs(tickEndUs));
          if (x2 - x1 < 1) continue;
          ctx.fillStyle = pattern;
          ctx.fillRect(x1, RULER_HEIGHT, x2 - x1, height - RULER_HEIGHT);
          // "T# ⟳" label — only when the range is wide enough to fit it without crowding.
          if (x2 - x1 > 48) {
            ctx.fillStyle = 'rgba(200, 210, 230, 0.85)';
            ctx.font = '10px monospace';
            ctx.textAlign = 'center';
            ctx.fillText(`T${s.tickNumber} \u27F3`, (x1 + x2) / 2, RULER_HEIGHT + 14);
          }
        }
      }
    }

    const hover = hoverRef.current;
    if (hover) drawTooltip(ctx, hover.x, hover.y, hover.lines, width, height);

    // Restore original methods before the debug line (so the debug draw itself isn't double-counted).
    ctx.fillRect = origFillRect;
    ctx.fillText = origFillText;

    // Debug stats — dark grey, bottom-left of the content area, barely noticeable.
    const renderMs = (performance.now() - renderStart).toFixed(1);
    ctx.fillStyle = '#666';
    ctx.font = '13px monospace';
    ctx.textAlign = 'left';
    ctx.fillText(`${rectCount} rects  ${textCount} texts  ${renderMs}ms`, GUTTER_WIDTH + 4, height - 4);

    ctx.restore();
  }, [trace, viewRange, selectedChunk, selectedSpan, collapseState, buildLayout, getVisibleTicks]);

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

  const onWheel = useCallback((e: WheelEvent) => {
    e.preventDefault();
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const vp = vpRef.current;
    const contentWidth = rect.width - GUTTER_WIDTH;
    const mouseX = e.clientX - rect.left - GUTTER_WIDTH;

    if (e.ctrlKey || e.metaKey) {
      vp.scrollY = Math.max(0, vp.scrollY + e.deltaY);
    } else if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
      const delta = e.shiftKey ? e.deltaY : e.deltaX;
      vp.offsetX += delta / vp.scaleX;
      onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
    } else {
      const usAtMouse = vp.offsetX + mouseX / vp.scaleX;
      const factor = e.deltaY > 0 ? 0.85 : 1.18;
      vp.scaleX = Math.max(0.001, Math.min(10000, vp.scaleX * factor));
      vp.offsetX = usAtMouse - mouseX / vp.scaleX;
      onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
    }
    scheduleRender();
  }, [scheduleRender, onViewRangeChange]);

  // Animated navigation: smoothly transition to a target range (used by undo/redo from mouse back/forward buttons
  // and zoom-to-selection). Sets isNavigating to suppress debounced nav push during the animation.
  const animateToRange = useCallback((target: TimeRange, suppressNavPush = false) => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return;
    if (suppressNavPush) navHistory.setNavigating(true);
    const contentWidth = canvas.getBoundingClientRect().width - GUTTER_WIDTH;
    zoomAnimRef.current = {
      from: { startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX },
      to: target,
      startTime: performance.now(),
    };
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => render());
  }, [render, navHistory]);

  const onMouseDown = useCallback((e: MouseEvent) => {
    // Mouse back (button 3) = nav undo (or redo if shift is held — for mice without a forward button).
    // Mouse forward (button 4) = nav redo.
    if (e.button === 3) {
      e.preventDefault();
      const target = e.shiftKey ? navHistory.redo() : navHistory.undo();
      if (target) animateToRange(target, true);
      return;
    }
    if (e.button === 4) {
      e.preventDefault();
      const target = navHistory.redo();
      if (target) animateToRange(target, true);
      return;
    }

    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    if (mx < GUTTER_WIDTH) {
      const layout = buildLayout();
      for (const track of layout) {
        if (!track.collapsible) continue;
        const ty = track.y - vpRef.current.scrollY;
        if (my >= ty && my <= ty + LABEL_ROW_HEIGHT) {
          setCollapseState(prev => ({ ...prev, [track.id]: !track.collapsed }));
          return;
        }
      }
      return;
    }

    isDraggingRef.current = true;
    dragStartRef.current = { x: e.clientX, y: e.clientY, offsetX: vpRef.current.offsetX, scrollY: vpRef.current.scrollY };
    // Middle-button or shift+left = pan (old behavior). Plain left-click = zoom-to-selection.
    dragModeRef.current = (e.button === 1 || e.shiftKey) ? 'pan' : 'select';
    zoomSelRef.current = null;
  }, [buildLayout]);

  const onMouseMove = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    if (isDraggingRef.current) {
      if (dragModeRef.current === 'pan') {
        // Pan mode (middle-drag or shift+left-drag): move the viewport.
        const vp = vpRef.current;
        const rect = canvas.getBoundingClientRect();
        const contentWidth = rect.width - GUTTER_WIDTH;
        vp.offsetX = dragStartRef.current.offsetX - (e.clientX - dragStartRef.current.x) / vp.scaleX;
        vp.scrollY = Math.max(0, dragStartRef.current.scrollY - (e.clientY - dragStartRef.current.y));
        mouseXRef.current = -1;
        onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
      } else {
        // Zoom-selection mode (plain left-drag): update the selection overlay. The render pass draws the rectangle.
        const rect = canvas.getBoundingClientRect();
        const startMx = dragStartRef.current.x - rect.left;
        const currentMx = e.clientX - rect.left;
        zoomSelRef.current = {
          x1: Math.max(GUTTER_WIDTH, Math.min(startMx, currentMx)),
          x2: Math.max(GUTTER_WIDTH, Math.max(startMx, currentMx)),
        };
        mouseXRef.current = -1;
      }
      scheduleRender();
      return;
    }

    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    // Always track cursor X for the crosshair line, even when not over an interactive element.
    mouseXRef.current = mx >= GUTTER_WIDTH ? mx : -1;
    if (mx < GUTTER_WIDTH) { hoverRef.current = null; scheduleRender(); return; }

    const vp = vpRef.current;
    const usAtMouse = vp.offsetX + (mx - GUTTER_WIDTH) / vp.scaleX;
    const layout = buildLayout();
    const visibleTicks = getVisibleTicks();

    let found = false;
    for (const track of layout) {
      if (track.collapsed || track.id === 'ruler') continue;
      const ty = track.y - vp.scrollY;
      if (my < ty || my > ty + track.height) continue;

      if (track.id.startsWith('slot-')) {
        const threadSlot = parseInt(track.id.split('-')[1]);
        const chunkRowHeight = track.chunkRowHeight ?? 0;
        const inChunkRegion = my < ty + chunkRowHeight;
        const hitMarginUs = 2 / vp.scaleX;

        if (inChunkRegion) {
          // Chunk bar row — find the chunk under the cursor.
          for (const tick of visibleTicks) {
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              if (usAtMouse < chunk.startUs - hitMarginUs || usAtMouse > chunk.endUs + hitMarginUs) continue;
              hoverRef.current = {
                x: mx, y: my,
                lines: [
                  chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName,
                  `Duration: ${formatDuration(chunk.durationUs)}`, `Thread slot: ${chunk.threadSlot}`,
                  `Entities: ${chunk.entitiesProcessed.toLocaleString()}`, `Tick: ${tick.tickNumber}`,
                ]
              };
              found = true; break;
            }
            if (found) break;
          }
        } else {
          // Span region — depth is derived from the Y offset below the chunk row. Only spans at that exact depth on this
          // slot can match, which gives precise hit-testing even in deeply nested hierarchies.
          const hoveredDepth = Math.floor((my - ty - chunkRowHeight) / SPAN_ROW_HEIGHT);
          if (hoveredDepth >= 0) {
            for (const tick of visibleTicks) {
              const slotSpans = tick.spansByThreadSlot.get(threadSlot);
              if (!slotSpans || slotSpans.length === 0) continue;
              const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
              if (!endMax) continue;
              let lo = 0, hi = slotSpans.length;
              const target = usAtMouse - hitMarginUs;
              while (lo < hi) {
                const mid = (lo + hi) >>> 1;
                if (endMax[mid] < target) lo = mid + 1; else hi = mid;
              }
              for (let i = lo; i < slotSpans.length; i++) {
                const span = slotSpans[i];
                if (span.startUs > usAtMouse + hitMarginUs) break;
                if ((span.depth ?? 0) !== hoveredDepth) continue;
                if (usAtMouse < span.startUs - hitMarginUs || usAtMouse > span.endUs + hitMarginUs) continue;
                hoverRef.current = {
                  x: mx, y: my,
                  lines: [
                    span.name,
                    `Duration: ${formatDuration(span.durationUs)}`,
                    `Depth: ${span.depth ?? 0}`,
                    `Thread slot: ${span.threadSlot}`,
                    `Tick: ${tick.tickNumber}`,
                  ]
                };
                found = true; break;
              }
              if (found) break;
            }
          }
        }
      } else if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            if (usAtMouse >= phase.startUs && usAtMouse <= phase.endUs) {
              hoverRef.current = { x: mx, y: my, lines: [phase.phaseName, `Duration: ${formatDuration(phase.durationUs)}`, `Tick: ${tick.tickNumber}`] };
              found = true; break;
            }
          }
          if (found) break;
        }
      } else if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
        const MRH = MINI_ROW_HEIGHT;
        const hoveredRow = Math.floor((my - ty) / MRH);
        const hitMarginUs = 2 / vp.scaleX;
        const MAX_TOOLTIP_OPS = 8;
        const hits: string[] = [];
        let totalHits = 0;
        let rowLabel = '';

        // Resolve which ops array + label to search based on track + row index.
        for (const tick of visibleTicks) {
          let ops: SpanData[];
          let endMax: Float64Array;
          if (track.id === 'page-cache') {
            if (hoveredRow === 0) { ops = tick.cacheFetch; endMax = tick.cacheFetchEndMax; rowLabel = 'Fetch'; }
            else if (hoveredRow === 1) { ops = tick.cacheAlloc; endMax = tick.cacheAllocEndMax; rowLabel = 'Allocate'; }
            else if (hoveredRow === 2) { ops = tick.cacheEvict; endMax = tick.cacheEvictEndMax; rowLabel = 'Evicted'; }
            else { ops = tick.cacheFlushes; endMax = tick.cacheFlushesEndMax; rowLabel = 'Flush'; }
          } else if (track.id === 'disk-io') {
            if (hoveredRow === 0) { ops = tick.diskReads; endMax = tick.diskReadsEndMax; rowLabel = 'Disk Reads'; }
            else { ops = tick.diskWrites; endMax = tick.diskWritesEndMax; rowLabel = 'Disk Writes'; }
          } else if (track.id === 'transactions') {
            if (hoveredRow === 0) { ops = tick.txCommits; endMax = tick.txCommitsEndMax; rowLabel = 'Commits'; }
            else if (hoveredRow === 1) { ops = tick.txRollbacks; endMax = tick.txRollbacksEndMax; rowLabel = 'Rollbacks'; }
            else { ops = tick.txPersists; endMax = tick.txPersistsEndMax; rowLabel = 'Persists'; }
          } else if (track.id === 'wal') {
            if (hoveredRow === 0) { ops = tick.walFlushes; endMax = tick.walFlushesEndMax; rowLabel = 'WAL Flushes'; }
            else { ops = tick.walWaits; endMax = tick.walWaitsEndMax; rowLabel = 'WAL Waits'; }
          } else {
            ops = tick.checkpointCycles; endMax = tick.checkpointCyclesEndMax; rowLabel = 'Checkpoints';
          }
          endMax ??= new Float64Array(0);
          if (ops.length === 0) continue;
          let lo = 0, hi = ops.length;
          const target = usAtMouse - hitMarginUs;
          while (lo < hi) {
            const mid = (lo + hi) >>> 1;
            if (endMax[mid] < target) lo = mid + 1; else hi = mid;
          }
          for (let i = lo; i < ops.length; i++) {
            const op = ops[i];
            if (op.startUs > usAtMouse + hitMarginUs) break;
            if (usAtMouse < op.startUs - hitMarginUs || usAtMouse > op.endUs + hitMarginUs) continue;
            totalHits++;
            if (hits.length < MAX_TOOLTIP_OPS) {
              const dur = op.durationUs;
              const asyncNote = op.kickoffDurationUs !== undefined ? ` (async: ${formatDuration(dur - op.kickoffDurationUs)})` : '';
              hits.push(`${op.name} ${formatDuration(dur)}${asyncNote} [slot ${op.threadSlot}]`);
            }
          }
        }

        if (totalHits > 0) {
          const lines = [
            `${rowLabel}: ${totalHits} op${totalHits > 1 ? 's' : ''} at cursor`,
            ...hits,
          ];
          if (totalHits > MAX_TOOLTIP_OPS) {
            lines.push(`... and ${totalHits - MAX_TOOLTIP_OPS} more`);
          }
          hoverRef.current = { x: mx, y: my, lines };
          found = true;
        }
      }
      break;
    }
    if (!found) hoverRef.current = null;
    scheduleRender();
  }, [trace, buildLayout, getVisibleTicks, scheduleRender, onViewRangeChange]);

  const onMouseUp = useCallback((e: MouseEvent) => {
    if (!isDraggingRef.current) return;
    const dx = Math.abs(e.clientX - dragStartRef.current.x);
    const dy = Math.abs(e.clientY - dragStartRef.current.y);
    isDraggingRef.current = false;

    // ── Zoom-to-selection commit ──
    // If the user dragged far enough horizontally in select mode, zoom the viewport to the selected time range.
    // The selection overlay (zoomSelRef) was being drawn per-frame during the drag; now we convert it to µs and apply.
    const sel = zoomSelRef.current;
    zoomSelRef.current = null;  // always clear the overlay
    if (dragModeRef.current === 'select' && sel && (sel.x2 - sel.x1) > 5) {
      const vp = vpRef.current;
      const canvas = canvasRef.current;
      if (canvas) {
        const contentWidth = canvas.getBoundingClientRect().width - GUTTER_WIDTH;
        const targetStartUs = vp.offsetX + (sel.x1 - GUTTER_WIDTH) / vp.scaleX;
        const targetEndUs = vp.offsetX + (sel.x2 - GUTTER_WIDTH) / vp.scaleX;
        if (targetEndUs > targetStartUs) {
          // Kick off an animated zoom from the current viewport to the selected range over 800 ms.
          zoomAnimRef.current = {
            from: { startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX },
            to: { startUs: targetStartUs, endUs: targetEndUs },
            startTime: performance.now(),
          };
          scheduleRender();
          return;
        }
      }
    }
    scheduleRender();  // clear the selection overlay visual even if we didn't zoom

    if (dx < 3 && dy < 3) {
      const canvas = canvasRef.current;
      if (!canvas) return;
      const rect = canvas.getBoundingClientRect();
      const mx = e.clientX - rect.left;
      const my = e.clientY - rect.top;
      if (mx < GUTTER_WIDTH) return;
      const usAtMouse = vpRef.current.offsetX + (mx - GUTTER_WIDTH) / vpRef.current.scaleX;
      const layout = buildLayout();
      const visibleTicks = getVisibleTicks();

      for (const track of layout) {
        if (track.collapsed) continue;
        const ty = track.y - vpRef.current.scrollY;
        if (my < ty || my > ty + track.height) continue;

        // Slot lane — the chunk row is the top chunkRowHeight pixels; everything below is span rows by depth.
        if (track.id.startsWith('slot-')) {
          const threadSlot = parseInt(track.id.split('-')[1]);
          const chunkRowHeight = track.chunkRowHeight ?? 0;
          const inChunkRegion = my < ty + chunkRowHeight;
          const hitMarginUs = 2 / vpRef.current.scaleX;

          if (inChunkRegion) {
            for (const tick of visibleTicks) {
              for (const chunk of tick.chunks) {
                if (chunk.threadSlot !== threadSlot) continue;
                if (usAtMouse < chunk.startUs - hitMarginUs || usAtMouse > chunk.endUs + hitMarginUs) continue;
                onChunkSelect(chunk);
                onSpanSelect(null);
                return;
              }
            }
          } else {
            const clickedDepth = Math.floor((my - ty - chunkRowHeight) / SPAN_ROW_HEIGHT);
            if (clickedDepth >= 0) {
              for (const tick of visibleTicks) {
                const slotSpans = tick.spansByThreadSlot.get(threadSlot);
                if (!slotSpans || slotSpans.length === 0) continue;
                const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
                if (!endMax) continue;
                let lo = 0, hi = slotSpans.length;
                const target = usAtMouse - hitMarginUs;
                while (lo < hi) {
                  const mid = (lo + hi) >>> 1;
                  if (endMax[mid] < target) lo = mid + 1; else hi = mid;
                }
                for (let i = lo; i < slotSpans.length; i++) {
                  const span = slotSpans[i];
                  if (span.startUs > usAtMouse + hitMarginUs) break;
                  if ((span.depth ?? 0) !== clickedDepth) continue;
                  if (usAtMouse < span.startUs - hitMarginUs || usAtMouse > span.endUs + hitMarginUs) continue;
                  onSpanSelect(span);
                  onChunkSelect(null);
                  return;
                }
              }
            }
          }
          break;
        }
      }
      // No hit on any interactive track — clear both selections.
      onChunkSelect(null);
      onSpanSelect(null);
    }
  }, [buildLayout, getVisibleTicks, onChunkSelect, onSpanSelect]);

  const onDblClick = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    if (mx < GUTTER_WIDTH) return;
    const vp = vpRef.current;
    const usAtMouse = vp.offsetX + (mx - GUTTER_WIDTH) / vp.scaleX;
    const layout = buildLayout();
    const visibleTicks = getVisibleTicks();
    const hitMarginUs = 2 / vp.scaleX;

    const zoomToSpan = (startUs: number, endUs: number) => {
      animateToRange({ startUs, endUs });
    };

    for (const track of layout) {
      if (track.collapsed || track.id === 'ruler') continue;
      const ty = track.y - vp.scrollY;
      if (my < ty || my > ty + track.height) continue;

      if (track.id.startsWith('slot-')) {
        const threadSlot = parseInt(track.id.split('-')[1]);
        const chunkRowHeight = track.chunkRowHeight ?? 0;

        if (my < ty + chunkRowHeight) {
          for (const tick of visibleTicks) {
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              if (usAtMouse < chunk.startUs - hitMarginUs || usAtMouse > chunk.endUs + hitMarginUs) continue;
              zoomToSpan(chunk.startUs, chunk.endUs);
              return;
            }
          }
        } else {
          const clickedDepth = Math.floor((my - ty - chunkRowHeight) / SPAN_ROW_HEIGHT);
          if (clickedDepth >= 0) {
            for (const tick of visibleTicks) {
              const slotSpans = tick.spansByThreadSlot.get(threadSlot);
              if (!slotSpans || slotSpans.length === 0) continue;
              const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
              if (!endMax) continue;
              let lo = 0, hi = slotSpans.length;
              while (lo < hi) { const mid = (lo + hi) >>> 1; if (endMax[mid] < usAtMouse - hitMarginUs) lo = mid + 1; else hi = mid; }
              for (let i = lo; i < slotSpans.length; i++) {
                const span = slotSpans[i];
                if (span.startUs > usAtMouse + hitMarginUs) break;
                if ((span.depth ?? 0) !== clickedDepth) continue;
                if (usAtMouse < span.startUs - hitMarginUs || usAtMouse > span.endUs + hitMarginUs) continue;
                zoomToSpan(span.startUs, span.endUs);
                return;
              }
            }
          }
        }
      } else if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            if (usAtMouse >= phase.startUs - hitMarginUs && usAtMouse <= phase.endUs + hitMarginUs) {
              zoomToSpan(phase.startUs, phase.endUs);
              return;
            }
          }
        }
      }
      break;
    }
  }, [buildLayout, getVisibleTicks, animateToRange]);

  return (
    <div ref={containerRef} style={{ width: '100%', height: '100%', position: 'relative' }}>
      <canvas
        ref={canvasRef}
        style={{ width: '100%', height: '100%', cursor: isDraggingRef.current ? 'grabbing' : 'crosshair' }}
        onWheel={onWheel}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={onMouseUp}
        onDblClick={onDblClick}
        onMouseLeave={() => { isDraggingRef.current = false; hoverRef.current = null; mouseXRef.current = -1; scheduleRender(); }}
        onContextMenu={(e) => e.preventDefault()}
      />
    </div>
  );
}

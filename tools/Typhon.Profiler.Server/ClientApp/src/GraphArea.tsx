import { useRef, useEffect, useCallback, useState } from 'preact/hooks';
import type { ProcessedTrace, ChunkSpan, TickData } from './traceModel';
import { getTicksInRange } from './traceModel';
import type { Viewport, TrackLayout, TimeRange } from './uiTypes';
import {
  setupCanvas, computeGridStep, drawTooltip, getSystemColor,
  PHASE_COLOR, BG_COLOR, HEADER_BG, BORDER_COLOR, TEXT_COLOR, DIM_TEXT, GRID_COLOR
} from './canvasUtils';

interface GraphAreaProps {
  trace: ProcessedTrace;
  tracePath: string | null;
  viewRange: TimeRange;
  onViewRangeChange: (range: TimeRange) => void;
  selectedChunk: ChunkSpan | null;
  onChunkSelect: (chunk: ChunkSpan | null) => void;
}

const GUTTER_WIDTH = 100;
const RULER_HEIGHT = 24;
const TRACK_HEIGHT = 28;
const TRACK_GAP = 2;
const PHASE_TRACK_HEIGHT = 20;
const COLLAPSED_HEIGHT = 4;
const LABEL_ROW_HEIGHT = 18;
const MIN_RECT_WIDTH = 1;
const FLAME_ROW_HEIGHT = 14;
const FLAME_TRACK_DEFAULT_HEIGHT = 120;

const TICK_BOUNDARY_COLOR = 'rgba(100, 100, 200, 0.3)';

function shortenName(name: string): string {
  const parenIdx = name.indexOf('(');
  const base = parenIdx >= 0 ? name.substring(0, parenIdx) : name;
  const parts = base.split('.');
  return parts.length >= 2 ? parts.slice(-2).join('.') : base;
}

function nameToColor(name: string): string {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) - hash + name.charCodeAt(i)) | 0;
  }
  return `hsl(${(hash & 0xFFFF) % 360}, 50%, 42%)`;
}

export function GraphArea({ trace, tracePath, viewRange, onViewRangeChange, selectedChunk, onChunkSelect }: GraphAreaProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const vpRef = useRef<Viewport>({ offsetX: viewRange.startUs, scaleX: 0.5, scrollY: 0 });
  const hoverRef = useRef<{ x: number; y: number; lines: string[] } | null>(null);
  const isDraggingRef = useRef(false);
  const dragStartRef = useRef({ x: 0, y: 0, offsetX: 0, scrollY: 0 });
  const rafRef = useRef(0);
  const [collapseState, setCollapseState] = useState<Record<string, boolean>>({});

  const workerCount = trace.metadata.header.workerCount;

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

    for (let w = 0; w < workerCount; w++) {
      const id = `worker-${w}`;
      const collapsed = collapseState[id] ?? false;
      const h = collapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : TRACK_HEIGHT + TRACK_GAP;
      layout.push({ id, label: `Worker ${w}`, y, height: TRACK_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed, collapsible: true });
      y += h;
    }

    // Phases track
    const phasesCollapsed = collapseState['phases'] ?? false;
    const ph = phasesCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : PHASE_TRACK_HEIGHT + TRACK_GAP;
    layout.push({ id: 'phases', label: 'Phases', y, height: PHASE_TRACK_HEIGHT, collapsedHeight: COLLAPSED_HEIGHT, collapsed: phasesCollapsed, collapsible: true });
    y += ph;

    // OTel Spans track
    const spansCollapsed = collapseState['spans'] ?? false;
    const spansHeight = spansCollapsed ? COLLAPSED_HEIGHT : 80;
    const sh = spansCollapsed ? COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT : spansHeight + TRACK_GAP;
    layout.push({ id: 'spans', label: 'OTel Spans', y, height: spansHeight, collapsedHeight: COLLAPSED_HEIGHT, collapsed: spansCollapsed, collapsible: true });
    y += sh;

    return layout;
  }, [workerCount, collapseState]);

  const getVisibleTicks = useCallback((): TickData[] => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return [];
    const rect = canvas.getBoundingClientRect();
    const contentWidth = rect.width - GUTTER_WIDTH;
    return getTicksInRange(trace, vp.offsetX, vp.offsetX + contentWidth / vp.scaleX);
  }, [trace]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d')!;
    const { width, height } = setupCanvas(canvas);
    const vp = vpRef.current;
    const layout = buildLayout();
    const visibleTicks = getVisibleTicks();
    const contentWidth = width - GUTTER_WIDTH;

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
        const gridStep = computeGridStep(visEndUs - visStartUs);
        const gridStart = Math.floor(visStartUs / gridStep) * gridStep;
        for (let t = gridStart; t <= visEndUs; t += gridStep) {
          const x = pxOfUs(t);
          if (x < GUTTER_WIDTH) continue;
          ctx.strokeStyle = GRID_COLOR; ctx.lineWidth = 0.5;
          ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke();
          ctx.fillStyle = DIM_TEXT; ctx.font = '9px monospace'; ctx.textAlign = 'center';
          ctx.fillText(`${(t - visibleTicks[0].startUs).toFixed(0)}us`, x, ty + 16);
        }
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

      // ── Worker tracks ──
      if (track.id.startsWith('worker-')) {
        const workerId = parseInt(track.id.split('-')[1]);
        for (const tick of visibleTicks) {
          for (const chunk of tick.chunks) {
            if (chunk.workerId !== workerId) continue;
            const x1 = pxOfUs(chunk.startUs);
            const x2 = pxOfUs(chunk.endUs);
            if (x2 < GUTTER_WIDTH || x1 > width) continue;
            const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
            ctx.fillStyle = getSystemColor(chunk.systemIndex);
            ctx.fillRect(x1, ty + 1, w, TRACK_HEIGHT - 2);
            if (selectedChunk && chunk.systemIndex === selectedChunk.systemIndex &&
                chunk.chunkIndex === selectedChunk.chunkIndex && chunk.workerId === selectedChunk.workerId &&
                Math.abs(chunk.startUs - selectedChunk.startUs) < 0.01) {
              ctx.strokeStyle = '#fff'; ctx.lineWidth = 2;
              ctx.strokeRect(x1, ty + 1, w, TRACK_HEIGHT - 2);
            }
            if (w > 40) {
              ctx.fillStyle = '#000'; ctx.font = '10px monospace'; ctx.textAlign = 'left';
              const label = chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName;
              ctx.fillText(label, x1 + 3, ty + TRACK_HEIGHT / 2 + 3, w - 6);
            }
          }
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
              ctx.fillStyle = TEXT_COLOR; ctx.font = '9px monospace'; ctx.textAlign = 'left';
              ctx.fillText(`${phase.phaseName} (${phase.durationUs.toFixed(0)}us)`, x1 + 3, ty + 12, w - 6);
            }
          }
        }
      }

      // ── OTel Spans track ──
      if (track.id === 'spans') {
        let hasSpans = false;
        const spanRowH = 14;
        // Collect all spans from visible ticks and assign rows by name (stacked)
        const spanNameRows = new Map<number, number>();
        let nextRow = 0;

        for (const tick of visibleTicks) {
          for (const span of tick.spans) {
            const x1 = pxOfUs(span.startUs);
            const x2 = pxOfUs(span.endUs);
            if (x2 < GUTTER_WIDTH || x1 > width) continue;

            // Assign a row to each unique span name
            if (!spanNameRows.has(span.nameId)) {
              spanNameRows.set(span.nameId, nextRow++);
            }
            const row = spanNameRows.get(span.nameId)!;
            const sy = ty + row * spanRowH;
            if (sy > ty + track.height) continue; // clip to track height

            const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
            ctx.fillStyle = nameToColor(span.name);
            ctx.fillRect(x1, sy, w, spanRowH - 1);

            if (w > 30) {
              ctx.fillStyle = '#ddd'; ctx.font = '9px monospace'; ctx.textAlign = 'left';
              ctx.fillText(`${span.name} (${span.durationUs.toFixed(0)}us)`, x1 + 2, sy + 10, w - 4);
            }
            hasSpans = true;
          }
        }

        if (!hasSpans) {
          ctx.fillStyle = '#333'; ctx.font = '10px monospace'; ctx.textAlign = 'center';
          ctx.fillText('No OTel spans in view (enable telemetry in typhon.telemetry.json)',
            GUTTER_WIDTH + contentWidth / 2, ty + track.height / 2 + 4);
        }
      }
    }

    const hover = hoverRef.current;
    if (hover) drawTooltip(ctx, hover.x, hover.y, hover.lines, width, height);
    ctx.restore();
  }, [trace, viewRange, selectedChunk, collapseState, buildLayout, getVisibleTicks]);

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
      vp.scaleX = Math.max(0.001, Math.min(100, vp.scaleX * factor));
      vp.offsetX = usAtMouse - mouseX / vp.scaleX;
      onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
    }
    scheduleRender();
  }, [scheduleRender, onViewRangeChange]);

  const onMouseDown = useCallback((e: MouseEvent) => {
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
  }, [buildLayout]);

  const onMouseMove = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    if (isDraggingRef.current) {
      const vp = vpRef.current;
      const rect = canvas.getBoundingClientRect();
      const contentWidth = rect.width - GUTTER_WIDTH;
      vp.offsetX = dragStartRef.current.offsetX - (e.clientX - dragStartRef.current.x) / vp.scaleX;
      vp.scrollY = Math.max(0, dragStartRef.current.scrollY - (e.clientY - dragStartRef.current.y));
      onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
      scheduleRender();
      return;
    }

    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
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

      if (track.id.startsWith('worker-')) {
        const workerId = parseInt(track.id.split('-')[1]);
        for (const tick of visibleTicks) {
          for (const chunk of tick.chunks) {
            if (chunk.workerId === workerId && usAtMouse >= chunk.startUs && usAtMouse <= chunk.endUs) {
              hoverRef.current = {
                x: mx, y: my,
                lines: [
                  chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName,
                  `Duration: ${chunk.durationUs.toFixed(1)} us`, `Worker: ${chunk.workerId}`,
                  `Entities: ${chunk.entitiesProcessed.toLocaleString()}`, `Tick: ${tick.tickNumber}`,
                ]
              };
              found = true; break;
            }
          }
          if (found) break;
        }
      } else if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            if (usAtMouse >= phase.startUs && usAtMouse <= phase.endUs) {
              hoverRef.current = { x: mx, y: my, lines: [phase.phaseName, `Duration: ${phase.durationUs.toFixed(1)} us`, `Tick: ${tick.tickNumber}`] };
              found = true; break;
            }
          }
          if (found) break;
        }
      } else if (track.id === 'spans') {
        for (const tick of visibleTicks) {
          for (const span of tick.spans) {
            if (usAtMouse >= span.startUs && usAtMouse <= span.endUs) {
              hoverRef.current = {
                x: mx, y: my,
                lines: [span.name, `Duration: ${span.durationUs.toFixed(1)} us`, `Tick: ${tick.tickNumber}`]
              };
              found = true; break;
            }
          }
          if (found) break;
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
        if (track.collapsed || !track.id.startsWith('worker-')) continue;
        const ty = track.y - vpRef.current.scrollY;
        if (my < ty || my > ty + track.height) continue;
        const workerId = parseInt(track.id.split('-')[1]);
        for (const tick of visibleTicks) {
          for (const chunk of tick.chunks) {
            if (chunk.workerId === workerId && usAtMouse >= chunk.startUs && usAtMouse <= chunk.endUs) {
              onChunkSelect(chunk); return;
            }
          }
        }
        break;
      }
      onChunkSelect(null);
    }
  }, [buildLayout, getVisibleTicks, onChunkSelect]);

  return (
    <div ref={containerRef} style={{ width: '100%', height: '100%', position: 'relative' }}>
      <canvas
        ref={canvasRef}
        style={{ width: '100%', height: '100%', cursor: isDraggingRef.current ? 'grabbing' : 'crosshair' }}
        onWheel={onWheel}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={onMouseUp}
        onMouseLeave={() => { isDraggingRef.current = false; hoverRef.current = null; scheduleRender(); }}
      />
    </div>
  );
}

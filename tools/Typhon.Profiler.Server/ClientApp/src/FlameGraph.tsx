import { useRef, useEffect, useCallback, useState } from 'preact/hooks';
import { fetchFlameGraph, type FlameNode } from './api';
import { setupCanvas, drawTooltip, BG_COLOR, HEADER_BG, BORDER_COLOR, TEXT_COLOR, DIM_TEXT } from './canvasUtils';
import type { TimeRange } from './uiTypes';

interface FlameGraphProps {
  tracePath: string | null;
  viewRange: TimeRange;
}

const FLAME_ROW_HEIGHT = 18;
const FLAME_GAP = 1;
const FLAME_TOTAL = FLAME_ROW_HEIGHT + FLAME_GAP;
const MIN_WIDTH_FOR_LABEL = 35;
const HEADER_HEIGHT = 24;

/** Generate a stable color from a string (method name) */
function nameToColor(name: string): string {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) - hash + name.charCodeAt(i)) | 0;
  }
  const h = ((hash & 0xFFFF) % 360);
  return `hsl(${h}, 55%, 45%)`;
}

interface LayoutRect {
  x: number;
  width: number;
  y: number;
  node: FlameNode;
  depth: number;
}

function layoutFlame(root: FlameNode, totalWidth: number): LayoutRect[] {
  const rects: LayoutRect[] = [];

  function walk(node: FlameNode, x: number, width: number, depth: number) {
    if (width < 0.5) return; // too narrow to render

    rects.push({ x, width, y: depth * FLAME_TOTAL, node, depth });

    let childX = x;
    for (const child of node.children) {
      const childWidth = (child.total / node.total) * width;
      walk(child, childX, childWidth, depth + 1);
      childX += childWidth;
    }
  }

  if (root.total > 0) {
    // Skip the root node, start from its children
    let childX = 0;
    for (const child of root.children) {
      const childWidth = (child.total / root.total) * totalWidth;
      walk(child, childX, childWidth, 0);
      childX += childWidth;
    }
  }

  return rects;
}

export function FlameGraph({ tracePath, viewRange }: FlameGraphProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [flameData, setFlameData] = useState<{ root: FlameNode; totalSamples: number } | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const hoverRef = useRef<{ x: number; y: number; lines: string[] } | null>(null);
  const rafRef = useRef(0);

  // Fetch flame graph data when viewRange changes
  useEffect(() => {
    if (!tracePath || viewRange.startUs >= viewRange.endUs) {
      setFlameData(null);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    fetchFlameGraph(tracePath, viewRange.startUs, viewRange.endUs)
      .then(data => {
        if (!cancelled) {
          setFlameData({ root: data.root, totalSamples: data.totalSamples });
          setLoading(false);
        }
      })
      .catch(err => {
        if (!cancelled) {
          setError(err.message);
          setLoading(false);
        }
      });

    return () => { cancelled = true; };
  }, [tracePath, viewRange.startUs, viewRange.endUs]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d')!;
    const { width, height } = setupCanvas(canvas);

    ctx.fillStyle = BG_COLOR;
    ctx.fillRect(0, 0, width, height);

    // Header
    ctx.fillStyle = HEADER_BG;
    ctx.fillRect(0, 0, width, HEADER_HEIGHT);
    ctx.fillStyle = TEXT_COLOR;
    ctx.font = '11px monospace';
    ctx.textAlign = 'left';

    if (loading) {
      ctx.fillText('Loading flame graph...', 8, 16);
      return;
    }

    if (error) {
      ctx.fillStyle = '#ff6b6b';
      ctx.fillText(`Error: ${error}`, 8, 16);
      return;
    }

    if (!flameData || flameData.totalSamples === 0) {
      ctx.fillStyle = DIM_TEXT;
      ctx.fillText('No CPU samples in this range (enable sampling with enableCpuSampling: true)', 8, 16);
      return;
    }

    ctx.fillText(`Flame Graph  |  ${flameData.totalSamples} samples`, 8, 16);

    // Layout and render flames
    const contentY = HEADER_HEIGHT + 4;
    const rects = layoutFlame(flameData.root, width);

    ctx.save();
    ctx.translate(0, contentY);

    for (const rect of rects) {
      if (rect.x + rect.width < 0 || rect.x > width) continue;

      const color = nameToColor(rect.node.name);
      ctx.fillStyle = color;
      ctx.fillRect(rect.x, rect.y, rect.width, FLAME_ROW_HEIGHT);

      // Border
      ctx.strokeStyle = 'rgba(0,0,0,0.2)';
      ctx.lineWidth = 0.5;
      ctx.strokeRect(rect.x, rect.y, rect.width, FLAME_ROW_HEIGHT);

      // Label
      if (rect.width > MIN_WIDTH_FOR_LABEL) {
        ctx.fillStyle = '#fff';
        ctx.font = '10px monospace';
        ctx.textAlign = 'left';
        // Shorten name: take last segment (method name)
        const shortName = shortenName(rect.node.name);
        ctx.fillText(shortName, rect.x + 2, rect.y + 13, rect.width - 4);
      }
    }

    // Hover tooltip
    const hover = hoverRef.current;
    if (hover) {
      drawTooltip(ctx, hover.x, hover.y - contentY, hover.lines, width, height - contentY);
    }

    ctx.restore();
  }, [flameData, loading, error]);

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

  // Hover detection
  const onMouseMove = useCallback((e: MouseEvent) => {
    if (!flameData || flameData.totalSamples === 0) {
      hoverRef.current = null;
      scheduleRender();
      return;
    }

    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top - HEADER_HEIGHT - 4;

    const rects = layoutFlame(flameData.root, rect.width);

    let found = false;
    for (const r of rects) {
      if (mx >= r.x && mx <= r.x + r.width && my >= r.y && my <= r.y + FLAME_ROW_HEIGHT) {
        const pct = ((r.node.total / flameData.totalSamples) * 100).toFixed(1);
        const selfPct = ((r.node.self / flameData.totalSamples) * 100).toFixed(1);
        hoverRef.current = {
          x: mx,
          y: e.clientY - rect.top,
          lines: [
            r.node.name,
            `Total: ${r.node.total} samples (${pct}%)`,
            `Self: ${r.node.self} samples (${selfPct}%)`,
          ]
        };
        found = true;
        break;
      }
    }

    if (!found) hoverRef.current = null;
    scheduleRender();
  }, [flameData, scheduleRender]);

  return (
    <div ref={containerRef} style={{ width: '100%', height: '100%', position: 'relative' }}>
      <canvas
        ref={canvasRef}
        style={{ width: '100%', height: '100%', cursor: 'crosshair' }}
        onMouseMove={onMouseMove}
        onMouseLeave={() => { hoverRef.current = null; scheduleRender(); }}
      />
    </div>
  );
}

/** Shorten a fully qualified .NET method name to something readable */
function shortenName(name: string): string {
  // "Namespace.Class.Method(args)" → "Class.Method"
  const parenIdx = name.indexOf('(');
  const base = parenIdx >= 0 ? name.substring(0, parenIdx) : name;
  const parts = base.split('.');
  if (parts.length >= 2) {
    return parts.slice(-2).join('.');
  }
  return base;
}

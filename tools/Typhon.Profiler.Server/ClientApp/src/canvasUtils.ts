import type { Viewport } from './uiTypes';

/** Color palette for systems — visually distinct, dark-theme friendly */
export const SYSTEM_COLORS = [
  '#e94560', '#4ecdc4', '#ffe66d', '#95e1d3', '#f38181',
  '#aa96da', '#a8d8ea', '#fcbad3', '#c3aed6', '#b8de6f',
  '#ff9a76', '#679b9b', '#ffc4a3', '#e8a87c', '#41b3a3',
  '#d63447', '#f57b51', '#f6efa6', '#5ee7df', '#b490ca',
];

export const PHASE_COLOR = '#2a5298';
export const SELECTED_COLOR = '#e94560';
export const BG_COLOR = '#1a1a2e';
export const HEADER_BG = '#16213e';
export const BORDER_COLOR = '#0f3460';
export const TEXT_COLOR = '#e0e0e0';
export const DIM_TEXT = '#888';
export const GRID_COLOR = '#1a1a3e';

/** Set up a canvas for HiDPI rendering. Returns logical width/height. */
export function setupCanvas(canvas: HTMLCanvasElement): { width: number; height: number } {
  const dpr = window.devicePixelRatio || 1;
  const rect = canvas.getBoundingClientRect();
  canvas.width = rect.width * dpr;
  canvas.height = rect.height * dpr;
  const ctx = canvas.getContext('2d')!;
  ctx.scale(dpr, dpr);
  return { width: rect.width, height: rect.height };
}

/** Convert a timestamp in µs to a pixel X coordinate */
export function toPixelX(us: number, baseUs: number, vp: Viewport, labelWidth: number): number {
  return labelWidth + (us - baseUs - vp.offsetX) * vp.scaleX;
}

/** Compute adaptive grid step size for the time ruler */
export function computeGridStep(timeRange: number): number {
  const steps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
  for (const step of steps) {
    if (timeRange / step < 20) return step;
  }
  return steps[steps.length - 1];
}

/** Draw a tooltip box with multiple lines of text */
export function drawTooltip(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  lines: string[],
  maxX: number,
  maxY: number
): void {
  const tooltipW = 220;
  const tooltipH = lines.length * 16 + 8;
  const tx = Math.min(x + 12, maxX - tooltipW - 4);
  const ty = Math.min(y + 12, maxY - tooltipH - 4);

  ctx.fillStyle = 'rgba(22, 33, 62, 0.95)';
  ctx.strokeStyle = BORDER_COLOR;
  ctx.lineWidth = 1;
  ctx.fillRect(tx, ty, tooltipW, tooltipH);
  ctx.strokeRect(tx, ty, tooltipW, tooltipH);

  ctx.fillStyle = TEXT_COLOR;
  ctx.font = '11px monospace';
  ctx.textAlign = 'left';
  for (let i = 0; i < lines.length; i++) {
    ctx.fillText(lines[i], tx + 6, ty + 14 + i * 16);
  }
}

/** Generate system colors for N systems */
export function getSystemColor(index: number): string {
  return SYSTEM_COLORS[index % SYSTEM_COLORS.length];
}

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

/**
 * Pick a human-friendly time grid step (in µs) such that labels on the ruler are spaced at least
 * <paramref name="minLabelPxSpacing"/> apart visually. Walks a 1-2-5 decade ladder from 1 µs up to 1 day so the ruler stays
 * readable at every zoom level from "one span" (sub-microsecond) to "whole session" (multi-second).
 *
 * Earlier versions took only <paramref name="timeRangeUs"/> and clamped at 10 ms, which meant a 5 s trace zoomed all the way
 * out tried to draw ~500 labels — overlap city, ~20 ms/frame wasted just drawing ruler text. Keying off pixel spacing
 * instead of time range gives us a guaranteed upper bound on label count (≈ contentWidth / minLabelPxSpacing), which the
 * ruler render uses to skip overwhelming the canvas.
 */
export function computeGridStep(timeRangeUs: number, contentWidthPx: number, minLabelPxSpacing: number = 90): number {
  if (timeRangeUs <= 0 || contentWidthPx <= 0) return 0.01;
  const minStepUs = (timeRangeUs * minLabelPxSpacing) / contentWidthPx;
  // 1-2-5 decade ladder from 10 ns (0.01 µs) up to 24 h. Sub-µs steps let the ruler show meaningful grid lines when the
  // user zooms into individual spans that last tens of nanoseconds.
  const steps = [
    0.01, 0.02, 0.05,
    0.1, 0.2, 0.5,
    1, 2, 5,
    10, 20, 50,
    100, 200, 500,
    1_000, 2_000, 5_000,
    10_000, 20_000, 50_000,
    100_000, 200_000, 500_000,
    1_000_000, 2_000_000, 5_000_000,
    10_000_000, 20_000_000, 50_000_000,
    100_000_000, 200_000_000, 500_000_000,
    1_000_000_000, 2_000_000_000, 5_000_000_000,
    10_000_000_000, 20_000_000_000, 60_000_000_000,
    600_000_000_000, 3_600_000_000_000, 86_400_000_000_000,
  ];
  for (const s of steps) {
    if (s >= minStepUs) return s;
  }
  return steps[steps.length - 1];
}

/**
 * Format a µs offset for display on the ruler. Picks the coarsest unit that gives a readable number — µs up to 1 ms,
 * ms up to 1 s, s up to 60 s, min up past. Precision scales so the number doesn't collapse to "0.0".
 */
export function formatRulerLabel(us: number): string {
  const abs = Math.abs(us);
  const sign = us < 0 ? '-' : '';
  // Sub-microsecond: display as nanoseconds
  if (abs < 1) {
    const ns = abs * 1000;
    return `${sign}${ns.toFixed(ns < 10 ? 1 : 0)}ns`;
  }
  if (abs < 1_000) {
    const decimals = abs < 10 ? 2 : abs < 100 ? 1 : 0;
    return `${sign}${abs.toFixed(decimals)}us`;
  }
  if (abs < 1_000_000) return `${sign}${(abs / 1_000).toFixed(abs < 10_000 ? 2 : abs < 100_000 ? 1 : 0)}ms`;
  if (abs < 60_000_000) return `${sign}${(abs / 1_000_000).toFixed(abs < 10_000_000 ? 2 : abs < 100_000_000 ? 1 : 0)}s`;
  const mins = Math.floor(abs / 60_000_000);
  const secs = (abs % 60_000_000) / 1_000_000;
  return `${sign}${mins}m${secs.toFixed(0).padStart(2, '0')}s`;
}

/** Format a µs duration with adaptive units — same logic as formatRulerLabel, exported under a semantic name. */
export const formatDuration = formatRulerLabel;

/** Draw a tooltip box with multiple lines of text */
export function drawTooltip(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  lines: string[],
  maxX: number,
  maxY: number
): void {
  ctx.font = '11px monospace';
  // Measure the widest line so the tooltip background fits all content. Minimum 120 px so short single-line tooltips
  // don't look weirdly narrow.
  let maxLineW = 120;
  for (const line of lines) {
    const w = ctx.measureText(line).width;
    if (w > maxLineW) maxLineW = w;
  }
  const tooltipW = maxLineW + 16;
  const tooltipH = lines.length * 16 + 8;
  const tx = Math.min(x + 12, maxX - tooltipW - 4);
  const ty = Math.min(y + 12, maxY - tooltipH - 4);

  ctx.fillStyle = 'rgba(22, 33, 62, 0.95)';
  ctx.strokeStyle = BORDER_COLOR;
  ctx.lineWidth = 1;
  ctx.fillRect(tx, ty, tooltipW, tooltipH);
  ctx.strokeRect(tx, ty, tooltipW, tooltipH);

  ctx.fillStyle = TEXT_COLOR;
  ctx.textAlign = 'left';
  for (let i = 0; i < lines.length; i++) {
    ctx.fillText(lines[i], tx + 6, ty + 14 + i * 16);
  }
}

/** Generate system colors for N systems */
export function getSystemColor(index: number): string {
  return SYSTEM_COLORS[index % SYSTEM_COLORS.length];
}

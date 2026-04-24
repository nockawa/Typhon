import type { GaugeSample, MemoryAllocEventData } from '@/libs/profiler/model/traceModel';
import type { Viewport } from '@/libs/profiler/model/uiTypes';
import { formatDuration, toPixelX } from '../canvasUtils';
import type { StudioTheme } from '../theme';

/**
 * Canvas 2D rendering primitives for the gauge region — ported from the old profiler's
 * `gaugeDraw.ts`. All functions are stateless and take a `GaugeTrackLayout` rect + the shared
 * `Viewport` so gauge samples line up pixel-for-pixel with span bars on the same X axis. Colours
 * come from the caller (either directly or via the theme passed to the drawing function) — no
 * hard-coded hex inside this file.
 *
 * The integration layer (`renderers.ts` + `TimeArea.tsx`) owns:
 *   - picking which samples to pass (binary-search the series by timestamp),
 *   - computing per-track Y-range auto-scaling,
 *   - placing the tracks in screen coordinates.
 * Keeping that separation makes these primitives trivially unit-testable (no DOM, no cache, no
 * series filtering).
 */

/**
 * A single gauge track's screen rectangle. `y` is the top edge, `height` is the drawable height,
 * `x`/`width` cover the horizontal extent (the label gutter lives LEFT of `x`, so `x` already
 * accounts for the gutter offset). Callers get `x + width` by adding, which matches the clipping
 * region the primitives use to prevent samples outside the track from bleeding into the ruler or
 * adjacent tracks.
 */
export interface GaugeTrackLayout {
  x: number;
  y: number;
  width: number;
  height: number;
}

export type GaugeUnit = 'bytes' | 'count' | 'percent' | 'bytes-compact' | 'duration';

/**
 * Convert a gauge value to its pixel Y coordinate inside a track. Linear scaling between `yMin`
 * and `yMax`; Y axis points down (Canvas convention) so higher values map to lower pixel Y.
 * Clamps to the track bounds — an out-of-range value clips at top or bottom rather than drawing
 * outside the layout.
 */
function valueToPixelY(value: number, yMin: number, yMax: number, layout: GaugeTrackLayout): number {
  if (yMax <= yMin) return layout.y + layout.height;
  const t = (value - yMin) / (yMax - yMin);
  const clampedT = Math.max(0, Math.min(1, t));
  return layout.y + layout.height - clampedT * layout.height;
}

/**
 * Binary-search for the LAST sample with `timestampUs <= target`. Returns 0 if all samples are
 * after target (caller uses samples[0] as the "first known" anchor — pessimistic step-function
 * assumption, but the only reasonable guess when the gauge had no pre-viewport reading).
 */
function findLeftAnchor(samples: readonly GaugeSample[], target: number): number {
  let lo = 0;
  let hi = samples.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (samples[mid].timestampUs <= target) lo = mid + 1;
    else hi = mid;
  }
  return lo > 0 ? lo - 1 : 0;
}

/**
 * Binary-search for the LAST sample with `timestampUs <= target`. Returns `samples.length - 1`
 * if every sample is before `target` (last value carried forward — correct step-function
 * continuation past the last snapshot).
 */
function findRightAnchor(samples: readonly GaugeSample[], target: number): number {
  let lo = 0;
  let hi = samples.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (samples[mid].timestampUs <= target) lo = mid + 1;
    else hi = mid;
  }
  return lo > 0 ? lo - 1 : 0;
}

/**
 * Draw a single gauge series as a line connecting successive samples. Samples assumed sorted by
 * `timestampUs`. Extends the first/last sample values horizontally to the layout's left/right
 * edges — step-function semantics, matching how gauge state is sampled (piecewise-constant
 * between snapshots).
 */
export function drawLineChart(
  ctx: CanvasRenderingContext2D,
  samples: readonly GaugeSample[],
  vp: Viewport,
  labelWidth: number,
  layout: GaugeTrackLayout,
  color: string,
  yMin: number,
  yMax: number,
  lineWidth: number = 1.5,
): void {
  if (samples.length === 0) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(layout.x, layout.y, layout.width, layout.height);
  ctx.clip();

  ctx.strokeStyle = color;
  ctx.lineWidth = lineWidth;
  ctx.beginPath();

  const viewStartUs = vp.offsetX;
  const viewEndUs = vp.offsetX + layout.width / Math.max(vp.scaleX, 0.001);
  const leftAnchorIdx = findLeftAnchor(samples, viewStartUs);
  const rightAnchorIdx = findRightAnchor(samples, viewEndUs);
  const firstY = valueToPixelY(samples[leftAnchorIdx].value, yMin, yMax, layout);
  const lastY = valueToPixelY(samples[rightAnchorIdx].value, yMin, yMax, layout);

  ctx.moveTo(layout.x, firstY);
  for (const s of samples) {
    const px = toPixelX(s.timestampUs, vp, labelWidth);
    const py = valueToPixelY(s.value, yMin, yMax, layout);
    ctx.lineTo(px, py);
  }
  ctx.lineTo(layout.x + layout.width, lastY);

  ctx.stroke();
  ctx.restore();
}

/**
 * Draw a gauge series as a filled area down to the baseline (`yMin`). Good for "amount over
 * time" gauges where the area below the line carries meaning (WAL commit-buffer occupancy,
 * unmanaged bytes, etc.).
 */
export function drawAreaChart(
  ctx: CanvasRenderingContext2D,
  samples: readonly GaugeSample[],
  vp: Viewport,
  labelWidth: number,
  layout: GaugeTrackLayout,
  fillColor: string,
  strokeColor: string,
  yMin: number,
  yMax: number,
  lineWidth: number = 1,
): void {
  if (samples.length === 0) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(layout.x, layout.y, layout.width, layout.height);
  ctx.clip();

  const baselineY = layout.y + layout.height;
  const leftX = layout.x;
  const rightX = layout.x + layout.width;
  const viewStartUs = vp.offsetX;
  const viewEndUs = vp.offsetX + layout.width / Math.max(vp.scaleX, 0.001);
  const leftAnchorIdx = findLeftAnchor(samples, viewStartUs);
  const rightAnchorIdx = findRightAnchor(samples, viewEndUs);
  const firstY = valueToPixelY(samples[leftAnchorIdx].value, yMin, yMax, layout);
  const lastY = valueToPixelY(samples[rightAnchorIdx].value, yMin, yMax, layout);

  ctx.fillStyle = fillColor;
  ctx.beginPath();
  ctx.moveTo(leftX, baselineY);
  ctx.lineTo(leftX, firstY);
  for (const s of samples) {
    const px = toPixelX(s.timestampUs, vp, labelWidth);
    const py = valueToPixelY(s.value, yMin, yMax, layout);
    ctx.lineTo(px, py);
  }
  ctx.lineTo(rightX, lastY);
  ctx.lineTo(rightX, baselineY);
  ctx.closePath();
  ctx.fill();

  if (lineWidth > 0) {
    ctx.strokeStyle = strokeColor;
    ctx.lineWidth = lineWidth;
    ctx.beginPath();
    ctx.moveTo(leftX, firstY);
    for (const s of samples) {
      const px = toPixelX(s.timestampUs, vp, labelWidth);
      const py = valueToPixelY(s.value, yMin, yMax, layout);
      ctx.lineTo(px, py);
    }
    ctx.lineTo(rightX, lastY);
    ctx.stroke();
  }

  ctx.restore();
}

/**
 * One layer in a stacked-area chart. Series samples must be aligned by `timestampUs` (same tick
 * boundaries) — the stacker iterates in parallel and sums values at each point. Missing samples
 * on a shorter layer are treated as 0.
 */
export interface StackedAreaLayer {
  series: readonly GaugeSample[];
  color: string;
  label: string;
}

/**
 * Draw N gauge series stacked to form a composition chart. Only honest when the input series are
 * mutually-exclusive buckets (e.g. page cache's Free / Clean / Dirty / Exclusive pages). For
 * overlapping series use multiple `drawAreaChart` calls with transparency instead.
 *
 * Assumes all series share the same sample count and `timestampUs[i]`. First series draws at the
 * bottom; subsequent layers stack on top. `yMax` is interpreted as "total stacked sum" — caller
 * computes `yMax = max over i of sum(layers[*].series[i].value)`.
 */
export function drawStackedArea(
  ctx: CanvasRenderingContext2D,
  layers: readonly StackedAreaLayer[],
  vp: Viewport,
  labelWidth: number,
  layout: GaugeTrackLayout,
  yMin: number,
  yMax: number,
): void {
  if (layers.length === 0 || layers[0].series.length === 0) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(layout.x, layout.y, layout.width, layout.height);
  ctx.clip();

  const pointCount = layers[0].series.length;
  const runningOffsets = new Float64Array(pointCount);

  const leftX = layout.x;
  const rightX = layout.x + layout.width;

  const viewStartUs = vp.offsetX;
  const viewEndUs = vp.offsetX + layout.width / Math.max(vp.scaleX, 0.001);
  const edgeSamples = layers[0].series;
  const leftAnchorIdx = findLeftAnchor(edgeSamples, viewStartUs);
  const rightAnchorIdx = findRightAnchor(edgeSamples, viewEndUs);

  for (const layer of layers) {
    const samples = layer.series;
    if (samples.length === 0) continue;
    const count = Math.min(samples.length, pointCount);

    const leftIdx = Math.min(leftAnchorIdx, count - 1);
    const rightIdx = Math.min(rightAnchorIdx, count - 1);
    const firstStacked = runningOffsets[leftIdx] + samples[leftIdx].value;
    const lastStacked = runningOffsets[rightIdx] + samples[rightIdx].value;
    const firstUpperY = valueToPixelY(firstStacked, yMin, yMax, layout);
    const lastUpperY = valueToPixelY(lastStacked, yMin, yMax, layout);
    const firstLowerY = valueToPixelY(runningOffsets[leftIdx], yMin, yMax, layout);
    const lastLowerY = valueToPixelY(runningOffsets[rightIdx], yMin, yMax, layout);

    ctx.fillStyle = layer.color;
    ctx.beginPath();
    ctx.moveTo(leftX, firstUpperY);
    for (let i = 0; i < count; i++) {
      const s = samples[i];
      const stackedValue = runningOffsets[i] + s.value;
      const px = toPixelX(s.timestampUs, vp, labelWidth);
      const py = valueToPixelY(stackedValue, yMin, yMax, layout);
      ctx.lineTo(px, py);
    }
    ctx.lineTo(rightX, lastUpperY);
    ctx.lineTo(rightX, lastLowerY);
    for (let i = count - 1; i >= 0; i--) {
      const s = samples[i];
      const px = toPixelX(s.timestampUs, vp, labelWidth);
      const py = valueToPixelY(runningOffsets[i], yMin, yMax, layout);
      ctx.lineTo(px, py);
    }
    ctx.lineTo(leftX, firstLowerY);
    ctx.closePath();
    ctx.fill();

    for (let i = 0; i < count; i++) {
      runningOffsets[i] += samples[i].value;
    }
  }

  ctx.restore();
}

/**
 * A discrete point to draw on a track. `shape` controls the glyph; typical use is `triangle-up`
 * for allocations, `triangle-down` for frees, `dot` for neutral events.
 */
export interface MarkerPoint {
  timestampUs: number;
  color: string;
  shape: 'triangle-up' | 'triangle-down' | 'dot';
}

/**
 * Draw discrete markers along a track's top edge. Size is fixed at 5 px half-extent.
 */
export function drawMarkers(
  ctx: CanvasRenderingContext2D,
  markers: readonly MarkerPoint[],
  vp: Viewport,
  labelWidth: number,
  layout: GaugeTrackLayout,
): void {
  if (markers.length === 0) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(layout.x, layout.y, layout.width, layout.height);
  ctx.clip();

  const half = 4;
  const yCenter = layout.y + half + 1;

  for (const m of markers) {
    const px = toPixelX(m.timestampUs, vp, labelWidth);
    if (px < layout.x - half || px > layout.x + layout.width + half) continue;

    ctx.fillStyle = m.color;
    ctx.beginPath();
    if (m.shape === 'triangle-up') {
      ctx.moveTo(px, yCenter - half);
      ctx.lineTo(px - half, yCenter + half);
      ctx.lineTo(px + half, yCenter + half);
      ctx.closePath();
    } else if (m.shape === 'triangle-down') {
      ctx.moveTo(px, yCenter + half);
      ctx.lineTo(px - half, yCenter - half);
      ctx.lineTo(px + half, yCenter - half);
      ctx.closePath();
    } else {
      ctx.arc(px, yCenter, half * 0.6, 0, Math.PI * 2);
    }
    ctx.fill();
  }

  ctx.restore();
}

/**
 * Draw a horizontal reference line across the full track width. Used for fixed capacities
 * (peaks, ceilings) that the dynamic series shouldn't exceed. `dashed` = 4/2 dash pattern.
 */
export function drawReferenceLine(
  ctx: CanvasRenderingContext2D,
  value: number,
  layout: GaugeTrackLayout,
  color: string,
  yMin: number,
  yMax: number,
  dashed: boolean = true,
): void {
  const py = valueToPixelY(value, yMin, yMax, layout);
  if (py < layout.y || py > layout.y + layout.height) return;

  ctx.save();
  ctx.strokeStyle = color;
  ctx.lineWidth = 1;
  if (dashed) ctx.setLineDash([4, 2]);
  ctx.beginPath();
  ctx.moveTo(layout.x, py);
  ctx.lineTo(layout.x + layout.width, py);
  ctx.stroke();
  ctx.restore();
}

/**
 * Draw the Y-axis label strip on the LEFT edge of a track. Emits the max label at the top and
 * (optionally) the min label at the bottom, plus the unit suffix.
 */
export function drawTrackAxis(
  ctx: CanvasRenderingContext2D,
  yMin: number,
  yMax: number,
  unit: GaugeUnit,
  layout: GaugeTrackLayout,
  labelWidth: number,
  color: string,
): void {
  ctx.save();
  ctx.fillStyle = color;
  ctx.font = '10px system-ui, sans-serif';
  ctx.textBaseline = 'top';
  ctx.textAlign = 'right';

  const maxLabel = formatGaugeValue(yMax, unit);
  ctx.fillText(maxLabel, layout.x - 4, layout.y + 1, labelWidth - 6);

  if (yMin !== 0) {
    ctx.textBaseline = 'bottom';
    const minLabel = formatGaugeValue(yMin, unit);
    ctx.fillText(minLabel, layout.x - 4, layout.y + layout.height - 1, labelWidth - 6);
  }

  ctx.restore();
}

/**
 * One entry in an inline legend — coloured swatch + label pair.
 */
export interface InlineLegendItem {
  color: string;
  label: string;
  shape?: 'square' | 'triangle' | 'circle';
}

const _legendLabelWidthCache = new Map<string, number>();

function measureLegendLabel(ctx: CanvasRenderingContext2D, label: string): number {
  const cached = _legendLabelWidthCache.get(label);
  if (cached !== undefined) return cached;
  const w = ctx.measureText(label).width;
  _legendLabelWidthCache.set(label, w);
  return w;
}

/**
 * Draw an inline legend at the top-left corner of a track row — sequence of coloured swatches +
 * labels. Painted after chart content but with a translucent backdrop so the overlay doesn't
 * hide whatever's underneath entirely. Colours come from `theme` (backdrop uses the same
 * `labelPillBg` as time-area mini-row pills; label text uses `theme.gaugeLegendText`).
 */
export function drawInlineLegend(
  ctx: CanvasRenderingContext2D,
  items: readonly InlineLegendItem[],
  layout: GaugeTrackLayout,
  theme: StudioTheme,
): void {
  if (items.length === 0) return;

  ctx.save();
  ctx.font = '10px system-ui, sans-serif';
  ctx.textBaseline = 'top';
  ctx.textAlign = 'left';

  const padX = 4;
  const padY = 5;
  const swatchSize = 8;
  const swatchToText = 4;
  const itemGap = 10;

  let totalWidth = 0;
  for (const item of items) {
    totalWidth += swatchSize + swatchToText + measureLegendLabel(ctx, item.label) + itemGap;
  }
  totalWidth -= itemGap;

  ctx.fillStyle = theme.labelPillBg;
  ctx.fillRect(layout.x + padX - 2, layout.y + padY - 1, totalWidth + 4, 11);

  let x = layout.x + padX;
  const y = layout.y + padY;
  const textY = y + (11 - 10) / 2;

  for (const item of items) {
    ctx.fillStyle = item.color;
    const shape = item.shape ?? 'square';
    const cx = x + swatchSize / 2;
    const cy = y + 1 + swatchSize / 2;
    if (shape === 'square') {
      ctx.fillRect(x, y + 1, swatchSize, swatchSize);
    } else if (shape === 'triangle') {
      ctx.beginPath();
      ctx.moveTo(cx, y + 1);
      ctx.lineTo(x, y + 1 + swatchSize);
      ctx.lineTo(x + swatchSize, y + 1 + swatchSize);
      ctx.closePath();
      ctx.fill();
    } else {
      ctx.beginPath();
      ctx.arc(cx, cy, swatchSize / 2, 0, Math.PI * 2);
      ctx.fill();
    }
    x += swatchSize + swatchToText;

    ctx.fillStyle = theme.gaugeLegendText;
    ctx.fillText(item.label, x, textY);
    x += measureLegendLabel(ctx, item.label) + itemGap;
  }

  ctx.restore();
}

/**
 * Format a numeric gauge value with adaptive units. Rules mirror the physical scale:
 * - `bytes` / `bytes-compact`: SI suffix (B, KiB, MiB, GiB, TiB). Compact trims decimals aggressively.
 * - `count`: comma-grouped integer (1,234,567). No unit suffix.
 * - `percent`: percentage with one decimal (42.5%).
 * - `duration`: microseconds → adaptive time string (via `formatDuration`).
 */
export function formatGaugeValue(value: number, unit: GaugeUnit): string {
  if (unit === 'count') {
    return Math.round(value).toLocaleString('en-US');
  }
  if (unit === 'percent') {
    return `${value.toFixed(1)}%`;
  }
  if (unit === 'duration') {
    return formatDuration(value);
  }

  const abs = Math.abs(value);
  const sign = value < 0 ? '-' : '';
  const compact = unit === 'bytes-compact';
  if (abs < 1024) return `${sign}${abs.toFixed(0)}B`;
  if (abs < 1024 * 1024) {
    const k = abs / 1024;
    return `${sign}${k.toFixed(compact ? 0 : (k < 10 ? 2 : k < 100 ? 1 : 0))}KiB`;
  }
  if (abs < 1024 * 1024 * 1024) {
    const m = abs / (1024 * 1024);
    return `${sign}${m.toFixed(compact ? 0 : (m < 10 ? 2 : m < 100 ? 1 : 0))}MiB`;
  }
  if (abs < 1024 * 1024 * 1024 * 1024) {
    const g = abs / (1024 * 1024 * 1024);
    return `${sign}${g.toFixed(compact ? 1 : (g < 10 ? 2 : 1))}GiB`;
  }
  const t = abs / (1024 * 1024 * 1024 * 1024);
  return `${sign}${t.toFixed(compact ? 1 : 2)}TiB`;
}

/**
 * Build a `MarkerPoint` from a `MemoryAllocEventData`. Palette passed in (typically `theme.gauges`)
 * so source-tag colouring stays consistent with everything else gauge-region paints. `sourceTag`
 * 0 (unattributed) falls back to `theme.mutedForeground`.
 */
export function memoryAllocEventToMarker(
  event: MemoryAllocEventData,
  gaugePalette: readonly string[],
  unattributedColor: string,
): MarkerPoint {
  const color = event.sourceTag === 0
    ? unattributedColor
    : gaugePalette[(event.sourceTag - 1) % gaugePalette.length];
  return {
    timestampUs: event.timestampUs,
    color,
    shape: event.direction === 0 ? 'triangle-up' : 'triangle-down',
  };
}

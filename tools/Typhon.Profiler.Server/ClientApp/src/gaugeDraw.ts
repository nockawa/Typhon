/**
 * Canvas 2D rendering primitives for the gauge region. All functions are stateless and take a <c>layout</c> rect + the shared
 * <c>Viewport</c> so they play the same coordinate game as the rest of <c>GraphArea</c> — no separate chart library, no isolated
 * viewport math. Every series uses <see cref="toPixelX"/> from <c>canvasUtils</c> so gauge samples line up pixel-for-pixel with the
 * span bars drawn on the same X axis.
 *
 * **What lives here vs. the integration layer.** These primitives are pure draw calls — they take already-filtered samples and
 * a numeric Y-range and render. The integration layer (<c>gaugeGroupRenderers.ts</c> + <c>GraphArea.tsx</c>) owns:
 *   (a) picking which samples to pass (binary-search the series by timestamp),
 *   (b) computing per-track Y-range auto-scaling,
 *   (c) placing the tracks in screen coordinates.
 * Keeping that separation means these primitives are trivially unit-testable (no DOM, no cache, no series filtering logic) and
 * the layout code stays in <c>GraphArea.tsx</c>.
 */

import type { Viewport } from './uiTypes';
import type { GaugeSample, MemoryAllocEventData } from './traceModel';
import { toPixelX, GAUGE_PALETTE, formatDuration } from './canvasUtils';

/**
 * A single gauge track's screen rectangle. <c>y</c> is the top edge, <c>height</c> is the drawable height, <c>x</c>/<c>width</c>
 * cover the horizontal extent (the label gutter lives LEFT of <c>x</c>, so <c>x</c> already accounts for the gutter offset). Callers
 * get <c>x + width</c> by adding, which matches the clipping region the primitives use to prevent samples outside the track from
 * bleeding into the ruler or adjacent tracks.
 */
export interface GaugeTrackLayout {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Convert a gauge value to its pixel Y coordinate inside a track. Assumes linear scaling between <c>yMin</c> and <c>yMax</c>;
 * the Y axis points down (Canvas convention) so higher values map to lower pixel Y. Clamps to the track bounds — an out-of-range
 * value clips at the top or bottom rather than drawing outside the layout.
 */
function valueToPixelY(value: number, yMin: number, yMax: number, layout: GaugeTrackLayout): number {
  if (yMax <= yMin) return layout.y + layout.height;  // degenerate range — pin to bottom
  const t = (value - yMin) / (yMax - yMin);
  const clampedT = Math.max(0, Math.min(1, t));
  return layout.y + layout.height - clampedT * layout.height;
}

/**
 * Draw a single gauge series as a line connecting successive samples. Samples are assumed sorted by <c>timestampUs</c>. Extends the
 * first/last sample values horizontally to the layout's left/right edges so a gauge with sparse samples (e.g., the first snapshot
 * arriving near the END of tick 1) still draws a visible line across the entire track — step-function semantics, which matches how
 * gauge state is sampled (piecewise-constant between snapshots, not scattered discrete events).
 */
export function drawLineChart(
  ctx: CanvasRenderingContext2D,
  samples: GaugeSample[],
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

  // Pick the samples that "anchor" the left and right edges of the visible viewport under step-function semantics:
  //   - leftAnchor  = last sample with timestamp <= viewStartUs (the value active at the left edge);
  //   - rightAnchor = last sample with timestamp <= viewEndUs   (the value active at the right edge).
  // Old behavior used samples[0] and samples[samples.length-1] for these extensions — correct only when the viewport covers
  // the full sample time range. When the viewport is narrower than the loaded sample range (common on trace-open: viewport
  // is one tick wide, but the first chunk loads 10+ ticks' worth of samples), using the endpoints of the entire array
  // stretches a polygon from a sample at t=0 to a sample at t=end across a viewport showing t=5–t=10, which visually
  // renders as a flat colored rect that "looks like wrong data." Using viewport-local anchors fixes it.
  const viewStartUs = vp.offsetX;
  const viewEndUs = vp.offsetX + layout.width / Math.max(vp.scaleX, 0.001);
  const leftAnchorIdx = findLeftAnchor(samples, viewStartUs);
  const rightAnchorIdx = findRightAnchor(samples, viewEndUs);
  const firstY = valueToPixelY(samples[leftAnchorIdx].value, yMin, yMax, layout);
  const lastY = valueToPixelY(samples[rightAnchorIdx].value, yMin, yMax, layout);

  // Extend first value leftward to the layout's left edge — step-function assumption: the gauge held this value before the first sample.
  ctx.moveTo(layout.x, firstY);

  for (const s of samples) {
    const px = toPixelX(s.timestampUs, vp, labelWidth);
    const py = valueToPixelY(s.value, yMin, yMax, layout);
    ctx.lineTo(px, py);
  }

  // Extend right-edge anchor value rightward to the layout's right edge — same assumption, forward in time.
  ctx.lineTo(layout.x + layout.width, lastY);

  ctx.stroke();
  ctx.restore();
}

/**
 * Binary-search for the LAST sample with <c>timestampUs &lt;= target</c>. Returns 0 if all samples are after target (so the
 * caller can use samples[0] as the "first known" anchor — a pessimistic step-function assumption, but the only reasonable
 * guess when the gauge had no pre-viewport reading).
 */
function findLeftAnchor(samples: GaugeSample[], target: number): number {
  let lo = 0, hi = samples.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (samples[mid].timestampUs <= target) lo = mid + 1;
    else hi = mid;
  }
  return lo > 0 ? lo - 1 : 0;
}

/**
 * Binary-search for the LAST sample with <c>timestampUs &lt;= target</c>. Returns <c>samples.length - 1</c> if every sample
 * is before <c>target</c> (last value carried forward — correct step-function continuation past the last snapshot).
 */
function findRightAnchor(samples: GaugeSample[], target: number): number {
  let lo = 0, hi = samples.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (samples[mid].timestampUs <= target) lo = mid + 1;
    else hi = mid;
  }
  return lo > 0 ? lo - 1 : 0;
}

/**
 * Draw a gauge series as a filled area down to the baseline (<c>yMin</c>). Good for "amount over time" gauges where the area below
 * the line carries meaning: WAL commit buffer occupancy, unmanaged bytes, etc. Uses a single subpath with closing lineTo calls —
 * no extra allocations.
 */
export function drawAreaChart(
  ctx: CanvasRenderingContext2D,
  samples: GaugeSample[],
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

  // Step-function rendering: the left edge uses the VALUE of the sample "active at viewStart" (last sample with t <= viewStart),
  // the right edge uses the value of the sample "active at viewEnd" (last sample with t <= viewEnd). Using samples[0] /
  // samples[last] here would stretch a polygon from first sample to last sample across a viewport that may be much narrower
  // than the loaded sample range — producing a flat colored rect instead of the actual chart shape. See `drawLineChart`'s
  // comment for the full rationale.
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
  ctx.lineTo(leftX, firstY);           // up to first value at left edge
  for (const s of samples) {
    const px = toPixelX(s.timestampUs, vp, labelWidth);
    const py = valueToPixelY(s.value, yMin, yMax, layout);
    ctx.lineTo(px, py);
  }
  ctx.lineTo(rightX, lastY);           // hold last value forward to right edge
  ctx.lineTo(rightX, baselineY);
  ctx.closePath();
  ctx.fill();

  // Stroke the top edge separately so the baseline and verticals don't get the outline.
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
 * One layer in a stacked-area chart. Series samples must be aligned by <c>timestampUs</c> (same tick boundaries) — the stacker
 * iterates in parallel and sums values at each point. If a layer is short, missing samples are treated as 0.
 */
export interface StackedAreaLayer {
  series: GaugeSample[];
  color: string;
  label: string;
}

/**
 * Draw N gauge series stacked to form a composition chart. Only honest when the input series are mutually-exclusive buckets (e.g.,
 * the page cache's Free / CleanUsed / DirtyUsed / Exclusive pages). For overlapping series use multiple <see cref="drawAreaChart"/>
 * calls with transparency instead — stacked-area misreads those as "piling up."
 *
 * Assumes all series share the same sample count and <c>timestampUs[i]</c>, which is the natural shape for per-tick gauge bundles
 * (one snapshot → N values, one sample per tick per layer). The first series draws at the bottom; each subsequent series stacks on
 * top. Y-range is interpreted as "total stacked sum" — caller computes <c>yMax = max over i of sum(layers[*].series[i].value)</c>.
 */
export function drawStackedArea(
  ctx: CanvasRenderingContext2D,
  layers: StackedAreaLayer[],
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

  // Running per-point offsets — reused across layers to track the top edge of the previous layer at each sample index.
  const pointCount = layers[0].series.length;
  const runningOffsets = new Float64Array(pointCount);
  // Start offsets at 0 (bottom baseline) implicitly — Float64Array default.

  const leftX = layout.x;
  const rightX = layout.x + layout.width;

  // Viewport-anchored indices for edge extensions. See the rationale in `drawLineChart` / `drawAreaChart`: anchoring edges to
  // samples[0] / samples[last] across all layers stretches a flat polygon whenever the loaded sample range is wider than the
  // viewport (common on trace open before the user pans/zooms). We resolve the indices using the FIRST layer's samples —
  // all layers share the same per-tick timestamps, so a single index resolves consistently across them.
  const viewStartUs = vp.offsetX;
  const viewEndUs = vp.offsetX + layout.width / Math.max(vp.scaleX, 0.001);
  const edgeSamples = layers[0].series;
  const leftAnchorIdx = findLeftAnchor(edgeSamples, viewStartUs);
  const rightAnchorIdx = findRightAnchor(edgeSamples, viewEndUs);

  for (const layer of layers) {
    const samples = layer.series;
    if (samples.length === 0) continue;
    const count = Math.min(samples.length, pointCount);

    // Step-function extension: use the viewport-anchor indices (not 0 and count-1) for the left/right edge Y values so the
    // polygon's flat extensions track the value actually active at each viewport edge — not the value of the very first /
    // very last loaded sample, which may be far outside the visible range.
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
    // Upper-edge forward pass — anchored to left edge at first sample's stacked value.
    ctx.moveTo(leftX, firstUpperY);
    for (let i = 0; i < count; i++) {
      const s = samples[i];
      const stackedValue = runningOffsets[i] + s.value;
      const px = toPixelX(s.timestampUs, vp, labelWidth);
      const py = valueToPixelY(stackedValue, yMin, yMax, layout);
      ctx.lineTo(px, py);
    }
    ctx.lineTo(rightX, lastUpperY);
    // Lower-edge backward pass — anchored to right edge at last sample's running offset.
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

    // Update running offsets for the next layer.
    for (let i = 0; i < count; i++) {
      runningOffsets[i] += samples[i].value;
    }
  }

  ctx.restore();
}

/**
 * A discrete point to draw on a track. <c>shape</c> controls the glyph; callers typically use <c>triangle-up</c> for allocations,
 * <c>triangle-down</c> for frees, and <c>dot</c> for neutral events.
 */
export interface MarkerPoint {
  timestampUs: number;
  color: string;
  shape: 'triangle-up' | 'triangle-down' | 'dot';
}

/**
 * Draw discrete markers along a track's top edge. Typical use: memory alloc/free events as small triangles above the heap composition
 * area chart, tick-marks for page evictions, etc. Marker size is fixed at 5 px half-extent — small enough to not clutter a zoomed-out
 * view, large enough to be clickable at realistic densities.
 */
export function drawMarkers(
  ctx: CanvasRenderingContext2D,
  markers: MarkerPoint[],
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
  const yCenter = layout.y + half + 1;  // slight offset from the track top edge

  for (const m of markers) {
    const px = toPixelX(m.timestampUs, vp, labelWidth);
    // Skip markers outside the clip rect — pure perf, the clip would handle visual correctness anyway.
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
 * Draw a horizontal reference line across the full track width. Used for fixed capacities (peaks, ceilings) that the dynamic series
 * shouldn't exceed. <c>dashed</c> renders a 4/2 dash pattern — visually distinguishes it from the live value line.
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
 * Draw the Y-axis label strip on the LEFT edge of a track. Emits the max label at the top and (optionally) the min label at the
 * bottom, plus the unit suffix. Callers control font + color via the canvas context state before calling — no style resets here.
 */
export function drawTrackAxis(
  ctx: CanvasRenderingContext2D,
  yMin: number,
  yMax: number,
  unit: 'bytes' | 'count' | 'percent' | 'bytes-compact' | 'duration',
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
 * One entry in an inline legend — a colored swatch + label pair. Drawn at the top-left of a track row so the reader can identify
 * each series without a separate key or mandatory hover. <c>shape</c> is optional: defaults to a filled square (standard series
 * swatch); pass <c>'triangle'</c> or <c>'circle'</c> to mirror a marker kind used on the track (e.g., GC-start = triangle,
 * GC-end = circle). The shape is drawn at the same size as the square swatch so legend items align vertically.
 */
export interface InlineLegendItem {
  color: string;
  label: string;
  shape?: 'square' | 'triangle' | 'circle';
}

/**
 * Draw an inline legend at the top-left corner of a track row — a sequence of colored swatches + labels laid out horizontally.
 * Intended for multi-series tracks where line colors alone aren't self-explanatory; the legend answers "which color is which?"
 * without requiring the reader to hover. Painted AFTER the chart content so it overlays lines that pass through the legend area,
 * but with a subtle translucent background so the overlay doesn't hide whatever's underneath entirely.
 *
 * Layout: 4 px left padding, 2 px top padding; each item = 8 px swatch + 4 px gap + label text + 10 px gap before next item.
 */
// Module-level text-width cache. Legend labels are static strings (and the font is fixed), so caching results keyed by the
// label string turns N canvas.measureText() calls per frame (each ~10 µs on typical hardware) into one Map lookup per call.
// At 6 gauge tracks × ~4 legend items × 2 measures per item, this saves ~0.5 ms per paint once the cache is warm — meaningful
// when the viewer is in a drag/pan loop running measureText on every rAF. The cache is never invalidated because the legend
// font doesn't change; if that ever becomes configurable, key on `font + label` instead of label alone.
const _legendLabelWidthCache = new Map<string, number>();
function measureLegendLabel(ctx: CanvasRenderingContext2D, label: string): number {
  const cached = _legendLabelWidthCache.get(label);
  if (cached !== undefined) return cached;
  const w = ctx.measureText(label).width;
  _legendLabelWidthCache.set(label, w);
  return w;
}

export function drawInlineLegend(
  ctx: CanvasRenderingContext2D,
  items: InlineLegendItem[],
  layout: GaugeTrackLayout,
): void {
  if (items.length === 0) return;

  ctx.save();
  ctx.font = '10px system-ui, sans-serif';
  ctx.textBaseline = 'top';
  ctx.textAlign = 'left';

  const padX = 4;
  // Bumped from 2 to 5 to match the horizontal margin from the perspective of the track BODY (i.e. after the 2 px accent stripe at
  // layout.y..layout.y+2). Previously the legend background landed at layout.y+1 — inside the stripe — making it look visually "too
  // high" by ~3 px vs. the 2-px left margin the user would expect parity with. `padY = 5` → background top at layout.y+4, i.e.
  // `(stripe-end at +2) + (2 px of margin mirroring the left)`.
  const padY = 5;
  const swatchSize = 8;
  const swatchToText = 4;
  const itemGap = 10;

  // Measure total width so we can paint a subtle translucent background behind it — keeps the legend legible even when a chart
  // line happens to cross through the labels. Using a single fillRect + per-item paint keeps this to two draw phases.
  let totalWidth = 0;
  for (const item of items) {
    totalWidth += swatchSize + swatchToText + measureLegendLabel(ctx, item.label) + itemGap;
  }
  totalWidth -= itemGap;  // no trailing gap after the last item

  ctx.fillStyle = 'rgba(0, 0, 0, 0.35)';
  ctx.fillRect(layout.x + padX - 2, layout.y + padY - 1, totalWidth + 4, 11);

  let x = layout.x + padX;
  const y = layout.y + padY;
  const textY = y + (11 - 10) / 2;  // vertically center 10 px font in the 11 px background strip

  for (const item of items) {
    ctx.fillStyle = item.color;
    const shape = item.shape ?? 'square';
    const cx = x + swatchSize / 2;
    const cy = y + 1 + swatchSize / 2;
    if (shape === 'square') {
      ctx.fillRect(x, y + 1, swatchSize, swatchSize);
    } else if (shape === 'triangle') {
      // Up-pointing triangle inscribed in the swatch box. Matches the on-track GcStart marker shape so the legend reads as a
      // faithful miniature of what the chart draws.
      ctx.beginPath();
      ctx.moveTo(cx, y + 1);
      ctx.lineTo(x, y + 1 + swatchSize);
      ctx.lineTo(x + swatchSize, y + 1 + swatchSize);
      ctx.closePath();
      ctx.fill();
    } else {  // circle
      ctx.beginPath();
      ctx.arc(cx, cy, swatchSize / 2, 0, Math.PI * 2);
      ctx.fill();
    }
    x += swatchSize + swatchToText;

    ctx.fillStyle = '#e0e0e0';
    ctx.fillText(item.label, x, textY);
    x += measureLegendLabel(ctx, item.label) + itemGap;
  }

  ctx.restore();
}

/**
 * Format a numeric gauge value with adaptive units. The format rules mirror the physical scale the gauge is in:
 * - <c>bytes</c> / <c>bytes-compact</c>: SI suffix (B, KiB, MiB, GiB, TiB). <c>bytes-compact</c> trims decimals more aggressively.
 * - <c>count</c>: comma-grouped integer (e.g. 1,234,567). No unit suffix.
 * - <c>percent</c>: percentage with one decimal (e.g. 42.5%).
 *
 * Called from both the drawing code and the hover-tooltip builders — exported so every byte/count/percent display in the
 * viewer goes through the same formatter without rule duplication.
 */
export function formatGaugeValue(value: number, unit: 'bytes' | 'count' | 'percent' | 'bytes-compact' | 'duration'): string {
  if (unit === 'count') {
    // Integer with thousands separators. toFixed(0) first to kill floating-point residue, then toLocaleString for commas.
    return Math.round(value).toLocaleString('en-US');
  }
  if (unit === 'percent') {
    return `${value.toFixed(1)}%`;
  }
  if (unit === 'duration') {
    // Microseconds in, human-readable time string out. Mirrors formatDuration in canvasUtils so axis labels and tooltips use the
    // same language across the viewer.
    return formatDuration(value);
  }

  // Bytes: pick the largest SI suffix that keeps the mantissa between 1 and 1024, then show 1-2 decimals.
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
 * Build a <c>MarkerPoint</c> from a <c>MemoryAllocEventData</c>. Palette is keyed on sourceTag so allocations from the same
 * subsystem colour-code together — good enough that "WAL vs page cache allocations" reads at a glance.
 */
export function memoryAllocEventToMarker(event: MemoryAllocEventData): MarkerPoint {
  // Marker palette: tag 0 ("Unattributed") stays neutral grey to read as "no specific subsystem"; tags 1..N pick from the shared
  // <c>GAUGE_PALETTE</c> so memory-alloc markers draw from the same Viridis language as every other gauge surface. Sourcetag is
  // subsystem-disjoint, so cycling through palette entries produces no visually-confusing collisions within a single session.
  const color = event.sourceTag === 0
    ? '#888'
    : GAUGE_PALETTE[(event.sourceTag - 1) % GAUGE_PALETTE.length];
  return {
    timestampUs: event.timestampUs,
    color,
    shape: event.direction === 0 ? 'triangle-up' : 'triangle-down',
  };
}

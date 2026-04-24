import type {
  GaugeSample,
  GaugeSeries,
  GcEvent,
  GcSuspensionEvent,
  MemoryAllocEventData,
  TickData,
} from '@/libs/profiler/model/traceModel';
import { GaugeId } from '@/libs/profiler/model/types';
import type { Viewport } from '@/libs/profiler/model/uiTypes';
import { CACHE_EXCLUSIVE_COLOR, toPixelX, type TooltipLine } from '../canvasUtils';
import type { StudioTheme } from '../theme';
import {
  drawAreaChart,
  drawInlineLegend,
  drawLineChart,
  drawMarkers,
  drawReferenceLine,
  drawStackedArea,
  drawTrackAxis,
  formatGaugeValue,
  memoryAllocEventToMarker,
  type GaugeTrackLayout,
  type MarkerPoint,
  type StackedAreaLayer,
} from './draw';
import { type GaugeGroupSpec } from './region';

/**
 * Per-category gauge-group rendering — ported from the old profiler's `gaugeGroupRenderers.ts`.
 * Six renderers (GC / Memory / Page Cache / Transient / WAL / Tx+UoW), the summary-strip helper,
 * and the tooltip-line builder all live here because they share private helpers
 * (`findNearestSnapshotTick`, colour constants, delta computation).
 *
 * Inputs arrive via {@link GaugeRenderContext}: `ticks` (for snapshot-based rendering), `gaugeData`
 * (the assembled bundle from the chunk cache — series, capacities, events), `theme` (for chrome
 * colours), plus the shared viewport + layout.
 */

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Context type
// ───────────────────────────────────────────────────────────────────────────────────────────────

export interface GaugeData {
  gaugeSeries: Map<GaugeId, GaugeSeries>;
  gaugeCapacities: Map<GaugeId, number>;
  memoryAllocEvents: readonly MemoryAllocEventData[];
  gcEvents: readonly GcEvent[];
  gcSuspensions: readonly GcSuspensionEvent[];
}

export interface GaugeRenderContext {
  ctx: CanvasRenderingContext2D;
  ticks: readonly TickData[];
  gaugeData: GaugeData;
  vp: Viewport;
  labelWidth: number;
  layout: GaugeTrackLayout;
  spec: GaugeGroupSpec;
  legendsVisible: boolean;
  theme: StudioTheme;
}

type GroupRenderer = (cx: GaugeRenderContext) => void;

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ───────────────────────────────────────────────────────────────────────────────────────────────

function drawLegendIfVisible(
  cx: GaugeRenderContext,
  items: { color: string; label: string; shape?: 'square' | 'triangle' | 'circle' }[],
  layout: GaugeTrackLayout,
): void {
  if (!cx.legendsVisible) return;
  drawInlineLegend(cx.ctx, items, layout, cx.theme);
}

/**
 * Pick a Y-axis max for a single series — max observed value padded by 10%, floor at 1 to avoid
 * degenerate 0..0 range. Stable across zoom (uses all loaded samples, not just visible).
 */
function yMaxFromSeries(series: GaugeSeries | undefined): number {
  if (series === undefined || series.samples.length === 0) return 1;
  let m = 0;
  for (const s of series.samples) {
    if (s.value > m) m = s.value;
  }
  return Math.max(1, m * 1.1);
}

/**
 * Max total stack height across a set of aligned layers — used as Y max for a stacked-area chart.
 * Returns 0 for all-zero layers so the caller can skip render entirely.
 */
function stackedYMax(layers: readonly (readonly GaugeSample[])[]): number {
  if (layers.length === 0 || layers[0].length === 0) return 0;
  let maxSum = 0;
  const n = layers[0].length;
  for (let i = 0; i < n; i++) {
    let s = 0;
    for (const layer of layers) {
      if (i < layer.length) s += layer[i].value;
    }
    if (s > maxSum) maxSum = s;
  }
  return maxSum > 0 ? maxSum * 1.05 : 0;
}

function getSeries(data: GaugeData, id: GaugeId): GaugeSeries | undefined {
  return data.gaugeSeries.get(id);
}

/**
 * Binary-search `ticks` (sorted by tickNumber; snapshot timestamps monotone-non-decreasing) for
 * the snapshot-bearing tick closest to `cursorUs`. Snapshot-less ticks are handled via a bounded
 * outward sweep so O(log N) holds even with sparse snapshots.
 */
function findNearestSnapshotTick(
  ticks: readonly TickData[],
  cursorUs: number,
): TickData | undefined {
  if (ticks.length === 0) return undefined;

  let lo = 0;
  let hi = ticks.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    const snap = ticks[mid].gaugeSnapshot;
    if (snap === undefined || snap.timestampUs < cursorUs) lo = mid + 1;
    else hi = mid;
  }

  let bestTick: TickData | undefined;
  let bestDist = Number.POSITIVE_INFINITY;
  const probe = (i: number): void => {
    if (i < 0 || i >= ticks.length) return;
    const snap = ticks[i].gaugeSnapshot;
    if (snap === undefined) return;
    const d = Math.abs(snap.timestampUs - cursorUs);
    if (d < bestDist) { bestDist = d; bestTick = ticks[i]; }
  };
  for (let i = lo - 1; i >= 0 && i >= lo - 16; i--) {
    probe(i);
    if (ticks[i].gaugeSnapshot !== undefined) break;
  }
  for (let i = lo; i < ticks.length && i <= lo + 16; i++) {
    probe(i);
    if (ticks[i].gaugeSnapshot !== undefined) break;
  }
  return bestTick;
}

function drawNoDataMessage(ctx: CanvasRenderingContext2D, layout: GaugeTrackLayout, message: string, theme: StudioTheme): void {
  ctx.save();
  ctx.fillStyle = theme.gaugeNoDataBg;
  ctx.font = '10px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(message, layout.x + layout.width / 2, layout.y + layout.height / 2, layout.width - 20);
  ctx.restore();
}

/**
 * Pixel-perfect split of a parent track body into N rows with the given weights. Cumulative-
 * weight rounding so boundaries never lose a pixel to rounding accumulation.
 */
function splitRows(parent: GaugeTrackLayout, rowHeights: readonly number[]): GaugeTrackLayout[] {
  const out: GaugeTrackLayout[] = [];
  const startY = parent.y;
  const usableHeight = parent.height + 1;
  const totalWeight = rowHeights.reduce((a, b) => a + b, 0);
  if (totalWeight === 0 || usableHeight <= 0) {
    for (const _h of rowHeights) {
      void _h;
      out.push({ x: parent.x, y: startY, width: parent.width, height: 0 });
    }
    return out;
  }
  let cumWeight = 0;
  let prevBoundary = 0;
  for (const h of rowHeights) {
    cumWeight += h;
    const nextBoundary = Math.round(cumWeight * usableHeight / totalWeight);
    out.push({ x: parent.x, y: startY + prevBoundary, width: parent.width, height: nextBoundary - prevBoundary });
    prevBoundary = nextBoundary;
  }
  return out;
}

function seriesPeak(samples: readonly GaugeSample[]): number {
  let peak = 0;
  for (const s of samples) {
    if (s.value > peak) peak = s.value;
  }
  return peak;
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Memory group
// ───────────────────────────────────────────────────────────────────────────────────────────────

// Heap-gen colours walk cool→warm (Gen0 blue / cheap → POH lime / expensive). Unmanaged sits at
// the dark-purple end off the heap-gen ladder so the two regions don't blend. Peak line uses the
// warmest yellow — "approaching limit" signal.
const HEAP_GEN_COLOR_INDICES = [2, 3, 4, 5, 6] as const;
const UNMANAGED_COLOR_INDEX = 0;
const PEAK_COLOR_INDEX = 7;

function renderMemoryGroup(cx: GaugeRenderContext): void {
  const gauges = cx.theme.gauges;
  const gen0 = getSeries(cx.gaugeData, GaugeId.GcHeapGen0Bytes);
  const gen1 = getSeries(cx.gaugeData, GaugeId.GcHeapGen1Bytes);
  const gen2 = getSeries(cx.gaugeData, GaugeId.GcHeapGen2Bytes);
  const loh = getSeries(cx.gaugeData, GaugeId.GcHeapLohBytes);
  const poh = getSeries(cx.gaugeData, GaugeId.GcHeapPohBytes);
  const unmanaged = getSeries(cx.gaugeData, GaugeId.MemoryUnmanagedTotalBytes);
  const unmanagedPeak = getSeries(cx.gaugeData, GaugeId.MemoryUnmanagedPeakBytes);
  const liveBlocks = getSeries(cx.gaugeData, GaugeId.MemoryUnmanagedLiveBlocks);

  if (gen0 === undefined && unmanaged === undefined) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no data in this trace`, cx.theme);
    return;
  }

  const rows = splitRows(cx.layout, [40, 28, 10]);
  const heapRow = rows[0];
  const unmanagedRow = rows[1];
  const markersRow = rows[2];

  // Row 1: heap composition stacked area
  if (gen0 || gen1 || gen2 || loh || poh) {
    const layers: StackedAreaLayer[] = [];
    if (gen0) layers.push({ series: gen0.samples, color: gauges[HEAP_GEN_COLOR_INDICES[0]], label: 'Gen0' });
    if (gen1) layers.push({ series: gen1.samples, color: gauges[HEAP_GEN_COLOR_INDICES[1]], label: 'Gen1' });
    if (gen2) layers.push({ series: gen2.samples, color: gauges[HEAP_GEN_COLOR_INDICES[2]], label: 'Gen2' });
    if (loh)  layers.push({ series: loh.samples,  color: gauges[HEAP_GEN_COLOR_INDICES[3]], label: 'LOH'  });
    if (poh)  layers.push({ series: poh.samples,  color: gauges[HEAP_GEN_COLOR_INDICES[4]], label: 'POH'  });
    const heapYMax = stackedYMax(layers.map((l) => l.series));
    if (heapYMax > 0) {
      drawStackedArea(cx.ctx, layers, cx.vp, cx.labelWidth, heapRow, 0, heapYMax);
      drawTrackAxis(cx.ctx, 0, heapYMax, 'bytes-compact', heapRow, cx.labelWidth, cx.theme.mutedForeground);
      drawLegendIfVisible(cx, layers.map((l) => ({ color: l.color, label: l.label })), heapRow);
    }
  }

  // Row 2: unmanaged total area + peak dashed
  if (unmanaged) {
    const yMax = yMaxFromSeries(unmanagedPeak ?? unmanaged);
    const unmanagedColor = gauges[UNMANAGED_COLOR_INDEX];
    const peakColor = gauges[PEAK_COLOR_INDEX];
    drawAreaChart(cx.ctx, unmanaged.samples, cx.vp, cx.labelWidth, unmanagedRow,
      unmanagedColor + '80', unmanagedColor, 0, yMax);

    if (unmanagedPeak && unmanagedPeak.samples.length > 0) {
      const peakValue = unmanagedPeak.samples[unmanagedPeak.samples.length - 1].value;
      drawReferenceLine(cx.ctx, peakValue, unmanagedRow, peakColor, 0, yMax, true);
    }
    drawTrackAxis(cx.ctx, 0, yMax, 'bytes-compact', unmanagedRow, cx.labelWidth, cx.theme.mutedForeground);
    const row2Legend = [{ color: unmanagedColor, label: 'Unmanaged' }];
    if (unmanagedPeak && unmanagedPeak.samples.length > 0) {
      row2Legend.push({ color: peakColor, label: 'Peak (dashed)' });
    }
    drawLegendIfVisible(cx, row2Legend, unmanagedRow);
  }

  // Row 3: live blocks line + alloc-event markers
  if (liveBlocks) {
    const yMax = yMaxFromSeries(liveBlocks);
    drawLineChart(cx.ctx, liveBlocks.samples, cx.vp, cx.labelWidth, markersRow, cx.theme.gaugeLiveLine, 0, yMax, 1);
  }
  if (cx.gaugeData.memoryAllocEvents.length > 0) {
    const markers: MarkerPoint[] = cx.gaugeData.memoryAllocEvents.map((e) =>
      memoryAllocEventToMarker(e, cx.theme.gauges, cx.theme.mutedForeground),
    );
    drawMarkers(cx.ctx, markers, cx.vp, cx.labelWidth, markersRow);
  }
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Page Cache group
// ───────────────────────────────────────────────────────────────────────────────────────────────

// Bucket colours: Free=navy (empty/cold), Clean=teal (stable), Dirty=mustard (write pending),
// Exclusive=coral-red (contention — off-palette identity constant). Overlay lines use mid-ladder
// hues far from any bucket to stay distinct.
const PAGE_CACHE_FREE_INDEX = 1;
const PAGE_CACHE_CLEAN_INDEX = 3;
const PAGE_CACHE_DIRTY_INDEX = 6;
const PAGE_CACHE_EPOCH_INDEX = 2;
const PAGE_CACHE_IO_INDEX = 5;

function renderPageCacheGroup(cx: GaugeRenderContext): void {
  const gauges = cx.theme.gauges;
  const free = getSeries(cx.gaugeData, GaugeId.PageCacheFreePages);
  const clean = getSeries(cx.gaugeData, GaugeId.PageCacheCleanUsedPages);
  const dirty = getSeries(cx.gaugeData, GaugeId.PageCacheDirtyUsedPages);
  const exclusive = getSeries(cx.gaugeData, GaugeId.PageCacheExclusivePages);
  const epoch = getSeries(cx.gaugeData, GaugeId.PageCacheEpochProtectedPages);
  const pendingIo = getSeries(cx.gaugeData, GaugeId.PageCachePendingIoReads);
  const totalCapacity = cx.gaugeData.gaugeCapacities.get(GaugeId.PageCacheTotalPages);

  if (!free && !clean && !dirty && !exclusive) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no data in this trace`, cx.theme);
    return;
  }

  const rows = splitRows(cx.layout, [40, 20]);
  const stackRow = rows[0];
  const overlayRow = rows[1];

  // Row 1: bucket stacked area
  const layers: StackedAreaLayer[] = [];
  if (free)      layers.push({ series: free.samples,      color: gauges[PAGE_CACHE_FREE_INDEX],  label: 'Free'      });
  if (clean)     layers.push({ series: clean.samples,     color: gauges[PAGE_CACHE_CLEAN_INDEX], label: 'Clean'     });
  if (dirty)     layers.push({ series: dirty.samples,     color: gauges[PAGE_CACHE_DIRTY_INDEX], label: 'Dirty'     });
  if (exclusive) layers.push({ series: exclusive.samples, color: CACHE_EXCLUSIVE_COLOR,          label: 'Exclusive' });

  const stackYMax = totalCapacity && totalCapacity > 0 ? totalCapacity : stackedYMax(layers.map((l) => l.series));
  if (stackYMax > 0) {
    drawStackedArea(cx.ctx, layers, cx.vp, cx.labelWidth, stackRow, 0, stackYMax);
    drawTrackAxis(cx.ctx, 0, stackYMax, 'count', stackRow, cx.labelWidth, cx.theme.mutedForeground);
    drawLegendIfVisible(cx, layers.map((l) => ({ color: l.color, label: l.label })), stackRow);
  }

  // Row 2: overlay lines
  const yMax2 = Math.max(yMaxFromSeries(epoch), yMaxFromSeries(pendingIo));
  if (epoch)     drawLineChart(cx.ctx, epoch.samples,     cx.vp, cx.labelWidth, overlayRow, gauges[PAGE_CACHE_EPOCH_INDEX], 0, yMax2, 1.25);
  if (pendingIo) drawLineChart(cx.ctx, pendingIo.samples, cx.vp, cx.labelWidth, overlayRow, gauges[PAGE_CACHE_IO_INDEX],    0, yMax2, 1.25);
  const row2Items: { color: string; label: string }[] = [];
  if (epoch)     row2Items.push({ color: gauges[PAGE_CACHE_EPOCH_INDEX], label: 'Epoch-protected' });
  if (pendingIo) row2Items.push({ color: gauges[PAGE_CACHE_IO_INDEX],    label: 'Pending I/O' });
  if (row2Items.length > 0) drawLegendIfVisible(cx, row2Items, overlayRow);
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// GC group
// ───────────────────────────────────────────────────────────────────────────────────────────────

// Cool→warm ladder: Gen0 teal (frequent/fast), Gen1 green (moderate), Gen2+ yellow (STW likely).
const GC_GEN_COLOR_INDICES: Record<number, number> = {
  0: 3,
  1: 5,
  2: 7,
};

function gcGenColor(generation: number, gauges: readonly string[]): string {
  const idx = GC_GEN_COLOR_INDICES[generation] ?? 7;
  return gauges[idx];
}

// GC pause-per-tick bar colour. Identity constant — deliberately off the Viridis palette so pause
// bars can't be confused with generation-specific markers. Same literal as PageCache Exclusive.
const GC_PAUSE_COLOR = CACHE_EXCLUSIVE_COLOR;

/**
 * Per-tick GC pause-time series, memoised per tick array identity. For each tick, sums the
 * overlap (µs) of every GcSuspension span with that tick's time window. Two-pointer O(T + S).
 */
const _gcPauseSeriesCache = new WeakMap<object, GaugeSample[]>();
function getPerTickPauseSeries(ticks: readonly TickData[], suspensions: readonly GcSuspensionEvent[]): GaugeSample[] {
  const cached = _gcPauseSeriesCache.get(ticks);
  if (cached) return cached;

  const result: GaugeSample[] = [];
  if (ticks.length === 0) {
    _gcPauseSeriesCache.set(ticks, result);
    return result;
  }

  let sIdx = 0;
  for (let tIdx = 0; tIdx < ticks.length; tIdx++) {
    const tick = ticks[tIdx];
    const tickStart = tick.startUs;
    const tickEnd = tick.endUs;
    let totalPauseUs = 0;
    for (let i = sIdx; i < suspensions.length && suspensions[i].startUs < tickEnd; i++) {
      const s = suspensions[i];
      const sEnd = s.startUs + s.durationUs;
      if (sEnd > tickStart) {
        const overlapStart = Math.max(s.startUs, tickStart);
        const overlapEnd = Math.min(sEnd, tickEnd);
        totalPauseUs += overlapEnd - overlapStart;
      }
    }
    while (sIdx < suspensions.length && suspensions[sIdx].startUs + suspensions[sIdx].durationUs <= tickEnd) {
      sIdx++;
    }
    result.push({ tickNumber: tick.tickNumber, timestampUs: tick.startUs, value: totalPauseUs });
  }

  _gcPauseSeriesCache.set(ticks, result);
  return result;
}

function renderGcGroup(cx: GaugeRenderContext): void {
  const gcEvents = cx.gaugeData.gcEvents;
  const gcSuspensions = cx.gaugeData.gcSuspensions;

  if (gcEvents.length === 0 && gcSuspensions.length === 0) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no GC activity captured (enable Typhon:Telemetry:Profiler:GC)`, cx.theme);
    return;
  }

  const [bodyRow] = splitRows(cx.layout, [1]);

  // Primary: per-tick pause-time bars.
  const pauseSeries = getPerTickPauseSeries(cx.ticks, gcSuspensions);
  if (pauseSeries.length > 0 && cx.ticks.length === pauseSeries.length) {
    let maxPause = 0;
    for (const s of pauseSeries) if (s.value > maxPause) maxPause = s.value;
    if (maxPause > 0) {
      const yMax = maxPause * 1.1;
      const viewStartUs = cx.vp.offsetX;
      const viewEndUs = cx.vp.offsetX + bodyRow.width / Math.max(cx.vp.scaleX, 0.001);
      const bodyBottom = bodyRow.y + bodyRow.height;

      cx.ctx.save();
      cx.ctx.beginPath();
      cx.ctx.rect(bodyRow.x, bodyRow.y, bodyRow.width, bodyRow.height);
      cx.ctx.clip();
      cx.ctx.fillStyle = GC_PAUSE_COLOR;

      for (let i = 0; i < cx.ticks.length; i++) {
        const pause = pauseSeries[i].value;
        if (pause <= 0) continue;
        const tick = cx.ticks[i];
        if (tick.endUs < viewStartUs) continue;
        if (tick.startUs > viewEndUs) break;
        const x1 = toPixelX(tick.startUs, cx.vp, cx.labelWidth);
        const x2 = toPixelX(tick.endUs, cx.vp, cx.labelWidth);
        const barW = Math.max(1, x2 - x1);
        const ratio = pause / yMax;
        const barH = ratio * bodyRow.height;
        const yTop = bodyBottom - barH;
        cx.ctx.fillRect(x1, yTop, barW, barH);
      }
      cx.ctx.restore();

      drawTrackAxis(cx.ctx, 0, yMax, 'duration', bodyRow, cx.labelWidth, cx.theme.mutedForeground);
    }
  }

  // Markers: GcStart triangles + GcEnd dots, colour-coded by generation.
  if (gcEvents.length > 0) {
    cx.ctx.save();
    cx.ctx.beginPath();
    cx.ctx.rect(bodyRow.x, bodyRow.y, bodyRow.width, bodyRow.height);
    cx.ctx.clip();
    const markerY = bodyRow.y + 5;
    for (const e of gcEvents) {
      const px = toPixelX(e.timestampUs, cx.vp, cx.labelWidth);
      if (px < bodyRow.x - 4 || px > bodyRow.x + bodyRow.width + 4) continue;
      cx.ctx.fillStyle = gcGenColor(e.generation, cx.theme.gauges);
      cx.ctx.beginPath();
      if (e.kind === 7 /* GcStart */) {
        cx.ctx.moveTo(px, markerY - 4);
        cx.ctx.lineTo(px - 4, markerY + 4);
        cx.ctx.lineTo(px + 4, markerY + 4);
        cx.ctx.closePath();
      } else {
        cx.ctx.arc(px, markerY, 3, 0, Math.PI * 2);
      }
      cx.ctx.fill();
    }
    cx.ctx.restore();
  }

  // Legend — the shape-key entries use theme.gaugeLegendText so "shape alone conveys meaning".
  drawLegendIfVisible(cx, [
    { color: GC_PAUSE_COLOR,          label: 'pause / tick' },
    { color: cx.theme.gaugeLegendText, label: 'start', shape: 'triangle' },
    { color: cx.theme.gaugeLegendText, label: 'end',   shape: 'circle' },
    { color: gcGenColor(0, cx.theme.gauges), label: 'Gen0' },
    { color: gcGenColor(1, cx.theme.gauges), label: 'Gen1' },
    { color: gcGenColor(2, cx.theme.gauges), label: 'Gen2+' },
  ], cx.layout);
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Transient Store group
// ───────────────────────────────────────────────────────────────────────────────────────────────

function renderTransientGroup(cx: GaugeRenderContext): void {
  const used = getSeries(cx.gaugeData, GaugeId.TransientStoreBytesUsed);
  const maxBytes = cx.gaugeData.gaugeCapacities.get(GaugeId.TransientStoreMaxBytes);

  if (!used) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no transient storage in use`, cx.theme);
    return;
  }

  const [bodyRow] = splitRows(cx.layout, [1]);
  const yMax = maxBytes && maxBytes > 0 ? maxBytes : yMaxFromSeries(used);
  drawAreaChart(cx.ctx, used.samples, cx.vp, cx.labelWidth, bodyRow,
    cx.spec.accentColor + '80', cx.spec.accentColor, 0, yMax);
  if (maxBytes && maxBytes > 0) {
    drawReferenceLine(cx.ctx, maxBytes, bodyRow, cx.spec.accentColor, 0, yMax, true);
  }
  drawTrackAxis(cx.ctx, 0, yMax, 'bytes-compact', bodyRow, cx.labelWidth, cx.theme.mutedForeground);
  const legend = [{ color: cx.spec.accentColor, label: 'Used' }];
  if (maxBytes && maxBytes > 0) legend.push({ color: cx.spec.accentColor, label: 'Max (dashed)' });
  drawLegendIfVisible(cx, legend, cx.layout);
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// WAL group
// ───────────────────────────────────────────────────────────────────────────────────────────────

// Cool fills for "current value", warm dashed for "reference limit". Same convention as Memory.
const WAL_COMMIT_BUFFER_INDEX = 1; // navy — buffer area
const WAL_CAPACITY_INDEX = 7;      // yellow — dashed capacity
const WAL_INFLIGHT_INDEX = 0;      // indigo-purple — inflight line
const WAL_STAGING_INDEX = 4;       // green — staging rented
const WAL_STAGING_PEAK_INDEX = 7;  // yellow — staging-peak dashed

function renderWalGroup(cx: GaugeRenderContext): void {
  const gauges = cx.theme.gauges;
  const commitBuf = getSeries(cx.gaugeData, GaugeId.WalCommitBufferUsedBytes);
  const inflight = getSeries(cx.gaugeData, GaugeId.WalInflightFrames);
  const staging = getSeries(cx.gaugeData, GaugeId.WalStagingPoolRented);
  const stagingPeak = getSeries(cx.gaugeData, GaugeId.WalStagingPoolPeakRented);
  const bufferCapacity = cx.gaugeData.gaugeCapacities.get(GaugeId.WalCommitBufferCapacityBytes);

  if (!commitBuf && !inflight && !staging) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — enable WAL (DurabilityMode ≠ None) for WAL gauges`, cx.theme);
    return;
  }

  const rows = splitRows(cx.layout, [50, 28]);
  const bufferRow = rows[0];
  const overlayRow = rows[1];

  // Row 1: commit-buffer area + optional capacity dashed
  if (commitBuf) {
    const dataMax = yMaxFromSeries(commitBuf);
    const yMax = Math.max(1, dataMax * 1.1);
    const capacityInRange = bufferCapacity !== undefined && bufferCapacity > 0 && bufferCapacity <= yMax;
    const commitColor = gauges[WAL_COMMIT_BUFFER_INDEX];
    const capacityColor = gauges[WAL_CAPACITY_INDEX];
    drawAreaChart(cx.ctx, commitBuf.samples, cx.vp, cx.labelWidth, bufferRow,
      commitColor + '80', commitColor, 0, yMax);
    if (capacityInRange) {
      drawReferenceLine(cx.ctx, bufferCapacity, bufferRow, capacityColor, 0, yMax, true);
    }
    drawTrackAxis(cx.ctx, 0, yMax, 'bytes-compact', bufferRow, cx.labelWidth, cx.theme.mutedForeground);
    const row1Legend = [{ color: commitColor, label: 'Commit buffer' }];
    if (capacityInRange) row1Legend.push({ color: capacityColor, label: 'Capacity (dashed)' });
    drawLegendIfVisible(cx, row1Legend, bufferRow);
  }

  // Row 2: inflight + staging rented + peak dashed
  if (inflight || staging) {
    const peakValue = (stagingPeak && stagingPeak.samples.length > 0)
      ? stagingPeak.samples[stagingPeak.samples.length - 1].value
      : 0;
    const dataMax = Math.max(yMaxFromSeries(inflight), yMaxFromSeries(staging), peakValue);
    const yMax2 = Math.max(1, dataMax * 1.1);
    const inflightColor = gauges[WAL_INFLIGHT_INDEX];
    const stagingColor = gauges[WAL_STAGING_INDEX];
    const stagingPeakColor = gauges[WAL_STAGING_PEAK_INDEX];
    if (inflight) drawLineChart(cx.ctx, inflight.samples, cx.vp, cx.labelWidth, overlayRow, inflightColor, 0, yMax2, 1.25);
    if (staging)  drawLineChart(cx.ctx, staging.samples,  cx.vp, cx.labelWidth, overlayRow, stagingColor,  0, yMax2, 1.25);
    if (stagingPeak && stagingPeak.samples.length > 0) {
      drawReferenceLine(cx.ctx, peakValue, overlayRow, stagingPeakColor, 0, yMax2, true);
    }
    drawTrackAxis(cx.ctx, 0, yMax2, 'count', overlayRow, cx.labelWidth, cx.theme.mutedForeground);
    const row2Legend: { color: string; label: string }[] = [];
    if (inflight) row2Legend.push({ color: inflightColor, label: 'Inflight' });
    if (staging)  row2Legend.push({ color: stagingColor,  label: 'Staging rented' });
    if (stagingPeak && stagingPeak.samples.length > 0) row2Legend.push({ color: stagingPeakColor, label: 'Staging peak (dashed)' });
    if (row2Legend.length > 0) drawLegendIfVisible(cx, row2Legend, overlayRow);
  }
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Tx + UoW group
// ───────────────────────────────────────────────────────────────────────────────────────────────

function deltaSeries(series: GaugeSeries | undefined): GaugeSample[] {
  if (series === undefined || series.samples.length === 0) return [];
  const out: GaugeSample[] = new Array(series.samples.length);
  out[0] = { tickNumber: series.samples[0].tickNumber, timestampUs: series.samples[0].timestampUs, value: 0 };
  for (let i = 1; i < series.samples.length; i++) {
    const prev = series.samples[i - 1].value;
    const cur = series.samples[i].value;
    out[i] = {
      tickNumber: series.samples[i].tickNumber,
      timestampUs: series.samples[i].timestampUs,
      value: Math.max(0, cur - prev),
    };
  }
  return out;
}

// Tx / UoW colours picked from the palette so "cool = live state / creation, warm = commit / rollback".
const TX_ACTIVE_INDEX = 1;        // navy
const TX_CREATED_INDEX = 2;       // blue
const TX_COMMIT_INDEX = 5;        // green
const TX_ROLLBACK_INDEX = 7;      // yellow
const UOW_ACTIVE_INDEX = 0;       // deep indigo
const UOW_CREATED_INDEX = 3;      // teal
const UOW_COMMITTED_INDEX = 6;    // mustard

function renderTxUowGroup(cx: GaugeRenderContext): void {
  const gauges = cx.theme.gauges;
  const txActive = getSeries(cx.gaugeData, GaugeId.TxChainActiveCount);
  const uowActive = getSeries(cx.gaugeData, GaugeId.UowRegistryActiveCount);
  const createdTotal = getSeries(cx.gaugeData, GaugeId.TxChainCreatedTotal);
  const commitTotal = getSeries(cx.gaugeData, GaugeId.TxChainCommitTotal);
  const rollbackTotal = getSeries(cx.gaugeData, GaugeId.TxChainRollbackTotal);
  const uowCreatedTotal = getSeries(cx.gaugeData, GaugeId.UowRegistryCreatedTotal);
  const uowCommittedTotal = getSeries(cx.gaugeData, GaugeId.UowRegistryCommittedTotal);

  if (!txActive && !uowActive && !createdTotal && !commitTotal && !rollbackTotal && !uowCreatedTotal && !uowCommittedTotal) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — throughput counters not in trace`, cx.theme);
    return;
  }

  const createdThisTick = deltaSeries(createdTotal);
  const commitsThisTick = deltaSeries(commitTotal);
  const rollbacksThisTick = deltaSeries(rollbackTotal);
  const uowCreatedThisTick = deltaSeries(uowCreatedTotal);
  const uowCommittedThisTick = deltaSeries(uowCommittedTotal);

  const txPeak = Math.max(
    txActive ? seriesPeak(txActive.samples) : 0,
    seriesPeak(createdThisTick), seriesPeak(commitsThisTick), seriesPeak(rollbacksThisTick),
  );
  const uowPeak = Math.max(
    uowActive ? seriesPeak(uowActive.samples) : 0,
    seriesPeak(uowCreatedThisTick), seriesPeak(uowCommittedThisTick),
  );
  const txYMax = Math.max(1, txPeak * 1.1);
  const uowYMax = Math.max(1, uowPeak * 1.1);

  const rows = splitRows(cx.layout, [38, 38]);
  const txRow = rows[0];
  const uowRow = rows[1];

  // Row 1: Tx
  const txActiveColor = gauges[TX_ACTIVE_INDEX];
  const txCreatedColor = gauges[TX_CREATED_INDEX];
  const txCommitColor = gauges[TX_COMMIT_INDEX];
  const txRollbackColor = gauges[TX_ROLLBACK_INDEX];
  if (txActive && seriesPeak(txActive.samples) > 0) {
    drawAreaChart(cx.ctx, txActive.samples, cx.vp, cx.labelWidth, txRow,
      txActiveColor + '55', txActiveColor, 0, txYMax, 1);
  }
  if (seriesPeak(createdThisTick) > 0) drawLineChart(cx.ctx, createdThisTick, cx.vp, cx.labelWidth, txRow, txCreatedColor, 0, txYMax, 1.5);
  if (seriesPeak(commitsThisTick) > 0) drawLineChart(cx.ctx, commitsThisTick, cx.vp, cx.labelWidth, txRow, txCommitColor, 0, txYMax, 1.5);
  if (seriesPeak(rollbacksThisTick) > 0) drawLineChart(cx.ctx, rollbacksThisTick, cx.vp, cx.labelWidth, txRow, txRollbackColor, 0, txYMax, 1.5);
  drawTrackAxis(cx.ctx, 0, txYMax, 'count', txRow, cx.labelWidth, cx.theme.mutedForeground);
  drawLegendIfVisible(cx, [
    { color: txActiveColor, label: 'active' },
    { color: txCreatedColor, label: 'created/tick' },
    { color: txCommitColor, label: 'commits/tick' },
    { color: txRollbackColor, label: 'rollbacks/tick' },
  ], txRow);

  // Row 2: UoW
  const uowActiveColor = gauges[UOW_ACTIVE_INDEX];
  const uowCreatedColor = gauges[UOW_CREATED_INDEX];
  const uowCommittedColor = gauges[UOW_COMMITTED_INDEX];
  if (uowActive && seriesPeak(uowActive.samples) > 0) {
    drawAreaChart(cx.ctx, uowActive.samples, cx.vp, cx.labelWidth, uowRow,
      uowActiveColor + '55', uowActiveColor, 0, uowYMax, 1);
  }
  if (seriesPeak(uowCreatedThisTick) > 0) drawLineChart(cx.ctx, uowCreatedThisTick, cx.vp, cx.labelWidth, uowRow, uowCreatedColor, 0, uowYMax, 1.5);
  if (seriesPeak(uowCommittedThisTick) > 0) drawLineChart(cx.ctx, uowCommittedThisTick, cx.vp, cx.labelWidth, uowRow, uowCommittedColor, 0, uowYMax, 1.5);
  drawTrackAxis(cx.ctx, 0, uowYMax, 'count', uowRow, cx.labelWidth, cx.theme.mutedForeground);
  drawLegendIfVisible(cx, [
    { color: uowActiveColor, label: 'active' },
    { color: uowCreatedColor, label: 'created/tick' },
    { color: uowCommittedColor, label: 'committed/tick' },
  ], uowRow);
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Dispatch map + summary strip
// ───────────────────────────────────────────────────────────────────────────────────────────────

export const GROUP_RENDERERS: Record<string, GroupRenderer> = {
  'gauge-gc': renderGcGroup,
  'gauge-memory': renderMemoryGroup,
  'gauge-persistence': renderPageCacheGroup,
  'gauge-transient': renderTransientGroup,
  'gauge-wal': renderWalGroup,
  'gauge-tx-uow': renderTxUowGroup,
};

const SUMMARY_PRIMARY_GAUGE: Record<string, GaugeId> = {
  'gauge-gc':          GaugeId.GcHeapCommittedBytes,
  'gauge-memory':      GaugeId.MemoryUnmanagedTotalBytes,
  'gauge-persistence': GaugeId.PageCacheDirtyUsedPages,
  'gauge-transient':   GaugeId.TransientStoreBytesUsed,
  'gauge-wal':         GaugeId.WalCommitBufferUsedBytes,
  'gauge-tx-uow':      GaugeId.TxChainActiveCount,
};

export function drawGaugeSummaryStrip(
  ctx: CanvasRenderingContext2D,
  gaugeData: GaugeData,
  trackId: string,
  vp: Viewport,
  layout: GaugeTrackLayout,
  labelWidth: number,
  theme: StudioTheme,
): void {
  const gaugeId = SUMMARY_PRIMARY_GAUGE[trackId];
  if (gaugeId === undefined) return;
  const series = getSeries(gaugeData, gaugeId);
  if (series === undefined || series.samples.length === 0) return;
  const yMax = yMaxFromSeries(series);
  if (yMax <= 0) return;
  const inset: GaugeTrackLayout = { x: layout.x, y: layout.y + 1, width: layout.width, height: layout.height - 2 };
  drawLineChart(ctx, series.samples, vp, labelWidth, inset, theme.gaugeSparkline, 0, yMax, 1);
}

// ───────────────────────────────────────────────────────────────────────────────────────────────
// Tooltip infrastructure — sub-row dispatch + per-group builders
// ───────────────────────────────────────────────────────────────────────────────────────────────

interface TooltipGaugeSpec {
  id: GaugeId;
  label: string;
  unit: 'bytes' | 'count' | 'percent' | 'bytes-compact';
  /** Colour to tint the tooltip line — resolved against the active palette at build time. */
  colorIdx?: number;
  /** Literal colour override (used for the Page-Cache Exclusive identity constant). */
  colorLiteral?: string;
}

const TOOLTIP_GAUGES: Record<string, TooltipGaugeSpec[]> = {
  'gauge-memory-heap': [
    { id: GaugeId.GcHeapGen0Bytes, label: 'Gen0', unit: 'bytes-compact', colorIdx: 2 },
    { id: GaugeId.GcHeapGen1Bytes, label: 'Gen1', unit: 'bytes-compact', colorIdx: 3 },
    { id: GaugeId.GcHeapGen2Bytes, label: 'Gen2', unit: 'bytes-compact', colorIdx: 4 },
    { id: GaugeId.GcHeapLohBytes,  label: 'LOH',  unit: 'bytes-compact', colorIdx: 5 },
    { id: GaugeId.GcHeapPohBytes,  label: 'POH',  unit: 'bytes-compact', colorIdx: 6 },
  ],
  'gauge-memory-unmanaged': [
    { id: GaugeId.MemoryUnmanagedTotalBytes, label: 'Unmanaged',   unit: 'bytes-compact', colorIdx: 0 },
    { id: GaugeId.MemoryUnmanagedPeakBytes,  label: 'Peak',        unit: 'bytes-compact', colorIdx: 7 },
    { id: GaugeId.MemoryUnmanagedLiveBlocks, label: 'Live blocks', unit: 'count' },
  ],
  'gauge-persistence-buckets': [
    { id: GaugeId.PageCacheFreePages,      label: 'Free',      unit: 'count', colorIdx: 1 },
    { id: GaugeId.PageCacheCleanUsedPages, label: 'Clean',     unit: 'count', colorIdx: 3 },
    { id: GaugeId.PageCacheDirtyUsedPages, label: 'Dirty',     unit: 'count', colorIdx: 6 },
    { id: GaugeId.PageCacheExclusivePages, label: 'Exclusive', unit: 'count', colorLiteral: CACHE_EXCLUSIVE_COLOR },
  ],
  'gauge-persistence-overlay': [
    { id: GaugeId.PageCacheEpochProtectedPages, label: 'Epoch-held',  unit: 'count', colorIdx: 2 },
    { id: GaugeId.PageCachePendingIoReads,      label: 'Pending I/O', unit: 'count', colorIdx: 5 },
  ],
  'gauge-transient': [
    { id: GaugeId.TransientStoreBytesUsed, label: 'Used', unit: 'bytes-compact', colorIdx: 1 },
  ],
  'gauge-wal-buffer': [
    { id: GaugeId.WalCommitBufferUsedBytes, label: 'Commit buffer', unit: 'bytes-compact', colorIdx: 1 },
  ],
  'gauge-wal-pool': [
    { id: GaugeId.WalInflightFrames,        label: 'Inflight',       unit: 'count', colorIdx: 0 },
    { id: GaugeId.WalStagingPoolRented,     label: 'Staging rented', unit: 'count', colorIdx: 4 },
    { id: GaugeId.WalStagingPoolPeakRented, label: 'Staging peak',   unit: 'count', colorIdx: 7 },
  ],
};

const TOOLTIP_CAPACITY_FOR_GROUP: Record<string, GaugeId | undefined> = {
  'gauge-persistence-buckets': GaugeId.PageCacheTotalPages,
  'gauge-transient':           GaugeId.TransientStoreMaxBytes,
  'gauge-wal-buffer':          GaugeId.WalCommitBufferCapacityBytes,
  'gauge-wal-pool':            GaugeId.WalStagingPoolCapacity,
};

function memorySubRowKey(localY: number, trackHeight: number): string {
  const weights = [40, 28, 10];
  const total = weights[0] + weights[1] + weights[2];
  const usableHeight = trackHeight + 1;
  const boundary1 = Math.round((weights[0] / total) * usableHeight);
  const boundary2 = Math.round(((weights[0] + weights[1]) / total) * usableHeight);
  if (localY < boundary1) return 'gauge-memory-heap';
  if (localY < boundary2) return 'gauge-memory-unmanaged';
  return 'gauge-memory-unmanaged';
}

function persistenceSubRowKey(localY: number, trackHeight: number): string {
  const weights = [40, 20];
  const total = weights[0] + weights[1];
  const usableHeight = trackHeight + 1;
  const boundary1 = Math.round((weights[0] / total) * usableHeight);
  if (localY < boundary1) return 'gauge-persistence-buckets';
  return 'gauge-persistence-overlay';
}

function walSubRowKey(localY: number, trackHeight: number): string {
  const weights = [50, 28];
  const total = weights[0] + weights[1];
  const usableHeight = trackHeight + 1;
  const boundary1 = Math.round((weights[0] / total) * usableHeight);
  if (localY < boundary1) return 'gauge-wal-buffer';
  return 'gauge-wal-pool';
}

function txUowSubRowKey(localY: number, trackHeight: number): 'tx' | 'uow' {
  const weights = [38, 38];
  const total = weights[0] + weights[1];
  const usableHeight = trackHeight + 1;
  const boundary1 = Math.round((weights[0] / total) * usableHeight);
  if (localY < boundary1) return 'tx';
  return 'uow';
}

/**
 * Build tooltip lines for a gauge-group hover. Dispatches to per-group builders based on
 * `trackId` + (when supplied) the sub-row derived from `localY` / `trackHeight`.
 */
export function buildGaugeTooltipLines(
  ticks: readonly TickData[],
  gaugeData: GaugeData,
  trackId: string,
  groupLabel: string,
  cursorUs: number,
  theme: StudioTheme,
  localY?: number,
  trackHeight?: number,
): TooltipLine[] {
  if (trackId === 'gauge-tx-uow') {
    const section: 'tx' | 'uow' | 'both' = (localY !== undefined && trackHeight !== undefined)
      ? txUowSubRowKey(localY, trackHeight)
      : 'both';
    const subLabel =
      section === 'tx' ? 'Tx+UoW — Transactions' :
      section === 'uow' ? 'Tx+UoW — UoW' :
      groupLabel;
    return buildTxUowTooltipLines(ticks, subLabel, cursorUs, section, theme);
  }
  if (trackId === 'gauge-memory' && localY !== undefined && trackHeight !== undefined) {
    trackId = memorySubRowKey(localY, trackHeight);
    groupLabel = trackId === 'gauge-memory-heap' ? 'Memory — Heap' : 'Memory — Unmanaged';
  }
  if (trackId === 'gauge-persistence' && localY !== undefined && trackHeight !== undefined) {
    trackId = persistenceSubRowKey(localY, trackHeight);
    groupLabel = trackId === 'gauge-persistence-buckets' ? 'Page Cache — Buckets' : 'Page Cache — Overlay';
  }
  if (trackId === 'gauge-wal' && localY !== undefined && trackHeight !== undefined) {
    trackId = walSubRowKey(localY, trackHeight);
    groupLabel = trackId === 'gauge-wal-buffer' ? 'WAL — Commit Buffer' : 'WAL — Pool';
  }
  if (trackId === 'gauge-gc') {
    return buildGcTooltipLines(ticks, gaugeData, groupLabel, cursorUs, theme);
  }

  const specs = TOOLTIP_GAUGES[trackId];
  if (specs === undefined) return [];

  const bestTick = findNearestSnapshotTick(ticks, cursorUs);
  if (bestTick === undefined || bestTick.gaugeSnapshot === undefined) return [];

  const snap = bestTick.gaugeSnapshot;
  const lines: TooltipLine[] = [groupLabel, `Tick: ${snap.tickNumber}`];

  const capacityId = TOOLTIP_CAPACITY_FOR_GROUP[trackId];
  const capacity = capacityId !== undefined ? gaugeData.gaugeCapacities.get(capacityId) : undefined;

  for (const spec of specs) {
    const value = snap.values.get(spec.id);
    if (value === undefined) continue;
    const color = spec.colorLiteral ?? (spec.colorIdx !== undefined ? theme.gauges[spec.colorIdx] : undefined);
    lines.push({ text: `${spec.label}: ${formatGaugeValue(value, spec.unit)}`, color });
  }

  if (capacity !== undefined) {
    const unit = specs[0]?.unit ?? 'count';
    lines.push(`Capacity: ${formatGaugeValue(capacity, unit)}`);
  }

  return lines;
}

function buildGcTooltipLines(
  ticks: readonly TickData[],
  gaugeData: GaugeData,
  groupLabel: string,
  cursorUs: number,
  theme: StudioTheme,
): TooltipLine[] {
  if (ticks.length === 0) return [];

  // Find the tick containing cursorUs.
  let lo = 0;
  let hi = ticks.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (ticks[mid].endUs > cursorUs) hi = mid;
    else lo = mid + 1;
  }
  if (lo >= ticks.length) return [];
  const tick = ticks[lo];
  if (cursorUs < tick.startUs) return [];

  const pauseSeries = getPerTickPauseSeries(ticks, gaugeData.gcSuspensions);
  const pauseSample = pauseSeries[lo];
  const pauseUs = pauseSample !== undefined ? pauseSample.value : 0;

  const starts: number[] = [0, 0, 0];
  const ends: number[]   = [0, 0, 0];
  for (const e of gaugeData.gcEvents) {
    if (e.timestampUs >= tick.endUs) break;
    if (e.timestampUs < tick.startUs) continue;
    const genBucket = e.generation >= 2 ? 2 : e.generation;
    if (e.kind === 7 /* GcStart */) starts[genBucket]++;
    else if (e.kind === 8 /* GcEnd */) ends[genBucket]++;
  }

  let cursorSuspensionUs = 0;
  for (const s of gaugeData.gcSuspensions) {
    if (s.startUs > cursorUs) break;
    if (s.startUs + s.durationUs >= cursorUs) {
      cursorSuspensionUs = s.durationUs;
      break;
    }
  }

  const lines: TooltipLine[] = [
    groupLabel,
    `Tick: ${tick.tickNumber}`,
    { text: `Pause / tick:   ${formatDurationInline(pauseUs)}`, color: GC_PAUSE_COLOR },
  ];

  const starsTotal = starts[0] + starts[1] + starts[2];
  const endsTotal  = ends[0]   + ends[1]   + ends[2];
  if (starsTotal > 0 || endsTotal > 0) {
    lines.push('');
    lines.push('(GC events in this tick)');
    if (starts[0] + ends[0] > 0) lines.push({ text: `Gen0 start/end:  ${starts[0]} / ${ends[0]}`, color: gcGenColor(0, theme.gauges) });
    if (starts[1] + ends[1] > 0) lines.push({ text: `Gen1 start/end:  ${starts[1]} / ${ends[1]}`, color: gcGenColor(1, theme.gauges) });
    if (starts[2] + ends[2] > 0) lines.push({ text: `Gen2+ start/end: ${starts[2]} / ${ends[2]}`, color: gcGenColor(2, theme.gauges) });
  }

  if (cursorSuspensionUs > 0) {
    lines.push('');
    lines.push('(cursor is INSIDE a suspension)');
    lines.push(`Pause total:    ${formatDurationInline(cursorSuspensionUs)}`);
  }

  return lines;
}

function formatDurationInline(us: number): string {
  if (us < 1) return '0 µs';
  if (us < 1000) return `${us.toFixed(0)} µs`;
  if (us < 1_000_000) return `${(us / 1000).toFixed(us < 10_000 ? 2 : 1)} ms`;
  return `${(us / 1_000_000).toFixed(2)} s`;
}

function buildTxUowTooltipLines(
  ticks: readonly TickData[],
  groupLabel: string,
  cursorUs: number,
  section: 'tx' | 'uow' | 'both',
  theme: StudioTheme,
): TooltipLine[] {
  const bestTick = findNearestSnapshotTick(ticks, cursorUs);
  if (bestTick === undefined || bestTick.gaugeSnapshot === undefined) return [];
  const cur = bestTick.gaugeSnapshot;
  const bestArrayIdx = ticks.indexOf(bestTick);
  let prev: typeof cur | undefined;
  for (let i = bestArrayIdx - 1; i >= 0; i--) {
    if (ticks[i].gaugeSnapshot !== undefined) {
      prev = ticks[i].gaugeSnapshot;
      break;
    }
  }

  const delta = (id: GaugeId): number | undefined => {
    const curV = cur.values.get(id);
    if (curV === undefined) return undefined;
    const prevV = prev?.values.get(id) ?? 0;
    return Math.max(0, curV - prevV);
  };

  const fmt = (v: number | undefined): string => v === undefined ? '—' : formatGaugeValue(v, 'count');

  const gauges = theme.gauges;
  const txLines = (): TooltipLine[] => {
    const txActive = cur.values.get(GaugeId.TxChainActiveCount);
    const txPool = cur.values.get(GaugeId.TxChainPoolSize);
    const created = delta(GaugeId.TxChainCreatedTotal);
    const commits = delta(GaugeId.TxChainCommitTotal);
    const rollbacks = delta(GaugeId.TxChainRollbackTotal);
    return [
      "(live state — what's alive at this exact tick)",
      { text: `Tx active:       ${fmt(txActive)}`, color: gauges[TX_ACTIVE_INDEX] },
      `Tx pool (idle):  ${fmt(txPool)}`,
      '',
      '(per-tick counts — what happened in this one tick)',
      { text: `Tx created:      ${fmt(created)}`,   color: gauges[TX_CREATED_INDEX] },
      { text: `Tx commits:      ${fmt(commits)}`,   color: gauges[TX_COMMIT_INDEX] },
      { text: `Tx rollbacks:    ${fmt(rollbacks)}`, color: gauges[TX_ROLLBACK_INDEX] },
    ];
  };

  const uowLines = (): TooltipLine[] => {
    const uowActive = cur.values.get(GaugeId.UowRegistryActiveCount);
    const uowVoid = cur.values.get(GaugeId.UowRegistryVoidCount);
    const uowCreated = delta(GaugeId.UowRegistryCreatedTotal);
    const uowCommitted = delta(GaugeId.UowRegistryCommittedTotal);
    return [
      "(live state — what's alive at this exact tick)",
      { text: `UoW active:     ${fmt(uowActive)}`,    color: gauges[UOW_ACTIVE_INDEX] },
      `UoW void:       ${fmt(uowVoid)}`,
      '',
      '(per-tick counts — what happened in this one tick)',
      { text: `UoW created:    ${fmt(uowCreated)}`,   color: gauges[UOW_CREATED_INDEX] },
      { text: `UoW committed:  ${fmt(uowCommitted)}`, color: gauges[UOW_COMMITTED_INDEX] },
    ];
  };

  const header: TooltipLine[] = [groupLabel, `Tick: ${cur.tickNumber}`];
  if (section === 'tx') return [...header, ...txLines()];
  if (section === 'uow') return [...header, ...uowLines()];
  return [...header, ...txLines(), '', ...uowLines()];
}

/**
 * Per-category gauge-group rendering. One function per group ID. Each reads the relevant gauges from <c>ProcessedTrace.gaugeSeries</c>
 * / <c>gaugeCapacities</c> / <c>memoryAllocEvents</c> and calls the <c>gaugeDraw</c> primitives against the shared viewport.
 *
 * **Data coverage.** Engine-side emission is now wired for:
 *   • Memory (unmanaged totals + GC heap + alloc events)                  → full rendering
 *   • Persistent store / page cache (bucket composition + overlays)        → full rendering
 *   • GC heap (via per-tick snapshot)                                      → committed-bytes line
 *   • Transient store (aggregated used bytes across every live store)      → area chart (no per-store Max line — cap is per-store,
 *                                                                            not a shared ceiling, so the Y axis auto-scales)
 *   • WAL + Tx+UoW                                                         → partial (awaiting host-class accessors)
 * Renderers for categories whose engine emission is still pending draw a subdued "no data yet" message so a future reader doesn't
 * hunt for a rendering bug.
 */

import type { ProcessedTrace, GaugeSample, GaugeSeries, MemoryAllocEventData } from './traceModel';
import type { Viewport } from './uiTypes';
import { GaugeId } from './types';
import type { GaugeGroupSpec } from './gaugeRegion';
import { toPixelX, GAUGE_PALETTE } from './canvasUtils';
import type { TooltipLine } from './canvasUtils';
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
} from './gaugeDraw';

/** Dispatch context passed to every group renderer — keeps each function's signature tight. */
export interface GaugeRenderContext {
  ctx: CanvasRenderingContext2D;
  trace: ProcessedTrace;
  vp: Viewport;
  /** Width of the left gutter in pixels; the draw primitives use it when mapping µs → X. */
  labelWidth: number;
  /** Screen rect reserved for this group's content (under the label row, inside the track body). */
  layout: GaugeTrackLayout;
  spec: GaugeGroupSpec;
  /**
   * Whether per-track inline legends ("Gen0 / Gen1 / LOH / …") should be drawn. Toggled by the <b>L</b> keyboard shortcut
   * and the <i>View → Show legends</i> menu item. When false, every <c>drawInlineLegend</c> call is skipped — freeing the top
   * row of each gauge track for an uncluttered view of the data.
   */
  legendsVisible: boolean;
}

/** Function shape for a per-group renderer. All renderers are pure draws — no state persisted between frames. */
type GroupRenderer = (ctx: GaugeRenderContext) => void;

/**
 * Legend-gated wrapper around <see cref="drawInlineLegend"/>. Every per-group renderer uses this instead of calling the primitive
 * directly so the <b>L</b> keyboard shortcut / View menu can hide every legend in one shot by flipping <c>cx.legendsVisible</c>.
 */
function drawLegendIfVisible(
  cx: GaugeRenderContext,
  items: { color: string; label: string; shape?: 'square' | 'triangle' | 'circle' }[],
  layout: GaugeTrackLayout,
): void {
  if (!cx.legendsVisible) return;
  drawInlineLegend(cx.ctx, items, layout);
}

// ═══════════════════════════════════════════════════════════════════════
// Helpers — shared across renderers
// ═══════════════════════════════════════════════════════════════════════

/**
 * Pick a Y-axis max for a single series. Uses the MAX observed value over all loaded samples (not just visible) so the scale stays
 * stable as the user zooms in/out — bars don't visually "grow" when you zoom into a region that happens to contain the peak.
 * Pads the max by 10% so the peak sample doesn't sit at the exact top edge of the track. Returns at least 1 to avoid a
 * degenerate 0..0 range when the series hasn't been sampled yet.
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
 * Sum of max values across a list of series at the same sample index — the Y max for a stacked-area chart. Because the stacker
 * iterates in parallel across series, the "worst-case stack height" at any sample i is sum of layer[*].samples[i].value. We take
 * the max over i. Returns at least 1.
 */
function stackedYMax(layers: GaugeSample[][]): number {
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
  // Return 0 for all-zero series so callers can skip rendering entirely instead of drawing a flat 1-pixel baseline that reads
  // as false signal. Non-zero: auto-scale with the usual 5% headroom.
  return maxSum > 0 ? maxSum * 1.05 : 0;
}

/** Fetch a series from the trace, returning undefined if missing (the group renderer then skips that sub-track cleanly). */
function getSeries(trace: ProcessedTrace, id: GaugeId): GaugeSeries | undefined {
  return trace.gaugeSeries?.get(id);
}

/**
 * Binary-search <paramref name="ticks"/> (sorted by tickNumber, with monotone-non-decreasing snapshot timestamps) for the
 * snapshot-bearing tick whose <c>gaugeSnapshot.timestampUs</c> is closest to <paramref name="cursorUs"/>. Used by the gauge
 * tooltip builders on every hover tick — at 10K+ loaded ticks this is the difference between a smooth tooltip (&lt;1 ms)
 * and visible hitching (~5 ms per hover during a pan-drag).
 *
 * Not every tick has a snapshot (live-mode chunk boundaries can leave gaps); when the candidate tick is snapshot-less we
 * walk outward to the nearest neighbors that do carry a snapshot, within a bounded sweep so this never degrades back to O(N).
 */
function findNearestSnapshotTick(
  ticks: ProcessedTrace['ticks'],
  cursorUs: number,
): ProcessedTrace['ticks'][number] | undefined {
  if (ticks.length === 0) return undefined;

  // Binary-search the sorted tick array for the first tick with snapshot.timestampUs >= cursorUs. For ticks without a
  // snapshot we skip them in the comparison key by probing outward — the loop body below handles the sparse-snapshot case.
  let lo = 0, hi = ticks.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    const snap = ticks[mid].gaugeSnapshot;
    // Ticks without a snapshot are treated as "too early" so the binary search keeps walking right until it finds a snap.
    if (snap === undefined || snap.timestampUs < cursorUs) lo = mid + 1;
    else hi = mid;
  }

  // `lo` is the first tick with snapshot >= cursor. The nearest candidate is either lo (if present) or lo - 1 (nearest with
  // snapshot before cursor). Walk left from lo-1 and right from lo to find the closest snapshot-bearing ticks; compare.
  let bestTick: ProcessedTrace['ticks'][number] | undefined;
  let bestDist = Number.POSITIVE_INFINITY;
  const probe = (i: number) => {
    if (i < 0 || i >= ticks.length) return;
    const snap = ticks[i].gaugeSnapshot;
    if (snap === undefined) return;
    const d = Math.abs(snap.timestampUs - cursorUs);
    if (d < bestDist) { bestDist = d; bestTick = ticks[i]; }
  };
  // Walk left from lo-1 to find the nearest predecessor with a snapshot; stop at the first hit (sparse gaps are rare).
  for (let i = lo - 1; i >= 0 && i >= lo - 16; i--) {
    probe(i);
    if (ticks[i].gaugeSnapshot !== undefined) break;
  }
  // Walk right from lo to find the nearest successor with a snapshot.
  for (let i = lo; i < ticks.length && i <= lo + 16; i++) {
    probe(i);
    if (ticks[i].gaugeSnapshot !== undefined) break;
  }
  return bestTick;
}

/** Single-label line at the centre of a track — used by "no data yet" fallbacks so collapsed/expanded state reads consistently. */
function drawNoDataMessage(ctx: CanvasRenderingContext2D, layout: GaugeTrackLayout, message: string): void {
  ctx.save();
  ctx.fillStyle = 'rgba(180, 180, 200, 0.4)';
  ctx.font = '10px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(message, layout.x + layout.width / 2, layout.y + layout.height / 2, layout.width - 20);
  ctx.restore();
}

/**
 * Sub-layout math — split a track body into N consecutive horizontal rows with the given pixel-height weights. Pixel-perfect: the rows
 * tile exactly (row[i].y + row[i].height === row[i+1].y) and the last row ends exactly at <c>parent.y + parent.height</c>, with zero
 * loss from rounding. `rowHeights` is treated as weights, not absolute heights — they're scaled so the total fills the parent body.
 *
 * The trick is cumulative-weight rounding: each boundary is computed as <c>round(cumWeight / totalWeight * usableHeight)</c> and the
 * row's height is the delta to the previous boundary. Rounding errors can't accumulate because the cumulative sum is what gets
 * rounded; the final boundary always rounds from <c>1.0 × usableHeight = usableHeight</c> exactly. Compare to the previous
 * "independent <c>h × scale</c> per row" math, which could drop 1–2 px at the bottom of the track depending on where the fractions
 * landed.
 *
 * Callers with a single section (e.g. GC, Transient) can pass <c>[1]</c> to get a single body row aligned to the full track minus the
 * 2-px accent stripe — same code path, no special-case.
 */
function splitRows(parent: GaugeTrackLayout, rowHeights: number[]): GaugeTrackLayout[] {
  const out: GaugeTrackLayout[] = [];
  // Sub-sections span from parent.y (the track's very top) to parent.y + parent.height + 1 (one pixel into the trackGap below, stopping
  // just before the next-track separator). The accent stripe drawn by drawAccentStripe at parent.y..parent.y+2 is painted BEFORE chart
  // content, so sub-section content paints over it — the stripe remains visible only in areas the chart doesn't reach (above the max
  // value line), which is the intended decorative effect. No top reservation needed.
  const startY = parent.y;
  const usableHeight = parent.height + 1;
  const totalWeight = rowHeights.reduce((a, b) => a + b, 0);
  if (totalWeight === 0 || usableHeight <= 0) {
    for (const _h of rowHeights) {
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

// ═══════════════════════════════════════════════════════════════════════
// Memory group
// ═══════════════════════════════════════════════════════════════════════

// Memory group — each heap gen gets its own Viridis slot walking cool→warm (Gen0 cool/cheap → POH warm/expensive). Unmanaged sits
// at the dark-purple end off the heap-gen ladder so it can't visually blend into the stacked area; peak line at the warmest yellow
// signals "approaching limit." Memory is the only track that uses all 8 palette slots — accent is forced to the sole leftover.
const HEAP_GEN_COLORS = [
  GAUGE_PALETTE[2],  // #3B528B  Gen0  — blue        (frequent, cheap)
  GAUGE_PALETTE[3],  // #2D738E  Gen1  — teal
  GAUGE_PALETTE[4],  // #22928B  Gen2  — mint-teal
  GAUGE_PALETTE[5],  // #2FB07E  LOH   — green
  GAUGE_PALETTE[6],  // #67CB5E  POH   — lime        (rarest, most expensive)
];
const UNMANAGED_COLOR = GAUGE_PALETTE[0];   // #440154  dark purple — off the heap-gen ladder entirely so unmanaged/managed don't blend
const PEAK_COLOR = GAUGE_PALETTE[7];        // #FDE725  yellow      — "high watermark / approaching limit"

function renderMemoryGroup(cx: GaugeRenderContext): void {

  const gen0 = getSeries(cx.trace, GaugeId.GcHeapGen0Bytes);
  const gen1 = getSeries(cx.trace, GaugeId.GcHeapGen1Bytes);
  const gen2 = getSeries(cx.trace, GaugeId.GcHeapGen2Bytes);
  const loh = getSeries(cx.trace, GaugeId.GcHeapLohBytes);
  const poh = getSeries(cx.trace, GaugeId.GcHeapPohBytes);
  const unmanaged = getSeries(cx.trace, GaugeId.MemoryUnmanagedTotalBytes);
  const unmanagedPeak = getSeries(cx.trace, GaugeId.MemoryUnmanagedPeakBytes);
  const liveBlocks = getSeries(cx.trace, GaugeId.MemoryUnmanagedLiveBlocks);

  // Available data guard — a trace with no snapshots (e.g., a very short run that stopped before the first gauge tick) has nothing to draw.
  if (gen0 === undefined && unmanaged === undefined) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no data in this trace`);
    return;
  }

  // Sub-layout: 3 rows — stacked heap (50%), unmanaged total+peak (35%), blocks+markers (15%).
  const rows = splitRows(cx.layout, [40, 28, 10]);
  const heapRow = rows[0];
  const unmanagedRow = rows[1];
  const markersRow = rows[2];

  // ── Row 1: heap composition stacked area ──
  if (gen0 || gen1 || gen2 || loh || poh) {
    const layers: StackedAreaLayer[] = [];
    if (gen0) layers.push({ series: gen0.samples, color: HEAP_GEN_COLORS[0], label: 'Gen0' });
    if (gen1) layers.push({ series: gen1.samples, color: HEAP_GEN_COLORS[1], label: 'Gen1' });
    if (gen2) layers.push({ series: gen2.samples, color: HEAP_GEN_COLORS[2], label: 'Gen2' });
    if (loh)  layers.push({ series: loh.samples,  color: HEAP_GEN_COLORS[3], label: 'LOH'  });
    if (poh)  layers.push({ series: poh.samples,  color: HEAP_GEN_COLORS[4], label: 'POH'  });
    const heapYMax = stackedYMax(layers.map(l => l.series));
    // Skip render when every heap-gen sample is 0 — `stackedYMax` returns 0 in that case, and drawing a stacked area at yMax=0
    // produces false visual signal (flat baseline that looks like data but represents nothing).
    if (heapYMax > 0) {
      drawStackedArea(cx.ctx, layers, cx.vp, cx.labelWidth, heapRow, 0, heapYMax);
      drawTrackAxis(cx.ctx, 0, heapYMax, 'bytes-compact', heapRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
      // Inline legend identifying which stacked segment is which heap gen. Drawn AFTER the stack so the translucent background strip
      // sits on top — the stacked area still shows through, labels stay readable.
      drawLegendIfVisible(cx,layers.map(l => ({ color: l.color, label: l.label })), heapRow);
    }
  }

  // ── Row 2: unmanaged total line + peak dashed ref ──
  if (unmanaged) {
    const yMax = yMaxFromSeries(unmanagedPeak ?? unmanaged);
    drawAreaChart(cx.ctx, unmanaged.samples, cx.vp, cx.labelWidth, unmanagedRow,
      UNMANAGED_COLOR + '80', UNMANAGED_COLOR, 0, yMax);

    if (unmanagedPeak && unmanagedPeak.samples.length > 0) {
      // Peak is monotonically non-decreasing, so the last sample is the current peak. Draw it as a horizontal reference line.
      const peakValue = unmanagedPeak.samples[unmanagedPeak.samples.length - 1].value;
      drawReferenceLine(cx.ctx, peakValue, unmanagedRow, PEAK_COLOR, 0, yMax, true);
    }
    drawTrackAxis(cx.ctx, 0, yMax, 'bytes-compact', unmanagedRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
    // Legend for row 2 — only includes Peak if the peak series actually has data (otherwise the dashed reference line isn't drawn).
    const row2Legend = [{ color: UNMANAGED_COLOR, label: 'Unmanaged' }];
    if (unmanagedPeak && unmanagedPeak.samples.length > 0) {
      row2Legend.push({ color: PEAK_COLOR, label: 'Peak' });
    }
    drawLegendIfVisible(cx,row2Legend, unmanagedRow);
  }

  // ── Row 3: live blocks thin line + alloc-event markers on the top edge ──
  if (liveBlocks) {
    const yMax = yMaxFromSeries(liveBlocks);
    // liveBlocks deliberately renders in a neutral grey rather than a palette hue — it's a support metric on the marker row, not a
    // primary series, and a palette color here would compete with the alloc-event markers for visual weight.
    drawLineChart(cx.ctx, liveBlocks.samples, cx.vp, cx.labelWidth, markersRow, 'rgba(170,170,170,0.9)', 0, yMax, 1);
  }
  if (cx.trace.memoryAllocEvents && cx.trace.memoryAllocEvents.length > 0) {
    const markers: MarkerPoint[] = cx.trace.memoryAllocEvents.map(memoryAllocEventToMarker);
    drawMarkers(cx.ctx, markers, cx.vp, cx.labelWidth, markersRow);
  }
}

// ═══════════════════════════════════════════════════════════════════════
// Page cache group
// ═══════════════════════════════════════════════════════════════════════

// Page-cache bucket colors — spaced across the Viridis ladder to maximize hue separation in the stacked area, with Exclusive picked
// OUTSIDE the palette to escape the yellow-family collision between Dirty (mustard [6]) and the palette's warmest yellow ([7]).
// Semantic alignment: "free" is navy (empty/cold), "clean" is teal (stable), "dirty" is mustard (activity/pending), "exclusive" is
// coral-red (hottest/contention — extends the cool→warm ladder past the palette's yellow peak into a redder register).
// Overlay lines (epoch / IO) use mid-ladder hues far from any bucket to stay readable on top.
const BUCKET_FREE_COLOR       = GAUGE_PALETTE[1];  // #1C3B84  navy       — "empty / available"
const BUCKET_CLEAN_COLOR      = GAUGE_PALETTE[3];  // #1E8784  teal       — stable state
const BUCKET_DIRTY_COLOR      = GAUGE_PALETTE[6];  // #C3C22E  mustard    — write pending
const BUCKET_EXCLUSIVE_COLOR  = '#E85D4D';         // coral-red — most contended (off-palette to avoid the yellow-next-to-yellow clash)
const EPOCH_COLOR             = GAUGE_PALETTE[2];  // #3B528B  blue       — overlay line, distinct from bucket segments
const IO_COLOR                = GAUGE_PALETTE[5];  // #2FB07E  green      — "in-flight I/O" line, distinct from buckets and epoch

function renderPageCacheGroup(cx: GaugeRenderContext): void {

  const free = getSeries(cx.trace, GaugeId.PageCacheFreePages);
  const clean = getSeries(cx.trace, GaugeId.PageCacheCleanUsedPages);
  const dirty = getSeries(cx.trace, GaugeId.PageCacheDirtyUsedPages);
  const exclusive = getSeries(cx.trace, GaugeId.PageCacheExclusivePages);
  const epoch = getSeries(cx.trace, GaugeId.PageCacheEpochProtectedPages);
  const pendingIo = getSeries(cx.trace, GaugeId.PageCachePendingIoReads);
  const totalCapacity = cx.trace.gaugeCapacities?.get(GaugeId.PageCacheTotalPages);

  if (!free && !clean && !dirty && !exclusive) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no data in this trace`);
    return;
  }

  // 2-row layout: stacked bucket composition (67%), overlay epoch/IO (33%).
  const rows = splitRows(cx.layout, [40, 20]);
  const stackRow = rows[0];
  const overlayRow = rows[1];

  // ── Row 1: mutually-exclusive bucket stack ──
  const layers: StackedAreaLayer[] = [];
  if (free)      layers.push({ series: free.samples,      color: BUCKET_FREE_COLOR,      label: 'Free'      });
  if (clean)     layers.push({ series: clean.samples,     color: BUCKET_CLEAN_COLOR,     label: 'Clean'     });
  if (dirty)     layers.push({ series: dirty.samples,     color: BUCKET_DIRTY_COLOR,     label: 'Dirty'     });
  if (exclusive) layers.push({ series: exclusive.samples, color: BUCKET_EXCLUSIVE_COLOR, label: 'Exclusive' });

  // Y-max is the total page cache size (fixed capacity) — gives a stable Y axis that's ALWAYS the cache size, so Free vs. Used
  // reads as a proportion of the whole cache rather than auto-scaling to the visible range (which would make a full-used cache
  // look the same as a half-used cache).
  const stackYMax = totalCapacity && totalCapacity > 0 ? totalCapacity : stackedYMax(layers.map(l => l.series));
  // Skip render when there's nothing to draw — happens when TotalPages capacity wasn't emitted AND every bucket sample is 0.
  if (stackYMax > 0) {
    drawStackedArea(cx.ctx, layers, cx.vp, cx.labelWidth, stackRow, 0, stackYMax);
    drawTrackAxis(cx.ctx, 0, stackYMax, 'count', stackRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
    // Inline legend identifying each bucket's color — drawn AFTER the stack so the translucent legend background sits on top.
    drawLegendIfVisible(cx,layers.map(l => ({ color: l.color, label: l.label })), stackRow);
  }

  // ── Row 2: epoch-protected + pending I/O overlay lines ──
  const yMax2 = Math.max(yMaxFromSeries(epoch), yMaxFromSeries(pendingIo));
  if (epoch)     drawLineChart(cx.ctx, epoch.samples,     cx.vp, cx.labelWidth, overlayRow, EPOCH_COLOR, 0, yMax2, 1.25);
  if (pendingIo) drawLineChart(cx.ctx, pendingIo.samples, cx.vp, cx.labelWidth, overlayRow, IO_COLOR,    0, yMax2, 1.25);
  // Row 2 legend — only includes series that actually got drawn (matches the if-guards above).
  const row2Items: { color: string; label: string }[] = [];
  if (epoch)     row2Items.push({ color: EPOCH_COLOR, label: 'Epoch-protected' });
  if (pendingIo) row2Items.push({ color: IO_COLOR,    label: 'Pending I/O' });
  if (row2Items.length > 0) drawLegendIfVisible(cx,row2Items, overlayRow);
}

// ═══════════════════════════════════════════════════════════════════════
// GC group
// ═══════════════════════════════════════════════════════════════════════

/**
 * GC marker colour palette. Gen0 = bright green (most frequent, least painful), Gen1 = yellow, Gen2 = red (stop-the-world likely).
 * LOH and POH are rare enough that any GcStart/End referencing them uses the Gen2 palette. Matches the standard perf-tool intuition
 * where "green = fast, red = slow" applies to collection generation.
 */
// GC generation colors — widely-spaced picks so the three marker hues stay distinguishable even at the 4-8 px marker size.
// Cool→warm progression still matches the cheap→expensive GC intuition: Gen0 is frequent/fast, Gen2+ implies a stop-the-world pause.
const GC_GEN_COLORS: Record<number, string> = {
  0: GAUGE_PALETTE[3],  // #2D738E  teal     — Gen0 (frequent, cheap)
  1: GAUGE_PALETTE[5],  // #2FB07E  green    — Gen1 (moderate cost)
  2: GAUGE_PALETTE[7],  // #FDE725  yellow   — Gen2 (STW likely)
};

function gcGenColor(generation: number): string {
  // Any generation beyond Gen2 (LOH, POH) collapses to the same STW yellow — semantically "this is the expensive tier."
  return GC_GEN_COLORS[generation] ?? GAUGE_PALETTE[7];
}

// Neutral gray for the shape-key entries in the GC legend — we want the shape (triangle / circle) to communicate meaning, not
// the color. A mid-gray reads as "no specific generation, just showing you what this shape represents."
const GC_LEGEND_SHAPE_COLOR = '#c0c0c0';

// Pause-per-tick bar color. Deliberately distinct from the Gen0/Gen1/Gen2+ marker colors (teal/green/yellow) so the pause bars
// can't be confused with any generation-specific event. Warm red-orange reads as "this cost latency budget" — the right semantic
// for stop-the-world pause time. Used both as the bar fill AND the legend swatch so "legend → chart" mapping is visually exact.
const GC_PAUSE_COLOR = '#E85D4D';

/**
 * Per-tick GC pause-time series, memoized per ProcessedTrace. For each tick, sums the overlap (in µs) of every GcSuspension span
 * with that tick's time window. The result is a standard GaugeSample[] array — timestamps are tick starts, values are total paused
 * microseconds inside that tick. This is the SAME cadence as every other gauge series, so <see cref="drawAreaChart"/> can render
 * it without special-casing.
 *
 * Algorithm is O(T + S) amortized via a two-pointer walk over sorted suspensions: sIdx advances past suspensions that have fully
 * completed before the next tick starts, so each suspension is visited at most once per tick it overlaps. Cached via WeakMap so
 * the O(T + S) pass runs exactly once per trace instance — subsequent renders (drag, pan, zoom) reuse the cached series.
 */
const _gcPauseSeriesCache = new WeakMap<object, GaugeSample[]>();
function getPerTickPauseSeries(trace: ProcessedTrace): GaugeSample[] {
  const cached = _gcPauseSeriesCache.get(trace);
  if (cached) return cached;

  const summary = trace.summary ?? [];
  const suspensions = trace.gcSuspensions ?? [];
  const result: GaugeSample[] = [];

  if (summary.length === 0) {
    _gcPauseSeriesCache.set(trace, result);
    return result;
  }

  let sIdx = 0;
  for (let tIdx = 0; tIdx < summary.length; tIdx++) {
    const tick = summary[tIdx];
    const tickStart = tick.startUs;
    const tickEnd = tick.startUs + tick.durationUs;
    let totalPauseUs = 0;
    // Visit every suspension that starts before this tick ends AND hasn't already been fully consumed by earlier ticks.
    for (let i = sIdx; i < suspensions.length && suspensions[i].startUs < tickEnd; i++) {
      const s = suspensions[i];
      const sEnd = s.startUs + s.durationUs;
      if (sEnd > tickStart) {
        const overlapStart = Math.max(s.startUs, tickStart);
        const overlapEnd = Math.min(sEnd, tickEnd);
        totalPauseUs += overlapEnd - overlapStart;
      }
    }
    // Advance sIdx past suspensions that have FULLY ended within this tick — future ticks can't overlap them. Suspensions that
    // span into the next tick stay at sIdx so they're counted again (their overlap portion for the next tick).
    while (sIdx < suspensions.length && suspensions[sIdx].startUs + suspensions[sIdx].durationUs <= tickEnd) {
      sIdx++;
    }
    result.push({ tickNumber: tick.tickNumber, timestampUs: tick.startUs, value: totalPauseUs });
  }

  _gcPauseSeriesCache.set(trace, result);
  return result;
}

function renderGcGroup(cx: GaugeRenderContext): void {
  const gcEvents = cx.trace.gcEvents ?? [];
  const gcSuspensions = cx.trace.gcSuspensions ?? [];

  if (gcEvents.length === 0 && gcSuspensions.length === 0) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no GC activity captured (enable Typhon:Telemetry:Profiler:GC)`);
    return;
  }

  const [bodyRow] = splitRows(cx.layout, [1]);

  // Primary content: per-tick GC pause-time bar chart. One rectangle per tick — X spans the tick's actual time range, height is the
  // pause µs for that tick. This is the "GC → latency" linkage in its most unambiguous form: width encodes "which tick", height
  // encodes "how much pause". Area-chart interpolation was wrong here — it drew triangles whose X spans were meaningless (just
  // "two tick boundaries apart") and whose X-width accidentally looked like it represented duration, duplicating what the Y axis
  // already said.
  const pauseSeries = getPerTickPauseSeries(cx.trace);
  const summary = cx.trace.summary ?? [];
  if (pauseSeries.length > 0 && summary.length === pauseSeries.length) {
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
      // Full-opacity fill so bar color literally matches the legend swatch — no alpha shift, no "is this the same color?" ambiguity.
      cx.ctx.fillStyle = GC_PAUSE_COLOR;

      for (let i = 0; i < summary.length; i++) {
        const pause = pauseSeries[i].value;
        if (pause <= 0) continue;
        const tick = summary[i];
        const tickEnd = tick.startUs + tick.durationUs;
        if (tickEnd < viewStartUs) continue;      // before viewport — skip
        if (tick.startUs > viewEndUs) break;      // summary is sorted — no more overlapping ticks
        const x1 = toPixelX(tick.startUs, cx.vp, cx.labelWidth);
        const x2 = toPixelX(tickEnd, cx.vp, cx.labelWidth);
        const barW = Math.max(1, x2 - x1);         // 1-px minimum so sub-pixel ticks remain visible
        const ratio = pause / yMax;                // normalize to [0..1] against yMax (already padded by 1.1)
        const barH = ratio * bodyRow.height;
        const yTop = bodyBottom - barH;
        cx.ctx.fillRect(x1, yTop, barW, barH);
      }
      cx.ctx.restore();

      drawTrackAxis(cx.ctx, 0, yMax, 'duration', bodyRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
    }
  }

  // Overlay: GcStart triangles (up-pointing) + GcEnd dots, along the top edge of the track, colored by generation. The start is a
  // triangle because it's an "ignition" event; the end is a dot because the interesting duration info already went into the
  // suspension bar. Both together give the user a punctuated timeline of GC activity.
  if (gcEvents.length > 0) {
    cx.ctx.save();
    cx.ctx.beginPath();
    cx.ctx.rect(bodyRow.x, bodyRow.y, bodyRow.width, bodyRow.height);
    cx.ctx.clip();
    const markerY = bodyRow.y + 5;  // sit near the top of the body, away from the pause-area peaks
    for (const e of gcEvents) {
      const px = toPixelX(e.timestampUs, cx.vp, cx.labelWidth);
      if (px < bodyRow.x - 4 || px > bodyRow.x + bodyRow.width + 4) continue;
      cx.ctx.fillStyle = gcGenColor(e.generation);
      cx.ctx.beginPath();
      if (e.kind === 7 /* GcStart */) {
        // Upward triangle — "here a GC started"
        cx.ctx.moveTo(px, markerY - 4);
        cx.ctx.lineTo(px - 4, markerY + 4);
        cx.ctx.lineTo(px + 4, markerY + 4);
        cx.ctx.closePath();
      } else {
        // Dot for GcEnd
        cx.ctx.arc(px, markerY, 3, 0, Math.PI * 2);
      }
      cx.ctx.fill();
    }
    cx.ctx.restore();
  }

  // Inline legend — placed AFTER the markers so the translucent background sits on top. Six items:
  //   • primary-series key (accent color) — the pause-time area chart's own swatch
  //   • two shape-keys (triangle=start, circle=end) — neutral gray so the shape alone conveys meaning
  //   • three color-keys (Gen0/Gen1/Gen2+) — each uses its own GC-gen color so the reader sees the exact hue used in markers
  drawLegendIfVisible(cx,[
    { color: GC_PAUSE_COLOR,        label: 'pause / tick' },
    { color: GC_LEGEND_SHAPE_COLOR, label: 'start',  shape: 'triangle' },
    { color: GC_LEGEND_SHAPE_COLOR, label: 'end',    shape: 'circle' },
    { color: GC_GEN_COLORS[0],      label: 'Gen0' },
    { color: GC_GEN_COLORS[1],      label: 'Gen1' },
    { color: GC_GEN_COLORS[2],      label: 'Gen2+' },
  ], cx.layout);
}

// ═══════════════════════════════════════════════════════════════════════
// Deferred groups — WAL / Tx+UoW
// ═══════════════════════════════════════════════════════════════════════

function renderTransientGroup(cx: GaugeRenderContext): void {
  const used = getSeries(cx.trace, GaugeId.TransientStoreBytesUsed);
  const maxBytes = cx.trace.gaugeCapacities?.get(GaugeId.TransientStoreMaxBytes);

  if (!used) {
    // Emitter aggregates across every live TransientStore and skips the gauge entirely when total is 0, so absence here means
    // the workload doesn't use any Transient storage. That's a valid state for many Typhon apps — show a clear empty message
    // rather than a scary "no data" fallback.
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — no transient storage in use`);
    return;
  }

  // Single body row via splitRows([1]) — keeps the chart below the 2-px accent stripe, same pixel-perfect convention as the
  // multi-section groups. Previously passed cx.layout directly, so the area chart overlapped the stripe.
  const [bodyRow] = splitRows(cx.layout, [1]);
  const yMax = maxBytes && maxBytes > 0 ? maxBytes : yMaxFromSeries(used);
  drawAreaChart(cx.ctx, used.samples, cx.vp, cx.labelWidth, bodyRow,
    cx.spec.accentColor + '80', cx.spec.accentColor, 0, yMax);
  if (maxBytes && maxBytes > 0) {
    drawReferenceLine(cx.ctx, maxBytes, bodyRow, cx.spec.accentColor, 0, yMax, true);
  }
  drawTrackAxis(cx.ctx, 0, yMax, 'bytes-compact', bodyRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
  // Legend — only Used is labeled; if the capacity reference line is drawn it gets a "Max" entry too.
  const legend = [{ color: cx.spec.accentColor, label: 'Used' }];
  if (maxBytes && maxBytes > 0) legend.push({ color: cx.spec.accentColor, label: 'Max (dashed)' });
  drawLegendIfVisible(cx,legend, cx.layout);
}

// WAL content colors — palette picks for the three series the track renders. Commit-buffer area uses the accent color (track identity);
// inflight frames + staging rented use distinct indices so the two overlays don't blur together.
const WAL_COMMIT_BUFFER_COLOR = GAUGE_PALETTE[2];  // #14618D  ocean blue — area fill (primary "backpressure" signal)
const WAL_INFLIGHT_COLOR      = GAUGE_PALETTE[6];  // #C3C22E  mustard    — "submitted but not durable" line
const WAL_STAGING_COLOR       = GAUGE_PALETTE[4];  // #35A96D  green      — pool rent level
const WAL_STAGING_PEAK_COLOR  = GAUGE_PALETTE[7];  // #F6D85C  warm yellow — peak high-watermark reference

function renderWalGroup(cx: GaugeRenderContext): void {

  const commitBuf = getSeries(cx.trace, GaugeId.WalCommitBufferUsedBytes);
  const inflight = getSeries(cx.trace, GaugeId.WalInflightFrames);
  const staging = getSeries(cx.trace, GaugeId.WalStagingPoolRented);
  const stagingPeak = getSeries(cx.trace, GaugeId.WalStagingPoolPeakRented);
  const bufferCapacity = cx.trace.gaugeCapacities?.get(GaugeId.WalCommitBufferCapacityBytes);
  const stagingCapacity = cx.trace.gaugeCapacities?.get(GaugeId.WalStagingPoolCapacity);

  if (!commitBuf && !inflight && !staging) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — enable WAL (DurabilityMode ≠ None) for WAL gauges`);
    return;
  }

  // Two-row layout: commit buffer area on top (the primary backpressure signal), inflight + staging lines below.
  const rows = splitRows(cx.layout, [50, 28]);
  const bufferRow = rows[0];
  const overlayRow = rows[1];

  // ── Row 1: commit-buffer used bytes as an area chart, with capacity as a dashed reference line if known ──
  if (commitBuf) {
    const yMax = bufferCapacity && bufferCapacity > 0 ? bufferCapacity : yMaxFromSeries(commitBuf);
    drawAreaChart(cx.ctx, commitBuf.samples, cx.vp, cx.labelWidth, bufferRow,
      WAL_COMMIT_BUFFER_COLOR + '80', WAL_COMMIT_BUFFER_COLOR, 0, yMax);
    if (bufferCapacity && bufferCapacity > 0) {
      drawReferenceLine(cx.ctx, bufferCapacity, bufferRow, WAL_COMMIT_BUFFER_COLOR, 0, yMax, true);
    }
    drawTrackAxis(cx.ctx, 0, yMax, 'bytes-compact', bufferRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
    const row1Legend = [{ color: WAL_COMMIT_BUFFER_COLOR, label: 'Commit buffer' }];
    if (bufferCapacity && bufferCapacity > 0) row1Legend.push({ color: WAL_COMMIT_BUFFER_COLOR, label: 'Capacity (dashed)' });
    drawLegendIfVisible(cx,row1Legend, bufferRow);
  }

  // ── Row 2: inflight frames + staging rented (two lines on a shared Y axis scaled to the larger of the two + staging capacity) ──
  if (inflight || staging) {
    const yMax2 = Math.max(
      yMaxFromSeries(inflight),
      yMaxFromSeries(staging),
      stagingCapacity && stagingCapacity > 0 ? stagingCapacity : 0,
    );
    if (inflight) drawLineChart(cx.ctx, inflight.samples, cx.vp, cx.labelWidth, overlayRow, WAL_INFLIGHT_COLOR, 0, yMax2, 1.25);
    if (staging)  drawLineChart(cx.ctx, staging.samples,  cx.vp, cx.labelWidth, overlayRow, WAL_STAGING_COLOR,  0, yMax2, 1.25);
    // Peak high-watermark reference line — shows "how close to capacity did staging-pool usage get?"
    if (stagingPeak && stagingPeak.samples.length > 0) {
      const peakValue = stagingPeak.samples[stagingPeak.samples.length - 1].value;
      drawReferenceLine(cx.ctx, peakValue, overlayRow, WAL_STAGING_PEAK_COLOR, 0, yMax2, true);
    }
    drawTrackAxis(cx.ctx, 0, yMax2, 'count', overlayRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
    const row2Legend: { color: string; label: string }[] = [];
    if (inflight) row2Legend.push({ color: WAL_INFLIGHT_COLOR, label: 'Inflight' });
    if (staging)  row2Legend.push({ color: WAL_STAGING_COLOR,  label: 'Staging rented' });
    if (stagingPeak && stagingPeak.samples.length > 0) row2Legend.push({ color: WAL_STAGING_PEAK_COLOR, label: 'Staging peak' });
    if (row2Legend.length > 0) drawLegendIfVisible(cx,row2Legend, overlayRow);
  }
}

/**
 * Derive a per-tick delta series from a cumulative monotonic series. Each output sample's value is
 * <c>cumulative[i] - cumulative[i-1]</c>. The first sample's delta is 0 (we have no previous snapshot to diff against). Timestamps
 * are inherited from the cumulative series unchanged.
 *
 * Intended caller: the Tx+UoW renderer, which wants to show "X transactions committed THIS tick" rather than "X total commits
 * since process start." The monotonic counter is emitted by the engine; the viewer does the differencing here because it's a pure
 * derived signal and keeps the wire format simple.
 */
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
      value: Math.max(0, cur - prev),  // monotonic counter should never decrease; clamp defensively
    };
  }
  return out;
}

// Semantic color picks from the Viridis GAUGE_PALETTE. Indices chosen for hue separation AND semantic fit — palette has no red, so
// "rollback" maps to the warmest available (yellow, signaling "extreme state / attention"), not a traditional failure-red.
//   Tx active     = dark blue — "current load" background area; darker than the created-line blue so the two blues don't blur together
//   Tx created    = blue   — "new work starting" (cool / the coolest tx-row line)
//   Tx commit     = green  — success / positive outcome
//   Tx rollback   = yellow — failure / most extreme state in the cool→warm ramp
//   UoW active    = deep teal — "current load" background area for the UoW row
//   UoW created   = teal   — new UoW lifecycle (cool)
//   UoW committed = mustard — durable success (warmer green, distinct from commit's mid-green so the two success states don't look alike)
const TX_ACTIVE_COLOR = GAUGE_PALETTE[1];     // #1C3B84  navy   — background area (Tx row)
const TX_CREATED_COLOR = GAUGE_PALETTE[2];    // #14618D  blue
const TX_COMMIT_COLOR = GAUGE_PALETTE[5];     // #35A96D  green
const TX_ROLLBACK_COLOR = GAUGE_PALETTE[7];   // #F6D85C  yellow
const UOW_ACTIVE_COLOR = GAUGE_PALETTE[0];    // #2A186A  deep indigo — background area (UoW row); distinct from the UoW-active line color
const UOW_CREATED_COLOR = GAUGE_PALETTE[3];   // #1E8784  teal
const UOW_COMMITTED_COLOR = GAUGE_PALETTE[6]; // #C3C22E  mustard

function renderTxUowGroup(cx: GaugeRenderContext): void {

  // Per-tick rate series come from cumulative counters — viewer derives Δ/Δt. Active-count / pool-size / void-count are already
  // snapshot values, so we use their samples directly.
  const txActive = getSeries(cx.trace, GaugeId.TxChainActiveCount);
  const uowActive = getSeries(cx.trace, GaugeId.UowRegistryActiveCount);
  const createdTotal = getSeries(cx.trace, GaugeId.TxChainCreatedTotal);
  const commitTotal = getSeries(cx.trace, GaugeId.TxChainCommitTotal);
  const rollbackTotal = getSeries(cx.trace, GaugeId.TxChainRollbackTotal);
  const uowCreatedTotal = getSeries(cx.trace, GaugeId.UowRegistryCreatedTotal);
  const uowCommittedTotal = getSeries(cx.trace, GaugeId.UowRegistryCommittedTotal);

  if (!txActive && !uowActive && !createdTotal && !commitTotal && !rollbackTotal && !uowCreatedTotal && !uowCommittedTotal) {
    drawNoDataMessage(cx.ctx, cx.layout, `${cx.spec.label} — throughput counters not in trace`);
    return;
  }

  // Derive per-tick deltas from the cumulative series. This is a free operation — one subtraction per sample per series.
  const createdThisTick = deltaSeries(createdTotal);
  const commitsThisTick = deltaSeries(commitTotal);
  const rollbacksThisTick = deltaSeries(rollbackTotal);
  const uowCreatedThisTick = deltaSeries(uowCreatedTotal);
  const uowCommittedThisTick = deltaSeries(uowCommittedTotal);

  // Per-row Y-scale (NOT unified across rows). The Tx row and UoW row have unrelated characteristics — a spike in UoW creations during
  // a burst spawn shouldn't flatten the Tx commit line to a single pixel. Each row's scale tracks the max of BOTH active-count (area)
  // and per-tick rates (lines) on that row so every signal stays readable on the shared y axis. The tooltip still shows raw counts
  // so cross-row magnitudes are recoverable.
  const txPeak = Math.max(
    txActive ? seriesPeak(txActive.samples) : 0,
    seriesPeak(createdThisTick), seriesPeak(commitsThisTick), seriesPeak(rollbacksThisTick));
  const uowPeak = Math.max(
    uowActive ? seriesPeak(uowActive.samples) : 0,
    seriesPeak(uowCreatedThisTick), seriesPeak(uowCommittedThisTick));
  const txYMax = Math.max(1, txPeak * 1.1);
  const uowYMax = Math.max(1, uowPeak * 1.1);

  // Two-row layout: Tx metrics on top, UoW below. Each row's total budget is ~40 px (the track is expandedHeight=80, see gaugeRegion.ts).
  const rows = splitRows(cx.layout, [38, 38]);
  const txRow = rows[0];
  const uowRow = rows[1];

  // ── Row 1: Tx ──
  // Active count drawn FIRST as a translucent area so the rate lines overlay on top of it. An all-zero active series is skipped —
  // TransactionChain.ActiveCount is always emitted when the engine has a TransactionChain, but defensive against future test harnesses.
  if (txActive && seriesPeak(txActive.samples) > 0) {
    drawAreaChart(cx.ctx, txActive.samples, cx.vp, cx.labelWidth, txRow,
      TX_ACTIVE_COLOR + '55', TX_ACTIVE_COLOR, 0, txYMax, 1);
  }
  // Only draw lines for series that have at least one non-zero sample. An all-zero series (e.g., RollbackTotal in a workload that never
  // rolls back) would otherwise render as a flat line pinned to the row's bottom edge — visual noise with no information.
  if (seriesPeak(createdThisTick) > 0) {
    drawLineChart(cx.ctx, createdThisTick, cx.vp, cx.labelWidth, txRow, TX_CREATED_COLOR, 0, txYMax, 1.5);
  }
  if (seriesPeak(commitsThisTick) > 0) {
    drawLineChart(cx.ctx, commitsThisTick, cx.vp, cx.labelWidth, txRow, TX_COMMIT_COLOR, 0, txYMax, 1.5);
  }
  if (seriesPeak(rollbacksThisTick) > 0) {
    drawLineChart(cx.ctx, rollbacksThisTick, cx.vp, cx.labelWidth, txRow, TX_ROLLBACK_COLOR, 0, txYMax, 1.5);
  }
  drawTrackAxis(cx.ctx, 0, txYMax, 'count', txRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
  // Inline legend — drawn AFTER the lines so the translucent background sits on top, keeping labels readable even when a line
  // passes through the legend strip. "active" comes first so the reader maps the area color to its meaning before interpreting
  // the per-tick lines overlaid on it.
  drawLegendIfVisible(cx, [
    { color: TX_ACTIVE_COLOR, label: 'active' },
    { color: TX_CREATED_COLOR, label: 'created/tick' },
    { color: TX_COMMIT_COLOR, label: 'commits/tick' },
    { color: TX_ROLLBACK_COLOR, label: 'rollbacks/tick' },
  ], txRow);

  // ── Row 2: UoW ──
  if (uowActive && seriesPeak(uowActive.samples) > 0) {
    drawAreaChart(cx.ctx, uowActive.samples, cx.vp, cx.labelWidth, uowRow,
      UOW_ACTIVE_COLOR + '55', UOW_ACTIVE_COLOR, 0, uowYMax, 1);
  }
  if (seriesPeak(uowCreatedThisTick) > 0) {
    drawLineChart(cx.ctx, uowCreatedThisTick, cx.vp, cx.labelWidth, uowRow, UOW_CREATED_COLOR, 0, uowYMax, 1.5);
  }
  if (seriesPeak(uowCommittedThisTick) > 0) {
    drawLineChart(cx.ctx, uowCommittedThisTick, cx.vp, cx.labelWidth, uowRow, UOW_COMMITTED_COLOR, 0, uowYMax, 1.5);
  }
  drawTrackAxis(cx.ctx, 0, uowYMax, 'count', uowRow, cx.labelWidth, 'rgba(180,180,200,0.55)');
  drawLegendIfVisible(cx, [
    { color: UOW_ACTIVE_COLOR, label: 'active' },
    { color: UOW_CREATED_COLOR, label: 'created/tick' },
    { color: UOW_COMMITTED_COLOR, label: 'committed/tick' },
  ], uowRow);
}

/** Max sample value in a series, or 0 if empty. Used for auto-scaling a row's y axis off the series it contains. */
function seriesPeak(samples: GaugeSample[]): number {
  let peak = 0;
  for (const s of samples) {
    if (s.value > peak) peak = s.value;
  }
  return peak;
}

// ═══════════════════════════════════════════════════════════════════════
// Dispatch table
// ═══════════════════════════════════════════════════════════════════════

/** Map of group-track ID → renderer. <c>GraphArea</c> looks up the renderer for the current track and calls it. */
// ═══════════════════════════════════════════════════════════════════════
// Summary-strip rendering (3-state expand: `summary` mode)
// ═══════════════════════════════════════════════════════════════════════

/**
 * Primary gauge series per track, used by <see cref="drawGaugeSummaryStrip"/>. Choice rationale per entry:
 *   - `gauge-gc` → committed bytes: total heap pressure, the most actionable GC signal.
 *   - `gauge-memory` → unmanaged total bytes: pairs with the GC-heap row's committed line; unmanaged is what the engine
 *     itself drives (ComponentTable growth etc.), distinct from .NET's own heap.
 *   - `gauge-persistence` → dirty pages: the most dynamic bucket; clean/free/exclusive change slowly, dirty moves on every
 *     write operation.
 *   - `gauge-transient` → bytes used: the only signal this track tracks.
 *   - `gauge-wal` → commit buffer used: the primary back-pressure signal — if this climbs toward capacity, commits stall.
 *   - `gauge-tx-uow` → Tx active count: instantaneous load snapshot. In workloads with only short-lived transactions this
 *     stays flat at 0, but in production workloads with long-lived background tx it's the most useful signal.
 */
const SUMMARY_PRIMARY_GAUGE: Record<string, GaugeId> = {
  'gauge-gc':          GaugeId.GcHeapCommittedBytes,
  'gauge-memory':      GaugeId.MemoryUnmanagedTotalBytes,
  'gauge-persistence': GaugeId.PageCacheDirtyUsedPages,
  'gauge-transient':   GaugeId.TransientStoreBytesUsed,
  'gauge-wal':         GaugeId.WalCommitBufferUsedBytes,
  'gauge-tx-uow':      GaugeId.TxChainActiveCount,
};

/**
 * Draw a one-row "heartbeat" preview of a gauge track's primary series. Used by the 3-state expand's `summary` mode — gives
 * the user a sense of "data exists here and how it moves" without the full legend / axis / accent-color language of the
 * expanded track. Rendered in <see cref="DIM_TEXT_LINE_COLOR"/> (neutral grey) explicitly — the user's request was "without
 * text or color", so tinting the line with an accent would defeat the purpose.
 *
 * Silently no-ops when the track has no data (a valid case: e.g., Transient when the workload doesn't use it). The label-row
 * chevron still shows so the user can click to expand and see the "no data" message from the full renderer.
 */
const SUMMARY_STRIP_LINE_COLOR = 'rgba(170, 170, 170, 0.75)';  // neutral grey at low alpha — reads as "preview" without competing with expanded-mode colors
export function drawGaugeSummaryStrip(
  ctx: CanvasRenderingContext2D,
  trace: ProcessedTrace,
  trackId: string,
  vp: Viewport,
  layout: GaugeTrackLayout,
  labelWidth: number,
): void {
  const gaugeId = SUMMARY_PRIMARY_GAUGE[trackId];
  if (gaugeId === undefined) return;
  const series = getSeries(trace, gaugeId);
  if (series === undefined || series.samples.length === 0) return;
  const yMax = yMaxFromSeries(series);
  if (yMax <= 0) return;
  // Inset layout by 1 px top/bottom so the line never touches the separator pixels of the gutter-border cross-hatch.
  const inset: GaugeTrackLayout = { x: layout.x, y: layout.y + 1, width: layout.width, height: layout.height - 2 };
  drawLineChart(ctx, series.samples, vp, labelWidth, inset, SUMMARY_STRIP_LINE_COLOR, 0, yMax, 1);
}

export const GROUP_RENDERERS: Record<string, GroupRenderer> = {
  'gauge-gc': renderGcGroup,
  'gauge-memory': renderMemoryGroup,
  'gauge-persistence': renderPageCacheGroup,
  'gauge-transient': renderTransientGroup,
  'gauge-wal': renderWalGroup,
  'gauge-tx-uow': renderTxUowGroup,
};

// ═══════════════════════════════════════════════════════════════════════
// Tooltip builders — one per group
// ═══════════════════════════════════════════════════════════════════════

/**
 * Per-group gauge-ID list with display label + unit. Drives the hover tooltip — iterating this in render order gives a predictable
 * layout (same gauge on the same line every time) regardless of which subset of values the trace actually carries. Missing values
 * are skipped at format time, so tooltips shrink gracefully when an engine subsystem hasn't emitted a particular gauge.
 */
interface TooltipGaugeSpec {
  id: GaugeId;
  label: string;
  unit: 'bytes' | 'count' | 'percent' | 'bytes-compact';
  /**
   * Color applied to this gauge's tooltip line so the reader can visually map "tooltip row → chart glyph" without reading a
   * separate legend. Should exactly match the color the renderer uses for the corresponding series. Omit for metadata-only
   * gauges that aren't drawn on the chart (e.g., peak reference lines can borrow the peak-line color).
   */
  color?: string;
}

const TOOLTIP_GAUGES: Record<string, TooltipGaugeSpec[]> = {
  // 'gauge-gc' intentionally absent — the GC track renders per-tick pause time (a derived series, not a raw gauge) and needs its own
  // tooltip builder (<see cref="buildGcTooltipLines"/>) to surface the right numbers: pause µs + Gc start/end counts by generation
  // within the matched tick. The generic gauge-snapshot path would still report committed bytes — stale relative to what's drawn.
  // Memory is split into TWO sub-tooltips based on which sub-row the cursor is over (see buildGaugeTooltipLines). Row 1 (heap stacked
  // area) → heap-gens only. Rows 2 + 3 (unmanaged area + markers) → unmanaged totals. Dispatch via the internal virtual keys below;
  // the plain 'gauge-memory' key is left out so a caller without sub-row info gets no tooltip (fail-safe: nothing is better than
  // showing a full tooltip while the user is hovering a heap-specific region).
  'gauge-memory-heap': [
    // Heap-gen lines share the HEAP_GEN_COLORS ladder used by the stacked-area renderer.
    { id: GaugeId.GcHeapGen0Bytes, label: 'Gen0', unit: 'bytes-compact', color: GAUGE_PALETTE[2] },
    { id: GaugeId.GcHeapGen1Bytes, label: 'Gen1', unit: 'bytes-compact', color: GAUGE_PALETTE[3] },
    { id: GaugeId.GcHeapGen2Bytes, label: 'Gen2', unit: 'bytes-compact', color: GAUGE_PALETTE[4] },
    { id: GaugeId.GcHeapLohBytes,  label: 'LOH',  unit: 'bytes-compact', color: GAUGE_PALETTE[5] },
    { id: GaugeId.GcHeapPohBytes,  label: 'POH',  unit: 'bytes-compact', color: GAUGE_PALETTE[6] },
  ],
  'gauge-memory-unmanaged': [
    { id: GaugeId.MemoryUnmanagedTotalBytes, label: 'Unmanaged',   unit: 'bytes-compact', color: GAUGE_PALETTE[0] },
    { id: GaugeId.MemoryUnmanagedPeakBytes,  label: 'Peak',        unit: 'bytes-compact', color: GAUGE_PALETTE[7] },
    { id: GaugeId.MemoryUnmanagedLiveBlocks, label: 'Live blocks', unit: 'count' },
  ],
  'gauge-persistence': [
    // Bucket colors exactly mirror the BUCKET_*_COLOR constants the renderer uses. Epoch + I/O overlay lines borrow the
    // row-2 overlay colors.
    { id: GaugeId.PageCacheFreePages,            label: 'Free',       unit: 'count', color: GAUGE_PALETTE[1] },
    { id: GaugeId.PageCacheCleanUsedPages,       label: 'Clean',      unit: 'count', color: GAUGE_PALETTE[3] },
    { id: GaugeId.PageCacheDirtyUsedPages,       label: 'Dirty',      unit: 'count', color: GAUGE_PALETTE[6] },
    { id: GaugeId.PageCacheExclusivePages,       label: 'Exclusive',  unit: 'count', color: '#E85D4D' },
    { id: GaugeId.PageCacheEpochProtectedPages,  label: 'Epoch-held', unit: 'count', color: GAUGE_PALETTE[2] },
    { id: GaugeId.PageCachePendingIoReads,       label: 'Pending I/O', unit: 'count', color: GAUGE_PALETTE[5] },
  ],
  'gauge-transient': [
    // Transient area uses the track's accent color — match it in the tooltip.
    { id: GaugeId.TransientStoreBytesUsed, label: 'Used', unit: 'bytes-compact', color: GAUGE_PALETTE[1] },
  ],
  'gauge-wal': [
    // Mirror the three WAL_*_COLOR constants defined on the renderer.
    { id: GaugeId.WalCommitBufferUsedBytes,    label: 'Commit buffer', unit: 'bytes-compact', color: GAUGE_PALETTE[2] },
    { id: GaugeId.WalInflightFrames,           label: 'Inflight',      unit: 'count',         color: GAUGE_PALETTE[6] },
    { id: GaugeId.WalStagingPoolRented,        label: 'Staging rented', unit: 'count',        color: GAUGE_PALETTE[4] },
  ],
  // Tx+UoW is intentionally absent from TOOLTIP_GAUGES — it gets its own tooltip builder (<see cref="buildTxUowTooltipLines"/>) because
  // the on-screen chart renders *per-tick deltas*, not raw cumulative totals. The generic <c>buildGaugeTooltipLines</c> can only
  // emit raw gauge values; diffing two adjacent snapshots to get the per-tick delta needs its own path.
};

/** Fixed-at-init capacity gauges — rendered as a dashed reference line in the chart; shown in tooltips as a suffix "(cap: XXX)". */
const TOOLTIP_CAPACITY_FOR_GROUP: Record<string, GaugeId | undefined> = {
  'gauge-persistence': GaugeId.PageCacheTotalPages,
  'gauge-transient': GaugeId.TransientStoreMaxBytes,
  'gauge-wal': GaugeId.WalCommitBufferCapacityBytes,
};

/**
 * Build tooltip lines for a gauge group hover. Finds the tick whose <c>gaugeSnapshot.timestampUs</c> is closest to
 * <paramref name="cursorUs"/>, then emits one line per gauge in the group's <c>TOOLTIP_GAUGES</c> list. Gauges not present in the
 * snapshot (e.g., deferred WAL gauges, or a group that hasn't been sampled yet) are skipped silently.
 *
 * Returns an empty array when no snapshot is within range — the caller treats that as "no hover hit" so the tooltip disappears.
 */
/**
 * Memory-track tooltip dispatcher. Splits by sub-row using the SAME weights as {@link splitRows} so the tooltip matches exactly
 * which visual band the cursor is over — row 1 (heap) gets the heap-gen breakdown, rows 2 + 3 (unmanaged + markers) get the
 * unmanaged totals. Computes boundaries from `localY` (cursor Y relative to track top) + `trackHeight` (track's full body height).
 */
function memorySubRowKey(localY: number, trackHeight: number): string {
  // Mirror splitRows math: usableHeight = trackHeight + 1 (the +1 is for the trackGap extension), boundaries are cumulative-
  // weight-rounded integer pixel counts. Keeping this in sync with splitRows' implementation is essential — weights [40, 28, 10]
  // here MUST match the renderer's [40, 28, 10] call.
  const weights = [40, 28, 10];
  const total = weights[0] + weights[1] + weights[2];
  const usableHeight = trackHeight + 1;
  const boundary1 = Math.round((weights[0] / total) * usableHeight);
  const boundary2 = Math.round(((weights[0] + weights[1]) / total) * usableHeight);
  if (localY < boundary1) return 'gauge-memory-heap';
  if (localY < boundary2) return 'gauge-memory-unmanaged';
  return 'gauge-memory-unmanaged';   // markers row falls through to the same tooltip as row 2 (live blocks IS the row-3 metric)
}

export function buildGaugeTooltipLines(
  trace: ProcessedTrace,
  trackId: string,
  groupLabel: string,
  cursorUs: number,
  /** Cursor Y coordinate in track-local space (0 = track top). Required for multi-sub-row tracks (Memory); ignored otherwise. */
  localY?: number,
  /** Track's full body height in pixels. Paired with localY for sub-row calculations. */
  trackHeight?: number,
): TooltipLine[] {
  // Tx+UoW gets its own specialized tooltip because the chart draws per-tick deltas (derived client-side), not raw cumulative
  // totals. Mixing the two in a single generic path would mean the tooltip lies about what the chart is showing.
  if (trackId === 'gauge-tx-uow') {
    return buildTxUowTooltipLines(trace, groupLabel, cursorUs);
  }
  // Memory track is sub-divided into 3 sub-rows via splitRows([40, 28, 10]). The tooltip content differs per row (heap-gen breakdown
  // in row 1, unmanaged totals in rows 2+3). Re-route through the sub-key map before the generic lookup below.
  if (trackId === 'gauge-memory' && localY !== undefined && trackHeight !== undefined) {
    trackId = memorySubRowKey(localY, trackHeight);
    if (trackId === 'gauge-memory-heap') groupLabel = 'Memory — Heap';
    else groupLabel = 'Memory — Unmanaged';
  }
  // GC track renders a derived per-tick pause-time area chart and per-generation event markers — the generic snapshot path would
  // only surface committed bytes (unrelated to what's drawn now). Dispatch to the GC-specific builder.
  if (trackId === 'gauge-gc') {
    return buildGcTooltipLines(trace, groupLabel, cursorUs);
  }

  const specs = TOOLTIP_GAUGES[trackId];
  if (specs === undefined) return [];

  // Binary-search the ticks array for the snapshot closest to cursorUs. trace.ticks is sorted by tickNumber and by
  // construction each tick's gaugeSnapshot.timestampUs is monotone non-decreasing (snapshot is taken at TickEnd), so we can
  // treat the sorted array as a search key. Find the first tick whose snapshot timestamp >= cursor, then compare that candidate
  // and its predecessor — whichever is closer wins. Degrades gracefully when ticks have no snapshot (continuous-load gap between
  // chunks): falls back to a linear scan of the snapshot-bearing slice. At 10K+ ticks the win is 5 ms → <1 ms per hover.
  const bestTick = findNearestSnapshotTick(trace.ticks, cursorUs);
  if (bestTick === undefined || bestTick.gaugeSnapshot === undefined) return [];

  const snap = bestTick.gaugeSnapshot;
  // Header lines stay in the default TEXT_COLOR — they're metadata (group name + tick #), not a specific drawn signal.
  const lines: TooltipLine[] = [groupLabel, `Tick: ${snap.tickNumber}`];

  const capacityId = TOOLTIP_CAPACITY_FOR_GROUP[trackId];
  const capacity = capacityId !== undefined ? trace.gaugeCapacities?.get(capacityId) : undefined;

  for (const spec of specs) {
    const value = snap.values.get(spec.id);
    if (value === undefined) continue;
    // Tint the gauge's tooltip line to match its chart color — the reader maps "cyan row = cyan curve" at a glance.
    lines.push({ text: `${spec.label}: ${formatGaugeValue(value, spec.unit)}`, color: spec.color });
  }

  if (capacity !== undefined) {
    // Pick the unit from the first spec of this group — all capacities match their group's primary unit.
    const unit = specs[0]?.unit ?? 'count';
    lines.push(`Capacity: ${formatGaugeValue(capacity, unit)}`);
  }

  return lines;
}

/**
 * Tooltip builder for the Tx+UoW group — emits the per-tick deltas that the chart draws, not the raw cumulative totals. For the
 * hovered tick, diffs the cumulative gauge value against the preceding snapshot to get "how many of X happened in this single tick."
 * Explanatory header clarifies what the numbers mean so a first-time reader isn't confused by the unit ("count per tick" vs "count").
 */
/**
 * GC-track tooltip. Matches what the track actually draws:
 *   - Header: group label + matched tick number.
 *   - Primary line: per-tick pause time in µs (same series the area chart uses, same accent color).
 *   - Event summary: count of GcStart / GcEnd events that landed inside the matched tick, grouped by generation. Lets the user
 *     read "this spike was ONE Gen2 collection" vs. "this spike was FOUR Gen0 collections" without hunting for individual markers.
 *   - Active suspension (if any): when the cursor lands inside a stop-the-world window, show that suspension's total duration so
 *     the user can distinguish "cursor on tick with GC pause" from "cursor on tick WITHIN the pause itself".
 */
function buildGcTooltipLines(trace: ProcessedTrace, groupLabel: string, cursorUs: number): TooltipLine[] {
  const summary = trace.summary ?? [];
  if (summary.length === 0) return [];

  // Find the tick containing cursorUs (binary search). summary is sorted by startUs and ticks are contiguous; use lower-bound on
  // endUs > cursor, then verify the match also satisfies startUs <= cursor.
  let lo = 0, hi = summary.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (summary[mid].startUs + summary[mid].durationUs > cursorUs) hi = mid;
    else lo = mid + 1;
  }
  if (lo >= summary.length) return [];
  const tick = summary[lo];
  if (cursorUs < tick.startUs) return [];

  const tickEnd = tick.startUs + tick.durationUs;

  // Per-tick pause time via the memoized series — same numbers the chart plots.
  const pauseSeries = getPerTickPauseSeries(trace);
  const pauseSample = pauseSeries[lo];
  const pauseUs = pauseSample !== undefined ? pauseSample.value : 0;

  // Per-generation GcStart / GcEnd counts inside this tick. gcEvents is sorted by timestampUs.
  const starts: number[] = [0, 0, 0];  // [Gen0, Gen1, Gen2+]
  const ends: number[]   = [0, 0, 0];
  for (const e of trace.gcEvents ?? []) {
    if (e.timestampUs >= tickEnd) break;
    if (e.timestampUs < tick.startUs) continue;
    const genBucket = e.generation >= 2 ? 2 : e.generation;
    if (e.kind === 7 /* GcStart */) starts[genBucket]++;
    else if (e.kind === 8 /* GcEnd */)   ends[genBucket]++;
  }

  // Cursor-inside-a-suspension: linear scan is fine; we're at ~10s of events per trace. If this becomes a hot path a binary search
  // can replace the loop. Shows the TOTAL suspension duration, not just the portion inside this tick.
  let cursorSuspensionUs = 0;
  for (const s of trace.gcSuspensions ?? []) {
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
    lines.push(``);
    lines.push(`(GC events in this tick)`);
    if (starts[0] + ends[0] > 0) {
      lines.push({ text: `Gen0 start/end:  ${starts[0]} / ${ends[0]}`, color: GC_GEN_COLORS[0] });
    }
    if (starts[1] + ends[1] > 0) {
      lines.push({ text: `Gen1 start/end:  ${starts[1]} / ${ends[1]}`, color: GC_GEN_COLORS[1] });
    }
    if (starts[2] + ends[2] > 0) {
      lines.push({ text: `Gen2+ start/end: ${starts[2]} / ${ends[2]}`, color: GC_GEN_COLORS[2] });
    }
  }

  if (cursorSuspensionUs > 0) {
    lines.push(``);
    lines.push(`(cursor is INSIDE a suspension)`);
    lines.push(`Pause total:    ${formatDurationInline(cursorSuspensionUs)}`);
  }

  return lines;
}

/** Thin local wrapper around <see cref="formatDuration"/>. Kept local so this file doesn't need another canvasUtils import. */
function formatDurationInline(us: number): string {
  if (us < 1) return '0 µs';
  if (us < 1000) return `${us.toFixed(0)} µs`;
  if (us < 1_000_000) return `${(us / 1000).toFixed(us < 10_000 ? 2 : 1)} ms`;
  return `${(us / 1_000_000).toFixed(2)} s`;
}

function buildTxUowTooltipLines(trace: ProcessedTrace, groupLabel: string, cursorUs: number): TooltipLine[] {
  // Binary-search for the nearest snapshot-bearing tick, same as the generic path. For Tx+UoW we ALSO need the predecessor
  // (for delta computation), which we find by walking backwards from the matched tick's array index until we hit another
  // snapshot — typically 0 or 1 steps because snapshots are emitted at every tick in practice.
  const bestTick = findNearestSnapshotTick(trace.ticks, cursorUs);
  if (bestTick === undefined || bestTick.gaugeSnapshot === undefined) return [];
  const cur = bestTick.gaugeSnapshot;
  const bestArrayIdx = trace.ticks.indexOf(bestTick);
  let prev: typeof cur | undefined;
  for (let i = bestArrayIdx - 1; i >= 0; i--) {
    if (trace.ticks[i].gaugeSnapshot !== undefined) {
      prev = trace.ticks[i].gaugeSnapshot;
      break;
    }
  }

  const delta = (id: GaugeId): number | undefined => {
    const curV = cur.values.get(id);
    if (curV === undefined) return undefined;
    const prevV = prev?.values.get(id) ?? 0;  // first tick: delta = current (implicit baseline of 0 before the run started)
    return Math.max(0, curV - prevV);         // cumulative counters are monotonic; clamp defensively against counter resets
  };

  const created = delta(GaugeId.TxChainCreatedTotal);
  const commits = delta(GaugeId.TxChainCommitTotal);
  const rollbacks = delta(GaugeId.TxChainRollbackTotal);
  const uowCreated = delta(GaugeId.UowRegistryCreatedTotal);
  const uowCommitted = delta(GaugeId.UowRegistryCommittedTotal);
  // Snapshot (live-state) gauges — read directly off the current snapshot, not differenced. These complement the per-tick deltas
  // by showing "what's alive right now" instead of "what happened this tick."
  const txActive = cur.values.get(GaugeId.TxChainActiveCount);
  const txPool = cur.values.get(GaugeId.TxChainPoolSize);
  const uowActive = cur.values.get(GaugeId.UowRegistryActiveCount);
  const uowVoid = cur.values.get(GaugeId.UowRegistryVoidCount);

  const fmt = (v: number | undefined): string => v === undefined ? '—' : formatGaugeValue(v, 'count');
  // Per-line coloring mirrors the renderer: active-count lines use the area fill color, rate lines use their overlay-line color.
  // Tx pool (idle) + UoW void are tooltip-only (not drawn on-chart), so they stay in the default TEXT_COLOR.
  return [
    groupLabel,
    `Tick: ${cur.tickNumber}`,
    `(live state — what's alive at this exact tick)`,
    { text: `Tx active:       ${fmt(txActive)}`,   color: TX_ACTIVE_COLOR },
    `Tx pool (idle):  ${fmt(txPool)}`,
    { text: `UoW active:      ${fmt(uowActive)}`,  color: UOW_ACTIVE_COLOR },
    `UoW void:        ${fmt(uowVoid)}`,
    ``,
    `(per-tick counts — what happened in this one tick)`,
    { text: `Tx created:      ${fmt(created)}`,    color: TX_CREATED_COLOR },
    { text: `Tx commits:      ${fmt(commits)}`,    color: TX_COMMIT_COLOR },
    { text: `Tx rollbacks:    ${fmt(rollbacks)}`,  color: TX_ROLLBACK_COLOR },
    { text: `UoW created:     ${fmt(uowCreated)}`, color: UOW_CREATED_COLOR },
    { text: `UoW committed:   ${fmt(uowCommitted)}`, color: UOW_COMMITTED_COLOR },
  ];
}

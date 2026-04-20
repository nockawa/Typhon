import type { Viewport } from './uiTypes';

// ═══════════════════════════════════════════════════════════════════════
// Palettes
// ═══════════════════════════════════════════════════════════════════════
//
// Strategy: each major UI section gets a **dedicated palette**, picked so that the hues carry section-specific semantic meaning and
// two items from different sections are visually distinguishable from each other even when placed adjacently. This replaces the
// earlier "one palette for everything" approach — a single ring big enough for both gauge lines and span bars ended up compromising
// on both (spans needed a narrower warm ramp; gauges wanted explicit semantic colors). Splitting means each palette can be tuned to
// its context, and unrelated items never accidentally pick the same color because they happen to hash to the same index.

/**
 * **Gauge palette** — dedicated 8-color Viridis ramp used for every gauge-region surface (line colors, accent stripes, heap-gen
 * stacked area, page-cache buckets, GC markers, Tx/UoW lines). Walks purple → indigo → blue → teal → green → lime → yellow so
 * adjacent indices are distinguishable AND the ring reads as a meaningful cool-to-warm progression when sorted. Sized exactly at
 * 8 — matches the Memory track's single-track maximum (accent + 5 heap gens + unmanaged + peak = 8 distinct colors needed in one
 * track), leaving zero waste.
 *
 * Callers pick by **explicit index** per semantic role. Same index can repeat across different tracks (e.g., [7] yellow = both
 * GC Gen2+ in the GC track AND Peak line in Memory track), which is fine because the tracks are spatially separated and the
 * semantic meaning ("most expensive / hottest state") is consistent across them.
 *
 * Three-palette strategy: <c>GAUGE_PALETTE</c> (this, gauges), <c>SPAN_PALETTE</c> (spans + flame graph), <c>TIMELINE_PALETTE</c>
 * (phase bars + mini-row strips). Distinct visual languages so the eye can tell "gauge signal / code execution / operation bar"
 * apart at a glance.
 */
/**
 * **Overview palette** — dedicated 3-color set for the tick-overview timeline (the top bar showing per-tick duration + selection range).
 * Exactly-sized to the three data-carrying roles: normal bar, over-P95 warning bar, and selection range. No hash-modulo, no semantic
 * reuse — each index maps 1:1 to a fixed role. Hover outlines and P95 reference lines deliberately stay off-palette (neutral white /
 * grey chrome) so the palette isn't weighed down with non-data roles.
 */
export const OVERVIEW_PALETTE = {
  bar: '#252E55',          // dark navy — baseline per-tick duration bar (low visual weight so only the outliers grab attention)
  selection: '#F6D85C',    // warm yellow, matched to GAUGE_PALETTE[7] — selected time range (rendered at 25% fill + 70% border via the alpha-hex suffixes applied at the use site)
  overP95: '#00C4FF',      // bright cyan — tick exceeded its P95 budget; high-luminance cool hue against the navy bar makes outliers pop without clashing with the yellow selection rect
};

export const GAUGE_PALETTE = [
  // Slot 0 was #2A186A (deep indigo-purple) but read as almost-black against the dark-grey tooltip background — the Memory gauge's
  // "Unmanaged" line + Tx+UoW's active-UoW area color both sit here and were unreadable when tinted onto the tooltip. Brightened
  // 75% (RGB × 1.75, rounded) to #4A2ABA so the text clears the perceptual threshold for readable contrast on the tooltip bg.
  // Hue preserved; slot 0 is still the darkest in the Viridis ramp, just no longer visually collapsing into the background.
  '#4A2ABA',  // 0  — deep indigo-purple (brightened 75% from #2A186A for tooltip contrast)
  '#1C3B84',  // 1  — navy blue
  '#14618D',  // 2  — ocean blue
  '#1E8784',  // 3  — teal
  '#35A96D',  // 4  — green
  '#76BA3E',  // 5  — olive-green
  '#C3C22E',  // 6  — mustard
  '#F6D85C',  // 7  — warm yellow
];

/**
 * **Span palette** — dedicated 8-color warm ramp used for ALL span-like rendering: top-level system chunks on the timeline, nested
 * spans inside chunks (Transaction.Commit, BTree.Insert, etc.), and flame-graph nodes. A dark-purple → magenta → coral → amber
 * progression, tuned for the dense mosaic the flame/span view produces — warmer overall than the gauge palette so the timeline and
 * gauge region don't read as "more of the same."
 *
 * Sized at 8 colors (not 16) because spans hash into this palette: collision rate scales with `1/palette_size`, and 8 is the point
 * where neighboring hues stay visually distinct at the 1-2 px widths spans can collapse to at zoomed-out levels. 16 was too finely
 * split — adjacent indices blurred together at small rendering sizes.
 *
 * Every span-coloring site in the codebase (two <c>nameToColor</c> functions + <c>getSystemColor</c>) routes through this array.
 * Replacing an entry re-colors everything in the span/timeline/flame-graph surface uniformly.
 */
export const SPAN_PALETTE = [
  '#2B1255',  // 0  — deep violet
  '#541965',  // 1  — rich plum
  '#7E266B',  // 2  — dusky plum
  '#A6386C',  // 3  — magenta-rose
  '#C7506B',  // 4  — coral pink
  '#DF6C69',  // 5  — salmon
  '#ED8C66',  // 6  — peach
  '#F5AB65',  // 7  — warm amber
];

/**
 * **Timeline-bar palette** — dedicated 13-color Turbo ramp used for the timeline's phase bars and the per-operation mini-row strips
 * that live under each slot lane (Page cache Fetch/Allocate/Evicted/Flush, Disk Reads/Writes, Transactions Commits/Rollbacks/Persists,
 * WAL Flushes/Waits, Checkpoint Cycles). Sized exactly at 13 — matches the count of distinct operation types that need their own
 * color, zero waste, zero collision potential. Each operation type gets a stable semantic index (see timeline-bar renderer for the
 * mapping).
 *
 * Cool-to-warm ladder deliberately flows through the operation semantics:
 *   0–1 context/phases (darkest cool),
 *   2–5 memory-side ops (blues / teals),
 *   6–7 transactional success states (greens),
 *   8   durability (lime),
 *   9   periodic overhead (yellow, checkpoint),
 *   10–11 I/O write pressure (orange / red-orange, WAL),
 *   12  failure (dark red, rollback).
 *
 * This palette is separate from both <c>PALETTE</c> (gauge region) and <c>SPAN_PALETTE</c> (span bars + flame graph) — three distinct
 * color languages, one per UI section, so the eye can tell "gauge signal / code execution / timeline bar" apart at a glance.
 */
export const TIMELINE_PALETTE = [
  '#30123B',  // 0  — deep purple    ← Phases (background context)
  '#413E93',  // 1  — indigo         ← Cache Fetch
  '#4568D7',  // 2  — blue           ← Cache Allocate
  '#4490FE',  // 3  — bright blue    ← Cache Evicted
  '#2FB6EA',  // 4  — cyan           ← Cache Flush (writeback)
  '#1BD6C3',  // 5  — teal           ← Disk Read
  '#29EF7F',  // 6  — green          ← Disk Write
  '#87F859',  // 7  — bright green   ← Tx Commit (success)
  '#C1EE3B',  // 8  — lime           ← Tx Persist (durability sub-step)
  '#EDD03A',  // 9  — yellow         ← Checkpoint Cycle (periodic)
  '#F99B29',  // 10 — orange         ← WAL Flush
  '#CF5916',  // 11 — red-orange     ← WAL Wait (stall)
  '#7A0403',  // 12 — dark red       ← Tx Rollback (failure)
];

/** Phase bars use index 0 of the timeline palette — kept as a named export so call sites read "PHASE_COLOR" instead of the raw index. */
export const PHASE_COLOR = TIMELINE_PALETTE[0];
export const SELECTED_COLOR = '#e94560';
// UI chrome — near-neutral greys with a tiny blue tint (B channel +3 to +6 over R/G). The old palette leaned heavily on navy (#0f3460
// etc.); data viz best-practice is to keep chrome neutral so the eye focuses on the *data* colors. The tiny blue bias preserves the
// "profiler feels cool and serious" mood without the data tracks fighting the background for attention.
export const BG_COLOR = '#1c1c1f';       // deep grey, B=R+3 (near-black, tiny cool tint)
export const HEADER_BG = '#26262a';      // mid-dark grey, B=R+4 (panel / header background)
export const BORDER_COLOR = '#34343a';   // lighter grey, B=R+6 (subtle dividers)
export const TEXT_COLOR = '#e0e0e0';     // already neutral
export const DIM_TEXT = '#888';          // already neutral
export const GRID_COLOR = '#202025';     // ruler grid lines, B=R+5

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

/**
 * Convert an absolute timestamp (µs) to a pixel X coordinate in canvas coordinates.
 *
 * <paramref name="vp.offsetX"/> is the viewport's left-edge timestamp in absolute µs. The pixel-X is measured from the canvas left
 * edge, so content starts at <paramref name="labelWidth"/> (past the gutter) and grows right as timestamps exceed <c>vp.offsetX</c>.
 */
export function toPixelX(us: number, vp: Viewport, labelWidth: number): number {
  return labelWidth + (us - vp.offsetX) * vp.scaleX;
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

/**
 * One line inside a tooltip. Strings render in the default <see cref="TEXT_COLOR"/>; objects carry an explicit <c>color</c>
 * so a caller can match the line's color to the signal it describes (e.g., the "Gen0: 4 MB" line in a gauge tooltip colors
 * itself with the same hue the Gen0 layer uses in the stacked area). Empty string / empty text means a vertical spacer row.
 */
export type TooltipLine = string | { text: string; color?: string };

function tooltipLineText(line: TooltipLine): string {
  return typeof line === 'string' ? line : line.text;
}

/** Draw a tooltip box with multiple lines of text. Each line may carry its own color. */
export function drawTooltip(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  lines: TooltipLine[],
  maxX: number,
  maxY: number
): void {
  ctx.font = '11px monospace';
  // Measure the widest line so the tooltip background fits all content. Minimum 120 px so short single-line tooltips
  // don't look weirdly narrow.
  let maxLineW = 120;
  for (const line of lines) {
    const w = ctx.measureText(tooltipLineText(line)).width;
    if (w > maxLineW) maxLineW = w;
  }
  const tooltipW = maxLineW + 16;
  const tooltipH = lines.length * 16 + 8;
  const tx = Math.min(x + 12, maxX - tooltipW - 4);
  const ty = Math.min(y + 12, maxY - tooltipH - 4);

  ctx.fillStyle = 'rgba(29, 30, 32, 0.95)';
  ctx.strokeStyle = BORDER_COLOR;
  ctx.lineWidth = 1;
  ctx.fillRect(tx, ty, tooltipW, tooltipH);
  ctx.strokeRect(tx, ty, tooltipW, tooltipH);

  ctx.textAlign = 'left';
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    // Per-line color override lets gauge-tooltip callers tint each metric line with the same hue its chart glyph uses, so the
    // reader can visually map "this line → that curve" without reading a separate legend.
    ctx.fillStyle = typeof line === 'string' ? TEXT_COLOR : (line.color ?? TEXT_COLOR);
    ctx.fillText(tooltipLineText(line), tx + 6, ty + 14 + i * 16);
  }
}

/**
 * Color for a system-indexed top-level chunk bar on the timeline. Routes through <see cref="SPAN_PALETTE"/> (not the main
 * <see cref="PALETTE"/>) since system chunks are a span-like surface. Modulo with the palette size guarantees the same system
 * always resolves to the same color across a session, and adjacent system indices get maximally-separated hues in the 8-color ring.
 */
export function getSystemColor(index: number): string {
  return SPAN_PALETTE[index % SPAN_PALETTE.length];
}

/**
 * Compute the contrast text color for a hex background. Rec. 601 perceived-brightness weighting (0.299R + 0.587G + 0.114B), threshold 0.55.
 * Returns <c>#111</c> on bright / <c>#eee</c> on dark / <c>#eee</c> on malformed input (defensive). Internal — most callers should go through
 * <see cref="pickContrastTextColor"/> which caches palette results.
 */
function computeContrastTextColor(hexBackground: string): string {
  const hex = hexBackground.startsWith('#') ? hexBackground.slice(1) : hexBackground;
  if (hex.length < 6) return '#eee';
  const r = parseInt(hex.slice(0, 2), 16);
  const g = parseInt(hex.slice(2, 4), 16);
  const b = parseInt(hex.slice(4, 6), 16);
  if (Number.isNaN(r) || Number.isNaN(g) || Number.isNaN(b)) return '#eee';
  const brightness = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return brightness > 0.55 ? '#111' : '#eee';
}

/**
 * Hash a span/node name to an index into <see cref="SPAN_PALETTE"/>. Shared single implementation — previously duplicated as local
 * <c>nameToColor</c> functions in <c>GraphArea.tsx</c> and <c>FlameGraph.tsx</c>. Callers get the index and then look up fill +
 * text color via direct array access (<c>SPAN_PALETTE[idx]</c> / <c>SPAN_PALETTE_TEXT_COLOR[idx]</c>), eliminating the string-keyed
 * Map lookup on the rendering hot path.
 *
 * Hash: djb2-style <c>((h &lt;&lt; 5) - h) + ch</c>. Cached per name because the same span/node names recur every frame — with typical
 * traces containing tens of thousands of identical names over the session, the hash is computed once per unique name and then served
 * from the <c>Map</c> at ~30 ns/call. The cache is unbounded but name cardinality in practice is hundreds at most (system names,
 * operation kinds, function names), so memory is negligible.
 */
const _nameIndexCache = new Map<string, number>();
export function nameToPaletteIndex(name: string): number {
  const cached = _nameIndexCache.get(name);
  if (cached !== undefined) return cached;
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) - hash + name.charCodeAt(i)) | 0;
  }
  // `>>> 0` coerces to unsigned before the modulo — JS bitwise ops produce signed 32-bit ints that can be negative.
  const idx = (hash >>> 0) % SPAN_PALETTE.length;
  _nameIndexCache.set(name, idx);
  return idx;
}

/**
 * Parallel array to <see cref="SPAN_PALETTE"/> — precomputed contrast text color for each palette entry. Indexed by the same position as
 * <see cref="SPAN_PALETTE"/>, so consumers that already have a palette index (e.g. <see cref="getSystemColor"/> / <c>nameToPaletteIndex</c>
 * call sites that first hash-mod into the palette) can skip the Map lookup entirely and go straight to <c>SPAN_PALETTE_TEXT_COLOR[index]</c>.
 *
 * Shape chosen as `readonly string[]` (not `Record<string,string>`) specifically because V8 inlines numeric-indexed access on small dense arrays
 * into direct memory loads — faster than object-property or Map-keyed access in a rendering hot path where this is called hundreds of times
 * per frame across timeline bars + flame-graph nodes.
 */
export const SPAN_PALETTE_TEXT_COLOR: readonly string[] = SPAN_PALETTE.map(computeContrastTextColor);

/**
 * Lookup-backed variant keyed by hex string. For callers that already have the fill color as a string (rather than its palette index), this is
 * O(1) Map.get on the cached palette entries. Off-palette inputs (none today, but future-proofed for e.g. interpolated colors) fall through to
 * <see cref="computeContrastTextColor"/>. Precomputed at module load — no per-call arithmetic for any palette color.
 */
const PALETTE_TEXT_CACHE: ReadonlyMap<string, string> = new Map(
  SPAN_PALETTE.map((c, i) => [c, SPAN_PALETTE_TEXT_COLOR[i]])
);

/**
 * Pick a foreground text color (dark or light) with acceptable contrast against the given hex background. O(1) cache hit for any
 * <see cref="SPAN_PALETTE"/> entry (100 % of current traffic); fallback recompute for off-palette inputs. Used on the rendering hot path —
 * chunk bars, span bars, flame-graph nodes — so the palette-hit path must not allocate or parse strings.
 */
export function pickContrastTextColor(hexBackground: string): string {
  const cached = PALETTE_TEXT_CACHE.get(hexBackground);
  if (cached !== undefined) return cached;
  return computeContrastTextColor(hexBackground);
}

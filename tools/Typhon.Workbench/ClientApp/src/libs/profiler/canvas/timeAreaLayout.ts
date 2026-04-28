import type { SpanData, TickData } from '@/libs/profiler/model/traceModel';
import type { TimeRange, TrackLayout, TrackState } from '@/libs/profiler/model/uiTypes';
import { appendGaugeTracks } from './gauges/region';

/**
 * Pure layout composer for the main time area. Originally ported from the retired
 * `Typhon.Profiler.Server/ClientApp/src/GraphArea.tsx::buildLayout()` (~90 LOC).
 *
 * Returns a flat `TrackLayout[]` — one entry per rendered track, top-to-bottom. Tracks are
 * either fixed-purpose ('ruler', 'phases', 'page-cache', 'disk-io', 'transactions', 'wal',
 * 'checkpoint') or per-thread-slot ('slot-N'). **Gauge tracks are deferred to Phase 2c**:
 * this function never inserts them, never imports `gaugeRegion.ts` / `gaugeGroupRenderers.ts`.
 * When 2c lands it will wrap this module with a gauge-injection step at {@link GAUGE_INSERT_AFTER}.
 */

export const RULER_HEIGHT = 24;
export const TRACK_HEIGHT = 28;
export const TRACK_GAP = 2;
export const PHASE_TRACK_HEIGHT = 20;
export const COLLAPSED_HEIGHT = 4;
export const LABEL_ROW_HEIGHT = 18;
export const SUMMARY_STRIP_HEIGHT = 14;
export const SUMMARY_STRIP_TOP_PAD = (LABEL_ROW_HEIGHT - SUMMARY_STRIP_HEIGHT) / 2;
export const MINI_ROW_HEIGHT = 11;
export const FLAME_ROW_HEIGHT = 14;
export const SPAN_ROW_HEIGHT = 22;
export const MIN_RECT_WIDTH = 1;

/** Ruler is always first; gauges insert right after it. */
export const GAUGE_INSERT_AFTER: TrackLayout['id'] = 'ruler';

export interface BuildLayoutInputs {
  /** Ordered thread slots that ever emitted chunks or spans. */
  activeSlots: number[];
  /** Subset of `activeSlots` that have ≥1 scheduler chunk somewhere in the session. */
  slotsWithChunks: Set<number>;
  /** slotIdx → deepest observed span depth on that slot. Missing = no spans on this slot. */
  spanMaxDepthBySlot: Map<number, number>;
  /** Optional slot→friendly-name map from trace metadata (ThreadInfo records). */
  threadNames: Record<number, string> | null | undefined;
  /** Track id → user-chosen collapse state (applies to slot lanes + fixed tracks + gauges). */
  collapseState: Record<string, TrackState>;
  /** Whether the gauge region is visible. `false` → `appendGaugeTracks` returns startY unchanged. */
  gaugeRegionVisible: boolean;
  /** Per-gauge-group collapse state (3-enum). Used by `appendGaugeTracks`. */
  gaugeCollapse: Record<string, TrackState>;
  /**
   * Ordered system indices that emitted ≥1 chunk in the session. One `system-N` track is emitted
   * per entry. Phase 2d — a second lens on the same `ChunkSpan[]` data the slot lanes already
   * render (by thread slot), re-grouped by ECS system.
   */
  activeSystems: number[];
  /** systemIndex → friendly name from `metadata.systems[i].name`. Fallback to `System N`. */
  systemNames: Record<number, string> | null | undefined;
  /** Whether the per-system lanes section is visible. `false` → skip emitting `system-*` tracks. */
  perSystemLanesVisible: boolean;
  /**
   * Per-track visibility maps from the section-filter popup. A track is rendered iff its key is absent
   * OR maps to `true`. Missing maps default to "everything visible" — preserves the old layout behavior
   * for callers that don't care about filtering.
   */
  slotVisibility?: Record<number, boolean>;
  systemVisibility?: Record<number, boolean>;
  gaugeVisibility?: Record<string, boolean>;
  engineOpVisibility?: Record<string, boolean>;
}

export interface LayoutResult {
  tracks: TrackLayout[];
  totalHeight: number;
}

/**
 * Build the track layout. Pure — same inputs always produce the same tracks[] and totalHeight.
 * Caller is responsible for caching across renders (React wrapper stores the result in a ref).
 */
export function buildLayout(inputs: BuildLayoutInputs): LayoutResult {
  const { activeSlots, slotsWithChunks, spanMaxDepthBySlot, threadNames, collapseState, gaugeRegionVisible, gaugeCollapse, activeSystems, systemNames, perSystemLanesVisible } = inputs;
  const slotVisibility = inputs.slotVisibility;
  const systemVisibility = inputs.systemVisibility;
  const gaugeVisibility = inputs.gaugeVisibility;
  const engineOpVisibility = inputs.engineOpVisibility;
  const tracks: TrackLayout[] = [];
  let y = 0;

  // Ruler — always first, never collapsible.
  tracks.push({ id: 'ruler', label: 'Time', y, height: RULER_HEIGHT, state: 'expanded', collapsible: false });
  // Trailing gap so the next track's top edge doesn't collide with the ruler separator line.
  y += RULER_HEIGHT + TRACK_GAP;

  // Gauges (2c) — six groups inserted between ruler and slot lanes. `appendGaugeTracks` pushes
  // up to six `TrackLayout` entries into the array in place and returns the next Y. When
  // `gaugeRegionVisible` is false the call returns `y` unchanged. `gaugeVisibility` filters out
  // individual gauge groups whose key is `false`.
  y = appendGaugeTracks(tracks, y, gaugeCollapse, SUMMARY_STRIP_HEIGHT, LABEL_ROW_HEIGHT, TRACK_GAP, gaugeRegionVisible, gaugeVisibility);

  // Non-gauge tracks are 2-state only (summary / expanded). `double` is gauge-only; if it ever appears
  // in `collapseState` for a non-gauge id we coerce it to `expanded`.
  const nonGaugeState = (id: string): TrackState => {
    const s = collapseState[id];
    return s === 'summary' ? 'summary' : 'expanded';
  };

  // Non-gauge height helper — `stripInsideLabel` controls whether the summary-mode strip sits inside
  // the label row's vertical band (slot lanes) or below it as a 4-px strip (fixed tracks, legacy).
  const nonGaugeHeight = (state: TrackState, expandedBody: number, stripInsideLabel = false): number => {
    if (state === 'summary') {
      return stripInsideLabel ? LABEL_ROW_HEIGHT + TRACK_GAP : LABEL_ROW_HEIGHT + COLLAPSED_HEIGHT;
    }
    return expandedBody + TRACK_GAP;
  };

  // One lane per active thread slot. Chunk row sits on top (only when the slot ever emitted a chunk);
  // span rows stack below it, sized to the slot's deepest observed depth. Span-only slots skip the
  // chunk row entirely so depth-0 spans land at the lane's top edge.
  for (const slotIdx of activeSlots) {
    if (slotVisibility?.[slotIdx] === false) continue;
    const id = `slot-${slotIdx}`;
    const state = nonGaugeState(id);
    const hasChunks = slotsWithChunks.has(slotIdx);
    const chunkRowHeight = hasChunks ? TRACK_HEIGHT : 0;
    const maxDepth = spanMaxDepthBySlot.get(slotIdx);
    const spanRegionHeight = maxDepth === undefined ? 0 : (maxDepth + 1) * SPAN_ROW_HEIGHT + 2;
    const expandedBody = Math.max(TRACK_HEIGHT, chunkRowHeight + spanRegionHeight);
    const h = nonGaugeHeight(state, expandedBody, /* stripInsideLabel */ true);

    const name = threadNames?.[slotIdx];
    tracks.push({
      id,
      label: name ? name : `Slot ${slotIdx}`,
      y,
      height: expandedBody,
      state,
      collapsible: true,
      chunkRowHeight,
    });
    y += h;
  }

  // Per-system chunk lanes (2d) — one track per ECS system that emitted ≥1 chunk. A second lens
  // on the chunk data: "when did System X run?" Same colour identity as the slot-lane chunk row
  // (both go through `getSystemColor(systemIndex, theme.spans)` so a chunk has the same hex in
  // both views). Default state = `summary` so a many-system trace doesn't eat the screen on
  // first open; user clicks the chevron to expand.
  if (perSystemLanesVisible) {
    for (const systemIdx of activeSystems) {
      if (systemVisibility?.[systemIdx] === false) continue;
      const id = `system-${systemIdx}`;
      const stored = collapseState[id];
      const state: TrackState = stored === 'summary' || stored === 'expanded' ? stored : 'summary';
      const expandedBody = TRACK_HEIGHT;
      const h = nonGaugeHeight(state, expandedBody, /* stripInsideLabel */ true);
      const name = systemNames?.[systemIdx];
      tracks.push({
        id,
        label: name ? name : `System ${systemIdx}`,
        y,
        height: expandedBody,
        state,
        collapsible: true,
      });
      y += h;
    }
  }

  // Fixed operation tracks below the slot lanes. Order here matches the old client and is the stable
  // semantic grouping: phases (scheduler), then disk/cache (storage), then tx/wal/checkpoint (durability).
  // Filtered out per-track via `engineOpVisibility[id] === false`.
  const pushFixed = (id: string, label: string, expandedBody: number): void => {
    if (engineOpVisibility?.[id] === false) return;
    const state = nonGaugeState(id);
    tracks.push({ id, label, y, height: expandedBody, state, collapsible: true });
    y += nonGaugeHeight(state, expandedBody);
  };
  pushFixed('phases',       'Phases',       PHASE_TRACK_HEIGHT);
  pushFixed('page-cache',   'Page Cache',   4 * MINI_ROW_HEIGHT);
  pushFixed('disk-io',      'Disk IO',      2 * MINI_ROW_HEIGHT);
  pushFixed('transactions', 'Transactions', 3 * MINI_ROW_HEIGHT);
  pushFixed('wal',          'WAL',          2 * MINI_ROW_HEIGHT);
  pushFixed('checkpoint',   'Checkpoint',   MINI_ROW_HEIGHT);

  return { tracks, totalHeight: y };
}

/**
 * Derive the list of ECS system indices that emitted ≥1 chunk across the loaded ticks. Used by
 * 2d's per-system lanes. O(T × C) per call — the React wrapper memoises on `ticks` identity so
 * this runs once per cache-version bump, not per frame.
 */
export function deriveActiveSystems(ticks: TickData[]): number[] {
  const set = new Set<number>();
  for (const tick of ticks) {
    for (const chunk of tick.chunks) {
      set.add(chunk.systemIndex);
    }
  }
  return Array.from(set).sort((a, b) => a - b);
}

/**
 * Derive the `{activeSlots, slotsWithChunks, spanMaxDepthBySlot}` triple from a list of ticks,
 * and assign every span a `renderDepth` via greedy interval packing across its slot.
 *
 * Why pack: spans carry a `depth` field that reflects the recorder's view of the call stack at
 * completion time. Long-running ops (Checkpoint.Cycle, GC suspensions) are recorded at the tick
 * where they *complete*, and their sub-ops that completed earlier got attributed to *earlier*
 * ticks with their own `depth=0` — so temporally-overlapping bars can share `depth` and draw on
 * top of each other. Packing produces a per-slot render row that guarantees two bars only share
 * a row if they don't overlap in time.
 *
 * Algorithm: for each slot, gather every span across all loaded ticks, sort by (startUs asc,
 * endUs desc so longer outer bars win lower rows), then walk left-to-right assigning each span
 * to the first row whose last span already ended. O(N log N + N*R) per slot, where R = max
 * depth observed. For normal traces R is small (<10) so the linear row scan is fine.
 *
 * Mutates `span.renderDepth` in place. Safe because `assembleTickViewAndNumbers` memoises on the
 * cache's `entriesVersion`, so span objects are stable references — the mutation persists until
 * the cache mutates again (at which point this function re-runs against the new ticks array).
 */
export function deriveSlotInfo(ticks: TickData[]): {
  activeSlots: number[];
  slotsWithChunks: Set<number>;
  spanMaxDepthBySlot: Map<number, number>;
} {
  const slotSet = new Set<number>();
  const withChunks = new Set<number>();
  const spansBySlot = new Map<number, SpanData[]>();
  for (const tick of ticks) {
    for (const chunk of tick.chunks) {
      slotSet.add(chunk.threadSlot);
      withChunks.add(chunk.threadSlot);
    }
    for (const [slot, arr] of tick.spansByThreadSlot) {
      if (arr.length === 0) continue;
      slotSet.add(slot);
      let bucket = spansBySlot.get(slot);
      if (!bucket) { bucket = []; spansBySlot.set(slot, bucket); }
      // Concatenate rather than interleave — per-slot sort after the full collection handles it.
      for (const s of arr) bucket.push(s);
    }
  }

  const depthBySlot = new Map<number, number>();
  for (const [slot, bucket] of spansBySlot) {
    // Sort: startUs ascending (so the packer sees spans in time order); endUs descending as a
    // tiebreak so when two spans start together, the longer one wins the lower (outer) row.
    bucket.sort((a, b) => a.startUs - b.startUs || b.endUs - a.endUs);
    const rowEnds: number[] = []; // rowEnds[r] = endUs of the last span placed on row r
    let maxRow = 0;
    for (const span of bucket) {
      let row = -1;
      for (let r = 0; r < rowEnds.length; r++) {
        if (rowEnds[r] <= span.startUs) { row = r; break; }
      }
      if (row < 0) { row = rowEnds.length; rowEnds.push(span.endUs); }
      else { rowEnds[row] = span.endUs; }
      span.renderDepth = row;
      if (row > maxRow) maxRow = row;
    }
    depthBySlot.set(slot, maxRow);
  }

  return {
    activeSlots: Array.from(slotSet).sort((a, b) => a - b),
    slotsWithChunks: withChunks,
    spanMaxDepthBySlot: depthBySlot,
  };
}

/**
 * Filter all ticks down to those that intersect the viewport time range. Half-open on the right
 * edge — a tick whose `startUs` equals `viewRange.endUs` is *not* visible. Matches the rendering
 * convention used by `toPixelX` (end-exclusive).
 */
export function getVisibleTicks(allTicks: TickData[], viewRange: TimeRange): TickData[] {
  if (viewRange.endUs <= viewRange.startUs) return [];
  const out: TickData[] = [];
  for (const t of allTicks) {
    if (t.endUs > viewRange.startUs && t.startUs < viewRange.endUs) out.push(t);
  }
  return out;
}

/**
 * Gauge region layout. Each gauge group (GC, Memory, Persistence, Transient, WAL, Tx+UoW) maps to a <c>TrackLayout</c> entry
 * inserted between the ruler and the first slot lane by <see cref="GraphArea.buildLayout"/>. Per-group rendering lives in
 * <c>gaugeGroupRenderers.ts</c>; this module owns:
 *   - the group descriptor table (<see cref="GAUGE_GROUPS"/>)
 *   - the <see cref="TrackState"/> persistence (load/save to localStorage)
 *   - the append-to-layout routine used by the parent build-layout pass
 * The whole-region toggle (keyboard <c>g</c>) is handled in <c>App.tsx</c> and threaded in via the <c>regionVisible</c> flag.
 */

import type { TrackLayout, TrackState } from './uiTypes';
import { GAUGE_PALETTE } from './canvasUtils';

/** Canonical IDs — used as <c>TrackLayout.id</c>, localStorage keys, and the argument to click-handler toggles. */
export const GAUGE_TRACK_IDS = {
  Gc: 'gauge-gc',
  Memory: 'gauge-memory',
  Persistence: 'gauge-persistence',
  Transient: 'gauge-transient',
  Wal: 'gauge-wal',
  TxUow: 'gauge-tx-uow',
} as const;

/** Descriptor for one gauge category. Drives both the layout machinery and the per-group dispatch in <c>gaugeGroupRenderers</c>. */
export interface GaugeGroupSpec {
  id: string;
  label: string;
  /** Color dot rendered next to the chevron — category identity at a glance. */
  accentColor: string;
  /** Total track body height (excluding the 18 px label row) when the group is in <c>expanded</c> state. Doubled for <c>double</c>. */
  expandedHeight: number;
  /**
   * When <c>true</c>, the group's default <c>TrackState</c> on first load is <c>'summary'</c> (spark-line preview); when
   * <c>false</c>, it opens in <c>'expanded'</c>. localStorage overrides this. The naming is vestigial from the original
   * boolean expand state — kept for minimal diff.
   */
  defaultCollapsed: boolean;
}

/**
 * The six gauge groups, in top-to-bottom render order. GC / Memory / Page Cache default to `expanded` (primary signals; open
 * fully on first load); Transient / WAL / Tx+UoW default to `summary` (secondary signals; show a spark-line preview that the
 * user can click to expand when needed).
 */
export const GAUGE_GROUPS: GaugeGroupSpec[] = [
  // Accent color per track — each MUST be visually distinct from every other track's accent AND from the colors used inside its own
  // track. Memory consumes all non-accent palette slots (heap gens + unmanaged + peak = 7), which forces Memory's accent to GAUGE_PALETTE[1]
  // indigo — that's the only slot left. Other accents are then chosen to not collide with their track's in-content colors.
  {
    id: GAUGE_TRACK_IDS.Gc,
    label: 'GC',
    accentColor: GAUGE_PALETTE[2],  // #3B528B blue — in-track Gen colors use [3]/[5]/[7], suspension uses [7], so [2] sits apart
    expandedHeight: 40,
    defaultCollapsed: false,
  },
  {
    id: GAUGE_TRACK_IDS.Memory,
    label: 'Memory',
    accentColor: GAUGE_PALETTE[1],  // #472D7A indigo — forced; heap gens + unmanaged + peak use every other slot
    expandedHeight: 80,
    defaultCollapsed: false,
  },
  {
    id: GAUGE_TRACK_IDS.Persistence,
    label: 'Page Cache',
    accentColor: GAUGE_PALETTE[4],  // #22928B mint-teal — buckets use [1]/[3]/[6]/[7] + overlays [2]/[5], so [4] is the untouched mid-band
    expandedHeight: 60,
    defaultCollapsed: false,
  },
  {
    id: GAUGE_TRACK_IDS.Transient,
    label: 'Transient Store',
    accentColor: GAUGE_PALETTE[6],  // #67CB5E lime — visually distinct from other tracks' accents; the Transient renderer uses slot [1] for its area fill
    expandedHeight: 24,
    defaultCollapsed: true,
  },
  {
    id: GAUGE_TRACK_IDS.Wal,
    label: 'WAL',
    accentColor: GAUGE_PALETTE[7],  // #FDE725 yellow — "hot write path" fits WAL's semantic; in-content uses [2]/[4]/[6], accent [7] stays apart
    expandedHeight: 80,
    defaultCollapsed: true,
  },
  {
    id: GAUGE_TRACK_IDS.TxUow,
    label: 'Transactions + UoW',
    accentColor: GAUGE_PALETTE[0],  // #440154 dark purple — distinct from the 4 line colors inside ([5] green / [7] yellow / [3] teal / [6] lime)
    // Expanded height matches WAL / Memory (80 px) so each sub-row has ~40 px — room for an area chart (active count) plus two-three
    // overlay rate lines. The previous 50 px only fit rate lines and left no vertical budget for the live-state "active" signal.
    expandedHeight: 80,
    defaultCollapsed: true,
  },
];

/** Set of IDs the GraphArea render loop uses to dispatch gauge tracks through <c>gaugeGroupRenderers</c>. */
export const GAUGE_TRACK_ID_SET: ReadonlySet<string> = new Set(GAUGE_GROUPS.map(g => g.id));

/**
 * Resolve the initial <c>collapseState</c> entry for a gauge group — honours localStorage override, falls back to the group's
 * <c>defaultCollapsed</c>. Called from <c>GraphArea</c> on mount to seed state before first render.
 *
 * localStorage format (<c>typhon-profiler.gaugeCollapse</c>): a JSON object mapping gauge track ID → boolean collapsed state.
 * Non-gauge track IDs (like the existing <c>slot-N</c> / <c>phases</c>) are NOT persisted here — their collapse is a session-local
 * concern. The narrow scope keeps the persistence story simple and avoids an ever-growing localStorage blob.
 */
/**
 * Load the persisted per-gauge <see cref="TrackState"/> map. Old v2 data stored booleans; v3 stores the 3-state string enum.
 * Migration: a stored boolean <c>true</c> → <c>'summary'</c>, <c>false</c> → <c>'expanded'</c> — preserves the user's
 * expand/collapse intent across the upgrade without forcing a reset.
 */
export function loadPersistedGaugeCollapseState(): Record<string, TrackState> {
  const out: Record<string, TrackState> = {};
  let stored: Record<string, unknown> = {};
  try {
    const raw = window.localStorage.getItem(PERSISTED_COLLAPSE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed === 'object') {
        stored = parsed as Record<string, unknown>;
      }
    }
  } catch {
    // Corrupt JSON / private mode / etc. — silently fall back to defaults.
  }

  const coerce = (v: unknown, fallback: TrackState): TrackState => {
    // v3 string form
    if (v === 'summary' || v === 'expanded' || v === 'double') return v;
    // v2 boolean form — only the two legacy states are representable, so migrate accordingly
    if (typeof v === 'boolean') return v ? 'summary' : 'expanded';
    return fallback;
  };

  for (const group of GAUGE_GROUPS) {
    const defaultState: TrackState = group.defaultCollapsed ? 'summary' : 'expanded';
    out[group.id] = coerce(stored[group.id], defaultState);
  }
  return out;
}

/** Persist only the gauge-group slice of <c>collapseState</c> to localStorage. Other track IDs pass through untouched. */
export function persistGaugeCollapseState(collapseState: Record<string, TrackState>): void {
  const slice: Record<string, TrackState> = {};
  for (const group of GAUGE_GROUPS) {
    const v = collapseState[group.id];
    if (v === 'summary' || v === 'expanded' || v === 'double') {
      slice[group.id] = v;
    }
  }
  try {
    window.localStorage.setItem(PERSISTED_COLLAPSE_KEY, JSON.stringify(slice));
  } catch {
    // localStorage unavailable (private mode, quota) — we lose persistence this session but rendering still works.
  }
}

// v2 → v3 when the state moved from boolean (collapsed/expanded) to a three-state enum (summary/expanded/double). Migration
// lives in `loadPersistedGaugeCollapseState` above — old v2 data is still read and coerced into the v3 enum, so users don't
// lose their open/close preferences on upgrade.
export const PERSISTED_COLLAPSE_KEY = 'typhon-profiler.gaugeCollapse.v3';

/**
 * Append gauge-group tracks to <paramref name="layout"/> starting at Y coordinate <paramref name="startY"/>. Returns the Y coordinate
 * immediately after the last gauge track — the caller continues layout (slot lanes, phases, etc.) from there.
 *
 * Track sizing mirrors the existing convention: when collapsed, track reserves <c>COLLAPSED_HEIGHT + LABEL_ROW_HEIGHT</c>; when
 * expanded, <c>expandedHeight + TRACK_GAP</c>. The <c>height</c> field stores the EXPANDED body height so the gutter label position
 * is stable regardless of collapse state — same invariant as the existing tracks.
 */
export function appendGaugeTracks(
  layout: TrackLayout[],
  startY: number,
  collapseState: Record<string, TrackState>,
  summaryStripHeight: number,
  labelRowHeight: number,
  trackGap: number,
  regionVisible: boolean = true,
): number {
  // Whole-region hide (keyboard 'g' toggle). When false, skip every gauge group entirely — zero layout contribution, the span lanes
  // slide up to occupy the reclaimed space. Simpler than hiding individual tracks because no collapse state needs to be touched;
  // toggling 'g' off then on restores the user's per-group track-state settings exactly as they were.
  if (!regionVisible) return startY;

  let y = startY;
  for (const group of GAUGE_GROUPS) {
    const defaultState: TrackState = group.defaultCollapsed ? 'summary' : 'expanded';
    const state = collapseState[group.id] ?? defaultState;
    // Body height by state:
    //   summary  → spark-line fits INSIDE the label row band (both in the same Y range, label in gutter, strip in content
    //              area). Stored `height` = summaryStripHeight so the renderer knows how tall the spark-line visual is;
    //              the track's TOTAL Y advance is separate (labelRowHeight + trackGap).
    //   expanded → the group's configured expandedHeight
    //   double   → 2× expandedHeight (gauge-only "see variation clearly" mode)
    const bodyHeight =
      state === 'summary' ? summaryStripHeight :
      state === 'double' ? group.expandedHeight * 2 :
      group.expandedHeight;
    layout.push({
      id: group.id,
      label: group.label,
      y,
      height: bodyHeight,
      state,
      collapsible: true,
    });
    // Summary mode consumes only the label-row band + inter-track gap (the spark-line renders inside that band). Expanded
    // and double modes consume the full body height + gap.
    y += state === 'summary'
      ? (labelRowHeight + trackGap)
      : (bodyHeight + trackGap);
  }
  return y;
}

const PERSISTED_REGION_VISIBLE_KEY = 'typhon-profiler.gaugeRegionVisible';

/** Read the gauge-region-visible flag from localStorage. Defaults to true on first run or corrupt state. */
export function loadPersistedGaugeRegionVisible(): boolean {
  try {
    const raw = window.localStorage.getItem(PERSISTED_REGION_VISIBLE_KEY);
    if (raw === 'false') return false;
  } catch {
    // Private mode / storage disabled — silently default to visible.
  }
  return true;
}

/** Persist the gauge-region-visible flag. Only writes "false" — absence means visible, keeping localStorage clean on the default case. */
export function persistGaugeRegionVisible(visible: boolean): void {
  try {
    if (visible) {
      window.localStorage.removeItem(PERSISTED_REGION_VISIBLE_KEY);
    } else {
      window.localStorage.setItem(PERSISTED_REGION_VISIBLE_KEY, 'false');
    }
  } catch {
    // Silently ignore — cosmetic preference.
  }
}

const PERSISTED_LEGENDS_VISIBLE_KEY = 'typhon-profiler.gaugeLegendsVisible';

/** Read the gauge-legends-visible flag from localStorage. Defaults to true (show legends) on first run. */
export function loadPersistedLegendsVisible(): boolean {
  try {
    const raw = window.localStorage.getItem(PERSISTED_LEGENDS_VISIBLE_KEY);
    if (raw === 'false') return false;
  } catch {
    // Private mode / storage disabled — silently default to visible.
  }
  return true;
}

/** Persist the gauge-legends-visible flag. Same "only write false" convention as <see cref="persistGaugeRegionVisible"/>. */
export function persistLegendsVisible(visible: boolean): void {
  try {
    if (visible) {
      window.localStorage.removeItem(PERSISTED_LEGENDS_VISIBLE_KEY);
    } else {
      window.localStorage.setItem(PERSISTED_LEGENDS_VISIBLE_KEY, 'false');
    }
  } catch {
    // Silently ignore — cosmetic preference.
  }
}

/** Small helper that looks up the group descriptor by track id — O(1) for a small table; used by the draw dispatcher. */
export function getGaugeGroupSpec(trackId: string): GaugeGroupSpec | undefined {
  for (const group of GAUGE_GROUPS) {
    if (group.id === trackId) return group;
  }
  return undefined;
}

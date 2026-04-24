import type { TrackLayout, TrackState } from '@/libs/profiler/model/uiTypes';
import { GAUGE_PALETTE } from '../canvasUtils';

/**
 * Gauge-region layout — ported from the old profiler's `gaugeRegion.ts` with all localStorage
 * helpers dropped. Persistence lives in `useProfilerViewStore` (zustand + safeStorage + schema
 * migration for the 3-state enum). This module stays pure: group descriptors + `appendGaugeTracks`.
 *
 * The six groups render top-to-bottom in the order of {@link GAUGE_GROUPS}, inserted between the
 * ruler and the first slot lane by `timeAreaLayout.buildLayout()`.
 */

/** Canonical IDs — used as `TrackLayout.id`, view-store keys, and the click-handler argument. */
export const GAUGE_TRACK_IDS = {
  Gc: 'gauge-gc',
  Memory: 'gauge-memory',
  Persistence: 'gauge-persistence',
  Transient: 'gauge-transient',
  Wal: 'gauge-wal',
  TxUow: 'gauge-tx-uow',
} as const;

export interface GaugeGroupSpec {
  id: string;
  label: string;
  /** Colour dot rendered next to the chevron — category identity at a glance. */
  accentColor: string;
  /** Body height (excluding the 18 px label row) when the group is in `expanded` state. `double` = 2×. */
  expandedHeight: number;
  /**
   * `'summary'` on first load → spark-line preview; `'expanded'` → open fully. View-store overrides.
   */
  defaultState: TrackState;
}

/**
 * Six gauge groups in render order. GC / Memory / Page Cache default to `expanded` (primary
 * signals, opened on first load); Transient / WAL / Tx+UoW default to `summary` (secondary
 * signals; user expands on demand). Accent colours are picked from `GAUGE_PALETTE` so each group
 * is visually distinct both from its neighbours AND from the hues used inside its own chart.
 */
export const GAUGE_GROUPS: readonly GaugeGroupSpec[] = [
  { id: GAUGE_TRACK_IDS.Gc,          label: 'GC',                  accentColor: GAUGE_PALETTE[2], expandedHeight: 40, defaultState: 'expanded' },
  { id: GAUGE_TRACK_IDS.Memory,      label: 'Memory',              accentColor: GAUGE_PALETTE[1], expandedHeight: 80, defaultState: 'expanded' },
  { id: GAUGE_TRACK_IDS.Persistence, label: 'Page Cache',          accentColor: GAUGE_PALETTE[4], expandedHeight: 60, defaultState: 'expanded' },
  { id: GAUGE_TRACK_IDS.Transient,   label: 'Transient Store',     accentColor: GAUGE_PALETTE[6], expandedHeight: 24, defaultState: 'summary'  },
  { id: GAUGE_TRACK_IDS.Wal,         label: 'WAL',                 accentColor: GAUGE_PALETTE[7], expandedHeight: 80, defaultState: 'summary'  },
  { id: GAUGE_TRACK_IDS.TxUow,       label: 'Transactions + UoW',  accentColor: GAUGE_PALETTE[0], expandedHeight: 80, defaultState: 'summary'  },
];

/** O(1) dispatch check — used by the time-area render loop to identify gauge tracks. */
export const GAUGE_TRACK_ID_SET: ReadonlySet<string> = new Set(GAUGE_GROUPS.map((g) => g.id));

/** Small O(N=6) lookup by ID — used by the per-group renderer dispatcher. */
export function getGaugeGroupSpec(trackId: string): GaugeGroupSpec | undefined {
  for (const group of GAUGE_GROUPS) {
    if (group.id === trackId) return group;
  }
  return undefined;
}

/**
 * Append gauge-group tracks to `layout` starting at Y coord `startY`. Returns the Y coord past
 * the last gauge track — caller continues layout from there.
 *
 * Body height by state:
 *   - `summary`  → `summaryStripHeight` (spark-line INSIDE the label row's Y band)
 *   - `expanded` → `group.expandedHeight`
 *   - `double`   → `2 × group.expandedHeight`
 *
 * Y advance by state:
 *   - `summary`  → `labelRowHeight + trackGap` (summary sits inside the label row band)
 *   - `expanded` / `double` → `bodyHeight + trackGap`
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
  if (!regionVisible) return startY;

  let y = startY;
  for (const group of GAUGE_GROUPS) {
    const state = collapseState[group.id] ?? group.defaultState;
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
    y += state === 'summary' ? (labelRowHeight + trackGap) : (bodyHeight + trackGap);
  }
  return y;
}

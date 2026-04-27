import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { TimeRange, TrackState } from '@/libs/profiler/model/uiTypes';

/**
 * View-state slice for the Profiler panel — viewport + toggle states + per-gauge-group collapse states.
 *
 * **Partial persistence:** toggles (gauge region visible, legends visible, per-system lanes visible) and gauge
 * collapse states persist across Workbench reopens — they're user preferences. `viewRange` does NOT persist:
 * each new session resets it to `{globalStartUs, globalEndUs}` from the metadata DTO, because a prior
 * session's viewport is meaningless on a different trace.
 */
interface ProfilerViewState {
  /** Current viewport (absolute µs timestamps). Session-scoped — not persisted. */
  viewRange: TimeRange;
  /**
   * Width of the auto-follow window in µs (live mode). When `liveFollowActive` is true and a new
   * tick lands, `ProfilerPanel` sets `viewRange = [latest.endUs - liveFollowWindowUs, latest.endUs]`.
   * Persisted as a UX preference — different traces / engines expose different timescales and the
   * user may want a wider/narrower follow window per workload.
   */
  liveFollowWindowUs: number;
  /** Toggled by the `g` key. Hides the full gauge region. */
  gaugeRegionVisible: boolean;
  /** Toggled by the `l` key. Hides inline legends across all sections. */
  legendsVisible: boolean;
  /** Per-system chunk-lanes section visibility. */
  perSystemLanesVisible: boolean;
  /**
   * Collapse state per gauge group, keyed by group id (e.g. "gauge-gc", "gauge-memory"). Absent key = default.
   *
   * Gauges are the ONLY tracks that support the 3-state cycle (`summary | expanded | double`). Other track
   * kinds persist separately as plain booleans. v2 of this store stored collapse as boolean
   * (`true → summary`, `false → expanded`); the persist migration in the store body coerces on first load.
   */
  gaugeCollapse: Record<string, TrackState>;

  setViewRange: (r: TimeRange) => void;
  setLiveFollowWindowUs: (us: number) => void;
  toggleGaugeRegion: () => void;
  toggleLegends: () => void;
  togglePerSystemLanes: () => void;
  setGaugeCollapse: (groupId: string, state: TrackState) => void;
}

// Safe localStorage wrapper — falls back silently in non-browser environments (tests, SSR).
const safeStorage = createJSONStorage(() => ({
  getItem: (name: string) => {
    try { return localStorage.getItem(name); } catch { return null; }
  },
  setItem: (name: string, value: string) => {
    try { localStorage.setItem(name, value); } catch { /* noop */ }
  },
  removeItem: (name: string) => {
    try { localStorage.removeItem(name); } catch { /* noop */ }
  },
}));

const INITIAL_VIEW_RANGE: TimeRange = { startUs: 0, endUs: 1_000_000 };
const DEFAULT_LIVE_FOLLOW_WINDOW_US = 1_000_000; // 1 s of history visible while live-following.

export const useProfilerViewStore = create<ProfilerViewState>()(
  persist(
    (set) => ({
      viewRange: INITIAL_VIEW_RANGE,
      liveFollowWindowUs: DEFAULT_LIVE_FOLLOW_WINDOW_US,
      gaugeRegionVisible: true,
      legendsVisible: true,
      perSystemLanesVisible: true,
      gaugeCollapse: {},

      setViewRange: (r) => set({ viewRange: r }),
      setLiveFollowWindowUs: (us) => set({ liveFollowWindowUs: Math.max(1, us) }),
      toggleGaugeRegion: () => set((s) => ({ gaugeRegionVisible: !s.gaugeRegionVisible })),
      toggleLegends: () => set((s) => ({ legendsVisible: !s.legendsVisible })),
      togglePerSystemLanes: () => set((s) => ({ perSystemLanesVisible: !s.perSystemLanesVisible })),
      setGaugeCollapse: (groupId, state) =>
        set((s) => ({ gaugeCollapse: { ...s.gaugeCollapse, [groupId]: state } })),
    }),
    {
      name: 'workbench-profiler-view',
      storage: safeStorage,
      version: 1,
      // Only persist UX preferences; viewRange is session-scoped and reset on each open.
      partialize: (s) => ({
        liveFollowWindowUs: s.liveFollowWindowUs,
        gaugeRegionVisible: s.gaugeRegionVisible,
        legendsVisible: s.legendsVisible,
        perSystemLanesVisible: s.perSystemLanesVisible,
        gaugeCollapse: s.gaugeCollapse,
      }),
      // v0 → v1: gaugeCollapse changed from `Record<string, boolean>` to `Record<string, TrackState>`.
      // Old boolean true meant "collapsed"; map it to 'summary' which is the gauges-region equivalent
      // of "collapsed to a spark-line in the label row". false → 'expanded'.
      migrate: (persisted: unknown, fromVersion: number): Partial<ProfilerViewState> | undefined => {
        if (!persisted || typeof persisted !== 'object') return undefined;
        const p = persisted as Partial<ProfilerViewState> & { gaugeCollapse?: Record<string, unknown> };
        if (fromVersion < 1 && p.gaugeCollapse) {
          const migrated: Record<string, TrackState> = {};
          for (const [id, v] of Object.entries(p.gaugeCollapse)) {
            if (typeof v === 'boolean') {
              migrated[id] = v ? 'summary' : 'expanded';
            } else if (v === 'summary' || v === 'expanded' || v === 'double') {
              migrated[id] = v;
            }
            // Any other shape → drop the entry; falls back to default on first read.
          }
          p.gaugeCollapse = migrated;
        }
        return p;
      },
    },
  ),
);

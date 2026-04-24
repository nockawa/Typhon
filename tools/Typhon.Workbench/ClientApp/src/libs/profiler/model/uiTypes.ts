/** Viewport state shared across all tracks in the GraphArea */
export interface Viewport {
  offsetX: number;   // µs offset (left edge of visible area, absolute)
  scaleX: number;    // pixels per µs
  scrollY: number;   // vertical scroll in pixels
}

/** Time range displayed in the graph area (absolute µs timestamps) */
export interface TimeRange {
  startUs: number;
  endUs: number;
}

/**
 * Three-state expand enum for collapsible tracks.
 *   - <c>summary</c>  : minimal strip below the label row. Gauge tracks render a dim grey spark-line of their primary series;
 *                       non-gauge tracks render the existing thin dark strip (unchanged pre-upgrade behavior).
 *   - <c>expanded</c> : default height — what the track showed before this upgrade.
 *   - <c>double</c>   : 2× the expanded height. GAUGE TRACKS ONLY — lets the user see fine-grained variation in signals that
 *                       would otherwise be compressed. Click-cycles on non-gauge tracks skip this state.
 */
export type TrackState = 'summary' | 'expanded' | 'double';

/** Track layout descriptor — precomputed for rendering */
export interface TrackLayout {
  id: string;           // "ruler", "slot-0", "phases"
  label: string;        // "Slot 0", "Phases"
  y: number;            // top Y position in content coordinates
  height: number;       // total height in pixels at the current state (summary / expanded / double)
  state: TrackState;    // three-state collapse/expand — see <see cref="TrackState"/>
  collapsible: boolean; // ruler is not collapsible
  /**
   * For slot-N tracks only: height of the chunk bar row at the top of the lane. Spans for this slot are drawn starting at
   * <c>y + chunkRowHeight</c> and extend downward by (span.depth × SPAN_ROW_HEIGHT). Undefined for non-slot tracks.
   */
  chunkRowHeight?: number;
}

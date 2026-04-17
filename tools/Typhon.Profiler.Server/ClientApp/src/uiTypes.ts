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

/** Track layout descriptor — precomputed for rendering */
export interface TrackLayout {
  id: string;           // "ruler", "slot-0", "phases"
  label: string;        // "Slot 0", "Phases"
  y: number;            // top Y position in content coordinates
  height: number;       // total height in pixels (expanded)
  collapsedHeight: number; // height when collapsed (4px strip)
  collapsed: boolean;
  collapsible: boolean; // ruler is not collapsible
  /**
   * For slot-N tracks only: height of the chunk bar row at the top of the lane. Spans for this slot are drawn starting at
   * <c>y + chunkRowHeight</c> and extend downward by (span.depth × SPAN_ROW_HEIGHT). Undefined for non-slot tracks.
   */
  chunkRowHeight?: number;
}

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
  id: string;           // "ruler", "worker-0", "phases"
  label: string;        // "Worker 0", "Phases"
  y: number;            // top Y position in content coordinates
  height: number;       // height in pixels (expanded)
  collapsedHeight: number; // height when collapsed (4px strip)
  collapsed: boolean;
  collapsible: boolean; // ruler is not collapsible
}

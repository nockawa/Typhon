import { create } from 'zustand';
import type { ChunkSpan, MarkerSelection, SpanData } from '@/libs/profiler/model/traceModel';

/**
 * Discriminated union of profiler-panel selections. The profiler can select four kinds of things, each
 * mapped to its own kind tag so DetailPanel's render branches know exactly what they're looking at:
 *
 *  - **span**: a nested span inside a scheduler chunk (Transaction.Commit, BTree.Insert, etc.)
 *  - **chunk**: a top-level scheduler chunk (a system's execution slot inside a tick)
 *  - **tick**: a whole tick on the overview strip
 *  - **marker**: a discrete event instant (GC, phase transition, memory alloc event)
 */
export type ProfilerSelection =
  | { kind: 'span'; span: SpanData }
  | { kind: 'chunk'; chunk: ChunkSpan }
  | { kind: 'tick'; tickNumber: number }
  | { kind: 'marker'; marker: MarkerSelection };

interface ProfilerSelectionState {
  selected: ProfilerSelection | null;
  /**
   * `Date.now()` at which `selected` last changed. Consumed by DetailPanel's recency arbitration so it can
   * pick whichever selection (field / resource / profiler) was touched most recently. Never decreases.
   */
  touchedAt: number;
  setSelected: (selection: ProfilerSelection) => void;
  clear: () => void;
}

export const useProfilerSelectionStore = create<ProfilerSelectionState>()((set) => ({
  selected: null,
  touchedAt: 0,
  setSelected: (selection) => set({ selected: selection, touchedAt: Date.now() }),
  clear: () => set({ selected: null, touchedAt: Date.now() }),
}));

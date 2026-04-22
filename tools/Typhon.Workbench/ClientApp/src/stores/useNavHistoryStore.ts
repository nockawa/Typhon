import { create } from 'zustand';
import { useSelectedResourceStore, type SelectedResource } from './useSelectedResourceStore';

export type NavEntry =
  | { kind: 'resource-selected'; resourceId: string; selected: SelectedResource; timestamp: number }
  | { kind: 'panel-opened'; panelId: string; timestamp: number };

const CAPACITY = 100;

interface NavHistoryState {
  entries: NavEntry[];
  pointer: number;
  canBack: boolean;
  canForward: boolean;
  isRestoring: boolean;
  push: (entry: NavEntry) => void;
  back: () => void;
  forward: () => void;
  clear: () => void;
}

function deriveFlags(entries: NavEntry[], pointer: number) {
  return {
    canBack: pointer > 0,
    canForward: pointer >= 0 && pointer < entries.length - 1,
  };
}

function restoreSideEffect(entry: NavEntry) {
  if (entry.kind === 'resource-selected') {
    useSelectedResourceStore.getState().setSelected(entry.selected);
  }
  // Other kinds: no-op in Phase 5 (forward-compat).
}

export const useNavHistoryStore = create<NavHistoryState>()((set, get) => ({
  entries: [],
  pointer: -1,
  canBack: false,
  canForward: false,
  isRestoring: false,

  push: (entry) =>
    set((s) => {
      // During a restore (back/forward dispatch), downstream setSelected firing push() is a no-op.
      if (s.isRestoring) return s;
      const kept = s.entries.slice(0, s.pointer + 1);
      const next = [...kept, entry].slice(-CAPACITY);
      const pointer = next.length - 1;
      return { entries: next, pointer, ...deriveFlags(next, pointer) };
    }),

  back: () => {
    const s = get();
    if (!s.canBack) return;
    const pointer = s.pointer - 1;
    set({ isRestoring: true, pointer, ...deriveFlags(s.entries, pointer) });
    restoreSideEffect(s.entries[pointer]);
    set({ isRestoring: false });
  },

  forward: () => {
    const s = get();
    if (!s.canForward) return;
    const pointer = s.pointer + 1;
    set({ isRestoring: true, pointer, ...deriveFlags(s.entries, pointer) });
    restoreSideEffect(s.entries[pointer]);
    set({ isRestoring: false });
  },

  clear: () =>
    set({
      entries: [],
      pointer: -1,
      canBack: false,
      canForward: false,
      isRestoring: false,
    }),
}));

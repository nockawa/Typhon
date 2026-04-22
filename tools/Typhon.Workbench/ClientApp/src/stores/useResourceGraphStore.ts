import { create } from 'zustand';

interface ResourceGraphState {
  filter: string;
  selectedId: string | null;
  setFilter: (filter: string) => void;
  setSelected: (id: string | null) => void;
}

export const useResourceGraphStore = create<ResourceGraphState>()((set) => ({
  filter: '',
  selectedId: null,
  setFilter: (filter) => set({ filter }),
  setSelected: (id) => set({ selectedId: id }),
}));

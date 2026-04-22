import { create } from 'zustand';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';

export interface SelectedResource {
  resourceId: string;
  kind: string;
  name: string;
  path: string[];
  raw: ResourceNodeDto;
}

interface SelectedResourceState {
  selected: SelectedResource | null;
  setSelected: (s: SelectedResource | null) => void;
  clear: () => void;
}

export const useSelectedResourceStore = create<SelectedResourceState>()((set) => ({
  selected: null,
  setSelected: (s) => set({ selected: s }),
  clear: () => set({ selected: null }),
}));

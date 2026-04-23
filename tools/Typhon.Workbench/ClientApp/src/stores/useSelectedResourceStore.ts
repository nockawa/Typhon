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
  /** <c>Date.now()</c> at which <c>selected</c> was last updated. Consumed by DetailPanel to pick
   *  whichever selection — resource vs. schema field — is most recent. */
  touchedAt: number;
  setSelected: (s: SelectedResource | null) => void;
  clear: () => void;
}

export const useSelectedResourceStore = create<SelectedResourceState>()((set) => ({
  selected: null,
  touchedAt: 0,
  setSelected: (s) => set({ selected: s, touchedAt: Date.now() }),
  clear: () => set({ selected: null, touchedAt: Date.now() }),
}));

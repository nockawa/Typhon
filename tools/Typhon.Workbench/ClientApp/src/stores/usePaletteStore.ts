import { create } from 'zustand';

interface PaletteState {
  open: boolean;
  toggle: () => void;
  setOpen: (open: boolean) => void;
}

export const usePaletteStore = create<PaletteState>()((set) => ({
  open: false,
  toggle: () => set((s) => ({ open: !s.open })),
  setOpen: (open) => set({ open }),
}));

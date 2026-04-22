import { create } from 'zustand';

interface TreeVisibilityState {
  visible: boolean;
  setVisible: (v: boolean) => void;
  toggle: () => void;
}

export const useTreeVisibilityStore = create<TreeVisibilityState>()((set) => ({
  visible: true,
  setVisible: (v) => set({ visible: v }),
  toggle: () => set((s) => ({ visible: !s.visible })),
}));

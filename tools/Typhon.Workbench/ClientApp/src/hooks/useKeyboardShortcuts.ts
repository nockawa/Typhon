import { useEffect } from 'react';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { usePaletteStore } from '@/stores/usePaletteStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useTreeVisibilityStore } from '@/stores/useTreeVisibilityStore';
import { useShiftShift } from './useShiftShift';

export function useKeyboardShortcuts(): void {
  const back = useNavHistoryStore((s) => s.back);
  const forward = useNavHistoryStore((s) => s.forward);
  const toggleTheme = useThemeStore((s) => s.toggle);
  const togglePalette = usePaletteStore((s) => s.toggle);
  const toggleTree = useTreeVisibilityStore((s) => s.toggle);

  useShiftShift(togglePalette);

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'k' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        e.preventDefault();
        togglePalette();
        return;
      }
      if (e.key === 'ArrowLeft' && e.altKey) {
        e.preventDefault();
        back();
        return;
      }
      if (e.key === 'ArrowRight' && e.altKey) {
        e.preventDefault();
        forward();
        return;
      }
      if (e.key === 'T' && e.ctrlKey && e.shiftKey) {
        e.preventDefault();
        toggleTheme();
        return;
      }
      if (e.key === '/' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        e.preventDefault();
        toggleTree();
        return;
      }
    }

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [togglePalette, back, forward, toggleTheme, toggleTree]);
}

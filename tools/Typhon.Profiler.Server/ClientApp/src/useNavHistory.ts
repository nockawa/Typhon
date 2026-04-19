import { useRef, useCallback, useState } from 'preact/hooks';
import type { TimeRange } from './uiTypes';

const MAX_HISTORY = 100;

export interface NavHistory {
  /** Push a settled view range onto the stack. Call after the user stops interacting (debounced). */
  push: (range: TimeRange) => void;
  /** Navigate back. Returns the target range to animate to, or null if at the beginning. */
  undo: () => TimeRange | null;
  /** Navigate forward. Returns the target range to animate to, or null if at the end. */
  redo: () => TimeRange | null;
  canUndo: boolean;
  canRedo: boolean;
  /** True while an undo/redo animation is in flight — suppresses debounced push so replay doesn't fork the stack. */
  isNavigating: boolean;
  setNavigating: (v: boolean) => void;
}

export function useNavHistory(): NavHistory {
  const stackRef = useRef<TimeRange[]>([]);
  const indexRef = useRef(-1);
  const [version, setVersion] = useState(0);
  const navigatingRef = useRef(false);

  const push = useCallback((range: TimeRange) => {
    // Suppress during undo/redo replay so the animated frames don't fork the stack.
    if (navigatingRef.current) return;
    const stack = stackRef.current;
    const idx = indexRef.current;
    if (idx >= 0) {
      const cur = stack[idx];
      if (Math.abs(cur.startUs - range.startUs) < 0.01 && Math.abs(cur.endUs - range.endUs) < 0.01) {
        return;
      }
    }
    stack.length = idx + 1;
    stack.push({ startUs: range.startUs, endUs: range.endUs });
    if (stack.length > MAX_HISTORY) {
      stack.shift();
    } else {
      indexRef.current = stack.length - 1;
    }
    setVersion(v => v + 1);
  }, []);

  const undo = useCallback((): TimeRange | null => {
    if (indexRef.current <= 0) return null;
    indexRef.current--;
    setVersion(v => v + 1);
    const entry = stackRef.current[indexRef.current];
    return { startUs: entry.startUs, endUs: entry.endUs };
  }, []);

  const redo = useCallback((): TimeRange | null => {
    const stack = stackRef.current;
    if (indexRef.current >= stack.length - 1) return null;
    indexRef.current++;
    setVersion(v => v + 1);
    const entry = stack[indexRef.current];
    return { startUs: entry.startUs, endUs: entry.endUs };
  }, []);

  const setNavigating = useCallback((v: boolean) => { navigatingRef.current = v; }, []);

  return {
    push,
    undo,
    redo,
    canUndo: indexRef.current > 0,
    canRedo: indexRef.current < stackRef.current.length - 1,
    isNavigating: navigatingRef.current,
    setNavigating,
  };
}

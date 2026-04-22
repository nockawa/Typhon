import { useEffect } from 'react';

const DOUBLE_SHIFT_WINDOW_MS = 300;

const INPUT_TAGS = new Set(['INPUT', 'TEXTAREA', 'SELECT']);

function isEditableTarget(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  if (INPUT_TAGS.has(el.tagName)) return true;
  return el.getAttribute('contenteditable') != null;
}

/** Pure handler factory — exported for unit testing. */
export function createShiftShiftHandler(
  callback: () => void,
  getEditableTarget: () => boolean = isEditableTarget,
  now: () => number = () => performance.now(),
): (e: KeyboardEvent) => void {
  let lastShiftAt = -Infinity;
  return function onKeyDown(e: KeyboardEvent) {
    if (e.key !== 'Shift') return;
    if (getEditableTarget()) return;
    const t = now();
    if (t - lastShiftAt <= DOUBLE_SHIFT_WINDOW_MS) {
      lastShiftAt = -Infinity;  // reset so a third Shift doesn't immediately re-fire
      callback();
    } else {
      lastShiftAt = t;
    }
  };
}

export function useShiftShift(callback: () => void): void {
  useEffect(() => {
    const handler = createShiftShiftHandler(callback);
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [callback]);
}

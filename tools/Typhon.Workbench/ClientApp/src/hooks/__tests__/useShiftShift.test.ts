// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest';
import { createShiftShiftHandler } from '../useShiftShift';

function shiftEvent(): KeyboardEvent {
  return new KeyboardEvent('keydown', { key: 'Shift', bubbles: true });
}

function otherEvent(): KeyboardEvent {
  return new KeyboardEvent('keydown', { key: 'a', bubbles: true });
}

describe('createShiftShiftHandler', () => {
  it('calls callback on double-Shift within 300ms', () => {
    const cb = vi.fn();
    let t = 0;
    const h = createShiftShiftHandler(cb, () => false, () => t);
    h(shiftEvent()); t = 100; h(shiftEvent());
    expect(cb).toHaveBeenCalledOnce();
  });

  it('does not call callback when gap exceeds 300ms', () => {
    const cb = vi.fn();
    let t = 0;
    const h = createShiftShiftHandler(cb, () => false, () => t);
    h(shiftEvent()); t = 400; h(shiftEvent());
    expect(cb).not.toHaveBeenCalled();
  });

  it('does not trigger on non-Shift key', () => {
    const cb = vi.fn();
    let t = 0;
    const h = createShiftShiftHandler(cb, () => false, () => t);
    h(otherEvent()); t = 50; h(otherEvent());
    expect(cb).not.toHaveBeenCalled();
  });

  it('does not trigger when editable target is active', () => {
    const cb = vi.fn();
    let t = 0;
    const h = createShiftShiftHandler(cb, () => true, () => t);
    h(shiftEvent()); t = 50; h(shiftEvent());
    expect(cb).not.toHaveBeenCalled();
  });

  it('resets timer after a successful double-Shift (no immediate third trigger)', () => {
    const cb = vi.fn();
    let t = 0;
    const h = createShiftShiftHandler(cb, () => false, () => t);
    h(shiftEvent()); t = 50; h(shiftEvent()); // first double → fires
    t = 100; h(shiftEvent()); t = 150; h(shiftEvent()); // second double → fires again
    expect(cb).toHaveBeenCalledTimes(2);
  });
});

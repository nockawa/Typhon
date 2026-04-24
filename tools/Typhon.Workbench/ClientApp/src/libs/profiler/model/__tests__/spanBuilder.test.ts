import { describe, expect, it } from 'vitest';
import { buildEmptyTick, buildSpan, buildTick } from './spanBuilder';

/**
 * Smoke tests for the shared span/tick fixture builder. If any of these fails, downstream tests
 * (`timeAreaLayout.test.ts`, `traceModel.test.ts`, etc.) will see corrupted inputs and their
 * failures will point at the wrong layer — so this is a small but important signal.
 */

describe('spanBuilder.buildSpan', () => {
  it('computes durationUs from start/end', () => {
    const s = buildSpan({ startUs: 100, endUs: 250 });
    expect(s.durationUs).toBe(150);
  });

  it('carries optional fields only when set', () => {
    const s = buildSpan({ startUs: 0, endUs: 10 });
    expect(s.depth).toBeUndefined();
    expect(s.spanId).toBeUndefined();
    expect(s.parentSpanId).toBeUndefined();

    const s2 = buildSpan({ startUs: 0, endUs: 10, depth: 2, spanId: 'a', parentSpanId: 'b' });
    expect(s2.depth).toBe(2);
    expect(s2.spanId).toBe('a');
    expect(s2.parentSpanId).toBe('b');
  });
});

describe('spanBuilder.buildTick', () => {
  it('groups spans by threadSlot', () => {
    const a = buildSpan({ startUs: 0, endUs: 50, threadSlot: 0, name: 'a' });
    const b = buildSpan({ startUs: 0, endUs: 50, threadSlot: 1, name: 'b' });
    const tick = buildTick({ tickNumber: 0, startUs: 0, durationUs: 100, spans: [a, b] });
    expect(tick.spansByThreadSlot.get(0)).toEqual([a]);
    expect(tick.spansByThreadSlot.get(1)).toEqual([b]);
  });

  it('computes per-slot running-max endUs', () => {
    const a = buildSpan({ startUs: 0, endUs: 100, threadSlot: 0 });
    const b = buildSpan({ startUs: 10, endUs: 50, threadSlot: 0 });
    const c = buildSpan({ startUs: 20, endUs: 200, threadSlot: 0 });
    const tick = buildTick({ tickNumber: 0, startUs: 0, durationUs: 300, spans: [a, b, c] });
    const em = tick.spanEndMaxByThreadSlot.get(0)!;
    // After sort by startUs: [a, b, c]. Running max: 100, 100, 200.
    expect(Array.from(em)).toEqual([100, 100, 200]);
  });

  it('records max span depth per thread slot', () => {
    const a = buildSpan({ startUs: 0, endUs: 100, threadSlot: 0, depth: 0 });
    const b = buildSpan({ startUs: 0, endUs: 100, threadSlot: 0, depth: 2 });
    const c = buildSpan({ startUs: 0, endUs: 100, threadSlot: 0, depth: 1 });
    const tick = buildTick({ tickNumber: 0, startUs: 0, durationUs: 100, spans: [a, b, c] });
    expect(tick.spanMaxDepthByThreadSlot.get(0)).toBe(2);
  });
});

describe('spanBuilder.buildEmptyTick', () => {
  it('returns a tick with no spans and empty per-slot maps', () => {
    const tick = buildEmptyTick(5, 100, 50);
    expect(tick.tickNumber).toBe(5);
    expect(tick.startUs).toBe(100);
    expect(tick.endUs).toBe(150);
    expect(tick.durationUs).toBe(50);
    expect(tick.spans).toEqual([]);
    expect(tick.spansByThreadSlot.size).toBe(0);
  });
});

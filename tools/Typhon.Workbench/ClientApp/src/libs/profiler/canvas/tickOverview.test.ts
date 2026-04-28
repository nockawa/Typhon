import { describe, expect, it } from 'vitest';
import { buildTickRows, computeSelectionIdxRange, type TickSummaryLike } from './tickOverview';

/**
 * Regression tests for the float-drift boundary clamp inside {@link buildTickRows}.
 *
 * The bug: `TickSummary.DurationUs` is a 32-bit float on the wire while `StartUs` is a 64-bit double, so
 * the JS-computed `start + duration` can land slightly past the next tick's wire `startUs`. Without the
 * clamp, strict-less-than overlap tests in {@link computeSelectionIdxRange} flip and a single-tick
 * selection silently bleeds into the next tick.
 */

describe('buildTickRows boundary clamp', () => {
  it('clamps endUs to next tick startUs when float drift overshoots', () => {
    // Synthesize the bug: tick0 endUs (computed as start + duration) lands ~1 ULP past tick1 startUs.
    const tick1Start = 1_000_000.5;
    const summaries: TickSummaryLike[] = [
      { tickNumber: 0, startUs: 999_990, durationUs: 10.500001, eventCount: 1 }, // computed end = 1_000_000.500001
      { tickNumber: 1, startUs: tick1Start, durationUs: 10, eventCount: 1 },
    ];

    const rows = buildTickRows(summaries);
    expect(rows).toHaveLength(2);
    expect(rows[0].endUs).toBe(tick1Start);
    // The clamp is on endUs only — durationUs is preserved verbatim for bar rendering.
    expect(rows[0].durationUs).toBeCloseTo(10.500001, 6);
  });

  it('preserves real gaps between ticks (next.startUs greater than start + duration)', () => {
    // Engine was idle between tick 0 and tick 1 — there's a true gap.
    const summaries: TickSummaryLike[] = [
      { tickNumber: 0, startUs: 100, durationUs: 10, eventCount: 1 }, // ends at 110
      { tickNumber: 1, startUs: 200, durationUs: 10, eventCount: 1 }, // 90us gap
    ];

    const rows = buildTickRows(summaries);
    expect(rows[0].endUs).toBe(110); // not clamped, real duration honored
    expect(rows[1].startUs).toBe(200);
  });

  it('does not clamp the last tick — endUs = start + duration', () => {
    const summaries: TickSummaryLike[] = [
      { tickNumber: 0, startUs: 0, durationUs: 5, eventCount: 1 },
      { tickNumber: 1, startUs: 5, durationUs: 7, eventCount: 1 },
    ];
    const rows = buildTickRows(summaries);
    expect(rows[1].endUs).toBe(12);
  });

  it('returns an empty array for null / empty input', () => {
    expect(buildTickRows(null)).toEqual([]);
    expect(buildTickRows(undefined)).toEqual([]);
    expect(buildTickRows([])).toEqual([]);
  });

  it('coerces string fields (orval emits number | string for int64-shape values)', () => {
    const summaries: TickSummaryLike[] = [
      { tickNumber: '0', startUs: '100', durationUs: '10', eventCount: '5' },
    ];
    const rows = buildTickRows(summaries);
    expect(rows[0].tickNumber).toBe(0);
    expect(rows[0].startUs).toBe(100);
    expect(rows[0].endUs).toBe(110);
    expect(rows[0].eventCount).toBe(5);
  });
});

describe('clamp + computeSelectionIdxRange — single-tick selection round-trip', () => {
  it('selecting tick N produces first=last=N (no spillover) even when float drift would overshoot', () => {
    // Reproduce the original symptom: clicking tick 1 should select ONLY tick 1, not 1+2.
    const tick2Start = 1_000_000;
    const summaries: TickSummaryLike[] = [
      { tickNumber: 0, startUs: 0,         durationUs: 10, eventCount: 1 },
      { tickNumber: 1, startUs: 10,        durationUs: 999_990.0001, eventCount: 1 }, // drift overshoots tick 2 start
      { tickNumber: 2, startUs: tick2Start, durationUs: 10, eventCount: 1 },
      { tickNumber: 3, startUs: tick2Start + 10, durationUs: 10, eventCount: 1 },
    ];
    const rows = buildTickRows(summaries);

    // Single-click on tick 1 sets viewRange to {tick1.startUs, tick1.endUs} — clamped end equals tick2.startUs.
    const tick1 = rows[1];
    const sel = computeSelectionIdxRange(rows, { startUs: tick1.startUs, endUs: tick1.endUs });
    expect(sel.first).toBe(1);
    expect(sel.last).toBe(1);
  });
});

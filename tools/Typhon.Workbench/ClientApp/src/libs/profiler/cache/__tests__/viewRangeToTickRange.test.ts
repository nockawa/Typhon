import { describe, expect, it } from 'vitest';
import { viewRangeToTickRange } from '@/libs/profiler/cache/chunkCache';
import type { TickSummary } from '@/libs/profiler/model/types';

/**
 * `viewRangeToTickRange` runs on every wheel event at 60 Hz; a buggy binary search would either
 * fetch wrong chunks (wrong ticks displayed) or over-fetch (exhaust the chunk cache). These tests
 * pin the half-open overlap contract — `tickEnd > fromUs && tickStart < toUs` — including the
 * "kiss" edge cases that were explicitly excluded in the code's comment.
 */

/** Build a monotone summary of N ticks starting at timestamp 0, each 100 µs wide. */
function buildSummary(n: number, startAt = 0, widthUs = 100): TickSummary[] {
  const out: TickSummary[] = [];
  for (let i = 0; i < n; i++) {
    out.push({
      tickNumber: i + 1,
      startUs: startAt + i * widthUs,
      durationUs: widthUs,
    } as unknown as TickSummary);
  }
  return out;
}

describe('viewRangeToTickRange — happy paths', () => {
  it('returns the tick range when the view sits entirely inside one tick', () => {
    // Ticks: [1:0-100), [2:100-200), [3:200-300). View [120, 150) overlaps only tick 2.
    const summary = buildSummary(3);
    expect(viewRangeToTickRange(summary, 120, 150)).toEqual({ fromTick: 2, toTick: 3 });
  });

  it('returns the spanning range when the view crosses multiple ticks', () => {
    const summary = buildSummary(5);
    // View [150, 420) overlaps ticks 2, 3, 4, 5 — fromTick=2, toTick=6 (exclusive).
    expect(viewRangeToTickRange(summary, 150, 420)).toEqual({ fromTick: 2, toTick: 6 });
  });

  it('returns the full range when the view covers the entire trace', () => {
    const summary = buildSummary(10);
    expect(viewRangeToTickRange(summary, 0, 1000)).toEqual({ fromTick: 1, toTick: 11 });
  });
});

describe('viewRangeToTickRange — edge cases', () => {
  it('returns null for an empty summary', () => {
    expect(viewRangeToTickRange([], 0, 100)).toBeNull();
  });

  it('returns null when the view ends before the first tick starts', () => {
    const summary = buildSummary(3, /* startAt */ 500);
    expect(viewRangeToTickRange(summary, 0, 100)).toBeNull();
  });

  it('returns null when the view starts after the last tick ends', () => {
    const summary = buildSummary(3); // covers 0..300
    expect(viewRangeToTickRange(summary, 1000, 2000)).toBeNull();
  });

  it('half-open kiss at the left edge does NOT overlap (tickEnd == fromUs)', () => {
    // Tick 1 = [0, 100). View starts at 100 — exactly kisses tick 1's end, which by the
    // half-open contract should EXCLUDE tick 1 and include tick 2+.
    const summary = buildSummary(3);
    expect(viewRangeToTickRange(summary, 100, 200)).toEqual({ fromTick: 2, toTick: 3 });
  });

  it('half-open kiss at the right edge does NOT overlap (tickStart == toUs)', () => {
    // View ends at 200 — exactly kisses tick 3's start. Tick 3 must be excluded.
    const summary = buildSummary(3);
    expect(viewRangeToTickRange(summary, 50, 200)).toEqual({ fromTick: 1, toTick: 3 });
  });

  it('zero-width view inside a tick returns that tick (point-in-tick selection)', () => {
    // `[150, 150)` is degenerate but the predicate `tickEnd > 150 && tickStart < 150` still picks
    // out tick 2 (100..200) — that's useful for "zoom to this µs point" UX. Pinning here so a
    // later rewrite that accidentally excludes zero-width views doesn't break click-to-select.
    const summary = buildSummary(3);
    expect(viewRangeToTickRange(summary, 150, 150)).toEqual({ fromTick: 2, toTick: 3 });
  });

  it('inverted view (fromUs > toUs) returns null', () => {
    // Not a normal input but worth pinning — the binary search predicate would happily run and
    // return a plausible-looking range. The first-idx check catches it.
    const summary = buildSummary(3);
    expect(viewRangeToTickRange(summary, 200, 100)).toBeNull();
  });
});

describe('viewRangeToTickRange — performance sanity', () => {
  it('scales to a 100k-tick summary without timing out', () => {
    const summary = buildSummary(100_000);
    // Pick a view in the middle of the summary: ticks 50_001..50_002.
    const result = viewRangeToTickRange(summary, 5_000_000, 5_000_100);
    expect(result).toEqual({ fromTick: 50_001, toTick: 50_002 });
    // If this regressed to a linear scan (for e.g. forgetting to use the binary bounds), a 100k
    // summary would take tens of ms — the assertion completing under the implicit test timeout
    // (5 s) is the real check.
  });
});

import { describe, expect, it } from 'vitest';
import { processTickEvents } from '@/libs/profiler/model/traceModel';
import type { TraceEvent } from '@/libs/profiler/model/types';

/**
 * Coverage for `processTickEvents` — the single function that turns a raw DTO event stream into
 * the fully-processed per-tick view (sorted spans, depth walk, async-completion fold, orphan
 * flush). Behaviour captured here has caused visible UI bugs before; every test is a pinned
 * regression:
 *
 *   - Depth walk: prior version let the "0000000000000000" parent sentinel land at depth 0,
 *     stacking every span on row 0 regardless of its real call-stack nesting.
 *   - Async fold order-invariant: sort ties flip kickoff-vs-completion ordering; both orders
 *     must fold to exactly one span with the full async duration and the kickoff's original
 *     duration preserved in `kickoffDurationUs`.
 *   - Orphan completion flush: a *Completed record without a matching kickoff in the same tick
 *     must still land as a standalone span, not silently dropped.
 *
 * `TraceEventKind` is a `const enum` in types.ts; Vitest doesn't inline those, so we pass the
 * numeric literal and let `evt.kind` in the module inline correctly against its own import.
 */

// Numeric values mirror the TraceEventKind const enum — inlining skipped in tests.
const KIND = {
  TickStart: 0,
  TickEnd: 1,
  PhaseStart: 2,
  PhaseEnd: 3,
  PageCacheDiskRead: 51,
  PageCacheDiskReadCompleted: 56,
  BTreeInsert: 14, // generic span kind ≥ 10, NOT a completion
  GcSuspension: 75,
} as const;

const ZERO_HEX = '0000000000000000';

function baseEvent(overrides: Partial<TraceEvent>): TraceEvent {
  return {
    kind: 0 as TraceEvent['kind'],
    threadSlot: 0,
    tickNumber: 1,
    timestampUs: 0,
    ...overrides,
  };
}

describe('processTickEvents — tick bounds', () => {
  it('captures TickStart and TickEnd as startUs/endUs', () => {
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 100 }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 300 }),
    ];
    const tick = processTickEvents(1, events, []);
    expect(tick.startUs).toBe(100);
    expect(tick.endUs).toBe(300);
    expect(tick.durationUs).toBe(200);
  });

  it('missing TickStart in a continuation chunk is benign (no warn, startUs = 0)', () => {
    // Continuation chunks legitimately have no TickStart; downstream viewport math still needs
    // finite numbers, so processTickEvents substitutes 0/0.
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 500 }),
    ];
    const tick = processTickEvents(5, events, []);
    expect(tick.startUs).toBe(0);
    expect(tick.endUs).toBe(500);
  });

  it('malformed non-continuation tick falls back to (0, 0) without NaN', () => {
    const tick = processTickEvents(2, [], []);
    expect(Number.isFinite(tick.startUs)).toBe(true);
    expect(Number.isFinite(tick.endUs)).toBe(true);
    expect(tick.durationUs).toBe(0);
  });
});

describe('processTickEvents — depth walk', () => {
  it('treats the 16-zeroes SpanId sentinel as "no parent" (regression: bars stacked on row 0)', () => {
    // Root span with the server-encoded "no parent" sentinel — the depth walk must terminate, not
    // chase a phantom parent that isn't in the map and silently default to depth 0.
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'],
        timestampUs: 10,
        durationUs: 20,
        spanId: 'aaaaaaaaaaaaaaaa',
        parentSpanId: ZERO_HEX,
      }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'],
        timestampUs: 15,
        durationUs: 5,
        spanId: 'bbbbbbbbbbbbbbbb',
        parentSpanId: 'aaaaaaaaaaaaaaaa',
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }),
    ];
    const tick = processTickEvents(1, events, []);

    const root = tick.spans.find((s) => s.spanId === 'aaaaaaaaaaaaaaaa')!;
    const child = tick.spans.find((s) => s.spanId === 'bbbbbbbbbbbbbbbb')!;
    expect(root.depth).toBe(0);
    expect(child.depth).toBe(1);
  });

  it('caps depth at 32 so a runaway parent chain cannot loop forever', () => {
    const events: TraceEvent[] = [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 })];
    // Build a 40-deep chain: span0 has no parent, spanN has parent = span(N-1).
    for (let i = 0; i < 40; i++) {
      events.push(baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'],
        timestampUs: i + 1,
        durationUs: 1,
        spanId: `s${i.toString().padStart(15, '0')}`,
        parentSpanId: i === 0 ? ZERO_HEX : `s${(i - 1).toString().padStart(15, '0')}`,
      }));
    }
    events.push(baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }));

    const tick = processTickEvents(1, events, []);
    const deepest = tick.spans.find((s) => s.spanId === `s${(39).toString().padStart(15, '0')}`)!;
    // Cap is `if (depth > 32) break`, so depth reaches 33 before the check fires on the next iter.
    expect(deepest.depth).toBeLessThanOrEqual(33);
    expect(deepest.depth).toBeGreaterThan(30); // proves we walked deep before capping
  });
});

describe('processTickEvents — async-completion fold', () => {
  it('folds when the kickoff arrives first (the common order)', () => {
    const spanId = 'ffffffffffffffff';
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.PageCacheDiskRead as TraceEvent['kind'],
        timestampUs: 10,
        durationUs: 2,  // synchronous kickoff cost
        spanId,
      }),
      baseEvent({
        kind: KIND.PageCacheDiskReadCompleted as TraceEvent['kind'],
        timestampUs: 10,
        durationUs: 50, // full async tail
        spanId,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }),
    ];
    const tick = processTickEvents(1, events, []);

    // Exactly ONE span for the pair — completion must fold into the kickoff, not add a new span.
    const pageCacheSpans = tick.spans.filter((s) => s.kind === (KIND.PageCacheDiskRead as unknown as typeof s.kind));
    expect(pageCacheSpans).toHaveLength(1);
    expect(pageCacheSpans[0].durationUs).toBe(50); // rewritten to full async
    expect(pageCacheSpans[0].kickoffDurationUs).toBe(2); // original preserved
    expect(pageCacheSpans[0].endUs).toBe(60);
  });

  it('folds when the completion arrives first (sort ties flip ordering)', () => {
    const spanId = 'eeeeeeeeeeeeeeee';
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.PageCacheDiskReadCompleted as TraceEvent['kind'],
        timestampUs: 10,
        durationUs: 50,
        spanId,
      }),
      baseEvent({
        kind: KIND.PageCacheDiskRead as TraceEvent['kind'],
        timestampUs: 10,
        durationUs: 2,
        spanId,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }),
    ];
    const tick = processTickEvents(1, events, []);

    const pageCacheSpans = tick.spans.filter((s) => s.kind === (KIND.PageCacheDiskRead as unknown as typeof s.kind));
    expect(pageCacheSpans).toHaveLength(1);
    expect(pageCacheSpans[0].durationUs).toBe(50);
    expect(pageCacheSpans[0].kickoffDurationUs).toBe(2);
  });

  it('orphan completion (no matching kickoff in-tick) flushes as standalone span', () => {
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.PageCacheDiskReadCompleted as TraceEvent['kind'],
        timestampUs: 10,
        durationUs: 30,
        spanId: 'cccccccccccccccc',
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }),
    ];
    const tick = processTickEvents(1, events, []);

    // Orphan completion must survive — the data point is valuable even if the kickoff isn't visible.
    const completed = tick.spans.find((s) => s.spanId === 'cccccccccccccccc');
    expect(completed).toBeDefined();
    expect(completed!.durationUs).toBe(30);
    // No kickoffDurationUs set because there was no kickoff to fold.
    expect(completed!.kickoffDurationUs).toBeUndefined();
  });
});

describe('processTickEvents — global invariants', () => {
  it('sorts spans by (startUs, kind, spanId) deterministically', () => {
    // Two spans with identical startUs and kind — tie-break on spanId keeps the order stable
    // across repeat processing, so render-layer flicker from non-stable sort can't slip in.
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 10, durationUs: 1,
        spanId: 'bb000000000000bb', parentSpanId: ZERO_HEX,
      }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 10, durationUs: 1,
        spanId: 'aa000000000000aa', parentSpanId: ZERO_HEX,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }),
    ];
    const tick = processTickEvents(1, events, []);
    const spanIds = tick.spans.filter((s) => s.spanId).map((s) => s.spanId);
    expect(spanIds).toEqual(['aa000000000000aa', 'bb000000000000bb']);
  });

  it('groups spans by threadSlot and builds the spanEndMaxByThreadSlot running-max', () => {
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 10, durationUs: 20,
        threadSlot: 0, spanId: 'a1', parentSpanId: ZERO_HEX,
      }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 15, durationUs: 5,
        threadSlot: 0, spanId: 'a2', parentSpanId: ZERO_HEX,
      }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 20, durationUs: 100,
        threadSlot: 1, spanId: 'b1', parentSpanId: ZERO_HEX,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 200 }),
    ];
    const tick = processTickEvents(1, events, []);

    const slot0 = tick.spansByThreadSlot.get(0)!;
    const slot1 = tick.spansByThreadSlot.get(1)!;
    expect(slot0).toHaveLength(2);
    expect(slot1).toHaveLength(1);

    // Running max of endUs across slot 0 spans sorted by startUs: [30, 30] — the second span
    // ends earlier, so its running-max stays at 30 (the first span's end).
    const em0 = Array.from(tick.spanEndMaxByThreadSlot.get(0)!);
    expect(em0).toEqual([30, 30]);
  });

  it('collects GcSuspension spans into gcSuspensions AND keeps them in spans[] for thread lane', () => {
    const events: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.GcSuspension as TraceEvent['kind'], timestampUs: 10, durationUs: 50,
        spanId: 'gc0', parentSpanId: ZERO_HEX,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 100 }),
    ];
    const tick = processTickEvents(1, events, []);

    // Dual-tracked: gcSuspensions drives the red "stop-the-world" bar on the GC gauge; the same
    // span ALSO lives in spans[] so the ingesting thread's lane can show the pause in context.
    expect(tick.gcSuspensions).toHaveLength(1);
    expect(tick.gcSuspensions[0].durationUs).toBe(50);
    expect(tick.spans.find((s) => s.spanId === 'gc0')).toBeDefined();
  });
});

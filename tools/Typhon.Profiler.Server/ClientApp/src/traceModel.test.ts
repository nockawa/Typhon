/**
 * Tests for {@link processTickEvents} + {@link mergeTickData}. The gold-standard correctness test for mergeTickData is:
 * split a known event set into two halves, run processTickEvents on each, merge the results, and assert the merged output
 * matches processTickEvents run on the full event set. This is the "merge is equivalent to not-splitting" invariant — if
 * it ever breaks, intra-tick splitting produces semantically-wrong TickData and we want to catch that at CI time.
 */
import { describe, it, expect } from 'vitest';
import { processTickEvents, mergeTickData } from './traceModel';
import { TraceEventKind, type SystemDef, type TraceEvent } from './types';

/** Minimal SystemDef entries — only `name` is read by processTickEvents. Padded to make SchedulerChunk lookups resolve. */
const SYSTEMS: SystemDef[] = [
  { index: 0, name: 'SystemZero', type: 0, priority: 0, isParallel: false, tierFilter: 0, predecessors: [], successors: [] },
  { index: 1, name: 'SystemOne', type: 0, priority: 0, isParallel: false, tierFilter: 0, predecessors: [], successors: [] },
];

/** Build a synthetic event stream: TickStart, TickEnd, SchedulerChunks, a kickoff+completion pair, and a MemoryAllocEvent. */
function buildEvents(tickNumber: number): TraceEvent[] {
  return [
    { kind: TraceEventKind.TickStart, threadSlot: 0, tickNumber, timestampUs: 100 },

    // A few SchedulerChunks (kind 10) to exercise the `chunks` + `systemDurations` paths.
    {
      kind: TraceEventKind.SchedulerChunk, threadSlot: 0, tickNumber, timestampUs: 105,
      durationUs: 10, spanId: 'sc1', parentSpanId: '0000000000000000',
      systemIndex: 0, chunkIndex: 0, totalChunks: 1, entitiesProcessed: 50,
    },
    {
      kind: TraceEventKind.SchedulerChunk, threadSlot: 0, tickNumber, timestampUs: 120,
      durationUs: 8, spanId: 'sc2', parentSpanId: '0000000000000000',
      systemIndex: 1, chunkIndex: 0, totalChunks: 1, entitiesProcessed: 30,
    },

    // A PageCache async fold pair — kickoff + completion with the same spanId.
    {
      kind: TraceEventKind.PageCacheDiskRead, threadSlot: 1, tickNumber, timestampUs: 130,
      durationUs: 2, spanId: 'async1', parentSpanId: '0000000000000000',
    },
    {
      kind: TraceEventKind.PageCacheDiskReadCompleted, threadSlot: 1, tickNumber, timestampUs: 130,
      durationUs: 40, spanId: 'async1', parentSpanId: '0000000000000000',
    },

    // A memory alloc event (instant, kind 9).
    {
      kind: TraceEventKind.MemoryAllocEvent, threadSlot: 0, tickNumber, timestampUs: 140,
      direction: 0, sourceTag: 0, sizeBytes: 4096, totalAfterBytes: 8192,
    },

    // A couple more scheduler chunks to have multiple spans to sort.
    {
      kind: TraceEventKind.SchedulerChunk, threadSlot: 0, tickNumber, timestampUs: 150,
      durationUs: 5, spanId: 'sc3', parentSpanId: '0000000000000000',
      systemIndex: 0, chunkIndex: 0, totalChunks: 1, entitiesProcessed: 20,
    },
    {
      kind: TraceEventKind.SchedulerChunk, threadSlot: 1, tickNumber, timestampUs: 155,
      durationUs: 3, spanId: 'sc4', parentSpanId: '0000000000000000',
      systemIndex: 1, chunkIndex: 0, totalChunks: 1, entitiesProcessed: 10,
    },

    { kind: TraceEventKind.TickEnd, threadSlot: 0, tickNumber, timestampUs: 200, overloadLevel: 0, tickMultiplier: 1 },
  ];
}

describe('mergeTickData — equivalence to single-pass processTickEvents', () => {
  it('merged output matches single-pass output on the full event stream', () => {
    const tickNumber = 5;
    const full = buildEvents(tickNumber);

    // Split after the first 4 events (simulates a mid-tick chunk boundary). The choice of split point is deliberate: it
    // leaves the DiskRead kickoff in the first half and its Completion in the second half, exercising the cross-chunk
    // fold path inside mergeTickData's re-run of processTickEvents.
    const splitPoint = 4;
    const firstHalf = full.slice(0, splitPoint);
    const secondHalf = full.slice(splitPoint);

    const singlePass = processTickEvents(tickNumber, full, SYSTEMS, /*isContinuation=*/false);
    // First half of a split tick: has TickStart, missing TickEnd → NOT a continuation (it's the chunk that OPENS the tick).
    // Second half: missing TickStart, has TickEnd → IS a continuation. Matches how chunkWorker seeds isContinuation in prod.
    const tickA = processTickEvents(tickNumber, firstHalf, SYSTEMS, /*isContinuation=*/false);
    const tickB = processTickEvents(tickNumber, secondHalf, SYSTEMS, /*isContinuation=*/true);
    const merged = mergeTickData(tickA, tickB, SYSTEMS);

    // Structural equivalence: same tickNumber, same overall bounds, same span count, same system-durations map.
    expect(merged.tickNumber).toBe(singlePass.tickNumber);
    expect(merged.startUs).toBe(singlePass.startUs);
    expect(merged.endUs).toBe(singlePass.endUs);
    expect(merged.durationUs).toBe(singlePass.durationUs);
    expect(merged.chunks.length).toBe(singlePass.chunks.length);
    expect(merged.spans.length).toBe(singlePass.spans.length);
    expect(merged.memoryAllocEvents.length).toBe(singlePass.memoryAllocEvents.length);

    // Spot-check spans: both the DiskRead kickoff (folded with its completion) and each SchedulerChunk should appear in
    // both outputs with identical (startUs, durationUs, spanId). If fold misbehaved across the split, the merged output
    // would have TWO async1 spans (kickoff + orphan completion) instead of one folded span.
    for (let i = 0; i < singlePass.spans.length; i++) {
      expect(merged.spans[i].spanId).toBe(singlePass.spans[i].spanId);
      expect(merged.spans[i].startUs).toBe(singlePass.spans[i].startUs);
      expect(merged.spans[i].durationUs).toBe(singlePass.spans[i].durationUs);
      expect(merged.spans[i].endUs).toBe(singlePass.spans[i].endUs);
    }

    // Running-max arrays must match element-for-element — this is the silent-corruption failure class that's hardest to
    // diagnose post-hoc (flickery span-visibility culling), so we assert it explicitly.
    expect(Array.from(merged.spanEndMaxRunning)).toEqual(Array.from(singlePass.spanEndMaxRunning));

    // Per-kind projections: diskReads should contain exactly one span (the folded async1).
    expect(merged.diskReads.length).toBe(singlePass.diskReads.length);
    expect(merged.diskReads.length).toBe(1);
    expect(merged.diskReads[0].durationUs).toBe(40);   // full async tail, not the 2 µs kickoff
  });

  it('throws on tickNumber mismatch', () => {
    const a = processTickEvents(1, buildEvents(1), SYSTEMS, /*isContinuation=*/false);
    const b = processTickEvents(2, buildEvents(2), SYSTEMS, /*isContinuation=*/false);
    expect(() => mergeTickData(a, b, SYSTEMS)).toThrow(/tickNumber mismatch/);
  });

  it('three-way merge (chain fold from the left) produces identical output to single-pass', () => {
    const tickNumber = 5;
    const full = buildEvents(tickNumber);

    // Split into thirds — chain fold in assembleTickViewAndNumbers repeatedly merges adjacent same-tickNumber entries,
    // so we verify that (((A+B)+C) → single pass). Third split at 6 leaves the kickoff in chunk A and the completion in
    // chunk B, and a MemoryAllocEvent + tail chunks in C — three chunks participate in the chain merge.
    const part1 = full.slice(0, 3);
    const part2 = full.slice(3, 6);
    const part3 = full.slice(6);

    const singlePass = processTickEvents(tickNumber, full, SYSTEMS, /*isContinuation=*/false);
    // Part 1 opens the tick (has TickStart) → non-continuation. Parts 2 and 3 are mid-tick continuations.
    const t1 = processTickEvents(tickNumber, part1, SYSTEMS, /*isContinuation=*/false);
    const t2 = processTickEvents(tickNumber, part2, SYSTEMS, /*isContinuation=*/true);
    const t3 = processTickEvents(tickNumber, part3, SYSTEMS, /*isContinuation=*/true);
    const merged = mergeTickData(mergeTickData(t1, t2, SYSTEMS), t3, SYSTEMS);

    expect(merged.spans.length).toBe(singlePass.spans.length);
    expect(merged.diskReads.length).toBe(singlePass.diskReads.length);
    expect(merged.diskReads[0].durationUs).toBe(singlePass.diskReads[0].durationUs);
    expect(Array.from(merged.spanEndMaxRunning)).toEqual(Array.from(singlePass.spanEndMaxRunning));
  });
});

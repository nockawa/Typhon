/**
 * Shared "group events by tickNumber, run processTickEvents per bucket" builder. Previously duplicated across
 * <c>chunkWorker.ts</c> (worker-thread binary path) and <c>chunkWorkerClient.ts</c> (main-thread sync fallback). The two copies
 * drifted independently on every signature change — factoring them here removes that drift class and gives us ONE place to
 * apply post-build optimisations (like the rawEvents wiping below for non-boundary ticks).
 */
import type { TraceEvent, SystemDef } from '../model/types';
import { processTickEvents, type TickData } from '../model/traceModel';

/**
 * Groups <paramref name="events"/> by tickNumber, runs <c>processTickEvents</c> per bucket, sorts by tick, then wipes
 * <c>rawEvents</c> on ticks that can't possibly participate in a cross-chunk merge (everything except the first and last
 * tick of the chunk).
 *
 * <b>Why wipe rawEvents on middle ticks:</b> a <c>TickData</c>'s <c>rawEvents</c> is the full raw event list — roughly the
 * same size as the derived <c>spans[]</c>. If we ship all of it across <c>postMessage</c> (structured clone) we double
 * the serialization cost on the worker→main boundary. But only BOUNDARY ticks (the first and last tickNumber in a chunk)
 * can ever be continued by another chunk and thus need rawEvents for a merge re-run. Middle ticks are fully self-contained
 * and will never merge — their rawEvents is pure dead weight. Wiping reduces clone cost on multi-tick chunks by ~95%
 * (a 100-tick chunk now retains rawEvents on 2 ticks instead of 100).
 *
 * <b>Edge cases preserved:</b> a chunk with exactly one tick retains rawEvents (it's both first and last). A chunk with
 * two ticks retains both. Continuation chunks always retain rawEvents on their first tick (which IS the merge candidate
 * with the previous chunk's last tick).
 *
 * @param continuationTickNumber Tick number to pass <c>isContinuation=true</c> to <c>processTickEvents</c>; use -1 when
 *   the chunk is not a continuation. Only the first tick of a continuation chunk gets the flag; subsequent ticks (if any)
 *   begin with their own TickStart and are structurally normal.
 */
export function buildTickDataFromEvents(
  events: TraceEvent[],
  systems: SystemDef[],
  continuationTickNumber: number,
): TickData[] {
  if (events.length === 0) return [];

  const byTick = new Map<number, TraceEvent[]>();
  for (const evt of events) {
    let bucket = byTick.get(evt.tickNumber);
    if (!bucket) {
      bucket = [];
      byTick.set(evt.tickNumber, bucket);
    }
    bucket.push(evt);
  }

  const result: TickData[] = [];
  for (const [tickNumber, bucket] of byTick) {
    const isContinuation = tickNumber === continuationTickNumber;
    result.push(processTickEvents(tickNumber, bucket, systems, isContinuation));
  }
  result.sort((a, b) => a.tickNumber - b.tickNumber);

  // Wipe rawEvents on all middle ticks — only chunk-boundary ticks (first, last) can merge with adjacent chunks.
  // For a single-tick chunk the loop body doesn't execute (length === 1), preserving its rawEvents. For a two-tick
  // chunk both are boundaries, and no iteration in [1, length-1) runs. Only for 3+ ticks do we start clearing middles.
  for (let i = 1; i < result.length - 1; i++) {
    result[i].rawEvents = [];
  }
  return result;
}

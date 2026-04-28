import { useCallback, useEffect, useRef } from 'react';
import { useEventSource } from '@/hooks/streams/useEventSource';
import {
  useProfilerSessionStore,
  type LiveStreamPayload,
} from '@/stores/useProfilerSessionStore';

/**
 * SSE subscription for the profiler live delta stream (#289 unified pipeline).
 *
 * Wraps {@link useEventSource} and dispatches the server's growth deltas into {@link useProfilerSessionStore}:
 * `metadata` (full snapshot on connect), `tickSummaryAdded`, `chunkAdded`, `globalMetricsUpdated`,
 * `heartbeat`, and `shutdown`.
 *
 * **rAF-coalesced batching.** Each SSE message handler runs synchronously on the main thread; under heavy ingest
 * (many chunkAdded + tickSummaryAdded per second from a busy engine), one-mutation-per-event meant N×O(N)
 * `[...prev, entry]` array spreads + N×subscriber notifications per frame, which stuttered the UI. We now buffer
 * incoming events in a ref and flush them via `requestAnimationFrame` so each native paint cycle applies AT MOST
 * one batched mutation. The `applyLiveBatch` store action collapses the N appends into a single O(N+batchSize)
 * spread + a single subscriber notification, regardless of how many events landed in the frame.
 *
 * Trade-off: deltas are visible to the UI ≤ one frame (~16 ms) later than they used to be. That's smaller than
 * any human-perceptible cadence and well under the engine's chunk-flush period (200 ms), so user-facing UX is
 * indistinguishable except smoother.
 */
export function useProfilerLiveStream(sessionId: string | null) {
  const applyLiveBatch = useProfilerSessionStore((s) => s.applyLiveBatch);

  // Buffered events accumulated between rAF flushes. Lives in a ref because:
  //   - Mutating it must NOT trigger a React re-render (we only re-render via the store mutation in flush()).
  //   - Identity stability lets the rAF callback read the latest batch without React closures going stale.
  const bufferRef = useRef<LiveStreamPayload[]>([]);
  const rafIdRef = useRef<number>(0);

  const flush = useCallback(() => {
    rafIdRef.current = 0;
    const batch = bufferRef.current;
    if (batch.length === 0) return;
    bufferRef.current = [];
    applyLiveBatch(batch);
  }, [applyLiveBatch]);

  const onMessage = useCallback(
    (payload: LiveStreamPayload) => {
      bufferRef.current.push(payload);
      if (rafIdRef.current === 0) {
        rafIdRef.current = requestAnimationFrame(flush);
      }
    },
    [flush],
  );

  // Cancel any pending flush on unmount / session change so a late-firing rAF can't hit a stale store.
  useEffect(() => {
    return () => {
      if (rafIdRef.current !== 0) {
        cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = 0;
      }
      bufferRef.current = [];
    };
  }, [sessionId]);

  const url = sessionId ? `/api/sessions/${sessionId}/profiler/stream` : null;
  return useEventSource<LiveStreamPayload>(url, onMessage);
}

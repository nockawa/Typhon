import { useCallback, useEffect, useRef } from 'react';
import { useEventSource } from '@/hooks/streams/useEventSource';
import {
  useProfilerSessionStore,
  type LiveStreamPayload,
  type LiveTickBatch,
} from '@/stores/useProfilerSessionStore';

/**
 * SSE subscription for the profiler live stream (Attach mode). Wraps {@link useEventSource} and dispatches
 * discriminated-union payloads into {@link useProfilerSessionStore}.
 *
 * Tick batches are buffered in a ref and flushed at 10 Hz to prevent setState storms when the engine emits at
 * 60+ Hz. Metadata is pushed directly to the store (no invalidation round-trip — the payload already carries
 * the full DTO and UI reads from the store).
 *
 * Heartbeats carry the server's connection status (`connected` / `reconnecting` / `disconnected`). The
 * `useEventSource` reconnect loop handles Workbench-server transport failures; the server-reported status
 * reflects the TCP link between the Workbench and the target Typhon app.
 */
const TICK_FLUSH_INTERVAL_MS = 100;

export function useProfilerLiveStream(sessionId: string | null) {
  const setMetadata = useProfilerSessionStore((s) => s.setMetadata);
  const setConnectionStatus = useProfilerSessionStore((s) => s.setConnectionStatus);
  const appendLiveTicks = useProfilerSessionStore((s) => s.appendLiveTicks);

  const pendingTicksRef = useRef<LiveTickBatch[]>([]);

  const onMessage = useCallback(
    (payload: LiveStreamPayload) => {
      switch (payload.kind) {
        case 'metadata':
          setMetadata(payload.metadata);
          break;
        case 'tick':
          pendingTicksRef.current.push(payload.tick);
          break;
        case 'heartbeat':
          setConnectionStatus(payload.status);
          break;
      }
    },
    [setMetadata, setConnectionStatus],
  );

  // 10 Hz flush — batches tick arrivals so store subscribers re-render at most every 100 ms instead of per tick.
  useEffect(() => {
    if (!sessionId) return;
    const flush = () => {
      if (pendingTicksRef.current.length > 0) {
        const batch = pendingTicksRef.current;
        pendingTicksRef.current = [];
        appendLiveTicks(batch);
      }
    };
    const interval = setInterval(flush, TICK_FLUSH_INTERVAL_MS);
    return () => {
      clearInterval(interval);
      flush(); // Drain anything buffered at unmount.
    };
  }, [sessionId, appendLiveTicks]);

  const url = sessionId ? `/api/sessions/${sessionId}/profiler/stream` : null;
  return useEventSource<LiveStreamPayload>(url, onMessage);
}

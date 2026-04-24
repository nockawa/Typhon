import { useCallback } from 'react';
import { useEventSource } from '@/hooks/streams/useEventSource';
import { useProfilerSessionStore, type BuildProgressPayload } from '@/stores/useProfilerSessionStore';

/**
 * SSE subscription for the profiler build-progress stream. Wraps the generic {@link useEventSource} hook to
 * dispatch payload frames into {@link useProfilerSessionStore}.
 *
 * Server emits frames as default `message` events (no `event:` prefix) with phase ∈ {building, done, error}
 * inside the JSON. Terminal phases close the stream; the hook's auto-reconnect kicks in on transport errors,
 * but terminal phases aren't errors — the EventSource just closes cleanly on the server side.
 */
export function useProfilerBuildProgress(sessionId: string | null) {
  const setBuildProgress = useProfilerSessionStore((s) => s.setBuildProgress);
  const setBuildError = useProfilerSessionStore((s) => s.setBuildError);

  const onMessage = useCallback(
    (payload: BuildProgressPayload) => {
      if (payload.phase === 'error') {
        setBuildError(payload.message ?? 'Build failed.');
        return;
      }
      setBuildProgress(payload);
    },
    [setBuildProgress, setBuildError],
  );

  const url = sessionId ? `/api/sessions/${sessionId}/profiler/build-progress` : null;
  return useEventSource<BuildProgressPayload>(url, onMessage);
}

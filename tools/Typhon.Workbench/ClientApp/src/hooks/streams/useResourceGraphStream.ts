import { useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import { useEventSource } from './useEventSource';

export interface ResourceGraphEventPayload {
  kind: 'Added' | 'Removed' | 'Mutated';
  nodeId: string;
  parentId: string;
  type: string;
  timestamp: string;
}

/**
 * Subscribes the session to the engine's resource-graph mutation stream. On every event the hook
 * invalidates the resource-root query so the tree + fuse index re-fetch. The server coalesces
 * bursts to at most 10 events/sec per session so the invalidation cadence stays gentle.
 */
export function useResourceGraphStream(): { state: 'connecting' | 'open' | 'closed' } {
  const sessionId = useSessionStore((s) => s.sessionId);
  const queryClient = useQueryClient();

  const url = sessionId ? `/api/sessions/${sessionId}/resources/stream` : null;

  const onMessage = useCallback(
    (_evt: ResourceGraphEventPayload) => {
      if (!sessionId) return;
      queryClient.invalidateQueries({
        queryKey: [`/api/sessions/${sessionId}/resources/root`],
      });
    },
    [queryClient, sessionId],
  );

  const state = useEventSource<ResourceGraphEventPayload>(url, onMessage);
  return { state };
}

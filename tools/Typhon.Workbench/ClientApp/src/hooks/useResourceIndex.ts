import { useMemo } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  useGetApiSessionsSessionIdResourcesRoot,
  getGetApiSessionsSessionIdResourcesRootQueryKey,
} from '@/api/generated/resources/resources';
import { useSessionStore } from '@/stores/useSessionStore';
import { ResourceIndex } from '@/libs/ResourceIndex';
import type { ResourceNodeDto } from '@/api/generated/model';

// Module-level refresh registration so non-React callers (base commands) can invalidate the cache.
let registeredRefresh: (() => void) | null = null;

export function refreshResourceGraph() {
  registeredRefresh?.();
}

export function useResourceIndex() {
  const sessionId = useSessionStore((s) => s.sessionId);
  const kind = useSessionStore((s) => s.kind);
  const queryClient = useQueryClient();

  // Resource graph is an Open-session concept — Trace and Attach sessions don't have one and the server
  // returns 409 for those kinds. Gate the query off entirely rather than spamming the logs with failures.
  const enabled = !!sessionId && kind === 'open';

  const query = useGetApiSessionsSessionIdResourcesRoot(
    sessionId ?? '',
    { depth: 'all' },
    { query: { enabled, staleTime: 30_000 } },
  );

  const root = query.data?.data?.root as ResourceNodeDto | undefined;
  const index = useMemo(() => (root ? ResourceIndex.build(root) : null), [root]);

  const refresh = () => {
    if (!sessionId) return;
    queryClient.invalidateQueries({
      queryKey: getGetApiSessionsSessionIdResourcesRootQueryKey(sessionId, { depth: 'all' }),
    });
  };

  registeredRefresh = refresh;

  return {
    root,
    index,
    isLoading: query.isLoading,
    isError: query.isError,
    isFetching: query.isFetching,
    refresh,
  };
}

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
  const queryClient = useQueryClient();

  const query = useGetApiSessionsSessionIdResourcesRoot(
    sessionId ?? '',
    { depth: 'all' },
    { query: { enabled: !!sessionId, staleTime: 30_000 } },
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

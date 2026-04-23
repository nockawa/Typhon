import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeNameArchetypes } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeArchetype, type ArchetypeInfo } from './types';

/**
 * All archetypes that contain the given component type in the current session. Runtime counts
 * (entity count, chunk count) drift during a live session, so we cache with a short staleTime and
 * expose refetch for manual refresh.
 */
export function useArchetypesForComponent(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeNameArchetypes(
    sessionId ?? '',
    typeName ?? '',
    { query: { enabled: !!sessionId && !!typeName, staleTime: 5_000 } },
  );

  const archetypes: ArchetypeInfo[] = useMemo(
    () => (query.data?.data ?? []).map(normalizeArchetype),
    [query.data],
  );

  return {
    archetypes,
    isLoading: query.isLoading,
    isError: query.isError,
    isFetching: query.isFetching,
    refetch: query.refetch,
  };
}

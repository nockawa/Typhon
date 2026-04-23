import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeNameSystems } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import {
  normalizeSystemRelationshipsResponse,
  type SystemRelationshipsResponse,
} from './types';

const EMPTY: SystemRelationshipsResponse = { runtimeHosted: false, systems: [] };

/**
 * Systems that read or reactively trigger on the given component type. Runtime-gated — the
 * envelope's <c>runtimeHosted</c> flag lets the panel distinguish "no relationships" from
 * "runtime not hosted". Until runtime hosting lands in the Workbench, the flag is always false.
 */
export function useSystemRelationships(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeNameSystems(
    sessionId ?? '',
    typeName ?? '',
    { query: { enabled: !!sessionId && !!typeName, staleTime: 30_000 } },
  );

  const response: SystemRelationshipsResponse = useMemo(
    () => (query.data?.data ? normalizeSystemRelationshipsResponse(query.data.data) : EMPTY),
    [query.data],
  );

  return {
    response,
    isLoading: query.isLoading,
    isError: query.isError,
  };
}

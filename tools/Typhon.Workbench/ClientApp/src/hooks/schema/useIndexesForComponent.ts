import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeNameIndexes } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeIndex, type IndexInfo } from './types';

/**
 * Indexes covering fields of the given component type. Schema-stable for the session lifetime so
 * we use Infinity staleTime — no refetch on selection churn.
 */
export function useIndexesForComponent(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeNameIndexes(
    sessionId ?? '',
    typeName ?? '',
    { query: { enabled: !!sessionId && !!typeName, staleTime: Infinity } },
  );

  const indexes: IndexInfo[] = useMemo(
    () => (query.data?.data ?? []).map(normalizeIndex),
    [query.data],
  );

  return {
    indexes,
    isLoading: query.isLoading,
    isError: query.isError,
  };
}

import { useMemo } from 'react';
import { useGetApiSessionsSessionIdSchemaComponentsTypeName } from '@/api/generated/schema/schema';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeSchema, type ComponentSchema } from './types';

/**
 * Full byte-layout schema for one component type — the data the Layout view renders. Stable once
 * loaded (schema is immutable within a session), so we use Infinity staleTime to avoid refetch on
 * selection churn.
 */
export function useComponentSchema(typeName: string | null) {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useGetApiSessionsSessionIdSchemaComponentsTypeName(
    sessionId ?? '',
    typeName ?? '',
    {
      query: {
        enabled: !!sessionId && !!typeName,
        staleTime: Infinity,
      },
    },
  );

  const schema: ComponentSchema | undefined = useMemo(
    () => (query.data?.data ? normalizeSchema(query.data.data) : undefined),
    [query.data],
  );

  return {
    schema,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
  };
}

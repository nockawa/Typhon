import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { ArchetypeInfoDto } from '@/api/generated/model';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';
import { normalizeArchetype, type ArchetypeInfo } from './types';

interface Envelope<T> {
  data: T;
  status: number;
  headers: Headers;
}

/**
 * List of every archetype registered in the current session. Archetype-first counterpart to
 * useComponentList — same shape as the per-component hook, no component filter. Uses raw customFetch
 * so we don't need Orval regen; the endpoint returns the same DTO as the per-component call.
 */
export function useArchetypeList() {
  const sessionId = useSessionStore((s) => s.sessionId);

  const query = useQuery({
    queryKey: ['schema', 'archetypes', sessionId],
    enabled: !!sessionId,
    staleTime: 5_000,
    queryFn: () =>
      customFetch<Envelope<ArchetypeInfoDto[]>>(
        `/api/sessions/${sessionId}/schema/archetypes`,
        { method: 'GET' },
      ),
  });

  const list: ArchetypeInfo[] = useMemo(
    () => (query.data?.data ?? []).map(normalizeArchetype),
    [query.data],
  );

  return {
    list,
    isLoading: query.isLoading,
    isError: query.isError,
    isFetching: query.isFetching,
    refetch: query.refetch,
  };
}

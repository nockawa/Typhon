import { create } from 'zustand';
import type { SessionDto, SessionDiagnosticDto } from '@/api/generated/model';

export type SessionKind = 'none' | 'open' | 'attach' | 'trace';
export type SessionState = 'Ready' | 'MigrationRequired' | 'Incompatible' | 'Attached' | 'Trace';

interface SessionStoreState {
  kind: SessionKind;
  sessionId: string | null;
  token: string | null;
  sessionState: SessionState | null;
  filePath: string | null;
  schemaDllPaths: string[] | null;
  schemaStatus: string | null;
  loadedComponentTypes: number;
  schemaDiagnostics: SessionDiagnosticDto[] | null;
  setSession: (dto: SessionDto) => void;
  clearSession: () => void;
}

export const useSessionStore = create<SessionStoreState>()((set) => ({
  kind: 'none',
  sessionId: null,
  token: null,
  sessionState: null,
  filePath: null,
  schemaDllPaths: null,
  schemaStatus: null,
  loadedComponentTypes: 0,
  schemaDiagnostics: null,
  setSession: (dto) =>
    set({
      kind: (dto.kind?.toLowerCase() ?? 'open') as SessionKind,
      sessionId: dto.sessionId,
      token: dto.sessionId,
      sessionState: (dto.state as SessionState) ?? null,
      filePath: dto.filePath ?? null,
      schemaDllPaths: (dto.schemaDllPaths as string[] | null | undefined) ?? null,
      schemaStatus: (dto.schemaStatus as string | null | undefined) ?? null,
      loadedComponentTypes: dto.loadedComponentTypes != null ? Number(dto.loadedComponentTypes) : 0,
      schemaDiagnostics:
        (dto.schemaDiagnostics as SessionDiagnosticDto[] | null | undefined) ?? null,
    }),
  clearSession: () =>
    set({
      kind: 'none',
      sessionId: null,
      token: null,
      sessionState: null,
      filePath: null,
      schemaDllPaths: null,
      schemaStatus: null,
      loadedComponentTypes: 0,
      schemaDiagnostics: null,
    }),
}));

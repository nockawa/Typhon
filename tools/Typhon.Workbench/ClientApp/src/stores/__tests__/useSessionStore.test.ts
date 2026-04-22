import { beforeEach, describe, expect, it } from 'vitest';
import { useSessionStore } from '../useSessionStore';
import type { SessionDto } from '@/api/generated/model';

const makeDto = (overrides: Partial<SessionDto> = {}): SessionDto => ({
  sessionId: 'a1b2c3d4-0000-0000-0000-000000000000',
  kind: 'Open',
  state: 'Ready',
  filePath: 'test.typhon',
  ...overrides,
});

beforeEach(() => {
  useSessionStore.getState().clearSession();
});

describe('useSessionStore', () => {
  it('defaults to none', () => {
    const s = useSessionStore.getState();
    expect(s.kind).toBe('none');
    expect(s.sessionId).toBeNull();
    expect(s.token).toBeNull();
    expect(s.filePath).toBeNull();
  });

  it('setSession stores dto fields and lowercases kind', () => {
    useSessionStore.getState().setSession(makeDto());
    const s = useSessionStore.getState();
    expect(s.kind).toBe('open');
    expect(s.sessionId).toBe('a1b2c3d4-0000-0000-0000-000000000000');
    expect(s.token).toBe('a1b2c3d4-0000-0000-0000-000000000000');
    expect(s.sessionState).toBe('Ready');
    expect(s.filePath).toBe('test.typhon');
    expect(s.loadedComponentTypes).toBe(0);
    expect(s.schemaDllPaths).toBeNull();
  });

  it('setSession captures Phase 4 schema fields', () => {
    useSessionStore.getState().setSession(
      makeDto({
        schemaDllPaths: ['C:/g/A.schema.dll', 'C:/g/B.schema.dll'],
        schemaStatus: 'user-specified',
        loadedComponentTypes: 5,
      }),
    );
    const s = useSessionStore.getState();
    expect(s.schemaDllPaths).toEqual(['C:/g/A.schema.dll', 'C:/g/B.schema.dll']);
    expect(s.schemaStatus).toBe('user-specified');
    expect(s.loadedComponentTypes).toBe(5);
  });

  it('setSession handles attach kind', () => {
    useSessionStore.getState().setSession(makeDto({ kind: 'Attach', state: 'Attached' }));
    expect(useSessionStore.getState().kind).toBe('attach');
  });

  it('clearSession resets all fields to none/null', () => {
    useSessionStore.getState().setSession(makeDto());
    useSessionStore.getState().clearSession();
    const s = useSessionStore.getState();
    expect(s.kind).toBe('none');
    expect(s.sessionId).toBeNull();
    expect(s.token).toBeNull();
    expect(s.filePath).toBeNull();
  });
});

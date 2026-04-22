import { beforeEach, describe, expect, it } from 'vitest';
import { useRecentFilesStore, type RecentFile } from '../useRecentFilesStore';

const makeEntry = (overrides: Partial<RecentFile> = {}): RecentFile => ({
  filePath: 'C:/games/demo.typhon',
  schemaDllPaths: ['C:/games/Game.schema.dll'],
  lastOpenedAt: '2026-04-21T12:00:00Z',
  lastState: 'Ready',
  ...overrides,
});

beforeEach(() => {
  useRecentFilesStore.setState({ entries: [] });
});

describe('useRecentFilesStore', () => {
  it('starts empty', () => {
    expect(useRecentFilesStore.getState().entries).toEqual([]);
  });

  it('record prepends new entries', () => {
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'b.typhon' }));
    const entries = useRecentFilesStore.getState().entries;
    expect(entries.map((e) => e.filePath)).toEqual(['b.typhon', 'a.typhon']);
  });

  it('record dedupes case-insensitively and moves to front', () => {
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'C:/DB/demo.typhon' }));
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'x.typhon' }));
    useRecentFilesStore
      .getState()
      .record(makeEntry({ filePath: 'c:/db/demo.typhon', lastState: 'Incompatible' }));
    const entries = useRecentFilesStore.getState().entries;
    expect(entries).toHaveLength(2);
    expect(entries[0].filePath).toBe('c:/db/demo.typhon');
    expect(entries[0].lastState).toBe('Incompatible');
  });

  it('record caps at 20 entries', () => {
    for (let i = 0; i < 25; i++) {
      useRecentFilesStore.getState().record(makeEntry({ filePath: `f${i}.typhon` }));
    }
    const entries = useRecentFilesStore.getState().entries;
    expect(entries).toHaveLength(20);
    expect(entries[0].filePath).toBe('f24.typhon');
  });

  it('remove deletes by filePath (case-insensitive)', () => {
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'A.typhon' }));
    useRecentFilesStore.getState().record(makeEntry({ filePath: 'B.typhon' }));
    useRecentFilesStore.getState().remove('a.typhon');
    const entries = useRecentFilesStore.getState().entries;
    expect(entries.map((e) => e.filePath)).toEqual(['B.typhon']);
  });

  it('clear empties the store', () => {
    useRecentFilesStore.getState().record(makeEntry());
    useRecentFilesStore.getState().clear();
    expect(useRecentFilesStore.getState().entries).toEqual([]);
  });

  describe('pins', () => {
    it('pinResource adds an id and getPins reads it back', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'Storage/Cache');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['Storage/Cache']);
    });

    it('pinResource is case-insensitive on filePath', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'A.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'node/1');
      expect(useRecentFilesStore.getState().getPins('A.typhon')).toEqual(['node/1']);
    });

    it('pinResource is idempotent', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'r1');
      useRecentFilesStore.getState().pinResource('a.typhon', 'r1');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['r1']);
    });

    it('unpinResource removes the id', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'r1');
      useRecentFilesStore.getState().pinResource('a.typhon', 'r2');
      useRecentFilesStore.getState().unpinResource('a.typhon', 'r1');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['r2']);
    });

    it('pins are per-file', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'b.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'rA');
      useRecentFilesStore.getState().pinResource('b.typhon', 'rB');
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['rA']);
      expect(useRecentFilesStore.getState().getPins('b.typhon')).toEqual(['rB']);
    });

    it('record preserves existing pins on re-record of same file', () => {
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon' }));
      useRecentFilesStore.getState().pinResource('a.typhon', 'pinned-id');
      useRecentFilesStore.getState().record(makeEntry({ filePath: 'a.typhon', lastState: 'MigrationRequired' }));
      expect(useRecentFilesStore.getState().getPins('a.typhon')).toEqual(['pinned-id']);
    });
  });
});

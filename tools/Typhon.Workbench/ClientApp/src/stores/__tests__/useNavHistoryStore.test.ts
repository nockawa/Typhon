import { beforeEach, describe, expect, it } from 'vitest';
import { useNavHistoryStore, type NavEntry } from '../useNavHistoryStore';
import { useSelectedResourceStore, type SelectedResource } from '../useSelectedResourceStore';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';

const makeRaw = (id: string): ResourceNodeDto => ({
  id,
  name: id,
  type: 'Segment',
  entityCount: null,
  children: [],
});

const makeSelected = (resourceId: string): SelectedResource => ({
  resourceId,
  kind: 'Segment',
  name: resourceId,
  path: [resourceId],
  raw: makeRaw(resourceId),
});

const resourceEntry = (id: string): NavEntry => ({
  kind: 'resource-selected',
  resourceId: id,
  selected: makeSelected(id),
  timestamp: Date.now(),
});

const panelEntry = (id: string): NavEntry => ({
  kind: 'panel-opened',
  panelId: id,
  timestamp: Date.now(),
});

beforeEach(() => {
  useNavHistoryStore.getState().clear();
  useSelectedResourceStore.getState().clear();
});

describe('useNavHistoryStore — basic navigation', () => {
  it('starts empty, cannot back or forward', () => {
    const s = useNavHistoryStore.getState();
    expect(s.pointer).toBe(-1);
    expect(s.canBack).toBe(false);
    expect(s.canForward).toBe(false);
  });

  it('push adds entry, cannot back from first entry', () => {
    useNavHistoryStore.getState().push(panelEntry('panel'));
    const s = useNavHistoryStore.getState();
    expect(s.entries).toHaveLength(1);
    expect(s.pointer).toBe(0);
    expect(s.canBack).toBe(false);
    expect(s.canForward).toBe(false);
  });

  it('push two entries: canBack=true from second', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().push(panelEntry('b'));
    const s = useNavHistoryStore.getState();
    expect(s.canBack).toBe(true);
    expect(s.canForward).toBe(false);
  });

  it('back then forward restores pointer', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().push(panelEntry('b'));
    useNavHistoryStore.getState().back();
    expect(useNavHistoryStore.getState().pointer).toBe(0);
    expect(useNavHistoryStore.getState().canForward).toBe(true);
    useNavHistoryStore.getState().forward();
    expect(useNavHistoryStore.getState().pointer).toBe(1);
    expect(useNavHistoryStore.getState().canForward).toBe(false);
  });

  it('back does nothing when canBack=false', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().back();
    expect(useNavHistoryStore.getState().pointer).toBe(0);
  });

  it('forward does nothing when canForward=false', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().forward();
    expect(useNavHistoryStore.getState().pointer).toBe(0);
  });

  it('push from mid-history discards forward stack', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().push(panelEntry('b'));
    useNavHistoryStore.getState().push(panelEntry('c'));
    useNavHistoryStore.getState().back();
    useNavHistoryStore.getState().back();
    useNavHistoryStore.getState().push(panelEntry('d'));
    const s = useNavHistoryStore.getState();
    expect(s.entries).toHaveLength(2);
    expect(s.pointer).toBe(1);
    expect(s.canForward).toBe(false);
  });
});

describe('useNavHistoryStore — ring buffer overflow', () => {
  it('caps at 100 entries on overflow', () => {
    for (let i = 0; i < 105; i++) {
      useNavHistoryStore.getState().push(panelEntry(`e${i}`));
    }
    const s = useNavHistoryStore.getState();
    expect(s.entries).toHaveLength(100);
    expect(s.pointer).toBe(99);
    expect(s.entries[0].kind === 'panel-opened' && s.entries[0].panelId).toBe('e5');
  });
});

describe('useNavHistoryStore — restore dispatches to selection store', () => {
  it('back() on resource-selected entries updates useSelectedResourceStore', () => {
    useNavHistoryStore.getState().push(resourceEntry('r1'));
    useNavHistoryStore.getState().push(resourceEntry('r2'));
    expect(useSelectedResourceStore.getState().selected).toBeNull();

    useNavHistoryStore.getState().back();
    expect(useSelectedResourceStore.getState().selected?.resourceId).toBe('r1');

    useNavHistoryStore.getState().forward();
    expect(useSelectedResourceStore.getState().selected?.resourceId).toBe('r2');
  });

  it('restore does not push (no stack growth)', () => {
    useNavHistoryStore.getState().push(resourceEntry('r1'));
    useNavHistoryStore.getState().push(resourceEntry('r2'));
    const sizeBefore = useNavHistoryStore.getState().entries.length;

    // Simulate a downstream setSelected → push() attempt during back()
    useNavHistoryStore.getState().back();

    // Stack must not have grown: back() flipped the restore flag, so any hypothetical push
    // inside the dispatch chain would be a no-op.
    expect(useNavHistoryStore.getState().entries.length).toBe(sizeBefore);
    expect(useNavHistoryStore.getState().isRestoring).toBe(false);
  });
});

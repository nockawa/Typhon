import { beforeEach, describe, expect, it } from 'vitest';
import { useSelectedResourceStore, type SelectedResource } from '../useSelectedResourceStore';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';

const raw: ResourceNodeDto = {
  id: 'storage/paged-mmf',
  name: 'PagedMMF',
  type: 'Segment',
  entityCount: null,
  children: [],
};

const sample: SelectedResource = {
  resourceId: 'storage/paged-mmf',
  kind: 'Segment',
  name: 'PagedMMF',
  path: ['storage', 'paged-mmf'],
  raw,
};

beforeEach(() => {
  useSelectedResourceStore.getState().clear();
});

describe('useSelectedResourceStore', () => {
  it('starts null', () => {
    expect(useSelectedResourceStore.getState().selected).toBeNull();
  });

  it('setSelected stores the value', () => {
    useSelectedResourceStore.getState().setSelected(sample);
    expect(useSelectedResourceStore.getState().selected?.resourceId).toBe('storage/paged-mmf');
  });

  it('clear resets to null', () => {
    useSelectedResourceStore.getState().setSelected(sample);
    useSelectedResourceStore.getState().clear();
    expect(useSelectedResourceStore.getState().selected).toBeNull();
  });
});

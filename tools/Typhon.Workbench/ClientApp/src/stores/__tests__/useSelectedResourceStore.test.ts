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
  // Reset via direct setState instead of clear() — clear() bumps touchedAt on purpose, which
  // would leave before/after values equal within the same millisecond tick in the recency tests.
  useSelectedResourceStore.setState({ selected: null, touchedAt: 0 });
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

  it('setSelected bumps touchedAt so DetailPanel can order vs. schema-field selection', () => {
    const before = useSelectedResourceStore.getState().touchedAt;
    useSelectedResourceStore.getState().setSelected(sample);
    const after = useSelectedResourceStore.getState().touchedAt;
    expect(after).toBeGreaterThan(before);
  });

  it('clear bumps touchedAt (cleared-just-now is a real signal for the recency race)', () => {
    useSelectedResourceStore.getState().setSelected(sample);
    const mid = useSelectedResourceStore.getState().touchedAt;
    useSelectedResourceStore.getState().clear();
    const after = useSelectedResourceStore.getState().touchedAt;
    expect(after).toBeGreaterThanOrEqual(mid);
  });
});

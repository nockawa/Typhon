import { beforeEach, describe, expect, it } from 'vitest';
import { useDockLayoutStore } from '../useDockLayoutStore';

beforeEach(() => {
  useDockLayoutStore.getState().clear();
});

describe('useDockLayoutStore', () => {
  it('returns null for unknown key', () => {
    expect(useDockLayoutStore.getState().get('missing')).toBeNull();
  });

  it('saves and retrieves a layout', () => {
    const layout = { panels: [{ id: 'tree' }] };
    useDockLayoutStore.getState().save('file.typhon', layout);
    expect(useDockLayoutStore.getState().get('file.typhon')).toEqual(layout);
  });

  it('overwrites existing layout on re-save', () => {
    useDockLayoutStore.getState().save('f', { v: 1 });
    useDockLayoutStore.getState().save('f', { v: 2 });
    expect((useDockLayoutStore.getState().get('f') as { v: number }).v).toBe(2);
  });

  it('clear removes all layouts', () => {
    useDockLayoutStore.getState().save('a', {});
    useDockLayoutStore.getState().save('b', {});
    useDockLayoutStore.getState().clear();
    expect(useDockLayoutStore.getState().get('a')).toBeNull();
  });
});

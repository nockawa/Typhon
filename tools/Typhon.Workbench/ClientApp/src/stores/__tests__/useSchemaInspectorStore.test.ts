import { describe, expect, it, beforeEach } from 'vitest';
import { useSchemaInspectorStore } from '../useSchemaInspectorStore';

describe('useSchemaInspectorStore', () => {
  beforeEach(() => {
    useSchemaInspectorStore.getState().reset();
  });

  it('starts empty with cacheLineBoundaries on and defaults off', () => {
    const s = useSchemaInspectorStore.getState();
    expect(s.selectedComponentType).toBeNull();
    expect(s.selectedField).toBeNull();
    expect(s.viewMode).toBe('layout');
    expect(s.overlays.cacheLineBoundaries).toBe(true);
    expect(s.overlays.defaults).toBe(false);
  });

  it('selectComponent replaces the type and clears the field', () => {
    const s = useSchemaInspectorStore.getState();
    s.selectComponent('PlayerStats');
    s.selectField('Health');
    s.selectComponent('Position');

    const next = useSchemaInspectorStore.getState();
    expect(next.selectedComponentType).toBe('Position');
    expect(next.selectedField).toBeNull();
  });

  it('toggleOverlay flips only the requested key', () => {
    const s = useSchemaInspectorStore.getState();
    s.toggleOverlay('defaults');
    s.toggleOverlay('cacheLineBoundaries');

    const next = useSchemaInspectorStore.getState();
    expect(next.overlays.defaults).toBe(true);
    expect(next.overlays.cacheLineBoundaries).toBe(false);
  });

  it('setViewMode updates the mode', () => {
    useSchemaInspectorStore.getState().setViewMode('archetypes');
    expect(useSchemaInspectorStore.getState().viewMode).toBe('archetypes');
  });

  it('reset returns to initial state', () => {
    const s = useSchemaInspectorStore.getState();
    s.selectComponent('X');
    s.selectField('Y');
    s.toggleOverlay('defaults');
    s.setViewMode('relationships');
    s.reset();

    const next = useSchemaInspectorStore.getState();
    expect(next.selectedComponentType).toBeNull();
    expect(next.selectedField).toBeNull();
    expect(next.fieldTouchedAt).toBe(0);
    expect(next.viewMode).toBe('layout');
    expect(next.overlays.defaults).toBe(false);
    expect(next.overlays.cacheLineBoundaries).toBe(true);
  });

  it('selectField bumps fieldTouchedAt so DetailPanel can order vs. resource-tree selection', () => {
    const before = useSchemaInspectorStore.getState().fieldTouchedAt;
    useSchemaInspectorStore.getState().selectField('X');
    const after = useSchemaInspectorStore.getState().fieldTouchedAt;
    expect(after).toBeGreaterThan(before);
  });

  it('selectComponent also bumps fieldTouchedAt (it clears the field → counts as a field-channel change)', () => {
    const before = useSchemaInspectorStore.getState().fieldTouchedAt;
    useSchemaInspectorStore.getState().selectComponent('X');
    const after = useSchemaInspectorStore.getState().fieldTouchedAt;
    expect(after).toBeGreaterThan(before);
  });
});

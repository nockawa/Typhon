import { describe, expect, it } from 'vitest';
import { ResourceIndex } from '../ResourceIndex';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';

const fixture: ResourceNodeDto = {
  id: 'root',
  name: 'Root',
  type: 'Database',
  entityCount: null,
  children: [
    {
      id: 'storage',
      name: 'Storage',
      type: 'Subsystem',
      entityCount: null,
      children: [
        { id: 'storage/paged-mmf', name: 'PagedMMF', type: 'Segment', entityCount: null, children: [] },
        { id: 'storage/wal', name: 'WAL', type: 'Segment', entityCount: null, children: [] },
      ],
    },
    {
      id: 'data',
      name: 'Data',
      type: 'Subsystem',
      entityCount: null,
      children: [
        { id: 'data/components', name: 'Components', type: 'ComponentTable', entityCount: null, children: [] },
      ],
    },
  ],
};

describe('ResourceIndex', () => {
  it('builds a flat entry list covering every node', () => {
    const idx = ResourceIndex.build(fixture);
    expect(idx.size).toBe(6);
  });

  it('search returns ranked hits by name substring', () => {
    const idx = ResourceIndex.build(fixture);
    const hits = idx.search('paged');
    expect(hits.length).toBeGreaterThan(0);
    // Synthetic uid encodes the full path from root (siblingIndex:naturalId segments).
    expect(hits[0].id).toContain('paged-mmf');
    expect(hits[0].raw.id).toBe('storage/paged-mmf');
    expect(hits[0].path).toEqual(['Root', 'Storage', 'PagedMMF']);
  });

  it('search returns hits by kind', () => {
    const idx = ResourceIndex.build(fixture);
    const hits = idx.search('Segment');
    const kinds = hits.map((h) => h.kind);
    expect(kinds).toContain('Segment');
  });

  it('getById resolves known uid', () => {
    const idx = ResourceIndex.build(fixture);
    // Look up via search to learn the synthetic uid, then round-trip via getById.
    const walHit = idx.search('WAL')[0];
    expect(walHit).toBeDefined();
    expect(idx.getById(walHit.id)?.name).toBe('WAL');
    expect(idx.getById('missing')).toBeNull();
  });

  it('empty query returns prefix of entries', () => {
    const idx = ResourceIndex.build(fixture);
    const hits = idx.search('');
    expect(hits.length).toBe(6);
  });
});

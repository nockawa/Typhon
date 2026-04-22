import type { ResourceIndex, ResourceHit } from '@/libs/ResourceIndex';

export interface ResourceCommandGroup {
  kind: string;
  hits: ResourceHit[];
}

export function groupHitsByKind(hits: ResourceHit[]): ResourceCommandGroup[] {
  const map = new Map<string, ResourceHit[]>();
  for (const h of hits) {
    const bucket = map.get(h.kind);
    if (bucket) bucket.push(h);
    else map.set(h.kind, [h]);
  }
  return Array.from(map, ([kind, hits]) => ({ kind, hits }));
}

export function buildResourceHits(index: ResourceIndex | null, query: string): ResourceHit[] {
  if (!index) return [];
  return index.search(query.replace(/^#/, '').trim(), 50);
}

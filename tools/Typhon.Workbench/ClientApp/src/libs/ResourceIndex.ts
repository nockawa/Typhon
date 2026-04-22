import Fuse from 'fuse.js';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';

export interface ResourceHit {
  id: string;
  name: string;
  kind: string;
  path: string[];
  raw: ResourceNodeDto;
  score: number;
}

interface IndexEntry {
  id: string;
  name: string;
  kind: string;
  path: string[];
  raw: ResourceNodeDto;
}

export class ResourceIndex {
  private readonly entries: IndexEntry[];
  private readonly byId: Map<string, IndexEntry>;
  private readonly fuse: Fuse<IndexEntry>;

  private constructor(entries: IndexEntry[]) {
    this.entries = entries;
    this.byId = new Map(entries.map((e) => [e.id, e]));
    this.fuse = new Fuse(entries, {
      keys: [
        { name: 'name', weight: 0.7 },
        { name: 'kind', weight: 0.2 },
        { name: 'pathStr', getFn: (e) => e.path.join('/'), weight: 0.1 },
      ],
      threshold: 0.35,
      ignoreLocation: true,
      includeScore: true,
    });
  }

  static build(root: ResourceNodeDto): ResourceIndex {
    const entries: IndexEntry[] = [];
    // Synthetic path-based uid matches the one produced by ResourceTreePanel.toResourceNode
    // so palette selections (by uid) line up with the tree's `selection` prop even when the
    // engine surfaces duplicate natural ids.
    const walk = (node: ResourceNodeDto, path: string[], parentUid: string, siblingIndex: number) => {
      const natural = node.id ?? '';
      const uid = `${parentUid}/${siblingIndex}:${natural}`;
      const name = node.name ?? natural;
      const kind = node.type ?? '';
      const nextPath = [...path, name];
      entries.push({ id: uid, name, kind, path: nextPath, raw: node });
      const children = node.children;
      if (children) {
        let i = 0;
        for (const c of children) walk(c, nextPath, uid, i++);
      }
    };
    walk(root, [], '', 0);
    return new ResourceIndex(entries);
  }

  search(query: string, limit = 50): ResourceHit[] {
    const q = query.trim();
    if (!q) {
      return this.entries.slice(0, limit).map((e) => ({
        id: e.id,
        name: e.name,
        kind: e.kind,
        path: e.path,
        raw: e.raw,
        score: 0,
      }));
    }
    return this.fuse
      .search(q, { limit })
      .map((r) => ({
        id: r.item.id,
        name: r.item.name,
        kind: r.item.kind,
        path: r.item.path,
        raw: r.item.raw,
        score: r.score ?? 0,
      }));
  }

  getById(id: string): ResourceHit | null {
    const e = this.byId.get(id);
    if (!e) return null;
    return { id: e.id, name: e.name, kind: e.kind, path: e.path, raw: e.raw, score: 0 };
  }

  get size(): number {
    return this.entries.length;
  }
}

import { useDeferredValue, useMemo, useState } from 'react';
import Fuse from 'fuse.js';
import type { IDockviewPanelProps } from 'dockview-react';
import { ArrowDown, ArrowUp, RefreshCw } from 'lucide-react';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { StatusBadge } from '@/components/ui/status-badge';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import type { ArchetypeInfo } from '@/hooks/schema/types';
import ArchetypeBrowserContextMenu from './ArchetypeBrowserContextMenu';

type SortKey = 'archetypeId' | 'componentCount' | 'entityCount' | 'chunkCount' | 'occupancyPct';
type SortDir = 'asc' | 'desc';
type QuickFilter = 'noEntities' | 'legacy';

/**
 * Dockable panel listing every archetype in the current session — the archetype-first counterpart
 * to the Component Browser. Sortable columns, fuzzy search on component type names, quick filters
 * for common triage shapes (no entities, legacy storage). Double-click / "Open in Data Browser" is
 * disabled until module 06 ships.
 */
export default function ArchetypeBrowserPanel(_props: IDockviewPanelProps) {
  const { list, isLoading, isError, refetch, isFetching } = useArchetypeList();

  const [query, setQuery] = useState('');
  const deferredQuery = useDeferredValue(query);
  const [sortKey, setSortKey] = useState<SortKey>('archetypeId');
  const [sortDir, setSortDir] = useState<SortDir>('asc');
  const [filters, setFilters] = useState<Set<QuickFilter>>(() => new Set());

  // Fuse indexes component type names so a user typing "Player" surfaces every archetype that
  // declares a component whose FullName contains Player.
  const fuse = useMemo(
    () =>
      new Fuse(list, {
        keys: [{ name: 'componentTypes', weight: 1 }],
        threshold: 0.35,
        ignoreLocation: true,
      }),
    [list],
  );

  const filtered = useMemo(() => {
    const base =
      deferredQuery.trim().length === 0
        ? list
        : fuse.search(deferredQuery).map((r) => r.item);

    return base.filter((a) => {
      if (filters.has('noEntities') && a.entityCount !== 0) return false;
      if (filters.has('legacy') && a.storageMode !== 'legacy') return false;
      return true;
    });
  }, [list, fuse, deferredQuery, filters]);

  const sorted = useMemo(() => {
    const arr = [...filtered];
    arr.sort((a, b) => {
      const va = readSortField(a, sortKey);
      const vb = readSortField(b, sortKey);
      return sortDir === 'asc' ? va - vb : vb - va;
    });
    return arr;
  }, [filtered, sortKey, sortDir]);

  const toggleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      setSortDir('asc');
    }
  };

  const toggleFilter = (f: QuickFilter) => {
    setFilters((prev) => {
      const next = new Set(prev);
      if (next.has(f)) {
        next.delete(f);
      } else {
        next.add(f);
      }
      return next;
    });
  };

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-2 py-1.5">
        <Input
          placeholder="Search archetypes… (component type)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="h-7 text-[12px]"
        />
        <Button
          size="sm"
          variant="ghost"
          className="h-7 w-7 p-0"
          onClick={() => refetch()}
          title="Refresh"
          aria-label="Refresh archetype list"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-1 border-b border-border px-2 py-1">
        <span className="text-[11px] text-muted-foreground">Quick filters:</span>
        <FilterChip label="No entities" active={filters.has('noEntities')} onClick={() => toggleFilter('noEntities')} />
        <FilterChip label="Legacy storage" active={filters.has('legacy')} onClick={() => toggleFilter('legacy')} />
        <span className="ml-auto text-[11px] text-muted-foreground">
          {sorted.length} of {list.length}
        </span>
      </div>

      <div className="flex-1 overflow-auto">
        {isError && (
          <p className="p-3 text-[12px] text-destructive">Failed to load archetypes.</p>
        )}
        {isLoading && !list.length && (
          <p className="p-3 text-[12px] text-muted-foreground">Loading archetypes…</p>
        )}
        {!isLoading && list.length === 0 && (
          <p className="p-3 text-[12px] text-muted-foreground">
            No archetype registered in this session.
          </p>
        )}
        {list.length > 0 && (
          <Table className="text-[12px]">
            <TableHeader>
              <TableRow>
                <SortableHead sortKey="archetypeId" label="ID" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <TableHead className="py-1 text-[11px]">Components</TableHead>
                <TableHead className="py-1 text-[11px]">Storage</TableHead>
                <SortableHead sortKey="entityCount" label="Entities" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="chunkCount" label="Chunks" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="occupancyPct" label="Occupancy" current={sortKey} dir={sortDir} onClick={toggleSort} />
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((a) => (
                <ArchetypeBrowserContextMenu key={a.archetypeId} archetype={a}>
                  <TableRow
                    data-testid="archetype-browser-row"
                    data-archetype-id={a.archetypeId}
                    data-storage-mode={a.storageMode}
                  >
                    <TableCell className="py-1 font-mono tabular-nums">#{a.archetypeId}</TableCell>
                    <TableCell className="py-1 text-[11px] text-muted-foreground">
                      <ComponentsSummary types={a.componentTypes} />
                    </TableCell>
                    <TableCell className="py-1">
                      <StorageModePill mode={a.storageMode} />
                    </TableCell>
                    <TableCell className="py-1 text-right tabular-nums">
                      {a.entityCount.toLocaleString()}
                    </TableCell>
                    <TableCell className="py-1 text-right tabular-nums">
                      {a.storageMode === 'cluster' ? `${a.chunkCount}×${a.chunkCapacity}` : '—'}
                    </TableCell>
                    <TableCell className="py-1 text-right tabular-nums">
                      {a.storageMode === 'cluster' && a.chunkCount > 0 ? `${a.occupancyPct.toFixed(1)}%` : '—'}
                    </TableCell>
                  </TableRow>
                </ArchetypeBrowserContextMenu>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  );
}

function StorageModePill({ mode }: { mode: 'cluster' | 'legacy' }) {
  if (mode === 'cluster') {
    return (
      <StatusBadge tone="success" title="SoA cluster chunks — preferred layout">
        cluster
      </StatusBadge>
    );
  }
  return (
    <StatusBadge tone="warn" title="Legacy per-ComponentTable segment storage — migration may improve performance">
      legacy
    </StatusBadge>
  );
}

function ComponentsSummary({ types }: { types: string[] }) {
  if (types.length === 0) return <span>(none)</span>;
  const shown = types.slice(0, 3);
  const rest = types.length - shown.length;
  return (
    <span title={types.join('\n')}>
      <span className="font-mono text-foreground">{shown.map(stripNamespace).join(', ')}</span>
      {rest > 0 && <span className="ml-1">+{rest}</span>}
      <span className="ml-1 text-muted-foreground">({types.length})</span>
    </span>
  );
}

function stripNamespace(fullName: string): string {
  const dot = fullName.lastIndexOf('.');
  return dot === -1 ? fullName : fullName.slice(dot + 1);
}

function FilterChip({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return (
    <Badge
      variant={active ? 'default' : 'outline'}
      className="cursor-pointer select-none px-2 py-0 text-[11px]"
      onClick={onClick}
    >
      {label}
    </Badge>
  );
}

function SortableHead({
  sortKey,
  label,
  current,
  dir,
  onClick,
}: {
  sortKey: SortKey;
  label: string;
  current: SortKey;
  dir: SortDir;
  onClick: (key: SortKey) => void;
}) {
  const active = current === sortKey;
  return (
    <TableHead
      className="cursor-pointer select-none py-1 text-[11px]"
      onClick={() => onClick(sortKey)}
    >
      <span className="inline-flex items-center gap-1">
        {label}
        {active && (dir === 'asc' ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
      </span>
    </TableHead>
  );
}

function readSortField(row: ArchetypeInfo, key: SortKey): number {
  switch (key) {
    case 'archetypeId':
      return Number(row.archetypeId);
    case 'componentCount':
      return row.componentTypes.length;
    case 'entityCount':
      return row.entityCount;
    case 'chunkCount':
      return row.chunkCount;
    case 'occupancyPct':
      return row.occupancyPct;
  }
}

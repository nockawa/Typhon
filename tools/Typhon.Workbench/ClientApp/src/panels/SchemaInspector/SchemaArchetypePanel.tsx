import { useMemo, useState } from 'react';
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
import { StatusBadge } from '@/components/ui/status-badge';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useArchetypesForComponent } from '@/hooks/schema/useArchetypesForComponent';
import type { ArchetypeInfo } from '@/hooks/schema/types';

type SortKey = 'archetypeId' | 'entityCount' | 'occupancyPct' | 'chunkCount';
type SortDir = 'asc' | 'desc';

/**
 * Archetype panel for the Schema Inspector. Shows every archetype that contains the focused
 * component, with a storage-mode pill (cluster vs. legacy) and runtime counts. Cross-module link
 * "Open in Data Browser" is disabled until module 06 ships.
 */
export default function SchemaArchetypePanel(_props: IDockviewPanelProps) {
  const selectedType = useSchemaInspectorStore((s) => s.selectedComponentType);
  const { archetypes, isLoading, isError, refetch, isFetching } = useArchetypesForComponent(selectedType);

  const [sortKey, setSortKey] = useState<SortKey>('archetypeId');
  const [sortDir, setSortDir] = useState<SortDir>('asc');

  const sorted = useMemo(() => {
    const arr = [...archetypes];
    arr.sort((a, b) => {
      const va = readSortField(a, sortKey);
      const vb = readSortField(b, sortKey);
      if (typeof va === 'string' && typeof vb === 'string') {
        return sortDir === 'asc' ? va.localeCompare(vb) : vb.localeCompare(va);
      }
      return sortDir === 'asc' ? Number(va) - Number(vb) : Number(vb) - Number(va);
    });
    return arr;
  }, [archetypes, sortKey, sortDir]);

  const toggleSort = (key: SortKey) => {
    if (sortKey === key) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else {
      setSortKey(key);
      setSortDir('asc');
    }
  };

  if (!selectedType) {
    return <EmptyState message="No component selected. Pick one from the Component Browser." />;
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <h3 className="font-mono text-[12px] font-semibold text-foreground">{selectedType}</h3>
        <span className="text-[11px] text-muted-foreground">
          archetypes containing this component
        </span>
        <span className="ml-auto text-[11px] text-muted-foreground">{archetypes.length}</span>
        <Button
          size="sm"
          variant="ghost"
          className="h-6 w-6 p-0"
          onClick={() => refetch()}
          title="Refresh"
          aria-label="Refresh archetype list"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      <div className="flex-1 overflow-auto">
        {isError && <p className="p-3 text-[12px] text-destructive">Failed to load archetypes.</p>}
        {isLoading && archetypes.length === 0 && (
          <p className="p-3 text-[12px] text-muted-foreground">Loading archetypes…</p>
        )}
        {!isLoading && archetypes.length === 0 && (
          <p className="p-3 text-[12px] text-muted-foreground">
            No archetype contains this component in the current session.
          </p>
        )}
        {archetypes.length > 0 && (
          <Table className="text-[12px]">
            <TableHeader>
              <TableRow>
                <SortableHead sortKey="archetypeId" label="ID" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <TableHead className="py-1 text-[11px]">Storage</TableHead>
                <SortableHead sortKey="entityCount" label="Entities" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="chunkCount" label="Chunks" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="occupancyPct" label="Occupancy" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <TableHead className="py-1 text-[11px]">Other Components</TableHead>
                <TableHead className="py-1 text-[11px]"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((a) => (
                <TableRow
                  key={a.archetypeId}
                  data-testid="archetype-row"
                  data-archetype-id={a.archetypeId}
                  data-storage-mode={a.storageMode}
                >
                  <TableCell className="py-1 font-mono tabular-nums">#{a.archetypeId}</TableCell>
                  <TableCell className="py-1">
                    <StorageModePill mode={a.storageMode} />
                  </TableCell>
                  <TableCell className="py-1 text-right tabular-nums">{a.entityCount.toLocaleString()}</TableCell>
                  <TableCell className="py-1 text-right tabular-nums">
                    {a.storageMode === 'cluster' ? `${a.chunkCount}×${a.chunkCapacity}` : '—'}
                  </TableCell>
                  <TableCell className="py-1 text-right tabular-nums">
                    {a.storageMode === 'cluster' && a.chunkCount > 0 ? `${a.occupancyPct.toFixed(1)}%` : '—'}
                  </TableCell>
                  <TableCell className="py-1 text-[10px] text-muted-foreground">
                    <OtherComponentsSummary focused={selectedType} all={a.componentTypes} />
                  </TableCell>
                  <TableCell className="py-1">
                    <Button
                      size="sm"
                      variant="outline"
                      className="h-6 px-2 text-[10px]"
                      disabled
                      title="Data Browser ships in module 06"
                    >
                      Open in Data Browser
                    </Button>
                  </TableCell>
                </TableRow>
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
    <StatusBadge
      tone="warn"
      title="Legacy per-ComponentTable segment storage — migration may improve performance"
    >
      legacy
    </StatusBadge>
  );
}

function OtherComponentsSummary({
  focused,
  all,
}: {
  focused: string;
  all: string[];
}) {
  // Filter out the focused component (matched by simple-name or full-name fallback).
  const others = all.filter((t) => !t.endsWith(focused) && t !== focused);
  if (others.length === 0) return <span>(none)</span>;
  const shown = others.slice(0, 3);
  const rest = others.length - shown.length;
  return (
    <span title={others.join('\n')}>
      {shown.map((s) => stripNamespace(s)).join(', ')}
      {rest > 0 && ` +${rest}`}
    </span>
  );
}

function stripNamespace(fullName: string): string {
  const dot = fullName.lastIndexOf('.');
  return dot === -1 ? fullName : fullName.slice(dot + 1);
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
    <TableHead className="cursor-pointer select-none py-1 text-[11px]" onClick={() => onClick(sortKey)}>
      <span className="inline-flex items-center gap-1">
        {label}
        {active && (dir === 'asc' ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
      </span>
    </TableHead>
  );
}

function EmptyState({ message }: { message: string }) {
  return (
    <div className="flex h-full items-center justify-center bg-background">
      <p className="text-[12px] text-muted-foreground">{message}</p>
    </div>
  );
}

function readSortField(row: ArchetypeInfo, key: SortKey): string | number {
  switch (key) {
    case 'archetypeId':
      return Number(row.archetypeId);
    case 'entityCount':
      return row.entityCount;
    case 'occupancyPct':
      return row.occupancyPct;
    case 'chunkCount':
      return row.chunkCount;
  }
}

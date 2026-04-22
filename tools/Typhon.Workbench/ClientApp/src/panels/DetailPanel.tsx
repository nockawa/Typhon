import { FolderOpen } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSelectedResourceStore } from '@/stores/useSelectedResourceStore';

export default function DetailPanel() {
 const selected = useSelectedResourceStore((s) => s.selected);

 if (!selected) {
 return (
 <div className="flex h-full items-center justify-center bg-background">
 <p className="text-density-sm text-muted-foreground">
 Select a resource to see details
 </p>
 </div>
 );
 }

 const { raw } = selected;
 const childrenCount = raw.children?.length ?? 0;
 const entityCount = raw.entityCount != null ? Number(raw.entityCount) : null;

 return (
 <div className="flex h-full flex-col bg-background p-3">
 <div className="rounded-md border border-border bg-card p-3 text-[12px]">
 <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
 <FolderOpen className="h-4 w-4 text-muted-foreground" />
 <h3 className="text-[13px] font-semibold text-foreground">{selected.name}</h3>
 <span className="ml-auto text-[11px] text-muted-foreground">{selected.kind}</span>
 </div>

 <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
 <dt className="text-muted-foreground">Id</dt>
 <dd className="truncate text-foreground">{raw.id ?? selected.resourceId}</dd>

 <dt className="text-muted-foreground">Path</dt>
 <dd className="truncate text-foreground">{selected.path.join(' / ')}</dd>

 <dt className="text-muted-foreground">Kind</dt>
 <dd className="text-foreground">{selected.kind}</dd>

 {entityCount != null && (
 <>
 <dt className="text-muted-foreground">Entities</dt>
 <dd className="text-foreground">{entityCount.toLocaleString()}</dd>
 </>
 )}

 <dt className="text-muted-foreground">Children</dt>
 <dd className="text-foreground">{childrenCount}</dd>
 </dl>

 <div className="mt-3 flex flex-wrap gap-2 border-t border-border pt-2">
 <Button disabled size="sm" variant="outline" title="Coming in a later phase">
 Open in Query
 </Button>
 <Button disabled size="sm" variant="outline" title="Coming in a later phase">
 Open in Entities
 </Button>
 <Button disabled size="sm" variant="outline" title="Coming in a later phase">
 Open in Schema
 </Button>
 </div>
 </div>
 </div>
 );
}

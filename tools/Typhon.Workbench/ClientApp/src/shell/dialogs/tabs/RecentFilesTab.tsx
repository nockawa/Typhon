import { Database, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useRecentFilesStore, type RecentFileState } from '@/stores/useRecentFilesStore';

interface Props {
 onOpen: (filePath: string, schemaDllPaths: string[]) => void;
}

const stateStyles: Record<RecentFileState, string> = {
 Ready: 'bg-green-500/15 text-green-600 dark:text-green-400',
 MigrationRequired: 'bg-amber-500/15 text-amber-700 dark:text-amber-300',
 Incompatible: 'bg-destructive/15 text-destructive',
};

export default function RecentFilesTab({ onOpen }: Props) {
 const entries = useRecentFilesStore((s) => s.entries);
 const remove = useRecentFilesStore((s) => s.remove);

 if (entries.length === 0) {
 return (
 <div className="flex h-full items-center justify-center text-density-sm text-muted-foreground">
 No recent files. Open a database from the <b className="px-1">Open File</b> tab.
 </div>
 );
 }

 return (
 <div className="flex h-full flex-col gap-1 overflow-auto p-1">
 {entries.map((e) => {
 const name = e.filePath.split(/[\\/]/).pop() ?? e.filePath;
 return (
 <div
 key={e.filePath}
 className="group flex items-center gap-2 rounded border border-transparent px-2 py-1
 hover:border-border hover:bg-muted"
 >
 <Database className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
 <button
 onClick={() => onOpen(e.filePath, e.schemaDllPaths)}
 className="min-w-0 flex-1 text-left"
 >
 <div className="flex items-baseline gap-2 ">
 <span className="truncate text-density-sm font-semibold">{name}</span>
 <span
 className={`shrink-0 rounded px-1 text-[10px] uppercase ${stateStyles[e.lastState]}`}
 >
 {e.lastState}
 </span>
 </div>
 <div className="truncate text-[10px] text-muted-foreground">
 {e.filePath}
 </div>
 </button>
 <Button
 variant="ghost"
 size="sm"
 className="h-6 w-6 shrink-0 p-0 opacity-0 group-hover:opacity-100"
 onClick={() => remove(e.filePath)}
 aria-label="Remove from recents"
 title="Remove from recents"
 >
 <Trash2 className="h-3 w-3" />
 </Button>
 </div>
 );
 })}
 </div>
 );
}

import { useEffect, useMemo, useRef, useState } from 'react';
import { ChevronUp, FileText, Folder } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { useGetApiFsHome, useGetApiFsList } from '@/api/generated/files/files';
import type { FileEntryDto } from '@/api/generated/model';

export interface FileBrowserProps {
 /**
 * Extension filter — include only files whose name ends with any of these (case-insensitive).
 * Directories are always shown. Example: ['.typhon'] or ['.schema.dll'].
 */
 extensionFilter?: string[];
 multiSelect?: boolean;
 /** Initial directory; falls back to the server's $HOME. */
 initialPath?: string;
 /** Called whenever the selection changes. Paths are full absolute paths. */
 onSelectionChange?: (paths: string[]) => void;
 /** Double-click / Enter on a file — used by single-select flows ("Open" on Enter). */
 onActivate?: (path: string) => void;
}

export default function FileBrowser({
 extensionFilter,
 multiSelect = false,
 initialPath,
 onSelectionChange,
 onActivate,
}: FileBrowserProps) {
 const homeQuery = useGetApiFsHome({
 query: { enabled: !initialPath, staleTime: Infinity },
 });
 const [path, setPath] = useState<string | null>(initialPath ?? null);

 // When home loads (no initialPath), seed the path.
 useEffect(() => {
 if (!path && homeQuery.data) {
 setPath(homeQuery.data.data.fullPath ?? null);
 }
 }, [path, homeQuery.data]);

 const listing = useGetApiFsList(
 { path: path ?? '' },
 { query: { enabled: !!path } },
 );

 const [selected, setSelected] = useState<Set<string>>(new Set());
 const [activeIndex, setActiveIndex] = useState(0);
 const listRef = useRef<HTMLDivElement>(null);

 const entries = useMemo(() => {
 const raw = listing.data?.data?.entries ?? [];
 if (!extensionFilter || extensionFilter.length === 0) return raw;
 return raw.filter((e: FileEntryDto) => {
 if (e.kind === 'dir') return true;
 const name = (e.name ?? '').toLowerCase();
 return extensionFilter.some((ext) => name.endsWith(ext.toLowerCase()));
 });
 }, [listing.data, extensionFilter]);

 useEffect(() => {
 onSelectionChange?.(Array.from(selected));
 }, [selected, onSelectionChange]);

 // Reset selection + highlight when directory changes
 useEffect(() => {
 setSelected(new Set());
 setActiveIndex(0);
 }, [path]);

 const descend = (entry: FileEntryDto) => {
 if (entry.kind === 'dir') {
 setPath(entry.fullPath ?? null);
 } else if (entry.fullPath) {
 if (multiSelect) {
 setSelected((prev) => {
 const next = new Set(prev);
 if (next.has(entry.fullPath as string)) next.delete(entry.fullPath as string);
 else next.add(entry.fullPath as string);
 return next;
 });
 } else {
 setSelected(new Set([entry.fullPath as string]));
 onActivate?.(entry.fullPath as string);
 }
 }
 };

 const ascend = () => {
 const parent = listing.data?.data?.parent;
 if (parent) setPath(parent);
 };

 const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
 if (entries.length === 0) return;
 switch (e.key) {
 case 'ArrowDown':
 e.preventDefault();
 setActiveIndex((i) => Math.min(i + 1, entries.length - 1));
 break;
 case 'ArrowUp':
 e.preventDefault();
 setActiveIndex((i) => Math.max(i - 1, 0));
 break;
 case 'Home':
 e.preventDefault();
 setActiveIndex(0);
 break;
 case 'End':
 e.preventDefault();
 setActiveIndex(entries.length - 1);
 break;
 case 'Enter':
 e.preventDefault();
 descend(entries[activeIndex]);
 break;
 case 'Backspace':
 e.preventDefault();
 ascend();
 break;
 }
 };

 return (
 <div className="flex h-full flex-col overflow-hidden rounded border border-border bg-background">
 {/* Breadcrumb / path input */}
 <div className="flex shrink-0 items-center gap-1 border-b border-border px-2 py-1">
 <button
 className="rounded p-1 text-muted-foreground hover:bg-muted disabled:opacity-40"
 onClick={ascend}
 disabled={!listing.data?.data?.parent}
 title="Up"
 >
 <ChevronUp className="h-3.5 w-3.5" />
 </button>
 <Input
 value={path ?? ''}
 onChange={(e) => setPath(e.target.value)}
 onKeyDown={(e) => {
 if (e.key === 'Enter') {
 e.preventDefault();
 // Force re-fetch by letting useGetApiFsList react — it already does.
 }
 }}
 className="h-6 border-0 bg-transparent text-[11px] shadow-none focus-visible:ring-0 focus-visible:ring-offset-0"
 placeholder="Path…"
 />
 </div>

 {/* List */}
 <div
 ref={listRef}
 tabIndex={0}
 onKeyDown={onKeyDown}
 className="min-h-0 flex-1 overflow-auto focus:outline-none"
 role="listbox"
 aria-label="Files"
 >
 {listing.isLoading && (
 <p className="px-3 py-2 text-[11px] text-muted-foreground">Loading…</p>
 )}
 {listing.isError && (
 <p className="px-3 py-2 text-[11px] text-destructive">Failed to read directory</p>
 )}
 {!listing.isLoading && entries.length === 0 && listing.data && (
 <p className="px-3 py-2 text-[11px] text-muted-foreground">Empty</p>
 )}
 {entries.map((e: FileEntryDto, i: number) => {
 const isActive = i === activeIndex;
 const isSelected = e.fullPath && selected.has(e.fullPath as string);
 const Icon = e.kind === 'dir' ? Folder : FileText;
 return (
 <div
 key={e.fullPath ?? `${i}-${e.name}`}
 role="option"
 aria-selected={isSelected || undefined}
 onClick={() => {
 setActiveIndex(i);
 descend(e);
 }}
 onDoubleClick={() => descend(e)}
 className={`flex cursor-pointer items-center gap-1.5 px-2 py-0.5 text-[11px] leading-relaxed
 ${isActive ? 'bg-muted' : ''}
 ${isSelected ? 'border-l-2 border-accent bg-muted text-foreground' : 'text-foreground hover:bg-muted/60'}`}
 >
 <Icon className="h-3 w-3 shrink-0 text-muted-foreground" />
 <span className="min-w-0 flex-1 truncate">{e.name}</span>
 {e.isSchemaDll && (
 <span className="ml-auto shrink-0 rounded bg-accent/20 px-1 text-[10px] uppercase text-accent">
 schema
 </span>
 )}
 </div>
 );
 })}
 </div>
 </div>
 );
}

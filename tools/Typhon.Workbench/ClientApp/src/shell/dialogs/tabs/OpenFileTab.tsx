import { useMemo, useState } from 'react';
import { Button } from '@/components/ui/button';
import FileBrowser from '@/shell/components/FileBrowser';
import SchemaDllPicker from '@/shell/components/SchemaDllPicker';

interface Props {
 onOpen: (filePath: string, schemaDllPaths: string[]) => void;
 isOpening?: boolean;
}

export default function OpenFileTab({ onOpen, isOpening }: Props) {
 const [filePath, setFilePath] = useState<string | null>(null);
 const [manualDlls, setManualDlls] = useState<string[] | null>(null);

 const initialDir = useMemo(() => {
 if (!filePath) return undefined;
 const lastSep = Math.max(filePath.lastIndexOf('/'), filePath.lastIndexOf('\\'));
 return lastSep >= 0 ? filePath.slice(0, lastSep) : undefined;
 }, [filePath]);

 const canOpen = !!filePath && !isOpening;
 const dllsForOpen = manualDlls ?? [];

 return (
 <div className="flex h-full flex-col gap-3">
 <div className="grid min-h-0 flex-1 grid-cols-2 gap-3">
 <div className="flex min-h-0 flex-col gap-1">
 <label className="shrink-0 text-density-sm text-muted-foreground">
 Database file
 </label>
 <div className="min-h-0 flex-1">
 <FileBrowser
 extensionFilter={['.typhon']}
 onSelectionChange={(paths) => setFilePath(paths[0] ?? null)}
 onActivate={(p) => setFilePath(p)}
 />
 </div>
 {filePath && (
 <p className="shrink-0 truncate text-[10px] text-muted-foreground" title={filePath}>
 Selected: <span className="text-foreground">{filePath}</span>
 </p>
 )}
 </div>

 <div className="flex min-h-0 flex-col">
 <SchemaDllPicker
 paths={dllsForOpen}
 onChange={setManualDlls}
 initialPath={initialDir}
 onAutoDetect={() => setManualDlls(null)}
 />
 {manualDlls === null && filePath && (
 <p className="mt-1 shrink-0 text-[10px] text-muted-foreground">
 Convention: server will auto-discover <code>*.schema.dll</code> next to the file.
 </p>
 )}
 </div>
 </div>

 <div className="flex shrink-0 justify-end gap-2">
 <Button
 onClick={() => filePath && onOpen(filePath, manualDlls ?? [])}
 disabled={!canOpen}
 className="text-density-sm"
 >
 {isOpening ? 'Opening…' : 'Open'}
 </Button>
 </div>
 </div>
 );
}

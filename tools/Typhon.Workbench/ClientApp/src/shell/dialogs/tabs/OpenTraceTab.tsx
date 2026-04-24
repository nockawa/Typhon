import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import FileBrowser from '@/shell/components/FileBrowser';

interface Props {
  onOpen: (filePath: string) => void;
  isOpening?: boolean;
}

/**
 * Open-Trace dialog tab — mirrors OpenFileTab but filtered to `.typhon-trace` files. Feeds the
 * path into ConnectDialog's `handleOpenTrace` callback, which POSTs to `/api/sessions/trace`.
 */
export default function OpenTraceTab({ onOpen, isOpening }: Props) {
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [pastedPath, setPastedPath] = useState<string>('');

  // Strip surrounding double quotes — Windows Explorer's "Copy as path" wraps the path in them.
  const pastedTrimmed = pastedPath.trim().replace(/^"(.*)"$/, '$1');
  const effectivePath = pastedTrimmed.length > 0 ? pastedTrimmed : selectedPath;
  const canOpen = !!effectivePath && !isOpening;

  return (
    <div className="flex h-full flex-col gap-3">
      <div className="flex min-h-0 flex-col gap-1">
        <label className="shrink-0 text-density-sm text-muted-foreground">Trace file</label>
        <div className="min-h-0 flex-1">
          <FileBrowser
            extensionFilter={['.typhon-trace']}
            onSelectionChange={(paths) => setSelectedPath(paths[0] ?? null)}
            onActivate={(p) => setSelectedPath(p)}
          />
        </div>
      </div>

      <div className="flex shrink-0 flex-col gap-1">
        <label className="text-density-sm text-muted-foreground">Or paste absolute path</label>
        <Input
          placeholder="C:\path\to\trace.typhon-trace"
          value={pastedPath}
          onChange={(e) => setPastedPath(e.target.value)}
          spellCheck={false}
          autoComplete="off"
          className="font-mono text-[12px]"
        />
      </div>

      {effectivePath && (
        <p className="shrink-0 truncate text-[10px] text-muted-foreground" title={effectivePath}>
          Will open: <span className="font-mono text-foreground">{effectivePath}</span>
        </p>
      )}

      <p className="shrink-0 text-[10px] text-muted-foreground">
        Sidecar cache is built on first open and reused on subsequent opens of the same file.
      </p>

      <div className="flex shrink-0 justify-end gap-2">
        <Button
          onClick={() => effectivePath && onOpen(effectivePath)}
          disabled={!canOpen}
          className="text-density-sm"
        >
          {isOpening ? 'Opening…' : 'Open'}
        </Button>
      </div>
    </div>
  );
}

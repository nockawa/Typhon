import type { ReactNode } from 'react';
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';

interface Props {
  resourceId: string;          // synthetic uid — used for pin storage (unique)
  naturalId: string;           // engine-native id (display / copy)
  name: string;
  path: string[];
  onReveal: () => void;        // clears filter + scrolls to row
  onRefreshSubtree: () => void;
  children: ReactNode;
}

export default function ResourceTreeContextMenu({
  resourceId,
  naturalId,
  name,
  path,
  onReveal,
  onRefreshSubtree,
  children,
}: Props) {
  const filePath = useSessionStore((s) => s.filePath);
  const pins = useRecentFilesStore((s) => (filePath ? s.getPins(filePath) : []));
  const pinResource = useRecentFilesStore((s) => s.pinResource);
  const unpinResource = useRecentFilesStore((s) => s.unpinResource);
  const clearFilter = useResourceGraphStore((s) => s.setFilter);

  const isPinned = pins.includes(resourceId);
  const pathStr = path.join('/');

  async function copyToClipboard(text: string) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Non-secure contexts / clipboard API unavailable — silently fail for now.
    }
  }

  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="w-56">
        <ContextMenuItem
          onSelect={() => {
            if (!filePath) return;
            if (isPinned) unpinResource(filePath, resourceId);
            else pinResource(filePath, resourceId);
          }}
          disabled={!filePath}
        >
          {isPinned ? 'Unpin' : 'Pin'}
        </ContextMenuItem>
        <ContextMenuItem onSelect={() => copyToClipboard(pathStr)}>
          Copy Path
        </ContextMenuItem>
        <ContextMenuItem
          onSelect={() => copyToClipboard(`{{ref:${naturalId}}}`)}
          title="DSL format finalized with Query Console"
        >
          Copy as DSL Reference
        </ContextMenuItem>
        <ContextMenuItem
          onSelect={() => {
            clearFilter('');
            onReveal();
          }}
        >
          Reveal in Tree
        </ContextMenuItem>
        <ContextMenuItem onSelect={onRefreshSubtree}>
          Refresh Subtree
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem disabled>Open in Schema Inspector</ContextMenuItem>
        <ContextMenuItem disabled>Open in Data Browser</ContextMenuItem>
        <ContextMenuItem disabled>Open in Query Console</ContextMenuItem>
        <ContextMenuItem disabled>Open in Profiler</ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem disabled className="text-muted-foreground">
          {name}
        </ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>
  );
}

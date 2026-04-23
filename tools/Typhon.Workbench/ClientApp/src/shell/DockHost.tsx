import { useEffect, useRef } from 'react';
import { DockviewReact, type DockviewApi, type DockviewReadyEvent, type IDockviewPanelProps } from 'dockview-react';
import { useDockLayoutStore } from '@/stores/useDockLayoutStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useTreeVisibilityStore } from '@/stores/useTreeVisibilityStore';
import DetailPanel from '@/panels/DetailPanel';
import LogsPanel from '@/panels/LogsPanel';
import ResourceTreePanel from '@/panels/ResourceTreePanel';
import PlaceholderStartHere from '@/panels/PlaceholderStartHere';
import SchemaBrowserPanel from '@/panels/SchemaBrowser/SchemaBrowserPanel';
import ArchetypeBrowserPanel from '@/panels/SchemaBrowser/ArchetypeBrowserPanel';
import SchemaLayoutPanel from '@/panels/SchemaInspector/SchemaLayoutPanel';
import SchemaArchetypePanel from '@/panels/SchemaInspector/SchemaArchetypePanel';
import SchemaIndexPanel from '@/panels/SchemaInspector/SchemaIndexPanel';
import SchemaRelationshipsPanel from '@/panels/SchemaInspector/SchemaRelationshipsPanel';
import { registerDockApi } from './commands/openSchemaBrowser';
import MigrationRequiredBanner from './banners/MigrationRequiredBanner';
import IncompatibleBanner from './banners/IncompatibleBanner';

const LAYOUT_KEY_DEFAULT = 'default';
const SAVE_DEBOUNCE_MS = 1_500;

const components: Record<string, React.FC<IDockviewPanelProps>> = {
  ResourceTree: ResourceTreePanel,
  StartHere: PlaceholderStartHere,
  Detail: DetailPanel,
  Logs: LogsPanel,
  SchemaBrowser: SchemaBrowserPanel,
  ArchetypeBrowser: ArchetypeBrowserPanel,
  SchemaLayout: SchemaLayoutPanel,
  SchemaArchetypes: SchemaArchetypePanel,
  SchemaIndexes: SchemaIndexPanel,
  SchemaRelationships: SchemaRelationshipsPanel,
};

function buildDefaultLayout(api: DockviewReadyEvent['api']) {
  const tree = api.addPanel({
    id: 'resource-tree',
    component: 'ResourceTree',
    title: 'Resources',
    position: { direction: 'left' },
  });
  tree.api.setSize({ width: 260 });

  api.addPanel({
    id: 'start-here',
    component: 'StartHere',
    title: 'Start Here',
    position: { direction: 'right', referencePanel: tree },
  });

  api.addPanel({
    id: 'detail',
    component: 'Detail',
    title: 'Detail',
    position: { direction: 'right' },
  });

  api.addPanel({
    id: 'logs',
    component: 'Logs',
    title: 'Logs',
    position: { direction: 'below' },
  });
}

export default function DockHost() {
  const filePath = useSessionStore((s) => s.filePath);
  const sessionState = useSessionStore((s) => s.sessionState);
  const layoutKey = filePath ?? LAYOUT_KEY_DEFAULT;
  const getLayout = useDockLayoutStore((s) => s.get);
  const saveLayout = useDockLayoutStore((s) => s.save);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const apiRef = useRef<DockviewApi | null>(null);
  const treeVisible = useTreeVisibilityStore((s) => s.visible);

  // Ctrl+/ toggles the Resource Tree panel. Dockview v5.2 has no setVisible/hide or edge-group
  // collapse primitives yet (the edgeGroups docs on master aren't in a released version), so we
  // remove on hide and re-add on restore. Panel state re-hydrates from Zustand on remount, so
  // selection/filter/pins survive the round-trip.
  const savedWidthRef = useRef<number>(260);
  useEffect(() => {
    const api = apiRef.current;
    if (!api) return;
    const panel = api.getPanel('resource-tree');
    if (!treeVisible) {
      if (panel) {
        const w = panel.group?.api.width;
        if (w && w > 10) savedWidthRef.current = w;
        api.removePanel(panel);
      }
    } else if (!panel) {
      const restored = api.addPanel({
        id: 'resource-tree',
        component: 'ResourceTree',
        title: 'Resources',
        position: { direction: 'left' },
      });
      restored.api.setSize({ width: savedWidthRef.current });
    }
  }, [treeVisible]);

  function onReady(event: DockviewReadyEvent) {
    apiRef.current = event.api;
    registerDockApi(event.api);
    const saved = getLayout(layoutKey);
    if (saved) {
      try {
        event.api.fromJSON(saved as Parameters<typeof event.api.fromJSON>[0]);
      } catch {
        // Saved layout invalid (version skew) — fall through to default
        buildDefaultLayout(event.api);
      }
    } else {
      buildDefaultLayout(event.api);
    }

    event.api.onDidLayoutChange(() => {
      clearTimeout(debounceRef.current);
      debounceRef.current = setTimeout(() => {
        saveLayout(layoutKey, event.api.toJSON());
      }, SAVE_DEBOUNCE_MS);
    });
  }

  const showMigration = sessionState === 'MigrationRequired';
  const showIncompatible = sessionState === 'Incompatible';

  return (
    <div className="flex h-full flex-col">
      {showMigration && <MigrationRequiredBanner />}
      {showIncompatible && <IncompatibleBanner />}
      <div className="relative min-h-0 flex-1">
        <DockviewReact
          className="h-full w-full dockview-theme-dark"
          components={components}
          onReady={onReady}
        />
        {showIncompatible && (
          <div
            className="pointer-events-auto absolute inset-0 cursor-not-allowed bg-background/40"
            aria-hidden="true"
          />
        )}
      </div>
    </div>
  );
}

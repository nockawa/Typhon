import type { DockviewApi } from 'dockview-react';

/**
 * Module-level dockview api registration — same pattern as refreshResourceGraph. DockHost publishes
 * its api on ready so palette commands and menu items can trigger panel opens without prop drilling.
 * If the api isn't registered yet (pre-mount), the command is a no-op.
 */
let registeredApi: DockviewApi | null = null;

export function registerDockApi(api: DockviewApi | null): void {
  registeredApi = api;
}

export function openSchemaBrowser(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('schema-browser');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({
    id: 'schema-browser',
    component: 'SchemaBrowser',
    title: 'Component Browser',
  });
}

export function openArchetypeBrowser(): void {
  openDockPanel('archetype-browser', 'ArchetypeBrowser', 'Archetype Browser');
}

export function openSchemaLayout(): void {
  openDockPanel('schema-layout', 'SchemaLayout', 'Component Layout');
}

export function openSchemaArchetypes(): void {
  openDockPanel('schema-archetypes', 'SchemaArchetypes', 'Component Archetypes');
}

export function openSchemaIndexes(): void {
  openDockPanel('schema-indexes', 'SchemaIndexes', 'Component Indexes');
}

export function openSchemaRelationships(): void {
  openDockPanel('schema-relationships', 'SchemaRelationships', 'Component Relationships');
}

/**
 * Open (or focus) the Detail panel — useful when the user closed it and wants it back. In
 * trace/attach sessions the default layout dock this to the right of the Profiler; in open
 * sessions it's already in the default layout, so this is mostly for "I closed it, bring it back".
 */
export function openDetailPanel(): void {
  openDockPanel('detail', 'Detail', 'Detail');
}

/**
 * Open (or focus) the Workbench Options panel — issue #293, Phase 4a. Available from anywhere via
 * the command palette. The panel is dockview-registered like every other tool window so the user
 * can keep it open alongside the profiler view.
 */
export function openOptionsPanel(): void {
  openDockPanel('options', 'Options', 'Options');
}

/**
 * Open the inline source-preview panel for a given file:line (issue #293, Phase 5). Each invocation
 * reuses one panel id so opening a second source from the Source row replaces the contents instead
 * of stacking panels.
 */
export function openSourcePreview(path: string, line: number): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('source-preview');
  if (existing) {
    existing.api.updateParameters({ path, line });
    existing.focus();
    return;
  }
  api.addPanel({
    id: 'source-preview',
    component: 'SourcePreview',
    title: 'Source Preview',
    params: { path, line },
  });
}

function openDockPanel(id: string, componentKey: string, title: string): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel(id);
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({ id, component: componentKey, title });
}

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

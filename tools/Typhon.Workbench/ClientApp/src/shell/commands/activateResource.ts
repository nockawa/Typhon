import { useSelectedResourceStore, type SelectedResource } from '@/stores/useSelectedResourceStore';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import type { ResourceHit } from '@/libs/ResourceIndex';

export function activateResource(hit: ResourceHit): void {
  // Short-circuit if this is already the active selection — otherwise react-arborist's
  // controlled `selection` prop round-trips back through onSelect and we'd push a duplicate
  // entry every time the user clicks the same row, polluting back/forward history.
  const current = useSelectedResourceStore.getState().selected;
  if (current?.resourceId === hit.id) return;

  const selected: SelectedResource = {
    resourceId: hit.id,
    kind: hit.kind,
    name: hit.name,
    path: hit.path,
    raw: hit.raw,
  };
  useSelectedResourceStore.getState().setSelected(selected);
  useNavHistoryStore.getState().push({
    kind: 'resource-selected',
    resourceId: hit.id,
    selected,
    timestamp: Date.now(),
  });
}

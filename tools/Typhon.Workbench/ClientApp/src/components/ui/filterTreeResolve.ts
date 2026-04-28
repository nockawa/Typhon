/**
 * Pure derivation logic for the {@link FilterTree} component. Lives in its own file so that the
 * `.tsx` component module satisfies React Fast Refresh's "components-only export" rule.
 *
 * The {@link resolveFilterNode} function takes a tree of {@link FilterNode}s and a search string,
 * and produces a parallel tree of {@link ResolvedFilterNode}s carrying:
 *   - the per-node tri-state (`checked` / `unchecked` / `indeterminate`) derived bottom-up,
 *   - the search-match flag (a leaf matches if its label contains the search; a group matches if
 *     any descendant matches OR its own label matches),
 *   - for groups, a flattened list of descendant leaf ids — used to dispatch a single batch toggle
 *     when the user clicks a parent's checkbox.
 */

export type FilterNodeState = 'checked' | 'unchecked' | 'indeterminate';

export interface FilterLeaf {
  kind: 'leaf';
  id: string;
  label: string;
  checked: boolean;
  /**
   * True when this leaf maps to a section that has a meaningful "double-expand" state (gauges in
   * the profiler UI). Hides the third collapse-action button on rows that wouldn't react to it —
   * UI-side promise that we don't render dead controls. Default `false`.
   */
  supportsDouble?: boolean;
}

export interface FilterGroup {
  kind: 'group';
  id: string;
  label: string;
  children: FilterNode[];
}

export type FilterNode = FilterLeaf | FilterGroup;

export interface ResolvedFilterNode {
  id: string;
  label: string;
  state: FilterNodeState;
  matches: boolean;
  /**
   * `true` when this row's "double-expand" action would affect at least one descendant. Leaf:
   * inherits from {@link FilterLeaf.supportsDouble}. Group: OR of every descendant.
   */
  supportsDouble: boolean;
  /** Leaf-only: id passed back to onToggleLeaves. */
  leafId?: string;
  /** Group-only: descendant leaf ids (recursively flattened) for batch toggle. */
  descendantLeafIds?: string[];
  children?: ResolvedFilterNode[];
}

export function resolveFilterNode(node: FilterNode, searchText: string): ResolvedFilterNode {
  const search = searchText.toLowerCase();
  if (node.kind === 'leaf') {
    return {
      id: node.id,
      label: node.label,
      state: node.checked ? 'checked' : 'unchecked',
      matches: search === '' || node.label.toLowerCase().includes(search),
      supportsDouble: node.supportsDouble === true,
      leafId: node.id,
    };
  }
  // Group — resolve children, derive aggregate state, and decide visibility from the search filter.
  const childResolved = node.children.map((c) => resolveFilterNode(c, searchText));
  let checked = 0;
  let unchecked = 0;
  let anyDouble = false;
  const descendantLeafIds: string[] = [];
  const collect = (rs: ResolvedFilterNode[]): void => {
    for (const r of rs) {
      if (r.supportsDouble) anyDouble = true;
      if (r.leafId !== undefined) {
        if (r.state === 'checked') checked++;
        else unchecked++;
        descendantLeafIds.push(r.leafId);
      } else if (r.children) {
        collect(r.children);
      }
    }
  };
  collect(childResolved);
  const total = checked + unchecked;
  const state: FilterNodeState = total === 0 ? 'unchecked' : checked === total ? 'checked' : checked === 0 ? 'unchecked' : 'indeterminate';
  // A group matches the search if any descendant leaf does. Empty search → always matches.
  const groupLabelMatches = search === '' || node.label.toLowerCase().includes(search);
  const anyChildMatches = childResolved.some((c) => c.matches);
  const matches = groupLabelMatches || anyChildMatches;
  return {
    id: node.id,
    label: node.label,
    state,
    matches,
    supportsDouble: anyDouble,
    descendantLeafIds,
    children: childResolved,
  };
}

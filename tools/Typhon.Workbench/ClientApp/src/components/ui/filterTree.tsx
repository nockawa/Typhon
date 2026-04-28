import * as React from 'react';
import { CheckSquare, ChevronDown, ChevronRight, Equal, Menu, Minus, MinusSquare, Square } from 'lucide-react';
import type { TrackState } from '@/libs/profiler/model/uiTypes';
import { cn } from '@/lib/utils';
import { resolveFilterNode, type FilterNode, type ResolvedFilterNode } from './filterTreeResolve';
export type { FilterLeaf, FilterGroup, FilterNode, FilterNodeState, ResolvedFilterNode } from './filterTreeResolve';

/**
 * Tri-state hierarchical filter tree. Pure presentational component — caller owns the state and the
 * `onToggle` callback dispatches into a store. Used by the TimeArea section-filter popup, but generic
 * enough to be reused for any "leaf bool + parent tri-state" UI.
 *
 * **State derivation** (in {@link resolveFilterNode}, sibling .ts file): parent state is computed
 * from descendants — all-`checked` → `checked`, all-`unchecked` → `unchecked`, otherwise
 * `indeterminate`.
 *
 * **Toggling rules:**
 *   - Leaf: flips checked ↔ unchecked.
 *   - Parent: if currently `checked`, sets every descendant leaf to `unchecked`; otherwise
 *     (`indeterminate` or `unchecked`) sets every descendant leaf to `checked`.
 *
 * **Search filter:** a leaf matches if its label contains `searchText` (case-insensitive). A parent
 * is shown if any descendant matches; non-matching subtrees are hidden entirely.
 */
export interface FilterTreeProps {
  nodes: FilterNode[];
  /** Substring filter; case-insensitive. Empty string = show everything. */
  searchText: string;
  /**
   * Caller-provided toggle handler. Receives the IDs of every leaf affected by the click and the new
   * state. For a leaf click that's a single ID; for a group click it's every descendant leaf id.
   */
  onToggleLeaves: (leafIds: string[], next: 'checked' | 'unchecked') => void;
  /**
   * Optional collapse-state dispatcher. When provided, every row gains three small icon buttons —
   * collapse-to-summary / expand / double-expand — that batch-dispatch the new state to all leaves
   * the row covers (one leaf id for a leaf row, every descendant leaf id for a group row). The
   * caller routes the leaf ids to the appropriate store setter (gauges vs. non-gauges). The buttons
   * are hidden if this prop is undefined.
   */
  onSetCollapseState?: (leafIds: string[], state: TrackState) => void;
}

const ROW_HEIGHT_PX = 22;
const INDENT_PX = 16;

interface RowProps {
  node: ResolvedFilterNode;
  depth: number;
  searchText: string;
  expanded: Set<string>;
  setExpanded: React.Dispatch<React.SetStateAction<Set<string>>>;
  onToggleLeaves: FilterTreeProps['onToggleLeaves'];
  onSetCollapseState: FilterTreeProps['onSetCollapseState'];
}

function Row({ node, depth, searchText, expanded, setExpanded, onToggleLeaves, onSetCollapseState }: RowProps): React.JSX.Element | null {
  if (!node.matches) return null;
  const isLeaf = node.leafId !== undefined;
  // While searching, auto-expand groups so matching descendants are visible without manual clicks.
  const isExpanded = !isLeaf && (searchText !== '' || expanded.has(node.id));
  // Leaf-row affects one leaf; group-row affects every descendant leaf.
  const affectedLeafIds = isLeaf ? [node.leafId!] : (node.descendantLeafIds ?? []);
  const onToggleCheckbox = (): void => {
    if (affectedLeafIds.length === 0) return;
    onToggleLeaves(affectedLeafIds, node.state === 'checked' ? 'unchecked' : 'checked');
  };
  const toggleExpand = (): void => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(node.id)) next.delete(node.id);
      else next.add(node.id);
      return next;
    });
  };
  const dispatchCollapse = (e: React.MouseEvent, state: TrackState): void => {
    e.stopPropagation();
    if (!onSetCollapseState || affectedLeafIds.length === 0) return;
    onSetCollapseState(affectedLeafIds, state);
  };
  const Icon = node.state === 'checked' ? CheckSquare : node.state === 'indeterminate' ? MinusSquare : Square;
  return (
    <>
      <div
        className="flex items-center gap-1 select-none cursor-pointer hover:bg-accent/40 rounded-sm px-1"
        style={{ paddingLeft: depth * INDENT_PX + 4, height: ROW_HEIGHT_PX }}
      >
        {!isLeaf ? (
          <button
            type="button"
            className="flex items-center justify-center w-4 h-4 text-muted-foreground hover:text-foreground"
            onClick={toggleExpand}
            aria-label={isExpanded ? 'Collapse' : 'Expand'}
          >
            {isExpanded ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
          </button>
        ) : (
          <span className="inline-block w-4" />
        )}
        <button
          type="button"
          className="flex items-center justify-center w-4 h-4"
          onClick={onToggleCheckbox}
          aria-label={node.state === 'checked' ? `Uncheck ${node.label}` : `Check ${node.label}`}
        >
          <Icon className={cn('h-3.5 w-3.5', node.state === 'unchecked' ? 'text-muted-foreground' : 'text-foreground')} />
        </button>
        <span className="text-sm leading-none truncate flex-1 min-w-0" title={node.label} onClick={onToggleCheckbox}>
          {node.label}
        </span>
        {/*
         * Collapse-action trio. Three icon buttons that batch-dispatch a TrackState to every leaf the
         * row covers. The caller (TimeAreaFilterButton) routes by leaf-id prefix to either the
         * gauge-collapse store (which actually supports `'double'`) or the non-gauge collapse store
         * (which clamps `'double'` to `'expanded'`). Icon progression — 1 line / 2 lines / 3 lines —
         * is read as compact / medium / large in the line-count sense, mapping to summary / expanded
         * / double. Hidden entirely when no handler is wired (e.g. unit tests).
         */}
        {onSetCollapseState ? (
          <div className="flex items-center gap-0.5 ml-1 shrink-0">
            <button
              type="button"
              className="flex items-center justify-center w-4 h-4 rounded-sm text-muted-foreground hover:text-foreground hover:bg-accent/60"
              onClick={(e) => dispatchCollapse(e, 'summary')}
              title="Collapse to summary"
              aria-label={`Collapse ${node.label}`}
            >
              <Minus className="h-3 w-3" />
            </button>
            <button
              type="button"
              className="flex items-center justify-center w-4 h-4 rounded-sm text-muted-foreground hover:text-foreground hover:bg-accent/60"
              onClick={(e) => dispatchCollapse(e, 'expanded')}
              title="Expand"
              aria-label={`Expand ${node.label}`}
            >
              <Equal className="h-3 w-3" />
            </button>
            {node.supportsDouble ? (
              <button
                type="button"
                className="flex items-center justify-center w-4 h-4 rounded-sm text-muted-foreground hover:text-foreground hover:bg-accent/60"
                onClick={(e) => dispatchCollapse(e, 'double')}
                title="Double-expand"
                aria-label={`Double-expand ${node.label}`}
              >
                <Menu className="h-3 w-3" />
              </button>
            ) : (
              // Reserve the slot so leaf rows that DO support double stay aligned with sibling rows
              // that don't (otherwise the Equal/Minus buttons would shift left when one neighbour
              // hides its third button).
              <span className="inline-block w-4" aria-hidden />
            )}
          </div>
        ) : null}
      </div>
      {isExpanded && node.children
        ? node.children.map((c) => (
            <Row
              key={c.id}
              node={c}
              depth={depth + 1}
              searchText={searchText}
              expanded={expanded}
              setExpanded={setExpanded}
              onToggleLeaves={onToggleLeaves}
              onSetCollapseState={onSetCollapseState}
            />
          ))
        : null}
    </>
  );
}

export function FilterTree({ nodes, searchText, onToggleLeaves, onSetCollapseState }: FilterTreeProps): React.JSX.Element {
  const [expanded, setExpanded] = React.useState<Set<string>>(() => new Set(nodes.filter((n) => n.kind === 'group').map((n) => n.id)));
  const resolved = React.useMemo(() => nodes.map((n) => resolveFilterNode(n, searchText)), [nodes, searchText]);
  return (
    <div className="flex flex-col">
      {resolved.map((n) => (
        <Row
          key={n.id}
          node={n}
          depth={0}
          searchText={searchText}
          expanded={expanded}
          setExpanded={setExpanded}
          onToggleLeaves={onToggleLeaves}
          onSetCollapseState={onSetCollapseState}
        />
      ))}
    </div>
  );
}

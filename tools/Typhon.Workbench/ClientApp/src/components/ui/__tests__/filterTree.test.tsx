import { describe, expect, it } from 'vitest';
import { resolveFilterNode, type FilterNode } from '@/components/ui/filterTreeResolve';

/**
 * Pure-logic tests for `resolveFilterNode` — the function the FilterTree component uses to derive
 * each node's tri-state and search-match flag. Covers:
 *  - leaf state derivation (checked → 'checked', !checked → 'unchecked')
 *  - parent state from descendants (all-checked / mixed → indeterminate / all-unchecked)
 *  - descendant-leaf-id flattening on groups (used for batch toggle)
 *  - search filter visibility (matching / non-matching subtree)
 */

function tree(): FilterNode[] {
  return [
    {
      kind: 'group', id: 'g1', label: 'Group One',
      children: [
        { kind: 'leaf', id: 'a', label: 'Alpha', checked: true },
        { kind: 'leaf', id: 'b', label: 'Bravo', checked: true },
      ],
    },
    {
      kind: 'group', id: 'g2', label: 'Group Two',
      children: [
        { kind: 'leaf', id: 'c', label: 'Charlie', checked: false },
        { kind: 'leaf', id: 'd', label: 'Delta',   checked: true  },
      ],
    },
  ];
}

describe('resolveFilterNode — leaf state', () => {
  it('checked leaf → state checked', () => {
    const r = resolveFilterNode({ kind: 'leaf', id: 'a', label: 'A', checked: true }, '');
    expect(r.state).toBe('checked');
    expect(r.leafId).toBe('a');
  });

  it('unchecked leaf → state unchecked', () => {
    const r = resolveFilterNode({ kind: 'leaf', id: 'a', label: 'A', checked: false }, '');
    expect(r.state).toBe('unchecked');
  });
});

describe('resolveFilterNode — parent tri-state derivation', () => {
  it('group with all-checked descendants → checked', () => {
    const r = resolveFilterNode(tree()[0], '');  // Group One: Alpha + Bravo, both checked
    expect(r.state).toBe('checked');
    expect(r.descendantLeafIds).toEqual(['a', 'b']);
  });

  it('group with mixed descendants → indeterminate', () => {
    const r = resolveFilterNode(tree()[1], '');  // Group Two: Charlie unchecked, Delta checked
    expect(r.state).toBe('indeterminate');
    expect(r.descendantLeafIds).toEqual(['c', 'd']);
  });

  it('group with all-unchecked descendants → unchecked', () => {
    const allOff: FilterNode = {
      kind: 'group', id: 'off', label: 'Off',
      children: [
        { kind: 'leaf', id: 'x', label: 'X', checked: false },
        { kind: 'leaf', id: 'y', label: 'Y', checked: false },
      ],
    };
    const r = resolveFilterNode(allOff, '');
    expect(r.state).toBe('unchecked');
  });

  it('nested group flattens descendant leaf ids in DFS order', () => {
    const nested: FilterNode = {
      kind: 'group', id: 'top', label: 'Top',
      children: [
        {
          kind: 'group', id: 'mid', label: 'Mid',
          children: [
            { kind: 'leaf', id: 'l1', label: 'L1', checked: true  },
            { kind: 'leaf', id: 'l2', label: 'L2', checked: false },
          ],
        },
        { kind: 'leaf', id: 'l3', label: 'L3', checked: true },
      ],
    };
    const r = resolveFilterNode(nested, '');
    expect(r.descendantLeafIds).toEqual(['l1', 'l2', 'l3']);
    expect(r.state).toBe('indeterminate');  // 2 checked, 1 unchecked
  });
});

describe('resolveFilterNode — supportsDouble propagation', () => {
  it('leaf without supportsDouble → supportsDouble=false', () => {
    const r = resolveFilterNode({ kind: 'leaf', id: 'a', label: 'A', checked: true }, '');
    expect(r.supportsDouble).toBe(false);
  });

  it('leaf with supportsDouble=true → supportsDouble=true', () => {
    const r = resolveFilterNode({ kind: 'leaf', id: 'a', label: 'A', checked: true, supportsDouble: true }, '');
    expect(r.supportsDouble).toBe(true);
  });

  it('group with at least one supports-double descendant → group.supportsDouble=true', () => {
    const r = resolveFilterNode({
      kind: 'group', id: 'g', label: 'G',
      children: [
        { kind: 'leaf', id: 'a', label: 'A', checked: true, supportsDouble: true },
        { kind: 'leaf', id: 'b', label: 'B', checked: true },
      ],
    }, '');
    expect(r.supportsDouble).toBe(true);
  });

  it('group with no supports-double descendants → group.supportsDouble=false', () => {
    const r = resolveFilterNode({
      kind: 'group', id: 'g', label: 'G',
      children: [
        { kind: 'leaf', id: 'a', label: 'A', checked: true },
        { kind: 'leaf', id: 'b', label: 'B', checked: true },
      ],
    }, '');
    expect(r.supportsDouble).toBe(false);
  });

  it('supportsDouble propagates through nested groups', () => {
    const r = resolveFilterNode({
      kind: 'group', id: 'top', label: 'Top',
      children: [
        {
          kind: 'group', id: 'mid', label: 'Mid',
          children: [
            { kind: 'leaf', id: 'l', label: 'Leaf', checked: true, supportsDouble: true },
          ],
        },
      ],
    }, '');
    expect(r.supportsDouble).toBe(true);
    expect(r.children![0].supportsDouble).toBe(true);
  });
});

describe('resolveFilterNode — search filter', () => {
  it('empty search matches every node', () => {
    const r = resolveFilterNode(tree()[0], '');
    expect(r.matches).toBe(true);
    expect(r.children![0].matches).toBe(true);
    expect(r.children![1].matches).toBe(true);
  });

  it('non-matching leaf → matches=false', () => {
    const r = resolveFilterNode({ kind: 'leaf', id: 'a', label: 'Alpha', checked: true }, 'beta');
    expect(r.matches).toBe(false);
  });

  it('matching leaf via case-insensitive substring → matches=true', () => {
    const r = resolveFilterNode({ kind: 'leaf', id: 'a', label: 'Alpha', checked: true }, 'PHA');
    expect(r.matches).toBe(true);
  });

  it('group matches if its label matches even when no descendant does', () => {
    const r = resolveFilterNode(tree()[1], 'Two');
    expect(r.matches).toBe(true);
  });

  it('group matches if any descendant matches', () => {
    const r = resolveFilterNode(tree()[0], 'alpha');
    expect(r.matches).toBe(true);
    expect(r.children![0].matches).toBe(true);   // Alpha matches
    expect(r.children![1].matches).toBe(false);  // Bravo doesn't
  });

  it('group with no matching descendant and non-matching label → matches=false', () => {
    const r = resolveFilterNode(tree()[0], 'zzz');
    expect(r.matches).toBe(false);
  });
});

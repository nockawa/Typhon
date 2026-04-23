import { describe, expect, it } from 'vitest';
import { buildSchemaRelationshipsGraph } from '../schemaRelationshipsLayout';
import type { SystemRelationship } from '../../../hooks/schema/types';

function sys(name: string, access: 'read' | 'reactive', systemType = 'query'): SystemRelationship {
  return {
    systemName: name,
    systemType,
    access,
    queryViewSchema: [],
    changeFilterTypes: [],
  };
}

describe('buildSchemaRelationshipsGraph', () => {
  it('always places a component node even with no systems', () => {
    const { nodes, edges } = buildSchemaRelationshipsGraph('MyGame.Position', []);
    expect(nodes).toHaveLength(1);
    expect(nodes[0].id).toBe('component');
    expect(nodes[0].data.kind).toBe('component');
    expect(nodes[0].data.label).toBe('Position'); // namespace stripped
    expect(edges).toHaveLength(0);
  });

  it('creates a read edge pointing INTO the component', () => {
    const { nodes, edges } = buildSchemaRelationshipsGraph('Position', [
      sys('Movement', 'read'),
    ]);
    expect(nodes).toHaveLength(2);
    const systemNode = nodes.find((n) => n.data.kind === 'system');
    expect(systemNode?.data.label).toBe('Movement');
    expect(systemNode?.data.access).toBe('read');

    expect(edges).toHaveLength(1);
    expect(edges[0].source).toBe(systemNode?.id);
    expect(edges[0].target).toBe('component');
    expect(edges[0].animated).toBe(false);
  });

  it('creates a reactive edge pointing FROM the component (animated)', () => {
    const { edges } = buildSchemaRelationshipsGraph('Position', [
      sys('PositionConsumer', 'reactive'),
    ]);
    expect(edges).toHaveLength(1);
    expect(edges[0].source).toBe('component');
    expect(edges[0].target).not.toBe('component');
    expect(edges[0].animated).toBe(true);
  });

  it('separates read and reactive horizontally via dagre LR layout', () => {
    const { nodes } = buildSchemaRelationshipsGraph('Position', [
      sys('Movement', 'read'),
      sys('PhysicsConsumer', 'reactive'),
    ]);
    const component = nodes.find((n) => n.id === 'component')!;
    const reader = nodes.find((n) => n.data.label === 'Movement')!;
    const reactor = nodes.find((n) => n.data.label === 'PhysicsConsumer')!;

    // Reader sits to the left of the component, reactor to the right (dagre LR).
    expect(reader.position.x).toBeLessThan(component.position.x);
    expect(reactor.position.x).toBeGreaterThan(component.position.x);
  });

  it('assigns unique node ids so duplicate system names do not collide', () => {
    const { nodes } = buildSchemaRelationshipsGraph('Position', [
      sys('Duplicate', 'read'),
      sys('Duplicate', 'reactive'),
    ]);
    const ids = nodes.map((n) => n.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('produces deterministic positions for the same input (snapshot-friendly)', () => {
    const first = buildSchemaRelationshipsGraph('Position', [sys('A', 'read'), sys('B', 'reactive')]);
    const second = buildSchemaRelationshipsGraph('Position', [sys('A', 'read'), sys('B', 'reactive')]);
    expect(first.nodes.map((n) => n.position)).toEqual(second.nodes.map((n) => n.position));
  });
});

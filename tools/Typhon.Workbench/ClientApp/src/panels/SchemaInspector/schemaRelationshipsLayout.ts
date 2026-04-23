import dagre from 'dagre';
import type { Node, Edge } from '@xyflow/react';
import type { SystemRelationship } from '@/hooks/schema/types';

/** Node data surfaced to the React Flow renderer. */
export interface SchemaNodeData extends Record<string, unknown> {
  kind: 'component' | 'system';
  label: string;
  systemType?: string;
  access?: 'read' | 'reactive';
}

export type SchemaNode = Node<SchemaNodeData>;

/** Dimensions used for dagre's layout math. Kept in this module so tests and the renderer agree. */
export const NODE_WIDTH = 180;
export const NODE_HEIGHT = 48;

/**
 * Build the React Flow graph for a focused component and the systems touching it.
 * Pure function — no React, no DOM — fully testable with Vitest.
 *
 * Layout: dagre LR (left-to-right). The component sits in the middle; systems with `read` access
 * flow into it from the left, systems with `reactive` access flow from the right (they react to
 * writes to the component rather than reading its current state in their input view).
 */
export function buildSchemaRelationshipsGraph(
  componentTypeName: string,
  systems: SystemRelationship[],
): { nodes: SchemaNode[]; edges: Edge[] } {
  const componentNodeId = 'component';
  const nodes: SchemaNode[] = [
    {
      id: componentNodeId,
      type: 'default',
      position: { x: 0, y: 0 },
      data: {
        kind: 'component',
        label: stripNamespace(componentTypeName),
      },
    },
  ];
  const edges: Edge[] = [];

  for (let i = 0; i < systems.length; i++) {
    const s = systems[i];
    const nodeId = `system-${i}`;
    nodes.push({
      id: nodeId,
      type: 'default',
      position: { x: 0, y: 0 },
      data: {
        kind: 'system',
        label: s.systemName,
        systemType: s.systemType,
        access: s.access,
      },
    });

    if (s.access === 'read') {
      // System reads the component → edge flows system → component (left-to-right is system on left)
      edges.push({
        id: `e-${nodeId}-comp`,
        source: nodeId,
        target: componentNodeId,
        label: 'reads',
        animated: false,
      });
    } else {
      // Reactive — triggers on writes. Edge flows component → system (system is on the right).
      edges.push({
        id: `e-comp-${nodeId}`,
        source: componentNodeId,
        target: nodeId,
        label: 'triggers',
        animated: true,
      });
    }
  }

  return applyDagreLayout({ nodes, edges });
}

/** Apply dagre's LR layout, updating node positions. Exported for tests that want to verify layout. */
export function applyDagreLayout({
  nodes,
  edges,
}: {
  nodes: SchemaNode[];
  edges: Edge[];
}): { nodes: SchemaNode[]; edges: Edge[] } {
  const g = new dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'LR', ranksep: 80, nodesep: 30, marginx: 20, marginy: 20 });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    g.setNode(n.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }
  dagre.layout(g);

  const positioned = nodes.map((n) => {
    const pos = g.node(n.id);
    return {
      ...n,
      position: { x: pos.x - NODE_WIDTH / 2, y: pos.y - NODE_HEIGHT / 2 },
    };
  });
  return { nodes: positioned, edges };
}

function stripNamespace(fullName: string): string {
  const dot = fullName.lastIndexOf('.');
  return dot === -1 ? fullName : fullName.slice(dot + 1);
}

import type { ChunkSpan } from './traceModel';
import type { SystemDef } from './types';
import { getSystemColor, HEADER_BG, BORDER_COLOR, TEXT_COLOR, DIM_TEXT, BG_COLOR } from './canvasUtils';

interface DetailPaneProps {
  chunk: ChunkSpan | null;
  systems: SystemDef[];
  onClose: () => void;
}

const SYSTEM_TYPE_NAMES: Record<number, string> = {
  0: 'PipelineSystem',
  1: 'QuerySystem',
  2: 'CallbackSystem',
};

function formatTier(tier: number): string {
  if (tier === 0x0F || tier === 15) return 'All';
  const names: string[] = [];
  if (tier & 1) names.push('Tier0');
  if (tier & 2) names.push('Tier1');
  if (tier & 4) names.push('Tier2');
  if (tier & 8) names.push('Tier3');
  return names.join(' | ') || 'None';
}

export function DetailPane({ chunk, systems, onClose }: DetailPaneProps) {
  const fieldStyle = {
    display: 'flex',
    justifyContent: 'space-between',
    padding: '3px 0',
    borderBottom: `1px solid ${BG_COLOR}`,
    fontSize: '11px',
  };

  const labelStyle = { color: DIM_TEXT };
  const valueStyle = { color: TEXT_COLOR, textAlign: 'right' as const };

  if (!chunk) {
    return (
      <div style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        height: '100%',
        color: DIM_TEXT,
        fontSize: '12px',
        fontFamily: 'monospace',
      }}>
        Select a chunk to view details
      </div>
    );
  }

  const sys = systems[chunk.systemIndex];
  const color = getSystemColor(chunk.systemIndex);
  const typeName = sys ? (SYSTEM_TYPE_NAMES[sys.type] ?? `Type(${sys.type})`) : '?';
  const fullTypeName = sys?.isParallel && sys.type === 1 ? 'QuerySystem.Parallel' : typeName;

  return (
    <div style={{
      height: '100%',
      overflow: 'auto',
      fontFamily: 'monospace',
      background: HEADER_BG,
    }}>
      {/* Header */}
      <div style={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        padding: '8px 12px',
        borderBottom: `1px solid ${BORDER_COLOR}`,
        background: BG_COLOR,
      }}>
        <span style={{ color, fontWeight: 'bold', fontSize: '13px' }}>
          {chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName}
        </span>
        <button
          onClick={onClose}
          style={{
            background: 'transparent',
            color: DIM_TEXT,
            border: 'none',
            cursor: 'pointer',
            fontSize: '14px',
            fontFamily: 'monospace',
            padding: '0 4px',
          }}
        >
          x
        </button>
      </div>

      {/* Fields */}
      <div style={{ padding: '8px 12px' }}>
        <div style={fieldStyle}>
          <span style={labelStyle}>Type</span>
          <span style={valueStyle}>{fullTypeName}</span>
        </div>
        <div style={fieldStyle}>
          <span style={labelStyle}>Worker</span>
          <span style={valueStyle}>{chunk.workerId}</span>
        </div>
        {chunk.isParallel && (
          <div style={fieldStyle}>
            <span style={labelStyle}>Chunk</span>
            <span style={valueStyle}>{chunk.chunkIndex + 1} / {chunk.totalChunks || '?'}</span>
          </div>
        )}
        <div style={fieldStyle}>
          <span style={labelStyle}>Duration</span>
          <span style={valueStyle}>{chunk.durationUs.toFixed(1)} us</span>
        </div>
        <div style={fieldStyle}>
          <span style={labelStyle}>Entities</span>
          <span style={valueStyle}>{chunk.entitiesProcessed.toLocaleString()}</span>
        </div>
        <div style={fieldStyle}>
          <span style={labelStyle}>Tier</span>
          <span style={valueStyle}>{sys ? formatTier(sys.tierFilter) : '?'}</span>
        </div>

        {/* DAG section */}
        <div style={{
          color: DIM_TEXT,
          marginTop: '12px',
          marginBottom: '4px',
          textTransform: 'uppercase',
          letterSpacing: '1px',
          fontSize: '10px',
        }}>
          DAG
        </div>
        <div style={fieldStyle}>
          <span style={labelStyle}>Predecessors</span>
          <span style={valueStyle}>
            {sys?.predecessors.length
              ? sys.predecessors.map(i => systems[i]?.name ?? `[${i}]`).join(', ')
              : '(none)'}
          </span>
        </div>
        <div style={fieldStyle}>
          <span style={labelStyle}>Successors</span>
          <span style={valueStyle}>
            {sys?.successors.length
              ? sys.successors.map(i => systems[i]?.name ?? `[${i}]`).join(', ')
              : '(none)'}
          </span>
        </div>
      </div>
    </div>
  );
}

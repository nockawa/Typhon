import type { ChunkSpan, SpanData } from './traceModel';
import { TraceEventKind, SpanKindNames, type SystemDef } from './types';
import { getSystemColor, formatDuration, HEADER_BG, BORDER_COLOR, TEXT_COLOR, DIM_TEXT, BG_COLOR } from './canvasUtils';

interface DetailPaneProps {
  chunk: ChunkSpan | null;
  span: SpanData | null;
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

export function DetailPane({ chunk, span, systems, onClose }: DetailPaneProps) {
  const fieldStyle = {
    display: 'flex',
    justifyContent: 'space-between',
    padding: '3px 0',
    borderBottom: `1px solid ${BG_COLOR}`,
    fontSize: '11px',
  };

  const labelStyle = { color: DIM_TEXT };
  const valueStyle = { color: TEXT_COLOR, textAlign: 'right' as const };
  const sectionLabelStyle = {
    color: DIM_TEXT,
    marginTop: '12px',
    marginBottom: '4px',
    textTransform: 'uppercase' as const,
    letterSpacing: '1px',
    fontSize: '10px',
  };

  // ── Span branch ───────────────────────────────────────────────────────────────────────────────────────────────────
  // OTel spans and chunks are handled in parallel branches rather than a union type: chunks have DAG context and entity
  // counts, spans have parent/trace linkage and depth. When both `chunk` and `span` are set (shouldn't happen — the
  // click handler clears the other on select) the span wins because the user's most recent interaction was a span click.
  if (span) {
    const kindName = SpanKindNames[span.kind] ?? `Kind(${span.kind})`;
    const spanColor = '#eee';
    return (
      <div style={{ height: '100%', overflow: 'auto', fontFamily: 'monospace', background: HEADER_BG }}>
        <div style={{
          display: 'flex', justifyContent: 'space-between', alignItems: 'center',
          padding: '8px 12px', borderBottom: `1px solid ${BORDER_COLOR}`, background: BG_COLOR,
        }}>
          <span style={{ color: spanColor, fontWeight: 'bold', fontSize: '13px' }}>{span.name || kindName}</span>
          <button
            onClick={onClose}
            style={{
              background: 'transparent', color: DIM_TEXT, border: 'none', cursor: 'pointer',
              fontSize: '14px', fontFamily: 'monospace', padding: '0 4px',
            }}
          >x</button>
        </div>

        <div style={{ padding: '8px 12px' }}>
          <div style={fieldStyle}><span style={labelStyle}>Kind</span><span style={valueStyle}>{kindName}</span></div>
          <div style={fieldStyle}><span style={labelStyle}>Depth</span><span style={valueStyle}>{span.depth ?? 0}</span></div>
          <div style={fieldStyle}><span style={labelStyle}>Thread slot</span><span style={valueStyle}>{span.threadSlot}</span></div>
          <div style={fieldStyle}><span style={labelStyle}>Start</span><span style={valueStyle}>{formatDuration(span.startUs)}</span></div>
          <div style={fieldStyle}><span style={labelStyle}>Duration</span><span style={valueStyle}>{formatDuration(span.durationUs)}</span></div>
          {span.kickoffDurationUs !== undefined && (
            <div style={fieldStyle}>
              <span style={labelStyle}>Kickoff</span>
              <span style={valueStyle}>{formatDuration(span.kickoffDurationUs)} (async tail: {formatDuration(span.durationUs - span.kickoffDurationUs)})</span>
            </div>
          )}

          <div style={sectionLabelStyle}>Linkage</div>
          <div style={fieldStyle}><span style={labelStyle}>SpanId</span><span style={{ ...valueStyle, fontSize: '10px' }}>{span.spanId ?? '(none)'}</span></div>
          <div style={fieldStyle}><span style={labelStyle}>Parent</span><span style={{ ...valueStyle, fontSize: '10px' }}>{span.parentSpanId ?? '(none)'}</span></div>
          {(span.traceIdHi || span.traceIdLo) && (
            <>
              <div style={fieldStyle}><span style={labelStyle}>TraceIdHi</span><span style={{ ...valueStyle, fontSize: '10px' }}>{span.traceIdHi}</span></div>
              <div style={fieldStyle}><span style={labelStyle}>TraceIdLo</span><span style={{ ...valueStyle, fontSize: '10px' }}>{span.traceIdLo}</span></div>
            </>
          )}

          {/* Kind-specific fields from the raw event DTO */}
          {span.rawEvent && (() => {
            const evt = span.rawEvent;
            const F = (label: string, value: string | number | boolean | undefined | null) =>
              value !== undefined && value !== null
                ? <div style={fieldStyle}><span style={labelStyle}>{label}</span><span style={valueStyle}>{String(value)}</span></div>
                : null;
            const k = span.kind;

            // Only render the section if there are kind-specific fields to show.
            const fields: any[] = [];

            // Transaction kinds
            if (k === TraceEventKind.TransactionCommit || k === TraceEventKind.TransactionRollback) {
              if (evt.tsn != null) fields.push(F('TSN', evt.tsn)!);
              if (evt.componentCount != null) fields.push(F('Components', evt.componentCount)!);
              if (evt.conflictDetected != null) fields.push(F('Conflict', evt.conflictDetected ? 'Yes' : 'No')!);
            } else if (k === TraceEventKind.TransactionCommitComponent) {
              if (evt.tsn != null) fields.push(F('TSN', evt.tsn)!);
              if (evt.componentTypeId != null) fields.push(F('ComponentTypeId', evt.componentTypeId)!);
            } else if (k === TraceEventKind.TransactionPersist) {
              if (evt.tsn != null) fields.push(F('TSN', evt.tsn)!);
              if (evt.walLsn != null) fields.push(F('WAL LSN', evt.walLsn)!);
            }
            // ECS kinds
            else if (k === TraceEventKind.EcsSpawn) {
              if (evt.archetypeId != null) fields.push(F('ArchetypeId', evt.archetypeId)!);
              if (evt.entityId != null) fields.push(F('EntityId', evt.entityId)!);
              if (evt.tsn != null) fields.push(F('TSN', evt.tsn)!);
            } else if (k === TraceEventKind.EcsDestroy) {
              if (evt.entityId != null) fields.push(F('EntityId', evt.entityId)!);
              if (evt.cascadeCount != null) fields.push(F('Cascades', evt.cascadeCount)!);
              if (evt.tsn != null) fields.push(F('TSN', evt.tsn)!);
            } else if (k === TraceEventKind.EcsQueryExecute || k === TraceEventKind.EcsQueryCount || k === TraceEventKind.EcsQueryAny) {
              if (evt.scanMode != null) fields.push(F('ScanMode', ['Empty','Broad','Targeted','TargetedCluster','Spatial'][evt.scanMode] ?? evt.scanMode)!);
              if (evt.resultCount != null) fields.push(F('Results', evt.resultCount)!);
              if (evt.found != null) fields.push(F('Found', evt.found ? 'Yes' : 'No')!);
            } else if (k === TraceEventKind.EcsViewRefresh) {
              if (evt.mode != null) fields.push(F('Mode', ['Pull','Incremental','Overflow'][evt.mode] ?? evt.mode)!);
              if (evt.resultCount != null) fields.push(F('Results', evt.resultCount)!);
              if (evt.deltaCount != null) fields.push(F('Deltas', evt.deltaCount)!);
            }
            // PageCache kinds
            else if (k >= TraceEventKind.PageCacheFetch && k <= TraceEventKind.PageCacheFlushCompleted) {
              if (evt.filePageIndex != null) fields.push(F('FilePageIndex', evt.filePageIndex)!);
              if (evt.pageCount != null) fields.push(F('PageCount', evt.pageCount)!);
            } else if (k === TraceEventKind.PageCacheBackpressure) {
              if (evt.retryCount != null) fields.push(F('RetryCount', evt.retryCount)!);
              if (evt.dirtyCount != null) fields.push(F('DirtyPages', evt.dirtyCount)!);
              if (evt.epochCount != null) fields.push(F('EpochProtected', evt.epochCount)!);
            }
            // WAL kinds
            else if (k === TraceEventKind.WalFlush) {
              if (evt.batchByteCount != null) fields.push(F('BatchBytes', evt.batchByteCount)!);
              if (evt.frameCount != null) fields.push(F('Frames', evt.frameCount)!);
              if (evt.highLsn != null) fields.push(F('HighLSN', evt.highLsn)!);
            } else if (k === TraceEventKind.WalSegmentRotate) {
              if (evt.newSegmentIndex != null) fields.push(F('NewSegment', evt.newSegmentIndex)!);
            } else if (k === TraceEventKind.WalWait) {
              if (evt.targetLsn != null) fields.push(F('TargetLSN', evt.targetLsn)!);
            }
            // Checkpoint kinds
            else if (k === TraceEventKind.CheckpointCycle) {
              if (evt.targetLsn != null) fields.push(F('TargetLSN', evt.targetLsn)!);
              if (evt.reason != null) fields.push(F('Reason', ['Periodic','Forced','Shutdown'][evt.reason] ?? evt.reason)!);
              if (evt.dirtyPageCount != null) fields.push(F('DirtyPages', evt.dirtyPageCount)!);
            } else if (k === TraceEventKind.CheckpointWrite) {
              if (evt.writtenCount != null) fields.push(F('Written', evt.writtenCount)!);
            } else if (k === TraceEventKind.CheckpointTransition) {
              if (evt.transitionedCount != null) fields.push(F('Transitioned', evt.transitionedCount)!);
            } else if (k === TraceEventKind.CheckpointRecycle) {
              if (evt.recycledCount != null) fields.push(F('Recycled', evt.recycledCount)!);
            }
            // Statistics
            else if (k === TraceEventKind.StatisticsRebuild) {
              if (evt.entityCount != null) fields.push(F('Entities', evt.entityCount)!);
              if (evt.mutationCount != null) fields.push(F('Mutations', evt.mutationCount)!);
              if (evt.samplingInterval != null) fields.push(F('SamplingInterval', evt.samplingInterval)!);
            }
            // Cluster migration
            else if (k === TraceEventKind.ClusterMigration) {
              if (evt.archetypeId != null) fields.push(F('ArchetypeId', evt.archetypeId)!);
              if (evt.migrationCount != null) fields.push(F('Migrations', evt.migrationCount)!);
            }

            if (fields.length === 0) return null;
            return <>
              <div style={sectionLabelStyle}>Payload</div>
              {fields}
            </>;
          })()}
        </div>
      </div>
    );
  }

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
        Select a chunk or span to view details
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
          <span style={labelStyle}>Thread slot</span>
          <span style={valueStyle}>{chunk.threadSlot}</span>
        </div>
        {chunk.isParallel && (
          <div style={fieldStyle}>
            <span style={labelStyle}>Chunk</span>
            <span style={valueStyle}>{chunk.chunkIndex + 1} / {chunk.totalChunks || '?'}</span>
          </div>
        )}
        <div style={fieldStyle}>
          <span style={labelStyle}>Duration</span>
          <span style={valueStyle}>{formatDuration(chunk.durationUs)}</span>
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
        <div style={sectionLabelStyle}>DAG</div>
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

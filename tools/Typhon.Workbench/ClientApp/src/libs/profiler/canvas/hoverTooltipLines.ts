import { formatDuration } from './canvasUtils';
import type { TimeAreaHover } from './timeAreaHitTest';
import { TraceEventKind } from '@/libs/profiler/model/types';

/**
 * Build the text lines shown in the TimeArea's hover tooltip for span / chunk / phase / mini-row-op
 * / tick hovers. Returns `null` for hovers that don't warrant a tooltip (help glyph, gutter
 * chevron, gauge — each has its own dedicated overlay).
 *
 * Kept as a pure function so the React wrapper only needs to supply the hit-test result; no store
 * reads or canvas state. Consumers portal the output through {@link HelpOverlay} or any equivalent
 * multi-line text overlay.
 */
export function buildHoverTooltipLines(hover: TimeAreaHover): string[] | null {
  if (!hover) return null;
  switch (hover.kind) {
    case 'span': {
      const s = hover.span;
      const lines = [
        s.name,
        `Duration: ${formatDuration(s.durationUs)}`,
        `Thread slot: ${s.threadSlot}`,
      ];
      const d = s.depth ?? 0;
      if (d > 0) lines.push(`Depth: ${d}`);
      if (s.kickoffDurationUs !== undefined && s.kickoffDurationUs !== s.durationUs) {
        lines.push(`Kickoff: ${formatDuration(s.kickoffDurationUs)}`);
      }
      // Kind-specific metadata from the rawEvent. ClusterMigration: archetype + entity count + total
      // component instances moved (entities × per-entity slot count). The `componentCount` value comes
      // from the engine's wire-additive payload; on traces produced by older engines it's undefined.
      if (s.kind === TraceEventKind.ClusterMigration && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.migrationCount !== undefined) lines.push(`Entities: ${s.rawEvent.migrationCount.toLocaleString()}`);
        if (s.rawEvent.componentCount !== undefined) lines.push(`Components: ${s.rawEvent.componentCount.toLocaleString()}`);
      }
      return lines;
    }
    case 'chunk': {
      const c = hover.chunk;
      const label = c.isParallel ? `${c.systemName}[${c.chunkIndex}]` : c.systemName;
      const lines = [
        label,
        `Duration: ${formatDuration(c.durationUs)}`,
        `Thread slot: ${c.threadSlot}`,
      ];
      if (c.entitiesProcessed > 0) lines.push(`Entities: ${c.entitiesProcessed.toLocaleString()}`);
      if (c.isParallel) lines.push(`Parallel: ${c.totalChunks} chunks`);
      return lines;
    }
    case 'phase': {
      const p = hover.phase;
      return [
        `Phase: ${p.phaseName}`,
        `Tick: ${hover.tickNumber}`,
        `Duration: ${formatDuration(p.durationUs)}`,
      ];
    }
    case 'phase-marker': {
      const m = hover.marker;
      const lines = [
        `Marker: ${m.label}`,
        `Tick: ${hover.tickNumber}`,
      ];
      if (m.detail !== undefined) {
        lines.push(m.detail);
      }
      return lines;
    }
    case 'mini-row-op': {
      const op = hover.op;
      return [
        `${hover.rowLabel}: ${op.name}`,
        `Duration: ${formatDuration(op.durationUs)}`,
        `Thread slot: ${op.threadSlot}`,
      ];
    }
    case 'tick':
    case 'help':
    case 'gutter-chevron':
    case 'gauge':
      return null;
  }
}

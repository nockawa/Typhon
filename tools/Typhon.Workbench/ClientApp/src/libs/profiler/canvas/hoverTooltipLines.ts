import { formatDuration } from './canvasUtils';
import type { TimeAreaHover } from './timeAreaHitTest';

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

import { useState, useRef, useCallback, useEffect } from 'preact/hooks';
import type { ProcessedTrace, ChunkSpan, SpanData } from './traceModel';
import type { TimeRange } from './uiTypes';
import type { NavHistory } from './useNavHistory';
import { GraphArea } from './GraphArea';
import { DetailPane } from './DetailPane';
import { BORDER_COLOR, HEADER_BG, DIM_TEXT } from './canvasUtils';

interface WorkspaceProps {
  trace: ProcessedTrace;
  tracePath: string | null;
  viewRange: TimeRange;
  onViewRangeChange: (range: TimeRange) => void;
  selectedChunk: ChunkSpan | null;
  onChunkSelect: (chunk: ChunkSpan | null) => void;
  selectedSpan: SpanData | null;
  onSpanSelect: (span: SpanData | null) => void;
  navHistory: NavHistory;
}

const MIN_DETAIL_WIDTH = 200;
const MAX_DETAIL_WIDTH = 600;
const DEFAULT_DETAIL_WIDTH = 280;

export function Workspace({ trace, tracePath, viewRange, onViewRangeChange, selectedChunk, onChunkSelect, selectedSpan, onSpanSelect, navHistory }: WorkspaceProps) {
  const [detailOpen, setDetailOpen] = useState(true);
  const [detailWidth, setDetailWidth] = useState(DEFAULT_DETAIL_WIDTH);
  const isDraggingRef = useRef(false);
  const dragStartRef = useRef({ x: 0, width: 0 });

  const onDividerMouseDown = useCallback((e: MouseEvent) => {
    e.preventDefault();
    isDraggingRef.current = true;
    dragStartRef.current = { x: e.clientX, width: detailWidth };

    const onMove = (me: MouseEvent) => {
      const dx = dragStartRef.current.x - me.clientX;
      setDetailWidth(Math.max(MIN_DETAIL_WIDTH, Math.min(MAX_DETAIL_WIDTH, dragStartRef.current.width + dx)));
    };

    const onUp = () => {
      isDraggingRef.current = false;
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };

    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }, [detailWidth]);

  const handleClose = useCallback(() => {
    setDetailOpen(false);
    onChunkSelect(null);
    onSpanSelect(null);
  }, [onChunkSelect, onSpanSelect]);

  useEffect(() => {
    if ((selectedChunk || selectedSpan) && !detailOpen) {
      setDetailOpen(true);
    }
  }, [selectedChunk, selectedSpan, detailOpen]);

  return (
    <div style={{ display: 'flex', flex: 1, overflow: 'hidden' }}>
      <div style={{ flex: 1, minWidth: '200px', overflow: 'hidden' }}>
        <GraphArea
          trace={trace}
          tracePath={tracePath}
          viewRange={viewRange}
          onViewRangeChange={onViewRangeChange}
          selectedChunk={selectedChunk}
          onChunkSelect={onChunkSelect}
          selectedSpan={selectedSpan}
          onSpanSelect={onSpanSelect}
          navHistory={navHistory}
        />
      </div>

      {detailOpen ? (
        <>
          <div
            onMouseDown={onDividerMouseDown}
            style={{
              width: '4px',
              cursor: 'col-resize',
              background: BORDER_COLOR,
              flexShrink: 0,
            }}
          />
          <div style={{ width: `${detailWidth}px`, flexShrink: 0, overflow: 'hidden' }}>
            <DetailPane
              chunk={selectedChunk}
              span={selectedSpan}
              systems={trace.metadata.systems}
              onClose={handleClose}
            />
          </div>
        </>
      ) : (
        <div
          onClick={() => setDetailOpen(true)}
          style={{
            width: '20px',
            cursor: 'pointer',
            background: HEADER_BG,
            borderLeft: `1px solid ${BORDER_COLOR}`,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            flexShrink: 0,
            fontSize: '12px',
            color: DIM_TEXT,
            writingMode: 'vertical-rl',
          }}
          title="Show detail pane"
        >
          Details
        </div>
      )}
    </div>
  );
}

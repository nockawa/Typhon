import { useState, useRef, useCallback, useEffect } from 'preact/hooks';
import type { ProcessedTrace, ChunkSpan, SpanData, MarkerSelection } from './traceModel';
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
  selectedMarker: MarkerSelection | null;
  onMarkerSelect: (marker: MarkerSelection | null) => void;
  navHistory: NavHistory;
  /** View-menu toggles — passed through verbatim to <see cref="GraphArea"/>. */
  gaugeRegionVisible: boolean;
  legendsVisible: boolean;
  /** Gutter-width callback forwarded to <see cref="GraphArea"/>. Lets the App align sibling canvases (TickTimeline "?" glyph). */
  onGutterWidthChange?: (gutterWidth: number) => void;
  /** Chunk-cache ref forwarded to <see cref="GraphArea"/> so the debug line can report live RAM + OPFS usage. */
  chunkCacheRef?: { current: import('./chunkCache').ChunkCacheState | null };
}

const MIN_DETAIL_WIDTH = 200;
const MAX_DETAIL_WIDTH = 600;
const DEFAULT_DETAIL_WIDTH = 280;

export function Workspace({ trace, tracePath, viewRange, onViewRangeChange, selectedChunk, onChunkSelect, selectedSpan, onSpanSelect, selectedMarker, onMarkerSelect, navHistory, gaugeRegionVisible, legendsVisible, onGutterWidthChange, chunkCacheRef }: WorkspaceProps) {
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
    onMarkerSelect(null);
  }, [onChunkSelect, onSpanSelect, onMarkerSelect]);

  useEffect(() => {
    if ((selectedChunk || selectedSpan || selectedMarker) && !detailOpen) {
      setDetailOpen(true);
    }
  }, [selectedChunk, selectedSpan, selectedMarker, detailOpen]);

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
          onMarkerSelect={onMarkerSelect}
          navHistory={navHistory}
          gaugeRegionVisible={gaugeRegionVisible}
          legendsVisible={legendsVisible}
          onGutterWidthChange={onGutterWidthChange}
          chunkCacheRef={chunkCacheRef}
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
              marker={selectedMarker}
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

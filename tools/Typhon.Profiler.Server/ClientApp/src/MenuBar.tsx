import { useState, useRef, useEffect } from 'preact/hooks';
import type { ProcessedTrace } from './traceModel';
import type { TimeRange } from './uiTypes';
import type { NavHistory } from './useNavHistory';
import { HEADER_BG, BORDER_COLOR, SELECTED_COLOR, TEXT_COLOR, DIM_TEXT, BG_COLOR } from './canvasUtils';

interface MenuBarProps {
  trace: ProcessedTrace | null;
  fileName: string | null;
  loading: boolean;
  isLive: boolean;
  onFileSelected: (files: File[]) => void;
  /**
   * Open a trace by its server-side filesystem path. Skips the upload-to-temp-dir step, so the sidecar cache file lives alongside the source
   * trace instead of accumulating under the system TEMP folder. Path can be absolute or relative to the server's working directory.
   */
  onOpenByPath: (path: string) => void;
  onLiveConnect: () => void;
  onLiveDisconnect: () => void;
  navHistory: NavHistory;
  onViewRangeChange: (range: TimeRange) => void;
}

export function MenuBar({ trace, fileName, loading, isLive, onFileSelected, onOpenByPath, onLiveConnect, onLiveDisconnect, navHistory, onViewRangeChange }: MenuBarProps) {
  const [menuOpen, setMenuOpen] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  // Close menu on click outside
  useEffect(() => {
    if (!menuOpen) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [menuOpen]);

  const handleLoad = () => {
    setMenuOpen(false);
    fileInputRef.current?.click();
  };

  const handleOpenByPath = () => {
    setMenuOpen(false);
    // Browsers can't give JS access to a file's full filesystem path (sandbox), so prompt the user for it directly. Use the last-used path
    // as the default so re-opening the same file is one click + Enter. Local-only dev tool; prompt UX is acceptable here.
    const remembered = window.localStorage.getItem('typhon-profiler.lastOpenPath') ?? '';
    const path = window.prompt('Path to .typhon-trace file (absolute, or relative to server working directory):', remembered);
    if (!path || !path.trim()) return;
    const trimmed = path.trim();
    window.localStorage.setItem('typhon-profiler.lastOpenPath', trimmed);
    onOpenByPath(trimmed);
  };

  const handleFileChange = (e: Event) => {
    const input = e.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      onFileSelected(Array.from(input.files));
      input.value = ''; // reset so same file can be reloaded
    }
  };

  return (
    <header style={{
      height: '28px',
      background: HEADER_BG,
      borderBottom: `1px solid ${BORDER_COLOR}`,
      display: 'flex',
      alignItems: 'center',
      padding: '0 4px',
      flexShrink: 0,
      fontSize: '12px',
      fontFamily: 'monospace',
      userSelect: 'none',
    }}>
      {/* Menu items */}
      <div ref={menuRef} style={{ position: 'relative' }}>
        <button
          onClick={() => setMenuOpen(!menuOpen)}
          style={{
            background: menuOpen ? BORDER_COLOR : 'transparent',
            color: TEXT_COLOR,
            border: 'none',
            padding: '4px 10px',
            cursor: 'pointer',
            fontSize: '12px',
            fontFamily: 'monospace',
            borderRadius: '2px',
          }}
        >
          File
        </button>

        {menuOpen && (
          <div style={{
            position: 'absolute',
            top: '100%',
            left: 0,
            background: HEADER_BG,
            border: `1px solid ${BORDER_COLOR}`,
            borderRadius: '2px',
            minWidth: '140px',
            zIndex: 1000,
            boxShadow: '0 4px 12px rgba(0,0,0,0.5)',
          }}>
            <button
              onClick={handleLoad}
              style={{
                display: 'block',
                width: '100%',
                background: 'transparent',
                color: TEXT_COLOR,
                border: 'none',
                padding: '6px 16px',
                cursor: 'pointer',
                fontSize: '12px',
                fontFamily: 'monospace',
                textAlign: 'left',
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = BORDER_COLOR)}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
            >
              Load...
            </button>
            <button
              onClick={handleOpenByPath}
              style={{
                display: 'block',
                width: '100%',
                background: 'transparent',
                color: TEXT_COLOR,
                border: 'none',
                padding: '6px 16px',
                cursor: 'pointer',
                fontSize: '12px',
                fontFamily: 'monospace',
                textAlign: 'left',
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = BORDER_COLOR)}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
              title="Open a .typhon-trace by server-side path. Cache file will be written alongside the source, not in %TEMP%."
            >
              Open from path...
            </button>
            <div style={{ height: '1px', background: BORDER_COLOR, margin: '2px 8px' }} />
            {!isLive ? (
              <button
                onClick={() => { setMenuOpen(false); onLiveConnect(); }}
                style={{
                  display: 'block',
                  width: '100%',
                  background: 'transparent',
                  color: TEXT_COLOR,
                  border: 'none',
                  padding: '6px 16px',
                  cursor: 'pointer',
                  fontSize: '12px',
                  fontFamily: 'monospace',
                  textAlign: 'left',
                }}
                onMouseEnter={(e) => (e.currentTarget.style.background = BORDER_COLOR)}
                onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
              >
                Connect Live
              </button>
            ) : (
              <button
                onClick={() => { setMenuOpen(false); onLiveDisconnect(); }}
                style={{
                  display: 'block',
                  width: '100%',
                  background: 'transparent',
                  color: SELECTED_COLOR,
                  border: 'none',
                  padding: '6px 16px',
                  cursor: 'pointer',
                  fontSize: '12px',
                  fontFamily: 'monospace',
                  textAlign: 'left',
                }}
                onMouseEnter={(e) => (e.currentTarget.style.background = BORDER_COLOR)}
                onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
              >
                Disconnect
              </button>
            )}
          </div>
        )}
      </div>

      {/* Nav undo/redo buttons — these call onViewRangeChange directly which snaps the viewport. The animated transition
           happens via the mouse back/forward buttons in GraphArea which use animateToRange. The menu buttons are a simple
           fallback for users without a 5-button mouse. */}
      <button
        onClick={() => { const r = navHistory.undo(); if (r) onViewRangeChange(r); }}
        disabled={!navHistory.canUndo}
        title="Navigate back (Mouse Back)"
        style={{
          background: 'transparent', border: 'none', cursor: navHistory.canUndo ? 'pointer' : 'default',
          color: navHistory.canUndo ? TEXT_COLOR : DIM_TEXT, fontSize: '14px', fontFamily: 'monospace',
          padding: '2px 6px', opacity: navHistory.canUndo ? 1 : 0.4,
        }}
      >◀</button>
      <button
        onClick={() => { const r = navHistory.redo(); if (r) onViewRangeChange(r); }}
        disabled={!navHistory.canRedo}
        title="Navigate forward (Mouse Forward)"
        style={{
          background: 'transparent', border: 'none', cursor: navHistory.canRedo ? 'pointer' : 'default',
          color: navHistory.canRedo ? TEXT_COLOR : DIM_TEXT, fontSize: '14px', fontFamily: 'monospace',
          padding: '2px 6px', opacity: navHistory.canRedo ? 1 : 0.4,
        }}
      >▶</button>

      {/* Spacer */}
      <div style={{ flex: 1 }} />

      {/* Status info */}
      {loading && (
        <span style={{ color: SELECTED_COLOR, marginRight: '16px' }}>Loading...</span>
      )}

      {isLive && (
        <span style={{
          color: '#00ff88',
          marginRight: '12px',
          fontWeight: 'bold',
          fontSize: '11px',
          letterSpacing: '1px',
          display: 'flex',
          alignItems: 'center',
          gap: '6px',
        }}>
          <span style={{
            width: '6px',
            height: '6px',
            borderRadius: '50%',
            background: '#00ff88',
            display: 'inline-block',
            animation: 'pulse 1.5s ease-in-out infinite',
          }} />
          LIVE
          <style>{`@keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.3; } }`}</style>
        </span>
      )}

      {trace && (
        <div style={{ display: 'flex', gap: '16px', color: DIM_TEXT }}>
          {fileName && (
            <span style={{ color: TEXT_COLOR }}>{fileName}</span>
          )}
          <span>{trace.ticks.length} ticks</span>
          <span>{trace.metadata.header.systemCount} systems</span>
          <span>{trace.metadata.header.workerCount} workers</span>
          <span>{trace.metadata.header.baseTickRate} Hz</span>
        </div>
      )}

      {/* Hidden file input */}
      <input
        ref={fileInputRef}
        type="file"
        accept=".typhon-trace,.nettrace"
        multiple
        style={{ display: 'none' }}
        onChange={handleFileChange}
      />
    </header>
  );
}

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
  /** View-menu state (owned by App). Drawn as checkmark indicators next to each item. */
  gaugeRegionVisible: boolean;
  legendsVisible: boolean;
  /** View-menu callbacks — identical flips to the 'g' / 'l' keyboard shortcuts; both paths land on the same state. */
  onToggleGauges: () => void;
  onToggleLegends: () => void;
  /**
   * Inline error message shown next to the nav buttons. When non-null, renders as a dismissible pill inside the MenuBar (instead of the
   * old full-width red strip that shifted the whole viewport down by a row). Null clears it. Auto-dismiss timing is driven from App.tsx
   * so the MenuBar stays presentational.
   */
  error?: string | null;
  /** Called when the user clicks the error pill's × button to dismiss manually. */
  onErrorDismiss?: () => void;
  /**
   * When true, shows a dim "⟳ Loading trace detail…" pill in the same zone as the error pill. Independent from <c>error</c> — both pills
   * can be visible simultaneously when a chunk fetch fails but others are still queued. Replaces the old full-width strip that used to
   * sit on its own row below the MenuBar.
   */
  chunksLoading?: boolean;
}

export function MenuBar({ trace, fileName, loading, isLive, onFileSelected, onOpenByPath, onLiveConnect, onLiveDisconnect, navHistory, onViewRangeChange, gaugeRegionVisible, legendsVisible, onToggleGauges, onToggleLegends, error, onErrorDismiss, chunksLoading }: MenuBarProps) {
  // Two independent menus — File and View — each with its own open-state so they don't fight over the same "one menu open at a time"
  // invariant. Clicking one closes the other explicitly before opening itself.
  const [fileMenuOpen, setFileMenuOpen] = useState(false);
  const [viewMenuOpen, setViewMenuOpen] = useState(false);
  const menuOpen = fileMenuOpen || viewMenuOpen;
  const fileInputRef = useRef<HTMLInputElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  // Close menus on click outside
  useEffect(() => {
    if (!menuOpen) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setFileMenuOpen(false);
        setViewMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [menuOpen]);

  const closeMenus = () => { setFileMenuOpen(false); setViewMenuOpen(false); };

  const handleLoad = () => {
    closeMenus();
    fileInputRef.current?.click();
  };

  const handleOpenByPath = () => {
    closeMenus();
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
      {/* Menu items — File + View share a single wrapper for the "click-outside closes" listener. Each menu gets its OWN relative
           positioning container so its dropdown anchors to its own button instead of fighting over the shared wrapper's origin. */}
      <div ref={menuRef} style={{ display: 'flex', alignItems: 'center' }}>
      <div style={{ position: 'relative' }}>
        <button
          onClick={() => { setViewMenuOpen(false); setFileMenuOpen(!fileMenuOpen); }}
          style={{
            background: fileMenuOpen ? BORDER_COLOR : 'transparent',
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

        {fileMenuOpen && (
          <div style={{
            position: 'absolute',
            top: '100%',
            left: 0,
            background: HEADER_BG,
            border: `1px solid ${BORDER_COLOR}`,
            borderRadius: '2px',
            minWidth: '175px',   // was 140 — +25% per request
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
                onClick={() => { closeMenus(); onLiveConnect(); }}
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
                onClick={() => { closeMenus(); onLiveDisconnect(); }}
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

      {/* View menu — two toggle items mirroring the 'g' and 'l' keyboard shortcuts. Current state shows as a ✓ checkmark so the
           user can tell at a glance which layers are currently visible. */}
      <div style={{ position: 'relative' }}>
        <button
          onClick={() => { setFileMenuOpen(false); setViewMenuOpen(!viewMenuOpen); }}
          style={{
            background: viewMenuOpen ? BORDER_COLOR : 'transparent',
            color: TEXT_COLOR,
            border: 'none',
            padding: '4px 10px',
            cursor: 'pointer',
            fontSize: '12px',
            fontFamily: 'monospace',
            borderRadius: '2px',
          }}
        >
          View
        </button>

        {viewMenuOpen && (
          <div style={{
            position: 'absolute',
            top: '100%',
            left: 0,
            background: HEADER_BG,
            border: `1px solid ${BORDER_COLOR}`,
            borderRadius: '2px',
            minWidth: '180px',
            zIndex: 1000,
            boxShadow: '0 4px 12px rgba(0,0,0,0.5)',
          }}>
            <button
              onClick={() => { closeMenus(); onToggleGauges(); }}
              style={{
                display: 'flex', width: '100%', alignItems: 'center', justifyContent: 'space-between', gap: '12px',
                background: 'transparent', color: TEXT_COLOR, border: 'none',
                padding: '6px 12px 6px 16px', cursor: 'pointer',
                fontSize: '12px', fontFamily: 'monospace', textAlign: 'left',
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = BORDER_COLOR)}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
              title="Toggle the entire gauge region (all groups: Memory, Page Cache, GC, WAL, …). Keyboard: g"
            >
              <span>{gaugeRegionVisible ? '✓' : '\u00a0'} Show gauges</span>
              <span style={{ color: DIM_TEXT, marginLeft: '8px' }}>g</span>
            </button>
            <button
              onClick={() => { closeMenus(); onToggleLegends(); }}
              style={{
                display: 'flex', width: '100%', alignItems: 'center', justifyContent: 'space-between', gap: '12px',
                background: 'transparent', color: TEXT_COLOR, border: 'none',
                padding: '6px 12px 6px 16px', cursor: 'pointer',
                fontSize: '12px', fontFamily: 'monospace', textAlign: 'left',
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = BORDER_COLOR)}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
              title="Toggle per-track inline legends (Gen0/Gen1/… color swatches). Keyboard: l"
            >
              <span>{legendsVisible ? '✓' : '\u00a0'} Show legends</span>
              <span style={{ color: DIM_TEXT, marginLeft: '8px' }}>l</span>
            </button>
          </div>
        )}
      </div>
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

      {/* Inline error pill. Sits immediately right of the ◀▶ nav buttons so errors land next to the navigation state the user was just
          interacting with, instead of pushing the whole viewport down by a row (the previous behavior). The × button dismisses manually;
          App.tsx also auto-dismisses on a length-scaled timer. */}
      {error && (
        <div
          role="alert"
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            marginLeft: '12px',
            padding: '3px 10px',
            background: '#5c1a1a',
            color: '#ff6b6b',
            fontSize: '12px',
            fontFamily: 'monospace',
            borderRadius: '3px',
            border: `1px solid #7a2a2a`,
            // Long error messages would push the nav buttons off-screen; cap the pill width and let overflow ellipsize.
            maxWidth: '50%',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
          title={error}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{error}</span>
          {onErrorDismiss && (
            <button
              onClick={onErrorDismiss}
              title="Dismiss"
              style={{
                background: 'transparent', border: 'none', cursor: 'pointer',
                color: '#ff6b6b', fontSize: '14px', fontFamily: 'monospace',
                padding: '0 2px', lineHeight: 1,
              }}
            >×</button>
          )}
        </div>
      )}

      {/* Loading-detail pill. Shown whenever chunk loading is in progress AFTER the initial trace open (i.e. the main overlay has
          finished but more chunks are being fetched in the background). Rendered as a separate pill from the error so the two can coexist
          when, say, one chunk fetch fails while others are still queued. The spin animation makes "activity in progress" unambiguous
          without needing a second visual dimension (color alone is too easy to miss peripherally). */}
      {chunksLoading && (
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
            marginLeft: '12px',
            padding: '3px 10px',
            background: '#2a2b30',
            color: '#a8aab0',
            fontSize: '11px',
            fontFamily: 'monospace',
            borderRadius: '3px',
            border: `1px solid ${BORDER_COLOR}`,
          }}
        >
          <span style={{ display: 'inline-block', animation: 'menubar-spin 1s linear infinite' }}>⟳</span>
          Loading trace detail…
          <style>{`@keyframes menubar-spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }`}</style>
        </div>
      )}

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

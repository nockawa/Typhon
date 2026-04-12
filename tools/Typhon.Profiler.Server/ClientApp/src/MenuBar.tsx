import { useState, useRef, useEffect } from 'preact/hooks';
import type { ProcessedTrace } from './traceModel';
import { HEADER_BG, BORDER_COLOR, SELECTED_COLOR, TEXT_COLOR, DIM_TEXT, BG_COLOR } from './canvasUtils';

interface MenuBarProps {
  trace: ProcessedTrace | null;
  fileName: string | null;
  loading: boolean;
  isLive: boolean;
  onFileSelected: (files: File[]) => void;
  onLiveConnect: () => void;
  onLiveDisconnect: () => void;
}

export function MenuBar({ trace, fileName, loading, isLive, onFileSelected, onLiveConnect, onLiveDisconnect }: MenuBarProps) {
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

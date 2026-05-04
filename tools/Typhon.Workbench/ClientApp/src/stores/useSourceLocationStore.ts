import { create } from 'zustand';

/**
 * One row of the compile-time source-location table emitted by `SourceLocationGenerator`.
 * Mirrors `Typhon.Profiler.SourceLocationManifestEntry` on the server. See
 * `claude/design/observability/09-profiler-source-attribution.md`.
 */
export interface SourceLocation {
  /** Site id carried in span records when bit 1 of SpanFlags is set. 0 = unknown source. */
  id: number;
  /** Index into the parallel `files` array. */
  fileId: number;
  /** 1-based line number within the file. */
  line: number;
  /** Compile-time hint of the kind. May be 0 — runtime kind byte is the source of truth. */
  kind: number;
  /** Containing-method short name for display. */
  method: string;
}

interface SourceLocationState {
  /** Map siteId → resolved location. Lookup miss = unknown source. */
  locations: Map<number, SourceLocation>;
  /** Map fileId → repo-relative path. */
  files: Map<number, string>;
  /**
   * Replace the table with a new manifest payload. Called once per session: at session-init
   * for live-attach (FileTable + SourceLocationManifest frames), or at trace-load for file-mode.
   */
  setManifest: (entries: SourceLocation[], files: Array<{ fileId: number; path: string }>) => void;
  /** Convenience: resolve a siteId to file/line/method, or null if unknown. */
  resolve: (siteId: number | undefined | null) => ResolvedSourceLocation | null;
  clear: () => void;
}

/** Materialized lookup result with the file path joined in. */
export interface ResolvedSourceLocation {
  file: string;
  line: number;
  method: string;
  kind: number;
}

export const useSourceLocationStore = create<SourceLocationState>()((set, get) => ({
  locations: new Map(),
  files: new Map(),
  setManifest: (entries, files) => {
    const locMap = new Map<number, SourceLocation>();
    for (const e of entries) locMap.set(e.id, e);
    const fileMap = new Map<number, string>();
    for (const f of files) fileMap.set(f.fileId, f.path);
    set({ locations: locMap, files: fileMap });
  },
  resolve: (siteId) => {
    if (!siteId) return null;
    const { locations, files } = get();
    const loc = locations.get(siteId);
    if (!loc) return null;
    const file = files.get(loc.fileId);
    if (!file) return null;
    return { file, line: loc.line, method: loc.method, kind: loc.kind };
  },
  clear: () => set({ locations: new Map(), files: new Map() }),
}));

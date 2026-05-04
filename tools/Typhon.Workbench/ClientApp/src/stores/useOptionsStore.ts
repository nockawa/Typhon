import { create } from 'zustand';

/**
 * Mirror of the C# `WorkbenchOptions` schema. Fields stay in sync with
 * `tools/Typhon.Workbench/Hosting/WorkbenchOptions.cs`. Adding a new category here means
 * adding the matching record + controller patch endpoint on the server.
 */
export type EditorKind = 'vsCode' | 'cursor' | 'rider' | 'visualStudio' | 'custom';

export interface EditorOptions {
  kind: EditorKind;
  customCommand: string;
}

export interface ProfilerOptions {
  workspaceRoot: string;
}

export interface WorkbenchOptions {
  editor: EditorOptions;
  profiler: ProfilerOptions;
}

const DEFAULT_OPTIONS: WorkbenchOptions = {
  editor: { kind: 'vsCode', customCommand: '' },
  profiler: { workspaceRoot: '' },
};

interface OptionsState {
  options: WorkbenchOptions;
  loaded: boolean;
  /** Operating system from `/api/system/os` — drives UI affordances (e.g., disabling VS on macOS). */
  os: 'windows' | 'macos' | 'linux' | 'other';

  /** Fetch the full options document from the server. Replaces local state. */
  fetch: () => Promise<void>;
  /** Patch the editor category. Optimistic update with rollback on HTTP error. */
  setEditor: (editor: EditorOptions) => Promise<void>;
  /** Patch the profiler category. Optimistic with rollback. */
  setProfiler: (profiler: ProfilerOptions) => Promise<void>;
  /** Trigger an editor-launch via the server. Returns the structured result. */
  openInEditor: (file: string, line: number, column?: number) => Promise<OpenInEditorResult>;
}

export interface OpenInEditorResult {
  ok: boolean;
  error: string;
  hint: string;
}

export const useOptionsStore = create<OptionsState>()((set, get) => ({
  options: DEFAULT_OPTIONS,
  loaded: false,
  os: 'other',

  fetch: async () => {
    const [optsResp, osResp] = await Promise.all([
      fetch('/api/options'),
      fetch('/api/system/os'),
    ]);
    if (optsResp.ok) {
      const opts = (await optsResp.json()) as WorkbenchOptions;
      set({ options: opts, loaded: true });
    }
    if (osResp.ok) {
      const osInfo = (await osResp.json()) as { os: 'windows' | 'macos' | 'linux' | 'other' };
      set({ os: osInfo.os });
    }
  },

  setEditor: async (editor) => {
    const prev = get().options;
    set({ options: { ...prev, editor } });
    try {
      const resp = await fetch('/api/options/editor', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(editor),
      });
      if (!resp.ok) {
        set({ options: prev });
        throw new Error(`PATCH /api/options/editor failed: ${resp.status}`);
      }
      const updated = (await resp.json()) as WorkbenchOptions;
      set({ options: updated });
    } catch (err) {
      set({ options: prev });
      throw err;
    }
  },

  setProfiler: async (profiler) => {
    const prev = get().options;
    set({ options: { ...prev, profiler } });
    try {
      const resp = await fetch('/api/options/profiler', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(profiler),
      });
      if (!resp.ok) {
        set({ options: prev });
        throw new Error(`PATCH /api/options/profiler failed: ${resp.status}`);
      }
      const updated = (await resp.json()) as WorkbenchOptions;
      set({ options: updated });
    } catch (err) {
      set({ options: prev });
      throw err;
    }
  },

  openInEditor: async (file, line, column) => {
    const resp = await fetch('/api/profiler/open-in-editor', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ file, line, column: column ?? null }),
    });
    if (!resp.ok) {
      return {
        ok: false,
        error: `HTTP ${resp.status}: ${resp.statusText}`,
        hint: '',
      };
    }
    return (await resp.json()) as OpenInEditorResult;
  },
}));

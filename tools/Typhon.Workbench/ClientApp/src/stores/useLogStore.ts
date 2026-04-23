import { create } from 'zustand';

export type LogLevel = 'info' | 'warn' | 'error';

/**
 * Log entry source. Single enum across the Workbench so the Logs panel can facet by origin when
 * multiple streams are live simultaneously (client-side events, server-side engine logs, attached
 * app logs). Today only `workbench-ui` is populated; `workbench-server` and `attached-app` come in
 * later phases (see `claude/design/typhon-workbench/modules/05-logs.md`).
 */
export type LogSource = 'workbench-ui' | 'workbench-server' | 'attached-app';

export interface LogEntry {
  id: number;
  timestamp: number; // ms since epoch
  level: LogLevel;
  source: LogSource;
  message: string;
  /** Optional structured detail — rendered in an expandable row. Must be JSON-serializable. */
  details?: unknown;
}

const MAX_ENTRIES = 500;

interface LogState {
  entries: LogEntry[];
  nextId: number;
  append: (entry: Omit<LogEntry, 'id' | 'timestamp'> & { timestamp?: number }) => void;
  clear: () => void;
}

export const useLogStore = create<LogState>()((set) => ({
  entries: [],
  nextId: 1,
  append: (input) =>
    set((s) => {
      const entry: LogEntry = {
        id: s.nextId,
        timestamp: input.timestamp ?? Date.now(),
        level: input.level,
        source: input.source,
        message: input.message,
        details: input.details,
      };
      // Bounded ring — drop oldest when we hit the cap. Simple slice keeps implementation tiny at the
      // cost of O(n) per append, but n=500 and logs typically accrue slowly on the client side.
      const next = s.entries.length >= MAX_ENTRIES ? s.entries.slice(1) : s.entries;
      return { entries: [...next, entry], nextId: s.nextId + 1 };
    }),
  clear: () => set({ entries: [], nextId: 1 }),
}));

/**
 * Convenience helpers for the common client-side logging patterns — one-liner at the call site
 * rather than having to spell out the full object literal each time.
 */
export const logInfo = (message: string, details?: unknown) =>
  useLogStore.getState().append({ level: 'info', source: 'workbench-ui', message, details });

export const logWarn = (message: string, details?: unknown) =>
  useLogStore.getState().append({ level: 'warn', source: 'workbench-ui', message, details });

export const logError = (message: string, details?: unknown) =>
  useLogStore.getState().append({ level: 'error', source: 'workbench-ui', message, details });

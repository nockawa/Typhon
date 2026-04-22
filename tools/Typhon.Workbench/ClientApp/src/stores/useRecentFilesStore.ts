import { create } from 'zustand';
import { persist, createJSONStorage } from 'zustand/middleware';

const MAX_ENTRIES = 20;

export type RecentFileState = 'Ready' | 'MigrationRequired' | 'Incompatible';

export interface RecentFile {
  filePath: string;
  schemaDllPaths: string[];
  lastOpenedAt: string;
  lastState: RecentFileState;
  pinnedResourceIds?: string[];
}

interface RecentFilesStore {
  entries: RecentFile[];
  record: (entry: RecentFile) => void;
  remove: (filePath: string) => void;
  clear: () => void;
  pinResource: (filePath: string, resourceId: string) => void;
  unpinResource: (filePath: string, resourceId: string) => void;
  getPins: (filePath: string) => string[];
}

const safeStorage = () => {
  try {
    return createJSONStorage(() => localStorage);
  } catch {
    return undefined;
  }
};

function normalizePath(p: string) {
  return p.toLowerCase();
}

export const useRecentFilesStore = create<RecentFilesStore>()(
  persist(
    (set, get) => ({
      entries: [],
      record: (entry) =>
        set((s) => {
          const key = normalizePath(entry.filePath);
          const existing = s.entries.find((e) => normalizePath(e.filePath) === key);
          const merged: RecentFile = {
            ...entry,
            pinnedResourceIds: entry.pinnedResourceIds ?? existing?.pinnedResourceIds ?? [],
          };
          const deduped = s.entries.filter((e) => normalizePath(e.filePath) !== key);
          return { entries: [merged, ...deduped].slice(0, MAX_ENTRIES) };
        }),
      remove: (filePath) =>
        set((s) => ({
          entries: s.entries.filter((e) => normalizePath(e.filePath) !== normalizePath(filePath)),
        })),
      clear: () => set({ entries: [] }),
      pinResource: (filePath, resourceId) =>
        set((s) => {
          const key = normalizePath(filePath);
          return {
            entries: s.entries.map((e) => {
              if (normalizePath(e.filePath) !== key) return e;
              const current = e.pinnedResourceIds ?? [];
              if (current.includes(resourceId)) return e;
              return { ...e, pinnedResourceIds: [...current, resourceId] };
            }),
          };
        }),
      unpinResource: (filePath, resourceId) =>
        set((s) => {
          const key = normalizePath(filePath);
          return {
            entries: s.entries.map((e) => {
              if (normalizePath(e.filePath) !== key) return e;
              const current = e.pinnedResourceIds ?? [];
              return { ...e, pinnedResourceIds: current.filter((id) => id !== resourceId) };
            }),
          };
        }),
      getPins: (filePath) => {
        const key = normalizePath(filePath);
        return get().entries.find((e) => normalizePath(e.filePath) === key)?.pinnedResourceIds ?? [];
      },
    }),
    {
      name: 'workbench-recent-files',
      storage: safeStorage(),
    },
  ),
);

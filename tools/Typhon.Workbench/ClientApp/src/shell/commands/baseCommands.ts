import { deleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { refreshResourceGraph } from '@/hooks/useResourceIndex';

export interface CommandItem {
  id: string;
  label: string;
  keywords?: string;
  action: () => void;
}

export function buildBaseCommands(): CommandItem[] {
  const { sessionId, clearSession } = useSessionStore.getState();
  const { toggle: toggleTheme } = useThemeStore.getState();

  const closeSession = () => {
    if (!sessionId) return;
    deleteApiSessionsId(sessionId).then(clearSession).catch(() => {});
  };

  return [
    { id: 'open-file',     label: 'Open File…',               keywords: 'open typhon',      action: () => {} },
    { id: 'open-recent',   label: 'Open Recent',              keywords: 'recent file',       action: () => {} },
    { id: 'attach',        label: 'Attach…',                  keywords: 'attach engine',     action: () => {} },
    { id: 'open-trace',    label: 'Open Trace…',              keywords: 'trace typhon',      action: () => {} },
    { id: 'close-session', label: 'Close Session',            keywords: 'close disconnect',  action: closeSession },
    { id: 'refresh-graph', label: 'Refresh Resource Graph',   keywords: 'refresh reload tree', action: refreshResourceGraph },
    { id: 'toggle-theme',  label: 'Toggle Dark / Light Mode', keywords: 'theme dark light',  action: toggleTheme },
    { id: 'reload',        label: 'Reload',                   keywords: 'refresh',           action: () => location.reload() },
    { id: 'about',         label: 'About Typhon Workbench',   keywords: 'version info',      action: () => {} },
  ];
}

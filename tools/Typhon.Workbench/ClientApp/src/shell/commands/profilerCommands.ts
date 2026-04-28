import type { DockviewApi } from 'dockview-react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import type { CommandItem } from './baseCommands';

/**
 * Module-level dockview api registration for profiler-module commands — same pattern as
 * openSchemaBrowser's registerDockApi. DockHost publishes its api on ready so palette commands
 * and menu items can trigger the Profiler panel without prop drilling.
 */
let registeredApi: DockviewApi | null = null;

export function registerProfilerDockApi(api: DockviewApi | null): void {
  registeredApi = api;
}

/** Opens the Profiler panel, or focuses it if already open. No-op if dock isn't mounted yet. */
export function openProfilerPanel(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('profiler');
  if (existing) {
    existing.focus();
    return;
  }
  api.addPanel({
    id: 'profiler',
    component: 'Profiler',
    title: 'Profiler',
  });
}

/**
 * Opens (or focuses) the Top Spans panel. Tab-stacks with Logs by default — same position the
 * default trace/attach layout uses, so a user that closed the panel and re-opens it ends up with
 * the same dock arrangement. Falls back to the Profiler reference if Logs isn't around either.
 * No-op if the dock api isn't mounted yet.
 */
export function openTopSpansPanel(): void {
  const api = registeredApi;
  if (!api) return;
  const existing = api.getPanel('top-spans');
  if (existing) {
    existing.focus();
    return;
  }
  const ref = api.getPanel('logs') ?? api.getPanel('profiler');
  api.addPanel({
    id: 'top-spans',
    component: 'TopSpans',
    title: 'Top spans',
    position: ref ? { referencePanel: ref } : undefined,
  });
}

function zoomToFullTrace(): void {
  const metadata = useProfilerSessionStore.getState().metadata;
  const gm = metadata?.globalMetrics;
  if (!gm) return;
  const startUs = Number(gm.globalStartUs ?? 0);
  const endUs = Number(gm.globalEndUs ?? 0);
  if (endUs > startUs) useProfilerViewStore.getState().setViewRange({ startUs, endUs });
}

function panViewport(directionMultiplier: number): void {
  const { viewRange, setViewRange } = useProfilerViewStore.getState();
  const range = viewRange.endUs - viewRange.startUs;
  if (range <= 0) return;
  const delta = range * 0.25 * directionMultiplier;
  setViewRange({ startUs: viewRange.startUs + delta, endUs: viewRange.endUs + delta });
}

/**
 * Viewport-animation bridge — TimeArea registers its local `animateToRange` on mount so other
 * modules (nav-history restore, etc.) can ask the profiler to tween the viewport to a target
 * range with the same 800 ms ease-out curve used for double-click zoom. When TimeArea isn't
 * mounted (profiler panel closed, still loading), `animateViewportToRange` falls back to
 * `setViewRange` — no animation, but navigation still works.
 *
 * Registration pattern mirrors {@link registerProfilerDockApi}: a single module-level slot. The
 * TimeArea component calls `registerAnimateViewport(fn)` on mount and `registerAnimateViewport(null)`
 * on unmount.
 */
let registeredAnimate: ((target: TimeRange) => void) | null = null;

export function registerAnimateViewport(fn: ((target: TimeRange) => void) | null): void {
  registeredAnimate = fn;
}

export function animateViewportToRange(target: TimeRange): void {
  if (registeredAnimate) registeredAnimate(target);
  else useProfilerViewStore.getState().setViewRange(target);
}

/**
 * Save-replay dialog opener. MenuBar mounts the dialog and registers its setOpen callback here so palette commands and
 * the View menu can both trigger it without prop-drilling through the dock layer. Same pattern as
 * {@link registerProfilerDockApi}.
 */
let registeredOpenSaveReplay: (() => void) | null = null;

export function registerOpenSaveReplay(fn: (() => void) | null): void {
  registeredOpenSaveReplay = fn;
}

export function openSaveReplayDialog(): void {
  registeredOpenSaveReplay?.();
}

/**
 * Profiler-module palette entries. Spread into `buildBaseCommands()` so they land alongside the
 * shell-level commands in the `Ctrl+K` palette.
 */
export function buildProfilerPaletteCommands(): CommandItem[] {
  return [
    { id: 'profiler-open',           label: 'Open Profiler Panel',   keywords: 'profiler open show',               action: openProfilerPanel },
    { id: 'profiler-top-spans',      label: 'Open Top Spans Panel',  keywords: 'profiler top spans table slow expensive sortable', action: openTopSpansPanel },
    { id: 'profiler-save-replay',    label: 'Save Session as .typhon-replay…', keywords: 'save replay export attach session', action: openSaveReplayDialog },
    { id: 'profiler-toggle-gauges',  label: 'Toggle Gauge Region',   keywords: 'gauges canvas profiler g',         action: () => useProfilerViewStore.getState().toggleGaugeRegion() },
    { id: 'profiler-toggle-legends', label: 'Toggle Legends',        keywords: 'legends labels profiler l',        action: () => useProfilerViewStore.getState().toggleLegends() },
    { id: 'profiler-toggle-systems', label: 'Toggle Per-System Lanes', keywords: 'systems lanes profiler',         action: () => useProfilerViewStore.getState().togglePerSystemLanes() },
    { id: 'profiler-zoom-full',      label: 'Zoom to Full Trace',    keywords: 'zoom full profiler reset home',    action: zoomToFullTrace },
    { id: 'profiler-pan-left',       label: 'Pan Left',              keywords: 'pan left profiler',                action: () => panViewport(-1) },
    { id: 'profiler-pan-right',      label: 'Pan Right',             keywords: 'pan right profiler',               action: () => panViewport(+1) },
  ];
}

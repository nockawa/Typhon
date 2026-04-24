import { useEffect, useMemo } from 'react';
import { Activity, AlertCircle, Loader2, Radio } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore, type ConnectionStatus } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useProfilerBuildProgress } from '@/hooks/profiler/useProfilerBuildProgress';
import { useProfilerLiveStream } from '@/hooks/profiler/useProfilerLiveStream';
import { useProfilerCache } from '@/hooks/profiler/useProfilerCache';
import TickOverview from './sections/TickOverview';
import TimeArea from './sections/TimeArea';

/**
 * Empty-shell profiler panel. Handles both session kinds:
 *  - **Trace** — sidecar cache build with progress overlay, then placeholder once metadata lands.
 *  - **Attach** — live status pill + tick counter + Follow toggle; no timeline rendering yet.
 *
 * Phase 1b proves the live-data pipeline end-to-end (TCP connect → Init frame → metadata DTO → tick SSE);
 * Phase 2 lifts the Canvas 2D renderers on top.
 */
export default function ProfilerPanel() {
  const sessionId = useSessionStore((s) => s.sessionId);
  const filePath = useSessionStore((s) => s.filePath);
  const kind = useSessionStore((s) => s.kind);

  const metadata = useProfilerSessionStore((s) => s.metadata);
  const buildProgress = useProfilerSessionStore((s) => s.buildProgress);
  const buildError = useProfilerSessionStore((s) => s.buildError);
  const connectionStatus = useProfilerSessionStore((s) => s.connectionStatus);
  const liveTickCount = useProfilerSessionStore((s) => s.liveTickCount);
  const liveFollowActive = useProfilerSessionStore((s) => s.liveFollowActive);
  const setLiveFollowActive = useProfilerSessionStore((s) => s.setLiveFollowActive);
  const setIsLive = useProfilerSessionStore((s) => s.setIsLive);

  const isAttach = kind === 'attach';
  const isTrace = kind === 'trace';

  const setViewRange = useProfilerViewStore((s) => s.setViewRange);

  // Reset viewRange to the `{0, 0}` "no-selection" sentinel on every metadata arrival. This keeps
  // TickOverview's orange overlay off by default (its `computeSelectionIdxRange` returns {-1,-1}
  // on a degenerate range). TimeArea internally treats `{0, 0}` as "show the full trace" — it
  // falls back to `metadata.globalMetrics` for its initial viewport so the user still sees
  // everything, but the store stays at the sentinel until a real pan/zoom/drag-zoom interaction.
  useEffect(() => {
    if (!metadata?.globalMetrics) return;
    setViewRange({ startUs: 0, endUs: 0 });
  }, [metadata, setViewRange]);

  // Chunk cache — creates on metadata arrival, fetches ticks overlapping the current viewRange,
  // returns `TickData[]` assembled from whatever chunks are resident. Live mode stays empty for
  // now (live→TickData conversion is a separate concern).
  const { ticks: timeAreaTicks, gaugeData, pendingRangesUs } = useProfilerCache(sessionId, isAttach);

  // Metadata polling runs in both Trace and Attach modes — server branches on session kind.
  // Gate on kind so the query doesn't fire for Open (DB) sessions, which would 409.
  useProfilerMetadata(isTrace || isAttach ? sessionId : null);
  // Build-progress SSE only meaningful for Trace mode; live-stream SSE only for Attach mode.
  useProfilerBuildProgress(isTrace ? sessionId : null);
  useProfilerLiveStream(isAttach ? sessionId : null);

  // Tell the store which mode we're in; panels and future renderers can branch off `isLive`.
  // The cleanup wipes every profiler-scoped store so switching sessions (or closing the panel)
  // doesn't leak stale SpanData references / DetailPanel selections / nav-history entries from
  // the previous trace into the next one.
  useEffect(() => {
    setIsLive(isAttach);
    return () => {
      useProfilerSessionStore.getState().reset();
      useProfilerSelectionStore.getState().clear();
      useNavHistoryStore.getState().clear();
    };
  }, [sessionId, isAttach, setIsLive]);

  const fileName = useMemo(() => {
    if (!filePath) return null;
    const parts = filePath.split(/[\\/]/);
    return parts[parts.length - 1] || filePath;
  }, [filePath]);

  if (!isTrace && !isAttach) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background">
        <div className="text-center text-sm text-muted-foreground">
          <Activity className="mx-auto mb-2 h-6 w-6" aria-hidden="true" />
          Open a trace file or attach to a live engine to view profiler data.
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      {/* Header */}
      <div className="flex flex-shrink-0 items-center gap-3 border-b border-border bg-card px-3 py-2 text-[12px]">
        {isAttach
          ? <Radio className="h-4 w-4 text-muted-foreground" aria-label="Live profiler session" />
          : <Activity className="h-4 w-4 text-muted-foreground" aria-label="Trace profiler session" />}
        <span className="font-semibold text-foreground">
          {isAttach ? (fileName ?? 'Live engine') : (fileName ?? 'Profiler')}
        </span>

        {isAttach && (
          <>
            <StatusPill status={connectionStatus} />
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-foreground">
              {liveTickCount.toLocaleString()} ticks received
            </span>
            <div className="ml-auto">
              <Button
                variant={liveFollowActive ? 'default' : 'outline'}
                size="sm"
                onClick={() => setLiveFollowActive(!liveFollowActive)}
                className="h-6 text-[11px]"
                aria-label={liveFollowActive ? 'Pause live follow' : 'Resume live follow'}
                aria-pressed={liveFollowActive}
                title="Phase 2 will hook this up to the live timeline's auto-scroll. No visible effect in Phase 1b."
              >
                {liveFollowActive ? 'Following' : 'Paused'}
              </Button>
            </div>
          </>
        )}

        {isTrace && metadata && (
          <>
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-foreground">
              {Number(metadata.globalMetrics?.totalTicks ?? 0).toLocaleString()} ticks
            </span>
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-foreground">
              {formatDurationUs(
                Number(metadata.globalMetrics?.globalEndUs ?? 0) -
                  Number(metadata.globalMetrics?.globalStartUs ?? 0),
              )}
            </span>
            <span className="text-muted-foreground">·</span>
            <span className="font-mono tabular-nums text-muted-foreground">
              {metadata.header?.systemCount ?? 0} systems
            </span>
          </>
        )}
      </div>

      {/* Body */}
      <div className="relative flex-1 overflow-hidden">
        {buildError ? (
          <ErrorState message={buildError} />
        ) : isTrace && !metadata ? (
          <BuildProgressOverlay progress={buildProgress} />
        ) : isAttach && !metadata ? (
          <LiveWaitingOverlay status={connectionStatus} />
        ) : (
          <div className="flex h-full w-full flex-col overflow-hidden">
            <TickOverview isLive={isAttach} />
            <div className="flex-1 min-h-0">
              <TimeArea ticks={timeAreaTicks} gaugeData={gaugeData} pendingRangesUs={pendingRangesUs} isLive={isAttach} />
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function StatusPill({ status }: { status: ConnectionStatus | null }) {
  const effective = status ?? 'connecting';
  const dotClass =
    effective === 'connected'
      ? 'bg-green-500'
      : effective === 'reconnecting' || effective === 'connecting'
        ? 'bg-amber-500 animate-pulse'
        : 'bg-red-500';
  const label =
    effective === 'connected'
      ? 'Connected'
      : effective === 'reconnecting'
        ? 'Reconnecting…'
        : effective === 'connecting'
          ? 'Connecting…'
          : 'Disconnected';
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-border bg-muted px-2 py-0.5 text-[10px] font-medium text-foreground">
      <span className={`h-2 w-2 rounded-full ${dotClass}`} />
      {label}
    </span>
  );
}

function LiveWaitingOverlay({ status }: { status: ConnectionStatus | null }) {
  return (
    <div className="flex h-full w-full items-center justify-center">
      <div className="w-full max-w-md px-8 text-center">
        <Loader2 className="mx-auto mb-3 h-5 w-5 animate-spin text-muted-foreground" aria-hidden="true" />
        <div className="text-[13px] font-semibold text-foreground">Waiting for the engine's Init frame…</div>
        <div className="mt-1 text-[11px] text-muted-foreground">
          {status === 'connected'
            ? 'TCP link is up; engine hasn\u2019t published its metadata yet.'
            : status === 'reconnecting'
              ? 'Reconnecting to the engine — the connection dropped before Init arrived.'
              : 'Establishing TCP connection to the engine.'}
        </div>
      </div>
    </div>
  );
}

function BuildProgressOverlay({
  progress,
}: {
  progress: ReturnType<typeof useProfilerSessionStore.getState>['buildProgress'];
}) {
  const pct = useMemo(() => {
    if (!progress?.totalBytes || progress.totalBytes === 0) return 0;
    return Math.min(100, Math.max(0, ((progress.bytesRead ?? 0) / progress.totalBytes) * 100));
  }, [progress]);

  return (
    <div className="flex h-full w-full items-center justify-center">
      <div className="w-full max-w-md px-8">
        <div className="mb-4 flex items-center gap-2 text-[13px] font-semibold text-foreground">
          <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
          Building trace cache…
        </div>
        <div className="mb-2 h-2 overflow-hidden rounded-full bg-muted">
          <div
            className="h-full bg-primary transition-[width] duration-200"
            style={{ width: `${pct}%` }}
          />
        </div>
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
          <dt className="text-muted-foreground">Progress</dt>
          <dd className="font-mono tabular-nums text-foreground">{pct.toFixed(1)}%</dd>
          <dt className="text-muted-foreground">Bytes</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {formatBytes(progress?.bytesRead ?? 0)} / {formatBytes(progress?.totalBytes ?? 0)}
          </dd>
          <dt className="text-muted-foreground">Ticks</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {(progress?.tickCount ?? 0).toLocaleString()}
          </dd>
          <dt className="text-muted-foreground">Events</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {(progress?.eventCount ?? 0).toLocaleString()}
          </dd>
        </dl>
      </div>
    </div>
  );
}

function ErrorState({ message }: { message: string }) {
  return (
    <div className="flex h-full w-full items-center justify-center">
      <div className="max-w-md px-8 text-center">
        <AlertCircle className="mx-auto mb-3 h-6 w-6 text-destructive" aria-hidden="true" />
        <div className="mb-1 text-[13px] font-semibold text-foreground">
          Trace cache build failed
        </div>
        <div className="font-mono text-[11px] text-muted-foreground">{message}</div>
      </div>
    </div>
  );
}

function formatDurationUs(us: number): string {
  if (us < 1000) return `${us.toFixed(0)} µs`;
  if (us < 1_000_000) return `${(us / 1000).toFixed(1)} ms`;
  if (us < 60_000_000) return `${(us / 1_000_000).toFixed(2)} s`;
  return `${(us / 60_000_000).toFixed(1)} min`;
}

function formatBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(2)} MB`;
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

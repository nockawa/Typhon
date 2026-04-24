import { Activity, Blocks, Clock, Tag } from 'lucide-react';
import type { ChunkSpan, MarkerSelection, SpanData } from '@/libs/profiler/model/traceModel';
import type { ProfilerSelection } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';

/**
 * Profiler selection detail — the fourth DetailPanel render branch (Phase 2e). Mirrors the
 * DL-grid style of `FieldDetail` / `ResourceDetail` so a user switching between a schema-field
 * click and a profiler-span click sees the same visual language.
 *
 * Branches by `selection.kind`:
 *   - `'span'`   → span metadata: name, kind, thread slot, duration, start/end µs, depth, parent span id
 *   - `'chunk'`  → scheduler chunk: system name, chunk/thread indices, duration, entity count, isParallel
 *   - `'tick'`   → tick summary: number (no further data available without the full TickData here)
 *   - `'marker'` → memory-alloc / GC marker kind-specific fields
 */

interface Props {
  selection: ProfilerSelection;
}

export default function ProfilerDetail({ selection }: Props): React.JSX.Element {
  switch (selection.kind) {
    case 'span':   return <SpanDetail span={selection.span} />;
    case 'chunk':  return <ChunkDetail chunk={selection.chunk} />;
    case 'tick':   return <TickDetail tickNumber={selection.tickNumber} />;
    case 'marker': return <MarkerDetail marker={selection.marker} />;
  }
}

// ─── Spans ─────────────────────────────────────────────────────────────────────────────────────

function SpanDetail({ span }: { span: SpanData }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Activity className="h-4 w-4 text-muted-foreground" />} title={span.name} suffix="span" />
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
          <dt className="text-muted-foreground">Kind</dt>
          <dd className="font-mono text-foreground">{span.kind}</dd>

          <dt className="text-muted-foreground">Thread</dt>
          <dd className="font-mono tabular-nums text-foreground">Slot {span.threadSlot}</dd>

          <dt className="text-muted-foreground">Start</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(span.startUs)}</dd>

          <dt className="text-muted-foreground">End</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(span.endUs)}</dd>

          <dt className="text-muted-foreground">Duration</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(span.durationUs)}</dd>

          {span.kickoffDurationUs !== undefined && (
            <>
              <dt className="text-muted-foreground">Kickoff</dt>
              <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(span.kickoffDurationUs)}</dd>
            </>
          )}

          {span.depth !== undefined && (
            <>
              <dt className="text-muted-foreground">Depth</dt>
              <dd className="font-mono tabular-nums text-foreground">{span.depth}</dd>
            </>
          )}

          {span.spanId && (
            <>
              <dt className="text-muted-foreground">Span id</dt>
              <dd className="truncate font-mono text-foreground">{span.spanId}</dd>
            </>
          )}

          {span.parentSpanId && (
            <>
              <dt className="text-muted-foreground">Parent</dt>
              <dd className="truncate font-mono text-foreground">{span.parentSpanId}</dd>
            </>
          )}

          {span.traceIdHi !== undefined && span.traceIdLo !== undefined && (
            <>
              <dt className="text-muted-foreground">Trace id</dt>
              <dd className="truncate font-mono text-foreground">{span.traceIdHi}.{span.traceIdLo}</dd>
            </>
          )}
        </dl>
      </div>
    </div>
  );
}

// ─── Chunks ────────────────────────────────────────────────────────────────────────────────────

function ChunkDetail({ chunk }: { chunk: ChunkSpan }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Blocks className="h-4 w-4 text-muted-foreground" />} title={chunk.systemName || `System ${chunk.systemIndex}`} suffix="chunk" />
        <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
          <dt className="text-muted-foreground">System</dt>
          <dd className="font-mono text-foreground">#{chunk.systemIndex} {chunk.systemName}</dd>

          <dt className="text-muted-foreground">Thread</dt>
          <dd className="font-mono tabular-nums text-foreground">Slot {chunk.threadSlot}</dd>

          <dt className="text-muted-foreground">Chunk</dt>
          <dd className="font-mono tabular-nums text-foreground">
            {chunk.chunkIndex} / {chunk.totalChunks} {chunk.isParallel ? '(parallel)' : '(serial)'}
          </dd>

          <dt className="text-muted-foreground">Start</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(chunk.startUs)}</dd>

          <dt className="text-muted-foreground">End</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatUs(chunk.endUs)}</dd>

          <dt className="text-muted-foreground">Duration</dt>
          <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(chunk.durationUs)}</dd>

          <dt className="text-muted-foreground">Entities</dt>
          <dd className="font-mono tabular-nums text-foreground">{chunk.entitiesProcessed.toLocaleString()}</dd>
        </dl>
      </div>
    </div>
  );
}

// ─── Ticks ─────────────────────────────────────────────────────────────────────────────────────

function TickDetail({ tickNumber }: { tickNumber: number }): React.JSX.Element {
  // Look up the summary entry for this tick from the metadata DTO (no need to keep the full
  // TickData around — summaries have start/duration/eventCount already). Fall back gracefully
  // when the summary isn't loaded yet.
  const tickSummary = useProfilerSessionStore((s) => {
    const summaries = s.metadata?.tickSummaries;
    if (!summaries) return null;
    for (const t of summaries) {
      if (Number(t.tickNumber) === tickNumber) return t;
    }
    return null;
  });

  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Clock className="h-4 w-4 text-muted-foreground" />} title={`Tick ${tickNumber}`} suffix="scheduler tick" />
        {tickSummary ? (
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
            <dt className="text-muted-foreground">Start</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatUs(Number(tickSummary.startUs))}</dd>

            <dt className="text-muted-foreground">Duration</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(Number(tickSummary.durationUs))}</dd>

            <dt className="text-muted-foreground">Events</dt>
            <dd className="font-mono tabular-nums text-foreground">{Number(tickSummary.eventCount).toLocaleString()}</dd>

            <dt className="text-muted-foreground">Max system</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(Number(tickSummary.maxSystemDurationUs))}</dd>
          </dl>
        ) : (
          <p className="text-[11px] text-muted-foreground">Summary not loaded.</p>
        )}
      </div>
    </div>
  );
}

// ─── Markers ──────────────────────────────────────────────────────────────────────────────────

function MarkerDetail({ marker }: { marker: MarkerSelection }): React.JSX.Element {
  return (
    <div className="flex h-full flex-col bg-background p-3">
      <div className="rounded-md border border-border bg-card p-3 text-[12px]">
        <Header icon={<Tag className="h-4 w-4 text-muted-foreground" />} title={marker.kind} suffix="marker" />
        {marker.kind === 'memory-alloc' && (
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
            <dt className="text-muted-foreground">Timestamp</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatUs(marker.event.timestampUs)}</dd>

            <dt className="text-muted-foreground">Direction</dt>
            <dd className="font-mono text-foreground">{marker.event.direction === 0 ? 'alloc' : 'free'}</dd>

            <dt className="text-muted-foreground">Size</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatBytes(marker.event.sizeBytes)}</dd>

            <dt className="text-muted-foreground">Total after</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatBytes(marker.event.totalAfterBytes)}</dd>

            <dt className="text-muted-foreground">Source tag</dt>
            <dd className="font-mono tabular-nums text-foreground">{marker.event.sourceTag}</dd>

            <dt className="text-muted-foreground">Thread</dt>
            <dd className="font-mono tabular-nums text-foreground">Slot {marker.event.threadSlot}</dd>
          </dl>
        )}
        {marker.kind === 'gc' && (
          <dl className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-[11px]">
            <dt className="text-muted-foreground">Kind</dt>
            <dd className="font-mono text-foreground">{marker.event.kind}</dd>

            <dt className="text-muted-foreground">Timestamp</dt>
            <dd className="font-mono tabular-nums text-foreground">{formatUs(marker.event.timestampUs)}</dd>

            <dt className="text-muted-foreground">Generation</dt>
            <dd className="font-mono tabular-nums text-foreground">{marker.event.generation}</dd>

            <dt className="text-muted-foreground">GC #</dt>
            <dd className="font-mono tabular-nums text-foreground">{marker.event.gcCount}</dd>

            {marker.event.pauseDurationUs !== undefined && (
              <>
                <dt className="text-muted-foreground">Pause</dt>
                <dd className="font-mono tabular-nums text-foreground">{formatDurationUs(marker.event.pauseDurationUs)}</dd>
              </>
            )}

            {marker.event.promotedBytes !== undefined && (
              <>
                <dt className="text-muted-foreground">Promoted</dt>
                <dd className="font-mono tabular-nums text-foreground">{formatBytes(marker.event.promotedBytes)}</dd>
              </>
            )}
          </dl>
        )}
      </div>
    </div>
  );
}

// ─── Helpers ──────────────────────────────────────────────────────────────────────────────────

function Header({ icon, title, suffix }: { icon: React.ReactNode; title: string; suffix: string }): React.JSX.Element {
  return (
    <div className="mb-2 flex items-center gap-2 border-b border-border pb-2">
      {icon}
      <h3 className="truncate text-[13px] font-semibold text-foreground">{title}</h3>
      <span className="ml-auto font-mono text-[11px] text-muted-foreground">{suffix}</span>
    </div>
  );
}

/**
 * Adaptive time formatting — picks the coarsest unit (ns / µs / ms / s) that keeps the displayed
 * number readable. Used for both absolute timestamps (Start / End / Timestamp) and durations, so
 * the Detail panel never shows "1500000.000 µs" when "1.5 s" conveys the same information. Three
 * decimals in each unit preserves enough precision to distinguish close values without becoming
 * noisy (e.g., sub-ns differences aren't worth surfacing in a detail read-out).
 */
function formatUs(us: number): string {
  const abs = Math.abs(us);
  const sign = us < 0 ? '-' : '';
  if (abs === 0) return '0 µs';
  if (abs < 1) {
    const ns = abs * 1000;
    return `${sign}${ns.toFixed(ns < 10 ? 1 : 0)} ns`;
  }
  if (abs < 1000) return `${sign}${abs.toFixed(3)} µs`;
  if (abs < 1_000_000) return `${sign}${(abs / 1000).toFixed(3)} ms`;
  return `${sign}${(abs / 1_000_000).toFixed(3)} s`;
}

/**
 * Same adaptive rule as {@link formatUs} — both timestamps and durations share the unit ladder so
 * a start-time of "2.5 s" and a duration of "1.2 s" read with matching visual grammar.
 */
const formatDurationUs = formatUs;

function formatBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KiB`;
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(2)} MiB`;
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} GiB`;
}

import { useRef, useEffect, useCallback, useState, useMemo } from 'preact/hooks';
import type { ProcessedTrace, ChunkSpan, TickData, SpanData, MarkerSelection } from './traceModel';
import { getTicksInRange } from './traceModel';
import type { Viewport, TrackLayout, TimeRange, TrackState } from './uiTypes';
import type { NavHistory } from './useNavHistory';
import {
  setupCanvas, computeGridStep, formatRulerLabel, formatDuration, drawTooltip, getSystemColor,
  PHASE_COLOR, BG_COLOR, HEADER_BG, BORDER_COLOR, TEXT_COLOR, DIM_TEXT, GRID_COLOR, SPAN_PALETTE, TIMELINE_PALETTE
} from './canvasUtils';
import type { TooltipLine } from './canvasUtils';
import {
  appendGaugeTracks, getGaugeGroupSpec, GAUGE_TRACK_ID_SET,
  loadPersistedGaugeCollapseState, persistGaugeCollapseState,
} from './gaugeRegion';
import { GROUP_RENDERERS, buildGaugeTooltipLines, drawGaugeSummaryStrip } from './gaugeGroupRenderers';
import type { ChunkCacheState } from './chunkCache';

interface GraphAreaProps {
  trace: ProcessedTrace;
  tracePath: string | null;
  viewRange: TimeRange;
  onViewRangeChange: (range: TimeRange) => void;
  selectedChunk: ChunkSpan | null;
  onChunkSelect: (chunk: ChunkSpan | null) => void;
  selectedSpan: SpanData | null;
  onSpanSelect: (span: SpanData | null) => void;
  onMarkerSelect: (marker: MarkerSelection | null) => void;
  navHistory: NavHistory;
  /** View-menu / 'g' toggle: whether the whole gauge region is visible. Owned by the App so the MenuBar can drive it. */
  gaugeRegionVisible: boolean;
  /** View-menu / 'l' toggle: whether per-track inline legends ("Gen0 / Gen1 / …") are drawn inside gauge tracks. */
  legendsVisible: boolean;
  /**
   * Called by the render loop whenever the measured gutter width changes. The App uses this to align sibling canvases — notably the
   * TickTimeline's help "?" glyph — with the gutter's right edge. Deduplicated via a render-scoped ref so steady-state repaints don't
   * bounce setState every frame.
   */
  onGutterWidthChange?: (gutterWidth: number) => void;
  /**
   * Ref to the App's chunk cache — used ONLY to render live cache-usage stats in the bottom debug line (RAM usage, OPFS usage,
   * OPFS hit rate). The render loop dereferences <c>.current</c> each frame so the stats stay current without needing App to
   * re-render on every chunk load.
   */
  chunkCacheRef?: { current: ChunkCacheState | null };
}

/**
 * Minimum gutter width. The actual width is computed per-trace via <see cref="computeGutterWidth"/> by measuring every track label
 * ("Time", "Phases", "Transaction + UoW", per-slot thread names, ...) and picking the widest plus padding. This constant is only the
 * FLOOR — prevents the gutter from collapsing below a sensible width on traces with very short labels.
 */
const MIN_GUTTER_WIDTH = 80;
const GUTTER_PAD_LEFT = 6;      // matches the fillText `x = 6` used when painting track labels
const GUTTER_PAD_RIGHT = 8;     // breathing room before the gutter/content separator at gutterWidth - 0.5
const GUTTER_CARET_WIDTH = 20;  // "▶ " / "▼ " / "▼▼ " prefix for collapsible tracks — sized for the double-expand "▼▼ " worst case in 10px monospace
// Reserved right-edge column for the per-row "?" help glyph when legends are visible. 14 px = "?" in 11px monospace (~7 px) + left
// margin from the label (~5 px) + right padding (~2 px). Only added to the gutter width when `legendsVisible` is true, so turning
// legends off collapses the gutter back to the label-fit width.
const HELP_ICON_WIDTH = 14;
// Slop around the glyph's hit box — makes the help tooltip forgiving to a 1–2 px mouse aim error. Small enough that clicking the
// chevron still registers as a collapse click (chevron lives at x=6..18, help zone at x=GUTTER_WIDTH-18..GUTTER_WIDTH).
const HELP_ICON_HIT_PAD = 3;
const RULER_HEIGHT = 24;
const TRACK_HEIGHT = 28;
const TRACK_GAP = 2;
const PHASE_TRACK_HEIGHT = 20;
const COLLAPSED_HEIGHT = 4;
const LABEL_ROW_HEIGHT = 18;
/**
 * Summary-strip visual height for gauge tracks + slot lanes (the new 3-state `summary` mode). The strip renders INSIDE the
 * label row's vertical band — the label sits in the gutter, the strip sits in the content area, they share the same Y
 * range and are horizontally adjacent. This keeps the summary mode's total footprint = <see cref="LABEL_ROW_HEIGHT"/>
 * regardless of strip height, so the feature doesn't inflate the gauge region when everything's in summary mode.
 * Non-gauge tracks (phases / page-cache / etc.) keep the legacy 4-px <see cref="COLLAPSED_HEIGHT"/> strip rendered
 * BELOW the label row (legacy layout).
 */
const SUMMARY_STRIP_HEIGHT = 14;
/** Top padding inside the label row band — centers the 14 px strip vertically within the 18 px band. */
const SUMMARY_STRIP_TOP_PAD = (LABEL_ROW_HEIGHT - SUMMARY_STRIP_HEIGHT) / 2;
const MINI_ROW_HEIGHT = 11;  // 9px text + 1px top margin + 1px bottom — used by Page Cache + Disk IO projection tracks
const MIN_RECT_WIDTH = 1;
const FLAME_ROW_HEIGHT = 14;
const FLAME_TRACK_DEFAULT_HEIGHT = 120;

const TICK_BOUNDARY_COLOR = 'rgba(120, 120, 130, 0.3)';  // near-neutral grey w/ tiny blue tint — keeps the ruler ticks visible without fighting data colors

const COAL_MAX_DEPTH = 8;
const _coalPool = {
  x1: new Float64Array(COAL_MAX_DEPTH),
  x2: new Float64Array(COAL_MAX_DEPTH),
  count: new Int32Array(COAL_MAX_DEPTH),
  sy: new Float64Array(COAL_MAX_DEPTH),
};

/**
 * Measure every track label in the supplied layout and return the smallest gutter width that fits the widest one (plus the caret
 * prefix for collapsible tracks, plus left/right padding). Floored at <see cref="MIN_GUTTER_WIDTH"/> so a trace with only short
 * labels still gets a reasonable gutter. Rounded up to the next even pixel so the gutter/content separator lands on a pixel
 * boundary (avoids sub-pixel anti-aliasing of vertical lines).
 *
 * Assumes the caller will draw labels in <c>10px monospace</c> — the canvas font state is set before measuring so the returned
 * width matches actual draw metrics.
 */
function computeGutterWidth(
  ctx: CanvasRenderingContext2D,
  layout: { label: string; collapsible: boolean }[],
  legendsVisible: boolean
): number {
  const prevFont = ctx.font;
  ctx.font = '10px monospace';
  let widest = MIN_GUTTER_WIDTH - GUTTER_PAD_LEFT - GUTTER_PAD_RIGHT;
  for (const t of layout) {
    const prefix = t.collapsible ? GUTTER_CARET_WIDTH : 0;
    const w = prefix + ctx.measureText(t.label).width;
    if (w > widest) widest = w;
  }
  ctx.font = prevFont;
  // When legends are visible, reserve a right-side column for the "?" help glyph so it never overlaps the longest label. When legends
  // are off, the column disappears entirely — no icon is drawn, no width is wasted.
  const helpReserve = legendsVisible ? HELP_ICON_WIDTH : 0;
  const raw = Math.ceil(widest + GUTTER_PAD_LEFT + GUTTER_PAD_RIGHT + helpReserve);
  // Round to next even pixel — keeps the gutter/content separator on a crisp 1px line instead of a half-pixel anti-aliased smear.
  return Math.max(MIN_GUTTER_WIDTH, raw + (raw & 1));
}

/**
 * Detailed multi-line help text shown when the user hovers the "?" glyph next to a track's label. Keyed by track id (or id prefix
 * for slot lanes). Each entry explains what the track draws, how to read the visual encoding, and the common interpretation
 * patterns. Intentionally verbose — the whole point of this tooltip is to replace tribal knowledge about the viewer with
 * inline documentation.
 *
 * The caller passes in <paramref name="slotLabel"/> (the resolved thread name, or "Slot N" fallback) so the slot-lane help
 * opens with the right label in its title line.
 */
function getTrackHelpLines(trackId: string, slotLabel: string): string[] {
  if (trackId.startsWith('slot-')) {
    return [
      `Thread lane — ${slotLabel}`,
      '',
      'What\'s drawn:',
      '  Top strip (if present): scheduler chunks — one colored bar per',
      '    ECS system execution bound to this worker thread within the',
      '    current tick. Color is a stable hash of the system index.',
      '  Below: nested spans drawn as a flame graph, one row per depth,',
      '    colored by a stable hash of the span name.',
      '',
      'How to read it:',
      '  Bar width is proportional to duration (X = µs).',
      '  Depth increases downward — depth 0 is the outermost instrumented',
      '    operation (e.g. Transaction.Commit), deeper rows are its nested',
      '    calls (BTree.Insert, PagedMMF.GetPage, ...).',
      '  Adjacent <=1 px spans at the same depth are coalesced into a grey',
      '    block labeled "N spans — zoom in" so the row stays readable at',
      '    wide time ranges. Zoom in to resolve individual spans.',
      '',
      'Interaction:',
      '  Click a chunk or span → details in the right pane.',
      '  Double-click → animated zoom to that span\'s time range.',
    ];
  }

  switch (trackId) {
    case 'gauge-gc':
      return [
        'GC activity timeline',
        '',
        'Shows how much of each tick was lost to stop-the-world GC',
        'pauses, plus marker events for every GC start/end. Sibling',
        'Memory track shows heap state (per-gen sizes); this track',
        'shows collection EVENTS and their latency impact.',
        '',
        'What\'s drawn:',
        '  Red-orange bars — one per tick that had any GC pause.',
        '    X-width = the tick\'s time range, height = µs of pause',
        '    inside that tick. Tall bars = latency-hit ticks.',
        '    Ticks with no GC pause render as a gap (no bar).',
        '  Triangle marker — GcStart event, colored by generation:',
        '    Gen0 teal / Gen1 green / Gen2+ yellow.',
        '  Dot marker — GcEnd event, colored by generation (same',
        '    palette as the triangles).',
        '',
        'How to read it:',
        '  A bar of, say, 12 ms on a tick whose budget is 16 ms',
        '    means GC consumed 75% of the frame — a direct latency hit.',
        '  Gen0 markers cluster frequently — normal.',
        '  Gen2+ markers are rare but usually accompany tall bars',
        '    (big pauses). Flag as risk when they land in latency-',
        '    sensitive ticks.',
        '  Cross-check with Memory track — a Gen2 pause usually',
        '    coincides with a drop in the Gen2 stacked area there.',
      ];

    case 'gauge-memory':
      return [
        'Memory gauge',
        '',
        'Total process memory footprint — both managed (GC heap) and',
        'unmanaged (NativeMemory via PinnedMemoryBlock, buffer pools,',
        'pinned page cache). Snapshot sampled once per scheduler tick.',
        '',
        'What\'s drawn:',
        '  Row 1 (top) — stacked-area of heap gens, bottom-to-top:',
        '    Gen0, Gen1, Gen2, LOH, POH — managed heap generations.',
        '  Row 2 (middle) — unmanaged area chart + dashed peak reference',
        '    line. The dashed line sits at max(unmanaged) × 1.1 / 1.1',
        '    = the session\'s observed peak; it only moves up, never down.',
        '    When the area touches the dashed line, you\'re at a new peak.',
        '  Row 3 (bottom) — live-block count line (thin grey) + one marker',
        '    glyph per allocation / free event. Triangle-UP = alloc,',
        '    triangle-DOWN = free. Color encodes the source subsystem',
        '    (WAL / PageCache / StagingPool / ComponentTable / etc. —',
        '    see MemoryAllocSource enum).',
        '',
        'How to read it:',
        '  Total height (rows 1+2) = current resident working set.',
        '  Unmanaged slice climbing = ComponentTable growth, page cache',
        '    expansion, or buffer-pool churn.',
        '  If unmanaged area is close to the dashed peak line, you\'re at',
        '    the session\'s high-water mark — expect eviction pressure',
        '    if total memory approaches the process budget.',
        '',
        'Interaction:',
        '  Click any marker → detail pane shows the event\'s direction',
        '    (Alloc / Free), source subsystem, size, running total after',
        '    the event, and the thread slot the allocation happened on.',
        '  Hit-test uses a 5 px time radius; the nearest marker within',
        '    that window is picked.',
        '',
        'Instrumentation note:',
        '  Marker emission is gated by TelemetryConfig.ProfilerMemoryAll-',
        '  ocationsActive — when ON, EVERY NativeMemory alloc and free',
        '  produces a marker (there is no size threshold). At high-',
        '  throughput workloads this can flood the producer ring; if',
        '  you see "Total dropped" in the runner output, consider',
        '  disabling memory-alloc profiling for that session.',
      ];

    case 'gauge-persistence':
      return [
        'Page Cache gauge',
        '',
        'Per-tick snapshot of the PagedMMF cache bucket population.',
        'Total height = the cache\'s fixed page capacity.',
        '',
        'What\'s drawn (stacked, bottom-to-top, mutually exclusive):',
        '  Free        — pages currently unallocated; pool residue.',
        '  CleanUsed   — resident + fully flushed to disk; safe to evict.',
        '  DirtyUsed   — resident, not yet flushed; DC > 0 blocks eviction',
        '                until checkpoint drains them.',
        '  Exclusive   — pinned by an active UoW (ACW > 0). Neither',
        '                evictable nor visible to the checkpoint snapshot.',
        '',
        'How to read it:',
        '  DirtyUsed climbing = checkpoint is not draining fast enough;',
        '    you\'re approaching the dirty-page backpressure point.',
        '  Exclusive steady & small = normal transactional workload.',
        '  Exclusive climbing = a long-running UoW is holding pages; if',
        '    it keeps climbing, suspect a stuck transaction.',
        '  Free shrinking toward 0 = cache is near capacity — next page',
        '    fault triggers eviction of a CleanUsed page.',
      ];

    case 'gauge-transient':
      return [
        'Transient Store gauge',
        '',
        'Per-tick snapshot of the pinned-heap bytes currently held by',
        'every live TransientStore in the engine. Transient storage is',
        'the heap-backed (non-persisted) page pool that backs components',
        'declared with StorageMode.Transient — no WAL, no checkpoint, no',
        'MVCC revision chain, no page-cache eviction. Pages are pinned',
        'heap allocations held until the store is disposed.',
        '',
        'What\'s drawn:',
        '  One stacked area: aggregated "Used" bytes across all live',
        '    transient stores, sampled once per scheduler tick.',
        '  Y axis auto-scales to the max observed value. There is NO',
        '    capacity reference line — each TransientStore has its own',
        '    TransientOptions.MaxMemoryBytes cap, and a per-store ceiling',
        '    on an aggregated "used" series would mislead (25% aggregate',
        '    can still mean one store is saturated).',
        '',
        'How to read it:',
        '  Total = Σ (PageCount × 8 KB) across every live store.',
        '  Breakdown: each ComponentTable with StorageMode.Transient',
        '    contributes 3 stores (component data + default index +',
        '    string64 index). Each cluster-eligible archetype also',
        '    contributes one cluster-transient store.',
        '  Monotonic climbing = transient pages are being allocated but',
        '    nothing is releasing them (stores only free on Dispose).',
        '    If a run needs long-lived transient data this is normal;',
        '    if you expect stable population, look for leaks.',
        '  Sudden drop = a ComponentTable or engine was disposed.',
        '',
        'Absent track = no ComponentTable with StorageMode.Transient',
        'and no cluster-eligible archetype is present — aggregated total',
        'is 0 and the emitter skips the gauge entirely.',
      ];

    case 'gauge-wal':
      return [
        'WAL gauge',
        '',
        'Per-tick snapshot of the Write-Ahead Log state. The WAL is the',
        'durability path for committed transactions — every commit appends',
        'a record and fsyncs it before returning to the caller. Drawn as',
        'two stacked sub-rows sharing the viewport\'s X axis.',
        '',
        'Row 1 (top — ocean-blue area chart):',
        '  Commit buffer bytes — how much of WalManager.CommitBuffer is',
        '    currently occupied (rising between fsyncs, dropping when a',
        '    flush completes and frees frames).',
        '  Dashed ocean-blue line — commit-buffer capacity (fixed at init,',
        '    drawn only if the engine reported BufferCapacity). Shows the',
        '    ceiling the area can never cross.',
        '  Y axis: bytes.',
        '',
        'Row 2 (bottom — overlaid line chart):',
        '  Mustard line — Inflight frames: commit frames submitted but not',
        '    yet marked durable. Nonzero = fsync is in flight or queued.',
        '  Green line — Staging rented: how many staging buffers are',
        '    currently borrowed from StagingBufferPool (a pooled rent/return',
        '    allocator for short-lived commit staging blocks).',
        '  Dashed yellow line — Staging peak: the session HIGH-WATER MARK',
        '    of staging rentals. It is monotonically non-decreasing by',
        '    design, so once the peak is reached it stays flat for the rest',
        '    of the trace. Useful as a "how close to capacity did staging',
        '    get in the worst moment?" reference — compare against the pool',
        '    capacity gauge to decide if the pool is sized correctly.',
        '  Y axis: count (shared between the two live lines + peak).',
        '',
        'How to read it:',
        '  Row 1 area climbing toward the dashed capacity line = commits',
        '    are outpacing the fsync cadence; commits may start stalling',
        '    on back-pressure. Check the Transactions track for Commit',
        '    duration spikes.',
        '  Row 2 mustard line climbing above green = lots of frames in',
        '    flight per staging rental (big batches). Climbing mustard with',
        '    stable green = fsync latency rising without pool pressure.',
        '  Row 2 green line pushing up against the yellow peak and staying',
        '    there = workload consistently hits the peak rental count;',
        '    sudden spikes above the peak SHOULD NOT happen (peak tracks',
        '    the max). If they do, the peak gauge wasn\'t emitted.',
        '',
        'If the entire track says "enable WAL (DurabilityMode ≠ None)",',
        'the engine is running with WAL disabled and none of these',
        'gauges have data.',
      ];

    case 'gauge-tx-uow':
      return [
        'Transactions + UoW gauge',
        '',
        'Per-tick snapshot of the transaction / unit-of-work subsystem.',
        'Transactions are thread-affine — one UoW per tick, one Transaction',
        'per system inside that UoW. Drawn as two stacked sub-rows sharing',
        'the viewport\'s X axis.',
        '',
        'Row 1 (top — Tx):',
        '  Navy filled area — Tx active count: transactions currently open',
        '    at this tick. This is the "load" signal — the size of the live',
        '    transaction set snapshot at each scheduler tick.',
        '  Blue line   — Tx created/tick:   ΔTxChainCreatedTotal / tick',
        '  Green line  — Tx commits/tick:   ΔTxChainCommitTotal / tick',
        '  Yellow line — Tx rollbacks/tick: ΔTxChainRollbackTotal / tick',
        '  Y axis: count (shared between area + lines on this row).',
        '',
        'Row 2 (bottom — UoW):',
        '  Deep-indigo area — UoW active count: units-of-work currently',
        '    open at this tick.',
        '  Teal line    — UoW created/tick:   ΔUowRegistryCreatedTotal / tick',
        '  Mustard line — UoW committed/tick: ΔUowRegistryCommittedTotal / tick',
        '  Y axis: count (shared between area + lines on this row).',
        '',
        'Hover tooltip also surfaces (not drawn on-chart):',
        '  Tx pool (idle) — pooled-but-unused Transaction instances; shows',
        '    pool sizing headroom relative to active count.',
        '  UoW void      — UoWs reserved but abandoned / voided; nonzero',
        '    is diagnostic of canceled operations.',
        '',
        'How to read it:',
        '  Tx active area tracks with the scheduler\'s system count in',
        '    steady state — big divergence = systems skipping their',
        '    transactions.',
        '  Commits line consistently close to created line = healthy',
        '    throughput. Gap between them = long-lived Tx holding work.',
        '  Rollbacks should be near zero in healthy workloads. Nonzero',
        '    during checkpoint = a UoW was cancelled to let the checkpoint',
        '    snapshot take exclusive access.',
        '  UoW committed/tick < UoW created/tick for several ticks = UoW',
        '    backlog growing — check Transactions mini-row below for',
        '    Persist durations.',
      ];

    case 'phases':
      return [
        'Phases',
        '',
        'Scheduler phase timeline. Each tick is divided into phases by the',
        'DagScheduler: Parallel, Serial, Writer, Barrier, and the checkpoint',
        'phases. The phase bars show when each phase started and how long',
        'it ran.',
        '',
        'What\'s drawn:',
        '  One horizontal bar per phase per tick, colored with the scheduler',
        '  accent (PHASE_COLOR). Width ∝ duration.',
        '  When the bar is wider than ~50 px, the phase name + duration is',
        '  rendered inline.',
        '',
        'How to read it:',
        '  Parallel phase is typically the widest — that\'s where worker',
        '    threads execute non-conflicting systems in parallel.',
        '  Serial / Writer phase narrow = low exclusive write load.',
        '  Barrier phase narrow = good; wide = many systems waiting on',
        '    the previous phase to drain.',
        '',
        'Interaction:',
        '  Hover shows the phase name + duration. Double-click zooms to',
        '  that phase\'s time range.',
      ];

    case 'page-cache':
      return [
        'Page Cache operations',
        '',
        'Four mini-rows of discrete PagedMMF cache events, one bar per',
        'operation. Complements the Page Cache GAUGE above (which shows',
        'bucket populations) by showing the events that MOVE pages between',
        'buckets.',
        '',
        'Rows (top-to-bottom):',
        '  Fetch    — a thread requested a page that was already resident.',
        '             Fast path, no disk I/O.',
        '  Allocate — a new page was allocated in the cache (either a page',
        '             fault that read from disk, or a Grow() on a segment).',
        '  Evicted  — a CleanUsed page was reclaimed to make room.',
        '  Flush    — a DirtyUsed page was written to disk by checkpoint.',
        '',
        'How to read it:',
        '  Bar width = operation duration. Adjacent <=1 px operations',
        '    coalesce into a grey block ("N ops") so the row stays readable',
        '    at wide time ranges.',
        '  Evicted bars appearing = cache is pressure-eviction driven.',
        '  Flush bars clustered = checkpoint cycle is running.',
        '',
        'Hover any bar to see op count + per-op list with slot + duration.',
      ];

    case 'disk-io':
      return [
        'Disk IO',
        '',
        'Raw disk read/write operations, one bar per I/O. Two mini-rows:',
        '  Reads   — page-fault reads pulling data into the cache.',
        '  Writes  — page flushes + WAL fsyncs writing out to disk.',
        '',
        'How to read it:',
        '  Bar width = op duration. Clusters of writes = checkpoint in',
        '    progress or WAL fsync batching.',
        '  Reads in steady-state workload = cache miss pressure; consider',
        '    increasing the cache size.',
        '',
        'Adjacent tiny ops coalesce into grey "N ops" blocks. Hover shows',
        'op count + per-op list.',
      ];

    case 'transactions':
      return [
        'Transactions',
        '',
        'Discrete transaction lifecycle events, one bar per event.',
        'Three mini-rows:',
        '  Commits    — Transaction.Commit spans (hot path for writes).',
        '  Rollbacks  — Transaction.Rollback spans (error / conflict path).',
        '  Persists   — WAL persistence call — the point at which the commit',
        '               becomes durable from the writer\'s perspective.',
        '',
        'How to read it:',
        '  Commit bar width = full commit duration (includes WAL wait).',
        '    Wide bars = WAL back-pressure or lock contention.',
        '  Rollback bars should be rare in healthy workloads.',
        '  A Persist bar always follows its Commit bar; the gap between',
        '    them is the WAL fsync wait.',
        '',
        'Hover shows the op name, duration, and thread slot; async ops',
        'also show the kickoff-vs-completion split.',
      ];

    case 'wal':
      return [
        'WAL operations',
        '',
        'Two mini-rows of Write-Ahead Log events:',
        '  Flushes  — WAL flush spans. Duration = fsync cost on this tick.',
        '  Waits    — threads blocked waiting for a flush to complete.',
        '',
        'How to read it:',
        '  Flush bars wide = slow fsync (disk latency, OS page-cache pressure).',
        '  Waits stacking up = commits are queueing behind a slow flush;',
        '    commit latency tail will be visible in the Transactions row.',
        '',
        'Adjacent tiny ops coalesce into grey "N ops" blocks. Hover shows',
        'op count + per-op list.',
      ];

    case 'checkpoint':
      return [
        'Checkpoint cycles',
        '',
        'One mini-row showing each complete checkpoint pass — snapshot',
        '→ flush-dirty → WAL-segment-recycle. Typhon runs checkpoints',
        'concurrently with application ticks; a cycle\'s duration spans',
        'many ticks.',
        '',
        'How to read it:',
        '  Bar start = snapshot begin (CAS on ActiveChunkWriters).',
        '  Bar end   = last dirty page flushed + WAL segments recycled.',
        '  Width ∝ cycle duration. Long bars = lots of DirtyUsed pages',
        '    to flush; see the Page Cache gauge for confirmation.',
        '',
        'Hover shows the cycle duration + slot that drove it.',
      ];

    default:
      return [
        'Track',
        '',
        'Hover a bar or event to see details. No help available for this',
        'track yet.',
      ];
  }
}

function shortenName(name: string): string {
  const parenIdx = name.indexOf('(');
  const base = parenIdx >= 0 ? name.substring(0, parenIdx) : name;
  const parts = base.split('.');
  return parts.length >= 2 ? parts.slice(-2).join('.') : base;
}

const _nameColorCache = new Map<string, string>();
/**
 * Pick a stable color for a nested span (Transaction.Commit, BTree.Insert, PageCache.DiskRead, etc.) from the dedicated
 * <see cref="SPAN_PALETTE"/>. Same hash-modulo pattern as <c>FlameGraph.nameToColor</c>: the same span name always resolves to the
 * same palette index across sessions, so a given span kind looks the same every time you open a trace. The cache avoids recomputing
 * the hash per-frame since the full-screen render paints hundreds of spans per tick.
 *
 * Routing both this function and <c>getSystemColor</c> through <c>SPAN_PALETTE</c> (instead of the main <c>PALETTE</c>) means every
 * span-like surface shares one palette — the timeline, the flame graph, and the system-colored parent chunks all draw from the same
 * warm dark-violet → amber ramp, visually distinct from the gauge region that uses the main Turbo palette.
 */
function nameToColor(name: string): string {
  let c = _nameColorCache.get(name);
  if (c !== undefined) return c;
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) - hash + name.charCodeAt(i)) | 0;
  }
  // `>>> 0` coerces to unsigned before the modulo — JS bitwise ops produce signed 32-bit ints that can be negative.
  const idx = (hash >>> 0) % SPAN_PALETTE.length;
  c = SPAN_PALETTE[idx];
  _nameColorCache.set(name, c);
  return c;
}

// Pending-chunk diagonal-stripe pattern. Built once per context (cached via WeakMap since each canvas has its own 2D context).
// Rendered over tick ranges whose chunks haven't landed yet so rapid panning doesn't leave empty holes — the user sees
// "something's loading here at this exact time range" instead of a blank space.
const _pendingPatternCache = new WeakMap<CanvasRenderingContext2D, CanvasPattern>();
function getPendingPattern(ctx: CanvasRenderingContext2D): CanvasPattern {
  const cached = _pendingPatternCache.get(ctx);
  if (cached) return cached;
  const tile = document.createElement('canvas');
  tile.width = 12; tile.height = 12;
  const tctx = tile.getContext('2d')!;
  tctx.fillStyle = 'rgba(48, 50, 54, 0.55)';   // pending-chunk fill — near-neutral dark grey with tiny blue tint
  tctx.fillRect(0, 0, 12, 12);
  tctx.strokeStyle = 'rgba(145, 150, 156, 0.35)';  // pending-chunk diagonal stripes — lighter grey, slight blue
  tctx.lineWidth = 1.5;
  tctx.beginPath();
  // Two diagonals per tile wrap cleanly across the 12 px edge so the stripe is continuous when the pattern repeats.
  tctx.moveTo(-2, 4); tctx.lineTo(8, -6);
  tctx.moveTo(-2, 16); tctx.lineTo(16, -2);
  tctx.moveTo(4, 16); tctx.lineTo(16, 4);
  tctx.stroke();
  const pat = ctx.createPattern(tile, 'repeat')!;
  _pendingPatternCache.set(ctx, pat);
  return pat;
}

// Coalesced-bar vertical-zig-zag pattern. Rendered over any flame-graph bar that represents N spans merged into one sub-pixel block, so the
// user can visually distinguish a coalesced block from a regular span without reading the text. Deliberately different geometry from the
// pending-chunk pattern (45° diagonal stripes): vertical zig-zag strands that repeat horizontally read as "bundle of packed threads marching
// down, many of them across" — the exact semantic of coalescing (many short spans squashed into one bar). Text on top stays readable because
// the zig-zag line is 1 px wide with 0.4 alpha, well under the text's full-opacity `#aaa` contrast, AND the strands run vertically while text
// is horizontal, so line strokes don't cross letter strokes.
//
// Tile geometry: 8 × 8 px. A single vertical zig-zag strand occupies x=0..3; the right half (x=3..8) is empty base. When the tile repeats
// horizontally, strands appear every 8 px — so a coalesced block shows a column of oblique strands, each marching top-to-bottom. 4 oblique
// segments per tile-height (no axis-aligned segments — all moves are diagonal). Endpoints at (0, 0) and (0, 8) match in X so vertical seams
// disappear when tiles stack downward.
const _coalescedPatternCache = new WeakMap<CanvasRenderingContext2D, CanvasPattern>();
function getCoalescedPattern(ctx: CanvasRenderingContext2D): CanvasPattern {
  const cached = _coalescedPatternCache.get(ctx);
  if (cached) return cached;
  const tile = document.createElement('canvas');
  tile.width = 8; tile.height = 8;
  const tctx = tile.getContext('2d')!;
  tctx.fillStyle = 'rgba(85, 85, 85, 0.5)';    // base — identical to the previous solid coalesced fill, preserves overall luminance
  tctx.fillRect(0, 0, 8, 8);
  tctx.strokeStyle = 'rgba(140, 140, 140, 0.5)'; // zig-zag accent — darkened slightly from the first pass so the pattern recedes and the
                                                 // brighter #ccc text reads as dominant; alpha bumped from 0.4 to 0.5 keeps the darker stroke
                                                 // visible against the rgba(85,…) base (contrast delta stays perceivable)
  tctx.lineWidth = 1;
  tctx.lineCap = 'butt';
  tctx.lineJoin = 'miter';
  tctx.beginPath();
  // Path: single vertical strand, 4 oblique segments zig-zagging between x=0 and x=3 as it descends. Endpoints at (0, 0) and (0, 8) match
  // so the strand flows seamlessly across vertical tile boundaries into the tile below — bar looks like one continuous zig-zag column, not a
  // stack of dashes. Horizontal tiling leaves x=3..8 untouched (base fill only), so strands read as distinct "columns" repeating every 8 px.
  tctx.moveTo(0, 0);
  tctx.lineTo(3, 2);
  tctx.lineTo(0, 4);
  tctx.lineTo(3, 6);
  tctx.lineTo(0, 8);
  tctx.stroke();
  const pat = ctx.createPattern(tile, 'repeat')!;
  _coalescedPatternCache.set(ctx, pat);
  return pat;
}

export function GraphArea({ trace, tracePath, viewRange, onViewRangeChange, selectedChunk, onChunkSelect, selectedSpan, onSpanSelect, onMarkerSelect, navHistory, gaugeRegionVisible, legendsVisible, onGutterWidthChange, chunkCacheRef }: GraphAreaProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const vpRef = useRef<Viewport>({ offsetX: viewRange.startUs, scaleX: 0.5, scrollY: 0 });
  const hoverRef = useRef<{ x: number; y: number; lines: TooltipLine[] } | null>(null);
  // Raw mouse X in canvas coordinates. Updated on every mouse move so the crosshair cursor line + timestamp label render
  // continuously, not just when hovering over an interactive element. -1 = cursor outside the canvas/gutter.
  const mouseXRef = useRef(-1);
  // Last-built track layout, stamped by the render loop. Mouse handlers (click, move, up, dblclick) read this instead of
  // re-invoking `buildLayout()` — the layout is deterministic for a given (trace, collapseState, activeSlots) tuple, which all
  // the handlers need to have already been rendered to act on anyway. At 50+ tracks on a large trace this turns ~1-2 ms of
  // handler latency per mouse event into essentially zero, and keeps the five handlers from duplicating buildLayout's work
  // against stale closure deps.
  //
  // Freshness: the render loop runs on every scheduleRender invocation, which happens on every state change that could alter
  // layout (collapseState, activeSlots, gaugeRegionVisible, trace prop). Between a setState and the next rAF, the ref is
  // stale for one frame — but so are the handlers' closures, so it's consistent. Initial render hasn't stamped yet: handlers
  // that fire before the first rAF get `null` and fall back to `buildLayout()` inline.
  const layoutRef = useRef<TrackLayout[] | null>(null);
  // Track id whose "?" help glyph is currently under the mouse, or null if none. Drives two affordances:
  //   1. The gutter-draw loop redraws that row's "?" in TEXT_COLOR (bright) instead of DIM_TEXT so the glyph reads as
  //      "this is interactive right now" — solves the discoverability problem of a same-tone-as-label dim glyph.
  //   2. The canvas-element cursor flips to `help` (OS-native affordance) so users see the glyph is clickable before
  //      they've even tested it by clicking.
  // Ref + scheduleRender pattern because per-row hover doesn't need React state re-renders (same mechanism as `hoverRef`).
  const hoveredHelpTrackIdRef = useRef<string | null>(null);
  const isDraggingRef = useRef(false);
  // 'pan' = middle-drag or shift+left-drag (old viewport-pan behavior). 'select' = left-drag zoom-to-selection.
  const dragModeRef = useRef<'pan' | 'select'>('select');
  const dragStartRef = useRef({ x: 0, y: 0, offsetX: 0, scrollY: 0 });
  // Zoom-to-selection overlay. Two canvas-space X coordinates; the render pass draws the overlay between them.
  const zoomSelRef = useRef<{ x1: number; x2: number } | null>(null);
  // (Nav history recording is centralized in App.tsx's handleViewRangeChange with a 500ms debounce.)
  // Animated zoom transition. When set, the render loop interpolates the viewport from `from` to `to` over 800 ms using
  // an ease-out curve. The animation self-clears when t reaches 1.
  const zoomAnimRef = useRef<{
    from: { startUs: number; endUs: number };
    to: { startUs: number; endUs: number };
    startTime: number;
  } | null>(null);
  const rafRef = useRef(0);
  // Gutter width is recomputed per-render by measuring every track label against the actual font metrics. Stored in a ref so the
  // mouse/effect callbacks (which run outside the render phase) can read the last-measured value without blocking on a re-render.
  // Starts at the floor; first render widens it to fit "Transaction + UoW" / long thread names / etc.
  const gutterWidthRef = useRef<number>(MIN_GUTTER_WIDTH);
  // Last value we pushed through <c>onGutterWidthChange</c>. Lets the render loop short-circuit redundant setState calls — only emit when
  // the measured width actually changed from what the parent already knows.
  const lastEmittedGutterWidthRef = useRef<number>(-1);
  // Gauge-group collapse state is seeded from localStorage on first render. Other track IDs (slot-N, phases, built-in categories)
  // are session-only — they pick up defaults inside buildLayout via the existing <c>collapseState[id] ?? false</c> pattern and don't
  // need to round-trip through storage.
  const [collapseState, setCollapseState] = useState<Record<string, TrackState>>(() => loadPersistedGaugeCollapseState());

  // Persist only the gauge-group slice. Skips when collapseState is empty (first mount after initial seed is still the seed) so we
  // don't overwrite localStorage with the same defaults immediately.
  useEffect(() => {
    persistGaugeCollapseState(collapseState);
  }, [collapseState]);

  // gaugeRegionVisible + legendsVisible are now App-owned (driven by the View menu + 'g'/'l' shortcuts registered in App.tsx)
  // and arrive as props — no local state, no keyboard handler here. Moving the handlers up lets the menu and the shortcuts
  // share a single source of truth and keeps render-region toggles consistent whether you use the keyboard or the menu.

  // Data-driven lane discovery. Each observed thread slot gets its own lane, regardless of whether the slot emitted scheduler
  // chunks, OTel spans, or both. A slot might be the main test thread emitting only spans, a thread-pool completion worker
  // emitting only async spans, or a scheduler worker emitting both — all three should show up as their own lane.
  //
  // We track three pieces of info per slot:
  //   - activeSlots: sorted list of all slot indices to render (ordered lane list)
  //   - slotsWithChunks: which of those slots ever emitted a scheduler chunk (drives whether to reserve a chunk row)
  //   - spanMaxDepthBySlot: deepest observed span depth per slot (drives span region height)
  //
  // slotsWithChunks is critical for layout correctness: a slot that NEVER emits chunks shouldn't waste TRACK_HEIGHT pixels at
  // the top of its lane for an empty chunk row, because the empty row visually looks like "depth 0 is blank" and pushes the
  // first real span down to where depth 1 should be. Only chunk-bearing slots reserve the chunk row.
  const { activeSlots, slotsWithChunks, spanMaxDepthBySlot } = useMemo(() => {
    const slotSet = new Set<number>();
    const withChunks = new Set<number>();
    const depthBySlot = new Map<number, number>();
    for (const tick of trace.ticks) {
      for (const chunk of tick.chunks) {
        slotSet.add(chunk.threadSlot);
        withChunks.add(chunk.threadSlot);
      }
      for (const [slot, d] of tick.spanMaxDepthByThreadSlot) {
        slotSet.add(slot);
        const prev = depthBySlot.get(slot) ?? -1;
        if (d > prev) depthBySlot.set(slot, d);
      }
    }
    return {
      activeSlots: Array.from(slotSet).sort((a, b) => a - b),
      slotsWithChunks: withChunks,
      spanMaxDepthBySlot: depthBySlot,
    };
  }, [trace]);
  const SPAN_ROW_HEIGHT = 22;

  // Sync viewport on external viewRange change
  useEffect(() => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    // Use the last-measured gutter width. On first mount (before any render has measured) this is MIN_GUTTER_WIDTH — the resulting
    // viewport will be slightly wider than ideal for that single frame; the next render corrects it.
    const contentWidth = rect.width - gutterWidthRef.current;
    const rangeUs = viewRange.endUs - viewRange.startUs;
    if (rangeUs > 0 && contentWidth > 0) {
      vp.offsetX = viewRange.startUs;
      vp.scaleX = contentWidth / rangeUs;
    }
    scheduleRender();
  }, [viewRange]);

  const buildLayout = useCallback((): TrackLayout[] => {
    const layout: TrackLayout[] = [];
    let y = 0;

    layout.push({ id: 'ruler', label: 'Time', y, height: RULER_HEIGHT, state: 'expanded', collapsible: false });
    // Include TRACK_GAP after the ruler so the first gauge track's accent stripe doesn't paint ON TOP of the ruler's separator line.
    // The separator is drawn at `ruler.y + ruler.trackAdvance - 0.5 = RULER_HEIGHT + TRACK_GAP - 0.5`; without this extra gap, the
    // first gauge starts at y=RULER_HEIGHT and its 2-px accent stripe overlaps (and visually bleeds above) the separator. Every other
    // track-to-track boundary already includes TRACK_GAP via the per-track advance math — this line matches that convention for the
    // ruler→gauge transition specifically.
    y += RULER_HEIGHT + TRACK_GAP;

    // Gauge region — sits directly under the ruler so the per-tick metrics share the X-axis with everything below. Each gauge group
    // is a collapsible <c>TrackLayout</c> entry (GC, Memory, Page Cache, Transient, WAL, Tx+UoW), rendered by its group-specific
    // renderer in <c>gaugeGroupRenderers.ts</c>. Gauge tracks are the only tracks where the 3-state `double` mode is available
    // — the summary-strip height here drives the gauge-summary spark-line rendering.
    y = appendGaugeTracks(layout, y, collapseState, SUMMARY_STRIP_HEIGHT, LABEL_ROW_HEIGHT, TRACK_GAP, gaugeRegionVisible);

    // Shared height helper for non-gauge collapsible tracks. Non-gauge tracks support only two user-reachable states
    // (summary / expanded) — the click-handler cycle skips `double` for them. Defensive fallback: if `double` is somehow
    // persisted, treat it as `expanded` here so no track ever ends up without a height.
    //
    // `stripInsideLabel` controls where the summary content sits:
    //   - `true` (slot lanes): strip renders INSIDE the label row's vertical band (same Y range as the label), so total
    //     summary footprint = LABEL_ROW_HEIGHT + TRACK_GAP. The strip is in the content area and the label is in the
    //     gutter — they don't overlap horizontally.
    //   - `false` (phases / page-cache / etc.): legacy 4-px dark strip rendered BELOW the label row. Total footprint =
    //     LABEL_ROW_HEIGHT + COLLAPSED_HEIGHT (no TRACK_GAP — the strip itself acts as the visual separator).
    const nonGaugeHeight = (state: TrackState, expandedBody: number, stripInsideLabel: boolean = false): number => {
      if (state === 'summary') {
        return stripInsideLabel
          ? LABEL_ROW_HEIGHT + TRACK_GAP
          : LABEL_ROW_HEIGHT + COLLAPSED_HEIGHT;
      }
      return expandedBody + TRACK_GAP;  // expanded + double fallback
    };
    // Resolve a non-gauge track's state with a default. Non-gauge tracks persist session-only (not saved to localStorage), so the
    // default branch fires the first time a trace loads. `expanded` is the default — we want users to see actual data on first open.
    //
    // IMPORTANT: non-gauge tracks are NOT valid in `double` state — the click-handler cycle for them skips double, but defensively
    // coerce `double → expanded` in case a future code path (or stale state from an earlier build that allowed it) ends up with
    // double on a non-gauge track. Without this coercion the track renders at 2× its normal body height: the `nonGaugeHeight`
    // helper falls through to the expanded-body branch for anything other than `summary`, so `double` would produce the same
    // layout footprint as `expanded` BUT the visual chevron would show "▼▼" and the click-handler would cycle into summary
    // next — jarring UX. Filter at the source.
    const nonGaugeState = (id: string): TrackState => {
      const s = collapseState[id];
      if (s === 'summary') return 'summary';
      return 'expanded';  // 'double' and anything unrecognized both fall through to expanded
    };

    // One lane per observed thread slot. Each lane contains an OPTIONAL chunk row at the top (only when the slot actually has
    // chunks) plus a variable-height span region below it, sized to fit the deepest span captured on that slot. Slots with no
    // spans render as a single chunk row; slots with no chunks AND spans render with their first depth row at the very top of
    // the lane (no empty chunk row). Spans draw starting at (trackY + chunkRowHeight) with per-depth vertical offset, so
    // setting chunkRowHeight to 0 for span-only slots means depth-0 spans land at ty exactly — no visual off-by-one.
    // Pull the slot→thread-name map off the trace metadata. Populated as ThreadInfo records (kind 77) flow through the chunk decoder;
    // sparse — slots for which no ThreadInfo has been observed fall back to the plain "Slot {n}" label.
    const threadNames = trace.metadata.threadNames;
    for (const slotIdx of activeSlots) {
      const id = `slot-${slotIdx}`;
      const state = nonGaugeState(id);
      const hasChunks = slotsWithChunks.has(slotIdx);
      const slotChunkRowHeight = hasChunks ? TRACK_HEIGHT : 0;
      const slotMaxDepth = spanMaxDepthBySlot.get(slotIdx);
      const spanRegionHeight = slotMaxDepth === undefined ? 0 : (slotMaxDepth + 1) * SPAN_ROW_HEIGHT + 2;
      // If a slot somehow has neither chunks nor spans (shouldn't happen — activeSlots filters against both) fall back to the
      // chunk-row height so the lane has at least some visible body.
      const expandedHeight = Math.max(TRACK_HEIGHT, slotChunkRowHeight + spanRegionHeight);
      // Slot lanes render the grey time-activity silhouette INSIDE the label row band (true → LABEL_ROW_HEIGHT + TRACK_GAP
      // total footprint). Other non-gauge tracks keep the legacy 4-px dark strip below the label.
      const h = nonGaugeHeight(state, expandedHeight, /* stripInsideLabel */ true);
      // Prefer the thread name when we have one — the slot index carries no information the name doesn't already imply, and the "Slot N — "
      // prefix overflowed the gutter on long names. Fall back to the plain slot label only when the ThreadInfo record didn't capture a name
      // (e.g., a thread-pool worker with its default name stripped, or an app that hasn't set Thread.CurrentThread.Name).
      const threadName = threadNames?.[slotIdx];
      const label = threadName ? threadName : `Slot ${slotIdx}`;
      layout.push({
        id,
        label,
        y,
        height: expandedHeight,
        state,
        collapsible: true,
        chunkRowHeight: slotChunkRowHeight,
      });
      y += h;
    }

    // Fixed-content non-gauge tracks below the slot lanes. All share the same state resolver + height helper above. Each track's
    // body height comes from its own MINI_ROW_HEIGHT-based constant.
    const pushFixedTrack = (id: string, label: string, expandedBody: number) => {
      const state = nonGaugeState(id);
      layout.push({ id, label, y, height: expandedBody, state, collapsible: true });
      y += nonGaugeHeight(state, expandedBody);
    };
    pushFixedTrack('phases',       'Phases',       PHASE_TRACK_HEIGHT);
    pushFixedTrack('page-cache',   'Page Cache',   4 * MINI_ROW_HEIGHT);
    pushFixedTrack('disk-io',      'Disk IO',      2 * MINI_ROW_HEIGHT);
    pushFixedTrack('transactions', 'Transactions', 3 * MINI_ROW_HEIGHT);
    pushFixedTrack('wal',          'WAL',          2 * MINI_ROW_HEIGHT);
    pushFixedTrack('checkpoint',   'Checkpoint',   MINI_ROW_HEIGHT);

    return layout;
  }, [activeSlots, slotsWithChunks, collapseState, spanMaxDepthBySlot, gaugeRegionVisible]);

  const getVisibleTicks = useCallback((): TickData[] => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return [];
    const rect = canvas.getBoundingClientRect();
    const contentWidth = rect.width - gutterWidthRef.current;
    // Defensive floor on scaleX — onWheel clamps to >=0.001 but external callers (zoom animation fallback, URL-driven
    // viewports) can reach this path with scaleX=0 and produce Infinity from the division below, cascading into NaN
    // axis labels and runaway tick-range queries. Clamp at the read site so every code path is safe.
    const safeScaleX = vp.scaleX > 0.001 ? vp.scaleX : 0.001;
    return getTicksInRange(trace, vp.offsetX, vp.offsetX + contentWidth / safeScaleX);
  }, [trace]);

  const render = useCallback(() => {
    const renderStart = performance.now();
    let rectCount = 0;
    let textCount = 0;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d')!;

    // Counting wrappers — intercept fillRect/fillText to count draw calls without touching every call site.
    const origFillRect = ctx.fillRect;
    const origFillText = ctx.fillText;
    ctx.fillRect = (x: number, y: number, w: number, h: number) => { rectCount++; origFillRect.call(ctx, x, y, w, h); };
    ctx.fillText = (text: string, x: number, y: number) => { textCount++; origFillText.call(ctx, text, x, y); };
    const { width, height } = setupCanvas(canvas);
    const vp = vpRef.current;

    // Build the layout and compute the dynamic gutter width FIRST so every downstream calculation (contentWidth, zoom animation's
    // scaleX, ruler positioning, clip rects) uses the measured value. Gutter width can change per-render if track labels change
    // (e.g., when ThreadInfo records land and lanes get renamed from "Slot N" to the real thread name). The ref lets mouse/effect
    // handlers outside this render phase read the last-measured value.
    const layoutEarly = buildLayout();
    layoutRef.current = layoutEarly;  // stamp for mouse handlers — see layoutRef declaration
    const GUTTER_WIDTH = computeGutterWidth(ctx, layoutEarly, legendsVisible);
    gutterWidthRef.current = GUTTER_WIDTH;
    // Notify parent (App.tsx) when the measured gutter width changes so siblings — specifically TickTimeline — can align their own affordances
    // (e.g. the help "?" glyph) with the gutter's right-edge X. Guarded by lastEmittedGutterWidthRef so steady-state renders don't bounce
    // setState every frame; queueMicrotask defers the setState out of the render phase to avoid "update during render" warnings.
    if (onGutterWidthChange && lastEmittedGutterWidthRef.current !== GUTTER_WIDTH) {
      lastEmittedGutterWidthRef.current = GUTTER_WIDTH;
      const cb = onGutterWidthChange;
      const w = GUTTER_WIDTH;
      queueMicrotask(() => cb(w));
    }
    const contentWidth = width - GUTTER_WIDTH;

    // ── Eager viewport sync ──
    // Without this, a parent-driven viewRange change (trace load, undo/redo, external "navigate to tick N") takes TWO paints
    // to reach the canvas correctly: the first paint uses stale `vpRef` values left from the previous trace/range and
    // produces a visibly-wrong frame (gauge samples mapped to the wrong X pixels, "ghost rect" across the GC / Memory rows).
    // The old `useEffect([viewRange])` runs only AFTER the first render commits, so there's always a one-frame flicker.
    //
    // Pulling the sync into render() closes that window — the render closure already captures viewRange via its useCallback
    // dep list, so the source of truth is available before any downstream draw primitive reads vp. During drag/wheel gestures
    // the imperative vp update + the resulting onViewRangeChange land vp and viewRange in agreement BEFORE the next rAF, so
    // the equality check below no-ops. The zoom-animation path owns vp exclusively (anim block below overwrites it) so we
    // skip the sync when an animation is active.
    if (zoomAnimRef.current === null) {
      const rangeUs = viewRange.endUs - viewRange.startUs;
      if (rangeUs > 0 && contentWidth > 0) {
        const expectedScaleX = contentWidth / rangeUs;
        if (Math.abs(vp.offsetX - viewRange.startUs) > 0.5 ||
            Math.abs(vp.scaleX - expectedScaleX) > 0.0001) {
          vp.offsetX = viewRange.startUs;
          vp.scaleX = expectedScaleX;
        }
      }
    }

    // ── Animated zoom transition ──
    // When zoomAnimRef is set, interpolate the viewport from `from` to `to` using an ease-out curve over 800 ms.
    // Each frame applies the interpolated range to the viewport and requests the next frame until t >= 1.
    const anim = zoomAnimRef.current;
    if (anim) {
      const elapsed = performance.now() - anim.startTime;
      const duration = 800;
      const rawT = Math.min(elapsed / duration, 1);
      // Ease-out cubic: fast start, smooth deceleration into the target.
      const t = 1 - (1 - rawT) * (1 - rawT) * (1 - rawT);
      const curStart = anim.from.startUs + (anim.to.startUs - anim.from.startUs) * t;
      const curEnd = anim.from.endUs + (anim.to.endUs - anim.from.endUs) * t;
      const curRange = curEnd - curStart;
      vp.offsetX = curStart;
      // If the animation target has identical start/end (degenerate zoom), `curRange = 0` and the division below would be
      // Infinity. Fall back to the existing scaleX — but also floor it to the same 0.001 minimum that `onWheel` enforces so
      // we never stall at an unusable near-zero zoom. Combined with the `getVisibleTicks` clamp, the viewport can't land in
      // an Infinity/NaN state regardless of how the animation starts.
      if (curRange > 0) {
        vp.scaleX = contentWidth / curRange;
      } else if (vp.scaleX < 0.001) {
        vp.scaleX = 0.001;
      }
      if (rawT >= 1) {
        zoomAnimRef.current = null;
        onViewRangeChange({ startUs: anim.to.startUs, endUs: anim.to.endUs });
        navHistory.setNavigating(false);
      } else {
        onViewRangeChange({ startUs: curStart, endUs: curEnd });
        // Schedule the next animation frame. Can't use scheduleRender here (circular dep), so direct rAF.
        cancelAnimationFrame(rafRef.current);
        rafRef.current = requestAnimationFrame(() => render());
      }
    }

    // Reuse the layout already built at the top of render — buildLayout is deterministic for the same inputs and a second call would
    // just allocate an identical array.
    const layout = layoutEarly;
    const visibleTicks = getVisibleTicks();

    ctx.fillStyle = BG_COLOR;
    ctx.fillRect(0, 0, width, height);

    // Early-return REMOVED (was: `if (visibleTicks.length === 0) fillText('No ticks in view'); return;`). Skipping the rest of
    // render meant the pending-chunk overlay (drawn later from trace.summary) never ran, so when a viewport pointed at ticks
    // whose chunks weren't loaded yet, the user saw a blank canvas with just a label — no grey-striped "data loading" feedback.
    // Letting render proceed with an empty visibleTicks is safe: every tick-based loop iterates it with a `for` which no-ops,
    // and the ruler's `visibleTicks[0]?.startUs ?? 0` fallback already handles the empty case. A centered "Loading ticks…" or
    // "No ticks in view" overlay is drawn at the END of render (after the pending pattern, so both are visible together).

    const pxOfUs = (us: number) => GUTTER_WIDTH + (us - vp.offsetX) * vp.scaleX;
    const visStartUs = vp.offsetX;
    const visEndUs = vp.offsetX + contentWidth / vp.scaleX;

    // ── Left gutter ──
    ctx.fillStyle = HEADER_BG;
    ctx.fillRect(0, 0, GUTTER_WIDTH, height);
    ctx.strokeStyle = BORDER_COLOR;
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(GUTTER_WIDTH - 0.5, 0); ctx.lineTo(GUTTER_WIDTH - 0.5, height); ctx.stroke();

    for (const track of layout) {
      const ty = track.y - vp.scrollY;
      ctx.fillStyle = DIM_TEXT;
      ctx.font = '10px monospace';
      ctx.textAlign = 'left';
      if (track.collapsible) {
        // Chevron reflects the 3-state expand level: ▶ = summary (minimal strip), ▼ = expanded (default body), ▼▼ = double-
        // expanded (gauge tracks only). Double uses two triangles to make "more expanded than single" unambiguous without
        // depending on emoji-family glyphs that render inconsistently across platforms.
        const chevron = track.state === 'summary' ? '\u25B6'
                      : track.state === 'double'  ? '\u25BC\u25BC'
                      : '\u25BC';
        ctx.fillText(`${chevron} ${track.label}`, 6, ty + 12);
      } else {
        ctx.fillText(track.label, 6, ty + 12);
      }
      // Per-row "?" help glyph, right-aligned in the gutter. Only painted when legends are visible — the feature is part of the
      // legend/legibility layer. Skipped for the non-collapsible "Time" row: with no chevron there, the "?" on the same row as
      // a standalone "Time" label looked out of place. Every other track (collapsible or not) gets one.
      if (legendsVisible && track.collapsible) {
        // Default tone: DIM_TEXT (matches the label) so the "?" sits as decoration, not a flashing call-to-action. When the
        // mouse is over THIS row's help hit zone, redraw in TEXT_COLOR (bright) — instant visual confirmation that the glyph
        // is interactive under the current cursor position.
        ctx.fillStyle = hoveredHelpTrackIdRef.current === track.id ? TEXT_COLOR : DIM_TEXT;
        ctx.font = '11px monospace';
        ctx.textAlign = 'right';
        // Glyph X = right edge of the gutter minus the pad. +1 keeps it slightly inset from the separator line so it doesn't
        // look like it's kissing the border.
        ctx.fillText('?', GUTTER_WIDTH - GUTTER_PAD_RIGHT + 1, ty + 12);
      }
      ctx.strokeStyle = BORDER_COLOR;
      ctx.lineWidth = 0.5;
      // Separator sits at the track's bottom edge. Advance calculation per state:
      //   summary + (gauge or slot) → strip renders INSIDE the label row band → advance = LABEL_ROW_HEIGHT + TRACK_GAP
      //   summary + other non-gauge → legacy 4-px dark strip BELOW label row → advance = LABEL_ROW_HEIGHT + COLLAPSED_HEIGHT
      //   expanded / double         → advance = track.height + TRACK_GAP (track.height already carries doubled body for `double`)
      const stripInsideLabel = GAUGE_TRACK_ID_SET.has(track.id) || track.id.startsWith('slot-');
      const trackAdvance = track.state === 'summary'
        ? (stripInsideLabel ? LABEL_ROW_HEIGHT + TRACK_GAP : LABEL_ROW_HEIGHT + COLLAPSED_HEIGHT)
        : (track.height + TRACK_GAP);
      const sepY = track.y + trackAdvance - vp.scrollY - 0.5;
      ctx.beginPath(); ctx.moveTo(0, sepY); ctx.lineTo(width, sepY); ctx.stroke();
    }

    // ── Content area ──
    ctx.save();
    ctx.beginPath(); ctx.rect(GUTTER_WIDTH, 0, contentWidth, height); ctx.clip();

    for (const track of layout) {
      const ty = track.y - vp.scrollY;

      if (track.id === 'ruler') {
        // ── Absolute anchor at the left edge + relative offset labels ──
        // The leftmost label shows the absolute elapsed time at the viewport's left edge (relative to the first tick's start).
        // All subsequent grid labels show offsets from that anchor: +100ms, +200ms, etc. This way the user reads "left edge
        // is at 8.00s, right edge is at 9.50s, the grid shows +0 / +250ms / +500ms / +750ms / +1.00s / +1.25s / +1.50s"
        // immediately without mental arithmetic.
        const gridStep = computeGridStep(visEndUs - visStartUs, contentWidth, 90);
        const baseUs = visibleTicks[0]?.startUs ?? 0;
        const leftEdgeUs = visStartUs;
        const gridStart = Math.ceil(leftEdgeUs / gridStep) * gridStep;

        // Left-edge absolute label — drawn in the gutter's content zone at the ruler row's Y position.
        ctx.fillStyle = TEXT_COLOR; ctx.font = '9px monospace'; ctx.textAlign = 'left';
        ctx.fillText(formatRulerLabel(leftEdgeUs - baseUs), GUTTER_WIDTH + 4, ty + 16);

        // Grid lines + offset labels
        for (let t = gridStart; t <= visEndUs; t += gridStep) {
          const x = pxOfUs(t);
          if (x < GUTTER_WIDTH + 60) continue;  // don't crowd the absolute anchor label
          ctx.strokeStyle = GRID_COLOR; ctx.lineWidth = 0.5;
          ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke();
          ctx.fillStyle = DIM_TEXT; ctx.font = '9px monospace'; ctx.textAlign = 'center';
          ctx.fillText(`+${formatRulerLabel(t - leftEdgeUs)}`, x, ty + 16);
        }

        // Adaptive per-tick tick-number step. Ticks don't share a uniform duration — a fast Transaction.Commit tick can be 100 µs while
        // an adjacent Checkpoint tick is 15 ms, so in the same viewport one region of the ruler can be packed (sub-pixel per tick) while
        // another is sparse (tens of pixels per tick). A single global step would either strip all labels from the sparse region or
        // overlap them in the dense region. Instead we walk visibleTicks left-to-right and decide per-tick:
        //   1. LOCAL step — computed from this tick's OWN pixel width (tick.durationUs × scaleX). Wider tick → smaller step (more
        //      labels); narrower tick → larger step (multiples of 5 / 25 / 100 / …).
        //   2. GREEDY spacing — the tick's label X must be at least <c>minLabelSpacingPx</c> beyond the previous label's X, otherwise
        //      we skip it regardless of niceness.
        //
        // Nice-steps progression ([1, 5, 10, 25, 50, …]) matches the user's "multiples of 5" convention. First visible tick is always
        // kept as an anchor so the ruler shows the current tick number even when the viewport starts mid-sequence. Both the dashed
        // boundary line and the T{number} label are gated on this same per-tick decision so the two visual elements always agree.
        //
        // Sizing: "T12345" renders at ~32 px in 9 px monospace; we require 40 px minimum gap so consecutive labels don't kiss.
        const minLabelSpacingPx = 40;
        const niceTickSteps = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 50000];
        const labeledTicks: typeof visibleTicks = [];
        let lastLabeledX = Number.NEGATIVE_INFINITY;
        for (const tick of visibleTicks) {
          const x = pxOfUs(tick.startUs);
          if (x < GUTTER_WIDTH) continue;
          const isAnchor = lastLabeledX === Number.NEGATIVE_INFINITY;
          if (!isAnchor && x - lastLabeledX < minLabelSpacingPx) continue;
          // Local step — how many ticks of THIS width would it take to span minLabelSpacingPx? Snap up to the next nice number.
          const pxThisTick = tick.durationUs * vp.scaleX;
          const localNeeded = pxThisTick > 0 ? Math.max(1, Math.ceil(minLabelSpacingPx / pxThisTick)) : 1;
          let localStep = niceTickSteps[niceTickSteps.length - 1];
          for (const s of niceTickSteps) { if (s >= localNeeded) { localStep = s; break; } }
          // Anchor always goes through; otherwise the tick number must land on the local step boundary.
          if (!isAnchor && tick.tickNumber % localStep !== 0) continue;
          labeledTicks.push(tick);
          lastLabeledX = x;
        }

        // Tick boundaries (dashed lines) — one per label, so the dash always sits under its own T{number}.
        ctx.setLineDash([3, 3]); ctx.strokeStyle = TICK_BOUNDARY_COLOR; ctx.lineWidth = 1;
        for (const tick of labeledTicks) {
          const x = pxOfUs(tick.startUs);
          ctx.beginPath(); ctx.moveTo(x, RULER_HEIGHT); ctx.lineTo(x, height); ctx.stroke();
        }
        ctx.setLineDash([]);
        // Tick-number labels share the ruler's typographic scale — same 9 px monospace and DIM_TEXT color as the "+time" offset labels
        // below them, so the two rows of ruler chrome read as one coherent strip. Center-aligned around the dashed boundary line so
        // each T# sits centered over its own vertical divider instead of floating to the right.
        ctx.fillStyle = DIM_TEXT; ctx.font = '9px monospace'; ctx.textAlign = 'center';
        for (const tick of labeledTicks) {
          const x = pxOfUs(tick.startUs);
          ctx.fillText(`${tick.tickNumber}`, x, ty + 8);
        }
        continue;
      }

      // ── Summary state ──
      // Three different renderings based on track kind:
      //   • gauge tracks → dim grey spark-line of their primary series (heartbeat preview of the main signal).
      //   • slot lanes   → grey time-activity silhouette (chunks + spans drawn as opaque grey rects at full strip height,
      //                    overlap is intentional — the union of activity intervals is what matters).
      //   • other non-gauge (phases / page-cache / disk-io / transactions / wal / checkpoint) → legacy 4-px dark strip
      //                    since those tracks don't have a canonical summary signal beyond "data exists here."
      if (track.state === 'summary') {
        // Strip sits INSIDE the label row's vertical band — label is in the gutter, strip is in the content area, both
        // occupy the same [ty .. ty+LABEL_ROW_HEIGHT] Y range. Centering via SUMMARY_STRIP_TOP_PAD places the 14-px strip
        // in the middle of the 18-px band, leaving 2 px of breathing room above and below.
        if (GAUGE_TRACK_ID_SET.has(track.id)) {
          drawGaugeSummaryStrip(ctx, trace, track.id, vp,
            { x: GUTTER_WIDTH, y: ty + SUMMARY_STRIP_TOP_PAD, width: contentWidth, height: SUMMARY_STRIP_HEIGHT },
            GUTTER_WIDTH);
        } else if (track.id.startsWith('slot-')) {
          const threadSlot = parseInt(track.id.split('-')[1]);
          const stripTop = ty + SUMMARY_STRIP_TOP_PAD;
          const stripH = SUMMARY_STRIP_HEIGHT;
          // Clip to the strip rect so any wide span near the viewport edge doesn't bleed into neighboring tracks.
          ctx.save();
          ctx.beginPath();
          ctx.rect(GUTTER_WIDTH, stripTop, contentWidth, stripH);
          ctx.clip();
          // Single grey color, single alpha — the union of activity is what reads; individual spans within that union
          // collapse to the same pixel color anyway. 0.55 alpha keeps the strip visible as a distinct band without
          // competing with the track-separator line below.
          ctx.fillStyle = 'rgba(170, 170, 170, 0.55)';
          const viewStartUs = vp.offsetX;
          const viewEndUs   = vp.offsetX + contentWidth / vp.scaleX;
          for (const tick of visibleTicks) {
            // Chunks on this slot
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              const x1 = pxOfUs(chunk.startUs);
              const x2 = pxOfUs(chunk.endUs);
              if (x2 < GUTTER_WIDTH || x1 > width) continue;
              const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
              ctx.fillRect(x1, stripTop, w, stripH);
            }
            // Spans on this slot — bounded iteration via binary search on the per-slot endMax table, same pattern the
            // expanded renderer uses. Depth is intentionally ignored for the silhouette; every span draws at full strip height.
            const slotSpans = tick.spansByThreadSlot.get(threadSlot);
            if (!slotSpans || slotSpans.length === 0) continue;
            const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
            if (!endMax) continue;
            let lo = 0, hi = slotSpans.length;
            while (lo < hi) {
              const mid = (lo + hi) >>> 1;
              if (endMax[mid] < viewStartUs) lo = mid + 1; else hi = mid;
            }
            for (let i = lo; i < slotSpans.length; i++) {
              const span = slotSpans[i];
              if (span.startUs > viewEndUs) break;
              const x1 = pxOfUs(span.startUs);
              const x2 = pxOfUs(span.endUs);
              if (x2 < GUTTER_WIDTH) continue;
              const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
              ctx.fillRect(x1, stripTop, w, stripH);
            }
          }
          ctx.restore();
        } else {
          ctx.fillStyle = '#222';
          ctx.fillRect(GUTTER_WIDTH, ty + LABEL_ROW_HEIGHT, contentWidth, COLLAPSED_HEIGHT);
        }
        continue;
      }

      // ── Gauge region tracks ──
      // Each gauge group has a known ID (see gaugeRegion.GAUGE_TRACK_IDS). Dispatch to the per-group renderer in gaugeGroupRenderers.ts
      // which pulls the relevant gauges from the trace and calls the gaugeDraw primitives. When a group has no data in the trace
      // (e.g., WAL disabled, workload never used Transient storage) the renderer falls back to a subdued "no data" message. State is
      // expanded or double at this point — `track.height` already carries the doubled body from appendGaugeTracks when in double mode,
      // so the renderer transparently renders taller thanks to splitRows proportional scaling.
      if (GAUGE_TRACK_ID_SET.has(track.id)) {
        const spec = getGaugeGroupSpec(track.id);
        const renderer = GROUP_RENDERERS[track.id];
        if (spec !== undefined && renderer !== undefined) {
          // The track body starts BELOW the label row and runs for track.height pixels. Gauge X-axis stays aligned with the rest
          // of the viewport because all toPixelX calls share the same vp.offsetX.
          renderer({
            ctx,
            trace,
            vp,
            labelWidth: GUTTER_WIDTH,
            layout: { x: GUTTER_WIDTH, y: ty, width: contentWidth, height: track.height },
            spec,
            legendsVisible,
          });
        }
        continue;
      }

      // ── Thread-slot lanes ──
      // A slot lane has two vertical regions:
      //   [ty .. ty + chunkRowHeight]       — scheduler chunk bar row
      //   [ty + chunkRowHeight .. trackBottom] — span rows, one per depth, drawn beneath the chunk row
      // Both regions share the same horizontal viewport transform. Spans and chunks on the same slot naturally interleave in
      // time because they share a single thread's timeline.
      if (track.id.startsWith('slot-')) {
        const threadSlot = parseInt(track.id.split('-')[1]);
        const chunkRowHeight = track.chunkRowHeight ?? 0;
        const spanRegionTop = ty + chunkRowHeight;
        const trackBottom = ty + track.height;

        // ── Chunk bar row (top of lane) ── only when this slot actually has chunks to draw. Span-only slots skip the entire
        // chunk pass so there's no phantom empty row before the first span.
        if (chunkRowHeight > 0) {
          for (const tick of visibleTicks) {
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              const x1 = pxOfUs(chunk.startUs);
              const x2 = pxOfUs(chunk.endUs);
              if (x2 < GUTTER_WIDTH || x1 > width) continue;
              const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
              ctx.fillStyle = getSystemColor(chunk.systemIndex);
              ctx.fillRect(x1, ty + 1, w, chunkRowHeight - 2);
              if (selectedChunk && chunk.systemIndex === selectedChunk.systemIndex &&
                  chunk.chunkIndex === selectedChunk.chunkIndex && chunk.threadSlot === selectedChunk.threadSlot &&
                  Math.abs(chunk.startUs - selectedChunk.startUs) < 0.01) {
                ctx.strokeStyle = '#fff'; ctx.lineWidth = 2;
                ctx.strokeRect(x1, ty + 1, w, chunkRowHeight - 2);
              }
              if (w > 40) {
                ctx.save();
                ctx.beginPath();
                ctx.rect(x1, ty + 1, w, chunkRowHeight - 2);
                ctx.clip();
                ctx.fillStyle = '#000'; ctx.font = '10px monospace'; ctx.textAlign = 'left';
                const label = chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName;
                ctx.fillText(label, x1 + 3, ty + chunkRowHeight / 2 + 3);
                ctx.restore();
              }
            }
          }
        }

        // ── Span rows (below chunk bar) ──
        // Each slot carries its own pre-sorted span array + running-max endUs table, so we binary-search the same way the
        // previous unified OTel track did, but per-slot. Rows are laid out by span.depth so the stable depth axis from
        // traceModel.ts is preserved — a given span always renders at a fixed row within its slot lane.
        const viewStartUs = vp.offsetX;
        const viewEndUs = vp.offsetX + contentWidth / vp.scaleX;
        ctx.font = '11px monospace';
        ctx.textAlign = 'left';

        for (const tick of visibleTicks) {
          const slotSpans = tick.spansByThreadSlot.get(threadSlot);
          if (!slotSpans || slotSpans.length === 0) continue;
          const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
          if (!endMax) continue;

          // Binary search for left-edge visibility.
          let lo = 0, hi = slotSpans.length;
          while (lo < hi) {
            const mid = (lo + hi) >>> 1;
            if (endMax[mid] < viewStartUs) lo = mid + 1; else hi = mid;
          }

          // Per-depth coalescing with INDEPENDENT state per depth level. Spans are sorted by startUs, not by depth, so
          // bars at different depths interleave constantly in the iteration order. The old single-depth state machine
          // flushed every time the depth changed — which was every other bar in a typical hierarchy — so runs never
          // reached 2 and coalescing never kicked in. Tracking state per depth independently means depth-0 bars
          // accumulate into a depth-0 run even when depth-1 and depth-2 bars appear between them in the sorted stream.
          //
          // Fixed-size arrays indexed by depth (max 8 levels — anything deeper is vanishingly rare in Typhon traces
          // and falls through to a clamped bucket). No heap allocation per frame.
          const { x1: coalX1, x2: coalX2, count: coalCount, sy: coalSy } = _coalPool;
          coalX1.fill(0); coalX2.fill(0); coalCount.fill(0); coalSy.fill(0);
          let prevFill = '';

          const flushDepth = (d: number) => {
            if (coalCount[d] >= 2) {
              const cw = Math.max(coalX2[d] - coalX1[d], 2);
              // Vertical-stripe pattern distinguishes coalesced blocks from normal span bars at a glance. The pattern is an OBJECT
              // (not a color string) so the <c>prevFill</c> fast-path comparison below never matches — we poison it with a sentinel
              // so the next per-span color assignment treats fillStyle as dirty and re-assigns. Without that, subsequent spans
              // would inherit the pattern on their bars.
              ctx.fillStyle = getCoalescedPattern(ctx);
              prevFill = '__pattern__';
              ctx.fillRect(coalX1[d], coalSy[d], cw, SPAN_ROW_HEIGHT - 2);
              if (cw > 50) {
                // Text color brightened to #ccc so it dominates the now-darker zig-zag pattern behind it.
                // Y offset: canvas default textBaseline is 'alphabetic', so the y coordinate is the text baseline. For a 9 px monospace
                // font inside a (SPAN_ROW_HEIGHT - 2)=20 px bar, baseline at coalSy + 13 centers the text cell vertically (ascent ~8,
                // descent ~2 → cell center = baseline - 3 = 10 = bar center). Previous -7 placed baseline at 15, visually ~2 px too low.
                ctx.fillStyle = '#ccc'; ctx.font = '9px monospace'; ctx.textAlign = 'left';
                ctx.fillText(`${coalCount[d]} spans — zoom in`, coalX1[d] + 3, coalSy[d] + SPAN_ROW_HEIGHT - 9);
                prevFill = '#ccc';
              }
            }
            coalCount[d] = 0;
          };

          const flushAllDepths = () => {
            for (let d = 0; d < COAL_MAX_DEPTH; d++) flushDepth(d);
          };

          for (let i = lo; i < slotSpans.length; i++) {
            const span = slotSpans[i];
            if (span.startUs > viewEndUs) break;
            const x1 = pxOfUs(span.startUs);
            const x2 = pxOfUs(span.endUs);
            if (x2 < GUTTER_WIDTH) continue;

            const depth = span.depth ?? 0;
            const d = depth < COAL_MAX_DEPTH ? depth : COAL_MAX_DEPTH - 1;
            const sy = spanRegionTop + depth * SPAN_ROW_HEIGHT;
            if (sy + SPAN_ROW_HEIGHT - 2 > trackBottom) continue;

            const actualWidth = x2 - x1;
            const w = actualWidth < MIN_RECT_WIDTH ? MIN_RECT_WIDTH : actualWidth;

            if (actualWidth <= 1) {
              // Try to extend the run at this depth.
              if (coalCount[d] > 0 && x1 <= coalX2[d] + 1) {
                coalCount[d]++;
                if (x2 > coalX2[d]) coalX2[d] = x2;
                if (coalX2[d] < coalX1[d] + 1) coalX2[d] = coalX1[d] + 1;
                continue;
              }
              // Can't extend — flush this depth's old run, draw bar individually, start new run at this depth.
              flushDepth(d);
              { const c = nameToColor(span.name); if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; } }
              ctx.fillRect(x1, sy, w, SPAN_ROW_HEIGHT - 2);
              coalX1[d] = x1;
              coalX2[d] = x1 + w;
              coalSy[d] = sy;
              coalCount[d] = 1;
              continue;
            }

            // Wide bar — flush only THIS depth's pending run, then draw individually.
            flushDepth(d);

            { const c = nameToColor(span.name); if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; } }
            ctx.fillRect(x1, sy, w, SPAN_ROW_HEIGHT - 2);

            if (selectedSpan && selectedSpan.spanId !== undefined && selectedSpan.spanId === span.spanId) {
              ctx.strokeStyle = '#fff';
              ctx.lineWidth = 2;
              ctx.strokeRect(x1, sy, w, SPAN_ROW_HEIGHT - 2);
            }

            if (actualWidth > 10) {
              ctx.save();
              ctx.beginPath();
              ctx.rect(x1, sy, actualWidth, SPAN_ROW_HEIGHT - 2);
              ctx.clip();
              ctx.fillStyle = '#eee';
              ctx.font = '11px monospace';
              // Same vertical-centering fix as the coalesced text a few lines up: baseline at sy + 13 places the 11 px cell center at
              // bar center (sy + 10). The offset is the same (−9) as for the 9 px coalesced font because ascent/descent scale
              // proportionally with font size, and both fonts render into the same 20-px bar. Previous −7 left the text ~2 px too low.
              ctx.fillText(`${span.name} (${formatDuration(span.durationUs)})`, x1 + 3, sy + SPAN_ROW_HEIGHT - 9);
              ctx.restore();
            }
          }
          flushAllDepths();
        }
      }

      // ── Phases track ──
      if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            const x1 = pxOfUs(phase.startUs);
            const x2 = pxOfUs(phase.endUs);
            if (x2 < GUTTER_WIDTH || x1 > width) continue;
            const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
            ctx.fillStyle = PHASE_COLOR;
            ctx.fillRect(x1, ty + 1, w, PHASE_TRACK_HEIGHT - 2);
            if (w > 50) {
              ctx.save();
              ctx.beginPath();
              ctx.rect(x1, ty + 1, w, PHASE_TRACK_HEIGHT - 2);
              ctx.clip();
              ctx.fillStyle = TEXT_COLOR; ctx.font = '9px monospace'; ctx.textAlign = 'left';
              ctx.fillText(`${phase.phaseName} (${formatDuration(phase.durationUs)})`, x1 + 3, ty + 12);
              ctx.restore();
            }
          }
        }
      }

      // ── Shared helper: draw a projection mini-row with coalescing ──
      // Used by both Page Cache and Disk IO tracks. Each row is MINI_ROW_HEIGHT (11 px) tall.
      if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
        const viewStartUs = vp.offsetX;
        const viewEndUs = vp.offsetX + contentWidth / vp.scaleX;
        const MRH = MINI_ROW_HEIGHT;
        const barH = MRH - 1;

        const drawMiniRow = (ops: SpanData[], endMax: Float64Array, rowY: number, colorFn: (op: SpanData) => string) => {
          if (ops.length === 0) return;
          let lo = 0, hi = ops.length;
          while (lo < hi) {
            const mid = (lo + hi) >>> 1;
            if (endMax[mid] < viewStartUs) lo = mid + 1; else hi = mid;
          }
          let coalX1 = 0, coalX2 = 0, coalCount = 0;
          let mrPrevFill = '';
          const flushCoal = () => {
            if (coalCount >= 2) {
              const cw = Math.max(coalX2 - coalX1, 2);
              // Same vertical-stripe coalesced pattern used by the flamegraph path above — keeps the "this is N merged ops" visual
              // language consistent across the flamegraph and the projection mini-rows. Pattern-object sentinel invalidates the
              // prev-fill cache so the next per-op color assignment re-paints fillStyle.
              ctx.fillStyle = getCoalescedPattern(ctx);
              mrPrevFill = '__pattern__';
              ctx.fillRect(coalX1, rowY, cw, barH);
            }
            coalCount = 0;
          };
          for (let i = lo; i < ops.length; i++) {
            const op = ops[i];
            if (op.startUs > viewEndUs) break;
            const x1 = pxOfUs(op.startUs);
            const x2 = pxOfUs(op.endUs);
            if (x2 < GUTTER_WIDTH) continue;
            const actualWidth = x2 - x1;
            const w = actualWidth < MIN_RECT_WIDTH ? MIN_RECT_WIDTH : actualWidth;
            if (actualWidth <= 1) {
              if (coalCount > 0 && x1 <= coalX2 + 1) {
                coalCount++;
                if (x2 > coalX2) coalX2 = x2;
                if (coalX2 < coalX1 + 1) coalX2 = coalX1 + 1;
                continue;
              }
              flushCoal();
              { const c = colorFn(op); if (c !== mrPrevFill) { ctx.fillStyle = c; mrPrevFill = c; } }
              ctx.fillRect(x1, rowY, w, barH);
              coalX1 = x1; coalX2 = x1 + w; coalCount = 1;
              continue;
            }
            flushCoal();
            { const c = colorFn(op); if (c !== mrPrevFill) { ctx.fillStyle = c; mrPrevFill = c; } }
            ctx.fillRect(x1, rowY, w, barH);
          }
          flushCoal();
        };

        // Row config per track type. Each row's color is a picked index in the dedicated <c>TIMELINE_PALETTE</c> (13 slots). Label uses
        // ~80% alpha ("CC"), bar uses ~15% alpha ("26"). Indices match the semantic ladder documented on TIMELINE_PALETTE:
        //   [0] Phases  [1] Cache Fetch  [2] Cache Allocate  [3] Cache Evicted  [4] Cache Flush
        //   [5] Disk Read  [6] Disk Write  [7] Tx Commit  [8] Tx Persist  [9] Checkpoint Cycle
        //   [10] WAL Flush  [11] WAL Wait  [12] Tx Rollback
        type RowDef = { label: string; labelColor: string; barColor: string; getOps: (t: TickData) => SpanData[]; getEndMax: (t: TickData) => Float64Array };
        const rows: RowDef[] =
          track.id === 'page-cache' ? [
            { label: 'Fetch',    labelColor: TIMELINE_PALETTE[1] + 'CC', barColor: TIMELINE_PALETTE[1] + '26', getOps: t => t.cacheFetch,   getEndMax: t => t.cacheFetchEndMax },
            { label: 'Allocate', labelColor: TIMELINE_PALETTE[2] + 'CC', barColor: TIMELINE_PALETTE[2] + '26', getOps: t => t.cacheAlloc,   getEndMax: t => t.cacheAllocEndMax },
            { label: 'Evicted',  labelColor: TIMELINE_PALETTE[3] + 'CC', barColor: TIMELINE_PALETTE[3] + '26', getOps: t => t.cacheEvict,   getEndMax: t => t.cacheEvictEndMax },
            { label: 'Flush',    labelColor: TIMELINE_PALETTE[4] + 'CC', barColor: TIMELINE_PALETTE[4] + '26', getOps: t => t.cacheFlushes, getEndMax: t => t.cacheFlushesEndMax },
          ] : track.id === 'disk-io' ? [
            { label: 'Reads',  labelColor: TIMELINE_PALETTE[5] + 'CC', barColor: TIMELINE_PALETTE[5] + '26', getOps: t => t.diskReads,  getEndMax: t => t.diskReadsEndMax },
            { label: 'Writes', labelColor: TIMELINE_PALETTE[6] + 'CC', barColor: TIMELINE_PALETTE[6] + '26', getOps: t => t.diskWrites, getEndMax: t => t.diskWritesEndMax },
          ] : track.id === 'transactions' ? [
            { label: 'Commits',   labelColor: TIMELINE_PALETTE[7]  + 'CC', barColor: TIMELINE_PALETTE[7]  + '26', getOps: t => t.txCommits,   getEndMax: t => t.txCommitsEndMax },
            { label: 'Rollbacks', labelColor: TIMELINE_PALETTE[12] + 'CC', barColor: TIMELINE_PALETTE[12] + '26', getOps: t => t.txRollbacks, getEndMax: t => t.txRollbacksEndMax },
            { label: 'Persists',  labelColor: TIMELINE_PALETTE[8]  + 'CC', barColor: TIMELINE_PALETTE[8]  + '26', getOps: t => t.txPersists,  getEndMax: t => t.txPersistsEndMax },
          ] : track.id === 'wal' ? [
            { label: 'Flushes', labelColor: TIMELINE_PALETTE[10] + 'CC', barColor: TIMELINE_PALETTE[10] + '26', getOps: t => t.walFlushes, getEndMax: t => t.walFlushesEndMax },
            { label: 'Waits',   labelColor: TIMELINE_PALETTE[11] + 'CC', barColor: TIMELINE_PALETTE[11] + '26', getOps: t => t.walWaits,   getEndMax: t => t.walWaitsEndMax },
          ] : /* checkpoint */ [
            { label: 'Cycles', labelColor: TIMELINE_PALETTE[9] + 'CC', barColor: TIMELINE_PALETTE[9] + '26', getOps: t => t.checkpointCycles, getEndMax: t => t.checkpointCyclesEndMax },
          ];

        ctx.font = '9px monospace'; ctx.textAlign = 'left';
        const labelPad = 3;

        for (let r = 0; r < rows.length; r++) {
          const row = rows[r];
          const rowY = ty + r * MRH;

          // Label pill
          const lw = ctx.measureText(row.label).width + labelPad * 2;
          ctx.fillStyle = 'rgba(30, 32, 36, 0.85)';
          ctx.fillRect(GUTTER_WIDTH + 2, rowY + 1, lw, MRH - 1);
          ctx.fillStyle = row.labelColor;
          ctx.fillText(row.label, GUTTER_WIDTH + 2 + labelPad, rowY + MRH - 2);

          // Draw bars — colorFn hoisted out of the tick loop to avoid closure allocation per tick.
          const colorFn = (_: SpanData) => row.barColor;
          for (const tick of visibleTicks) {
            const ops = row.getOps(tick);
            const endMax = row.getEndMax(tick);
            drawMiniRow(ops, endMax, rowY, colorFn);
          }
        }
      }

    }

    // ── Mouse crosshair cursor line + timestamp label ──
    // A thin vertical line at the cursor's X position with the exact absolute time (relative to first tick start) displayed
    // at the bottom of the line. Drawn after all tracks so it overlays everything. Visible whenever the cursor is over the
    // content area (mx >= GUTTER_WIDTH); hidden during drag (mouseXRef.current == -1) so it doesn't jitter while panning.
    const crosshairX = mouseXRef.current;
    if (crosshairX >= GUTTER_WIDTH) {
      const cursorUs = vp.offsetX + (crosshairX - GUTTER_WIDTH) / vp.scaleX;
      const baseUs = visibleTicks[0]?.startUs ?? 0;
      // Vertical line
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.3)';
      ctx.lineWidth = 1;
      ctx.setLineDash([]);
      ctx.beginPath();
      ctx.moveTo(crosshairX, RULER_HEIGHT);
      ctx.lineTo(crosshairX, height);
      ctx.stroke();
      // Timestamp label at the bottom of the line, in a small pill background so it's readable over any content.
      const label = formatRulerLabel(cursorUs - baseUs);
      ctx.font = '9px monospace';
      const labelWidth = ctx.measureText(label).width + 8;
      const labelX = Math.min(crosshairX - labelWidth / 2, width - labelWidth - 2);
      const labelY = height - 18;
      ctx.fillStyle = 'rgba(30, 32, 36, 0.9)';
      ctx.fillRect(labelX, labelY, labelWidth, 16);
      ctx.fillStyle = TEXT_COLOR;
      ctx.textAlign = 'center';
      ctx.fillText(label, crosshairX, labelY + 12);
    }

    // ── Zoom-selection overlay ──
    // During a drag-to-zoom gesture, draw a semi-transparent highlight over the selected region with a center line and
    // a duration label. The overlay spans the full canvas height (minus the ruler) so the user can see the time range
    // they're about to zoom into relative to all tracks.
    const sel = zoomSelRef.current;
    if (sel && sel.x2 > sel.x1) {
      const selW = sel.x2 - sel.x1;
      // Semi-transparent blue fill
      ctx.fillStyle = 'rgba(120, 128, 144, 0.2)';  // selection band fill — muted blue-grey so it's recognizably "selected" without overpowering
      ctx.fillRect(sel.x1, RULER_HEIGHT, selW, height - RULER_HEIGHT);
      // Left + right edge lines
      ctx.strokeStyle = 'rgba(140, 150, 170, 0.7)';  // selection band stroke — slightly more blue than the fill so the boundary reads clearly
      ctx.lineWidth = 1;
      ctx.setLineDash([]);
      ctx.beginPath();
      ctx.moveTo(sel.x1, RULER_HEIGHT); ctx.lineTo(sel.x1, height);
      ctx.moveTo(sel.x2, RULER_HEIGHT); ctx.lineTo(sel.x2, height);
      ctx.stroke();
      // Horizontal center line
      const centerY = (RULER_HEIGHT + height) / 2;
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.5)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(sel.x1, centerY);
      ctx.lineTo(sel.x2, centerY);
      ctx.stroke();
      // Duration label centered below the center line
      const selStartUs = vp.offsetX + (sel.x1 - GUTTER_WIDTH) / vp.scaleX;
      const selEndUs = vp.offsetX + (sel.x2 - GUTTER_WIDTH) / vp.scaleX;
      const selDuration = selEndUs - selStartUs;
      const durLabel = formatRulerLabel(selDuration);
      ctx.font = '11px monospace';
      ctx.textAlign = 'center';
      const durLabelX = (sel.x1 + sel.x2) / 2;
      // Pill background
      const durLabelW = ctx.measureText(durLabel).width + 10;
      ctx.fillStyle = 'rgba(30, 32, 36, 0.9)';
      ctx.fillRect(durLabelX - durLabelW / 2, centerY + 4, durLabelW, 18);
      ctx.fillStyle = '#fff';
      ctx.fillText(durLabel, durLabelX, centerY + 17);
    }

    // ── Pending-chunk placeholders ──
    // Paint a diagonal-stripe overlay over any summary tick whose chunks aren't resident yet. During rapid pan/wheel this
    // prevents the empty content area from looking broken: the user sees striped rectangles at the exact time ranges that are
    // about to fill in. Summary is authoritative (always loaded at open time); trace.ticks is the resident subset. The overlay
    // is clipped to [RULER_HEIGHT, height] so the tick-number labels in the ruler remain visible on top.
    // Empty in live mode (no summary) and a no-op when all visible ticks are already resident.
    if (trace.summary && trace.summary.length > 0) {
      const residentNums = new Set<number>(trace.tickNumbers);
      if (residentNums.size < trace.summary.length) {
        const pattern = getPendingPattern(ctx);
        for (const s of trace.summary) {
          const tickEndUs = s.startUs + s.durationUs;
          if (tickEndUs <= visStartUs) continue;
          if (s.startUs >= visEndUs) break;               // summary is sorted by startUs — safe to early-exit
          if (residentNums.has(s.tickNumber)) continue;
          const x1 = Math.max(GUTTER_WIDTH, pxOfUs(s.startUs));
          const x2 = Math.min(width, pxOfUs(tickEndUs));
          if (x2 - x1 < 1) continue;
          ctx.fillStyle = pattern;
          ctx.fillRect(x1, RULER_HEIGHT, x2 - x1, height - RULER_HEIGHT);
          // "T# ⟳" label — only when the range is wide enough to fit it without crowding.
          if (x2 - x1 > 48) {
            ctx.fillStyle = 'rgba(208, 210, 216, 0.85)';  // near-white text with barely-there blue tint
            ctx.font = '10px monospace';
            ctx.textAlign = 'center';
            ctx.fillText(`${s.tickNumber} \u27F3`, (x1 + x2) / 2, RULER_HEIGHT + 14);
          }
        }
      }
    }

    const hover = hoverRef.current;
    if (hover) drawTooltip(ctx, hover.x, hover.y, hover.lines, width, height);

    // Empty-viewport overlay. Drawn AFTER the pending-chunk pattern so the user sees "data is coming, decoding a dense region"
    // context rather than just a blank canvas. When summary has overlapping ticks → "Loading ticks…" (data exists, cache empty);
    // when summary has nothing at this time range → "No ticks in view" (viewport is truly off-trace). The background-rect guard
    // makes the text legible over the grey diagonal pending pattern.
    if (visibleTicks.length === 0) {
      const centerX = width / 2;
      const centerY = height / 2;
      // Detect "summary has ticks here" = "loading" vs "truly empty".
      let isLoading = false;
      if (trace.summary && trace.summary.length > 0) {
        const vpStart = vp.offsetX;
        const vpEnd = vp.offsetX + contentWidth / Math.max(vp.scaleX, 0.001);
        for (const s of trace.summary) {
          const endUs = s.startUs + s.durationUs;
          if (endUs <= vpStart) continue;
          if (s.startUs >= vpEnd) break;
          isLoading = true;
          break;
        }
      }
      const label = isLoading ? 'Loading ticks…' : 'No ticks in view';
      // Backdrop for legibility over any pending pattern underneath.
      ctx.fillStyle = 'rgba(29, 30, 32, 0.85)';
      ctx.textAlign = 'center';
      ctx.font = '14px monospace';
      const labelWidth = ctx.measureText(label).width;
      ctx.fillRect(centerX - labelWidth / 2 - 12, centerY - 12, labelWidth + 24, 24);
      ctx.fillStyle = isLoading ? TEXT_COLOR : DIM_TEXT;
      ctx.fillText(label, centerX, centerY + 5);
    }

    // Restore original methods before the debug line (so the debug draw itself isn't double-counted).
    ctx.fillRect = origFillRect;
    ctx.fillText = origFillText;

    // Debug stats — dark grey, bottom-left of the content area, barely noticeable.
    const renderMs = (performance.now() - renderStart).toFixed(1);
    ctx.fillStyle = '#666';
    ctx.font = '13px monospace';
    ctx.textAlign = 'left';
    // Cache-usage suffix: RAM cache (ChunkCacheState in-memory LRU) + OPFS cache (persistent disk store). Read each render from
    // the ref so numbers are live — no App-level re-render required when chunks load.
    let cacheLabel = '';
    const cache = chunkCacheRef?.current;
    if (cache) {
      const ramMB = (cache.totalBytes / (1024 * 1024)).toFixed(0);
      const ramBudgetMB = (cache.budgetBytes / (1024 * 1024)).toFixed(0);
      cacheLabel = `  ram:${ramMB}/${ramBudgetMB}M`;
      const opfs = cache.opfsStore;
      if (opfs) {
        const opfsMB = (opfs.cachedTotalBytes / (1024 * 1024)).toFixed(0);
        const totalLookups = opfs.hits + opfs.misses;
        const hitPct = totalLookups > 0 ? Math.round((opfs.hits / totalLookups) * 100) : 0;
        cacheLabel += `  opfs:${opfsMB}M h:${opfs.hits}/${totalLookups}(${hitPct}%) w:${opfs.writes}`;
      }
    }
    ctx.fillText(`${rectCount} rects  ${textCount} texts  ${renderMs}ms${cacheLabel}`, GUTTER_WIDTH + 4, height - 4);

    ctx.restore();
    // `legendsVisible` is listed explicitly: it doesn't flow through `buildLayout` (which only drives layout/position) but it IS
    // captured in the closure and passed into every gauge render context, so toggling it has to invalidate this useCallback to
    // pick up the new flag value. Without this dep, the legends-toggle menu item / 'l' shortcut flips state but the rAF loop
    // keeps drawing the stale snapshot — no visible change until the next viewport/trace mutation accidentally re-creates
    // `render`. `gaugeRegionVisible` doesn't need the same treatment because it's already pulled in transitively via
    // `buildLayout`'s dep list.
  }, [trace, viewRange, selectedChunk, selectedSpan, collapseState, buildLayout, getVisibleTicks, legendsVisible]);

  const scheduleRender = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    // Wrap render() in try/catch so a downstream draw failure never leaves the whole canvas transparent. <see cref="setupCanvas"/>
    // clears the canvas at the top of render — if the subsequent draw logic throws (division-by-zero in a gauge path, out-of-bounds
    // index on a binary search when the viewport lands outside the trace, etc.), the canvas stays blank until the NEXT render call
    // succeeds. That was the observable "blank span area including gutter" symptom after excessive pan-left. The try/catch keeps the
    // rAF loop healthy (exception doesn't bubble into browser's rAF error path and kill the frame) and logs the first few failures
    // so the dev has something to investigate. Clamping <c>handleViewRangeChange</c> in App.tsx should already prevent the known
    // trigger — this is belt-and-suspenders for unknown future regressions.
    rafRef.current = requestAnimationFrame(() => {
      try {
        render();
      } catch (err) {
        console.error('GraphArea render failed:', err);
      }
    });
  }, [render]);

  useEffect(() => {
    scheduleRender();
    const obs = new ResizeObserver(() => scheduleRender());
    if (containerRef.current) obs.observe(containerRef.current);
    return () => { obs.disconnect(); cancelAnimationFrame(rafRef.current); };
  }, [scheduleRender]);

  const onWheel = useCallback((e: WheelEvent) => {
    e.preventDefault();
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const vp = vpRef.current;
    // Snapshot the dynamic gutter width once per handler invocation — avoids repeated ref lookups and keeps the rest of the code
    // identical to the pre-dynamic-gutter version.
    const GUTTER_WIDTH = gutterWidthRef.current;
    const contentWidth = rect.width - GUTTER_WIDTH;
    const mouseX = e.clientX - rect.left - GUTTER_WIDTH;

    if (e.ctrlKey || e.metaKey) {
      vp.scrollY = Math.max(0, vp.scrollY + e.deltaY);
    } else if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
      const delta = e.shiftKey ? e.deltaY : e.deltaX;
      vp.offsetX += delta / vp.scaleX;
      onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
    } else {
      const usAtMouse = vp.offsetX + mouseX / vp.scaleX;
      const factor = e.deltaY > 0 ? 0.85 : 1.18;
      vp.scaleX = Math.max(0.001, Math.min(10000, vp.scaleX * factor));
      vp.offsetX = usAtMouse - mouseX / vp.scaleX;
      onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
    }
    scheduleRender();
  }, [scheduleRender, onViewRangeChange]);

  // Animated navigation: smoothly transition to a target range (used by undo/redo from mouse back/forward buttons
  // and zoom-to-selection). Sets isNavigating to suppress debounced nav push during the animation.
  const animateToRange = useCallback((target: TimeRange, suppressNavPush = false) => {
    const vp = vpRef.current;
    const canvas = canvasRef.current;
    if (!canvas) return;
    if (suppressNavPush) navHistory.setNavigating(true);
    const contentWidth = canvas.getBoundingClientRect().width - gutterWidthRef.current;
    zoomAnimRef.current = {
      from: { startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX },
      to: target,
      startTime: performance.now(),
    };
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => render());
  }, [render, navHistory]);

  const onMouseDown = useCallback((e: MouseEvent) => {
    // Mouse back (button 3) = nav undo (or redo if shift is held — for mice without a forward button).
    // Mouse forward (button 4) = nav redo.
    if (e.button === 3) {
      e.preventDefault();
      const target = e.shiftKey ? navHistory.redo() : navHistory.undo();
      if (target) animateToRange(target, true);
      return;
    }
    if (e.button === 4) {
      e.preventDefault();
      const target = navHistory.redo();
      if (target) animateToRange(target, true);
      return;
    }

    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const GUTTER_WIDTH = gutterWidthRef.current;

    if (mx < GUTTER_WIDTH) {
      const layout = layoutRef.current ?? buildLayout();
      for (const track of layout) {
        if (!track.collapsible) continue;
        const ty = track.y - vpRef.current.scrollY;
        if (my >= ty && my <= ty + LABEL_ROW_HEIGHT) {
          // Cycle the 3-state expand. Gauge tracks go summary → expanded → double → summary. Non-gauge tracks skip `double`
          // (they don't benefit from 2× height — their content is discrete bars/flames, not continuous signals) and cycle
          // summary → expanded → summary.
          const isGauge = GAUGE_TRACK_ID_SET.has(track.id);
          const cur = track.state;
          const next: TrackState =
            cur === 'summary'  ? 'expanded'
          : cur === 'expanded' ? (isGauge ? 'double' : 'summary')
          : /* 'double' */       'summary';
          setCollapseState(prev => ({ ...prev, [track.id]: next }));
          return;
        }
      }
      return;
    }

    isDraggingRef.current = true;
    dragStartRef.current = { x: e.clientX, y: e.clientY, offsetX: vpRef.current.offsetX, scrollY: vpRef.current.scrollY };
    // Middle-button or shift+left = pan (old behavior). Plain left-click = zoom-to-selection.
    dragModeRef.current = (e.button === 1 || e.shiftKey) ? 'pan' : 'select';
    zoomSelRef.current = null;
  }, [buildLayout]);

  const onMouseMove = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const GUTTER_WIDTH = gutterWidthRef.current;

    if (isDraggingRef.current) {
      if (dragModeRef.current === 'pan') {
        // Pan mode (middle-drag or shift+left-drag): move the viewport.
        const vp = vpRef.current;
        const rect = canvas.getBoundingClientRect();
        const contentWidth = rect.width - GUTTER_WIDTH;
        vp.offsetX = dragStartRef.current.offsetX - (e.clientX - dragStartRef.current.x) / vp.scaleX;
        vp.scrollY = Math.max(0, dragStartRef.current.scrollY - (e.clientY - dragStartRef.current.y));
        mouseXRef.current = -1;
        onViewRangeChange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
      } else {
        // Zoom-selection mode (plain left-drag): update the selection overlay. The render pass draws the rectangle.
        const rect = canvas.getBoundingClientRect();
        const startMx = dragStartRef.current.x - rect.left;
        const currentMx = e.clientX - rect.left;
        zoomSelRef.current = {
          x1: Math.max(GUTTER_WIDTH, Math.min(startMx, currentMx)),
          x2: Math.max(GUTTER_WIDTH, Math.max(startMx, currentMx)),
        };
        mouseXRef.current = -1;
      }
      scheduleRender();
      return;
    }

    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    // Always track cursor X for the crosshair line, even when not over an interactive element.
    mouseXRef.current = mx >= GUTTER_WIDTH ? mx : -1;
    // Gutter = chrome, not data — show the OS default arrow cursor there instead of the crosshair we use over the content
    // area. Keeps clicking chevrons / hovering "?" glyphs feeling like ordinary UI, not a data-region drag.
    if (mx < GUTTER_WIDTH) {
      if (canvas.style.cursor !== 'default') canvas.style.cursor = 'default';
      // Help-glyph hit-test: when legends are visible, the right-most HELP_ICON_WIDTH pixels of the gutter are a hot zone for the
      // per-row "?" help tooltip. Runs BEFORE the plain-gutter bail-out below so the hover feedback survives the "cursor is inside
      // the gutter" check. The hit band is the label row (ty..ty+LABEL_ROW_HEIGHT) of each collapsible track — matches the Y band
      // where the glyph is actually drawn.
      if (legendsVisible && mx >= GUTTER_WIDTH - HELP_ICON_WIDTH - HELP_ICON_HIT_PAD) {
        const layoutForHelp = layoutRef.current ?? buildLayout();
        const threadNamesForHelp = trace.metadata.threadNames;
        let helpFound = false;
        for (const track of layoutForHelp) {
          if (!track.collapsible) continue;
          const ty = track.y - vpRef.current.scrollY;
          if (my < ty || my > ty + LABEL_ROW_HEIGHT) continue;
          let label = track.label;
          if (track.id.startsWith('slot-')) {
            const slotIdx = parseInt(track.id.split('-')[1]);
            const threadName = threadNamesForHelp?.[slotIdx];
            label = threadName ? `${threadName} (slot ${slotIdx})` : `Slot ${slotIdx}`;
          }
          hoverRef.current = { x: mx, y: my, lines: getTrackHelpLines(track.id, label) };
          // Record the hovered track so the gutter-draw loop knows which "?" to brighten. Intentionally NOT mutating
          // canvas.style.cursor — the OS `help` cursor (arrow with a small "?" glyph bottom-right on Windows) ends up
          // rendered over the tooltip text and obscures the content the user is trying to read. Brightening the glyph
          // itself is enough affordance.
          hoveredHelpTrackIdRef.current = track.id;
          helpFound = true;
          break;
        }
        if (!helpFound) {
          hoverRef.current = null;
          hoveredHelpTrackIdRef.current = null;
        }
      } else {
        hoverRef.current = null;
        hoveredHelpTrackIdRef.current = null;
      }
      scheduleRender();
      return;
    }

    // Mouse is in the content area — clear any prior gutter-side help hover so the bright "?" doesn't linger after the
    // cursor moves out of the help zone, and flip the cursor back from `default` (gutter chrome) to `crosshair` (data region).
    if (hoveredHelpTrackIdRef.current !== null) {
      hoveredHelpTrackIdRef.current = null;
    }
    if (canvas.style.cursor === 'default') canvas.style.cursor = 'crosshair';

    const vp = vpRef.current;
    const usAtMouse = vp.offsetX + (mx - GUTTER_WIDTH) / vp.scaleX;
    const layout = layoutRef.current ?? buildLayout();
    const visibleTicks = getVisibleTicks();

    let found = false;
    for (const track of layout) {
      // Summary-state tracks have no interactive content (just a spark line or dark strip) — skip hover hit-tests against them.
      if (track.state === 'summary' || track.id === 'ruler') continue;
      const ty = track.y - vp.scrollY;
      if (my < ty || my > ty + track.height) continue;

      if (track.id.startsWith('slot-')) {
        const threadSlot = parseInt(track.id.split('-')[1]);
        const chunkRowHeight = track.chunkRowHeight ?? 0;
        const inChunkRegion = my < ty + chunkRowHeight;
        const hitMarginUs = 2 / vp.scaleX;

        if (inChunkRegion) {
          // Chunk bar row — find the chunk under the cursor.
          for (const tick of visibleTicks) {
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              if (usAtMouse < chunk.startUs - hitMarginUs || usAtMouse > chunk.endUs + hitMarginUs) continue;
              hoverRef.current = {
                x: mx, y: my,
                lines: [
                  chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName,
                  `Duration: ${formatDuration(chunk.durationUs)}`, `Thread slot: ${chunk.threadSlot}`,
                  `Entities: ${chunk.entitiesProcessed.toLocaleString()}`, `Tick: ${tick.tickNumber}`,
                ]
              };
              found = true; break;
            }
            if (found) break;
          }
        } else {
          // Span region — depth is derived from the Y offset below the chunk row. Only spans at that exact depth on this
          // slot can match, which gives precise hit-testing even in deeply nested hierarchies.
          const hoveredDepth = Math.floor((my - ty - chunkRowHeight) / SPAN_ROW_HEIGHT);
          if (hoveredDepth >= 0) {
            for (const tick of visibleTicks) {
              const slotSpans = tick.spansByThreadSlot.get(threadSlot);
              if (!slotSpans || slotSpans.length === 0) continue;
              const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
              if (!endMax) continue;
              let lo = 0, hi = slotSpans.length;
              const target = usAtMouse - hitMarginUs;
              while (lo < hi) {
                const mid = (lo + hi) >>> 1;
                if (endMax[mid] < target) lo = mid + 1; else hi = mid;
              }
              for (let i = lo; i < slotSpans.length; i++) {
                const span = slotSpans[i];
                if (span.startUs > usAtMouse + hitMarginUs) break;
                if ((span.depth ?? 0) !== hoveredDepth) continue;
                if (usAtMouse < span.startUs - hitMarginUs || usAtMouse > span.endUs + hitMarginUs) continue;
                hoverRef.current = {
                  x: mx, y: my,
                  lines: [
                    span.name,
                    `Duration: ${formatDuration(span.durationUs)}`,
                    `Depth: ${span.depth ?? 0}`,
                    `Thread slot: ${span.threadSlot}`,
                    `Tick: ${tick.tickNumber}`,
                  ]
                };
                found = true; break;
              }
              if (found) break;
            }
          }
        }
      } else if (GAUGE_TRACK_ID_SET.has(track.id)) {
        // Gauge region tooltip. Unlike span tracks (where hover finds a specific event bar), gauges are time-series so we find the
        // NEAREST snapshot tick to the cursor and show ALL gauges for this group at that tick. Sub-row-aware for tracks with multiple
        // stacked sub-rows (Memory) — the tooltip builder splits dispatch by which row the cursor is over. Passing `localY` (track-
        // relative Y) and `track.height` gives the builder the info it needs; other tracks ignore them.
        const gaugeSpec = getGaugeGroupSpec(track.id);
        if (gaugeSpec !== undefined) {
          const localY = my - ty;
          const lines = buildGaugeTooltipLines(trace, track.id, gaugeSpec.label, usAtMouse, localY, track.height);
          if (lines.length > 0) {
            hoverRef.current = { x: mx, y: my, lines };
            found = true;
          }
        }
      } else if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            if (usAtMouse >= phase.startUs && usAtMouse <= phase.endUs) {
              hoverRef.current = { x: mx, y: my, lines: [phase.phaseName, `Duration: ${formatDuration(phase.durationUs)}`, `Tick: ${tick.tickNumber}`] };
              found = true; break;
            }
          }
          if (found) break;
        }
      } else if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
        const MRH = MINI_ROW_HEIGHT;
        const hoveredRow = Math.floor((my - ty) / MRH);
        const hitMarginUs = 2 / vp.scaleX;
        const MAX_TOOLTIP_OPS = 8;
        const hits: string[] = [];
        let totalHits = 0;
        let rowLabel = '';

        // Resolve which ops array + label to search based on track + row index.
        for (const tick of visibleTicks) {
          let ops: SpanData[];
          let endMax: Float64Array;
          if (track.id === 'page-cache') {
            if (hoveredRow === 0) { ops = tick.cacheFetch; endMax = tick.cacheFetchEndMax; rowLabel = 'Fetch'; }
            else if (hoveredRow === 1) { ops = tick.cacheAlloc; endMax = tick.cacheAllocEndMax; rowLabel = 'Allocate'; }
            else if (hoveredRow === 2) { ops = tick.cacheEvict; endMax = tick.cacheEvictEndMax; rowLabel = 'Evicted'; }
            else { ops = tick.cacheFlushes; endMax = tick.cacheFlushesEndMax; rowLabel = 'Flush'; }
          } else if (track.id === 'disk-io') {
            if (hoveredRow === 0) { ops = tick.diskReads; endMax = tick.diskReadsEndMax; rowLabel = 'Disk Reads'; }
            else { ops = tick.diskWrites; endMax = tick.diskWritesEndMax; rowLabel = 'Disk Writes'; }
          } else if (track.id === 'transactions') {
            if (hoveredRow === 0) { ops = tick.txCommits; endMax = tick.txCommitsEndMax; rowLabel = 'Commits'; }
            else if (hoveredRow === 1) { ops = tick.txRollbacks; endMax = tick.txRollbacksEndMax; rowLabel = 'Rollbacks'; }
            else { ops = tick.txPersists; endMax = tick.txPersistsEndMax; rowLabel = 'Persists'; }
          } else if (track.id === 'wal') {
            if (hoveredRow === 0) { ops = tick.walFlushes; endMax = tick.walFlushesEndMax; rowLabel = 'WAL Flushes'; }
            else { ops = tick.walWaits; endMax = tick.walWaitsEndMax; rowLabel = 'WAL Waits'; }
          } else {
            ops = tick.checkpointCycles; endMax = tick.checkpointCyclesEndMax; rowLabel = 'Checkpoints';
          }
          endMax ??= new Float64Array(0);
          if (ops.length === 0) continue;
          let lo = 0, hi = ops.length;
          const target = usAtMouse - hitMarginUs;
          while (lo < hi) {
            const mid = (lo + hi) >>> 1;
            if (endMax[mid] < target) lo = mid + 1; else hi = mid;
          }
          for (let i = lo; i < ops.length; i++) {
            const op = ops[i];
            if (op.startUs > usAtMouse + hitMarginUs) break;
            if (usAtMouse < op.startUs - hitMarginUs || usAtMouse > op.endUs + hitMarginUs) continue;
            totalHits++;
            if (hits.length < MAX_TOOLTIP_OPS) {
              const dur = op.durationUs;
              const asyncNote = op.kickoffDurationUs !== undefined ? ` (async: ${formatDuration(dur - op.kickoffDurationUs)})` : '';
              hits.push(`${op.name} ${formatDuration(dur)}${asyncNote} [slot ${op.threadSlot}]`);
            }
          }
        }

        if (totalHits > 0) {
          const lines = [
            `${rowLabel}: ${totalHits} op${totalHits > 1 ? 's' : ''} at cursor`,
            ...hits,
          ];
          if (totalHits > MAX_TOOLTIP_OPS) {
            lines.push(`... and ${totalHits - MAX_TOOLTIP_OPS} more`);
          }
          hoverRef.current = { x: mx, y: my, lines };
          found = true;
        }
      }
      break;
    }
    if (!found) hoverRef.current = null;
    scheduleRender();
    // `legendsVisible` is in the dep list because the in-gutter help-glyph hit-test at the top of this handler branches on it.
    // Without it the closure would freeze to the initial value and enabling/disabling legends wouldn't change the help tooltip's
    // availability until some other dep invalidated this useCallback.
  }, [trace, buildLayout, getVisibleTicks, scheduleRender, onViewRangeChange, legendsVisible]);

  const onMouseUp = useCallback((e: MouseEvent) => {
    if (!isDraggingRef.current) return;
    const dx = Math.abs(e.clientX - dragStartRef.current.x);
    const dy = Math.abs(e.clientY - dragStartRef.current.y);
    isDraggingRef.current = false;
    const GUTTER_WIDTH = gutterWidthRef.current;

    // ── Zoom-to-selection commit ──
    // If the user dragged far enough horizontally in select mode, zoom the viewport to the selected time range.
    // The selection overlay (zoomSelRef) was being drawn per-frame during the drag; now we convert it to µs and apply.
    const sel = zoomSelRef.current;
    zoomSelRef.current = null;  // always clear the overlay
    if (dragModeRef.current === 'select' && sel && (sel.x2 - sel.x1) > 5) {
      const vp = vpRef.current;
      const canvas = canvasRef.current;
      if (canvas) {
        const contentWidth = canvas.getBoundingClientRect().width - GUTTER_WIDTH;
        const targetStartUs = vp.offsetX + (sel.x1 - GUTTER_WIDTH) / vp.scaleX;
        const targetEndUs = vp.offsetX + (sel.x2 - GUTTER_WIDTH) / vp.scaleX;
        if (targetEndUs > targetStartUs) {
          // Kick off an animated zoom from the current viewport to the selected range over 800 ms.
          zoomAnimRef.current = {
            from: { startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX },
            to: { startUs: targetStartUs, endUs: targetEndUs },
            startTime: performance.now(),
          };
          scheduleRender();
          return;
        }
      }
    }
    scheduleRender();  // clear the selection overlay visual even if we didn't zoom

    if (dx < 3 && dy < 3) {
      const canvas = canvasRef.current;
      if (!canvas) return;
      const rect = canvas.getBoundingClientRect();
      const mx = e.clientX - rect.left;
      const my = e.clientY - rect.top;
      if (mx < GUTTER_WIDTH) return;
      const usAtMouse = vpRef.current.offsetX + (mx - GUTTER_WIDTH) / vpRef.current.scaleX;
      const layout = layoutRef.current ?? buildLayout();
      const visibleTicks = getVisibleTicks();

      for (const track of layout) {
        if (track.state === 'summary') continue;  // summary state has no interactive content — no click target
        const ty = track.y - vpRef.current.scrollY;
        if (my < ty || my > ty + track.height) continue;

        // Slot lane — the chunk row is the top chunkRowHeight pixels; everything below is span rows by depth.
        if (track.id.startsWith('slot-')) {
          const threadSlot = parseInt(track.id.split('-')[1]);
          const chunkRowHeight = track.chunkRowHeight ?? 0;
          const inChunkRegion = my < ty + chunkRowHeight;
          const hitMarginUs = 2 / vpRef.current.scaleX;

          if (inChunkRegion) {
            for (const tick of visibleTicks) {
              for (const chunk of tick.chunks) {
                if (chunk.threadSlot !== threadSlot) continue;
                if (usAtMouse < chunk.startUs - hitMarginUs || usAtMouse > chunk.endUs + hitMarginUs) continue;
                onChunkSelect(chunk);
                onSpanSelect(null);
                return;
              }
            }
          } else {
            const clickedDepth = Math.floor((my - ty - chunkRowHeight) / SPAN_ROW_HEIGHT);
            if (clickedDepth >= 0) {
              for (const tick of visibleTicks) {
                const slotSpans = tick.spansByThreadSlot.get(threadSlot);
                if (!slotSpans || slotSpans.length === 0) continue;
                const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
                if (!endMax) continue;
                let lo = 0, hi = slotSpans.length;
                const target = usAtMouse - hitMarginUs;
                while (lo < hi) {
                  const mid = (lo + hi) >>> 1;
                  if (endMax[mid] < target) lo = mid + 1; else hi = mid;
                }
                for (let i = lo; i < slotSpans.length; i++) {
                  const span = slotSpans[i];
                  if (span.startUs > usAtMouse + hitMarginUs) break;
                  if ((span.depth ?? 0) !== clickedDepth) continue;
                  if (usAtMouse < span.startUs - hitMarginUs || usAtMouse > span.endUs + hitMarginUs) continue;
                  onSpanSelect(span);
                  onChunkSelect(null);
                  return;
                }
              }
            }
          }
          break;
        }

        // Gauge-track marker hit-test (memory alloc markers + GC markers). Search for the NEAREST marker within the hit radius —
        // markers can sit very close together (two GC events in the same tick, etc.) so picking strictly by "timestamp <= cursor"
        // would always pick the leftmost instead of the visually-closest.
        if (track.id === 'gauge-memory' && trace.memoryAllocEvents && trace.memoryAllocEvents.length > 0) {
          const hitMarginUs = 5 / vpRef.current.scaleX;
          let bestEvt: typeof trace.memoryAllocEvents[number] | undefined;
          let bestDist = hitMarginUs;
          for (const evt of trace.memoryAllocEvents) {
            const d = Math.abs(evt.timestampUs - usAtMouse);
            if (d < bestDist) { bestDist = d; bestEvt = evt; }
          }
          if (bestEvt) {
            onMarkerSelect({ kind: 'memory-alloc', event: bestEvt });
            onChunkSelect(null);
            onSpanSelect(null);
            return;
          }
        }
        if (track.id === 'gauge-gc' && trace.gcEvents && trace.gcEvents.length > 0) {
          const hitMarginUs = 5 / vpRef.current.scaleX;
          let bestEvt: typeof trace.gcEvents[number] | undefined;
          let bestDist = hitMarginUs;
          for (const evt of trace.gcEvents) {
            const d = Math.abs(evt.timestampUs - usAtMouse);
            if (d < bestDist) { bestDist = d; bestEvt = evt; }
          }
          if (bestEvt) {
            onMarkerSelect({ kind: 'gc', event: bestEvt });
            onChunkSelect(null);
            onSpanSelect(null);
            return;
          }
        }
      }
      // No hit on any interactive track — clear every selection.
      onChunkSelect(null);
      onSpanSelect(null);
      onMarkerSelect(null);
    }
  }, [buildLayout, getVisibleTicks, onChunkSelect, onSpanSelect, onMarkerSelect, trace.memoryAllocEvents, trace.gcEvents]);

  const onDblClick = useCallback((e: MouseEvent) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const GUTTER_WIDTH = gutterWidthRef.current;
    if (mx < GUTTER_WIDTH) return;
    const vp = vpRef.current;
    const usAtMouse = vp.offsetX + (mx - GUTTER_WIDTH) / vp.scaleX;
    const layout = layoutRef.current ?? buildLayout();
    const visibleTicks = getVisibleTicks();
    const hitMarginUs = 2 / vp.scaleX;

    const zoomToSpan = (startUs: number, endUs: number) => {
      animateToRange({ startUs, endUs });
    };

    for (const track of layout) {
      if (track.state === 'summary' || track.id === 'ruler') continue;  // summary state has no interactive content; ruler isn't clickable
      const ty = track.y - vp.scrollY;
      if (my < ty || my > ty + track.height) continue;

      if (track.id.startsWith('slot-')) {
        const threadSlot = parseInt(track.id.split('-')[1]);
        const chunkRowHeight = track.chunkRowHeight ?? 0;

        if (my < ty + chunkRowHeight) {
          for (const tick of visibleTicks) {
            for (const chunk of tick.chunks) {
              if (chunk.threadSlot !== threadSlot) continue;
              if (usAtMouse < chunk.startUs - hitMarginUs || usAtMouse > chunk.endUs + hitMarginUs) continue;
              zoomToSpan(chunk.startUs, chunk.endUs);
              return;
            }
          }
        } else {
          const clickedDepth = Math.floor((my - ty - chunkRowHeight) / SPAN_ROW_HEIGHT);
          if (clickedDepth >= 0) {
            for (const tick of visibleTicks) {
              const slotSpans = tick.spansByThreadSlot.get(threadSlot);
              if (!slotSpans || slotSpans.length === 0) continue;
              const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
              if (!endMax) continue;
              let lo = 0, hi = slotSpans.length;
              while (lo < hi) { const mid = (lo + hi) >>> 1; if (endMax[mid] < usAtMouse - hitMarginUs) lo = mid + 1; else hi = mid; }
              for (let i = lo; i < slotSpans.length; i++) {
                const span = slotSpans[i];
                if (span.startUs > usAtMouse + hitMarginUs) break;
                if ((span.depth ?? 0) !== clickedDepth) continue;
                if (usAtMouse < span.startUs - hitMarginUs || usAtMouse > span.endUs + hitMarginUs) continue;
                zoomToSpan(span.startUs, span.endUs);
                return;
              }
            }
          }
        }
      } else if (track.id === 'phases') {
        for (const tick of visibleTicks) {
          for (const phase of tick.phases) {
            if (usAtMouse >= phase.startUs - hitMarginUs && usAtMouse <= phase.endUs + hitMarginUs) {
              zoomToSpan(phase.startUs, phase.endUs);
              return;
            }
          }
        }
      }
      break;
    }
  }, [buildLayout, getVisibleTicks, animateToRange]);

  return (
    <div ref={containerRef} style={{ width: '100%', height: '100%', position: 'relative' }}>
      <canvas
        ref={canvasRef}
        style={{ width: '100%', height: '100%', cursor: isDraggingRef.current ? 'grabbing' : 'crosshair' }}
        onWheel={onWheel}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={onMouseUp}
        onDblClick={onDblClick}
        onMouseLeave={() => {
          isDraggingRef.current = false;
          hoverRef.current = null;
          mouseXRef.current = -1;
          hoveredHelpTrackIdRef.current = null;
          scheduleRender();
        }}
        onContextMenu={(e) => e.preventDefault()}
      />
    </div>
  );
}

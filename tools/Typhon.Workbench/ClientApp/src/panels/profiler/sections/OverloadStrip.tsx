import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { setupCanvas } from '@/libs/profiler/canvas/canvasUtils';
import { getStudioThemeTokens } from '@/libs/profiler/canvas/theme';
import {
  HELP_GLYPH_MARGIN_RIGHT,
  HELP_GLYPH_Y_BASELINE,
  HELP_ICON_GLYPH_WIDTH,
  isInHelpHitZone,
} from '@/libs/profiler/canvas/tickOverview';
import { HelpOverlay } from '@/panels/profiler/components/HelpOverlay';

/**
 * Overload diagnostics strip — issue #289 follow-up.
 *
 * <p>Auto-hidden ribbon directly under <c>TickOverview</c>. Surfaces, per tick:
 * <ul>
 *   <li><b>overrunRatio</b> = <c>actualMs / targetMs</c> as a vertical bar — taller bars = more
 *       overrun. Reference horizontal lines drawn at the OverloadDetector escalation threshold
 *       (1.20×) and the engine target (1.00×).</li>
 *   <li><b>tickMultiplier</b> as bar colour using the same amber → red ramp <c>TickOverview</c>
 *       uses for its bar tint, so a glance ties multiplier (here) to throttle (in TickOverview).</li>
 * </ul>
 *
 * <p><b>Auto-hide rule.</b> The strip occupies zero vertical space when every tick has
 * <c>multiplier &lt;= 1</c> AND <c>overrunRatio &lt; 1.0</c> — i.e. the engine has stayed inside its
 * budget the whole trace and there is nothing to diagnose. The first time the trace exhibits an
 * overrun (or any throttle), the strip materialises automatically.
 *
 * <p><b>Sparkline, not aligned scroll.</b> The strip compresses ALL ticks into the available width
 * (no pan/scroll). The user reads the strip for "is the engine throttling? when?" and switches to
 * <c>TickOverview</c>'s tinted bars for per-tick correlation.
 */
const OVERLOAD_HELP_LINES: string[] = [
  'Overload diagnostics strip',
  '',
  'Why it appears:',
  '  Visible only when at least one tick has multiplier > 1 OR',
  '  overrunRatio ≥ 1.0. A clean trace = strip stays hidden.',
  '',
  'What it shows (per tick, one column):',
  '',
  '  Bar height — overrunRatio = actualMs / targetMs',
  '    targetMs = 1000 / BaseTickRate (engine target at 1× rate).',
  '    1.0× means the tick took exactly its budget.',
  '    Y-axis caps at 2.0× — anything past that clamps to the top.',
  '',
  '  Bar colour — tick multiplier (engine throttle state)',
  '    1×  default — bar uses ratio-based hue (neutral / amber / red)',
  '    2×  amber              — engine slowed itself once',
  '    3×  orange             — second escalation step',
  '    4×  red                — third step (your migration-storm zone)',
  '    6×  dark red           — engine pinned at MinTickRateHz floor',
  '',
  '  Reference lines (dashed)',
  '    1.0× — the per-tick budget',
  '    1.2× — OverloadDetector escalation threshold',
  '         (5 consecutive ticks above this triggers throttle escalation)',
  '',
  'Intent classes (in tooltips):',
  '  CatchUp   — metronome target was already past at wait start; the',
  '              engine fired the next tick immediately (no real wait)',
  '  Throttled — multiplier > 1; engine voluntarily waited longer',
  '              between ticks to give itself headroom',
  '  Headroom  — multiplier == 1; normal idle waiting for the next',
  '              60 Hz boundary',
  '',
  'Hover any column for that tick\'s ratio, multiplier, level,',
  'pre-tick metronome wait, and the OverloadDetector streak counter:',
  '',
  '  Streak overrun: N / 5    (escalate at 5)',
  '    Consecutive ticks with overrunRatio > 1.2. Resets to 0 on',
  '    any non-overrun tick. Reaches 5 → multiplier escalates.',
  '',
  '  Streak underrun: N / 20  (deescalate at 20)',
  '    Consecutive ticks with overrunRatio < 0.6. Resets to 0 on',
  '    any overrun (a single tick > 1.2× breaks the streak).',
  '    Reaches 20 → multiplier deescalates one step.',
  '',
  '    Watch the streak: if it climbs to 18-19 then resets, your',
  '    workload has a periodic spike preventing deescalation.',
  '    Note: ticks in the 0.6×-1.2× dead-zone preserve the counter',
  '    (no climb, no reset) — only sub-0.6× ticks advance it.',
  '',
  'Move to TickOverview above to find the same tick by colour and',
  'click into TimeArea for span detail.',
  '',
  'Source data:',
  '  TickSummary.{OverloadLevel, TickMultiplier, MetronomeWaitUs,',
  '  MetronomeIntentClass} — written by IncrementalCacheBuilder',
  '  from the engine\'s TickEnd payload + observed Metronome.Wait',
  '  spans (kind 241). Cache version v10+.',
  '',
  'Press \'l\' to toggle help glyphs.',
];

export default function OverloadStrip() {
  const metadata = useProfilerSessionStore((s) => s.metadata);
  const legendsVisible = useProfilerViewStore((s) => s.legendsVisible);
  // Subscribe to the theme store so the component re-renders on theme switch — same pattern as TickOverview.
  // `getStudioThemeTokens()` reads live CSS vars; calling it inside the draw closure picks up the new tokens.
  useThemeStore((s) => s.theme);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [tickTooltip, setTickTooltip] = useState<{ lines: readonly string[]; clientX: number; clientY: number } | null>(null);
  const [helpTooltip, setHelpTooltip] = useState<{ clientX: number; clientY: number } | null>(null);
  // Refs read by the draw closure — avoid recreating draw on every state change.
  const helpHoveredRef = useRef(false);
  helpHoveredRef.current = helpTooltip !== null;

  // header.baseTickRate is orval's `number | string` (covers >2^53 ints — irrelevant here, but the type is shared with i64
  // counters elsewhere). Coerce defensively.
  const baseTickRate = Number(metadata?.header?.baseTickRate ?? 60);
  const targetMsForBaseRate = 1000 / Math.max(baseTickRate, 1);

  // Pre-compute one row per tick.
  const rows = useMemo(() => {
    if (!metadata?.tickSummaries) return [];
    const out = new Array<{
      tickNumber: number;
      ratio: number;       // overrunRatio = actualMs / targetMs (capped at 2.0 for chart Y)
      multiplier: number;  // 0/1 = nominal, 2..6 = throttle chain
      level: number;       // 0..4 OverloadLevel
      waitUs: number;      // metronome wait that preceded this tick
      intent: number;      // 0=CatchUp, 1=Throttled, 2=Headroom
      durationUs: number;  // raw tick duration for the tooltip
      consecOver: number;  // OverloadDetector consecutive-overrun streak (kind 242)
      consecUnder: number; // OverloadDetector consecutive-underrun streak (kind 242)
    }>(metadata.tickSummaries.length);
    for (let i = 0; i < metadata.tickSummaries.length; i++) {
      const s = metadata.tickSummaries[i];
      const durationUs = Number(s.durationUs);
      const actualMs = durationUs / 1000;
      const ratio = targetMsForBaseRate > 0 ? actualMs / targetMsForBaseRate : 0;
      out[i] = {
        tickNumber: Number(s.tickNumber),
        ratio,
        multiplier: s.tickMultiplier ?? 0,
        level: s.overloadLevel ?? 0,
        waitUs: s.metronomeWaitUs ?? 0,
        intent: s.metronomeIntentClass ?? 0,
        durationUs,
        consecOver: s.consecutiveOverrun ?? 0,
        consecUnder: s.consecutiveUnderrun ?? 0,
      };
    }
    return out;
  }, [metadata, targetMsForBaseRate]);

  // Auto-hide condition: every tick is healthy.
  const visible = useMemo(() => {
    for (let i = 0; i < rows.length; i++) {
      if (rows[i].multiplier > 1 || rows[i].ratio >= 1.0) return true;
    }
    return false;
  }, [rows]);

  // Render. Sparkline-style: width = container, one column per tick, vertical = ratio.
  // Theme tokens are read fresh inside the closure — the surrounding `useThemeStore` subscription
  // re-renders the component on theme switch which re-builds this callback with the new values.
  const draw = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas || rows.length === 0) return;
    const { width, height } = setupCanvas(canvas);
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    const theme = getStudioThemeTokens();

    // Background + bottom border.
    ctx.fillStyle = theme.card;
    ctx.fillRect(0, 0, width, height);

    // Y-axis range: 0 .. 2.0 ratio (above 2.0 clamps so the strip doesn't compress healthy data).
    const yMax = 2.0;
    const yRatio = (r: number) => height - 1 - (Math.min(r, yMax) / yMax) * (height - 2);

    // Threshold lines: 1.0 (target) and 1.2 (escalation). 0.6 omitted to keep the strip readable.
    ctx.strokeStyle = theme.mutedForeground;
    ctx.lineWidth = 0.5;
    ctx.setLineDash([3, 3]);
    [1.0, 1.2].forEach((r) => {
      const y = yRatio(r);
      ctx.beginPath();
      ctx.moveTo(0, y);
      ctx.lineTo(width, y);
      ctx.stroke();
    });
    ctx.setLineDash([]);

    // Per-tick column. Compress all ticks to width; min 1 px per column.
    const colWidth = Math.max(1, width / rows.length);
    for (let i = 0; i < rows.length; i++) {
      const r = rows[i];
      const x = (i / rows.length) * width;
      const top = yRatio(r.ratio);
      const baselineY = height - 1;
      const colour = multiplierTint(r.multiplier) ?? (r.ratio >= 1.2 ? '#dc2626' : r.ratio >= 1.0 ? '#f59e0b' : theme.overviewBar);
      ctx.fillStyle = colour;
      const drawW = Math.max(1, Math.floor(colWidth));
      ctx.fillRect(Math.floor(x), Math.floor(top), drawW, Math.ceil(baselineY - top));
    }

    // "1.2× / 1.0×" labels at left.
    ctx.font = '9px monospace';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = theme.mutedForeground;
    ctx.fillText('1.2×', 2, yRatio(1.2));
    ctx.fillText('1.0×', 2, yRatio(1.0));

    // Top-left strip label — backdrop + text on top of any bar that pokes into the corner.
    ctx.font = 'bold 10px monospace';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    const labelText = 'Overload';
    const labelWidth = ctx.measureText(labelText).width;
    ctx.fillStyle = theme.tooltipBackground;
    ctx.fillRect(2, 1, labelWidth + 4, 12);
    ctx.fillStyle = theme.mutedForeground;
    ctx.fillText(labelText, 4, 2);

    // "?" help glyph anchored top-right — same constants TickOverview uses so the two strips
    // line up visually. Backdrop ensures readability over any bar that climbs to the top of the
    // strip. Toggle with 'l' key — gated on legendsVisible from the profiler view store.
    if (legendsVisible) {
      ctx.font = 'bold 11px monospace';
      ctx.textAlign = 'right';
      ctx.textBaseline = 'alphabetic';
      const glyphRight = width - HELP_GLYPH_MARGIN_RIGHT;
      const bgW = HELP_ICON_GLYPH_WIDTH + 6;
      const bgH = 14;
      ctx.fillStyle = theme.tooltipBackground;
      ctx.fillRect(glyphRight - bgW + 3, HELP_GLYPH_Y_BASELINE - 11, bgW, bgH);
      ctx.fillStyle = helpHoveredRef.current ? theme.foreground : theme.mutedForeground;
      ctx.fillText('?', glyphRight, HELP_GLYPH_Y_BASELINE);
    }
  }, [rows, legendsVisible]);

  useEffect(() => {
    if (!visible) return;
    draw();
  }, [draw, visible]);

  // Re-render on resize.
  useEffect(() => {
    if (!visible) return;
    const ro = new ResizeObserver(() => draw());
    if (canvasRef.current) ro.observe(canvasRef.current);
    return () => ro.disconnect();
  }, [draw, visible]);

  // Hover handling — help glyph hit-test takes priority. While the cursor is over the "?",
  // we suppress the per-tick tooltip and brighten the glyph; the help overlay follows the cursor.
  const onPointerMove = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    if (!canvas || rows.length === 0) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    // Help-glyph hit test — same helper TickOverview uses for consistency.
    if (isInHelpHitZone(mx, my, rect.width, legendsVisible)) {
      if (tickTooltip !== null) setTickTooltip(null);
      setHelpTooltip({ clientX: e.clientX, clientY: rect.bottom });
      // Re-draw to brighten the glyph.
      draw();
      return;
    }

    // Outside the help zone — hide the help overlay if it was up, then show per-tick tooltip.
    if (helpTooltip !== null) {
      setHelpTooltip(null);
      draw();
    }

    const idx = Math.min(rows.length - 1, Math.max(0, Math.floor((mx / rect.width) * rows.length)));
    const r = rows[idx];
    const intentLabel = r.intent === 0 ? 'CatchUp' : r.intent === 1 ? 'Throttled' : r.intent === 2 ? 'Headroom' : `?${r.intent}`;
    const lines: string[] = [
      `Tick ${r.tickNumber}`,
      `Duration: ${(r.durationUs / 1000).toFixed(2)} ms`,
      `Ratio: ${r.ratio.toFixed(2)}×  (target ${targetMsForBaseRate.toFixed(2)} ms)`,
    ];
    if (r.multiplier > 1) lines.push(`Throttled: mult=${r.multiplier} (level ${r.level})`);
    if (r.waitUs > 0) {
      const waitLabel = r.waitUs >= 65535 ? '≥65 ms' : `${(r.waitUs / 1000).toFixed(1)} ms`;
      lines.push(`Pre-tick wait: ${waitLabel} (${intentLabel})`);
    }
    // OverloadDetector streak counters — climb to escalate (5) / deescalate (20). A "/ 5" or "/ 20"
    // gives the user the threshold context. Show only the active counter — the other side is always 0
    // since they're mutually-exclusive (set by Update's branches), and double-zero adds no info.
    if (r.consecOver > 0) {
      lines.push(`Streak overrun: ${r.consecOver} / 5  (escalate at 5)`);
    } else if (r.consecUnder > 0) {
      lines.push(`Streak underrun: ${r.consecUnder} / 20  (deescalate at 20)`);
    }
    setTickTooltip({ lines, clientX: e.clientX, clientY: rect.bottom });
  }, [rows, legendsVisible, tickTooltip, helpTooltip, draw, targetMsForBaseRate]);

  const onPointerLeave = useCallback(() => {
    if (tickTooltip !== null) setTickTooltip(null);
    if (helpTooltip !== null) {
      setHelpTooltip(null);
      draw();
    }
  }, [tickTooltip, helpTooltip, draw]);

  if (!visible) return null;

  // Mutually exclusive overlays — help overlay wins when present.
  const overlay = helpTooltip !== null ? (
    <HelpOverlay lines={OVERLOAD_HELP_LINES} clientX={helpTooltip.clientX} clientY={helpTooltip.clientY} />
  ) : tickTooltip !== null ? (
    <HelpOverlay lines={tickTooltip.lines} clientX={tickTooltip.clientX} clientY={tickTooltip.clientY} gap={2} />
  ) : null;

  return (
    <div
      ref={containerRef}
      className="w-full shrink-0 select-none border-b border-border"
      style={{ height: '32px' }}
    >
      <canvas
        ref={canvasRef}
        className="h-full w-full cursor-crosshair touch-none"
        onPointerMove={onPointerMove}
        onPointerLeave={onPointerLeave}
      />
      {overlay}
    </div>
  );
}

/**
 * Multiplier → bar tint. Mirrors <c>multiplierBarTint</c> in <c>tickOverview.ts</c> so the two
 * strips read as a single visual story. Returns null for mult&lt;=1 — caller falls back to a
 * ratio-based hue (amber if &gt;=1.0, red if &gt;=1.2, normal otherwise).
 */
function multiplierTint(multiplier: number): string | null {
  if (multiplier <= 1) return null;
  if (multiplier === 2) return '#d97706';
  if (multiplier === 3) return '#ea580c';
  if (multiplier === 4) return '#dc2626';
  return '#991b1b';
}

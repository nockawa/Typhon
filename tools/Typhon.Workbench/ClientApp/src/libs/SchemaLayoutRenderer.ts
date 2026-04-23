import type { ComponentSchema, Field } from '@/hooks/schema/types';

/** Fixed x64 cache-line size. Not configurable — Typhon targets x64. */
export const CACHE_LINE_BYTES = 64;

/** A stretch of bytes with no field — rendered as a diagonal-stripe pattern. */
export interface PaddingSegment {
  offset: number;
  size: number;
}

/** Visual constants for the byte grid. Exported so tests can reason in pixel space. */
export const LAYOUT_METRICS = Object.freeze({
  ROW_HEIGHT: 48,
  RULER_HEIGHT: 20,
  LEFT_MARGIN: 60, // space for the offset gutter (0x00, 0x40, ...)
  TOP_MARGIN: 4,
  /** Target visible bytes per row. Always a divisor of CACHE_LINE_BYTES so boundaries align. */
  BYTES_PER_ROW: 64,
  /** Minimum pixel width per byte — canvas stretches fields above this. */
  MIN_BYTE_PX: 8,
  /** Font size for field labels inside rectangles. */
  LABEL_FONT_PX: 11,
  /** Font size for offset gutter + ruler. */
  RULER_FONT_PX: 10,
});

/** Colors read from Tailwind CSS variables. Supplied by the React wrapper so light/dark themes work. */
export interface SchemaLayoutTheme {
  background: string;
  gridLine: string;
  ruler: string;
  label: string;
  fieldFill: string;
  fieldStroke: string;
  paddingFill: string;
  paddingStroke: string;
  cacheLine: string;
  warning: string;
  selection: string;
  indexedAccent: string;
}

/** Default dark-theme tokens — used as a fallback when <c>getStudioThemeTokens</c> isn't available (e.g., Vitest). */
export const DEFAULT_THEME: SchemaLayoutTheme = {
  background: '#0f172a',
  gridLine: '#1e293b',
  ruler: '#94a3b8',
  label: '#e2e8f0',
  fieldFill: '#1e3a8a',
  fieldStroke: '#3b82f6',
  paddingFill: '#334155',
  paddingStroke: '#475569',
  cacheLine: '#ef4444',
  warning: '#f59e0b',
  selection: '#facc15',
  indexedAccent: '#10b981',
};

/**
 * Owner-drawn cache-line-aligned component byte grid. Stateless per invocation — re-render on any
 * data/selection/theme change. Pure class: accepts a canvas, takes no React dependency, fully testable
 * with a mock 2D context.
 */
export class SchemaLayoutRenderer {
  private readonly canvas: HTMLCanvasElement;
  private readonly ctx: CanvasRenderingContext2D;
  private schema: ComponentSchema | null = null;
  private selection: string | null = null;
  private theme: SchemaLayoutTheme = DEFAULT_THEME;
  private dpr = 1;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    const ctx = canvas.getContext('2d');
    if (!ctx) {
      throw new Error('SchemaLayoutRenderer: 2D context unavailable');
    }
    this.ctx = ctx;
  }

  setSchema(schema: ComponentSchema | null): void {
    this.schema = schema;
  }

  setSelection(fieldName: string | null): void {
    this.selection = fieldName;
  }

  setTheme(theme: SchemaLayoutTheme): void {
    this.theme = theme;
  }

  /** Call before render() when canvas CSS size or devicePixelRatio changed. */
  setDevicePixelRatio(dpr: number): void {
    this.dpr = Math.max(1, dpr);
  }

  /**
   * Padding segments — gaps between consecutive fields (ordered by offset), plus any tail padding
   * between the last field and storageSize. The Layout view renders these with a distinct pattern
   * so wasted bytes are obvious.
   */
  computePadding(): PaddingSegment[] {
    if (!this.schema) return [];
    const fields = this.orderedFields();
    const segments: PaddingSegment[] = [];
    let cursor = 0;
    for (const f of fields) {
      if (f.offset > cursor) {
        segments.push({ offset: cursor, size: f.offset - cursor });
      }
      cursor = f.offset + f.size;
    }
    if (cursor < this.schema.storageSize) {
      segments.push({ offset: cursor, size: this.schema.storageSize - cursor });
    }
    return segments;
  }

  /**
   * Fields whose byte range spans a 64-byte cache-line boundary — the cache-miss-per-access signal
   * the Layout view highlights with a warning icon.
   */
  computeCrossBoundary(): Field[] {
    if (!this.schema) return [];
    return this.orderedFields().filter((f) => {
      const start = Math.floor(f.offset / CACHE_LINE_BYTES);
      const end = Math.floor((f.offset + f.size - 1) / CACHE_LINE_BYTES);
      return end > start;
    });
  }

  /** Translate a click in canvas CSS pixels to the field it landed on, or null for padding/ruler/gutter. */
  hitTest(x: number, y: number): Field | null {
    if (!this.schema) return null;
    const rows = this.numberOfRows();
    const { BYTES_PER_ROW, ROW_HEIGHT, RULER_HEIGHT, LEFT_MARGIN, TOP_MARGIN } = LAYOUT_METRICS;
    if (x < LEFT_MARGIN || y < TOP_MARGIN + RULER_HEIGHT) return null;

    const row = Math.floor((y - TOP_MARGIN - RULER_HEIGHT) / ROW_HEIGHT);
    if (row < 0 || row >= rows) return null;

    const bytePx = this.bytePx();
    const byteInRow = Math.floor((x - LEFT_MARGIN) / bytePx);
    if (byteInRow < 0 || byteInRow >= BYTES_PER_ROW) return null;

    const byteOffset = row * BYTES_PER_ROW + byteInRow;
    if (byteOffset >= this.schema.storageSize) return null;

    for (const f of this.orderedFields()) {
      if (byteOffset >= f.offset && byteOffset < f.offset + f.size) {
        return f;
      }
    }
    return null;
  }

  /** Full redraw. Idempotent; safe to call on every React effect. */
  render(): void {
    this.applyDprTransform();

    const { width, height } = this.cssSize();
    const ctx = this.ctx;
    ctx.fillStyle = this.theme.background;
    ctx.fillRect(0, 0, width, height);

    if (!this.schema) return;

    this.drawRuler();
    this.drawPadding();
    this.drawFields();
    this.drawCacheLineBoundaries();
    this.drawSelection();
  }

  // ── internals ─────────────────────────────────────────────────────────────────

  private orderedFields(): Field[] {
    return this.schema ? this.schema.fields : [];
  }

  private numberOfRows(): number {
    if (!this.schema) return 0;
    return Math.max(1, Math.ceil(this.schema.storageSize / LAYOUT_METRICS.BYTES_PER_ROW));
  }

  private bytePx(): number {
    const { LEFT_MARGIN, BYTES_PER_ROW, MIN_BYTE_PX } = LAYOUT_METRICS;
    const usable = Math.max(0, this.cssSize().width - LEFT_MARGIN - 8);
    return Math.max(MIN_BYTE_PX, Math.floor(usable / BYTES_PER_ROW));
  }

  private cssSize(): { width: number; height: number } {
    // In DPR-aware mode the canvas's attribute width/height is scaled by dpr; CSS size is what we
    // care about for layout math. Prefer the CSS size from getBoundingClientRect when available,
    // else fall back to the attribute dims divided by dpr.
    const rect =
      typeof this.canvas.getBoundingClientRect === 'function'
        ? this.canvas.getBoundingClientRect()
        : null;
    if (rect && rect.width > 0 && rect.height > 0) {
      return { width: rect.width, height: rect.height };
    }
    return { width: this.canvas.width / this.dpr, height: this.canvas.height / this.dpr };
  }

  private applyDprTransform(): void {
    // Scale the draw coords so we reason in CSS pixels while drawing at physical resolution.
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
  }

  private drawRuler(): void {
    const ctx = this.ctx;
    const { LEFT_MARGIN, TOP_MARGIN, RULER_HEIGHT, BYTES_PER_ROW, RULER_FONT_PX } = LAYOUT_METRICS;
    const bytePx = this.bytePx();
    ctx.fillStyle = this.theme.ruler;
    ctx.font = `${RULER_FONT_PX}px ui-monospace, monospace`;
    ctx.textBaseline = 'middle';
    ctx.textAlign = 'left';
    for (let b = 0; b <= BYTES_PER_ROW; b += 8) {
      const x = LEFT_MARGIN + b * bytePx;
      ctx.fillText(b.toString(), x + 2, TOP_MARGIN + RULER_HEIGHT / 2);
    }
    // Offset gutter per row — centered between the view's left edge and the grid's left edge so
    // the `0x…` labels breathe instead of hugging the panel border.
    const rows = this.numberOfRows();
    ctx.textAlign = 'center';
    for (let r = 0; r < rows; r++) {
      const offset = r * BYTES_PER_ROW;
      const y = TOP_MARGIN + RULER_HEIGHT + r * LAYOUT_METRICS.ROW_HEIGHT + LAYOUT_METRICS.ROW_HEIGHT / 2;
      ctx.fillText(`0x${offset.toString(16).padStart(2, '0').toUpperCase()}`, LEFT_MARGIN / 2, y);
    }
    ctx.textAlign = 'left';
  }

  private drawPadding(): void {
    const ctx = this.ctx;
    ctx.fillStyle = this.theme.paddingFill;
    ctx.strokeStyle = this.theme.paddingStroke;
    ctx.lineWidth = 1;
    for (const seg of this.computePadding()) {
      this.forEachSegmentRect(seg.offset, seg.size, (x, y, w, h) => {
        ctx.fillRect(x, y, w, h);
        ctx.strokeRect(x, y, w, h);
        this.drawDiagonalStripes(x, y, w, h);
      });
    }
  }

  private drawFields(): void {
    const ctx = this.ctx;
    const { LABEL_FONT_PX } = LAYOUT_METRICS;
    const crossBoundary = new Set(this.computeCrossBoundary().map((f) => f.name));
    for (const f of this.orderedFields()) {
      this.forEachSegmentRect(f.offset, f.size, (x, y, w, h) => {
        ctx.fillStyle = this.theme.fieldFill;
        ctx.fillRect(x, y, w, h);
        ctx.strokeStyle = crossBoundary.has(f.name) ? this.theme.warning : this.theme.fieldStroke;
        ctx.lineWidth = crossBoundary.has(f.name) ? 2 : 1;
        ctx.strokeRect(x, y, w, h);

        // Bookmark tag in the bottom-right corner = "this field has an index". Sits below the two
        // text lines (name + typeName) so it never competes with them for horizontal space. Only
        // drawn when the cell is wide enough to fit the icon at all.
        const INDEX_ICON_W = 8;
        const INDEX_ICON_H = 11;
        const INDEX_ICON_PADDING = 3;
        if (f.isIndexed && w >= INDEX_ICON_W + INDEX_ICON_PADDING * 2) {
          this.drawIndexIcon(
            x + w - INDEX_ICON_W - INDEX_ICON_PADDING,
            y + h - INDEX_ICON_H - INDEX_ICON_PADDING,
          );
        }

        ctx.font = `${LABEL_FONT_PX}px ui-monospace, monospace`;
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';

        // Truncate to fit the rect rather than relying on fillText's maxWidth argument, which
        // horizontally scales glyphs until they look smeared. fitText drops characters and appends
        // an ellipsis, matching how CSS text-overflow works.
        const textMax = Math.max(0, w - 8);
        ctx.fillStyle = this.theme.label;
        ctx.fillText(fitText(ctx, f.name, textMax), x + 4, y + 6);
        ctx.fillStyle = this.theme.ruler;
        ctx.fillText(fitText(ctx, f.typeName, textMax), x + 4, y + 6 + LABEL_FONT_PX + 2);
      });
    }
  }

  private drawCacheLineBoundaries(): void {
    if (!this.schema) return;
    const ctx = this.ctx;
    const { LEFT_MARGIN, TOP_MARGIN, RULER_HEIGHT, ROW_HEIGHT, BYTES_PER_ROW } = LAYOUT_METRICS;
    ctx.strokeStyle = this.theme.cacheLine;
    ctx.lineWidth = 2;
    const rows = this.numberOfRows();
    const bytePx = this.bytePx();
    const rowWidth = BYTES_PER_ROW * bytePx;

    // Two cases depending on BYTES_PER_ROW vs CACHE_LINE_BYTES:
    //   (1) BYTES_PER_ROW is a multiple of CACHE_LINE_BYTES (our default 64) — every row starts on
    //       a cache-line boundary. Draw horizontal rules between adjacent rows.
    //   (2) BYTES_PER_ROW divides CACHE_LINE_BYTES — cache-line boundaries fall inside rows. Draw
    //       vertical rules at those in-row positions.
    if (BYTES_PER_ROW % CACHE_LINE_BYTES === 0) {
      for (let r = 1; r < rows; r++) {
        const y = TOP_MARGIN + RULER_HEIGHT + r * ROW_HEIGHT;
        ctx.beginPath();
        ctx.moveTo(LEFT_MARGIN, y);
        ctx.lineTo(LEFT_MARGIN + rowWidth, y);
        ctx.stroke();
      }
    } else {
      for (let r = 0; r < rows; r++) {
        const rowStartByte = r * BYTES_PER_ROW;
        const rowEndByte = Math.min(rowStartByte + BYTES_PER_ROW, this.schema.storageSize);
        for (let b = rowStartByte + 1; b < rowEndByte; b++) {
          if (b % CACHE_LINE_BYTES === 0) {
            const x = LEFT_MARGIN + (b - rowStartByte) * bytePx;
            const yTop = TOP_MARGIN + RULER_HEIGHT + r * ROW_HEIGHT;
            ctx.beginPath();
            ctx.moveTo(x, yTop);
            ctx.lineTo(x, yTop + ROW_HEIGHT);
            ctx.stroke();
          }
        }
      }
    }
  }

  private drawSelection(): void {
    if (!this.schema || !this.selection) return;
    const field = this.orderedFields().find((f) => f.name === this.selection);
    if (!field) return;
    const ctx = this.ctx;
    // Match the base field's stroke thickness — selection is signaled by color (and the soft white
    // wash below), not by a heavier outline. Cross-boundary fields use 2px, everyone else 1px.
    const isCrossBoundary = this.computeCrossBoundary().some((f) => f.name === field.name);
    const lineWidth = isCrossBoundary ? 2 : 1;
    this.forEachSegmentRect(field.offset, field.size, (x, y, w, h) => {
      // Semi-transparent blue wash for the selected cell — tints the fill so the selection reads
      // at a glance regardless of the underlying fieldFill color. Drawn before the stroke so the
      // new border sits crisply on top of the tint.
      ctx.fillStyle = 'rgba(0, 0, 255, 0.15)';
      ctx.fillRect(x, y, w, h);
      ctx.strokeStyle = this.theme.selection;
      ctx.lineWidth = lineWidth;
      ctx.strokeRect(x, y, w, h);
    });
  }

  /**
   * Field occupies a byte range that may cross row boundaries. Call <paramref name="cb"/> once per
   * contiguous rectangle (one per row).
   */
  private forEachSegmentRect(
    offset: number,
    size: number,
    cb: (x: number, y: number, w: number, h: number) => void,
  ): void {
    const { BYTES_PER_ROW, LEFT_MARGIN, TOP_MARGIN, RULER_HEIGHT, ROW_HEIGHT } = LAYOUT_METRICS;
    const bytePx = this.bytePx();
    let remaining = size;
    let cursor = offset;
    while (remaining > 0) {
      const row = Math.floor(cursor / BYTES_PER_ROW);
      const colStart = cursor % BYTES_PER_ROW;
      const takeInRow = Math.min(remaining, BYTES_PER_ROW - colStart);
      const x = LEFT_MARGIN + colStart * bytePx;
      const y = TOP_MARGIN + RULER_HEIGHT + row * ROW_HEIGHT;
      cb(x, y, takeInRow * bytePx, ROW_HEIGHT);
      cursor += takeInRow;
      remaining -= takeInRow;
    }
  }

  /**
   * Draw a small bookmark/tag glyph (8×11 px) anchored at (<paramref name="x"/>, <paramref name="y"/>).
   * Universal "tagged/indexed" visual — a rectangle with a V-notch at the bottom. Filled with the
   * indexed-accent color.
   */
  private drawIndexIcon(x: number, y: number): void {
    const ctx = this.ctx;
    const w = 8;
    const h = 11;
    const notch = 3;
    ctx.fillStyle = this.theme.indexedAccent;
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.lineTo(x + w, y);
    ctx.lineTo(x + w, y + h);
    ctx.lineTo(x + w / 2, y + h - notch);
    ctx.lineTo(x, y + h);
    ctx.closePath();
    ctx.fill();
  }

  private drawDiagonalStripes(x: number, y: number, w: number, h: number): void {
    const ctx = this.ctx;
    ctx.save();
    ctx.beginPath();
    ctx.rect(x, y, w, h);
    ctx.clip();
    ctx.strokeStyle = this.theme.paddingStroke;
    ctx.lineWidth = 1;
    for (let i = -h; i < w + h; i += 6) {
      ctx.beginPath();
      ctx.moveTo(x + i, y);
      ctx.lineTo(x + i + h, y + h);
      ctx.stroke();
    }
    ctx.restore();
  }
}

/**
 * Fit `text` into `maxWidth` pixels under the context's current font, truncating with a single
 * Unicode ellipsis when necessary. Returns an empty string when not even the ellipsis fits.
 * Binary-searches the character cut point to stay O(log n) per call, matters for wide grids with
 * many fields redrawn on every pan/zoom.
 */
function fitText(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string {
  if (maxWidth <= 0 || text.length === 0) return '';
  if (ctx.measureText(text).width <= maxWidth) return text;
  const ellipsis = '…';
  const ellipsisWidth = ctx.measureText(ellipsis).width;
  if (ellipsisWidth > maxWidth) return '';
  let lo = 0;
  let hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    const width = ctx.measureText(text.slice(0, mid)).width + ellipsisWidth;
    if (width <= maxWidth) lo = mid;
    else hi = mid - 1;
  }
  return lo > 0 ? text.slice(0, lo) + ellipsis : '';
}

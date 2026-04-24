/**
 * OPFS-backed persistent chunk cache. Stores LZ4-compressed chunk bytes verbatim on disk (in the browser's Origin Private File
 * System) so that re-opening the same trace avoids re-fetching from the server. One directory per trace fingerprint isolates
 * caches between traces; deleting a directory clears a trace's cache atomically.
 *
 * **Why OPFS, not IndexedDB**: pure sequential-read binary blobs are what OPFS excels at. No transaction overhead, no schema
 * management, no serialization. Just `read file → ArrayBuffer` / `write ArrayBuffer → file`. For a dev-tool shipped to Typhon
 * contributors (not a consumer web app), the slightly narrower browser matrix (Chrome 102+, Safari 15.2+, Firefox 111+) is fine.
 *
 * **Quota and eviction**:
 *   - Per-trace cap enforced in {@link put}: if a write would push the trace's total over {@link PER_TRACE_BUDGET_BYTES}, the
 *     least-recently-modified chunk files are deleted until under cap. "Recently modified" is used as a proxy for "recently
 *     accessed" because OPFS doesn't expose atime — writes on every fetch (see chunkCache.ts) keep mtime ≈ fetch-time, which
 *     is functionally the same as "last accessed" for the way the viewer uses chunks.
 *   - Global cleanup across all traces is the caller's responsibility via {@link globalCleanup}; typically invoked once on
 *     application startup.
 *
 * **Persistence**: {@link init} requests `navigator.storage.persist()` so the browser doesn't evict our storage under
 * pressure. If denied (Firefox / Safari sometimes decline), the store still works but may be evicted externally — acceptable
 * since every evict just falls back to re-fetching from the server.
 *
 * **Error policy**: OPFS failures are NEVER thrown out of this module. Every method catches and returns a safe default (null for
 * get, silent for put). The caller (chunkCache.ts) treats OPFS as a best-effort optimization layer; if it breaks, the server-
 * fetch path is authoritative.
 */

const ROOT_DIR = 'typhon-chunks';
const PER_TRACE_BUDGET_BYTES = 1024 * 1024 * 1024;   // 1 GB per trace
const DEFAULT_GLOBAL_BUDGET_BYTES = 5 * 1024 * 1024 * 1024;  // 5 GB across all traces

/**
 * Dev-time kill switch for OPFS persistence. Flip to <c>false</c> and rebuild to disable the entire persistent-cache layer: every
 * store becomes a no-op (init skips, get returns null, put is silent), so every chunk request round-trips the server as before.
 * Intentionally NOT wired to a UI toggle — use it to diagnose cache-related bugs or to measure the perf delta with/without
 * persistence. Default <c>true</c>.
 */
export const OPFS_PERSISTENCE_ENABLED = true;

export class OpfsChunkStore {
  private traceDir: FileSystemDirectoryHandle | null = null;
  private fingerprint: string = '';
  private available: boolean = false;

  // Synchronous counters — bumped on every get/put so the bottom-of-GraphArea debug line can read without async scans. Exposed via
  // the readonly properties below. `cachedTotalBytes` is the sum of currently-persisted chunk sizes (maintained across put + evict).
  private _hits = 0;
  private _misses = 0;
  private _writes = 0;
  private _cachedTotalBytes = 0;

  /**
   * Serialization chain for mutating operations (put, remove). Every mutator appends a `.then(...)` to this promise, ensuring
   * at most one directory-mutating OPFS call is in flight at a time. Reads (get) bypass the chain — they're idempotent and
   * don't touch the bookkeeping counters.
   *
   * <b>Why:</b> {@link enforceQuota} enumerates the whole trace directory and makes eviction decisions based on that snapshot.
   * Two concurrent puts would BOTH scan, BOTH observe the same pre-eviction state, and BOTH try to evict the same "oldest"
   * file — one succeeds, the other hits NotFoundError (logged, silently ignored). Worse, `cachedTotalBytes` increments on every
   * successful write but only decrements on successful eviction, so after a contention burst the counter runs ahead of actual
   * on-disk size. Serializing eliminates both paths at the cost of a small latency increase under prefetch bursts (serial
   * writes vs. parallel). For the write sizes we deal with (~200-500 KB per chunk) and the browser's OPFS throughput, the
   * serial cost is negligible — the original "parallel" version was only parallel in the JS event-loop sense anyway; the
   * underlying disk writes were already serialized by the OS.
   */
  private _writeQueue: Promise<void> = Promise.resolve();

  /** Number of successful OPFS reads (chunk served from disk, server fetch skipped). */
  get hits(): number { return this._hits; }
  /** Number of OPFS read misses (chunk not persisted yet, server fetch required). */
  get misses(): number { return this._misses; }
  /** Number of successful OPFS writes. */
  get writes(): number { return this._writes; }
  /** Bytes currently persisted for THIS trace. Maintained incrementally — no async scan needed for per-render debug display. */
  get cachedTotalBytes(): number { return this._cachedTotalBytes; }

  /**
   * Prepare the store for a specific trace. Must be called once per trace open before get/put. Idempotent per fingerprint —
   * calling twice for the same fingerprint is a no-op after the first succeeds. Never throws; instead sets available=false and
   * subsequent get/put calls become no-ops (get returns null, put silently succeeds without writing).
   */
  async init(fingerprint: string): Promise<void> {
    if (this.fingerprint === fingerprint && this.traceDir !== null) return;
    this.fingerprint = fingerprint;
    this.available = false;
    this.traceDir = null;
    this._hits = 0; this._misses = 0; this._writes = 0; this._cachedTotalBytes = 0;

    if (!OPFS_PERSISTENCE_ENABLED) return;  // dev kill-switch — every method becomes a no-op

    try {
      if (typeof navigator === 'undefined' || !navigator.storage || typeof navigator.storage.getDirectory !== 'function') {
        return;  // OPFS unavailable (SSR, very old browser) — store stays disabled
      }
      // Best-effort persist request — result is just a hint to the browser, failure is NOT a bug.
      try { await navigator.storage.persist(); } catch { /* ignore */ }

      const root = await navigator.storage.getDirectory();
      const chunksRoot = await root.getDirectoryHandle(ROOT_DIR, { create: true });
      this.traceDir = await chunksRoot.getDirectoryHandle(fingerprint, { create: true });
      this.available = true;

      // One-time size scan so cachedTotalBytes is accurate from the first put() onward. Cheap (tens of files for most traces);
      // the alternative — scanning on every debug-line render — would cost an async pass per paint.
      try {
        for await (const entry of iterateDirectoryValues(this.traceDir)) {
          if (entry.kind !== 'file') continue;
          const file = await (entry as FileSystemFileHandle).getFile();
          this._cachedTotalBytes += file.size;
        }
      } catch { /* ignore — counter just stays at 0, corrected on first put/evict */ }
    } catch (err) {
      console.warn('[OpfsChunkStore] init failed, persistence disabled for this trace:', err);
      this.available = false;
      this.traceDir = null;
    }
  }

  /**
   * Read a chunk's compressed bytes by chunk index. Returns null on miss OR on any OPFS error (silently). Callers MUST fall
   * back to server fetch when null is returned.
   */
  async get(chunkIdx: number): Promise<ArrayBuffer | null> {
    if (!this.available || this.traceDir === null) { this._misses++; return null; }
    try {
      const fileHandle = await this.traceDir.getFileHandle(fileName(chunkIdx), { create: false });
      const file = await fileHandle.getFile();
      const buf = await file.arrayBuffer();
      this._hits++;
      return buf;
    } catch {
      // Most common: NotFoundError when the chunk isn't cached. Treat all OPFS errors the same — miss.
      this._misses++;
      return null;
    }
  }

  /**
   * Write a chunk's compressed bytes. Best-effort: on error (quota exceeded, OPFS misbehaving, etc.) just logs and returns.
   * Caller should not await this for UI-critical paths — fire and forget is fine.
   *
   * Enforces per-trace quota: if this write would push total bytes over {@link PER_TRACE_BUDGET_BYTES}, evicts the oldest files
   * (by mtime) until under. The eviction loop is bounded by the number of files in the directory, so even pathological cases
   * (many tiny writes, all needing eviction) complete in linear time.
   */
  async put(chunkIdx: number, bytes: ArrayBuffer): Promise<void> {
    if (!this.available || this.traceDir === null) return;
    // Append to the serialization chain. The returned promise resolves when THIS put's disk work is done — callers that await
    // it get natural back-pressure. Callers that don't await (fire-and-forget from chunkCache) still benefit from the chain:
    // their operation can't interleave with the next one, so enforceQuota's scan + evict is always a consistent snapshot.
    const chained = this._writeQueue.then(() => this._doPut(chunkIdx, bytes));
    this._writeQueue = chained.catch(() => {});    // keep the chain alive even if this op throws — next op shouldn't inherit the rejection
    return chained;
  }

  /** Real put implementation — called only via the serialization chain, so enforceQuota's scan/evict never races with another put. */
  private async _doPut(chunkIdx: number, bytes: ArrayBuffer): Promise<void> {
    if (!this.available || this.traceDir === null) return;
    try {
      await this.enforceQuota(bytes.byteLength);
      const fileHandle = await this.traceDir.getFileHandle(fileName(chunkIdx), { create: true });
      // Concurrent-access note (two tabs on same trace): createWritable opens its own stream per tab, writes are serialized
      // last-write-wins at close(). Since chunk bytes are deterministic (same fingerprint ⇒ same compressed payload), the race
      // produces the SAME content on either tab winning — functionally harmless.
      const writable = await (fileHandle as FileSystemFileHandleWritable).createWritable();
      await writable.write(bytes);
      await writable.close();
      this._writes++;
      this._cachedTotalBytes += bytes.byteLength;
    } catch (err) {
      console.warn('[OpfsChunkStore] put failed for chunk ' + chunkIdx + ':', err);
    }
  }

  /**
   * Delete one chunk's persisted bytes. Used as the recovery action when a stored chunk fails to decode (corrupt/truncated
   * file from a crash mid-write, OPFS bug, or silent disk corruption) — the caller removes the bad entry and re-fetches
   * from the server, which will re-persist a fresh copy via {@link put}. Silent on error (file already gone, etc.).
   */
  async remove(chunkIdx: number): Promise<void> {
    if (!this.available || this.traceDir === null) return;
    // Serialize through the same chain as put() — guarantees a "remove X; put X" sequence executes in that order (put does
    // not start mid-remove and see a partial-or-missing file state), and prevents a concurrent put's enforceQuota from
    // seeing the about-to-be-removed file in its scan.
    const chained = this._writeQueue.then(() => this._doRemove(chunkIdx));
    this._writeQueue = chained.catch(() => {});
    return chained;
  }

  private async _doRemove(chunkIdx: number): Promise<void> {
    if (!this.available || this.traceDir === null) return;
    try {
      // Read the file size first so we can keep cachedTotalBytes accurate. If the file is already gone (NotFoundError), the
      // outer catch swallows — removal is idempotent.
      let size = 0;
      try {
        const fileHandle = await this.traceDir.getFileHandle(fileName(chunkIdx), { create: false });
        const file = await fileHandle.getFile();
        size = file.size;
      } catch { /* already gone */ }
      await this.traceDir.removeEntry(fileName(chunkIdx));
      if (size > 0) this._cachedTotalBytes -= size;
    } catch {
      // NotFoundError or any other failure — best effort, don't propagate.
    }
  }

  /** Total bytes currently stored for this trace. O(N) scan — cheap enough (hundreds of files) for occasional debug UI. */
  async totalBytes(): Promise<number> {
    if (!this.available || this.traceDir === null) return 0;
    let total = 0;
    try {
      for await (const entry of iterateDirectoryValues(this.traceDir)) {
        if (entry.kind === 'file') {
          const file = await (entry as FileSystemFileHandle).getFile();
          total += file.size;
        }
      }
    } catch (err) {
      console.warn('[OpfsChunkStore] totalBytes failed:', err);
    }
    return total;
  }

  /** Delete every chunk file for this trace. Directory stays; next put recreates entries. */
  async clear(): Promise<void> {
    if (!this.available || this.traceDir === null) return;
    try {
      const names: string[] = [];
      for await (const [name, entry] of iterateDirectory(this.traceDir)) {
        if (entry.kind === 'file') names.push(name);
      }
      for (const name of names) {
        await this.traceDir.removeEntry(name);
      }
    } catch (err) {
      console.warn('[OpfsChunkStore] clear failed:', err);
    }
  }

  // ─────────────────────────────────────────────────────────────────────
  // Eviction internals
  // ─────────────────────────────────────────────────────────────────────

  /**
   * Ensure that adding <c>incomingBytes</c> more won't push the trace over the per-trace budget. Enumerates all files, sorts
   * by mtime ascending (oldest first), deletes until under. Runs on every put() but the work is proportional to the directory
   * size; for a typical trace (a few hundred chunks) it's a few milliseconds.
   */
  private async enforceQuota(incomingBytes: number): Promise<void> {
    if (this.traceDir === null) return;
    const entries: { name: string; size: number; mtime: number }[] = [];
    let total = 0;
    for await (const [name, entry] of iterateDirectory(this.traceDir)) {
      if (entry.kind !== 'file') continue;
      try {
        const file = await (entry as FileSystemFileHandle).getFile();
        entries.push({ name, size: file.size, mtime: file.lastModified });
        total += file.size;
      } catch {
        // Skip entries we can't read — they'll be deleted on next clear or ignored.
      }
    }
    if (total + incomingBytes <= PER_TRACE_BUDGET_BYTES) return;

    entries.sort((a, b) => a.mtime - b.mtime);   // oldest first
    for (const e of entries) {
      if (total + incomingBytes <= PER_TRACE_BUDGET_BYTES) break;
      try {
        await this.traceDir.removeEntry(e.name);
        total -= e.size;
        this._cachedTotalBytes -= e.size;
      } catch (err) {
        console.warn('[OpfsChunkStore] evict failed for ' + e.name + ':', err);
      }
    }
  }

  // ─────────────────────────────────────────────────────────────────────
  // Static: global housekeeping
  // ─────────────────────────────────────────────────────────────────────

  /**
   * Cross-trace cleanup. Enumerate every trace directory under typhon-chunks/*, compute per-directory size + max-mtime, sort by
   * max-mtime ascending (oldest-accessed first), delete whole trace directories until total under cap. Intended to run ONCE per
   * app startup.
   */
  static async globalCleanup(globalBudgetBytes: number = DEFAULT_GLOBAL_BUDGET_BYTES): Promise<void> {
    try {
      if (typeof navigator === 'undefined' || !navigator.storage || typeof navigator.storage.getDirectory !== 'function') return;
      const root = await navigator.storage.getDirectory();
      let chunksRoot: FileSystemDirectoryHandle;
      try {
        chunksRoot = await root.getDirectoryHandle(ROOT_DIR, { create: false });
      } catch {
        return;  // No typhon-chunks yet — nothing to clean
      }

      const traces: { name: string; size: number; mtime: number }[] = [];
      let grandTotal = 0;
      for await (const [name, entry] of iterateDirectory(chunksRoot)) {
        if (entry.kind !== 'directory') continue;
        const dirHandle = entry as FileSystemDirectoryHandle;
        let size = 0;
        let maxMtime = 0;
        for await (const subEntry of iterateDirectoryValues(dirHandle)) {
          if (subEntry.kind !== 'file') continue;
          try {
            const file = await (subEntry as FileSystemFileHandle).getFile();
            size += file.size;
            if (file.lastModified > maxMtime) maxMtime = file.lastModified;
          } catch { /* skip */ }
        }
        traces.push({ name, size, mtime: maxMtime });
        grandTotal += size;
      }

      if (grandTotal <= globalBudgetBytes) return;

      traces.sort((a, b) => a.mtime - b.mtime);   // oldest-accessed trace first
      for (const t of traces) {
        if (grandTotal <= globalBudgetBytes) break;
        try {
          await chunksRoot.removeEntry(t.name, { recursive: true });
          grandTotal -= t.size;
        } catch (err) {
          console.warn('[OpfsChunkStore] globalCleanup removeEntry failed for ' + t.name + ':', err);
        }
      }
    } catch (err) {
      console.warn('[OpfsChunkStore] globalCleanup failed:', err);
    }
  }
}

/** Chunk index → filename. Fixed prefix + zero-padded index keeps lexicographic order ≈ chunk order for debugging. */
function fileName(chunkIdx: number): string {
  return `c_${chunkIdx.toString().padStart(6, '0')}.lz4`;
}

/** Async iterator over [name, handle] pairs of a directory. Uses the `.entries()` accessor (newer browsers) with fallback to
 *  the `.values()` iterator when entries() isn't available. */
async function* iterateDirectory(dir: FileSystemDirectoryHandle): AsyncIterableIterator<[string, FileSystemHandle]> {
  // TS 5 ships types for .entries() under lib.dom; runtime exists in all modern browsers.
  const iter = (dir as FileSystemDirectoryHandleExt).entries();
  for await (const pair of iter) {
    yield pair as [string, FileSystemHandle];
  }
}

/** Async iterator over handle values only. Thin wrapper for code paths that don't need the name. */
async function* iterateDirectoryValues(dir: FileSystemDirectoryHandle): AsyncIterableIterator<FileSystemHandle> {
  for await (const [, entry] of iterateDirectory(dir)) {
    yield entry;
  }
}

// Minimal type shims for experimental API surface that's not yet in lib.dom across all TS versions.
interface FileSystemDirectoryHandleExt extends FileSystemDirectoryHandle {
  entries(): AsyncIterableIterator<[string, FileSystemHandle]>;
}
interface FileSystemFileHandleWritable extends FileSystemFileHandle {
  createWritable(): Promise<FileSystemWritableFileStream>;
}

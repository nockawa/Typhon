# Typhon Profiler — User Manual

**Last Updated:** 2026-04-19
**Applies to:** Typhon Profiler — engine side `feature/243-tracy-profiler`, viewer Phase 4 (cache v8).
**Audience:** developers running a Typhon-based application who need to profile it, and operators reading profiler traces in the web viewer. If you're looking for the design-level "how does it work internally," read [typhon-profiler.md](./typhon-profiler.md) instead.

---

## 1. What the Profiler Is

A low-overhead in-process event tracer + live/offline web viewer. The engine records typed events (transaction commits, B+Tree operations, page-cache traffic, WAL flushes, checkpoints, scheduler work, GC collections, unmanaged allocations, per-tick gauge snapshots) into per-thread SPSC ring buffers; a dedicated consumer thread drains them and fans out to exporters — a `.typhon-trace` file on disk, a live TCP stream, or both. The web viewer (a React SPA served by `Typhon.Profiler.Server`) shows:

- A per-thread **flame graph** of every system, transaction, and sub-span that ran, with nested parent/child depth
- A **tick timeline** at the top, one bar per tick, colored by duration
- A **gauge region** above the timeline covering Memory, Page Cache, WAL, Transactions + UoW, and GC — all aligned to the same time axis
- **Sub-rows** per track (reads / writes / evictions / commits / rollbacks / checkpoints / etc.) for quick visual scanning without clicking individual spans
- A **detail pane** on the right for inspecting the selected span, chunk, or marker
- An optional **CPU flame graph** tab fed by a companion `.nettrace` file (EventPipe CPU samples)

The engine-side cost is "zero when disabled" in the literal JIT-elision sense: every emit site gates on a `static readonly bool`, and the branch is folded out at JIT time when the subsystem is off. When enabled, typical overhead is a few nanoseconds per span on the producer thread.

---

## 2. Quick Start

### 2.1 Enable the profiler in your app

1. **Reference the engine** (normal): `Typhon.Engine` via `ProjectReference` or NuGet.

2. **Drop a `typhon.telemetry.json` next to your executable** (the file is loaded from the current working directory or the assembly directory at startup):

   ```json
   {
     "Typhon": {
       "Telemetry": {
         "Enabled": true,
         "Profiler": {
           "Enabled": true,
           "GcTracing":         { "Enabled": true },
           "MemoryAllocations": { "Enabled": true },
           "Gauges":            { "Enabled": true }
         }
       }
     }
   }
   ```

   Every sub-flag is independent and gates a different subsystem — see §3. Set `false` on any sub-flag you don't need; the emit sites for that subsystem then cost nothing at runtime.

3. **Start the profiler and attach an exporter** at the top of your program, before the engine starts producing events you care about:

   ```csharp
   using Typhon.Engine.Profiler;
   using Typhon.Engine.Profiler.Exporters;

   // (Typhon runtime construction — services, DatabaseEngine, etc.)

   // Build a session-metadata descriptor (systems, archetypes, component types)
   var metadata = new ProfilerSessionMetadata(
       systems: runtime.EnumerateSystemsForProfiler(),
       archetypes: ArchetypeRegistry.EnumerateForProfiler(),
       componentTypes: ArchetypeRegistry.EnumerateComponentTypesForProfiler()
   );

   // Attach exporters BEFORE starting
   var fileExporter = new FileExporter("./myrun.typhon-trace");
   TyphonProfiler.AttachExporter(fileExporter);

   // (optional) live stream on TCP :9876 for the web viewer to pick up
   var tcpExporter = new TcpExporter(port: 9876);
   TyphonProfiler.AttachExporter(tcpExporter);

   // Start — idempotent; safe to call twice
   TyphonProfiler.Start(parent: ResourceRegistry.Profiler, metadata);

   try
   {
       // …run your workload…
   }
   finally
   {
       TyphonProfiler.Stop();   // flushes + joins exporter threads
   }
   ```

   Practical tips:
   - **Name your main thread** (`Thread.CurrentThread.Name = "MyApp.Main"`) before emitting anything. The profiler captures the name in a `ThreadInfo` record when your thread claims its slot, and the viewer labels the lane with it. Unnamed threads show as numeric slot indices.
   - `AttachExporter`/`DetachExporter` must be called while the profiler is stopped. Attempting to mutate the exporter list while running races against the consumer thread.
   - `TyphonProfiler.Start` is idempotent — double-calling is a no-op (not an exception).
   - You don't have to add any tracing calls to your own code. Typhon's built-in call sites (transactions, B+Trees, page cache, WAL, checkpoints, etc.) emit automatically. Add your own via `TyphonEvent.BeginNamedSpan("MyCustomSpan")` if you want app-specific spans.

### 2.2 Run the viewer server

From the Typhon repo:

```bash
cd tools/Typhon.Profiler.Server
dotnet run -c Release
```

The server listens on `http://localhost:5000` by default. Open that URL in your browser — you'll get the React SPA. The server serves both static assets and the REST + SSE API the SPA talks to.

### 2.3 Open a trace

Three ways, exposed by the **File** menu in the top bar:

- **Load…** — file picker that uploads a `.typhon-trace` (and optional `.nettrace` CPU-sample companion) from your machine to the server. Goes into a per-process temp directory; cleaned up when the server shuts down.
- **Open from path…** — type an absolute or server-visible file path. No upload, no copy, no duplication. The sidecar cache is built alongside the source the first time you open it and reused on subsequent opens. This is the fastest path for traces you already have on the same machine as the server.
- **Connect Live** — prompts for `host:port` (your app's `TcpExporter` endpoint). The server connects, streams events, and fans them out to your browser in real time. A "LIVE" green pill appears in the menu bar. Disconnect via **File → Disconnect Live**.

The first time you open a large trace, the server builds a **sidecar cache** (`.typhon-trace-cache`) next to the source. A **build-progress overlay** shows MB scanned, tick count, event count, and estimated time. Subsequent opens of the same file skip the build — the cache is keyed by a SHA-256 fingerprint over `(mtime, length, first 4 KB, last 4 KB)`; if nothing has changed, the cache is reused instantly.

---

## 3. Configuration Reference

### 3.1 `typhon.telemetry.json` — full schema

Loaded once at application start. Read in this order: environment variables (`TYPHON__TELEMETRY__*`) first, then `typhon.telemetry.json` in the current working directory, then the one next to the assembly. Built-in defaults (all false) apply last.

```json
{
  "Typhon": {
    "Telemetry": {
      "Enabled": true,
      "Profiler": {
        "Enabled":           true,
        "GcTracing":         { "Enabled": true },
        "MemoryAllocations": { "Enabled": true },
        "Gauges":            { "Enabled": true }
      }
    }
  }
}
```

| Key | Purpose | Performance cost when on |
|---|---|---|
| `Typhon.Telemetry.Enabled` | Master gate for all telemetry subsystems. If `false`, every other flag is forced off regardless. | 0 (pure class-load read) |
| `Profiler.Enabled` | Master profiler gate. Required for anything below to matter. When `false`, every `BeginXxx` factory short-circuits to `default(T)` and costs essentially nothing. | A few nanoseconds per span on the producer thread |
| `Profiler.GcTracing.Enabled` | Starts the GC ingestion thread and subscribes to the `.NET` runtime GC events. Emits `GcStart`/`GcEnd`/`GcSuspension` records + populates the GC gauge track. | One dedicated background thread; negligible per-event cost (GCs happen at human timescales) |
| `Profiler.MemoryAllocations.Enabled` | Every `PinnedMemoryBlock` allocate/free emits a `MemoryAllocEvent` instant. Covers all of Typhon's NativeMemory traffic (single funnel). | ~40 B on the wire per alloc/free; order-of-nanoseconds cost at the call site |
| `Profiler.Gauges.Enabled` | Per-tick `PerTickSnapshot` emits — memory totals, page-cache bucket counts, WAL buffer usage, transaction/UoW live counts + cumulative throughput. Needed for the entire gauge region to populate. | One snapshot record per tick; reads existing `Interlocked` source fields, one pass |

**Operational pattern**: leave `Profiler.Enabled: true` always (it's cheap by default); turn on the sub-flags selectively when you need their data, turn them off when you don't. Production-like runs with `GcTracing`/`MemoryAllocations`/`Gauges` all enabled measure <1 % scheduler-thread overhead on the workloads we've tested.

### 3.2 `ProfilerOptions` — runtime tunables

Passed to `TyphonProfiler.Start` in code (there's no JSON binding for these yet — tune in code if you need to).

| Field | Default | What it controls |
|---|---|---|
| `ConsumerCadence` | 1 ms | How often the consumer thread wakes to drain slot rings and fan out to exporters. Lower = lower end-to-end latency for live viewers, higher CPU. Higher = batched I/O efficiency, more records buffered. |
| `PerExporterChannelDepth` | 4 | Bounded queue depth between the consumer thread and each exporter. When an exporter is slow, this many cadence ticks of batches can buffer before drop-newest kicks in. |
| `MergeBufferBytes` | 512 KB | Capacity of the consumer's per-pass merge scratch buffer. Drains from all slots accumulate here before sorting. Sized so a single drain pass can absorb a heavy burst (tens of thousands of records) without leaving bytes in the producer rings for the next pass. |

**When to tune:**
- **Lots of short-lived threads** churning through slot claim/retire: increase `ThreadSlotRegistry.DefaultBufferCapacity` (currently 128 KB/slot) so spawn bursts don't overflow the per-slot ring.
- **High-latency exporter** (e.g., slow disk or network): either increase `PerExporterChannelDepth` (more buffering before drop) or relax the source (fewer events via finer `typhon.telemetry.json` gating).
- **Live viewer feels laggy**: reduce `ConsumerCadence` toward 500 µs. Viewer update cadence caps at consumer cadence.

### 3.3 Environment-variable override

Any key above can be overridden by environment variables using double-underscore as the key separator:

```bash
TYPHON__TELEMETRY__PROFILER__GAUGES__ENABLED=false dotnet run
```

Useful for toggling a sub-flag on a CI run without editing the JSON.

---

## 4. Running the Viewer

### 4.1 Starting the server

```bash
cd tools/Typhon.Profiler.Server
dotnet run -c Release
```

- Serves on `http://localhost:5000` by default (change with `--urls` or `ASPNETCORE_URLS`).
- CORS is wide-open (`AllowAnyOrigin`) — this is a dev tool meant for localhost.
- File-path inputs from the browser go through `PathGuard.ValidateTracePath` which enforces an extension whitelist (`.typhon-trace`, `.nettrace`) and resolves `..` segments. Still: run the server on a trusted machine; it's not hardened for internet exposure.
- Response compression is on (Brotli + Gzip, fastest level) for JSON responses. Streaming (SSE + binary chunk) bypasses compression by design.

### 4.2 What the server does

- Serves the React SPA at `/`
- Serves REST + SSE endpoints under `/api/`
- Builds sidecar caches on first open, verifies fingerprint freshness on subsequent opens
- Optionally acts as a TCP client to your engine's `TcpExporter` (see §4.4 "Live mode")

The server has no authentication, TLS, or rate limiting. If you need to analyze traces from a production machine, either run the server on that machine and tunnel the port, or copy the `.typhon-trace` file off the machine and run the server locally.

### 4.3 File-mode vs. live-mode

| | File mode | Live mode |
|---|---|---|
| Source | Static `.typhon-trace` file on disk | TCP stream from a running app's `TcpExporter` |
| Trigger | File → Load or File → Open from path | File → Connect Live |
| Persistence | OPFS browser cache remembers decoded chunks between page reloads (keyed by fingerprint) | In-memory buffer only — reloading the page clears everything |
| Completeness | Full trace visible; pan/zoom is random-access | Incrementally appended; you can pan to history but "follow mode" auto-scrolls to newest |
| Companion data | Optional `.nettrace` CPU-sample flame graph sidecar | Not available |

### 4.4 Live-mode setup

On the **app side**, attach a `TcpExporter` before starting the profiler:

```csharp
TyphonProfiler.AttachExporter(new TcpExporter(port: 9876));
TyphonProfiler.Start(parent, metadata);
```

On the **viewer side**, File → Connect Live → enter `localhost:9876`. The server opens the TCP connection, reads the INIT frame (metadata), and streams Block frames. The browser gets a live SSE feed with `metadata` + `tick` + 5 s `heartbeat` events.

`TcpExporter` is single-client — only one viewer can connect at a time. A second connection is rejected. If you want multiple observers, add a second `TcpExporter` on a different port.

---

## 5. Reading the Viewer

### 5.1 Layout overview

```
┌────────────────────────────────────────────────────────────────────────┐
│ MenuBar: File, View, Undo/Redo, filename, "LIVE" pill, error banner    │
├────────────────────────────────────────────────────────────────────────┤
│ TickTimeline: one bar per tick, height ∝ duration, viewport highlighted│
├────────────────────────────────────────────────────────────────────────┤
│ GraphArea (scrollable/zoomable canvas):                                │
│   ├── Gauge region — Memory / PageCache / WAL / Tx+UoW / GC            │
│   ├── Phase track — SystemDispatch / UoW Flush / WriteTickFence        │
│   ├── Per-thread-slot lanes — system chunks + spans (flame graph)      │
│   │   └── Mini-rows under each lane: disk IO / cache / tx / wal / ...  │
│   └── Pending-chunk placeholder stripes (over ticks still loading)     │
├────────────────────────────────────────────────────────────────────────┤
│ DetailPane (resizable from the divider) — selected span/chunk fields   │
└────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Navigating

- **Mouse wheel**: pan horizontally across the timeline.
- **Shift + wheel**: zoom in/out, anchored at the cursor.
- **Drag on TickTimeline**: jump-pan the viewport.
- **Double-click a span**: smooth-animate the viewport to that span's bounds.
- **Click a span/chunk/marker**: select it; DetailPane shows its fields.
- **Back / Forward buttons** in the MenuBar (or mouse back/forward if your mouse has them): undo/redo viewport changes. History is debounced — rapid pan + scroll counts as one history entry.

### 5.3 The gauge region (top)

Collapsible via **View → Show gauges** (shortcut `g`). Each track shows data drawn from per-tick `PerTickSnapshot` records; all tracks share the same time axis as the flame-graph below.

| Track | What it shows |
|---|---|
| **Memory — GC heap** | Stacked area: Gen0 + Gen1 + Gen2 + LOH + POH sizes after the last GC, plus a committed-bytes line |
| **Memory — Unmanaged** | Total unmanaged bytes allocated through `PinnedMemoryBlock` + a dashed peak-reference line + live-blocks count |
| **Page Cache — buckets** | Stacked area of mutually-exclusive bucket counts: Free / CleanUsed / DirtyUsed / Exclusive / EpochProtected pages |
| **Page Cache — pending IO** | Line chart of `PendingIoReads` — reads currently in flight |
| **WAL — Commit buffer** | Current WAL commit-buffer usage vs. capacity reference line; scales to observed data when usage ≪ capacity so a "quiet" WAL is still readable |
| **WAL — Pool** | Staging pool rented / peak-rented / capacity; cumulative total rents derived into a per-tick rent-rate |
| **Transactions + UoW** | Live active/pool/registry counts (stacked) + per-tick throughput derived from cumulative `*Total` counters by subtracting consecutive snapshots |
| **GC** | Red bars for `GcSuspension` windows (full EE-suspend); triangle/dot markers for `GcStart` / `GcEnd` events |

**Hovering sub-rows** gives a sub-row-specific tooltip. Each track's sub-row layout is fixed (weight-split); a single tooltip dispatches on the Y coordinate so you get the right details without having to click the exact event.

Tracks can be collapsed independently (click the caret next to the track label); collapse state is persisted to `localStorage`.

### 5.4 The flame-graph area (bottom)

- **Phase track** (horizontal strip): tick phases as colored bars — SystemDispatch, UoW Flush, WriteTickFence, etc.
- **Per-thread-slot lanes**: one horizontal band per producer thread. Label = the thread's name captured at slot claim time.
  - **Top of each lane**: `SchedulerChunk` spans — one per system chunk run on this thread, colored per system.
  - **Below that**: nested OpenTelemetry-style span flame graph. Depth is walked from `ParentSpanId` links, cap 32. Same-depth adjacent spans ≤ 1 px wide coalesce into a grey "N spans — zoom in" block for readability.
  - **Mini-rows under the flame**: small stacked indicators for disk reads/writes, cache hits/misses, transaction commits/rollbacks/persists, WAL flushes/waits, and checkpoints. Each mini-row has a label pill (colored swatch + text) at the left edge.

### 5.5 Detail pane (right)

Shows the fields of whatever's selected:

- **Span** (e.g., `TransactionCommit`): start/end/duration, thread slot + name, depth, parent span, and kind-specific fields (TSN, component count, conflict-detected flag, etc.). If the span was async-folded, you'll see both the synchronous kickoff duration and the full async tail.
- **Scheduler chunk**: system name, chunk index, entities processed, total chunks, thread slot.
- **Marker** (memory alloc, GC event): timestamp, size/direction/source for allocs; generation/reason/type/heap deltas for GC events.

Drag the divider to resize (min 200 px, max 600 px). Click X in the detail pane to clear the selection.

### 5.6 FlameGraph tab (optional, `.nettrace` CPU samples)

If you uploaded a `.nettrace` file alongside your trace, a CPU-sample flame graph is available. It's a separate view from the main timeline — it shows stacks-per-sample aggregated across the selected time range, with counts per symbol. Useful for "why is X slow" follow-up once the timeline has pointed at a slow tick.

---

## 6. Keyboard Shortcuts

| Key | Action |
|---|---|
| `g` | Toggle gauge region |
| `l` | Toggle gauge legends |
| Mouse wheel | Pan horizontally |
| Shift + wheel | Zoom |
| Click span/chunk | Select, populate detail pane |
| Double-click span | Smooth-animate zoom to span bounds |
| Mouse Back/Forward | Undo / redo viewport change |

Input focus (e.g., when typing in DetailPane fields) suppresses `g` / `l` to avoid surprises.

---

## 7. Performance & Sizing

### 7.1 Engine-side

- Per-span overhead (profiler enabled): ~25–50 ns on the producer thread in the common case (JIT-friendly code path, no allocations).
- Per-span overhead (profiler disabled): 0 ns. `static readonly bool` gates fold the branch at JIT.
- Per-tick snapshot overhead (Gauges enabled): one pass over ~20 `Interlocked`-updated source fields + one record emit on the scheduler thread. Sub-microsecond.
- GC tracing overhead: the `GcIngestionThread` runs at GC-event frequency (seconds-to-minutes in steady state). Negligible.
- **If you see "dropped events"** (`TyphonProfiler.TotalDroppedEvents > 0`): the producer is emitting faster than the consumer can drain. Check `ProfilerOptions.ConsumerCadence` (try lowering), or increase `ThreadSlotRegistry.DefaultBufferCapacity` (default 128 KB/slot) if drops are clustered during spawn bursts.

### 7.2 Viewer-side

- **In-memory chunk cache**: 500 MB LRU by default. Configurable — tune in `chunkCache.ts` if you're viewing traces measured in tens of GB.
- **OPFS persistent cache**: 1 GB per trace, 5 GB global (automatic eviction of oldest-accessed). Transparent second-level cache; survives page reloads.
- **Worker pool**: 1–4 workers based on `navigator.hardwareConcurrency`. Least-busy dispatch; per-slot failure isolation (one OOM doesn't kill the pool).
- **Assembly memoization**: pure pan/zoom without chunk churn reuses the previous merged assembly — specifically designed to keep intra-tick-split traces smooth.

### 7.3 Sidecar cache sizing

The `.typhon-trace-cache` file is roughly **10–30 % the size** of the source `.typhon-trace` (depending on fold ratio and compression). Built once per fingerprint; lives next to the source on disk; safe to delete (will be rebuilt on next open).

---

## 8. Troubleshooting

### 8.1 "Nothing shows up in the viewer" / no events captured

Check, in order:
1. `typhon.telemetry.json` is in the same directory as your executable (or your working directory when you ran it).
2. `Typhon.Telemetry.Enabled` AND `Profiler.Enabled` are both `true`.
3. You called `TyphonProfiler.Start(…)` before the code you want to profile runs.
4. You attached at least one exporter (`FileExporter` for offline, `TcpExporter` for live).
5. You called `TyphonProfiler.Stop()` at the end — without Stop, the file may be missing the final block.

A smoke-test check: `TyphonProfiler.ActiveSlotCount > 0` after your workload runs = at least one thread claimed a slot.

### 8.2 "GC track is empty / GC -- no GC activity capture"

Verify `Profiler.GcTracing.Enabled: true` in your config. The `TelemetryConfig.ProfilerGcTracingActive` flag is `static readonly` and read once at class load — if you edit the JSON after the process starts, you have to restart. Also verify the process is actually doing GCs (the event listener captures every GC but a genuinely idle process won't have any).

### 8.3 "Memory allocations track looks empty"

`Profiler.MemoryAllocations.Enabled: true` is required. Note that this only covers `PinnedMemoryBlock`-based NativeMemory allocations today — Typhon's arena/pool/`ArrayPool` sites aren't instrumented yet (see the [roadmap](../../../claude/design/observability/06-profiler-feature-roadmap.md) tier 3.2).

### 8.4 "Pan/zoom is stuttery on one particular trace"

Usually this is a pathologically dense tick (hundreds of thousands of events in a single tick). The v8 cache's intra-tick split should handle this transparently, but if you're looking at an older cache file, the fingerprint check should have forced a rebuild. Delete the `.typhon-trace-cache` sidecar and reopen — the new sidecar will use v8 chunking.

If stuttering persists, check the browser devtools console for OPFS errors (some browsers or private-browsing modes disable OPFS). The viewer falls back to server fetch only, which is slower.

### 8.5 "Chunk failed to load" toasts

Transient network errors (server restarted, momentary 500) trigger a 30-second retry-after window per chunk. During that window the chunk shows as a striped placeholder. If the condition persists beyond 30 s, pan away and back or reopen the trace.

### 8.6 "Live mode keeps disconnecting"

The engine-side `TcpExporter` is single-client — if a second viewer tries to connect, the first is dropped. Also check your app is still running (if it crashed, the connection closes cleanly).

### 8.7 "Cache keeps rebuilding on every open"

The sidecar is keyed by `(source mtime, length, first 4 KB, last 4 KB)`. If you regenerate the source file in place (e.g., a CI job overwrites it), the fingerprint changes and the cache rebuilds. That's correct behavior. If you want to keep old sidecars across regenerations, copy the source + sidecar to a stable path before opening.

### 8.8 "The viewer says X-Chunk-Is-Continuation mismatch"

This is a DEV-mode assertion that fires when the server's manifest flag disagrees with the header on the fetched chunk — it indicates the sidecar format is out of sync with the client. Delete the sidecar (`.typhon-trace-cache`) and reopen to force a rebuild with the current server. In production builds the mismatch logs a warning and trusts the manifest.

---

## 9. Trace File & Sidecar Cache

### 9.1 `.typhon-trace` — the source of truth

Produced by `FileExporter`. Binary format, magic `'TYTR'`, current version 3. Contains:
- 64-byte header (timing anchors, counts, timestamp frequency)
- System/archetype/component-type tables
- LZ4-compressed record blocks (up to 256 KB uncompressed each)
- Optional trailing span-name table (for runtime-interned `NamedSpan` kinds)

Immutable after capture. Safe to copy, rename, move, email. **Never edit** — the fingerprint would drift from its sidecar without rebuild.

### 9.2 `.typhon-trace-cache` — the sidecar

Produced by the server the first time a trace is opened. Binary, magic `'TYTC'`, current version 8. Contains:
- Per-tick summaries (for instant timeline rendering)
- Chunk manifest (pointers into the LZ4-compressed chunk payload, plus `Flags` bit 0 = `IsContinuation`)
- Global metrics (p95 tick duration, total events, etc.)
- Folded chunk data (LZ4-compressed, with async-completion pairs collapsed into single records)
- Pre-computed GC suspension list

Regenerable. If the sidecar is missing or its fingerprint doesn't match the source, the server rebuilds it automatically. Deleting a sidecar is always safe — it'll be rebuilt on next open.

### 9.3 `.nettrace` — optional CPU-sample companion

If you produce a `.nettrace` (via `dotnet-trace` or the EventPipe API) during the same session, place it next to the `.typhon-trace` and upload both via File → Load. The viewer will offer a FlameGraph tab. The correlation works by QPC timestamp — as long as the two files were captured on the same machine during the same session, timestamps align.

To capture both at once:
```bash
dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime,System.Threading.Tasks.TplEventSource \
    --format nettrace --output myrun.nettrace --process-id <pid>
```
Run your workload while the trace is collecting, then Ctrl+C. You'll get both `myrun.typhon-trace` (from your app's `FileExporter`) and `myrun.nettrace` (from `dotnet-trace`).

---

## 10. Appendix: Event kind catalog (quick reference)

| ID | Kind | Category | Span / Instant |
|---|---|---|---|
| 0–6 | `TickStart` … `Instant` | Scheduler | Instant |
| 7 | `GcStart` | GC | Instant |
| 8 | `GcEnd` | GC | Instant |
| 9 | `MemoryAllocEvent` | Memory | Instant |
| 10 | `SchedulerChunk` | Scheduler | Span |
| 20–23 | `TransactionCommit`/`Rollback`/`CommitComponent`/`Persist` | Transaction | Span |
| 30–35 | `EcsSpawn`/`Destroy`/`QueryExecute`/`QueryCount`/`QueryAny`/`ViewRefresh` | ECS | Span |
| 40–43 | `BTreeInsert`/`Delete`/`NodeSplit`/`NodeMerge` | B+Tree | Span |
| 50–59 | `PageCacheFetch`/`DiskRead`/`DiskWrite`/`AllocatePage`/`Flush`/`Evicted`/`...Completed`/`Backpressure` | Page Cache | Span |
| 60 | `ClusterMigration` | Migration | Span |
| 75 | `GcSuspension` | GC | Span |
| 76 | `PerTickSnapshot` | Gauges | Instant-like |
| 77 | `ThreadInfo` | Slot identity | Instant-like |
| 80–82 | `WalFlush`/`SegmentRotate`/`Wait` | WAL | Span |
| 83–88 | `CheckpointCycle`/`Collect`/`Write`/`Fsync`/`Transition`/`Recycle` | Checkpoint | Span |
| 89 | `StatisticsRebuild` | Statistics | Span |
| 200 | `NamedSpan` | User-custom | Span |

The full registry (required / optional payload fields per kind) is in [typhon-profiler.md §4.4](./typhon-profiler.md). The gauge ID registry (what each `PerTickSnapshot` entry means) is in [typhon-profiler.md §12](./typhon-profiler.md).

---

## 11. See Also

- [typhon-profiler.md](./typhon-profiler.md) — design-level architecture of the engine capture pipeline, wire format, sidecar cache, and viewer internals
- [06-profiler-feature-roadmap.md](../../../claude/design/observability/06-profiler-feature-roadmap.md) — what's coming next (analyzer layer, DB-specific views, lock contention, arena allocation tracing)
- [02-span-instrumentation.md](../../../claude/design/observability/02-span-instrumentation.md) — how Typhon spans correlate with OTel Activity context (if you're also running an OTLP exporter alongside the profiler)
- [`src/Typhon.Engine/Profiler/`](../../../src/Typhon.Engine/Profiler/) — engine-side source
- [`tools/Typhon.Profiler.Server/`](../) — server + React client source

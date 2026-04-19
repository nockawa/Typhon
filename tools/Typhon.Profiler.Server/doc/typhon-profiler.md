# Typhon Profiler — Typed-Event Capture

**Last Updated:** 2026-04-19
**Status:** Implemented (engine + viewer Phase 1–4 + GC tracing + gauges + intra-tick split)
**GitHub Issue:** [#243](https://github.com/nockawa/Typhon/issues/243)
**Branch:** `feature/243-tracy-profiler`
**Supersedes:** the original `IRuntimeInspector` + 32-byte fixed-event profiler, and the earlier `06-tracy-style-profiler.md` design plan (content promoted here).
**User-facing companion:** [readme.md](./readme.md) — how to enable, run, and read the profiler from the app/operator side.

---

## 1. What This Document Is

A design-reference for the typed-event profiler + scalable web viewer as they exist in the code today — engine capture path, wire format, sidecar cache, chunk-based server, binary TS decoder, viewer plumbing, and the complete call-site catalog. It replaces the pre-implementation plan that used to live at `06-tracy-style-profiler.md`.

The document evolved in five waves:

1. **Engine + v3 wire format + monolithic viewer** — original #243 scope. Producer API, SPSC ring, consumer drain, file + TCP exporters, `/api/trace/events` endpoint, Preact SPA.
2. **Scalable viewer (Phase 1–3)** — sidecar cache + chunk-based lazy loading + binary LZ4 transport added on top, targeting traces 100× the original size (~15 GB source). `/api/trace/open`, `/api/trace/chunk`, `/api/trace/chunk-binary`, `/api/trace/build-progress` SSE. TS decoder + LZ4 block decompressor in the Web Worker. Viewport-driven prefetch + LRU cache. See §8.
3. **Event catalog extensions** — Page-cache async completions + backpressure, WAL, Checkpoint, Statistics, ClusterMigration added beyond the original 20 call sites. See §4.4.
4. **Runtime observability extensions** — In-process `.NET` GC-event tracing (`GcStart`, `GcEnd`, `GcSuspension`) via an `EventListener` + dedicated ingestion thread; unmanaged `MemoryAllocEvent` instants tied to `PinnedMemoryBlock`; per-tick gauge snapshots (`PerTickSnapshot` + stable `GaugeId` registry) covering memory / page cache / WAL / Transactions+UoW; per-slot `ThreadInfo` identity records. See §§4.4, 6.7, 12.
5. **Viewer Phase 4 — intra-tick splitting (cache v8)** — sidecar cache bumped to `ChunkerVersion = 8`; `ChunkManifestEntry.Flags` (bit 0 = `IsContinuation`); server can emit multiple chunks with the same `FromTick`/`ToTick` when a single tick exceeds `IntraTickByteCap` (2 MiB) or `IntraTickEventCap` (100 000 events); chunks on the wire are identified by `chunkIdx` instead of a tick range; client-side `mergeTickData` stitches continuation chunks transparently during assembly; worker pool (1–4) + OPFS persistent chunk cache + per-chunk failure gate + assembly memoization keep pan/zoom smooth on pathological 2M-event ticks. See §§4.6, 8.1, 8.3.

The roadmap for upcoming work (analyzer layer, DB-specific views, lock contention) lives in [06-profiler-feature-roadmap.md](../../../claude/design/observability/06-profiler-feature-roadmap.md).

If you're looking for:

- **How to use the profiler from engine code** → §5 Producer API
- **How to add a new event kind** → §4 Wire Format + §5.4 Ref-struct event pattern
- **How to write a new exporter** → §6 Exporters + `IProfilerExporter`
- **How the browser viewer reads the data** → §8 Viewer pipeline
- **Sidecar cache / chunking architecture** → §8.2
- **Binary transport + TS decoder** → §8.3
- **What's still TODO** → §10 Known gaps + [06-profiler-feature-roadmap.md](../../../claude/design/observability/06-profiler-feature-roadmap.md)

---

## 2. Goals and Non-Goals

### Goals

1. **Any-thread emission** — any thread can emit events in ~25-50 ns on the hot path. No "only workers" restriction.
2. **Typed events** — one ref-struct event type per conceptual operation (BTreeInsert, TransactionCommit, EcsSpawn, etc.) with compile-time-known payload shapes. No string-keyed dictionaries in the hot path.
3. **Variable-size records** — each kind encodes only the fields it needs. A B+Tree span header-only record is 37 bytes; an ECS view refresh with three optional fields is 50 bytes. The old fixed 64-byte struct wasted ~40 bytes per record on fields that were always zero.
4. **Zero allocations** — producer hot path allocates nothing: ref structs on the stack, slot buffers pre-allocated, consumer batches pooled and refcounted.
5. **Distributed-trace context** — every span record can optionally carry a 16-byte `TraceId` hi/lo pair captured from `Activity.Current` at begin time, so Typhon spans nest correctly under an enclosing HTTP request activity.
6. **Per-exporter failure isolation** — a slow or crashed exporter cannot stall the consumer or other exporters; its batches get drop-oldest on queue overflow.
7. **JIT-elimination when disabled** — `TelemetryConfig.ProfilerActive` is a `static readonly bool` set at class load, so `if (!ProfilerActive) return default;` folds to nothing on Tier 1 JIT when the config has the profiler disabled.
8. **Unified API** — one `TyphonEvent` surface replaces both the old `IRuntimeInspector` and `TyphonActivitySource.StartActivity`. No dual producer paths.

### Non-Goals

1. **OTel `Activity` side-output** — users who want OTel interop use the (future) OTLP exporter or consume `.typhon-trace` files directly. No inline `Activity` allocations — that was the reason for this rewrite.
2. **Unbounded thread count** — capped at 256 concurrent live threads via a fixed-size registry. Overflow drops events rather than growing unboundedly.
3. **Lossless capture** — drop-newest at both producer-to-slot and consumer-to-exporter points. Tracy's "prefer losing a sample over blocking" philosophy.
4. **Backward compatibility with the old `.typhon-trace` format** — v3 is a clean break. v2 files are unreadable by the new reader. The format is versioned (header carries `Version = 3`), so future evolution is possible.
5. **Hard throughput targets** — correctness and good primitives are the goal; measured performance is a byproduct, not a contractual number.

---

## 3. Architecture Overview

Three processes collaborate end-to-end: the profiled application (Typhon engine with the `TyphonEvent` producer API + dedicated consumer thread), the profiler server (ASP.NET Core Minimal API that parses records into JSON DTOs), and the browser viewer (Preact SPA with a Canvas-based Gantt renderer). Both file and live paths share the same wire format, the same codec stack, and the same parser on the consumer side.

<a href="./assets/typhon-profiler-architecture.svg">
  <img src="./assets/typhon-profiler-architecture.svg" width="1200"
       alt="Typhon Profiler — System Architecture (v3 typed-event)">
</a>
<sub>D2 source: <code>assets/src/typhon-profiler-architecture.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

Key invariants:

- **SPSC per slot**: each slot has one producer (the owning thread) and one consumer (the profiler consumer thread). No CAS on the hot path — pure release/acquire ordering via `Volatile.Read`/`Write` on `_head`/`_tail`.
- **Multi-slot, single consumer**: the consumer thread is the only reader of any slot, so it owns the timestamp sort across slots, the free-slot-reclaim, and the batch fan-out.
- **Single writer per exporter queue**: the consumer writes, the per-exporter thread reads via `BlockingCollection.GetConsumingEnumerable`. No shared exporter-thread pool.
- **Refcounted batches**: one batch is fanned out to N exporters; the last one to call `batch.Release()` returns it to the pool. Dropped batches (queue full) call `Release()` immediately to balance the refcount.

---

## 4. Wire Format

The wire format is identical between the on-disk `.typhon-trace` file, the live TCP Block frame, and the in-memory ring buffer. Producers emit bytes directly; the consumer moves bytes verbatim; exporters compress with LZ4 into blocks. No delta encoding anywhere — LZ4 handles field-level redundancy well enough on its own, and delta-encoding would require kind-aware cursor arithmetic.

<a href="./assets/typhon-profiler-wire-protocol.svg">
  <img src="./assets/typhon-profiler-wire-protocol.svg" width="1200"
       alt="Typhon Profiler — Wire Format (record layout, frame envelope, block layout, v8 manifest entry)">
</a>
<sub>D2 source: <code>assets/src/typhon-profiler-wire-protocol.d2</code></sub>

### 4.1 Record framing

Each record starts with a 12-byte **common header** at an even byte offset inside the ring:

```
offset 0..1    u16   Size            // total record size in bytes, including these 2 bytes
offset 2       u8    Kind            // TraceEventKind discriminant
offset 3       u8    ThreadSlot      // owning slot index 0..255
offset 4..11   i64   StartTimestamp  // Stopwatch.GetTimestamp() at begin (span) or emit (instant)
```

Two reserved `Size` values:

| Value | Meaning |
|---|---|
| `0x0000` | **EmptySlot** — ring slot is reserved but not yet committed; consumer skips over it until published. |
| `0xFFFF` | **WrapSentinel** — remainder of the ring from here to end-of-buffer is padding; consumer resets its read cursor to offset 0 of the slot. |

The "even byte offset" invariant exists so the `Size` field (a `u16`) is always at an even address, which avoids unaligned 2-byte loads on ARM/ARM64 targets. `TraceRecordRing.TryReserve` rounds reservation sizes up to the next even number to maintain it.

### 4.2 Span header extension

All span kinds (discriminant `≥ 10`) include a 25-byte **span header extension** at offset 12, immediately after the common header:

```
offset 12..19  i64  DurationTicks    // end - start, in Stopwatch ticks
offset 20..27  u64  SpanId           // per-slot monotonic, never reset
offset 28..35  u64  ParentSpanId     // enclosing Typhon span's SpanId, or 0 for top-level
offset 36      u8   SpanFlags        // bit 0 = has trace context (next 16 B follow)
```

Span IDs are generated by `SpanIdGenerator.NextId`:

```
SpanId = (slotIdx << 56) | counter
```

The `counter` is a `long` on the owning `ThreadSlot` that gets incremented by the producer only (no `Interlocked`), and is **never reset across slot reclaims**. This means successive owners of the same slot produce monotonically-increasing IDs, and `(slotIdx, counter)` pairs are unique for the process lifetime without any cross-thread synchronization. 56 bits of counter is ~2280 years at 1 M spans/sec/slot — effectively unbounded.

### 4.3 Optional trace context

When `SpanFlags & 0x01` is set, an additional 16 bytes follow at offset 37:

```
offset 37..44  u64  TraceIdHi        // upper 8 bytes of Activity.TraceId
offset 45..52  u64  TraceIdLo        // lower 8 bytes of Activity.TraceId
```

This gives a span record one of two preamble sizes:

| Form | Preamble size |
|---|---|
| Instant record (kind < 10) | 12 bytes (common header only) |
| Span record, no trace context | 37 bytes (common + span extension) |
| Span record, with trace context | 53 bytes (common + span extension + trace context) |

Per-kind typed payload follows the preamble.

### 4.4 Event kinds and payloads

Event kinds are numeric and **wire-stable** — they're part of the file format. Never renumber or reuse a retired ID; only append.

| ID | Kind | Span | Required payload | Optional payload |
|---|---|---|---|---|
| 0 | `TickStart` | instant | — | — |
| 1 | `TickEnd` | instant | `u8 overloadLevel`, `u8 tickMultiplier` | — |
| 2 | `PhaseStart` | instant | `u8 phase` | — |
| 3 | `PhaseEnd` | instant | `u8 phase` | — |
| 4 | `SystemReady` | instant | `u16 systemIdx`, `u16 predecessorCount` | — |
| 5 | `SystemSkipped` | instant | `u16 systemIdx`, `u8 skipReason` | — |
| 6 | `Instant` | instant | `i32 nameId`, `i32 payload` | — |
| 7 | `GcStart` | instant | `u8 generation`, `u8 reason`, `u8 type`, `u32 count` | — |
| 8 | `GcEnd` | instant | `u8 generation`, `u32 count`, `i64 pauseDurationTicks`, `u64 promotedBytes`, `u64 gen0/gen1/gen2/loh/poh sizes`, `u64 totalCommittedBytes` | — |
| 9 | `MemoryAllocEvent` | instant | `u8 direction` (Alloc/Free), `u16 sourceTag`, `u64 sizeBytes`, `u64 totalAfterBytes` | — |
| 10 | `SchedulerChunk` | span | `u16 systemIdx`, `u16 chunkIdx`, `u16 totalChunks`, `i32 entitiesProcessed` | — |
| 20 | `TransactionCommit` | span | `i64 tsn` | `i32 componentCount`, `bool conflictDetected` |
| 21 | `TransactionRollback` | span | `i64 tsn` | `i32 componentCount` |
| 22 | `TransactionCommitComponent` | span | `i64 tsn`, `i32 componentTypeId` | — |
| 23 | `TransactionPersist` | span | `i64 tsn` | `i64 walLsn` |
| 30 | `EcsSpawn` | span | `u16 archetypeId` | `u64 entityId`, `i64 tsn` |
| 31 | `EcsDestroy` | span | `u64 entityId` | `i32 cascadeCount`, `i64 tsn` |
| 32 | `EcsQueryExecute` | span | `u16 archetypeTypeId` | `i32 resultCount`, `u8 scanMode` |
| 33 | `EcsQueryCount` | span | `u16 archetypeTypeId` | `i32 resultCount`, `u8 scanMode` |
| 34 | `EcsQueryAny` | span | `u16 archetypeTypeId` | `bool found`, `u8 scanMode` |
| 35 | `EcsViewRefresh` | span | `u16 archetypeTypeId` | `u8 mode`, `i32 resultCount`, `i32 deltaCount` |
| 40 | `BTreeInsert` | span | — | — |
| 41 | `BTreeDelete` | span | — | — |
| 42 | `BTreeNodeSplit` | span | — | — |
| 43 | `BTreeNodeMerge` | span | — | — |
| 50 | `PageCacheFetch` | span | `i32 filePageIndex` | — |
| 51 | `PageCacheDiskRead` | span | `i32 filePageIndex` | — |
| 52 | `PageCacheDiskWrite` | span | `i32 filePageIndex` | `i32 pageCount` |
| 53 | `PageCacheAllocatePage` | span | `i32 filePageIndex` | — |
| 54 | `PageCacheFlush` | span | `i32 pageCount` *(in FilePageIndex slot)* | — |
| 55 | `PageEvicted` | span | `i32 filePageIndex` *(evicted page)* | — |
| 56 | `PageCacheDiskReadCompleted` | span | `i32 filePageIndex` | — |
| 57 | `PageCacheDiskWriteCompleted` | span | `i32 filePageIndex` | — |
| 58 | `PageCacheFlushCompleted` | span | `i32 pageCount` *(in FilePageIndex slot)* | — |
| 59 | `PageCacheBackpressure` | span | `i32 retryCount`, `i32 dirtyCount`, `i32 epochCount` | — |
| 60 | `ClusterMigration` | span | `u16 archetypeId`, `i32 migrationCount` | — |
| 75 | `GcSuspension` | span | `u8 reason` (`GcSuspendReason`), `u8 optMask` (reserved) | — |
| 76 | `PerTickSnapshot` | instant-like *(see note)* | `u32 tickNumber`, `u16 fieldCount`, `u32 flags`, then `fieldCount × {u16 gaugeId; u8 valueKind; [4 or 8] bytes}` | — |
| 77 | `ThreadInfo` | instant-like *(see note)* | `i32 managedThreadId`, `u16 nameByteCount`, UTF-8 name bytes | — |
| 80 | `WalFlush` | span | `i32 batchByteCount`, `i32 frameCount`, `i64 highLsn` | — |
| 81 | `WalSegmentRotate` | span | `i32 newSegmentIndex` | — |
| 82 | `WalWait` | span | `i64 targetLsn` | — |
| 83 | `CheckpointCycle` | span | `i64 targetLsn`, `u8 reason` | `i32 dirtyPageCount` |
| 84 | `CheckpointCollect` | span | — | — |
| 85 | `CheckpointWrite` | span | — | `i32 writtenCount` |
| 86 | `CheckpointFsync` | span | — | — |
| 87 | `CheckpointTransition` | span | — | `i32 transitionedCount` |
| 88 | `CheckpointRecycle` | span | — | `i32 recycledCount` |
| 89 | `StatisticsRebuild` | span | `i32 entityCount`, `i32 mutationCount`, `i32 samplingInterval` | — |
| 200 | `NamedSpan` | span | `u16 nameByteCount`, UTF-8 bytes | — |

Optional fields are gated by a 1-byte `optMask` in the payload. Each setter on the producer ref struct sets its bit; the encoder writes only the set fields in canonical order. The reader walks the same order guided by the mask. `EcsQueryAny.Found` and `EcsQuery{Execute,Count}.ResultCount` share the same 4-byte payload slot because a given query kind never sets both — the encoder uses the mask bit to distinguish which field is present.

**Async-completion pattern** (`PageCacheDiskReadCompleted`, `PageCacheDiskWriteCompleted`, `PageCacheFlushCompleted`): the kickoff span (e.g., `PageCacheDiskRead`) is closed at the synchronous call's return; a matching `*Completed` span carrying the SAME `SpanId` and `StartTimestamp` is emitted from the thread-pool worker that completes the async I/O, with `durationTicks` = full async tail. This gives the viewer both "how long did the sync API take" and "how long until the OS actually completed" for every async operation. Cache-build-side folding (see §8.2) collapses matched kickoff+completion pairs within a chunk into a single record whose duration is the full async tail.

**Instant-like-but-numbered-≥-10 carve-out**: `PerTickSnapshot` (76) and `ThreadInfo` (77) have wire shape of an instant record (no 25-byte span header extension, no duration, no span IDs) but are placed in the ≥ 10 range to keep category grouping clean (metric/identity records live with the other observability-specific kinds). `TraceEventKindExtensions.IsSpan` explicitly excludes both; any future instant-style kind placed above 9 MUST be added to the exclusion or the consumer will misread the next 25 bytes as span metadata.

**Reserved ranges** for upcoming engine work (per the roadmap in [06-profiler-feature-roadmap.md](../../../claude/design/observability/06-profiler-feature-roadmap.md)):
- **70–74**: Lock-tracking span kinds (`LockAcquire`, `LockRelease`, `LockWaitBegin`, `LockWaitEnd`) — tier 2.3
- **90–99**: Typhon engine arena/pool allocation tracing (`AllocRent`, `AllocReturn`, `AllocPin`) — tier 3.2, beyond today's PinnedMemoryBlock-only coverage at kind 9

**ID encoding on the wire vs JSON**: `SpanId`, `ParentSpanId`, `TraceIdHi/Lo`, `EntityId`, `Tsn`, and LSN values are 64-bit on the wire. When the server emits JSON DTOs, these are encoded as **decimal strings** (not hex) — e.g., `"spanId": "1234567890"`. Decimal averages ~30% shorter than the previous 16-char hex, preserves full 64-bit precision across the JS `Number` ceiling, and zero IDs become the single character `"0"`. The client's tree-walk treats `"0"` as "no parent."

### 4.5 Block framing

The ring-buffer bytes are grouped into LZ4-compressed **blocks** at the exporter layer. Each block has a 12-byte header:

```
offset 0..3   i32   UncompressedBytes
offset 4..7   i32   CompressedBytes
offset 8..11  i32   RecordCount
```

followed by `CompressedBytes` of LZ4-encoded record payload. Max block size is 256 KB (`TraceFileWriter.MaxBlockBytes`), which matches `TraceRecordBatchPool.MaxPayloadBytes` so one batch = one block.

### 4.6 File layout (`.typhon-trace` v3) + sidecar cache (`.typhon-trace-cache`)

**Source file** (immutable after capture, written by `FileExporter`):

```
[TraceFileHeader]              64 B, fixed layout, Magic = 'TYTR', Version = 3
[SystemDefinitionTable]        variable — system DAG
[ArchetypeTable]               variable — archetypeId → name map
[ComponentTypeTable]           variable — componentTypeId → name map
[CompressedBlock]*             repeating — [12 B header][LZ4 payload]
[SpanNameTable]                optional trailing — runtime-interned NamedSpan names
```

**Sidecar cache file** (regenerable, built on first open — see §8.2 for the full layout): `<source>-cache` alongside the source. Magic `'TYTC'`, `ChunkerVersion = 8` (last bumps: `7 → 8` added `PerTickSnapshot` + `ThreadInfo` support + `Padding → Flags` manifest rename + intra-tick splitting). Section-based: CacheHeader → SectionTable → FoldedChunkData → TickIndex → TickSummaries → GlobalMetrics → SystemAggregates → ChunkManifest → SpanNameTable. Embeds a SHA-256 fingerprint over `(source mtime, length, first 4 KB, last 4 KB)`; mismatched fingerprint triggers automatic rebuild. Bump `ChunkerVersion` to invalidate all stale caches forcibly.

**`ChunkManifestEntry.Flags`** (v8+): 32-bit field replacing the former `Padding` word. Bit 0 (`FLAG_IS_CONTINUATION = 0x1`) is set when this chunk is a continuation of a tick already started in an earlier chunk — i.e., the previous chunk's `ToTick` overlaps this chunk's `FromTick` because a single tick exceeded `IntraTickByteCap` (2 × `ByteCap` = 2 MiB) or `IntraTickEventCap` (2 × `EventCap` = 100 000) mid-build and the builder flushed mid-tick. All other bits are reserved. Clients seed the decoder differently for continuation chunks (see §8.6). See §8.2 for the build algorithm.

The header carries `TimestampFrequency`, `BaseTickRate`, `WorkerCount`, `SystemCount`, `ArchetypeCount`, `ComponentTypeCount`, `CreatedUtcTicks`, and `SamplingSessionStartQpc` (the QPC anchor for `.nettrace` flamegraph correlation, 0 when no EventPipe companion is attached). The `Reserved[9]` bytes pad to 52 B total struct size and are available for forward compatibility.

The span-name table is only written when the profiler actually encountered a runtime-interned `NamedSpan` name during the session. `TraceFileReader.ReadNextBlock` peeks the first 4 bytes of each "block header" slot and detects the `SPAN` magic sentinel — if present, it seeks back and reads the span-name table inline, then continues with the next actual block. This lets the table appear at any point in the file, including the very end.

### 4.7 Live TCP protocol

The live stream wraps the same byte content in three frame types, each with a 5-byte envelope `[u8 type][u32 length]`:

| Frame type | Payload | When sent |
|---|---|---|
| `Init = 1` | Byte-identical to the first four sections of a `.typhon-trace` file (header + systems + archetypes + componentTypes) | On client connect, sent in blocking mode |
| `Block = 2` | 12-byte block header + LZ4-compressed record payload | Per consumer drain cycle, sent in non-blocking mode |
| `Shutdown = 3` | Empty | On profiler stop |

Because the Init payload is identical to the file format, the viewer server parses it by wrapping the bytes in a `MemoryStream` and passing it to `TraceFileReader` — one parser serves both file and live paths.

---

## 5. Producer API (`TyphonEvent`)

The producer-side surface lives in `src/Typhon.Engine/Profiler/TyphonEvent.cs`. It's a static class with three groups of methods:

### 5.1 Factory methods (one per typed event)

Each typed event has a `BeginXxx(...)` factory that returns a ref-struct instance with `ThreadSlot`, `StartTimestamp`, `SpanId`, `ParentSpanId`, `PreviousSpanId`, and `TraceIdHi/Lo` pre-populated. The caller sets optional fields on the returned struct and disposes it when the scope ends.

```csharp
public static BTreeInsertEvent        BeginBTreeInsert();
public static BTreeDeleteEvent        BeginBTreeDelete();
public static BTreeNodeSplitEvent     BeginBTreeNodeSplit();
public static BTreeNodeMergeEvent     BeginBTreeNodeMerge();

public static TransactionCommitEvent            BeginTransactionCommit(long tsn);
public static TransactionRollbackEvent           BeginTransactionRollback(long tsn);
public static TransactionCommitComponentEvent    BeginTransactionCommitComponent(long tsn, int componentTypeId);

public static EcsSpawnEvent      BeginEcsSpawn(ushort archetypeId);
public static EcsDestroyEvent    BeginEcsDestroy(ulong entityId);
public static EcsQueryExecuteEvent  BeginEcsQueryExecute(ushort archetypeTypeId);
public static EcsQueryCountEvent    BeginEcsQueryCount(ushort archetypeTypeId);
public static EcsQueryAnyEvent      BeginEcsQueryAny(ushort archetypeTypeId);
public static EcsViewRefreshEvent   BeginEcsViewRefresh(ushort archetypeTypeId);

public static PageCacheFetchEvent         BeginPageCacheFetch(int filePageIndex);
public static PageCacheDiskReadEvent      BeginPageCacheDiskRead(int filePageIndex);
public static PageCacheDiskWriteEvent     BeginPageCacheDiskWrite(int filePageIndex);
public static PageCacheAllocatePageEvent  BeginPageCacheAllocatePage(int filePageIndex);
public static PageCacheFlushEvent         BeginPageCacheFlush(int pageCount);

public static ClusterMigrationEvent BeginClusterMigration(ushort archetypeId, int migrationCount);
```

Every factory early-returns `default(T)` when `TelemetryConfig.ProfilerActive == false`, when the slot registry is full, or when the name is runtime-suppressed. `default(T)` has `SpanId == 0`, and `SpanScope.Dispose` checks that before publishing — so a disposed default instance is a no-op.

### 5.2 Scheduler-internal emit helpers

The DAG scheduler emits instant and scheduler-chunk events without a `using`-scope. These call sites compute both timestamps on the scheduler thread and pass them directly, so there's no ref-struct to Dispose:

```csharp
internal static void EmitTickStart(long timestamp);
internal static void EmitTickEnd(long timestamp, byte overloadLevel, byte tickMultiplier);
internal static void EmitPhaseStart(TickPhase phase, long timestamp);
internal static void EmitPhaseEnd(TickPhase phase, long timestamp);
internal static void EmitSystemReady(ushort systemIdx, ushort predecessorCount, long timestamp);
internal static void EmitSystemSkipped(ushort systemIdx, byte skipReason, long timestamp);
internal static void EmitSchedulerChunk(
    int systemIdx, int chunkIdx, int totalChunks,
    long startTimestamp, long endTimestamp, int entitiesProcessed);
```

### 5.3 Thread-local control

```csharp
public static void SetCurrentTickNumber(int tickNumber);    // DagScheduler calls at tick entry
public static void SuppressActivityContextOnThisThread();    // scheduler workers opt out
public static void RestoreActivityContextOnThisThread();     //   of Activity.Current capture
public static long TotalDroppedEvents { get; }               // diagnostic sum across slots
public static int ActiveSlotCount { get; }
```

The scheduler worker entry path calls `SuppressActivityContextOnThisThread()` so the per-chunk hot path skips the `Activity.Current` read entirely — workers don't carry ambient request context, and the cost savings (~5-9 ns/span) matter at worker frequency.

### 5.4 Ref-struct event pattern

Every typed event is a `ref struct` implementing `ITraceEventEncoder`:

```csharp
public interface ITraceEventEncoder
{
    int ComputeSize();
    void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten);
}
```

The contract:

1. **Fields**: core span fields (`ThreadSlot`, `StartTimestamp`, `SpanId`, `ParentSpanId`, `PreviousSpanId`, `TraceIdHi`, `TraceIdLo`) + required payload fields as public mutable fields + optional payload fields as private backing + public properties whose setters set both the value and an optional-mask bit.
2. **`ComputeSize`**: returns the exact wire size of the record including the span header extension, optional trace context (if `TraceIdHi != 0 || TraceIdLo != 0`), and any optional payload fields whose mask bit is set. Called by `TyphonEvent.PublishEvent<T>` to reserve ring space.
3. **`EncodeTo`**: serializes the record to the destination span at `endTimestamp`. Called once per event.
4. **`Dispose`**: delegates to the shared helper:
   ```csharp
   public void Dispose() => TyphonEvent.PublishEvent(in this, ThreadSlot, PreviousSpanId, SpanId);
   ```

The shared `PublishEvent<T>` uses C# 13's `allows ref struct` constraint so 17 event types reuse the same publish logic without boxing:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal static void PublishEvent<T>(in T evt, byte threadSlot, ulong previousSpanId, ulong spanId)
    where T : struct, ITraceEventEncoder, allows ref struct
{
    if (spanId == 0) return;                              // default instance → no-op
    var endTs = Stopwatch.GetTimestamp();
    var size = evt.ComputeSize();
    var slot = ThreadSlotRegistry.GetSlot(threadSlot);
    var ring = slot.Buffer;
    if (ring != null && ring.TryReserve(size, out var dst))
    {
        evt.EncodeTo(dst, endTs, out _);
        ring.Publish();
    }
    t_currentOpenSpanId = previousSpanId;                 // LIFO unwind of nested Typhon spans
}
```

### 5.5 `using var` caveat

Because `using var` declarations mark the local as effectively read-only (C# spec), the compiler blocks setters on the ref struct with CS1654. Call sites that need to populate optional fields use a plain `var` + `try/finally`:

```csharp
var scope = TyphonEvent.BeginTransactionCommit(tsn);
scope.ComponentCount = _componentInfos.Count;
try
{
    // ... transaction body ...
    scope.ConflictDetected = hasConflict;
    return true;
}
finally
{
    scope.Dispose();
}
```

For call sites with no optional fields to set, `using var` is fine:

```csharp
using var scope = TyphonEvent.BeginBTreeInsert();
```

This is a known ergonomic gotcha. A possible future refactor — make setters `readonly` with a mutable backing struct accessed via a hidden ref — would fix it, but isn't worth the API churn at this scale (9 affected sites).

---

## 6. Consumer Pipeline

The engine-side capture path — from a typed ref-struct event construct-in-callsite, through the gate, the per-thread ring, the dedicated drain thread, and the exporter fan-out — is shown end-to-end below. The file and TCP exporters share the same block encoder; only the output sink differs.

<a href="./assets/typhon-profiler-engine-pipeline.svg">
  <img src="./assets/typhon-profiler-engine-pipeline.svg" width="1200"
       alt="Typhon Profiler — Engine-Side Capture Pipeline (v3 hot path + GC ingestion + gauge emitter)">
</a>
<sub>D2 source: <code>assets/src/typhon-profiler-engine-pipeline.d2</code></sub>

### 6.1 Slot registry (`ThreadSlotRegistry`)

- **256 fixed slots**, `ThreadSlot[] s_slots`, indexed by a `[ThreadStatic] t_slotIndex`.
- Slot claim: `GetOrAssignSlot()` scans `0..HighWaterMark`, reads each slot's `State` via `Volatile.Read`, CAS-claims `Free → Active`, assigns `t_slotIndex`, and installs a `[ThreadStatic] SlotReleaser` finalizer sentinel.
- Slot retire: the sentinel's finalizer runs on thread death and `Volatile.Write`s `State = Retiring`.
- Slot reclaim: the consumer thread drains any `Retiring` slot fully, then CAS-reclaims `Retiring → Free` so the next thread can reuse it. `SpanCounter` is **not** reset — successive owners see monotonically-increasing counter values, which keeps SpanIds unique across the process lifetime.
- High-water mark caching: `s_highWaterMark` tracks the highest slot index ever claimed, so the scan bails out early in the common case (few threads).

### 6.2 Thread ring buffer (`TraceRecordRing`)

- Backing: `byte[]` with power-of-2 capacity, default 128 KB (`ThreadSlotRegistry.DefaultBufferCapacity`).
- Head/tail: `CacheLinePaddedLong` on separate 64-byte cache lines to avoid false sharing.
- `TryReserve(size, out Span<byte> destination)`:
  1. Rounds `size` up to the next even number (even-size invariant for the `Size` field).
  2. Atomically bumps the reservation window.
  3. If the window would cross the end-of-buffer, writes a `WrapSentinel` at the current position and wraps.
  4. Returns a span into the reserved range, or fails with `false` on full (increments `_droppedEvents`).
- `Publish()`: releases the reservation by advancing head with `Volatile.Write`.
- `Drain(Span<byte> destination)`: single-consumer drain; walks records, strips padding, copies to destination, skips wrap sentinels.
- `Reset()`: called when a slot is reclaimed Free → new owner sees an empty ring.

### 6.3 Consumer thread (`ProfilerConsumerThread`)

Derives from `HighResolutionTimerServiceBase` (the engine's metronome timer). Uses the base class's self-calibrating Sleep → Yield → Spin wait to hit its cadence (default 1 ms) on Windows without P/Invoke.

On each cadence tick, `DrainAndFanOut()`:

1. Walks slots `0..HighWaterMark`, reading each slot's state.
2. For `Active` or `Retiring` slots, drains the slot's ring into a thread-local merge scratch buffer (default 512 KB).
3. For `Retiring` slots that were fully drained, CAS-reclaims them to `Free`.
4. Builds an `int[] offsets` index over the merge scratch by walking the `u16 size` fields.
5. Sorts the offsets by the `i64` timestamp at `offset+4` via a cached `TimestampOffsetComparer`. Sorting the index — not the records — avoids moving variable-length bytes.
6. Slices into batches of at most 256 KB payload or 8192 records (whichever hits first), writes into `TraceRecordBatch` instances rented from `TraceRecordBatchPool` with `refcount = exporters.Count`.
7. For each exporter, `TryEnqueue(batch)` onto its `ExporterQueue`. On drop, the queue calls `batch.Release()` to balance the refcount.

### 6.4 Exporter queue (`ExporterQueue`)

Wraps `BlockingCollection<TraceRecordBatch>` with `boundedCapacity = 4`. On `TryEnqueue` failure (queue full), increments `_droppedBatches` and calls `batch.Release()` so the pool refcount stays balanced — without that, dropped batches would leak in the pool. The consumer-side `GetConsumingEnumerable(ct)` drives the per-exporter thread.

### 6.5 Batch pool (`TraceRecordBatchPool`)

- `ConcurrentBag<TraceRecordBatch>` backing store.
- Pre-allocates 8 batches of 256 KB payload + 8192 offsets each on first use.
- `Rent(int exporterCount)` pops from the bag (or allocates if empty), resets count, sets refcount.
- `Return(batch)` is called from `TraceRecordBatch.Release()` when refcount hits 0.

### 6.6 Gauge snapshot emitter (`GaugeSnapshotEmitter`)

Per-tick packed-metric pipeline that keeps fine-grained counter values out of the hot path. Called by `DagScheduler` at end-of-tick when `TelemetryConfig.ProfilerGaugesActive`.

- Reads the full gauge set (unmanaged-memory totals, GC heap sizes, page-cache bucket counts, WAL buffer usage, transaction/UoW counters — see §12 for the registry) from their `Interlocked` source fields in a single pass.
- Packs the set into a single `PerTickSnapshot` record (kind 76) with format `[u32 tickNumber][u16 fieldCount][u32 flags][fieldCount × {u16 gaugeId, u8 valueKind, 4|8 bytes}]`.
- Emits on the scheduler thread's own slot — interleaves with the scheduler's `SchedulerChunk` records. (The SPSC invariant holds because both emissions come from the same thread.) One snapshot per tick; viewer binary-searches snapshot samples for the current viewport range and derives per-tick deltas for cumulative counters.
- Fixed-at-init gauges (`PageCacheTotalPages`, `TransientStoreMaxBytes`, `WalCommitBufferCapacityBytes`, `WalStagingPoolCapacity`) are emitted only in the first snapshot; the viewer caches them separately as capacities.

### 6.7 GC ingestion thread (`GcTracingHost` / `GcEventListener` / `GcIngestionThread`)

Separate in-process capture pipeline for .NET runtime GC events — kept off the producer hot path so instrumenting the scheduler's own emissions never contends with runtime-event delivery. Enabled by `TelemetryConfig.ProfilerGcTracingActive`.

1. **`GcEventListener`** — a `System.Diagnostics.Tracing.EventListener` subscribed to the `Microsoft-Windows-DotNETRuntime` provider at `Informational` level with the `GCKeyword (0x1)` filter. Cross-platform: the provider name is the legacy ETW label but CoreCLR emits the events identically on Linux/macOS through the in-process EventPipe pipeline.
   - Listens for four event IDs: `1 = GCStart_V2`, `2 = GCEnd_V1`, `9 = GCSuspendEEBegin_V1`, `3 = GCRestartEEEnd_V1`. All other runtime events fall through to a no-op.
   - Timestamp source: `Stopwatch.GetTimestamp()` captured in the callback (not `EventWrittenEventArgs.TimeStamp`, which would require a reference-anchor conversion and introduce clock-skew hazards). ≤ 100 ns lag is well within tolerance at GC-event frequencies.
   - Ctor-during-base-ctor footgun: `EventListener`'s base constructor fires `OnEventSourceCreated` for pre-existing sources *before* the derived constructor body completes. The `Microsoft-Windows-DotNETRuntime` source is always pre-existing, so the early callback always fires. The `_ready` gate defers `EnableEvents` until the queue field is confirmed assigned; a post-base-ctor catch-up block in the derived ctor enables the source once the gate is flipped. Removing that catch-up (based on a static-analyzer false positive that can't see through virtual dispatch into the base ctor) silently breaks GC capture — the listener holds the source reference but never subscribes.
   - Enqueues a `GcEventRecord` struct onto a lock-free `GcEventQueue`.
2. **`GcIngestionThread`** — a dedicated OS thread that drains `GcEventQueue` and translates records into wire-format events emitted on its own producer slot. On `GCEnd` it snapshots `System.GC.GetGCMemoryInfo()` for per-generation size-after values + `TotalCommittedBytes`, which ride on the `GcEnd` payload.
3. **Emitted kinds**: `GcStart` (7) + `GcEnd` (8) instants bracket each collection; `GcSuspension` (75) is a span covering the EE-suspend window (`GCSuspendEEBegin` → `GCRestartEEEnd`), with `ParentSpanId = 0` (process-level, no caller attribution, no `Activity.Current` capture).
4. **Viewer integration**: GC events live in a dedicated gauge-region track. Suspension spans are also handed to the viewer eagerly at `/api/trace/open` time (pre-computed by `TraceSessionService.GetOrComputeGcSuspensions`) so the suspension track's `yMax` is stable across LRU chunk churn.

**Unmanaged-memory tracing** (kind 9 `MemoryAllocEvent`) is a parallel-but-separate pipeline: `PinnedMemoryBlock` construct/dispose sites directly emit the instant with `direction`, `sourceTag`, `sizeBytes`, `totalAfterBytes`. Gated on `TelemetryConfig.ProfilerMemoryAllocationsActive`. Because `PinnedMemoryBlock` is the single funnel for NativeMemory allocation, one instrumentation point covers all engine NativeMemory traffic — no ingestion thread needed.

---

## 7. Exporters

All exporters implement `IProfilerExporter`:

```csharp
public interface IProfilerExporter : IDisposable
{
    string Name { get; }
    ExporterQueue Queue { get; }
    void Initialize(ProfilerSessionMetadata metadata);
    void ProcessBatch(TraceRecordBatch batch);
    void Flush();
}
```

`TyphonProfiler.Start(parent, metadata)` spawns one dedicated OS thread per attached exporter running the consume loop:

```csharp
foreach (var batch in exp.Queue.GetConsumingEnumerable(ct))
{
    exp.ProcessBatch(batch);
    batch.Release();
}
```

### 7.1 `FileExporter`

- Writes a v3 `.typhon-trace` file via `TraceFileWriter`.
- `Initialize`: opens the file, writes the header + system/archetype/componentType tables.
- `ProcessBatch`: calls `_writer.WriteRecords(batch.Payload.AsSpan(0, batch.PayloadBytes), batch.Count)` which LZ4-compresses a single block.
- `Flush`: flushes the file stream at shutdown. Does **not** write a span-name table — there's no runtime interning pressure yet (all 20 migrated call sites use compile-time kind enums).
- Parented under `ResourceRegistry.Profiler` so it shows up in diagnostics.

### 7.2 `TcpExporter`

- Listens on a configurable port, single-client model (rejects second concurrent connection).
- `Initialize`: starts the TCP accept loop on a dedicated thread; builds the INIT payload (header + metadata tables) using the same byte layout `TraceFileWriter` produces.
- On client connect: sends `Init` frame in blocking mode, then switches the socket to non-blocking.
- `ProcessBatch`: encodes the batch via `TraceBlockEncoder.EncodeBlock` into an LZ4-compressed `Block` frame and sends it. `WouldBlock` errors count as `DroppedFrames` — the exporter thread never blocks on a slow client.
- `Flush`: sends `Shutdown` frame, closes the socket, stops the accept loop.

### 7.3 Test infrastructure (`InMemoryExporter`)

Not an engine-side exporter — lives in `test/Typhon.Engine.Tests/Profiler/TestInfra/InMemoryExporter.cs`. Copies each received batch's payload bytes + offsets index into a thread-safe list so tests can walk records without needing a file or socket. Used by profiler round-trip / exporter integration tests.

---

## 8. Viewer Pipeline (`Typhon.Profiler.Server`)

The browser viewer is an ASP.NET Core Minimal API + a Preact SPA under `tools/Typhon.Profiler.Server/`. The original v3 design served the entire trace through `/api/trace/events` in one JSON response; the Phase 1–3 rewrite replaced that with a sidecar cache + chunk-based lazy loading + binary LZ4 transport so traces 100× the original size (targeting ~15 GB) render in under a second to first paint.

Three architectural layers:

1. **Immutable source** — `.typhon-trace` file on disk, exactly as written by `FileExporter`. Never modified after capture.
2. **Sidecar cache** — `.typhon-trace-cache` file written alongside the source on first open. Holds per-tick summaries, global metrics, chunk manifest, span-name table, and LZ4-compressed record chunks. Auto-regenerated if the source's fingerprint changes.
3. **Viewport-driven pull client** — the SPA fetches only the chunks overlapping the current viewport; a client-side LRU cache + prefetch ±2 keeps pan/zoom responsive.

<a href="./assets/typhon-profiler-viewer-flow.svg">
  <img src="./assets/typhon-profiler-viewer-flow.svg" width="1200"
       alt="Typhon Profiler — Viewer Client Data Flow (v3)">
</a>
<sub>D2 source: <code>assets/src/typhon-profiler-viewer-flow.d2</code></sub>

### 8.1 REST + SSE endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/health` | Liveness probe |
| `POST /api/trace/upload` | Multipart upload of `trace` + optional `nettrace` companion → returns per-process temp path (`%TEMP%/typhon-profiler/{pid}/{guid}.typhon-trace`) + parsed metadata. Partial uploads are cleaned up on failure via `FileOps.TryDelete`. |
| `GET /api/trace/metadata?path=X` | Reads header + system/archetype/componentType tables from a file (legacy; callers prefer `/open`) |
| `GET /api/trace/open?path=X` | Triggers sidecar build if needed; returns metadata + `tickSummaries` + `globalMetrics` + `chunkManifest` (each entry `{fromTick, toTick, eventCount, isContinuation}`) + `spanNames` + pre-computed `gcSuspensions` in one payload. Enough for the timeline overview to render without any detail-chunk loads. |
| `GET /api/trace/chunk?path=X&chunkIdx=N` | JSON-decoded events for one chunk (identified by its position in the manifest). Legacy path; kept as fallback. |
| `GET /api/trace/chunk-binary?path=X&chunkIdx=N` | Raw LZ4 bytes from the sidecar cache, passed through verbatim. Metadata in response headers: `X-Chunk-From-Tick`, `X-Chunk-To-Tick`, `X-Chunk-Event-Count`, `X-Chunk-Uncompressed-Bytes`, `X-Chunk-Is-Continuation` (`"0"` or `"1"`), `X-Timestamp-Frequency`. All custom headers are centralized in the `ChunkBinaryHeaders` const class and published via `Access-Control-Expose-Headers` so the browser viewer can read them from a CORS response. Default path since Phase 3 — ~3–5× smaller wire + zero server JSON-encode cost + offloaded decompression and decoding to the Web Worker. |
| `GET /api/trace/build-progress?path=X` | SSE stream. If the cache is fresh: fires `event: done` immediately. Otherwise kicks a build on the thread pool and forwards `BuildProgress { bytesRead, totalBytes, tickCount, eventCount }` frames as `event: progress` every ~200 ms, then `event: done` on success or `event: error` on failure. |
| `GET /api/trace/summary?path=X` | Binary `TickSummary[]` via `Results.Stream` — no intermediate `byte[]` copy, pooled 64 KB output buffer. Useful for progressive-overview re-fetch. |
| `GET /api/trace/flamegraph?path=X&fromUs=...&toUs=...` | `.nettrace` CPU-sample flame graph correlated against the trace's QPC anchor (unchanged) |
| `GET /api/live/status` | Current live-session state |
| `GET /api/live/metadata` | Session metadata from the INIT frame |
| `GET /api/live/events` | Live SSE: `metadata` on connect, `tick` per batch, `heartbeat` every 5 s |

**Response compression**: Brotli + Gzip registered at `CompressionLevel.Fastest`, `MimeTypes = ["application/json"]`. SSE (`text/event-stream`) and `application/octet-stream` (chunk-binary) are excluded deliberately — SSE would buffer-then-compress and break streaming; chunk-binary is already LZ4.

**Static-file cache-control**: `.html` marked `no-cache` (so index.html always resolves latest bundle-hash reference); Vite hashed `.js`/`.css` bundles marked `immutable, max-age=31536000`.

**Shutdown cleanup**: `%TEMP%/typhon-profiler/{pid}/` deleted on `IHostApplicationLifetime.ApplicationStopping` — per-process subdir keeps concurrent dev servers from stepping on each other.

Minimal API JSON: `CamelCase`, `WhenWritingNull`. The null-elision still matters for the legacy JSON chunk path (~4× payload reduction), but the binary path renders it moot.

### 8.2 Sidecar cache (`.typhon-trace-cache`)

The sidecar is a compact, seekable binary file written alongside the source. Purpose: turn repeat opens from "re-scan the whole .typhon-trace" (seconds) into "mmap an already-built index" (milliseconds), and serve chunks by direct byte-range read instead of decoding on request.

**Lifecycle** (`TraceSessionService.GetOrBuild`):

1. Compute source fingerprint: SHA-256 over `(mtime, length, first 4 KB, last 4 KB)`.
2. If sidecar exists AND its embedded fingerprint matches: open it, stash the reader on the per-path `SessionSlot`, return.
3. Else: run `TraceFileCacheBuilder.Build` on a thread pool thread (streaming progress to any subscribed SSE), then open the freshly-written sidecar.

**Freshness fast-path**: after the first valid open, the slot caches the source's `(mtime, length)` tuple. Every subsequent `GetOrBuild` call short-circuits via `IsFreshFast` — no SHA-256 unless those values change. Saves ~1 ms per chunk request. The slot also caches the source's `TimestampFrequency`, so `/api/trace/chunk-binary` doesn't reopen the source per request.

**File layout** (section-based, magic `'TYTC'`, `ChunkerVersion = 8`):

```
[CacheHeader 128 B]  magic, version, flags, SourceFingerprint[32], SectionTable offset/count, CreatedUtcTicks
[SectionTable]       one entry per section: id, offset, length
[FoldedChunkData]    N × [LZ4-compressed record bytes] — no per-chunk framing, sizes come from ChunkManifest
[TickIndex]          (optional) reserved for progressive-overview pagination
[TickSummaries]      List<TickSummary> — tickNumber, startUs, durationUs, eventCount, maxSystemDurationUs, activeSystemsBitmask, startUs
[GlobalMetrics]      single struct — globalStartUs, globalEndUs, maxTickDurationUs, maxSystemDurationUs, p95TickDurationUs, totalEvents, totalTicks, systemAggregateCount
[SystemAggregates]   List<SystemAggregateDuration> — invocation count + total duration per system index
[ChunkManifest]      List<ChunkManifestEntry> — fromTick, toTick, cacheByteOffset, cacheByteLength, eventCount, uncompressedBytes, Flags (u32; bit 0 = IsContinuation)
[SpanNameTable]      runtime-interned NamedSpan names
```

**Chunk-build algorithm** (`TraceFileCacheBuilder`):

- Scan the source exactly once via `TraceFileReader.ReadNextBlock` + record walk.
- Accumulate records into a `MemoryStream` chunk buffer. Two sets of size caps drive flushes:
  - **Tick-boundary flush** — when a tick ends, flush if any of: `ticksInChunk >= TickCap (100)`, `chunkBuffer.Length >= ByteCap (1 MiB)`, or `chunkEventCount >= EventCap (50 000)`. Emit manifest entry with `Flags = 0`, open the next chunk at the NEXT tick number.
  - **Intra-tick (mid-tick) flush** — when adding a single record *within a tick* would push `tickBytesInChunk + size > IntraTickByteCap (2 × ByteCap = 2 MiB)` or `tickEventsInChunk >= IntraTickEventCap (2 × EventCap = 100 000)`. Flush what we have as chunk A with `FromTick = currentTick`, `ToTick = currentTick + 1`, `Flags = 0`; open chunk B with same `FromTick = currentTick`, `Flags = FLAG_IS_CONTINUATION`. `openKickoffs` is cleared (completions landing in the continuation chunk fall through to the client's cross-chunk fold). The per-tick byte/event accumulators reset; the tick's cumulative `eventCount` (used by summary) does NOT — it straddles all continuation chunks.
- `FlushChunk(writer, buffer, manifest, fromTick, toTick, eventCount, flags)` — the signature takes a `uint flags` parameter written into the manifest entry verbatim.
- On each tick close: `FinalizeTick` appends a `TickSummary`; global max-tick-duration, max-system-duration, and the system-aggregate dictionary are updated. Validation rejects ticks with `firstTs <= 0` or non-monotonic timelines (prevents corrupt summary entries that would break binary search in the viewer).
- Throttled progress callback at tick boundaries (min interval `ProgressIntervalMs = 200`).
- Async-completion fold: a `openKickoffs: Dictionary<SpanId, chunkOffset>` maps each PageCache kickoff (DiskRead/Write/Flush) to its byte offset in the current chunk buffer. When a matching `*Completed` record arrives in the same chunk, the builder seeks back and rewrites the kickoff's `DurationTicks` field with the completion's full-async duration, then DROPS the completion record (`foldedCount++`). Cleared on chunk flush — cross-chunk pairs fall through to the client's fold logic. Typically drops ~30–40% of PageCache events on I/O-heavy traces.
- Post-scan: sort system aggregates by index, compute p95 from sorted durations, write the remaining sections + finalize the header.

**GC suspension pre-computation** (`TraceSessionService.GetOrComputeGcSuspensions`): on first `/api/trace/open` for a path, the service walks the manifest and decodes just the `GcSuspension` records into an ordered list. The list is cached on the per-path `SessionSlot` and returned in the `/api/trace/open` payload so the viewer has a stable `yMax` for the suspension track without needing every chunk resident — prevents the scale from rescaling on LRU churn.

**Builder progress sink**: `IProgress<BuildProgress>` parameter forwards `{ bytesRead, totalBytes, tickCount, eventCount }` frames to whatever subscribed (the SSE endpoint bridges to a bounded `Channel<T>` for cross-thread delivery).

### 8.3 Binary chunk transport + client-side decoder

Default transport since Phase 3. End-to-end:

**Server** (`/api/trace/chunk-binary`):

1. `TraceSessionService.GetOrBuild` returns a cached reader (already fingerprint-verified).
2. `entry = manifest[chunkIdx]` — the client sends the position directly (v8+ wire change; prior `ChunkIndexByFromTick` lookup retired because `FromTick` is no longer unique when intra-tick splits produce continuation chunks sharing a tick range).
3. `TraceFileCacheReader.ReadChunkRaw(entry, …)` into a pooled `byte[]`.
4. Response headers set (including `X-Chunk-Is-Continuation` derived from `entry.Flags & FLAG_IS_CONTINUATION`); all custom header names come from the single `ChunkBinaryHeaders` const class whose `ExposedHeadersList` is plumbed into `Access-Control-Expose-Headers` — renaming a header updates both call sites atomically.
5. `Results.Stream` pumps the pooled buffer straight to the response body — no intermediate copy into `byte[]`. ArrayPool buffer returned in the stream callback's `finally`.

**Client** (`ClientApp/src/`):

1. `api.ts::fetchChunkBinary(path, chunkIdx, signal)` — `fetch().arrayBuffer()`, parse `X-Chunk-*` headers including `isContinuation` (derived from `X-Chunk-Is-Continuation`).
2. `chunkWorkerClient::processBinaryInWorker(compressed, uncompressedBytes, fromTick, ticksPerUs, systems, isContinuation)` — posts to one of 1–4 Web Workers with the `ArrayBuffer` in the transfer list (zero-copy).
3. `chunkWorker.ts` — inside the Worker:
   a. `lz4Block::decompressLz4Block(compressed, uncompressedBytes)` — pure-TS LZ4 block decoder, ~60 lines. Uses native `Uint8Array.copyWithin` for non-overlapping match copies and a byte loop for RLE overlap.
   b. `chunkDecoder::decodeChunkBinary(raw, firstTick, ticksPerUs, isContinuation)` — walks the record block via a `BinaryReader` `DataView` wrapper, dispatches on kind, produces `TraceEvent[]`. Instant kinds + all span codec families (including `GcStart`/`GcEnd`/`GcSuspension`/`MemoryAllocEvent`/`PerTickSnapshot`/`ThreadInfo`) ported from `RecordDecoder.cs`. IDs emitted as decimal strings via `readU64Decimal` / `readI64Decimal` (match the server's JSON shape). Tick-counter seeding: `isContinuation ? firstTick : firstTick - 1` — continuation chunks skip the `-1` because their first record is not a `TickStart`.
   c. `tickBuilder::buildTickDataFromEvents(events, systems, continuationTickNumber)` — groups events by tick number, runs `processTickEvents(tick, bucket, systems, tick === continuationTickNumber)` on each, sorts. Wipes `rawEvents` on middle ticks (they can never participate in a merge); keeps it on first + last buckets in case their tick is split across chunks.
4. Worker returns `TickData[]`; `chunkCache` caches it under `chunkIdx`.

**Worker pool + failure isolation** (`chunkWorkerClient.ts`):

- Size: `Math.min(4, Math.max(1, navigator.hardwareConcurrency))`. Lazy spawn per slot on first use.
- Dispatch: "least busy" — slot with fewest in-flight requests. Keeps a balanced queue without a shared task queue.
- Failure isolation: per-worker `onerror` handler rejects only requests owned by that slot, nulls the worker so the next request respawns it; other pool members continue serving. A JSON fallback path (inline decode on the main thread) kicks in when no worker is available.

**LRU cache + viewport prefetch** (`chunkCache.ts`):

- Budget: 500 MB resident by default (tuned upward from the original 200 MB as traces grew). Each chunk's bytes estimated as `eventCount × AVG_BYTES_PER_EVENT` (~500 B/event) for LRU accounting.
- `ensureRangeLoaded(cache, path, metadata, manifest, fromTick, toTick, prefetchBefore, prefetchAfter, signal)` — resolves chunks overlapping `[fromTick, toTick)` plus asymmetric prefetch neighbors, keyed on manifest index.
- **Visible vs. prefetch**: only visible-range fetches receive the `AbortSignal` from the viewport effect. Prefetches are un-cancellable so rapid wheel-navigation doesn't kill every in-flight speculative load.
- **Idempotent**: in-flight promises are deduplicated on chunk index. A second viewport change that hits the same chunk returns the existing promise instead of kicking a new fetch.
- **Failure gate**: `failedChunks` map records transient errors with a 30 s retry-after window — prevents hammering a momentarily-flaky server while still recovering automatically.
- **Eviction**: LRU-ordered over chunks NOT in the current (visible + prefetch) pin set. Resident chunks protecting the current viewport are immune regardless of age. When total bytes sit above budget because EVERYTHING is pinned, a latched `overBudgetWarned` flag logs once.
- **Velocity-aware prefetch** (`App.tsx`): tracks `lastViewportShiftRef = (fromTick, toTick, at)`. When the leading edge is moving > 8 ticks/sec in one direction, prefetch biases 5 chunks on the leading edge + 1 on the trailing. Stationary/slow → symmetric default of 2 each side.
- **Range lookup** (`viewRangeToTickRange`): binary search over the summary (sorted by `startUs`). Strict half-open: tick `[tickStart, tickEnd)` overlaps `[fromUs, toUs)` iff `tickEnd > fromUs && tickStart < toUs`.
- **Assembly memoization** (`assembleTickViewAndNumbers`): `cache.entriesVersion` bumps on every insert/delete; the last assembly's output + its captured version are cached. If `version === cache.entriesVersion` on the next call (pure pan/zoom, no chunk churn), return the cached result — cuts per-frame cost in half on intra-tick-split traces where re-running the merge pass is non-trivial.
- **Intra-tick merge at assembly** (`mergeTickData` in `traceModel.ts`): after flattening all chunks, a pass scans for adjacent `TickData` entries sharing a `tickNumber` (produced by a split tick). For each pair, `mergeTickData(a, b)` concatenates `a.rawEvents + b.rawEvents` and re-runs `processTickEvents(tick, combined, systems, isContinuation=false)` — the gold-standard correctness proof: merged output is byte-identical to a single-pass run on the combined events. `rawEvents` is preserved across merges so chain-folds (3+ continuation chunks) work; a `mergedIndices` tracker wipes `rawEvents` on the FINAL merged result after the chain-fold loop completes.
- **DEV assertion**: if a fetched chunk's `X-Chunk-Is-Continuation` header disagrees with the manifest's `isContinuation` flag, `import.meta.env.DEV` mode throws loudly (catches cache-format skew in CI); production mode logs a warning and trusts the manifest so the viewer stays usable.

**OPFS persistent chunk store** (`opfsChunkStore.ts`): optional second-level cache in the browser's Origin Private File System. Keyed by trace fingerprint; per-file layout `[u32 uncompressedBytes | LZ4 bytes]`. Writes are serialized through a promise chain (at most one directory-mutating op in flight). Per-trace quota 1 GB, global 5 GB — oldest-accessed files evict first. Failures are silent (server fetch is authoritative); the cache is pure acceleration. Requires `navigator.storage.persist()` hint (best-effort). Not available on all browsers — code falls back to server fetch transparently.

**GraphArea pending-chunk placeholders**: striped overlay over summary ticks whose chunks aren't resident yet, drawn on top of the tracks but under the tooltip. Keeps rapid panning from showing a blank canvas while chunks are loading.

### 8.4 Progressive cache build UI

`App.tsx` subscribes to `/api/trace/build-progress` BEFORE calling `/api/trace/open` (open blocks until the cache is built). Progress frames feed a `BuildProgressOverlay` component (progress bar, MB read/total, tick/event counts). A client-side `withTimeout` wraps the SSE subscription at 120 s to guarantee `setLoading(true)` can't stick forever if the server hangs or a proxy buffers the stream.

If the cache is already fresh, the SSE fires `done` immediately and the overlay never renders.

### 8.5 Open-from-path flow

`MenuBar.tsx` exposes `File > Open from path...` which prompts for an absolute or server-relative path and posts it directly to `/api/trace/open` — bypassing the upload step. The sidecar cache file lives next to the source, not under `%TEMP%`, so re-opening the same file on subsequent sessions stays instant (no re-upload, no cache duplication).

`localStorage['typhon-profiler.lastOpenPath']` remembers the last path so one-click + Enter reopens the same trace.

### 8.6 Record decoder (`RecordDecoder.cs`) — JSON path + live

Lives in `tools/Typhon.Profiler.Server/RecordDecoder.cs`. Walks raw block bytes and produces `LiveTraceEvent` DTOs. Used by:

- `/api/trace/chunk` (legacy JSON chunk path, kept as fallback)
- `LiveSessionService` (TCP blocks decoded server-side before SSE broadcast)

Dispatch table covers all 37 span + instant kinds via the per-codec `Decode` methods.

**Tick-counter seeding** — two entry points:

- `SetCurrentTick(fromTick - 1)` — **normal chunk**. The first `TickStart` record in the chunk increments the counter to `fromTick`; subsequent records tag correctly.
- `SetCurrentTickForContinuation(fromTick)` — **continuation chunk** (`ChunkManifestEntry.Flags & FLAG_IS_CONTINUATION` set). Seeds the counter directly at `fromTick`, no `-1` — the chunk has no leading `TickStart` because its tick was already opened in a previous chunk. Every record until the next (if any) internal `TickStart` is tagged with `fromTick`.

The client's `chunkDecoder.ts::decodeChunkBinary(bytes, firstTick, ticksPerUs, isContinuation)` mirrors this exactly; tests in `chunkDecoder.test.ts` + `RecordDecoderContinuationTests.cs` pin the seed invariants.

**Malformed-block recovery** (Phase-4 hardening): `DecodeBlock` snapshots `_currentTick` + `output.Count` on entry; if any record's size is implausible it rolls both back. Critical for the live path, where a single bad block used to mis-number every subsequent event in the session.

**Tick number derivation**: records don't carry tick numbers on the wire. The decoder increments `_currentTick` on every `TickStart`. For a trace loaded from tick 0, accurate; for a mid-session reconnect, the first displayed tick is `fromTick` (or 1 in live mode) even if the engine was on tick 12 345.

### 8.7 Live session service (`LiveSessionService`)

BackgroundService that connects as a TCP client to the engine's `TcpExporter` port. Reconnect loop on socket failure.

Per-block payload uses `ArrayPool<byte>.Shared.Rent(length)` to avoid ~1–2 MB/s LOH churn at normal stream rates. `HandleInit` / `HandleBlock` take `(byte[] payload, int length)` pairs since the pooled buffer may be larger than the valid prefix.

**TickEnd timestamp fix** (Phase-4 hardening): `TraceFileCacheBuilder` sets `currentTickLastTs = startTs` unconditionally for `TickEnd` — parallel-worker drain can produce out-of-order timestamps, and the prior `if (startTs > currentTickLastTs)` guard left tick duration wrong under clock skew.

### 8.8 Preact client (`ClientApp/src/`)

Two data sources, one viewer:

- **File mode**: `fetchChunkBinary` → Worker decompress+decode → `TickData[]` → `assembleTickViewAndNumbers` builds the flat view from the LRU cache on each load. Single traversal returns both `tickData` and `tickNumbers` arrays.
- **Live mode**: `connectLive` SSE → `LiveTickSource` buffer → 10 Hz flush via `processTickAndAppend`. `EventSource.onerror` closes the source explicitly (no auto-retry loop).

Component layout:

| Component | Role |
|---|---|
| `App.tsx` | Root state: `LoadedTraceBundle { trace, tracePath, fileName }`, `viewRange`, `selectedChunk/Span`, live-mode refs. Viewport effect fires on `viewRange` change, runs `ensureRangeLoaded` + `assembleTickViewAndNumbers`. `AbortController` cancels in-flight visible fetches on rapid pan. Velocity-aware prefetch bias computed per viewport change. `BuildProgressOverlay` rendered during sidecar builds. |
| `MenuBar.tsx` | File menu (Load / Open from path / Connect Live), nav undo/redo buttons, file-info strip. |
| `TickTimeline.tsx` | Per-tick overview bars. Split useMemo — `summaryRows` depends only on `trace.summary` (stable across chunk loads, avoids 500 K-row remap); `liveRows` depends on `trace.ticks` (live mode only). Binary-search wheel handler for ~500 K tick traces. |
| `GraphArea.tsx` | Canvas Gantt per thread slot, phase lane, chunk lane, span depth rows. Pending-chunk placeholders drawn over non-resident ticks. |
| `DetailPane.tsx` | Per-span field display. |
| `FlameGraph.tsx` | `.nettrace` CPU-sample view. |
| `Workspace.tsx` | Layout shell: GraphArea + DetailPane + FlameGraph panels. |
| `traceModel.ts` | Per-tick projection builder (fused single-pass over `spans[]`). |
| `chunkCache.ts` | LRU + prefetch (see §8.3). |
| `chunkWorker.ts` / `chunkWorkerClient.ts` | Web Worker plumbing for JSON + binary pipelines. |
| `lz4Block.ts` | Pure-TS LZ4 block decoder. |
| `binaryReader.ts` | Little-endian `DataView` wrapper with i64→Number (timestamps) + u64/i64→decimal string (IDs). |
| `chunkDecoder.ts` | Full binary decoder — 7 instant kinds + 24 span codec families, byte-equivalent output to `RecordDecoder.cs`. |
| `liveSource.ts` | `connectLive` EventSource wrapper. Explicit `es.close()` on error. |
| `api.ts` | Fetch wrappers + `subscribeBuildProgress`. |
| `useNavHistory.ts` | Mouse back/forward + undo/redo on viewport changes; debounced push. |

---

## 9. Configuration

`typhon.telemetry.json` controls the profiler gate + opt-in subsystems:

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

The profiler surfaces four independent `static readonly bool` gates, each read once at class load:

| Flag | Purpose | What it enables |
|---|---|---|
| `TelemetryConfig.ProfilerActive` | Master profiler gate | Any event emission from the engine hot path; when `false` every `BeginXxx` factory short-circuits to `default(T)` |
| `TelemetryConfig.ProfilerGcTracingActive` | GC ingestion thread | Subscription to `Microsoft-Windows-DotNETRuntime` + emission of `GcStart`/`GcEnd`/`GcSuspension` |
| `TelemetryConfig.ProfilerMemoryAllocationsActive` | Unmanaged-alloc events | Emission of `MemoryAllocEvent` from `PinnedMemoryBlock` sites |
| `TelemetryConfig.ProfilerGaugesActive` | Per-tick snapshots | `GaugeSnapshotEmitter` runs end-of-tick and emits `PerTickSnapshot` |

Because every flag is `static readonly`, the Tier 1 JIT folds the gate check `if (!…Active) return default;` to a no-op at JIT time — genuinely zero cost when disabled. Adding a new gauge, GC-kind, or alloc-event emission site costs nothing when its subsystem gate is off, which is why the engine can afford to leave these on a per-kind opt-in without taxing production.

Runtime API:

```csharp
TyphonProfiler.IsRunning;                 // bool
TyphonProfiler.Start(parent, metadata);   // idempotent
TyphonProfiler.Stop();                    // idempotent, flushes + joins exporter threads
TyphonProfiler.AttachExporter(exp);       // must be called while Stopped
TyphonProfiler.DetachExporter(exp);       // must be called while Stopped
TyphonProfiler.TotalDroppedEvents;        // long, diagnostic
TyphonProfiler.ActiveSlotCount;           // int
```

Starting is idempotent — double-`Start` is a no-op (not an exception). `AttachExporter`/`DetachExporter` must be called while stopped — mutating the exporter list while the consumer is running would race with fan-out.

---

## 10. Known Gaps and Follow-Ups

The upcoming feature roadmap (analyzer layer, DB-specific views, GC + allocation tracing, keyboard navigation, etc.) is maintained in a separate doc: see [06-profiler-feature-roadmap.md](../../../claude/design/observability/06-profiler-feature-roadmap.md).

Technical follow-ups not yet scheduled:

1. **OTLP exporter** — not implemented. The architecture supports it trivially (`IProfilerExporter` + a dedicated thread), but emitting gRPC OTLP frames from a Typhon batch is a separate design.
2. **Tick-number wire field** — currently derived on the decoder side by counting `TickStart` records. A 4-byte `tickNumber` field in the `TickStart` payload would eliminate the mid-session "tick 1" offset for reconnect scenarios. Trivial to add via a new optional-mask bit.
3. **EcsQuery archetype ID** — queries pass `0` at three call sites because ECS queries are mask-based and match multiple archetypes. A future refactor could emit the mask as a side-band `ulong` pair in a new optional field.
4. **Layering: wire types live in `Typhon.Engine`** — `TraceEventKind`, `TraceRecordHeader`, and per-kind codecs live under `src/Typhon.Engine/Profiler/Events/`; external consumers (including `Typhon.Profiler.Server`) therefore link against the whole engine to decode a trace. Cleaner would be to move wire-format types into `Typhon.Profiler`. Mechanical refactor.
5. **`using var` CS1654 gotcha** — call sites that populate optional fields use the verbose `var + try/finally` pattern. A future API refactor (mutable backing struct behind a readonly wrapper) could fix it.
6. **Span-name table writer never called** — `TraceFileWriter.WriteSpanNames(...)` exists but `FileExporter` never calls it. Since all migrated call sites use compile-time kind enums, there's nothing to intern. If `NamedSpan` starts getting used for dynamic strings, `FileExporter.Flush()` should flush the intern table. `TraceFileReader.ReadSpanNames` already tolerates the absence.
7. **Progressive-build broadcaster** — the SSE build-progress endpoint is single-subscriber today. If two browser tabs open the same uncached trace simultaneously, the second gets no progress stream (build is already running; its progress sink belongs to the first subscriber). Low-priority edge case for a single-dev tool. Would require a progress-broadcaster pattern on `TraceSessionService`.
8. **Tier 3.2 arena/pool allocation tracing** — today's memory tracing covers only `PinnedMemoryBlock` (NativeMemory). Typhon's internal arenas / `ArrayPool` / `stackalloc` sites are not instrumented. Event-kind range 90–99 is reserved for this.
9. **Tier 2.3 lock-contention view** — reserved range 70–74 for `LockAcquire`/`LockRelease`/`LockWaitBegin`/`LockWaitEnd` span kinds; no engine-side emission yet.

---

## 11. Key Invariants

These are the correctness contracts worth remembering when modifying any of the code:

1. **`TraceRecordRing.TryReserve` rounds sizes up to the next even number** so the `Size` `u16` field is always at an even byte offset. Removing this breaks unaligned 2-byte loads on ARM.
2. **`SpanCounter` is producer-only-written and never reset.** Adding `Interlocked` to the increment would regress the hot path by ~5 ns/span; resetting on slot reclaim would produce SpanId collisions across successive owners of the same slot.
3. **`t_currentOpenSpanId` is a `[ThreadStatic]` LIFO stack managed by `SpanScope`-equivalent structs** via `PreviousSpanId` capture on construct and restore on Dispose. Breaking the LIFO contract (e.g., Dispose in a different order than begin) corrupts the nesting chain.
4. **`ExporterQueue.TryEnqueue` must call `batch.Release()` on drop** — without it, dropped batches leak in the pool and the pool drains over time.
5. **Exporter list mutation (`AttachExporter`/`DetachExporter`) is only safe while stopped.** The consumer thread iterates `s_exporters` during fan-out without locking; mutating it concurrently races.
6. **The INIT frame payload is byte-identical to the first four sections of a `.typhon-trace` file.** Both `FileExporter` and `TcpExporter.BuildInitPayload` must produce exactly the same bytes for a given metadata, and both must be kept in sync with `TraceFileWriter.WriteHeader` + table writers. The consumer side reuses `TraceFileReader` on the INIT bytes wrapped in a `MemoryStream` — any divergence breaks live viewer sessions.
7. **`TelemetryConfig.ProfilerActive` must be `static readonly`**, not a plain `static bool`. JIT elimination of the producer gate depends on the field being `readonly` — the JIT only folds branches on values it can prove won't change after class load. Demoting it to mutable breaks the zero-cost-when-disabled guarantee.

---

## 12. Gauge Registry (PerTickSnapshot payload)

`PerTickSnapshot` (kind 76) carries a packed `{gaugeId, valueKind, value}` array. Gauge IDs are 16-bit unsigned and partitioned into category ranges; each category leaves headroom for appending. **Never renumber an existing entry — only append.** The full registry lives in `src/Typhon.Engine/Profiler/Events/GaugeId.cs`.

### 12.1 Category ranges

| Range | Category | Notes |
|---|---|---|
| `0x0100–0x010F` | Unmanaged memory (`PinnedMemoryBlock` via NativeMemory) | `MemoryUnmanagedTotalBytes`, `MemoryUnmanagedPeakBytes`, `MemoryUnmanagedLiveBlocks` |
| `0x0110–0x011F` | GC heap (sampled from `GC.GetGCMemoryInfo`) | `GcHeapGen0Bytes` … `GcHeapPohBytes`, `GcHeapCommittedBytes` |
| `0x0200–0x020F` | PersistentStore / page cache | Total/Free/CleanUsed/DirtyUsed/Exclusive/EpochProtected, `PendingIoReads` |
| `0x0210–0x021F` | TransientStore | `BytesUsed`, `MaxBytes` (fixed-at-init) |
| `0x0300–0x030F` | WAL | Commit buffer used/capacity, inflight frames, staging pool rented/peak/capacity, cumulative total-rents |
| `0x0400–0x040F` | Transactions + UoW live counts | `TxChainActiveCount`, `TxChainPoolSize`, `UowRegistryActiveCount`, `UowRegistryVoidCount` |
| `0x0410–0x041F` | Cumulative throughput counters (U64) | `TxChainCommitTotal`, `TxChainRollbackTotal`, `UowRegistryCreatedTotal`, `UowRegistryCommittedTotal`, `TxChainCreatedTotal` — monotonic; viewer derives per-tick throughput by subtracting consecutive snapshots |

**Fixed-at-init gauges** (emitted only in the first snapshot, cached on the client as capacities): `PageCacheTotalPages`, `TransientStoreMaxBytes`, `WalCommitBufferCapacityBytes`, `WalStagingPoolCapacity`.

### 12.2 `GaugeValueKind` — wire representation selector

Each entry declares the bytes after `valueKind`:

| Kind | Size | Meaning |
|---|---|---|
| `U32Count = 0` | 4 B | Unsigned 32-bit count |
| `U64Bytes = 1` | 8 B | Unsigned 64-bit value (typically bytes, but also cumulative-counter totals that need the u64 range) |
| `I64Signed = 2` | 8 B | Signed 64-bit for signed deltas |
| `U32PercentHundredths = 3` | 4 B | Percentage as hundredths (e.g., 5025 = 50.25%) — reserved for future |

### 12.3 How the viewer consumes the registry

- `gaugeRegion.ts` lays out gauge tracks in the top half of the viewport based on which `GaugeId` categories appeared in the trace's snapshots.
- `gaugeGroupRenderers.ts` groups IDs into render-logic "tracks":
  - **Memory** — two sub-rows: GC heap (stacked-area Gen0/1/2/LOH/POH + committed-bytes line) and Unmanaged (total-bytes line + peak-dashed reference).
  - **Page Cache** — bucket-count stacked area (Free/CleanUsed/DirtyUsed/Exclusive/EpochProtected) above, pending-IO-reads line below. Capacity reference drawn only when within axis.
  - **WAL** — two sub-rows: commit-buffer usage vs. capacity (scale-to-data when usage << capacity), staging pool rented/peak/capacity.
  - **Transactions + UoW** — live counts (`TxChainActiveCount` etc.) + per-tick throughput derived from cumulative counters.
  - **GC** — `GcSuspension` spans as red bars + `GcStart`/`GcEnd` markers as triangles.
- Tooltips dispatched per sub-row via weight-split hit-testing — hovering the "GC heap" band gives Gen0/1/2 breakdown; hovering "Unmanaged" gives total + peak.

### 12.4 Adding a new gauge

1. Append an entry in `GaugeId.cs` — reuse an existing category range if thematic; otherwise open a new range (keep headroom).
2. Document the expected `GaugeValueKind`.
3. Wire a source field (typically `Interlocked`-updated from the owning subsystem) into `GaugeSnapshotEmitter.EmitSnapshot`.
4. If it belongs on an existing render track, add it to the track's renderer; otherwise introduce a new track via `gaugeRegion.ts`.
5. Update the tooltip builder in `gaugeGroupRenderers.ts` so the new value is explained to the user.
6. Add a round-trip test in `TraceEventRoundTripTests.cs` covering the new `{gaugeId, valueKind}` pair.

---

## 13. References

- **GitHub Issue:** [#243 Tracy-Style Profiler](https://github.com/nockawa/Typhon/issues/243)
- **User manual:** [readme.md](./readme.md) — how to enable the profiler, run the viewer, and read the UI
- **Feature roadmap:** [06-profiler-feature-roadmap.md](../../../claude/design/observability/06-profiler-feature-roadmap.md) — upcoming work (analyzer layer, DB-specific views, arena allocation tracing, lock contention, keyboard navigation)
- **Existing reference doc for the old profiler:** `claude/reference/observability/runtime-profiler.md` — describes the pre-#243 32-byte fixed-event profiler. **Stale** — this document is the current reference.
- **Engine source:** `src/Typhon.Engine/Profiler/`
- **Wire library source:** `src/Typhon.Profiler/` (includes sidecar reader/writer)
- **Viewer source:** `tools/Typhon.Profiler.Server/` (ASP.NET Core + Preact + Web Worker)
- **Smoke-test runner:** `test/AntHill/profiling/Program.cs`
- **Configuration:** `typhon.telemetry.json` in each consuming project's output directory

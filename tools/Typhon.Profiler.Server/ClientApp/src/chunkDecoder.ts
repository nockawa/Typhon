/**
 * Client-side binary decoder for a Typhon trace chunk — ports <c>Typhon.Profiler.Server/RecordDecoder.cs</c> to TypeScript.
 *
 * **Why.** The legacy /api/trace/chunk endpoint decompresses LZ4 chunks on the server, walks the records with RecordDecoder, and emits JSON
 * — round-tripping every record through two string encodings plus the server CPU cost of JSON encode. The new /api/trace/chunk-binary
 * endpoint ships the raw LZ4 bytes straight from the sidecar cache. This module decompresses + decodes them entirely in-browser (inside the
 * Web Worker), so the wire carries compact binary and server CPU stays near zero for chunk serving.
 *
 * **Coverage.** All 31 TraceEventKind values the server supports: 7 instant kinds + 24 span kinds across 10 codec families (SchedulerChunk,
 * 4× BTree, 3× Transaction + Persist, 2× EcsLifecycle + 3× Query + ViewRefresh, 6× PageCache + Backpressure + 3× PageCache async completions,
 * ClusterMigration, 3× WAL, 6× Checkpoint, Statistics, NamedSpan). Unknown kinds fall through to a generic span decoder that surfaces the
 * header-level fields only.
 *
 * **Fidelity target.** Output records are byte-equivalent to the server's JSON path for the same chunk — same fields, same units (µs), same
 * ID formatting (decimal strings for 64-bit IDs, matching <c>RecordDecoder.Id</c> / <c>SignedId</c>). Any divergence would cause different
 * viewer behavior depending on transport — a bug-hunting nightmare we explicitly want to avoid.
 */

import { BinaryReader } from './binaryReader';
import { TraceEventKind, type TraceEvent } from './types';

// ═══════════════════════════════════════════════════════════════════════
// Layout constants — mirror TraceRecordHeader.cs exactly
// ═══════════════════════════════════════════════════════════════════════

/** 12-byte common header shared by every record. Layout: u16 size, u8 kind, u8 threadSlot, i64 startTs. */
const COMMON_HEADER_SIZE = 12;
/** 25-byte span header extension: i64 durationTicks, u64 spanId, u64 parentSpanId, u8 spanFlags. */
const SPAN_HEADER_EXT_SIZE = 25;
/** Optional 16-byte trace-context extension: u64 traceIdHi, u64 traceIdLo. Present iff spanFlags bit 0 set. */
const TRACE_CONTEXT_SIZE = 16;
/** bit 0 of spanFlags: set when the span record carries an OpenTelemetry-style trace context. */
const SPAN_FLAGS_HAS_TRACE_CONTEXT = 0x01;

/**
 * Entry point. Decode an LZ4-decompressed record block. <paramref name="firstTick"/> primes the tick counter so the first TickStart in the
 * block lands on that tick number (matching <c>RecordDecoder.SetCurrentTick(fromTick - 1)</c>). <paramref name="ticksPerUs"/> is
 * <c>timestampFrequency / 1_000_000</c> — arrives via the X-Timestamp-Frequency response header on the binary chunk endpoint.
 *
 * Malformed records (size less than header, size overruns slice, or size exceeds ushort range) stop the walk early — partial results remain
 * returned. Mirrors the server's behavior and keeps the viewer useful on truncated traces.
 */
export function decodeChunkBinary(bytes: Uint8Array, firstTick: number, ticksPerUs: number): TraceEvent[] {
  const reader = new BinaryReader(bytes);
  const events: TraceEvent[] = [];
  // Seed at (fromTick - 1) so the first TickStart increment produces fromTick. Matches RecordDecoder.SetCurrentTick(fromTick - 1).
  let currentTick = firstTick - 1;
  let pos = 0;

  while (pos + COMMON_HEADER_SIZE <= reader.length) {
    const size = reader.readU16(pos);
    if (size < COMMON_HEADER_SIZE || pos + size > reader.length) {
      break;
    }

    const kindByte = reader.readU8(pos + 2);
    const threadSlot = reader.readU8(pos + 3);
    const startTs = reader.readI64AsNumber(pos + 4);
    const timestampUs = startTs / ticksPerUs;

    if (kindByte === TraceEventKind.TickStart) {
      currentTick++;
    }

    const kind = kindByte as TraceEventKind;
    let evt: TraceEvent | null;

    // PerTickSnapshot (kind 76) and ThreadInfo (kind 77) are numerically in the span range but have INSTANT wire shape — no span header
    // extension after the common header. Route them to the instant branch explicitly; otherwise readSpanHeader would read 25 bytes of
    // payload as span metadata and mis-align every subsequent record in the chunk. Mirrors the server's IsSpan() carve-out — any future
    // instant-style kind with numeric value ≥ 10 must be added to this list AND to TraceEventKindExtensions.IsSpan on the C# side.
    if (kindByte < 10 || kind === TraceEventKind.PerTickSnapshot || kind === TraceEventKind.ThreadInfo) {
      evt = decodeInstant(reader, pos, kind, threadSlot, currentTick, timestampUs, ticksPerUs);
    } else {
      evt = decodeSpan(reader, pos, kind, threadSlot, currentTick, timestampUs, ticksPerUs);
    }

    if (evt !== null) {
      events.push(evt);
    }

    pos += size;
  }

  return events;
}

// ═══════════════════════════════════════════════════════════════════════
// Instant decoders — mirror InstantEventCodec.Decode + RecordDecoder.DecodeInstant
// ═══════════════════════════════════════════════════════════════════════

function decodeInstant(
  reader: BinaryReader,
  pos: number,
  kind: TraceEventKind,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
  ticksPerUs: number,
): TraceEvent | null {
  const payloadOffset = pos + COMMON_HEADER_SIZE;

  switch (kind) {
    case TraceEventKind.TickStart:
      return { kind, threadSlot, tickNumber, timestampUs };

    case TraceEventKind.TickEnd:
      return {
        kind, threadSlot, tickNumber, timestampUs,
        overloadLevel: reader.readU8(payloadOffset),
        tickMultiplier: reader.readU8(payloadOffset + 1),
      };

    case TraceEventKind.PhaseStart:
    case TraceEventKind.PhaseEnd:
      return {
        kind, threadSlot, tickNumber, timestampUs,
        phase: reader.readU8(payloadOffset),
      };

    case TraceEventKind.SystemReady:
      // predecessorCount (offset+2, u16) is decoded by server-side InstantEventCodec but intentionally dropped from the JSON DTO — mirror that.
      return {
        kind, threadSlot, tickNumber, timestampUs,
        systemIndex: reader.readU16(payloadOffset),
      };

    case TraceEventKind.SystemSkipped:
      return {
        kind, threadSlot, tickNumber, timestampUs,
        systemIndex: reader.readU16(payloadOffset),
        skipReason: reader.readU8(payloadOffset + 2),
      };

    case TraceEventKind.Instant:
      // Generic instant — i32 nameId + i32 payload in wire. Not surfaced on the server's JSON DTO; mirror that by returning header only.
      return { kind, threadSlot, tickNumber, timestampUs };

    case TraceEventKind.MemoryAllocEvent:
      return decodeMemoryAllocEvent(reader, pos, threadSlot, tickNumber, timestampUs);

    case TraceEventKind.PerTickSnapshot:
      return decodePerTickSnapshot(reader, pos, threadSlot, timestampUs);

    case TraceEventKind.GcStart:
      return decodeGcStart(reader, pos, threadSlot, tickNumber, timestampUs);

    case TraceEventKind.GcEnd:
      return decodeGcEnd(reader, pos, threadSlot, tickNumber, timestampUs, ticksPerUs);

    case TraceEventKind.ThreadInfo:
      return decodeThreadInfo(reader, pos, threadSlot, tickNumber, timestampUs);

    default:
      return null;
  }
}

/**
 * Shared TextDecoder for UTF-8 name payloads (ThreadInfo, and any future variable-length-string kinds). Reusing the instance avoids
 * per-record allocation; it's safe for arbitrary byte counts.
 */
const utf8Decoder = new TextDecoder('utf-8');

/**
 * Decode a ThreadInfo (kind 77) record. Wire: <c>i32 managedThreadId, u16 nameByteCount, byte[nameByteCount] nameUtf8</c> after the
 * common header.
 */
function decodeThreadInfo(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
): TraceEvent {
  const o = pos + COMMON_HEADER_SIZE;
  const managedThreadId = reader.readI32(o);
  const nameByteCount = reader.readU16(o + 4);
  const threadName = nameByteCount > 0 ? reader.readUtf8(o + 6, nameByteCount, utf8Decoder) : undefined;
  return {
    kind: TraceEventKind.ThreadInfo,
    threadSlot,
    tickNumber,
    timestampUs,
    managedThreadId,
    threadName,
  };
}

/**
 * Decode a GcStart (kind 7) record. Wire: <c>u8 generation, u8 reason, u8 type, u32 count</c> after the common header.
 */
function decodeGcStart(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
): TraceEvent {
  const o = pos + COMMON_HEADER_SIZE;
  return {
    kind: TraceEventKind.GcStart,
    threadSlot,
    tickNumber,
    timestampUs,
    generation: reader.readU8(o),
    gcReason: reader.readU8(o + 1),
    gcType: reader.readU8(o + 2),
    gcCount: reader.readU32(o + 3),
  };
}

/**
 * Decode a GcEnd (kind 8) record. Wire: <c>u8 generation, u32 count, i64 pauseDurationTicks, u64 promotedBytes</c>, then five u64
 * per-gen sizes + u64 committed — those last six are already materialised via the per-tick gauge snapshot so we don't re-emit them
 * here. Only the pause duration and promoted bytes are unique to the GcEnd record.
 */
function decodeGcEnd(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
  ticksPerUs: number,
): TraceEvent {
  const o = pos + COMMON_HEADER_SIZE;
  return {
    kind: TraceEventKind.GcEnd,
    threadSlot,
    tickNumber,
    timestampUs,
    generation: reader.readU8(o),
    gcCount: reader.readU32(o + 1),
    gcPauseDurationUs: reader.readI64AsNumber(o + 5) / ticksPerUs,
    gcPromotedBytes: reader.readI64AsNumber(o + 13),
  };
}

/**
 * Decode a <c>MemoryAllocEvent</c> record (kind 9). Wire layout after the 12-byte common header:
 * <c>u8 direction, u16 sourceTag, u64 sizeBytes, u64 totalAfterBytes</c>. Mirrors the server's <c>DecodeMemoryAllocEvent</c>.
 */
function decodeMemoryAllocEvent(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
): TraceEvent {
  const payloadOffset = pos + COMMON_HEADER_SIZE;
  return {
    kind: TraceEventKind.MemoryAllocEvent,
    threadSlot,
    tickNumber,
    timestampUs,
    direction: reader.readU8(payloadOffset),
    sourceTag: reader.readU16(payloadOffset + 1),
    // sizeBytes / totalAfterBytes are u64 on the wire. Using readI64AsNumber is safe here because realistic allocation sizes and running
    // totals live well below 2^53 (we'd need a petabyte of RAM to overflow). If that assumption ever breaks, switch to a u64-as-double
    // helper that masks the sign bit.
    sizeBytes: reader.readI64AsNumber(payloadOffset + 3),
    totalAfterBytes: reader.readI64AsNumber(payloadOffset + 11),
  };
}

/**
 * Decode a <c>PerTickSnapshot</c> record (kind 76). Wire layout after the 12-byte common header:
 * <c>u32 tickNumber, u16 fieldCount, u32 flags, then repeated {u16 id, u8 valueKind, [4|8] bytes value}</c>. The record's embedded
 * tickNumber is authoritative (from the scheduler's CurrentTickNumber at emit) — the caller's <c>currentTick</c> counter may lag or
 * lead by a tick depending on where the snapshot landed relative to the TickStart/TickEnd markers, so we use the payload's value.
 */
function decodePerTickSnapshot(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  timestampUs: number,
): TraceEvent {
  const prefixOffset = pos + COMMON_HEADER_SIZE;
  const tickNumber = reader.readU32(prefixOffset);
  const fieldCount = reader.readU16(prefixOffset + 4);
  const flags = reader.readU32(prefixOffset + 6);

  const gauges: Record<number, number> = {};
  let offset = prefixOffset + 10;
  for (let i = 0; i < fieldCount; i++) {
    const id = reader.readU16(offset);
    const valueKind = reader.readU8(offset + 2);
    offset += 3;

    // valueKind dispatch — matches GaugeValueKind on the server. Sizes must stay in sync with the codec.
    let value: number;
    switch (valueKind) {
      case 0: // U32Count
      case 3: // U32PercentHundredths
        value = reader.readU32(offset);
        offset += 4;
        break;
      case 1: // U64Bytes — safe as number up to 2^53 (petabyte scale)
        value = reader.readI64AsNumber(offset);
        offset += 8;
        break;
      case 2: // I64Signed — read as signed; sign is preserved by readI64AsNumber
        value = reader.readI64AsNumber(offset);
        offset += 8;
        break;
      default:
        // Unknown value kind — stop walking this snapshot to avoid reading past the record. The partial gauges we've already collected stay in the DTO.
        return { kind: TraceEventKind.PerTickSnapshot, threadSlot, tickNumber, timestampUs, flags, gauges };
    }

    gauges[id] = value;
  }

  return {
    kind: TraceEventKind.PerTickSnapshot,
    threadSlot,
    tickNumber,
    timestampUs,
    flags,
    gauges,
  };
}

// ═══════════════════════════════════════════════════════════════════════
// Span header helper — reads durationTicks + spanId + parentSpanId + optional trace context
// ═══════════════════════════════════════════════════════════════════════

interface SpanHeader {
  durationUs: number;
  spanId: string;
  parentSpanId: string;
  traceIdHi: string | null;
  traceIdLo: string | null;
  payloadOffset: number;
}

/**
 * Reads the 25-byte span header extension + optional 16-byte trace-context extension, returns the shared span fields plus the offset at
 * which the kind-specific payload begins. All 24 span-kind decoders call this first.
 */
function readSpanHeader(reader: BinaryReader, recordPos: number, ticksPerUs: number): SpanHeader {
  const extStart = recordPos + COMMON_HEADER_SIZE;
  const durationTicks = reader.readI64AsNumber(extStart);
  const spanId = reader.readU64Decimal(extStart + 8);
  const parentSpanId = reader.readU64Decimal(extStart + 16);
  const spanFlags = reader.readU8(extStart + 24);
  const hasTraceContext = (spanFlags & SPAN_FLAGS_HAS_TRACE_CONTEXT) !== 0;

  let traceIdHi: string | null = null;
  let traceIdLo: string | null = null;
  let payloadOffset = extStart + SPAN_HEADER_EXT_SIZE;

  if (hasTraceContext) {
    traceIdHi = reader.readU64Decimal(payloadOffset);
    traceIdLo = reader.readU64Decimal(payloadOffset + 8);
    payloadOffset += TRACE_CONTEXT_SIZE;
  }

  return {
    durationUs: durationTicks / ticksPerUs,
    spanId,
    parentSpanId,
    traceIdHi,
    traceIdLo,
    payloadOffset,
  };
}

/** Build the base TraceEvent from header fields — all span decoders spread this then add kind-specific fields. */
function baseSpanEvent(
  kind: TraceEventKind, threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const evt: TraceEvent = {
    kind, threadSlot, tickNumber, timestampUs,
    durationUs: header.durationUs,
    spanId: header.spanId,
    parentSpanId: header.parentSpanId,
  };
  if (header.traceIdHi !== null) {
    evt.traceIdHi = header.traceIdHi;
    evt.traceIdLo = header.traceIdLo ?? undefined;
  }
  return evt;
}

// ═══════════════════════════════════════════════════════════════════════
// Span decoder dispatch — mirrors RecordDecoder.DecodeRecord switch
// ═══════════════════════════════════════════════════════════════════════

function decodeSpan(
  reader: BinaryReader, pos: number, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, ticksPerUs: number,
): TraceEvent | null {
  const header = readSpanHeader(reader, pos, ticksPerUs);

  switch (kind) {
    case TraceEventKind.SchedulerChunk:
      return decodeSchedulerChunk(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.BTreeInsert:
    case TraceEventKind.BTreeDelete:
    case TraceEventKind.BTreeNodeSplit:
    case TraceEventKind.BTreeNodeMerge:
      // No typed payload — header fields are the complete record.
      return baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.TransactionCommit:
    case TraceEventKind.TransactionRollback:
    case TraceEventKind.TransactionCommitComponent:
      return decodeTransaction(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.TransactionPersist:
      return decodeTransactionPersist(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsSpawn:
      return decodeEcsSpawn(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsDestroy:
      return decodeEcsDestroy(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsQueryExecute:
    case TraceEventKind.EcsQueryCount:
    case TraceEventKind.EcsQueryAny:
      return decodeEcsQuery(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsViewRefresh:
      return decodeEcsViewRefresh(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.PageCacheFetch:
    case TraceEventKind.PageCacheDiskRead:
    case TraceEventKind.PageCacheDiskWrite:
    case TraceEventKind.PageCacheAllocatePage:
    case TraceEventKind.PageCacheFlush:
    case TraceEventKind.PageEvicted:
    case TraceEventKind.PageCacheDiskReadCompleted:
    case TraceEventKind.PageCacheDiskWriteCompleted:
    case TraceEventKind.PageCacheFlushCompleted:
      return decodePageCache(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.PageCacheBackpressure:
      return decodePageCacheBackpressure(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.ClusterMigration:
      return decodeClusterMigration(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.WalFlush:
    case TraceEventKind.WalSegmentRotate:
    case TraceEventKind.WalWait:
      return decodeWal(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.CheckpointCycle:
    case TraceEventKind.CheckpointCollect:
    case TraceEventKind.CheckpointWrite:
    case TraceEventKind.CheckpointFsync:
    case TraceEventKind.CheckpointTransition:
    case TraceEventKind.CheckpointRecycle:
      return decodeCheckpoint(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.StatisticsRebuild:
      return decodeStatisticsRebuild(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.NamedSpan:
      // Fall back to generic span — the name payload isn't surfaced on the server's DTO either.
      return baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);

    default:
      // Unknown kind ≥ 10 — emit header-only event so the viewer can still render timing. Matches RecordDecoder.DecodeGenericSpan.
      return baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  }
}

// ═══════════════════════════════════════════════════════════════════════
// Per-kind span decoders — one per codec family, mirror the server's DecodeXxx methods
// ═══════════════════════════════════════════════════════════════════════

function decodeSchedulerChunk(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Payload: u16 systemIdx, u16 chunkIdx, u16 totalChunks, i32 entitiesProcessed.
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    systemIndex: reader.readU16(o),
    chunkIndex: reader.readU16(o + 2),
    totalChunks: reader.readU16(o + 4),
    entitiesProcessed: reader.readI32(o + 6),
  };
}

// Optional-field mask constants matching the C# codecs. Keep these in step with the corresponding [Opt*] constants in Events/*.cs.
const OPT_TX_COMPONENT_COUNT = 0x01;
const OPT_TX_CONFLICT_DETECTED = 0x02;
const OPT_TX_WAL_LSN = 0x01;

function decodeTransaction(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i64 tsn] [i32 componentTypeId?] (CommitComponent only) [u8 optMask] [i32 componentCount?] [u8 conflictDetected?]
  let cursor = header.payloadOffset;
  const tsn = reader.readI64Decimal(cursor);
  cursor += 8;

  let componentTypeId: number | undefined;
  if (kind === TraceEventKind.TransactionCommitComponent) {
    componentTypeId = reader.readI32(cursor);
    cursor += 4;
  }

  const optMask = reader.readU8(cursor);
  cursor += 1;

  let componentCount: number | undefined;
  let conflictDetected: boolean | undefined;
  if ((optMask & OPT_TX_COMPONENT_COUNT) !== 0) {
    componentCount = reader.readI32(cursor);
    cursor += 4;
  }
  if ((optMask & OPT_TX_CONFLICT_DETECTED) !== 0) {
    conflictDetected = reader.readU8(cursor) !== 0;
    cursor += 1;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.tsn = tsn;
  if (componentTypeId !== undefined) evt.componentTypeId = componentTypeId;
  if (componentCount !== undefined) evt.componentCount = componentCount;
  if (conflictDetected !== undefined) evt.conflictDetected = conflictDetected;
  return evt;
}

function decodeTransactionPersist(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i64 tsn] [u8 optMask] [i64 walLsn?]
  let cursor = header.payloadOffset;
  const tsn = reader.readI64Decimal(cursor);
  cursor += 8;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let walLsn: string | undefined;
  if ((optMask & OPT_TX_WAL_LSN) !== 0) {
    walLsn = reader.readI64Decimal(cursor);
    cursor += 8;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.tsn = tsn;
  if (walLsn !== undefined) evt.walLsn = walLsn;
  return evt;
}

const OPT_SPAWN_ENTITY_ID = 0x01;
const OPT_SPAWN_TSN = 0x02;

function decodeEcsSpawn(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeId] [u8 optMask] [u64 entityId?] [i64 tsn?]
  let cursor = header.payloadOffset;
  const archetypeId = reader.readU16(cursor);
  cursor += 2;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let entityId: string | undefined;
  let tsn: string | undefined;
  if ((optMask & OPT_SPAWN_ENTITY_ID) !== 0) {
    entityId = reader.readU64Decimal(cursor);
    cursor += 8;
  }
  if ((optMask & OPT_SPAWN_TSN) !== 0) {
    tsn = reader.readI64Decimal(cursor);
    cursor += 8;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.archetypeId = archetypeId;
  if (entityId !== undefined) evt.entityId = entityId;
  if (tsn !== undefined) evt.tsn = tsn;
  return evt;
}

const OPT_DESTROY_CASCADE_COUNT = 0x01;
const OPT_DESTROY_TSN = 0x02;

function decodeEcsDestroy(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u64 entityId] [u8 optMask] [i32 cascadeCount?] [i64 tsn?]
  let cursor = header.payloadOffset;
  const entityId = reader.readU64Decimal(cursor);
  cursor += 8;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let cascadeCount: number | undefined;
  let tsn: string | undefined;
  if ((optMask & OPT_DESTROY_CASCADE_COUNT) !== 0) {
    cascadeCount = reader.readI32(cursor);
    cursor += 4;
  }
  if ((optMask & OPT_DESTROY_TSN) !== 0) {
    tsn = reader.readI64Decimal(cursor);
    cursor += 8;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.entityId = entityId;
  if (cascadeCount !== undefined) evt.cascadeCount = cascadeCount;
  if (tsn !== undefined) evt.tsn = tsn;
  return evt;
}

const OPT_QUERY_RESULT_COUNT = 0x01;
const OPT_QUERY_SCAN_MODE = 0x02;
const OPT_QUERY_FOUND = 0x04;

function decodeEcsQuery(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeTypeId] [u8 optMask] [i32 resultCount-or-found?] [u8 scanMode?]
  // ResultCount and Found share the same 4 B slot — decoder disambiguates by which optMask bit is set.
  let cursor = header.payloadOffset;
  const archetypeTypeId = reader.readU16(cursor);
  cursor += 2;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let resultCount: number | undefined;
  let scanMode: number | undefined;
  let found: boolean | undefined;

  if ((optMask & (OPT_QUERY_RESULT_COUNT | OPT_QUERY_FOUND)) !== 0) {
    const slot = reader.readI32(cursor);
    if ((optMask & OPT_QUERY_FOUND) !== 0) {
      found = slot !== 0;
    } else {
      resultCount = slot;
    }
    cursor += 4;
  }
  if ((optMask & OPT_QUERY_SCAN_MODE) !== 0) {
    scanMode = reader.readU8(cursor);
    cursor += 1;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  // Server DTO uses `archetypeId` on the wire (not `archetypeTypeId`) — the type ID IS the archetype ID in the viewer's vocabulary.
  evt.archetypeId = archetypeTypeId;
  if (resultCount !== undefined) evt.resultCount = resultCount;
  if (scanMode !== undefined) evt.scanMode = scanMode;
  if (found !== undefined) evt.found = found;
  return evt;
}

const OPT_VR_MODE = 0x01;
const OPT_VR_RESULT_COUNT = 0x02;
const OPT_VR_DELTA_COUNT = 0x04;

function decodeEcsViewRefresh(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeTypeId] [u8 optMask] [u8 mode?] [i32 resultCount?] [i32 deltaCount?]
  let cursor = header.payloadOffset;
  const archetypeTypeId = reader.readU16(cursor);
  cursor += 2;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let mode: number | undefined;
  let resultCount: number | undefined;
  let deltaCount: number | undefined;

  if ((optMask & OPT_VR_MODE) !== 0) {
    mode = reader.readU8(cursor);
    cursor += 1;
  }
  if ((optMask & OPT_VR_RESULT_COUNT) !== 0) {
    resultCount = reader.readI32(cursor);
    cursor += 4;
  }
  if ((optMask & OPT_VR_DELTA_COUNT) !== 0) {
    deltaCount = reader.readI32(cursor);
    cursor += 4;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.archetypeId = archetypeTypeId;
  if (mode !== undefined) evt.mode = mode;
  if (resultCount !== undefined) evt.resultCount = resultCount;
  if (deltaCount !== undefined) evt.deltaCount = deltaCount;
  return evt;
}

const OPT_PC_PAGE_COUNT = 0x01;

function decodePageCache(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i32 filePageIndex] [u8 optMask] [i32 pageCount?]
  // The Flush kinds reuse the filePageIndex slot to carry PageCount (per the wire spec on TraceEventKind.PageCacheFlush and *FlushCompleted).
  let cursor = header.payloadOffset;
  const primary = reader.readI32(cursor);
  cursor += 4;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let secondary: number | undefined;
  if ((optMask & OPT_PC_PAGE_COUNT) !== 0) {
    secondary = reader.readI32(cursor);
    cursor += 4;
  }

  const isFlush = kind === TraceEventKind.PageCacheFlush || kind === TraceEventKind.PageCacheFlushCompleted;
  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  if (isFlush) {
    evt.pageCount = primary;
  } else {
    evt.filePageIndex = primary;
    if (secondary !== undefined) evt.pageCount = secondary;
  }
  return evt;
}

function decodePageCacheBackpressure(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i32 retryCount] [i32 dirtyCount] [i32 epochCount]
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    retryCount: reader.readI32(o),
    dirtyCount: reader.readI32(o + 4),
    epochCount: reader.readI32(o + 8),
  };
}

function decodeClusterMigration(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeId] [i32 migrationCount]
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
    migrationCount: reader.readI32(o + 2),
  };
}

function decodeWal(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);

  switch (kind) {
    case TraceEventKind.WalFlush:
      // [i32 batchByteCount][i32 frameCount][i64 highLsn]
      evt.batchByteCount = reader.readI32(o);
      evt.frameCount = reader.readI32(o + 4);
      evt.highLsn = reader.readI64Decimal(o + 8);
      break;
    case TraceEventKind.WalSegmentRotate:
      // [i32 newSegmentIndex]
      evt.newSegmentIndex = reader.readI32(o);
      break;
    case TraceEventKind.WalWait:
      // [i64 targetLsn]
      evt.targetLsn = reader.readI64Decimal(o);
      break;
  }
  return evt;
}

const OPT_CP_COUNT = 0x01;   // shared bit 0: DirtyPageCount / WrittenCount / TransitionedCount / RecycledCount depending on kind

function decodeCheckpoint(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  const o = header.payloadOffset;

  switch (kind) {
    case TraceEventKind.CheckpointCycle: {
      // [i64 targetLsn][u8 reason][u8 optMask][i32 dirtyPageCount?]
      evt.targetLsn = reader.readI64Decimal(o);
      evt.reason = reader.readU8(o + 8);
      const optMask = reader.readU8(o + 9);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.dirtyPageCount = reader.readI32(o + 10);
      }
      break;
    }
    case TraceEventKind.CheckpointWrite: {
      // [u8 optMask][i32 writtenCount?]
      const optMask = reader.readU8(o);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.writtenCount = reader.readI32(o + 1);
      }
      break;
    }
    case TraceEventKind.CheckpointTransition: {
      const optMask = reader.readU8(o);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.transitionedCount = reader.readI32(o + 1);
      }
      break;
    }
    case TraceEventKind.CheckpointRecycle: {
      const optMask = reader.readU8(o);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.recycledCount = reader.readI32(o + 1);
      }
      break;
    }
    // Collect + Fsync have no payload; header-only event is correct.
  }
  return evt;
}

function decodeStatisticsRebuild(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i32 entityCount][i32 mutationCount][i32 samplingInterval]
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    entityCount: reader.readI32(o),
    mutationCount: reader.readI32(o + 4),
    samplingInterval: reader.readI32(o + 8),
  };
}

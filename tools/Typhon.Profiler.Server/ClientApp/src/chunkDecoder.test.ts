/**
 * Tests for {@link decodeChunkBinary}'s tick-counter seeding under both the normal and continuation seeding regimes.
 * The decoder is stateful (tick counter advances on every TickStart), and mis-seeding leads to silently-wrong tick
 * numbers on decoded events — the worst failure class to diagnose post-hoc. These tests make the seeding contract
 * regression-safe at the function boundary, independent of the full fetch/worker pipeline.
 */
import { describe, it, expect } from 'vitest';
import { decodeChunkBinary } from './chunkDecoder';
import { TraceEventKind } from './types';

const COMMON_HEADER_SIZE = 12;
const TICK_END_RECORD_SIZE = COMMON_HEADER_SIZE + 2;
const TICK_START_RECORD_SIZE = COMMON_HEADER_SIZE;

/**
 * Write a 14-byte TickEnd record (common header + u8 overloadLevel + u8 tickMultiplier) into `bytes` at `offset`.
 * Chosen as the default "generic passthrough event" for these tests because (a) TickEnd has a well-defined minimal
 * payload, (b) it decodes to a non-null TraceEvent, (c) it does NOT advance the decoder's tick counter (unlike TickStart).
 */
function writeTickEnd(bytes: Uint8Array, offset: number, ts: number): void {
  const view = new DataView(bytes.buffer, bytes.byteOffset + offset, TICK_END_RECORD_SIZE);
  view.setUint16(0, TICK_END_RECORD_SIZE, true);
  view.setUint8(2, TraceEventKind.TickEnd);
  view.setUint8(3, 0);   // threadSlot
  // Write ts as i64 little-endian via two u32s (DataView has no i64 / BigInt path short of setBigInt64).
  view.setUint32(4, ts & 0xFFFFFFFF, true);
  view.setUint32(8, Math.floor(ts / 0x100000000), true);
}

/** Write a 12-byte TickStart record (common header only, empty payload). */
function writeTickStart(bytes: Uint8Array, offset: number, ts: number): void {
  const view = new DataView(bytes.buffer, bytes.byteOffset + offset, TICK_START_RECORD_SIZE);
  view.setUint16(0, TICK_START_RECORD_SIZE, true);
  view.setUint8(2, TraceEventKind.TickStart);
  view.setUint8(3, 0);
  view.setUint32(4, ts & 0xFFFFFFFF, true);
  view.setUint32(8, Math.floor(ts / 0x100000000), true);
}

describe('decodeChunkBinary — seeding', () => {
  it('continuation chunk: seed at firstTick, all events tagged with firstTick', () => {
    const seedTick = 42;
    const count = 10;
    const bytes = new Uint8Array(count * TICK_END_RECORD_SIZE);
    for (let i = 0; i < count; i++) {
      writeTickEnd(bytes, i * TICK_END_RECORD_SIZE, 100 + i);
    }

    const events = decodeChunkBinary(bytes, seedTick, /*ticksPerUs=*/10, /*isContinuation=*/true);

    expect(events).toHaveLength(count);
    for (const e of events) {
      expect(e.tickNumber).toBe(seedTick);
    }
  });

  it('normal chunk with leading TickStart: counter bumps to firstTick', () => {
    const fromTick = 42;
    const tailCount = 9;
    const bytes = new Uint8Array(TICK_START_RECORD_SIZE + tailCount * TICK_END_RECORD_SIZE);
    writeTickStart(bytes, 0, 100);
    for (let i = 0; i < tailCount; i++) {
      writeTickEnd(bytes, TICK_START_RECORD_SIZE + i * TICK_END_RECORD_SIZE, 101 + i);
    }

    const events = decodeChunkBinary(bytes, fromTick, /*ticksPerUs=*/10, /*isContinuation=*/false);

    expect(events).toHaveLength(1 + tailCount);
    // TickStart itself is tagged with fromTick (increment happens BEFORE tagging). All subsequent events inherit that number.
    for (const e of events) {
      expect(e.tickNumber).toBe(fromTick);
    }
  });

  it('continuation chunk with internal TickStart: counter increments mid-block', () => {
    const seedTick = 42;
    const headCount = 5;
    const tailCount = 4;
    const bytes = new Uint8Array(
      headCount * TICK_END_RECORD_SIZE + TICK_START_RECORD_SIZE + tailCount * TICK_END_RECORD_SIZE,
    );
    let offset = 0;
    for (let i = 0; i < headCount; i++) {
      writeTickEnd(bytes, offset, 100 + i);
      offset += TICK_END_RECORD_SIZE;
    }
    writeTickStart(bytes, offset, 200);
    offset += TICK_START_RECORD_SIZE;
    for (let i = 0; i < tailCount; i++) {
      writeTickEnd(bytes, offset, 201 + i);
      offset += TICK_END_RECORD_SIZE;
    }

    const events = decodeChunkBinary(bytes, seedTick, /*ticksPerUs=*/10, /*isContinuation=*/true);

    expect(events).toHaveLength(headCount + 1 + tailCount);
    // Head events: on the seed tick (partial tail of a split tick).
    for (let i = 0; i < headCount; i++) {
      expect(events[i].tickNumber).toBe(seedTick);
    }
    // TickStart and everything after: on seedTick + 1.
    for (let i = headCount; i < events.length; i++) {
      expect(events[i].tickNumber).toBe(seedTick + 1);
    }
  });
});

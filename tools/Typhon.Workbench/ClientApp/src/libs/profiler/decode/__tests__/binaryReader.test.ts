import { describe, expect, it } from 'vitest';
import { BinaryReader } from '@/libs/profiler/decode/binaryReader';

/**
 * Covers the primitive reads that back every record-decode path. A regression here — wrong
 * endianness, off-by-one, sign-extension bug — would silently corrupt every decoded record,
 * showing up as scrambled timestamps or backwards IDs deep in the viewer. Unit coverage at the
 * byte level catches the class of bug that integration tests don't see until the UI looks wrong.
 */

function makeReader(bytes: number[]): BinaryReader {
  return new BinaryReader(new Uint8Array(bytes));
}

describe('BinaryReader — little-endian primitives', () => {
  it('readU8 returns the raw byte', () => {
    const r = makeReader([0x00, 0x7F, 0x80, 0xFF]);
    expect(r.readU8(0)).toBe(0x00);
    expect(r.readU8(1)).toBe(0x7F);
    expect(r.readU8(2)).toBe(0x80);
    expect(r.readU8(3)).toBe(0xFF);
  });

  it('readU16 is little-endian', () => {
    // 0x1234 LE == [0x34, 0x12]
    const r = makeReader([0x34, 0x12, 0xFF, 0xFF]);
    expect(r.readU16(0)).toBe(0x1234);
    expect(r.readU16(2)).toBe(0xFFFF);
  });

  it('readU32 is little-endian', () => {
    // 0xDEADBEEF LE == [0xEF, 0xBE, 0xAD, 0xDE]
    const r = makeReader([0xEF, 0xBE, 0xAD, 0xDE]);
    expect(r.readU32(0)).toBe(0xDEADBEEF);
  });

  it('readI32 sign-extends the high bit', () => {
    // 0xFFFFFFFF LE → -1 as i32 (signed).
    const r = makeReader([0xFF, 0xFF, 0xFF, 0xFF]);
    expect(r.readI32(0)).toBe(-1);
    // 0x80000000 LE → Int32 min.
    const r2 = makeReader([0x00, 0x00, 0x00, 0x80]);
    expect(r2.readI32(0)).toBe(-2147483648);
  });

  it('readI64AsNumber round-trips safe-integer-range values', () => {
    // 100 = 0x64 00 00 00 00 00 00 00 LE
    const r = makeReader([0x64, 0, 0, 0, 0, 0, 0, 0]);
    expect(r.readI64AsNumber(0)).toBe(100);

    // 2^32 (= 4294967296) — straddles the 32-bit boundary to prove low+high composition.
    const r2 = makeReader([0, 0, 0, 0, 1, 0, 0, 0]);
    expect(r2.readI64AsNumber(0)).toBe(4294967296);
  });

  it('readU64Decimal preserves precision above 2^53', () => {
    // 0xFFFFFFFFFFFFFFFF → max u64 as decimal string.
    const r = makeReader([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
    expect(r.readU64Decimal(0)).toBe('18446744073709551615');

    // 2^53 + 1 — the first integer Number can't represent exactly.
    // 2^53 = 0x20000000000000; +1 = 0x20000000000001 → LE [01, 00, 00, 00, 00, 00, 20, 00]
    const r2 = makeReader([0x01, 0, 0, 0, 0, 0, 0x20, 0]);
    expect(r2.readU64Decimal(0)).toBe('9007199254740993');
  });

  it('readI64Decimal preserves sign on negative values', () => {
    // 0xFFFFFFFFFFFFFFFF as i64 → -1
    const r = makeReader([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
    expect(r.readI64Decimal(0)).toBe('-1');

    // Int64 min = 0x8000000000000000
    const r2 = makeReader([0, 0, 0, 0, 0, 0, 0, 0x80]);
    expect(r2.readI64Decimal(0)).toBe('-9223372036854775808');
  });

  it('readUtf8 decodes a UTF-8 slice with a caller-provided TextDecoder', () => {
    const decoder = new TextDecoder('utf-8');
    const bytes = new Uint8Array([0x48, 0x65, 0x6C, 0x6C, 0x6F]); // "Hello"
    const r = new BinaryReader(bytes);
    expect(r.readUtf8(0, 5, decoder)).toBe('Hello');
    // Zero-length returns empty string without touching the decoder.
    expect(r.readUtf8(0, 0, decoder)).toBe('');
  });

  it('respects Uint8Array byteOffset so a subarray reader reads from the right position', () => {
    // A Uint8Array that's a view on a bigger buffer, starting at offset 4. BinaryReader must honour
    // that offset when constructing its DataView — getting this wrong would read garbage from the
    // beginning of the parent buffer.
    const parent = new Uint8Array([0, 0, 0, 0, 0x34, 0x12, 0, 0]);
    const sub = parent.subarray(4, 6);
    const r = new BinaryReader(sub);
    expect(r.length).toBe(2);
    expect(r.readU16(0)).toBe(0x1234);
  });
});

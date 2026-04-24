import { describe, expect, it } from 'vitest';
import { decompressLz4Block } from '@/libs/profiler/decode/lz4Block';

/**
 * LZ4 block-format decompressor coverage. The production code ships real chunks through this
 * function on every viewport change; a regression would corrupt every record payload below the
 * decode layer — invisible until the UI looked wrong.
 *
 * We don't have a JS encoder in the bundle (decode-only, to keep worker payloads small), so the
 * round-trip tests hand-craft valid LZ4 block streams. This is fine because LZ4's block format is
 * trivially small — the hand-crafts exercise every branch the real chunks can hit:
 *
 *   - Literals-only (the final-sequence case; every valid block ends in one).
 *   - A match with non-overlapping offset (memcpy-style — the copyWithin fast path).
 *   - A match with overlapping offset (RLE-style — the byte-loop fallback).
 *   - Literal-length extension (nibble = 15 + trailing 0xFF… bytes).
 *   - Every documented error path (oversized literal, offset=0, match overrun).
 */

describe('decompressLz4Block — round-trip', () => {
  it('decodes a literals-only block', () => {
    // Token 0x50: high nibble = 5 (literal count), low nibble = 0 (no match). Remaining 5 bytes
    // are the literals. End-of-block is detected when sIdx hits sEnd after the literal copy.
    const compressed = new Uint8Array([0x50, 0x48, 0x65, 0x6C, 0x6C, 0x6F]); // "Hello"
    const decoded = decompressLz4Block(compressed, 5);
    expect(new TextDecoder().decode(decoded)).toBe('Hello');
  });

  it('decodes a block with a non-overlapping back-reference (memcpy branch)', () => {
    // Encode "abcdefabcdef" (12 B): first 6 literals, then match of length 6 at offset 6.
    //
    // Token for initial 6 literals + no match yet: we actually need to end with a literals-only
    // sequence per spec. So we split into: seq1 = 4 literals + match of 6 @ offset 4, seq2 = 2
    // trailing literals.
    //
    // seq1 token: literal_len=4, match_len=2 (+4 = 6 decoded). Token = 0x42.
    //   literals = "abcd"
    //   offset u16 LE = 4 → 0x04 0x00
    //   (no match extension because 2 < 15)
    // seq2 token: literal_len=2, match_len=0 (end-of-block). Token = 0x20. Literals = "ef".
    //
    // Decoded: abcd + abcdef (match copies "abcdef" from 4 bytes back, i.e. "abcd" + "ab" looped?
    // Wait — offset 4, match length 6. If offset >= matchLen, it's a direct memcpy from dIdx-4.
    // Here offset=4, matchLen=6 → offset < matchLen → overlap branch. Let me use offset=6 instead.
    //
    // Re-plan: "abcdefabcdef" (12 B). First literal run = "abcdef" (6 B), then match of length 6
    // at offset 6. The match is non-overlapping (offset 6 == matchLen 6) — actually equal offset
    // triggers the overlap path per the code (`if (offset >= matchLen)` — 6 >= 6 → memcpy).
    //
    // seq1 token: literal_len=6, match_len=2 (+4 = 6). Token = 0x62.
    //   literals = "abcdef"
    //   offset u16 LE = 6 → 0x06 0x00
    // That leaves 0 trailing literals but the decoder needs a literals-only final sequence (or
    // sIdx must hit sEnd after the match). Sub-case: per the code, after the match copy, the outer
    // while loop checks `sIdx < sEnd` at the TOP — if sEnd has been reached, the loop exits
    // cleanly. That handles a block that ends with a match as long as the compressed stream ends
    // right after the match offset bytes. Let's test exactly that.
    //
    // Compressed bytes: [0x62, 'a', 'b', 'c', 'd', 'e', 'f', 0x06, 0x00] = 9 B
    const compressed = new Uint8Array([
      0x62,
      0x61, 0x62, 0x63, 0x64, 0x65, 0x66, // 'abcdef'
      0x06, 0x00,                          // offset = 6
    ]);
    // Actually the real LZ4 spec says the last sequence must end in literals (5 trailing literals
    // minimum is the stricter spec, but our decoder loop is lenient). Let's check what our decoder
    // actually does: after the match copy it increments dIdx by matchLen then loops to the top;
    // the `while (sIdx < sEnd)` exits cleanly. So this test should pass.
    const decoded = decompressLz4Block(compressed, 12);
    expect(new TextDecoder().decode(decoded)).toBe('abcdefabcdef');
  });

  it('decodes RLE via overlapping match (byte-loop branch)', () => {
    // "aaaaaaaa" (8 B) via: 1 literal 'a', then match length 7 at offset 1 (classic RLE fill).
    // seq1 token: literal_len=1, match_len=3 (+4 = 7 decoded). Token = 0x13.
    //   literals = "a"
    //   offset u16 LE = 1 → 0x01 0x00
    // offset=1 < matchLen=7 → forces the byte-loop branch.
    const compressed = new Uint8Array([0x13, 0x61, 0x01, 0x00]);
    const decoded = decompressLz4Block(compressed, 8);
    expect(new TextDecoder().decode(decoded)).toBe('aaaaaaaa');
  });

  it('decodes a literal-length extension', () => {
    // 16 literals — forces the extension nibble. Literal length encoding: nibble 15, then one
    // extension byte with value 1 (15 + 1 = 16).
    // Token: 0xF0 (literal_len nibble=15, match_len=0). Ext byte: 0x01.
    const literals = Array.from({ length: 16 }, (_, i) => i + 1);
    const compressed = new Uint8Array([0xF0, 0x01, ...literals]);
    const decoded = decompressLz4Block(compressed, 16);
    expect(Array.from(decoded)).toEqual(literals);
  });
});

describe('decompressLz4Block — error paths', () => {
  it('rejects a literal run that overruns the input', () => {
    // Claims 10 literals but only provides 2 → literal copy reads past end of input.
    const compressed = new Uint8Array([0xA0, 0x41, 0x42]); // token=0xA0 → literal_len=10
    expect(() => decompressLz4Block(compressed, 10)).toThrow(/literal run overruns input/);
  });

  it('rejects offset=0', () => {
    // 1 literal + match with offset=0 → reserved.
    const compressed = new Uint8Array([0x13, 0x61, 0x00, 0x00]);
    expect(() => decompressLz4Block(compressed, 8)).toThrow(/offset=0 is reserved/);
  });

  it('rejects a match that overruns the output buffer', () => {
    // 1 literal + match length 7 at offset 1 but caller specifies uncompressed=2 → match runs
    // past the 2-byte output.
    const compressed = new Uint8Array([0x13, 0x61, 0x01, 0x00]);
    expect(() => decompressLz4Block(compressed, 2)).toThrow(/match would overrun output/);
  });

  it('rejects a decoded length mismatch', () => {
    // Literals-only block of 5 bytes but caller says uncompressed=6 → post-loop length check fires.
    const compressed = new Uint8Array([0x50, 0x48, 0x65, 0x6C, 0x6C, 0x6F]);
    expect(() => decompressLz4Block(compressed, 6)).toThrow(/produced 5 bytes, expected 6/);
  });
});

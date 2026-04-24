/**
 * Pure-TypeScript LZ4 block-format decompressor.
 *
 * **Block format, not frame format.** The `.typhon-trace-cache` sidecar stores chunks using LZ4 block format — raw compressed bytes with no
 * magic/header/flags — because the server already knows the compressed and uncompressed lengths from the ChunkManifestEntry. This decoder
 * matches the same contract: pass in the compressed bytes + known uncompressed length, get the decoded bytes back. There's no autodetection
 * and no frame-level metadata to parse.
 *
 * **Why pure-TS instead of a wasm library?** The LZ4 block format is trivially small (~60 lines of decompressor). Bundling a ~20 KB wasm
 * payload, arranging wasm module fetching inside a Web Worker, and dealing with sync vs async instantiation is more code and risk than
 * writing the decompressor inline. A wasm implementation would be faster on huge chunks (say, ≥4 MB) but our chunks are bounded at
 * TraceFileCacheConstants.ByteCap = 1 MiB — a JS decompressor handles those in ~2-4 ms on a modern laptop, which is far below the network
 * round-trip floor we just eliminated. Cost/benefit favors zero-dep.
 *
 * **Reference:** https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
 */

/**
 * Decompress <paramref name="compressed"/> into a fresh <paramref name="uncompressedLength"/>-byte <see cref="Uint8Array"/>. Throws if the
 * compressed stream is malformed (undersized, runs past the end, unterminated match extension).
 *
 * **Contract:** the caller MUST pass the exact uncompressed length — this is the `UncompressedBytes` field from the ChunkManifestEntry. We
 * don't guess: the block format has no way to derive it from the stream, and overshooting the output buffer would silently write garbage
 * past the tail of the decoded records.
 */
export function decompressLz4Block(compressed: Uint8Array, uncompressedLength: number): Uint8Array {
  const output = new Uint8Array(uncompressedLength);
  let sIdx = 0;                       // cursor into the compressed input
  let dIdx = 0;                       // cursor into the decompressed output
  const sEnd = compressed.length;

  while (sIdx < sEnd) {
    // ── Token byte: high 4 bits = literal length, low 4 bits = match length offset ──
    const token = compressed[sIdx++];
    let literalLen = (token >>> 4) & 0x0F;
    let matchLen = token & 0x0F;

    // ── Literal length extension ──
    // If high nibble is 15, read additional bytes; each 0xFF adds 255, any byte < 0xFF terminates and contributes its value, then stop.
    if (literalLen === 15) {
      while (sIdx < sEnd) {
        const b = compressed[sIdx++];
        literalLen += b;
        if (b !== 0xFF) break;
      }
    }

    // ── Copy literals ──
    // Manual byte-by-byte copy is simple and handles any length; TypedArray.set with a subarray would do a single memcpy but requires
    // bounds math. At our chunk sizes (≤1 MiB) the per-byte loop costs ~1-2 ms total — acceptable.
    if (literalLen > 0) {
      if (sIdx + literalLen > sEnd) {
        throw new Error(`LZ4 decode: literal run overruns input (needed ${literalLen} at ${sIdx}, ${sEnd - sIdx} available)`);
      }
      output.set(compressed.subarray(sIdx, sIdx + literalLen), dIdx);
      sIdx += literalLen;
      dIdx += literalLen;
    }

    // ── End-of-block detection ──
    // The last sequence in a valid LZ4 block carries ONLY literals — no offset or match length. When sIdx hits sEnd after the literal copy,
    // we're done. The spec guarantees the last 5 bytes of output are always literals, so offset=0 is reserved (never emitted as a match).
    if (sIdx >= sEnd) {
      break;
    }

    // ── Match offset (2 bytes LE) ──
    if (sIdx + 2 > sEnd) {
      throw new Error(`LZ4 decode: offset read overruns input at ${sIdx}`);
    }
    const offset = compressed[sIdx] | (compressed[sIdx + 1] << 8);
    sIdx += 2;
    if (offset === 0) {
      throw new Error('LZ4 decode: offset=0 is reserved');
    }

    // ── Match length extension ──
    // Same pattern as literal. The decoded match length is (token's low nibble) + 4 minimum length; extension adds on top of that.
    if (matchLen === 15) {
      while (sIdx < sEnd) {
        const b = compressed[sIdx++];
        matchLen += b;
        if (b !== 0xFF) break;
      }
    }
    matchLen += 4;                    // minimum-match-length constant from the spec

    // ── Copy match ──
    // Match source = output[dIdx - offset]. Two cases:
    //   1. offset >= matchLen: source and destination regions don't overlap — native Uint8Array.copyWithin uses memmove, ~10-50× faster than
    //      the JS byte loop.
    //   2. offset < matchLen: destination overlaps the source (LZ4's RLE idiom, commonly used for zero fills). copyWithin would copy the
    //      initial source range exactly once and then read already-written destination bytes back as "source", which produces different
    //      output than the spec mandates. Fall back to the byte loop here — small matches only in practice.
    const matchStart = dIdx - offset;
    if (matchStart < 0) {
      throw new Error(`LZ4 decode: match points before output start (offset ${offset}, dIdx ${dIdx})`);
    }
    if (dIdx + matchLen > uncompressedLength) {
      throw new Error(`LZ4 decode: match would overrun output (dIdx ${dIdx} + matchLen ${matchLen} > ${uncompressedLength})`);
    }
    if (offset >= matchLen) {
      output.copyWithin(dIdx, matchStart, matchStart + matchLen);
    } else {
      for (let i = 0; i < matchLen; i++) {
        output[dIdx + i] = output[matchStart + i];
      }
    }
    dIdx += matchLen;
  }

  if (dIdx !== uncompressedLength) {
    throw new Error(`LZ4 decode: produced ${dIdx} bytes, expected ${uncompressedLength}`);
  }
  return output;
}

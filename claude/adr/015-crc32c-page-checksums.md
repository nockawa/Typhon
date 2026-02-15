# ADR-015: CRC32C Hardware-Accelerated Page Checksums

**Status**: Accepted
**Date**: 2025-01 (inferred from conversation history and WAL design)
**Deciders**: Developer + Claude (design session)

## Context

Pages can be corrupted by:
1. **Torn writes**: Power failure during page write (NVMe AWUPF may be 4KB on consumer drives, page is 8KB)
2. **Bit rot**: Silent storage corruption over time
3. **Firmware bugs**: Controller writes wrong data to wrong location

Detection requires a checksum verified on every page read. The checksum algorithm must be:
- Fast enough for hot-path verification (~0.1–1µs per 8KB page)
- Hardware-accelerated on modern CPUs
- Standard (interoperable, well-tested)

## Decision

Use **CRC32C (Castagnoli polynomial)** with hardware acceleration via SSE4.2 `CRC32` instruction:

- Checksum stored in `PageBaseHeader.CRC32C` field
- Verified on every page read from disk
- Updated on every page write
- Hardware path: ~0.4µs per 8KB page (with 3-way interleaving: ~6 GB/s)
- Software fallback: ~2.5µs per 8KB page (~0.5 GB/s)
- Same checksum used for WAL records

## Alternatives Considered

1. **CRC32 (IEEE 802.3 polynomial)** — `System.IO.Hashing.Crc32`. No hardware instruction support. Wrong polynomial for SSE4.2. ~6× slower than CRC32C hardware path.
2. **xxHash (xxHash3/xxHash64)** — Faster in software (~10 GB/s), but no hardware instruction. Not standard in storage systems.
3. **SHA-256** — Cryptographically strong, but ~100× slower. Overkill for data integrity (not adversarial).
4. **Fletcher-32** — Simple, fast in software, but weaker error detection than CRC32C.
5. **No checksum** — Fastest, but silent corruption undetectable until data is consumed (potentially catastrophic for a database).

## Consequences

**Positive:**
- ~0.4µs per page with SSE4.2 (negligible overhead on read path)
- Standard algorithm: used by PostgreSQL, RocksDB, ext4, Btrfs, iSCSI, SCTP
- Detects all single-bit errors, all double-bit errors, and most multi-bit burst errors
- Hardware support on all modern x86 (since Nehalem 2008) and ARM (since ARMv8-A)
- Same code path for page checksums and WAL record checksums

**Negative:**
- Not cryptographically secure (intentional corruption not detected — but this isn't a threat model for embedded DB)
- Requires `System.Runtime.Intrinsics` for hardware path (platform-specific code)
- Software fallback path is 6× slower (relevant for very old hardware only)
- 4 bytes per page for checksum storage (negligible)

**Cross-references:**
- [03-storage.md](../overview/03-storage.md) — CRC32C in PageBaseHeader
- [06-durability.md](../overview/06-durability.md) §6.1 — WAL record CRC (authoritative header format)
- [06-durability.md](../overview/06-durability.md) §6.2 — Page checksums design
- [11-utilities.md](../overview/11-utilities.md) — CRC32C utility implementation

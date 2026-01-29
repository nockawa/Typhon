# ADR-006: 8KB Fixed Page Size

**Status**: Accepted
**Date**: 2024-01 (project inception)
**Deciders**: Developer

## Context

Page-based storage engines must choose a fixed page size that balances:
- I/O efficiency (larger pages = fewer I/O ops for sequential scans)
- Space efficiency (smaller pages = less wasted space for small components)
- Alignment with hardware (NVMe atomic write unit, OS page size, cache lines)
- Recovery granularity (larger pages = more data at risk from torn writes)

## Decision

Use **8192 bytes (8KB)** as the fixed page size:

```
Page (8192 bytes):
  - PageBaseHeader (64 bytes): PageIndex, ChangeRevision, CRC32C, Flags
  - PageMetadata (128 bytes): Occupancy bitmaps for chunks
  - PageRawData (8000 bytes): Actual component/index data
```

The 192-byte header leaves 8000 bytes of usable payload per page.

## Alternatives Considered

1. **4KB** (PostgreSQL, SQLite) — Matches OS page size exactly, but too small for many component types when considering MVCC overhead.
2. **16KB** (InnoDB default) — Better for large components, but wastes more space for small ones; larger torn-write risk window.
3. **Variable page sizes** — Complex buffer management, no SIMD-friendly alignment.
4. **64KB** (some analytics DBs) — Great for sequential scans, terrible for random point lookups.

## Consequences

**Positive:**
- Matches common NVMe atomic write units (4KB minimum, 8KB often)
- Divisible by 64-byte cache lines (125 cache lines per page)
- 8000-byte payload fits most ECS components with room for revision chains
- Compatible with most OS virtual memory page sizes (4KB granularity)

**Negative:**
- Components larger than ~8000 bytes cannot fit in a single chunk
- Some wasted space for very small components (16-byte component in 64-byte chunk)
- Torn writes still possible on consumer NVMe with AWUPF=4KB (mitigated by CRC32C + FPI)

**Cross-references:**
- [03-storage.md](../overview/03-storage.md) §3.1 — Page structure
- [ADR-015](015-crc32c-page-checksums.md) — Torn write detection
- [ADR-024](024-fpi-over-double-write-buffer.md) — Torn write repair

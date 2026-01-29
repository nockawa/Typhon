# ADR-024: Full-Page Images Over Double-Write Buffer

**Status**: Accepted
**Date**: 2025-01 (inferred from WAL design)
**Deciders**: Developer + Claude (design session)

## Context

Torn writes occur when a crash interrupts a page write. For Typhon's 8KB pages on consumer NVMe with 4KB atomic write unit (AWUPF=4KB):
- First 4KB written successfully
- Last 4KB contains stale data

The page is now corrupt: CRC32C will detect it, but how to repair?

Two industry approaches:
1. **Full-Page Image (FPI)**: Write the complete page to WAL before modifying it. On recovery, replace torn page with WAL copy.
2. **Double-Write Buffer (DWB)**: Write pages to a separate buffer first, then to their final location. On recovery, check DWB for intact copies. (InnoDB approach)

## Decision

Use **Full-Page Images (FPI)** — write the complete 8KB page to the WAL on first modification after each checkpoint:

```
First write to page P after checkpoint:
  1. Write FPI record to WAL: [Header(32B) + PageData(8000B)]
  2. Modify page P in memory
  3. (Eventually) Checkpoint writes P to disk

Recovery for torn page:
  1. CRC32C detects corruption
  2. Find FPI in WAL for this page
  3. Restore page from FPI
  4. Replay subsequent logical records
```

Only the **first modification per page per checkpoint interval** writes an FPI. Subsequent modifications to the same page only write small logical records.

## Alternatives Considered

1. **Double-Write Buffer (InnoDB)** — Writes every dirty page twice (DWB + final location). 2× write amplification for all pages. Simpler recovery but much more I/O.
2. **No torn page protection** — Rely on hardware atomicity guarantees. Unsafe on consumer NVMe (AWUPF < page size).
3. **Checksummed sub-page writes** — Write 4KB sub-pages with individual checksums. Complex, and doesn't help if both halves need to be consistent.
4. **Copy-on-Write pages** — Write to new location, atomically update pointer. Requires page-level redirection table (complexity).

## Consequences

**Positive:**
- Write amplification only on first modification (amortized across checkpoint interval)
- No separate doublewrite file to manage
- FPI naturally flows through existing WAL pipeline
- Recovery uses same WAL replay infrastructure (no separate DWB recovery path)
- Most pages modified multiple times per checkpoint → FPI cost amortized well

**Negative:**
- First modification per checkpoint is expensive: 8KB FPI + logical record (vs just logical)
- WAL size spikes at beginning of checkpoint interval (many FPIs)
- Recovery must scan WAL for FPIs (indexed by page ID in WAL record header)
- Cold pages modified once per checkpoint get worst-case amplification

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.4 — FPI mechanism
- [ADR-015](015-crc32c-page-checksums.md) — CRC32C detects torn pages
- [ADR-011](011-logical-wal-records.md) — Logical records for subsequent modifications
- [design/WAL-Design.md](../design/WAL-Design.md) — FPI record format

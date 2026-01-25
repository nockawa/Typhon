# ADR-026: Separate SSD for WAL vs Data

**Status**: Accepted
**Date**: 2025-01 (inferred from WAL design)
**Deciders**: Developer + Claude (design session)

## Context

Typhon has two fundamentally different I/O patterns:

1. **WAL writes**: Sequential append-only, small records (80–300B typical), latency-critical (Immediate mode = FUA on every commit)
2. **Data page reads**: Random access, 8KB pages, throughput-critical (cache misses during queries)

On a single SSD:
- WAL FUA writes compete with data page reads in the device queue
- NVMe command scheduling may delay WAL writes behind queued reads
- NAND FTL garbage collection (triggered by writes) can spike read latency

## Decision

**Recommend deploying WAL and data files on separate SSDs** for production workloads:

```
SSD 1 (Data):
  - Database file(s) with component pages, index pages
  - Workload: 70% random reads (B+Tree lookups, cache misses)
  - Optimized for: IOPS, random read latency

SSD 2 (WAL):
  - WAL segment files (append-only)
  - Workload: 100% sequential writes (FUA)
  - Optimized for: Write latency, sequential throughput
```

This is a **recommendation**, not a requirement. Typhon works on a single SSD but with potential latency interference.

## Alternatives Considered

1. **Single SSD for everything** — Simpler deployment, but WAL FUA writes interfere with data reads (head-of-line blocking in device queue).
2. **WAL on RAM disk / battery-backed NVRAM** — Lowest possible WAL latency, but expensive hardware and data loss risk if backup battery fails.
3. **WAL on separate partition (same SSD)** — Same physical device, no I/O isolation. Only organizational benefit.
4. **NVMe namespaces (same device, separate queues)** — Some isolation, but shares NAND FTL and GC. Not universally supported.

## Consequences

**Positive:**
- WAL FUA latency unaffected by data read load (dedicated device queue)
- Data read latency unaffected by WAL write pressure (no FTL GC interference)
- Separate wear patterns: WAL SSD gets high-endurance TLC/SLC; data SSD gets high-capacity QLC
- Can size/tier each SSD independently (small fast SSD for WAL, large SSD for data)

**Negative:**
- Additional hardware cost (two SSDs instead of one)
- More complex deployment and configuration
- Not always possible in cloud environments (instance storage may be single device)
- Overkill for development/testing (single SSD is fine for non-production)

**Cross-references:**
- [06-durability.md](../overview/06-durability.md) §6.9 — Deployment recommendations
- [design/WAL-Design.md](../design/WAL-Design.md) — I/O patterns analysis
- [ADR-020](020-dedicated-wal-writer-thread.md) — WAL writer thread design

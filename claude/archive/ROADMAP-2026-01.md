# Typhon Development Roadmap

**Last Updated:** January 2026
**Current Focus:** Telemetry & Instrumentation Infrastructure

---

## Status Legend

| Symbol | Meaning |
|--------|---------|
| :white_large_square: | Not started |
| :construction: | In progress |
| :white_check_mark: | Completed |
| :no_entry: | Blocked |
| :thinking: | Needs research/design |

---

## Current Phase: Telemetry & Observability

> Building a comprehensive, zero-overhead-when-disabled telemetry system for debugging, profiling, and production monitoring. This phase also includes AccessControl refactoring and performance optimizations discovered along the way.

### Active Work

| Status | Item | Design Doc | Branch | Notes |
|--------|------|------------|--------|-------|
| :construction: | TelemetryConfig system | — | `telemetry` | JIT-friendly static config with JSON/env var support |
| :construction: | AccessControl telemetry instrumentation | — | `telemetry` | Tracking contention, duration, access patterns |
| :white_check_mark: | Share code path between regular and telemetry versions | — | `telemetry` | Conditional compilation with `#if TELEMETRY` |
| :white_check_mark: | ConcurrentBitmapL3All optimization | — | `telemetry` | Direct bitfield access ~2x faster |

### Telemetry Components Status

| Component | Config Done | Instrumentation | Notes |
|-----------|-------------|-----------------|-------|
| AccessControl | :white_check_mark: | :construction: | Contention, duration, patterns |
| PagedMMF | :white_check_mark: | :white_large_square: | Page alloc, eviction, I/O, cache ratio |
| BTree | :white_check_mark: | :white_large_square: | Splits, merges, depth, comparisons |
| Transaction | :white_check_mark: | :white_large_square: | Commit/rollback, conflicts, duration |

### Up Next

| Priority | Item | Design Doc | Estimate | Dependencies |
|----------|------|------------|----------|--------------|
| P0 | PagedMMF telemetry instrumentation | — | M | TelemetryConfig |
| P1 | BTree telemetry instrumentation | — | M | TelemetryConfig |
| P1 | Transaction telemetry instrumentation | — | M | TelemetryConfig |
| P2 | Telemetry data export/visualization | [Dashboard](research/Dashboard.md) | L | All instrumentation |

---

## Backlog

### Core Engine

| Priority | Item | Status | Design Doc | Notes |
|----------|------|--------|------------|-------|
| | Query System | :thinking: | [QuerySystem](research/QuerySystem.md), [QueryEngine](design/QueryEngine.md) | Research done, design ready |
| | Persistent Views | :thinking: | [PersistentViews](research/PersistentViews.md) | |
| | Component Collections | :white_check_mark: | [ComponentCollection](archive/ComponentCollection.md) | Implemented Dec 2024 |
| | View Transactions | :thinking: | [ViewTransactionDesign](design/ViewTransactionDesign.md) | |

### Performance

| Priority | Item | Status | Design Doc | Notes |
|----------|------|--------|------------|-------|
| | Threading Improvements | :thinking: | [ThreadingDocumentation](research/ThreadingDocumentation.md) | |
| | Optimization Pass | :thinking: | [Optimizations](research/Optimizations.md) | |
| | Chunk Access Refactor | :thinking: | [ChunkAccess](research/ChunkAccess.md) | |

### Reliability & Maintenance

| Priority | Item | Status | Design Doc | Notes |
|----------|------|--------|------------|-------|
| | Deferred Cleanup | :white_large_square: | [CompRevDeferredCleanup](design/CompRevDeferredCleanup.md) | Design complete |
| | Reliability Improvements | :thinking: | [Reliability](research/Reliability.md) | |

### Infrastructure

| Priority | Item | Status | Design Doc | Notes |
|----------|------|--------|------------|-------|
| | Resource Registry | :white_large_square: | [ResourceRegistry](design/ResourceRegistry.md) | Design complete |
| | Dashboard/Tooling | :thinking: | [Dashboard](research/Dashboard.md) | |

---

## Completed

| Item | Completed | Notes |
|------|-----------|-------|
| MVCC Implementation | 2024 | Core snapshot isolation, revision chains |
| B+Tree Indexes | 2024 | L16/L32/L64/String64 variants, single & multiple value |
| Transaction System | 2024 | Two-phase commit, conflict resolution, TSN-based |
| Persistence Layer (PagedMMF) | 2024 | Memory-mapped files, clock-sweep eviction |
| ChunkBasedSegment | 2024 | Fixed-size chunk allocation with 3-level bitmaps |
| Secondary Indexes | 2024 | B+Tree indexes on component fields |
| ComponentCollection | Dec 2024 | AllowMultiple component support |
| ChunkAccessor refactoring | Dec 2024 | Replaced ChunkRandomAccessor, added Roslyn analyzers |
| ComponentRevisionManager extraction | Dec 2024 | Extracted revision manipulation into dedicated types |
| Resource Registry design | Jan 2026 | [ResourceRegistry](design/ResourceRegistry.md) |
| Architecture documentation | Nov 2025 | [Architecture](reference/Architecture.md) |

---

## Ideas Parking Lot

> Items that might be worth doing someday, but aren't planned yet.

- _Idea 1_
- _Idea 2_

---

## Workflow

### Adding New Items

1. **Idea stage:** Add to `ideas/` or "Ideas Parking Lot"
2. **Research needed:** Create a doc in `research/` and move item to Backlog with :thinking:
3. **Ready to design:** Create a doc in `design/`
4. **Design approved:** Update status to :white_large_square:
5. **In progress:** Move to "Active Work", create branch, update status to :construction:
6. **Done:** Move doc to `reference/` (if still relevant) or `archive/`, update "Completed"

### Priority Levels

- **P0:** Critical - blocks other work or is a showstopper
- **P1:** High - important for current phase goals
- **P2:** Medium - should be done this phase if time allows
- **P3:** Low - nice to have, can slip to next phase

### Estimates

- **S (Small):** < 1 day
- **M (Medium):** 1-3 days
- **L (Large):** 3-7 days
- **XL (Extra Large):** > 1 week (consider breaking down)

---

## Notes

_Space for general notes, decisions, or context about the roadmap._


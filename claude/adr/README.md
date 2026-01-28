# Architecture Decision Records (ADRs)

> Capturing the "why" behind Typhon's architectural choices.

---

## What is an ADR?

An Architecture Decision Record documents a single architectural decision: the context that motivated it, the options considered, the decision made, and the consequences (positive and negative).

ADRs are **immutable once accepted** — if a decision is later reversed, a new ADR supersedes the old one (which is marked `Superseded by ADR-XXX`).

---

## Format

Each ADR follows this structure:

```markdown
# ADR-NNN: Title

**Status**: Accepted | Superseded by ADR-XXX | Deprecated
**Date**: YYYY-MM-DD (when decision was made/inferred)
**Deciders**: Who made the decision

## Context
What is the issue that we're seeing that motivates this decision?

## Decision
What is the change that we're proposing/have agreed to implement?

## Alternatives Considered
What other options were evaluated? Why were they rejected?

## Consequences
What becomes easier or more difficult because of this decision?
```

---

## Index

### Core Architecture (001–005)

| # | Title | Status | Date |
|---|-------|--------|------|
| [001](001-three-tier-api-hierarchy.md) | Three-Tier API Hierarchy (DatabaseEngine → UoW → Tx) | Accepted | 2025-01 |
| [002](002-ecs-data-model.md) | ECS Data Model with Blittable Components | Accepted | 2024-01 |
| [003](003-mvcc-snapshot-isolation.md) | MVCC Snapshot Isolation with Optimistic Concurrency | Accepted | 2024-01 |
| [004](004-embedded-engine-no-server.md) | Embedded Engine (No Server Process) | Accepted | 2024-01 |
| [005](005-durability-mode-per-uow.md) | Durability Mode Per Unit of Work | Accepted | 2025-01 |

### Storage & Persistence (006–010)

| # | Title | Status | Date |
|---|-------|--------|------|
| [006](006-8kb-page-size.md) | 8KB Fixed Page Size | Accepted | 2024-01 |
| [007](007-clock-sweep-eviction.md) | Clock-Sweep Page Cache Eviction | Accepted | 2024-06 |
| [008](008-chunk-based-segments.md) | ChunkBasedSegment with Three-Level Bitmap | Accepted | 2024-06 |
| [009](009-pinned-memory-unsafe-code.md) | Pinned Memory and Unsafe Code for Hot Paths | Accepted | 2024-01 |
| [010](010-soa-simd-chunk-accessor.md) | SOA Layout with SIMD for ChunkAccessor | Accepted | 2024-09 |

### Durability & WAL (011–015)

| # | Title | Status | Date |
|---|-------|--------|------|
| [011](011-logical-wal-records.md) | Logical WAL Records (Not Physical Pages) | Accepted | 2025-01 |
| [012](012-mpsc-ring-buffer-wal.md) | Lock-Free MPSC Ring Buffer for WAL Serialization | Accepted | 2025-01 |
| [013](013-epoch-based-crash-recovery.md) | Epoch-Based Crash Recovery | Accepted | 2025-01 |
| [014](014-no-point-in-time-recovery.md) | No Point-in-Time Recovery (WAL Recycled After Checkpoint) | Accepted | 2025-01 |
| [015](015-crc32c-page-checksums.md) | CRC32C Hardware-Accelerated Page Checksums | Accepted | 2025-01 |

### Concurrency & Synchronization (016–020, 031)

| # | Title | Status | Date |
|---|-------|--------|------|
| [016](016-three-mode-resource-access-control.md) | Three-Mode ResourceAccessControl (ACCESSING/MODIFY/DESTROY) | Accepted | 2025-01 |
| [017](017-64bit-access-control-state.md) | 64-Bit Atomic State for AccessControl | Partially superseded by 031 | 2024-09 |
| [018](018-adaptive-spin-wait.md) | Adaptive Spin-Wait (No Allocation Contention) | Accepted | 2024-06 |
| [019](019-runtime-telemetry-toggle.md) | Runtime Telemetry Toggle via Static Readonly | Accepted | 2025-01 |
| [020](020-dedicated-wal-writer-thread.md) | Dedicated WAL Writer Thread (Not ThreadPool) | Accepted | 2025-01 |
| [031](031-unified-concurrency-patterns.md) | Unified Concurrency Patterns (WaitContext, Deadline, IContentionTarget) | Accepted | 2026-01 |

### Indexing & Queries (021–023)

| # | Title | Status | Date |
|---|-------|--------|------|
| [021](021-specialized-btree-variants.md) | Specialized B+Tree Variants (No Runtime Generics) | Accepted | 2024-06 |
| [022](022-64byte-cache-aligned-nodes.md) | 64-Byte Cache-Aligned B+Tree Nodes | Accepted | 2024-06 |
| [023](023-circular-buffer-revision-chains.md) | Circular Buffer for MVCC Revision Chains | Accepted | 2024-01 |

### Recovery & Operations (024–026)

| # | Title | Status | Date |
|---|-------|--------|------|
| [024](024-fpi-over-double-write-buffer.md) | Full-Page Images Over Double-Write Buffer | Accepted | 2025-01 |
| [025](025-checkpoint-manager-sole-fsync-owner.md) | Checkpoint Manager as Sole Data Page fsync Owner | Accepted | 2025-01 |
| [026](026-separate-wal-ssd.md) | Separate SSD for WAL vs Data | Accepted | 2025-01 |

### Data Layout & Sizing (027)

| # | Title | Status | Date |
|---|-------|--------|------|
| [027](027-even-sized-hot-path-structs.md) | Even-Sized Structs for Hot-Path Data | Accepted | 2025-01 |

### Backup & Restore (028–030)

| # | Title | Status | Date |
|---|-------|--------|------|
| [028](028-cow-snapshot-backup.md) | Copy-on-Write Snapshot Backup (No WAL Dependency) | Accepted | 2025-01 |
| [029](029-reverse-delta-incremental-snapshots.md) | Reverse-Delta Incremental Snapshots | Accepted | 2025-01 |
| [030](030-dual-limit-retention-policy.md) | Dual-Limit Retention Policy for Backup Snapshots | Accepted | 2025-01 |

---

## Relationship to Other Documentation

ADRs complement the existing document lifecycle:

```
ideas/ → research/ → design/ → reference/
                                    ↑
                               adr/ captures the KEY DECISIONS
                               made during the journey above
```

- **Design docs** describe *how* something works in detail
- **ADRs** capture *why* a specific choice was made over alternatives
- **Overview docs** provide the architectural big picture

---

## Adding New ADRs

When a new architectural decision is made:

1. Create `NNN-short-title.md` using the template above
2. Add entry to the index table in this README
3. If superseding an old ADR, update the old one's status

Use the next available number. Numbers are never reused.

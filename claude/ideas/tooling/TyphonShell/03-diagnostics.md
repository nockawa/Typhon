# Part 03 — Diagnostics & Inspection

**Date:** 2026-02-08
**Status:** Raw idea

## Why Diagnostics Are the Killer Feature

Any database tool can do CRUD. What makes `Typhon.Shell` uniquely valuable is **direct visibility into engine internals**. Since the shell hosts the engine in-process, it has access to every internal data structure — no remote protocol, no sampling, no approximation. This is the kind of information that otherwise requires attaching a debugger or adding custom instrumentation.

## Page Cache Diagnostics

The page cache is central to Typhon's performance. Being able to observe its behavior interactively is invaluable for tuning.

### `cache-stats` — Cache Overview

```
tsh:mydb> cache-stats
  Page Cache
  ──────────────────────────────────────
  Total pages:     256 (2 MB)
  State breakdown:
    Free:          45  (17.6%)
    Idle:          142 (55.5%)
    Shared:        58  (22.7%)
    Exclusive:     3   (1.2%)
    Dirty:         8   (3.1%)
  ──────────────────────────────────────
  Hit rate:        94.2%  (12,847 / 13,632)
  Evictions:       1,204
  Clock hand:      page 87
  Dirty flushes:   34
```

### `cache-pages` — Page-Level Detail

```
tsh:mydb> cache-pages
  Page   State       Segment              Pins  Dirty  ChangeRev
  ────   ─────       ───────              ────  ─────  ─────────
  0      Idle        Header               0     no     1
  1      Idle        CompA.Data           0     no     42
  2      Shared      CompA.Data           2     no     42
  3      Exclusive   CompA.RevTable       1     yes    101
  ...

# Filter by state
tsh:mydb> cache-pages where state=dirty
  Page 3   Exclusive   CompA.RevTable   1  yes  101
  Page 17  Exclusive   CompA.PK_Index   1  yes  99
  ...

# Filter by segment
tsh:mydb> cache-pages where segment=CompA.Data
  Page 1   Idle     CompA.Data  0  no  42
  Page 2   Shared   CompA.Data  2  no  42
  Page 4   Idle     CompA.Data  0  no  38
  ...
```

### `cache-watch` — Live Monitoring (Stretch Goal)

```
tsh:mydb> cache-watch --interval 1s
  [Every 1s — Ctrl+C to stop]
  Time       Hit%   Evict  Dirty  Shared  Excl
  10:30:01   94.2%  0      8      58      3
  10:30:02   93.8%  2      6      61      1
  10:30:03   95.1%  0      4      55      2
  ^C
```

## Segment Diagnostics

### `segments` — Segment Overview

```
tsh:mydb> segments
  Segment               Type          ChunkSize  Pages   Occupancy  Chunks (used/total)
  ─────────────────     ──────────    ─────────  ─────   ─────────  ───────────────────
  CompA.Data            ChunkBased    12 B       15      72.3%      1,847 / 2,555
  CompA.RevTable        ChunkBased    64 B       4       45.1%      288 / 639
  CompA.PK_Index        ChunkBased    64 B       3       81.0%      156 / 192
  CompA.Score_Index     ChunkBased    64 B       2       63.2%      81 / 128
  CompB.Data            ChunkBased    8 B        6       89.4%      2,145 / 2,400
  ...
```

### `segment-detail` — Deep Dive

```
tsh:mydb> segment-detail CompA.Data
  CompA.Data — ChunkBasedSegment
  ──────────────────────────────────────
  Chunk size:      12 bytes
  Pages:           4–18 (15 pages)
  Occupancy:       72.3% (1,847 / 2,555 chunks)

  Bitmap levels:
    L0:  12 / 15 pages have free chunks
    L1:  varies by page
    L2:  per-chunk (not summarized)

  Per-page breakdown:
    Page 4:   98.2%  (157/160 chunks used)
    Page 5:   95.0%  (152/160 chunks used)
    Page 6:   88.1%  (141/160 chunks used)
    ...
    Page 18:  12.5%  (20/160 chunks used)   ← recently allocated
```

## B+Tree Diagnostics

### `btree` — Index Overview

```
tsh:mydb> btree CompA.PlayerId
  B+Tree: CompA.PlayerId (L32, unique)
  ──────────────────────────────────────
  Depth:         3
  Total nodes:   47
  Total keys:    1,024
  Fill factor:   78.4%

  Level breakdown:
    Level 0 (root):  1 node,   23 keys   (71.9% full)
    Level 1:         4 nodes,  112 keys   (87.5% full)
    Level 2 (leaf):  42 nodes, 889 keys   (76.8% full)

  Min key:  1
  Max key:  1,024
```

### `btree-dump` — Node-Level Inspection

```
tsh:mydb> btree-dump CompA.PlayerId --level 0
  Root Node (chunk 23)
  ──────────────────────────────────────
  Keys:     [128, 256, 384, 512, 640, 768, 896]
  Children: [24, 25, 26, 27, 28, 29, 30, 31]

# Dump a specific leaf node
tsh:mydb> btree-dump CompA.PlayerId --chunk 42
  Leaf Node (chunk 42)
  ──────────────────────────────────────
  Keys:   [897, 898, 899, ..., 920]
  Values: [chunk:1847, chunk:1848, chunk:1849, ..., chunk:1870]
  Next:   chunk 43
  Prev:   chunk 41
```

### `btree-validate` — Integrity Check

```
tsh:mydb> btree-validate CompA.PlayerId
  Validating B+Tree CompA.PlayerId...
  ✓ All keys sorted correctly
  ✓ All leaf nodes linked (forward + backward)
  ✓ All internal node pointers valid
  ✓ No orphan nodes detected
  ✓ Key count matches data segment (1,024)
  Validation passed
```

## MVCC & Revision Diagnostics

### `revisions` — Entity Revision History

```
tsh:mydb> revisions 1 CompA
  Entity 1 — CompA revision chain
  ──────────────────────────────────────
  Chain: 1 chunk, 3 items
  First revision: 1 (index 0)

  Rev  Tick   Time                 Committed  Data Preview
  ───  ────   ────                 ─────────  ────────────
  1    100    2026-02-08 10:30:00  yes        PlayerId=1 Health=100.0 Score=0
  2    142    2026-02-08 10:30:05  yes        PlayerId=1 Health=95.0  Score=10
  3    187    2026-02-08 10:31:12  yes        PlayerId=1 Health=95.0  Score=15
```

### `transactions` — Active Transaction State

```
tsh:mydb> transactions
  Active Transactions
  ──────────────────────────────────────
  Active:    1
  MinTick:   200
  Pool size: 8 available

  Tick  Age        Operations  Dirty
  ────  ───        ──────────  ─────
  200   2.3s       0           no       ← current shell transaction
```

### `mvcc-stats` — MVCC Overview

```
tsh:mydb> mvcc-stats CompA
  MVCC Statistics — CompA
  ──────────────────────────────────────
  Total entities:     1,024
  Total revisions:    3,847
  Avg revisions/entity: 3.76
  Max chain length:   12  (entity 42)
  Single-revision:    612 (59.8%)
  Multi-chunk chains: 3   (entities 42, 187, 901)

  Revision age distribution:
    < 1 min:    245 revisions
    1-10 min:   1,802 revisions
    > 10 min:   1,800 revisions (candidates for GC)
```

## Memory & Resource Diagnostics

### `memory` — Memory Usage Breakdown

```
tsh:mydb> memory
  Memory Usage
  ──────────────────────────────────────
  Page cache:      2.0 MB  (256 × 8 KB)
  Transaction pool: 128 KB
  Chunk accessors:  64 KB
  B+Tree caches:    32 KB
  Total managed:    ~2.2 MB
```

## Diagnostic Command Summary

| Command | What It Shows |
|---------|---------------|
| `cache-stats` | Page cache hit rate, state breakdown, eviction count |
| `cache-pages` | Per-page state, segment assignment, dirty flags |
| `cache-watch` | Live cache metrics over time |
| `segments` | All segments with type, occupancy, page ranges |
| `segment-detail` | Deep dive into one segment's per-page occupancy |
| `btree` | Index statistics: depth, fill factor, key range |
| `btree-dump` | Node-level key/pointer inspection |
| `btree-validate` | Structural integrity verification |
| `revisions` | Entity revision chain with data at each revision |
| `transactions` | Active transactions, MinTick, pool state |
| `mvcc-stats` | Revision distribution, chain lengths, GC candidates |
| `memory` | Memory usage breakdown by subsystem |

## Open Questions

- [ ] Should diagnostics expose raw page bytes (`page-dump <N>`)? Useful for debugging but verbose.
- [ ] Should `cache-watch` exist at v1 or is it a stretch goal? (Requires background thread + terminal handling)
- [ ] How much of this info should be queryable via the engine's existing observability (telemetry/metrics) vs. direct struct access?
- [ ] Should there be a `profile` command that runs a workload and reports cache/segment stats before and after?

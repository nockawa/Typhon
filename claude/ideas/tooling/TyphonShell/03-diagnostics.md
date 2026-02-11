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

### `page-dump` — Raw Page Inspection

```
tsh:mydb> page-dump 3
  Page 3 — Structured View
  ══════════════════════════════════════

  PageBaseHeader (64 bytes)
  ──────────────────────────────────────
  PageIndex:       3
  ChangeRevision:  101
  SegmentId:       2 (CompA.RevTable)
  Flags:           0x01 (InUse)
  Checksum:        0xA3F7_1C02

  PageMetadata (128 bytes)
  ──────────────────────────────────────
  ChunkSize:       64 B
  Chunks used:     98 / 125 (78.4%)
  L1 bitmap:       0xFF_FF_FF_E0_00_00_00_00

  PageRawData (8000 bytes)
  ──────────────────────────────────────
  0000: 01 00 00 00 2A 00 00 00  64 00 00 00 00 00 00 00  |....*...d.......|
  0010: 03 00 00 00 00 00 80 42  0F 00 00 00 00 00 00 00  |.......B........|
  0020: 00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00  |................|
  ...
  (truncated — full 8000 bytes shown in actual output)

# Pure hex dump for piping
tsh:mydb> page-dump 3 --raw
  0000: 03 00 00 00 65 00 00 00  02 00 00 00 01 00 00 00  ...
  (full 8192 bytes)

# Pipe to external tool
tsh mydb.typhon -c "page-dump 3 --raw" | xxd
```

### `cache-watch` — Live Monitoring (Post-v1)

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

## Resource Tree Navigator

### `resources` — Interactive Full-Screen Explorer

The `resources` command is the flagship diagnostic feature. It launches a **full-screen interactive TUI** (powered by [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)) that lets you explore the engine's entire [Resource Graph](../../overview/08-resources.md) through keyboard-driven drill-down navigation.

This is a fundamentally different interaction model from the other diagnostic commands. Instead of asking specific questions (`cache-stats`, `btree CompA.PlayerId`), you **discover** the engine's internals by navigating the resource tree — seeing what exists, how it's structured, and what each node's current state looks like.

#### Layout

```
┌─ Resource Tree ──────────────────┬─ Details ─────────────────────────┐
│ ▼ Root                           │ Name: PageCache                   │
│   ▼ Storage                      │ Type: A.1 Page Cache              │
│     ► PageCache            ◄──   │ Resource Type: Memory, Capacity,  │
│     ► SegmentManager             │   DiskIO, Contention              │
│     ► ChunkAccessorCache         │                                   │
│   ▼ DataEngine                   │ ── Capacity ──────────────────    │
│     ► TransactionPool            │ Current:    185 / 256 pages       │
│     ► ComponentTable<CompA>      │ Utilization: 72.3%                │
│     ► ComponentTable<CompB>      │                                   │
│   ► Durability                   │ ── Memory ────────────────────    │
│   ► Backup                       │ Allocated:  2.0 MB                │
│   ► Execution                    │ Peak:       2.0 MB                │
│   ▼ Allocation                   │                                   │
│     ► MemoryAllocator            │ ── DiskIO ────────────────────    │
│     ► BlockAllocators            │ Reads:  12,847  (102.4 MB)        │
│     ► OccupancyBitmaps           │ Writes: 1,204   (9.6 MB)          │
│                                  │                                   │
│                                  │ ── Contention ────────────────    │
│                                  │ Waits: 23   Total: 145 μs         │
│                                  │ Max:   42 μs  Timeouts: 0         │
└──────────────────────────────────┴───────────────────────────────────┘
 ↑↓ Navigate  →/Enter Expand  ← Collapse  / Filter  q Quit
```

#### Keyboard Controls

| Key | Action |
|-----|--------|
| **↑ / ↓** | Move selection between visible nodes |
| **→ / Enter** | Expand selected node (reveal children) |
| **←** | Collapse selected node (hide children) |
| **Ctrl+→** | Expand all children recursively |
| **Ctrl+←** | Collapse all children recursively |
| **/** | Filter nodes by name (type-ahead search) |
| **Home / End** | Jump to first / last node |
| **Page Up / Down** | Scroll by page |
| **q / Esc** | Exit back to REPL |

#### How It Works

1. The user types `resources` at the REPL prompt (PrettyPrompt handles input)
2. PrettyPrompt yields control after Enter
3. The command handler initializes Terminal.Gui, which switches to the **alternate screen buffer**
4. Terminal.Gui's `TreeView<T>` is populated from `ResourceRegistry.Root` and its children
5. The `SelectionChanged` event updates the right-hand detail pane with the selected node's metrics (read via `IMetricSource.ReadMetrics()`)
6. The user navigates the tree interactively
7. When the user presses `q`, Terminal.Gui exits and restores the normal screen buffer
8. The REPL prompt reappears — scrollback is fully preserved

#### Detail Pane Content

The detail pane adapts to the selected node's metric kinds. Each `IMetricSource` node reports its metrics via `ReadMetrics(IMetricWriter)`, and the detail pane renders whichever kinds are present:

| Metric Kind | What's Shown |
|-------------|-------------|
| **Memory** | Allocated bytes, peak bytes |
| **Capacity** | Current / maximum, utilization percentage |
| **DiskIO** | Read/write ops, read/write bytes |
| **Contention** | Wait count, total/max wait μs, timeouts |
| **Throughput** | Named counters (e.g., "Lookups: 12,847") |
| **Duration** | Last/avg/max μs for named operations |

For structural nodes (pure grouping, like "Storage" or "Root") that don't implement `IMetricSource`, the detail pane shows aggregate info: child count, total memory of subtree, and a list of immediate children with their types.

#### Lazy Loading

Terminal.Gui's `ITreeBuilder<T>` interface supports on-demand child loading. For large trees (many ComponentTables, many indexes), children are loaded when the user expands a node — not all at once. This keeps the initial render fast.

#### Why Full-Screen TUI?

The resource graph (see [Overview 08](../../overview/08-resources.md) §8.5) has 35+ nodes across 6 subsystems with 3+ levels of depth. A static `resources` command printing all nodes as a flat table or even a Spectre.Console `Tree` would be:

1. **Overwhelming** — too much information at once, most of it irrelevant to the current investigation
2. **One-directional** — you can't ask follow-up questions without running another command
3. **Context-free** — seeing "PageCache: 72.3%" doesn't naturally lead you to explore its children or siblings

The interactive navigator solves all three: you see only the expanded nodes, you drill into whatever catches your attention, and the tree structure itself is the navigation context. It's the difference between reading a table of contents and having a file browser.

### `resources --flat` — Non-Interactive Fallback

For batch/pipe mode and CI/CD, a non-interactive flat view is also available:

```
tsh:mydb> resources --flat
  Resource                            Type            Memory    Capacity   Contention
  ──────────────────────              ──────          ──────    ────────   ──────────
  Root                                Node            2.2 MB    —          —
  Root/Storage                        Node            2.1 MB    —          —
  Root/Storage/PageCache              Memory+Cap      2.0 MB    72.3%      23 waits
  Root/Storage/SegmentManager         Cap+Thru        —         —          —
  Root/Storage/SegmentManager/CompA   Memory+Cap      48 KB     72.3%      —
  Root/DataEngine                     Node            128 KB    —          —
  Root/DataEngine/TransactionPool     Cap+Thru+Dur    —         12.5%      —
  ...
```

This is a standard Spectre.Console table — no Terminal.Gui needed. It's the fallback for non-interactive contexts and for users who prefer text output.

## Diagnostic Command Summary

| Command | What It Shows |
|---------|---------------|
| `cache-stats` | Page cache hit rate, state breakdown, eviction count |
| `cache-pages` | Per-page state, segment assignment, dirty flags |
| `cache-watch` | Live cache metrics over time *(post-v1)* |
| `page-dump` | Raw page bytes: structured (header + hex) or `--raw` (pure hex) |
| `segments` | All segments with type, occupancy, page ranges |
| `segment-detail` | Deep dive into one segment's per-page occupancy |
| `btree` | Index statistics: depth, fill factor, key range |
| `btree-dump` | Node-level key/pointer inspection |
| `btree-validate` | Structural integrity verification |
| `revisions` | Entity revision chain with data at each revision |
| `transactions` | Active transactions, MinTick, pool state |
| `mvcc-stats` | Revision distribution, chain lengths, GC candidates |
| `memory` | Memory usage breakdown by subsystem |
| **`resources`** | **Interactive full-screen resource tree explorer (Terminal.Gui TUI)** |
| `resources --flat` | Non-interactive flat resource table (Spectre.Console, CI/CD friendly) |

## Decisions

- [x] **`page-dump` included in v1** — Two modes: `page-dump <N>` (structured view: decoded `PageBaseHeader` + `PageMetadata` fields, then hex+ASCII dump of `PageRawData`) and `page-dump <N> --raw` (pure 8192-byte hex dump for piping to external tools). Trivial to implement (page is already an 8KB buffer in memory), uniquely valuable for engine developers verifying storage layout at the byte level. Verbosity is self-resolving — nobody stumbles into this command accidentally.
- [x] **`cache-watch` is a post-v1 stretch goal** — Requires background thread, Spectre.Console `Live` display cooperating with PrettyPrompt's terminal state, and graceful Ctrl+C cancellation. The static `cache-stats` command covers 95% of the use case. For v1, users can run `cache-stats` repeatedly or use a host shell loop (`while true; do tsh mydb.typhon -c "cache-stats"; sleep 1; done`).
- [x] **`profile` command is post-v1** — The concept (snapshot engine counters → run commands → diff) is sound, but `set timing on` + manual `cache-stats` before/after covers 80% of the use case. When implemented post-v1, it should capture all engine-internal counters (matching the Metrics Catalog in [Overview 09](../../overview/09-observability.md) §9.2), run the commands, and produce a clean diff table.
- [x] **Interactive resource navigator via [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)** — The `resources` command launches a full-screen TUI session using Terminal.Gui's `TreeView<T>` widget. Keyboard-driven navigation (↑↓→←), detail pane updated via `SelectionChanged` event, lazy child loading via `ITreeBuilder<T>`. Terminal.Gui runs sequentially with Spectre.Console/PrettyPrompt using the alternate screen buffer (like `vim`). Spectre.Console was evaluated and rejected for this use case: its `Tree` widget is display-only, its `Live` display prohibits keyboard input, and its prompt internals (`ListPrompt<T>`, `IListPromptStrategy<T>`) are private API. Consolonia (Avalonia-based TUI) was rejected as too heavy and beta quality.
- [x] **Data source: Resource Graph + direct struct access, not the telemetry layer** — The shell and the [Observability layer](../../overview/09-observability.md) are peers — both read from the [Resource Graph](../../overview/08-resources.md) (`IResource` tree + `IMetricSource.ReadMetrics()`), but for different audiences. The shell never goes through the OTel/telemetry layer (Tracks 1-4). Rationale: (1) the telemetry layer is disabled by default for zero overhead — shell diagnostics must work out of the box with no configuration; (2) telemetry provides aggregates over time, while the shell needs current-state snapshots; (3) the shell has full in-process access to the same data telemetry reads from. Two data sources, cleanly separated: **Resource Graph** for aggregate metrics and tree navigation (`resources`, `cache-stats`, `memory` — via `IMetricSource`), **direct struct access** for structural inspection (`btree-dump`, `page-dump`, `revisions`, `segment-detail` — reading internal data structures the resource graph doesn't expose).

## Open Questions

*(None remaining — all resolved.)*

# Phase 2 — Diagnostics & Inspection

**Date:** February 2026
**Status:** Implemented
**Parent:** [Typhon.Shell Design](./README.md)
**Prerequisites:** [Phase 1](./01-phase1-core.md) (REPL, schema loading, session model)

---

> 💡 **TL;DR:** Phase 2 adds engine diagnostics — the shell's killer feature. Page cache, segments, B+Trees, MVCC state, memory usage, and an interactive resource tree explorer. Jump to [§4 (Command Reference)](#4-command-reference) for the full command spec, [§11 (Resource Tree Navigator)](#11-resource-tree-navigator) for the flagship TUI feature, or [§13 (Grammar Extensions)](#13-grammar-extensions) for the PEG additions.

---

## Table of Contents

1. [Overview](#1-overview)
2. [Data Source Architecture](#2-data-source-architecture)
3. [Terminal Ownership Update](#3-terminal-ownership-update)
4. [Command Reference](#4-command-reference)
5. [Page Cache Diagnostics](#5-page-cache-diagnostics)
6. [Segment Diagnostics](#6-segment-diagnostics)
7. [B+Tree Diagnostics](#7-btree-diagnostics)
8. [MVCC & Revision Diagnostics](#8-mvcc--revision-diagnostics)
9. [Memory Diagnostics](#9-memory-diagnostics)
10. [Transaction Pool Diagnostics](#10-transaction-pool-diagnostics)
11. [Resource Tree Navigator](#11-resource-tree-navigator)
12. [Flat Resource View](#12-flat-resource-view)
13. [Grammar Extensions](#13-grammar-extensions)
14. [Phase 2 Decisions](#14-phase-2-decisions)

## 1. Overview

Phase 2 adds the features that make `tsh` uniquely valuable. While any database tool can do CRUD, no other tool can show you Typhon's internal state: page cache hit rates, B+Tree depth, revision chain lengths, segment occupancy, MVCC transaction state, or the full resource graph.

Since the shell hosts the engine in-process, it has direct access to every internal data structure — no remote protocol, no sampling, no approximation. This is the kind of information that otherwise requires attaching a debugger or adding custom instrumentation.

**What Phase 2 includes:**
- Page cache diagnostics (`cache-stats`, `cache-pages`, `page-dump`)
- Segment diagnostics (`segments`, `segment-detail`)
- B+Tree diagnostics (`btree`, `btree-dump`, `btree-validate`)
- MVCC diagnostics (`revisions`, `transactions`, `mvcc-stats`)
- Memory diagnostics (`memory`)
- Interactive resource tree explorer (`resources`)
- Flat resource view (`resources --flat`)

**What Phase 2 does NOT include:**
- `cache-watch` (live monitoring) — deferred to post-v1, requires background thread
- `profile` command — deferred to post-v1, `set timing on` covers 80% of the use case

## 2. Data Source Architecture

Diagnostic commands read from two distinct data sources, chosen based on what information they need:

### Resource Graph (Aggregate Metrics)

The [Resource Graph](../../overview/08-resources.md) is a tree of `IResource` nodes, each optionally implementing `IMetricSource`. The shell reads metrics via `IMetricSource.ReadMetrics()` — the same data the telemetry layer uses, but accessed directly in-process without the OTel pipeline.

**Used by:** `cache-stats`, `memory`, `transactions`, `resources`, `resources --flat`

**Why not telemetry?** The telemetry layer is disabled by default for zero overhead. Shell diagnostics must work out of the box. Additionally, telemetry provides time-aggregated data, while the shell needs current-state snapshots.

### Direct Struct Access (Structural Inspection)

Some diagnostic commands need to inspect internal data structures that the Resource Graph doesn't expose: individual page headers, B+Tree node contents, revision chain entries, per-page occupancy bitmaps.

**Used by:** `cache-pages`, `page-dump`, `segments`, `segment-detail`, `btree`, `btree-dump`, `btree-validate`, `revisions`, `mvcc-stats`

**How:** These commands access engine internals via the `DatabaseEngine` instance — reading `PagedMMF` state, `ChunkBasedSegment` metadata, `BTree` nodes, and `CompRevStorageHeader` entries directly.

### Separation Principle

```
┌─────────────────────────────────┐    ┌─────────────────────────────────┐
│        Resource Graph           │    │      Direct Struct Access       │
│  (aggregate metrics, tree nav)  │    │  (structural inspection)        │
│                                 │    │                                 │
│  cache-stats                    │    │  cache-pages                    │
│  memory                         │    │  page-dump                      │
│  transactions                   │    │  segments, segment-detail       │
│  resources, resources --flat    │    │  btree, btree-dump              │
│                                 │    │  btree-validate                 │
│  IMetricSource.ReadMetrics()    │    │  revisions, mvcc-stats          │
│                                 │    │                                 │
│                                 │    │  Raw pointers / unsafe access   │
└─────────────────────────────────┘    └─────────────────────────────────┘
```

These two sources are **peers** — the shell and the observability layer both read from the Resource Graph, but for different audiences (interactive inspection vs. time-series monitoring).

## 3. Terminal Ownership Update

Phase 2 adds Terminal.Gui to the terminal ownership model:

```
┌─────────────────────────────────────────────────────────────┐
│                  Terminal Ownership Timeline                │
│                                                             │
│  PrettyPrompt          Spectre.Console        Terminal.Gui  │
│  (input phase)         (output phase)         (interactive) │
│                                                             │
│  ┌──────────┐          ┌──────────────┐                     │
│  │ tsh:mydb>│ ──Enter──│ table output │ ──done──► prompt    │
│  └──────────┘          └──────────────┘                     │
│                                                             │
│  ┌──────────┐          ┌───────────────────────┐            │
│  │ tsh:mydb>│ ──Enter──│ Terminal.Gui session  │──q──►prompt│
│  │resources │          │ (alternate screen)    │            │
│  └──────────┘          └───────────────────────┘            │
└─────────────────────────────────────────────────────────────┘
```

Terminal.Gui uses the **alternate screen buffer** (same mechanism as `vim` and `less`). When the `resources` command launches, the REPL scrollback is preserved on the normal screen. When the user exits (`q`), the normal screen is restored and the REPL prompt reappears exactly where it was.

## 4. Command Reference

### Phase 2 Command Summary

| Category | Command | Description |
|----------|---------|-------------|
| **Page Cache** | `cache-stats` | Cache overview: hit rate, state breakdown, evictions |
| | `cache-pages [where ...]` | Per-page state detail, filterable |
| | `page-dump <page> [--raw]` | Raw page bytes: structured or hex |
| **Segments** | `segments` | All segments with occupancy, page ranges |
| | `segment-detail <segment>` | Deep dive into one segment |
| **B+Tree** | `btree <index>` | Index statistics: depth, fill factor, key range |
| | `btree-dump <index> [--level N \| --chunk N]` | Node-level key/pointer inspection |
| | `btree-validate <index>` | Structural integrity verification |
| **MVCC** | `revisions <id> <component>` | Entity revision chain with data |
| | `transactions` | Active transactions, MinTick, pool state |
| | `mvcc-stats <component>` | Revision distribution, chain lengths |
| **Memory** | `memory` | Memory usage breakdown by subsystem |
| **Resources** | `resources` | Interactive full-screen resource tree (TUI) |
| | `resources --flat` | Non-interactive flat resource table |

All diagnostic commands require a database to be open. They do **not** require an active transaction — they read engine state directly.

## 5. Page Cache Diagnostics

### `cache-stats`

Reads from the Resource Graph (`IMetricSource` on the PageCache resource node). Shows aggregate cache metrics.

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

### `cache-pages`

Reads from direct struct access (page cache array). Shows per-page state.

```
tsh:mydb> cache-pages
  Page   State       Segment              Pins  Dirty  ChangeRev
  ────   ─────       ───────              ────  ─────  ─────────
  0      Idle        Header               0     no     1
  1      Idle        CompA.Data           0     no     42
  2      Shared      CompA.Data           2     no     42
  3      Exclusive   CompA.RevTable       1     yes    101
  ...
```

#### Filtering

`cache-pages` supports a simple `where` clause for filtering by state or segment:

```
tsh:mydb> cache-pages where state=dirty
tsh:mydb> cache-pages where segment=CompA.Data
tsh:mydb> cache-pages where state=shared segment=CompA.Data
```

Filters are `AND`-combined when multiple are specified. Valid filter keys:
- `state` — `free`, `idle`, `shared`, `exclusive`, `dirty`
- `segment` — segment name (e.g., `CompA.Data`, `CompA.RevTable`)

### `page-dump`

Reads from direct struct access (raw page buffer). Two modes:

**Structured view (default):**

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
  ...
```

**Raw hex dump (for piping):**

```
tsh:mydb> page-dump 3 --raw
  0000: 03 00 00 00 65 00 00 00  02 00 00 00 01 00 00 00  ...
  (full 8192 bytes)
```

```bash
# Pipe to external tool
tsh mydb.typhon -c "page-dump 3 --raw" | xxd
```

## 6. Segment Diagnostics

### `segments`

Reads from direct struct access (segment manager). Shows all segments with type, size, and occupancy.

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

### `segment-detail`

Deep dive into a specific segment's per-page occupancy.

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
    Page 18:  12.5%  (20/160 chunks used)   <- recently allocated
```

The segment name uses the format `<Component>.<Suffix>` where suffix is one of: `Data`, `RevTable`, `PK_Index`, `<FieldName>_Index`.

## 7. B+Tree Diagnostics

### `btree`

Index-level statistics. The index name uses the format `<Component>.<FieldName>`.

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

### `btree-dump`

Node-level inspection. Two access modes: by tree level or by chunk ID.

**By level:**
```
tsh:mydb> btree-dump CompA.PlayerId --level 0
  Root Node (chunk 23)
  ──────────────────────────────────────
  Keys:     [128, 256, 384, 512, 640, 768, 896]
  Children: [24, 25, 26, 27, 28, 29, 30, 31]
```

**By chunk ID:**
```
tsh:mydb> btree-dump CompA.PlayerId --chunk 42
  Leaf Node (chunk 42)
  ──────────────────────────────────────
  Keys:   [897, 898, 899, ..., 920]
  Values: [chunk:1847, chunk:1848, chunk:1849, ..., chunk:1870]
  Next:   chunk 43
  Prev:   chunk 41
```

### `btree-validate`

Structural integrity verification. Checks sort order, link consistency, pointer validity, and key count agreement with the data segment.

```
tsh:mydb> btree-validate CompA.PlayerId
  Validating B+Tree CompA.PlayerId...
  [1/4] Key sort order...              ok  (1,024 keys)
  [2/4] Leaf node links (fwd+bwd)...   ok  (42 leaves)
  [3/4] Internal node pointers...      ok  (5 internal nodes)
  [4/4] Key count vs data segment...   ok  (1,024 match)

  Validation passed
```

On failure:
```
  [1/4] Key sort order...              FAIL
    Leaf node 42: unsorted keys at position 12 (920 > 919)

  Validation FAILED (1 error)
```

## 8. MVCC & Revision Diagnostics

### `revisions`

Shows the revision chain for a specific entity/component pair. Reads directly from the `CompRevStorageHeader` and revision chain chunks.

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

### `transactions`

Shows active transaction state. Reads from the Resource Graph (TransactionPool metrics) and the TransactionChain.

```
tsh:mydb> transactions
  Active Transactions
  ──────────────────────────────────────
  Active:    1
  MinTick:   200
  Pool size: 8 available

  Tick  Age        Operations  Dirty
  ────  ───        ──────────  ─────
  200   2.3s       0           no       <- current shell transaction
```

### `mvcc-stats`

Aggregate MVCC statistics for a component type. Reads from direct struct access (iterating revision chain headers).

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

## 9. Memory Diagnostics

### `memory`

Memory usage breakdown. Reads from the Resource Graph (Memory metric kind across all nodes).

```
tsh:mydb> memory
  Memory Usage
  ──────────────────────────────────────
  Page cache:      2.0 MB  (256 x 8 KB)
  Transaction pool: 128 KB
  Chunk accessors:  64 KB
  B+Tree caches:    32 KB
  Total managed:    ~2.2 MB
```

## 10. Transaction Pool Diagnostics

The `transactions` command (§8) already covers pool state. Additional detail is available via the `resources` navigator (Phase 2, §11), which shows the TransactionPool resource node with its full metric breakdown.

## 11. Resource Tree Navigator

### Overview

The `resources` command is the flagship diagnostic feature. It launches a **full-screen interactive TUI** (powered by [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)) for exploring the engine's [Resource Graph](../../overview/08-resources.md) via keyboard-driven drill-down navigation.

This is fundamentally different from the other diagnostic commands. Instead of asking specific questions (`cache-stats`, `btree CompA.PlayerId`), you **discover** the engine's internals by navigating the resource tree — seeing what exists, how it's structured, and what each node's current state looks like.

### Layout

```
┌─ Resource Tree ──────────────────┬─ Details ─────────────────────────┐
│ > Root                           │ Name: PageCache                   │
│   > Storage                      │ Type: A.1 Page Cache              │
│     > PageCache            <--   │ Resource Type: Memory, Capacity,  │
│     > SegmentManager             │   DiskIO, Contention              │
│     > ChunkAccessorCache         │                                   │
│   > DataEngine                   │ -- Capacity ──────────────────    │
│     > TransactionPool            │ Current:    185 / 256 pages       │
│     > ComponentTable<CompA>      │ Utilization: 72.3%                │
│     > ComponentTable<CompB>      │                                   │
│   > Durability                   │ -- Memory ────────────────────    │
│   > Backup                       │ Allocated:  2.0 MB                │
│   > Execution                    │ Peak:       2.0 MB                │
│   > Allocation                   │                                   │
│     > MemoryAllocator            │ -- DiskIO ────────────────────    │
│     > BlockAllocators            │ Reads:  12,847  (102.4 MB)        │
│     > OccupancyBitmaps           │ Writes: 1,204   (9.6 MB)          │
│                                  │                                   │
│                                  │ -- Contention ────────────────    │
│                                  │ Waits: 23   Total: 145 us         │
│                                  │ Max:   42 us  Timeouts: 0         │
└──────────────────────────────────┴───────────────────────────────────┘
 Up/Down Navigate  Right/Enter Expand  Left Collapse  / Filter  q Quit
```

### Keyboard Controls

| Key | Action |
|-----|--------|
| **Up / Down** | Move selection between visible nodes |
| **Right / Enter** | Expand selected node (reveal children) |
| **Left** | Collapse selected node (hide children) |
| **Ctrl+Right** | Expand all children recursively |
| **Ctrl+Left** | Collapse all children recursively |
| **/** | Filter nodes by name (type-ahead search) |
| **Home / End** | Jump to first / last node |
| **Page Up / Down** | Scroll by page |
| **q / Esc** | Exit back to REPL |

### Terminal.Gui Integration

**Lifecycle:**

1. User types `resources` at the REPL prompt (PrettyPrompt handles input)
2. PrettyPrompt yields control after Enter
3. Command handler initializes Terminal.Gui → switches to **alternate screen buffer**
4. Terminal.Gui's `TreeView<T>` is populated from `ResourceRegistry.Root` and children
5. `SelectionChanged` event updates the detail pane with selected node's metrics
6. User navigates interactively
7. User presses `q` → Terminal.Gui exits, restores normal screen buffer
8. REPL prompt reappears — scrollback fully preserved

**Key implementation detail:** Terminal.Gui and PrettyPrompt/Spectre.Console never run simultaneously. Terminal.Gui uses the alternate screen buffer, ensuring the REPL scrollback is unaffected.

### Detail Pane Content

The detail pane adapts to the selected node's metric kinds. Each `IMetricSource` reports metrics via `ReadMetrics(IMetricWriter)`:

| Metric Kind | What's Shown |
|-------------|-------------|
| **Memory** | Allocated bytes, peak bytes |
| **Capacity** | Current / maximum, utilization percentage |
| **DiskIO** | Read/write ops, read/write bytes |
| **Contention** | Wait count, total/max wait time, timeouts |
| **Throughput** | Named counters (e.g., "Lookups: 12,847") |
| **Duration** | Last/avg/max for named operations |

For structural nodes (pure grouping, like "Storage" or "Root") that don't implement `IMetricSource`, the detail pane shows: child count, total memory of subtree, list of immediate children with types.

### Lazy Loading

Terminal.Gui's `ITreeBuilder<T>` supports on-demand child loading. For large trees (many ComponentTables, many indexes), children are loaded when the user expands a node — not all at once. This keeps the initial render fast.

## 12. Flat Resource View

### `resources --flat`

Non-interactive fallback for batch/pipe mode and CI/CD. Standard Spectre.Console table — no Terminal.Gui needed.

```
tsh:mydb> resources --flat
  Resource                            Type            Memory    Capacity   Contention
  ──────────────────────              ──────          ──────    ────────   ──────────
  Root                                Node            2.2 MB    --         --
  Root/Storage                        Node            2.1 MB    --         --
  Root/Storage/PageCache              Memory+Cap      2.0 MB    72.3%      23 waits
  Root/Storage/SegmentManager         Cap+Thru        --        --         --
  Root/Storage/SegmentManager/CompA   Memory+Cap      48 KB     72.3%      --
  Root/DataEngine                     Node            128 KB    --         --
  Root/DataEngine/TransactionPool     Cap+Thru+Dur    --        12.5%      --
  ...
```

In batch mode (`tsh mydb.typhon -c "resources --flat"`), this is the only mode — Terminal.Gui interactive sessions are suppressed when stdin is not a terminal.

## 13. Grammar Extensions

Phase 2 extends the Phase 1 PEG grammar with diagnostic commands. The core tokenization and value rules are inherited unchanged.

```peg
# ═══════════════════════════════════════════════════════════
# Typhon Shell — Phase 2 Grammar Extensions
# ═══════════════════════════════════════════════════════════

# Extends the Command rule from Phase 1:
#   Command <- DatabaseCmd / SchemaCmd / TransactionCmd
#            / DataCmd / DiagnosticCmd / ShellCmd

# ── Diagnostic Commands ────────────────────────────────────

DiagnosticCmd   <- CacheStatsCmd / CachePagesCmd / PageDumpCmd
                 / SegmentsCmd / SegmentDetailCmd
                 / BtreeCmd / BtreeDumpCmd / BtreeValidateCmd
                 / RevisionsCmd / TransactionsCmd / MvccStatsCmd
                 / MemoryCmd / ResourcesCmd

# ── Page Cache ─────────────────────────────────────────────

CacheStatsCmd   <- 'cache-stats'

CachePagesCmd   <- 'cache-pages' (WS 'where' WS CacheFilter
                   (WS CacheFilter)*)?
CacheFilter     <- CacheStateFilter / CacheSegmentFilter
CacheStateFilter   <- 'state' WS? '=' WS? PageState
CacheSegmentFilter <- 'segment' WS? '=' WS? SegmentName
PageState       <- 'free' / 'allocating' / 'idle' / 'exclusive'

PageDumpCmd     <- 'page-dump' WS UnsignedInt (WS '--raw')?

# ── Segments ───────────────────────────────────────────────

SegmentsCmd     <- 'segments'
SegmentDetailCmd <- 'segment-detail' WS SegmentName

# ── B+Tree ─────────────────────────────────────────────────

BtreeCmd        <- 'btree' WS IndexName
BtreeDumpCmd    <- 'btree-dump' WS IndexName
                   (WS BtreeDumpOption)?
BtreeDumpOption <- '--level' WS UnsignedInt
                 / '--chunk' WS UnsignedInt
BtreeValidateCmd <- 'btree-validate' WS IndexName

# ── MVCC ───────────────────────────────────────────────────

RevisionsCmd    <- 'revisions' WS EntityId WS ComponentName
TransactionsCmd <- 'transactions'
MvccStatsCmd    <- 'mvcc-stats' WS ComponentName

# ── Memory ─────────────────────────────────────────────────

MemoryCmd       <- 'memory'

# ── Resources ──────────────────────────────────────────────

ResourcesCmd    <- 'resources' (WS '--flat')?

# ── Shared Terminals ───────────────────────────────────────

SegmentName     <- ComponentName '.' Identifier
                                                # e.g., CompA.Data, CompA.PK_Index
IndexName       <- ComponentName '.' Identifier
                                                # e.g., CompA.PlayerId
```

### Grammar Notes

1. **`cache-pages where` uses keyword filtering, not the query `where` clause.** The filter syntax is simple key=value pairs, not arbitrary expressions. This is intentional — `cache-pages` filters on known page attributes, not user-defined fields.
2. **`SegmentName` and `IndexName` share the same grammar** (`Component.Field`) but are resolved differently at runtime: segment names map to storage segments, index names map to B+Tree instances.
3. **`resources` without `--flat`** launches the Terminal.Gui interactive session. In non-interactive mode (pipe, `-c`, `--exec`), `resources` without `--flat` is an error — the user must explicitly use `--flat`.

## 14. Phase 2 Decisions

### Resolved

- [x] **Data source: Resource Graph + direct struct access** — Two data sources, cleanly separated. Resource Graph for aggregate metrics and tree navigation. Direct struct access for structural inspection. Shell never goes through the OTel/telemetry layer.
- [x] **`page-dump` included in v1** — Two modes: structured view (decoded headers + hex dump) and `--raw` (pure hex for piping). Trivial to implement — the page is already an 8KB buffer in memory.
- [x] **`cache-watch` deferred to post-v1** — Requires background thread, Spectre.Console `Live` display, and Ctrl+C cancellation. Static `cache-stats` covers 95% of the use case.
- [x] **`profile` deferred to post-v1** — `set timing on` + manual `cache-stats` before/after covers 80% of the use case.
- [x] **Interactive TUI via Terminal.Gui** — `resources` launches a full-screen session using `TreeView<T>`. Terminal.Gui runs sequentially with Spectre.Console/PrettyPrompt via alternate screen buffer.
- [x] **`resources` in non-interactive mode** — Error without `--flat`. Prevents Terminal.Gui from being launched in pipe/batch mode where it can't work.
- [x] **`cache-pages` filter syntax: simple key=value** — Not reusing query `where` clause grammar. The filter applies to page attributes (state, segment), not user-defined fields. Simple and sufficient.
- [x] **`btree-validate` scope: structural only** — Validates sort order, links, pointers, key count. Does not validate data integrity (e.g., whether pointed-to revision chains are valid). Full cross-reference validation is a post-v1 `verify` command responsibility.

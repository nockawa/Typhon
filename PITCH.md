# Typhon — The Real-Time ACID Database Engine

**Full ACID transactions. Microsecond latency. Zero compromises.**

---

## The Problem

Every game server, every simulation, every real-time system faces the same impossible choice:

**Option A: Use a real database.** Get transactions, crash recovery, and data integrity — but pay the price in latency. Network round-trips to PostgreSQL or Redis add milliseconds you don't have. A 60fps game tick is 16.6ms. A round-trip to your database just ate half of it.

**Option B: Roll your own.** Keep state in memory, serialize to disk when you can, pray nothing crashes between saves. You'll ship faster, but you'll also ship bugs: lost inventory, duplicated currency, corrupted world state. Every online game has war stories. Every player remembers.

**Option C: Use an embedded database.** SQLite, LevelDB, RocksDB — fast, local, no network. But none of them were designed for your data model. You'll spend months wrapping ECS components into relational tables, fighting impedance mismatch, and discovering that "embedded" doesn't mean "real-time."

There is no Option D. Until now.

---

## What Is Typhon?

Typhon is an **embedded ACID database engine** that runs inside your process, speaks your data model natively, and delivers **microsecond-level operations** with full transactional guarantees.

It is not a general-purpose SQL database. It is not a key-value store with transactions bolted on. It is a purpose-built engine for systems where **every microsecond matters and data loss is unacceptable**.

```
┌─────────────────────────────────────────────┐
│              Your Application                │
│                                              │
│   Game Server · Simulation · Trading Engine  │
│                                              │
│  ┌─────────────────────────────────────────┐ │
│  │            Typhon Engine                 │ │
│  │                                          │ │
│  │  MVCC Snapshot Isolation · B+Tree Index  │ │
│  │  WAL Durability · Crash Recovery         │ │
│  │  Zero-Copy Reads · Cache-Line Aligned    │ │
│  └─────────────────────────────────────────┘ │
│              ▼            ▼                   │
│         Memory-Mapped Storage Files           │
└─────────────────────────────────────────────┘
        No network. No serialization.
           No separate process.
```

### The Core Idea

Your data lives as **components** — small, typed, fixed-size structs attached to **entities**. If you've ever used Unity ECS, Bevy, or Flecs, this is your native language. If you haven't, think of it as a database where every "row" is an entity ID and every "column group" is a component type — but stored in cache-optimized columnar layout, not row-oriented tables.

```csharp
// Define your data as plain structs
[Component]
public struct Position { public float X, Y, Z; }

[Component]
public struct Health { public int Current, Max; }

[Component]
public struct Inventory { public int Slots, Gold; }

// Use it with full ACID transactions
using var uow = engine.CreateUnitOfWork(DurabilityMode.GroupCommit);
var tx = uow.CreateTransaction();

var entity = tx.CreateEntity(ref position);
tx.CreateComponent(entity, ref health);
tx.CreateComponent(entity, ref inventory);

tx.Commit();  // Atomic. Durable. Done.
```

No ORM. No mapping layer. No serialization. Your struct *is* the storage format.

---

## Why Typhon Is a Game Changer

### 1. Microsecond Operations, Not Millisecond

Typhon doesn't just aim for "fast." It is engineered at the hardware level for predictable, microsecond-latency operations:

| Technique | What It Does |
|-----------|-------------|
| **Memory-mapped I/O** | Pages live in your address space. Reads are pointer dereferences, not syscalls |
| **Blittable zero-copy components** | Your struct *is* the on-disk format. No serialize/deserialize step |
| **SIMD-accelerated chunk search** | Vector256 parallel comparisons find cached data in ~10 CPU cycles |
| **128-byte ACLP-aware B+Tree nodes** | Adjacent Cache Line Prefetch fetches the second cache line for free |
| **Lock-free MVCC reads** | Readers never acquire locks. Period. Not even latches |
| **Epoch-based resource protection** | 2 operations per transaction scope, not 2N per resource access |
| **Magic-multiplier fast division** | 3-4 CPU cycles instead of 20-80 for segment addressing |
| **Hardware CRC32C checksums** | SSE4.2-accelerated page integrity verification in ~0.4µs per 8KB page |

This isn't theoretical. Every layer — from page cache eviction (clock-sweep, O(1)) to WAL serialization (lock-free MPSC ring buffer) to B+Tree traversal (latch-coupling with ~50-500ns hold times) — is designed around **cache lines, not abstractions**.

### 2. Real ACID, Not "Pretty Close"

Typhon provides genuine ACID guarantees, not approximations:

- **Atomicity:** Transactions commit entirely or not at all. UnitOfWork boundaries ensure even groups of transactions are atomic
- **Consistency:** Schema validation, typed components, constraint enforcement at commit time
- **Isolation:** MVCC snapshot isolation — every transaction sees a consistent point-in-time view of the database. No dirty reads, no phantom reads, no read skew
- **Durability:** Write-Ahead Log with configurable modes, Full-Page Images for torn write protection, CRC32C checksums for bit-rot detection, sub-100ms crash recovery

And the isolation model makes **deadlocks impossible by construction** — not detected-and-retried, but *structurally impossible*:

1. MVCC means readers never acquire locks
2. Latch-coupling on B+Trees enforces strict top-down ordering
3. No cross-table latch holding during transactions

### 3. You Choose Your Durability Cost

Not every write is equally important. Typhon lets you choose **per Unit of Work**:

| Mode | Latency | Data Risk | Use Case |
|------|---------|-----------|----------|
| **Immediate** | ~15-85µs | Zero after commit returns | Financial trades, audit logs |
| **Group Commit** | ~1µs | Up to one flush interval (default 5ms) | Game ticks, real-time simulation |
| **Deferred** | ~1µs | Until explicit flush | Bulk imports, analytics, testing |

A single Typhon database can serve all three modes simultaneously. Your player authentication transaction gets Immediate durability while your physics state update uses Group Commit — same engine, same data, same ACID guarantees, different cost profiles.

### 4. Concurrent Indexing That Scales

Typhon 1.0 implements **Optimistic Lock Coupling (OLC)** on its B+Tree indexes — the same technique used by research databases targeting 256+ core machines:

- **Readers are completely lock-free.** They read a version counter, traverse the node, then verify the counter hasn't changed. No CAS, no latch, no contention
- **Writers latch only the nodes they modify** — typically 1-3 nodes for an insert, not the entire tree
- **Compound Move operations** eliminate redundant tree traversals when updating indexed fields — saving 200-600ns per field change

The result: linear throughput scaling as you add cores, with no cliff where "the whole tree is locked."

### 5. An ECS-Native Data Model

If your application already thinks in entities and components, Typhon speaks your language. But even if it doesn't, the ECS model brings powerful structural advantages:

- **Compositional data.** Attach any combination of components to any entity. No rigid table schemas. Add a `Poisoned` component to an entity? Just attach it. Remove it when the effect ends. No NULL columns, no sparse tables
- **Cache-friendly bulk processing.** All `Position` components are stored contiguously. Iterating over every position in the world is a sequential memory scan, not a random-access chase through row pointers
- **Secondary indexes with MVCC versioning.** Mark a field as indexed, and Typhon maintains a concurrent B+Tree that respects transaction isolation. Historical index entries are preserved in HEAD/TAIL buffers for temporal consistency
- **Schema evolution without downtime.** Add fields, remove fields, change defaults — Typhon migrates existing data automatically while preserving entity identity and chunk allocation

### 6. Query Engine with Persistent Views

Typhon's query engine is designed around **index-first execution** — no table scans, ever:

```csharp
var view = engine.CreateView<Position, Health>()
    .Where((pos, hp) => pos.X > 100 && hp.Current < hp.Max / 2)
    .Build();

// First execution: index-driven pipeline, most selective predicate first
var results = view.Execute(tx);

// Subsequent executions: incremental delta tracking
// Only re-evaluates entities that actually changed
var updated = view.Refresh(tx);  // O(changed) not O(total)
```

**Persistent views** maintain cached entity sets with field-granular invalidation. When an entity's `Health.Current` changes, only views that filter on `Health.Current` are updated. Views filtering on `Position.X` are untouched. This delivers **up to 50,000x faster updates** compared to full re-query — making real-time reactive queries practical even at scale.

### 7. Production-Grade Crash Safety

Typhon implements a complete durability stack — not a "just fsync and hope" approach:

```
Write Path:
  Transaction Commit
    → WAL Record (logical, 32-288 bytes, not 8KB page copies)
    → Lock-Free MPSC Ring Buffer (zero contention between writers)
    → WAL Writer Thread (FUA guarantee before ack)
    → Done. Sub-100µs.

Background:
  Checkpoint Manager (every ~30s)
    → Seqlock-consistent page snapshots (zero overhead on write path)
    → CRC32C verification per page
    → Dirty page flush to data files
    → WAL segment recycling

Crash Recovery:
  1. Read UoW Registry (which transactions were committed?)
  2. Scan WAL for committed records
  3. Replay forward (apply committed changes)
  4. Repair torn pages from Full-Page Images
  5. Void incomplete epochs
  6. Ready. Sub-100ms total.
```

**Full-Page Images** capture the pre-modification state of a page the first time it's dirtied after a checkpoint. If a crash tears a page mid-write, the FPI restores it to a known-good state before WAL replay proceeds. This is the same strategy used by PostgreSQL — but with logical (not physical) WAL records that are **10-50x smaller** for typical ECS workloads.

### 8. Incremental Backup Without Downtime

Typhon's backup system runs while the database is fully operational:

- **Forward incremental backups** — only changed pages are captured (O(changed) I/O, not O(total))
- **Scoped Copy-on-Write** — a shadow buffer captures pre-modification state only for pages dirtied during backup (~10% memory overhead)
- **Self-contained `.pack` files** — each backup is a complete unit with page index, CRC verification, and optional LZ4 compression
- **Chain compaction** — weekly compaction bounds chain depth; restore walks newest-first for O(DB size) recovery

At scale: a 100GB database with 10% daily churn produces ~15GB incremental backups, restores in ~50 seconds, and never blocks a single transaction.

### 9. Zero-Overhead Observability

Telemetry that costs nothing when you don't need it:

```csharp
// This field is static readonly — JIT evaluates it once at startup
if (TelemetryConfig.BTreeActive)
{
    span = TyphonActivitySource.StartActivity("BTree.Insert");
    span?.SetTag("index.name", indexName);
}
// When BTreeActive is false, JIT eliminates the entire block.
// Not "branch not taken." ELIMINATED. The instructions don't exist.
```

Four independent telemetry tracks with different cost profiles:
- **Track 1:** Static readonly guards → JIT dead-code elimination (zero overhead)
- **Track 2:** DI-injectable options → cold-path configuration only
- **Track 3:** Deep diagnostics → lock history, contention analysis
- **Track 4:** Per-resource callbacks → enable telemetry on individual indexes or page pools

Full OpenTelemetry integration (traces, metrics, health checks) through a single OTLP endpoint. Use Jaeger, Prometheus, Grafana, SigNoz — or anything else that speaks OTLP.

---

## Who Is Typhon For?

### Game Servers

You're running a persistent online world. Thousands of entities with positions, health, inventory, quest state. You need to:
- Update 100K+ components per tick at 20-60Hz
- Never lose a player's inventory to a crash
- Query "all enemies within 50m with health below 50%" in microseconds
- Handle concurrent player sessions without deadlocks

Typhon was built for exactly this. The ECS model *is* your data model. MVCC means your game loop reads never block your persistence writes. Group Commit gives you durability within a single tick. And when the server crashes at 3 AM, crash recovery takes less than 100ms.

### Simulations & Digital Twins

Large-scale simulations with millions of entities, each with multiple state components. You need:
- Snapshot isolation so analysis threads see consistent state while simulation advances
- Temporal queries to answer "what was the state 1000 ticks ago?"
- Incremental views that update reactively as entities change
- Schema evolution as your simulation model matures

Typhon's MVCC gives you free consistent snapshots. Revision chains *are* your history — temporal queries come nearly free. Persistent views with delta tracking make reactive queries practical at scale.

### Financial & Trading Systems

High-frequency data with strict durability requirements. You need:
- Sub-millisecond transaction latency
- Immediate durability for trades, deferred for market data
- Audit trail (temporal queries on revision history)
- Embedded deployment (no external database dependency)

Typhon's mixed durability modes let you run both workloads in the same engine. Immediate mode gives you FUA-guaranteed durability. The logical WAL keeps record sizes small. And the embedded model means your trading engine has no network dependency.

### IoT & Edge Computing

Resource-constrained environments processing sensor data. You need:
- In-process database with minimal footprint
- Configurable memory budgets with back-pressure (not OOM crashes)
- Incremental backups over limited bandwidth
- Crash recovery without manual intervention

Typhon's resource management system enforces memory budgets with graceful back-pressure. Forward incremental backups minimize transfer size. And crash recovery is fully automatic.

---

## The Architecture at a Glance

```
┌──────────────────────────────────────────────────────────────┐
│                        Application                            │
├──────────────────────────────────────────────────────────────┤
│  DatabaseEngine                                               │
│  ├─ UnitOfWork (durability boundary)                         │
│  │  └─ Transaction (MVCC snapshot)                           │
│  │     ├─ CreateEntity / ReadEntity / UpdateEntity            │
│  │     ├─ CreateComponent / RemoveComponent                   │
│  │     └─ Query / View (index-driven, incremental)           │
│  ├─ ComponentTable (per-type storage + indexes)              │
│  │  ├─ ChunkBasedSegment (SOA, SIMD-optimized)              │
│  │  ├─ B+Tree Primary Index (128-byte OLC nodes)            │
│  │  └─ B+Tree Secondary Indexes (versioned HEAD/TAIL)       │
│  ├─ PagedMMF (memory-mapped, 8KB pages, clock-sweep cache)  │
│  ├─ WAL (MPSC ring buffer → FUA writer → crash recovery)    │
│  ├─ Checkpoint Manager (background flush, WAL recycling)     │
│  ├─ Backup Engine (forward incremental, scoped CoW)         │
│  ├─ Resource System (budgets, back-pressure, health checks)  │
│  └─ Observability (OTLP traces + metrics, zero-overhead)    │
├──────────────────────────────────────────────────────────────┤
│  Concurrency Primitives                                       │
│  ├─ AccessControl (64-bit atomic RW lock, writer-preferring) │
│  ├─ AccessControlSmall (32-bit, per B+Tree node)             │
│  ├─ EpochManager / EpochGuard (scope-based protection)       │
│  ├─ OlcLatch (optimistic lock coupling for indexes)          │
│  └─ AdaptiveWaiter (spin → yield → sleep)                    │
├──────────────────────────────────────────────────────────────┤
│  Storage Files (memory-mapped, CRC32C verified)               │
└──────────────────────────────────────────────────────────────┘
```

---

## What Makes Typhon Different — A Comparison

|  | Traditional DB (PostgreSQL) | Embedded KV (RocksDB) | In-Memory (Redis) | **Typhon** |
|--|---|---|---|---|
| **Deployment** | Separate server | Library | Separate server | **Library (in-process)** |
| **Latency** | Milliseconds (network) | Microseconds (disk) | Microseconds (network) | **Microseconds (memory-mapped)** |
| **ACID** | Full | Limited | None | **Full** |
| **Data Model** | Relational | Key-Value | Key-Value | **ECS (Entity-Component)** |
| **Concurrency** | Lock-based + MVCC | Single-writer | Single-threaded | **MVCC + OLC (lock-free reads)** |
| **Durability** | One mode | Configurable | Optional | **Per-UoW configurable** |
| **Crash Recovery** | WAL replay | WAL replay | AOF/RDB | **WAL + FPI + UoW registry** |
| **Schema Evolution** | ALTER TABLE | N/A | N/A | **Automatic migration** |
| **Observability** | External tools | Minimal | Minimal | **Built-in OTLP (zero-overhead)** |
| **Temporal Queries** | Extension (pg_temporal) | No | No | **Native (revision chains)** |
| **Deadlocks** | Detected & retried | N/A | N/A | **Impossible by construction** |

---

## The 1.0 Feature Set

Typhon 1.0 delivers a complete, production-ready embedded database engine:

### Core Engine
- [x] ECS data model with blittable component storage
- [x] MVCC snapshot isolation with optimistic conflict detection
- [x] Multi-component entities with automatic lifecycle management
- [x] Schema versioning and automatic evolution with migration functions

### Indexing
- [x] Specialized B+Tree variants (16/32/64-bit keys, String64 keys)
- [x] 128-byte cache-line-aligned nodes with ACLP optimization
- [x] Optimistic Lock Coupling for concurrent readers and writers
- [x] Versioned secondary indexes with HEAD/TAIL MVCC buffers
- [x] Compound Move operations for efficient field updates
- [x] Leaf-level enumeration for range scans

### Durability & Recovery
- [x] Write-Ahead Log with lock-free MPSC ring buffer
- [x] Three durability modes (Immediate / Group Commit / Deferred)
- [x] Full-Page Images for torn write protection
- [x] CRC32C hardware-accelerated page checksums
- [x] Checkpoint Manager with background dirty page flush
- [x] Sub-100ms crash recovery with WAL replay

### Query System
- [x] Index-first streaming query pipeline
- [x] Selectivity-driven execution planning
- [x] Persistent views with field-granular incremental updates
- [x] Navigation joins via entity ID references

### Backup & Restore
- [x] Online incremental backup (no downtime)
- [x] Forward-chained incremental snapshots with dirty bitmap tracking
- [x] Scoped Copy-on-Write shadow buffers
- [x] LZ4 page-level compression
- [x] Chain compaction with configurable retention policies

### Storage Engine
- [x] Memory-mapped file I/O with 8KB pages
- [x] Clock-sweep page cache eviction
- [x] ChunkBasedSegment with SIMD-accelerated search
- [x] Epoch-based resource protection (no per-page ref counting)
- [x] Resource budgets with graceful back-pressure

### Observability
- [x] OpenTelemetry integration (traces, metrics, health checks)
- [x] Zero-overhead telemetry via JIT dead-code elimination
- [x] Four independent telemetry tracks (static guards → deep diagnostics)
- [x] Per-resource contention monitoring
- [x] Runtime resource graph with cascade failure detection

### Concurrency
- [x] Deadlock-free by construction (MVCC + latch-coupling + no cross-table holding)
- [x] 64-bit atomic AccessControl with writer-preferring fairness
- [x] Epoch-based scope protection replacing reference counting
- [x] Adaptive spin-wait with zero-allocation contention handling
- [x] Deadline-based timeout composition throughout the stack

### Developer Experience
- [x] Typhon Shell (tsh) — interactive REPL with diagnostics
- [x] Fluent C# API with compile-time type safety
- [x] Automatic secondary index maintenance
- [x] Comprehensive error model with transient/fatal classification

---

## Beyond 1.0: The Roadmap

Typhon's architecture is designed to grow. The MVCC revision chains and ECS model unlock features that would require fundamental redesigns in traditional databases:

**Temporal Queries** — Typhon already stores every version of every component. Exposing `ReadEntityAtVersion(entityId, timestamp)` and `GetRevisionHistory(entityId)` is a thin API layer on top of existing infrastructure. Time-travel debugging, audit trails, anti-cheat replay — nearly free.

**Spatial Indexing** — Uniform grid indexes for "all entities within 50m" queries, graduating to octree indexes for 3D worlds with variable density. First-class spatial queries for game servers.

**Latch-Free Leaf Updates** — Phase 3 of the OLC roadmap: CAS-based leaf node modifications inspired by the FB+-tree paper, eliminating write latches entirely for non-structural changes.

**Async-Aware UnitOfWork** — `async/await` integration at the API boundary while keeping the engine internals synchronous. One request = one UoW, whether that request comes from a game loop, a web API, or an IoT handler.

---

## The Bottom Line

Typhon exists because the real-time systems of 2025 deserve better than the databases of 2005.

Game servers shouldn't have to choose between "fast" and "correct." Trading engines shouldn't have to run a separate PostgreSQL instance for the 5% of writes that need durability. Simulations shouldn't have to sacrifice transactional consistency to hit their tick rate.

**Typhon is a microsecond-latency ACID database engine that speaks your data model, runs in your process, and never makes you choose between performance and correctness.**

It's not an incremental improvement. It's a different answer to the question: *what would a database look like if it was designed for real-time systems from the ground up?*

---

*Typhon is open source, written in C#/.NET 8+, and targets Windows and Linux.*
*[GitHub](https://github.com/nockawa/Typhon) · MIT License*

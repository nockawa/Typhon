# Typhon Architecture Overview

This directory provides a **complete architectural overview** of the Typhon database engine - from high-level components down to implementation details.

---

## What is Typhon?

**Typhon** is an embedded, real-time ACID database engine with microsecond-level performance targets. It combines:

- **Entity-Component-System (ECS) data model** - Entities are just IDs; data lives in typed, blittable component structs
- **MVCC concurrency** - Snapshot isolation with optimistic conflict detection, readers never block writers
- **Persistent B+Tree indexes** - Automatic secondary indexes on component fields
- **Memory-mapped storage** - Clock-sweep page cache with configurable durability

### Who is it for?

Typhon is designed for workloads that need:

| Workload | Why Typhon Fits |
|----------|-----------------|
| **Game servers** | High-frequency updates (100K+ ops/sec), ECS-native model, transactional safety for player state |
| **Simulations** | MVCC allows concurrent reads during world updates, incremental view maintenance |
| **Real-time systems** | Microsecond reads, predictable latency, no GC pressure from data storage |
| **Embedded applications** | No separate server, runs in-process, single-file storage |

### What it's NOT

- Not a general-purpose SQL database (no joins in the traditional sense - uses entity references)
- Not designed for cloud/distributed deployments (single-node embedded)
- Not optimized for analytical/OLAP workloads (focuses on OLTP patterns)

---

## Typhon 101: Quick Start

### Define a Component

```csharp
[Component]
public struct PlayerComponent
{
    [Field]
    public String64 Name;      // Fixed-size string (max 64 bytes)

    [Field]
    [Index]                    // Creates B+Tree for fast lookups
    public int AccountId;

    [Field]
    public float Experience;
}
```

**Key rules**: Components must be unmanaged structs (no strings, arrays, or class references). Use `String64` for short text.

### Basic Operations

```csharp
// Setup (once at startup)
using var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponent<PlayerComponent>();

// Create (using UnitOfWork → Transaction pattern)
using (var uow = dbe.CreateUnitOfWork(TimeSpan.FromSeconds(5), DurabilityMode.Immediate))
{
    using var t = uow.CreateTransaction();
    var player = new PlayerComponent { Name = "Alice", AccountId = 123, Experience = 0 };
    long entityId = t.CreateEntity(ref player);
    t.Commit();  // Durable on return (Immediate mode)
}

// Read
using (var uow = dbe.CreateUnitOfWork(TimeSpan.FromSeconds(1)))
{
    using var t = uow.CreateTransaction();
    if (t.ReadEntity(entityId, out PlayerComponent player))
        Console.WriteLine($"Player: {player.Name}, XP: {player.Experience}");
}

// Update (batch with deferred durability)
using (var uow = dbe.CreateUnitOfWork(TimeSpan.FromSeconds(5), DurabilityMode.Deferred))
{
    using var t = uow.CreateTransaction();
    if (t.ReadEntity(entityId, out PlayerComponent player))
    {
        player.Experience += 100;
        t.UpdateEntity(entityId, player);
        t.Commit();
    }
    await uow.FlushAsync();  // One FUA for all changes
}

// Query by index
using (var uow = dbe.CreateUnitOfWork(TimeSpan.FromSeconds(1)))
{
    using var t = uow.CreateTransaction();
    var index = t.GetIndex<PlayerComponent, int>(nameof(PlayerComponent.AccountId));
    foreach (var id in index.Get(123))
    {
        t.ReadEntity(id, out PlayerComponent p);
        Console.WriteLine($"Found: {p.Name}");
    }
}
```

### Multi-Component Entities

An entity can have multiple component types attached:

```csharp
using var uow = dbe.CreateUnitOfWork(TimeSpan.FromSeconds(5), DurabilityMode.Immediate);
using var t = uow.CreateTransaction();

// Entity 42 has both PlayerComponent and InventoryComponent
t.CreateEntity(ref player);           // Returns entityId = 42
t.CreateComponent(42, ref inventory); // Attach another component
t.CreateComponent(42, ref health);    // And another
t.Commit();

// Read any component by entity ID (new transaction, same UoW)
using var t2 = uow.CreateTransaction();
t2.ReadEntity(42, out PlayerComponent p);
t2.ReadEntity(42, out InventoryComponent i);
```

### Transaction Guarantees

- **Atomicity** - All changes in a transaction succeed or all fail
- **Isolation** - Snapshot isolation; you see a consistent view from transaction start
- **Durability** - Configurable per-UnitOfWork (Deferred / GroupCommit / Immediate), with per-transaction override
- **Conflict Resolution** - Default "last write wins", or provide custom merge logic

---

## Current Capabilities vs. Roadmap

### What Works Today

| Feature | Status | Notes |
|---------|--------|-------|
| Component CRUD | ✅ Solid | Create, Read, Update, Delete within transactions |
| MVCC Transactions | ✅ Solid | Snapshot isolation, optimistic concurrency, conflict detection |
| B+Tree Indexes | ✅ Solid | L16/L32/L64/String64 variants, single & multi-value |
| Page Cache | ✅ Solid | Memory-mapped files, clock-sweep eviction, 8KB pages |
| Secondary Indexes | ✅ Solid | Automatic index maintenance on marked fields |
| Multi-Component Entities | ✅ Solid | Same entity ID across multiple component tables |
| Transaction Pooling | ✅ Solid | Reuse transaction objects to reduce allocations |

### In Progress / Designed

| Feature | Status | Notes |
|---------|--------|-------|
| Query Engine | 📐 Designed | WHERE clauses, Views, incremental maintenance - [design ready](../design/QueryEngine.md) |
| Telemetry | 🔧 In Progress | Contention tracking, page I/O metrics, configurable |
| AccessControl Refactor | 🔧 In Progress | Timeout/deadline support, better telemetry |

### Designed (Architecture Defined, Not Yet Implemented)

| Feature | Priority | Design Doc |
|---------|----------|------------|
| WAL / Durability | High | [06-durability.md](06-durability.md) — WAL, FUA, ring buffer, epoch-based recovery |
| Unit of Work | High | [02-execution.md](02-execution.md) / [04-data.md](04-data.md) — Durability boundary, three-tier API |
| Crash Recovery | High | [06-durability.md §6.7](06-durability.md#67-crash-recovery) — Epoch voiding, WAL replay |
| Checkpointing | High | [06-durability.md §6.6](06-durability.md#66-checkpoint-manager) — Periodic dirty page fsync |
| Backup & Restore | Medium | [07-backup.md](07-backup.md) — Incremental page-based backups |

### Planned (Not Yet Started)

| Feature | Priority | Notes |
|---------|----------|-------|
| Resource Budgets | Medium | Memory limits, fail-fast on exhaustion |

---

## Purpose

When working on Typhon, it's easy to lose sight of the forest for the trees. This overview serves as:

1. **Navigation Map** - Understand where each piece fits in the larger architecture
2. **Design Reference** - Capture key decisions and their rationale
3. **Onboarding Guide** - Get up to speed on the system's structure
4. **Planning Tool** - Identify gaps, dependencies, and implementation priorities

## How to Use

Start with the **Main Components** below for the big picture, then drill into specific areas as needed. Each component document follows a consistent structure: purpose, sub-components, current state, and key design decisions.

---

## Main Components (Level 0)

Typhon is organized into **11 main components**, each with distinct responsibilities:

| # | Component | Description | Status |
|---|-----------|-------------|--------|
| **1** | [Concurrency & Synchronization](./01-concurrency.md) | Latches, locks, deadlines, cancellation, wait infrastructure | Mixed |
| **2** | [Execution System](./02-execution.md) | UnitOfWork, durability modes, commit path, background workers | 📐 Designed |
| **3** | [Storage Engine](./03-storage.md) | Page management, buffer pool, segments, disk I/O | Exists |
| **4** | [Data Engine](./04-data.md) | Schema, ComponentTable, B+Trees, MVCC, transactions | Exists |
| **5** | [Query Engine](./05-query.md) | Query parsing, filtering (WHERE), sorting, limits | New |
| **6** | [Durability & Recovery](./06-durability.md) | WAL, FUA, epoch-based crash recovery, checkpointing | 📐 Designed |
| **7** | [Backup & Restore](./07-backup.md) | Page-based incremental backup, checkpoint-consistent restore | 📐 Designed |
| **8** | [Resource Management](./08-resources.md) | Live resource graph, memory budgets, exhaustion policies | Partial |
| **9** | [Observability](./09-observability.md) | OpenTelemetry traces/metrics, health checks, diagnostics | Partial |
| **10** | [Error Handling & Resilience](./10-errors.md) | Consistent error model, exception hierarchy | Scattered |
| **11** | [Shared Utilities](./11-utilities.md) | Catalog manager, memory allocators, disk management | Partial |

### Status Legend

- **Exists** - Core functionality implemented, may need hardening
- **Partial** - Some pieces exist, others missing
- **Mixed** - Sub-components vary significantly in maturity
- **📐 Designed** - Architecture defined in docs, not yet implemented in code
- **New** - Not yet implemented, no design docs
- **Scattered** - Exists but not cohesively organized

---

## Architecture Principles

These principles guide Typhon's design:

1. **Microsecond Performance** - Every memory access counts; minimize cache misses, maximize data locality
2. **ACID with Flexibility** - Full transaction support, with durability configurable per-UnitOfWork (Deferred/GroupCommit/Immediate)
3. **ECS-Inspired Model** - Entities are just IDs; components are blittable structs
4. **MVCC for Concurrency** - Snapshot isolation via multi-version concurrency control
5. **Embedded First** - No separate server process; runs in-process with the application

---

## Configuration & Tuning Guide

This section provides practical guidance for tuning Typhon's key configuration parameters.

### Page Cache Size

The page cache is the primary knob for memory/performance trade-offs.

```csharp
services.Configure<PagedMMFOptions>(opt =>
{
    opt.PageCacheSize = 512;  // 512 pages × 8KB = 4MB cache
});
```

| Parameter | Default | Range | Guidance |
|-----------|---------|-------|----------|
| `PageCacheSize` | 256 pages (2MB) | 64 – 65536 | Set to `working_set_size / 8KB` |

**Rule of Thumb:** Cache should hold your "hot" working set:
- **Small game** (1K entities × 3 components × 64B): ~200KB → 32 pages minimum
- **Medium game** (100K entities × 5 components × 128B): ~64MB → 8192 pages
- **Large simulation** (1M entities): Consider 128MB+ cache or accept cache misses

**Symptoms of Undersized Cache:**
- High `PageMissRate` in telemetry
- Increased I/O latency variance
- Clock-sweep counter consistently at 0

### B+Tree Configuration

B+Tree node size is **fixed at 64 bytes** (one CPU cache line) and cannot be changed. This ensures:
- Single cache-line access per node traversal
- Predictable memory layout
- SIMD-friendly search within nodes

**Index Design Guidance:**

| Field Type | Index Overhead | Notes |
|------------|----------------|-------|
| `int`, `long` | ~8-16 bytes/entity | Compact, cache-efficient |
| `float`, `double` | ~8-16 bytes/entity | Same as integers |
| `String64` | ~72 bytes/entity | Key + padding; consider if truly needed |

**When NOT to Index:**
- Fields rarely queried
- High-cardinality fields with full scans anyway
- Fields updated every tick (index maintenance cost)

### Transaction Pool

The transaction pool reduces allocation pressure.

| Parameter | Value | Notes |
|-----------|-------|-------|
| Pool size | 16 objects | Fixed, not configurable |
| Overflow behavior | Allocates new | Never blocks, never shrinks |
| Reset on return | Full state reset | Clean for next use |

**Memory Impact:** 16 transactions × ~2KB each = ~32KB baseline. Beyond 16 concurrent transactions, heap allocations occur but are typically short-lived.

### Durability Mode Selection

Choose durability mode based on your data's criticality:

| Mode | FUA Frequency | Data-at-Risk | Best For |
|------|---------------|--------------|----------|
| **Deferred** | On explicit `FlushAsync()` | Unbounded until flush | Batch imports, non-critical data |
| **GroupCommit** | Every 5ms | Up to 5ms of transactions | Game servers, real-time apps |
| **Immediate** | Every commit | Zero (after commit returns) | Financial, audit logs |

**GroupCommit Interval (5ms) Rationale:**
- Fits within one game tick at 60fps (16.6ms)
- Below human perception threshold
- ~1% CPU overhead at 200 FUA/sec
- Adjust down to 2ms for financial apps, up to 10ms for batch-heavy workloads

### Memory Budget Estimation

Estimate Typhon's memory footprint:

```
Total Memory ≈ PageCache + TransactionPool + ComponentOverhead + IndexOverhead

Where:
  PageCache       = PageCacheSize × 8KB
  TransactionPool = 16 × 2KB = 32KB (fixed)
  ComponentOverhead = N_entities × sizeof(component) × avg_revisions × N_component_types
  IndexOverhead   = N_indexed_fields × N_entities × 16 bytes (approx)
```

**Example Calculation (100K player entities, 3 components, 2 indexes):**
```
PageCache       = 1024 pages × 8KB           =   8 MB
TransactionPool = 32KB                        =  32 KB
Components      = 100K × 256B × 2 revisions   =  51 MB
Indexes         = 2 × 100K × 16B              =   3 MB
─────────────────────────────────────────────────────
Total                                         ≈ ~63 MB
```

### Performance Tuning Checklist

| Symptom | Likely Cause | Tuning Action |
|---------|--------------|---------------|
| High read latency variance | Cache misses | Increase `PageCacheSize` |
| Commit latency spikes | Immediate mode overhead | Switch to GroupCommit |
| Memory growing unbounded | Long-running transactions | Ensure transactions commit/dispose promptly |
| Index queries slow | Missing index | Add `[Index]` attribute to queried fields |
| High contention on hot pages | Many writers, same page | Spread data across entities, reduce hot spots |

### Telemetry for Tuning

Enable the `Telemetry` build configuration for detailed metrics:

```bash
dotnet build -c Telemetry
```

Key metrics to watch:
- `PageHitRate` — Target >95% for cache-sensitive workloads
- `ContentionWaitTimeMs` — Should be <1ms in healthy systems
- `CommitLatencyP99` — Tail latency indicates durability mode impact

---

## Thread Safety Contract

Typhon is an **embedded library** — it runs inside your process, on your threads. There is no "Typhon thread pool" or scheduler. Understanding which APIs are thread-safe is essential for correct usage.

### API Thread Safety

| API | Thread Safety | Notes |
|-----|---------------|-------|
| `DatabaseEngine` | ✅ Thread-safe | Single instance shared across all threads |
| `RegisterComponent<T>()` | ⚠️ **Startup only** | Call before any transactions; not safe during runtime |
| `CreateUnitOfWork()` | ✅ Thread-safe | Each call returns an independent UoW |
| `UnitOfWork` | ❌ Single-thread | Owned by creating thread; do not share |
| `CreateTransaction()` | ❌ Single-thread | Must be called from UoW's owning thread |
| `Transaction` | ❌ Single-thread | All operations must be on same thread |
| `tx.Commit()` | ❌ Single-thread | Internally uses thread-safe structures, but caller must be consistent |
| `GetIndex<T>().Get()` | ✅ Thread-safe | Read path uses shared latches; concurrent reads OK |

**Key insight:** The `DatabaseEngine` is the shared coordination point. Each thread creates its own `UnitOfWork` and `Transaction`. Typhon's internal latch system (`AccessControl`) handles concurrent access to pages, indexes, and revision chains transparently.

### Recommended Threading Patterns

**Pattern A: Thread-per-UoW (Game Server Workers)**

Each worker thread creates its own UoW and transactions:

```csharp
// Worker thread
while (running)
{
    using var uow = dbe.CreateUnitOfWork(TimeSpan.FromSeconds(5), DurabilityMode.GroupCommit);
    using var tx = uow.CreateTransaction();

    // Process work items for this thread
    ProcessPlayerUpdates(tx, workQueue.Dequeue());

    tx.Commit();
}
```

**Pattern B: UoW-per-Tick (Single Game Loop)**

Main thread processes all entities each tick:

```csharp
// Game loop thread
while (running)
{
    var tickDeadline = DateTime.UtcNow.AddMilliseconds(16);  // 60fps budget

    using var uow = dbe.CreateUnitOfWork(tickDeadline, DurabilityMode.GroupCommit);
    using var tx = uow.CreateTransaction();

    foreach (var entityId in activeEntities)
    {
        UpdateEntity(tx, entityId);
    }

    tx.Commit();

    // GroupCommit flushes asynchronously; next tick can start immediately
}
```

**Pattern C: Long-Lived UoW with Pooled Transactions (Session)**

Single UoW spans multiple transactions (e.g., player session):

```csharp
// Per-player session handler
using var uow = dbe.CreateUnitOfWork(sessionTimeout, DurabilityMode.Deferred);

while (sessionActive)
{
    using var tx = uow.CreateTransaction();  // Pooled internally
    HandlePlayerAction(tx, nextAction);
    tx.Commit();
}

await uow.FlushAsync();  // One FUA for entire session
```

### What NOT to Do

| Anti-Pattern | Problem | Fix |
|--------------|---------|-----|
| Share `Transaction` across threads | Race conditions, corrupted state | One transaction per thread |
| Call `RegisterComponent<T>()` at runtime | Concurrent modification of schema registry | Register all components at startup |
| Hold `Transaction` open across `await` | Thread may change; transaction tied to original | Commit before await, create new tx after |
| Assume index sees uncommitted writes | Known limitation (see [§4.7](04-data.md#index-maintenance-timing-known-limitation)) | Track entity IDs directly for just-created entities |

### Internal Background Workers

Typhon spawns a small number of internal threads for housekeeping (not user-configurable):

| Worker | Purpose | User Impact |
|--------|---------|-------------|
| **WAL Writer** | Drains ring buffer, performs FUA writes | GroupCommit/Immediate durability |
| **Checkpoint Manager** | Periodic dirty page flush | Reduces recovery time |
| **Deadline Watchdog** | Monitors UoW timeouts | Cancels expired operations |

These are implementation details — you don't configure or interact with them directly. They coordinate via lock-free structures (ring buffer, atomic watermarks) to minimize impact on your threads.

### Why No Thread Pool Configuration?

Traditional databases (PostgreSQL, SQL Server) have worker pools because they manage connections from external clients. Typhon is embedded — **your application owns the threads**. This means:

- No artificial thread limits imposed by the database
- No context switches between "your code" and "database code"
- Latency is predictable (no queueing behind other requests)
- You control parallelism via your own thread/task management

The only serialization point is the WAL ring buffer slot reservation (one CAS per commit), which is lock-free and sub-microsecond.

---

## Dependency Flow

Components have natural dependencies on each other:

<a href="../assets/typhon-dependency-flow.svg">
  <img src="../assets/typhon-dependency-flow.svg" width="945"
       alt="Typhon — Component Dependency Flow (click for full-size SVG)">
</a>
<sub>D2 source: <code>assets/src/typhon-dependency-flow.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>

The vertical flow shows the primary dependency chain. Cross-cutting concerns (7-10) integrate throughout all layers via dotted lines.

---

## Current Codebase Mapping

| Component | Primary Location in Code |
|-----------|-------------------------|
| 1. Concurrency | `src/Typhon.Engine/Misc/AccessControl/`, `Misc/AdaptiveWaiter.cs` |
| 2. Execution | *(to be created)* |
| 3. Storage | `src/Typhon.Engine/Persistence Layer/` |
| 4. Data | `src/Typhon.Engine/Database Engine/` |
| 5. Query | *(to be created)* |
| 6. Durability | *(to be created)* |
| 7. Backup | *(to be created)* |
| 8. Observability | `src/Typhon.Engine/Telemetry/` |
| 9. Resources | `src/Typhon.Engine/Misc/Resource Registry/`, `Misc/MemoryAllocator/` |
| 10. Errors | *(scattered, needs consolidation)* |
| 11. Utilities | `src/Typhon.Engine/Misc/`, `Collections/` |

---

## Next Steps

Each component document (linked above) will be created as we drill down. Priority order for documentation:

1. **Concurrency** - Foundation everything depends on
2. **Storage Engine** - Core persistence, already exists
3. **Data Engine** - Core database logic, already exists
4. **Execution System** - Needed for timeouts and background work
5. **Durability** - The "D" in ACID
6. *Others as needed...*

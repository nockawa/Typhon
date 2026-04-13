# Typhon

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

### ⚠️ The engine is in active development and not usable as a library or product. ⚠️

There is no documentation, no stable API, no NuGet package, and no support.


**A microsecond-latency ACID data engine combining ECS archetype storage with tick-based parallel execution.**

Typhon is an embedded data engine for real-time workloads like game servers, simulations, and stateful dataflow.<br/> 
It pairs a microsecond-latency ACID store — MVCC snapshot isolation, configurable durability, source-generated ECS archetype accessors — with a tick-based parallel runtime that dispatches fine-grained system chunks across worker threads, each operating directly on the store.<br/>
The runtime doesn't sit on top of the database — it runs inside it.
---

## Key Features

- **Microsecond Operations** — Optimized for µs-level latency with pinned memory, SIMD, and lock-free reads
- **ACID Transactions** — Full transactional semantics with optimistic concurrency control
- **MVCC Snapshot Isolation** — Readers never block writers; each transaction sees a consistent snapshot
- **ECS Archetype System** — Entities are typed by archetype; components are blittable structs with source-generated accessors and archetype inheritance
- **Entity Clusters** — SoA batched storage co-locating up to 64 entities per cluster, with cluster-native SIMD predicate evaluation (AVX-512 / AVX2 / scalar dispatch), zone-map pruning, and k-way sorted merge
- **Query Engine** — Typed queries with `Where`, `With`/`Without` filters, polymorphic iteration, OR logic, navigation joins, and a cluster execution path that routes filtered scans through SIMD gather-compare on cluster SoA columns
- **Views & Change Tracking** — Incremental entity-set monitoring across transactions (added/removed/modified detection) with ring-buffer delta streaming
- **B+Tree Indexes** — Cache-aligned 128-byte nodes with optimistic lock coupling and specialized key-size variants
- **Spatial Indexing** — Page-backed wide R-Tree for AABB / Radius / Ray / Frustum / kNN queries, plus a per-cell cluster broadphase (`SpatialGrid`) with BMI2 Morton-encoded cell keys and multi-resolution `SimTier` dispatch for near/far/coarse simulation budgets
- **Write-Ahead Logging** — WAL with configurable durability modes (Deferred, GroupCommit, Immediate) and coalesced tick-boundary snapshots via `TickFence` / `ClusterTickFence` chunks for the runtime tick loop
- **Page Integrity** — CRC32C checksums, full-page images, and checkpoint-based recovery
- **Schema Versioning** — Component revisions with field-level migration support
- **Three Storage Modes** — Versioned (full MVCC + WAL), SingleVersion (last-writer-wins, tick-fence WAL), Transient (heap-only, no persistence)
- **Configurable Durability** — Choose per-UnitOfWork whether data is deferred, group-committed, or immediately fsynced
- **Game Server Runtime** — Tick-based micro-task `DagScheduler` with any-worker parallel dispatch, per-system change filters, typed MPSC event queues, side-transactions, cluster dormancy with staggered heartbeat wake, checkerboard Red/Black dispatch, and a 4-level overload response hierarchy (tick-rate modulation, player shedding)
- **Subscription Server + Client SDK** — TCP delta streaming of published views with MemoryPack serialization, per-client incremental sync, backpressure-driven resync, and a zero-engine-dependency `Typhon.Client` assembly for game clients
- **Observability** — Runtime telemetry, metrics, and diagnostics with zero-cost JIT-eliminated toggles
- **Deep-Trace Profiler** — Per-tick Gantt and flame-graph visualization via a `.typhon-trace` binary format, live TCP streaming to a React/Vite viewer, and optional `dotnet-trace` CPU sampling correlation for full managed-stack profiles
- **Interactive Shell (tsh)** — Database REPL for inspection and debugging
- **Roslyn Analyzers** — Custom analyzers detecting undisposed engine resources at compile time

## Quick Start

```csharp
// Define components
[Component("Game.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

[Component("Game.Health", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Health
{
    [Field] public int Current;
    [Field] public int Max;
}

// Define an archetype (ID is a unique 12-bit identifier embedded in EntityId)
[Archetype(1)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}

// Register and initialize
var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponentFromAccessor<Position>();
dbe.RegisterComponentFromAccessor<Health>();
dbe.InitializeArchetypes();

// Spawn an entity
using var tx = dbe.CreateQuickTransaction();

var pos = new Position { X = 10, Y = 20, Z = 0 };
var hp  = new Health { Current = 100, Max = 100 };
var id  = tx.Spawn<Unit>(Unit.Position.Set(in pos), Unit.Health.Set(in hp));

// Read it back
ref readonly var p = ref tx.Open(id).Read(Unit.Position);
// p.X == 10, p.Y == 20

// Mutate
ref var h = ref tx.OpenMut(id).Write(Unit.Health);
h.Current = 80;

tx.Commit();
```

### Running as a Game Server (Runtime)

For tick-based workloads, the `TyphonRuntime` wraps the engine with a DAG scheduler, parallel worker pool, and tick loop:

```csharp
// Register components and archetypes as above, then:

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    // Parallel query system — runs once per tick across worker threads
    schedule.QuerySystem("Movement", ctx =>
    {
        foreach (var id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref var pos = ref e.Write(Unit.Position);
            pos.X += e.Read(Unit.Velocity).X * ctx.DeltaTime;
        }
    }, input: () => activeUnitsView, parallel: true);

    // Sequential cleanup system — runs after Movement completes
    schedule.CallbackSystem("Cleanup", ctx => { /* ... */ }, after: "Movement");
});

runtime.Start();  // Tick loop runs at configured Hz (default 60)
```

The runtime creates one `UnitOfWork` per tick (Deferred durability), one `Transaction` per system (or per parallel chunk), and coalesces all writes into tick-fence WAL chunks at the end of each tick.

## Architecture

<a href="typhon-architecture-layers.svg">
  <img src="typhon-architecture-layers.svg" width="1165"
       alt="Typhon Architecture Layers">
</a>

## Development Status

Typhon is in **active development** targeting an alpha release. Current state:

- [x] Core transaction engine with MVCC
- [x] B+Tree indexes with optimistic lock coupling
- [x] Component-level durability options
- [x] Write-Ahead Logging with configurable durability modes
- [x] Page integrity (CRC32C, full-page images, checkpoints)
- [x] Query engine with filtering, sorting, and navigation
- [x] Views and incremental change tracking
- [x] ECS archetype system with source-generated accessors
- [x] Entity cluster storage (SoA batched, SIMD predicate evaluation, zone-map pruning, k-way sorted merge)
- [x] Dual page store abstraction (`PersistentStore` + `TransientStore`)
- [x] Schema versioning and evolution
- [x] Spatial indexing (page-backed wide R-Tree: AABB / Radius / Ray / Frustum / kNN)
- [x] Spatial grid with per-cell cluster broadphase and BMI2 Morton encoding
- [x] Multi-resolution simulation (`SimTier` dispatch, cluster dormancy, checkerboard Red/Black)
- [x] Game server runtime (`DagScheduler`, parallel system dispatch, overload management, event queues)
- [x] Subscription server and `Typhon.Client` SDK (TCP delta streaming, MemoryPack wire protocol)
- [x] Deep-trace runtime profiler (`.typhon-trace` format, live TCP streaming, React viewer)
- [x] Interactive shell (tsh)
- [x] Observability and monitoring stack
- [x] HashMap collections with key-size specialization
- [ ] Crash recovery testing
- [ ] Backup and restore

## Project Structure

```
Typhon/
├── src/
│   ├── Typhon.Engine/              # Main database engine
│   │   ├── Collections/            # HashMap, concurrent data structures
│   │   ├── Concurrency/            # Latches, epoch management, adaptive wait
│   │   ├── Data/                   # MVCC, ECS, indexes, schema, queries, clusters, spatial
│   │   ├── Durability/             # WAL, checkpointing, recovery
│   │   ├── Errors/                 # Error model, resilience
│   │   ├── Execution/              # UnitOfWork, transaction management
│   │   ├── Memory/                 # Memory management, allocators
│   │   ├── Misc/                   # Shared utilities (String64, Variant, etc.)
│   │   ├── Observability/          # Telemetry, metrics, diagnostics
│   │   ├── Resources/              # Resource graph, lifecycle
│   │   ├── Runtime/                # DagScheduler, tick loop, systems, subscriptions, inspectors
│   │   └── Storage/                # Pages, cache, segments, IPageStore / PersistentStore / TransientStore
│   ├── Typhon.Analyzers/           # Roslyn analyzers (dispose detection)
│   ├── Typhon.Client/              # External client SDK (TCP subscriptions, zero engine deps)
│   ├── Typhon.Generators/          # Source generators (archetype accessors)
│   ├── Typhon.Profiler/            # Trace file format, readers/writers, Chrome Trace exporter
│   ├── Typhon.Protocol/            # MemoryPack wire-format types (TickDeltaMessage, etc.)
│   ├── Typhon.Schema.Definition/   # Component & archetype attributes
│   ├── Typhon.Shell/               # Interactive database shell (tsh)
│   └── Typhon.Shell.Extensibility/ # Shell extension points
├── test/
│   ├── Typhon.Engine.Tests/        # NUnit test suite
│   ├── Typhon.Client.Tests/        # Client SDK tests
│   ├── Typhon.Benchmark/           # BenchmarkDotNet performance tests
│   ├── Typhon.ARPG.Schema/         # Example ARPG game schema
│   ├── Typhon.ARPG.Shell/          # Shell demo with ARPG data
│   ├── Typhon.MonitoringDemo/      # Observability demo
│   └── AntHill/                    # Godot-based ant-colony demo (runtime + clusters + spatial tiers)
├── tools/
│   └── Typhon.Profiler.Server/     # ASP.NET Core ingestion + React/Vite profiler viewer
├── claude/                         # Architecture docs, ADRs, design specs
└── benchmark/                      # Benchmark results
```

## History

This project has had quite a journey:

- **2015** — Initial bootstrap with a different design, quickly shelved
- **2020** — COVID resurrection as a POC: "Can we build a µs-latency ACID database for persistent games?" Promising results, then shelved again
- **2025** — Third resurrection with firm intention to reach alpha stage
- **2025-2026** — Rapid progress: WAL & durability, query engine, ECS archetype system with source generators, schema evolution, observability stack, and interactive shell delivered
- **Q2 2026 alpha push** — Game-server runtime (`DagScheduler`, parallel system dispatch, overload management), entity clusters with cluster-native SIMD query execution, dual page store abstraction (`PersistentStore` + `TransientStore`), spatial indexing (page-backed R-Tree + spatial grid cluster broadphase), multi-resolution `SimTier` dispatch with cluster dormancy and checkerboard, TCP subscription server with zero-engine-dependency client SDK, and a deep-trace runtime profiler with live-streaming viewer

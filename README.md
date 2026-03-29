# Typhon

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

### ⚠️ The engine is in active development and not usable as a library or product. ⚠️

There is no documentation, no stable API, no NuGet package, and no support.


**A microsecond-latency ACID database engine combining ECS architecture with MVCC isolation.**

Typhon is an embedded database designed for real-time workloads like game servers, and simulations. It delivers ACID transactions with configurable durability, snapshot isolation via MVCC, and a data model built on Entity-Component-System archetypes with source-generated accessors.

---

## Key Features

- **Microsecond Operations** — Optimized for µs-level latency with pinned memory, SIMD, and lock-free reads
- **ACID Transactions** — Full transactional semantics with optimistic concurrency control
- **MVCC Snapshot Isolation** — Readers never block writers; each transaction sees a consistent snapshot
- **ECS Archetype System** — Entities are typed by archetype; components are blittable structs with source-generated accessors and archetype inheritance
- **Query Engine** — Typed queries with `Where`, `With`/`Without` filters, polymorphic iteration, OR logic, and navigation queries
- **Views & Change Tracking** — Incremental entity-set monitoring across transactions (added/removed detection)
- **B+Tree Indexes** — Cache-aligned 128-byte nodes with optimistic lock coupling and specialized key-size variants
- **Write-Ahead Logging** — WAL with configurable durability modes (Deferred, GroupCommit, Immediate)
- **Page Integrity** — CRC32C checksums, full-page images, and checkpoint-based recovery
- **Schema Versioning** — Component revisions with field-level migration support
- **Three Storage Modes** — Versioned (full MVCC + WAL), SingleVersion (last-writer-wins, tick-fence WAL), Transient (heap-only, no persistence)
- **Configurable Durability** — Choose per-UnitOfWork whether data is deferred, group-committed, or immediately fsynced
- **Observability** — Runtime telemetry, metrics, and diagnostics with zero-cost toggle
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

## Architecture

<a href="typhon-architecture-layers.svg">
  <img src="typhon-architecture-layers.svg" width="1165"
       alt="Typhon Architecture Layers">
</a>

For detailed architecture, see the [Overview Documentation](claude/overview/).

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
- [x] Schema versioning and evolution
- [x] Interactive shell (tsh)
- [x] Observability and monitoring stack
- [x] HashMap collections with key-size specialization
- [ ] Crash recovery testing
- [ ] Backup and restore
- [ ] Spatial indexing

## Project Structure

```
Typhon/
├── src/
│   ├── Typhon.Engine/              # Main database engine
│   │   ├── Collections/            # HashMap, concurrent data structures
│   │   ├── Concurrency/            # Latches, epoch management, adaptive wait
│   │   ├── Data/                   # MVCC, ECS, indexes, schema, queries
│   │   ├── Durability/             # WAL, checkpointing, recovery
│   │   ├── Errors/                 # Error model, resilience
│   │   ├── Execution/              # UnitOfWork, transaction management
│   │   ├── Memory/                 # Memory management, allocators
│   │   ├── Observability/          # Telemetry, metrics, diagnostics
│   │   ├── Resources/              # Resource graph, lifecycle
│   │   └── Storage/                # Pages, cache, segments
│   ├── Typhon.Analyzers/           # Roslyn analyzers (dispose detection)
│   ├── Typhon.Generators/          # Source generators (archetype accessors)
│   ├── Typhon.Schema.Definition/   # Component & archetype attributes
│   ├── Typhon.Shell/               # Interactive database shell (tsh)
│   └── Typhon.Shell.Extensibility/ # Shell extension points
├── test/
│   ├── Typhon.Engine.Tests/        # NUnit test suite
│   ├── Typhon.Benchmark/           # BenchmarkDotNet performance tests
│   ├── Typhon.ARPG.Schema/         # Example ARPG game schema
│   ├── Typhon.ARPG.Shell/          # Shell demo with ARPG data
│   └── Typhon.MonitoringDemo/      # Observability demo
├── claude/                         # Architecture docs, ADRs, design specs
└── benchmark/                      # Benchmark results
```

## History

This project has had quite a journey:

- **2015** — Initial bootstrap with a different design, quickly shelved
- **2020** — COVID resurrection as a POC: "Can we build a µs-latency ACID database for persistent games?" Promising results, then shelved again
- **2025** — Third resurrection with firm intention to reach alpha stage
- **2025-2026** — Rapid progress: WAL & durability, query engine, ECS archetype system with source generators, schema evolution, observability stack, and interactive shell delivered

Along the way, explorations in unsafe/GC-free .NET programming led to [Tomate](https://github.com/nockawa/Tomate) — a separate project that could theoretically integrate but intentionally doesn't (yet).

---

<p align="center">
  <i>Built with excessive amounts of unsafe code</i>
</p>

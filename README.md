# 🐍 Typhon

[![Build](https://github.com/nockawa/Typhon/actions/workflows/build-documentation.yml/badge.svg)](https://github.com/nockawa/Typhon/actions)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

**A microsecond-latency ACID database engine combining ECS architecture with MVCC isolation.**

Typhon is an embedded database designed for real-time workloads like game servers, simulations, and high-frequency trading systems. It delivers ACID transactions with configurable durability, snapshot isolation via MVCC, and a data model inspired by Entity-Component-System patterns.

📖 [Documentation](https://nockawa.github.io/Typhon/) · 🐛 [Issues](https://github.com/nockawa/Typhon/issues) · 📋 [Project Board](https://github.com/users/nockawa/projects/7)

---

## ✨ Key Features

- **Microsecond Operations** — Optimized for µs-level latency with pinned memory, SIMD, and lock-free reads
- **ACID Transactions** — Full transactional semantics with optimistic concurrency control
- **Configurable Durability** — Choose per-component whether data persists to disk or stays in-memory
- **MVCC Snapshot Isolation** — Readers never block writers; each transaction sees a consistent snapshot
- **ECS-Inspired Data Model** — Entities are just IDs; components are blittable structs with automatic indexing
- **B+Tree Indexes** — Cache-aligned 64-byte nodes with specialized variants for different key sizes

## 🚀 Quick Start

```csharp
// Define a component
[Component]
public struct Player
{
    [Field] [Index] public int PlayerId;
    [Field] public float Health;
    [Field] public String64 Name;
}

// Use the database
using var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponent<Player>();

using var tx = dbe.CreateQuickTransaction();

// Create an entity
var player = new Player { PlayerId = 42, Health = 100f, Name = "Alice" };
var entityId = tx.CreateEntity(ref player);

// Read it back
tx.ReadEntity(entityId, out Player loaded);

// Commit the transaction
tx.Commit();
```

## 📦 Installation

> ⚠️ **Pre-release**: Typhon is in active development. No NuGet package yet.

```bash
# Clone and build
git clone https://github.com/nockawa/Typhon.git
cd Typhon
dotnet build
dotnet test
```

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     DatabaseEngine                          │
│  (API, Transaction Management, Component Registration)      │
├─────────────────────────────────────────────────────────────┤
│                     Transaction Layer                       │
│  (MVCC, Snapshot Isolation, Conflict Detection)             │
├─────────────────────────────────────────────────────────────┤
│                     Component Tables                        │
│  (Per-Type Storage, Revision Chains, B+Tree Indexes)        │
├─────────────────────────────────────────────────────────────┤
│                     Persistence Layer                       │
│  (PagedMMF, 8KB Pages, Clock-Sweep Cache, Segments)         │
└─────────────────────────────────────────────────────────────┘
```

For detailed architecture, see the [Overview Documentation](claude/overview/).

## 🎯 Use Cases

Typhon is designed for workloads where **latency matters more than throughput**:

| Domain | Application |
|--------|-------------|
| **Gaming** | Persistent world state, real-time entity updates, MMO backends |
| **Finance** | High-frequency trading, tick-by-tick market data, order management |
| **Embedded** | In-process database without network overhead, edge computing |
| **ECS Persistence** | Natural storage layer for entity-component-system architectures |

## 📚 Documentation

- **[API Documentation](https://nockawa.github.io/Typhon/)** — Full API reference
- **[Architecture Overview](claude/overview/)** — 11-part deep dive into internals
- **[ADRs](claude/adr/)** — 30 Architecture Decision Records explaining design choices
- **[Contributing](CONTRIB.md)** — Development workflow and guidelines

## 🧪 Development Status

Typhon is in **active development** targeting an alpha release. Current focus:

- [x] Core transaction engine with MVCC
- [x] B+Tree indexes with concurrent access
- [x] Component-level durability options
- [ ] Write-Ahead Logging (WAL)
- [ ] Query engine with filtering/sorting
- [ ] Backup and restore

See the [Project Board](https://github.com/users/nockawa/projects/7) for current priorities.

## 📜 History

This project has had quite a journey:

- **2015** — Initial bootstrap with a different design, quickly shelved
- **2020** — COVID resurrection as a POC: "Can we build a µs-latency ACID database for persistent games?" Promising results, then shelved again
- **2025** — Third resurrection with firm intention to reach alpha stage

Along the way, explorations in unsafe/GC-free .NET programming led to [🍅 Tomate](https://github.com/nockawa/Tomate) — a separate project that could theoretically integrate but intentionally doesn't (yet).

---

<p align="center">
  <i>Built with 🐍 and excessive amounts of unsafe code</i>
</p>

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Typhon is a real-time, low-latency ACID database engine with microsecond-level performance targets. It uses an Entity-Component-System (ECS) architecture combined with traditional database features like transactions, MVCC (Multi-Version Concurrency Control), and persistent B+Tree indexes.

**Key Goals:**
- Fast (microsecond-level operations)
- Reliable (ACID transactions with optional durability per component)
- ECS-inspired data model for game/simulation workloads
- Snapshot isolation via MVCC for high concurrency

## Build & Development Commands

**Build the solution:**
```bash
dotnet build Typhon.sln
```

**Build specific configurations:**
```bash
dotnet build -c Debug
dotnet build -c Release
dotnet build -c VerboseLogging  # Enables verbose logging via VERBOSELOGGING define
```

**Run all tests:**
```bash
dotnet test
```

**Run tests from specific project:**
```bash
dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj
```

**Run a single test:**
```bash
dotnet test --filter "FullyQualifiedName~TransactionTests.CreateComp_SingleTransaction_SuccessfulCommit"
```

**Run benchmarks:**
```bash
cd test/Typhon.Benchmark
dotnet run -c Release
```

**Run specific benchmark:**
```bash
cd test/Typhon.Benchmark
dotnet run -c Release --filter '*PagedMemoryFile*'
```

## High-Level Architecture

### Core Architectural Layers

**1. DatabaseEngine (Orchestration Layer)**
- Top-level API for database operations
- Creates and manages transactions via TransactionChain
- Maintains ComponentTables (one per component type) in concurrent dictionary
- Generates unique primary keys for entities
- Located in: `src/Typhon.Engine/Database Engine/DatabaseEngine.cs`

**2. Transaction System (Concurrency Control)**
- **Transaction**: Implements MVCC snapshot isolation with optimistic concurrency control
  - Each transaction has a timestamp (tick) defining its snapshot point
  - Caches component operations until commit
  - Two-phase commit with conflict detection
  - Located in: `src/Typhon.Engine/Database Engine/Transaction.cs`

- **TransactionChain**: Doubly-linked list managing all active transactions
  - Tracks MinTick (oldest transaction) for garbage collection of old revisions
  - Transaction pooling to reduce allocations
  - Located in: `src/Typhon.Engine/Database Engine/TransactionChain.cs`

**3. ComponentTable (Data Storage per Component Type)**
- One table per component type, stores all instances
- Key segments:
  - **ComponentSegment**: Stores actual component data (fixed-size chunks)
  - **CompRevTableSegment**: Stores component revision chains (MVCC history)
  - **PrimaryKeyIndex**: B+Tree mapping entity IDs to revision chains
  - **Secondary Indexes**: Separate B+Trees for each indexed field
- Located in: `src/Typhon.Engine/Database Engine/ComponentTable.cs`

**4. Persistence Layer (Storage & Caching)**
- **PagedMMF**: Base class handling memory-mapped file I/O
  - 8KB pages with sophisticated caching (default: 256 pages = 2MB cache)
  - Clock-sweep eviction algorithm
  - Page states: Free, Allocating, Idle, Shared, Exclusive, IdleAndDirty
  - Async I/O operations
  - Located in: `src/Typhon.Engine/Persistence Layer/PagedMMF.cs`

- **ManagedPagedMMF**: Extends PagedMMF with segment management
  - Manages page allocation via occupancy bitmaps (3-level L0/L1/L2)
  - Tracks changes via ChangeSets
  - Handles root file header and system schema
  - Located in: `src/Typhon.Engine/Persistence Layer/ManagedPagedMMF.cs`

- **ChunkBasedSegment**: Fixed-size chunk allocation within pages
  - Occupancy tracking via 3-level bitmaps for efficient allocation
  - ChunkRandomAccessor provides cached access
  - Located in: `src/Typhon.Engine/Persistence Layer/ChunkBasedSegment.cs`

### MVCC Implementation Details

**Component Revisions:**
- Each component update creates a new revision instead of in-place modification
- Revisions stored as circular buffers in revision chains
- Each revision has: ComponentChunkId, DateTime, IsolationFlag

**Revision Chain Structure (CompRevStorageHeader):**
- NextChunkId: Points to next chunk in chain (for growing chains)
- FirstItemRevision: Revision number of oldest item in circular buffer
- FirstItemIndex: Start index in circular buffer
- ItemCount: Total items in chain
- ChainLength: Number of chunks in chain
- LastCommitRevisionIndex: Used for conflict detection

**Transaction Isolation:**
- Snapshot isolation: transactions see database as of their creation timestamp
- IsolationFlag: new revisions invisible to other transactions until committed
- Optimistic locking: no locks during execution, conflict detection at commit
- Read-your-own-writes: transactions see their own uncommitted changes via cache

**Conflict Resolution:**
- At commit, compare CurRevisionIndex with LastCommitRevisionIndex
- Default: "last write wins" - creates new revision and copies data forward
- Supports custom ConcurrencyConflictHandler for manual resolution

### B+Tree Indexes

- Generic implementation supporting all primitive types + String64
- Separate implementations: L16BTree, L32BTree, L64BTree, String64BTree
- Single vs Multiple value indexes (unique vs non-unique)
- Node storage via fixed-size chunks in ChunkBasedSegment
- Thread-safe with AccessControl for concurrent operations
- Located in: `src/Typhon.Engine/Database Engine/BPTree/`

### Entity-Component-System Model

**Entity**: Just a primary key (long integer), no inherent structure

**Component**: Unmanaged blittable struct marked with `[Component]` attribute
- Must be fixed layout in memory (no managed references)
- Stored as fixed-size records
- Supports MVCC with multiple revisions

**Multi-Component Entities**:
- Same entity ID can have components in multiple ComponentTables
- Operations can touch multiple components atomically in one transaction

**Schema System:**
- Components defined via attributes: `[Component]`, `[Field]`, `[Index]`
- Runtime schema metadata in DBComponentDefinition
- DatabaseDefinitions: registry of all component schemas
- Located in: `src/Typhon.Engine/Database Engine/Schema/`

## Important Implementation Details

### Performance considerations
- This project must have optimized memory access in the hot path or critical APIs. It is known that each memory access, even a byte, would fetch an entire cache line, and cache misses are expensive. So memory indirections must be as low as possible and data locality must be maximized.
- We must then rely on Structure of Array Optimization (SOA) to avoid excessive cache misses and process the data through SIMD instructions as much as possible.
- CPU cache miss is something we want to avoid as much as possible, and we must strive to minimize the number of cache misses by optimizing memory access patterns and using efficient data structures.

### Unsafe Code & Performance
- Project uses `<AllowUnsafeBlocks>true` extensively
- Heavy use of pointers, stackalloc, and unmanaged memory for performance
- GCHandle pins page cache to avoid GC moves
- Blittable struct requirements for components ensure zero-copy operations

### Concurrency Primitives
- **AccessControl**: Reader-writer lock for general concurrent access
- **AccessControlSmall**: Compact version for space-constrained scenarios
- **AdaptiveWaiter**: Spin-then-yield optimization for lock contention
- Lock-free reads via shared page access
- Located in: `src/Typhon.Engine/Misc/`

### Page Structure
```
Page (8192 bytes):
  - PageBaseHeader (64 bytes): PageIndex, ChangeRevision, etc.
  - PageMetadata (128 bytes): Occupancy bitmaps for chunks
  - PageRawData (8000 bytes): Actual data
```

### Segment Types
1. **LogicalSegment**: Base class for multi-page abstractions (500 indices in root, 2000 per overflow)
2. **ChunkBasedSegment**: Fixed-size chunks (components, indexes, revisions)
3. **VariableSizedBufferSegment**: Variable-size data (index multi-values)
4. **StringTableSegment**: String storage

### Testing Patterns
- Tests use NUnit framework
- Base class: `TestBase<T>` provides service provider setup
- Tests register components via `RegisterComponents(dbe)`
- Noise generation helpers (`CreateNoiseCompA`, `UpdateNoiseCompA`) for concurrency testing
- Test case sources for parameterized tests: `BuildNoiseCasesL1`, `BuildNoiseCasesL2`
- Located in: `test/Typhon.Engine.Tests/`

### Logging
- Uses Serilog with Microsoft.Extensions.Logging abstractions
- Custom enricher: CurrentFrameEnricher (adds frame context)
- VerboseLogging configuration enables detailed tracing via `VERBOSELOGGING` define
- Test projects configured with Serilog.Sinks.Seq for structured logging

## Project Structure

```
Typhon/
├── src/Typhon.Engine/           # Main database engine library
│   ├── Database Engine/         # Transaction, ComponentTable, schema, B+Trees
│   ├── Persistence Layer/       # PagedMMF, ManagedPagedMMF, segments
│   ├── Collections/             # Concurrent data structures (bitmaps, arrays)
│   ├── Misc/                    # Utilities (locks, String64, Variant, etc.)
│   └── Hosting/                 # DI extensions
├── test/
│   ├── Typhon.Engine.Tests/     # NUnit test suite
│   └── Typhon.Benchmark/        # BenchmarkDotNet performance tests
├── doc/                         # DocFx documentation
└── claude/                      # Claude working files
```

## Development Notes

### Component Definition Example
```csharp
[Component]
public struct MyComponent
{
    [Field]
    [Index] // Creates B+Tree index on this field
    public int PlayerId;

    [Field]
    public float Health;
}
```

### Transaction Usage Pattern
```csharp
using var dbe = serviceProvider.GetRequiredService<DatabaseEngine>();
dbe.RegisterComponent<MyComponent>();

using var t = dbe.CreateTransaction();
var component = new MyComponent { PlayerId = 123, Health = 100f };
var entityId = t.CreateEntity(ref component);

// Read within same transaction
var success = t.ReadEntity(entityId, out MyComponent read);

// Commit or rollback
var committed = t.Commit(); // or t.Rollback()
```

### Key Performance Optimizations
1. **Page Caching**: Clock-sweep algorithm minimizes disk I/O
2. **Sequential Allocation**: Allocates adjacent pages for contiguous writes
3. **Transaction Pooling**: Reuses transaction objects
4. **ChunkRandomAccessor**: Caches recently accessed chunks
5. **Batch I/O**: Groups contiguous page writes
6. **Lock-Free Reads**: Shared page access doesn't block readers
7. **Adaptive Waiting**: Spin-wait then yield for contention
8. **Pinned Memory**: GCHandle prevents GC moves on page cache

## Common Pitfalls

1. **Component Blittability**: Components must be unmanaged structs (no strings, arrays, or managed references). Use String64 for short strings.
2. **Transaction Disposal**: Always use `using` statements with transactions to ensure proper cleanup.
3. **Revision Tracking**: Don't assume revision numbers are contiguous after conflicts or rollbacks.
4. **Page Cache Size**: Default 2MB cache may be insufficient for large datasets. Configure via PagedMMFOptions.
5. **VerboseLogging Build**: The VerboseLogging configuration is for debugging only and significantly impacts performance.

## Documentation

Full documentation available at: https://nockawa.github.io/Typhon/

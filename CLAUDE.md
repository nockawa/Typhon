# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Typhon is a real-time, low-latency ACID database engine with microsecond-level performance targets. It uses an Entity-Component-System (ECS) architecture combined with traditional database features like transactions, MVCC (Multi-Version Concurrency Control), and persistent B+Tree indexes.

**Key Goals:**
- Fast (microsecond-level operations)
- Reliable (ACID transactions with optional durability per component)
- ECS-inspired data model for game/simulation workloads
- Snapshot isolation via MVCC for high concurrency

> 💡 **TL;DR — Developer quick start:** Jump to [Build & Development Commands](#build--development-commands) to build, test, and benchmark immediately. For architecture orientation, see the [Quick Navigation](#quick-navigation) table below.

## Key Documentation Resources

Typhon maintains comprehensive documentation in the `claude/` directory. Use these resources to understand architecture, design rationale, and development workflow.
When working on a new idea, always start by reading relevant documents in `claude/overview`, you can also read files in `claude/reference` to get more context of existing features and APIs.

### Design Doc Alignment
Before proposing implementations or design changes, ALWAYS read the relevant existing design documents first (in docs/design/) and verify alignment. Never deviate from established specs without explicitly noting the deviation and getting user approval.

### Quick Navigation

| When You Need... | Go To | Key Contents |
|------------------|-------|--------------|
| **How the engine works** | `claude/overview/` | 11-part architecture guide covering all subsystems |
| **Why a decision was made** | `claude/adr/` | 30 Architecture Decision Records with rationale |
| **Current priorities** | [GitHub Project](https://github.com/users/nockawa/projects/7) | Work tracking, status, roadmap |
| **Feature designs** | `claude/design/` | Pre-implementation specifications |
| **Deep research** | `claude/research/` | Analysis studies (e.g., timeout patterns, query systems) |
| **Document workflows** | `claude/README.md` | Lifecycle, templates, trigger phrases |

### Architecture Overview Series

The `claude/overview/` directory is the **authoritative architectural reference**:

| # | Document | Focus |
|---|----------|-------|
| 01 | [Concurrency](claude/overview/01-concurrency.md) | AccessControl, latches, deadlines, thread safety |
| 02 | [Execution](claude/overview/02-execution.md) | UnitOfWork, durability modes, commit path |
| 03 | [Storage](claude/overview/03-storage.md) | PagedMMF, page cache, segments, I/O |
| 04 | [Data](claude/overview/04-data.md) | MVCC, ComponentTable, indexes, transactions |
| 05 | [Query](claude/overview/05-query.md) | Query parsing, filtering, sorting |
| 06 | [Durability](claude/overview/06-durability.md) | WAL, crash recovery, checkpoints |
| 07 | [Backup](claude/overview/07-backup.md) | Incremental backup, restore |
| 08 | [Resources](claude/overview/08-resources.md) | Memory budgets, resource graph |
| 09 | [Observability](claude/overview/09-observability.md) | Telemetry, metrics, diagnostics |
| 10 | [Errors](claude/overview/10-errors.md) | Error model, exception hierarchy |
| 11 | [Utilities](claude/overview/11-utilities.md) | Allocators, disk management, shared utilities |

### Documentation-Heavy Project
This project is documentation-first. Most work involves creating, updating, or refining markdown design docs, ADRs, and planning documents. When updating docs, preserve existing structure and version headers. Cross-reference related documents. Always check for consistency across the full doc set when making changes.

## Build & Development Commands

**Build the solution:**
```bash
dotnet build Typhon.slnx
```

**Build specific configurations:**
```bash
dotnet build -c Debug
dotnet build -c Release
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

> See also: [ADR-001: Three-Tier API Hierarchy](claude/adr/001-three-tier-api-hierarchy.md), [ADR-004: Embedded Engine](claude/adr/004-embedded-engine-no-server.md)

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

> See also: [ADR-003: MVCC Snapshot Isolation](claude/adr/003-mvcc-snapshot-isolation.md), [ADR-005: Durability Mode Per UoW](claude/adr/005-durability-mode-per-uow.md)

**3. ComponentTable (Data Storage per Component Type)**
- One table per component type, stores all instances
- Key segments:
  - **ComponentSegment**: Stores actual component data (fixed-size chunks)
  - **CompRevTableSegment**: Stores component revision chains (MVCC history)
  - **PrimaryKeyIndex**: B+Tree mapping entity IDs to revision chains
  - **Secondary Indexes**: Separate B+Trees for each indexed field
- Located in: `src/Typhon.Engine/Database Engine/ComponentTable.cs`

> See also: [ADR-002: ECS Data Model](claude/adr/002-ecs-data-model.md), [ADR-008: ChunkBasedSegment](claude/adr/008-chunk-based-segments.md)

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

> See also: [ADR-006: 8KB Page Size](claude/adr/006-8kb-page-size.md), [ADR-007: Clock-Sweep Eviction](claude/adr/007-clock-sweep-eviction.md)

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

> See also: [ADR-023: Circular Buffer Revision Chains](claude/adr/023-circular-buffer-revision-chains.md)

### B+Tree Indexes

- Generic implementation supporting all primitive types + String64
- Separate implementations: L16BTree, L32BTree, L64BTree, String64BTree
- Single vs Multiple value indexes (unique vs non-unique)
- Node storage via fixed-size chunks in ChunkBasedSegment
- Thread-safe with AccessControl for concurrent operations
- Located in: `src/Typhon.Engine/Database Engine/BPTree/`

> See also: [ADR-021: Specialized B+Tree Variants](claude/adr/021-specialized-btree-variants.md), [ADR-022: 64-Byte Cache-Aligned Nodes](claude/adr/022-64byte-cache-aligned-nodes.md)

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

> See also: [ADR-009: Pinned Memory and Unsafe Code](claude/adr/009-pinned-memory-unsafe-code.md), [ADR-010: SOA Layout with SIMD](claude/adr/010-soa-simd-chunk-accessor.md), [ADR-027: Even-Sized Structs](claude/adr/027-even-sized-hot-path-structs.md)

### Unsafe Code & Performance
- Project uses `<AllowUnsafeBlocks>true` extensively
- Heavy use of pointers, stackalloc, and unmanaged memory for performance
- GCHandle pins page cache to avoid GC moves
- Blittable struct requirements for components ensure zero-copy operations

### Coding Standards
- **Follow `.editorconfig`**: All C# code must follow the formatting rules in `/.editorconfig`. Key rules include:
  - Expression-bodied members for simple methods/properties (`=>` syntax)
  - Braces on new lines (`csharp_new_line_before_open_brace = all`)
  - Always use braces for control flow statements
  - Collection expressions (`[]` instead of `Array.Empty<T>()`)
  - Private fields use `_camelCase` (underscore prefix)
  - Use `ArgumentNullException.ThrowIfNull()` for null checks
- **160 column max line length**: Lines must not exceed 160 characters. When a statement exceeds this limit:
  - Method parameters: Wrap after opening parenthesis, one parameter per line
  - Method arguments: Wrap after opening parenthesis, one argument per line
  - Chained calls: Wrap before the dot
  - Binary expressions: Wrap before the operator
  - Collection initializers: Wrap elements if line is too long
- **No nullable reference types**: Do not use `#nullable enable` or nullable annotations (`Type?`). Typhon does not rely on C# nullable reference types feature. Pass `null` for optional parameters without annotations.
- **Thread IDs stored as 16 bits**: All synchronization primitives that store thread IDs must use exactly 16 bits (max 65,535). This ensures consistency across `AccessControl`, `AccessControlSmall`, and `ResourceAccessControl`, and provides headroom for servers with 500+ cores.
- **No LINQ in hot paths**: Avoid LINQ in performance-critical code due to allocations and delegate overhead.
- **Prefer `ref struct` for short-lived helpers**: Use `ref struct` for stack-only types that wrap references (e.g., `AtomicChange`, `LockData`).
- **No `Volatile.Read`/`Write` for ≤64-bit types**: On x64, reads and writes of primitives up to 64 bits are naturally atomic. `Volatile.Read`/`Write` only adds unnecessary memory barrier overhead. Use plain field access instead. Reserve `Interlocked` operations for read-modify-write sequences (increment, compare-exchange, etc.).

### Concurrency Primitives
- **AccessControl**: Reader-writer lock for general concurrent access
- **AccessControlSmall**: Compact version for space-constrained scenarios
- **AdaptiveWaiter**: Spin-then-yield optimization for lock contention
- Lock-free reads via shared page access
- Located in: `src/Typhon.Engine/Misc/`

> See also: [ADR-016: Three-Mode ResourceAccessControl](claude/adr/016-three-mode-resource-access-control.md), [ADR-017: 64-Bit Atomic State](claude/adr/017-64bit-access-control-state.md), [ADR-018: Adaptive Spin-Wait](claude/adr/018-adaptive-spin-wait.md)

### .NET API Correctness
When working with .NET APIs (especially Activity/ActivitySource, System.Threading, Volatile, DI), read the actual source files and existing usage patterns in the codebase BEFORE writing code. Do NOT guess at API signatures or behavior. If unsure, ask the user or search docs first.

### Page Structure
```
Page (8192 bytes):
  - PageBaseHeader (64 bytes): PageIndex, ChangeRevision, etc.
  - PageMetadata (128 bytes): Occupancy bitmaps for chunks
  - PageRawData (8000 bytes): Actual data
```

> See also: [ADR-015: CRC32C Page Checksums](claude/adr/015-crc32c-page-checksums.md)

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
- Telemetry configuration enables detailed tracing via `TELEMETRY` define
- Test projects configured with Serilog.Sinks.Seq for structured logging

### Debugging Approach
When debugging issues, do NOT propose root cause explanations without evidence. Follow the user's diagnostic guidance (traces, logs, specific code paths). Avoid jumping to conclusions — enumerate hypotheses, then systematically verify each one starting with the most likely based on available data.

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
└── claude/                      # Development documentation & design
    ├── README.md                # Document lifecycle and workflows
    ├── overview/                # Architecture overview (11 documents)
    │   ├── 01-concurrency.md    # Locks, latches, thread-safety
    │   ├── 02-execution.md      # UoW, transactions, durability
    │   ├── 03-storage.md        # PagedMMF, caching, I/O
    │   ├── 04-data.md           # MVCC, indexes, revision chains
    │   └── ...                  # Query, durability, backup, etc.
    ├── adr/                     # Architecture Decision Records (30)
    │   ├── 001-three-tier-api-hierarchy.md
    │   ├── 002-ecs-data-model.md
    │   └── ...                  # Indexed in adr/README.md
    ├── ideas/                   # Early-stage ideas
    ├── research/                # Analysis & studies
    ├── design/                  # Pre-implementation specs
    ├── reference/               # Post-implementation guides
    ├── assets/                  # Diagrams (D2 source + SVG)
    │   ├── src/                 # D2 source files
    │   ├── viewer.html          # Interactive diagram viewer
    │   └── *.svg                # Rendered diagrams
    └── archive/                 # Historical documents
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
5. **Telemetry Build**: The Telemetry configuration is for debugging only and significantly impacts performance.

## Architecture Diagrams

Visual documentation is maintained in `claude/assets/`:

- **D2 source files**: `claude/assets/src/*.d2` (editable diagrams)
- **Rendered SVGs**: `claude/assets/*.svg` (embedded in docs)
- **Interactive viewer**: Open `claude/assets/viewer.html` in browser for pan-zoom navigation

Key diagrams: `typhon-architecture-layers.svg`, `typhon-commit-path.svg`, `typhon-data-mvcc-read.svg`, `typhon-storage-overview.svg`

For D2 tooling, themes, and embedding conventions, see [`claude/README.md` § Diagrams](claude/README.md#diagrams--visual-assets).

## Development Workflow

Work tracking is managed via the [Typhon dev GitHub Project](https://github.com/users/nockawa/projects/7). The `claude/` directory contains the knowledge base (architecture, designs, research), while the GitHub Project is the source of truth for work status.

> **See also:** [CONTRIB.md](CONTRIB.md) for the full development workflow documentation including rituals, automation, and daily guides.

### Claude Code Skills

| Skill | Purpose |
|-------|---------|
| `/dev-status` | Show current development status from GitHub Project |
| `/start-research #XX` | Start research on an issue (creates research doc, links ideas, updates status) |
| `/start-design #XX` | Start design for an issue (creates design doc from research/ideas, updates status to Ready) |
| `/start-work #XX` | Begin work on an issue (updates status, creates branch, verifies design) |
| `/complete-subtask #XX` | Complete a sub-issue (close it, check parent checkbox, update design doc) |
| `/complete-work #XX` | Finish work (close issue, prompt for doc updates, archive design) |
| `/create-issue` | Create new GitHub issue with project fields |
| `/weekly-review` | Weekly progress summary and stale item detection |
| `/mountain-view` | Full backlog analysis - see the entire mountain of work |

### Issue Lifecycle

```
Backlog → Research → Ready → In Progress → Review → Done
```

1. **Backlog**: Captured but not yet prioritized
2. **Research**: Needs exploration before design (use `/start-research #XX`, creates `claude/research/` doc)
3. **Ready**: Design complete, ready to implement (use `/start-design #XX`, creates `claude/design/` doc)
4. **In Progress**: Active development (use `/start-work #XX`)
5. **Review**: PR open, awaiting merge
6. **Done**: Complete (use `/complete-work #XX`)

#### GitHub Issue Completion Checklist
When closing a GitHub issue: 1) Check ALL checkboxes in the issue body, 2) Update the project board status, 3) Move any related design docs to the appropriate folder, 4) Verify with `gh issue view` that everything is properly updated.

### Project Fields

- **Status**: Workflow stage (Backlog → Done)
- **Priority**: P0-Critical, P1-High, P2-Medium, P3-Low
- **Phase**: Telemetry, Query, WAL, Reliability, Infrastructure
- **Area**: Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives
- **Estimate**: XS, S, M, L, XL
- **Target**: Target date for Roadmap view

## Working with Claude

### Clarification-First Workflow

For complex, ambiguous, or open-ended requests, Claude should **ask clarifying questions before providing an answer**. This is the default behavior - don't wait to be asked "do you have questions?"

**When to ask first (default to asking for anything non-trivial):**
- Request has multiple valid interpretations
- Scope is unclear (how deep? how broad? which aspects?)
- Trade-offs exist that depend on user preference
- Implementation could go several directions
- The "right" answer depends on context not yet provided
- Architectural or design decisions are involved
- Performance vs simplicity trade-offs exist

**How to ask:**
- Use the AskUserQuestion tool with a wizard-like flow
- Present 2-4 clear options per question
- Include brief descriptions explaining what each option means/implies
- Ask 1-4 focused questions to narrow scope
- Then proceed with the clarified understanding

**When NOT to ask (just proceed):**
- Simple, unambiguous requests with clear scope
- User explicitly says "just do it", "don't ask", or "try something"
- Follow-up to an already-clarified topic in the same conversation
- Urgent fixes where speed matters more than perfection
- User has provided detailed specifications already

### Document Lifecycle Integration

This project uses a structured document lifecycle in `claude/`. Documents progress through stages:

```
ideas/ → research/ → design/ → reference/ → archive/
```

**When creating documents**, Claude asks for the category location (e.g., `database-engine/`, `persistence/`) unless specified explicitly.

For trigger phrases, templates, directory conventions, and workflows, see [`claude/README.md`](claude/README.md).

### Documentation Writing Principles

When creating or updating long documents (design docs, guides, references, CONTRIB.md, etc.), follow the **"do first, read later"** principle:

- **Always include a TL;DR or quick-start signpost** near the top of any document longer than ~100 lines. Use a `> 💡` blockquote pointing readers to the most actionable section (e.g., "Skip to [Getting Started](#getting-started) for hands-on steps").
- **Structure for two audiences**: the reader who wants to *do something right now* and the reader who wants to *understand the full picture*. Put practical/actionable content (daily guides, setup steps, checklists) in clearly labeled sections. Put conceptual/architectural content (lifecycle stages, design rationale, principles) separately.
- **Front-load the practical.** Readers should be able to start working within the first screenful of content, then circle back for depth when they're ready.

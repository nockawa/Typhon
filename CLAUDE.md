# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Opinion vs Action

When the user asks for your opinion on a design choice, code approach, or any topic — give your opinion only and challenge the user on their choices. Do NOT edit files or make changes; wait for explicit instructions to proceed.

## Key Documentation Resources

Typhon maintains comprehensive documentation in the `claude/` directory. 
Use these resources to understand architecture, design rationale, and development workflow.
When working on a new idea, always start by reading relevant documents in `claude/overview`, you can also read files in `claude/reference` to get more context of existing features and APIs.
Architecture Design Records are located in `claude/adr`, use it when designing new code.

## Project Overview
Typhon is a real-time, low-latency ACID database engine with microsecond-level performance targets, using an ECS architecture with MVCC snapshot isolation.

### Design Doc Alignment
Before proposing implementations or design changes, ALWAYS read the relevant existing design documents first (in `claude/design/`) and verify alignment. 
Never deviate from established specs without explicitly noting the deviation and getting user approval.

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

### D2 Diagrams
Architecture diagrams use the **D2** language. Source files live in `claude/assets/src/*.d2`, rendered SVGs in `claude/assets/*.svg`.

- **Conventions:** See [`claude/d2-conventions.md`](claude/d2-conventions.md) for color palette, shapes, and patterns
- **Render:** `"/c/Program Files/D2/d2.exe" --theme 0 assets/src/name.d2 assets/name.svg`
- **Viewer:** Open `claude/assets/viewer.html` for interactive pan-zoom
- **After adding:** Update the `DIAGRAMS` array in `viewer.html`

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

**IMPORTANT — Test timeout safety:** Typhon unit tests should complete in under 5 seconds. If tests run longer, it almost certainly means an infinite loop or deadlock. When running tests, ALWAYS use a 15-second timeout and kill the process if it hasn't completed. Use `timeout 15` (on Windows) or equivalent to enforce this.

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

## Important Implementation Details

### Performance considerations
- Always try to **control/optimize memory indirection** to reduce CPU cache miss and maximize data locality. 
- **Cache-line aware**: Every memory access fetches an entire cache line. 
- Prefer Structure of Arrays (SOA) layout over Array of Structures.

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

### Concurrency / synchronization primitives
- Rely on .NET's Interlocked class.
- Rely on AccessControl, AccessControlSmall, EpochManager / EpochGuard.
- **AdaptiveWaiter**: Spin-then-yield optimization for lock contention
- Located in: `src/Typhon.Engine/Concurrency/`

### .NET API Correctness
Do NOT guess at .NET API signatures or behavior. Look up documentation by fetching: `https://learn.microsoft.com/en-us/dotnet/api/{fully.qualified.name.in.lowercase}`

Examples: `system.threading.interlocked.compareexchange`, `system.runtime.interopservices.gchandle`
Also read existing usage patterns in the codebase before writing new code.

### Testing Patterns
- Tests use NUnit framework
- Base class: `TestBase<T>` provides service provider setup
- Tests register components via `RegisterComponents(dbe)`
- Noise generation helpers (`CreateNoiseCompA`, `UpdateNoiseCompA`) for concurrency testing
- Test case sources for parameterized tests: `BuildNoiseCasesL1`, `BuildNoiseCasesL2`
- Located in: `test/Typhon.Engine.Tests/`

### Unit test code generation
- Avoid relying on Thread.Sleep, prefer thread synchronization mechanisms.
- Unit test execution time should be below < 30ms for very simple test, < 100 for medium and < 300 for complex ones.

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
```

## Development Workflow

Work tracking is managed via the [Typhon dev GitHub Project](https://github.com/users/nockawa/projects/7). The `claude/` directory contains the knowledge base (architecture, designs, research), while the GitHub Project is the source of truth for work status.

> **See also:** [CONTRIB.md](CONTRIB.md) for the full development workflow documentation including rituals, automation, and daily guides.

### Claude Code Skills

| Skill | Purpose |
|-------|---------|
| `/dev-status` | Show current development status from GitHub Project |
| `/start-research #XX` | Start research on an issue (creates research doc, links ideas, updates status) |
| `/start-design #XX` | Start design for an issue (creates design doc from research/ideas, updates status to Ready) |
| `/start-task #XX` | Begin work on an issue (updates status, creates branch, verifies design) |
| `/start-subtask #XX` | Start a sub-issue (updates status, validates dependencies, updates design doc) |
| `/complete-subtask #XX` | Complete a sub-issue (close it, check parent checkbox, update design doc) |
| `/complete-task #XX` | Finish work (close issue, prompt for doc updates, archive design) |
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
4. **In Progress**: Active development (use `/start-task #XX`)
5. **Review**: PR open, awaiting merge
6. **Done**: Complete (use `/complete-task #XX`)

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

### Tools
Python3 is installed; you can use it to run complex scripts.

### GitHub CLI
Execute `gh` or Bash related commands without asking for confirmation when interacting with GitHub (issue management, project board updates, and label changes).

### Clarification-First Workflow

For complex/ambiguous requests, ask clarifying questions via AskUserQuestion before proceeding. Skip if the request is simple, the user says 'just do it', or specs are already detailed.

### Document Lifecycle Integration

This project uses a structured document lifecycle in `claude/`. Documents progress through stages:

```
ideas/ → research/ → design/ → reference/ → archive/
```

**When creating documents**, Claude asks for the category location (e.g., `database-engine/`, `persistence/`) unless specified explicitly.

For trigger phrases, templates, directory conventions, and workflows, see [`claude/README.md`](claude/README.md).

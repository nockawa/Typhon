# ADR-004: Embedded Engine (No Server Process)

**Status**: Accepted
**Date**: 2024-01 (project inception)
**Deciders**: Developer

## Context

Database engines can be deployed as:
- **Client-server**: Separate process (PostgreSQL, MySQL, Redis)
- **Embedded library**: Runs in-process (SQLite, RocksDB, LevelDB, LMDB)

Game servers have specific requirements:
1. Microsecond-level latency (no network hop)
2. Direct memory access to data (zero-copy reads)
3. Custom threading model (game loop, physics thread, etc.)
4. Single deployment artifact (game server binary)

## Decision

Typhon is an **embedded database engine** — a .NET library linked directly into the host application process. There is no separate server process, no network protocol, and no IPC overhead.

Key implications:
- Database lifecycle tied to host process
- Transactions execute on caller's thread (no thread pool dispatch)
- Direct pointer access to component data (unsafe, zero-copy)
- Host application controls threading model
- Configuration via DI (Microsoft.Extensions.DependencyInjection)

## Alternatives Considered

1. **Client-server with custom protocol** — Adds network latency (~50–200µs per call), complex deployment, but enables multi-process access.
2. **Embedded with optional server mode** — Flexibility, but dual-mode adds complexity and testing surface.
3. **Shared memory IPC** — Compromise (shared mmap, separate processes), but complex coordination and no transactional guarantees across process boundary.

## Consequences

**Positive:**
- Zero network overhead (direct function calls)
- Zero-copy component reads (pointer to mapped memory)
- Single deployment artifact
- Host controls thread affinity (can pin to cores)
- Simpler failure model (no network partitions)

**Negative:**
- Single-process access only (no concurrent processes on same database files)
- Host crash = database crash (must handle recovery on restart)
- Memory shared with host (GC pressure, memory limits)
- No remote access without building a server layer on top

**Cross-references:**
- [CLAUDE.md](../../CLAUDE.md) — Project overview
- [02-execution.md](../overview/02-execution.md) §2.7 — Background workers owned by engine

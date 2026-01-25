# Async-Aware Unit of Work

**Date:** 2026-01-22
**Status:** Worth exploring
**Captures:** Discussion about integrating async/await with Typhon's Unit of Work pattern

## Overview

This idea explores whether Typhon's `UnitOfWork` and `ExecutionContext` should integrate with .NET's async/await pattern via `AsyncLocal<T>`. The core insight is that async is a **user-level convenience** at the UoW boundary, while Typhon internals remain strictly synchronous for performance.

## Target Audience

- Typhon architecture decision-makers
- Developers evaluating Typhon for async workloads (ASP.NET Core, SignalR, gRPC)

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-the-proposal.md) | **The Proposal** | What async UoW means and why it matters |
| [02](./02-devils-advocate.md) | **Devil's Advocate** | Challenges and counter-arguments |
| [03](./03-use-cases.md) | **Use Cases Beyond Games** | Financial trading, web APIs, IoT, workflows |
| [04](./04-architecture.md) | **Architecture Sketch** | Layered design with async boundary |

## Key Insights

### The Boundary Separation Principle

Async is a **user-level convenience**, not something that infects Typhon's internals:

```
┌─────────────────────────────────────────────────────────┐
│  User Layer (async-friendly)                            │
│  - UoW with AsyncLocal<ExecutionContext>               │
│  - await allowed, CancellationToken propagation        │
├─────────────────────────────────────────────────────────┤
│  Typhon API Boundary                                    │
│  - Sync methods: tx.ReadEntity(), tx.UpdateComponent() │
│  - Optional async: FlushAsync() for I/O-bound          │
├─────────────────────────────────────────────────────────┤
│  Typhon Internals (sync, no AsyncLocal overhead)        │
│  - MVCC, B+Trees, page cache                           │
│  - ExecutionContext passed explicitly as parameter     │
│  - Patate: dedicated threads, no async                 │
└─────────────────────────────────────────────────────────┘
```

### The "One Request = One UoW" Generalization

If "one player command = one UoW" works for games, then:

| Domain | Request | UoW Mapping |
|--------|---------|-------------|
| MMORPG | Player command | One UoW per command |
| Web API | HTTP request | One UoW per request |
| IoT Gateway | Sensor batch | One UoW per device heartbeat |
| Trading Platform | Order execution | One UoW per order |
| Workflow Engine | Task step | One UoW per workflow step |

### Performance Cost is Acceptable

- AsyncLocal overhead (~50-100ns) paid **once at UoW entry**
- Inside Typhon, explicit `ExecutionContext ctx` parameters - no overhead
- Patate (sub-microsecond parallel processing) remains unaffected

## The Core Question

> **Could Typhon's async-aware UoW pattern become a universal primitive for request-scoped transactional work in .NET applications?**

The answer hinges on whether Typhon's combination of:
- Very low latency (microseconds)
- Multi-level atomic operations
- Durability guarantees
- Familiar .NET patterns

...provides a compelling alternative to existing solutions.

## Quick Reference

### What Makes This Different

| Aspect | Traditional ORMs | Typhon Async UoW |
|--------|------------------|------------------|
| Durability control | Per-commit | User-controlled (Deferred/Immediate) |
| Timeout | Connection-level | UoW-level, flows through all operations |
| Cancellation | Manual propagation | Automatic via AsyncLocal |
| Crash recovery | Transaction log | UoW-level atomic rollback |
| Performance | Milliseconds | Microseconds |

### Why This Matters for .NET Developers

- **Familiar async/await** - no learning curve for async patterns
- **ASP.NET Core integration** - middleware creates UoW per request
- **Distributed tracing** - ExecutionContext carries Activity context
- **Saga pattern** - clean compensation with UoW rollback

## Open Questions

1. **Scope creep?** - Is this beyond Typhon's core mission as a game database?
2. **Competitive advantage?** - Is this better than Entity Framework + PostgreSQL?
3. **Complexity cost?** - Does async context make debugging harder?
4. **Parent-child UoW** - How exactly should spawned UoWs coordinate?

## Related

- [02-execution.md](../../overview/02-execution.md) - Execution System component design
- [Patate Research](../../research/Patate.md) - Ultra-low-latency parallel processing

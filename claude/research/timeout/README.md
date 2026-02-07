# Database Engine Timeout Internals

A comprehensive 7-part deep-dive into how SQL Server, PostgreSQL, and MySQL/InnoDB handle timeouts internally.

## Overview

This series explores the design patterns, data structures, and implementation details of timeout handling in major database engines. It covers everything from the fundamental architectural decisions to specific implementation mechanics.

## Target Audience

- Systems programmers building timeout-aware systems
- Database internals enthusiasts
- Developers implementing lock managers, connection pools, or query executors
- Anyone curious about how "the 30 second timeout" actually works

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-timeout-taxonomy.md) | **Timeout Taxonomy** | Complete classification of all timeout types across 7 architectural layers |
| [02](./02-design-patterns.md) | **Design Patterns** | TimeSpan vs Deadline, Execution Context, Timeout Check Strategies |
| [03](./03-sql-server-internals.md) | **SQL Server Internals** | SQLOS, SOS_Task, Lock Manager, Query Executor timeout mechanics |
| [04](./04-postgresql-internals.md) | **PostgreSQL Internals** | Signal-based architecture, PGPROC, CHECK_FOR_INTERRUPTS |
| [05](./05-mysql-innodb-internals.md) | **MySQL/InnoDB Internals** | THD, trx_t, background timeout thread, two-layer architecture |
| [06](./06-comparative-analysis.md) | **Comparative Analysis** | Side-by-side comparison, pros/cons, decision matrix |
| [07](./07-design-guidelines.md) | **Design Guidelines** | Principles for building your own timeout systems |
| [Error Foundation](./ErrorFoundationTimeoutActivation.md) | **Error Foundation & Timeout Activation** | Research for #36: exception hierarchy, WaitContext.Null replacement, Result&lt;T&gt; |

## Key Insights

### The Fundamental Choice: Deadline vs TimeSpan

All three engines internally use **absolute deadlines** rather than relative timeouts:

```
Client says: "timeout = 30 seconds"
Engine stores: "deadline = now + 30s = 10:00:30"
All operations check: "is now >= deadline?"
```

This prevents timeout accumulation across nested calls.

### Architecture Comparison

| Engine | Model | Timeout Signal | Context Structure |
|--------|-------|----------------|-------------------|
| SQL Server | Thread + SQLOS | TDS Attention (0x06) | SOS_Task |
| PostgreSQL | Process | POSIX signals (SIGALRM) | PGPROC |
| MySQL | Thread + Storage Engine | Timer thread + killed flag | THD + trx_t |

### Timeout Check Locations

All engines check for timeout/cancellation at "yield points":
- After processing N rows (64-1000 typically)
- Before acquiring locks
- During/after I/O operations
- Between query plan operators
- At network I/O boundaries

## Quick Reference

### Default Timeout Values

| Timeout | SQL Server | PostgreSQL | MySQL |
|---------|------------|------------|-------|
| Command/Statement | 30s (client) | 0 (disabled) | 0 (disabled) |
| Lock wait | -1 (infinite) | 0 (disabled) | 50s |
| Deadlock detection | ~5s | 1s delay | Immediate |
| Idle connection | None | 0 (disabled) | 8 hours |

### Key Configuration Settings

**SQL Server:**
- `CommandTimeout` (client)
- `SET LOCK_TIMEOUT`
- Resource Governor `REQUEST_MAX_CPU_TIME_SEC`

**PostgreSQL:**
- `statement_timeout`
- `lock_timeout`
- `deadlock_timeout`
- `idle_in_transaction_session_timeout`

**MySQL:**
- `innodb_lock_wait_timeout`
- `max_execution_time`
- `lock_wait_timeout` (MDL)
- `wait_timeout`

## How to Use This Series

1. **Start with Part 1** if you want a complete understanding of all timeout types
2. **Start with Part 2** if you're designing your own system and want patterns
3. **Jump to Parts 3-5** if you're debugging a specific engine
4. **Read Part 6** for quick comparisons and decision guidance
5. **Reference Part 7** when implementing your own timeout system

## Diagrams

All documents use Mermaid diagrams for visualization. If viewing in an environment that doesn't render Mermaid, consider using:
- VS Code with Mermaid extension
- GitHub (renders Mermaid natively)
- Mermaid Live Editor (https://mermaid.live)

## License

This documentation is provided for educational purposes.

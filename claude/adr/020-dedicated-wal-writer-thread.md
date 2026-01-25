# ADR-020: Dedicated WAL Writer Thread (Not ThreadPool)

**Status**: Accepted
**Date**: 2025-01 (inferred from WAL design)
**Deciders**: Developer + Claude (design session)

## Context

The WAL writer drains the MPSC ring buffer and performs FUA (Force Unit Access) writes to disk. This thread must:

1. Wake up with minimal latency when signaled (for Immediate mode)
2. Never be delayed by ThreadPool exhaustion
3. Have predictable, consistent performance characteristics
4. Run continuously for the lifetime of the database

## Decision

Use a **dedicated OS thread** (not ThreadPool work item) for the WAL writer:

```csharp
// Dedicated thread, optionally pinned to a specific core
var walThread = new Thread(WalWriterLoop)
{
    Name = "Typhon-WAL-Writer",
    IsBackground = true,
    Priority = ThreadPriority.AboveNormal
};
walThread.Start();
```

**Wake mechanisms:**
- `AutoResetEvent` (or `ManualResetEventSlim`) for signal from Immediate-mode commits
- Timer-based wake for GroupCommit interval (every 5ms default)
- Threshold-based wake when ring buffer reaches N% full

## Alternatives Considered

1. **ThreadPool.QueueUserWorkItem** — Subject to ThreadPool exhaustion under load. Unpredictable wake latency (could wait for thread injection: ~500ms worst case).
2. **Task.Run (async)** — Same ThreadPool issues. Additionally, async I/O adds state machine overhead for what is a tight synchronous loop.
3. **Multiple writer threads** — Could improve throughput, but sequential write is already I/O optimal for NVMe. Multiple writers add ordering complexity.
4. **Inline in committing thread** — Each committer does its own FUA. Removes dedicated thread, but creates lock contention on WAL file and prevents batching.

## Consequences

**Positive:**
- Predictable wake latency (~1–10µs from signal to running)
- Immune to ThreadPool starvation (host app's async work doesn't affect WAL)
- Can pin to a specific CPU core for cache affinity
- AboveNormal priority ensures WAL writes aren't preempted by application work
- Single writer = sequential I/O = optimal for NVMe FTL

**Negative:**
- One additional OS thread always alive (minimal resource cost)
- Thread affinity is platform-specific (Linux `pthread_setaffinity_np`, Windows `SetThreadAffinityMask`)
- If thread crashes, database stops accepting durable commits (must detect and fail-fast)
- Dedicated thread burns a core during GroupCommit wake cycles (mitigated by sleep between flushes)

**Cross-references:**
- [02-execution.md](../overview/02-execution.md) §2.7 — Background workers
- [06-durability.md](../overview/06-durability.md) §6.1 — WAL writer responsibilities
- [ADR-012](012-mpsc-ring-buffer-wal.md) — Ring buffer (producer side)
- [ADR-026](026-separate-wal-ssd.md) — Separate SSD for WAL I/O

# 🥔 Ultra-Low Latency Parallel Processing 🥔

**Date:** 2026-01-16
**Status:** In progress
**Outcome:** TBD - Evaluating approaches for sub-microsecond parallel dispatch

**Related Documents:**
- [Architecture Deep Dive](Patate-Architecture.md)
- [Custom Primitives Toolbox](Patate-Primitives.md)

---

## Context

Typhon needs to process thousands of elements (entities) with extremely short per-element computation times (< 100 nanoseconds). The goal is to leverage multiple CPU cores (32+) to minimize total latency while supporting:

- **SIMD processing**: Elements processed in packs (e.g., 8 elements for 32-bit types)
- **Stage dependencies**: Stage C can depend on stages A and B completing first
- **Zero allocation**: Pre-allocated buffers from custom memory pools, no GC pressure
- **ECS queries**: Filter entities by component predicates, transform and store results

### Target Use Case

```
Query: All entities with components A, B, C, and D
Operation:
  1. Read components A, B, C
  2. Apply predicate filter (SIMD-accelerated)
  3. Process matching entities
  4. Write results to component D
```

### The Fundamental Challenge

```
1000 elements × 100ns each = 100 µs sequential

With SIMD (8 elements/pack): 125 packs × 100ns = 12.5 µs
With 32 cores: 12.5 µs / 32 = ~0.4 µs + synchronization overhead

If synchronization takes 5-50 µs, parallelization HURTS performance!
```

The challenge is achieving synchronization overhead **below 1 µs** to make parallelization worthwhile.

### Invocation Characteristics

| Parameter | Value |
|-----------|-------|
| Element count | Thousands (1,000 - 100,000) |
| Per-element time | < 100 nanoseconds |
| Invocation frequency | Thousands per second |
| Target cores | 32+ |
| SIMD width | 8 elements (32-bit) or 4 elements (64-bit) |

---

## Questions to Answer

1. What synchronization mechanisms achieve sub-microsecond overhead?
2. How to efficiently distribute SIMD-aligned work chunks?
3. How to handle stage dependencies with minimal latency?
4. How to balance dedicated threads vs CPU availability for other work?
5. What's the minimum element count where parallelization helps?

---

## Analysis

### Solution 1: Static Partitioning with Spin Barrier

**Description:** Pre-divide work statically at setup time. Each thread always processes the same portion. Use a spin-only barrier between stages.

```
Setup:
  Thread 0 → Elements [0, 249]
  Thread 1 → Elements [250, 499]
  Thread 2 → Elements [500, 749]
  Thread 3 → Elements [750, 999]

Execution:
  All threads wake → Process their portion → Spin barrier → Next stage
```

**Pros:**
- Extremely low overhead (~100-300 ns barrier cost)
- No dynamic work distribution
- Cache-friendly (each thread works on contiguous memory)
- Trivial to implement

**Cons:**
- Poor load balancing if work varies per element
- Thread count must match partition count
- Inflexible to runtime adjustments

**Best For:** Uniform per-element work, fixed thread count, simplest implementation

**Latency Characteristics:**

| Component | Estimated Cost |
|-----------|----------------|
| Thread wake (spin-waiting) | 50-200 ns |
| Barrier synchronization | 100-300 ns |
| **Total overhead per stage** | **~200-500 ns** |

---

### Solution 2: Chunked Work with Atomic Counter

**Description:** Divide work into more chunks than threads. Threads grab chunks via atomic increment until all chunks are processed.

```
Work: 1000 elements, 125 SIMD packs, divided into 32 chunks (~4 packs each)

Thread 0: grabs chunk 0 → processes → grabs chunk 5 → processes...
Thread 1: grabs chunk 1 → processes → grabs chunk 8 → processes...

Threads compete for next available chunk via atomic counter.
```

**Pros:**
- Good load balancing (fast threads grab more chunks)
- Works with any thread count
- SIMD-friendly (chunks aligned to SIMD width)
- Moderate implementation complexity

**Cons:**
- Atomic increment contention under high thread counts
- Slightly higher overhead than static partitioning
- Chunk size tuning required

**Best For:** Variable per-element work, flexible thread count, balance of simplicity and load balancing

**Latency Characteristics:**

| Component | Estimated Cost |
|-----------|----------------|
| Thread wake (spin-waiting) | 50-200 ns |
| Atomic increment (uncontended) | 10-20 ns |
| Atomic increment (contended, 32 threads) | 100-500 ns |
| Completion detection | 50-100 ns |
| **Total overhead per stage** | **~300-800 ns** |

---

### Solution 3: Work Stealing Deques

**Description:** Each thread has its own double-ended queue (deque). Threads push/pop from their own deque (LIFO for cache locality), steal from others (FIFO for load balancing).

```
Thread 0 deque: [chunk4, chunk3, chunk2, chunk1, chunk0] ← push/pop this end
Thread 1 deque: [chunk9, chunk8, chunk7, chunk6, chunk5] ← push/pop this end
                ↑ steal from this end

When Thread 0 finishes its work, it steals from Thread 1's deque.
```

**Pros:**
- Optimal load balancing
- Efficient for highly variable work
- Cache-friendly (own work is LIFO - temporal locality)
- Industry-proven pattern (used in .NET ThreadPool, Intel TBB)

**Cons:**
- Complex implementation (easy to get wrong)
- Higher per-operation overhead
- Memory barriers and CAS operations add latency
- Overkill for uniform work

**Best For:** Highly variable per-element work, task-parallel workloads, when optimal load balancing is critical

**Latency Characteristics:**

| Component | Estimated Cost |
|-----------|----------------|
| Push (owner) | 5-15 ns |
| Pop (owner, no contention) | 10-20 ns |
| Steal (successful) | 50-100 ns |
| Steal (contended) | 100-300 ns |
| **Total overhead per stage** | **~500-1500 ns** |

---

### Solution 4: Hybrid Chunked + Affinity

**Description:** Combine static partitioning for primary work with atomic counter for remainder. Threads have "affinity" chunks they always process first, then help with overflow.

```
Thread 0: Always processes chunks [0-3] first, then helps with remainder
Thread 1: Always processes chunks [4-7] first, then helps with remainder
...
Remainder pool: chunks [28-31] grabbed by whoever finishes first
```

**Pros:**
- Primary work has zero synchronization overhead
- Handles work variance via remainder pool
- Cache-optimal for affinity portions
- Good balance of latency and load balancing

**Cons:**
- Slightly more complex than pure chunked
- Tuning affinity vs remainder ratio
- Less effective if work is highly skewed

**Best For:** Mostly uniform work with occasional variance, cache-sensitive workloads

**Latency Characteristics:**

| Component | Estimated Cost |
|-----------|----------------|
| Thread wake | 50-200 ns |
| Affinity phase | 0 ns overhead |
| Remainder (if needed) | 10-100 ns per grab |
| Completion | 50-100 ns |
| **Total overhead per stage** | **~150-400 ns** |

---

### Solution 5: Pre-Computed Task Graph (DAG)

**Description:** Build a directed acyclic graph (DAG) of stages at setup time. Execute stages respecting dependencies with minimal runtime overhead.

```
        ┌──→ [Stage B] ──┐
[Stage A]                 ├──→ [Stage D]
        └──→ [Stage C] ──┘

Stages A, B, C can have internal parallelism.
Stage D waits for B and C via atomic dependency counter.
```

**Pros:**
- Arbitrary dependency patterns
- Maximum parallelism (independent stages run concurrently)
- Clean separation of graph definition and execution
- Reusable graphs across invocations

**Cons:**
- Higher setup cost
- More complex implementation
- Memory overhead for graph structure
- Overkill for simple linear pipelines

**Best For:** Complex multi-stage pipelines, DAG dependencies, when stages can run in parallel

**Latency Characteristics:**

| Component | Estimated Cost |
|-----------|----------------|
| Dependency decrement | 10-20 ns |
| Stage dispatch | 50-200 ns |
| Graph traversal | 20-50 ns per stage |
| **Total overhead per stage** | **~100-300 ns** |

---

### Solution 6: Ring Buffer Command Queue

**Description:** Main thread writes commands to a ring buffer. Worker threads spin on the buffer, consuming commands as they appear. Lock-free single-producer pattern.

```
Main thread:                  Ring Buffer:              Workers:
                             ┌─────────────┐
Write cmd 1 ───────────────► │ Cmd 1       │ ◄─────── Worker 0 reads
Write cmd 2 ───────────────► │ Cmd 2       │ ◄─────── Worker 1 reads
Write cmd 3 ───────────────► │ Cmd 3       │ ◄─────── Worker 2 reads
                             │ (empty)     │
                             └─────────────┘

Commands are work descriptors (stage + chunk range).
```

**Pros:**
- Very low latency dispatch
- Lock-free single-producer pattern
- Excellent for streaming workloads
- Decouples producer from consumers

**Cons:**
- Buffer sizing requires tuning
- Can lose work if buffer overflows
- More complex completion tracking
- Multi-producer requires additional synchronization

**Best For:** Streaming/continuous workloads, single-producer scenarios

**Latency Characteristics:**

| Component | Estimated Cost |
|-----------|----------------|
| Command dispatch | 10-30 ns |
| Command consume | 20-50 ns |
| Completion tracking | 50-100 ns |
| **Total overhead per dispatch** | **~100-200 ns** |

---

### Solutions to AVOID for Sub-Microsecond Work

These standard .NET solutions are **NOT suitable**:

| Solution | Typical Overhead | Problem |
|----------|------------------|---------|
| `Parallel.For` | 5-50 µs | ThreadPool queue overhead, task creation |
| `PLINQ` | 10-100 µs | Enumerable overhead, query compilation |
| `Task.WhenAll` | 2-20 µs per task | Allocation per task, scheduler overhead |
| `TPL Dataflow` | 1-10 µs per message | Complex state machines, coarse-grained design |
| `System.Threading.Barrier` | 1-15 µs | Kernel transitions when spinning fails |

**Why They Don't Work:**

```
Work: 1000 elements × 100ns = 100 µs total

Parallel.For overhead: ~20 µs
Net time on 32 cores: 100/32 + 20 = ~23 µs

Sequential: 100 µs
Parallel.For: 23 µs (4x speedup, not 32x)

Custom solution: 100/32 + 0.5 = ~3.6 µs (28x speedup)
```

---

## Comparison Matrix

| Solution | Overhead/Stage | Load Balance | Complexity | Best For |
|----------|----------------|--------------|------------|----------|
| Static Partitioning | ~200-500 ns | Poor | Low | Uniform work, simplicity |
| Chunked Atomic | ~300-800 ns | Good | Medium | Variable work, flexibility |
| Work Stealing | ~500-1500 ns | Excellent | Very High | Highly variable work |
| **Hybrid Affinity** | **~150-400 ns** | **Good** | **Medium** | **Recommended default** |
| Task Graph DAG | ~100-300 ns/stage | N/A | High | Complex dependencies |
| Ring Buffer | ~100-200 ns | Configurable | Medium | Streaming workloads |

---

## Detailed Design Considerations

### Thread Pool Configuration

**Option A: Fully Dedicated Threads**
```
Thread count = ProcessorCount
```
- **Pros:** Maximum throughput, predictable latency
- **Cons:** Starves other work (Typhon transactions, I/O, background tasks)

**Option B: Leave Headroom (Recommended)**
```
Thread count = ProcessorCount - 2
```
- **Pros:** Other work can proceed, better overall system behavior
- **Cons:** Slightly lower peak throughput

**Option C: Configurable with Scaling**
```
Min threads = ProcessorCount / 2
Max threads = ProcessorCount - 1
Scale based on queue depth
```
- **Pros:** Balances throughput and system responsiveness
- **Cons:** More complex, scaling decision adds latency

### SIMD Alignment Requirements

Chunks must align with SIMD boundaries for efficient vector processing:

| Element Type | SIMD Width (AVX2) | Elements per Vector | Recommended Chunk Size |
|--------------|-------------------|---------------------|------------------------|
| 32-bit (int, float) | 256 bits | 8 | 32, 64, or 128 elements |
| 64-bit (long, double) | 256 bits | 4 | 16, 32, or 64 elements |
| 16-bit (short) | 256 bits | 16 | 64, 128, or 256 elements |

Chunk size should be: `SIMD_WIDTH × N` where N balances overhead vs load balance.

### False Sharing Mitigation

Control structures shared between threads must be padded to cache line boundaries (64 bytes) to prevent false sharing:

```
Bad:  [Counter1][Counter2][Counter3] ← All in same cache line, ping-pong between cores
Good: [Counter1][Padding...][Counter2][Padding...] ← Each in separate cache line
```

### Completion Notification Strategies

**Synchronous (Spin-wait):**
- Main thread spins until completion counter reaches expected value
- Lowest latency for short waits
- Burns CPU cycles

**Asynchronous (Event + TaskCompletionSource):**
- Last worker signals event, completes Task
- Caller can await without blocking
- Slightly higher latency (~100-500 ns for event signal)

### Thread Safety Integration

Thread safety for shared data structures will leverage existing Typhon primitives:
- **`NewAccessControl`**: Full-featured reader-writer lock with telemetry support
- **`AccessControlSmall`**: Compact 4-byte reader-writer lock for space-constrained scenarios

These provide the shared/exclusive access patterns needed for:
- Stage result buffers (shared read during next stage, exclusive write during current)
- Work queue access (if using shared queue patterns)
- Completion state updates

---

## Recommendation

**Primary Solution: Hybrid Affinity + Chunked Atomic Counter**

This approach combines:
1. Static affinity for primary work (zero synchronization overhead)
2. Atomic counter for remainder/overflow (good load balancing)
3. Spin barrier for stage transitions (minimal latency)
4. DAG scheduler for stage dependencies (flexible pipeline definition)

See [Architecture Deep Dive](Patate-Architecture.md) for detailed design.

---

## Next Steps

- [ ] Review Architecture Deep Dive document
- [ ] Review Custom Primitives Toolbox document
- [ ] Prototype core components
- [ ] Benchmark against sequential and SIMD-only baselines
- [ ] Determine crossover point for parallelization benefit
- [ ] Design Typhon query integration API

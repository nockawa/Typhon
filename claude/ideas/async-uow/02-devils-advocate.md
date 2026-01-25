# Part 2: Devil's Advocate

This document captures the challenges raised against async UoW and how they were addressed.

---

## Challenge 1: Durability Semantics Break Down

### The Argument

With `DurabilityMode.Immediate`, each commit is fsynced. If user awaits between transactions:

```csharp
using var uow = db.CreateUnitOfWork(DurabilityMode.Immediate);

tx1.Commit();  // Fsynced - DURABLE

await httpClient.PostAsync(...);  // External call - 10 seconds

// CRASH HERE - tx1 is durable, tx2 never happened
// Alice lost gold but payment wasn't processed!

tx2.Commit();
```

Async makes it "natural" to write code that creates inconsistent states between Typhon and external systems.

### The Counter-Argument

**This problem exists without async too:**

```csharp
tx1.Commit();  // Durable
Thread.Sleep(30000);  // Or sync HTTP call, or heavy computation
// CRASH - same problem!
tx2.Commit();
```

Async doesn't *create* this issue, it just makes it more *natural* to write. The real answer:

1. **User education** - Document that `Immediate` + external calls between transactions is risky
2. **Guidance** - Recommend single-transaction UoWs for critical operations
3. **Pattern** - External calls should happen *before* or *after* the UoW, not interleaved

### Verdict: Challenge Withdrawn

This is a documentation/guidance problem, not an async-specific architectural flaw.

---

## Challenge 2: Lock Holding Across Awaits

### The Argument

Transactions hold MVCC state. If held across long awaits:

```csharp
using var tx = uow.CreateTransaction();
var player = tx.ReadEntity<Player>(playerId);  // Snapshot taken

await Task.Delay(30000);  // Simulating slow external call

tx.UpdateComponent<Position>(playerId, ...);
tx.Commit();
```

The transaction is "alive" for 30 seconds:
- MVCC revision chains grow (can't GC old revisions)
- Concurrent transactions on same entities will conflict
- Memory pressure increases

### The Counter-Argument

**Same as Challenge 1** - this exists without async:

```csharp
var player = tx.ReadEntity<Player>(playerId);
Thread.Sleep(30000);  // Same problem!
tx.Commit();
```

The guidance is the same: don't hold transactions open across I/O. Async doesn't change this rule, it just makes violations easier to spot in code reviews.

### Verdict: Challenge Withdrawn

User education issue, not architectural.

---

## Challenge 3: Deadline Meaningless With Unbounded Awaits

### The Argument

The `Deadline` struct enforces timeout within Typhon operations. But external awaits don't check it:

```csharp
using var uow = db.CreateUnitOfWork(timeout: TimeSpan.FromMilliseconds(500));

await externalService.SlowCallAsync();  // Takes 5 seconds!
// Deadline expired 4.5 seconds ago, but we're still running
```

Typhon's `ThrowIfCancelled()` isn't called during the external await.

### The Counter-Argument

**CancellationToken propagation solves this:**

```csharp
await externalService.SlowCallAsync(uow.CancellationToken);
// If deadline expires, token is cancelled, call throws
```

This is idiomatic .NET. ASP.NET Core does the same with `HttpContext.RequestAborted`. Users must pass the token to external calls - if they don't, deadline enforcement is weakened. But that's user discipline, not architectural failure.

### Verdict: Challenge Addressed

Pattern is familiar; enforcement via `CancellationToken` propagation is standard .NET practice.

---

## Challenge 4: Parent-Child UoW = Distributed Transactions

### The Argument

The proposal mentioned "parent-child UoW relationships with shared deadline/cancellation." This sounds like nested transactions or distributed transactions:

```csharp
using var parentUow = db.CreateUnitOfWork();
await SpawnChildOperation();  // Creates child UoW

// What if child flushes but parent doesn't?
// What if parent times out while child is mid-flush?
```

This reintroduces the complexity that was explicitly avoided by making UoWs "flat, not nestable."

### The Counter-Argument

**Clarification: This is task coordination, not transaction nesting.**

The "parent-child" terminology was misleading. What's actually proposed:

```csharp
using var parentUow = db.CreateUnitOfWork();

// Spawn INDEPENDENT UoW on thread pool
var childTask = Task.Run(() => {
    using var childUow = db.CreateUnitOfWork();
    // ... independent work ...
    childUow.Flush();  // Independently durable
});

// Parent can wait for child completion (like Task.Wait)
await childTask;

parentUow.Flush();  // Parent's own durability
```

The UoWs are **independent** - they don't share transaction state. The "relationship" is purely for:
- Linked `CancellationToken` (parent cancellation propagates to children)
- Task coordination (parent can await children)

This is standard TPL, not distributed transactions.

### Verdict: Challenge Withdrawn

Misunderstanding clarified. No transaction nesting, just task coordination.

---

## Challenge 5: AsyncLocal Performance Costs

### The Argument

`AsyncLocal<T>` has overhead on every await:

```
Every await point:
1. Capture current ExecutionContext (~20-30ns)
2. Store in Task state machine (~10-20ns)
3. Restore on continuation (~20-30ns)
4. Check for changes (~10-20ns)

Total: ~50-100ns per await
```

For Patate research targeting sub-microsecond operations, this is 10-50% of the budget.

### The Counter-Argument

**The key insight: async doesn't exist inside Typhon.**

```
┌─────────────────────────────────────────┐
│  User Layer: AsyncLocal overhead HERE   │  ← 50-100ns per await
├─────────────────────────────────────────┤
│  Typhon Boundary: Context captured ONCE │  ← One-time cost
├─────────────────────────────────────────┤
│  Typhon Internals: Explicit parameters  │  ← ZERO overhead
│  - B+Tree traversal                     │
│  - MVCC checks                          │
│  - Page cache lookups                   │
│  - Patate parallel processing           │
└─────────────────────────────────────────┘
```

The AsyncLocal overhead is paid at the **user level**, not in the hot paths. Inside Typhon, it's `ExecutionContext ctx` parameters all the way down - no AsyncLocal lookups.

### Verdict: Challenge Addressed

Performance-critical paths are protected. Overhead is only at user boundary.

---

## Challenge 6: Testing and Debugging Nightmares

### The Argument

With explicit `ExecutionContext ctx` parameters:
- Visible in debugger at every call site
- Easy to mock in tests
- Clear flow through code

With `AsyncLocal<ExecutionContext>`:
- Context is invisible in call signatures
- `ExecutionContext.Current` can be null, wrong, or stale
- Race conditions with capture/restore
- Test isolation requires careful setup

### The Counter-Argument

**This applies to all async/await code, not Typhon specifically.**

Modern tooling handles this well:
- Visual Studio / Rider Task windows
- Async callstack debugging
- `AsyncLocal` is well-understood by .NET developers

If this argument held, nobody would use `HttpContext.Current` or `IHttpContextAccessor` - but they do, successfully.

### Verdict: Challenge Withdrawn

Not Typhon-specific; ecosystem handles this well.

---

## Summary of Challenges

| # | Challenge | Verdict | Resolution |
|---|-----------|---------|------------|
| 1 | Durability breaks with awaits between tx | Withdrawn | Same problem exists without async; user guidance |
| 2 | Lock holding across awaits | Withdrawn | Same problem exists without async; user guidance |
| 3 | Deadline meaningless during external awaits | Addressed | CancellationToken propagation is idiomatic |
| 4 | Parent-child = distributed transactions | Withdrawn | Clarified: task coordination, not tx nesting |
| 5 | AsyncLocal performance overhead | Addressed | Overhead only at user boundary, not internals |
| 6 | Testing/debugging complexity | Withdrawn | Not Typhon-specific; tooling handles it |

## Remaining Concerns (Not Blockers)

1. **User Guidance Needed**: Document risks of `Immediate` mode with interleaved external calls
2. **CancellationToken Discipline**: Users must pass tokens to external calls
3. **Sync-over-Async Warning**: Blocking inside async code can starve thread pool

These are documentation and education concerns, not architectural blockers.

---

## Next: Use Cases

See [03-use-cases.md](./03-use-cases.md) for applications beyond games.

# Part 1: The Proposal

**Question:** Could Unit of Work be async-aware using .NET's `AsyncLocal<T>`?

---

## The Observation

Typhon's primary use case (game servers) often involves:
- Player commands arriving over network (async I/O)
- ASP.NET Core or custom TCP servers (async pipelines)
- Commands that may call external services (payment, matchmaking)
- Need for timeout and cancellation to propagate through the stack

The .NET TPL includes `AsyncLocal<T>`, which automatically flows values through async/await continuations across thread pool threads. This is how ASP.NET Core's `HttpContext.Current` works.

## The Proposal

Make Typhon's `ExecutionContext` flow via `AsyncLocal<T>` at the UoW boundary:

```csharp
// User code - async-friendly
app.MapPost("/trade", async (TradeRequest trade, TyphonDb db) =>
{
    await using var uow = db.CreateUnitOfWorkAsync(
        timeout: TimeSpan.FromMilliseconds(500));

    // Validate with external service (async)
    await complianceService.ValidateAsync(trade, uow.CancellationToken);

    // Typhon operations (sync, but context flows)
    using var tx = uow.CreateTransaction();
    tx.UpdateComponent<Inventory>(trade.SellerId, inv => inv.RemoveItem(trade.ItemId));
    tx.UpdateComponent<Inventory>(trade.BuyerId, inv => inv.AddItem(trade.ItemId));
    tx.Commit();

    await uow.FlushAsync();  // Async I/O for durability

    return Results.Ok();
});
```

## What "Async UoW" Means

### 1. User Code Can Await Inside UoW

The UoW scope can span across `await` points:

```csharp
using var uow = db.CreateUnitOfWork();

await Step1Async();  // Thread pool thread A
// ... await returns on thread B
await Step2Async();  // Thread pool thread C
// ... await returns on thread D

tx.Commit();  // Still same UoW, context flowed through
```

### 2. ExecutionContext Flows Across Threads

`AsyncLocal<ExecutionContext>` ensures the context is captured and restored:

```csharp
// Conceptually
public static class TyphonContext
{
    private static readonly AsyncLocal<ExecutionContext> _current = new();

    public static ExecutionContext? Current
    {
        get => _current.Value;
        internal set => _current.Value = value;
    }
}
```

### 3. CancellationToken Propagates to External Calls

The UoW's deadline-derived `CancellationToken` can be passed to any async operation:

```csharp
await httpClient.PostAsync(url, content, uow.CancellationToken);
// If UoW deadline expires, this call is cancelled
```

### 4. Typhon Internals Remain Synchronous

**Critical distinction**: async is only at the user boundary. Inside Typhon:

```csharp
// Inside Typhon - explicit parameter passing, no AsyncLocal
internal void ProcessPage(ExecutionContext ctx, Page page)
{
    ctx.ThrowIfCancelled();  // Explicit check
    // ... synchronous processing
}
```

## Why This Matters

### For .NET Developers

| Benefit | Description |
|---------|-------------|
| **Familiar patterns** | async/await works as expected |
| **No ceremony** | Context flows automatically |
| **Tooling support** | Visual Studio Task window, async debugging |
| **Interop** | Works with HttpClient, gRPC, SignalR |

### For Typhon

| Benefit | Description |
|---------|-------------|
| **Broader adoption** | Not just game servers |
| **ASP.NET Core integration** | Middleware pattern fits naturally |
| **Modern .NET** | Aligns with ecosystem expectations |

## The Key Insight

> **Async doesn't exist inside Typhon's implementation** - it's a user-facing convenience.

The AsyncLocal overhead (~50-100ns per await) is paid at the **user level**, not in Typhon's hot paths. The B+Tree traversal, MVCC checks, page cache lookups - all remain synchronous with explicit parameter passing.

This is the same pattern ASP.NET Core uses: the request pipeline is async, but Kestrel's inner loops are carefully synchronous.

## Design Implications

### UnitOfWork API

```csharp
public sealed class UnitOfWork : IAsyncDisposable, IDisposable
{
    // Sync creation (for compatibility)
    public static UnitOfWork Create(TimeSpan timeout, DurabilityMode mode);

    // Async-aware creation (sets up AsyncLocal)
    public static ValueTask<UnitOfWork> CreateAsync(TimeSpan timeout, DurabilityMode mode);

    // Context access
    public ExecutionContext Context { get; }

    // Flush options
    public void Flush();            // Sync
    public Task FlushAsync();       // Async I/O
}
```

### Transaction API

Transactions remain synchronous - database operations are fast:

```csharp
public sealed class Transaction : IDisposable
{
    // All sync - no async needed for in-memory operations
    public bool ReadEntity<T>(long id, out T component);
    public void UpdateComponent<T>(long id, Action<T> mutator);
    public void Commit();
    public void Rollback();
}
```

### ExecutionContext

```csharp
public sealed class ExecutionContext
{
    // Static ambient access (via AsyncLocal)
    public static ExecutionContext? Current => TyphonContext.Current;

    // Instance members
    public Deadline Deadline { get; }
    public CancellationToken CancellationToken { get; }
    public bool IsInHoldoff { get; }

    // Distributed tracing integration
    public Activity? Activity { get; }
}
```

## Next: Challenges

See [02-devils-advocate.md](./02-devils-advocate.md) for counter-arguments and how they were addressed.

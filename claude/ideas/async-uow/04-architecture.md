# Part 4: Architecture Sketch

How the async-aware UoW would be structured in Typhon.

---

## Layered Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│  User Application Layer                                             │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  ASP.NET Core / SignalR / gRPC / Custom TCP                   │  │
│  │  - Async request handling                                      │  │
│  │  - await external services                                     │  │
│  │  - Uses UoW per request/command                                │  │
│  └───────────────────────────────────────────────────────────────┘  │
│                                 │                                    │
│                                 ▼                                    │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  Unit of Work (async boundary)                                 │  │
│  │  - AsyncLocal<ExecutionContext>                                │  │
│  │  - CreateUnitOfWorkAsync() / await using                       │  │
│  │  - FlushAsync() for async I/O                                  │  │
│  └───────────────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────────────┤
│  Typhon Public API                                                  │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  Transaction API (synchronous)                                 │  │
│  │  - tx.ReadEntity<T>(), tx.UpdateComponent<T>()                 │  │
│  │  - tx.Commit(), tx.Rollback()                                  │  │
│  │  - No async - operations are fast                              │  │
│  └───────────────────────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  Query API (synchronous, future async for large scans)        │  │
│  │  - db.Query<T>().Where(...).ToList()                           │  │
│  │  - Uses ExecutionContext for timeout/cancellation              │  │
│  └───────────────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────────────┤
│  Typhon Engine (synchronous, explicit context)                      │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  MVCC / Transaction Manager                                    │  │
│  │  - ExecutionContext passed as parameter                        │  │
│  │  - ctx.ThrowIfCancelled() at yield points                      │  │
│  └───────────────────────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  B+Tree Indexes / Component Tables                             │  │
│  │  - Pure synchronous operations                                 │  │
│  │  - Explicit context parameter                                  │  │
│  └───────────────────────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  Page Cache / Persistence                                      │  │
│  │  - Synchronous reads (from cache)                              │  │
│  │  - Async writes via FlushAsync() path                          │  │
│  └───────────────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────────────┤
│  Patate (Future: Ultra-Low-Latency Parallel)                        │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  Dedicated threads, spin barriers                              │  │
│  │  - NO async, NO AsyncLocal                                     │  │
│  │  - Sub-microsecond synchronization                             │  │
│  └───────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Key Design Decisions

### 1. AsyncLocal Only at UoW Boundary

```csharp
public sealed class UnitOfWork : IAsyncDisposable
{
    private static readonly AsyncLocal<ExecutionContext?> _current = new();

    public static ExecutionContext? Current => _current.Value;

    public static async ValueTask<UnitOfWork> CreateAsync(
        TimeSpan timeout,
        DurabilityMode durability = DurabilityMode.Deferred)
    {
        var ctx = ExecutionContextPool.Rent(
            Deadline.FromTimeout(timeout),
            durability);

        // Set ambient context
        _current.Value = ctx;

        return new UnitOfWork(ctx);
    }

    public async ValueTask DisposeAsync()
    {
        // Clear ambient context
        _current.Value = null;

        // Return to pool
        ExecutionContextPool.Return(_context);
    }
}
```

### 2. Transaction Uses Context from UoW

```csharp
public sealed class Transaction : IDisposable
{
    private readonly ExecutionContext _ctx;

    internal Transaction(UnitOfWork uow)
    {
        _ctx = uow.Context;
        // Start transaction with MVCC snapshot
    }

    public bool ReadEntity<T>(long id, out T component) where T : unmanaged
    {
        _ctx.ThrowIfCancelled();  // Check at entry
        // ... read from component table
    }

    public void UpdateComponent<T>(long id, Action<T> mutator) where T : unmanaged
    {
        _ctx.ThrowIfCancelled();
        // ... update component
    }

    public void Commit()
    {
        _ctx.ThrowIfCancelled();
        // ... commit transaction
    }
}
```

### 3. Internal Methods Take Explicit Context

```csharp
// Inside Typhon - NO AsyncLocal access
internal class ComponentTable<T>
{
    public bool TryRead(
        ExecutionContext ctx,  // Explicit!
        long entityId,
        long snapshotTick,
        out T component)
    {
        ctx.ThrowIfCancelled();

        // ... B+Tree lookup, revision chain walk
    }
}

internal class BTreeNode
{
    public int Search(
        ExecutionContext ctx,  // Explicit!
        Span<byte> key)
    {
        // NO ctx.ThrowIfCancelled() here - too fine-grained
        // Checked at higher level
    }
}
```

### 4. FlushAsync for Async I/O

```csharp
public sealed class UnitOfWork
{
    public void Flush()
    {
        // Synchronous fsync
        _persistence.FlushSync(_pendingChanges);
    }

    public async Task FlushAsync()
    {
        // Async I/O - releases thread during disk write
        await _persistence.FlushAsync(_pendingChanges, _context.CancellationToken);
    }
}
```

---

## Context Propagation Flow

```
CreateUnitOfWorkAsync(timeout: 500ms)
    │
    ├── 1. Create ExecutionContext from pool
    │       - Deadline = now + 500ms
    │       - CancellationToken from deadline
    │
    ├── 2. Set AsyncLocal<ExecutionContext>.Value = ctx
    │
    └── 3. Return UnitOfWork

await externalService.CallAsync(uow.CancellationToken)
    │
    └── (Context flows via AsyncLocal through await)

uow.CreateTransaction()
    │
    ├── 1. Get ctx from UoW (not AsyncLocal!)
    │
    └── 2. Create Transaction with ctx

tx.ReadEntity<Player>(id)
    │
    ├── 1. ctx.ThrowIfCancelled()
    │
    └── 2. componentTable.TryRead(ctx, id, ...)
              │
              └── 3. ctx.ThrowIfCancelled() (at entry)

await uow.FlushAsync()
    │
    ├── 1. Async disk I/O
    │
    └── 2. (Context continues to flow)

DisposeAsync()
    │
    ├── 1. Clear AsyncLocal
    │
    └── 2. Return ctx to pool
```

---

## ExecutionContext Design

```csharp
public sealed class ExecutionContext
{
    // Identity
    internal long UnitOfWorkId { get; }

    // Deadline (monotonic time)
    public Deadline Deadline { get; }
    public bool IsExpired => Deadline.IsExpired;
    public TimeSpan Remaining => Deadline.Remaining;

    // Cancellation
    private readonly CancellationTokenSource _cts;
    public CancellationToken CancellationToken => _cts.Token;
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    // Holdoff (critical sections)
    private int _holdoffCount;
    public bool IsInHoldoff => _holdoffCount > 0;
    public void BeginHoldoff() => Interlocked.Increment(ref _holdoffCount);
    public void EndHoldoff() => Interlocked.Decrement(ref _holdoffCount);

    // Yield point check
    public void ThrowIfCancelled()
    {
        if (_holdoffCount > 0) return;  // In critical section

        if (Deadline.IsExpired)
            throw new TimeoutException("Operation deadline expired");

        _cts.Token.ThrowIfCancellationRequested();
    }

    // Distributed tracing
    public Activity? Activity { get; internal set; }

    // Wait state tracking (diagnostics)
    public string? CurrentWaitType { get; internal set; }
    public long WaitStartTicks { get; internal set; }

    // Durability mode
    public DurabilityMode DurabilityMode { get; }

    // Pool integration
    internal void Reset()
    {
        _cts.TryReset();
        _holdoffCount = 0;
        Activity = null;
        CurrentWaitType = null;
    }
}
```

---

## Pooling Strategy

```csharp
internal static class ExecutionContextPool
{
    private static readonly ConcurrentQueue<ExecutionContext> _pool = new();
    private const int MaxPoolSize = 256;

    public static ExecutionContext Rent(Deadline deadline, DurabilityMode mode)
    {
        if (_pool.TryDequeue(out var ctx))
        {
            ctx.Initialize(deadline, mode);
            return ctx;
        }

        return new ExecutionContext(deadline, mode);
    }

    public static void Return(ExecutionContext ctx)
    {
        ctx.Reset();

        if (_pool.Count < MaxPoolSize)
        {
            _pool.Enqueue(ctx);
        }
        // else: let GC collect it
    }
}
```

---

## ASP.NET Core Integration Example

```csharp
// Middleware
public class TyphonUnitOfWorkMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context, TyphonDb db)
    {
        // Timeout from request deadline or default
        var timeout = context.RequestAborted.CanBeCanceled
            ? TimeSpan.FromSeconds(30)  // Or calculate remaining
            : TimeSpan.FromSeconds(30);

        await using var uow = await db.CreateUnitOfWorkAsync(timeout);

        // Link request abort to UoW cancellation
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            uow.CancellationToken);

        // Store for DI
        context.Features.Set<IUnitOfWork>(uow);

        try
        {
            await _next(context);

            // Success - persist
            if (!uow.IsRolledBack)
            {
                await uow.FlushAsync();
            }
        }
        catch
        {
            // Exception - UoW not flushed = rollback
            throw;
        }
    }
}

// DI registration
services.AddScoped<IUnitOfWork>(sp =>
{
    var context = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
    return context.Features.Get<IUnitOfWork>()!;
});

// Controller usage
[ApiController]
public class UsersController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public UsersController(IUnitOfWork uow) => _uow = uow;

    [HttpPost]
    public IActionResult Create(CreateUserRequest request)
    {
        using var tx = _uow.CreateTransaction();
        var id = tx.CreateEntity(new User { Name = request.Name });
        tx.Commit();

        return CreatedAtAction(nameof(Get), new { id }, new { Id = id });
    }
}
```

---

## What This Enables

| Pattern | How It Works |
|---------|--------------|
| **Request-scoped UoW** | Middleware creates/disposes automatically |
| **Automatic rollback** | Exception = UoW disposed without flush |
| **Timeout propagation** | Deadline flows through all operations |
| **Cancellation** | Request abort cancels all Typhon work |
| **Distributed tracing** | Activity flows via ExecutionContext |
| **DI integration** | IUnitOfWork injectable per-request |

---

## Open Implementation Questions

1. **Should `ExecutionContext.Current` be public?**
   - Pro: Convenient deep access without parameter drilling
   - Con: Can be null if accessed outside UoW scope

2. **Should Transaction support `CommitAsync()`?**
   - Currently proposed as sync (fast operation)
   - Async variant could be useful for very large transactions

3. **How to handle nested awaits that spawn new threads?**
   - `Task.Run()` captures `AsyncLocal` - this works
   - `ThreadPool.QueueUserWorkItem` does NOT capture - document this

4. **Should Patate operations check the ExecutionContext?**
   - Currently: No, Patate is separate
   - Could add: Optional cancellation check at stage boundaries

---

## Summary

The architecture maintains a clear boundary:

| Layer | Async Model | Context Access |
|-------|-------------|----------------|
| User code | Full async/await | AsyncLocal (ambient) |
| UoW boundary | async creation/disposal | Sets/clears AsyncLocal |
| Transaction API | Synchronous | From UoW (not AsyncLocal) |
| Typhon internals | Synchronous | Explicit parameter |
| Patate | Dedicated threads | None (or explicit) |

This provides the best of both worlds:
- **User convenience**: Familiar async patterns, automatic context flow
- **Internal performance**: No AsyncLocal overhead in hot paths
- **Flexibility**: Can still use explicit parameters when needed

# Part 3: Use Cases Beyond Games

If Typhon's UoW pattern works for game commands, what else could it power?

---

## The Generalization

| Domain | Request | UoW Mapping | Durability |
|--------|---------|-------------|------------|
| **MMORPG** | Player command | One UoW per command | Mixed |
| **Web API** | HTTP request | One UoW per request | Usually Immediate |
| **IoT Gateway** | Sensor batch | One UoW per heartbeat | Deferred (batch) |
| **Trading Platform** | Order execution | One UoW per order | Immediate |
| **Workflow Engine** | Task step | One UoW per step | Immediate |
| **Collaborative Editing** | User operation | One UoW per edit | Deferred |

---

## Use Case 1: Financial Trading Platform

### Scenario

High-frequency trading platform with:
- Sub-millisecond latency requirements
- Strict durability for money movement
- External calls to exchanges and compliance services

### Code Pattern

```csharp
app.MapPost("/orders", async (OrderRequest order, TyphonDb db) =>
{
    await using var uow = db.CreateUnitOfWorkAsync(
        timeout: TimeSpan.FromMilliseconds(50),  // HFT demands speed!
        durability: DurabilityMode.Immediate);   // Money is involved

    // Step 1: External compliance check
    await complianceService.ValidateAsync(order, uow.CancellationToken);

    // Step 2: Reserve funds (Typhon)
    using var tx = uow.CreateTransaction();
    var account = tx.ReadEntity<Account>(order.AccountId);
    if (account.Balance < order.Quantity * order.LimitPrice)
        return Results.BadRequest("Insufficient funds");

    tx.UpdateComponent<Account>(order.AccountId, a =>
        a.ReservedBalance += order.Quantity * order.LimitPrice);
    tx.Commit();

    // Step 3: Submit to exchange (external)
    var exchangeResult = await exchange.SubmitOrderAsync(
        order, uow.CancellationToken);

    // Step 4: Record order (Typhon)
    using var tx2 = uow.CreateTransaction();
    tx2.CreateEntity(new Order {
        AccountId = order.AccountId,
        ExchangeOrderId = exchangeResult.OrderId,
        Status = OrderStatus.Pending
    });
    tx2.Commit();

    await uow.FlushAsync();

    return Results.Ok(exchangeResult);
});
```

### Why Typhon Fits

| Requirement | Typhon Provides |
|-------------|-----------------|
| Sub-ms latency | Microsecond operations |
| Durability | Immediate mode with fsync |
| Timeout | 50ms deadline enforced |
| Audit trail | MVCC revision history |

---

## Use Case 2: ASP.NET Core Web API

### Scenario

Traditional web API with:
- Request-scoped transactions
- Need for automatic rollback on errors
- Distributed tracing requirements

### Code Pattern: Middleware Integration

```csharp
public class UnitOfWorkMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context, TyphonDb db)
    {
        var timeout = context.RequestAborted.CanBeCanceled
            ? GetRemainingTimeout(context)
            : TimeSpan.FromSeconds(30);

        await using var uow = db.CreateUnitOfWorkAsync(timeout);

        // Store for DI resolution
        context.Items["UoW"] = uow;

        try
        {
            await _next(context);

            // Success - make durable
            await uow.FlushAsync();
        }
        catch
        {
            // UoW disposed without flush = automatic rollback
            throw;
        }
    }
}

// In endpoint
app.MapPost("/users", async (CreateUserRequest req, HttpContext context) =>
{
    var uow = (UnitOfWork)context.Items["UoW"]!;

    using var tx = uow.CreateTransaction();
    var userId = tx.CreateEntity(new User { Name = req.Name });
    tx.Commit();

    return Results.Created($"/users/{userId}", new { Id = userId });
});
```

### Why Typhon Fits

| Requirement | Typhon Provides |
|-------------|-----------------|
| Request scope | UoW per request |
| Auto-rollback | Dispose without flush |
| Tracing | ExecutionContext carries Activity |
| Performance | Faster than PostgreSQL round-trips |

---

## Use Case 3: IoT Gateway

### Scenario

IoT gateway receiving sensor data:
- Thousands of devices reporting every second
- Batch processing for efficiency
- Occasional critical alerts need immediate durability

### Code Pattern

```csharp
// Batch processing - deferred durability
async Task ProcessSensorBatch(IEnumerable<SensorReading> readings)
{
    await using var uow = db.CreateUnitOfWorkAsync(
        timeout: TimeSpan.FromSeconds(1),
        durability: DurabilityMode.Deferred);  // Batch for efficiency

    foreach (var reading in readings)
    {
        using var tx = uow.CreateTransaction();

        tx.UpdateComponent<DeviceState>(reading.DeviceId, state =>
        {
            state.LastReading = reading.Value;
            state.LastSeen = reading.Timestamp;
        });

        // Check for alerts
        if (reading.Value > state.AlertThreshold)
        {
            // Critical alert - spawn immediate UoW
            await ProcessAlertAsync(reading, uow.CancellationToken);
        }

        tx.Commit();
    }

    await uow.FlushAsync();  // Single fsync for entire batch
}

// Critical path - immediate durability
async Task ProcessAlertAsync(SensorReading reading, CancellationToken ct)
{
    await using var alertUow = db.CreateUnitOfWorkAsync(
        timeout: TimeSpan.FromMilliseconds(100),
        durability: DurabilityMode.Immediate);  // Must be durable!

    using var tx = alertUow.CreateTransaction();
    tx.CreateEntity(new Alert {
        DeviceId = reading.DeviceId,
        Value = reading.Value,
        Timestamp = reading.Timestamp
    });
    tx.Commit();

    await alertUow.FlushAsync();

    // Notify monitoring system
    await notificationService.SendAlertAsync(reading, ct);
}
```

### Why Typhon Fits

| Requirement | Typhon Provides |
|-------------|-----------------|
| High throughput | Batched fsync |
| Low latency | Microsecond updates |
| Mixed durability | Deferred + Immediate modes |
| Time-series | MVCC revision history |

---

## Use Case 4: Workflow Engine

### Scenario

Long-running workflow with:
- Steps that may take minutes
- Need for crash recovery
- Compensation logic on failure

### Code Pattern

```csharp
async Task ExecuteWorkflowStep(WorkflowInstance workflow, StepDefinition step)
{
    await using var uow = db.CreateUnitOfWorkAsync(
        timeout: step.Timeout,
        durability: DurabilityMode.Immediate);  // Each step durable

    using var tx = uow.CreateTransaction();

    // Mark step as in-progress
    tx.UpdateComponent<WorkflowInstance>(workflow.Id, w =>
    {
        w.CurrentStep = step.Id;
        w.Status = WorkflowStatus.InProgress;
    });
    tx.Commit();
    await uow.FlushAsync();  // Durable before external work

    try
    {
        // Execute step logic (may be async, may call external services)
        var result = await step.ExecuteAsync(workflow.Context, uow.CancellationToken);

        // Record success
        await using var successUow = db.CreateUnitOfWorkAsync(
            timeout: TimeSpan.FromSeconds(5),
            durability: DurabilityMode.Immediate);

        using var tx2 = successUow.CreateTransaction();
        tx2.UpdateComponent<WorkflowInstance>(workflow.Id, w =>
        {
            w.StepResults[step.Id] = result;
            w.Status = WorkflowStatus.StepComplete;
        });
        tx2.Commit();
        await successUow.FlushAsync();
    }
    catch (Exception ex)
    {
        // Compensation
        await step.CompensateAsync(workflow.Context);

        await using var failUow = db.CreateUnitOfWorkAsync(
            timeout: TimeSpan.FromSeconds(5),
            durability: DurabilityMode.Immediate);

        using var tx3 = failUow.CreateTransaction();
        tx3.UpdateComponent<WorkflowInstance>(workflow.Id, w =>
        {
            w.Status = WorkflowStatus.Failed;
            w.Error = ex.Message;
        });
        tx3.Commit();
        await failUow.FlushAsync();

        throw;
    }
}
```

### Why Typhon Fits

| Requirement | Typhon Provides |
|-------------|-----------------|
| Crash recovery | UoW atomic rollback |
| Step durability | Immediate mode |
| Timeout per step | UoW-level deadline |
| State history | MVCC revisions |

---

## Use Case 5: Real-Time Collaborative Editing

### Scenario

Collaborative document editing (like Google Docs):
- Real-time updates from multiple users
- Operational Transform for conflict resolution
- Low latency for responsive feel

### Code Pattern

```csharp
// SignalR hub
public async Task ApplyEdit(string docId, EditOperation op)
{
    await using var uow = db.CreateUnitOfWorkAsync(
        timeout: TimeSpan.FromMilliseconds(100),  // Real-time!
        durability: DurabilityMode.Deferred);     // Can batch

    using var tx = uow.CreateTransaction();

    try
    {
        // Read current document state
        var doc = tx.ReadEntity<Document>(docId);

        // Apply Operational Transform
        var transformed = OperationalTransform.Apply(doc, op);

        tx.UpdateComponent<Document>(docId, _ => transformed);
        tx.Commit();
        await uow.FlushAsync();

        // Broadcast to other clients
        await Clients.OthersInGroup(docId).SendAsync("EditApplied", op);
    }
    catch (ConcurrencyConflictException)
    {
        // Someone else edited - client needs to rebase
        var currentVersion = tx.ReadEntity<Document>(docId).Version;
        await Clients.Caller.SendAsync("ConflictDetected", currentVersion);
    }
}
```

### Why Typhon Fits

| Requirement | Typhon Provides |
|-------------|-----------------|
| Real-time | Microsecond latency |
| Concurrency | MVCC conflict detection |
| History | Revision chain for undo |
| Multi-user | Concurrent UoWs |

---

## Comparison: Why Typhon vs Alternatives?

| Aspect | PostgreSQL + EF Core | Redis | Typhon |
|--------|---------------------|-------|--------|
| **Latency** | Milliseconds | Microseconds | Microseconds |
| **Durability** | Always fsync | Optional | User-controlled |
| **ACID** | Full | Limited | Full |
| **Embedded** | No (separate process) | No | Yes |
| **MVCC** | Yes | No | Yes |
| **Timeout** | Connection-level | Per-command | UoW-level |
| **Async pattern** | DbContext scope | Manual | AsyncLocal |

---

## The Bold Claim

> **Typhon's combination of microsecond latency, user-controlled durability, and idiomatic async patterns could make it a compelling choice for any .NET application that needs fast, transactional, embedded data storage.**

The question isn't "can it be done?" but "is Typhon the best tool for these jobs?"

---

## Next: Architecture

See [04-architecture.md](./04-architecture.md) for the layered design.

# The Definitive Guide to `ActivitySource.StartActivity` in .NET

> **Scope**: .NET 5 through .NET 10 — covering every `StartActivity` and `CreateActivity` overload, `ActivityKind`, `Activity.Current`, the sampling pipeline, and the relationship with OpenTelemetry (OTLP).

---

## Terminology Mapping: .NET ↔ OpenTelemetry

Before diving in, the single most important thing to understand is that **.NET's `System.Diagnostics` types *are* the OpenTelemetry implementation for .NET**. There is no separate "OTel Span" object. Microsoft and the OTel community agreed to extend the existing `Activity` class rather than introduce a parallel type.

| OpenTelemetry Concept | .NET Type                          | Since   |
|------------------------|------------------------------------|---------|
| **Tracer**             | `ActivitySource`                   | .NET 5  |
| **Span**               | `Activity`                         | .NET Core 2.0 (extended in .NET 5) |
| **SpanKind**           | `ActivityKind`                     | .NET 5  |
| **SpanContext**         | `ActivityContext`                   | .NET 5  |
| **Link**               | `ActivityLink`                     | .NET 5  |
| **Event**              | `ActivityEvent`                    | .NET 5  |
| **TracerProvider**     | `ActivityListener`                 | .NET 5  |

The old `DiagnosticSource` / `DiagnosticListener` APIs are **not deprecated** but are considered the legacy approach. All new instrumentation should use `ActivitySource` / `ActivityListener`.

---

## The Big Picture: How `StartActivity` Fits In

```
┌──────────────────────────────────────────────────────────────────────┐
│                        YOUR APPLICATION CODE                         │
│                                                                      │
│   ActivitySource source = new("MyApp.Service", "1.0.0");            │
│                                                                      │
│   using (var activity = source.StartActivity("ProcessOrder"))        │
│   {                                                                  │
│       activity?.SetTag("order.id", orderId);                        │
│       // ... business logic ...                                      │
│   }  // ← Dispose() calls Stop(), which fires ActivityStopped       │
│                                                                      │
└──────────┬───────────────────────────────────────────────────────────┘
           │
           │  1. Checks if any listener is interested
           │  2. Calls Sample/SampleUsingParentId delegate
           │  3. Creates Activity (or returns null)
           │  4. Sets Activity.Current (AsyncLocal<Activity>)
           │  5. Fires ActivityStarted callback
           │
           ▼
┌──────────────────────────────────────────────────────────────────────┐
│                     ACTIVITY LISTENER (SDK / Agent)                   │
│                                                                      │
│   ActivityListener listener = new()                                  │
│   {                                                                  │
│       ShouldListenTo = src => src.Name == "MyApp.Service",          │
│       Sample = (ref ActivityCreationOptions<ActivityContext> o)      │
│                   => ActivitySamplingResult.AllDataAndRecorded,      │
│       ActivityStarted  = a => { /* enrich */ },                     │
│       ActivityStopped  = a => { /* export to OTLP */ },             │
│   };                                                                 │
│   ActivitySource.AddActivityListener(listener);                      │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

---

## `ActivitySource.StartActivity` — All Overloads

There are **4 overloads** of `StartActivity` plus **3 overloads** of `CreateActivity` (the non-starting counterpart). All return `Activity?` — **the nullable return is critical**.

### Overload 1: The Simple One

```csharp
public Activity? StartActivity(
    [CallerMemberName] string name = "",
    ActivityKind kind = ActivityKind.Internal);
```

**When to use**: Most common. Quick instrumentation of an internal operation.

**Behavior**:
- If `name` is omitted, the **calling method name** is used via `[CallerMemberName]` (since .NET 7).
- `kind` defaults to `Internal`.
- Parent is **implicitly** `Activity.Current` (whatever is the ambient current activity).
- Returns `null` if no listener is interested (performance optimization — zero allocations).

```csharp
// Simplest usage — method name becomes the activity name
using var activity = _source.StartActivity();

// With explicit name
using var activity = _source.StartActivity("ProcessPayment");

// With explicit name and kind
using var activity = _source.StartActivity("GET /api/orders", ActivityKind.Server);
```

### Overload 2: Explicit Parent via `ActivityContext`

```csharp
public Activity? StartActivity(
    string name,
    ActivityKind kind,
    ActivityContext parentContext,
    IEnumerable<KeyValuePair<string, object?>>? tags = default,
    IEnumerable<ActivityLink>? links = default,
    DateTimeOffset startTime = default);
```

**When to use**: When you need to **explicitly set the parent** using a W3C `ActivityContext` (TraceId + SpanId + TraceFlags + TraceState). Common for incoming HTTP requests where you parse the `traceparent` header.

**Behavior**:
- The `parentContext` **overrides** `Activity.Current` as the parent.
- If `parentContext` is `default` (all zeroes), **it does NOT create a root activity** — it still uses `Activity.Current` as parent (see Pitfalls section).
- Tags and links are set **before** the activity is started, so listeners see them in `ActivityStarted`.

```csharp
// Parse incoming W3C trace context
var parentContext = new ActivityContext(
    ActivityTraceId.CreateFromString(traceParentHeader.AsSpan(3, 32)),
    ActivitySpanId.CreateFromString(traceParentHeader.AsSpan(36, 16)),
    ActivityTraceFlags.Recorded,
    isRemote: true);

var tags = new List<KeyValuePair<string, object?>>
{
    new("http.method", "GET"),
    new("http.url", "/api/orders"),
};

using var activity = _source.StartActivity(
    "HTTP GET /api/orders",
    ActivityKind.Server,
    parentContext,
    tags);
```

### Overload 3: Explicit Parent via String ParentId

```csharp
public Activity? StartActivity(
    string name,
    ActivityKind kind,
    string? parentId,
    IEnumerable<KeyValuePair<string, object?>>? tags = default,
    IEnumerable<ActivityLink>? links = default,
    DateTimeOffset startTime = default);
```

**When to use**: When you receive a parent ID as a **raw string** (e.g., from a message queue header that carries the `traceparent` value as-is).

**Behavior**:
- The `parentId` string is parsed to extract trace context.
- If `parentId` is `null`, `Activity.Current` is used as parent.
- Uses the `SampleUsingParentId` listener callback (not `Sample`).

```csharp
// From a message queue header
string? parentId = message.Headers["traceparent"];

using var activity = _source.StartActivity(
    "ProcessMessage",
    ActivityKind.Consumer,
    parentId);
```

### Overload 4: Kind-First (Name Last)

```csharp
public Activity? StartActivity(
    ActivityKind kind,
    ActivityContext parentContext = default,
    IEnumerable<KeyValuePair<string, object?>>? tags = default,
    IEnumerable<ActivityLink>? links = default,
    DateTimeOffset startTime = default,
    [CallerMemberName] string name = "");
```

**When to use**: When `ActivityKind` is the primary concern and you want to rely on `[CallerMemberName]` for the operation name.

**Behavior**:
- `name` is last, with `[CallerMemberName]` default.
- Same parent-resolution logic as Overload 2.

```csharp
using var activity = _source.StartActivity(ActivityKind.Client);
// activity.OperationName == "CallingMethodName"
```

---

## `ActivitySource.CreateActivity` — The Non-Starting Counterpart

Added in **.NET 6** (via [dotnet/runtime#42784](https://github.com/dotnet/runtime/issues/42784)) to solve a real problem: sometimes you need to **enrich the activity before it starts**, so that listeners receive the full data in `ActivityStarted`.

```csharp
// Overload 1: Simple
public Activity? CreateActivity(string name, ActivityKind kind);

// Overload 2: With parent context
public Activity? CreateActivity(
    string name,
    ActivityKind kind,
    ActivityContext parentContext,
    IEnumerable<KeyValuePair<string, object?>>? tags = default,
    IEnumerable<ActivityLink>? links = default,
    ActivityIdFormat idFormat = ActivityIdFormat.Unknown);

// Overload 3: With parent ID
public Activity? CreateActivity(
    string name,
    ActivityKind kind,
    string? parentId,
    IEnumerable<KeyValuePair<string, object?>>? tags = default,
    IEnumerable<ActivityLink>? links = default,
    ActivityIdFormat idFormat = ActivityIdFormat.Unknown);
```

**Key difference from `StartActivity`**: the Activity is **not started** and **not set as `Activity.Current`**. You must call `.Start()` manually.

```csharp
using var activity = _source.CreateActivity("DbQuery", ActivityKind.Client, parentContext);
if (activity is not null)
{
    // Enrich BEFORE starting — listeners see all this in ActivityStarted
    activity.AddBaggage("db.type", "postgresql");
    activity.SetTag("db.statement", sql);
    activity.Start(); // NOW it becomes Activity.Current and fires ActivityStarted
}
```

### `StartActivity` vs `CreateActivity` Decision Flow

```
           ┌─────────────────────────────┐
           │ Need to add tags/baggage     │
           │ BEFORE ActivityStarted fires?│
           └──────┬──────────┬────────────┘
                  │YES       │NO
                  ▼          ▼
         CreateActivity   StartActivity
         + manual enrichment   (tags param or post-start ?. enrichment)
         + .Start()
```

---

## `ActivityKind` Deep Dive

`ActivityKind` describes the **role** of this operation in a distributed trace. It directly maps to the OpenTelemetry `SpanKind`. The runtime source code includes this helpful table:

```
    ActivityKind    Synchronous  Asynchronous    Remote Incoming    Remote Outgoing
    ────────────────────────────────────────────────────────────────────────────────
      Internal            ✓           ✓
      Client              ✓                                              ✓
      Server              ✓                            ✓
      Producer                        ✓                                  maybe
      Consumer                        ✓               maybe
    ────────────────────────────────────────────────────────────────────────────────
```

### `ActivityKind.Internal` (0) — Default

The operation is **internal** to the application. No remote parent, no remote children. Think "business logic processing", "database call within the same process boundary".

```csharp
// Default — no need to specify
using var activity = _source.StartActivity("ValidateOrder");
```

**OTel mapping**: `SPAN_KIND_INTERNAL`. Exporters typically render these as child spans in a flame chart without any special decoration.

### `ActivityKind.Server` (1)

The activity represents an **incoming request** from an external component. The canonical example is an HTTP server handling a request.

```csharp
using var activity = _source.StartActivity(
    "HTTP GET /api/orders",
    ActivityKind.Server,
    extractedContext); // parsed from incoming traceparent header
```

**Key behaviors**:
- The parent is typically **remote** (`ActivityContext.IsRemote = true`).
- One Server span should exist per logical incoming request.
- In OTLP exporters, Server spans become "Request" telemetry (e.g., in Application Insights).

### `ActivityKind.Client` (2)

The activity represents an **outgoing request** to an external component. The canonical example is an HTTP client making a request.

```csharp
using var activity = _source.StartActivity(
    "HTTP GET https://payment-service/charge",
    ActivityKind.Client);

activity?.SetTag("http.method", "GET");
activity?.SetTag("server.address", "payment-service");
```

**Key behaviors**:
- Typically the child of a Server or Internal span.
- Pairs with a Server span on the receiving side.
- In OTLP exporters, Client spans become "Dependency" telemetry.

### `ActivityKind.Producer` (3)

The activity represents the **sending side of an asynchronous operation** (e.g., publishing a message to a queue).

```csharp
using var activity = _source.StartActivity(
    "OrderCreated publish",
    ActivityKind.Producer);

activity?.SetTag("messaging.system", "rabbitmq");
activity?.SetTag("messaging.destination.name", "orders");
```

**Key distinction from Client**: the response is **not expected synchronously**. The Producer may complete before the Consumer even picks up the message.

### `ActivityKind.Consumer` (4)

The activity represents the **receiving side of an asynchronous operation** (e.g., processing a message from a queue).

```csharp
string? parentId = message.Headers["traceparent"];

using var activity = _source.StartActivity(
    "ProcessOrder consume",
    ActivityKind.Consumer,
    parentId);
```

**Key distinction from Server**: the incoming data comes from a queue/topic/event, not a synchronous request.

### Choosing the Right Kind — Decision Table

```
Is this operation...                              → Use
───────────────────────────────────────────────────────────────
Internal logic, no network boundary?              → Internal
Handling an incoming synchronous request?         → Server
Making an outgoing synchronous request?           → Client
Sending a message to a queue/topic?               → Producer
Processing a message from a queue/topic?          → Consumer
───────────────────────────────────────────────────────────────
```

---

## `Activity.Current` and the `AsyncLocal<Activity?>` Mechanism

### How It Works

`Activity.Current` is backed by a `static AsyncLocal<Activity?>`. This means:

```
    Thread 1                          Thread 2 (spawned from Thread 1)
    ────────────────────              ────────────────────────────────
    Activity.Current = A              Activity.Current = A  (inherited)
    │                                 │
    ├─ StartActivity("B")            │
    │  Activity.Current = B          │  (still sees A — copy-on-write)
    │  │                              │
    │  └─ Dispose/Stop B             │
    │     Activity.Current = A       │
    │                                 ├─ StartActivity("C")
    │                                 │  Activity.Current = C  (parent = A)
    │                                 │  │
    │                                 │  └─ Dispose/Stop C
    │                                 │     Activity.Current = A
```

**Key properties of AsyncLocal**:
- Values flow **down** into child async operations (Task.Run, async/await).
- Values do **not** flow **up** — a child changing `Activity.Current` doesn't affect the parent's context.
- This is **copy-on-write** semantics.

### The `Start()` → Parent Chain

When an activity is started (via `StartActivity` or manual `.Start()`):

1. `Activity.Current` is captured as the `Parent` of the new activity.
2. The new activity becomes `Activity.Current`.
3. When the activity is `Stop()`ed or `Dispose()`d, `Activity.Current` is restored to the previous value (the `Parent`).

```csharp
// Activity.Current == null

using (var a = source.StartActivity("A"))
{
    // Activity.Current == a, a.Parent == null

    using (var b = source.StartActivity("B"))
    {
        // Activity.Current == b, b.Parent == a

        using (var c = source.StartActivity("C"))
        {
            // Activity.Current == c, c.Parent == b
        }
        // Activity.Current == b  (restored after c.Dispose)
    }
    // Activity.Current == a  (restored after b.Dispose)
}
// Activity.Current == null  (restored after a.Dispose)
```

### The Public Setter on `Activity.Current`

Since .NET Core 3.0 ([dotnet/runtime#25936](https://github.com/dotnet/runtime/issues/25936)), `Activity.Current` has a **public setter**:

```csharp
Activity.Current = someActivity;  // ← Legal but use with care!
Activity.Current = null;          // ← Creates a "clean" context
```

**Legitimate use cases**:
- **Restoring context** after a hop through native code (classic ASP.NET/IIS scenario).
- **Forking a background task** that shouldn't inherit the current trace:

```csharp
using var requestActivity = source.StartActivity("HandleRequest", ActivityKind.Server);
Task.Run(() =>
{
    Activity.Current = null;  // Background task starts with clean context
    // ... fire-and-forget work ...
});
```

### Common `Activity.Current` Pitfall: Passing `default` as ParentContext

This is a notorious trap. Passing `default(ActivityContext)` does **not** create a root activity:

```csharp
// ⚠️ THIS DOES NOT CREATE A ROOT ACTIVITY
// It still uses Activity.Current as the parent!
var activity = source.StartActivity(
    "MyOperation",
    ActivityKind.Internal,
    parentContext: default);  // default == all zeroes, treated as "no explicit parent"
```

To create a **true root activity**, you must:

```csharp
// Option 1: Null out Activity.Current first
var previousActivity = Activity.Current;
Activity.Current = null;
var root = source.StartActivity("NewRoot");
// Restore if needed after

// Option 2: Proposed but not yet shipped — CreateRootActivity
// See: https://github.com/dotnet/runtime/issues/82673
```

---

## The Sampling Pipeline

When `StartActivity` or `CreateActivity` is called, the runtime goes through a multi-step decision process to determine if an `Activity` object should be created at all.

### Step-by-Step Flow

```
  StartActivity("Name", Kind, ...)
        │
        ▼
  ┌─────────────────────────────┐
  │ Any ActivityListener         │──── NO ──→ return null
  │ registered?                  │
  └──────────┬──────────────────┘
             │ YES
             ▼
  ┌─────────────────────────────┐
  │ listener.ShouldListenTo     │──── NO ──→ return null
  │ (this ActivitySource)?       │           (for this listener;
  └──────────┬──────────────────┘            check next listener)
             │ YES
             ▼
  ┌─────────────────────────────────────────────┐
  │ Has parent context?                          │
  │   → YES: call listener.Sample(               │
  │           ref ActivityCreationOptions         │
  │                    <ActivityContext>)          │
  │   → Has parentId string?                     │
  │     → YES: call listener.SampleUsingParentId( │
  │             ref ActivityCreationOptions        │
  │                      <string>)                │
  │   → Neither: call listener.Sample with        │
  │              default context                  │
  └──────────┬──────────────────────────────────┘
             │
             ▼
  ┌───────────────────────────────────────────────────────┐
  │ ActivitySamplingResult determines behavior:            │
  │                                                        │
  │  None             → Activity NOT created → return null │
  │  PropagationData  → Activity created (minimal data)    │
  │  AllData          → Activity created, IsAllDataRequested│
  │  AllDataAndRecorded → Activity created + Recorded flag │
  └───────────────────────────────────────────────────────┘
```

### `ActivitySamplingResult` Values Explained

| Value | Activity Created? | `IsAllDataRequested` | `Recorded` Flag | Use Case |
|-------|:-:|:-:|:-:|---|
| `None` | ❌ | — | — | Drop completely. No overhead. |
| `PropagationData` | ✅ | `false` | `false` | Preserve trace context for propagation, but don't record this span. |
| `AllData` | ✅ | `true` | `false` | Record locally but don't set the W3C `Recorded` flag (won't propagate downstream). |
| `AllDataAndRecorded` | ✅ | `true` | `true` | Full recording. Sets W3C `sampled` flag. Downstream services should also record. |

### ⚠️ .NET 10 Breaking Change: `PropagationData` with Recorded Parent

Before .NET 10, when a `PropagationData` sampling result was returned for an activity whose parent had the `Recorded` flag set, the child incorrectly inherited `Recorded = true` and `IsAllDataRequested = true`.

Starting in **.NET 10**, this is fixed to follow the OTel spec:

| | Before .NET 10 (bug) | .NET 10+ (correct) |
|---|:-:|:-:|
| `Recorded` | `true` (inherited from parent) | `false` |
| `IsAllDataRequested` | `true` | `false` |

If you implemented a custom `ActivityListener.Sample` returning `PropagationData`, verify your code isn't relying on this flawed behavior.

### `SampleUsingParentId` vs `Sample`

The `ActivityListener` has **two** sampling callbacks:

```csharp
public SampleActivity<string>?          SampleUsingParentId { get; set; }
public SampleActivity<ActivityContext>?  Sample              { get; set; }
```

**Which one gets called?**

- If the parent is an `ActivityContext` (from overload 2 or 4, or from `Activity.Current`), → `Sample` is called.
- If the parent is a raw string `parentId` (from overload 3), → `SampleUsingParentId` is called **first**. If it returns anything other than `None`, **then `Sample` is also called** with the parsed context.
- If neither sampling callback is set, the activity **is not created**.

**This is the #1 reason `StartActivity` returns `null` unexpectedly!** If you set `ShouldListenTo` but forget to set `Sample`, all activities return `null`.

```csharp
// ⚠️ BROKEN — StartActivity always returns null
var listener = new ActivityListener
{
    ShouldListenTo = s => true,
    // Missing Sample/SampleUsingParentId!
};

// ✅ CORRECT — must set at least Sample
var listener = new ActivityListener
{
    ShouldListenTo = s => true,
    Sample = (ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllDataAndRecorded,
};
```

---

## `Activity.IsAllDataRequested` — The Performance Hint

When an activity is created with `PropagationData`, tags, events, and links are **still accepted** but the listener has signaled it doesn't care about them. `IsAllDataRequested` lets instrumentation code skip expensive enrichment:

```csharp
using var activity = _source.StartActivity("HeavyOperation");

if (activity?.IsAllDataRequested == true)
{
    // Only compute expensive tags if someone actually wants them
    var serialized = JsonSerializer.Serialize(complexObject);
    activity.SetTag("request.body", serialized);
}
```

---

## The `HasListeners` Optimization

Before even calling `StartActivity`, you can check if anyone is listening:

```csharp
// Avoid allocating parameters if nobody is listening
if (_source.HasListeners())
{
    var tags = new List<KeyValuePair<string, object?>>
    {
        new("http.method", method),
        new("http.url", url),
    };

    using var activity = _source.StartActivity("Request", ActivityKind.Server, parentContext, tags);
}
```

This is useful when constructing the `tags` or `links` collections is itself expensive.

---

## `Activity` Lifecycle: Start, Enrich, Stop

```
  CreateActivity / StartActivity
      │
      ▼
  ┌─────────────────┐         ┌──────────────────┐
  │  Activity.Start()│────────→│ ActivityStarted   │ (callback to listeners)
  │  (automatic for  │         │ Activity.Current  │ = this activity
  │  StartActivity)  │         └──────────────────┘
  └────────┬────────┘
           │
           │  activity?.SetTag("key", "value")
           │  activity?.AddEvent(new ActivityEvent("..."))
           │  activity?.SetStatus(ActivityStatusCode.Error, "msg")
           │
           ▼
  ┌─────────────────┐         ┌──────────────────┐
  │  Activity.Stop() │────────→│ ActivityStopped   │ (callback to listeners)
  │  (via Dispose)   │         │ Activity.Current  │ = parent activity
  └─────────────────┘         └──────────────────┘
```

### `IDisposable` Pattern (Recommended)

`Activity` implements `IDisposable`. `Dispose()` calls `Stop()`. This is the idiomatic pattern:

```csharp
using var activity = _source.StartActivity("Operation");
activity?.SetTag("key", "value");
// ... do work ...
// activity is automatically stopped at end of scope
```

**Null safety**: The `using` statement handles `null` gracefully — if `StartActivity` returns `null`, `Dispose()` is simply never called.

### Manual Stop (When Needed)

```csharp
var activity = _source.StartActivity("LongRunning");
try
{
    await DoWorkAsync();
    activity?.SetStatus(ActivityStatusCode.Ok);
}
catch (Exception ex)
{
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    activity?.AddException(ex);   // AddException is an extension from OTel SDK
    throw;
}
finally
{
    activity?.Stop();
}
```

---

## Practical Usage Patterns

### Pattern 1: ASP.NET Core Incoming Request (Server)

ASP.NET Core automatically creates a Server activity for each request. You typically **don't** create one manually. Instead, enrich it:

```csharp
// In middleware or filter
var activity = Activity.Current;
activity?.SetTag("tenant.id", tenantId);
activity?.SetTag("user.id", userId);
```

### Pattern 2: Outgoing HTTP Call (Client)

`HttpClient` instrumentation handles this automatically via `System.Net.Http`. Manual example:

```csharp
using var activity = _source.StartActivity("CallPaymentService", ActivityKind.Client);
activity?.SetTag("server.address", "payment.internal");
activity?.SetTag("http.request.method", "POST");

var response = await _httpClient.PostAsync(url, content);

activity?.SetTag("http.response.status_code", (int)response.StatusCode);
if (!response.IsSuccessStatusCode)
    activity?.SetStatus(ActivityStatusCode.Error);
```

### Pattern 3: Message Queue Producer/Consumer

**Producer side**:
```csharp
using var activity = _source.StartActivity("OrderCreated publish", ActivityKind.Producer);
activity?.SetTag("messaging.system", "rabbitmq");
activity?.SetTag("messaging.destination.name", "orders");

// Inject trace context into message headers
var headers = new Dictionary<string, string>();
if (activity is not null)
{
    headers["traceparent"] = activity.Id!;
    if (activity.TraceStateString is not null)
        headers["tracestate"] = activity.TraceStateString;
}

await PublishMessage(message, headers);
```

**Consumer side**:
```csharp
string? parentId = message.Headers.GetValueOrDefault("traceparent");

using var activity = _source.StartActivity(
    "OrderCreated process",
    ActivityKind.Consumer,
    parentId);

activity?.SetTag("messaging.system", "rabbitmq");
activity?.SetTag("messaging.destination.name", "orders");
// process the message...
```

### Pattern 4: Batch Processing with Links

When a single operation processes multiple incoming traces, use `ActivityLink`:

```csharp
var links = batchMessages.Select(msg =>
{
    var ctx = ActivityContext.Parse(msg.Headers["traceparent"], msg.Headers.GetValueOrDefault("tracestate"));
    return new ActivityLink(ctx);
}).ToList();

using var activity = _source.StartActivity(
    "ProcessBatch",
    ActivityKind.Consumer,
    parentContext: default,   // no single parent
    links: links);

activity?.SetTag("batch.size", batchMessages.Count);
```

### Pattern 5: CreateActivity for Deferred Start

```csharp
using var activity = _source.CreateActivity(
    "DatabaseQuery",
    ActivityKind.Client,
    Activity.Current?.Context ?? default);

if (activity is not null)
{
    // These are visible in ActivityStarted
    activity.SetTag("db.system", "postgresql");
    activity.SetTag("db.name", "orders");
    activity.AddBaggage("db.statement.hash", ComputeHash(sql));
    activity.Start();   // NOW listeners see everything
}

// ... execute query ...
```

---

## Common Pitfalls and How to Avoid Them

### Pitfall 1: `StartActivity` Returns `null`

**Cause**: No `ActivityListener` is registered, or the listener's `Sample` callback returns `None`, or `Sample` is not set at all.

**Fix**: Ensure your OpenTelemetry SDK (or custom listener) is configured and the source name matches:

```csharp
// OTel SDK registration
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("MyApp.Service")          // Must match ActivitySource name!
        .AddOtlpExporter());
```

**Always use `?.`** when accessing activity members:
```csharp
activity?.SetTag("key", "value");    // Safe — no-op if null
activity.SetTag("key", "value");     // 💥 NullReferenceException if no listener
```

### Pitfall 2: `default(ActivityContext)` Doesn't Create a Root

Passing `default` as `parentContext` is treated as "no explicit parent specified", not "I want a root span". `Activity.Current` is still used.

### Pitfall 3: Forgetting `Sample` on `ActivityListener`

Setting `ShouldListenTo` without `Sample` means the listener is registered but never approves activity creation. Every `StartActivity` returns `null`.

### Pitfall 4: `AllData` vs `AllDataAndRecorded`

Using `AllData` creates the activity locally but does **not** set the W3C `Recorded` flag. Downstream services may decide not to record their part of the trace. For end-to-end tracing, use `AllDataAndRecorded`.

### Pitfall 5: `AsyncLocal` Doesn't Flow Through Native Hops

In classic ASP.NET (IIS), the execution context can be lost when the thread hops through native IIS code. Use `Activity.Current` setter to restore context at known pipeline points.

---

## Configuration Checklist for OTLP

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Register OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            // 2. Register ALL your ActivitySource names
            .AddSource("MyApp.OrderService")
            .AddSource("MyApp.PaymentService")

            // 3. Add auto-instrumentation
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation()

            // 4. Export to OTLP
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = new Uri("http://otel-collector:4317");
            });
    });

// 5. In your services, declare ActivitySource as a static field
public class OrderService
{
    private static readonly ActivitySource _source =
        new("MyApp.OrderService", "1.0.0");  // Name must match AddSource!

    public async Task ProcessOrder(Order order)
    {
        using var activity = _source.StartActivity("ProcessOrder");
        activity?.SetTag("order.id", order.Id);
        // ...
    }
}
```

---

## Version History

| .NET Version | Notable Changes |
|---|---|
| **.NET 5** | `ActivitySource`, `ActivityListener`, `ActivityKind`, `ActivityContext`, `ActivityLink` introduced. `Activity` extended with `IDisposable`. |
| **.NET 6** | `CreateActivity` overloads added (unstarted activities). `ActivityIdFormat` parameter. |
| **.NET 7** | `[CallerMemberName]` added to `name` parameter on simple overloads. `ActivitySource` constructor gains `tags` parameter. |
| **.NET 8** | `ActivitySourceOptions` constructor added. `Activity.AddLink()` for adding links post-creation. |
| **.NET 9** | Links can be added after activity creation via `Activity.AddLink()` (stabilized). `ActivitySource.Tags` and `TelemetrySchemaUrl` properties. |
| **.NET 10** | **Breaking change**: `PropagationData` with `Recorded` parent no longer inherits `Recorded`/`IsAllDataRequested`. Aligns with OTel spec. |

---

## Quick Reference Card

```
┌──────────────────────────────────────────────────────────────────────┐
│                 ActivitySource.StartActivity Cheat Sheet              │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  SIMPLE:        source.StartActivity("Name")                        │
│  WITH KIND:     source.StartActivity("Name", ActivityKind.Server)   │
│  WITH PARENT:   source.StartActivity("Name", Kind, parentContext)   │
│  WITH PARENT ID:source.StartActivity("Name", Kind, parentIdString)  │
│  KIND-FIRST:    source.StartActivity(ActivityKind.Client)           │
│  UNSTARTED:     source.CreateActivity("Name", Kind) + .Start()     │
│                                                                      │
│  ALWAYS:                                                             │
│    ✓ Use using/Dispose pattern                                      │
│    ✓ Use ?. for null safety                                         │
│    ✓ Register ActivitySource name with AddSource()                  │
│    ✓ Set Sample on ActivityListener                                 │
│    ✓ Use AllDataAndRecorded for full tracing                        │
│                                                                      │
│  NEVER:                                                              │
│    ✗ Assume StartActivity returns non-null                          │
│    ✗ Pass default(ActivityContext) expecting a root span             │
│    ✗ Forget to register your source name with the OTel SDK          │
│    ✗ Mix up AllData (local only) with AllDataAndRecorded (propagated)│
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

---

## References

- [Microsoft Learn — ActivitySource.StartActivity](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.activitysource.startactivity)
- [Microsoft Learn — Distributed Tracing Instrumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
- [dotnet/runtime — ActivitySource.cs source code](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/ActivitySource.cs)
- [dotnet/designs — Activity Improvements Spec](https://github.com/dotnet/designs/blob/main/accepted/2020/diagnostics/activity-improvements.md)
- [dotnet/runtime#42784 — CreateActivity API proposal](https://github.com/dotnet/runtime/issues/42784)
- [dotnet/runtime#82673 — Root Activity API proposal](https://github.com/dotnet/runtime/issues/82673)
- [dotnet/runtime#65528 — Root Activity when Current is not null](https://github.com/dotnet/runtime/issues/65528)
- [dotnet/runtime#45070 — StartActivity returns null](https://github.com/dotnet/runtime/issues/45070)
- [.NET 10 Breaking Change — Activity Sampling](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/activity-sampling)
- [OpenTelemetry .NET — Instrumentation Guide](https://opentelemetry.io/docs/languages/dotnet/instrumentation/)
- [OpenTelemetry .NET SDK — README](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Api/README.md)
- [Nicholas Blumhardt — ActivityListener Sampling](https://nblumhardt.com/2024/10/activity-listener-sampling/)
- [Jimmy Bogard — ActivitySource and OpenTelemetry 1.0](https://www.jimmybogard.com/building-end-to-end-diagnostics-activitysource-and-open/)

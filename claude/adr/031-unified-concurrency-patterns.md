# ADR-031: Unified Concurrency Patterns (WaitContext, Deadline, IContentionTarget)

**Status**: Accepted
**Date**: 2026-01-29
**Deciders**: Developer + Claude Code
**Supersedes**: Partially updates ADR-017 (thread ID field width)

## Context

Issue #18 overhauled the AccessControl family to establish consistent patterns across three synchronization primitives:

| Type | Size | Purpose |
|------|------|---------|
| **AccessControl** | 64-bit | Full-featured reader-writer lock |
| **AccessControlSmall** | 32-bit | Compact RW lock for high-density scenarios |
| **ResourceAccessControl** | 32-bit | 3-mode lifecycle lock (Accessing/Modify/Destroy) |

Prior to this work, these primitives had:
- Inconsistent timeout parameters (`TimeSpan?`, `CancellationToken` in various combinations)
- Different thread ID field widths (10-bit, 8-bit, none)
- No unified telemetry integration

## Decisions

### 1. 16-Bit Thread IDs Everywhere

**All three primitives now use exactly 16 bits for thread ID storage.**

```csharp
// Coding standard rule added to CLAUDE.md:
// "Thread IDs stored as 16 bits: All synchronization primitives that store
// thread IDs must use exactly 16 bits (max 65,535)."
```

**Rationale**:
- Servers with 500+ cores are common in enterprise/cloud scenarios
- 10 bits (max 1,024) was insufficient
- 16 bits (max 65,535) provides headroom for future growth
- Consistent across all primitives simplifies reasoning about thread limits

**Impact on ADR-017**: The bit layout for AccessControl changes:
```
Old: Bits 52-61: Thread ID (10 bits)
New: Bits 32-47: Thread ID (16 bits)
```

### 2. WaitContext + Deadline Pattern

**Unified timeout/cancellation handling via `WaitContext` and `Deadline` types.**

```csharp
// WaitContext: ref struct combining deadline + cancellation
public ref struct WaitContext
{
    public Deadline Deadline;
    public CancellationToken Token;

    public static WaitContext Infinite();
    public static WaitContext FromTimeout(TimeSpan timeout);
    public static WaitContext FromDeadline(Deadline deadline);
}

// Deadline: monotonic timestamp for timeout tracking
public readonly struct Deadline
{
    public static Deadline Infinite { get; }
    public static Deadline FromTimeout(TimeSpan timeout);
    public bool IsExpired { get; }
    public TimeSpan Remaining { get; }
}
```

**API pattern**: All lock methods accept `ref WaitContext` as first parameter:
```csharp
public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);
public bool EnterSharedAccess(ref WaitContext ctx, IContentionTarget target = null);
```

**Rationale**:
- Single parameter handles both timeout and cancellation
- `Deadline` uses high-resolution monotonic clock (Stopwatch-based)
- Avoids repeated `DateTime.UtcNow` calls in spin loops
- `ref struct` ensures stack allocation, no heap pressure
- Consistent API across all three primitives

### 3. IContentionTarget Callback Interface

**Per-resource telemetry via callback pattern.**

```csharp
public interface IContentionTarget
{
    TelemetryLevel TelemetryLevel { get; }  // None, Light, Deep
    IResource OwningResource { get; }        // Optional link to resource graph

    void RecordContention(long waitUs);                         // Light mode
    void LogLockOperation(LockOperation operation, long durationUs); // Deep mode
}

public enum TelemetryLevel { None = 0, Light = 1, Deep = 2 }

public enum LockOperation : byte
{
    None = 0,
    SharedAcquired, SharedReleased, SharedWaitStart,
    ExclusiveAcquired, ExclusiveReleased, ExclusiveWaitStart,
    PromoteToExclusiveStart, PromoteToExclusiveAcquired, DemoteToShared,
    TimedOut, Canceled
}
```

**API pattern**: Optional last parameter on all lock methods:
```csharp
public bool EnterExclusiveAccess(ref WaitContext ctx, IContentionTarget target = null);
```

**Rationale**:
- Inverts dependency: locks don't need to know about resources
- Resources opt-in by implementing `IContentionTarget`
- Three granularity levels (None/Light/Deep) for different needs
- Negligible overhead when `target` is null (simple null check)
- Links to resource graph without inheritance (`IContentionTarget` doesn't extend `IResource`)

## Alternatives Considered

### For Thread IDs
1. **Keep 10-bit** — Rejected: insufficient for modern server hardware
2. **32-bit** — Rejected: wastes space, no practical benefit over 16-bit
3. **Variable width per primitive** — Rejected: inconsistent, error-prone

### For Timeout Handling
1. **Separate `TimeSpan` + `CancellationToken` params** — Rejected: cluttered API, repeated deadline calculation
2. **Single `CancellationToken` with `.WaitHandle`** — Rejected: CancellationToken doesn't carry timeout info
3. **`Task.Delay` pattern** — Rejected: allocates, not suitable for low-level sync primitives

### For Telemetry
1. **Global flags (Track 1-3 pattern)** — Already exists, but doesn't provide per-resource granularity
2. **Event-based (ETW/EventSource)** — Rejected: higher overhead, complex correlation
3. **Virtual method dispatch** — Rejected: interface dispatch (~1.3ns) is fine for contention paths

## Consequences

**Positive:**
- Consistent API across all three synchronization primitives
- 16-bit thread IDs support modern server scale
- `Deadline` avoids repeated clock reads in spin loops
- Per-resource telemetry enables targeted diagnostics
- Clean separation: locks don't depend on resource types

**Negative:**
- `WaitContext` as `ref struct` can't be stored in heap objects (by design)
- Existing code using old API needs migration
- `IContentionTarget` adds 1 parameter to all lock methods

## Cross-References

- [01-concurrency.md](../overview/01-concurrency.md) — Concurrency overview (updated)
- [09-observability.md](../overview/09-observability.md) — Track 4 telemetry
- [11-utilities.md](../overview/11-utilities.md) — AccessControl family documentation (updated)
- [ADR-016](016-three-mode-resource-access-control.md) — ResourceAccessControl design
- [ADR-017](017-64bit-access-control-state.md) — Original 64-bit state design (thread ID width now superseded)
- [ADR-019](019-runtime-telemetry-toggle.md) — Telemetry toggle system
- Reference docs: `claude/reference/concurrency/` (moved from design/)
